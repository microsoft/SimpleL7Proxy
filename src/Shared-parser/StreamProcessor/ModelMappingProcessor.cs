//#define LLM_SUPPRESS_NETWORK_OUTPUT

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using SharedParser.Tokenomics.LLM;
using SharedParser.Tokenomics.LLM.Mapping;
using SharedParser.Tokenomics.LLM.Models;
using SimpleL7Proxy.StreamProcessor;

namespace SharedParser.StreamProcessor;

/// <summary>Maps response text between explicitly selected model API formats.</summary>
public class ModelMappingProcessor : UsageStreamProcessor {
	private readonly LLMApiFormatEnum _sourceFormat;
	private (ValidModel SourceModel, ValidModel TargetModel, bool Streaming)? _mappingOptions;
	private LLMPostamble? _postamble;

	/// <summary>Creates a single-use response mapper for JSON or SSE input and output.</summary>
	public ModelMappingProcessor(ValidModel sourceModel, ValidModel targetModel, bool streaming = false)
		: base(includeSafetySignals: false) {
		ArgumentNullException.ThrowIfNull(sourceModel);
		ArgumentNullException.ThrowIfNull(targetModel);
        
		_sourceFormat = sourceModel.ApiFormat;
		_mappingOptions = (sourceModel, targetModel, streaming);
	}

	/// <summary>Writes the converted response without flushing or closing the destination.</summary>
	public override async Task CopyToAsync(HttpContent sourceContent, Stream outputStream, bool debug) {
		ArgumentNullException.ThrowIfNull(sourceContent);
		ArgumentNullException.ThrowIfNull(outputStream);
		ThrowIfDisposed();
		if (!outputStream.CanWrite) {
			throw new ArgumentException("The output stream must be writable.", nameof(outputStream));
		}
		var options = _mappingOptions
			?? throw new InvalidOperationException("This response mapper has already been used.");

		_postamble = null;
		BackgroundRequest = false;
		BackgroundRequestId = string.Empty;
		BackgroundCompleted = false;

#if LLM_SUPPRESS_NETWORK_OUTPUT
		var mappingOutputStream = Stream.Null;
#else
		var mappingOutputStream = outputStream;
#endif
		Func<NonNullString, ValueTask> processLine;
		Func<ValueTask> complete;
		if (options.Streaming) {
			var mapper = new LLMSSEContentMapper(options.SourceModel, options.TargetModel, mappingOutputStream);
			processLine = mapper.ProcessLineAsync;
			complete = mapper.CompleteAsync;
			_postamble = mapper.Postamble;
		}
		else {
			var mapper = new LLMContentMapper(options.SourceModel, options.TargetModel, mappingOutputStream);
			processLine = mapper.ProcessLineAsync;
			complete = mapper.CompleteAsync;
			_postamble = mapper.Postamble;
		}
		_mappingOptions = null;
		options = default;

		using var sourceStream = await sourceContent.ReadAsStreamAsync().ConfigureAwait(false);
		using var reader = new StreamReader(sourceStream, Encoding.UTF8);
		string? line;
		while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null) {
			await processLine(new NonNullString(line)).ConfigureAwait(false);
		}
		await complete().ConfigureAwait(false);
		BackgroundCompleted = string.Equals(_postamble.Status, "completed", StringComparison.OrdinalIgnoreCase);
#if LLM_SUPPRESS_NETWORK_OUTPUT
		await outputStream.WriteAsync("ok"u8.ToArray()).ConfigureAwait(false);
#endif
	}

	/// <summary>Publishes captured usage in the source API schema without inventing missing counts.</summary>
	public override void GetStats(IDictionary<string, string> eventData, HttpResponseHeaders headers) {
		ArgumentNullException.ThrowIfNull(eventData);
		var postamble = _postamble;
		if (postamble is null) {
			eventData["Usage.Available"] = bool.FalseString;
			return;
		}
		eventData["Usage.Available"] = (postamble.InputTokens.HasValue || postamble.OutputTokens.HasValue ||
			postamble.TotalTokens.HasValue || postamble.CachedInputTokens.HasValue ||
			postamble.CacheCreationInputTokens.HasValue || postamble.ReasoningOutputTokens.HasValue).ToString();

		bool completion = _sourceFormat == LLMApiFormatEnum.OpenAIChatCompletions;
		AddUsageField(eventData, completion ? "Usage.Prompt_Tokens" : "Usage.Input_Tokens", postamble.InputTokens);
		AddUsageField(eventData, completion ? "Usage.Completion_Tokens" : "Usage.Output_Tokens", postamble.OutputTokens);
		AddUsageField(eventData, "Usage.Total_Tokens", postamble.TotalTokens);
		AddUsageField(eventData, "Usage.Cached_Tokens", postamble.CachedInputTokens);
		if (_sourceFormat == LLMApiFormatEnum.AnthropicMessages) {
			AddUsageField(eventData, "Usage.Cache_Read_Input_Tokens", postamble.CachedInputTokens);
			AddUsageField(eventData, "Usage.Cache_Creation_Input_Tokens", postamble.CacheCreationInputTokens);
		}
		else {
			AddUsageField(eventData, completion ? "Usage.Prompt_Tokens_Details.Cached_Tokens" : "Usage.Input_Tokens_Details.Cached_Tokens", postamble.CachedInputTokens);
			AddUsageField(eventData, completion ? "Usage.Completion_Tokens_Details.Reasoning_Tokens" : "Usage.Output_Tokens_Details.Reasoning_Tokens", postamble.ReasoningOutputTokens);
		}
	}

	private static void AddUsageField(IDictionary<string, string> eventData, string key, long? value) {
		if (value is long count) {
			eventData[key] = count.ToString(CultureInfo.InvariantCulture);
		}
	}
}

