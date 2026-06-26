using System.IO;
using System.Text.Json;
using PayrixLauncher.Models;

namespace PayrixLauncher.Services;

public static class HttpEnvironmentService
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string StoreFile =>
        Path.Combine(AppContext.BaseDirectory, "http_environments.json");

    public static HttpEnvironmentStore Load()
    {
        try
        {
            if (!File.Exists(StoreFile)) return Seed();
            var json = File.ReadAllText(StoreFile);
            return JsonSerializer.Deserialize<HttpEnvironmentStore>(json, _opts)
                   ?? Seed();
        }
        catch { return Seed(); }
    }

    public static void Save(HttpEnvironmentStore store)
    {
        try { File.WriteAllText(StoreFile, JsonSerializer.Serialize(store, _opts)); }
        catch { }
    }

    // Ship three starter environments so the user has something useful on first run
    private static HttpEnvironmentStore Seed() => new()
    {
        Environments =
        [
            new HttpEnvironment
            {
                Name = "Local",
                Variables =
                [
                    new EnvVariable { Key = "baseUrl",  Value = "http://localhost/BQECoreApi" },
                    new EnvVariable { Key = "apiKey",   Value = "" },
                    new EnvVariable { Key = "merchant", Value = "" },
                ]
            },
            new HttpEnvironment
            {
                Name = "Sandbox",
                Variables =
                [
                    new EnvVariable { Key = "baseUrl",  Value = "https://test.api.payrix.com" },
                    new EnvVariable { Key = "apiKey",   Value = "" },
                    new EnvVariable { Key = "merchant", Value = "" },
                ]
            },
            new HttpEnvironment
            {
                Name = "Production",
                Variables =
                [
                    new EnvVariable { Key = "baseUrl",  Value = "https://api.payrix.com" },
                    new EnvVariable { Key = "apiKey",   Value = "" },
                    new EnvVariable { Key = "merchant", Value = "" },
                ]
            }
        ]
    };

    /// <summary>
    /// Ensures "Local" environment exists in the store. Safe to call on every load —
    /// inserts at index 0 only if missing, then persists.
    /// </summary>
    public static void EnsureLocalEnvironment(HttpEnvironmentStore store)
    {
        var local = store.Environments.FirstOrDefault(e =>
            string.Equals(e.Name, "Local", StringComparison.OrdinalIgnoreCase));

        if (local == null)
        {
            // Add fresh Local environment at index 0
            store.Environments.Insert(0, new HttpEnvironment
            {
                Name = "Local",
                Variables =
                [
                    new EnvVariable { Key = "baseUrl",  Value = "http://localhost/BQECoreApi" },
                    new EnvVariable { Key = "apiKey",   Value = "" },
                    new EnvVariable { Key = "merchant", Value = "" },
                ]
            });
            Save(store);
        }
        else
        {
            // Fix stale localhost URL (e.g. old port 44301 placeholder)
            var baseUrlVar = local.Variables.FirstOrDefault(v => v.Key == "baseUrl");
            if (baseUrlVar != null &&
                (baseUrlVar.Value.Contains("44301") || baseUrlVar.Value.Contains("8080") || string.IsNullOrWhiteSpace(baseUrlVar.Value)))
            {
                baseUrlVar.Value = "http://localhost/BQECoreApi";
                Save(store);
            }
        }
    }
}
