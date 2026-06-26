using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PayrixLauncher.Models;

public class DisbursementRecord : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [JsonPropertyName("id")]          public string? Id { get; set; }
    [JsonPropertyName("created")]     public string? Created { get; set; }
    [JsonPropertyName("modified")]    public string? Modified { get; set; }
    [JsonPropertyName("creator")]     public string? Creator { get; set; }
    [JsonPropertyName("modifier")]    public string? Modifier { get; set; }
    [JsonPropertyName("entity")]      public string? Entity { get; set; }
    [JsonPropertyName("account")]     public string? Account { get; set; }
    [JsonPropertyName("payout")]      public string? Payout { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("amount")]      public long? Amount { get; set; }
    [JsonPropertyName("status")]
    [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleStringConverter))]
    public string? StatusRaw { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int? Status => int.TryParse(StatusRaw, out var v) ? v : null;
    [JsonPropertyName("processed")]   public string? Processed { get; set; }
    [JsonPropertyName("currency")]    public string? Currency { get; set; }
    [JsonPropertyName("payment")]     public string? Payment { get; set; }
    [JsonPropertyName("sameDay")]     public int? SameDay { get; set; }
    [JsonPropertyName("returnedAmount")] public long? ReturnedAmount { get; set; }
    [JsonPropertyName("statement")]   public string? Statement { get; set; }
    [JsonPropertyName("settlement")]  public string? Settlement { get; set; }
    [JsonPropertyName("disbursementEntriesStatus")] public string? DisbursementEntriesStatus { get; set; }
    [JsonPropertyName("fundingStatus")] public string? FundingStatus { get; set; }
    [JsonPropertyName("secondaryDescriptor")] public string? SecondaryDescriptor { get; set; }
    [JsonPropertyName("lastNegativeEntry")]        public string? LastNegativeEntry { get; set; }
    [JsonPropertyName("lastPositiveEntry")]        public string? LastPositiveEntry { get; set; }
    [JsonPropertyName("lastPositiveReserveEntry")] public string? LastPositiveReserveEntry { get; set; }
    [JsonPropertyName("lastNegativeReserveEntry")] public string? LastNegativeReserveEntry { get; set; }
    [JsonPropertyName("lastPositivePendingEntry")] public string? LastPositivePendingEntry { get; set; }
    [JsonPropertyName("lastNegativePendingEntry")] public string? LastNegativePendingEntry { get; set; }

    // ── Enriched (set after fetch) ────────────────────────────────────────────

    private string? _companyName;
    [JsonIgnore]
    public string? CompanyName
    {
        get => _companyName;
        set { _companyName = value; OnPropertyChanged(); }
    }

    // ── Computed helpers ─────────────────────────────────────────────────────

    private static readonly System.Globalization.CultureInfo _usd =
        System.Globalization.CultureInfo.GetCultureInfo("en-US");

    private static string FormatDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        if (DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToLocalTime().ToString("MMM d, yyyy  h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
        return raw.Length >= 10 ? raw[..10] : raw;
    }

    public string CreatedFormatted   => FormatDate(Created);
    public string ProcessedFormatted => FormatDate(Processed);

    public string AmountFormatted => Amount.HasValue
        ? (Amount.Value / 100m).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
        : "0.00";

    /// <summary>Formatted with thousand-separators, as Payrix sends in the alert field.</summary>
    public string AmountAlertFormatted => Amount.HasValue
        ? (Amount.Value / 100m).ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-US"))
        : "0.00";

    public string StatusLabel
    {
        get
        {
            // A populated Processed date means the disbursement completed — matches production behaviour.
            if (!string.IsNullOrWhiteSpace(Processed)) return "Completed";
            return Status switch
            {
                1 => "Pending",
                2 => "Scheduled",
                3 => "Completed",
                4 => "Failed",
                _ => "Unknown"
            };
        }
    }

    public string StatusColor => StatusLabel switch
    {
        "Completed" => "#22C55E",
        "Pending"   => "#F59E0B",
        "Scheduled" => "#3B82F6",
        "Failed"    => "#EF4444",
        _           => "#9CA3AF"
    };

    public string StatusBg => StatusLabel switch
    {
        "Completed" => "#2622C55E",
        "Pending"   => "#26F59E0B",
        "Scheduled" => "#263B82F6",
        "Failed"    => "#26EF4444",
        _           => "#269CA3AF"
    };

    public string ShortId => string.IsNullOrEmpty(Id) ? ""
        : Id.Length > 22 ? Id[..22] + "…" : Id;

    public string CreatedDateFormatted
    {
        get
        {
            if (string.IsNullOrEmpty(Created)) return DateTime.UtcNow.ToString("MM-dd-yyyy");
            if (DateTime.TryParse(Created, out var dt)) return dt.ToString("MM-dd-yyyy");
            return Created.Length >= 10 ? Created[..10] : Created;
        }
    }
}

public class DisbursementResponse
{
    [JsonPropertyName("response")]
    public DisbursementResponseBody? Response { get; set; }
}

public class DisbursementResponseBody
{
    [JsonPropertyName("data")]
    public List<DisbursementRecord> Data { get; set; } = [];

    [JsonPropertyName("errors")]
    public List<PayrixError> Errors { get; set; } = [];
}

// ── Disbursement entry (line item) ───────────────────────────────────────────

public class DisbursementEntry
{
    [JsonPropertyName("id")]           public string? Id          { get; set; }
    [JsonPropertyName("created")]      public string? Created     { get; set; }
    [JsonPropertyName("disbursement")] public string? Disbursement { get; set; }
    [JsonPropertyName("amount")]       public long?   Amount      { get; set; }
    [JsonPropertyName("amountUsed")]   public long?   AmountUsed  { get; set; }
    [JsonPropertyName("description")]  public string? Description { get; set; }
    [JsonPropertyName("event")]        public int?    Event       { get; set; }
    [JsonPropertyName("eventId")]      public string? EventId     { get; set; }
    [JsonPropertyName("pendingEntry")] public string? PendingEntry { get; set; }
    [JsonPropertyName("reserveEntry")] public string? ReserveEntry { get; set; }
    [JsonPropertyName("entry")]        public DisbursementEntryDetail? Entry { get; set; }

    public string AmountFormatted => Amount.HasValue
        ? (Amount.Value / 100m).ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"))
        : "$0.00";

    public string ShortEntryId => string.IsNullOrEmpty(Id) ? ""
        : Id.Length > 22 ? Id[..22] + "…" : Id;

    public string EventLabel => Event switch
    {
        1  => "Sale",
        2  => "Refund",
        3  => "Chargeback",
        4  => "Fee",
        5  => "Adjustment",
        6  => "Reserve",
        7  => "Release",
        8  => "Funding",
        _  => Event.HasValue ? $"Event {Event}" : "—"
    };

    public bool HasEventId => !string.IsNullOrEmpty(EventId);
}

public class DisbursementEntryDetail
{
    [JsonPropertyName("id")]          public string? Id          { get; set; }
    [JsonPropertyName("txn")]         public string? Txn         { get; set; }
    [JsonPropertyName("amount")]      public long?   Amount      { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("event")]       public int?    Event       { get; set; }
    [JsonPropertyName("isFee")]       public int?    IsFee       { get; set; }
    [JsonPropertyName("disbursement")] public string? Disbursement { get; set; }
    [JsonPropertyName("entity")]      public string? Entity      { get; set; }
    [JsonPropertyName("fund")]        public string? Fund        { get; set; }
    [JsonPropertyName("fee")]         public string? Fee         { get; set; }
    [JsonPropertyName("refund")]      public string? Refund      { get; set; }
    [JsonPropertyName("chargeback")]  public string? Chargeback  { get; set; }
    [JsonPropertyName("adjustment")]  public string? Adjustment  { get; set; }
    [JsonPropertyName("pending")]     public int?    Pending     { get; set; }
    [JsonPropertyName("statement")]   public string? Statement   { get; set; }
    [JsonPropertyName("settlement")]  public string? Settlement  { get; set; }
}

public class DisbursementEntryResponse
{
    [JsonPropertyName("response")]
    public DisbursementEntryResponseBody? Response { get; set; }
}

public class DisbursementEntryResponseBody
{
    [JsonPropertyName("data")]
    public List<DisbursementEntry> Data { get; set; } = [];

    [JsonPropertyName("errors")]
    public List<PayrixError> Errors { get; set; } = [];
}

// ── Entity lookup ────────────────────────────────────────────────────────────

public class EntityRecord
{
    [JsonPropertyName("id")]     public string? Id     { get; set; }
    [JsonPropertyName("login")]  public string? Login  { get; set; }
    [JsonPropertyName("custom")] public string? Custom { get; set; }
    [JsonPropertyName("name")]   public string? Name   { get; set; }

    /// <summary>
    /// Parses the Payrix custom field ("AccountID,CompanyID") and returns
    /// (accountId, companyId). Either may be null if parsing fails.
    /// </summary>
    public (string? accountId, string? companyId) ParseCustom()
    {
        if (string.IsNullOrWhiteSpace(Custom)) return (null, null);
        var parts = Custom.Split(',');
        return parts.Length >= 2 ? (parts[0].Trim(), parts[1].Trim()) : (parts[0].Trim(), null);
    }
}

public class EntityResponse
{
    [JsonPropertyName("response")]
    public EntityResponseBody? Response { get; set; }
}

public class EntityResponseBody
{
    [JsonPropertyName("data")]
    public List<EntityRecord> Data { get; set; } = [];

    [JsonPropertyName("errors")]
    public List<PayrixError> Errors { get; set; } = [];
}
