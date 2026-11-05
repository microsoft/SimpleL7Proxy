using System.IO;

using SharedParser.Tokenomics.LLM.Formaters;
using SharedParser.Tokenomics.LLM.Parsers;

namespace SharedParser.Tokenomics.LLM;

public class LLMSSEContentMapper
{
    public string SourceModel { get; }
    public string TargetModel { get; }
    /// <summary>Gets the target API format used for output serialization.</summary>
    public LLMApiFormatEnum TargetFormat { get; }
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

    /// <summary>Creates an SSE mapper with explicit source and target API formats.</summary>
    public LLMSSEContentMapper(string sourceModel, string targetModel, Stream outputStream, LLMApiFormatEnum sourceFormat, LLMApiFormatEnum targetFormat, bool capturePreLines = false)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        ArgumentNullException.ThrowIfNull(targetModel);
        if (!outputStream.CanWrite) {
            throw new ArgumentException("The output stream must be writable.", nameof(outputStream));
        }
        SourceModel = sourceModel;
        TargetModel = targetModel;
        TargetFormat = targetFormat;
        OutputStream = outputStream;
        _dataWriter = LLMApiOutput.Create(targetFormat, true, outputStream, targetModel);
        _preParser = LLMApiParser.Create(sourceFormat, true, capturePreLines);
        _processLine = _preParser.ParsePre;
    }

    public void ProcessLine(String line)
    {
        ArgumentNullException.ThrowIfNull(line);
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
        else if (ParserState != LLMParserStateEnum.Pre && !_outputCompleted) {
            _dataWriter.DrainSerializedBytes();
        }
    }

    /// <summary>Validates EOF and writes the final target output once.</summary>
    public void Complete() {
        _preParser.Complete();
        if (!_outputCompleted) {
            _dataWriter.OutputPost(Preamble, Postamble);
            _outputCompleted = true;
        }
    }
}