#:project ../../src/Shared-parser/Shared-parser.csproj

using System.Text;
using System.Text.Json;
using SharedParser;
using SharedParser.Tokenomics.LLM.Catalog;
using SharedParser.Tokenomics.LLM.Mapping;
using SharedParser.Tokenomics.LLM.Models;

const string usage = """
Usage:
  dotnet run --no-cache --file test/ManualTests/LLMMapFile.cs -- <input-file> <input-model> <output-model> <input-format> <output-format> [--sse]

Formats (case-insensitive):
  OpenAIChatCompletions
  OpenAIResponses
  AnthropicMessages

Without --sse, reads JSON and prints indented JSON.
With --sse, reads SSE and prints SSE events unchanged in their generated framing.
Model IDs label the mapping; they do not select the API formats.
Converted output goes to stdout; diagnostics go to stderr.
""";

if (args.Length == 1 && args[0] is "--help" or "-h") {
    Console.WriteLine(usage);
    return 0;
}
if (args.Length is not (5 or 6) || (args.Length == 6 && args[5] != "--sse")) {
    Console.Error.WriteLine(usage);
    return 2;
}
if (!Enum.TryParse<LLMApiEnum>(args[3], true, out var inputFormat)
    || inputFormat is not (LLMApiEnum.OpenAIChatCompletions or LLMApiEnum.OpenAIResponses or LLMApiEnum.AnthropicMessages)
    || !Enum.TryParse<LLMApiEnum>(args[4], true, out var outputFormat)
    || outputFormat is not (LLMApiEnum.OpenAIChatCompletions or LLMApiEnum.OpenAIResponses or LLMApiEnum.AnthropicMessages)) {
    Console.Error.WriteLine("Invalid API format. Use OpenAIChatCompletions, OpenAIResponses, or AnthropicMessages.");
    return 2;
}

string inputFile = args[0];
string inputModel = args[1];
string outputModel = args[2];
bool streaming = args.Length == 6;
if (string.IsNullOrWhiteSpace(inputModel) || string.IsNullOrWhiteSpace(outputModel)) {
    Console.Error.WriteLine("Input and output model IDs must not be empty.");
    return 2;
}

try {
    using var output = new MemoryStream();
    Action<string> processLine;
    Action complete;
    if (streaming) {
        var mapper = new LLMSSEContentMapper(
            new ValidModel(inputModel, inputFormat), new ValidModel(outputModel, outputFormat), output);
        processLine = line => mapper.ProcessLine(new NonNullString(line));
        complete = mapper.Complete;
    }
    else {
        var mapper = new LLMContentMapper(
            new ValidModel(inputModel, inputFormat), new ValidModel(outputModel, outputFormat), output);
        processLine = line => mapper.ProcessLine(new NonNullString(line));
        complete = mapper.Complete;
    }

    Console.Error.WriteLine($"{inputFile}: {inputModel} ({inputFormat}) -> {outputModel} ({outputFormat}), {(streaming ? "SSE" : "JSON")}");
    foreach (string line in File.ReadLines(inputFile)) {
        processLine(line);
    }
    complete();

    byte[] bytes = output.ToArray();
    if (streaming) {
        Console.Write(Encoding.UTF8.GetString(bytes));
    }
    else {
        using var document = JsonDocument.Parse(bytes);
        using (var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true })) {
            document.RootElement.WriteTo(writer);
        }
        Console.WriteLine();
    }
    Console.Error.WriteLine($"Completed: {bytes.Length} output bytes.");
    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
    or JsonException or ArgumentException or NotSupportedException or InvalidOperationException) {
    Console.Error.WriteLine($"Conversion failed: {exception.Message}");
    return 1;
}