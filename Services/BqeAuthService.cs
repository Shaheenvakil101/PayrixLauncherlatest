using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PayrixLauncher.Services;

/// <summary>
/// Authenticates against BQE Core Admin Portal API.
///
/// Tries all known endpoint variants automatically and returns raw response for debugging.
/// </summary>
public static class BqeAuthService
{
    // Match browser JSON.stringify behaviour — do NOT escape <, >, &, + as \uXXXX.
    // System.Text.Json escapes these by default; if the password contains them the
    // server receives a different string than what was typed, causing auth failure.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ResponseType constants from BQECoreSharedLib.ResponseType
    private const int RT_OK                 = 0;
    private const int RT_INVALID_CREDS      = 1;
    private const int RT_INACTIVE_ACCOUNT   = 2;
    private const int RT_MULTIPLE_COMPANIES = 3;
    private const int RT_NO_COMPANY         = 4;
    private const int RT_TRIAL_EXPIRED      = 6;
    private const int RT_SUB_EXPIRED        = 7;
    private const int RT_2FA_REQUIRED       = 12;

    // All known endpoint paths — tried in order until one returns a parseable response
    // (path, useFormEncoded)
    // useFormEncoded=true → send application/x-www-form-urlencoded instead of JSON.
    // Needed for MVC actions that have ASP.NET request validation enabled (ValidateSignIn).
    private static readonly (string Path, bool FormEncoded)[] Endpoints =
    [
        // Local IIS — full virtual path names
        ("/BQECoreAdminPortalAPI/API/Account/ValidateUser",    false),
        ("/BQECoreAdminPortalApi/API/Account/ValidateUser",    false),
        ("/BQECoreAdminPortalapi/API/Account/ValidateUser",    false),
        ("/BQECoreAdminPortalapi/api/Account/ValidateUser",    false),
        // Cloud — direct API (no request-validation issue)
        ("/adminapi/api/Account/ValidateUser",                 false),
        ("/adminapi/API/Account/ValidateUser",                 false),
        // Cloud — web app MVC action.  Browser sends JSON { Email, Password } with no Company_ID;
        // MVC5 JsonValueProviderFactory defaults missing Guid to Guid.Empty (no 422).
        // Note: /webapp/Account/ValidateSSOUser is an email probe only — no token returned.
        ("/webapp/Account/ValidateSignIn",                     false),
        // Bare /api root
        ("/coreapi/api/Account/ValidateUser",                  false),
        ("/api/Account/ValidateUser",                          false),
        ("/API/Account/ValidateUser",                          false),
        ("/coreapi/api/Account/Login",                         false),
        ("/api/auth/login",                                    false),
    ];

    // ── Public API ────────────────────────────────────────────────────────

    public static Task<BqeLoginResult> LoginAsync(
        string baseUrl, string email, string password, string? companyId = null)
        => TryAllEndpointsAsync(baseUrl, email, password, companyId);

    public static Task<BqeLoginResult> LoginWithCompanyAsync(
        string baseUrl, string email, string password, string companyId)
        => LoginAsync(baseUrl, email, password, companyId);

    // ── Core logic ────────────────────────────────────────────────────────

    private static async Task<BqeLoginResult> TryAllEndpointsAsync(
        string baseUrl, string email, string password, string? companyId)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return BqeLoginResult.Fail("BQE Core URL is required.", "");

        using var client = MakeClient(baseUrl);
        // X-Requested-With mimics browser AJAX — some server pipelines skip validation for XHR
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        // Browser sends only { Email, Password } as JSON — no Company_ID.
        // When companyId is specified (multi-company selection), include it.
        object body = companyId != null
            ? (object)new { Email = email, Password = password, Company_ID = Guid.Parse(companyId) }
            : new { Email = email, Password = password };

        var log = new System.Text.StringBuilder();
        log.AppendLine($"Base URL (normalised): {baseUrl}");
        // Show exact JSON that will be sent (password redacted for display only)
        var bodyJson = JsonSerializer.Serialize(body, JsonOpts);
        var redacted  = System.Text.RegularExpressions.Regex.Replace(
            bodyJson, @"""Password""\s*:\s*""[^""]*""", @"""Password"":""***""");
        log.AppendLine($"Body JSON: {redacted}");
        log.AppendLine();

        bool firstAttempt = true;
        foreach (var (path, formEncoded) in Endpoints)
        {
            var fullUrl = baseUrl.TrimEnd('/') + path;
            log.AppendLine($"→ POST {fullUrl}{(formEncoded ? " [form]" : "")}");
            var (statusCode, json, connErr) = await PostRawAsync(client, path, body, formEncoded);

            if (connErr != null)
            {
                log.AppendLine($"  ✗ {connErr}");
                // Any network failure on first attempt = server unreachable, abort
                if (firstAttempt)
                {
                    log.AppendLine("  (aborting — server unreachable on first attempt)");
                    return BqeLoginResult.Fail(connErr, log.ToString());
                }
                firstAttempt = false;
                continue;
            }
            firstAttempt = false;

            log.AppendLine($"  HTTP {statusCode}");
            if (json != null)
                log.AppendLine($"  {json[..Math.Min(json.Length, 400)]}");

            // 404/405 = wrong path, try next
            if (statusCode == 404 || statusCode == 405) { log.AppendLine("  (not found, trying next)"); continue; }

            // Any non-404/405 failure on a form-encoded endpoint — retry with JSON.
            // Form binding of non-nullable Guid (Company_ID) fails with 422 when the field
            // is absent; JSON binding handles missing fields gracefully.
            if (formEncoded && statusCode != 404 && statusCode != 405 && !(statusCode >= 200 && statusCode < 300))
            {
                log.AppendLine($"  ✗ {statusCode} on form-encoded — retrying with JSON");
                var (statusCode2, json2, connErr2) = await PostRawAsync(client, path, body, false);
                log.AppendLine($"  HTTP {statusCode2} (JSON retry)");
                if (statusCode2 >= 200 && statusCode2 < 300)
                {
                    statusCode = statusCode2; json = json2;
                    goto parseit;
                }
                if (statusCode2 != 404 && statusCode2 != 405)
                    log.AppendLine("  (JSON retry also failed, trying next endpoint)");
            }

            // 5xx = server-side error on a route that EXISTS — don't mask with "try next".
            // "Parser Error" means IIS/ASP.NET found the app but its web.config has a syntax or
            // handler-registration error.  Surface it immediately so the user can fix it.
            if (statusCode >= 500)
            {
                var serverMsg = StripHtml(json, 300);
                var hint = serverMsg.Contains("Parser Error", StringComparison.OrdinalIgnoreCase)
                    ? "\n\nThis is an IIS/ASP.NET configuration error in BQECoreAdminPortalAPI.\n" +
                      "Check the web.config for that application in IIS Manager, or look in the\n" +
                      "Windows Event Log (Application) for the full parser error detail."
                    : "";
                log.AppendLine($"  ✗ Server error: {serverMsg}");
                return BqeLoginResult.Fail(
                    $"HTTP {statusCode} — server error at {path}:{hint}\n\n{serverMsg}",
                    log.ToString());
            }

            // 409 = endpoint found but BQE threw an exception (e.g. wrong password, account issue)
            // Stop trying — this IS the right endpoint, don't mask the real error
            if (statusCode == 409 || statusCode == 401 || statusCode == 403)
            {
                var msg = TryExtractMessage(json) ?? $"HTTP {statusCode}";
                log.AppendLine($"  ✗ Auth failure: {msg}");
                return BqeLoginResult.Fail(msg, log.ToString());
            }

            // Got a 2xx/3xx response — try to parse it
            parseit:
            var result = ParseLoginResponse(json, email);
            result.RawLog = log.ToString();

            if (result.Success || result.Companies != null ||
                result.Error?.Contains("password", StringComparison.OrdinalIgnoreCase) == true ||
                result.Error?.Contains("inactive",  StringComparison.OrdinalIgnoreCase) == true ||
                result.Error?.Contains("expired",   StringComparison.OrdinalIgnoreCase) == true ||
                result.Error?.Contains("2FA",       StringComparison.OrdinalIgnoreCase) == true)
            {
                return result;
            }

            log.AppendLine($"  (unparseable or generic error — trying next endpoint)");
        }

        return BqeLoginResult.Fail(
            "Login failed — no endpoint responded correctly.\n\n" +
            "All known endpoint paths returned 404. The staging/cloud server may use a\n" +
            "different virtual path. Check the Raw Response log for details, or open\n" +
            $"  {baseUrl.TrimEnd('/')}/api/Account/ValidateUser\n" +
            "in a browser (expect a 405 Method Not Allowed if the route exists, 404 if not).\n\n" +
            "If the admin API is at a different sub-path, enter just the host URL and\n" +
            "contact a dev to add the correct path to the endpoint list.",
            log.ToString());
    }

    // ── Response parsing ──────────────────────────────────────────────────

    private static BqeLoginResult ParseLoginResponse(string? json, string email)
    {
        if (string.IsNullOrWhiteSpace(json))
            return BqeLoginResult.Fail("Empty response.", "");

        try
        {
            var doc  = JsonDocument.Parse(json!);
            var root = doc.RootElement;

            // Unwrap the MVC-layer envelope: { IsSuccessStatusCode, Response: {...}, Error: {...} }
            // Used by /webapp/Account/ValidateSignIn
            if (root.TryGetProperty("Response", out var innerResp) &&
                innerResp.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("IsSuccessStatusCode", out var ok) &&
                    ok.ValueKind == JsonValueKind.False)
                {
                    var errMsg = "";
                    if (root.TryGetProperty("Error", out var errObj) &&
                        errObj.ValueKind == JsonValueKind.Object)
                        errMsg = GetStr(errObj, "Message", "message") ?? "";
                    return BqeLoginResult.Fail(
                        string.IsNullOrEmpty(errMsg) ? "Sign-in failed." : errMsg, "");
                }
                // Re-parse using the inner Response object
                root = innerResp;
            }

            // Read ResponseType (defaults to 1=invalid if missing)
            int rt = 1;
            if (root.TryGetProperty("ResponseType", out var rtp) && rtp.TryGetInt32(out var rv))
                rt = rv;

            var message = GetStr(root, "Message", "message") ?? "";

            switch (rt)
            {
                case RT_OK:
                    var token = GetStr(root, "BusinessToken", "Token", "token",
                                              "access_token", "AccessToken");
                    if (string.IsNullOrWhiteSpace(token))
                        return BqeLoginResult.Fail(
                            "Login returned OK but no token found in response.", "");
                    return BqeLoginResult.Ok(token!, ExtractUserName(root, email));

                case RT_MULTIPLE_COMPANIES:
                    var companies = ExtractCompanies(root);
                    return BqeLoginResult.MultiCompany(companies);

                case RT_INVALID_CREDS:
                    return BqeLoginResult.Fail("Invalid email or password.", "");

                case RT_INACTIVE_ACCOUNT:
                    return BqeLoginResult.Fail("Account not activated — check your email.", "");

                case RT_NO_COMPANY:
                    return BqeLoginResult.Fail("No company file found for this account.", "");

                case RT_TRIAL_EXPIRED:
                    return BqeLoginResult.Fail("Trial has expired.", "");

                case RT_SUB_EXPIRED:
                    return BqeLoginResult.Fail("Subscription has expired.", "");

                case RT_2FA_REQUIRED:
                    return BqeLoginResult.Fail("2FA required — not supported here.", "");

                default:
                    return BqeLoginResult.Fail(
                        string.IsNullOrWhiteSpace(message)
                            ? $"Login failed (ResponseType={rt})." : message, "");
            }
        }
        catch
        {
            // Not a Core-format JSON — this endpoint returned something else
            return BqeLoginResult.Fail("Unrecognised response format.", "");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static async Task<(int statusCode, string? json, string? connError)>
        PostRawAsync(HttpClient client, string path, object body, bool formEncoded = false)
    {
        try
        {
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            HttpContent content;
            if (formEncoded)
            {
                // Form-encoded avoids ASP.NET JsonValueProviderFactory request validation,
                // which rejects JSON bodies containing null values (serialised as "<null>").
                var props = body.GetType().GetProperties()
                    .Select(p => new KeyValuePair<string, string>(
                        p.Name, p.GetValue(body)?.ToString() ?? ""))
                    .ToList();
                content = new FormUrlEncodedContent(props);
            }
            else
            {
                content = new StringContent(
                    JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
            }

            var resp = await client.PostAsync(path, content).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ((int)resp.StatusCode, json, null);
        }
        catch (HttpRequestException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            var msg = detail == ex.Message ? $"Connection failed: {ex.Message}"
                                           : $"Connection failed: {ex.Message}\n  Detail: {detail}";
            return (0, null, msg);
        }
        catch (TaskCanceledException)   { return (0, null, "Request timed out."); }
        catch (Exception ex)            { return (0, null, ex.Message); }
    }

    private static HttpClient MakeClient(string baseUrl)
    {
        var inner   = ProxyConfig.MakeHandler();
        var handler = new LoggingHandler("BQE Auth", inner);
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout     = TimeSpan.FromSeconds(20)
        };
    }

    private static string? GetStr(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static string ExtractUserName(JsonElement root, string email)
    {
        if (root.TryGetProperty("UserInfo", out var ui))
        {
            var full = GetStr(ui, "FullName", "fullName", "DisplayName", "Name");
            if (!string.IsNullOrWhiteSpace(full)) return full;
            var fn = GetStr(ui, "FirstName", "firstName") ?? "";
            var ln = GetStr(ui, "LastName",  "lastName")  ?? "";
            var nm = $"{fn} {ln}".Trim();
            if (!string.IsNullOrWhiteSpace(nm)) return nm;
        }
        var local = email.Split('@')[0];
        return string.Join(" ", local.Split('.').Select(p =>
            p.Length > 0 ? char.ToUpper(p[0]) + p[1..].ToLower() : p));
    }

    private static List<BqeCompany> ExtractCompanies(JsonElement root)
    {
        var list = new List<BqeCompany>();
        if (!root.TryGetProperty("CompanyFiles", out var arr) ||
            arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
        {
            var id   = GetStr(item, "Company_ID", "CompanyId", "ID") ?? "";
            var name = GetStr(item, "CompanyName", "Name",     "companyName") ?? id;
            if (!string.IsNullOrEmpty(id))
                list.Add(new BqeCompany { Id = id, Name = name });
        }
        return list;
    }

    private static string? TryExtractMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(json!);
            return GetStr(doc.RootElement,
                "Message", "message", "error", "Error",
                "title",   "Title",   "detail", "Detail",
                "DetailMessage");
        }
        catch { return null; }
    }

    /// <summary>Strips HTML tags and collapses whitespace for human-readable server error messages.</summary>
    private static string StripHtml(string? html, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(html)) return html ?? "";
        if (!html.TrimStart().StartsWith("<")) return html[..Math.Min(html.Length, maxLen)];

        var titleMatch = System.Text.RegularExpressions.Regex.Match(
            html, @"<title[^>]*>(.*?)</title>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : "";

        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ").Trim();
        var summary = string.IsNullOrEmpty(title) ? text : $"[{title}] {text}";
        return summary[..Math.Min(summary.Length, maxLen)];
    }

    private static string NormalizeBaseUrl(string url)
    {
        url = url.Trim().TrimEnd('/');

        // Strip any known app/api sub-paths so we always work from the host root
        foreach (var suffix in new[]
        {
            "/BQECoreAdminPortalAPI/API/Account/ValidateUser",
            "/BQECoreAdminPortalAPI/API/Account",
            "/BQECoreAdminPortalAPI/API",
            "/BQECoreAdminPortalAPI",
            "/BQECoreAdminPortalapi/API",
            "/BQECoreAdminPortalapi",
            "/BQECoreAdminPortalWebApp",
            "/BQECoreWebApp",           // ← user entered web-app URL, strip it
            "/webapp",                  // ← cloud deployment web app virtual path
            "/adminapi/api",            // ← cloud deployment API virtual path + route prefix
            "/adminapi",                // ← cloud deployment API virtual path
            "/coreapi/api/Account/ValidateUser",
            "/coreapi/api/Account",
            "/coreapi/api",
            "/coreapi",
            "/api",                     // ← user may paste URL with /api suffix (e.g. staging-admin.bqecore-np.com/api)
        })
        {
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return url[..^suffix.Length];
        }
        return url;
    }
}

// ── Result model ──────────────────────────────────────────────────────────────

public class BqeLoginResult
{
    public bool              Success   { get; private set; }
    public string?           Token     { get; private set; }
    public string?           UserName  { get; private set; }
    public string?           Error     { get; private set; }
    public List<BqeCompany>? Companies { get; private set; }
    public string            RawLog    { get; set; } = "";

    public static BqeLoginResult Ok(string token, string userName) =>
        new() { Success = true, Token = token, UserName = userName };

    public static BqeLoginResult MultiCompany(List<BqeCompany> companies) =>
        new() { Companies = companies };

    public static BqeLoginResult Fail(string error, string log) =>
        new() { Error = error, RawLog = log };
}

public class BqeCompany
{
    public string Id   { get; set; } = "";
    public string Name { get; set; } = "";
    public override string ToString() => Name;
}
