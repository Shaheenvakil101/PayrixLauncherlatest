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
