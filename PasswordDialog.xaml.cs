using System.Windows;
using System.Windows.Input;

namespace PayrixLauncher;

public partial class PasswordDialog : Window
{
    // The correct production password.
    private const string ProductionPassword = "BQEProd@2024";

    public bool Confirmed { get; private set; }

    public PasswordDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e) => TryConfirm();
    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void PasswordBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryConfirm();
        if (e.Key == Key.Escape) DialogResult = false;
    }

    private void TryConfirm()
    {
        if (PasswordBox.Password == ProductionPassword)
        {
            Confirmed = true;
            DialogResult = true;
        }
        else
        {
            ErrorText.Visibility = Visibility.Visible;
            PasswordBox.Clear();
            PasswordBox.Focus();
        }
    }
}
