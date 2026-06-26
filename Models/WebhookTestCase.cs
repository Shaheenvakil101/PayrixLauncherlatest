using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PayrixLauncher.Models;

public class WebhookTestCase : INotifyPropertyChanged
{
    public string Name        { get; init; } = "";
    public string Description { get; init; } = "";
    public string Tag         { get; init; } = "";

    private string _payload = "";
    public string Payload
    {
        get => _payload;
        set
        {
            _payload = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EntityId));
            OnPropertyChanged(nameof(EntityName));
            OnPropertyChanged(nameof(PayrixCreatedDisplay));
            OnPropertyChanged(nameof(PayrixSentDisplay));
        }
    }

    /// <summary>Entity ID extracted from data[0].entity in the webhook payload.</summary>
    public string EntityId => ExtractPayloadField("entity");

    /// <summary>
    /// Entity name — extracted from alert.entityName (Merchant Boarded webhook)
    /// or alert.merchantName as fallback.
    /// </summary>
    public string EntityName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_payload)) return "";
            try
            {
                using var doc = JsonDocument.Parse(_payload);
                var root = doc.RootElement;
                // Navigate to response.alert
                JsonElement alert = default;
                if (root.TryGetProperty("response", out var resp) &&
                    resp.TryGetProperty("alert", out alert))
                { /* alert set */ }
                else if (root.TryGetProperty("alert", out alert))
                { /* flat */ }

                if (alert.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "entityName", "merchantName" })
                        if (alert.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                        {
                            var s = v.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) return s;
                        }
                }
            }
            catch { }
            return "";
        }
    }

    /// <summary>Webhook created timestamp from alert.created in the payload.</summary>
    public string PayrixCreatedDisplay => ExtractAlertField("created");

    /// <summary>Webhook processed/sent timestamp from alert.processed in the payload.</summary>
    public string PayrixSentDisplay => ExtractAlertField("processed");

    private string ExtractAlertField(string field)
    {
        if (string.IsNullOrWhiteSpace(_payload)) return "";
        try
        {
            using var doc = JsonDocument.Parse(_payload);
            var root = doc.RootElement;
            JsonElement alert = default;
            if (root.TryGetProperty("response", out var resp) && resp.TryGetProperty("alert", out alert)) { }
            else if (root.TryGetProperty("alert", out alert)) { }
            if (alert.ValueKind == JsonValueKind.Object &&
                alert.TryGetProperty(field, out var v) &&
                v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private string ExtractPayloadField(string field)
    {
        if (string.IsNullOrWhiteSpace(_payload)) return "";
        try
        {
            using var doc = JsonDocument.Parse(_payload);
            var root = doc.RootElement;
            foreach (var path in new Func<JsonElement, (bool ok, string? val)>[]
            {
                r => r.TryGetProperty("response", out var rr) &&
                     rr.TryGetProperty("data", out var d) &&
                     d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0 &&
                     d[0].TryGetProperty(field, out var v) ? (true, v.GetString()) : (false, null),
                r => r.TryGetProperty("data", out var d) &&
                     d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0 &&
                     d[0].TryGetProperty(field, out var v) ? (true, v.GetString()) : (false, null),
                r => r.TryGetProperty(field, out var v) ? (true, v.GetString()) : (false, null),
            })
            {
                var (ok, val) = path(root);
                if (ok && !string.IsNullOrEmpty(val)) return val!;
            }
        }
        catch { }
        return "";
    }

    // Tag badge colours
    public string TagBg => Tag switch
    {
        "Entities" => "#EDE9FE",
        "Merchant" => "#DCFCE7",
        "Payment"  => "#DBEAFE",
        _          => "#F1F5F9"
    };
    public string TagFg => Tag switch
    {
        "Entities" => "#6D28D9",
        "Merchant" => "#15803D",
        "Payment"  => "#1D4ED8",
        _          => "#64748B"
    };

    private TestStatus _status = TestStatus.Pending;
    public TestStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusLabel)); OnPropertyChanged(nameof(IsPending)); OnPropertyChanged(nameof(IsFailed)); }
    }

    private int? _httpCode;
    public int? HttpCode
    {
        get => _httpCode;
        set { _httpCode = value; OnPropertyChanged(); }
    }

    private long _durationMs;
    public long DurationMs
    {
        get => _durationMs;
        set { _durationMs = value; OnPropertyChanged(); }
    }

    private string _detail = "";
    public string Detail
    {
        get => _detail;
        set { _detail = value; OnPropertyChanged(); }
    }

    public string StatusLabel => Status switch
    {
        TestStatus.Pending  => "—",
        TestStatus.Running  => "…",
        TestStatus.Pass     => "PASS",
        TestStatus.Fail     => "FAIL",
        TestStatus.Skipped  => "SKIP",
        _                   => "?"
    };

    public bool IsPending => Status == TestStatus.Pending;
    public bool IsFailed  => Status == TestStatus.Fail;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum TestStatus { Pending, Running, Pass, Fail, Skipped }
