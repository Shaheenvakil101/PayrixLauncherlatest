using System.Text;
using System.Text.Json;

namespace PayrixLauncher.Services;

public static class JsonToolsService
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    // ── Formatter ─────────────────────────────────────────────────────────────

    public static (string result, string? error) Format(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return ("", "Input is empty.");
        try
        {
            var doc = JsonDocument.Parse(input, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return (JsonSerializer.Serialize(doc, Pretty), null);
        }
        catch (JsonException ex)
        {
            return ("", $"Invalid JSON: {ex.Message}");
        }
    }

    // ── Compare ───────────────────────────────────────────────────────────────

    public record DiffEntry(string Path, string? LeftValue, string? RightValue)
    {
        public bool OnlyInLeft  => RightValue is null;
        public bool OnlyInRight => LeftValue  is null;
        public bool Different   => LeftValue is not null && RightValue is not null && LeftValue != RightValue;
    }

    public static (List<DiffEntry> diffs, string? error) Compare(string leftJson, string rightJson)
    {
        if (string.IsNullOrWhiteSpace(leftJson))  return ([], "Left JSON is empty.");
        if (string.IsNullOrWhiteSpace(rightJson)) return ([], "Right JSON is empty.");

        JsonDocument leftDoc, rightDoc;
        try  { leftDoc  = JsonDocument.Parse(leftJson,  new JsonDocumentOptions { AllowTrailingCommas = true }); }
        catch (JsonException ex) { return ([], $"Left JSON invalid: {ex.Message}"); }
        try  { rightDoc = JsonDocument.Parse(rightJson, new JsonDocumentOptions { AllowTrailingCommas = true }); }
        catch (JsonException ex) { return ([], $"Right JSON invalid: {ex.Message}"); }

        var left  = Flatten(leftDoc.RootElement,  "$");
        var right = Flatten(rightDoc.RootElement, "$");

        var allKeys = left.Keys.Union(right.Keys).OrderBy(k => k).ToList();
        var diffs   = new List<DiffEntry>();

        foreach (var key in allKeys)
        {
            var lv = left.TryGetValue(key, out var l)  ? l : null;
            var rv = right.TryGetValue(key, out var r) ? r : null;
            if (lv != rv)
                diffs.Add(new DiffEntry(key, lv, rv));
        }

        return (diffs, null);
    }

    private static Dictionary<string, string> Flatten(JsonElement element, string prefix)
    {
        var result = new Dictionary<string, string>();
        FlattenInto(element, prefix, result);
        return result;
    }

    private static void FlattenInto(JsonElement element, string path, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    FlattenInto(prop.Value, $"{path}.{prop.Name}", result);
                break;

            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in element.EnumerateArray())
                    FlattenInto(item, $"{path}[{i++}]", result);
                break;

            default:
                result[path] = element.ToString();
                break;
        }
    }

    // ── Summary ───────────────────────────────────────────────────────────────

    public static string BuildDiffSummary(List<DiffEntry> diffs)
    {
        if (diffs.Count == 0) return "✅  No differences found — JSONs are identical.";

        var sb = new StringBuilder();
        sb.AppendLine($"⚠  {diffs.Count} difference(s) found:\n");

        foreach (var d in diffs)
        {
            if (d.OnlyInLeft)
                sb.AppendLine($"  ➖  {d.Path}\n       Left only: {d.LeftValue}");
            else if (d.OnlyInRight)
                sb.AppendLine($"  ➕  {d.Path}\n       Right only: {d.RightValue}");
            else
                sb.AppendLine($"  ≠   {d.Path}\n       Left:  {d.LeftValue}\n       Right: {d.RightValue}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
