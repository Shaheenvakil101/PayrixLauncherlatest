using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using PayrixLauncher.Models;

namespace PayrixLauncher.Services;

/// <summary>
/// Singleton that stores all captured HTTP sessions.
/// Raise SessionAdded / SessionUpdated so the UI can bind live.
/// </summary>
public static class TrafficLogger
{
    private static int _counter = 0;
    private static bool _paused = false;

    public static ObservableCollection<TrafficSession> Sessions { get; } = [];

    public static bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    public static event Action<TrafficSession>? SessionAdded;

    public static TrafficSession? BeginSession(string method, string url, string requestHeaders,
                                                string requestBody, string source)
    {
        if (_paused) return null;

        var uri    = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null;
        var host   = uri?.Host ?? url;
        var proto  = uri?.Scheme.ToUpperInvariant() ?? "HTTP";
        var path   = uri != null ? uri.PathAndQuery : url;

        var s = new TrafficSession
        {
            SessionId      = System.Threading.Interlocked.Increment(ref _counter),
            Method         = method.ToUpperInvariant(),
            Protocol       = proto,
            Host           = host,
            Url            = path,
            Source         = source,
            RequestHeaders = requestHeaders,
            RequestBody    = requestBody,
            CapturedAt     = System.DateTime.Now
        };

        // Must add on UI thread
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Sessions.Add(s);
            SessionAdded?.Invoke(s);
        });

        return s;
    }

    public static void Clear()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Sessions.Clear());
        _counter = 0;
    }

    public static void Export(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PayrixLauncher Traffic Export");
        sb.AppendLine($"# Exported: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        foreach (var s in Sessions)
        {
            sb.AppendLine($"=== Session #{s.SessionId} — {s.Method} {s.Protocol}://{s.Host}{s.Url} ===");
            sb.AppendLine($"Status: {s.ResultLabel}  |  Duration: {s.DurationLabel}  |  Source: {s.Source}");
            sb.AppendLine("--- Request Headers ---");
            sb.AppendLine(s.RequestHeaders);
            if (!string.IsNullOrWhiteSpace(s.RequestBody))
            {
                sb.AppendLine("--- Request Body ---");
                sb.AppendLine(s.RequestBody);
            }
            sb.AppendLine("--- Response Headers ---");
            sb.AppendLine(s.ResponseHeaders);
            sb.AppendLine("--- Response Body ---");
            sb.AppendLine(s.ResponseBodyPretty);
            sb.AppendLine();
        }
        System.IO.File.WriteAllText(path, sb.ToString());
    }
}

/// <summary>
/// DelegatingHandler inserted into every HttpClient chain.
/// Captures request + response and feeds them to TrafficLogger.
/// </summary>
public sealed class LoggingHandler : DelegatingHandler
{
    private readonly string _source;

    public LoggingHandler(string source, HttpMessageHandler inner)
        : base(inner)
    {
        _source = source;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, System.Threading.CancellationToken ct)
    {
        // Read request body (don't consume the stream)
        string reqBody = "";
        if (request.Content != null)
        {
            reqBody = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Re-wrap so the original handler can still read it
            request.Content = new StringContent(reqBody,
                Encoding.UTF8,
                request.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        TrafficSession? session = null;
        try
        {
            var reqContentHeaders = request.Content != null
                ? request.Content.Headers.Cast<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>>>()
                : Enumerable.Empty<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>>>();

            var reqHeaders = BuildHeaderString(request.Headers
                .Cast<System.Collections.Generic.KeyValuePair<string,
                 System.Collections.Generic.IEnumerable<string>>>()
                .Concat(reqContentHeaders)
                .GroupBy(h => h.Key)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value))));

            session = TrafficLogger.BeginSession(
                request.Method.Method,
                request.RequestUri?.ToString() ?? "",
                reqHeaders, reqBody, _source);
        }
        catch { /* logging setup failed — continue without session */ }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            if (session != null)
            {
                session.Result        = 0;
                session.DurationMs    = sw.ElapsedMilliseconds;
                session.ResponseBody  = ex.Message;
            }
            throw;
        }

        sw.Stop();

        // Logging must never break the actual request — wrap in try/catch
        if (session != null)
        {
            try
            {
                string respBody = "";
                if (response.Content != null)
                {
                    respBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    // Re-wrap so the caller can still read the body
                    response.Content = new StringContent(respBody,
                        Encoding.UTF8,
                        response.Content.Headers.ContentType?.MediaType ?? "application/json");
                }

                var respContentHeaders = response.Content != null
                    ? response.Content.Headers.Cast<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>>>()
                    : Enumerable.Empty<System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>>>();

                var respHeaders = BuildHeaderString(response.Headers
                    .Cast<System.Collections.Generic.KeyValuePair<string,
                     System.Collections.Generic.IEnumerable<string>>>()
                    .Concat(respContentHeaders)
                    .GroupBy(h => h.Key)
                    .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value))));

                session.Result          = (int)response.StatusCode;
                session.ResponseHeaders = respHeaders;
                session.ResponseBody    = respBody;
                session.DurationMs      = sw.ElapsedMilliseconds;
            }
            catch
            {
                // Logging failed — record status only, never propagate
                session.Result     = (int)response.StatusCode;
                session.DurationMs = sw.ElapsedMilliseconds;
            }
        }

        return response;
    }

    private static string BuildHeaderString(
        System.Collections.Generic.Dictionary<string, string> headers)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in headers)
            sb.AppendLine($"{k}: {v}");
        return sb.ToString();
    }
}
