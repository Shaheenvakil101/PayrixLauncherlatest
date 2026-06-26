using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PayrixLauncher.Models;

namespace PayrixLauncher.Services;

/// <summary>
/// Calls BQE Core Admin Portal subscription endpoints.
/// All calls require the BusinessToken from BqeAuthService.
/// </summary>
public static class BqeSubscriptionService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new FlexibleStringConverter(), new FlexibleIntConverter() }
    };

    // ── Get subscriptions for a company ──────────────────────────────────

    /// <summary>
    /// POST /BQECoreAdminPortalAPI/API/CoreHost/SubscribePackages
    /// Returns all active subscription packages for the given company.
    /// BQEParameters uses a FilterList — Company_ID is a filter, not a top-level field.
    /// </summary>
    public static async Task<(List<UserSubscribePackage> packages, string? error, string rawLog)>
        GetSubscriptionsAsync(string baseUrl, string token, Guid companyId)
    {
        using var client = MakeClient(baseUrl, token);
        var body    = BuildFilterParams(companyId);
        var bodyJson = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true });
        var fullUrl  = HostRoot(baseUrl) + "/BQECoreAdminPortalAPI/API/CoreHost/SubscribePackages";

        var log = new System.Text.StringBuilder();
        log.AppendLine($"POST {fullUrl}");
        log.AppendLine($"Body:\n{bodyJson}");
        log.AppendLine();

        // Try primary endpoint first, then fallback
        string[] endpoints =
        [
            "/BQECoreAdminPortalAPI/API/CoreHost/SubscribePackages",
            "/BQECoreAdminPortalAPI/API/CoreHost/GetCompanySubscripitons"
        ];

        string? lastErr = null;
        foreach (var ep in endpoints)
        {
            log.AppendLine($"\n→ POST {HostRoot(baseUrl)}{ep}");
            var (json, err) = await PostAsync(client, ep, body);

            if (err != null) { log.AppendLine($"  Error: {err}"); lastErr = err; continue; }

            log.AppendLine($"  Response ({json?.Length} chars): {json?[..Math.Min(json?.Length ?? 0, 500)]}");

            if (json == "null" || string.IsNullOrWhiteSpace(json))
            {
                log.AppendLine("  (null/empty — token may not have access to this company; try Fetch from DB)");
                lastErr = null;   // not an error — DB fetch is the reliable path
                continue;
            }

            try
            {
                var list = JsonSerializer.Deserialize<List<UserSubscribePackage>>(json!, JsonOpts) ?? [];
                log.AppendLine($"\nParsed {list.Count} subscription(s) from {ep}.");
                return (list, list.Count == 0 ? "API returned 0 results — use Fetch from DB instead" : null, log.ToString());
            }
            catch (Exception ex)
            {
                log.AppendLine($"  Parse error: {ex.Message}");
                lastErr = $"Parse error: {ex.Message}";
            }
        }

        // null from all endpoints = token mismatch for this company — not a hard error
        return ([], lastErr, log.ToString());
    }

    // ── Get inactive subscriptions ────────────────────────────────────────

    public static async Task<(List<UserSubscribePackage> packages, string? error)>
        GetInactiveSubscriptionsAsync(string baseUrl, string token, Guid companyId)
    {
        using var client = MakeClient(baseUrl, token);
        var (json, err) = await PostAsync(client,
            $"/BQECoreAdminPortalAPI/API/CoreHost/GetCompanyInactiveSubscripitons",
            BuildFilterParams(companyId, includeInactive: true));
        if (err != null) return ([], err);
        try
        {
            return (JsonSerializer.Deserialize<List<UserSubscribePackage>>(json!, JsonOpts) ?? [], null);
        }
        catch (Exception ex) { return ([], $"Parse error: {ex.Message}"); }
    }

    // ── Build BQEParameters with FilterList ───────────────────────────────

    /// <summary>
    /// Builds the BQEParameters body that the Admin Portal API expects.
    /// Company_ID is specified as a filter, not a top-level field.
    /// </summary>
    private static object BuildFilterParams(Guid companyId, bool includeInactive = false)
    {
        // SubscriptionStatus: Active=0, InActive=1, Pending=2, Expired=4
        // The server-side PackageQuery already filters to Active(0)+Pending(2) internally.
        // We only send Company_ID — additional status filter caused null responses.
        var filters = new List<object>
        {
            new
            {
                Field       = "Company_ID",
                StartValue  = companyId.ToString(),
                Operator    = 0,    // FilterOperator.EqualTo
                Conjunction = 0     // LogicalOperator.None (first filter, no conjunction)
            }
        };

        if (includeInactive)
        {
            // Include all statuses by adding Status filter with range covering 0-9
            filters.Add(new
            {
                Field          = "Status",
                StartValue     = "0",
                EndValue        = "9",
                Operator       = 6,    // FilterOperator.Range
                Conjunction    = 1     // LogicalOperator.AND
            });
        }

        return new
        {
            FilterList = filters,
            PageInfo   = new { PageIndex = 0, PageSize = 200 },
            SortList   = Array.Empty<object>()
        };
    }

    // ── Get available packages for a company (for Add Subscription) ──────

    /// <summary>
    /// GET /BQECoreAdminPortalAPI/API/CoreHost/GetOrderHelper?company_ID={id}
    /// Returns packages + their plans available to order for the company.
    /// </summary>
    public static async Task<(BqeOrderHelper? helper, string? error)>
        GetOrderHelperAsync(string baseUrl, string token, Guid companyId)
    {
        using var client = MakeClient(baseUrl, token);
        try
        {
            var resp = await client.GetAsync(
                $"/BQECoreAdminPortalAPI/API/CoreHost/GetOrderHelper?company_ID={companyId}")
                .ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode} — {json[..Math.Min(json.Length, 200)]}");

            var helper = JsonSerializer.Deserialize<BqeOrderHelper>(json, JsonOpts);
            return (helper, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ── Place a new order (add subscription) ─────────────────────────────

    /// <summary>
    /// POST /BQECoreAdminPortalAPI/API/CoreHost/PlaceOrder
    /// Places an order for a new subscription.
    /// Uses NoCreditCard / admin path (PaymentOption = 2).
    /// </summary>
    /// <summary>
    /// Inserts a subscription directly into BQECoreHost using the V2 schema:
    ///   CompanySubscription (parent) + SubscriptionDetail (child with Plan_ID/Status/ExpiresOn).
    /// Also inserts into the legacy Subscription table for backward compatibility.
    /// Used as a fallback when all API endpoints fail auth checks.
    /// </summary>
    public static async Task<(bool success, string? error, string rawLog)>
        AddSubscriptionDirectDbAsync(string connStr, Guid companyId, Guid packageId, Guid planId,
                                     int licenses, bool autoRenew, int monthMultiplier)
    {
        var log = new System.Text.StringBuilder();
        connStr = DbHelper.EnsureTrustedCert(connStr);

        connStr = System.Text.RegularExpressions.Regex.Replace(
            connStr, @"Initial Catalog\s*=[^;]+", "Initial Catalog=BQECoreHost",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var now       = DateTime.UtcNow;
        var expiresOn = now.AddMonths(monthMultiplier > 0 ? monthMultiplier : 12);
        var csId      = Guid.NewGuid();   // CompanySubscription.ID
        var sdId      = Guid.NewGuid();   // SubscriptionDetail.ID

        var insertCsb = new SqlConnectionStringBuilder(connStr)
        {
            InitialCatalog         = "BQECoreHost",
            TrustServerCertificate = true,
        };

        log.AppendLine($"=== Direct DB Insert (V2: CompanySubscription + SubscriptionDetail) ===");
        log.AppendLine($"Server:    {insertCsb.DataSource}");
        log.AppendLine($"CompanySubscription.ID: {csId}");
        log.AppendLine($"SubscriptionDetail.ID:  {sdId}");
        log.AppendLine($"Company: {companyId}  Package: {packageId}  Plan: {planId}");
        log.AppendLine($"Licenses: {licenses}  AutoRenew: {autoRenew}");
        log.AppendLine($"StartsOn: {now:yyyy-MM-dd}  ExpiresOn: {expiresOn:yyyy-MM-dd}");

        try
        {
            await using var conn = new SqlConnection(insertCsb.ConnectionString);
            await conn.OpenAsync();

            // ── 1. CompanySubscription (V2 parent) ───────────────────────────
            await using (var cmd = new SqlCommand(@"
INSERT INTO [CompanySubscription]
    (ID, Company_ID, Package_ID, NumberOfLicense, AutoRenew, StartsOn, CreatedOn, UpdatedOn)
VALUES
    (@ID, @Company_ID, @Package_ID, @NumberOfLicense, @AutoRenew, @StartsOn, @Now, @Now)", conn)
                { CommandTimeout = 15 })
            {
                cmd.Parameters.AddWithValue("@ID",              csId);
                cmd.Parameters.AddWithValue("@Company_ID",      companyId);
                cmd.Parameters.AddWithValue("@Package_ID",      packageId);
                cmd.Parameters.AddWithValue("@NumberOfLicense", licenses);
                cmd.Parameters.AddWithValue("@AutoRenew",       autoRenew);
                cmd.Parameters.AddWithValue("@StartsOn",        now);
                cmd.Parameters.AddWithValue("@Now",             now);
                await cmd.ExecuteNonQueryAsync();
                log.AppendLine($"→ Inserted CompanySubscription {csId}.");
            }

            // ── 2. SubscriptionDetail (V2 child — has Plan_ID, Status, ExpiresOn) ──
            await using (var cmd = new SqlCommand(@"
INSERT INTO [SubscriptionDetail]
    (ID, CompanySubscription_ID, Plan_ID, Status, NumberOfLicense, StartsOn, ExpiresOn, CreatedOn, UpdatedOn)
VALUES
    (@ID, @CS_ID, @Plan_ID, @Status, @NumberOfLicense, @StartsOn, @ExpiresOn, @Now, @Now)", conn)
                { CommandTimeout = 15 })
            {
                cmd.Parameters.AddWithValue("@ID",              sdId);
                cmd.Parameters.AddWithValue("@CS_ID",           csId);
                cmd.Parameters.AddWithValue("@Plan_ID",         planId);
                cmd.Parameters.AddWithValue("@Status",          0);   // Active
                cmd.Parameters.AddWithValue("@NumberOfLicense", licenses);
                cmd.Parameters.AddWithValue("@StartsOn",        now);
                cmd.Parameters.AddWithValue("@ExpiresOn",       expiresOn);
                cmd.Parameters.AddWithValue("@Now",             now);
                await cmd.ExecuteNonQueryAsync();
                log.AppendLine($"→ Inserted SubscriptionDetail {sdId}.");
            }

            log.AppendLine("✅ Subscription created successfully in BQECoreHost (V2).");
            return (true, null, log.ToString());
        }
        catch (Exception ex)
        {
            log.AppendLine($"→ DB error: {ex.Message}");
            return (false, $"DB insert failed: {ex.Message}", log.ToString());
        }
    }

    /// <summary>
    /// Deletes a subscription from BQECoreHost by removing SubscriptionDetail rows first,
    /// then the CompanySubscription parent row (V2 schema). Falls back to deleting from
    /// the legacy Subscription table if no V2 row is found.
    /// </summary>
    public static async Task<(bool success, string? error, string rawLog)>
        DeleteSubscriptionFromDbAsync(string connStr, Guid subscriptionId)
    {
        var log = new System.Text.StringBuilder();
        log.AppendLine($"[DELETE] SubscriptionId = {subscriptionId}");

        // Force correct catalog
        var csb = new SqlConnectionStringBuilder(connStr)
        {
            InitialCatalog          = "BQECoreHost",
            TrustServerCertificate  = true,
        };

        try
        {
            await using var conn = new SqlConnection(csb.ConnectionString);
            await conn.OpenAsync();
            log.AppendLine($"→ Connected to {csb.DataSource} / {csb.InitialCatalog}");

            // ── V2 path: CompanySubscription + SubscriptionDetail ────────────
            int v2Rows;
            await using (var chk = new SqlCommand(
                "SELECT COUNT(*) FROM [CompanySubscription] WHERE [ID] = @ID", conn))
            {
                chk.Parameters.AddWithValue("@ID", subscriptionId);
                v2Rows = (int)(await chk.ExecuteScalarAsync() ?? 0);
            }

            if (v2Rows > 0)
            {
                // Delete child rows first (FK constraint)
                int detailCount;
                await using (var delDetail = new SqlCommand(
                    "DELETE FROM [SubscriptionDetail] WHERE [CompanySubscription_ID] = @ID", conn))
                {
                    delDetail.Parameters.AddWithValue("@ID", subscriptionId);
                    detailCount = await delDetail.ExecuteNonQueryAsync();
                }
                log.AppendLine($"→ Deleted {detailCount} SubscriptionDetail row(s).");

                // Delete parent row
                await using var delParent = new SqlCommand(
                    "DELETE FROM [CompanySubscription] WHERE [ID] = @ID", conn);
                delParent.Parameters.AddWithValue("@ID", subscriptionId);
                await delParent.ExecuteNonQueryAsync();
                log.AppendLine("→ Deleted CompanySubscription row.");
                log.AppendLine("✅ Subscription deleted (V2 schema).");
                return (true, null, log.ToString());
            }

            // ── V1 fallback: legacy Subscription table ───────────────────────
            log.AppendLine("→ No V2 CompanySubscription row found — trying legacy Subscription table.");
            int v1Rows;
            await using (var chkV1 = new SqlCommand(
                "SELECT COUNT(*) FROM [Subscription] WHERE [ID] = @ID", conn))
            {
                chkV1.Parameters.AddWithValue("@ID", subscriptionId);
                v1Rows = (int)(await chkV1.ExecuteScalarAsync() ?? 0);
            }

            if (v1Rows > 0)
            {
                await using var delV1 = new SqlCommand(
                    "DELETE FROM [Subscription] WHERE [ID] = @ID", conn);
                delV1.Parameters.AddWithValue("@ID", subscriptionId);
                await delV1.ExecuteNonQueryAsync();
                log.AppendLine("→ Deleted legacy Subscription row.");
                log.AppendLine("✅ Subscription deleted (V1/legacy schema).");
                return (true, null, log.ToString());
            }

            log.AppendLine("⚠️ Subscription ID not found in either CompanySubscription or Subscription table.");
            return (false, "Subscription not found in Host DB.", log.ToString());
        }
        catch (Exception ex)
        {
            log.AppendLine($"→ DB error: {ex.Message}");
            return (false, $"DB delete failed: {ex.Message}", log.ToString());
        }
    }

    // ── Update company status Trial → Active ────────────────────────────────

    /// <summary>
    /// Sets Company.CompanyStatus = 0 (Active) for the given company if it is currently
    /// in Trial status (CompanyStatus = 2). No-op (returns success) if already Active.
    /// Operates against the BQECoreHost main DB (not the payment-service DB).
    /// </summary>
    public static async Task<(bool success, string? error, string rawLog)>
        UpdateCompanyStatusToActiveAsync(string connStr, Guid companyId)
    {
        var log = new System.Text.StringBuilder();
        log.AppendLine($"[UpdateCompanyStatus] CompanyId = {companyId}");

        var csb = new SqlConnectionStringBuilder(connStr)
        {
            InitialCatalog         = "BQECoreHost",
            TrustServerCertificate = true,
        };

        try
        {
            await using var conn = new SqlConnection(csb.ConnectionString);
            await conn.OpenAsync();
            log.AppendLine($"→ Connected to {csb.DataSource} / {csb.InitialCatalog}");

            // Check current status
            int? current = null;
            await using (var chk = new SqlCommand(
                "SELECT CompanyStatus FROM [Company] WHERE [ID] = @ID", conn))
            {
                chk.Parameters.AddWithValue("@ID", companyId);
                var val = await chk.ExecuteScalarAsync();
                if (val is not null and not DBNull) current = Convert.ToInt32(val);
            }

            log.AppendLine($"→ Current CompanyStatus = {current?.ToString() ?? "NULL"}");

            if (current == 0)
            {
                log.AppendLine("→ Already Active — no update needed.");
                return (true, null, log.ToString());
            }

            // Update to Active (0)
            int rows;
            await using (var upd = new SqlCommand(
                "UPDATE [Company] SET CompanyStatus = 0, UpdatedOn = @Now WHERE [ID] = @ID", conn))
            {
                upd.Parameters.AddWithValue("@ID",  companyId);
                upd.Parameters.AddWithValue("@Now", DateTime.UtcNow);
                rows = await upd.ExecuteNonQueryAsync();
            }

            log.AppendLine($"→ Updated {rows} row(s). CompanyStatus set to 0 (Active).");
            return rows > 0
                ? (true, null, log.ToString())
                : (false, "Company row not found or not updated.", log.ToString());
        }
        catch (Exception ex)
        {
            log.AppendLine($"→ DB error: {ex.Message}");
            return (false, $"Status update failed: {ex.Message}", log.ToString());
        }
    }

    // ── Assign users to subscription ─────────────────────────────────────────

    /// <summary>
    /// Returns all AccountCompany users for the given company, each flagged
    /// IsAssigned=true if they already have subscriptionId assigned in
    /// AccountCompanySubscription.
    /// </summary>
    public static async Task<(List<Models.AssignableUser> users, string? error)>
        GetAssignableUsersAsync(string connStr, Guid companyId, Guid subscriptionId)
    {
        var csb = new SqlConnectionStringBuilder(connStr)
        {
            InitialCatalog         = "BQECoreHost",
            TrustServerCertificate = true,
        };

        try
        {
            await using var conn = new SqlConnection(csb.ConnectionString);
            await conn.OpenAsync();

            const string sql = @"
SELECT
    ac.ID                                          AS AcId,
    COALESCE(p.FirstName, '')                      AS FirstName,
    COALESCE(p.LastName,  '')                      AS LastName,
    COALESCE(a.Email,     '')                      AS Email,
    CASE WHEN acs.AccountCompany_ID IS NOT NULL
         THEN 1 ELSE 0 END                         AS IsAssigned
FROM        [AccountCompany]             ac
INNER JOIN  [Account]                    a   ON a.ID         = ac.Account_ID
LEFT  JOIN  [Profile]                    p   ON p.ID         = a.Profile_ID
LEFT  JOIN  [AccountCompanySubscription] acs ON acs.AccountCompany_ID = ac.ID
                                             AND acs.Subscription_ID  = @SubId
WHERE ac.Company_ID = @CompanyId
ORDER BY p.LastName, p.FirstName, a.Email";

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@CompanyId", companyId);
            cmd.Parameters.AddWithValue("@SubId",     subscriptionId);

            var users = new List<Models.AssignableUser>();
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                users.Add(new Models.AssignableUser
                {
                    Id         = rdr.GetGuid(rdr.GetOrdinal("AcId")),
                    FirstName  = rdr["FirstName"] as string,
                    LastName   = rdr["LastName"]  as string,
                    Email      = rdr["Email"]     as string,
                    IsAssigned = (int)rdr["IsAssigned"] == 1,
                });
            }
            return (users, null);
        }
        catch (Exception ex)
        {
            return ([], $"Could not load users: {ex.Message}");
        }
    }

    /// <summary>
    /// Inserts/deletes rows in AccountCompanySubscription for the given lists.
    /// Returns a log string and any error.
    /// </summary>
    public static async Task<(bool success, string? error, string rawLog)>
        SaveUserAssignmentsAsync(string connStr, Guid subscriptionId,
                                 IEnumerable<Guid> toAssign,
                                 IEnumerable<Guid> toUnassign)
    {
        var log = new System.Text.StringBuilder();
        log.AppendLine($"[ASSIGN] Subscription = {subscriptionId}");

        var csb = new SqlConnectionStringBuilder(connStr)
        {
            InitialCatalog         = "BQECoreHost",
            TrustServerCertificate = true,
        };

        var assigns   = toAssign.ToList();
        var unassigns = toUnassign.ToList();
        log.AppendLine($"→ Assign: {assigns.Count}   Unassign: {unassigns.Count}");

        try
        {
            await using var conn = new SqlConnection(csb.ConnectionString);
            await conn.OpenAsync();

            // Unassign
            foreach (var userId in unassigns)
            {
                await using var del = new SqlCommand(
                    @"DELETE FROM [AccountCompanySubscription]
                      WHERE [AccountCompany_ID] = @UserId
                        AND [Subscription_ID]   = @SubId", conn);
                del.Parameters.AddWithValue("@UserId", userId);
                del.Parameters.AddWithValue("@SubId",  subscriptionId);
                int rows = await del.ExecuteNonQueryAsync();
                log.AppendLine($"  ✂ Unassigned {userId}  ({rows} row deleted)");
            }

            // Assign
            var now = DateTime.UtcNow;
            foreach (var userId in assigns)
            {
                // Skip if already exists (idempotent)
                await using var chk = new SqlCommand(
                    @"SELECT COUNT(*) FROM [AccountCompanySubscription]
                      WHERE [AccountCompany_ID] = @UserId
                        AND [Subscription_ID]   = @SubId", conn);
                chk.Parameters.AddWithValue("@UserId", userId);
                chk.Parameters.AddWithValue("@SubId",  subscriptionId);
                if ((int)(await chk.ExecuteScalarAsync() ?? 0) > 0)
                {
                    log.AppendLine($"  ⏩ Already assigned {userId} — skipped");
                    continue;
                }

                var newId = Guid.NewGuid();
                await using var ins = new SqlCommand(@"
INSERT INTO [AccountCompanySubscription]
    ([ID],[AccountCompany_ID],[Subscription_ID],[CreatedOn],[UpdatedOn])
VALUES
    (@ID, @UserId, @SubId, @Now, @Now)", conn);
                ins.Parameters.AddWithValue("@ID",     newId);
                ins.Parameters.AddWithValue("@UserId", userId);
                ins.Parameters.AddWithValue("@SubId",  subscriptionId);
                ins.Parameters.AddWithValue("@Now",    now);
                await ins.ExecuteNonQueryAsync();
                log.AppendLine($"  ✅ Assigned   {userId}  (row {newId})");
            }

            log.AppendLine("✅ Assignment changes saved.");
            return (true, null, log.ToString());
        }
        catch (Exception ex)
        {
            log.AppendLine($"→ DB error: {ex.Message}");
            return (false, $"Save assignments failed: {ex.Message}", log.ToString());
        }
    }

    public static async Task<(bool success, string? error, string rawLog)>
        PlaceOrderAsync(string baseUrl, string token,
                        Guid companyId, Guid packageId, Guid planId,
                        int licenses, DateTime startsOn, bool autoRenew,
                        Guid? regionId = null)
    {
        using var client = MakeClient(baseUrl, token);
        var log = new System.Text.StringBuilder();

        // Mirrors BQECoreAdminPortal NoCreditCard/AdminPortal path.
        // RequestSource: Core=0, AdminPortal=1 — must be AdminPortal so noCardSource check passes
        //   in CompanySubscriptionManager.PlaceFoundationBasedOrder.
        // IsRenewal=true bypasses the "packages already added" duplicate check in
        //   ValidateCompanySubscriptions so the admin can add any module regardless of state.
        // Order shape must match BQECoreHostModel.Order / LineItem exactly.
        // - Source / PaymentOption are overridden server-side by CoreHostManager.PlaceOrder
        //   (Source=1/AdminPortal, PaymentOption=2/NoCreditCard) so we don't need to send them,
        //   but sending the correct values avoids any model-binding ambiguity.
        // - StartsOn is NOT a field on LineItem — the server sets it from DateTime.UtcNow.
        //   ExpiresOn is the only nullable date on LineItem; leave it null so the plan's
        //   default term is used.
        // - Region is NOT a field on Order — omit to avoid deserialisation noise.
        // - CreditCard must be null for the NoCreditCard payment path.
        var order = new
        {
            Company_ID    = companyId,
            OrderDate     = DateTime.UtcNow,
            AutoRenew     = autoRenew,
            PaymentOption = 2,      // PaymentOptions.NoCreditCard
            Source        = 1,      // RequestSource.AdminPortal
            SendInvoice   = false,
            Action        = 0,      // OrderAction.New
            IsRenewal     = true,
            CreditCard    = (object?)null,
            Subscriptions = new[]
            {
                new
                {
                    Package_ID      = packageId,
                    Plan_ID         = planId,
                    NumberOfLicense = licenses,
                    ExpiresOn       = (string?)null,   // let server apply plan default term
                }
            }
        };

        var orderJson = System.Text.Json.JsonSerializer.Serialize(order,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        log.AppendLine($"=== PlaceOrder Request ===");
        log.AppendLine($"Body:\n{orderJson}");
        log.AppendLine();

        try
        {
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            // Try endpoints that write to BQECoreHost DB (the correct DB).
            // BQECoreAdminPortalAPI/API/CoreHost/PlaceOrder is intentionally excluded —
            // it accepts the token and returns HTTP 204 but writes to BQECoreAdminPortal DB instead.
            // If both fail, the caller falls back to direct DB insert into BQECoreHost.
            string[] endpoints =
            [
                "/BQECoreAdminPortalWebApp/Base/PostObject?url=api/CoreHost/PlaceOrder",  // needs cookie auth
                "/BQECoreHostApi/api/CoreHost/PlaceOrder",                                 // needs Host token
            ];

            foreach (var ep in endpoints)
            {
                log.AppendLine($"→ POST {HostRoot(baseUrl)}{ep}");
                var resp = await client.PostAsync(ep, new StringContent(orderJson, System.Text.Encoding.UTF8, "application/json"))
                    .ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                log.AppendLine($"  HTTP {(int)resp.StatusCode}");
                log.AppendLine($"  Response: {body[..Math.Min(body.Length, 1000)]}");

                if (resp.IsSuccessStatusCode)
                {
                    var trimmed = body.TrimStart();

                    // HTML response = unauthenticated redirect to Sign In page → skip to next endpoint
                    if (trimmed.StartsWith("<!") || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                    {
                        log.AppendLine("  → Got HTML (Sign In redirect) — this endpoint requires cookie auth, skipping.");
                        continue;
                    }

                    // The portal proxy wraps responses: { "IsSuccessStatusCode": true/false, ... }
                    if (!string.IsNullOrWhiteSpace(body) && body != "null" && body.Length > 4)
                    {
                        bool isProxyFailure = body.Contains("\"IsSuccessStatusCode\":false") ||
                                              body.Contains("\"IsSuccessStatusCode\": false");
                        bool isDirectError  = body.Contains("\"ExceptionMessage\"") ||
                                              body.Contains("not allowed") ||
                                              body.Contains("BQEException") ||
                                              body.Contains("\"IsSuccess\":false") ||
                                              body.Contains("\"Success\":false") ||
                                              body.Contains("\"status\":0") ||
                                              body.Contains("\"Status\":0");
                        if (isProxyFailure || isDirectError)
                        {
                            log.AppendLine("  → Detected error in HTTP 200 body.");
                            return (false, $"HTTP 200 but error [{ep}]:\n{StripHtml(body, 600)}", log.ToString());
                        }
                    }
                    log.AppendLine("  → Success.");
                    return (true, null, log.ToString());
                }

                // 404 = endpoint doesn't exist → try next
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) { log.AppendLine("  → 404, trying next."); continue; }

                var bodyPreview = StripHtml(body, 600);
                return (false, $"HTTP {(int)resp.StatusCode} [{ep}]:\n{bodyPreview}", log.ToString());
            }

            return (false, "API endpoints unavailable (need cookie/host-token auth) — falling back to direct DB insert", log.ToString());
        }
        catch (Exception ex)
        {
            log.AppendLine($"Exception: {ex.Message}");
            return (false, $"{ex.Message}\nRequest: {orderJson}", log.ToString());
        }
    }

    // ── Change package expiry date ────────────────────────────────────────

    /// <summary>
    /// POST /BQECoreAdminPortalAPI/API/CoreHost/ChangePackageDates
    /// Extends or changes the expiry date of a subscription.
    /// </summary>
    public static async Task<(bool success, string? error)>
        ChangeExpiryDateAsync(string baseUrl, string token,
                              Guid companyId, Guid subscriptionId, Guid? packageId,
                              DateTime newExpiry, bool autoRenew)
    {
        using var client = MakeClient(baseUrl, token);

        // Matches the ActionHelper<SubscribePackageUpdate> shape used by subscription.js
        // Note: ValidateCall() in Extensions.cs requires a non-empty Note (reason) — without it
        // the API throws HTTP 409 "Please enter the reason before processing."
        var body = new
        {
            Company_ID = companyId,
            Note       = "Expiry date updated via BQE Core ePayment Tools",   // required by ValidateCall()
            Action     = new
            {
                ID        = subscriptionId,
                Package_ID = packageId,
                ExpiresOn  = newExpiry.ToString("MM/dd/yyyy"),
                AutoRenew  = autoRenew
            }
        };

        var (json, err) = await PostAsync(client,
            "/BQECoreAdminPortalAPI/API/CoreHost/ChangePackageDates", body);
        if (err != null) return (false, err);
        return (true, null);
    }

    // ── HTML stripping ────────────────────────────────────────────────────

    /// <summary>
    /// Strips HTML tags from a server error response so it's human-readable.
    /// Extracts the innerText equivalent up to <paramref name="maxLen"/> chars.
    /// </summary>
    private static string StripHtml(string html, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(html)) return html ?? "";

        // If it doesn't look like HTML just return as-is
        if (!html.TrimStart().StartsWith("<")) return html[..Math.Min(html.Length, maxLen)];

        // Try to extract <title> for a quick summary
        var titleMatch = System.Text.RegularExpressions.Regex.Match(
            html, @"<title[^>]*>(.*?)</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : "";

        // Strip all HTML tags, collapse whitespace
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ").Trim();

        var summary = string.IsNullOrEmpty(title) ? text : $"[{title}] {text}";
        return summary[..Math.Min(summary.Length, maxLen)];
    }

    // ── ePayment / Payrix feature flag ───────────────────────────────────

    /// <summary>
    /// Enables the Payrix ePayment flag for a company by calling
    /// POST /BQECoreAdminPortalAPI/api/CompanyFlags/SetCompanyFlag
    /// Payload: { "CompanyId": "...", "Flags": { "Payrix": true }, "BatchWebhookLimit": 0 }
    /// </summary>
    public static async Task<(bool ok, string? error, string log)>
        SetPayrixFlagAsync(string baseUrl, string token, Guid companyId)
    {
        using var client = MakeClient(baseUrl, token);

        var body = new
        {
            CompanyId = companyId,
            Flags     = new { Payrix = true },
            BatchWebhookLimit = 0
        };

        var bodyJson = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true });
        var log      = new System.Text.StringBuilder();

        string[] endpoints =
        [
            "/BQECoreAdminPortalAPI/api/CompanyFlags/SetCompanyFlag",
            "/BQECoreAdminPortalWebApp/Base/PostObject?url=api/CompanyFlags/SetCompanyFlag",
        ];

        foreach (var ep in endpoints)
        {
            log.AppendLine($"POST {HostRoot(baseUrl)}{ep}");
            log.AppendLine($"Body:\n{bodyJson}");

            var (json, err) = await PostAsync(client, ep, body).ConfigureAwait(false);
            if (err == null)
            {
                log.AppendLine($"Response: {json}");
                return (true, null, log.ToString());
            }
            log.AppendLine($"Error: {err}");
        }

        return (false, "All SetCompanyFlag endpoints failed — see log", log.ToString());
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────

    /// <summary>Extracts scheme+host+port only — strips any path that may have crept into the stored URL.</summary>
    private static string HostRoot(string url)
    {
        url = url.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Authority}";
        return url.TrimEnd('/');
    }

    private static HttpClient MakeClient(string baseUrl, string token)
    {
        var root  = HostRoot(baseUrl);
        var inner = ProxyConfig.MakeHandler();
        var handler = new LoggingHandler("BQE Subscriptions", inner);
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(root),
            Timeout      = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        // BQE Admin Portal API reads token from Authorization header's Parameter
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", token);
        // X-BaseUrl header used by AuthMessageHandler for token refresh
        client.DefaultRequestHeaders.Add("X-BaseUrl", root);
        return client;
    }

    private static async Task<(string? json, string? error)>
        PostAsync(HttpClient client, string path, object body)
    {
        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(path, content).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode} — {json[..Math.Min(json.Length, 200)]}");

            return (json, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }
}
