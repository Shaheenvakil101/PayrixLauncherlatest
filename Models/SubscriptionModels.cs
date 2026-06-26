using System.Text.Json.Serialization;

namespace PayrixLauncher.Models;

/// <summary>
/// Maps to BQECoreHostModel.UserSubscribePackage returned by
/// POST /BQECoreAdminPortalAPI/API/CoreHost/SubscribePackages
/// </summary>
public class UserSubscribePackage
{
    [JsonPropertyName("ID")]
    public Guid Id { get; set; }

    [JsonPropertyName("Parent_ID")]
    public Guid? ParentId { get; set; }

    [JsonPropertyName("Package_ID")]
    public Guid? PackageId { get; set; }

    [JsonPropertyName("PackageName")]
    public string? PackageName { get; set; }

    [JsonPropertyName("PlanName")]
    public string? PlanName { get; set; }

    [JsonPropertyName("PackageEnum")]
    [JsonConverter(typeof(FlexibleStringConverter))]   // API returns int (enum value), not string
    public string? PackageEnum { get; set; }

    [JsonPropertyName("PackageType")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? PackageType { get; set; }

    [JsonPropertyName("NumberOfLicense")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? NumberOfLicense { get; set; }

    [JsonPropertyName("LicenseUsed")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? LicenseUsed { get; set; }

    [JsonPropertyName("AutoRenew")]
    public bool AutoRenew { get; set; }

    [JsonPropertyName("Status")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Status { get; set; }

    [JsonPropertyName("StartsOn")]
    public string? StartsOn { get; set; }

    [JsonPropertyName("ExpiresOn")]
    public string? ExpiresOn { get; set; }

    // ── Computed display helpers ───────────────────────────────────────────

    [JsonIgnore]
    public int LicenseRemaining => (NumberOfLicense ?? 0) - (LicenseUsed ?? 0);

    [JsonIgnore]
    public string AutoRenewLabel => AutoRenew ? "✓" : "—";

    // SubscriptionStatus enum from BQECoreSharedLib:
    // Active=0, InActive=1, Pending=2, Refunded=3, Expired=4, Renewed=5, Canceled=6, LicenseAdded=7, UnPaid=8, IncompleteExpired=9
    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        0 => "Active",
        1 => "Inactive",
        2 => "Pending",
        3 => "Refunded",
        4 => "Expired",
        5 => "Renewed",
        6 => "Cancelled",
        7 => "License Added",
        8 => "Unpaid",
        9 => "Incomplete",
        _ => Status.HasValue ? $"Status {Status}" : "—"
    };

    [JsonIgnore]
    public string StatusColor => Status switch
    {
        0 or 5 => "#17A34A",   // Active / Renewed — green
        2      => "#F59E0B",   // Pending — amber
        1 or 6 => "#64748B",   // Inactive / Cancelled — gray
        3 or 4 or 8 or 9 => "#DC2625",  // Refunded / Expired / Unpaid — red
        7      => "#2c99f0",   // License Added — blue
        _      => "#64748B"
    };

    [JsonIgnore]
    public string ExpiryFormatted
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ExpiresOn)) return "—";
            if (DateTime.TryParse(ExpiresOn, out var d)) return d.ToString("dd MMM yyyy");
            return ExpiresOn;
        }
    }

    [JsonIgnore]
    public string StartFormatted
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StartsOn)) return "—";
            if (DateTime.TryParse(StartsOn, out var d)) return d.ToString("dd MMM yyyy");
            return StartsOn;
        }
    }

    [JsonIgnore]
    public bool IsExpiringSoon
    {
        get
        {
            if (!DateTime.TryParse(ExpiresOn, out var d)) return false;
            return d <= DateTime.Now.AddDays(30) && d >= DateTime.Now;
        }
    }

    [JsonIgnore]
    public bool IsExpired
    {
        get
        {
            if (!DateTime.TryParse(ExpiresOn, out var d)) return false;
            return d < DateTime.Now;
        }
    }

    // ── Icon helpers ──────────────────────────────────────────────────────────

    [JsonIgnore]
    public string PackageInitials =>
        string.IsNullOrWhiteSpace(PackageName) ? "?" : PackageName.Trim()[0].ToString().ToUpper();

    // Background and foreground colours for the icon pill, cycled by first letter
    [JsonIgnore]
    public string PackageIconBg => (PackageName?.ToUpperInvariant().FirstOrDefault() ?? 'Z') switch
    {
        'A' or 'B'                   => "#EFF6FF",
        'C' or 'D'                   => "#F0FDF4",
        'E' or 'F'                   => "#FFF7ED",
        'G' or 'H'                   => "#F5F3FF",
        'I' or 'J' or 'K' or 'L'    => "#F0FDFA",
        'M' or 'N' or 'O' or 'P'    => "#FFFBEB",
        'Q' or 'R' or 'S' or 'T'    => "#FEF2F2",
        _                            => "#F1F5F9"
    };

    [JsonIgnore]
    public string PackageIconFg => (PackageName?.ToUpperInvariant().FirstOrDefault() ?? 'Z') switch
    {
        'A' or 'B'                   => "#2563EB",
        'C' or 'D'                   => "#16A34A",
        'E' or 'F'                   => "#EA580C",
        'G' or 'H'                   => "#7C3AED",
        'I' or 'J' or 'K' or 'L'    => "#0D9488",
        'M' or 'N' or 'O' or 'P'    => "#D97706",
        'Q' or 'R' or 'S' or 'T'    => "#DC2626",
        _                            => "#64748B"
    };

    // ── Status badge — light background + coloured text ───────────────────────

    [JsonIgnore]
    public string StatusBgColor => Status switch
    {
        0 or 5 => "#F0FDF4",
        2      => "#FFFBEB",
        1 or 6 => "#F8FAFC",
        7      => "#EFF6FF",
        3 or 4 or 8 or 9 => "#FEF2F2",
        _      => "#F8FAFC"
    };

    // StatusColor (already defined above) is reused as the text colour.

    // ── Short ID for display ──────────────────────────────────────────────────

    [JsonIgnore]
    public string IdShort => Id.ToString().Length >= 4 ? Id.ToString()[..4] + "…" : Id.ToString();
}

// ── Add Subscription models ───────────────────────────────────────────────────

public class BqePackage
{
    [JsonPropertyName("ID")]   public Guid   Id          { get; set; }
    [JsonPropertyName("Name")] public string? Name        { get; set; }
    [JsonPropertyName("PackageType")] [JsonConverter(typeof(FlexibleIntConverter))]
                               public int?   PackageType { get; set; }
    [JsonPropertyName("Plans")] public List<BqePlan> Plans { get; set; } = [];
    public override string ToString() => Name ?? Id.ToString();
}

public class BqePlan
{
    [JsonPropertyName("ID")]               public Guid    Id              { get; set; }
    [JsonPropertyName("Name")]             public string? Name            { get; set; }
    [JsonPropertyName("MonthMultiplier")]  [JsonConverter(typeof(FlexibleIntConverter))]
                                           public int?    MonthMultiplier { get; set; }
    [JsonPropertyName("MonthlyPrice")]     public decimal? MonthlyPrice   { get; set; }
    [JsonIgnore] public string DisplayName =>
        $"{Name}  (×{MonthMultiplier} mo  —  ${MonthlyPrice:F2}/mo)";
    public override string ToString() => DisplayName;
}

public class BqeOrderHelper
{
    [JsonPropertyName("Packages")]  public List<BqePackage> Packages  { get; set; } = [];
    [JsonPropertyName("Region")]    public BqeRegion?       Region    { get; set; }
}

public class BqeRegion
{
    [JsonPropertyName("ID")]   public Guid   Id   { get; set; }
    [JsonPropertyName("Name")] public string? Name { get; set; }
}

/// <summary>A company user shown in the Assign Users panel.</summary>
public class AssignableUser
{
    public Guid    Id         { get; set; }   // AccountCompany.ID
    public string? Email      { get; set; }
    public string? FirstName  { get; set; }
    public string? LastName   { get; set; }
    public bool    IsAssigned { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName =>
        string.IsNullOrWhiteSpace($"{FirstName}{LastName}")
            ? (Email ?? Id.ToString())
            : $"{FirstName} {LastName}".Trim() + (Email != null ? $"  ({Email})" : "");
}

/// <summary>BQEParameters wrapper used by SubscribePackages endpoint.</summary>
public class BqeParameters
{
    [JsonPropertyName("Company_ID")]
    public Guid CompanyId { get; set; }
}

/// <summary>ActionHelper used by ChangePackageDates endpoint.</summary>
public class ChangePackageDateRequest
{
    [JsonPropertyName("ID")]
    public Guid SubscriptionId { get; set; }

    [JsonPropertyName("Company_ID")]
    public Guid CompanyId { get; set; }

    [JsonPropertyName("ExpiresOn")]
    public string ExpiresOn { get; set; } = "";

    [JsonPropertyName("AutoRenew")]
    public bool AutoRenew { get; set; }

    /// <summary>Required by ValidateCall() — throws 409 if empty.</summary>
    [JsonPropertyName("Note")]
    public string Note { get; set; } = "Expiry date updated via BQE Core ePayment Tools";
}
