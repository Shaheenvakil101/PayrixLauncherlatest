using System.IO;
using System.Text.Json;
using PayrixLauncher.Models;

namespace PayrixLauncher.Services;

public static class SettingsService
{
    // Store settings next to the EXE so they survive republish and are easy to find/edit.
    // For a single-file publish, AppContext.BaseDirectory is the folder containing PayrixTools.exe.
    public static readonly string SettingsFilePath =
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static readonly string SettingsFile = SettingsFilePath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch { /* ignore — return defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch { /* best-effort */ }
    }
}
