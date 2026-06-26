using Microsoft.Data.SqlClient;

namespace PayrixLauncher.Services;

/// <summary>
/// Queries the BQECoreHost payment-service database to resolve a Payrix
/// transaction ID → Core AccountID + CompanyID.
/// </summary>
public static class HostDbService
{
    /// <summary>
    /// Given a Payrix transaction/disbursement ID, returns the Core CompanyID
    /// and AccountID stored in the host DB.
    /// Returns (null, null, errorMessage) if the connection string is empty,
    /// the query fails, or no matching row is found.
    /// </summary>
    public static async Task<(string? companyId, string? accountId, string? error)>
        GetIdsForTransactionAsync(string? connectionString, string txnId)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return (null, null, null);   // feature disabled — no error shown

        if (string.IsNullOrWhiteSpace(txnId))
            return (null, null, "No transaction ID to look up.");

        const string sql = @"
SELECT TOP 1
    CAST(c.CoreCompany_ID AS NVARCHAR(50)) AS CompanyID
FROM ServiceEntity se
INNER JOIN Company c ON se.Company_ID = c.ID
WHERE se.RequestID = @txnId
ORDER BY se.CreatedOn DESC";

        connectionString = SanitizeConnectionString(connectionString);

        // Microsoft.Data.SqlClient v5+ requires TrustServerCertificate for local/dev servers
        connectionString = DbHelper.EnsureTrustedCert(connectionString);

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@txnId", System.Data.SqlDbType.NVarChar, 100)
                { Value = txnId });

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var companyId = reader["CompanyID"]?.ToString();
                return (companyId, null, null);   // AccountID not in payment-service DB
            }

            return (null, null, "No record found in host DB for this transaction.");
        }
        catch (Exception ex)
        {
            return (null, null, $"Host DB error: {ex.Message}");
        }
    }

    /// <summary>
    /// Queries the BQECoreHost MAIN DB to resolve a CoreCompany_ID → Core AccountID.
    /// BQECoreHost schema:
    ///   Company.ID  = CoreCompany_ID (from payment-service DB)
    ///   Account.ID  = Core AccountID
    ///   AccountCompany joins Company ↔ Account (if present), otherwise try Account.Company_ID direct.
    /// Returns (accountId, errorMessage) — error is non-null only when connection string is set but query fails.
    /// </summary>
    public static async Task<(string? accountId, string? via, string? error)> GetAccountIdAsync(
        string? connectionString, string coreCompanyId, string? email = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(coreCompanyId))
            return (null, null, null);   // feature disabled

        connectionString = SanitizeConnectionString(connectionString);
        connectionString = DbHelper.EnsureTrustedCert(connectionString);

        // The @companyId parameter is the CoreCompany_ID value from the payment-service DB,
        // which equals Company.ID in the main BQECoreHost DB.
        //
        // Strategy 1: AccountCompany join table  (ac.Account_ID)
        // Strategy 2: Company has Account_ID column pointing to Account
        // Strategy 3: Account has Company_Id or CompanyId column pointing to Company
        // Build strategy list — email-based lookup first (most reliable)
        var strategyList = new List<(string sql, string label, bool useEmail)>();

        if (!string.IsNullOrWhiteSpace(email))
        {
            strategyList.Add((@"SELECT TOP 1 CAST(a.ID AS NVARCHAR(50)) AS AccountID
FROM   Account a
WHERE  a.Email = @email", "Account.Email", true));
        }

        // CompanyID-based fallbacks
        strategyList.AddRange(new[]
        {
            (@"SELECT TOP 1 CAST(a.ID AS NVARCHAR(50)) AS AccountID
FROM   Account a
INNER JOIN AccountCompany ac ON ac.Account_ID = a.ID
WHERE  ac.Company_ID = @companyId", "Account+AccountCompany", false),

            (@"SELECT TOP 1 CAST(ac.Account_ID AS NVARCHAR(50)) AS AccountID
FROM   AccountCompany ac
WHERE  ac.Company_ID = @companyId", "AccountCompany.Company_ID", false),

            (@"SELECT TOP 1 CAST(c.Account_ID AS NVARCHAR(50)) AS AccountID
FROM   Company c
WHERE  c.ID = @companyId", "Company.Account_ID", false),
        });

        if (!Guid.TryParse(coreCompanyId, out var companyGuid))
            return (null, null, $"Invalid CompanyID format: {coreCompanyId}");

        var errors = new System.Text.StringBuilder();
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            foreach (var (sql, label, useEmail) in strategyList)
            {
                try
                {
                    using var cmd = new SqlCommand(sql, conn);
                    if (useEmail)
                        cmd.Parameters.Add(new SqlParameter("@email", System.Data.SqlDbType.NVarChar, 200)
                            { Value = email! });
                    else
                        cmd.Parameters.Add(new SqlParameter("@companyId", System.Data.SqlDbType.UniqueIdentifier)
                            { Value = companyGuid });

                    var result = await cmd.ExecuteScalarAsync();
                    if (result is not null && result != DBNull.Value)
                        return (result.ToString(), label, null);
                    errors.Append($"[{label}: no row] ");
                }
                catch (SqlException ex)
                {
                    errors.Append($"[{label}: {ex.Message.Split('\n')[0].Trim()}] ");
                }
            }

            return (null, null, $"AccountID not found — tried: {errors}");
        }
        catch (Exception ex)
        {
            var preview = connectionString.Length > 60 ? connectionString[..60] + "…" : connectionString;
            return (null, null, $"Main DB error: {ex.Message}  |  ConnStr: {preview}");
        }
    }

    /// <summary>
    /// Given a CoreCompany_ID (GUID), returns the company Name from the BQECoreHost main DB.
    /// </summary>
    public static async Task<(string? companyName, string? error)> GetCompanyNameAsync(
        string? connectionString, string coreCompanyId)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(coreCompanyId))
            return (null, null);

        if (!Guid.TryParse(coreCompanyId, out var companyGuid))
            return (null, $"Invalid CompanyID: {coreCompanyId}");

        connectionString = SanitizeConnectionString(connectionString);
        connectionString = DbHelper.EnsureTrustedCert(connectionString);

        const string sql = @"SELECT TOP 1 Name FROM Company WHERE ID = @companyId";

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@companyId", System.Data.SqlDbType.UniqueIdentifier)
                { Value = companyGuid });
            var result = await cmd.ExecuteScalarAsync();
            return result is not null && result != DBNull.Value
                ? (result.ToString(), null)
                : (null, "Company not found.");
        }
        catch (Exception ex)
        {
            return (null, $"Host DB error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the connection string to the company's actual Core application database.
    /// Gets Company.DatabaseID from BQECoreHost, then builds a connection using the
    /// same server/credentials as the host DB but with DatabaseID as the catalog.
    /// ThirdPartySettings, BQSTable, PaymentService all live in this DB.
    /// </summary>
    public static async Task<(string? connStr, string? error)> GetCompanyDbConnectionAsync(
        string? hostConnStr, string companyId, string accountId)
    {
        if (string.IsNullOrWhiteSpace(hostConnStr)) return (null, "No host connection string.");

        if (!Guid.TryParse(companyId, out var cGuid)) return (null, $"Invalid companyId: {companyId}");
        if (!Guid.TryParse(accountId, out var aGuid)) return (null, $"Invalid accountId: {accountId}");

        hostConnStr = SanitizeConnectionString(hostConnStr);
        hostConnStr = DbHelper.EnsureTrustedCert(hostConnStr);

        // Simple approach:
        // 1. Query Company.DatabaseID from BQECoreHost (company & accountcompany are in host DB)
        // 2. Swap the Initial Catalog in the host connection string to the company DB name
        // This works because for local dev, all DBs are on the same SQL Server instance.
        try
        {
            hostConnStr = SanitizeConnectionString(hostConnStr);
        hostConnStr = DbHelper.EnsureTrustedCert(hostConnStr);

            await using var conn = new SqlConnection(hostConnStr);
            await conn.OpenAsync();

            // Get DatabaseID from Company table (joined via AccountCompany to verify ownership)
            const string sql = @"
SELECT TOP 1 c.DatabaseID
FROM Company c
LEFT JOIN AccountCompany ac ON ac.Company_ID = c.ID
WHERE c.ID = @cid";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@cid", System.Data.SqlDbType.UniqueIdentifier) { Value = cGuid });

            var dbIdRaw = await cmd.ExecuteScalarAsync();
            var dbId = dbIdRaw?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(dbId))
                return (null, $"Company.DatabaseID is null/empty for CompanyID={companyId}. Raw value='{dbIdRaw}'");

            // Replace Initial Catalog in the host connection string with the company DB name
            var coreConn = ReplaceInitialCatalog(hostConnStr, dbId);
            return (coreConn, null);
        }
        catch (Exception ex)
        {
            return (null, $"DB error: {ex.Message}");
        }
    }

    /// <summary>Replaces the Initial Catalog in a connection string.</summary>
    private static string ReplaceInitialCatalog(string connStr, string newCatalog)
    {
        var sb = new System.Text.StringBuilder();
        var parts = connStr.Split(';');
        bool found = false;
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            if (part.Trim().StartsWith("Initial Catalog", StringComparison.OrdinalIgnoreCase) ||
                part.Trim().StartsWith("Database", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append($"Initial Catalog={newCatalog};");
                found = true;
            }
            else
            {
                sb.Append(part.TrimEnd(';') + ";");
            }
        }
        if (!found) sb.Append($"Initial Catalog={newCatalog};");
        return sb.ToString().TrimEnd(';');
    }

    /// <summary>
    /// Returns just the DatabaseID (= Core application DB name) for a company from BQECoreHost.
    /// Used to tell the user which database to configure as Payment Service DB.
    /// </summary>
    public static async Task<string> GetCompanyDatabaseIdAsync(string? hostConnStr, string companyId)
    {
        if (string.IsNullOrWhiteSpace(hostConnStr) || !Guid.TryParse(companyId, out var cGuid))
            return "(unknown)";

        hostConnStr = SanitizeConnectionString(hostConnStr);
        hostConnStr = DbHelper.EnsureTrustedCert(hostConnStr);

        try
        {
            await using var conn = new SqlConnection(hostConnStr);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT TOP 1 DatabaseID FROM Company WHERE ID = @cid", conn);
            cmd.Parameters.Add(new SqlParameter("@cid", System.Data.SqlDbType.UniqueIdentifier) { Value = cGuid });
            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString() ?? "(null)";
        }
        catch (Exception ex) { return $"(error: {ex.Message})"; }
    }

    /// <summary>
    /// Returns the CoreApiURI (e.g. "http://localhost/coreapi/") for the given company
    /// from the Route table in BQECoreHost main DB.
    /// The WebApp uses CookieHelper.Route.ApiURI (= CoreApiURI) when building API calls.
    /// </summary>
    public static async Task<(string? apiUrl, string? error)> GetCoreApiUrlAsync(
        string? connectionString, string companyId)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(companyId))
            return (null, "Connection string or company ID missing.");

        if (!Guid.TryParse(companyId, out var companyGuid))
            return (null, $"Invalid company ID: {companyId}");

        connectionString = SanitizeConnectionString(connectionString);
        connectionString = DbHelper.EnsureTrustedCert(connectionString);

        // Join AccountCompany → Route to get the company's CoreApiURI
        const string sql = @"
SELECT TOP 1 r.CoreApiURI
FROM   AccountCompany ac
INNER JOIN Route r ON r.ID = ac.Route_ID
WHERE  ac.Company_ID = @companyId
UNION
SELECT TOP 1 CoreApiURI FROM Route WHERE ID = (
    SELECT TOP 1 Route_ID FROM Company WHERE ID = @companyId
)";

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@companyId", System.Data.SqlDbType.UniqueIdentifier)
                { Value = companyGuid });
            var result = await cmd.ExecuteScalarAsync();
            if (result is not null && result != DBNull.Value)
            {
                var url = result.ToString()?.TrimEnd('/') + "/";
                return (url, null);
            }
            // Fallback: return the first/only route
            using var cmd2 = new SqlCommand("SELECT TOP 1 CoreApiURI FROM Route ORDER BY ID", conn);
            var fallback = await cmd2.ExecuteScalarAsync();
            return fallback is not null && fallback != DBNull.Value
                ? (fallback.ToString()?.TrimEnd('/') + "/", null)
                : (null, "No Route record found in DB.");
        }
        catch (Exception ex)
        {
            return (null, $"DB error: {ex.Message}");
        }
    }

    /// <summary>
    /// Queries the BQECoreHost MAIN DB by email to return AccountID + CompanyID in one shot.
    /// Strategy: Account.Email → AccountID, then AccountCompany → CompanyID.
    /// </summary>
    public static async Task<(string? accountId, string? companyId, string? error)>
        GetAccountAndCompanyByEmailAsync(string? mainConnStr, string email)
    {
        if (string.IsNullOrWhiteSpace(mainConnStr) || string.IsNullOrWhiteSpace(email))
            return (null, null, "Connection string or email is empty.");

        mainConnStr = SanitizeConnectionString(mainConnStr);
        mainConnStr = DbHelper.EnsureTrustedCert(mainConnStr);

        try
        {
            await using var conn = new SqlConnection(mainConnStr);
            await conn.OpenAsync();

            // Step 1 — AccountID from Account.Email
            string? accountId = null;
            const string sqlAccount = @"SELECT TOP 1 CAST(ID AS NVARCHAR(50)) FROM Account WHERE Email = @email";
            using (var cmd = new SqlCommand(sqlAccount, conn))
            {
                cmd.Parameters.Add(new SqlParameter("@email", System.Data.SqlDbType.NVarChar, 200) { Value = email });
                var res = await cmd.ExecuteScalarAsync();
                if (res is not null && res != DBNull.Value)
                    accountId = res.ToString();
            }

            if (accountId is null)
                return (null, null, $"No Account found for email: {email}");

            // Step 2 — CompanyID from AccountCompany or Company.Account_ID
            string? companyId = null;
            var companyStrategies = new[]
            {
                (@"SELECT TOP 1 CAST(Company_ID AS NVARCHAR(50)) FROM AccountCompany WHERE Account_ID = @accountId",
                 "AccountCompany"),
                (@"SELECT TOP 1 CAST(ID AS NVARCHAR(50)) FROM Company WHERE Account_ID = @accountId",
                 "Company.Account_ID"),
            };

            if (Guid.TryParse(accountId, out var accountGuid))
            {
                foreach (var (sql, _) in companyStrategies)
                {
                    using var cmd = new SqlCommand(sql, conn);
                    cmd.Parameters.Add(new SqlParameter("@accountId", System.Data.SqlDbType.UniqueIdentifier) { Value = accountGuid });
                    var res = await cmd.ExecuteScalarAsync();
                    if (res is not null && res != DBNull.Value)
                    {
                        companyId = res.ToString();
                        break;
                    }
                }
            }

            return (accountId, companyId, null);
        }
        catch (Exception ex)
        {
            return (null, null, $"DB error: {ex.Message}");
        }
    }

    /// <summary>
    /// Queries the BQECoreHost MAIN DB by email and returns ALL (AccountID, CompanyID, CompanyName)
    /// tuples associated with that email — one per company the account is linked to via AccountCompany.
    /// Falls back to Company.Account_ID if AccountCompany has no rows for the account.
    /// </summary>
    public static async Task<(List<(string accountId, string companyId, string companyName)> results, string? error)>
        GetAllAccountCompaniesByEmailAsync(string? mainConnStr, string email)
    {
        var empty = new List<(string, string, string)>();

        if (string.IsNullOrWhiteSpace(mainConnStr) || string.IsNullOrWhiteSpace(email))
            return (empty, "Connection string or email is empty.");

        mainConnStr = SanitizeConnectionString(mainConnStr);
        mainConnStr = DbHelper.EnsureTrustedCert(mainConnStr);

        try
        {
            await using var conn = new SqlConnection(mainConnStr);
            await conn.OpenAsync();

            // Step 1 — get ALL AccountIDs matching this email (normally one, but handle duplicates)
            var accountIds = new List<string>();
            const string sqlAccounts = @"SELECT CAST(ID AS NVARCHAR(50)) FROM Account WHERE Email = @email";
            using (var cmd = new SqlCommand(sqlAccounts, conn))
            {
                cmd.Parameters.Add(new SqlParameter("@email", System.Data.SqlDbType.NVarChar, 200) { Value = email });
                using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                    if (!rdr.IsDBNull(0)) accountIds.Add(rdr.GetString(0));
            }

            if (accountIds.Count == 0)
                return (empty, $"No Account found for email: {email}");

            var results = new List<(string accountId, string companyId, string companyName)>();

            foreach (var accountId in accountIds)
            {
                if (!Guid.TryParse(accountId, out var accountGuid)) continue;

                // Strategy 1: AccountCompany join — may return multiple companies per account
                const string sqlAC = @"
SELECT CAST(ac.Company_ID AS NVARCHAR(50)),
       ISNULL(c.Name, '') AS CompanyName
FROM   AccountCompany ac
LEFT JOIN Company c ON c.ID = ac.Company_ID
WHERE  ac.Account_ID = @accountId";

                bool foundViaAC = false;
                using (var cmd = new SqlCommand(sqlAC, conn))
                {
                    cmd.Parameters.Add(new SqlParameter("@accountId", System.Data.SqlDbType.UniqueIdentifier) { Value = accountGuid });
                    using var rdr = await cmd.ExecuteReaderAsync();
                    while (await rdr.ReadAsync())
                    {
                        if (rdr.IsDBNull(0)) continue;
                        foundViaAC = true;
                        results.Add((accountId, rdr.GetString(0), rdr.IsDBNull(1) ? "" : rdr.GetString(1)));
                    }
                }

                if (foundViaAC) continue;

                // Strategy 2: Company.Account_ID fallback
                const string sqlComp = @"
SELECT CAST(ID AS NVARCHAR(50)),
       ISNULL(Name, '') AS CompanyName
FROM   Company
WHERE  Account_ID = @accountId";

                using var cmd2 = new SqlCommand(sqlComp, conn);
                cmd2.Parameters.Add(new SqlParameter("@accountId", System.Data.SqlDbType.UniqueIdentifier) { Value = accountGuid });
                using var rdr2 = await cmd2.ExecuteReaderAsync();
                while (await rdr2.ReadAsync())
                {
                    if (rdr2.IsDBNull(0)) continue;
                    results.Add((accountId, rdr2.GetString(0), rdr2.IsDBNull(1) ? "" : rdr2.GetString(1)));
                }
            }

            if (results.Count == 0)
                return (empty, $"Account found but no linked Company for email: {email}");

            return (results, null);
        }
        catch (Exception ex)
        {
            return (empty, $"DB error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns all companies linked to the given Account ID via AccountCompany table.
    /// Used by the Subscriptions tab to show a company picker when an account has multiple companies.
    /// </summary>
    public static async Task<(List<(string companyId, string companyName)> results, string? error)>
        GetCompaniesByAccountIdAsync(string? mainConnStr, string accountId)
    {
        var empty = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(mainConnStr) || string.IsNullOrWhiteSpace(accountId))
            return (empty, "Connection string or account ID is empty.");

        if (!Guid.TryParse(accountId, out var accountGuid))
            return (empty, $"Invalid Account ID: {accountId}");

        mainConnStr = SanitizeConnectionString(mainConnStr);
        mainConnStr = DbHelper.EnsureTrustedCert(mainConnStr);

        try
        {
            await using var conn = new SqlConnection(mainConnStr);
            await conn.OpenAsync();

            const string sql = @"
SELECT CAST(ac.Company_ID AS NVARCHAR(50)),
       ISNULL(c.Name, CAST(ac.Company_ID AS NVARCHAR(50))) AS CompanyName
FROM   AccountCompany ac
LEFT JOIN Company c ON c.ID = ac.Company_ID
WHERE  ac.Account_ID = @accountId
UNION
SELECT CAST(c2.ID AS NVARCHAR(50)),
       ISNULL(c2.Name, CAST(c2.ID AS NVARCHAR(50)))
FROM   Company c2
WHERE  c2.Account_ID = @accountId
  AND  NOT EXISTS (
         SELECT 1 FROM AccountCompany ac2 WHERE ac2.Account_ID = @accountId
       )";

            var results = new List<(string, string)>();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@accountId", System.Data.SqlDbType.UniqueIdentifier) { Value = accountGuid });
            using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                if (rdr.IsDBNull(0)) continue;
                results.Add((rdr.GetString(0), rdr.IsDBNull(1) ? rdr.GetString(0) : rdr.GetString(1)));
            }

            return results.Count == 0
                ? (empty, "No companies found for this Account ID.")
                : (results, null);
        }
        catch (Exception ex)
        {
            return (empty, $"DB error: {ex.Message}");
        }
    }

    /// <summary>
    /// Strips any accidental prefix before the first recognised SQL connection-string keyword
    /// (handles copy-paste of JSON key names, whitespace, quotes, etc.).
    /// </summary>
    /// <summary>
    /// Returns all companies from the main Core DB (AccountCompany JOIN Company).
    /// Used to populate the Subscriptions tab Company ID dropdown with every company.
    /// </summary>
    public static async Task<(List<(string companyId, string companyName, string accountId)> results, string? error)>
        GetAllCompaniesAsync(string? mainConnStr)
    {
        var empty = new List<(string, string, string)>();
        if (string.IsNullOrWhiteSpace(mainConnStr)) return (empty, "No Core DB connection configured.");

        mainConnStr = SanitizeConnectionString(mainConnStr);
        mainConnStr = DbHelper.EnsureTrustedCert(mainConnStr);

        const string sql = @"
SELECT DISTINCT
    CAST(ac.Company_ID AS NVARCHAR(50)) AS CompanyId,
    ISNULL(c.Name, CAST(ac.Company_ID AS NVARCHAR(50))) AS CompanyName,
    CAST(ac.Account_ID AS NVARCHAR(50)) AS AccountId
FROM AccountCompany ac
LEFT JOIN Company c ON c.ID = ac.Company_ID
WHERE ac.Company_ID IS NOT NULL
ORDER BY c.Name";

        try
        {
            await using var conn = new SqlConnection(mainConnStr);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
            await using var rdr = await cmd.ExecuteReaderAsync();

            var results = new List<(string, string, string)>();
            while (await rdr.ReadAsync())
            {
                var cid   = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                var cname = rdr.IsDBNull(1) ? cid : rdr.GetString(1);
                var aid   = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
                if (!string.IsNullOrEmpty(cid))
                    results.Add((cid, cname, aid));
            }

            return results.Count > 0
                ? (results, null)
                : (empty, "No companies found in the Core DB.");
        }
        catch (Exception ex)
        {
            return (empty, $"DB error: {ex.Message}");
        }
    }

    public static string SanitizeConnectionStringPublic(string cs) => SanitizeConnectionString(cs);
    public static string ReplaceInitialCatalogPublic(string connStr, string catalog) => ReplaceInitialCatalog(connStr, catalog);
    private static string SanitizeConnectionString(string cs)
    {
        cs = cs.Trim().Trim('"').Trim('\'');

        // Find the first occurrence of a known keyword at the start of a token
        string[] startKeywords = ["Data Source=", "Server=", "data source=", "server="];
        foreach (var kw in startKeywords)
        {
            var idx = cs.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
                return cs[idx..];
        }
        return cs;
    }

    /// <summary>
    /// Fetches ALL data needed to pre-fill the Payrix merchant signup portal URL.
    /// Queries Company, Account, and Employee tables directly from BQECoreHost main DB.
    /// Returns a strongly-typed record with every field; empty strings for missing values.
    /// </summary>
    public static async Task<MerchantSignupData> GetMerchantSignupDataAsync(
        string? hostConnStr, string companyId, string accountId)
    {
        var result = new MerchantSignupData { Custom = $"{accountId},{companyId}" };

        if (string.IsNullOrWhiteSpace(hostConnStr)) return result;

        hostConnStr = SanitizeConnectionString(hostConnStr);
        hostConnStr = DbHelper.EnsureTrustedCert(hostConnStr);

        try
        {
            await using var conn = new SqlConnection(hostConnStr);
            await conn.OpenAsync();

            // ── Company details from Company table in BQECoreHost ────────────
            // Only Name is guaranteed; other fields depend on schema version
            if (Guid.TryParse(companyId, out var cGuid))
            {
                // Detect which columns exist to avoid "Invalid column name" errors
                var compCols = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var colCmd = new SqlCommand(
                    "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='Company'", conn))
                using (var colRdr = await colCmd.ExecuteReaderAsync())
                    while (await colRdr.ReadAsync()) compCols.Add(colRdr.GetString(0));

                // Build SELECT dynamically — only include columns that exist
                var selects = new System.Collections.Generic.List<string> { "ISNULL(c.Name,'') AS Name" };
                void AddCol(string col, string alias)
                { if (compCols.Contains(col)) selects.Add($"ISNULL(c.{col},'') AS {alias}"); }

                AddCol("Email",   "Email");
                AddCol("Phone",   "Phone");
                AddCol("Address", "Address");
                AddCol("Address2","Address2");
                AddCol("City",    "City");
                AddCol("State",   "State");
                AddCol("Zip",     "Zip");
                AddCol("Country", "Country");
                AddCol("URL",     "URL");
                AddCol("Website", "URL");

                var sql = $"SELECT TOP 1 {string.Join(",", selects)} FROM Company c WHERE c.ID = @cid";
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.Add(new SqlParameter("@cid", System.Data.SqlDbType.UniqueIdentifier) { Value = cGuid });
                using var rdr = await cmd.ExecuteReaderAsync();
                if (await rdr.ReadAsync())
                {
                    result.CompanyName = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                    // Read remaining columns by index (only those added above)
                    int i = 1;
                    if (compCols.Contains("Email"))   result.CompanyEmail = rdr.IsDBNull(i) ? "" : rdr.GetString(i++); else i++;
                    if (compCols.Contains("Phone"))   result.CompanyPhone = (rdr.IsDBNull(i) ? "" : rdr.GetString(i++)).Replace(" ","").Replace("-","").Replace("(","").Replace(")",""); else i++;
                    if (compCols.Contains("Address")) result.Address1     = rdr.IsDBNull(i) ? "" : rdr.GetString(i++); else i++;
                    if (compCols.Contains("Address2"))result.Address2     = rdr.IsDBNull(i) ? "" : rdr.GetString(i++); else i++;
                    if (compCols.Contains("City"))    result.City         = rdr.IsDBNull(i) ? "" : rdr.GetString(i++); else i++;
                    if (compCols.Contains("State"))   result.State        = rdr.IsDBNull(i) ? "" : rdr.GetString(i++); else i++;
                    if (compCols.Contains("Zip"))     result.Zip          = rdr.IsDBNull(i) ? "" : rdr.GetString(i++); else i++;
                    if (compCols.Contains("Country")) result.Country      = (rdr.IsDBNull(i) ? "" : rdr.GetString(i++)) is { Length: > 0 } c2 ? c2 : "USA"; else i++;
                    if (compCols.Contains("URL") || compCols.Contains("Website"))
                        result.Website = rdr.IsDBNull(i) ? "" : rdr.GetString(i);
                }
            }

            // ── Account / Owner details ─────────────────────────────────────
            if (Guid.TryParse(accountId, out var aGuid))
            {
                // Step 1: Get email from Account table (always exists)
                try
                {
                    using var emailCmd = new SqlCommand("SELECT TOP 1 ISNULL(Email,'') FROM Account WHERE ID=@aid", conn);
                    emailCmd.Parameters.Add(new SqlParameter("@aid", System.Data.SqlDbType.UniqueIdentifier) { Value = aGuid });
                    var emailVal = await emailCmd.ExecuteScalarAsync();
                    if (emailVal is string em && !string.IsNullOrEmpty(em))
                        result.OwnerEmail = em;
                }
                catch { }

                // Step 2: Get name from Profile table (BQECoreHost) — columns: FirstName, LastName
                try
                {
                    const string profileSql = @"
SELECT TOP 1
    ISNULL(p.FirstName, '') AS FirstName,
    ISNULL(p.LastName,  '') AS LastName,
    ISNULL(p.Phone,     '') AS Phone
FROM Profile p WHERE p.Account_ID = @aid";
                    using var cmd2 = new SqlCommand(profileSql, conn);
                    cmd2.Parameters.Add(new SqlParameter("@aid", System.Data.SqlDbType.UniqueIdentifier) { Value = aGuid });
                    using var rdr2 = await cmd2.ExecuteReaderAsync();
                    if (await rdr2.ReadAsync())
                    {
                        if (!rdr2.IsDBNull(0)) result.OwnerFirst = rdr2.GetString(0);
                        if (!rdr2.IsDBNull(1)) result.OwnerLast  = rdr2.GetString(1);
                        if (!rdr2.IsDBNull(2)) result.OwnerPhone = rdr2.GetString(2).Replace(" ","").Replace("-","").Replace("(","").Replace(")","");
                    }
                }
                catch { }

                // Step 3: If name still empty, try Employee table in CORE DB
                // Employee table columns: EmpFName, EmpLName (NOT FirstName/LastName)
                if (string.IsNullOrEmpty(result.OwnerFirst) && !string.IsNullOrEmpty(result.OwnerEmail))
                {
                    try
                    {
                        // Get Core DB connection for this company
                        var (coreConn3, _) = await GetCompanyDbConnectionAsync(hostConnStr, companyId, accountId);
                        if (!string.IsNullOrEmpty(coreConn3))
                        {
                            await using var coreConn4 = new SqlConnection(coreConn3);
                            await coreConn4.OpenAsync();
                            using var empCmd = new SqlCommand(@"
SELECT TOP 1 ISNULL(EmpFName,'') AS First, ISNULL(EmpLName,'') AS Last
FROM Employee WHERE Email = @email", coreConn4);
                            empCmd.Parameters.AddWithValue("@email", result.OwnerEmail);
                            using var empRdr = await empCmd.ExecuteReaderAsync();
                            if (await empRdr.ReadAsync())
                            {
                                if (!empRdr.IsDBNull(0) && string.IsNullOrEmpty(result.OwnerFirst)) result.OwnerFirst = empRdr.GetString(0);
                                if (!empRdr.IsDBNull(1) && string.IsNullOrEmpty(result.OwnerLast))  result.OwnerLast  = empRdr.GetString(1);
                            }
                        }
                    }
                    catch { }
                }

                // Legacy fallback — old Employee query with wrong column names (kept for safety)
                if (string.IsNullOrEmpty(result.OwnerFirst))
                {
                    try
                    {
                        const string empSql = @"
SELECT TOP 1
    ISNULL(e.EmpFName, '') AS FirstName,
    ISNULL(e.EmpLName, '') AS LastName,
    ISNULL(e.Email,    '') AS Email,
    ISNULL(e.Phone,    '') AS Phone
FROM Employee e
INNER JOIN AccountCompany ac ON ac.Company_ID = e.Company_ID
WHERE ac.Account_ID = @aid";
                        using var cmd4 = new SqlCommand(empSql, conn);
                        cmd4.Parameters.Add(new SqlParameter("@aid", System.Data.SqlDbType.UniqueIdentifier) { Value = aGuid });
                        using var rdr4 = await cmd4.ExecuteReaderAsync();
                        if (await rdr4.ReadAsync())
                        {
                            if (string.IsNullOrEmpty(result.OwnerFirst)) result.OwnerFirst = rdr4.GetString(0);
                            if (string.IsNullOrEmpty(result.OwnerLast))  result.OwnerLast  = rdr4.GetString(1);
                            if (string.IsNullOrEmpty(result.OwnerEmail)) result.OwnerEmail = rdr4.GetString(2);
                            if (string.IsNullOrEmpty(result.OwnerPhone)) result.OwnerPhone = rdr4.GetString(3);
                        }
                    }
                    catch { }
                }
            }

            // Derive username from email
            if (!string.IsNullOrEmpty(result.OwnerEmail) && result.OwnerEmail.Contains('@'))
                result.Username = System.Text.RegularExpressions.Regex.Replace(
                    result.OwnerEmail[..result.OwnerEmail.IndexOf('@')], "[^a-zA-Z0-9]", "");
        }
        catch { /* return whatever we have */ }

        return result;
    }

    /// <summary>All data needed to pre-fill the Payrix merchant signup portal URL.</summary>
    public record MerchantSignupData
    {
        public string Custom       { get; set; } = "";
        public string CompanyName  { get; set; } = "";
        public string CompanyEmail { get; set; } = "";
        public string CompanyPhone { get; set; } = "";
        public string Address1     { get; set; } = "";
        public string Address2     { get; set; } = "";
        public string City         { get; set; } = "";
        public string State        { get; set; } = "";
        public string Zip          { get; set; } = "";
        public string Country      { get; set; } = "USA";
        public string Website      { get; set; } = "";
        public string OwnerFirst   { get; set; } = "";
        public string OwnerLast    { get; set; } = "";
        public string OwnerEmail   { get; set; } = "";
        public string OwnerPhone   { get; set; } = "";
        public string OwnerMI      { get; set; } = "";
        public string OwnerTitle   { get; set; } = "";
        public string Username     { get; set; } = "";
    }

    // ── ThirdPartySetting cleanup ─────────────────────────────────────────────

    /// <summary>
    /// Returns the set of Payrix merchant IDs that have a linked ThirdPartySetting (Type=19) row
    /// across all company databases. <paramref name="hostConnStr"/> must point to the BQECoreHost
    /// database (the one that has the Company table); each company-specific DB is resolved from it.
    /// </summary>
    public static async Task<(HashSet<string> ids, string? error)>
        GetLinkedMerchantIdsAsync(string hostConnStr, IEnumerable<string> candidateIds)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(hostConnStr)) return (result, null);

        var ids = candidateIds.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (ids.Count == 0) return (result, null);

        hostConnStr = SanitizeConnectionString(hostConnStr);
        hostConnStr = DbHelper.EnsureTrustedCert(hostConnStr);

        try
        {
            // Step 1: get all company DB names from the host (framework) DB
            var companyDbs = new List<string>();
            await using (var hostConn = new SqlConnection(hostConnStr))
            {
                await hostConn.OpenAsync();
                await using var cmd = new SqlCommand(
                    "SELECT DISTINCT DatabaseID FROM Company WHERE DatabaseID IS NOT NULL AND DatabaseID <> ''",
                    hostConn);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var dbName = reader.GetString(0).Trim();
                    if (!string.IsNullOrWhiteSpace(dbName))
                        companyDbs.Add(dbName);
                }
            }

            if (companyDbs.Count == 0) return (result, "No company databases found in Company table.");

            // Step 2: search ThirdPartySettings in each company DB
            var tokens = new List<string>();
            foreach (var dbName in companyDbs)
            {
                var companyConnStr = ReplaceInitialCatalogPublic(hostConnStr, dbName);
                try
                {
                    await using var companyConn = new SqlConnection(companyConnStr);
                    await companyConn.OpenAsync();
                    await using var cmd = new SqlCommand(
                        "SELECT AccessToken FROM ThirdPartySettings WHERE Type = 19 AND AccessToken IS NOT NULL",
                        companyConn);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        tokens.Add(reader.GetString(0));
                }
                catch
                {
                    // Skip company DBs that are unavailable or missing the table
                }
            }

            foreach (var mid in ids)
                if (tokens.Any(t => t.IndexOf(mid, StringComparison.OrdinalIgnoreCase) >= 0))
                    result.Add(mid);

            return (result, null);
        }
        catch (Exception ex)
        {
            return (result, $"DB error: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes ThirdPartySetting / BQSTable / PaymentService rows for a given merchant ID across
    /// all company databases. <paramref name="hostConnStr"/> must point to the BQECoreHost database
    /// (the one that has the Company table); each company-specific DB is resolved from it.
    /// Returns (totalRowsDeleted, error).
    /// </summary>
    public static async Task<(int rows, string? error)>
        DeleteThirdPartySettingAsync(string hostConnStr, string merchantId)
    {
        if (string.IsNullOrWhiteSpace(hostConnStr)) return (0, "No Core DB connection string.");
        if (string.IsNullOrWhiteSpace(merchantId))  return (0, "No merchant ID provided.");

        hostConnStr = SanitizeConnectionString(hostConnStr);
        hostConnStr = DbHelper.EnsureTrustedCert(hostConnStr);

        try
        {
            // Step 1: get all company DB names from the host DB
            var companyDbs = new List<string>();
            await using (var hostConn = new SqlConnection(hostConnStr))
            {
                await hostConn.OpenAsync();
                await using var cmd = new SqlCommand(
                    "SELECT DISTINCT DatabaseID FROM Company WHERE DatabaseID IS NOT NULL AND DatabaseID <> ''",
                    hostConn);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var dbName = reader.GetString(0).Trim();
                    if (!string.IsNullOrWhiteSpace(dbName))
                        companyDbs.Add(dbName);
                }
            }

            if (companyDbs.Count == 0) return (0, "No company databases found in Company table.");

            // Step 2: delete from each company DB
            int totalRows = 0;
            foreach (var dbName in companyDbs)
            {
                var companyConnStr = ReplaceInitialCatalogPublic(hostConnStr, dbName);
                try
                {
                    await using var conn = new SqlConnection(companyConnStr);
                    await conn.OpenAsync();

                    await using var delTps = new SqlCommand(@"
                        DELETE FROM ThirdPartySettings
                        WHERE Type = 19
                          AND (AccessToken LIKE '%' + @mid + '%'
                            OR AccessToken LIKE '%' + LOWER(@mid) + '%')", conn);
                    delTps.Parameters.AddWithValue("@mid", merchantId);
                    totalRows += await delTps.ExecuteNonQueryAsync();

                    await using var delKv = new SqlCommand(@"
                        DELETE FROM BQSTable
                        WHERE ParamName LIKE 'PaymentService_SignUpProcess_Payrix%'
                          AND ParamValue LIKE '%' + @mid + '%'", conn);
                    delKv.Parameters.AddWithValue("@mid", merchantId);
                    await delKv.ExecuteNonQueryAsync();

                    await using var delPs = new SqlCommand(@"
                        DELETE FROM PaymentService
                        WHERE AccessToken LIKE '%' + @mid + '%'", conn);
                    delPs.Parameters.AddWithValue("@mid", merchantId);
                    await delPs.ExecuteNonQueryAsync();
                }
                catch
                {
                    // Skip company DBs that are unavailable or missing the tables
                }
            }

            return (totalRows, null);
        }
        catch (Exception ex)
        {
            return (0, $"DB error: {ex.Message}");
        }
    }
}
