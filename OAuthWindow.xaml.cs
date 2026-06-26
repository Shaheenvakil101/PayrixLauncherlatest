using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace PayrixLauncher;

public partial class OAuthWindow : Window
{
    public string? Email    { get; private set; }
    public string? FullName { get; private set; }

    private readonly string  _clientId;
    private readonly string? _clientSecret;
    private readonly string  _authUrl;
    private readonly string  _tokenUrl;
    private readonly string  _scope;
    private readonly string  _codeVerifier;
    private readonly string  _redirectUri = "http://localhost:5678/callback";

    private static readonly HttpClient _http = new();

    public OAuthWindow(string provider, string clientId, string authUrl, string tokenUrl, string scope,
                       string? clientSecret = null)
    {
        InitializeComponent();
        TitleText.Text = $"Sign in with {provider}";
        _clientId      = clientId;
        _clientSecret  = clientSecret;
        _authUrl       = authUrl;
        _tokenUrl      = tokenUrl;
        _scope         = scope;
        _codeVerifier  = GenerateCodeVerifier();
        Loaded += OAuthWindow_Loaded;
    }

    private async void OAuthWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await WebView.EnsureCoreWebView2Async();
        var challenge = GenerateCodeChallenge(_codeVerifier);
        var url = $"{_authUrl}" +
                  $"?client_id={Uri.EscapeDataString(_clientId)}" +
                  $"&response_type=code" +
                  $"&redirect_uri={Uri.EscapeDataString(_redirectUri)}" +
                  $"&scope={Uri.EscapeDataString(_scope)}" +
                  $"&code_challenge={challenge}" +
                  $"&code_challenge_method=S256" +
                  $"&prompt=select_account";
        WebView.Source = new Uri(url);
    }

    private async void WebView_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith(_redirectUri, StringComparison.OrdinalIgnoreCase)) return;

        e.Cancel = true;

        var uri   = new Uri(e.Uri);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var code  = query["code"];
        var error = query["error_description"] ?? query["error"];

        if (string.IsNullOrEmpty(code))
        {
            System.Windows.MessageBox.Show(error ?? "Authentication was cancelled.", "Sign In Failed",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            return;
        }

        // Exchange code for tokens
        var (email, name, err) = await ExchangeCodeAsync(code);
        if (err is not null)
        {
            System.Windows.MessageBox.Show(err, "Sign In Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            return;
        }

        Email    = email;
        FullName = name;
        DialogResult = true;
    }

    private async Task<(string? email, string? name, string? error)> ExchangeCodeAsync(string code)
    {
        try
        {
            var body = new Dictionary<string, string>
            {
                ["grant_type"]    = "authorization_code",
                ["client_id"]     = _clientId,
                ["code"]          = code,
                ["redirect_uri"]  = _redirectUri,
                ["code_verifier"] = _codeVerifier
            };
            // Google (and some others) require client_secret even for desktop PKCE flows
            if (!string.IsNullOrEmpty(_clientSecret))
                body["client_secret"] = _clientSecret;

            using var resp = await _http.PostAsync(_tokenUrl, new FormUrlEncodedContent(body));
            var json = await resp.Content.ReadAsStringAsync();

            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!resp.IsSuccessStatusCode)
            {
                var msg = root.TryGetProperty("error_description", out var d) ? d.GetString() : json;
                return (null, null, msg ?? "Token exchange failed.");
            }

            // Decode id_token to get email + name
            if (root.TryGetProperty("id_token", out var idTokenProp))
            {
                var (email, name) = ParseIdToken(idTokenProp.GetString() ?? "");
                return (email, name, null);
            }

            // Fallback: use access_token userinfo endpoint (Google)
            if (root.TryGetProperty("access_token", out var atProp))
            {
                var (email, name) = await FetchUserInfoAsync(atProp.GetString() ?? "");
                return (email, name, null);
            }

            return (null, null, "No identity token returned.");
        }
        catch (Exception ex) { return (null, null, ex.Message); }
    }

    private static (string? email, string? name) ParseIdToken(string idToken)
    {
        try
        {
            var parts   = idToken.Split('.');
            if (parts.Length < 2) return (null, null);
            var payload = parts[1];
            // Pad base64url
            payload = payload.Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            var json    = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
            var name  = root.TryGetProperty("name",  out var n) ? n.GetString()
                      : root.TryGetProperty("given_name", out var g) ? g.GetString()
                      : null;
            return (email, name);
        }
        catch { return (null, null); }
    }

    private static async Task<(string? email, string? name)> FetchUserInfoAsync(string accessToken)
    {
        try
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var json = await _http.GetStringAsync("https://www.googleapis.com/oauth2/v3/userinfo");
            using var doc = JsonDocument.Parse(json);
            var root  = doc.RootElement;
            var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
            var name  = root.TryGetProperty("name",  out var n) ? n.GetString() : null;
            return (email, name);
        }
        catch { return (null, null); }
    }

    // ── PKCE helpers ─────────────────────────────────────────────────────────

    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // ── Window chrome ─────────────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseBtn_Click(object sender, MouseButtonEventArgs e)
        => DialogResult = false;
}
