using System.IO;
using System.Text.Json;
using PayrixLauncher.Models;

namespace PayrixLauncher.Services;

public static class HttpCollectionService
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string StoreFile =>
        Path.Combine(AppContext.BaseDirectory, "http_collections.json");

    public static HttpCollectionStore Load()
    {
        try
        {
            if (!File.Exists(StoreFile)) return new HttpCollectionStore();
            var json = File.ReadAllText(StoreFile);
            return JsonSerializer.Deserialize<HttpCollectionStore>(json, _opts)
                   ?? new HttpCollectionStore();
        }
        catch { return new HttpCollectionStore(); }
    }

    public static void Save(HttpCollectionStore store)
    {
        try { File.WriteAllText(StoreFile, JsonSerializer.Serialize(store, _opts)); }
        catch { /* swallow — non-critical */ }
    }
}
