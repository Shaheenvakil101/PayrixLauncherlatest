using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;

namespace PayrixLauncher;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            var msg = $"[{DateTime.Now}] DISPATCHER: {e.Exception}\n\n";
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), msg);
            System.Windows.MessageBox.Show(e.Exception.Message, "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var msg = $"[{DateTime.Now}] UNHANDLED: {e.ExceptionObject}\n\n";
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), msg);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // ── Ensure WebView2 Runtime is installed ─────────────────────────────
        // Required for the embedded Payrix portal browser.
        // Silently downloads + installs the Microsoft bootstrapper if missing.
        EnsureWebView2RuntimeAsync().GetAwaiter().GetResult();

        try
        {
            var settings = Services.SettingsService.Load();
            string email, name;

            if (!string.IsNullOrWhiteSpace(settings.UserEmail))
            {
                email = settings.UserEmail;
                var local = email.Split('@')[0];
                name = string.Join(" ", local.Split('.').Select(p =>
                    p.Length > 0 ? char.ToUpper(p[0]) + p[1..].ToLower() : p));
                if (string.IsNullOrWhiteSpace(name)) name = local;
            }
            else
            {
                var raw   = System.Security.Principal.WindowsIdentity.GetCurrent().Name ?? "";
                var parts = raw.Split('\\');
                var user  = parts.Length > 1 ? parts[1] : parts[0];
                email = $"{user.ToLower()}@bqe.com";
                name  = string.Join(" ", user.Split('.').Select(p =>
                    p.Length > 0 ? char.ToUpper(p[0]) + p[1..].ToLower() : p));
            }

            var main = new MainWindow(name, email);
            System.Windows.Application.Current.MainWindow = main;
            main.Show();
        }
        catch (Exception ex)
        {
            var msg = $"[{DateTime.Now}] STARTUP: {ex}\n\n";
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), msg);
            System.Windows.MessageBox.Show(ex.Message, "Startup Error");
            Shutdown();
        }
    }

    // ── WebView2 Runtime auto-installer ──────────────────────────────────────
    // Checks if the Evergreen WebView2 runtime is installed.
    // If not, downloads the official Microsoft bootstrapper (~2 MB) and runs it silently.
    // The bootstrapper auto-detects the machine architecture and installs the correct runtime.
    private static async Task EnsureWebView2RuntimeAsync()
    {
        // Fast check: registry key written by WebView2 installer
        if (IsWebView2Installed()) return;

        var result = System.Windows.MessageBox.Show(
            "This tool requires the Microsoft WebView2 Runtime (free, ~120 MB).\n\n" +
            "It will be downloaded and installed automatically.\n\n" +
            "Click OK to install now, or Cancel to skip (portal browser won't work).",
            "WebView2 Runtime Required",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        if (result != MessageBoxResult.OK) return;

        var splash = new SplashWindow("Installing WebView2 Runtime…\nThis may take a minute.");
        splash.Show();

        try
        {
            // Official Microsoft Evergreen bootstrapper URL
            const string bootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
            var tmpPath = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");

            splash.SetStatus("Downloading WebView2 installer…");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                var bytes = await http.GetByteArrayAsync(bootstrapperUrl).ConfigureAwait(true);
                await File.WriteAllBytesAsync(tmpPath, bytes).ConfigureAwait(true);
            }

            splash.SetStatus("Installing WebView2 Runtime… (please wait)");
            var psi = new ProcessStartInfo(tmpPath, "/silent /install")
            {
                UseShellExecute  = true,
                Verb             = "runas",   // elevate so installer can write to Program Files
                CreateNoWindow   = true
            };
            using var proc = Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync().ConfigureAwait(true);

            try { File.Delete(tmpPath); } catch { /* ignore */ }

            splash.Close();

            if (!IsWebView2Installed())
            {
                System.Windows.MessageBox.Show(
                    "WebView2 installation may not have completed.\n" +
                    "The portal browser tab may not work until you install it manually from:\n" +
                    "https://developer.microsoft.com/en-us/microsoft-edge/webview2/",
                    "WebView2 Install Notice",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            splash.Close();
            System.Windows.MessageBox.Show(
                $"Could not install WebView2 automatically:\n{ex.Message}\n\n" +
                "Download manually from:\nhttps://developer.microsoft.com/en-us/microsoft-edge/webview2/",
                "WebView2 Install Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static bool IsWebView2Installed()
    {
        // Check both HKLM (machine-wide) and HKCU (per-user) installs
        string[] regPaths =
        [
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{2CD8A007-E189-409D-A2C8-9AF4EF3C72AA}",
        ];

        foreach (var path in regPaths)
        {
            foreach (var hive in new[] {
                Microsoft.Win32.Registry.LocalMachine,
                Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    using var key = hive.OpenSubKey(path);
                    if (key?.GetValue("pv") is string ver && !string.IsNullOrEmpty(ver) && ver != "0.0.0.0")
                        return true;
                }
                catch { /* registry access denied — assume not installed */ }
            }
        }

        // Also try instantiating the WebView2 environment as a final check
        try
        {
            var ver = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(ver);
        }
        catch { return false; }
    }
}
