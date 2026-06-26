using System.Net;
using System.Net.Http;

namespace PayrixLauncher.Services;

/// <summary>
/// Shared HTTP proxy configuration.
/// When Fiddler (or any proxy) is enabled, all HttpClient instances in the app
/// route their traffic through it — letting you inspect Payrix API calls,
/// BQE Core requests, etc. in real time.
///
/// Default:  http://localhost:8888  (Fiddler's default listener)
/// </summary>
public static class ProxyConfig
{
    private static bool   _enabled     = false;
    private static string _proxyUrl    = "http://localhost:8888";
    private static bool   _bypassLocal = false;

    /// <summary>Apply settings loaded from AppSettings.</summary>
    public static void Apply(Models.AppSettings s)
    {
        var url = string.IsNullOrWhiteSpace(s.ProxyUrl) ? "http://localhost:8888" : s.ProxyUrl.Trim();

        // Validate: a proxy URL must have an explicit port (e.g. :8888).
        bool validProxy = false;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            validProxy = uri.IsDefaultPort == false && string.IsNullOrEmpty(uri.PathAndQuery.TrimStart('/'));

        _enabled     = s.ProxyEnabled && validProxy;
        _proxyUrl    = url;
        _bypassLocal = s.ProxyBypassLocal;

        LastValidationError = (s.ProxyEnabled && !validProxy)
            ? $"Proxy URL '{url}' doesn't look like a proxy — expected format: http://localhost:8888"
            : null;

        // Apply as the PROCESS-WIDE default proxy so ALL outgoing HTTP traffic
        // (PayrixService, BqeAuthService, HTTP Client tab, WebView2, etc.) goes through Fiddler.
        ApplySystemProxy();
    }

    /// <summary>
    /// Sets or clears the process-wide default HTTP proxy.
    /// Affects all HttpClient / HttpWebRequest / WebClient instances that
    /// don't override their proxy explicitly.
    /// </summary>
    private static void ApplySystemProxy()
    {
        if (_enabled)
        {
            try
            {
                var proxy = new WebProxy(_proxyUrl, _bypassLocal)
                {
                    // Accept Fiddler's self-signed certificate for HTTPS interception
                    BypassList = _bypassLocal ? new[] { "localhost", "127\\..*" } : Array.Empty<string>()
                };
                System.Net.WebRequest.DefaultWebProxy = proxy;
            }
            catch { /* invalid URL — leave proxy unchanged */ }
        }
        else
        {
            // Restore system default (no proxy)
            System.Net.WebRequest.DefaultWebProxy = System.Net.WebRequest.GetSystemWebProxy();
        }
    }

    /// <summary>Non-null when proxy was requested but the URL was rejected as invalid.</summary>
    public static string? LastValidationError { get; private set; }

    /// <summary>
    /// Returns an HttpClientHandler configured for the current proxy settings.
    /// Always disables SSL certificate validation (required for Fiddler HTTPS interception).
    /// </summary>
    public static HttpClientHandler MakeHandler()
    {
        var handler = new HttpClientHandler
        {
            // Accept any certificate — required for Fiddler HTTPS interception
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            // UseProxy=true inherits the system default proxy set by ApplySystemProxy()
            UseProxy = _enabled
        };

        if (_enabled && !string.IsNullOrWhiteSpace(_proxyUrl))
        {
            try
            {
                handler.Proxy = new WebProxy(_proxyUrl, _bypassLocal);
            }
            catch { /* invalid URL — fall back to system proxy */ }
        }

        return handler;
    }

    public static bool   IsEnabled  => _enabled;
    public static string CurrentUrl => _proxyUrl;
}
