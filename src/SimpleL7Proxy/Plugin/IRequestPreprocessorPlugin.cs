using System.Net;

namespace SimpleL7Proxy;

/// <summary>
/// Lightweight hook to inspect/mutate incoming requests before normal server validation and enqueue.
/// Implementations may short-circuit request processing with a specific status code.
/// </summary>
public interface IRequestPreprocessorPlugin
{
    Task<RequestPreprocessorResult> ProcessAsync(RequestData requestData, CancellationToken cancellationToken = default);
}

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

/// <summary>
/// Default no-op plugin so the pipeline always has a safe baseline implementation.
/// </summary>
public sealed class AllowAllRequestPreprocessorPlugin : IRequestPreprocessorPlugin
{
    public Task<RequestPreprocessorResult> ProcessAsync(RequestData requestData, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(RequestPreprocessorResult.Continue());
    }
}