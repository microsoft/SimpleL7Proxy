namespace SimpleL7Proxy.Plugin;

/// <summary>
/// Lightweight hook to inspect/mutate incoming requests before normal server validation and enqueue.
/// Implementations may short-circuit request processing with a specific status code.
/// </summary>
public interface IRequestPreprocessorPlugin
{
    Task<RequestPreprocessorResult> ProcessAsync(RequestData requestData, CancellationToken cancellationToken = default);
}



