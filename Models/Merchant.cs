using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PayrixLauncher.Models;

public class Merchant : INotifyPropertyChanged
{
    private bool _isSelected;

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    private int _status;
    [JsonPropertyName("status")]
    [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleIntToNonNullableConverter))]
    public int Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPC();
            OnPC(nameof(StatusLabel));
            OnPC(nameof(StatusColor));
            OnPC(nameof(StatusBg));
            OnPC(nameof(ToggleStatusLabel));
        }
    }

    [JsonPropertyName("created")]
    public string Created { get; set; } = "";

    [JsonPropertyName("modified")]
    public string Modified { get; set; } = "";

    [JsonPropertyName("descriptor")]
    public string? Descriptor { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("address1")]
    public string? Address1 { get; set; }

    [JsonPropertyName("address2")]
    public string? Address2 { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("zip")]
    public string? Zip { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("entity")]
    public string? Entity { get; set; }

    [JsonPropertyName("dba")]
    public string? Dba { get; set; }

    [JsonPropertyName("mcc")]
    public string? Mcc { get; set; }

    [JsonPropertyName("boarded")]
    public string? Boarded { get; set; }

    [JsonPropertyName("new")]
    public int? New { get; set; }

    [JsonPropertyName("established")]
    public string? Established { get; set; }

    [JsonPropertyName("annualCCSales")]
    public long? AnnualCCSales { get; set; }

    [JsonPropertyName("avgTicket")]
    public long? AvgTicket { get; set; }

    [JsonPropertyName("autoBoarded")]
    public string? AutoBoarded { get; set; }

    [JsonPropertyName("inactive")]
    public int? Inactive { get; set; }

    [JsonPropertyName("frozen")]
    public int? Frozen { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("chargebackNotificationEmail")]
    public string? ChargebackNotificationEmail { get; set; }

    [JsonPropertyName("creator")]
    public string? Creator { get; set; }

    [JsonPropertyName("modifier")]
    public string? Modifier { get; set; }

    [JsonPropertyName("applePayActive")]
    public int? ApplePayActive { get; set; }

    [JsonPropertyName("googlePayActive")]
    public int? GooglePayActive { get; set; }

    [JsonPropertyName("kyc")]
    public MerchantKyc? Kyc { get; set; }

    // ── Enriched fields (populated after fetch) ──────────────────────────────

    private string? _entityName;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EntityName
    {
        get => _entityName;
        set { _entityName = value; OnPC(); }
    }

    // ── Display helpers ──────────────────────────────────────────────────────

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPC(); }
    }

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set { _isPinned = value; OnPC(); OnPC(nameof(PinIcon)); }
    }

    /// <summary>📌 when pinned, 📍 when not — bound to the pin toggle button content.</summary>
    public string PinIcon => _isPinned ? "📌" : "📍";

    private bool _toggleBusy;
    public bool ToggleBusy
    {
        get => _toggleBusy;
        set { _toggleBusy = value; OnPC(); OnPC(nameof(ToggleEnabled)); }
    }
    public bool ToggleEnabled => !ToggleBusy;

    private bool _linkedToCore;
    public bool LinkedToCore
    {
        get => _linkedToCore;
        set { _linkedToCore = value; OnPC(); }
    }

    private int? _memberCount;
    public int? MemberCount
    {
        get => _memberCount;
        set { _memberCount = value; OnPC(); OnPC(nameof(MemberCountDisplay)); }
    }
    public string MemberCountDisplay => _memberCount.HasValue ? _memberCount.Value.ToString() : "…";

    private bool _isMarked;
    public bool IsMarked
    {
        get => _isMarked;
        set { _isMarked = value; OnPC(); }
    }

    /// <summary>
    /// Payrix status codes: 0=Created, 1=Active, 2=Boarded/Live, 3=Inactive, 4=Suspended.
    /// Both 1 and 2 are live states that can process payments.
    /// </summary>
    public bool IsLive => Status == 1 || Status == 2;
    public string ToggleStatusLabel => IsLive ? "Deactivate" : "Activate";

    public string StatusLabel => Status switch
    {
        0 => "Created",
        1 => "Active",
        2 => "Boarded",
        3 => "Inactive",
        4 => "Suspended",
        _ => $"Status {Status}"
    };
    // Semi-transparent backgrounds (26 ≈ 15 % alpha) work in both light and dark themes
    public string StatusColor => Status switch { 1 or 2 => "#22C55E", 0 => "#6366F1", 3 => "#9CA3AF", 4 => "#EF4444", _ => "#9CA3AF" };
    public string StatusBg    => Status switch { 1 or 2 => "#2622C55E", 0 => "#266366F1", 3 => "#269CA3AF", 4 => "#26EF4444", _ => "#269CA3AF" };

    public string KycLabel => Kyc?.Status switch
    {
        1 => "Approved", 2 => "Pending", 3 => "Declined", 4 => "Review",
        null => "N/A",
        _ => "N/A"
    };
    public string KycColor => Kyc?.Status switch
    {
        1 => "#22C55E", 2 => "#F59E0B", 3 => "#EF4444", 4 => "#3B82F6", _ => "#9CA3AF"
    };
    public string KycBg => Kyc?.Status switch
    {
        1 => "#2622C55E", 2 => "#26F59E0B", 3 => "#26EF4444", 4 => "#263B82F6", _ => "#269CA3AF"
    };

    /// <summary>
    /// Best human-readable name: Descriptor (business DBA) → Name (if it differs from the Id)
    /// → truncated Id as last resort.
    /// </summary>
    public string DisplayName
    {
        get
        {
            // Descriptor = statement DBA name (best human label)
            if (!string.IsNullOrWhiteSpace(Descriptor)) return Descriptor;
            // Name = registered entity name (may equal Id in sandbox, still prefer over email)
            if (!string.IsNullOrWhiteSpace(Name)) return Name;
            // Email as last readable fallback
            if (!string.IsNullOrWhiteSpace(Email)) return Email;
            return ShortId;
        }
    }

    public string ShortId => Id.Length > 14 ? Id[..14] + "…" : Id;

    public string AddressLine => string.Join(", ",
        new[] { Address1, City, State, Zip, Country }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    public string CreatedFormatted
    {
        get
        {
            if (DateTime.TryParse(Created, out var d)) return d.ToString("MMM d, yyyy");
            return Created;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class MerchantKyc
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

// ── Payrix list response wrapper ─────────────────────────────────────────────
public class PayrixMerchantResponse
{
    [JsonPropertyName("response")]
    public PayrixMerchantPayload? Response { get; set; }
}

public class PayrixMerchantPayload
{
    [JsonPropertyName("data")]
    public List<Merchant> Data { get; set; } = [];

    [JsonPropertyName("total")]
    public int? Total { get; set; }
}
