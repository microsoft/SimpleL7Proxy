using SimpleL7Proxy.Plugin;

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