using System.Windows;
using System.Windows.Input;

namespace PayrixLauncher;

public partial class InputDialog : Window
{
    public string InputText => InputBox.Text;

    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        InitializeComponent();
        Title           = title;
        PromptText.Text = prompt;
        InputBox.Text   = defaultValue;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)     => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)  DialogResult = true;
        if (e.Key == System.Windows.Input.Key.Escape) DialogResult = false;
    }
}
