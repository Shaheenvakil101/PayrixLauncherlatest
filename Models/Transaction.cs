using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PayrixLauncher.Models;

public class Transaction : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [JsonPropertyName("items")]
    public List<TransactionItem> Items { get; set; } = [];

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("created")]
    public string? Created { get; set; }

    [JsonPropertyName("modified")]
    public string? Modified { get; set; }

    [JsonPropertyName("creator")]
    public string? Creator { get; set; }

    [JsonPropertyName("modifier")]
    public string? Modifier { get; set; }

    [JsonPropertyName("ipCreated")]
    public string? IpCreated { get; set; }

    [JsonPropertyName("ipModified")]
    public string? IpModified { get; set; }

    [JsonPropertyName("merchant")]
    public string? Merchant { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("payment")]
    public string? Payment { get; set; }

    [JsonPropertyName("fortxn")]
    public string? Fortxn { get; set; }

    [JsonPropertyName("fromtxn")]
    public string? Fromtxn { get; set; }

    [JsonPropertyName("batch")]
    public string? Batch { get; set; }

    [JsonPropertyName("subscription")]
    public string? Subscription { get; set; }

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("expiration")]
    public string? Expiration { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("authDate")]
    public string? AuthDate { get; set; }

    [JsonPropertyName("authCode")]
    public string? AuthCode { get; set; }

    [JsonPropertyName("captured")]

    public string? Captured { get; set; }

    [JsonPropertyName("settled")]

    public string? Settled { get; set; }

    [JsonPropertyName("settledCurrency")]
    public string? SettledCurrency { get; set; }

    [JsonPropertyName("settledTotal")]
    public decimal? SettledTotal { get; set; }

    [JsonPropertyName("allowPartial")]
    public int AllowPartial { get; set; }

    [JsonPropertyName("order")]
    public string? Order { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("descriptor")]
    public string? Descriptor { get; set; }

    [JsonPropertyName("terminal")]
    public string? Terminal { get; set; }

    [JsonPropertyName("terminalCapability")]
    public string? TerminalCapability { get; set; }

    [JsonPropertyName("entryMode")]
    public string? EntryMode { get; set; }

    [JsonPropertyName("origin")]
    public int? Origin { get; set; }

    [JsonPropertyName("tax")]
    public decimal? Tax { get; set; }

    [JsonPropertyName("total")]
    public decimal? Total { get; set; }

    [JsonPropertyName("cashback")]
    public decimal? Cashback { get; set; }

    [JsonPropertyName("authorization")]
    public string? Authorization { get; set; }

    private decimal? _approved;
    [JsonPropertyName("approved")]
    public decimal? Approved
    {
        get => _approved;
        set
        {
            _approved = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ApprovedDollars));
            OnPropertyChanged(nameof(ApprovedFormatted));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(HasDiscrepancy));
            OnPropertyChanged(nameof(DiscrepancyLabel));
            OnPropertyChanged(nameof(DiscrepancyDetails));
            OnPropertyChanged(nameof(DiscrepancySummary));
        }
    }

    [JsonPropertyName("cvv")]
    public int? Cvv { get; set; }

    [JsonPropertyName("swiped")]
    public int? Swiped { get; set; }

    [JsonPropertyName("emv")]
    public int? Emv { get; set; }

    [JsonPropertyName("signature")]
    public int? Signature { get; set; }

    [JsonPropertyName("unattended")]
    public string? Unattended { get; set; }

    [JsonPropertyName("clientIp")]
    public string? ClientIp { get; set; }

    [JsonPropertyName("first")]
    public string? First { get; set; }

    [JsonPropertyName("middle")]
    public string? Middle { get; set; }

    [JsonPropertyName("last")]
    public string? Last { get; set; }

    [JsonPropertyName("company")]
    public string? Company { get; set; }

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

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("status")]
    public int? Status { get; set; }

    private int? _refunded;
    [JsonPropertyName("refunded")]
    public int? Refunded
    {
        get => _refunded;
        set
        {
            _refunded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(HasDiscrepancy));
            OnPropertyChanged(nameof(DiscrepancyLabel));
            OnPropertyChanged(nameof(DiscrepancyDetails));
            OnPropertyChanged(nameof(DiscrepancySummary));
        }
    }

    [JsonPropertyName("reserved")]
    public int? Reserved { get; set; }

    [JsonPropertyName("misused")]

    public string? Misused { get; set; }

    [JsonPropertyName("imported")]
    public int? Imported { get; set; }

    [JsonPropertyName("inactive")]
    public int? Inactive { get; set; }

    [JsonPropertyName("frozen")]
    public int? Frozen { get; set; }

    [JsonPropertyName("discount")]
    public decimal? Discount { get; set; }

    [JsonPropertyName("shipping")]
    public decimal? Shipping { get; set; }

    [JsonPropertyName("duty")]
    public decimal? Duty { get; set; }

    [JsonPropertyName("pin")]
    public int? Pin { get; set; }

    [JsonPropertyName("traceNumber")]
    public string? TraceNumber { get; set; }

    [JsonPropertyName("cvvStatus")]
    public string? CvvStatus { get; set; }

    [JsonPropertyName("unauthReason")]
    public string? UnauthReason { get; set; }

    [JsonPropertyName("fee")]
    public decimal? Fee { get; set; }

    [JsonPropertyName("fundingCurrency")]
    public string? FundingCurrency { get; set; }

    [JsonPropertyName("authentication")]
    public string? Authentication { get; set; }

    [JsonPropertyName("authenticationId")]
    public string? AuthenticationId { get; set; }

    [JsonPropertyName("cofType")]
    public string? CofType { get; set; }

    [JsonPropertyName("copyReason")]
    public string? CopyReason { get; set; }

    [JsonPropertyName("originalApproved")]
    public decimal? OriginalApproved { get; set; }

    [JsonPropertyName("currencyConversion")]
    public string? CurrencyConversion { get; set; }

    [JsonPropertyName("serviceCode")]
    public string? ServiceCode { get; set; }

    [JsonPropertyName("authTokenCustomer")]
    public string? AuthTokenCustomer { get; set; }

    [JsonPropertyName("debtRepayment")]
    public int? DebtRepayment { get; set; }

    [JsonPropertyName("statement")]
    public string? Statement { get; set; }

    [JsonPropertyName("convenienceFee")]
    public decimal? ConvenienceFee { get; set; }

    [JsonPropertyName("surcharge")]
    public decimal? Surcharge { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("funded")]

    public string? Funded { get; set; }

    [JsonPropertyName("fundingEnabled")]
    public int? FundingEnabled { get; set; }

    [JsonPropertyName("requestSequence")]
    public int? RequestSequence { get; set; }

    [JsonPropertyName("processedSequence")]
    public int? ProcessedSequence { get; set; }

    [JsonPropertyName("mobile")]
    public string? Mobile { get; set; }

    [JsonPropertyName("pinEntryCapability")]
    public string? PinEntryCapability { get; set; }

    private string? _returned;
    [JsonPropertyName("returned")]
    public string? Returned
    {
        get => _returned;
        set
        {
            _returned = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(HasDiscrepancy));
            OnPropertyChanged(nameof(DiscrepancyLabel));
            OnPropertyChanged(nameof(DiscrepancyDetails));
            OnPropertyChanged(nameof(DiscrepancySummary));
        }
    }

    [JsonPropertyName("txnsession")]
    public string? Txnsession { get; set; }

    [JsonPropertyName("networkTokenIndicator")]
    public int? NetworkTokenIndicator { get; set; }

    [JsonPropertyName("softPosDeviceTypeIndicator")]
    public string? SoftPosDeviceTypeIndicator { get; set; }

    [JsonPropertyName("softPosId")]
    public string? SoftPosId { get; set; }

    [JsonPropertyName("tip")]
    public decimal? Tip { get; set; }

    [JsonPropertyName("pinlessDebitConversion")]
    public string? PinlessDebitConversion { get; set; }

    [JsonPropertyName("submittedMethod")]
    public string? SubmittedMethod { get; set; }

    [JsonPropertyName("processedMethod")]
    public string? ProcessedMethod { get; set; }

    [JsonPropertyName("paymentType")]
    public string? PaymentType { get; set; }

    [JsonPropertyName("gateway")]
    public string? Gateway { get; set; }

    /// <summary>
    /// Human-readable gateway name. Uses the raw Gateway field when present;
    /// falls back to deriving from PaymentType/Type when the API omits it.
    /// </summary>
    [JsonIgnore]
    public string GatewayDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Gateway)) return Gateway;
            // Derive from payment type
            var pt = PaymentType?.ToLowerInvariant() ?? "";
            if (pt == "ach" || pt == "echeck" || Type == 7 || Type == 8) return "ACH / eCheck";
            if (pt == "creditcard" || pt == "debitcard") return "Card";
            // Fall back to platform
            if (!string.IsNullOrWhiteSpace(Platform)) return Platform;
            return "—";
        }
    }

    public string PaymentTypeLabel
    {
        get
        {
            var fromField = PaymentType?.ToLowerInvariant() switch
            {
                "creditcard"  => "Credit Card",
                "debitcard"   => "Debit Card",
                "ach"         => "ACH",
                "cash"        => "Cash",
                "check"       => "Check",
                "echeck"      => "eCheck",
                null or ""    => null,
                var s         => s
            };
            if (!string.IsNullOrEmpty(fromField)) return fromField;

            return Type switch
            {
                7 or 8        => "eCheck",
                1 or 2 or 3   => "Credit Card",
                4 or 5 or 6   => "Credit Card",
                _             => ""
            };
        }
    }

    // Populated after fetch — not part of the JSON response
    [JsonIgnore]
    private string? _disbursementId;
    public string? DisbursementId
    {
        get => _disbursementId;
        set { _disbursementId = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    private string? _coreCompanyName;
    public string? CoreCompanyName
    {
        get => _coreCompanyName;
        set { _coreCompanyName = value; OnPropertyChanged(); OnPropertyChanged(nameof(MerchantDisplayName)); }
    }

    [JsonIgnore]
    private string? _entityId;
    public string? EntityId
    {
        get => _entityId;
        set { _entityId = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    private string? _entityName;
    public string? EntityName
    {
        get => _entityName;
        set { _entityName = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    private string? _merchantName;
    public string? MerchantName
    {
        get => _merchantName;
        set { _merchantName = value; OnPropertyChanged(); }
    }

    /// <summary>Best available merchant name: BQE company name → statement descriptor → truncated Payrix merchant ID.</summary>
    [JsonIgnore]
    public string MerchantDisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CoreCompanyName)) return CoreCompanyName;
            if (!string.IsNullOrWhiteSpace(Descriptor))      return Descriptor;
            var id = Merchant ?? "";
            return id.Length > 14 ? id[..14] + "…" : (id.Length > 0 ? id : "—");
        }
    }

    [JsonIgnore]
    private PaymentInfo? _paymentDetails;
    public PaymentInfo? PaymentDetails
    {
        get => _paymentDetails;
        set
        {
            _paymentDetails = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PaymentMethodDisplay));
            OnPropertyChanged(nameof(PaymentTypeLabel));
        }
    }

    public string PaymentMethodDisplay
    {
        get
        {
            var fromDetails = PaymentDetails?.Display ?? "";
            if (!string.IsNullOrEmpty(fromDetails)) return fromDetails;

            return Type switch
            {
                7 or 8 => "ACH / eCheck",
                _      => ""
            };
        }
    }

    // Computed helpers
    public string TypeLabel => Type switch
    {
        1 => "Sale",
        2 => "Authorize",
        3 => "Capture",
        4 => "Refund",
        5 => "Void",
        6 => "Credit",
        7 => "eCheck Sale",
        8 => "eCheck Return",
        _ => $"Type {Type}"
    };

    /// <summary>Chip label for the TYPE column: "ACH" for eCheck types, "Card" for card types.</summary>
    public string TypeChipLabel => (Type == 7 || Type == 8) ? "ACH" : "Card";

    public bool IsAch => Type == 7 || Type == 8;

    /// <summary>
    /// Full transaction status, derived from Type + Status + Returned + Refunded fields.
    /// Priority: explicit reversal signals (returned date, refunded flag, type) take precedence
    /// over the numeric Status code so the grid always reflects the true final state.
    /// </summary>
    public string StatusLabel
    {
        get
        {
            bool isReturned = !string.IsNullOrWhiteSpace(Returned);
            bool isRefunded = Refunded.HasValue && Refunded.Value > 0;
            int  status     = Status ?? 0;

            // --- Type-driven labels (most specific) ---
            if (Type == 4) return isReturned ? "Refund Returned" : "Refunded";
            if (Type == 5) return "Voided";
            if (Type == 6) return status == 2 ? "Disbursement Failed" : "Disbursed";

            if (Type == 7) // ACH Sale
            {
                if (isReturned) return "ACH Returned";
                if (isRefunded) return "ACH Refunded";
                if (status == 1) return "ACH Approved";
                if (status == 2) return "ACH Declined";
                if (status == 3) return "ACH Pending";   // submitted to bank, in transit
                if (status == 4) return "ACH Settled";
                if (status == 5) return "ACH Returned";
                return status > 0 ? $"ACH – Status {status}" : "ACH Pending";
            }

            if (Type == 8) return "ACH Return";

            // --- Returned / Refunded flags override status code for CC Sales ---
            if (isReturned && isRefunded) return "Returned & Refunded";
            if (isReturned)               return "Returned";
            if (isRefunded)               return "Refunded";

            // --- Standard CC status codes ---
            if (status == 1) return "Approved";
            if (status == 2) return "Declined";
            if (status == 3) return "Captured";
            if (status == 4) return "Settled";
            if (status == 5) return "Held";
            return status > 0 ? $"Status {status}" : "Pending";
        }
    }

    /// <summary>Hex colour for the status badge — matches webapp badge colors.</summary>
    public string StatusColor
    {
        get
        {
            var lbl = StatusLabel;
            // Green — approved / settled / disbursed
            if (lbl == "Approved"   || lbl == "ACH Approved" ||
                lbl == "Settled"    || lbl == "ACH Settled"  || lbl == "Disbursed")
                return "#16A34A";
            // Cyan — captured / processing
            if (lbl == "Captured")
                return "#0891B2";
            // Amber — pending / in-transit
            if (lbl == "Pending" || lbl == "ACH Pending")
                return "#D97706";
            // Purple — refunded / held
            if (lbl == "Refunded"         || lbl == "ACH Refunded" ||
                lbl == "Refund Returned"  || lbl == "Held")
                return "#7C3AED";
            // Gray — voided
            if (lbl == "Voided")
                return "#6B7280";
            // Red — failures / returns / declines
            if (lbl == "Returned"          || lbl == "ACH Returned"     ||
                lbl == "ACH Return"         || lbl == "Returned & Refunded" ||
                lbl == "Declined"           || lbl == "ACH Declined"    ||
                lbl == "Disbursement Failed")
                return "#DC2626";
            // Gray — unknown
            return "#64748B";
        }
    }

    /// <summary>Extracts the invoice number from the "Invoice Number: XXXX" Order field.</summary>
    public string InvoiceNoFromOrder
    {
        get
        {
            var order = Order ?? "";
            var idx = order.IndexOf("Invoice Number:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return order[(idx + "Invoice Number:".Length)..].Trim();
            // fallback: strip "Invoice Number:[XXXX]" style
            idx = order.IndexOf('[');
            var end = order.IndexOf(']');
            if (idx >= 0 && end > idx)
                return order[(idx + 1)..end].Trim();
            return order.Trim();
        }
    }
    public string CustomerName => $"{First} {Last}".Trim();
    public decimal TotalDollars    => (Total    ?? 0) / 100;
    public decimal ApprovedDollars => (Approved ?? 0) / 100;
    public decimal TaxDollars      => (Tax      ?? 0) / 100;

    private static readonly System.Globalization.CultureInfo _usd =
        System.Globalization.CultureInfo.GetCultureInfo("en-US");
    public string TotalFormatted    => TotalDollars.ToString("C2", _usd);
    public string ApprovedFormatted => ApprovedDollars.ToString("C2", _usd);
    public string TaxFormatted      => TaxDollars.ToString("C2", _usd);

    public string CreatedDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Created)) return "—";
            if (DateTime.TryParse(Created, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToLocalTime().ToString("MMM d, yyyy  h:mm tt", _usd);
            return Created;
        }
    }

    // ── Discrepancy detection (Core vs Payrix) ────────────────────────────────

    public bool HasDiscrepancy => DiscrepancyDetails.Count > 0;

    public string DiscrepancyLabel => HasDiscrepancy ? "⚠ Mismatch" : "✓ OK";

    public List<string> DiscrepancyDetails
    {
        get
        {
            var issues = new List<string>();
            var fmt    = System.Globalization.CultureInfo.GetCultureInfo("en-US");

            string Fmt(decimal v) => v.ToString("C2", fmt);

            // Line items total doesn't match the transaction header amount.
            // Only meaningful when items have been fetched — and only for active sale/capture types.
            // Skip if the transaction was subsequently returned or refunded (Payrix zeroes approved on the original).
            if (Items.Count > 0 && (Type == 1 || Type == 3 || Type == 7))
            {
                var itemsSum  = Items.Sum(i => i.Total ?? 0);
                var headerAmt = Approved ?? Total ?? 0;
                if (itemsSum != headerAmt)
                {
                    var diff = Math.Abs(itemsSum - headerAmt) / 100;
                    issues.Add(
                        $"Line items sum ({Fmt(itemsSum / 100)}) ≠ Transaction approved " +
                        $"({Fmt(headerAmt / 100)}) — difference {Fmt(diff)}");
                }
            }

            // Declined but processor returned a non-zero approved amount — genuine concern.
            if (Status == 2 && (Approved ?? 0) > 0)
                issues.Add($"Declined but Approved = {Fmt(ApprovedDollars)} — possible double-charge risk");


            return issues;
        }
    }

    public string DiscrepancySummary => HasDiscrepancy
        ? string.Join("\n", DiscrepancyDetails.Select((r, i) => $"• {r}"))
        : "No discrepancies";
}

public class PayrixResponse
{
    [JsonPropertyName("response")]
    public PayrixResponseBody? Response { get; set; }
}

public class PayrixResponseBody
{
    [JsonPropertyName("data")]
    public List<Transaction> Data { get; set; } = [];

    [JsonPropertyName("errors")]
    public List<PayrixError> Errors { get; set; } = [];

    /// <summary>Server-reported total record count — use this to drive pagination instead of
    /// relying solely on page size to detect the last page.</summary>
    [JsonPropertyName("total")]
    public int? Total { get; set; }
}

public class PayrixError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    /// <summary>Which field / resource the error refers to (e.g. "token", "payment", "merchant").</summary>
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    /// <summary>Error severity: 1=warning, 2=error.</summary>
    [JsonPropertyName("severity")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Severity { get; set; }

    /// <summary>Machine-readable error code string (e.g. "required_field", "no_such_record").</summary>
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    /// <summary>Human-readable summary: "[token] no_such_record — The referenced resource does not exist (code 15)"</summary>
    public string Summary
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(Field))     sb.Append($"[{Field}] ");
            if (!string.IsNullOrEmpty(ErrorCode)) sb.Append($"{ErrorCode} — ");
            sb.Append(!string.IsNullOrEmpty(Msg) ? Msg : "(unknown error)");
            if (Code != 0)                         sb.Append($" (code {Code})");
            return sb.ToString();
        }
    }
}

public class PayrixItemsResponse
{
    [JsonPropertyName("response")]
    public PayrixItemsResponseBody? Response { get; set; }
}

public class PayrixItemsResponseBody
{
    [JsonPropertyName("data")]
    public List<TransactionItem> Data { get; set; } = [];

    [JsonPropertyName("errors")]
    public List<PayrixError> Errors { get; set; } = [];
}
