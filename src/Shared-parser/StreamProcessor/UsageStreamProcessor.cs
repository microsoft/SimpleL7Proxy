using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.StreamProcessor;

/// <summary>
/// Forwards response bytes unchanged while incrementally extracting provider-neutral usage fields.
/// Supports both SSE data events and ordinary JSON, including a short text preamble before JSON.
/// </summary>
public abstract class UsageStreamProcessor : BaseStreamProcessor
{
    private const int BufferSize = 16 * 1024;

    private readonly bool _includeSafetySignals;
    private readonly Dictionary<string, string> _usageFields = new(StringComparer.OrdinalIgnoreCase);
    private bool _backgroundFlag;
    private bool _contentFiltered;
    private bool _jailbreakDetected;
    private bool _modelFound;
    private string? _parseError;
    private string? _status;

    protected UsageStreamProcessor(bool includeSafetySignals)
    {
        _includeSafetySignals = includeSafetySignals;
    }

    public override async Task CopyToAsync(HttpContent sourceContent, Stream outputStream, bool debug)
    {
        ArgumentNullException.ThrowIfNull(sourceContent);
        ArgumentNullException.ThrowIfNull(outputStream);
        ThrowIfDisposed();

        var decoder = new ResponseEnvelopeDecoder(ObserveScalar, RecordParseError);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            using var sourceStream = await sourceContent.ReadAsStreamAsync().ConfigureAwait(false);
            int bytesRead;
            while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, BufferSize)).ConfigureAwait(false)) > 0)
            {
                await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                decoder.Consume(buffer.AsSpan(0, bytesRead));
            }

            decoder.Complete();
        }
        catch (IOException exception) when (exception.Message.Contains("Connection reset by peer", StringComparison.OrdinalIgnoreCase))
        {
            if (debug)
            {
                _logger?.LogInformation("Ignoring response stream reset: {Message}", exception.Message);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        BackgroundCompleted = string.Equals(_status, "completed", StringComparison.OrdinalIgnoreCase);
        BackgroundRequest = _backgroundFlag && _modelFound && !string.IsNullOrEmpty(BackgroundRequestId);
        if (!BackgroundRequest)
        {
            BackgroundRequestId = string.Empty;
        }
    }

    public override void GetStats(IDictionary<string, string> eventData, HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        foreach (var field in _usageFields)
        {
            eventData[JsonStreamProcessor.ConvertToPascalCase(field.Key)] = field.Value;
        }

        eventData["Usage.Available"] = (_usageFields.Count > 0).ToString();

        if (_includeSafetySignals)
        {
            eventData["Usage.Is_Jailbreak_Detected"] = _jailbreakDetected.ToString();
            eventData["Usage.Is_Content_Filtered"] = _contentFiltered.ToString();
        }

        if (TryGetUsageValue("usage.prompt_tokens_details.cached_tokens", out var cachedTokens))
        {
            eventData["Usage.Cached_Tokens"] = cachedTokens;
        }

        if (!string.IsNullOrEmpty(_parseError))
        {
            eventData["Usage.ParseError"] = _parseError;
        }
    }

    private void ObserveScalar(string path, JsonTokenType tokenType, string value)
    {
        if (IsUsagePath(path))
        {
            StoreUsageValue(path, value);
        }

        if (_includeSafetySignals && value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith(".jailbreak.detected", StringComparison.OrdinalIgnoreCase))
            {
                _jailbreakDetected = true;
            }

            if (path.Contains("content_filter_results", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".filtered", StringComparison.OrdinalIgnoreCase))
            {
                _contentFiltered = true;
            }
        }

        var fieldName = path[(path.LastIndexOf('.') + 1)..];
        if (fieldName.Equals("background", StringComparison.OrdinalIgnoreCase) && tokenType == JsonTokenType.True)
        {
            _backgroundFlag = true;
        }
        else if (fieldName.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            _status = value;
        }
        else if (fieldName.Equals("model", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
        {
            _modelFound = true;
        }
        else if (fieldName.Equals("id", StringComparison.OrdinalIgnoreCase) && BackgroundRequestId.Length == 0)
        {
            BackgroundRequestId = value;
        }
    }

    private void StoreUsageValue(string path, string value)
    {
        if (_usageFields.TryGetValue(path, out var existingValue) &&
            long.TryParse(existingValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var existingNumber) &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var newNumber))
        {
            _usageFields[path] = Math.Max(existingNumber, newNumber).ToString(CultureInfo.InvariantCulture);
            return;
        }

        _usageFields[path] = value;
    }

    private bool TryGetUsageValue(string suffix, out string value)
    {
        foreach (var field in _usageFields)
        {
            if (field.Key.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                (field.Key.Length > suffix.Length &&
                 field.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                 field.Key[field.Key.Length - suffix.Length - 1] == '.'))
            {
                value = field.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool IsUsagePath(string path)
    {
        foreach (var segment in path.Split('.'))
        {
            if (IsUsageSegment(segment))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUsageSegment(string segment) =>
        segment.Equals("usage", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("usageMetadata", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("billed_units", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("amazon-bedrock-invocationMetrics", StringComparison.OrdinalIgnoreCase);

    private void RecordParseError(string message)
    {
        _parseError ??= message;
    }

    private enum EnvelopeKind
    {
        Unknown,
        Json,
        Sse
    }

    private sealed class ResponseEnvelopeDecoder
    {
        private readonly Action<string, JsonTokenType, string> _observeScalar;
        private readonly Action<string> _recordError;
        private readonly MemoryStream _lineBuffer = new();
        private readonly MemoryStream _sseData = new();
        private EnvelopeKind _kind;
        private JsonScalarObserver? _jsonObserver;

        public ResponseEnvelopeDecoder(
            Action<string, JsonTokenType, string> observeScalar,
            Action<string> recordError)
        {
            _observeScalar = observeScalar;
            _recordError = recordError;
        }

        public void Consume(ReadOnlySpan<byte> bytes)
        {
            var index = 0;
            while (index < bytes.Length)
            {
                if (_kind == EnvelopeKind.Json)
                {
                    _jsonObserver!.Consume(bytes[index..], isFinalBlock: false);
                    return;
                }

                var currentByte = bytes[index++];
                _lineBuffer.WriteByte(currentByte);

                if (_kind == EnvelopeKind.Unknown && IsJsonStart(currentByte) && ContainsOnlyLeadingWhitespace(_lineBuffer))
                {
                    _kind = EnvelopeKind.Json;
                    _jsonObserver = new JsonScalarObserver(_observeScalar, _recordError);
                    _jsonObserver.Consume(_lineBuffer.GetBuffer().AsSpan(0, (int)_lineBuffer.Length), isFinalBlock: false);
                    _lineBuffer.SetLength(0);
                    continue;
                }

                if (currentByte == (byte)'\n')
                {
                    ProcessLine();
                }
            }
        }

        public void Complete()
        {
            if (_kind == EnvelopeKind.Json)
            {
                _jsonObserver!.Complete();
                return;
            }

            if (_lineBuffer.Length > 0)
            {
                ProcessLine();
            }

            if (_kind == EnvelopeKind.Json)
            {
                _jsonObserver!.Complete();
                return;
            }

            if (_kind == EnvelopeKind.Sse)
            {
                ProcessSseEvent();
            }
        }

        private void ProcessLine()
        {
            var line = _lineBuffer.GetBuffer().AsSpan(0, (int)_lineBuffer.Length);
            if (line.Length > 0 && line[^1] == (byte)'\n')
            {
                line = line[..^1];
            }
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (_kind == EnvelopeKind.Unknown)
            {
                var trimmedLine = TrimAsciiWhitespace(line);
                if (trimmedLine.Length == 0)
                {
                    _lineBuffer.SetLength(0);
                    return;
                }

                if (StartsWithSseField(trimmedLine))
                {
                    _kind = EnvelopeKind.Sse;
                }
                else
                {
                    var jsonStart = IndexOfJsonStart(trimmedLine);
                    if (jsonStart >= 0)
                    {
                        _kind = EnvelopeKind.Json;
                        _jsonObserver = new JsonScalarObserver(_observeScalar, _recordError);
                        _jsonObserver.Consume(trimmedLine[jsonStart..], isFinalBlock: false);
                        _jsonObserver.Consume("\n"u8, isFinalBlock: false);
                    }

                    _lineBuffer.SetLength(0);
                    return;
                }
            }

            if (_kind == EnvelopeKind.Sse)
            {
                ProcessSseLine(line);
            }

            _lineBuffer.SetLength(0);
        }

        private void ProcessSseLine(ReadOnlySpan<byte> line)
        {
            if (line.Length == 0)
            {
                ProcessSseEvent();
                return;
            }

            if (!line.StartsWith("data:"u8))
            {
                return;
            }

            var value = line[5..];
            if (value.Length > 0 && value[0] == (byte)' ')
            {
                value = value[1..];
            }

            if (_sseData.Length > 0)
            {
                _sseData.WriteByte((byte)'\n');
            }
            _sseData.Write(value);
        }

        private void ProcessSseEvent()
        {
            if (_sseData.Length == 0)
            {
                return;
            }

            var payload = TrimAsciiWhitespace(_sseData.GetBuffer().AsSpan(0, (int)_sseData.Length));
            _sseData.SetLength(0);

            if (payload.SequenceEqual("[DONE]"u8))
            {
                return;
            }

            var jsonStart = IndexOfJsonStart(payload);
            if (jsonStart < 0)
            {
                return;
            }

            var observer = new JsonScalarObserver(_observeScalar, _recordError);
            observer.Consume(payload[jsonStart..], isFinalBlock: true);
        }

        private static bool StartsWithSseField(ReadOnlySpan<byte> line) =>
            line.StartsWith("data:"u8) ||
            line.StartsWith("event:"u8) ||
            line.StartsWith("id:"u8) ||
            line.StartsWith("retry:"u8) ||
            line.StartsWith(":"u8);

        private static bool IsJsonStart(byte value) => value is (byte)'{' or (byte)'[';

        private static int IndexOfJsonStart(ReadOnlySpan<byte> value)
        {
            var objectIndex = value.IndexOf((byte)'{');
            var arrayIndex = value.IndexOf((byte)'[');
            return objectIndex < 0
                ? arrayIndex
                : arrayIndex < 0 ? objectIndex : Math.Min(objectIndex, arrayIndex);
        }

        private static bool ContainsOnlyLeadingWhitespace(MemoryStream buffer)
        {
            var value = buffer.GetBuffer().AsSpan(0, (int)buffer.Length - 1);
            return TrimAsciiWhitespace(value).Length == 0;
        }

        private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
        {
            var start = 0;
            while (start < value.Length && value[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                start++;
            }

            var end = value.Length;
            while (end > start && value[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                end--;
            }

            return value[start..end];
        }
    }

    private sealed class JsonScalarObserver
    {
        private readonly Action<string, JsonTokenType, string> _observeScalar;
        private readonly Action<string> _recordError;
        private readonly Stack<bool> _containerAddedSegment = new();
        private readonly List<string> _pathSegments = [];
        private JsonReaderState _readerState;
        private byte[] _pendingBytes = [];
        private string? _pendingProperty;
        private bool _failed;

        public JsonScalarObserver(
            Action<string, JsonTokenType, string> observeScalar,
            Action<string> recordError)
        {
            _observeScalar = observeScalar;
            _recordError = recordError;
        }

        public void Consume(ReadOnlySpan<byte> bytes, bool isFinalBlock)
        {
            if (_failed)
            {
                return;
            }

            var combinedLength = _pendingBytes.Length + bytes.Length;
            var combined = ArrayPool<byte>.Shared.Rent(Math.Max(1, combinedLength));
            try
            {
                _pendingBytes.CopyTo(combined, 0);
                bytes.CopyTo(combined.AsSpan(_pendingBytes.Length));
                var input = combined.AsSpan(0, combinedLength);
                var reader = new Utf8JsonReader(input, isFinalBlock, _readerState);

                while (reader.Read())
                {
                    ProcessToken(ref reader);
                }

                _readerState = reader.CurrentState;
                _pendingBytes = input[(int)reader.BytesConsumed..].ToArray();
            }
            catch (JsonException exception)
            {
                _failed = true;
                _recordError(exception.Message);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(combined);
            }
        }

        public void Complete()
        {
            Consume([], isFinalBlock: true);
        }

        private void ProcessToken(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    _pendingProperty = reader.GetString();
                    break;

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    var addedSegment = _pendingProperty is not null;
                    if (addedSegment)
                    {
                        _pathSegments.Add(_pendingProperty!);
                    }
                    _containerAddedSegment.Push(addedSegment);
                    _pendingProperty = null;
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (_containerAddedSegment.Pop())
                    {
                        _pathSegments.RemoveAt(_pathSegments.Count - 1);
                    }
                    _pendingProperty = null;
                    break;

                case JsonTokenType.String:
                    if (ShouldObserveScalar())
                    {
                        ObserveScalar(reader.TokenType, reader.GetString() ?? string.Empty);
                    }
                    else
                    {
                        _pendingProperty = null;
                    }
                    break;

                case JsonTokenType.Number:
                    if (ShouldObserveScalar())
                    {
                        var number = reader.HasValueSequence
                            ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
                            : Encoding.UTF8.GetString(reader.ValueSpan);
                        ObserveScalar(reader.TokenType, number);
                    }
                    else
                    {
                        _pendingProperty = null;
                    }
                    break;

                case JsonTokenType.True:
                    if (ShouldObserveScalar())
                    {
                        ObserveScalar(reader.TokenType, "true");
                    }
                    else
                    {
                        _pendingProperty = null;
                    }
                    break;

                case JsonTokenType.False:
                    if (ShouldObserveScalar())
                    {
                        ObserveScalar(reader.TokenType, "false");
                    }
                    else
                    {
                        _pendingProperty = null;
                    }
                    break;

                case JsonTokenType.Null:
                    _pendingProperty = null;
                    break;
            }
        }

        private bool ShouldObserveScalar()
        {
            if (_pendingProperty is null)
            {
                return false;
            }

            if (IsUsageSegment(_pendingProperty) || _pathSegments.Any(IsUsageSegment))
            {
                return true;
            }

            if (_pendingProperty.Equals("background", StringComparison.OrdinalIgnoreCase) ||
                _pendingProperty.Equals("status", StringComparison.OrdinalIgnoreCase) ||
                _pendingProperty.Equals("model", StringComparison.OrdinalIgnoreCase) ||
                _pendingProperty.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return (_pendingProperty.Equals("detected", StringComparison.OrdinalIgnoreCase) &&
                    _pathSegments.Any(segment => segment.Equals("jailbreak", StringComparison.OrdinalIgnoreCase))) ||
                   (_pendingProperty.Equals("filtered", StringComparison.OrdinalIgnoreCase) &&
                    _pathSegments.Any(segment => segment.Equals("content_filter_results", StringComparison.OrdinalIgnoreCase)));
        }

        private void ObserveScalar(JsonTokenType tokenType, string value)
        {
            if (_pendingProperty is null)
            {
                return;
            }

            var path = _pathSegments.Count == 0
                ? _pendingProperty
                : string.Concat(string.Join('.', _pathSegments), ".", _pendingProperty);
            _observeScalar(path, tokenType, value);
            _pendingProperty = null;
        }
    }
}