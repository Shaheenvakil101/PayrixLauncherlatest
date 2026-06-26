using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PayrixLauncher.Models;

/// <summary>
/// Represents a single captured HTTP session — mirrors what Fiddler shows per row.
/// </summary>
public class TrafficSession : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private int    _result;
    private long   _durationMs = -1;
    private string _responseBody = "";
    private string _responseHeaders = "";

    // ── Session identity ─────────────────────────────────────────────────────
    public int    SessionId      { get; init; }
    public string Protocol       { get; init; } = "HTTPS";
    public string Method         { get; init; } = "GET";
    public string Host           { get; init; } = "";
    public string Url            { get; init; } = "";
    public string Source         { get; init; } = "";   // PayrixService / BqeAuthService / HTTP Client
    public System.DateTime CapturedAt { get; init; } = System.DateTime.Now;

    // ── Request ───────────────────────────────────────────────────────────────
    public string RequestHeaders { get; init; } = "";
    public string RequestBody    { get; init; } = "";
    public long   RequestBodyLen => System.Text.Encoding.UTF8.GetByteCount(RequestBody);

    // ── Response (set after response received) ────────────────────────────────
    public int Result
    {
        get => _result;
        set { _result = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResultColor)); OnPropertyChanged(nameof(ResultLabel)); }
    }

    public string ResponseHeaders
    {
        get => _responseHeaders;
        set { _responseHeaders = value; OnPropertyChanged(); OnPropertyChanged(nameof(ContentType)); }
    }

    public string ResponseBody
    {
        get => _responseBody;
        set { _responseBody = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResponseBodyLen)); OnPropertyChanged(nameof(ResponseBodyPretty)); }
    }

    public long DurationMs
    {
        get => _durationMs;
        set { _durationMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(DurationLabel)); }
    }

    // ── Computed display helpers ───────────────────────────────────────────────
    public string ResultLabel  => Result == 0 ? "…" : Result.ToString();
    public string DurationLabel => DurationMs < 0 ? "…" : DurationMs < 1000 ? $"{DurationMs} ms" : $"{DurationMs / 1000.0:F1} s";
    public long   ResponseBodyLen => System.Text.Encoding.UTF8.GetByteCount(ResponseBody);
    public string BodySizeLabel => FormatBytes(ResponseBodyLen);

    public string ContentType
    {
        get
        {
            foreach (var line in ResponseHeaders.Split('\n'))
            {
                if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                    return line.Substring("Content-Type:".Length).Trim().Split(';')[0].Trim();
            }
            return "";
        }
    }

    public string ResultColor => Result switch
    {
        >= 200 and < 300 => "#17A34A",
        >= 300 and < 400 => "#F59E0B",
        >= 400 and < 500 => "#EF4444",
        >= 500           => "#DC2626",
        0                => "#64748B",
        _                => "#64748B"
    };

    public string RowColor => Result switch
    {
        >= 400 => "#3A1515",
        >= 300 => "#2A2A10",
        0      => "#1A1F2A",
        _      => "Transparent"
    };

    /// <summary>Response body pretty-printed if JSON, otherwise raw.</summary>
    public string ResponseBodyPretty
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ResponseBody)) return "";
            var t = ResponseBody.TrimStart();
            if (t.StartsWith("{") || t.StartsWith("["))
            {
                try
                {
                    var doc = JsonDocument.Parse(ResponseBody);
                    return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                }
                catch { }
            }
            return ResponseBody;
        }
    }

    private static string FormatBytes(long b) =>
        b < 1024 ? $"{b} B" :
        b < 1024 * 1024 ? $"{b / 1024.0:F1} KB" :
        $"{b / (1024.0 * 1024):F1} MB";
}
