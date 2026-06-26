using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PayrixLauncher.Models;

// ── Key/value row (Params, Headers, Form) ────────────────────────────────────

public class HttpKeyValueRow : INotifyPropertyChanged
{
    private bool   _enabled = true;
    private string _key     = "";
    private string _value   = "";
    private string _desc    = "";

    public bool   Enabled     { get => _enabled; set { _enabled = value; OnPC(); } }
    public string Key         { get => _key;     set { _key     = value; OnPC(); } }
    public string Value       { get => _value;   set { _value   = value; OnPC(); } }
    public string Description { get => _desc;    set { _desc    = value; OnPC(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// ── Response header row ───────────────────────────────────────────────────────

public class HttpHeaderEntry
{
    public string Key   { get; init; } = "";
    public string Value { get; init; } = "";
}

// ── Saved request (inside a collection) ──────────────────────────────────────

public class SavedHttpRequest : INotifyPropertyChanged
{
    private string _name = "";

    public string Id      { get; set; } = Guid.NewGuid().ToString("N");
    public string Method  { get; set; } = "GET";
    public string Url     { get; set; } = "";
    public string Body    { get; set; } = "";
    public string AuthType     { get; set; } = "No Auth";
    public string AuthKey      { get; set; } = "";
    public string AuthValue    { get; set; } = "";
    public string AuthLocation { get; set; } = "Header";
    public string Name
    {
        get => _name;
        set { _name = value; OnPC(); OnPC(nameof(Category)); OnPC(nameof(ShortName)); }
    }

    public List<HttpKeyValueRow> Params  { get; set; } = [];
    public List<HttpKeyValueRow> Headers { get; set; } = [];
    public List<HttpKeyValueRow> Form    { get; set; } = [];

    // Derived — not serialised; used by the sidebar display

    /// <summary>Prefix before " | " e.g. "Budget" from "Budget | Budget List". Empty if no pipe.</summary>
    public string Category  => Name.Contains(" | ") ? Name.Split(" | ")[0].Trim() : "";

    /// <summary>Part after " | " e.g. "Budget List". Full name if no pipe.</summary>
    public string ShortName => Name.Contains(" | ") ? Name.Split(" | ")[1].Trim() : Name;

    public string MethodColor => Method switch
    {
        "POST"    => "#F59E0B",
        "PUT"     => "#3B82F6",
        "PATCH"   => "#8B5CF6",
        "DELETE"  => "#EF4444",
        "HEAD"    => "#6B7280",
        "OPTIONS" => "#6B7280",
        _         => "#22C55E"
    };

    public string MethodBadgeBg => Method switch
    {
        "POST"    => "#2A1C00",
        "PUT"     => "#001A30",
        "PATCH"   => "#140A28",
        "DELETE"  => "#2A0505",
        "HEAD"    => "#1A1A1A",
        "OPTIONS" => "#1A1A1A",
        _         => "#062010"   // GET
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// ── Request group (folder inside a collection) ────────────────────────────────

public class RequestGroup : INotifyPropertyChanged
{
    private bool _expanded = true;

    public string Category { get; set; } = "";
    public bool Expanded
    {
        get => _expanded;
        set { _expanded = value; OnPC(); }
    }

    public ObservableCollection<SavedHttpRequest> Requests { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// ── Collection ────────────────────────────────────────────────────────────────

public class HttpCollection : INotifyPropertyChanged
{
    private string _name = "New Collection";
    private bool   _expanded = true;
    private bool   _isFavorite = false;

    public string Id   { get; set; } = Guid.NewGuid().ToString("N");
    public string Name
    {
        get => _name;
        set { _name = value; OnPC(); }
    }
    public bool Expanded
    {
        get => _expanded;
        set { _expanded = value; OnPC(); }
    }
    public bool IsFavorite
    {
        get => _isFavorite;
        set { _isFavorite = value; OnPC(); OnPC(nameof(FavoriteIcon)); OnPC(nameof(FavoriteTip)); }
    }
    public string FavoriteIcon => _isFavorite ? "★" : "☆";
    public string FavoriteTip  => _isFavorite ? "Remove from favourites" : "Add to favourites";

    // ── Requests collection — invalidates grouped cache on change ────────────
    private ObservableCollection<SavedHttpRequest> _requests = [];

    public ObservableCollection<SavedHttpRequest> Requests
    {
        get => _requests;
        set
        {
            if (_requests != null)
                _requests.CollectionChanged -= InvalidateGroupedCache;
            _requests = value ?? [];
            _requests.CollectionChanged += InvalidateGroupedCache;
            InvalidateGroupedCache(null, null!);
            OnPC();
        }
    }

    public HttpCollection()
    {
        _requests.CollectionChanged += InvalidateGroupedCache;
    }

    private void InvalidateGroupedCache(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs? e)
    {
        _groupedCache = null;
        // Only raise the property-changed if we already have a live binding
        // (avoids spurious rebuilds during JSON deserialization)
        if (_hasGroupedBinding) OnPC(nameof(GroupedRequests));
    }

    // ── Grouped view (not serialised) ─────────────────────────────────────────
    // Cached: rebuilt lazily only after Requests change or explicit invalidation.
    private ObservableCollection<RequestGroup>? _groupedCache;
    private bool _hasGroupedBinding;
    private readonly Dictionary<string, bool> _groupExpanded = [];

    /// <summary>
    /// Returns requests grouped by their Category prefix, in the order the
    /// first request of each category appears.  Ungrouped requests (no pipe in
    /// name) land in a group with an empty Category string.
    /// The result is CACHED — only rebuilt when Requests changes or
    /// <see cref="RaiseGroupedRequestsChanged"/> is called.
    /// </summary>
    public ObservableCollection<RequestGroup> GroupedRequests
    {
        get
        {
            _hasGroupedBinding = true;
            if (_groupedCache != null) return _groupedCache;

            var groups = new ObservableCollection<RequestGroup>();
            var seen   = new Dictionary<string, RequestGroup>(StringComparer.Ordinal);

            foreach (var req in _requests)
            {
                var cat = req.Category;
                if (!seen.TryGetValue(cat, out var g))
                {
                    g = new RequestGroup
                    {
                        Category = cat,
                        Expanded = _groupExpanded.TryGetValue(cat, out var ex) ? ex : true
                    };
                    seen[cat] = g;
                    groups.Add(g);
                }
                g.Requests.Add(req);
            }
            _groupedCache = groups;
            return _groupedCache;
        }
    }

    /// <summary>Call when the user toggles a group header to persist expand state.</summary>
    public void SetGroupExpanded(string category, bool expanded)
    {
        _groupExpanded[category] = expanded;
    }

    /// <summary>Invalidates the grouped-requests cache and notifies the UI to re-bind.</summary>
    public void RaiseGroupedRequestsChanged()
    {
        _groupedCache = null;
        OnPC(nameof(GroupedRequests));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// ── Root store (one file on disk) ─────────────────────────────────────────────

public class HttpCollectionStore
{
    public List<HttpCollection> Collections { get; set; } = [];
}

// ── Request log entry (saved to request_log.json) ────────────────────────────

public class RequestLogEntry
{
    public string Url       { get; set; } = "";
    public string Method    { get; set; } = "GET";
    public string Token     { get; set; } = "";   // bearer token or empty
    public string AuthType  { get; set; } = "";
    public string SavedAt   { get; set; } = "";   // ISO 8601

    // Deduplication key — same token + URL + method = same entry
    public string Key => $"{Method}|{Url}|{Token}";

    // Display helpers (not serialised)
    /// <summary>Shows just the path portion so the sidebar doesn't overflow with base URLs.</summary>
    public string DisplayUrl
    {
        get
        {
            try
            {
                var u = new Uri(Url);
                // show path + query, drop scheme+host if the URL is parseable
                var pq = u.PathAndQuery;
                return string.IsNullOrEmpty(pq) || pq == "/" ? Url : pq;
            }
            catch { return Url; }
        }
    }

    public string MethodColor => Method switch
    {
        "POST"    => "#F59E0B",
        "PUT"     => "#3B82F6",
        "PATCH"   => "#8B5CF6",
        "DELETE"  => "#EF4444",
        "HEAD"    => "#6B7280",
        "OPTIONS" => "#6B7280",
        _         => "#22C55E"
    };

    public string MethodBadgeBg => Method switch
    {
        "POST"    => "#2A1C00",
        "PUT"     => "#001A30",
        "PATCH"   => "#140A28",
        "DELETE"  => "#2A0505",
        "HEAD"    => "#1A1A1A",
        "OPTIONS" => "#1A1A1A",
        _         => "#062010"
    };
}
