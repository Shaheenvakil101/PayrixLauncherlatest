using System.IO;
using System.Text.Json;
using PayrixLauncher.Models;

namespace PayrixLauncher.Services;

/// <summary>
/// Persists recent request history to request_log.json.
/// LRU behaviour: re-sending an existing URL+method promotes it to the top.
/// Capped at <see cref="MaxEntries"/> entries.
/// </summary>
public static class RequestLogService
{
    public const int MaxEntries = 50;

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string LogFile =>
        Path.Combine(AppContext.BaseDirectory, "request_log.json");

    // ── Load ──────────────────────────────────────────────────────────────────

    public static List<RequestLogEntry> Load()
    {
        try
        {
            if (!File.Exists(LogFile)) return [];
            var json = File.ReadAllText(LogFile);
            return JsonSerializer.Deserialize<List<RequestLogEntry>>(json, _opts) ?? [];
        }
        catch { return []; }
    }

    // ── Append (LRU — promotes duplicate to top, caps at MaxEntries) ──────────

    public static void Append(RequestLogEntry entry)
    {
        try
        {
            var entries = Load();

            // Remove any existing entry with the same key so it moves to top
            entries.RemoveAll(e => e.Key == entry.Key);

            entry.SavedAt = DateTime.UtcNow.ToString("o");
            entries.Insert(0, entry); // newest / most-recently-used first

            // Trim to cap
            if (entries.Count > MaxEntries)
                entries = entries[..MaxEntries];

            File.WriteAllText(LogFile, JsonSerializer.Serialize(entries, _opts));
        }
        catch { /* log silently */ }
    }

    // ── Save full list ────────────────────────────────────────────────────────

    public static void Save(List<RequestLogEntry> entries)
    {
        try { File.WriteAllText(LogFile, JsonSerializer.Serialize(entries, _opts)); }
        catch { }
    }
}
