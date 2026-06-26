using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PayrixLauncher.Models;

namespace PayrixLauncher.Services;

public enum PayrixEnvironment
{
    Sandbox,
    Production
}

public class PayrixService
{
    private readonly HttpClient _client;
    private readonly PayrixEnvironment _environment;

    // Session-level cache: merchantId → (entityId, entityName, merchantName)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? entityId, string? entityName, string? merchantName)>
        _merchantEntityCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new PayrixLauncher.Models.FlexibleStringConverter(), new PayrixLauncher.Models.FlexibleIntConverter() }
    };

    public static string SandboxBaseUrl    => "https://test-api.payrix.com";
    public static string ProductionBaseUrl => "https://epaymentsapi.bqecore.com";

    // ── Login: exchange username+password for an API token ───────────────────

    /// <summary>
    /// Returns (token, rawJson, error).
    /// rawJson is always populated so the caller can inspect/log the full response.
    /// </summary>
    public static async Task<(string? token, string rawJson, string? error)> LoginAsync(
        string username, string password, PayrixEnvironment environment)
    {
        var baseUrl = environment == PayrixEnvironment.Production ? ProductionBaseUrl : SandboxBaseUrl;
        using var handler = ProxyConfig.MakeHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Payrix login accepts both { login, password } and { username, password }
        var body = new StringContent(
            JsonSerializer.Serialize(new { login = username, password }),
            System.Text.Encoding.UTF8,
            "application/json");

        string json = "";
        try
        {
            var response = await client.PostAsync("/login", body).ConfigureAwait(false);
            json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (null, json, $"HTTP {(int)response.StatusCode}: {json}");

            using var doc = JsonDocument.Parse(json);

            // Try every known response shape Payrix uses:
            // Shape 1:  { "response": { "data": [ { "token": "..." } ] } }
            // Shape 2:  { "response": { "data": [ { "apiKey": "..." } ] } }
            // Shape 3:  { "token": "..." }
            // Shape 4:  { "apiKey": "..." }
            // Shape 5:  { "data": { "token": "..." } }
            string? token = null;

            if (doc.RootElement.TryGetProperty("response", out var resp))
            {
                if (resp.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array &&
                    data.GetArrayLength() > 0)
                {
                    var first = data[0];
                    token = TryGetString(first, "token")
                         ?? TryGetString(first, "apiKey")
                         ?? TryGetString(first, "api_key")
                         ?? TryGetString(first, "key");
                }
                // Also check directly under "response"
                token ??= TryGetString(resp, "token")
                       ?? TryGetString(resp, "apiKey");
            }

            // Top-level fallbacks
            token ??= TryGetString(doc.RootElement, "token")
                   ?? TryGetString(doc.RootElement, "apiKey")
                   ?? TryGetString(doc.RootElement, "api_key");

            return string.IsNullOrEmpty(token)
                ? (null, json, $"Login OK but no token found in response")
                : (token, json, null);
        }
        catch (Exception ex)
        {
            return (null, json, $"Login exception: {ex.Message}");
        }
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    // ── Shared connection pools — one per environment (never disposed) ────────
    private static readonly HttpClient _sandboxClient    = CreatePooledClient(isSandbox: true);
    private static readonly HttpClient _productionClient = CreatePooledClient(isSandbox: false);

    private static HttpMessageHandler MakeSocketsHandler()
    {
        // When Fiddler/proxy is active, HttpClientHandler handles the CONNECT tunnel
        // correctly; SocketsHttpHandler can fail HTTPS handshake through Fiddler.
        if (ProxyConfig.IsEnabled)
            return ProxyConfig.MakeHandler();

        return new SocketsHttpHandler
        {
            PooledConnectionLifetime    = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer     = 10,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            },
            AutomaticDecompression =
                System.Net.DecompressionMethods.GZip |
                System.Net.DecompressionMethods.Deflate |
                System.Net.DecompressionMethods.Brotli,
            UseProxy = false
        };
    }

    private static HttpClient CreatePooledClient(bool isSandbox)
    {
        var handler = MakeSocketsHandler();
        var baseUrl = isSandbox ? SandboxBaseUrl : ProductionBaseUrl;
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout     = TimeSpan.FromSeconds(60)
        };
    }

    public PayrixService(string apiKey, PayrixEnvironment environment)
    {
        _environment = environment;
        // Pick the right shared client and clone with per-request APIKEY header
        var baseClient = environment == PayrixEnvironment.Sandbox ? _sandboxClient : _productionClient;

        // We can't mutate DefaultRequestHeaders on a shared client,
        // so we use a delegating handler wrapper that injects the key per-request.
        var innerHandler = new ApiKeyHandler(apiKey, environment == PayrixEnvironment.Sandbox
            ? SandboxBaseUrl : ProductionBaseUrl);
        var handler = new LoggingHandler("Payrix", innerHandler);
        _client = new HttpClient(handler)
        {
            BaseAddress = new Uri(environment == PayrixEnvironment.Sandbox ? SandboxBaseUrl : ProductionBaseUrl),
            Timeout     = TimeSpan.FromSeconds(60)
        };
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _ = baseClient; // suppress unused warning
    }

    // ── Fetch single transaction by ID ────────────────────────────────────────

    public async Task<(Transaction? transaction, string rawJson, string? error)> GetTransactionAsync(string txnId)
    {
        var lastJson = "";
        try
        {
            // ── Run all 3 strategies in parallel — take the first that returns an exact ID match ──
            // Strategy 1: exact-ID path   (standard Payrix API)
            // Strategy 2: query-string search  (sandbox search param in URL)
            // Strategy 3: header-based search  (Production BQECore proxy)

            var cts = new System.Threading.CancellationTokenSource();

            async Task<(Transaction? txn, string json, int strategy)> TryStrategy1()
            {
                var resp = await _client.GetAsync(
                    $"/txns/{Uri.EscapeDataString(txnId)}?expand[items][]",
                    cts.Token).ConfigureAwait(false);
                var j = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return (null, j, 1);
                var parsed = JsonSerializer.Deserialize<PayrixResponse>(j, JsonOptions);
                var txn    = parsed?.Response?.Data?.FirstOrDefault(t => t.Id == txnId);
                return (txn?.Id != null ? txn : null, j, 1);
            }

            async Task<(Transaction? txn, string json, int strategy)> TryStrategy2()
            {
                var resp = await _client.GetAsync(
                    $"/txns?search[id][eq]={Uri.EscapeDataString(txnId)}&page[limit]=1&page[number]=1&expand[items][]",
                    cts.Token).ConfigureAwait(false);
                var j = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return (null, j, 2);
                var parsed = JsonSerializer.Deserialize<PayrixResponse>(j, JsonOptions);
                if (parsed?.Response?.Errors is { Count: > 0 }) return (null, j, 2);
                var txn = parsed?.Response?.Data?.FirstOrDefault(t => t.Id == txnId);
                return (txn?.Id != null ? txn : null, j, 2);
            }

            async Task<(Transaction? txn, string json, int strategy)> TryStrategy3()
            {
                var req = new HttpRequestMessage(
                    HttpMethod.Get, "/txns?page[limit]=1&page[number]=1&expand[items][]");
                req.Headers.Add("search", $"id[eq]={txnId}");
                var resp = await _client.SendAsync(req, cts.Token).ConfigureAwait(false);
                var j    = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return (null, j, 3);
                var parsed = JsonSerializer.Deserialize<PayrixResponse>(j, JsonOptions);
                if (parsed?.Response?.Errors is { Count: > 0 }) return (null, j, 3);
                var txn = parsed?.Response?.Data?.FirstOrDefault(t => t.Id == txnId);
                return (txn?.Id != null ? txn : null, j, 3);
            }

            var tasks = new[]
            {
                TryStrategy1().ContinueWith(t => t.Exception == null ? t.Result : (null, "", 1)),
                TryStrategy2().ContinueWith(t => t.Exception == null ? t.Result : (null, "", 2)),
                TryStrategy3().ContinueWith(t => t.Exception == null ? t.Result : (null, "", 3)),
            };

            // Wait for the first successful hit; cancel remaining once found
            (Transaction? txn, string json, int strategy) winner = (null, "", 0);
            var remaining = tasks.ToList();
            while (remaining.Count > 0)
            {
                var done = await Task.WhenAny(remaining).ConfigureAwait(false);
                remaining.Remove(done);
                var result = await done.ConfigureAwait(false);
                lastJson = result.json.Length > lastJson.Length ? result.json : lastJson;
                if (result.txn != null)
                {
                    winner = result;
                    cts.Cancel(); // signal remaining strategies to abort
                    break;
                }
            }

            if (winner.txn != null)
                return (winner.txn, winner.json, null);

            // Nothing found — surface best diagnostic from last response
            var errors = JsonSerializer.Deserialize<PayrixResponse>(lastJson, JsonOptions)?.Response?.Errors;
            var errMsg = errors is { Count: > 0 }
                ? string.Join("  |  ", errors.Select(e => e.Summary))
                : $"Transaction '{txnId}' not found (tried path, query-string and header search).";
            return (null, lastJson, errMsg);
        }
        catch (Exception ex)
        {
            return (null, lastJson, $"Fetch error: {ex.Message}");
        }
    }

    // ── Fetch payment info (card brand / last 4) ─────────────────────────────

    public async Task<PaymentInfo?> GetPaymentAsync(string paymentId)
    {
        HttpResponseMessage response;

        if (_environment == PayrixEnvironment.Production)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/payments?page[limit]=1&page[number]=1");
            request.Headers.Add("search", $"id[eq]={paymentId}");
            response = await _client.SendAsync(request).ConfigureAwait(false);
        }
        else
        {
            response = await _client.GetAsync($"/payments/{paymentId}").ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<PayrixPaymentResponse>(json, JsonOptions);
        return parsed?.Response?.Data.FirstOrDefault();
    }


    // ── Fetch line items for a transaction ───────────────────────────────────

    public async Task<List<TransactionItem>> GetTransactionItemsAsync(string txnId)
    {
        HttpResponseMessage response;

        if (_environment == PayrixEnvironment.Production)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/txnItems?page[limit]=50&page[number]=1");
            request.Headers.Add("search", $"txn[eq]={txnId}");
            response = await _client.SendAsync(request).ConfigureAwait(false);
        }
        else
        {
            // Sandbox: try fetching transaction with expanded items
            response = await _client.GetAsync($"/txns/{txnId}?expand[items][]").ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var txnJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var txnParsed = JsonSerializer.Deserialize<PayrixResponse>(txnJson, JsonOptions);
                var items = txnParsed?.Response?.Data.FirstOrDefault()?.Items;
                if (items is { Count: > 0 })
                    return items;
            }
            // Fallback: query txnItems directly
            response = await _client.GetAsync($"/txnItems?page[limit]=50&page[number]=1&search[txn][eq]={txnId}").ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
            return [];

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<PayrixItemsResponse>(json, JsonOptions);
        return parsed?.Response?.Data ?? [];
    }

    // ── Date helpers ──────────────────────────────────────────────────────────

    /// <summary>Formats a date as the Payrix created-field format "YYYY-MM-DD HH:MM:SS".</summary>
    private static List<Transaction> ApplyDateFilter(IEnumerable<Transaction> txns, DateTime? from, DateTime? to)
    {
        var q = txns;
        if (from.HasValue)
            q = q.Where(t => DateTime.TryParse(t.Created, out var d) && d.Date >= from.Value.Date);
        if (to.HasValue)
            q = q.Where(t => DateTime.TryParse(t.Created, out var d) && d.Date <= to.Value.Date);
        return q.ToList();
    }

    private static string DateToPayrixString(DateTime dt, bool endOfDay = false)
        => endOfDay
            ? dt.Date.ToString("yyyy-MM-dd") + " 23:59:59"
            : dt.Date.ToString("yyyy-MM-dd") + " 00:00:00";

    /// <summary>
    /// Builds the date-range search fragment to append to a query-string or header.
    /// Returns e.g. "search[created][gte]=2024-01-01 00:00:00&amp;search[created][lte]=2024-12-31 23:59:59"
    /// or an empty string if both dates are null.
    /// </summary>
    private static string DateQsFragment(DateTime? from, DateTime? to)
    {
        var parts = new List<string>();
        if (from.HasValue)
            parts.Add($"search[created][gte]={Uri.EscapeDataString(DateToPayrixString(from.Value))}");
        if (to.HasValue)
            parts.Add($"search[created][lte]={Uri.EscapeDataString(DateToPayrixString(to.Value, endOfDay: true))}");
        return parts.Count > 0 ? string.Join("&", parts) + "&" : "";
    }

    /// <summary>Same fragment but for the production header (no "search[...]" prefix, comma-separated).</summary>
    private static string DateHeaderFragment(DateTime? from, DateTime? to)
    {
        var parts = new List<string>();
        if (from.HasValue)
            parts.Add($"created[gte]={DateToPayrixString(from.Value)}");
        if (to.HasValue)
            parts.Add($"created[lte]={DateToPayrixString(to.Value, endOfDay: true)}");
        return parts.Count > 0 ? string.Join(",", parts) : "";
    }

    // ── Fetch transactions filtered by payment category ──────────────────────

    /// <summary>
    /// Fetches transactions filtered to a specific payment category.
    /// <paramref name="category"/> is "ach" (type=7,8), "card" (type=1-6), etc.
    /// When email is provided it is used as an additional server-side filter.
    /// All paths do a multi-type fetch + client-side filter so no results are missed.
    /// </summary>
    public async Task<(List<Transaction> transactions, string rawJson, string? error)>
        SearchByPaymentCategoryAsync(string? email, string category, int limit = 20,
                                     DateTime? fromDate = null, DateTime? toDate = null,
                                     string? merchantId = null)
    {
        bool IsMatch(Transaction t) => category switch
        {
            "ach"          => t.Type == 7 || t.Type == 8,
            // ACH Return = eCheck Sale (type 7) that came back — status 5 or returned date set
            "achreturn"    => t.Type == 7 && (t.Status == 5 || t.Returned != null),
            // ACH Refund = a separate eCheck Refund transaction (type 8)
            "achrefund"    => t.Type == 8,
            "card"         => t.Type >= 1 && t.Type <= 5,   // types 1–5 = CC; type 6 = Disbursement (excluded)
            // CC Refund = type 5 (Credit_Card_Refund_Transaction) — Payrix sends subject "captured" + type=5
            "ccrefund"     => t.Type == 5,
            // CC Return = type 4 with a returned date (chargeback on a Reverse Auth)
            "ccreturn"     => t.Type == 4 && t.Returned != null,
            "disbursement" => t.Type == 6,
            _              => true
        };

        bool hasEmail   = !string.IsNullOrWhiteSpace(email);

        string json = "{}";
        // Accumulate de-duped transactions from all API calls
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all  = new List<Transaction>();

        void Merge(List<Transaction> batch)
        {
            foreach (var t in batch)
                if (t.Id is not null && seen.Add(t.Id))
                    all.Add(t);
        }

        var dateQs      = DateQsFragment(fromDate, toDate);
        var dateHdr     = DateHeaderFragment(fromDate, toDate);
        bool hasMerchant = !string.IsNullOrWhiteSpace(merchantId);
        var txnBase      = hasMerchant
            ? $"/merchants/{Uri.EscapeDataString(merchantId!.Trim())}/txns"
            : "/txns";

        try
        {
            if (_environment == PayrixEnvironment.Production)
            {
                // ── Production: header-based search with pagination ───────────────
                async Task<List<Transaction>> ProdFetch(string? searchHeader = null)
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(searchHeader)) parts.Add(searchHeader);
                    if (!string.IsNullOrEmpty(dateHdr))      parts.Add(dateHdr.TrimEnd(','));
                    var collected = new List<Transaction>();
                    int pg = 1;
                    int srvTotal = int.MaxValue;
                    while (collected.Count < limit && collected.Count < srvTotal)
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get,
                            $"{txnBase}?page[limit]={PayrixPageSize}&page[number]={pg}&expand[items][]");
                        if (parts.Count > 0)
                            req.Headers.Add("search", string.Join(",", parts));
                        var resp = await _client.SendAsync(req).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode) break;
                        json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var p = JsonSerializer.Deserialize<PayrixResponse>(json, JsonOptions);
                        if (p?.Response?.Errors is { Count: > 0 }) break;
                        var data = p?.Response?.Data ?? [];
                        if (data.Count == 0) break;
                        if (pg == 1 && p?.Response?.Total is int pt && pt > 0) srvTotal = pt;
                        collected.AddRange(data);
                        if (srvTotal < int.MaxValue && collected.Count >= srvTotal) break;
                        pg++;
                    }
                    return collected;
                }

                if (hasEmail)
                {
                    // Try all email header variants Production uses; stop on first hit
                    foreach (var fmt in new[] { $"email[eq]={email}", $"email[equals]={email}", $"email[EQUALS]={email}" })
                    {
                        var batch = await ProdFetch(fmt);
                        Merge(batch);
                        if (all.Count > 0) break;
                    }
                }
                else
                {
                    // No email — broad fetch first, filter client-side.
                    // This is the most reliable path: Production may not honour type[eq] headers.
                    Merge(await ProdFetch());
                }

                // If the broad/email fetch yielded nothing, try type + status header variants.
                if (all.Count == 0)
                {
                    foreach (var typeVal in CategoryTypes(category))
                    {
                        foreach (var op in new[] { "eq", "equals", "EQUALS" })
                        {
                            // Add status filter for ACH Return and CC Return
                            var statusHeader = category switch
                            {
                                "achreturn" => $",status[{op}]=5",
                                "ccreturn"  => $",returned[gt]=0",
                                _           => ""
                            };
                            var batch = await ProdFetch($"type[{op}]={typeVal}{statusHeader}");
                            Merge(batch);
                            if (batch.Count > 0) break;
                        }
                    }
                }
            }
            else
            {
                // ── Sandbox: query-string based search with pagination ───────────
                async Task<List<Transaction>> SboxFetch(string qs)
                {
                    var sep = string.IsNullOrEmpty(qs) || string.IsNullOrEmpty(dateQs) ? "" : "&";
                    var collected = new List<Transaction>();
                    int pg = 1;
                    int srvTotal = int.MaxValue;
                    while (collected.Count < limit && collected.Count < srvTotal)
                    {
                        var resp = await _client.GetAsync(
                            $"{txnBase}?{dateQs}{sep}{qs}&page[limit]={PayrixPageSize}&page[number]={pg}&expand[items][]")
                            .ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode) break;
                        json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var p = JsonSerializer.Deserialize<PayrixResponse>(json, JsonOptions);
                        var data = p?.Response?.Data ?? [];
                        if (data.Count == 0) break;
                        if (pg == 1 && p?.Response?.Total is int st && st > 0) srvTotal = st;
                        collected.AddRange(data);
                        if (srvTotal < int.MaxValue && collected.Count >= srvTotal) break;
                        pg++;
                    }
                    return collected;
                }

                // Build the per-category type+status qualifier (e.g. achreturn adds status=5)
                string StatusQs(int typeVal) => category switch
                {
                    "achreturn" => $"search[type][eq]={typeVal}&search[status][eq]=5",
                    "achrefund" => $"search[type][eq]={typeVal}",
                    "ccreturn"  => $"search[type][eq]={typeVal}&search[returned][gt]=0",
                    _           => $"search[type][eq]={typeVal}"
                };

                if (hasEmail)
                {
                    var enc = Uri.EscapeDataString(email!);
                    foreach (var typeVal in CategoryTypes(category))
                        Merge(await SboxFetch($"search[email][EQUALS]={enc}&{StatusQs(typeVal)}"));

                    // Fallback: email only (type/status filter may not work on all sandbox endpoints)
                    if (all.Count == 0)
                        Merge(await SboxFetch($"search[email][EQUALS]={Uri.EscapeDataString(email!)}"));
                }
                else
                {
                    // No email: type + status-specific fetches
                    foreach (var typeVal in CategoryTypes(category))
                        Merge(await SboxFetch(StatusQs(typeVal)));

                    // Broad fallback: no filter — rely on client-side IsMatch
                    if (all.Count == 0)
                        Merge(await SboxFetch(""));
                }
            }
        }
        catch (Exception ex)
        {
            return ([], json, $"Fetch error: {ex.Message}");
        }

        IEnumerable<Transaction> candidates = all.Where(IsMatch);
        if (hasEmail)
            candidates = candidates.Where(t => string.Equals(t.Email, email, StringComparison.OrdinalIgnoreCase));

        var filtered = ApplyDateFilter(candidates, fromDate, toDate)
            .OrderByDescending(t => t.Created)
            .Take(limit)
            .ToList();

        return (filtered, json, filtered.Count == 0 ? $"No {CategoryLabel(category)} transactions found." : null);
    }

    // All Payrix type values to query for a given category
    private static int[] CategoryTypes(string category) => category switch
    {
        "ach"          => [7, 8],
        "achreturn"    => [7],   // eCheck Sale (type 7) that was returned — status 5
        "achrefund"    => [8],   // eCheck Refund (type 8) — a separate refund record
        "card"         => [1, 2, 3, 4, 5],   // type 6 = Disbursement — not a CC txn
        "ccrefund"     => [5],   // Credit_Card_Refund_Transaction = type 5
        "ccreturn"     => [4],
        "disbursement" => [6],
        _              => [1]
    };

    private static int CategoryFirstType(string category) => CategoryTypes(category)[0];

    private static string CategoryLabel(string category) => category switch
    {
        "ach"          => "ACH/eCheck",
        "achreturn"    => "ACH Return (eCheck Returned)",
        "achrefund"    => "ACH Refund (eCheck Refund)",
        "ccrefund"     => "CC Refund",
        "ccreturn"     => "CC Return",
        "disbursement" => "Disbursement",
        _              => "Credit Card"
    };

    // ── Fetch real ACH funded webhook from Payrix (reconstructed from transaction) ──

    /// <summary>
    /// Fetches the most recent funded eCheck Sale transaction (type=7, status=4 / funded≠null)
    /// from Payrix and reconstructs the "Your eCheck sale has been funded" webhook payload
    /// from its fields.  This is more reliable than querying notification logs.
    /// Returns (webhookPayload, rawTxnJson, transaction, error).
    /// </summary>
    public async Task<(string? webhookPayload, string rawJson, Transaction? txn, string? error)>
        GetAchFundedWebhookAsync(string? email = null, int limit = 20,
                                 DateTime? fromDate = null, DateTime? toDate = null)
    {
        string json = "{}";
        List<Transaction> candidates = [];

        var fetchLimit = Math.Max(limit * 5, 100);
        var dateQs  = DateQsFragment(fromDate, toDate);
        var dateHdr = DateHeaderFragment(fromDate, toDate);

        // Strategy A: search type=7 by email (if provided) or broad fetch
        async Task TryFetch(string endpoint, string? searchHeader)
        {
            try
            {
                HttpResponseMessage resp;
                if (searchHeader is not null)
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    req.Headers.Add("search", searchHeader);
                    resp = await _client.SendAsync(req).ConfigureAwait(false);
                }
                else
                {
                    resp = await _client.GetAsync(endpoint).ConfigureAwait(false);
                }
                if (!resp.IsSuccessStatusCode) return;
                var j = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                json = j;
                var p = JsonSerializer.Deserialize<PayrixResponse>(j, JsonOptions);
                var data = p?.Response?.Data ?? [];
                foreach (var t in data)
                    if (!candidates.Any(c => c.Id == t.Id))
                        candidates.Add(t);
            }
            catch { /* ignore, try next */ }
        }

        var emailParam = string.IsNullOrEmpty(email) ? null : Uri.EscapeDataString(email);

        var baseEmail = emailParam is null ? "" : $"search[email][EQUALS]={emailParam}&";

        if (_environment == PayrixEnvironment.Production)
        {
            // type=7, all statuses — one broad call + one per-status call to ensure none are skipped
            var hdrBase = string.IsNullOrEmpty(dateHdr) ? "type[eq]=7" : $"type[eq]=7,{dateHdr}";
            var tasks = new List<Task>
            {
                TryFetch($"/txns?page[limit]={fetchLimit}&page[number]=1", hdrBase),
            };
            if (!string.IsNullOrEmpty(email))
            {
                var hdrEmail = string.IsNullOrEmpty(dateHdr) ? $"email[eq]={email}" : $"email[eq]={email},{dateHdr}";
                tasks.Add(TryFetch($"/txns?page[limit]={fetchLimit}&page[number]=1", hdrEmail));
            }
            foreach (var status in new[] { 1, 2, 3, 4 })
                tasks.Add(TryFetch($"/txns?page[limit]={fetchLimit}&page[number]=1",
                                   hdrBase));   // production proxy may need separate hits
            await Task.WhenAll(tasks);
        }
        else
        {
            // Sandbox: broad type=7 + one call per status to catch everything
            var tasks = new List<Task>
            {
                TryFetch($"/txns?{dateQs}{baseEmail}search[type][eq]=7&page[limit]={fetchLimit}&page[number]=1", null),
            };
            foreach (var status in new[] { 1, 2, 3, 4 })
                tasks.Add(TryFetch(
                    $"/txns?{dateQs}{baseEmail}search[type][eq]=7&search[status][eq]={status}&page[limit]={fetchLimit}&page[number]=1",
                    null));
            await Task.WhenAll(tasks);

            // Broad fallback: email only, filter type=7 client-side
            if (candidates.Count == 0 && !string.IsNullOrEmpty(email))
                await TryFetch(
                    $"/txns?{dateQs}search[email][EQUALS]={emailParam}&page[limit]={fetchLimit}&page[number]=1",
                    null);
        }

        // Pick the best candidate: type=7, funded or status=4, newest first
        // Accept any type=7 regardless of status (Approved/Captured/Settled/Funded).
        // Sort: funded first, then settled, then by newest created.
        var funded = candidates
            .Where(t => t.Type == 7)
            .OrderByDescending(t => (
                !string.IsNullOrEmpty(t.Funded) ? 3 :         // fully funded
                (t.Status == 4 || t.Status == 3)  ? 2 :       // settled / captured
                1,                                              // approved — still processable
                t.Created ?? ""))
            .FirstOrDefault();

        if (funded is null)
            return (null, json, null, "No eCheck Sale (type=7) transactions found for this account.");

        var payload = WebhookTestService.BuildAchFundedPayloadFromTransaction(funded);
        return (payload, json, funded, null);
    }

    // ── Fetch transactions for a specific merchant ────────────────────────────
    // Tries the sub-resource path first; falls back to query-string filter if empty.
    public async Task<(List<Transaction> transactions, string rawJson, string? error)>
        GetMerchantTransactionsAsync(string merchantId, int limit = 20)
    {
        var enc = Uri.EscapeDataString(merchantId);

        async Task<(List<Transaction>, string, string?)> TryEndpoint(string endpoint)
        {
            var all = new List<Transaction>();
            var lastJson = "{}";
            int page = 1;
            try
            {
                int serverTotal = int.MaxValue;
                while (all.Count < limit && all.Count < serverTotal)
                {
                    var sep = endpoint.Contains('?') ? "&" : "?";
                    var resp = await _client.GetAsync(
                        $"{endpoint}{sep}page[limit]={PayrixPageSize}&page[number]={page}")
                        .ConfigureAwait(false);
                    lastJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return (all, lastJson, $"HTTP {(int)resp.StatusCode}");
                    var p = JsonSerializer.Deserialize<PayrixResponse>(lastJson, JsonOptions);
                    if (p?.Response?.Errors is { Count: > 0 })
                        return (all, lastJson, string.Join("  |  ", p.Response.Errors.Select(e => e.Summary)));
                    var data = p?.Response?.Data ?? [];
                    if (data.Count == 0) break;
                    if (page == 1 && p?.Response?.Total is int t && t > 0) serverTotal = t;
                    all.AddRange(data);
                    if (serverTotal < int.MaxValue && all.Count >= serverTotal) break;
                    page++;
                }
            }
            catch (Exception ex) { return (all, lastJson, $"Fetch error: {ex.Message}"); }
            // Client-side guard: only keep transactions that actually belong to this merchant
            var matched = all
                .Where(t => string.Equals(t.Merchant, merchantId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.Created)
                .Take(limit)
                .ToList();
            return (matched, lastJson, null);
        }

        // Strategy 1: sub-resource path
        var (txns1, json1, err1) = await TryEndpoint($"/merchants/{enc}/txns");
        if (err1 == null && txns1.Count > 0)
            return (txns1, json1, null);

        // Strategy 2: query-string filter on main /txns endpoint
        var (txns2, json2, err2) = await TryEndpoint($"/txns?search[merchant][EQUALS]={enc}");
        if (err2 == null && txns2.Count > 0)
            return (txns2, json2, null);

        // Return whatever we got (may be empty)
        var bestErr = err1 ?? err2;
        return (txns1.Count > 0 ? txns1 : txns2, txns1.Count > 0 ? json1 : json2, bestErr);
    }

    // ── Search full transactions (optionally filtered by email) ───────────────

    // Payrix hard-caps page[limit] at 100.  We paginate internally to honour larger limits.
    private const int PayrixPageSize = 100;

    public async Task<(List<Transaction> transactions, string rawJson, string? error)> SearchByEmailAsync(
        string? email, int limit = 20, DateTime? fromDate = null, DateTime? toDate = null,
        string? merchantId = null)
    {
        bool hasEmail    = !string.IsNullOrWhiteSpace(email);
        bool hasMerchant = !string.IsNullOrWhiteSpace(merchantId);
        // Payrix does not support search[merchant] filter on /txns.
        // Use the sub-resource path /merchants/{id}/txns when a merchant is specified.
        var txnBase    = hasMerchant
            ? $"/merchants/{Uri.EscapeDataString(merchantId!.Trim())}/txns"
            : "/txns";
        var dateQs  = DateQsFragment(fromDate, toDate);
        var dateHdr = DateHeaderFragment(fromDate, toDate);

        // ── No email supplied: paginate to collect up to `limit` latest transactions ──
        if (!hasEmail)
        {
            var all = new List<Transaction>();
            var lastJson = "{}";
            int page = 1;
            try
            {
                int serverTotal = int.MaxValue;
                while (all.Count < limit && all.Count < serverTotal)
                {
                    HttpResponseMessage resp;
                    if (_environment == PayrixEnvironment.Production && !string.IsNullOrEmpty(dateHdr))
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get,
                            $"{txnBase}?page[limit]={PayrixPageSize}&page[number]={page}&expand[items][]");
                        if (!string.IsNullOrEmpty(dateHdr)) req.Headers.Add("search", dateHdr.TrimEnd(','));
                        resp = await _client.SendAsync(req).ConfigureAwait(false);
                    }
                    else
                    {
                        var endpoint = $"{txnBase}?{dateQs}page[limit]={PayrixPageSize}&page[number]={page}&expand[items][]";
                        resp = await _client.GetAsync(endpoint).ConfigureAwait(false);
                    }
                    lastJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                        return (all.Count > 0 ? all : [], lastJson, $"HTTP {(int)resp.StatusCode}");

                    var p = JsonSerializer.Deserialize<PayrixResponse>(lastJson, JsonOptions);
                    if (p?.Response?.Errors is { Count: > 0 })
                        return (all.Count > 0 ? all : [], lastJson,
                            string.Join("  |  ", p.Response.Errors.Select(e => e.Summary)));

                    var data = p?.Response?.Data ?? [];
                    if (data.Count == 0) break;
                    // Use server-reported total on first page so we know how many pages exist
                    if (page == 1 && p?.Response?.Total is int t && t > 0) serverTotal = t;
                    all.AddRange(data);

                    if (serverTotal < int.MaxValue && all.Count >= serverTotal) break;
                    page++;
                }
            }
            catch (Exception ex) { return (all.Count > 0 ? all : [], lastJson, $"Fetch error: {ex.Message}"); }

            // Apply client-side date filter as a safety net in case server ignores the param
            var result = ApplyDateFilter(all, fromDate, toDate);
            return (result.OrderByDescending(t => t.Created).Take(limit).ToList(), lastJson, null);
        }

        // ── Email supplied — Production: header-based search with pagination ───
        if (_environment == PayrixEnvironment.Production)
        {
            var formats = new[] { $"email[eq]={email}", $"email[equals]={email}", $"email[EQUALS]={email}" };
            foreach (var searchVal in formats)
            {
                var all = new List<Transaction>();
                var lastJson = "{}";
                int page = 1;
                bool hadError = false;
                int serverTotal2 = int.MaxValue;
                while (all.Count < limit && all.Count < serverTotal2)
                {
                    var req = new HttpRequestMessage(HttpMethod.Get,
                        $"{txnBase}?page[limit]={PayrixPageSize}&page[number]={page}&expand[items][]");
                    var searchParts = new List<string> { searchVal };
                    if (!string.IsNullOrEmpty(dateHdr)) searchParts.Add(dateHdr.TrimEnd(','));
                    req.Headers.Add("search", string.Join(",", searchParts.Where(s => !string.IsNullOrEmpty(s))));
                    var resp = await _client.SendAsync(req).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) { hadError = true; break; }
                    lastJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var p = JsonSerializer.Deserialize<PayrixResponse>(lastJson, JsonOptions);
                    if (p?.Response?.Errors is { Count: > 0 }) { hadError = true; break; }
                    var data = p?.Response?.Data ?? [];
                    if (data.Count == 0) break;
                    if (page == 1 && p?.Response?.Total is int t2 && t2 > 0) serverTotal2 = t2;
                    all.AddRange(data);
                    if (serverTotal2 < int.MaxValue && all.Count >= serverTotal2) break;
                    page++;
                }
                if (hadError || all.Count == 0) continue;
                var filtered = ApplyDateFilter(
                        all.Where(t => string.Equals(t.Email, email, StringComparison.OrdinalIgnoreCase)),
                        fromDate, toDate)
                    .OrderByDescending(t => t.Created)
                    .Take(limit)
                    .ToList();
                if (filtered.Count > 0)
                    return (filtered, lastJson, null);
            }
            // All formats empty — return empty with diagnostic JSON
            var diagReq = new HttpRequestMessage(HttpMethod.Get,
                $"/txns?page[limit]={PayrixPageSize}&page[number]=1&expand[items][]");
            diagReq.Headers.Add("search", $"email[eq]={email}");
            var diagResp = await _client.SendAsync(diagReq).ConfigureAwait(false);
            var diagJson = await diagResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ([], diagJson, $"No transactions found for {email}.");
        }

        // ── Sandbox: query-string email filter with pagination ─────────────────
        {
            var all = new List<Transaction>();
            var lastJson = "{}";
            int page = 1;
            try
            {
                int serverTotal3 = int.MaxValue;
                while (all.Count < limit && all.Count < serverTotal3)
                {
                    var endpoint = $"{txnBase}?search[email][EQUALS]={Uri.EscapeDataString(email!)}" +
                                   $"&{dateQs}page[limit]={PayrixPageSize}&page[number]={page}&expand[items][]";
                    var resp = await _client.GetAsync(endpoint).ConfigureAwait(false);
                    lastJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) break;
                    var p = JsonSerializer.Deserialize<PayrixResponse>(lastJson, JsonOptions);
                    if (p?.Response?.Errors is { Count: > 0 }) break;
                    var data = p?.Response?.Data ?? [];
                    if (data.Count == 0) break;
                    if (page == 1 && p?.Response?.Total is int t3 && t3 > 0) serverTotal3 = t3;
                    all.AddRange(data);
                    if (serverTotal3 < int.MaxValue && all.Count >= serverTotal3) break;
                    page++;
                }
            }
            catch { /* fall through */ }

            var filtered = ApplyDateFilter(
                    all.Where(t => string.Equals(t.Email, email, StringComparison.OrdinalIgnoreCase)),
                    fromDate, toDate)
                .OrderByDescending(t => t.Created)
                .Take(limit)
                .ToList();

            return (filtered, lastJson, filtered.Count == 0
                ? $"No transactions found for {email}."
                : null);
        }
    }

    // ── Fetch latest N transaction IDs (optionally filtered by email) ─────────

    public async Task<(string[] txnIds, string? error)> GetLatestTransactionIdsAsync(string? email, int count = 5)
    {
        bool hasEmail = !string.IsNullOrWhiteSpace(email);
        HttpResponseMessage response;

        if (_environment == PayrixEnvironment.Production)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/txns?page[limit]={count}&page[number]=1");
            if (hasEmail)
                request.Headers.Add("search", $"email[equals]={email}");
            response = await _client.SendAsync(request).ConfigureAwait(false);
        }
        else
        {
            var endpoint = hasEmail
                ? $"/txns?search[email][EQUALS]={Uri.EscapeDataString(email!)}&page[limit]={count}&page[number]=1"
                : $"/txns?page[limit]={count}&page[number]=1";
            response = await _client.GetAsync(endpoint).ConfigureAwait(false);
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return ([], $"HTTP {(int)response.StatusCode} {response.StatusCode} — {json}");

        var parsed = JsonSerializer.Deserialize<PayrixResponse>(json, JsonOptions);
        var errors = parsed?.Response?.Errors;

        if (errors is { Count: > 0 })
            return ([], string.Join("  |  ", errors.Select(e => e.Summary)));

        var ids = parsed?.Response?.Data
            .OrderByDescending(t => t.Created)
            .Select(t => t.Id)
            .Where(id => id is not null)
            .Cast<string>()
            .ToArray() ?? [];

        return (ids, null);
    }

    // ── Fetch withdrawal / disbursement webhook ──────────────────────────────

    // ── Fetch disbursement entries (line items) for a disbursement ID ────────────

    /// <summary>
    /// Fetches all disbursement entries (line items) for a given disbursement ID
    /// from /disbursementEntries, with the entry sub-object expanded.
    /// Returns an empty list if none found.
    /// </summary>
    public async Task<List<DisbursementEntry>> GetDisbursementEntriesAsync(string disbId, int limit = 200)
    {
        var escaped = Uri.EscapeDataString(disbId);
        try
        {
            // Strategy 1: sub-resource path — URL-scoped, most reliable.
            var resp1 = await _client.GetAsync(
                $"/disbursements/{escaped}/disbursementEntries?page[limit]={limit}&page[number]=1&expand[entry][]").ConfigureAwait(false);

            if (resp1.IsSuccessStatusCode)
            {
                var j1 = await resp1.Content.ReadAsStringAsync().ConfigureAwait(false);
                var p1 = JsonSerializer.Deserialize<DisbursementEntryResponse>(j1, JsonOptions);
                var d1 = p1?.Response?.Data ?? [];
                if (d1.Count > 0) return d1;
            }

            // Strategy 2: query-string search with "equals" operator + expand
            var resp2 = await _client.GetAsync(
                $"/disbursementEntries?search[disbursement][equals]={escaped}&page[limit]={limit}&page[number]=1&expand[entry][]").ConfigureAwait(false);

            if (resp2.IsSuccessStatusCode)
            {
                var j2 = await resp2.Content.ReadAsStringAsync().ConfigureAwait(false);
                var p2 = JsonSerializer.Deserialize<DisbursementEntryResponse>(j2, JsonOptions);
                var d2 = p2?.Response?.Data ?? [];
                if (d2.Count > 0) return d2;
            }

            // Strategy 3: query-string search with "eq" operator (no expand)
            var resp3 = await _client.GetAsync(
                $"/disbursementEntries?search[disbursement][eq]={escaped}&page[limit]={limit}&page[number]=1").ConfigureAwait(false);

            if (!resp3.IsSuccessStatusCode) return [];

            var j3 = await resp3.Content.ReadAsStringAsync().ConfigureAwait(false);
            var p3 = JsonSerializer.Deserialize<DisbursementEntryResponse>(j3, JsonOptions);
            return p3?.Response?.Data ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Given a list of transaction IDs, returns a dictionary mapping each txnId → disbursementId.
    /// Makes a single bulk call to /disbursementEntries (no server-side filter) and matches client-side,
    /// which is more reliable on sandbox than per-txn eventId queries.
    /// </summary>
    public async Task<Dictionary<string, string>> GetDisbursementIdMapAsync(List<string> txnIds)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (txnIds.Count == 0) return map;

        var idSet = new HashSet<string>(txnIds, StringComparer.OrdinalIgnoreCase);

        try
        {
            // Strategy 1: bulk fetch recent entries (no filter) and match client-side
            foreach (var page in new[] { 1, 2, 3 })
            {
                var resp = await _client.GetAsync(
                    $"/disbursementEntries?page[limit]=200&page[number]={page}").ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) break;

                var json   = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<DisbursementEntryResponse>(json, JsonOptions);
                var data   = parsed?.Response?.Data ?? [];

                foreach (var e in data)
                    if (e.EventId is not null && e.Disbursement is not null && idSet.Contains(e.EventId))
                        map.TryAdd(e.EventId, e.Disbursement);

                // Stop paging once all transactions are resolved or no more data
                if (data.Count < 200 || map.Count == idSet.Count) break;
            }

            // Strategy 2: for any still-unresolved txnIds, try individual eventId queries
            var unresolved = txnIds.Where(id => !map.ContainsKey(id)).ToList();
            foreach (var txnId in unresolved)
            {
                var disbId = await GetDisbursementIdByTxnIdAsync(txnId).ConfigureAwait(false);
                if (disbId is not null)
                    map[txnId] = disbId;
            }
        }
        catch { /* best-effort */ }

        return map;
    }

    /// <summary>
    /// Given a transaction ID, searches /disbursementEntries where eventId == txnId
    /// to find the disbursement that contains that transaction.
    /// Returns the disbursement ID string, or null if not found.
    /// </summary>
    public async Task<string?> GetDisbursementIdByTxnIdAsync(string txnId)
    {
        var escaped = Uri.EscapeDataString(txnId);
        try
        {
            // eventId on a disbursement entry stores the txn/refund/chargeback ID
            foreach (var op in new[] { "equals", "eq" })
            {
                var resp = await _client.GetAsync(
                    $"/disbursementEntries?search[eventId][{op}]={escaped}&page[limit]=5&page[number]=1").ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;

                var json   = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<DisbursementEntryResponse>(json, JsonOptions);
                // Prefer an exact eventId match; fall back to first entry with a disbursement ID
                var entries = parsed?.Response?.Data ?? [];
                var disbId = entries.FirstOrDefault(e => e.EventId == txnId)?.Disbursement
                          ?? entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Disbursement))?.Disbursement;
                if (!string.IsNullOrEmpty(disbId)) return disbId;
            }
            return null;
        }
        catch { return null; }
    }

    // ── Create / fetch entities (merchants) ──────────────────────────────────

    /// <summary>
    /// Creates a new entity (merchant) in Payrix by POSTing to /entities.
    /// The payload matches the "Entities created successfully" webhook structure.
    /// Returns (entityId, rawJson, error).
    /// </summary>
    public async Task<(string? entityId, string rawJson, string? error)>
        CreateEntityAsync(
            string  name,
            string  email,
            string  custom,
            string  address1      = "",
            string  city          = "",
            string  state         = "",
            string  zip           = "",
            string  country       = "USA",
            string  phone         = "",
            string  ein           = "",
            string  website       = "",
            string  timezone      = "cst",
            int     type          = 2,
            string  ownerFirst    = "",
            string  ownerLast     = "",
            string  ownerEmail    = "",
            string  ownerPhone    = "",
            // Complete member fields for full merchant info in Payrix portal
            string  ownerDob      = "01/01/1980",
            string  ownerSsn      = "123456789",  // test SSN for sandbox
            string  ownerAddress1 = "",
            string  ownerCity     = "",
            string  ownerState    = "",
            string  ownerZip      = "",
            decimal ownerOwnership = 100)
    {
        object? membersArray = null;
        if (!string.IsNullOrWhiteSpace(ownerFirst) || !string.IsNullOrWhiteSpace(ownerLast))
        {
            membersArray = new[]
            {
                new
                {
                    first     = ownerFirst,
                    last      = ownerLast,
                    email     = string.IsNullOrEmpty(ownerEmail)    ? email    : ownerEmail,
                    phone     = string.IsNullOrEmpty(ownerPhone)    ? phone    : ownerPhone,
                    address1  = string.IsNullOrEmpty(ownerAddress1) ? address1 : ownerAddress1,
                    city      = string.IsNullOrEmpty(ownerCity)     ? city     : ownerCity,
                    state     = string.IsNullOrEmpty(ownerState)    ? state    : ownerState,
                    zip       = string.IsNullOrEmpty(ownerZip)      ? zip      : ownerZip,
                    country   = "USA",
                    title     = "Owner",
                    dob       = ownerDob,
                    ssn       = ownerSsn,
                    ownership = ownerOwnership
                }
            };
        }

        var body = new
        {
            type,
            name,
            email,
            custom,
            address1,
            city,
            state,
            zip,
            country,
            phone,
            website,
            ein,
            timezone,
            tcVersion    = "1.0",
            tcDate       = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            tcIp         = "127.0.0.1",
            locations    = 1,
            currency     = "USD"
        };

        try
        {
            // Build JSON manually so we can conditionally include members
            static string R(string? v, string fb) => string.IsNullOrWhiteSpace(v) ? fb : v.Trim();
            // Entity required fields — no nulls/empty
            var dict = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["type"]      = body.type,
                ["name"]      = R(body.name,     "BQE Merchant"),
                ["custom"]    = R(body.custom,   ""),
                ["email"]     = R(body.email,    "merchant@bqe.com"),
                ["phone"]     = R(body.phone,    "2132000000"),
                ["address1"]  = R(body.address1, "123 Main St"),
                ["city"]      = R(body.city,     "Los Angeles"),
                ["state"]     = R(body.state,    "CA"),
                ["zip"]       = R(body.zip,      "90001"),
                ["country"]   = R(body.country,  "USA"),
                ["website"]   = R(body.website,  "https://www.bqe.com"),
                ["ein"]       = R(body.ein,      "897978978"),
                ["timezone"]  = R(body.timezone, "cst"),
                ["tcVersion"] = body.tcVersion,
                ["tcDate"]    = body.tcDate,
                ["tcIp"]      = body.tcIp,
                ["locations"] = body.locations,
                ["currency"]  = body.currency,
            };
            if (membersArray != null)
                dict["members"] = membersArray;

            var json    = System.Text.Json.JsonSerializer.Serialize(dict, JsonOptions);
            var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var resp    = await _client.PostAsync("/entities", content).ConfigureAwait(false);
            var rawJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, $"REQUEST:\n{json}\n\nRESPONSE:\n{rawJson}",
                    ExtractPayrixError(rawJson) ?? $"HTTP {(int)resp.StatusCode}");

            var parsed = System.Text.Json.JsonSerializer.Deserialize<PayrixResponse>(rawJson, JsonOptions);

            // Check for API-level errors including member/field validation (HTTP 200 but errors array populated)
            var errors = parsed?.Response?.Errors;
            if (errors is { Count: > 0 })
            {
                var errDetail = string.Join("  |  ", errors.Select(e =>
                    $"[{e.Field ?? e.ErrorCode ?? "?"}] {e.Summary} (code {e.Code})"));
                return (null, $"REQUEST:\n{json}\n\nRESPONSE:\n{rawJson}", errDetail);
            }

            var entityId = parsed?.Response?.Data?.FirstOrDefault()?.Id;
            if (string.IsNullOrEmpty(entityId))
                return (null, $"REQUEST:\n{json}\n\nRESPONSE:\n{rawJson}",
                    "Entity created but ID not returned in response.");

            // ── Create member separately via POST /members ───────────────────
            // Payrix ignores inline 'members' in entity creation body — must POST separately
            if (membersArray is Array memberArr && memberArr.Length > 0)
            {
                try
                {
                    var m = memberArr.GetValue(0)!;

                    static string S(object obj, string prop)
                    {
                        try { return obj.GetType().GetProperty(prop)?.GetValue(obj)?.ToString() ?? ""; }
                        catch { return ""; }
                    }

                    // Build member payload — all Payrix member fields
                    // Payrix form fields: first, last, email, phone, address1, city, state, zip,
                    //                    country, dob (MM/DD/YYYY), ssn (9 digits), ownership (0-100),
                    //                    title, type (1=owner)
                    string sv(string prop) => S(m, prop);
                    // nz: return val if non-empty, else fallback — NO NULLS for required fields
                    string req(string val, string fallback) => string.IsNullOrWhiteSpace(val) ? fallback : val.Trim();

                    // ── Required member fields — all must have values ────────────
                    var firstName  = req(sv("first"),    "Owner");
                    var lastName   = req(sv("last"),     "Member");
                    var memberEmail= req(sv("email"),    "merchant@bqe.com");
                    var memberPhone= req(sv("phone"),    "2132000000");  // required, 10 digits
                    var addr1      = req(sv("address1"), "123 Main St");
                    var mCity      = req(sv("city"),     "Los Angeles");
                    var mState     = req(sv("state"),    "CA");
                    var mZip       = req(sv("zip"),      "90001");
                    var mCountry   = req(sv("country"),  "USA");
                    var dob        = req(sv("dob"),      "19841031");  // MM-DD-YYYY
                    var title      = req(sv("title"),    "Owner");
                    var ownership  = decimal.TryParse(sv("ownership"), out var ov) ? (int)ov : 100;
                    var ssnRaw     = System.Text.RegularExpressions.Regex.Replace(sv("ssn"), @"\D", "");
                    var ssn        = ssnRaw.Length == 9 ? ssnRaw : "767567272";  // sandbox test SSN

                    var memberDict = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["entity"]    = entityId,       // required
                        ["type"]      = 1,              // required: 1 = owner/signer
                        ["first"]     = firstName,      // required
                        ["last"]      = lastName,       // required
                        ["email"]     = memberEmail,    // required
                        ["phone"]     = memberPhone,    // required
                        ["address1"]  = addr1,          // required
                        ["city"]      = mCity,           // required
                        ["state"]     = mState,          // required
                        ["zip"]       = mZip,            // required
                        ["country"]   = mCountry,        // required
                        ["dob"]       = dob,            // required: YYYYMMDD format
                        ["ssn"]       = ssn,            // required: 9 digits
                        ["ownership"] = ownership,      // required: 0-100
                        ["title"]     = title,          // required
                    };
                    // No removal of empty strings — all fields are guaranteed non-empty above

                    var memberPayload = JsonSerializer.Serialize(memberDict, JsonOptions);
                    var memberContent = new System.Net.Http.StringContent(memberPayload, System.Text.Encoding.UTF8, "application/json");
                    var memberResp    = await _client.PostAsync("/members", memberContent).ConfigureAwait(false);
                    var memberJson    = await memberResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    rawJson = $"ENTITY:\n{rawJson}\n\nPOST /members HTTP {(int)memberResp.StatusCode}:\nREQ: {memberPayload}\nRESP: {memberJson}";
                }
                catch (Exception mex)
                {
                    rawJson = $"{rawJson}\n\nPOST /members ERROR: {mex.Message}";
                }
            }

            return (entityId, rawJson, null);
        }
        catch (Exception ex)
        {
            return (null, "{}", $"Exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates an existing Payrix entity (name, email, address, members/owner).
    /// Used to fix wrong company name / owner on previously created entities.
    /// </summary>
    public async Task<(string rawJson, string? error)> UpdateEntityAsync(
        string  entityId,
        string  name,
        string  custom     = "",
        string  email      = "",
        string  phone      = "",
        string  address1   = "",
        string  city       = "",
        string  state      = "",
        string  zip        = "",
        string  website    = "",
        string  ownerFirst = "",
        string  ownerLast  = "",
        string  ownerEmail = "",
        string  ownerPhone = "")
    {
        var json = "{}";
        try
        {
            object? membersArray = null;
            if (!string.IsNullOrWhiteSpace(ownerFirst) || !string.IsNullOrWhiteSpace(ownerLast))
            {
                membersArray = new[]
                {
                    new
                    {
                        first   = ownerFirst,
                        last    = ownerLast,
                        email   = string.IsNullOrEmpty(ownerEmail) ? email : ownerEmail,
                        phone   = string.IsNullOrEmpty(ownerPhone) ? phone : ownerPhone,
                        country = "USA",
                        title   = "Owner"
                    }
                };
            }

            var body = new
            {
                name,
                custom,
                email,
                phone,
                address1,
                city,
                state,
                zip,
                website,
                members = membersArray
            };

            var payload = System.Text.Json.JsonSerializer.Serialize(body, JsonOptions);
            var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");

            // Try PUT first; fall back to PATCH
            var resp = await _client.PutAsync($"/entities/{Uri.EscapeDataString(entityId)}", content).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
            {
                var patchReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Patch,
                    $"/entities/{Uri.EscapeDataString(entityId)}")
                { Content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json") };
                resp = await _client.SendAsync(patchReq).ConfigureAwait(false);
            }

            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (json, ExtractPayrixError(json) ?? $"HTTP {(int)resp.StatusCode}");

            return (json, null);
        }
        catch (Exception ex) { return (json, $"UpdateEntity error: {ex.Message}"); }
    }

    /// <summary>
    /// Fetches the most recent N disbursement records from /disbursements.
    /// </summary>
    public async Task<List<DisbursementRecord>> GetLatestDisbursementsAsync(int limit = 20, string? merchantId = null)
    {
        try
        {
            var disbBase = !string.IsNullOrWhiteSpace(merchantId)
                ? $"/merchants/{Uri.EscapeDataString(merchantId!.Trim())}/disbursements"
                : "/disbursements";
            var resp = await _client.GetAsync(
                $"{disbBase}?page[limit]={limit}&page[number]=1").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];

            var json   = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<DisbursementResponse>(json, JsonOptions);
            return parsed?.Response?.Data
                       ?.OrderByDescending(r => r.Created)
                       .ToList() ?? [];
        }
        catch { return []; }
    }

    /// <summary>
    /// Fetches a disbursement record from /disbursements AND its line items from
    /// /disbursementEntries, then builds the withdrawal webhook payload with the
    /// entries embedded so Core can process the deposit without a second Payrix call.
    /// If <paramref name="disbId"/> is supplied, fetches that specific record; otherwise returns
    /// the most-recent one (filtered by email if provided).
    /// </summary>
    public async Task<(string? webhookPayload, string rawJson, DisbursementRecord? record, List<DisbursementEntry> entries, string? error)>
        GetWithdrawalWebhookAsync(string? disbId = null, string? email = null, int limit = 20,
                                  DateTime? fromDate = null, DateTime? toDate = null)
    {
        string json = "{}";
        var dateQs = DateQsFragment(fromDate, toDate);
        try
        {
            DisbursementRecord? rec = null;

            if (!string.IsNullOrEmpty(disbId))
            {
                // Try direct ID lookup first
                var resp = await _client.GetAsync($"/disbursements/{disbId}").ConfigureAwait(false);
                json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    var p = JsonSerializer.Deserialize<DisbursementResponse>(json, JsonOptions);
                    rec = p?.Response?.Data?.FirstOrDefault(r => r.Id == disbId)
                       ?? p?.Response?.Data?.FirstOrDefault();
                }

                // Fallback: query-string search
                if (rec is null)
                {
                    var qs = await _client.GetAsync(
                        $"/disbursements?search[id][eq]={disbId}&page[limit]=5&page[number]=1").ConfigureAwait(false);
                    if (qs.IsSuccessStatusCode)
                    {
                        json = await qs.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var p = JsonSerializer.Deserialize<DisbursementResponse>(json, JsonOptions);
                        rec = p?.Response?.Data?.FirstOrDefault();
                    }
                }

                if (rec is null)
                    return (null, json, null, [], $"Disbursement '{disbId}' not found.");
            }
            else
            {
                // Fetch most-recent disbursement (with optional date filter)
                var endpoint = string.IsNullOrEmpty(email)
                    ? $"/disbursements?{dateQs}page[limit]={limit}&page[number]=1"
                    : $"/disbursements?{dateQs}search[email][EQUALS]={Uri.EscapeDataString(email)}&page[limit]={limit}&page[number]=1";

                var resp = await _client.GetAsync(endpoint).ConfigureAwait(false);
                json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                    return (null, json, null, [], $"HTTP {(int)resp.StatusCode} fetching disbursements.");

                var p = JsonSerializer.Deserialize<DisbursementResponse>(json, JsonOptions);
                if (p?.Response?.Errors is { Count: > 0 })
                    return (null, json, null, [], string.Join("; ", p.Response.Errors.Select(e => e.Msg)));

                rec = p?.Response?.Data?.OrderByDescending(r => r.Created).FirstOrDefault();
                if (rec is null)
                    return (null, json, null, [], "No disbursements found.");
            }

            // Fetch line items for this disbursement ID
            var entries = await GetDisbursementEntriesAsync(rec.Id!).ConfigureAwait(false);

            // Build payload with entries embedded
            var wp = WebhookTestService.BuildWithdrawalPayloadFromRecord(rec, entries: entries);
            return (wp, json, rec, entries, null);
        }
        catch (Exception ex)
        {
            return (null, json, null, [], $"Fetch error: {ex.Message}");
        }
    }

    // ── Fetch entity → parse custom (AccountID,CompanyID) ────────────────────

    /// <summary>
    /// Calls GET /entities/{entityId} and returns the parsed AccountID and CompanyID
    /// from the entity's <c>custom</c> field ("AccountID,CompanyID" format).
    /// </summary>
    /// <summary>
    /// Fetches all transactions belonging to a specific Payrix disbursement ID.
    /// Tries /disbursements/{id}/txns first, then falls back to /txns?search[disbursement][id][eq]=.
    /// </summary>
    public async Task<(List<Transaction> transactions, string rawJson, string? error)>
        GetTransactionsByDisbursementAsync(string disbursementId, int limit = 100)
    {
        string json = "{}";
        try
        {
            // Strategy 1: sub-resource path
            var resp1 = await _client.GetAsync(
                $"/disbursements/{Uri.EscapeDataString(disbursementId)}/txns?page[limit]={limit}&page[number]=1&expand[items][]").ConfigureAwait(false);
            json = await resp1.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resp1.IsSuccessStatusCode)
            {
                var p1 = JsonSerializer.Deserialize<PayrixResponse>(json, JsonOptions);
                if (p1?.Response?.Data is { Count: > 0 })
                    return (p1.Response.Data, json, null);
            }

            // Strategy 2: filter param
            var resp2 = await _client.GetAsync(
                $"/txns?search[disbursement][id][eq]={Uri.EscapeDataString(disbursementId)}&page[limit]={limit}&page[number]=1&expand[items][]").ConfigureAwait(false);
            json = await resp2.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resp2.IsSuccessStatusCode)
            {
                var p2 = JsonSerializer.Deserialize<PayrixResponse>(json, JsonOptions);
                if (p2?.Response?.Data is { Count: > 0 })
                    return (p2.Response.Data, json, null);
            }

            return ([], json, $"No transactions found for disbursement '{disbursementId}'.");
        }
        catch (Exception ex)
        {
            return ([], json, $"Disbursement fetch error: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches a single entity record and returns its id, login, custom and raw JSON.
    /// • If <paramref name="entityId"/> is supplied → direct GET /entities/{id}
    /// • If <paramref name="customFilter"/> is supplied (AccountID,CompanyID) →
    ///     1. Try Payrix API filter first (fast path)
    ///     2. Fall back to page-walking up to 10 pages, matching custom field client-side
    /// • Otherwise → most recent entity (page 1, limit 1)
    /// </summary>
    public async Task<(string? id, string? login, string? name, string? custom, string rawJson, string? error)>
        GetEntityAsync(string? entityId = null, string? customFilter = null)
    {
        string json = "{}";
        try
        {
            // ── Direct lookup by ID ────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(entityId))
            {
                var resp = await _client.GetAsync($"/entities/{Uri.EscapeDataString(entityId)}").ConfigureAwait(false);
                json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var p   = JsonSerializer.Deserialize<EntityResponse>(json, JsonOptions);
                    var rec = p?.Response?.Data?.FirstOrDefault();
                    if (rec is not null)
                        return (rec.Id, rec.Login, rec.Name, rec.Custom, json, null);
                }
                return (null, null, null, null, json, $"Entity not found (HTTP {(int)resp.StatusCode}).");
            }

            // ── Search by custom field (AccountID,CompanyID) ────────────────────
            if (!string.IsNullOrWhiteSpace(customFilter))
            {
                // Strategy 1: API-level filter (may not be supported by all Payrix envs)
                var filterResp = await _client.GetAsync(
                    $"/entities?search[custom][eq]={Uri.EscapeDataString(customFilter)}&page[limit]=50&page[number]=1")
                    .ConfigureAwait(false);
                var filterJson = await filterResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (filterResp.IsSuccessStatusCode)
                {
                    var fp  = JsonSerializer.Deserialize<EntityResponse>(filterJson, JsonOptions);
                    var hit = fp?.Response?.Data?.FirstOrDefault(r =>
                        string.Equals(r.Custom?.Trim(), customFilter.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (hit is not null)
                        return (hit.Id, hit.Login, hit.Name, hit.Custom, filterJson, null);
                }

                // Strategy 2: page-walk up to 10 pages (100 entities), match custom client-side
                for (int page = 1; page <= 10; page++)
                {
                    var pageResp = await _client.GetAsync(
                        $"/entities?page[limit]=50&page[number]={page}")
                        .ConfigureAwait(false);
                    var pageJson = await pageResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!pageResp.IsSuccessStatusCode) break;

                    var pp   = JsonSerializer.Deserialize<EntityResponse>(pageJson, JsonOptions);
                    var data = pp?.Response?.Data;
                    if (data is null || data.Count == 0) break;   // no more pages

                    var match = data.FirstOrDefault(r =>
                        string.Equals(r.Custom?.Trim(), customFilter.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                        return (match.Id, match.Login, match.Name, match.Custom, pageJson, null);
                }

                return (null, null, null, null, json,
                    $"No entity found with custom = \"{customFilter}\" in Payrix.");
            }

            // ── Fallback: most recent entity ───────────────────────────────────
            {
                var resp = await _client.GetAsync("/entities?page[limit]=1&page[number]=1").ConfigureAwait(false);
                json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var p   = JsonSerializer.Deserialize<EntityResponse>(json, JsonOptions);
                    var rec = p?.Response?.Data?.FirstOrDefault();
                    if (rec is not null)
                        return (rec.Id, rec.Login, rec.Name, rec.Custom, json, null);
                }
                return (null, null, null, null, json, $"Entity not found (HTTP {(int)resp.StatusCode}).");
            }
        }
        catch (Exception ex)
        {
            return (null, null, null, null, json, $"Entity fetch error: {ex.Message}");
        }
    }

    public async Task<(string? accountId, string? companyId, string? entityName, string rawJson, string? error)>
        GetEntityCustomAsync(string entityId)
    {
        string json = "{}";
        try
        {
            // Strategy 1: direct path
            var resp = await _client.GetAsync($"/entities/{Uri.EscapeDataString(entityId)}").ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                var p = JsonSerializer.Deserialize<EntityResponse>(json, JsonOptions);
                var rec = p?.Response?.Data?.FirstOrDefault();
                if (rec is not null)
                {
                    var (accountId, companyId) = rec.ParseCustom();
                    return (accountId, companyId, rec.Name, json, null);
                }
            }

            // Strategy 2: query-string search
            var qs = await _client.GetAsync(
                $"/entities?search[id][eq]={Uri.EscapeDataString(entityId)}&page[limit]=1&page[number]=1").ConfigureAwait(false);
            json = await qs.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (qs.IsSuccessStatusCode)
            {
                var p2 = JsonSerializer.Deserialize<EntityResponse>(json, JsonOptions);
                var rec2 = p2?.Response?.Data?.FirstOrDefault();
                if (rec2 is not null)
                {
                    var (accountId, companyId) = rec2.ParseCustom();
                    return (accountId, companyId, rec2.Name, json, null);
                }
            }

            return (null, null, null, json, $"Entity {entityId} not found or has no custom field.");
        }
        catch (Exception ex)
        {
            return (null, null, null, json, $"Entity fetch error: {ex.Message}");
        }
    }

    // ── Merchants ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches ALL transactions in the given date range, auto-paginating through all pages.
    /// Used by the Reports tab to work on user-supplied dates rather than the pre-loaded cache.
    /// </summary>
    public async Task<(List<Transaction> transactions, string? error)>
        FetchTransactionsForReportAsync(DateTime? from, DateTime? to,
                                        string? merchantId = null,
                                        int pageSize = 200,
                                        IProgress<string>? progress = null)
    {
        var all  = new List<Transaction>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastError = (string?)null;
        int page = 1;

        var dateQs  = DateQsFragment(from, to);
        var dateHdr = DateHeaderFragment(from, to);

        // Merchant ID filter fragments
        var merchQs  = string.IsNullOrWhiteSpace(merchantId) ? "" :
                       $"search[merchant][EQUALS]={Uri.EscapeDataString(merchantId.Trim())}&";
        var merchHdr = string.IsNullOrWhiteSpace(merchantId) ? "" :
                       $"merchant[eq]={merchantId.Trim()}";

        try
        {
            while (true)
            {
                progress?.Report($"Fetching page {page}… ({all.Count} so far)");

                List<Transaction> batch;

                if (_environment == PayrixEnvironment.Production)
                {
                    var req = new HttpRequestMessage(HttpMethod.Get,
                        $"/txns?page[limit]={pageSize}&page[number]={page}&expand[items][]");

                    // Combine date + merchant into one search header
                    var hdrParts = new List<string>();
                    if (!string.IsNullOrEmpty(dateHdr))  hdrParts.Add(dateHdr);
                    if (!string.IsNullOrEmpty(merchHdr)) hdrParts.Add(merchHdr);
                    if (hdrParts.Count > 0)
                        req.Headers.Add("search", string.Join(",", hdrParts));

                    var resp = await _client.SendAsync(req).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        lastError = $"HTTP {(int)resp.StatusCode} on page {page}";
                        break;
                    }
                    var j = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    batch = JsonSerializer.Deserialize<PayrixResponse>(j, JsonOptions)?.Response?.Data ?? [];
                }
                else
                {
                    // Sandbox: date range + merchant in query-string
                    var url = $"/txns?{dateQs}{merchQs}page[limit]={pageSize}&page[number]={page}&expand[items][]";
                    var resp = await _client.GetAsync(url).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        lastError = $"HTTP {(int)resp.StatusCode} on page {page}";
                        break;
                    }
                    var j = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    batch = JsonSerializer.Deserialize<PayrixResponse>(j, JsonOptions)?.Response?.Data ?? [];
                }

                foreach (var t in batch)
                    if (t.Id is not null && seen.Add(t.Id))
                        all.Add(t);

                if (batch.Count < pageSize) break;   // last page
                page++;
            }
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }

        return (all, lastError);
    }

    // ── Single-call full merchant signup via POST /logins ────────────────────
    // This is the same flow BQE Core uses: one atomic call that creates login + entity +
    // merchant + member + bank account together.  Payrix auto-boards the merchant
    // (sets status=2 / autoBoarded=1) immediately for sandbox because all required
    // KYC data arrives at once.  Separate /entities→/merchants→/members calls leave
    // the merchant at status=0 because Payrix never receives the member data atomically.

    /// <summary>
    /// Replicates BQE Core's Payrix merchant signup: POST /logins with a fully-nested
    /// entity + merchant + member + bank-account payload.
    ///
    /// On success, Payrix auto-boards the merchant (status=2, autoBoarded=1).
    /// Returns the new entity ID (to look up the merchant via GET /merchants?entity=...).
    ///
    /// Response does not include the merchant ID directly — caller must call
    /// GetMerchantByEntityAsync after a brief delay to retrieve it.
    /// </summary>
    public async Task<(string? entityId, string? loginId, string rawJson, string? error)>
        SignUpViaLoginsAsync(
            // Login credentials (Payrix portal login for the merchant)
            string loginEmail,
            string loginPassword    = "BqeTest@2024!",  // default sandbox password
            string loginFirst       = "Owner",
            string loginLast        = "Admin",
            // Entity (company) details
            string companyName      = "BQE Merchant",
            string companyCustom    = "",   // REQUIRED: "accountId,companyId" — used for webhook routing
            string companyEmail     = "",
            string companyPhone     = "2132000000",
            string companyAddress1  = "123 Main St",
            string companyCity      = "Los Angeles",
            string companyState     = "CA",
            string companyZip       = "90001",
            string companyEin       = "897978978",
            string companyWebsite   = "https://www.bqe.com",
            // Merchant details
            string dba              = "",
            string mcc              = "8931",
            // Owner/member details
            string ownerFirst       = "Owner",
            string ownerLast        = "Member",
            string ownerEmail       = "",
            string ownerPhone       = "",
            string ownerAddress1    = "",
            string ownerCity        = "",
            string ownerState       = "CA",
            string ownerZip         = "",
            string ownerDob         = "19841031",   // YYYYMMDD
            string ownerSsn         = "767567272",  // 9-digit test SSN
            int    ownerOwnership   = 100,
            string ownerTitle       = "President",
            // Bank / settlement account (ACH checking)
            string routingNumber    = "021000021",   // Chase test routing
            string accountNumber    = "123456789012")
    {
        var json = "{}";
        try
        {
            static string R(string? v, string fb) => string.IsNullOrWhiteSpace(v) ? fb : v.Trim();
            var ssnDigits = System.Text.RegularExpressions.Regex.Replace(ownerSsn ?? "", @"\D", "");
            if (ssnDigits.Length != 9) ssnDigits = "767567272";

            var body = new System.Collections.Generic.Dictionary<string, object>
            {
                ["roles"]        = 1,
                ["username"]     = System.Text.RegularExpressions.Regex.Replace(
                                     R(loginEmail, "merchant@bqe.com").Split('@')[0],
                                     @"[^a-zA-Z0-9]", "")
                                 + DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["password"]     = loginPassword,
                ["first"]        = R(loginFirst,  "Owner"),
                ["last"]         = R(loginLast,   "Admin"),
                ["email"]        = R(loginEmail,  "merchant@bqe.com"),
                ["portalAccess"] = 1,
                ["entities"]     = new[]
                {
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["type"]      = 2,   // 2 = LLC (Sole Proprietor/type=0 rejected by Payrix even with 100% ownership)
                        ["name"]      = R(companyName, "BQE Merchant"),
                        ["ein"]       = R(companyEin,  "897978978"),
                        ["website"]   = R(companyWebsite, "https://www.bqe.com"),
                        ["tcVersion"] = "1.0",
                        ["currency"]  = "USD",
                        ["custom"]    = R(companyCustom, ""),  // "accountId,companyId" — critical for webhook routing
                        ["email"]     = R(companyEmail,  R(loginEmail, "merchant@bqe.com")),
                        ["phone"]     = R(companyPhone,  "2132000000"),
                        ["address1"]  = R(companyAddress1, "123 Main St"),
                        ["city"]      = R(companyCity,     "Los Angeles"),
                        ["state"]     = R(companyState,    "CA"),
                        ["zip"]       = R(companyZip,      "90001"),
                        ["country"]   = "USA",
                        ["accounts"]  = new[]
                        {
                            new System.Collections.Generic.Dictionary<string, object>
                            {
                                ["primary"]  = 1,
                                ["currency"] = "USD",
                                ["account"]  = new System.Collections.Generic.Dictionary<string, object>
                                {
                                    ["method"]  = 8,             // 8 = CheckingAccount (PayrixMethod enum)
                                    ["number"]  = R(accountNumber, "123456789012"),
                                    ["routing"] = R(routingNumber, "021000021"),
                                }
                            }
                        },
                        ["merchant"]  = new System.Collections.Generic.Dictionary<string, object>
                        {
                            ["dba"]     = R(string.IsNullOrEmpty(dba) ? companyName : dba, "BQE MERCHANT").ToUpperInvariant(),
                            ["new"]     = 1,
                            ["mcc"]     = R(mcc, "8931"),
                            ["status"]  = 0,  // Payrix sets status=2 internally on boarding approval
                            ["members"] = new[]
                            {
                                new System.Collections.Generic.Dictionary<string, object>
                                {
                                    ["first"]     = R(ownerFirst,    "Owner"),
                                    ["last"]      = R(ownerLast,     "Member"),
                                    ["email"]     = R(ownerEmail,    R(loginEmail, "merchant@bqe.com")),
                                    ["phone"]     = R(ownerPhone,    R(companyPhone, "2132000000")),
                                    ["address1"]  = R(ownerAddress1, R(companyAddress1, "123 Main St")),
                                    ["city"]      = R(ownerCity,     R(companyCity, "Los Angeles")),
                                    ["state"]     = R(ownerState,    "CA"),
                                    ["zip"]       = R(ownerZip,      R(companyZip, "90001")),
                                    ["country"]   = "USA",
                                    ["title"]     = R(ownerTitle,    "President"),
                                        ["primary"]   = 1,       // integer, not string — required for portal to show member details
                                    ["ownership"] = ownerOwnership,
                                    ["ssn"]       = long.Parse(ssnDigits),                          // integer, not string
                                    ["dob"]       = int.Parse(R(ownerDob, "19841031")),              // integer YYYYMMDD, not string
                                }
                            }
                        }
                    }
                }
            };

            var payload = JsonSerializer.Serialize(body, JsonOptions);
            var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp    = await _client.PostAsync("/logins", content).ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, null, $"REQUEST:\n{payload}\n\nRESPONSE:\n{json}",
                    ExtractPayrixError(json) ?? $"HTTP {(int)resp.StatusCode}");

            // Parse entity ID and merchant ID from the /logins response.
            //
            // When auto-boarding fires, Payrix returns a MERCHANT object in data[0]:
            //   response.data[0].id     → merchant ID  (t1_mer_...)
            //   response.data[0].entity → entity ID    (t1_ent_...)
            //
            // Fallback: older/non-boarding flow returns a LOGIN object:
            //   response.data[0].entities[0].id              → entity ID
            //   response.data[0].entities[0].merchant.id     → merchant ID
            //
            // Also check response.alert.merchantId as a last resort.
            string? entityId = null, loginId = null, merchantId = null;
            try
            {
                var doc      = System.Text.Json.JsonDocument.Parse(json);
                var response = doc.RootElement.GetProperty("response");
                var data     = response.GetProperty("data");

                if (data.ValueKind == System.Text.Json.JsonValueKind.Array && data.GetArrayLength() > 0)
                {
                    var first = data[0];

                    // Detect which format.
                    // A merchant object has an "entity" string field AND its own "id" starts with "_mer_".
                    // A login object also has an "entity" string field but its "id" starts with "_log_".
                    // Check the id prefix to disambiguate.
                    var rawId = first.TryGetProperty("id", out var rawIdEl) ? rawIdEl.GetString() : null;
                    bool looksLikeMerchant = rawId != null &&
                        (rawId.Contains("_mer_", StringComparison.OrdinalIgnoreCase));

                    if (looksLikeMerchant &&
                        first.TryGetProperty("entity", out var entField) &&
                        entField.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        // Auto-boarded path — data[0] IS the merchant object
                        merchantId = rawId;
                        entityId   = entField.GetString();
                    }
                    else
                    {
                        // Login-object path — data[0] is the login with nested entities
                        loginId = rawId;

                        if (first.TryGetProperty("entities", out var ents) &&
                            ents.ValueKind == System.Text.Json.JsonValueKind.Array &&
                            ents.GetArrayLength() > 0)
                        {
                            var ent0 = ents[0];
                            entityId = ent0.TryGetProperty("id", out var eid) ? eid.GetString() : null;

                            if (ent0.TryGetProperty("merchant", out var mer) &&
                                mer.ValueKind == System.Text.Json.JsonValueKind.Object)
                                merchantId = mer.TryGetProperty("id", out var mrid) ? mrid.GetString() : null;
                        }

                        // Also check login's entity string field for entityId fallback
                        if (entityId == null &&
                            first.TryGetProperty("entity", out var loginEnt) &&
                            loginEnt.ValueKind == System.Text.Json.JsonValueKind.String)
                            entityId = loginEnt.GetString();
                    }
                }

                // Last resort: pull from response.alert
                if (merchantId == null &&
                    response.TryGetProperty("alert", out var alert) &&
                    alert.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    merchantId ??= alert.TryGetProperty("merchantId", out var amid) ? amid.GetString() : null;
                }
            }
            catch { /* IDs will be retrieved via GET /entities?custom lookup fallback */ }

            // Return "entityId|merchantId" so the caller can skip the FindEntityByCustomAsync lookup
            var combinedId = merchantId != null ? $"{entityId}|{merchantId}" : entityId;
            return (combinedId, loginId, $"REQUEST:\n{payload}\n\nRESPONSE:\n{json}", null);
        }
        catch (Exception ex)
        {
            return (null, null, json, $"SignUpViaLogins error: {ex.Message}");
        }
    }

    /// <summary>
    /// Searches Payrix entities by the custom field (format: "accountId,companyId").
    /// Uses a LIKE search on the companyId portion — same as BQE Core's LinkPayrixMerchant.
    /// Returns the first matching entity.
    /// </summary>
    public async Task<(string? entityId, string? entityName, string rawJson, string? error)>
        FindEntityByCustomAsync(string accountId, string companyId)
    {
        var json = "{}";
        try
        {
            // Payrix LIKE search: custom contains the companyId
            var search = Uri.EscapeDataString($"%{companyId}%");
            var resp   = await _client.GetAsync(
                $"/entities?search[custom][like]={search}&page[limit]=10").ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, null, json, ExtractPayrixError(json) ?? $"HTTP {(int)resp.StatusCode}");

            var doc  = System.Text.Json.JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("response").GetProperty("data");
            if (data.ValueKind == System.Text.Json.JsonValueKind.Array && data.GetArrayLength() > 0)
            {
                // Pick the entity whose custom contains both accountId AND companyId
                foreach (var ent in data.EnumerateArray())
                {
                    var customVal = ent.TryGetProperty("custom", out var cv) ? cv.GetString() ?? "" : "";
                    if (customVal.Contains(companyId, StringComparison.OrdinalIgnoreCase))
                    {
                        var eid   = ent.TryGetProperty("id",   out var eid2)   ? eid2.GetString()  : null;
                        var ename = ent.TryGetProperty("name", out var ename2) ? ename2.GetString() : null;
                        if (!string.IsNullOrEmpty(eid))
                            return (eid, ename, json, null);
                    }
                }
            }
            return (null, null, json, $"No entity found with custom containing companyId={companyId}");
        }
        catch (Exception ex) { return (null, null, json, $"FindEntity error: {ex.Message}"); }
    }

    /// <summary>
    /// Searches merchants by name, DBA, email, account email, or entity/merchant ID.
    /// Runs all applicable searches in parallel and merges deduplicated results.
    /// </summary>
    public async Task<(List<Merchant> merchants, string rawJson, string? error)>
        SearchMerchantsAsync(string query)
    {
        var json = "{}";
        try
        {
            query = query.Trim();
            var results = new List<Merchant>();
            var enc     = Uri.EscapeDataString(query);

            void Merge(string? rawJson, List<Merchant>? data)
            {
                if (rawJson is not null) json = rawJson;
                if (data is null) return;
                foreach (var m in data)
                    if (!string.IsNullOrEmpty(m.Id) && !results.Any(r => r.Id == m.Id))
                        results.Add(m);
            }

            List<Merchant>? Parse(string j)
                => JsonSerializer.Deserialize<PayrixMerchantResponse>(j, JsonOptions)?.Response?.Data;

            bool isId    = query.StartsWith("t1_", StringComparison.OrdinalIgnoreCase) ||
                           query.StartsWith("p1_", StringComparison.OrdinalIgnoreCase);
            bool isEmail = query.Contains('@');

            // Run all relevant searches in parallel
            var tasks = new List<Task<(string raw, List<Merchant>? data)>>();

            Task<(string, List<Merchant>?)> Fetch(string url) => GetRawAsync(url)
                .ContinueWith(t => (t.Result, Parse(t.Result)));

            if (!isId && !isEmail)
            {
                // Text search: name, DBA, email LIKE
                tasks.Add(Fetch($"/merchants?search[name][LIKE]=%25{enc}%25&page[limit]=50"));
                tasks.Add(Fetch($"/merchants?search[dba][LIKE]=%25{enc}%25&page[limit]=50"));
                tasks.Add(Fetch($"/merchants?search[email][LIKE]=%25{enc}%25&page[limit]=50"));
            }

            if (isEmail)
            {
                // Exact + partial email matches
                tasks.Add(Fetch($"/merchants?search[email][EQUALS]={enc}&page[limit]=50"));
                tasks.Add(Fetch($"/merchants?search[email][LIKE]=%25{enc}%25&page[limit]=50"));
                // Also try chargebackNotificationEmail
                tasks.Add(Fetch($"/merchants?search[chargebackNotificationEmail][EQUALS]={enc}&page[limit]=50"));
            }

            if (isId)
            {
                // ID-based: entity ID, direct merchant ID
                tasks.Add(Fetch($"/merchants?search[entity][EQUALS]={enc}&page[limit]=10"));
                tasks.Add(Fetch($"/merchants/{enc}"));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var t in tasks) Merge(t.Result.raw, t.Result.data);

            if (results.Count == 0)
                return ([], json, $"No merchants found matching '{query}'.");

            return (results.OrderBy(m => m.Name).ToList(), json, null);
        }
        catch (Exception ex)
        {
            return ([], json, $"Merchant search error: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a Payrix merchant record for an existing entity.
    /// Sets all fields required for a merchant to accept transactions.
    /// Note: autoBoarded and status=2 are Payrix-internal fields that cannot be set via API;
    /// the merchant may still require Payrix boarding approval before processing live transactions.
    /// </summary>
    public async Task<(string? merchantId, string rawJson, string? error)>
        CreateMerchantAsync(
            string entityId,
            string dba          = "",
            string mcc          = "8931",
            string email        = "",
            string environment  = "ecommerce",
            string defaultGroup = "")
    {
        var json = "{}";
        try
        {
            // dba is REQUIRED — merchant won't create without it
            // "new": 0 = existing business with prior processing history
            // All volume/ticket/environment fields set to maximise auto-boarding chance
            var body = new System.Collections.Generic.Dictionary<string, object>
            {
                ["entity"]        = entityId,
                ["dba"]           = string.IsNullOrEmpty(dba) ? "BQE MERCHANT" : dba.ToUpperInvariant(),
                ["mcc"]           = mcc,
                ["environment"]   = environment,
                ["new"]           = 0,
                ["annualCCSales"] = 100000,
                ["avgTicket"]     = 150,
                ["established"]   = "20200101",
                ["percentKeyed"]  = 100,
                ["percentEcomm"]  = 100,
                ["chargebackNotificationEmail"] = string.IsNullOrEmpty(email) ? "merchant@bqe.com" : email
            };

            // Assign default fee group if provided
            if (!string.IsNullOrWhiteSpace(defaultGroup))
                body["feegroup"] = defaultGroup;

            var payload = JsonSerializer.Serialize(body, JsonOptions);
            var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp    = await _client.PostAsync("/merchants", content).ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, json, ExtractPayrixError(json) ?? $"HTTP {(int)resp.StatusCode}");

            // Parse merchant ID directly from JSON — avoid model deserialization issues
            // (Payrix returns status/"applePayActive" as strings but model expects int)
            string? merchantId = null;
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(json);
                merchantId = doc.RootElement
                    .GetProperty("response")
                    .GetProperty("data")[0]
                    .GetProperty("id")
                    .GetString();
            }
            catch { /* id not in expected path */ }

            // Fallback: try model deserialization
            if (string.IsNullOrEmpty(merchantId))
            {
                try
                {
                    var opts = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                    };
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<PayrixMerchantResponse>(json, opts);
                    merchantId = parsed?.Response?.Data?.FirstOrDefault()?.Id;
                }
                catch { }
            }

            return (merchantId, json, string.IsNullOrEmpty(merchantId)
                ? $"Merchant created but ID not extracted. Response: {json[..Math.Min(200, json.Length)]}"
                : null);
        }
        catch (Exception ex) { return (null, json, $"CreateMerchant error: {ex.Message}\nResponse: {json[..Math.Min(200,json.Length)]}"); }
    }

    /// <summary>
    /// Post-creation merchant configuration steps:
    /// 1. PUT extended fields (annualCCSales, avgTicket, established, environment, percentKeyed)
    /// 2. POST /accounts to add a settlement bank account (Payrix internal structure)
    /// 3. Attempt PUT status=1 so Payrix internal system can pick it up for boarding
    ///
    /// Note: status=2 (Boarded) and autoBoarded=1 are Payrix-internal and cannot be set via API.
    /// Returns a multi-step log string and any error.
    /// </summary>
    public async Task<(string log, string? error)> ConfigureMerchantAsync(
        string merchantId,
        string entityId,
        string email          = "merchant@bqe.com",
        string routingNumber  = "021000021",   // Chase test routing number (ACH)
        string accountNumber  = "123456789012") // ACH checking account number
    {
        var log = new System.Text.StringBuilder();
        string? lastError = null;

        // ── Step 1: PUT extended merchant fields + status=1 ───────────────────
        // Setting status=1 signals Payrix to pick up the merchant for boarding review.
        // status=2 (Boarded) and autoBoarded=1 are set internally by Payrix; they cannot
        // be forced via API, but having status=1 + member + bank account triggers auto-boarding
        // in the Payrix sandbox.
        try
        {
            var body = new System.Collections.Generic.Dictionary<string, object>
            {
                ["annualCCSales"] = 100000,
                ["avgTicket"]     = 150,
                ["established"]   = "20200101",
                ["environment"]   = "ecommerce",
                ["percentKeyed"]  = 100,
                ["percentEcomm"]  = 100,
                ["status"]        = 1,
            };
            var payload = System.Text.Json.JsonSerializer.Serialize(body, JsonOptions);
            var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp    = await _client.PutAsync($"/merchants/{Uri.EscapeDataString(merchantId)}", content).ConfigureAwait(false);
            var raw     = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var err     = ExtractPayrixError(raw);
            log.AppendLine($"[1] PUT /merchants/{merchantId} → HTTP {(int)resp.StatusCode} {(err != null ? $"WARN: {err}" : "OK")}");
        }
        catch (Exception ex) { log.AppendLine($"[1] PUT /merchants error: {ex.Message}"); }

        // ── Step 2: POST /accounts — ACH settlement bank account ─────────────
        // Payrix /accounts nested account object:
        //   method=8  → ACH checking (proven working; method=1/2/3 are credit/debit types)
        //   routing   → string routing number (e.g. "021000021" for Chase)
        //   number    → bank account number string (numeric, any length)
        // The entity field links the account to the merchant's entity for payout routing.
        try
        {
            var acctBody = new System.Collections.Generic.Dictionary<string, object>
            {
                ["entity"]   = entityId,
                ["primary"]  = 1,
                ["type"]     = "all",
                ["currency"] = "USD",
                ["account"]  = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["method"]  = 8,             // ACH checking
                    ["routing"] = routingNumber, // "021000021" (Chase) — must be string, not int enum
                    ["number"]  = accountNumber, // bank account number
                }
            };
            var payload = System.Text.Json.JsonSerializer.Serialize(acctBody, JsonOptions);
            var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp    = await _client.PostAsync("/accounts", content).ConfigureAwait(false);
            var raw     = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var acctId  = "";
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(raw);
                acctId = doc.RootElement.GetProperty("response").GetProperty("data")[0].GetProperty("id").GetString() ?? "";
            }
            catch { }
            var err = string.IsNullOrEmpty(acctId) ? (ExtractPayrixError(raw) ?? "no ID in response") : null;
            log.AppendLine($"[2] POST /accounts → HTTP {(int)resp.StatusCode} {(err != null ? $"WARN: {err}" : $"account={acctId}")}");
        }
        catch (Exception ex) { log.AppendLine($"[2] POST /accounts error: {ex.Message}"); }

        return (log.ToString(), lastError);
    }

    /// <summary>
    /// Creates a member linked to BOTH entity AND merchant so it appears in the Payrix merchant profile.
    /// All required fields must be non-empty.
    /// </summary>
    /// <summary>
    /// Creates a member linked to an entity ONLY (no merchant field).
    /// Portal flow: entity type=2 auto-creates a merchant; entity members become merchant owners.
    /// This matches how the BQESignup portal creates owners.
    /// </summary>
    public async Task<(string rawJson, string? error)> CreateMemberForEntityAsync(
        string merchantId,
        string first, string last, string email, string phone,
        string address1, string city, string state, string zip,
        string dob, string ssn, int ownership)
    {
        var json = "{}";
        try
        {
            static string R(string? v, string fb) => string.IsNullOrWhiteSpace(v) ? fb : v.Trim();
            var ssnDigits = System.Text.RegularExpressions.Regex.Replace(ssn ?? "", @"\D", "");

            // Link member to MERCHANT only — entity field causes "vendor not found"
            // Payrix portal creates members through signup flow, not via this endpoint on entities
            var body = new System.Collections.Generic.Dictionary<string, object>
            {
                ["merchant"]  = merchantId,  // link to merchant profile
                ["type"]      = 1,
                ["first"]     = R(first,    "Owner"),
                ["last"]      = R(last,     "Member"),
                ["email"]     = R(email,    "merchant@bqe.com"),
                ["phone"]     = R(phone,    "2132000000"),
                ["address1"]  = R(address1, "123 Main St"),
                ["city"]      = R(city,     "Los Angeles"),
                ["state"]     = R(state,    "CA"),
                ["zip"]       = R(zip,      "90001"),
                ["country"]   = "USA",
                ["dob"]       = R(dob,      "19841031"),  // YYYYMMDD required
                ["ssn"]       = ssnDigits.Length == 9 ? ssnDigits : "767567272",
                ["ownership"] = ownership > 0 ? ownership : 100,
                ["title"]     = "Owner",
            };

            var payload  = JsonSerializer.Serialize(body, JsonOptions);
            var content  = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp     = await _client.PostAsync("/members", content).ConfigureAwait(false);
            var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            json = $"REQ:\n{payload}\n\nHTTP {(int)resp.StatusCode}\nRESP:\n{respBody}";

            if (respBody.Contains("\"errors\"") && !respBody.Contains("\"errors\":[]"))
            {
                var errDetail = ExtractPayrixError(respBody);
                return (json, errDetail ?? $"Member errors: {respBody[..Math.Min(300, respBody.Length)]}");
            }

            return resp.IsSuccessStatusCode ? (json, null) : (json, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return (json, $"Member error: {ex.Message}"); }
    }

    public async Task<(string rawJson, string? error)> CreateMemberForMerchantAsync(
        string entityId, string merchantId,
        string first, string last, string email, string phone,
        string address1, string city, string state, string zip,
        string dob, string ssn, int ownership)
    {
        var json = "{}";
        try
        {
            static string R(string? v, string fb) => string.IsNullOrWhiteSpace(v) ? fb : v.Trim();
            var ssnDigits = System.Text.RegularExpressions.Regex.Replace(ssn ?? "", @"\D", "");

            // Link member to MERCHANT (not entity alone).
            // "entity" alone → "vendor not found" error.
            // "merchant" links to the merchant profile so owner fields appear.
            var body = new System.Collections.Generic.Dictionary<string, object>
            {
                ["merchant"]  = merchantId, // primary link — makes owner appear on merchant profile
                ["entity"]    = entityId,   // also link to entity
                ["type"]      = 1,          // 1 = beneficial owner / primary contact
                ["first"]     = R(first,    "Owner"),
                ["last"]      = R(last,     "Member"),
                ["email"]     = R(email,    "merchant@bqe.com"),
                ["phone"]     = R(phone,    "2132000000"),
                ["address1"]  = R(address1, "123 Main St"),
                ["city"]      = R(city,     "Los Angeles"),
                ["state"]     = R(state,    "CA"),
                ["zip"]       = R(zip,      "90001"),
                ["country"]   = "USA",
                ["dob"]       = R(dob,      "19841031"),  // YYYYMMDD format required
                ["ssn"]       = ssnDigits.Length == 9 ? ssnDigits : "767567272",
                ["ownership"] = ownership > 0 ? ownership : 100,
                ["title"]     = "Owner",
            };

            var payload  = JsonSerializer.Serialize(body, JsonOptions);
            var content  = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp     = await _client.PostAsync("/members", content).ConfigureAwait(false);
            var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            json = $"REQ:\n{payload}\n\nHTTP {(int)resp.StatusCode}\nRESP:\n{respBody}";

            // Check for errors in response body even on HTTP 200
            if (respBody.Contains("\"errors\"") && !respBody.Contains("\"errors\":[]"))
            {
                var errDetail = ExtractPayrixError(respBody);
                return (json, errDetail ?? $"Member created with errors: {respBody[..Math.Min(300, respBody.Length)]}");
            }

            return resp.IsSuccessStatusCode
                ? (json, null)
                : (json, ExtractPayrixError(respBody) ?? $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return (json, $"Member error: {ex.Message}"); }
    }

    /// <summary>
    /// Fetches up to <paramref name="limit"/> merchants from /merchants.
    /// Returns (list, rawJson, error).
    /// </summary>
    public async Task<(List<Merchant> merchants, string rawJson, string? error)>
        GetMerchantsAsync(int limit = 0)  // limit=0 → fetch all
    {
        var lastJson = "{}";
        const int apiPageSize = 100;
        try
        {
            var all  = new List<Merchant>();
            int page = 1;

            while (true)
            {
                var resp = await _client.GetAsync(
                    $"/merchants?page[limit]={apiPageSize}&page[number]={page}").ConfigureAwait(false);
                lastJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                    return (all.Count > 0 ? all : [], lastJson,
                            $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} (page {page})");

                var parsed = JsonSerializer.Deserialize<PayrixMerchantResponse>(lastJson, JsonOptions);
                var data   = parsed?.Response?.Data ?? [];

                if (data.Count == 0) break;
                all.AddRange(data);
                if (data.Count < apiPageSize) break;  // last page — fewer results than requested
                if (limit > 0 && all.Count >= limit) break;

                page++;
            }

            // Apply user-requested cap
            if (limit > 0 && all.Count > limit)
                all = all.Take(limit).ToList();

            return (all, lastJson, null);
        }
        catch (Exception ex)
        {
            return ([], lastJson, $"Merchant fetch error: {ex.Message}");
        }
    }

    /// <summary>Raw GET helper — returns the response body string.</summary>
    public async Task<string> GetRawAsync(string path)
    {
        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var resp = await _client.GetAsync(path).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>Raw PUT helper — sends JSON body and returns the response body string.</summary>
    public async Task<string> PutRawAsync(string path, string jsonBody)
    {
        var content = new System.Net.Http.StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var resp = await _client.PutAsync(path, content).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the merchant record whose entity field matches <paramref name="entityId"/>.
    /// Tries API filter first; falls back to page-walk (up to 200 merchants).
    /// </summary>
    public async Task<(Merchant? merchant, string rawJson, string? error)>
        GetMerchantByEntityAsync(string entityId)
    {
        var json = "{}";
        try
        {
            // Strategy 1: direct entity filter — parse raw JSON and match on "entity" field
            // Do NOT use a fallback that returns any random merchant if the filter finds no match.
            var filterJson = await GetRawAsync(
                $"/merchants?search[entity][eq]={Uri.EscapeDataString(entityId)}&page[limit]=50");
            json = filterJson;
            var fd  = JsonDocument.Parse(filterJson);
            var arr = fd.RootElement.GetProperty("response").GetProperty("data");
            foreach (var el in arr.EnumerateArray())
            {
                var elEntity = el.TryGetProperty("entity", out var ev) ? ev.GetString() : null;
                if (string.Equals(elEntity, entityId, StringComparison.OrdinalIgnoreCase))
                {
                    var m = JsonSerializer.Deserialize<Merchant>(el.GetRawText(), JsonOptions);
                    if (m is not null) return (m, filterJson, null);
                }
            }

            // Strategy 2: page-walk — match strictly on entity field, never return wrong merchant
            for (int page = 1; page <= 5; page++)
            {
                var pj = await GetRawAsync($"/merchants?page[limit]=50&page[number]={page}");
                var pd = JsonDocument.Parse(pj);
                var pa = pd.RootElement.GetProperty("response").GetProperty("data");
                if (pa.GetArrayLength() == 0) break;
                foreach (var el in pa.EnumerateArray())
                {
                    var elEntity = el.TryGetProperty("entity", out var ev) ? ev.GetString() : null;
                    if (string.Equals(elEntity, entityId, StringComparison.OrdinalIgnoreCase))
                    {
                        var m = JsonSerializer.Deserialize<Merchant>(el.GetRawText(), JsonOptions);
                        if (m is not null) return (m, pj, null);
                    }
                }
            }

            return (null, json, $"No merchant found for entity {entityId}");
        }
        catch (Exception ex)
        {
            return (null, json, $"Merchant lookup error: {ex.Message}");
        }
    }

    /// <summary>Gets a single merchant by ID.</summary>
    public async Task<(Merchant? merchant, string rawJson, string? error)>
        GetMerchantAsync(string merchantId)
    {
        var json = "{}";
        try
        {
            var resp = await _client.GetAsync(
                $"/merchants/{Uri.EscapeDataString(merchantId)}").ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, json, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

            // Response may be wrapped: { response: { data: [...] } } or direct object
            try
            {
                var wrapped = JsonSerializer.Deserialize<PayrixMerchantResponse>(json, JsonOptions);
                var m = wrapped?.Response?.Data?.FirstOrDefault();
                if (m != null) return (m, json, null);
            }
            catch { /* fall through to direct */ }

            var direct = JsonSerializer.Deserialize<Merchant>(json, JsonOptions);
            return (direct, json, direct == null ? "Could not parse merchant." : null);
        }
        catch (Exception ex)
        {
            return (null, json, $"Merchant fetch error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns (entityId, entityName) for a given merchant ID.
    /// Results are cached for the lifetime of this service instance to avoid
    /// redundant API calls when enriching many transactions.
    /// </summary>
    public async Task<(string? entityId, string? entityName, string? merchantName)>
        GetMerchantEntityAsync(string merchantId)
    {
        if (string.IsNullOrWhiteSpace(merchantId)) return (null, null, null);

        if (_merchantEntityCache.TryGetValue(merchantId, out var cached))
            return cached;

        try
        {
            var (merchant, _, _) = await GetMerchantAsync(merchantId).ConfigureAwait(false);
            var merchantName = merchant?.Name ?? merchant?.Descriptor;

            if (merchant?.Entity is null)
            {
                var r0 = (null as string, null as string, merchantName);
                _merchantEntityCache[merchantId] = r0;
                return r0;
            }

            var entityId   = merchant.Entity;
            string? entityName = null;

            // Fetch entity name
            var entityResp = await _client.GetAsync(
                $"/entities/{Uri.EscapeDataString(entityId)}").ConfigureAwait(false);
            if (entityResp.IsSuccessStatusCode)
            {
                var entityJson = await entityResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(entityJson);
                if (doc.RootElement.TryGetProperty("response", out var r) &&
                    r.TryGetProperty("data", out var d) &&
                    d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0 &&
                    d[0].TryGetProperty("name", out var n))
                    entityName = n.GetString();
            }

            var result = (entityId, entityName, merchantName);
            _merchantEntityCache[merchantId] = result;
            return result;
        }
        catch
        {
            _merchantEntityCache[merchantId] = (null, null, null);
            return (null, null, null);
        }
    }

    /// <summary>Fetches entity name for a given entity ID (uses merchant entity cache indirectly).</summary>
    public async Task<string?> GetEntityNameAsync(string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return null;
        try
        {
            var resp = await _client.GetAsync(
                $"/entities/{Uri.EscapeDataString(entityId)}").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("response", out var r) &&
                r.TryGetProperty("data", out var d) &&
                d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0 &&
                d[0].TryGetProperty("name", out var n))
                return n.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Sets a merchant's status via PUT /merchants/{id} (falls back to PATCH on 405).
    /// status: 1 = Active, 2 = Inactive, 3 = Suspended.
    /// Returns (updatedMerchant, rawJson, error).
    /// </summary>
    public async Task<(Merchant? merchant, string rawJson, string? error)>
        UpdateMerchantStatusAsync(string merchantId, int newStatus)
    {
        var json = "{}";
        try
        {
            var url = $"/merchants/{Uri.EscapeDataString(merchantId)}";

            // Payrix status codes: 0=Created, 1=Submitted, 2=Active/Boarded, 3=Inactive, 4=Suspended
            // Activating (status→2): include autoBoarded=1 to bypass sandbox underwriting check.
            object body = newStatus == 2
                ? new { status = 2, autoBoarded = 1 }
                : new { status = newStatus };
            var payload = JsonSerializer.Serialize(body);

            StringContent MakeBody() => new(payload, System.Text.Encoding.UTF8, "application/json");

            // Try PUT first; fall back to PATCH if the server returns 405 Method Not Allowed
            var resp = await _client.PutAsync(url, MakeBody()).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
            {
                var patchReq = new HttpRequestMessage(HttpMethod.Patch, url) { Content = MakeBody() };
                resp = await _client.SendAsync(patchReq).ConfigureAwait(false);
            }

            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                // Surface the Payrix error body so the caller can show it
                string detail = ExtractPayrixError(json)
                                ?? $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                return (null, json, detail);
            }

            // Parse wrapped { response: { data: [...] } } or direct object
            try
            {
                var wrapped = JsonSerializer.Deserialize<PayrixMerchantResponse>(json, JsonOptions);
                var m = wrapped?.Response?.Data?.FirstOrDefault();
                if (m != null) return (m, json, null);
            }
            catch { /* fall through to direct */ }

            var direct = JsonSerializer.Deserialize<Merchant>(json, JsonOptions);
            return (direct, json, direct == null ? "Could not parse updated merchant." : null);
        }
        catch (Exception ex)
        {
            return (null, json, $"Update error: {ex.Message}");
        }
    }

    /// <summary>
    /// DELETE /transactions/{id} — voids/removes a transaction from Payrix.
    /// Returns (ok, rawJson, error).
    /// </summary>
    public async Task<(bool ok, string rawJson, string? error)>
        DeleteTransactionAsync(string transactionId)
    {
        var json = "{}";
        try
        {
            var url  = $"/transactions/{Uri.EscapeDataString(transactionId)}";
            var resp = await _client.DeleteAsync(url).ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var detail = ExtractPayrixError(json) ?? $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                return (false, json, detail);
            }
            return (true, json, null);
        }
        catch (Exception ex)
        {
            return (false, json, $"Delete error: {ex.Message}");
        }
    }

    /// <summary>
    /// DELETE /merchants/{id} — removes the merchant from Payrix.
    /// Only works for merchants that have not processed any transactions.
    /// Returns (ok, rawJson, error).
    /// </summary>
    public async Task<(bool ok, string rawJson, string? error)>
        DeleteMerchantAsync(string merchantId)
    {
        var json = "{}";
        try
        {
            var url  = $"/merchants/{Uri.EscapeDataString(merchantId)}";
            var resp = await _client.DeleteAsync(url).ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var detail = ExtractPayrixError(json) ?? $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                return (false, json, detail);
            }

            // Payrix returns HTTP 200 even for rejected operations — errors live in response.errors
            var bodyErr = ExtractPayrixError(json);
            if (bodyErr != null)
                return (false, json, bodyErr);

            return (true, json, null);
        }
        catch (Exception ex)
        {
            return (false, json, $"Delete error: {ex.Message}");
        }
    }

    /// <summary>
    /// GET /members?merchant={merchantId} — returns all members on the merchant.
    /// Used by the health check to confirm at least one owner/principal exists.
    /// </summary>
    public async Task<(int count, string? firstMemberId, string rawJson, string? error)>
        GetMembersForMerchantAsync(string merchantId)
    {
        var (members, rawJson, err) = await GetMembersDetailAsync(merchantId);
        var first = members.Count > 0 && members[0].TryGetValue("id", out var id) ? id : null;
        return (members.Count, first, rawJson, err);
    }

    /// <summary>
    /// PUT /members/{id} — updates an existing member record with new details.
    /// Pass only the fields to change; omitted fields are left as-is by Payrix.
    /// </summary>
    public async Task<(string rawJson, string? error)> UpdateMemberAsync(
        string memberId,
        string first, string last, string email, string phone,
        string address1, string city, string state, string zip, string country,
        string dob, string ssn, int ownership, string title)
    {
        var json = "{}";
        try
        {
            var ssnDigits = System.Text.RegularExpressions.Regex.Replace(ssn ?? "", @"\D", "");
            var body = new System.Collections.Generic.Dictionary<string, object>();

            void Add(string key, string? val) { if (!string.IsNullOrWhiteSpace(val)) body[key] = val; }
            Add("first",    first);
            Add("last",     last);
            Add("email",    email);
            Add("phone",    phone);
            Add("address1", address1);
            Add("city",     city);
            Add("state",    state);
            Add("zip",      zip);
            Add("country",  string.IsNullOrWhiteSpace(country) ? "USA" : country);
            Add("dob",      dob);
            Add("title",    title);
            if (ssnDigits.Length == 9) body["ssn"] = ssnDigits;
            if (ownership > 0) body["ownership"] = ownership;

            var payload = JsonSerializer.Serialize(body, JsonOptions);
            var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");

            // Try PUT first, fall back to PATCH
            HttpResponseMessage resp;
            string respBody;
            resp = await _client.PutAsync($"/members/{Uri.EscapeDataString(memberId)}", content).ConfigureAwait(false);
            respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
            {
                content  = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                var req  = new HttpRequestMessage(new HttpMethod("PATCH"), $"/members/{Uri.EscapeDataString(memberId)}") { Content = content };
                resp     = await _client.SendAsync(req).ConfigureAwait(false);
                respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }

            json = $"REQ:\n{payload}\n\nHTTP {(int)resp.StatusCode}\nRESP:\n{respBody}";
            if (!resp.IsSuccessStatusCode)
                return (json, ExtractPayrixError(respBody) ?? $"HTTP {(int)resp.StatusCode}");

            if (respBody.Contains("\"errors\"") && !respBody.Contains("\"errors\":[]"))
                return (json, ExtractPayrixError(respBody) ?? "Update returned errors");

            return (json, null);
        }
        catch (Exception ex) { return (json, ex.Message); }
    }

    /// <summary>
    /// GET /members?merchant={merchantId} — returns full member details (name, email, type, ownership, title, status).
    /// </summary>
    public async Task<(List<System.Collections.Generic.Dictionary<string,string>> members, string rawJson, string? error)>
        GetMembersDetailAsync(string merchantId)
    {
        var json = "{}";
        var result = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string,string>>();
        try
        {
            var resp = await _client.GetAsync(
                $"/members?merchant={Uri.EscapeDataString(merchantId)}&limit=20").ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (result, json, $"HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var r) ||
                !r.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return (result, json, null);

            string S(JsonElement el, string key) =>
                el.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null
                    ? v.ToString() : "";

            foreach (var m in data.EnumerateArray())
            {
                var d = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["id"]        = S(m, "id"),
                    ["first"]     = S(m, "first"),
                    ["last"]      = S(m, "last"),
                    ["email"]     = S(m, "email"),
                    ["phone"]     = S(m, "phone"),
                    ["title"]     = S(m, "title"),
                    ["ownership"] = S(m, "ownership"),
                    ["type"]      = S(m, "type"),
                    ["status"]    = S(m, "status"),
                    ["created"]   = S(m, "created"),
                    ["address1"]  = S(m, "address1"),
                    ["city"]      = S(m, "city"),
                    ["state"]     = S(m, "state"),
                    ["zip"]       = S(m, "zip"),
                    ["country"]   = S(m, "country"),
                };
                result.Add(d);
            }
            return (result, json, null);
        }
        catch (Exception ex) { return (result, json, ex.Message); }
    }

    /// <summary>
    /// POST /tokens — creates a test token with a Visa test card number.
    /// Returns (tokenId, rawJson, error).
    /// Success means the merchant is correctly boarded and accepting tokens.
    /// </summary>
    public async Task<(string? tokenId, string rawJson, string? error)>
        TestTokenAsync(string merchantId)
    {
        var json = "{}";
        try
        {
            // Step 0: create a temporary customer (Payrix requires a customer record for /tokens)
            string? customerId = null;
            try
            {
                var custBody = new Dictionary<string, object>
                {
                    ["merchant"]   = merchantId,
                    ["firstName"]  = "Test",
                    ["lastName"]   = "User",
                    ["email"]      = "healthcheck@test.invalid"
                };
                var custContent = new System.Net.Http.StringContent(
                    JsonSerializer.Serialize(custBody, JsonOptions),
                    System.Text.Encoding.UTF8, "application/json");
                var custResp = await _client.PostAsync("/customers", custContent).ConfigureAwait(false);
                var custJson = await custResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var custDoc = JsonDocument.Parse(custJson);
                if (custDoc.RootElement.TryGetProperty("response", out var cr) &&
                    cr.TryGetProperty("data", out var cd) &&
                    cd.ValueKind == JsonValueKind.Array && cd.GetArrayLength() > 0)
                    customerId = cd[0].TryGetProperty("id", out var cid) ? cid.GetString() : null;
            }
            catch { /* non-fatal — proceed without customer */ }

            var tokenBody = new Dictionary<string, object>
            {
                ["merchant"]   = merchantId,
                ["number"]     = "4111111111111111",   // Visa test card
                ["cvv"]        = "999",
                ["expiration"] = "1230",               // December 2030
                ["type"]       = 1,                    // 1 = credit card
                ["mode"]       = "token"
            };
            if (!string.IsNullOrEmpty(customerId))
                tokenBody["customer"] = customerId;

            var content = new System.Net.Http.StringContent(
                JsonSerializer.Serialize(tokenBody, JsonOptions),
                System.Text.Encoding.UTF8, "application/json");
            var resp = await _client.PostAsync("/tokens", content).ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, json, ExtractPayrixError(json) ?? $"HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(json);
            string? tokenId = null;
            if (doc.RootElement.TryGetProperty("response", out var r) &&
                r.TryGetProperty("data", out var d) &&
                d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0)
                tokenId = d[0].TryGetProperty("id", out var tp) ? tp.GetString() : null;

            return (tokenId, json, string.IsNullOrEmpty(tokenId)
                ? (ExtractPayrixError(json) ?? "Token created but ID not found in response")
                : null);
        }
        catch (Exception ex) { return (null, json, ex.Message); }
    }

    /// <summary>
    /// Pulls ALL error messages out of a Payrix response body, including the field name.
    /// Returns a formatted string like "[token] The referenced resource does not exist (code 5)"
    /// or null if no errors found / JSON is unparseable.
    /// </summary>
    // ── Real payment test: tokenize + charge ─────────────────────────────────

    /// <summary>
    /// Step 1 of a real payment test: tokenize a card number for the given merchant.
    /// Returns (tokenId, maskedNumber, rawJson, error).
    /// Uses Visa test card 4111111111111111 by default.
    /// </summary>
    public async Task<(string? tokenId, string? maskedNumber, string rawJson, string? error)>
        TokenizeCardAsync(
            string merchantId,
            string cardNumber  = "4111111111111111",
            string expiration  = "1230",
            string cvv         = "999",
            string first       = "Test",
            string last        = "User",
            string address1    = "123 Main St",
            string city        = "Los Angeles",
            string state       = "CA",
            string zip         = "90001",
            string email       = "test@bqe.com")
    {
        var json = "{}";
        try
        {
            // ── Step 0: ensure a customer exists for this merchant ───────────────
            // Payrix /tokens requires a customer record — without one it returns
            // [customer] code 15 ("referenced resource does not exist").
            // Create a lightweight test customer and use its ID in the token request.
            string? customerId = null;
            try
            {
                var custBody = JsonSerializer.Serialize(new
                {
                    merchant  = merchantId,
                    firstName = first,
                    lastName  = last,
                    email     = email,
                }, JsonOptions);
                var custResp = await _client.PostAsync("/customers",
                    new System.Net.Http.StringContent(custBody, System.Text.Encoding.UTF8, "application/json"))
                    .ConfigureAwait(false);
                var custJson = await custResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (custResp.IsSuccessStatusCode)
                {
                    using var cd = JsonDocument.Parse(custJson);
                    if (cd.RootElement.TryGetProperty("response", out var cr) &&
                        cr.TryGetProperty("data", out var cdata) &&
                        cdata.ValueKind == JsonValueKind.Array && cdata.GetArrayLength() > 0)
                        customerId = cdata[0].TryGetProperty("id", out var cid) ? cid.GetString() : null;
                }
            }
            catch { /* non-fatal — attempt token without customer */ }

            // ── Step 1: tokenize ─────────────────────────────────────────────────
            var tokenBody = new System.Collections.Generic.Dictionary<string, object>
            {
                ["merchant"]   = merchantId,
                ["number"]     = cardNumber.Replace(" ", "").Replace("-", ""),
                ["cvv"]        = cvv,
                ["expiration"] = expiration,   // MMYY
                ["type"]       = 1,            // 1 = credit card
                ["mode"]       = "token",
            };
            if (!string.IsNullOrEmpty(customerId))
                tokenBody["customer"] = customerId;

            var content = new System.Net.Http.StringContent(
                JsonSerializer.Serialize(tokenBody, JsonOptions),
                System.Text.Encoding.UTF8, "application/json");
            var resp = await _client.PostAsync("/tokens", content).ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, null, json, ExtractPayrixError(json) ?? $"HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(json);
            string? tokenId = null, masked = null;
            if (doc.RootElement.TryGetProperty("response", out var r) &&
                r.TryGetProperty("data", out var d) &&
                d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0)
            {
                var t = d[0];
                tokenId = t.TryGetProperty("id",     out var idP) ? idP.GetString() : null;
                masked  = t.TryGetProperty("number", out var nP)  ? nP.GetString()  : null;
            }
            if (string.IsNullOrEmpty(tokenId))
                return (null, null, json, ExtractPayrixError(json) ?? "Token ID not found in response");

            return (tokenId, masked, json, null);
        }
        catch (Exception ex) { return (null, null, json, ex.Message); }
    }

    /// <summary>
    /// Step 2 of a real payment test: charge using a pre-created token.
    /// type: 1=Sale, 2=Auth-only
    /// amount is in cents (e.g. 100 = $1.00).
    /// origin: 2=ECOMMERCE (required by Payrix; omitting causes "required_field" error).
    /// currency: "USD" string — Payrix rejects ISO numeric "840".
    /// Returns (txnId, status, rawJson, error).
    /// </summary>
    public async Task<(string? txnId, string? status, string rawJson, string? error)>
        ChargeSaleAsync(
            string merchantId,
            string tokenId,
            int    amountCents = 100,
            int    type        = 1,
            string currency    = "USD")   // "USD" string — NOT "840" (ISO numeric rejected by Payrix)
    {
        var json = "{}";
        try
        {
            var body = new
            {
                merchant = merchantId,
                token    = tokenId,
                total    = amountCents,
                type     = type,          // 1 = Sale
                currency = currency,      // "USD" — Payrix requires string, not ISO-4217 numeric
                origin   = 2,            // 2 = ECOMMERCE — required; omitting → "required_field" error
            };
            var content = new System.Net.Http.StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                System.Text.Encoding.UTF8, "application/json");
            var resp = await _client.PostAsync("/txns", content).ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, null, json, ExtractPayrixError(json) ?? $"HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(json);
            string? txnId = null, status = null;
            if (doc.RootElement.TryGetProperty("response", out var r) &&
                r.TryGetProperty("data", out var d) &&
                d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0)
            {
                var t = d[0];
                txnId  = t.TryGetProperty("id",     out var idP) ? idP.GetString()  : null;
                status = t.TryGetProperty("status", out var stP) ? stP.GetRawText() : null;
            }
            if (string.IsNullOrEmpty(txnId))
                return (null, null, json, ExtractPayrixError(json) ?? "Transaction ID not found in response");

            return (txnId, status, json, null);
        }
        catch (Exception ex) { return (null, null, json, ex.Message); }
    }

    /// <summary>
    /// Charges a card inline (no pre-created token) — POST /txns with nested payment object.
    /// This is the most reliable path: avoids "token: no_such_record" caused by pending-token
    /// state and skips the separate POST /tokens round-trip entirely.
    /// origin=2 (ECOMMERCE) and currency="USD" are required Payrix fields.
    /// Returns (txnId, status, rawJson, error).
    /// </summary>
    public async Task<(string? txnId, string? status, string rawJson, string? error)>
        ChargeInlineAsync(
            string merchantId,
            string cardNumber  = "4111111111111111",
            string expiration  = "1230",
            string cvv         = "999",
            string name        = "Test User",
            int    amountCents = 100,
            int    type        = 1,
            string currency    = "USD")
    {
        var json = "{}";
        try
        {
            var body = new System.Collections.Generic.Dictionary<string, object>
            {
                ["merchant"] = merchantId,
                ["total"]    = amountCents,
                ["type"]     = type,      // 1 = Sale
                ["currency"] = currency,  // "USD"
                ["origin"]   = 2,         // 2 = ECOMMERCE
                ["payment"]  = new
                {
                    number     = cardNumber.Replace(" ", "").Replace("-", ""),
                    cvv        = cvv,
                    expiration = expiration,  // MMYY
                    name       = name,
                }
            };
            var content = new System.Net.Http.StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                System.Text.Encoding.UTF8, "application/json");
            var resp = await _client.PostAsync("/txns", content).ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, null, json, ExtractPayrixError(json) ?? $"HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(json);
            string? txnId = null, status = null;
            if (doc.RootElement.TryGetProperty("response", out var r) &&
                r.TryGetProperty("data", out var d) &&
                d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0)
            {
                var t = d[0];
                txnId  = t.TryGetProperty("id",       out var idP) ? idP.GetString()  : null;
                status = t.TryGetProperty("status",   out var stP) ? stP.GetRawText() : null;
            }
            if (string.IsNullOrEmpty(txnId))
                return (null, null, json, ExtractPayrixError(json) ?? "Transaction ID not found in response");

            return (txnId, status, json, null);
        }
        catch (Exception ex) { return (null, null, json, ex.Message); }
    }

    /// <summary>
    /// Runs a full real-payment test: charge $1.00 inline → void/cancel the charge.
    ///
    /// Uses inline payment (card details in the txn body) as the primary path — this avoids
    /// the "token: no_such_record" error that occurs when a newly created token stays in
    /// "pending" status and cannot be referenced immediately.
    ///
    /// Falls back to tokenize → charge if the merchant explicitly requires pre-tokenization.
    /// Returns a detailed step-by-step result.
    /// </summary>
    public async Task<PaymentTestResult> RunPaymentTestAsync(
        string merchantId,
        string cardNumber  = "4111111111111111",
        string expiration  = "1230",
        string cvv         = "999",
        int    amountCents = 100)
    {
        var result = new PaymentTestResult { MerchantId = merchantId, AmountCents = amountCents };

        // ── Primary path: inline payment (no pre-created token) ──────────────
        // Proved reliable in sandbox: avoids "pending" token no_such_record errors.
        var (txnId, status, txnJson, txnErr) = await ChargeInlineAsync(
            merchantId, cardNumber, expiration, cvv,
            name: "Test User", amountCents: amountCents);
        result.TxnJson = txnJson;

        if (txnErr != null || string.IsNullOrEmpty(txnId))
        {
            // ── Fallback: tokenize first, then charge ─────────────────────────
            // Some production merchants require a stored token for PCI compliance.
            result.TokenJson = txnJson;  // preserve inline error in TokenJson for diagnosis
            var (tokenId, masked, tokJson, tokErr) = await TokenizeCardAsync(
                merchantId, cardNumber, expiration, cvv);
            result.TokenJson = tokJson;

            if (tokErr != null || string.IsNullOrEmpty(tokenId))
            {
                var hint = tokErr?.Contains("no_such_record", StringComparison.OrdinalIgnoreCase) == true
                    ? "\n\nHint: If this is from BQE Core's UI (not PayrixTools), the app server has a\n" +
                      "cached merchant ID. Run 'iisreset /restart' as Administrator to reload settings."
                    : "";
                result.Error = $"Inline charge failed: {txnErr}\nTokenize also failed: {tokErr}{hint}";
                result.Stage = "tokenize";
                return result;
            }
            result.TokenId    = tokenId;
            result.MaskedCard = masked ?? cardNumber[..4] + "XXXXXXXX" + cardNumber[^4..];

            (txnId, status, txnJson, txnErr) = await ChargeSaleAsync(merchantId, tokenId, amountCents);
            result.TxnJson = txnJson;
            if (txnErr != null || string.IsNullOrEmpty(txnId))
            {
                result.Error = $"Charge failed: {txnErr}";
                result.Stage = "charge";
                return result;
            }
        }

        result.TxnId     = txnId;
        result.TxnStatus = status;
        result.MaskedCard ??= cardNumber[..4] + "XXXXXXXX" + cardNumber[^4..];

        // ── Void: cancel immediately so no charge settles ─────────────────────
        var (_, _, voidJson, voidErr) = await VoidTransactionAsync(txnId!);
        result.VoidJson  = voidJson;
        result.VoidError = voidErr;

        result.Success = true;
        return result;
    }

    /// <summary>
    /// Voids/cancels a transaction by setting inactive=1 via PUT.
    /// Payrix does not support a standalone void transaction type via POST /txns for
    /// uncaptured auths; setting inactive=1 on the existing txn is the reliable cancel path.
    /// </summary>
    public async Task<(string? txnId, string? status, string rawJson, string? error)>
        VoidTransactionAsync(string txnId)
    {
        var json = "{}";
        try
        {
            // PUT inactive=1 — cancels an uncaptured/unsettled transaction.
            // (POST /txns with type=3 returns "mismatched_txns" for unsettled auths.)
            var body    = new { inactive = 1 };
            var content = new System.Net.Http.StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                System.Text.Encoding.UTF8, "application/json");
            var resp = await _client.PutAsync($"/txns/{Uri.EscapeDataString(txnId)}", content).ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (null, null, json, ExtractPayrixError(json) ?? $"HTTP {(int)resp.StatusCode}");
            return (txnId, "cancelled", json, null);
        }
        catch (Exception ex) { return (null, null, json, ex.Message); }
    }

    private static string? ExtractPayrixError(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            static string FormatErr(System.Text.Json.JsonElement e)
            {
                var field = e.TryGetProperty("field", out var f) ? f.GetString() : null;
                var msg   = e.TryGetProperty("msg",   out var m) ? m.GetString() : null;
                var code  = e.TryGetProperty("code",  out var c) ? (int?)c.GetInt32() : null;
                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrEmpty(field)) sb.Append($"[{field}] ");
                sb.Append(!string.IsNullOrEmpty(msg) ? msg : "(unknown error)");
                if (code is > 0) sb.Append($" (code {code})");
                return sb.ToString();
            }

            // { "response": { "errors": [ ... ] } }
            if (doc.RootElement.TryGetProperty("response", out var resp) &&
                resp.TryGetProperty("errors", out var errs) &&
                errs.GetArrayLength() > 0)
            {
                return string.Join("  |  ", errs.EnumerateArray().Select(FormatErr));
            }
            // { "errors": [ ... ] }  (flat form)
            if (doc.RootElement.TryGetProperty("errors", out var errs2) &&
                errs2.GetArrayLength() > 0)
            {
                return string.Join("  |  ", errs2.EnumerateArray().Select(FormatErr));
            }
        }
        catch { /* ignore parse failures */ }
        return null;
    }

    // ── Per-request API key injection ─────────────────────────────────────────
    private sealed class ApiKeyHandler : DelegatingHandler
    {
        private readonly string _apiKey;
        public ApiKeyHandler(string apiKey, string baseUrl)
            : base(MakeSocketsHandler())
        {
            _apiKey = apiKey;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.TryAddWithoutValidation("APIKEY", _apiKey);
            return base.SendAsync(request, ct);
        }
    }
}

// ── Payment test result ───────────────────────────────────────────────────────

public class PaymentTestResult
{
    public string  MerchantId  { get; set; } = "";
    public int     AmountCents { get; set; }
    public bool    Success     { get; set; }
    public string? Stage       { get; set; }   // "tokenize" | "charge" | "void"
    public string? Error       { get; set; }
    public string? TokenId     { get; set; }
    public string? MaskedCard  { get; set; }
    public string? TxnId       { get; set; }
    public string? TxnStatus   { get; set; }
    public string? VoidError   { get; set; }
    public string? TokenJson   { get; set; }
    public string? TxnJson     { get; set; }
    public string? VoidJson    { get; set; }
    public string  AmountLabel => $"${AmountCents / 100m:F2}";
}
