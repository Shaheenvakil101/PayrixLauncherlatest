using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PayrixLauncher.Models;

public class EnvVariable : INotifyPropertyChanged
{
    private bool   _enabled = true;
    private string _key     = "";
    private string _value   = "";

    public bool   Enabled { get => _enabled; set { _enabled = value; OnPC(); } }
    public string Key     { get => _key;     set { _key     = value; OnPC(); } }
    public string Value   { get => _value;   set { _value   = value; OnPC(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class HttpEnvironment : INotifyPropertyChanged
{
    private string _name = "New Environment";

    public string Id   { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set { _name = value; OnPC(); } }
    public ObservableCollection<EnvVariable> Variables { get; set; } = [];

    // ── Auth settings (stored per environment) ────────────────────────────────
    // 0 = No Auth  1 = API Key  2 = Bearer  3 = Basic
    public int    AuthTypeIndex      { get; set; } = 0;

    // API Key
    public string AuthApiKeyName     { get; set; } = "APIKEY";
    public string AuthApiKeyValue    { get; set; } = "";
    // 0 = Header  1 = Query Params
    public int    AuthApiKeyLocation { get; set; } = 0;

    // Bearer
    public string AuthBearerToken    { get; set; } = "";

    // Basic
    public string AuthBasicUser      { get; set; } = "";
    public string AuthBasicPass      { get; set; } = "";

    // ── Last-used request state (restored when switching to this environment) ──
    public string LastUrl    { get; set; } = "";
    public string LastMethod { get; set; } = "GET";

    public string? Resolve(string key) =>
        Variables.FirstOrDefault(v => v.Enabled && v.Key == key)?.Value;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class HttpEnvironmentStore
{
    public string               ActiveId     { get; set; } = "";
    public List<HttpEnvironment> Environments { get; set; } = [];
}
