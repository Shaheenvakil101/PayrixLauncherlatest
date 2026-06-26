using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PayrixLauncher.Models;

// ── Configuration ─────────────────────────────────────────────────────────────

public class PerfTestConfig
{
    public string CollectionId      { get; set; } = "";
    public List<string> EnvIds      { get; set; } = [];
    public int    Iterations        { get; set; } = 10;
    public int    Concurrency       { get; set; } = 1;
    public int    DelayMs           { get; set; } = 0;
    public int    TimeoutMs         { get; set; } = 10_000;
    public bool   StopOnFirstError  { get; set; } = false;
}

// ── A single request result ───────────────────────────────────────────────────

public class PerfRequestResult
{
    public int    Iteration      { get; init; }
    public string Environment    { get; init; } = "";
    public string RequestName    { get; init; } = "";
    public string Method         { get; init; } = "";
    public string Url            { get; init; } = "";
    public int    StatusCode     { get; init; }
    public long   ElapsedMs      { get; init; }
    public long   TtfbMs         { get; init; }   // Time to First Byte (headers received)
    public long   DownloadMs     { get; init; }   // Content download time
    public int    ResponseBytes  { get; init; }
    public int    RedirectCount  { get; init; }   // HTTP redirects followed
    public string ContentType    { get; init; } = "";
    public bool   Success        { get; init; }
    public string Error          { get; init; } = "";

    public string StatusDisplay   => Success ? StatusCode.ToString() : (StatusCode > 0 ? StatusCode.ToString() : "ERR");
    public string StatusColor     => Success ? "#22C55E" : "#EF4444";
    public string ElapsedDisplay  => $"{ElapsedMs} ms";
    public string TtfbDisplay     => TtfbMs   > 0 ? $"{TtfbMs} ms"   : "—";
    public string DownloadDisplay => DownloadMs > 0 ? $"{DownloadMs} ms" : "—";
    public string SizeDisplay     => ResponseBytes < 1024 ? $"{ResponseBytes} B" : $"{ResponseBytes / 1024.0:F1} KB";
    public string RedirectDisplay => RedirectCount > 0 ? RedirectCount.ToString() : "—";
}

// ── Per-request aggregate stats ───────────────────────────────────────────────

public class PerfRequestStat : INotifyPropertyChanged
{
    private long _min, _max, _avg, _p95, _p99, _avgTtfb;
    private int  _total, _passed, _failed;
    private double _rps;

    public string RequestName { get; init; } = "";
    public string Environment { get; init; } = "";

    public long   Min     { get => _min;     set { _min     = value; OnPC(); OnPC(nameof(MinDisplay));     } }
    public long   Max     { get => _max;     set { _max     = value; OnPC(); OnPC(nameof(MaxDisplay));     } }
    public long   Avg     { get => _avg;     set { _avg     = value; OnPC(); OnPC(nameof(AvgDisplay));     } }
    public long   P95     { get => _p95;     set { _p95     = value; OnPC(); OnPC(nameof(P95Display));     } }
    public long   P99     { get => _p99;     set { _p99     = value; OnPC(); OnPC(nameof(P99Display));     } }
    public long   AvgTtfb { get => _avgTtfb; set { _avgTtfb = value; OnPC(); OnPC(nameof(AvgTtfbDisplay)); } }
    public int    Total   { get => _total;   set { _total   = value; OnPC(); OnPC(nameof(PassRate));       } }
    public int    Passed  { get => _passed;  set { _passed  = value; OnPC(); OnPC(nameof(PassRate));       } }
    public int    Failed  { get => _failed;  set { _failed  = value; OnPC(); OnPC(nameof(FailDisplay));    } }
    public double Rps     { get => _rps;     set { _rps     = value; OnPC(); OnPC(nameof(RpsDisplay));     } }

    public string MinDisplay      => $"{Min} ms";
    public string MaxDisplay      => $"{Max} ms";
    public string AvgDisplay      => $"{Avg} ms";
    public string P95Display      => $"{P95} ms";
    public string P99Display      => $"{P99} ms";
    public string AvgTtfbDisplay  => AvgTtfb > 0 ? $"{AvgTtfb} ms" : "—";
    public string PassRate        => Total > 0 ? $"{Passed * 100.0 / Total:F1}%" : "—";
    public string FailDisplay     => Failed > 0 ? Failed.ToString() : "0";
    public string RpsDisplay      => $"{Rps:F2} req/s";
    public string PassRateColor   => Total > 0 && Passed == Total ? "#22C55E" : "#EF4444";

    public static PerfRequestStat FromResults(string name, string env, IList<PerfRequestResult> results)
    {
        var times     = results.Where(r => r.Success).Select(r => r.ElapsedMs).OrderBy(x => x).ToList();
        var ttfbTimes = results.Where(r => r.Success && r.TtfbMs > 0).Select(r => r.TtfbMs).ToList();
        var stat  = new PerfRequestStat { RequestName = name, Environment = env };
        stat.Total  = results.Count;
        stat.Passed = results.Count(r => r.Success);
        stat.Failed = results.Count(r => !r.Success);
        if (times.Count > 0)
        {
            stat.Min = times[0];
            stat.Max = times[^1];
            stat.Avg = (long)times.Average();
            stat.P95 = times[(int)Math.Ceiling(times.Count * 0.95) - 1];
            stat.P99 = times[(int)Math.Ceiling(times.Count * 0.99) - 1];
        }
        if (ttfbTimes.Count > 0)
            stat.AvgTtfb = (long)ttfbTimes.Average();
        // RPS = successful requests / total elapsed seconds
        if (results.Count > 0)
        {
            var totalSec = results.Sum(r => r.ElapsedMs) / 1000.0;
            stat.Rps = totalSec > 0 ? stat.Passed / totalSec : 0;
        }
        return stat;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// ── Test run summary ─────────────────────────────────────────────────────────

public class PerfRunSummary : INotifyPropertyChanged
{
    private int    _totalReqs, _passed, _failed;
    private long   _durationMs;
    private double _overallRps;
    private string _status = "Idle";

    public int    TotalRequests { get => _totalReqs;  set { _totalReqs  = value; OnPC(); } }
    public int    Passed        { get => _passed;     set { _passed     = value; OnPC(); OnPC(nameof(PassRate)); } }
    public int    Failed        { get => _failed;     set { _failed     = value; OnPC(); OnPC(nameof(PassRate)); } }
    public long   DurationMs    { get => _durationMs; set { _durationMs = value; OnPC(); OnPC(nameof(DurationDisplay)); } }
    public double OverallRps    { get => _overallRps; set { _overallRps = value; OnPC(); OnPC(nameof(RpsDisplay)); } }
    public string Status        { get => _status;     set { _status     = value; OnPC(); } }

    public string PassRate       => TotalRequests > 0 ? $"{Passed * 100.0 / TotalRequests:F1}%" : "—";
    public string DurationDisplay=> DurationMs < 1000 ? $"{DurationMs} ms" : $"{DurationMs / 1000.0:F2} s";
    public string RpsDisplay     => $"{OverallRps:F2} req/s";
    public string PassRateColor  => TotalRequests > 0 && Passed == TotalRequests ? "#22C55E" : "#EF4444";
    public string StatusColor    => Status == "Running" ? "#F59E0B" : Status == "Done" ? "#22C55E" : Status == "Error" ? "#EF4444" : "#6B7280";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
