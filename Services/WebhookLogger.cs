using System.IO;
using System.Text;

namespace PayrixLauncher.Services;

/// <summary>
/// Writes a structured, daily-rotating log of every webhook POST and Fetch operation.
/// Log files are written to  &lt;AppDir&gt;/logs/webhook-yyyy-MM-dd.log
/// </summary>
public static class WebhookLogger
{
    // ── Log directory ────────────────────────────────────────────────────────

    private static readonly string LogDir =
        Path.Combine(AppContext.BaseDirectory, "logs");

    public static string LogFilePath =>
        Path.Combine(LogDir, $"webhook-{DateTime.Now:yyyy-MM-dd}.log");

    private static readonly object _lock = new();

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Log a POST request + response (manual ⚡ Send POST button).</summary>
    public static void LogPost(
        string webhookType,
        string url,
        string payload,
        int? httpCode,
        string responseBody,
        long durationMs,
        Exception? exception = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Separator('═'));
        sb.AppendLine($"[{Timestamp()}]  POST  ▸  {webhookType}");
        sb.AppendLine($"URL      : {url}");
        sb.AppendLine($"Time     : {durationMs} ms");

        sb.AppendLine();
        sb.AppendLine("── REQUEST PAYLOAD ──────────────────────────────────────────────────────────");
        sb.AppendLine(payload.Trim());

        sb.AppendLine();
        sb.AppendLine("── RESPONSE ─────────────────────────────────────────────────────────────────");
        if (exception is not null)
        {
            sb.AppendLine($"EXCEPTION : {exception.GetType().Name}");
            // Walk the full inner-exception chain so the real cause is visible
            var ex = exception;
            int depth = 0;
            while (ex is not null)
            {
                var indent = depth == 0 ? "  Cause   : " : $"  Inner {depth}  : ";
                sb.AppendLine($"{indent}[{ex.GetType().Name}] {ex.Message}");
                ex = ex.InnerException;
                depth++;
            }
        }
        else
        {
            var success = httpCode is >= 200 and < 300;
            sb.AppendLine($"HTTP     : {httpCode}  {(success ? "✓ OK" : "✗ FAILED")}");
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                sb.AppendLine();
                sb.AppendLine(TryPrettyPrint(responseBody));
            }
        }

        sb.AppendLine(Separator('─'));
        sb.AppendLine();

        Write(sb.ToString());
    }

    /// <summary>Log a test-runner POST (single test or run-all).</summary>
    public static void LogTestPost(
        string testName,
        string url,
        string payload,
        int? httpCode,
        string detail,
        long durationMs,
        bool passed)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Separator('─'));
        sb.AppendLine($"[{Timestamp()}]  TEST  ▸  {testName}  {(passed ? "✓ PASS" : "✗ FAIL")}");
        sb.AppendLine($"URL      : {url}");
        sb.AppendLine($"HTTP     : {httpCode?.ToString() ?? "—"}   ({durationMs} ms)");
        sb.AppendLine($"Result   : {detail}");

        sb.AppendLine();
        sb.AppendLine("── PAYLOAD ──────────────────────────────────────────────────────────────────");
        sb.AppendLine(payload.Trim());
        sb.AppendLine();

        Write(sb.ToString());
    }

    /// <summary>Log a Fetch-from-Payrix operation.</summary>
    public static void LogFetch(
        string webhookType,
        string environment,
        string rawJson,
        string? builtPayload,
        string? error)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Separator('─'));
        sb.AppendLine($"[{Timestamp()}]  FETCH  ▸  {webhookType}  [{environment}]  {(error is null ? "✓ OK" : "✗ " + error)}");

        sb.AppendLine();
        sb.AppendLine("── RAW PAYRIX RESPONSE ──────────────────────────────────────────────────────");
        sb.AppendLine(TryPrettyPrint(rawJson));

        if (builtPayload is not null)
        {
            sb.AppendLine();
            sb.AppendLine("── BUILT WEBHOOK PAYLOAD ────────────────────────────────────────────────────");
            sb.AppendLine(builtPayload.Trim());
        }
        sb.AppendLine();

        Write(sb.ToString());
    }

    /// <summary>
    /// Log any error with context — entity creation, merchant creation, DB errors,
    /// signup failures, etc.  Always written regardless of success/failure.
    /// </summary>
    public static void LogError(
        string operation,
        string error,
        string? details     = null,
        string? requestJson = null,
        Exception? exception = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Separator('─'));
        sb.AppendLine($"[{Timestamp()}]  ❌ ERROR  ▸  {operation}");
        sb.AppendLine($"Error    : {error}");

        if (!string.IsNullOrWhiteSpace(details))
        {
            sb.AppendLine();
            sb.AppendLine("── DETAILS ──────────────────────────────────────────────────────────────────");
            sb.AppendLine(details.Trim());
        }

        if (!string.IsNullOrWhiteSpace(requestJson))
        {
            sb.AppendLine();
            sb.AppendLine("── REQUEST ──────────────────────────────────────────────────────────────────");
            sb.AppendLine(requestJson.Trim());
        }

        if (exception is not null)
        {
            sb.AppendLine();
            sb.AppendLine("── EXCEPTION ────────────────────────────────────────────────────────────────");
            var ex = exception;
            while (ex != null)
            {
                sb.AppendLine($"  {ex.GetType().Name}: {ex.Message}");
                ex = ex.InnerException;
            }
            sb.AppendLine($"StackTrace: {exception.StackTrace}");
        }

        sb.AppendLine();
        Write(sb.ToString());
    }

    /// <summary>Log a successful operation for audit trail.</summary>
    public static void LogSuccess(string operation, string details)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{Timestamp()}]  ✅ OK  ▸  {operation}  —  {details}");
        sb.AppendLine();
        Write(sb.ToString());
    }

    /// <summary>Returns the path of today's log file (creates if needed) — for "Open" button.</summary>
    public static string EnsureAndGetPath()
    {
        Directory.CreateDirectory(LogDir);
        var path = LogFilePath;
        if (!File.Exists(path))
            File.WriteAllText(path,
                $"PayrixLauncher webhook log — created {Timestamp()}{Environment.NewLine}{Environment.NewLine}");
        return path;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static void Write(string text)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            lock (_lock)
                File.AppendAllText(LogFilePath, text, Encoding.UTF8);
        }
        catch { /* never crash the app because of logging */ }
    }

    private static string Timestamp() =>
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

    private static string Separator(char ch) =>
        new(ch, 80);

    private static string TryPrettyPrint(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(empty)";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(
                doc.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
