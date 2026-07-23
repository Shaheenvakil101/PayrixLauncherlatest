using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PayrixLauncher.Models;

namespace PayrixLauncher.Services;

public static class WebhookTestService
{
    public const string StdCustom = "5F523389-1A90-4603-8F26-1D996FCEA6E5,1C6201C3-2A24-433D-A0F9-9AC05DFB924B";

    public static readonly string LocalEndpoint      = "http://localhost/BQECoreHostApi/api/paymentservice/PayrixEventListner";
    public static readonly string StagingEndpoint    = "https://staging-hostapi.bqecore-np.com/hostapi/api/paymentservice/PayrixEventListner";
    public static readonly string SprintEndpoint     = "https://sprint-hostapi.bqecore-np.com/hostapi/api/paymentservice/PayrixEventListner";
    public static readonly string ProductionEndpoint = "https://bc-iad01-web01-hostapilb.bqecore.com/hostapi/api/paymentservice/PayrixEventListner";

    // ── Payload builder ───────────────────────────────────────────────────────

    private static string BuildPayload(
        string subject,
        string entityId  = "ent_test_001",
        string merchantId = "mer_test_001",
        string dataCustom = StdCustom,
        string alertCustom = StdCustom) => $$"""
        {
          "response": {
            "alert": {
              "subject": "{{subject}}",
              "paymentType": "",
              "entityCustom": "{{alertCustom}}"
            },
            "data": [{
              "id": "{{merchantId}}",
              "entity": "{{entityId}}",
              "custom": "{{dataCustom}}",
              "statusReason": null
            }]
          }
        }
        """;

    /// <summary>
    /// Builds the full ACH/eCheck "funded" webhook payload.
    /// Payrix sends this when an eCheck sale has settled/funded.
    /// Use a unique <paramref name="txnId"/> per test run to bypass the idempotency guard.
    /// </summary>
    public static string BuildAchFundedPayload(
        string txnId     = "t1_txn_69f47e89b6fa7b757b18869",
        string txnAmount = "898.00",
        string invoiceNo = "1029") => $$"""
        {
          "response": {
            "alert": {
              "subject": "Your eCheck sale has been funded",
              "paymentType": "Checking",
              "paymentNumber": "{{txnAmount}}",
              "paymentRouting": "0300",
              "txnId": "{{txnId}}",
              "txnAmount": "{{txnAmount}}",
              "txnSettled": "01-07-2025",
              "txnSettledTotal": "{{txnAmount}}",
              "txnCardHolder": "medalist capital, inc",
              "txnCreated": "01-07-2025",
              "txnStatus": "Settled",
              "merchantName": "Silver Studio Architects"
            },
            "data": [{
              "id": "{{txnId}}",
              "created": "2025-09-24 05:46:22.0378",
              "modified": "2025-09-24 05:46:29.3044",
              "creator": "p1_log_628e7fc19ea97423329ca3e",
              "modifier": "000000000000001",
              "ipCreated": "3.215.169.147",
              "ipModified": "130.41.52.112",
              "merchant": "t1_mer_674591183a6074fa70e5949",
              "token": null,
              "payment": "p1_pmt_670d22842b3fb8703fbf68b",
              "fortxn": null,
              "fromtxn": null,
              "batch": "t1_bth_69f47e89d5260842c29fa57",
              "subscription": null,
              "type": 7,
              "expiration": null,
              "currency": "USD",
              "platform": "VANTIV",
              "authDate": null,
              "authCode": null,
              "captured": "01-07-2025 19:45:36",
              "settled": 20250321,
              "settledCurrency": "USD",
              "settledTotal": 89800,
              "allowPartial": 0,
              "order": "Invoice Number: {{invoiceNo}}",
              "description": "{\"Invoice_ID\":\"2pz35RVPO4GfSVyc1C1ATdF20hdP1ZlxTlQ+VhxzhcYWvlur8M2tWLF1sm9iyxVD\",\"Account_ID\":\"MzIKSnuSYYKZxyb3lVtGbwwAQnk79QyYPDRrMijTivFXs9JLxpfLQihINgCPSvmO\",\"Company_ID\":\"ZIheBihs6dCOEW16qoRoZbPJ/5Bg6zCQymn3ZhQGaFFFEokcXs6bdnW145hYZdrR\",\"Invoice_No\":\"Payment on Invoice:[{{invoiceNo}}]\",\"Service_ID\":\"Iz5JYBclDHbkEKUVqeVoF/arueJFXVQaq37u0y1ZqPwOSCH607SH0R+RE7YgBb8R\",\"GateWay\":\"6\",\"PaymentType\":\"1\",\"StatementInfo\":null,\"TransactionID\":\"null\"}",
              "descriptor": "CHECK STRIPE ACCOUNT",
              "terminal": null,
              "status": "4",
              "total": 89800,
              "approved": 89800,
              "first": "medalist capital, inc",
              "funded": 20250107,
              "fundingEnabled": 1,
              "origin": 2
            }]
          }
        }
        """;

    /// <summary>
    /// Builds a realistic ACH funded webhook payload from a real fetched transaction.
    /// Use this when you have a live type=7 (eCheck Sale) transaction and want to
    /// re-fire the "funded" event against a local / staging endpoint.
    /// </summary>
    public static string BuildAchFundedPayloadFromTransaction(Transaction txn)
    {
        var txnId      = txn.Id ?? "t1_txn_unknown";
        var totalCents = (long)(txn.Total ?? txn.Approved ?? 0);
        var amountStr  = (totalCents / 100m).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        var invoiceNo  = txn.InvoiceNoFromOrder;
        var merchant   = txn.Merchant ?? "t1_mer_unknown";
        var batch      = txn.Batch    ?? "t1_bth_unknown";
        var payment    = txn.Payment  ?? "p1_pmt_unknown";
        var cardHolder = txn.First ?? "Unknown";
        var created    = txn.Created  ?? "2025-01-01 00:00:00.0000";
        var modified   = txn.Modified ?? created;
        var origin     = txn.Origin?.ToString() ?? "2";

        // funded / settled — fall back to captured date or created date when not yet settled
        var funded = !string.IsNullOrEmpty(txn.Funded)   ? txn.Funded
                   : !string.IsNullOrEmpty(txn.Captured) ? txn.Captured[..10]   // "YYYY-MM-DD HH:mm:ss" → take date part
                   : created[..10];
        // funded as YYYYMMDD int for JSON
        var fundedInt = int.TryParse(funded.Replace("-", "").Replace("/", "")[..8], out var fd) ? fd : 20250101;

        // settled — fall back to funded numeric value
        var settledInt = int.TryParse(txn.Settled?.Replace("-", ""), out var sd)
                         ? sd : fundedInt;

        var desc     = (txn.Description ?? "{}").Replace("\\", "\\\\").Replace("\"", "\\\"");
        var orderVal = (txn.Order ?? $"Invoice Number: {invoiceNo}").Replace("\"", "\\\"");

        return $$"""
        {
          "response": {
            "alert": {
              "subject": "Your eCheck sale has been funded",
              "paymentType": "Checking",
              "paymentNumber": "{{amountStr}}",
              "paymentRouting": "0300",
              "txnId": "{{txnId}}",
              "txnAmount": "{{amountStr}}",
              "txnSettled": "{{funded}}",
              "txnSettledTotal": "{{amountStr}}",
              "txnCardHolder": "{{cardHolder}}",
              "txnCreated": "{{created}}",
              "txnStatus": "Settled",
              "merchantName": "BQE Core"
            },
            "data": [{
              "id": "{{txnId}}",
              "created": "{{created}}",
              "modified": "{{modified}}",
              "merchant": "{{merchant}}",
              "payment": "{{payment}}",
              "batch": "{{batch}}",
              "type": 7,
              "currency": "USD",
              "platform": "VANTIV",
              "captured": "{{created}}",
              "settled": {{settledInt}},
              "settledCurrency": "USD",
              "settledTotal": {{totalCents}},
              "allowPartial": 0,
              "order": "{{orderVal}}",
              "description": "{{desc}}",
              "descriptor": "CHECK STRIPE ACCOUNT",
              "status": "4",
              "total": {{totalCents}},
              "approved": {{totalCents}},
              "first": "{{cardHolder}}",
              "funded": {{fundedInt}},
              "fundingEnabled": 1,
              "origin": {{origin}},
              "currency": "USD"
            }]
          }
        }
        """;
    }

    // ── eCheck / ACH Return payload ──────────────────────────────────────────
    // Real Payrix structure (confirmed from live webhook):
    //   alert.subject  = "Your transaction has been returned"
    //   alert.txnStatus = "Returned"
    //   alert.returnDescription = reason string from bank
    //   data[0].type   = 7  (eCheck Sale — same type as funded; status distinguishes)
    //   data[0].status = "5" (Returned)
    //   data[0].fortxn = null
    //   data[0].returned = "YYYYMMDD"
    //   data[0].settled / settledTotal / settledCurrency are populated

    public static string BuildECheckReturnPayload(
        string txnId     = "t1_txn_67dd026badc4395bd51b215",
        string txnAmount = "67.87",
        string invoiceNo = "1102") => $$"""
        {
          "response": {
            "alert": {
              "subject": "Your transaction has been returned",
              "txnId": "{{txnId}}",
              "txnAmount": "{{txnAmount}}",
              "txnCardHolder": "Shaheen Vakil",
              "txnCreated": "21-03-2025",
              "txnStatus": "Returned",
              "merchantName": "CRAFT ENGINEERING STUDIO PLLC",
              "orderNumber": "Invoice Number: {{invoiceNo}}",
              "paymentType": "Checking",
              "paymentNumber": "6004",
              "paymentRouting": "0021",
              "returnDescription": "Please correct the account information before resubmitting the payment"
            },
            "data": [{
              "id": "{{txnId}}",
              "created": "2025-03-21 02:08:43.7196",
              "modified": "2025-03-21 02:08:45.5232",
              "creator": "p1_log_628e7fc19ea97423329ca3e",
              "modifier": "000000000000001",
              "ipCreated": "3.215.169.147",
              "ipModified": "3.215.169.147",
              "merchant": "p1_mer_642201ebf35335819c4a564",
              "token": null,
              "payment": "t1_pmt_67dbf5735eca55d63d4eff9",
              "fortxn": null,
              "fromtxn": null,
              "batch": "t1_bth_67dd026bc324c0230f26970",
              "subscription": null,
              "type": 7,
              "expiration": null,
              "currency": "USD",
              "platform": "VANTIV",
              "authDate": null,
              "authCode": null,
              "captured": "2025-03-21 19:51:16",
              "settled": 20250321,
              "settledCurrency": "USD",
              "settledTotal": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "allowPartial": 0,
              "order": "Invoice Number: {{invoiceNo}}",
              "description": null,
              "descriptor": "TEST EPAYMENT WEBHOOKS",
              "terminal": null,
              "terminalCapability": null,
              "entryMode": null,
              "origin": 2,
              "tax": null,
              "total": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "cashback": null,
              "authorization": null,
              "approved": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "cvv": 0,
              "swiped": 0,
              "emv": 0,
              "signature": 0,
              "unattended": null,
              "clientIp": null,
              "first": "Shaheen",
              "middle": null,
              "last": "Vakil",
              "company": null,
              "email": null,
              "address1": null,
              "address2": null,
              "city": null,
              "state": null,
              "zip": null,
              "country": null,
              "phone": null,
              "status": "5",
              "refunded": 0,
              "reserved": 0,
              "misused": null,
              "imported": 0,
              "inactive": 0,
              "frozen": 0,
              "discount": 0,
              "shipping": 0,
              "duty": 0,
              "pin": 0,
              "traceNumber": null,
              "cvvStatus": null,
              "unauthReason": null,
              "fee": null,
              "fundingCurrency": "USD",
              "authentication": null,
              "authenticationId": null,
              "cofType": null,
              "copyReason": null,
              "originalApproved": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "currencyConversion": null,
              "serviceCode": null,
              "authTokenCustomer": null,
              "debtRepayment": 0,
              "statement": null,
              "convenienceFee": 0,
              "surcharge": null,
              "channel": null,
              "funded": 20250311,
              "fundingEnabled": 1,
              "requestSequence": 1,
              "processedSequence": 1,
              "mobile": null,
              "pinEntryCapability": null,
              "returned": "20250321",
              "txnsession": null,
              "networkTokenIndicator": 0,
              "softPosDeviceTypeIndicator": null,
              "softPosId": null,
              "tip": null
            }]
          }
        }
        """;

    public static string BuildECheckReturnPayloadFromTransaction(Transaction txn)
    {
        var txnId      = txn.Id       ?? "t1_txn_unknown";
        var totalCents = (long)(txn.Total ?? txn.Approved ?? 0);
        var amountStr  = (totalCents / 100m).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        var invoiceNo  = txn.InvoiceNoFromOrder;
        var merchant   = txn.Merchant ?? "p1_mer_unknown";
        var batch      = txn.Batch    ?? "t1_bth_unknown";
        var payment    = txn.Payment  ?? "t1_pmt_unknown";
        var cardHolder = txn.First    ?? "Plaid Checking";
        var created    = txn.Created  ?? "2026-01-01 00:00:00.0000";
        var modified   = txn.Modified ?? created;
        var captured   = !string.IsNullOrEmpty(txn.Captured) ? txn.Captured : created;
        var returned   = !string.IsNullOrEmpty(txn.Returned) ? txn.Returned : "20260101";
        var settled    = txn.Settled?.ToString() ?? "null";
        var orderVal   = (txn.Order ?? $"Invoice Number: {invoiceNo}").Replace("\"", "\\\"");
        var desc       = txn.Description is null ? "null"
                         : $"\"{txn.Description.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

        return $$"""
        {
          "response": {
            "alert": {
              "subject": "Your transaction has been returned",
              "txnId": "{{txnId}}",
              "txnAmount": "{{amountStr}}",
              "txnCardHolder": "{{cardHolder}}",
              "txnCreated": "{{created}}",
              "txnStatus": "Returned",
              "merchantName": "BQE Core",
              "orderNumber": "{{orderVal}}",
              "paymentType": "Checking",
              "paymentNumber": "6004",
              "paymentRouting": "0021",
              "returnDescription": "Please correct the account information before resubmitting the payment"
            },
            "data": [{
              "id": "{{txnId}}",
              "created": "{{created}}",
              "modified": "{{modified}}",
              "creator": "p1_log_628e7fc19ea97423329ca3e",
              "modifier": "000000000000001",
              "merchant": "{{merchant}}",
              "token": null,
              "payment": "{{payment}}",
              "fortxn": null,
              "fromtxn": null,
              "batch": "{{batch}}",
              "subscription": null,
              "type": 7,
              "expiration": null,
              "currency": "USD",
              "platform": "VANTIV",
              "authDate": null,
              "authCode": null,
              "captured": "{{captured}}",
              "settled": {{settled}},
              "settledCurrency": "USD",
              "settledTotal": {{totalCents}},
              "allowPartial": 0,
              "order": "{{orderVal}}",
              "description": {{desc}},
              "descriptor": "BQE Core",
              "terminal": null,
              "terminalCapability": null,
              "entryMode": null,
              "origin": 2,
              "tax": null,
              "total": {{totalCents}},
              "cashback": null,
              "authorization": null,
              "approved": {{totalCents}},
              "cvv": 0,
              "swiped": 0,
              "emv": 0,
              "signature": 0,
              "unattended": null,
              "clientIp": null,
              "first": "{{cardHolder}}",
              "middle": null,
              "last": null,
              "company": null,
              "email": null,
              "address1": null,
              "address2": null,
              "city": null,
              "state": null,
              "zip": null,
              "country": null,
              "phone": null,
              "status": "5",
              "refunded": 0,
              "reserved": 0,
              "misused": null,
              "imported": 0,
              "inactive": 0,
              "frozen": 0,
              "discount": 0,
              "shipping": 0,
              "duty": 0,
              "pin": 0,
              "traceNumber": null,
              "cvvStatus": null,
              "unauthReason": null,
              "fee": null,
              "fundingCurrency": "USD",
              "authentication": null,
              "authenticationId": null,
              "cofType": null,
              "copyReason": null,
              "originalApproved": {{totalCents}},
              "currencyConversion": null,
              "serviceCode": null,
              "authTokenCustomer": null,
              "debtRepayment": 0,
              "statement": null,
              "convenienceFee": 0,
              "surcharge": null,
              "channel": null,
              "fundingEnabled": 1,
              "requestSequence": 1,
              "processedSequence": 1,
              "mobile": null,
              "pinEntryCapability": null,
              "returned": "{{returned}}",
              "txnsession": null,
              "networkTokenIndicator": 0,
              "softPosDeviceTypeIndicator": null,
              "softPosId": null,
              "tip": null
            }]
          }
        }
        """;
    }

    // ── CC Refund payload ─────────────────────────────────────────────────────
    // Payrix sends type=5 (Credit_Card_Refund_Transaction) with subject
    // "Your transaction has been captured". The gateway switch maps that
    // subject + type=5 → Event.ChargeRefunded.
    // Subject "Your transaction has been refunded" is NOT handled by the gateway.

    public static string BuildCcRefundPayload(
        string txnId     = "t1_txn_cc_refund_001",
        string fortxnId  = "t1_txn_cc_refund_001",
        string txnAmount = "74.00",
        string invoiceNo = "1102",
        string cardLast4 = "1111",
        string cardBrand = "Visa") => $$"""
        {
          "response": {
            "alert": {
              "subject": "Your transaction has been captured",
              "txnId": "{{txnId}}",
              "txnAmount": "{{txnAmount}}",
              "txnCardHolder": "Test Cardholder",
              "txnCreated": "2026-01-13 08:01:09",
              "txnStatus": "Captured",
              "merchantName": "BQE Core",
              "orderNumber": "Invoice Number: {{invoiceNo}}",
              "paymentType": "{{cardBrand}}",
              "paymentNumber": "{{cardLast4}}"
            },
            "data": [{
              "id": "{{txnId}}",
              "created": "2026-01-13 04:04:17.129",
              "modified": "2026-01-13 04:04:19.5158",
              "merchant": "t1_mer_626f798aec3d7dea43bb707",
              "token": null,
              "payment": "t1_pmt_61e1db4aa7e4068ff940d1e",
              "fortxn": "{{fortxnId}}",
              "fromtxn": null,
              "batch": "t1_bth_67dbd91868f1448d792eb48",
              "type": 5,
              "currency": "USD",
              "platform": "VANTIV",
              "captured": "2026-01-13 08:01:09",
              "settled": null,
              "settledCurrency": null,
              "settledTotal": null,
              "allowPartial": 0,
              "order": "Invoice Number: {{invoiceNo}}",
              "description": null,
              "descriptor": "BQE Core",
              "origin": 2,
              "total": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "approved": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "first": "Test Cardholder",
              "status": "1",
              "refunded": 0,
              "fundingEnabled": 1
            }]
          }
        }
        """;

    public static string BuildCcRefundPayloadFromTransaction(Transaction txn)
    {
        var txnId      = txn.Id      ?? "t1_txn_unknown";
        var fortxnId   = txn.Fortxn  ?? txnId;
        var totalCents = (long)(txn.Total ?? txn.Approved ?? 0);
        var amountStr  = (totalCents / 100m).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        var invoiceNo  = txn.InvoiceNoFromOrder;
        var merchant   = txn.Merchant ?? "t1_mer_unknown";
        var batch      = txn.Batch    ?? "t1_bth_unknown";
        var payment    = txn.Payment  ?? "t1_pmt_unknown";
        var cardHolder = txn.First    ?? "Test Cardholder";
        var created    = txn.Created  ?? "2026-01-01 00:00:00.0000";
        var modified   = txn.Modified ?? created;
        var captured   = !string.IsNullOrEmpty(txn.Captured) ? txn.Captured : created;
        var orderVal   = (txn.Order ?? $"Invoice Number: {invoiceNo}").Replace("\"", "\\\"");
        var cardBrand  = txn.PaymentDetails?.CardBrand ?? "Visa";
        var last4      = txn.PaymentDetails?.Last4 ?? "0000";

        return $$"""
        {
          "response": {
            "alert": {
              "subject": "Your transaction has been captured",
              "txnId": "{{txnId}}",
              "txnAmount": "{{amountStr}}",
              "txnCardHolder": "{{cardHolder}}",
              "txnCreated": "{{created}}",
              "txnStatus": "Captured",
              "merchantName": "BQE Core",
              "orderNumber": "{{orderVal}}",
              "paymentType": "{{cardBrand}}",
              "paymentNumber": "{{last4}}"
            },
            "data": [{
              "id": "{{txnId}}",
              "created": "{{created}}",
              "modified": "{{modified}}",
              "merchant": "{{merchant}}",
              "payment": "{{payment}}",
              "fortxn": "{{fortxnId}}",
              "fromtxn": null,
              "batch": "{{batch}}",
              "type": 5,
              "currency": "USD",
              "platform": "VANTIV",
              "captured": "{{captured}}",
              "settled": null,
              "settledCurrency": null,
              "settledTotal": null,
              "allowPartial": 0,
              "order": "{{orderVal}}",
              "description": null,
              "descriptor": "BQE Core",
              "origin": 2,
              "total": {{totalCents}},
              "approved": {{totalCents}},
              "first": "{{cardHolder}}",
              "status": "1",
              "refunded": 0,
              "fundingEnabled": 1
            }]
          }
        }
        """;
    }

    // ── CC Return payload ─────────────────────────────────────────────────────

    public static string BuildCcReturnPayload(
        string txnId     = "t1_txn_cc_return_001",
        string fortxnId  = "t1_txn_cc_return_001",
        string txnAmount = "74.00",
        string invoiceNo = "1102",
        string cardLast4 = "1111",
        string cardBrand = "Visa") => $$"""
        {
          "response": {
            "alert": {
              "subject": "Your transaction has been returned",
              "txnId": "{{txnId}}",
              "txnAmount": "{{txnAmount}}",
              "txnCardHolder": "Test Cardholder",
              "txnCreated": "2026-01-13 08:01:09",
              "txnStatus": "Captured",
              "merchantName": "BQE Core",
              "orderNumber": "Invoice Number: {{invoiceNo}}",
              "paymentType": "{{cardBrand}}",
              "paymentNumber": "{{cardLast4}}"
            },
            "data": [{
              "id": "{{txnId}}",
              "created": "2026-01-13 04:04:17.129",
              "modified": "2026-01-13 04:04:19.5158",
              "merchant": "t1_mer_626f798aec3d7dea43bb707",
              "token": null,
              "payment": "t1_pmt_61e1db4aa7e4068ff940d1e",
              "fortxn": "{{fortxnId}}",
              "fromtxn": null,
              "batch": "t1_bth_67dbd91868f1448d792eb48",
              "type": 4,
              "currency": "USD",
              "platform": "VANTIV",
              "captured": "2026-01-13 08:01:09",
              "settled": null,
              "settledCurrency": null,
              "settledTotal": null,
              "allowPartial": 0,
              "order": "Invoice Number: {{invoiceNo}}",
              "description": null,
              "descriptor": "BQE Core",
              "origin": 2,
              "total": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "approved": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "first": "Test Cardholder",
              "status": "1",
              "returned": 1,
              "refunded": 0,
              "fundingEnabled": 1
            }]
          }
        }
        """;

    public static string BuildCcReturnPayloadFromTransaction(Transaction txn)
    {
        var txnId      = txn.Id      ?? "t1_txn_unknown";
        var fortxnId   = txn.Fortxn  ?? txnId;
        var totalCents = (long)(txn.Total ?? txn.Approved ?? 0);
        var amountStr  = (totalCents / 100m).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        var invoiceNo  = txn.InvoiceNoFromOrder;
        var merchant   = txn.Merchant ?? "t1_mer_unknown";
        var batch      = txn.Batch    ?? "t1_bth_unknown";
        var payment    = txn.Payment  ?? "t1_pmt_unknown";
        var cardHolder = txn.First    ?? "Test Cardholder";
        var created    = txn.Created  ?? "2026-01-01 00:00:00.0000";
        var modified   = txn.Modified ?? created;
        var captured   = !string.IsNullOrEmpty(txn.Captured) ? txn.Captured : created;
        var orderVal   = (txn.Order ?? $"Invoice Number: {invoiceNo}").Replace("\"", "\\\"");
        var cardBrand  = txn.PaymentDetails?.CardBrand ?? "Visa";
        var last4      = txn.PaymentDetails?.Last4 ?? "0000";

        return $$"""
        {
          "response": {
            "alert": {
              "subject": "Your transaction has been returned",
              "txnId": "{{txnId}}",
              "txnAmount": "{{amountStr}}",
              "txnCardHolder": "{{cardHolder}}",
              "txnCreated": "{{created}}",
              "txnStatus": "Captured",
              "merchantName": "BQE Core",
              "orderNumber": "{{orderVal}}",
              "paymentType": "{{cardBrand}}",
              "paymentNumber": "{{last4}}"
            },
            "data": [{
              "id": "{{txnId}}",
              "created": "{{created}}",
              "modified": "{{modified}}",
              "merchant": "{{merchant}}",
              "payment": "{{payment}}",
              "fortxn": "{{fortxnId}}",
              "fromtxn": null,
              "batch": "{{batch}}",
              "type": 4,
              "currency": "USD",
              "platform": "VANTIV",
              "captured": "{{captured}}",
              "settled": null,
              "settledCurrency": null,
              "settledTotal": null,
              "allowPartial": 0,
              "order": "{{orderVal}}",
              "description": null,
              "descriptor": "BQE Core",
              "origin": 2,
              "total": {{totalCents}},
              "approved": {{totalCents}},
              "first": "{{cardHolder}}",
              "status": "1",
              "returned": 1,
              "refunded": 0,
              "fundingEnabled": 1
            }]
          }
        }
        """;
    }

    // ── Disbursement (Credit / type=6) payload ───────────────────────────────

    public static string BuildDisbursementPayload(
        string txnId     = "t1_txn_disbursement_001",
        string txnAmount = "250.00",
        string invoiceNo = "1029",
        string cardLast4 = "1111",
        string cardBrand = "Visa") => $$"""
        {
          "response": {
            "alert": {
              "subject": "Your disbursement has been funded",
              "paymentType": "{{cardBrand}}",
              "paymentNumber": "{{cardLast4}}",
              "txnId": "{{txnId}}",
              "txnAmount": "{{txnAmount}}",
              "txnSettled": "01-07-2025",
              "txnSettledTotal": "{{txnAmount}}",
              "txnCardHolder": "Test Cardholder",
              "txnCreated": "2025-01-07 12:00:00",
              "txnStatus": "Settled",
              "merchantName": "BQE Core"
            },
            "data": [{
              "id": "{{txnId}}",
              "created": "2025-01-07 12:00:00.0000",
              "modified": "2025-01-07 12:00:01.0000",
              "merchant": "t1_mer_626f798aec3d7dea43bb707",
              "token": null,
              "payment": "t1_pmt_61e1db4aa7e4068ff940d1e",
              "fortxn": null,
              "fromtxn": null,
              "batch": "t1_bth_67dbd91868f1448d792eb48",
              "type": 6,
              "currency": "USD",
              "platform": "VANTIV",
              "captured": "2025-01-07 12:00:00",
              "settled": 20250107,
              "settledCurrency": "USD",
              "settledTotal": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "allowPartial": 0,
              "order": "Invoice Number: {{invoiceNo}}",
              "description": null,
              "descriptor": "BQE Core",
              "origin": 2,
              "total": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "approved": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "first": "Test Cardholder",
              "funded": 20250107,
              "fundingEnabled": 1,
              "status": "4"
            }]
          }
        }
        """;

    public static string BuildDisbursementPayloadFromTransaction(Transaction txn)
    {
        var txnId      = txn.Id      ?? "t1_txn_unknown";
        var totalCents = (long)(txn.Total ?? txn.Approved ?? 0);
        var amountStr  = (totalCents / 100m).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        var invoiceNo  = txn.InvoiceNoFromOrder;
        var merchant   = txn.Merchant ?? "t1_mer_unknown";
        var batch      = txn.Batch    ?? "t1_bth_unknown";
        var payment    = txn.Payment  ?? "t1_pmt_unknown";
        var cardHolder = txn.First    ?? "Test Cardholder";
        var created    = txn.Created  ?? "2025-01-01 00:00:00.0000";
        var modified   = txn.Modified ?? created;
        var captured   = !string.IsNullOrEmpty(txn.Captured) ? txn.Captured : created;
        var orderVal   = (txn.Order ?? $"Invoice Number: {invoiceNo}").Replace("\"", "\\\"");
        var cardBrand  = txn.PaymentDetails?.CardBrand ?? "Visa";
        var last4      = txn.PaymentDetails?.Last4 ?? "0000";

        var funded = !string.IsNullOrEmpty(txn.Funded)   ? txn.Funded
                   : !string.IsNullOrEmpty(txn.Captured) ? txn.Captured[..10]
                   : created[..10];
        var fundedInt  = int.TryParse(funded.Replace("-", "").Replace("/", "")[..8], out var fd) ? fd : 20250101;
        var settledInt = int.TryParse(txn.Settled?.Replace("-", ""), out var sd) ? sd : fundedInt;

        return $$"""
        {
          "response": {
            "alert": {
              "subject": "Your disbursement has been funded",
              "paymentType": "{{cardBrand}}",
              "paymentNumber": "{{last4}}",
              "txnId": "{{txnId}}",
              "txnAmount": "{{amountStr}}",
              "txnSettled": "{{funded}}",
              "txnSettledTotal": "{{amountStr}}",
              "txnCardHolder": "{{cardHolder}}",
              "txnCreated": "{{created}}",
              "txnStatus": "Settled",
              "merchantName": "BQE Core"
            },
            "data": [{
              "id": "{{txnId}}",
              "created": "{{created}}",
              "modified": "{{modified}}",
              "merchant": "{{merchant}}",
              "payment": "{{payment}}",
              "fortxn": null,
              "fromtxn": null,
              "batch": "{{batch}}",
              "type": 6,
              "currency": "USD",
              "platform": "VANTIV",
              "captured": "{{captured}}",
              "settled": {{settledInt}},
              "settledCurrency": "USD",
              "settledTotal": {{totalCents}},
              "allowPartial": 0,
              "order": "{{orderVal}}",
              "description": null,
              "descriptor": "BQE Core",
              "origin": 2,
              "total": {{totalCents}},
              "approved": {{totalCents}},
              "first": "{{cardHolder}}",
              "funded": {{fundedInt}},
              "fundingEnabled": 1,
              "status": "4"
            }]
          }
        }
        """;
    }

    // ── ACH Refund (type=8, full payload) ────────────────────────────────────

    public static string BuildAchRefundPayload(
        string txnId     = "t1_txn_69660a911b36bf11fc0707d",
        string fortxnId  = "t1_txn_69660a911b36bf11fc0707d",
        string txnAmount = "74.00",
        string invoiceNo = "1102") => $$"""
        {
          "response": {
            "alert": {
              "subject": "Your transaction has been captured",
              "txnId": "{{txnId}}",
              "txnAmount": "{{txnAmount}}",
              "txnCardHolder": "Plaid Checking",
              "txnCreated": "2026-01-13 08:01:09",
              "txnStatus": "Captured",
              "merchantName": "BQE Test Merchant",
              "orderNumber": "Invoice Number: {{invoiceNo}}",
              "paymentType": "Checking",
              "paymentNumber": "0000",
              "paymentRouting": "1533"
            },
            "data": [{
              "id": "{{txnId}}",
              "created": "2026-01-13 04:04:17.129",
              "modified": "2026-01-13 04:04:19.5158",
              "creator": "t1_log_62569f6cbadd46cdd334107",
              "modifier": "000000000000001",
              "ipCreated": "152.58.113.100",
              "ipModified": "152.58.113.100",
              "merchant": "t1_mer_626f798aec3d7dea43bb707",
              "token": null,
              "payment": "t1_pmt_61e1db4aa7e4068ff940d1e",
              "fortxn": "{{fortxnId}}",
              "fromtxn": null,
              "batch": "t1_bth_67dbd91868f1448d792eb48",
              "subscription": null,
              "type": 8,
              "expiration": null,
              "currency": "USD",
              "platform": "VANTIV",
              "authDate": null,
              "authCode": null,
              "captured": "2025-03-27 08:01:09",
              "settled": null,
              "settledCurrency": null,
              "settledTotal": null,
              "allowPartial": 0,
              "order": "Invoice Number: {{invoiceNo}}",
              "description": null,
              "descriptor": "BQE Test Merchant",
              "terminal": null,
              "terminalCapability": null,
              "entryMode": null,
              "origin": 2,
              "tax": null,
              "total": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "cashback": null,
              "authorization": null,
              "approved": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "cvv": 0,
              "swiped": 0,
              "emv": 0,
              "signature": 0,
              "unattended": null,
              "clientIp": null,
              "first": "Plaid Checking",
              "middle": null,
              "last": null,
              "company": null,
              "email": null,
              "address1": null,
              "address2": null,
              "city": null,
              "state": null,
              "zip": null,
              "country": null,
              "phone": null,
              "status": "3",
              "refunded": 0,
              "reserved": 0,
              "misused": null,
              "imported": 0,
              "inactive": 0,
              "frozen": 0,
              "discount": 0,
              "shipping": 0,
              "duty": 0,
              "pin": 0,
              "traceNumber": null,
              "cvvStatus": null,
              "unauthReason": null,
              "fee": null,
              "fundingCurrency": "USD",
              "authentication": null,
              "authenticationId": null,
              "cofType": null,
              "copyReason": null,
              "originalApproved": {{(int)(decimal.Parse(txnAmount) * 100)}},
              "currencyConversion": null,
              "serviceCode": null,
              "authTokenCustomer": null,
              "debtRepayment": 0,
              "statement": null,
              "convenienceFee": 0,
              "surcharge": null,
              "channel": null,
              "funded": null,
              "fundingEnabled": 1,
              "requestSequence": 2,
              "processedSequence": 1,
              "mobile": null,
              "pinEntryCapability": null,
              "returned": null,
              "txnsession": null
            }]
          }
        }
        """;

    public static string BuildAchRefundPayloadFromTransaction(Transaction txn)
    {
        var txnId      = txn.Id      ?? "t1_txn_unknown";
        var fortxnId   = txn.Fortxn  ?? txnId;
        var totalCents = (long)(txn.Total ?? txn.Approved ?? 0);
        var amountStr  = (totalCents / 100m).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        var invoiceNo  = txn.InvoiceNoFromOrder;
        var merchant   = txn.Merchant ?? "t1_mer_unknown";
        var batch      = txn.Batch    ?? "t1_bth_unknown";
        var payment    = txn.Payment  ?? "t1_pmt_unknown";
        var cardHolder = txn.First    ?? "Plaid Checking";
        var created    = txn.Created  ?? "2026-01-01 00:00:00.0000";
        var modified   = txn.Modified ?? created;
        var captured   = !string.IsNullOrEmpty(txn.Captured) ? txn.Captured : created;
        var orderVal   = (txn.Order ?? $"Invoice Number: {invoiceNo}").Replace("\"", "\\\"");
        var origin     = txn.Origin?.ToString() ?? "2";
        var desc       = txn.Description is null ? "null"
                         : $"\"{txn.Description.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

        return $$"""
        {
          "response": {
            "alert": {
              "subject": "Your transaction has been captured",
              "txnId": "{{txnId}}",
              "txnAmount": "{{amountStr}}",
              "txnCardHolder": "{{cardHolder}}",
              "txnCreated": "{{created}}",
              "txnStatus": "Captured",
              "merchantName": "BQE Core",
              "orderNumber": "{{orderVal}}",
              "paymentType": "Checking",
              "paymentNumber": "0000",
              "paymentRouting": "1533"
            },
            "data": [{
              "id": "{{txnId}}",
              "created": "{{created}}",
              "modified": "{{modified}}",
              "creator": "000000000000001",
              "modifier": "000000000000001",
              "ipCreated": null,
              "ipModified": null,
              "merchant": "{{merchant}}",
              "token": null,
              "payment": "{{payment}}",
              "fortxn": "{{fortxnId}}",
              "fromtxn": null,
              "batch": "{{batch}}",
              "subscription": null,
              "type": 8,
              "expiration": null,
              "currency": "USD",
              "platform": "VANTIV",
              "authDate": null,
              "authCode": null,
              "captured": "{{captured}}",
              "settled": null,
              "settledCurrency": null,
              "settledTotal": null,
              "allowPartial": 0,
              "order": "{{orderVal}}",
              "description": {{desc}},
              "descriptor": "BQE Core",
              "terminal": null,
              "terminalCapability": null,
              "entryMode": null,
              "origin": {{origin}},
              "tax": null,
              "total": {{totalCents}},
              "cashback": null,
              "authorization": null,
              "approved": {{totalCents}},
              "cvv": 0,
              "swiped": 0,
              "emv": 0,
              "signature": 0,
              "unattended": null,
              "clientIp": null,
              "first": "{{cardHolder}}",
              "middle": null,
              "last": null,
              "company": null,
              "email": null,
              "address1": null,
              "address2": null,
              "city": null,
              "state": null,
              "zip": null,
              "country": null,
              "phone": null,
              "status": "3",
              "refunded": 0,
              "reserved": 0,
              "misused": null,
              "imported": 0,
              "inactive": 0,
              "frozen": 0,
              "discount": 0,
              "shipping": 0,
              "duty": 0,
              "pin": 0,
              "traceNumber": null,
              "cvvStatus": null,
              "unauthReason": null,
              "fee": null,
              "fundingCurrency": "USD",
              "authentication": null,
              "authenticationId": null,
              "cofType": null,
              "copyReason": null,
              "originalApproved": {{totalCents}},
              "currencyConversion": null,
              "serviceCode": null,
              "authTokenCustomer": null,
              "debtRepayment": 0,
              "statement": null,
              "convenienceFee": 0,
              "surcharge": null,
              "channel": null,
              "funded": null,
              "fundingEnabled": 1,
              "requestSequence": 2,
              "processedSequence": 1,
              "mobile": null,
              "pinEntryCapability": null,
              "returned": null,
              "txnsession": null
            }]
          }
        }
        """;
    }

    // ── Withdrawal (Disbursement record / p1_dbm_) payload ───────────────────

    public static string BuildWithdrawalPayload(
        string disbId       = "p1_dbm_671821bc0135617068e9e22",
        string amount       = "6131.24",
        string lastFour     = "3314",
        string merchantName = "BQE Core",
        string disbName     = "Automatically generated payout schedule",
        string entityId     = "p1_ent_652981a62e2e91967050b52",
        string payoutId     = "p1_pay_6531424017799bebf32ff72",
        string paymentId    = "p1_pmt_65d63c035d1cb29e7f9bf68")
    {
        var amountDecimal = decimal.TryParse(amount,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
        var amountCents = (long)(amountDecimal * 100);
        var alertAmount = amountDecimal.ToString("N2",
            System.Globalization.CultureInfo.GetCultureInfo("en-US"));

        return $$"""
        {
          "response": {
            "alert": {
              "subject": "Report complete for this withdrawal",
              "merchantName": "{{merchantName}}",
              "bankName": "",
              "lastFour": "{{lastFour}}",
              "withdrawalCreated": "10-22-2024",
              "withdrawalStatus": "Processed",
              "withdrawalAmount": "{{alertAmount}}",
              "withdrawalName": "{{disbName}}"
            },
            "data": [{
              "id": "{{disbId}}",
              "created": "2024-10-22 18:05:48.0081",
              "modified": "2024-10-22 22:19:13.492",
              "creator": "000000000000001",
              "modifier": "000000000000001",
              "entity": "{{entityId}}",
              "account": "df441b7951936f8c8314535df0bba5a6",
              "payout": "{{payoutId}}",
              "description": "{{disbName}}",
              "amount": {{amountCents}},
              "status": 3,
              "processed": "2024-10-22 18:42:34",
              "currency": "USD",
              "payment": "{{paymentId}}",
              "expiration": null,
              "sameDay": 0,
              "returnedAmount": null,
              "statement": null,
              "settlement": null,
              "lastNegativeEntry": "p1_etr_67170dc64f02a2321c866bf",
              "lastNegativePendingEntry": "0",
              "lastPositiveReserveEntry": "p1_rer_661725dedb32bbcf0424486",
              "disbursementEntriesStatus": "processed",
              "lastPositiveEntry": "p1_etr_6717091988aded090a483c1",
              "lastPositivePendingEntry": "0",
              "lastNegativeReserveEntry": "p1_rer_6617dd2009f47d8720ed02e",
              "fundingStatus": "pending",
              "secondaryDescriptor": "CORE ePayments"
            }]
          }
        }
        """;
    }

    public static string BuildWithdrawalPayloadFromRecord(
        PayrixLauncher.Models.DisbursementRecord rec,
        string? customField  = null,
        string  merchantName = "BQE Core",
        string  lastFour     = "0000",
        string  bankName     = "",
        IReadOnlyList<PayrixLauncher.Models.DisbursementEntry>? entries = null)
    {
        var disbId      = rec.Id      ?? "p1_dbm_unknown";
        var entity      = rec.Entity  ?? "p1_ent_unknown";
        var account     = rec.Account ?? "0000000000000000";
        var payout      = rec.Payout  ?? "p1_pay_unknown";
        var payment     = rec.Payment ?? "p1_pmt_unknown";
        var description = (rec.Description ?? "Disbursement").Replace("\"", "\\\"");
        var amountCents = rec.Amount ?? 0;
        var alertAmount = rec.AmountAlertFormatted;
        var created     = rec.Created   ?? "2024-01-01 00:00:00.0000";
        var modified    = rec.Modified  ?? created;
        var processed   = rec.Processed ?? created;
        var createdDate = rec.CreatedDateFormatted;
        var status      = rec.Status ?? 3;
        var statusLabel = rec.StatusLabel;
        var entriesStatus   = rec.DisbursementEntriesStatus ?? "processed";
        var fundingStatus   = rec.FundingStatus ?? "pending";
        var secondaryDesc   = (rec.SecondaryDescriptor ?? "CORE ePayments").Replace("\"", "\\\"");
        var sameDay         = rec.SameDay ?? 0;
        var lastNegEntry    = rec.LastNegativeEntry ?? "0";
        var lastPosEntry    = rec.LastPositiveEntry ?? "0";
        var lastPosResEntry = rec.LastPositiveReserveEntry ?? "0";
        var lastNegResEntry = rec.LastNegativeReserveEntry ?? "0";
        var lastPosPend     = rec.LastPositivePendingEntry ?? "0";
        var lastNegPend     = rec.LastNegativePendingEntry ?? "0";
        var retAmtJson      = rec.ReturnedAmount.HasValue ? rec.ReturnedAmount.Value.ToString() : "null";
        var statementJson   = rec.Statement  is null ? "null" : $"\"{rec.Statement}\"";
        var settlementJson  = rec.Settlement is null ? "null" : $"\"{rec.Settlement}\"";
        // "LocalCompanyId" is only included when there is an actual value — never output null
        var customLine      = customField is { Length: > 0 } cf
            ? $",\n              \"LocalCompanyId\": \"{cf}\""
            : string.Empty;
        var merchantEsc  = merchantName.Replace("\"", "\\\"");
        var bankNameEsc  = bankName.Replace("\"", "\\\"");
        var entriesJson  = SerializeEntries(entries);

        return $$"""
        {
          "response": {
            "alert": {
              "subject": "Report complete for this withdrawal",
              "merchantName": "{{merchantEsc}}",
              "bankName": "{{bankNameEsc}}",
              "lastFour": "{{lastFour}}",
              "withdrawalCreated": "{{createdDate}}",
              "withdrawalStatus": "{{statusLabel}}",
              "withdrawalAmount": "{{alertAmount}}",
              "withdrawalName": "{{description}}"
            },
            "data": [{
              "id": "{{disbId}}",
              "created": "{{created}}",
              "modified": "{{modified}}",
              "creator": "000000000000001",
              "modifier": "000000000000001",
              "entity": "{{entity}}",
              "account": "{{account}}",
              "payout": "{{payout}}",
              "description": "{{description}}",
              "amount": {{amountCents}},
              "status": {{status}},
              "processed": "{{processed}}",
              "currency": "USD",
              "payment": "{{payment}}",
              "expiration": null,
              "sameDay": {{sameDay}},
              "returnedAmount": {{retAmtJson}},
              "statement": {{statementJson}},
              "settlement": {{settlementJson}},
              "lastNegativeEntry": "{{lastNegEntry}}",
              "lastNegativePendingEntry": "{{lastNegPend}}",
              "lastPositiveReserveEntry": "{{lastPosResEntry}}",
              "disbursementEntriesStatus": "{{entriesStatus}}",
              "lastPositiveEntry": "{{lastPosEntry}}",
              "lastPositivePendingEntry": "{{lastPosPend}}",
              "lastNegativeReserveEntry": "{{lastNegResEntry}}",
              "fundingStatus": "{{fundingStatus}}",
              "secondaryDescriptor": "{{secondaryDesc}}",
              "disbursementEntries": {{entriesJson}}{{customLine}}
            }]
          }
        }
        """;
    }

    // ── Withdrawal Processed (subject: "Your withdrawal was processed") ────────

    public static string BuildWithdrawalProcessedPayload(
        string  disbId      = "p1_dbm_6a10d2f0bcef792b69e2f0d",
        string  amount      = "5248.50",
        string  lastFour    = "2172",
        string  merchantName = "BQE Core",
        string  disbName    = "Automatically generated payout schedule",
        string  entityId    = "p1_ent_652981a62e2e91967050b52",
        string  payoutId    = "p1_pay_6531424017799bebf32ff72",
        string  paymentId   = "p1_pmt_65d63c035d1cb29e7f9bf68",
        string  address1    = "",
        string  address2    = "",
        string  fullLocation = "",
        int     minFirstBusinessDays = 6,
        int     minBusinessDays = 1,
        bool    isFirstWithdrawal = false)
    {
        if (!decimal.TryParse(amount, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var amt))
            amt = 524850m / 100m;
        var amountCents = (long)(amt * 100);
        var alertAmount = amt.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var createdDate = DateTime.UtcNow.ToString("MM-dd-yyyy");
        var created     = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffff");
        var isFirst     = isFirstWithdrawal ? "true" : "false";

        return $$"""
        {
          "response": {
            "alert": {
              "subject": "Your withdrawal was processed",
              "merchantName": "{{merchantName}}",
              "entityId": "{{entityId}}",
              "disbursementId": "{{disbId}}",
              "address1": "{{address1}}",
              "address2": "{{address2}}",
              "fullLocation": "{{fullLocation}}",
              "bankName": "",
              "lastFour": "{{lastFour}}",
              "withdrawalCreated": "{{createdDate}}",
              "withdrawalStatus": "Processed",
              "withdrawalAmount": "{{alertAmount}}",
              "withdrawalName": "{{disbName}}",
              "minFirstBusinessDays": {{minFirstBusinessDays}},
              "minBusinessDays": {{minBusinessDays}},
              "isFirstWithdrawal": {{isFirst}}
            },
            "data": [{
              "id": "{{disbId}}",
              "created": "{{created}}",
              "modified": "{{created}}",
              "creator": "000000000000001",
              "modifier": "000000000000001",
              "entity": "{{entityId}}",
              "account": "df441b7951936f8c8314535df0bba5a6",
              "payout": "{{payoutId}}",
              "description": "{{disbName}}",
              "amount": {{amountCents}},
              "status": "3",
              "processed": "{{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}}",
              "currency": "USD",
              "payment": "{{paymentId}}",
              "expiration": null,
              "sameDay": 0,
              "returnedAmount": null,
              "statement": null,
              "settlement": null,
              "lastNegativeEntry": null,
              "lastNegativePendingEntry": null,
              "lastPositiveReserveEntry": null,
              "disbursementEntriesStatus": "pending",
              "lastPositiveEntry": null,
              "lastPositivePendingEntry": null,
              "lastNegativeReserveEntry": null,
              "fundingStatus": "pending",
              "secondaryDescriptor": "CORE ePayments",
              "custom": null
            }]
          }
        }
        """;
    }

    public static string BuildWithdrawalProcessedPayloadFromRecord(
        PayrixLauncher.Models.DisbursementRecord rec,
        string? customField          = null,
        string  merchantName         = "BQE Core",
        string  lastFour             = "0000",
        string  bankName             = "",
        string  address1             = "",
        string  address2             = "",
        string  fullLocation         = "",
        int     minFirstBusinessDays = 6,
        int     minBusinessDays      = 1,
        bool    isFirstWithdrawal    = false,
        IReadOnlyList<PayrixLauncher.Models.DisbursementEntry>? entries = null)
    {
        var disbId      = rec.Id      ?? "p1_dbm_unknown";
        var entity      = rec.Entity  ?? "p1_ent_unknown";
        var account     = rec.Account ?? "0000000000000000";
        var payout      = rec.Payout  ?? "p1_pay_unknown";
        var payment     = rec.Payment ?? "p1_pmt_unknown";
        var description = (rec.Description ?? "Disbursement").Replace("\"", "\\\"");
        var amountCents = rec.Amount ?? 0;
        var alertAmount = rec.AmountAlertFormatted;
        var created     = rec.Created   ?? "2024-01-01 00:00:00.0000";
        var modified    = rec.Modified  ?? created;
        var processed   = rec.Processed ?? created;
        var createdDate = rec.CreatedDateFormatted;
        var status      = rec.StatusRaw ?? "3";
        var statusLabel = rec.StatusLabel;
        var entriesStatus   = rec.DisbursementEntriesStatus ?? "pending";
        var fundingStatus   = rec.FundingStatus ?? "pending";
        var secondaryDesc   = (rec.SecondaryDescriptor ?? "CORE ePayments").Replace("\"", "\\\"");
        var sameDay         = rec.SameDay ?? 0;
        var retAmtJson      = rec.ReturnedAmount.HasValue ? rec.ReturnedAmount.Value.ToString() : "null";
        var statementJson   = rec.Statement  is null ? "null" : $"\"{rec.Statement}\"";
        var settlementJson  = rec.Settlement is null ? "null" : $"\"{rec.Settlement}\"";
        var customLine      = customField is { Length: > 0 } cf
            ? $",\n              \"LocalCompanyId\": \"{cf}\""
            : string.Empty;
        var isFirst         = isFirstWithdrawal ? "true" : "false";
        var merchantEsc     = merchantName.Replace("\"", "\\\"");
        var bankNameEsc     = bankName.Replace("\"", "\\\"");
        var addr1Esc        = address1.Replace("\"", "\\\"");
        var addr2Esc        = address2.Replace("\"", "\\\"");
        var locEsc          = fullLocation.Replace("\"", "\\\"");
        var lastNegEntry    = rec.LastNegativeEntry ?? "null";
        var lastPosEntry    = rec.LastPositiveEntry ?? "null";
        var lastPosResEntry = rec.LastPositiveReserveEntry ?? "null";
        var lastNegResEntry = rec.LastNegativeReserveEntry ?? "null";
        var lastPosPend     = rec.LastPositivePendingEntry ?? "null";
        var lastNegPend     = rec.LastNegativePendingEntry ?? "null";
        var lastNegEntryJson    = lastNegEntry    == "null" ? "null" : $"\"{lastNegEntry}\"";
        var lastPosEntryJson    = lastPosEntry    == "null" ? "null" : $"\"{lastPosEntry}\"";
        var lastPosResEntryJson = lastPosResEntry == "null" ? "null" : $"\"{lastPosResEntry}\"";
        var lastNegResEntryJson = lastNegResEntry == "null" ? "null" : $"\"{lastNegResEntry}\"";
        var lastPosPendJson     = lastPosPend     == "null" ? "null" : $"\"{lastPosPend}\"";
        var lastNegPendJson     = lastNegPend     == "null" ? "null" : $"\"{lastNegPend}\"";
        var entriesJson         = SerializeEntries(entries);

        return $$"""
        {
          "response": {
            "alert": {
              "subject": "Your withdrawal was processed",
              "merchantName": "{{merchantEsc}}",
              "entityId": "{{entity}}",
              "disbursementId": "{{disbId}}",
              "address1": "{{addr1Esc}}",
              "address2": "{{addr2Esc}}",
              "fullLocation": "{{locEsc}}",
              "bankName": "{{bankNameEsc}}",
              "lastFour": "{{lastFour}}",
              "withdrawalCreated": "{{createdDate}}",
              "withdrawalStatus": "{{statusLabel}}",
              "withdrawalAmount": "{{alertAmount}}",
              "withdrawalName": "{{description}}",
              "minFirstBusinessDays": {{minFirstBusinessDays}},
              "minBusinessDays": {{minBusinessDays}},
              "isFirstWithdrawal": {{isFirst}}
            },
            "data": [{
              "id": "{{disbId}}",
              "created": "{{created}}",
              "modified": "{{modified}}",
              "creator": "000000000000001",
              "modifier": "000000000000001",
              "entity": "{{entity}}",
              "account": "{{account}}",
              "payout": "{{payout}}",
              "description": "{{description}}",
              "amount": {{amountCents}},
              "status": "{{status}}",
              "processed": "{{processed}}",
              "currency": "USD",
              "payment": "{{payment}}",
              "expiration": null,
              "sameDay": {{sameDay}},
              "returnedAmount": {{retAmtJson}},
              "statement": {{statementJson}},
              "settlement": {{settlementJson}},
              "lastNegativeEntry": {{lastNegEntryJson}},
              "lastNegativePendingEntry": {{lastNegPendJson}},
              "lastPositiveReserveEntry": {{lastPosResEntryJson}},
              "disbursementEntriesStatus": "{{entriesStatus}}",
              "lastPositiveEntry": {{lastPosEntryJson}},
              "lastPositivePendingEntry": {{lastPosPendJson}},
              "lastNegativeReserveEntry": {{lastNegResEntryJson}},
              "fundingStatus": "{{fundingStatus}}",
              "secondaryDescriptor": "{{secondaryDesc}}",
              "disbursementEntries": {{entriesJson}}{{customLine}}
            }]
          }
        }
        """;
    }

    // ── Withdrawal Pending / Scheduled stage payloads ───────────────────────

    public static string BuildWithdrawalPendingPayloadFromRecord(
        PayrixLauncher.Models.DisbursementRecord rec,
        string? customField  = null,
        string  merchantName = "BQE Core",
        string  lastFour     = "0000",
        string  bankName     = "",
        IReadOnlyList<PayrixLauncher.Models.DisbursementEntry>? entries = null)
        => BuildWithdrawalStagedPayloadFromRecord(rec, 1, customField, merchantName, lastFour, bankName, entries);

    public static string BuildWithdrawalScheduledPayloadFromRecord(
        PayrixLauncher.Models.DisbursementRecord rec,
        string? customField  = null,
        string  merchantName = "BQE Core",
        string  lastFour     = "0000",
        string  bankName     = "",
        IReadOnlyList<PayrixLauncher.Models.DisbursementEntry>? entries = null)
        => BuildWithdrawalStagedPayloadFromRecord(rec, 2, customField, merchantName, lastFour, bankName, entries);

    private static string BuildWithdrawalStagedPayloadFromRecord(
        PayrixLauncher.Models.DisbursementRecord rec,
        int     stageStatus,          // 1=Pending, 2=Scheduled
        string? customField  = null,
        string  merchantName = "BQE Core",
        string  lastFour     = "0000",
        string  bankName     = "",
        IReadOnlyList<PayrixLauncher.Models.DisbursementEntry>? entries = null)
    {
        var disbId      = rec.Id      ?? "p1_dbm_unknown";
        var entity      = rec.Entity  ?? "p1_ent_unknown";
        var account     = rec.Account ?? "0000000000000000";
        var payout      = rec.Payout  ?? "p1_pay_unknown";
        var payment     = rec.Payment ?? "p1_pmt_unknown";
        var description = (rec.Description ?? "Disbursement").Replace("\"", "\\\"");
        var amountCents = rec.Amount ?? 0;
        var alertAmount = rec.AmountAlertFormatted;
        var created     = rec.Created  ?? "2024-01-01 00:00:00.0000";
        var modified    = rec.Modified ?? created;
        var createdDate = rec.CreatedDateFormatted;
        var statusLabel = stageStatus == 1 ? "Pending" : "Scheduled";
        var entriesStatus   = "pending";
        var fundingStatus   = rec.FundingStatus ?? "pending";
        var secondaryDesc   = (rec.SecondaryDescriptor ?? "CORE ePayments").Replace("\"", "\\\"");
        var sameDay         = rec.SameDay ?? 0;
        var retAmtJson      = rec.ReturnedAmount.HasValue ? rec.ReturnedAmount.Value.ToString() : "null";
        var statementJson   = rec.Statement  is null ? "null" : $"\"{rec.Statement}\"";
        var settlementJson  = rec.Settlement is null ? "null" : $"\"{rec.Settlement}\"";
        var customLine      = customField is { Length: > 0 } cf
            ? $",\n              \"LocalCompanyId\": \"{cf}\""
            : string.Empty;
        var merchantEsc = merchantName.Replace("\"", "\\\"");
        var bankNameEsc = bankName.Replace("\"", "\\\"");
        var entriesJson = SerializeEntries(entries);

        return $$"""
        {
          "response": {
            "alert": {
              "subject": "Report complete for this withdrawal",
              "merchantName": "{{merchantEsc}}",
              "bankName": "{{bankNameEsc}}",
              "lastFour": "{{lastFour}}",
              "withdrawalCreated": "{{createdDate}}",
              "withdrawalStatus": "{{statusLabel}}",
              "withdrawalAmount": "{{alertAmount}}",
              "withdrawalName": "{{description}}"
            },
            "data": [{
              "id": "{{disbId}}",
              "created": "{{created}}",
              "modified": "{{modified}}",
              "creator": "000000000000001",
              "modifier": "000000000000001",
              "entity": "{{entity}}",
              "account": "{{account}}",
              "payout": "{{payout}}",
              "description": "{{description}}",
              "amount": {{amountCents}},
              "status": {{stageStatus}},
              "processed": null,
              "currency": "USD",
              "payment": "{{payment}}",
              "expiration": null,
              "sameDay": {{sameDay}},
              "returnedAmount": {{retAmtJson}},
              "statement": {{statementJson}},
              "settlement": {{settlementJson}},
              "lastNegativeEntry": "0",
              "lastNegativePendingEntry": "0",
              "lastPositiveReserveEntry": "0",
              "disbursementEntriesStatus": "{{entriesStatus}}",
              "lastPositiveEntry": "0",
              "lastPositivePendingEntry": "0",
              "lastNegativeReserveEntry": "0",
              "fundingStatus": "{{fundingStatus}}",
              "secondaryDescriptor": "{{secondaryDesc}}",
              "disbursementEntries": {{entriesJson}}{{customLine}}
            }]
          }
        }
        """;
    }

    // ── Onboarding payloads (Entities Created / Merchant Boarded) ────────────

    /// <summary>
    /// Builds a Merchant Boarded webhook payload using real merchant + entity data.
    /// Matches the exact structure Payrix sends for the "Merchant Boarded" alert.
    /// </summary>
    public static string BuildMerchantBoardedPayload(
        string merchantId       = "t1_mer_626f798aec3d7dea43bb707",
        string entityId         = "t1_ent_626f798ae3f3897d0fa4d44",
        string entityName       = "Luettgen - Koepp",
        string? dba             = null,
        string merchantCreated  = "05-02-2022",
        string merchantStatus   = "Boarded",
        string mcc              = "1799",
        string ownerFirstName   = "James",
        string ownerLastName    = "Foster",
        string created          = "2022-05-02 02:26:18.972",
        string modified         = "2022-05-02 02:26:23.5837",
        string login            = "t1_log_61f884eb1e0b6066b1c546f",
        string email            = "Shaheen@bqe.com",
        string? boarded         = "20220502",
        string environment      = "cardPresent",
        string custom           = StdCustom)
    {
        var dbaJson    = dba    == null ? "null" : $"\"{dba}\"";

        return $$"""
        {
          "response": {
            "alert": {
              "subject": "Merchant has been boarded",
              "entityName": "{{entityName}}",
              "merchantId": "{{merchantId}}",
              "merchantDba": {{dbaJson}},
              "merchantCreated": "{{merchantCreated}}",
              "merchantStatus": "{{merchantStatus}}",
              "merchantMcc": "{{mcc}}",
              "ownerFirstName": "{{ownerFirstName}}",
              "ownerLastName": "{{ownerLastName}}",
              "entityCustom": "{{custom}}"
            },
            "data": [
              {
                "id": "{{merchantId}}",
                "created": "{{created}}",
                "modified": "{{modified}}",
                "creator": "{{login}}",
                "modifier": "{{login}}",
                "lastActivity": null,
                "entity": "{{entityId}}",
                "dba": {{dbaJson}},
                "new": 1,
                "established": null,
                "annualCCSales": 0,
                "avgTicket": 0,
                "amex": null,
                "discover": null,
                "mcc": "{{mcc}}",
                "status": "2",
                "boarded": "{{boarded}}",
                "inactive": 0,
                "frozen": 0,
                "environment": "{{environment}}",
                "visaMvv": null,
                "chargebackNotificationEmail": "{{email}}",
                "statusReason": null,
                "totalApprovedSales": 0,
                "autoBoarded": "1",
                "saqType": null,
                "saqDate": null,
                "qsa": null,
                "letterStatus": 0,
                "letterDate": null,
                "tcAttestation": 0,
                "visaDisclosure": 0,
                "disclosureIP": null,
                "disclosureDate": null,
                "accountClosureReasonCode": null,
                "accountClosureReasonDate": null,
                "annualCCSaleVolume": null,
                "annualACHSaleVolume": null,
                "riskLevel": null,
                "creditRatio": null,
                "creditTimeliness": null,
                "chargebackRatio": null,
                "ndxDays": null,
                "ndxPercentage": null,
                "advancedBilling": null,
                "locationType": null,
                "percentKeyed": null,
                "totalVolume": null,
                "percentEcomm": null,
                "seasonal": null,
                "amexVolume": null,
                "incrementalAuthSupported": null,
                "tmxSessionId": null,
                "percentBusiness": null,
                "applePayActive": 1,
                "applePayStatus": null,
                "googlePayActive": 1
              }
            ]
          }
        }
        """;
    }

    /// <summary>
    /// Builds a Merchant Boarded payload from real Payrix merchant + entity records.
    /// Falls back to the template if data is incomplete.
    /// </summary>
    public static string BuildMerchantBoardedPayloadFromData(
        Models.Merchant merchant,
        string entityId,
        string entityName,
        string ownerFirstName = "James",
        string ownerLastName  = "Foster",
        string custom         = StdCustom)
    {
        // Format boarded date: "20220502" → "05-02-2022"
        string FormatBoarded(string? b)
        {
            if (string.IsNullOrWhiteSpace(b) || b.Length < 8) return DateTime.UtcNow.ToString("MM-dd-yyyy");
            return $"{b[4..6]}-{b[6..8]}-{b[0..4]}";
        }

        string statusLabel = merchant.Status switch { 1 => "Active", 2 => "Boarded", 3 => "Suspended", _ => "Boarded" };
        string createdDate = FormatBoarded(merchant.Boarded) != DateTime.UtcNow.ToString("MM-dd-yyyy")
            ? FormatBoarded(merchant.Boarded)
            : FormatBoarded(merchant.Created?.Replace("-","").Replace(" ","").Replace(":","")[..8]);

        return BuildMerchantBoardedPayload(
            merchantId      : merchant.Id,
            entityId        : entityId,
            entityName      : entityName,
            dba             : merchant.Dba,
            merchantCreated : createdDate,
            merchantStatus  : statusLabel,
            mcc             : merchant.Mcc ?? "1799",
            ownerFirstName  : ownerFirstName,
            ownerLastName   : ownerLastName,
            created         : merchant.Created ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            modified        : merchant.Modified ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffff"),
            login           : merchant.Creator ?? "t1_log_000000000000000000000000",
            email           : merchant.ChargebackNotificationEmail ?? merchant.Email ?? "merchant@bqe.com",
            boarded         : merchant.Boarded ?? DateTime.UtcNow.ToString("yyyyMMdd"),
            environment     : merchant.Environment ?? "cardPresent",
            custom          : custom);
    }

    /// <summary>
    /// Builds an onboarding webhook payload with the given entity custom field
    /// (format: "AccountID,CompanyID").
    /// </summary>
    public static string BuildOnboardingPayload(
        string subject  = "Entities created successfully.",
        string entityId = "t1_ent_onboard_001",
        string custom   = StdCustom,
        string login    = "t1_log_61f884eb1e0b6066b1c546f") => $$"""
        {
          "response": {
            "alert": {
              "subject": "{{subject}}"
            },
            "data": [{
              "id": "{{entityId}}",
              "created": "2022-05-02 02:26:18.9341",
              "modified": "2022-05-02 02:26:18.9341",
              "creator": "{{login}}",
              "modifier": "{{login}}",
              "ipCreated": "172.70.218.213",
              "ipModified": "172.70.218.213",
              "clientIp": null,
              "login": "{{login}}",
              "parameter": null,
              "type": 2,
              "name": "Luettgen - Koepp",
              "address1": "025 Ardith Ford",
              "address2": null,
              "city": "West Aisha",
              "state": "TX",
              "zip": "62868",
              "country": "USA",
              "timezone": "cst",
              "phone": "7027678096",
              "fax": null,
              "email": "Shaheen@bqe.com",
              "website": "http://testwebsite.com",
              "ein": "1492",
              "tcVersion": "1.0",
              "tcDate": "2022-05-02 02:26:18",
              "tcIp": "172.70.218.213",
              "tcAcceptDate": null,
              "tcAcceptIp": null,
              "custom": "{{custom}}",
              "inactive": 0,
              "frozen": 0,
              "tinStatus": 0,
              "reserved": 0,
              "checkStage": "underwriting",
              "public": 0,
              "customerPhone": null,
              "locations": 1,
              "industry": null,
              "displayName": null,
              "totalCreditDisbursements": 0,
              "payoutSecondaryDescriptor": null,
              "einType": null,
              "irsFilingName": null,
              "currency": "USD",
              "appleDomain": null
            }]
          }
        }
        """;

    /// <summary>
    /// Takes the raw JSON returned by GET /entities (which already contains
    /// the full entity object) and produces a webhook-shaped payload:
    ///   • Injects response.alert.subject
    ///   • Replaces response.data[0].custom with the supplied value
    ///   • Strips everything else from response (errors, totalItems, etc.)
    /// Falls back to the template-based builder if parsing fails.
    /// </summary>
    public static string BuildOnboardingPayloadFromEntityJson(
        string rawEntityJson,
        string subject,
        string custom)
    {
        try
        {
            var root   = JsonNode.Parse(rawEntityJson);
            var data   = root?["response"]?["data"]?.AsArray();
            var entity = data?.Count > 0 ? data[0]?.AsObject() : null;

            if (entity is not null)
            {
                // Replace only the custom field; every other field stays real
                entity["custom"] = JsonValue.Create(custom);

                // Remove the entity from its original array so we can re-use it
                data!.RemoveAt(0);

                var payload = new JsonObject
                {
                    ["response"] = new JsonObject
                    {
                        ["alert"] = new JsonObject { ["subject"] = JsonValue.Create(subject) },
                        ["data"]  = new JsonArray { entity }
                    }
                };

                return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
        }
        catch { /* fall through */ }

        // Fallback: template with defaults
        return BuildOnboardingPayload(subject, custom: custom);
    }

    // ── Test catalogue ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the standard set of webhook test cases.
    /// Pass <paramref name="entityCustom"/> ("AccountID,CompanyID") so that onboarding
    /// webhooks use the correct entity for the target environment — required for local testing.
    /// Transaction webhooks (ACH funded, CC refund/return, etc.) rely on encrypted metadata
    /// embedded in the description field; for local environments use real sandbox transactions
    /// originally created by that local BQE Core instance via "Fetch Real Transaction &amp; Send".
    /// </summary>
    public static List<WebhookTestCase> BuildTestCases(string? entityCustom = null)
    {
        var custom = string.IsNullOrWhiteSpace(entityCustom) ? StdCustom : entityCustom.Trim();
        return
        [
            new()
            {
                Name        = "Entities Created",
                Tag         = "Entities",
                Description = "Entities created successfully — triggers merchant creation.",
                Payload     = BuildOnboardingPayload("Entities created successfully.", "t1_ent_onboard_001", custom)
            },
            new()
            {
                Name        = "Merchant Boarded",
                Tag         = "Merchant",
                Description = "Merchant has been boarded — triggers merchant update.",
                Payload     = BuildMerchantBoardedPayload(custom: custom)
            },
            new()
            {
                Name        = "ACH eCheck Funded",
                Tag         = "Payment",
                Description = "paymentType=Checking, txnStatus=Settled → triggers payout.",
                Payload     = BuildAchFundedPayload(
                                  txnId:     "t1_txn_69f47e89b6fa7b757b18869",
                                  txnAmount: "898.00",
                                  invoiceNo: "1029")
            },
            new()
            {
                Name        = "ACH eCheck Return",
                Tag         = "Payment",
                Description = "type=7, status=5 → records ACH return  ⚠ Local: use Fetch Real Txn",
                Payload     = BuildECheckReturnPayload(
                                  txnId:     "t1_txn_67dd026badc4395bd51b215",
                                  txnAmount: "67.87",
                                  invoiceNo: "1102")
            },
            new()
            {
                Name        = "CC Refund",
                Tag         = "Payment",
                Description = "type=5, status=1, subject=captured → records CC refund/credit  ⚠ Local: use Fetch Real Txn",
                Payload     = BuildCcRefundPayload(
                                  txnId:     "t1_txn_cc_refund_001",
                                  fortxnId:  "t1_txn_cc_refund_001",
                                  txnAmount: "74.00",
                                  invoiceNo: "1102")
            },
            new()
            {
                Name        = "CC Return",
                Tag         = "Payment",
                Description = "type=4, returned=1 → records CC return  ⚠ Local: use Fetch Real Txn",
                Payload     = BuildCcReturnPayload(
                                  txnId:     "t1_txn_cc_return_001",
                                  fortxnId:  "t1_txn_cc_return_001",
                                  txnAmount: "74.00",
                                  invoiceNo: "1102")
            },
            new()
            {
                Name        = "Disbursement",
                Tag         = "Payment",
                Description = "type=6, status=4 → records outbound disbursement  ⚠ Local: set PayrixLocalEntityCustom",
                Payload     = BuildDisbursementPayload(
                                  txnId:     "t1_txn_disbursement_001",
                                  txnAmount: "250.00",
                                  invoiceNo: "1029")
            },
            new()
            {
                Name        = "ACH Refund",
                Tag         = "Payment",
                Description = "type=8, status=3 — ACH/eCheck refund with all standard fields  ⚠ Local: use Fetch Real Txn",
                Payload     = BuildAchRefundPayload(
                                  txnId:     "t1_txn_69660a911b36bf11fc0707d",
                                  fortxnId:  "t1_txn_69660a911b36bf11fc0707d",
                                  txnAmount: "74.00",
                                  invoiceNo: "1102")
            },
            new()
            {
                Name        = "Withdrawal",
                Tag         = "Payment",
                Description = "p1_dbm_ record, subject=withdrawal report → records payout  ⚠ Local: set PayrixLocalEntityCustom",
                Payload     = BuildWithdrawalPayload(
                                  disbId:   "p1_dbm_671821bc0135617068e9e22",
                                  amount:   "6131.24",
                                  lastFour: "3314")
            },
            new()
            {
                Name        = "Withdrawal Processed",
                Tag         = "Payment",
                Description = "p1_dbm_ record, subject=Your withdrawal was processed → funds deposited",
                Payload     = BuildWithdrawalProcessedPayload(
                                  disbId:   "p1_dbm_6a10d2f0bcef792b69e2f0d",
                                  amount:   "5248.50",
                                  lastFour: "2172")
            },
        ];
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Serialises a list of disbursement entries to a compact JSON array string
    /// suitable for embedding in a webhook payload. Returns "[]" if null/empty.
    /// Amounts are kept in cents (as Payrix sends them).
    /// </summary>
    private static string SerializeEntries(IReadOnlyList<PayrixLauncher.Models.DisbursementEntry>? entries)
    {
        if (entries is null || entries.Count == 0) return "[]";

        var sb = new System.Text.StringBuilder();
        sb.Append('[');
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            AppendJsonStr(sb, "id",           e.Id,           first: true);
            AppendJsonStr(sb, "disbursement", e.Disbursement);
            sb.Append($",\"amount\":{e.Amount ?? 0}");
            sb.Append($",\"amountUsed\":{e.AmountUsed ?? 0}");
            if (e.Event.HasValue)      sb.Append($",\"event\":{e.Event.Value}");
            AppendJsonStr(sb, "eventId",      e.EventId);
            AppendJsonStr(sb, "description",  e.Description);
            AppendJsonStr(sb, "pendingEntry", e.PendingEntry);
            AppendJsonStr(sb, "reserveEntry", e.ReserveEntry);

            // Embed the entry sub-object if present
            if (e.Entry is not null)
            {
                sb.Append(",\"entry\":{");
                AppendJsonStr(sb, "id",           e.Entry.Id,    first: true);
                AppendJsonStr(sb, "txn",          e.Entry.Txn);
                AppendJsonStr(sb, "disbursement", e.Entry.Disbursement);
                AppendJsonStr(sb, "entity",       e.Entry.Entity);
                AppendJsonStr(sb, "fund",         e.Entry.Fund);
                AppendJsonStr(sb, "fee",          e.Entry.Fee);
                AppendJsonStr(sb, "refund",       e.Entry.Refund);
                AppendJsonStr(sb, "chargeback",   e.Entry.Chargeback);
                AppendJsonStr(sb, "adjustment",   e.Entry.Adjustment);
                AppendJsonStr(sb, "description",  e.Entry.Description);
                AppendJsonStr(sb, "statement",    e.Entry.Statement);
                AppendJsonStr(sb, "settlement",   e.Entry.Settlement);
                sb.Append($",\"amount\":{e.Entry.Amount ?? 0}");
                if (e.Entry.Event.HasValue)  sb.Append($",\"event\":{e.Entry.Event.Value}");
                if (e.Entry.IsFee.HasValue)  sb.Append($",\"isFee\":{e.Entry.IsFee.Value}");
                if (e.Entry.Pending.HasValue) sb.Append($",\"pending\":{e.Entry.Pending.Value}");
                sb.Append('}');
            }
            else
            {
                sb.Append(",\"entry\":null");
            }

            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static void AppendJsonStr(System.Text.StringBuilder sb, string key, string? value, bool first = false)
    {
        if (!first) sb.Append(',');
        if (value is null)
            sb.Append($"\"{key}\":null");
        else
            sb.Append($"\"{key}\":\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
    }

    private static string GetDeepMessage(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null)
            inner = inner.InnerException;
        return inner == ex
            ? ex.Message
            : $"{inner.Message}  [{ex.GetType().Name}]";
    }

    // ── Runner ────────────────────────────────────────────────────────────────

    public static async Task RunAsync(
        WebhookTestCase test,
        string endpointUrl,
        CancellationToken ct = default)
    {
        test.Status    = TestStatus.Running;
        test.HttpCode  = null;
        test.Detail    = "";
        test.DurationMs = 0;

        var sw = Stopwatch.StartNew();
        try
        {
            using var handler = new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    System.Net.Http.HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client  = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            using var content = new StringContent(test.Payload, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(endpointUrl, content, ct);
            sw.Stop();

            test.DurationMs = sw.ElapsedMilliseconds;
            test.HttpCode   = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                test.Status = TestStatus.Pass;
                test.Detail = $"HTTP {test.HttpCode} — OK";
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                test.Status = TestStatus.Fail;
                test.Detail = $"HTTP {test.HttpCode} — {body.Trim()}";
            }
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            test.DurationMs = sw.ElapsedMilliseconds;
            test.Status     = TestStatus.Fail;
            test.Detail     = "Timed out after 30 s — is the endpoint reachable?";
        }
        catch (Exception ex)
        {
            sw.Stop();
            test.DurationMs = sw.ElapsedMilliseconds;
            test.Status     = TestStatus.Fail;
            test.Detail     = GetDeepMessage(ex);
        }
        finally
        {
            WebhookLogger.LogTestPost(
                testName:   test.Name,
                url:        endpointUrl,
                payload:    test.Payload,
                httpCode:   test.HttpCode,
                detail:     test.Detail,
                durationMs: test.DurationMs,
                passed:     test.Status == TestStatus.Pass);
        }
    }
}
