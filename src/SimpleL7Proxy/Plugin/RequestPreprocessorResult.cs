using System.Net;

namespace SimpleL7Proxy.Plugin;

/// <summary>
/// Decision returned by <see cref="IRequestPreprocessorPlugin"/>.
/// </summary>
public readonly record struct RequestPreprocessorResult(
    bool ShouldContinue,
    HttpStatusCode StatusCode,
    string Message)
{
    public static RequestPreprocessorResult Continue() =>
        new(true, HttpStatusCode.OK, string.Empty);

    public static RequestPreprocessorResult Reject(HttpStatusCode statusCode, string message) =>
        new(false, statusCode, message);
}