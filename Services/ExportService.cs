using System.IO;
using System.Text;
using System.Text.Json;
using PayrixLauncher.Models;

namespace PayrixLauncher.Services;

public static class ExportService
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public static async Task SaveJsonAsync(IEnumerable<Transaction> transactions, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, $"transactions_{Timestamp()}.json");
        var json = JsonSerializer.Serialize(transactions, PrettyJson);
        await File.WriteAllTextAsync(path, json);
        Console.WriteLine($"  JSON saved -> {path}");
    }

    public static async Task SaveCsvAsync(IEnumerable<Transaction> transactions, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, $"transactions_{Timestamp()}.csv");

        var sb = new StringBuilder();

        // Transaction headers
        sb.Append("txn_id,txn_created,txn_modified,txn_creator,txn_modifier,");
        sb.Append("ipCreated,ipModified,merchant,token,payment,fortxn,fromtxn,batch,subscription,");
        sb.Append("type,expiration,currency,platform,authDate,authCode,captured,settled,settledCurrency,settledTotal,");
        sb.Append("allowPartial,order,description,descriptor,terminal,terminalCapability,entryMode,origin,");
        sb.Append("tax,total,cashback,authorization,approved,cvv,swiped,emv,signature,unattended,clientIp,");
        sb.Append("first,middle,last,company,email,address1,address2,city,state,zip,country,phone,");
        sb.Append("status,refunded,reserved,misused,imported,inactive,frozen,discount,shipping,duty,pin,");
        sb.Append("traceNumber,cvvStatus,unauthReason,fee,fundingCurrency,authentication,authenticationId,");
        sb.Append("cofType,copyReason,originalApproved,currencyConversion,serviceCode,authTokenCustomer,");
        sb.Append("debtRepayment,statement,convenienceFee,surcharge,channel,funded,fundingEnabled,");
        sb.Append("requestSequence,processedSequence,mobile,pinEntryCapability,returned,txnsession,");
        sb.Append("networkTokenIndicator,softPosDeviceTypeIndicator,softPosId,tip,pinlessDebitConversion,submittedMethod,processedMethod,");
        sb.Append("discrepancy_status,discrepancy_details,");
        // Item headers
        sb.AppendLine("item_id,item_created,item_modified,item_creator,item_modifier,item_txn,item_name,item_description,item_custom,item_quantity,item_price,item_inactive,item_frozen,item_um,item_commodityCode,item_total,item_discount,item_productCode,item_discountTreatment");

        foreach (var t in transactions)
        {
            var txnPart = string.Join(",",
                Q(t.Id), Q(t.Created), Q(t.Modified), Q(t.Creator), Q(t.Modifier),
                Q(t.IpCreated), Q(t.IpModified), Q(t.Merchant), Q(t.Token), Q(t.Payment),
                Q(t.Fortxn), Q(t.Fromtxn), Q(t.Batch), Q(t.Subscription),
                t.Type, Q(t.Expiration), Q(t.Currency), Q(t.Platform),
                Q(t.AuthDate), Q(t.AuthCode), Q(t.Captured), Q(t.Settled),
                Q(t.SettledCurrency), t.SettledTotal, t.AllowPartial,
                Q(t.Order), Q(t.Description), Q(t.Descriptor),
                Q(t.Terminal), Q(t.TerminalCapability), Q(t.EntryMode), t.Origin,
                t.Tax, t.Total, t.Cashback, Q(t.Authorization), t.Approved,
                t.Cvv, t.Swiped, t.Emv, t.Signature, Q(t.Unattended), Q(t.ClientIp),
                Q(t.First), Q(t.Middle), Q(t.Last), Q(t.Company), Q(t.Email),
                Q(t.Address1), Q(t.Address2), Q(t.City), Q(t.State), Q(t.Zip), Q(t.Country), Q(t.Phone),
                t.Status, t.Refunded, t.Reserved, Q(t.Misused), t.Imported, t.Inactive, t.Frozen,
                t.Discount, t.Shipping, t.Duty, t.Pin,
                Q(t.TraceNumber), Q(t.CvvStatus), Q(t.UnauthReason), t.Fee, Q(t.FundingCurrency),
                Q(t.Authentication), Q(t.AuthenticationId),
                Q(t.CofType), Q(t.CopyReason), t.OriginalApproved, Q(t.CurrencyConversion),
                Q(t.ServiceCode), Q(t.AuthTokenCustomer),
                t.DebtRepayment, Q(t.Statement), t.ConvenienceFee, t.Surcharge,
                Q(t.Channel), Q(t.Funded), t.FundingEnabled,
                t.RequestSequence, t.ProcessedSequence, Q(t.Mobile), Q(t.PinEntryCapability),
                Q(t.Returned), Q(t.Txnsession),
                t.NetworkTokenIndicator, Q(t.SoftPosDeviceTypeIndicator), Q(t.SoftPosId),
                t.Tip, Q(t.PinlessDebitConversion), Q(t.SubmittedMethod), Q(t.ProcessedMethod),
                Q(t.DiscrepancyLabel), Q(t.DiscrepancySummary)
            );

            if (t.Items.Count == 0)
            {
                sb.AppendLine($"{txnPart},,,,,,,,,,,,,,,,,,");
            }
            else
            {
                foreach (var i in t.Items)
                {
                    var itemPart = string.Join(",",
                        Q(i.Id), Q(i.Created), Q(i.Modified), Q(i.Creator), Q(i.Modifier),
                        Q(i.Txn), Q(i.Item), Q(i.Description), Q(i.Custom),
                        i.Quantity, i.Price, i.Inactive, i.Frozen, Q(i.Um),
                        Q(i.CommodityCode), i.Total, i.Discount, Q(i.ProductCode), i.DiscountTreatment
                    );
                    sb.AppendLine($"{txnPart},{itemPart}");
                }
            }
        }

        await File.WriteAllTextAsync(path, sb.ToString());
        Console.WriteLine($"  CSV saved -> {path}");
    }

    private static string Q(object? val) =>
        val is null ? "" : $"\"{val.ToString()!.Replace("\"", "\"\"")}\"";

    private static string Timestamp() => DateTime.Now.ToString("yyyyMMdd_HHmmss");
}
