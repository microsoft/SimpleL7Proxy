namespace chat_tester.Components.Shared;

/// <summary>A single result row from a URL-tester run.</summary>
public sealed class ProbeResult
{
    public ProbeResult(
        int index,
        string url,
        int statusCode,
        string outcome,
        string responseHeaders = "",
        string responseBody = "",
        TimeSpan timeToFirstByte = default,
        TimeSpan duration = default,
        string contentType = "",
        long bytes = 0)
    {
        Index = index;
        Url = url;
        StatusCode = statusCode;
        Outcome = outcome;
        ResponseHeaders = responseHeaders;
        ResponseBody = responseBody;
        TimeToFirstByte = timeToFirstByte;
        Duration = duration;
        ContentType = contentType;
        Bytes = bytes;
    }

    public int Index { get; }
    public string Url { get; }
    public int StatusCode { get; }
    public string Outcome { get; }
    public string ResponseHeaders { get; }
    public string ResponseBody { get; }
    public TimeSpan TimeToFirstByte { get; }
    public TimeSpan Duration { get; }
    public string ContentType { get; }
    public long Bytes { get; }
}
