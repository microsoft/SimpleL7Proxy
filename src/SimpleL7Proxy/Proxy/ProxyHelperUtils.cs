using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Proxy;

/// <summary>
/// Utility class for HTTP header manipulation, response generation, and logging operations
/// used by the proxy worker.
/// </summary>
public static class ProxyHelperUtils
{
    // Exclude hop-by-hop and restricted headers that HttpListener manages
    private static readonly HashSet<string> ExcludedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length", "Transfer-Encoding", "Connection", "Proxy-Connection",
        "Keep-Alive", "Upgrade", "Trailer", "TE", "Date", "Server"
    };

    /// <summary>
    /// Copies headers from a NameValueCollection to an HttpRequestMessage.
    /// </summary>
    /// <param name="sourceHeaders">The source headers to copy from</param>
    /// <param name="targetMessage">The target HTTP request message</param>
    /// <param name="ignoreHeaders">If true, skip S7P and X-MS-CLIENT headers and content-length</param>
    public static void CopyHeaders(
        NameValueCollection sourceHeaders,
        HttpRequestMessage? targetMessage,
        bool ignoreHeaders = false,
        List<string>? stripHeaders = null)
    {
        if (targetMessage == null) return;

        foreach (var key in sourceHeaders.AllKeys)
        {
            if (key == null) continue;

            // Skip headers in the strip list
            if (stripHeaders != null && stripHeaders.Contains(key, StringComparer.OrdinalIgnoreCase))
                continue;

            if (!ignoreHeaders || (!key.StartsWith("S7P") && !key.StartsWith("X-MS-CLIENT", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("content-length", StringComparison.OrdinalIgnoreCase)))
            {
                targetMessage.Headers.TryAddWithoutValidation(key, sourceHeaders[key]);
            }
        }
    }

    /// <summary>
    /// Copies response headers from an HttpResponseMessage to a ProxyData object.
    /// </summary>
    /// <param name="response">The HTTP response message to copy headers from</param>
    /// <param name="pr">The ProxyData object to copy headers to</param>
    public static void CopyResponseHeaders(HttpResponseMessage response, ProxyData pr)
    {
        // Response headers
        foreach (var header in response.Headers)
        {
            if (ExcludedHeaders.Contains(header.Key)) continue;
            pr.Headers[header.Key] = string.Join(", ", header.Value);
        }

        // Content headers
        foreach (var header in response.Content.Headers)
        {
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                pr.ContentHeaders[header.Key] = string.Join(", ", header.Value);
                continue;
            }

            if (!ExcludedHeaders.Contains(header.Key))
            {
                pr.Headers[header.Key] = string.Join(", ", header.Value);
            }
            pr.ContentHeaders[header.Key] = string.Join(", ", header.Value);
        }
    }

    /// <summary>
    /// Logs HTTP headers for debugging purposes.
    /// </summary>
    /// <param name="headers">The headers to log</param>
    /// <param name="prefix">A prefix string to prepend to each log line</param>
    /// <param name="logger">The logger instance to use</param>
    public static void LogHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, 
        string prefix, 
        ILogger logger)
    {
        foreach (var header in headers)
        {
            logger.LogDebug("{Prefix} {HeaderKey} : {HeaderValues}", prefix, header.Key, string.Join(", ", header.Value));
        }
    }

    /// <summary>
    /// Generates an error message with request summary details when all backend hosts fail.
    /// </summary>
    /// <param name="incompleteRequests">List of incomplete request attempt details</param>
    /// <param name="sb">Output: StringBuilder containing the formatted error message</param>
    /// <param name="statusMatches">Output: True if all status codes are the same or all are timeout-related</param>
    /// <param name="currentStatusCode">Output: The determined status code to return</param>
    public static void GenerateErrorMessage(
        List<Dictionary<string, string>> incompleteRequests, 
        out StringBuilder sb, 
        out bool statusMatches, 
        out int currentStatusCode)
    {
        sb = new StringBuilder();
        sb.AppendLine("Error processing request.  No active hosts were able to handle the request.");
        sb.AppendLine("Request Summary:");
        statusMatches = true;
        currentStatusCode = 503; // Default to Service Unavailable if no hosts attempted

        var statusCodes = new List<int>();

        int iter = 0;
        Dictionary<string, object> requestSummary = [];
        foreach (var requestAttempt in incompleteRequests)
        {
            if (requestAttempt.TryGetValue("Status", out var statusStr) && int.TryParse(statusStr, out var status))
            {
                statusCodes.Add(status);
            }

            iter++;
            requestSummary["Attempt-" + iter] = requestAttempt;
        }

        if (statusCodes.Count > 0)
        {
            // If all status codes are 408 or 412, use the latest (last) one
            if (statusCodes.All(s => s == 408 || s == 412 || s == 429))
            {
                currentStatusCode = statusCodes.Last();
                statusMatches = true;
            }
            // If all status codes are the same, return that one
            else if (statusCodes.Distinct().Count() == 1)
            {
                currentStatusCode = statusCodes.First();
                statusMatches = true;
            }
            else
            {
                statusMatches = false;
                currentStatusCode = statusCodes.Last();
            }
        }
        // else: statusCodes.Count == 0, use default 503 Service Unavailable
        
        sb.AppendLine(JsonSerializer.Serialize(requestSummary, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine();
    }

    /// <summary>
    /// Adds incomplete request details to event data for telemetry.
    /// </summary>
    /// <param name="incompleteRequests">List of incomplete request attempt details</param>
    /// <param name="eventData">The event data dictionary to add to</param>
    public static void AddIncompleteRequestsToEventData(
        List<Dictionary<string, string>> incompleteRequests, 
        System.Collections.Concurrent.ConcurrentDictionary<string, string> eventData)
    {
        int i = 0;
        foreach (var summary in incompleteRequests)
        {
            i++;
            foreach (var key in summary.Keys)
            {
                eventData[$"Attempt-{i}-{key}"] = summary[key];
            }
        }
    }

    /// <summary>
    /// Records incomplete request details to event data and returns the appropriate status code.
    /// </summary>
    /// <param name="data">Event data dictionary to record to</param>
    /// <param name="statusCode">The status code to record</param>
    /// <param name="message">Error message to include</param>
    /// <param name="incompleteRequests">Optional list of incomplete request details</param>
    /// <param name="e">Optional exception that occurred</param>
    /// <returns>The status code passed in</returns>
    public static HttpStatusCode RecordIncompleteRequests(
        System.Collections.Concurrent.ConcurrentDictionary<string, string> data,
        HttpStatusCode statusCode,
        string message,
        List<Dictionary<string, string>>? incompleteRequests = null,
        Exception? e = null)
    {
        data["Status"] = statusCode.ToString();
        data["Message"] = message;

        if (incompleteRequests != null)
        {
            AddIncompleteRequestsToEventData(incompleteRequests, data);
        }

        return statusCode;
    }
}
