using System.Text.Json.Serialization;

namespace PayrixLauncher.Models;

public class PaymentInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("number")]
    public string? Number { get; set; }

    [JsonPropertyName("expiration")]
    public string? Expiration { get; set; }

    [JsonPropertyName("type")]
    public int? Type { get; set; }

    [JsonPropertyName("method")]
    public int? Method { get; set; }

    [JsonPropertyName("bin")]
    public string? Bin { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    // Last 4 digits from masked number (e.g. "4111xxxxxxxx1111" → "1111")
    public string Last4 => Number?.Length >= 4 ? Number[^4..] : (Number ?? "");

    // Formatted expiry MM/YY from MMYY string
    public string ExpiryFormatted => Expiration?.Length == 4
        ? $"{Expiration[..2]}/{Expiration[2..]}"
        : (Expiration ?? "");

    public string CardBrand => Type switch
    {
        1 => "Visa",
        2 => "Mastercard",
        3 => "Amex",
        4 => "Discover",
        5 => "Diners",
        6 => "JCB",
        _ => "Card"
    };

    public string MethodLabel => Method switch
    {
        1 => "Card",
        2 => "ACH",
        3 => "Cash",
        _ => ""
    };

    public string Display => string.IsNullOrEmpty(Last4)
        ? MethodLabel
        : $"{CardBrand} ****{Last4}";
}

public class PayrixPaymentResponse
{
    [JsonPropertyName("response")]
    public PayrixPaymentResponseBody? Response { get; set; }
}

public class PayrixPaymentResponseBody
{
    [JsonPropertyName("data")]
    public List<PaymentInfo> Data { get; set; } = [];

    [JsonPropertyName("errors")]
    public List<PayrixError> Errors { get; set; } = [];
}

