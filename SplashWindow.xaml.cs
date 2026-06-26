using System.Windows;

namespace PayrixLauncher;

public partial class SplashWindow : Window
{
    public SplashWindow(string initialStatus = "Please wait…")
    {
        InitializeComponent();
        StatusText.Text = initialStatus;
    }

    public void SetStatus(string message)
    {
        Dispatcher.Invoke(() => StatusText.Text = message);
    }
}
