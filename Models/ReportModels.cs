using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PayrixLauncher.Models;

// ── Aggregate report row (By Type / By Status / Summary) ──────────────────────
public class ReportRow
{
    private static readonly CultureInfo _usd = CultureInfo.GetCultureInfo("en-US");

    public string  Label        { get; set; } = "";
    public int     Count        { get; set; }
    public decimal TotalAmount  { get; set; }
    public decimal AvgAmount    { get; set; }
    public string  Share        { get; set; } = "—";

    // Badge colours (bound directly as strings; WPF type-converts them)
    public string Color   { get; set; } = "#6B7280";
    public string BgColor { get; set; } = "#1A1A1A";

    // Display helpers
    public string CountStr       => Count.ToString("N0", _usd);
    public string TotalFormatted => TotalAmount.ToString("C2", _usd);
    public string AvgFormatted   => AvgAmount.ToString("C2", _usd);
}

// ── Individual failed-transaction row ─────────────────────────────────────────
public class FailedTxnRow
{
    public string Date         { get; set; } = "";
    public string ShortId      { get; set; } = "";
    public string MerchantName { get; set; } = "";
    public string Customer     { get; set; } = "";
    public string TxnType      { get; set; } = "";
    public string Amount       { get; set; } = "";
    public string Status       { get; set; } = "";
    public string StatusColor  { get; set; } = "#EF4444";
    public string StatusBg     { get; set; } = "#26EF4444";
}

// ── Accounts tab — one row per merchant account ───────────────────────────────
public class AccountMerchantRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private static readonly CultureInfo _usd = CultureInfo.GetCultureInfo("en-US");

    public string MerchantId  { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ShortId     => MerchantId.Length > 14 ? MerchantId[..14] + "…" : MerchantId;

    /// <summary>Always pinned to row 0 regardless of sort order.</summary>
    public bool IsPinned { get; set; }

    // ── Status (mutable so toggling updates the badge live) ──────────────────
    private int _statusCode;
    public int StatusCode
    {
        get => _statusCode;
        set
        {
            _statusCode = value;
            OnPC();
            OnPC(nameof(StatusLabel));
            OnPC(nameof(StatusColor));
            OnPC(nameof(StatusBg));
            OnPC(nameof(ToggleStatusLabel));
            OnPC(nameof(ToggleBusy));
        }
    }
    // Payrix status codes (sandbox + production):
    //   0 = Created/Processing   1 = Submitted/Pending
    //   2 = Active (Boarded)     3 = Inactive   4 = Suspended
    public string StatusLabel     => StatusCode switch { 2 => "Active", 3 => "Inactive", 4 => "Suspended", 1 => "Submitted", 0 => "Created", _ => $"Status {StatusCode}" };
    public string StatusColor     => StatusCode switch { 2 => "#22C55E", 1 => "#F59E0B", 0 => "#6366F1", 3 => "#9CA3AF", 4 => "#EF4444", _ => "#9CA3AF" };
    public string StatusBg        => StatusCode switch { 2 => "#2622C55E", 1 => "#26F59E0B", 0 => "#266366F1", 3 => "#269CA3AF", 4 => "#26EF4444", _ => "#269CA3AF" };

    /// <summary>Label shown on the toggle button ("Deactivate" when Active/Boarded, "Activate" otherwise).</summary>
    public string ToggleStatusLabel => StatusCode == 2 ? "Deactivate" : "Activate";

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

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPC(); }
    }

    // ── KYC ──────────────────────────────────────────────────────────────────
    public int    KycCode  { get; set; }
    public string KycLabel => KycCode switch { 1 => "Approved", 2 => "Pending", 3 => "Declined", 4 => "Review", _ => "N/A" };
    public string KycColor => KycCode switch { 1 => "#22C55E", 2 => "#F59E0B", 3 => "#EF4444", 4 => "#3B82F6", _ => "#9CA3AF" };
    public string KycBg    => KycCode switch { 1 => "#2622C55E", 2 => "#26F59E0B", 3 => "#26EF4444", 4 => "#263B82F6", _ => "#269CA3AF" };

    // ── Transaction stats ─────────────────────────────────────────────────────
    public int     TxnCount  { get; set; }
    public decimal VolumeRaw { get; set; }
    public string  Volume    => VolumeRaw.ToString("C2", _usd);

    // ── Other ─────────────────────────────────────────────────────────────────
    public string Currency         { get; set; } = "";
    public string CreatedFormatted { get; set; } = "";
    public string CreatedRaw       { get; set; } = ""; // for sort
}
