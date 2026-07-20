namespace PayrixLauncher.Services;

public static class PayrixErrorHelper
{
    public static string Classify(Exception ex)
    {
        var inner = ex is AggregateException agg ? agg.InnerException ?? ex : ex;

        if (inner is TaskCanceledException or OperationCanceledException ||
            inner.InnerException is TimeoutException ||
            Contains(inner, "timeout") || Contains(inner, "timed out") ||
            Contains(inner, "too long") || Contains(inner, "canceled"))
            return "The server took too long to respond. Try a smaller date range or check your connection.";

        if (Contains(inner, "401") || Contains(inner, "Unauthorized") || Contains(inner, "api key"))
            return "API key rejected (401). Go to Settings and verify your Sandbox or Production key.";

        if (Contains(inner, "403") || Contains(inner, "Forbidden") || Contains(inner, "Access denied"))
            return "Access denied (403). Your API key may not have permission for this operation.";

        if (Contains(inner, "404") || Contains(inner, "Not Found"))
            return "Resource not found (404). The record may not exist in this environment.";

        if (inner is System.Net.Http.HttpRequestException ||
            Contains(inner, "connection") || Contains(inner, "network") ||
            Contains(inner, "socket") || Contains(inner, "DNS"))
            return $"Network error — could not reach the API. Check your connection. ({FirstLine(inner)})";

        return $"Request failed — {FirstLine(inner)}";
    }

    private static bool Contains(Exception ex, string term) =>
        ex.Message.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        (ex.InnerException?.Message.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string FirstLine(Exception ex) =>
        (ex.Message ?? "").Split('\n')[0].Trim();
}
