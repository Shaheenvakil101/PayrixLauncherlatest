using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PayrixLauncher.Models;

namespace PayrixLauncher;

public partial class EnvironmentManagerDialog : Window
{
    private static readonly JsonSerializerOptions _pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ObservableCollection<HttpEnvironment> _envs;
    private bool _suppress;   // block change events while loading

    public EnvironmentManagerDialog(ObservableCollection<HttpEnvironment> envs)
    {
        InitializeComponent();
        _envs = envs;
        EnvListBox.ItemsSource = _envs;
        if (_envs.Count > 0) EnvListBox.SelectedIndex = 0;
    }

    // ── List selection ────────────────────────────────────────────────────────

    private void EnvList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var env = EnvListBox.SelectedItem as HttpEnvironment;

        _suppress = true;

        VarsGrid.ItemsSource  = env?.Variables;
        EnvNameBox.Text       = env?.Name ?? "";
        EnvNameBox.IsEnabled  = env != null;
        VarsGrid.IsEnabled    = env != null;

        // Load auth settings
        if (env != null)
        {
            EnvAuthTypeBox.SelectedIndex      = env.AuthTypeIndex;
            EnvApiKeyNameBox.Text             = env.AuthApiKeyName;
            EnvApiKeyValueBox.Text            = env.AuthApiKeyValue;
            EnvApiKeyLocationBox.SelectedIndex = env.AuthApiKeyLocation;
            EnvBearerTokenBox.Text            = env.AuthBearerToken;
            EnvBasicUserBox.Text              = env.AuthBasicUser;
            EnvBasicPassBox.Text              = env.AuthBasicPass;
            UpdateAuthPanels(env.AuthTypeIndex);
        }
        else
        {
            EnvAuthTypeBox.SelectedIndex = 0;
            UpdateAuthPanels(0);
        }

        EnvApiKeyPanel.IsEnabled  = env != null;
        EnvBearerPanel.IsEnabled  = env != null;
        EnvBasicPanel.IsEnabled   = env != null;
        EnvAuthTypeBox.IsEnabled  = env != null;

        _suppress = false;
    }

    // ── Name editing ──────────────────────────────────────────────────────────

    private void EnvName_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (EnvListBox.SelectedItem is HttpEnvironment env)
            env.Name = EnvNameBox.Text;
    }

    // ── Variables grid ────────────────────────────────────────────────────────

    private void Vars_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) { /* bindings handle it */ }

    // ── Auth type selector ────────────────────────────────────────────────────

    private void EnvAuthType_Changed(object sender, SelectionChangedEventArgs e)
    {
        var idx = EnvAuthTypeBox.SelectedIndex;
        UpdateAuthPanels(idx);
        if (_suppress) return;
        if (EnvListBox.SelectedItem is HttpEnvironment env)
            env.AuthTypeIndex = idx;
    }

    private void UpdateAuthPanels(int idx)
    {
        // Guard: panels are defined after EnvAuthTypeBox in XAML, so they may be null
        // during InitializeComponent() when the ComboBox fires SelectionChanged early.
        if (EnvApiKeyPanel == null || EnvBearerPanel == null || EnvBasicPanel == null) return;
        EnvApiKeyPanel.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        EnvBearerPanel.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        EnvBasicPanel.Visibility  = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Auth field edits ──────────────────────────────────────────────────────

    private void EnvAuth_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (EnvListBox.SelectedItem is not HttpEnvironment env) return;

        env.AuthApiKeyName     = EnvApiKeyNameBox.Text;
        env.AuthApiKeyValue    = EnvApiKeyValueBox.Text;
        env.AuthApiKeyLocation = EnvApiKeyLocationBox.SelectedIndex;
        env.AuthBearerToken    = EnvBearerTokenBox.Text;
        env.AuthBasicUser      = EnvBasicUserBox.Text;
        env.AuthBasicPass      = EnvBasicPassBox.Text;
    }

    // ── Add / Dup / Delete ────────────────────────────────────────────────────

    private void AddEnv_Click(object sender, RoutedEventArgs e)
    {
        var env = new HttpEnvironment { Name = "New Environment" };
        env.Variables.Add(new EnvVariable { Key = "baseUrl", Value = "" });
        _envs.Add(env);
        EnvListBox.SelectedItem = env;
        EnvNameBox.Focus();
        EnvNameBox.SelectAll();
    }

    private void DupEnv_Click(object sender, RoutedEventArgs e)
    {
        if (EnvListBox.SelectedItem is not HttpEnvironment src) return;
        var copy = new HttpEnvironment
        {
            Name              = src.Name + " (copy)",
            AuthTypeIndex     = src.AuthTypeIndex,
            AuthApiKeyName    = src.AuthApiKeyName,
            AuthApiKeyValue   = src.AuthApiKeyValue,
            AuthApiKeyLocation= src.AuthApiKeyLocation,
            AuthBearerToken   = src.AuthBearerToken,
            AuthBasicUser     = src.AuthBasicUser,
            AuthBasicPass     = src.AuthBasicPass,
        };
        foreach (var v in src.Variables)
            copy.Variables.Add(new EnvVariable { Enabled = v.Enabled, Key = v.Key, Value = v.Value });
        _envs.Add(copy);
        EnvListBox.SelectedItem = copy;
    }

    private void DeleteEnv_Click(object sender, RoutedEventArgs e)
    {
        if (EnvListBox.SelectedItem is not HttpEnvironment env) return;
        if (System.Windows.MessageBox.Show($"Delete environment '{env.Name}'?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _envs.Remove(env);
    }

    // ── Import ────────────────────────────────────────────────────────────────

    private void ImportEnv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Import Environment",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            HttpEnvironment? env = null;

            try { env = JsonSerializer.Deserialize<HttpEnvironment>(json); } catch { }

            if (env == null || string.IsNullOrWhiteSpace(env.Name))
            {
                var doc  = JsonDocument.Parse(json);
                var root = doc.RootElement;
                env = new HttpEnvironment
                {
                    Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "Imported" : "Imported"
                };
                if (root.TryGetProperty("values", out var vals))
                {
                    foreach (var item in vals.EnumerateArray())
                    {
                        env.Variables.Add(new EnvVariable
                        {
                            Key     = item.TryGetProperty("key",     out var k)  ? k.GetString()  ?? "" : "",
                            Value   = item.TryGetProperty("value",   out var v)  ? v.GetString()  ?? "" : "",
                            Enabled = !item.TryGetProperty("enabled", out var en) || en.GetBoolean()
                        });
                    }
                }
            }

            if (env == null) { System.Windows.MessageBox.Show("Could not parse file.", "Import Failed"); return; }
            env.Id = Guid.NewGuid().ToString("N");
            _envs.Add(env);
            EnvListBox.SelectedItem = env;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Import failed: {ex.Message}", "Error");
        }
    }

    // ── Export ────────────────────────────────────────────────────────────────

    private void ExportEnv_Click(object sender, RoutedEventArgs e)
    {
        if (EnvListBox.SelectedItem is not HttpEnvironment env) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export Environment",
            Filter     = "JSON files (*.json)|*.json",
            DefaultExt = ".json",
            FileName   = env.Name.Replace(" ", "_")
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(env, _pretty));
    }

    // ── Chrome ────────────────────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
