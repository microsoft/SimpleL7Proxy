using System.Net;
using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.StreamProcessor;

/// <summary>
/// Factory for creating and managing stream processors.
/// Optimized for high-throughput scenarios where every transaction uses this factory.
/// Uses singleton instances for stateless processors to minimize allocations.
/// </summary>
public sealed class StreamProcessorFactory
{
    private readonly ILogger<StreamProcessorFactory> _logger;
    
    // Singleton instance for the default processor (stateless, can be reused)
    private static readonly IStreamProcessor DefaultStreamProcessorInstance = new DefaultStreamProcessor();
    
    // Factory functions for processors (lazy instantiation when needed)
    private static readonly Dictionary<string, Func<IStreamProcessor>> ProcessorFactories = 
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI"] = static () => new OpenAIProcessor(),
            ["AllUsage"] = static () => new AllUsageProcessor(),
            ["DefaultStream"] = static () => DefaultStreamProcessorInstance, // Reuse singleton
            ["MultiLineAllUsage"] = static () => new MultiLineAllUsageProcessor()
        };

    // Constants for processor selection logic
    private const string DEFAULT_PROCESSOR = "Default";
    private const string STREAM_PROCESSOR = "DefaultStream";
    private const string PROCESSOR_SUFFIX = "Processor";
    private const string TOKEN_PROCESSOR_HEADER = "TOKENPROCESSOR";
    private const string EVENT_STREAM_MEDIA = "text/event-stream";

    public StreamProcessorFactory(ILogger<StreamProcessorFactory> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Determines the appropriate stream processor based on response headers and content type.
    /// 
    /// PROCESSOR SELECTION LOGIC:
    ///   1. Check for "TOKENPROCESSOR" header in 200 OK responses
    ///   2. Use event-stream processor for "text/event-stream" content type
    ///   3. Use "Inline" processor if explicitly requested
    ///   4. Default to "DefaultStream" for standard responses
    /// 
    /// AVAILABLE PROCESSORS:
    ///   - DefaultStream: Pass-through streaming (default)
    ///   - OpenAI: OpenAI-specific response processing with batch detection
    ///   - AllUsage: Usage tracking for OpenAI responses
    ///   - MultiLineAllUsage: Multi-line usage data extraction
    /// </summary>
    /// <param name="proxyResponse">The HTTP response from the backend</param>
    /// <param name="mediaType">The content type of the response</param>
    /// <returns>The name of the processor to use for streaming the response</returns>
    public static string DetermineStreamProcessor(HttpResponseMessage proxyResponse, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(proxyResponse);

        // Extract processor from header (200 OK only)
        var processor = proxyResponse.StatusCode == HttpStatusCode.OK &&
                    proxyResponse.Headers.TryGetValues(TOKEN_PROCESSOR_HEADER, out var values) &&
                    values.FirstOrDefault()?.Trim() is { Length: > 0 } headerValue
            ? StripProcessorSuffix(headerValue, PROCESSOR_SUFFIX)
            : DEFAULT_PROCESSOR;

        // Use pattern matching for cleaner logic
        return processor switch
        {
            "Inline" => STREAM_PROCESSOR,
            DEFAULT_PROCESSOR when mediaType.Equals(EVENT_STREAM_MEDIA, StringComparison.OrdinalIgnoreCase) 
                => STREAM_PROCESSOR,
            _ => processor
        };
    }

    /// <summary>
    /// Gets a stream processor instance by name with fallback to default.
    /// Optimized for performance - uses singleton for default processor to reduce allocations.
    /// </summary>
    /// <param name="processorName">The name of the processor to retrieve</param>
    /// <param name="resolvedProcessorName">The actual processor name that was used (after fallback)</param>
    /// <returns>An instance of the requested stream processor</returns>
    public IStreamProcessor GetStreamProcessor(string processorName, out string resolvedProcessorName)
    {
        if (!ProcessorFactories.TryGetValue(processorName, out var factory))
        {
            _logger.LogWarning("Unknown processor requested: {Requested}. Falling back to default.", processorName);
            factory = ProcessorFactories[STREAM_PROCESSOR];
            resolvedProcessorName = STREAM_PROCESSOR;
        }
        else
        {
            resolvedProcessorName = processorName;
        }

        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Processor '{Requested}' failed to construct. Falling back to default. Error: {Message}", 
                processorName, ex.Message);
            resolvedProcessorName = STREAM_PROCESSOR;
            return ProcessorFactories[STREAM_PROCESSOR]();
        }
    }

    /// <summary>
    /// Strips the "Processor" suffix from a processor name if present.
    /// Example: "OpenAIProcessor" -> "OpenAI"
    /// </summary>
    private static string StripProcessorSuffix(string value, string suffix)
        => value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value[..^suffix.Length]
            : value;
}
