using System.Security.Principal;
using System.Windows;
using System.Windows.Input;

namespace PayrixLauncher;

public partial class LoginWindow : Window
{
    public string LoggedInName  { get; private set; } = "User";
    public string LoggedInEmail { get; private set; } = "";

    private bool _showingPlain = false;

    private const string BqeTenant = "bqe.com";

    // OAuth endpoints
    private static class OAuth
    {
        public const string GoogleAuth    = "https://accounts.google.com/o/oauth2/v2/auth";
        public const string GoogleToken   = "https://oauth2.googleapis.com/token";
        public const string GoogleScope   = "openid email profile";

        public const string MicrosoftAuth  = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
        public const string MicrosoftToken = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
        public const string MicrosoftScope = "openid email profile";

        public const string AppleAuth  = "https://appleid.apple.com/auth/authorize";
        public const string AppleToken = "https://appleid.apple.com/auth/token";
        public const string AppleScope = "openid email name";
    }

    public LoginWindow()
    {
        InitializeComponent();
        PreFillEmail();
        ManualEmailBox.Focus();
    }

    // ── Pre-fill from Windows identity ───────────────────────────────────────

    private void PreFillEmail()
    {
        try
        {
            var raw      = WindowsIdentity.GetCurrent().Name ?? "";
            var parts    = raw.Split('\\');
            var username = parts.Length > 1 ? parts[1] : parts[0];
            ManualEmailBox.Text = $"{username.ToLower()}@{BqeTenant}";
            var nameParts = username.Split('.');
            LoggedInName = string.Join(" ", nameParts.Select(p =>
                p.Length > 0 ? char.ToUpper(p[0]) + p[1..].ToLower() : p));
        }
        catch { /* leave blank */ }
    }

    // ── Field events ─────────────────────────────────────────────────────────

    private void EmailBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateSignInButton(); HideMessage();
    }

    private void WinPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_showingPlain) WinPasswordPlainBox.Text = WinPasswordBox.Password;
        UpdateSignInButton(); HideMessage();
    }

    private void UpdateSignInButton() =>
        SsoSignInBtn.IsEnabled =
            !string.IsNullOrWhiteSpace(ManualEmailBox.Text) &&
            !string.IsNullOrEmpty(GetPassword());

    // ── Eye toggle ────────────────────────────────────────────────────────────

    private void EyeToggle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _showingPlain = !_showingPlain;
        if (_showingPlain)
        {
            WinPasswordPlainBox.Text       = WinPasswordBox.Password;
            WinPasswordBox.Visibility      = Visibility.Collapsed;
            WinPasswordPlainBox.Visibility = Visibility.Visible;
            WinPasswordPlainBox.CaretIndex = WinPasswordPlainBox.Text.Length;
            WinPasswordPlainBox.Focus();
        }
        else
        {
            WinPasswordBox.Password        = WinPasswordPlainBox.Text;
            WinPasswordPlainBox.Visibility = Visibility.Collapsed;
            WinPasswordBox.Visibility      = Visibility.Visible;
            WinPasswordBox.Focus();
        }
    }

    private string GetPassword() =>
        _showingPlain ? WinPasswordPlainBox.Text : WinPasswordBox.Password;

    // ── Forgot password ───────────────────────────────────────────────────────

    private void ForgotPassword_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (System.IO.File.Exists(HashFile))
                System.IO.File.Delete(HashFile);
        }
        catch { /* ignore */ }

        WinPasswordBox.Clear();
        WinPasswordPlainBox.Clear();
        ShowMessage("Password reset. Enter a new password to set it.", isError: false);
        WinPasswordBox.Focus();
    }

    // ── Email / password sign in ──────────────────────────────────────────────

    private void SsoSignIn_Click(object sender, RoutedEventArgs e) => TrySignIn();

    private void Email_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Tab)
        {
            WinPasswordBox.Focus();
            e.Handled = true;
        }
    }

    private void Password_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SsoSignInBtn.IsEnabled) TrySignIn();
    }

    private void TrySignIn()
    {
        var email = ManualEmailBox.Text.Trim();
        var pwd   = GetPassword();

        if (string.IsNullOrWhiteSpace(email)) { ShowMessage("Please enter your email.", isError: true); ManualEmailBox.Focus(); return; }
        if (string.IsNullOrEmpty(pwd))        { ShowMessage("Please enter your password.", isError: true); WinPasswordBox.Focus(); return; }

        var enteredHash = Services.CryptoService.Sha256Hash(pwd);
        var storedHash  = LoadStoredHash();

        if (storedHash is null)
        {
            SaveStoredHash(enteredHash);
            SetIdentity(email);
            DialogResult = true;
        }
        else if (string.Equals(enteredHash, storedHash, StringComparison.OrdinalIgnoreCase))
        {
            SetIdentity(email);
            DialogResult = true;
        }
        else
        {
            ShowMessage("Incorrect password. Please try again.", isError: true);
            WinPasswordBox.Clear();
            WinPasswordPlainBox.Clear();
            UpdateSignInButton();
            (_showingPlain ? (UIElement)WinPasswordPlainBox : WinPasswordBox).Focus();
        }
    }

    // ── Social login ─────────────────────────────────────────────────────────

    private void GoogleLogin_Click(object sender, RoutedEventArgs e)
    {
        var settings = Services.SettingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.GoogleClientId))
        {
            ShowMessage("Google client ID not configured.\nAdd GoogleClientId to Settings → OAuth.", isError: true);
            return;
        }
        OpenOAuth("Google", settings.GoogleClientId,
                  OAuth.GoogleAuth, OAuth.GoogleToken, OAuth.GoogleScope,
                  clientSecret: settings.GoogleClientSecret);
    }

    private void MicrosoftLogin_Click(object sender, RoutedEventArgs e)
    {
        var settings = Services.SettingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.MicrosoftClientId))
        {
            ShowMessage("Microsoft client ID not configured.\nAdd MicrosoftClientId to Settings → OAuth.", isError: true);
            return;
        }
        OpenOAuth("Microsoft", settings.MicrosoftClientId,
                  OAuth.MicrosoftAuth, OAuth.MicrosoftToken, OAuth.MicrosoftScope);
    }

    private void AppleLogin_Click(object sender, RoutedEventArgs e)
    {
        var settings = Services.SettingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.AppleClientId))
        {
            ShowMessage("Apple client ID not configured.\nAdd AppleClientId to Settings → OAuth.", isError: true);
            return;
        }
        OpenOAuth("Apple", settings.AppleClientId,
                  OAuth.AppleAuth, OAuth.AppleToken, OAuth.AppleScope);
    }

    private void OpenOAuth(string provider, string clientId,
                           string authUrl, string tokenUrl, string scope,
                           string? clientSecret = null)
    {
        var win = new OAuthWindow(provider, clientId, authUrl, tokenUrl, scope, clientSecret)
        {
            Owner = this
        };
        var result = win.ShowDialog();
        if (result == true && win.Email is not null)
        {
            SetIdentity(win.Email, win.FullName);
            DialogResult = true;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetIdentity(string email, string? displayName = null)
    {
        LoggedInEmail = email;
        if (displayName is not null)
        {
            LoggedInName = displayName;
        }
        else
        {
            var local = email.Split('@')[0];
            LoggedInName = string.Join(" ", local.Split('.').Select(p =>
                p.Length > 0 ? char.ToUpper(p[0]) + p[1..].ToLower() : p));
            if (string.IsNullOrWhiteSpace(LoggedInName)) LoggedInName = local;
        }
    }

    // ── Hash storage ──────────────────────────────────────────────────────────

    private static string HashFile =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "auth.dat");

    private static string? LoadStoredHash()
    {
        try
        {
            if (!System.IO.File.Exists(HashFile)) return null;
            var h = System.IO.File.ReadAllText(HashFile).Trim();
            return Services.CryptoService.IsValidSha256Hash(h) ? h : null;
        }
        catch { return null; }
    }

    private static void SaveStoredHash(string hash)
    {
        try { System.IO.File.WriteAllText(HashFile, hash); } catch { }
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void ShowMessage(string msg, bool isError)
    {
        AuthErrorText.Text       = msg;
        AuthErrorText.Foreground = isError
            ? System.Windows.Media.Brushes.Red
            : System.Windows.Media.Brushes.Green;
        AuthErrorText.Visibility = Visibility.Visible;
    }

    private void HideMessage()
    {
        AuthErrorText.Text       = "";
        AuthErrorText.Visibility = Visibility.Collapsed;
    }

    private void SetBusy(bool busy)
    {
        SsoSignInBtn.IsEnabled        = !busy;
        SignInBtnText.Text            = busy ? "Signing in…" : "Sign In";
        ManualEmailBox.IsEnabled      = !busy;
        WinPasswordBox.IsEnabled      = !busy;
        WinPasswordPlainBox.IsEnabled = !busy;
    }

    // ── Window chrome ─────────────────────────────────────────────────────────

    private void CloseBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DialogResult = false;

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
