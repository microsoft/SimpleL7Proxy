using System.IO;

using SharedParser.Tokenomics.LLM.Formatting;
using SharedParser.Tokenomics.LLM.Models;
using SharedParser.Tokenomics.LLM.Parsing;

namespace SharedParser.Tokenomics.LLM.Mapping;

public class LLMSSEContentMapper
{
    public ValidModel SourceModel { get; }
    public ValidModel TargetModel { get; }
    /// <summary>Gets the target API format used for output serialization.</summary>
    public LLMApiFormatEnum TargetFormat => TargetModel.ApiFormat;
    public Stream OutputStream { get; }
    /// <summary>Gets diagnostic preamble lines when capture is enabled, excluding generated content.</summary>
    public string[] preBuffer => _preParser.PreBuffer;
    public LLMParserStateEnum ParserState => _preParser.ParserState;
    /// <summary>Gets the structured source preamble.</summary>
    public LLMPreamble Preamble => _preParser.Preamble;
    /// <summary>Gets the final source metadata collected in memory.</summary>
    public LLMPostamble Postamble => _preParser.Postamble;
    /// <summary>Gets the zero-based buffered line containing the start of generated content.</summary>
    public int ContentLineIndex => _preParser.ContentLineIndex;
    /// <summary>Gets the UTF-16 offset of generated content within its original SSE line.</summary>
    public int ContentStartOffset => _preParser.ContentStartOffset;

    private readonly LLMApiParser _preParser;
    private readonly LLMApiOutput _dataWriter;
    private LLMApiParser.LineProcessor _processLine;
    private bool _outputCompleted;

    /// <summary>Creates an SSE mapper with validated models and a caller-validated destination.</summary>
    public LLMSSEContentMapper(ValidModel sourceModel, ValidModel targetModel, Stream outputStream, bool capturePreLines = false)
    {
        SourceModel = sourceModel;
        TargetModel = targetModel;
        OutputStream = outputStream;
        _dataWriter = LLMApiOutput.Create(targetModel, true, outputStream);
        _preParser = LLMApiParser.Create(sourceModel, true, capturePreLines);
        _processLine = _preParser.ParsePre;
    }

    public void ProcessLine(NonNullString line)
    {
        ProcessLineCore(line);
        _dataWriter.DrainSerializedBytes();
    }

    /// <summary>Processes an SSE line and asynchronously writes available events without flushing the destination.</summary>
    public ValueTask ProcessLineAsync(NonNullString line) {
        ProcessLineCore(line);
        return _dataWriter.DrainSerializedBytesAsync();
    }

    private void ProcessLineCore(NonNullString line) {
        LLMParserStateEnum previousState = ParserState;
        _processLine = _processLine(line);

        if (previousState == LLMParserStateEnum.Pre && ParserState == LLMParserStateEnum.Data) {
            _dataWriter.OutputPre(Preamble);
            _processLine = _preParser.ResumeData(_dataWriter.OutputData);
        }

        if (!_outputCompleted && _preParser.IsComplete && ParserState == LLMParserStateEnum.Post) {
            _dataWriter.OutputPost(Preamble, Postamble);
            _outputCompleted = true;
        }
    }

    /// <summary>Validates EOF and writes the final target output once.</summary>
    public void Complete() {
        CompleteCore();
        _dataWriter.DrainSerializedBytes();
    }

    /// <summary>Validates EOF and asynchronously writes final events without flushing or closing the destination.</summary>
    public ValueTask CompleteAsync() {
        CompleteCore();
        return _dataWriter.DrainSerializedBytesAsync();
    }

    private void CompleteCore() {
        _preParser.Complete();
        if (!_outputCompleted) {
            _dataWriter.OutputPost(Preamble, Postamble);
            _outputCompleted = true;
        }
    }
}