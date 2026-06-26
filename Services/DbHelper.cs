using Microsoft.Data.SqlClient;

namespace PayrixLauncher.Services;

public static class DbHelper
{
    public static string EnsureTrustedCert(string connStr)
    {
        if (string.IsNullOrWhiteSpace(connStr)) return connStr;
        try
        {
            var b = new SqlConnectionStringBuilder(connStr.Trim().TrimEnd(';'))
            {
                TrustServerCertificate = true
            };
            return b.ConnectionString;
        }
        catch
        {
            // Fallback: force-replace any existing =False, then append if absent
            var result = System.Text.RegularExpressions.Regex.Replace(
                connStr,
                @"(Trust\s*Server\s*Certificate\s*=\s*)False\b",
                "${1}True",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!result.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase) &&
                !result.Contains("Trust Server Certificate", StringComparison.OrdinalIgnoreCase))
                result = result.TrimEnd(';') + ";TrustServerCertificate=True";
            return result;
        }
    }

    public static async Task<string> GetCompanyDbColumnAsync(SqlConnection openConn)
    {
        const string sql = @"
SELECT TOP 1 COLUMN_NAME
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_NAME = 'Company'
  AND  COLUMN_NAME IN ('DatabaseID','DatabaseName','DB_Name','CompanyDatabase','DBName')
ORDER BY CASE COLUMN_NAME
    WHEN 'DatabaseID'      THEN 1
    WHEN 'DatabaseName'    THEN 2
    WHEN 'DB_Name'         THEN 3
    WHEN 'CompanyDatabase' THEN 4
    WHEN 'DBName'          THEN 5
    ELSE 6 END";
        try
        {
            await using var cmd = new SqlCommand(sql, openConn);
            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString() ?? "DatabaseID";
        }
        catch { return "DatabaseID"; }
    }
}
