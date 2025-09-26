using System.Text.Json.Nodes;
using System.Net.Http.Headers;
using SimpleL7Proxy.Events;
using System.Text.RegularExpressions;

/*
 * JSON STREAM PROCESSOR BASE CLASS DOCUMENTATION
 * =============================================
 * 
 * PURPOSE:
 * - Provides common JSON streaming and parsing logic for OpenAI-like APIs
 * - Implements the standard pattern of streaming content while capturing the last line
 * - Handles common exception patterns (IOException, OperationCanceledException)
 * - Provides template methods for customizing JSON processing logic
 * 
 * STREAMING PATTERN:
 * - Streams all content immediately while collecting lines for processing
 * - Captures last <MaxLines> meaningful lines for statistics extraction (skips [DONE] markers)
 * - Uses efficient circular buffer instead of queue for better performance
 * - Handles "data: [DONE]" termination pattern common in streaming APIs
 * - Uses consistent exception handling and error data storage
 * 
 * DATA MANAGEMENT:
 * - Maintains protected Dictionary<string, string> for statistics
 * - Provides helper methods for storing error information
 * - Handles disposal of data dictionary properly
 * 
 * TEMPLATE METHODS:
 * - ProcessLastLines: Override to implement specific JSON parsing logic with access to last <MaxLines> lines
 * - ProcessLastLine: Legacy method for backward compatibility (calls ProcessLastLines)
 * - PopulateEventData: Override to customize how statistics are transferred to events
 * - ShouldIgnoreException: Override to customize exception handling logic
 */

namespace SimpleL7Proxy.StreamProcessor
{
    /// <summary>
    /// Abstract base class for stream processors that handle JSON-based streaming APIs
    /// with last-lines statistics extraction patterns. Uses an efficient circular buffer to capture 
    /// the last 6 meaningful lines for processors that need to analyze ending content.
    /// </summary>
    public abstract class JsonStreamProcessor : BaseStreamProcessor
    {
        protected Dictionary<string, string> data = new();
        protected virtual int MaxLines { get; } = 10; 
        protected virtual int MinLineLength { get; } = 20;
        
        /// <summary>
        /// Implements the common streaming pattern used by JSON-based processors.
        /// </summary>
        public override async Task CopyToAsync(System.Net.Http.HttpContent sourceContent, Stream outputStream)
        {

            var lastLines = new string[MaxLines]; // Fixed array for last 6 lines
            int currentIndex = 0; // Current write position
            int lineCount = 0;    // Total lines written

            try
            {
                using var sourceStream = await sourceContent.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new StreamReader(sourceStream);
                using var writer = new StreamWriter(outputStream, bufferSize: 4096, leaveOpen: true);

                string? currentLine;

                // Stream all content immediately while tracking meaningful lines
                while ((currentLine = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    //cancellationToken?.ThrowIfCancellationRequested();

                    // Write each line immediately - no delays
                    Task t = writer.WriteLineAsync(currentLine);

                    // Only process through lines that could have usage in them
                    if (currentLine.Length > MinLineLength)
                    {
                        lastLines[currentIndex] = currentLine;
                        currentIndex = (currentIndex + 1) % MaxLines; // Wrap around
                        lineCount++;
                    }

                    await t.ConfigureAwait(false);
                }
            }
            catch (IOException e)
            {
                if (!ShouldIgnoreException(e))
                {
                    data["LastError"] = e.Message;
                    throw;
                }
                // Exception is ignored (e.g., "Connection reset by peer")
            }
            catch (OperationCanceledException)
            {
                data["LastError"] = "Operation was cancelled";
                throw;
            }
            catch (Exception e)
            {
                data["LastError"] = $"Unexpected error: {e.Message}";
                throw;
            }
            finally
            {
                // Process the last lines if we have any
                if (lineCount > 0)
                {
                    try
                    {
                        // Walk through lines to find the one with usage data
                        // copy from currentIndex to the end into the buffer
                        var validLines = new string[Math.Min(lineCount, MaxLines)];

                        if (lineCount >= MaxLines)
                        {
                            Array.Copy(lastLines, currentIndex, validLines, 0, MaxLines - currentIndex);
                            Array.Copy(lastLines, 0, validLines, MaxLines - currentIndex, currentIndex);
                        }
                        else
                        {
                            Array.Copy(lastLines, 0, validLines, 0, lineCount);
                        }

                        string? usageLine = null;

                        var idPattern = @"\s*""id""\s*:\s*""([^""]+)""";

                        // Loop through lines starting from most recent, going backwards
                        for (int i = 0; i < validLines.Length; i++)
                        {
                            var line = validLines[i];
                            if (line.IndexOf("usage", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                usageLine = line;
                                break; // Found the line with usage
                            }
                            else
                            {
                                BackgroundRequest = false;

                                // Check if this task was accepted as a background task
                                if (line.Contains(@"""background"": true"))
                                {
                                    // Console.WriteLine("This is a background request");
                                    BackgroundRequest = true;
                                }
                                var match = Regex.Match(line, idPattern, RegexOptions.Singleline);
                                var jsonBlock = String.Empty;

                                if (match.Success)
                                {
                                    BackgroundRequestId = match.Groups[1].Value; 
                                    BackgroundRequest = true;
                                    
                                }

                            }
                        }

                        // Fall back to most recent line if no usage found
                        var primaryLine = usageLine ?? validLines[0];
                        ProcessLastLines(validLines, primaryLine);
                    }
                    catch (Exception ex)
                    {
                        data["LastLineProcessingError"] = ex.Message;
                    }
                }
                else
                {
                    Console.WriteLine("No content received from source stream.");
                }
            }
        }

        /// <summary>
        /// Converts a key like "usage.foo_bar" to "Usage.Foo_Bar" format.
        /// </summary>
        public static string ConvertToPascalCase(string key)
        {
            var splitParts = key.Split('.', '[', ']');
            var resultParts = new List<string>(splitParts.Length);

            foreach (var part in splitParts)
            {
                if (!string.IsNullOrEmpty(part))
                {
                    resultParts.Add(ConvertWordToPascalCase(part));
                }
            }

            return string.Join(".", resultParts);
        }

        /// <summary>
        /// Converts a word like "foo_bar" to "Foo_Bar" format.
        /// </summary>
        public static string ConvertWordToPascalCase(string word)
        {
            if (string.IsNullOrEmpty(word)) return word;
            
            // Split by underscore and capitalize first letter of each part
            var parts = word.Split('_');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0)
                {
                    parts[i] = char.ToUpper(parts[i][0]) + parts[i].Substring(1).ToLower();
                }
            }
            
            return string.Join("_", parts);
        }
        /// <summary>
        /// Implements the common pattern of transferring data dictionary to event data.
        /// </summary>
        public override void GetStats(ProxyEvent eventData, HttpResponseHeaders headers)
        {
            PopulateEventData(eventData, headers);
        }

        /// <summary>
        /// Template method for processing the last lines of JSON content.
        /// Derived classes must implement this to extract specific statistics.
        /// </summary>
        /// <param name="lastLines">Array of the last significant lines from the stream (up to <MaxLines> lines).</param>
        /// <param name="primaryLine">The primary line to process (typically the last non-[DONE] line).</param>
        protected abstract void ProcessLastLines(string[] lastLines, string primaryLine);

        /// <summary>
        /// Legacy template method for backward compatibility.
        /// Calls the new ProcessLastLines method with just the primary line.
        /// </summary>
        /// <param name="lastLine">The last significant line from the stream.</param>
        protected virtual void ProcessLastLine(string lastLine)
        {
            // Default implementation for backward compatibility
            ProcessLastLines(new[] { lastLine }, lastLine);
        }

        /// <summary>
        /// Template method for populating event data with extracted statistics.
        /// Derived classes can override to customize how data is transferred.
        /// </summary>
        /// <param name="eventData">The event data object to populate.</param>
        /// <param name="headers">The HTTP response headers.</param>
        protected virtual void PopulateEventData(ProxyEvent eventData, HttpResponseHeaders headers)
        {
            // Default implementation: copy all data to event data
            foreach (var kvp in data)
            {
                eventData[kvp.Key] = kvp.Value;
            }
        }

        /// <summary>
        /// Template method for determining whether to ignore specific exceptions.
        /// Default behavior ignores "Connection reset by peer" IOException.
        /// </summary>
        /// <param name="exception">The IOException to evaluate.</param>
        /// <returns>True if the exception should be ignored, false otherwise.</returns>
        protected virtual bool ShouldIgnoreException(IOException exception)
        {
            return exception.Message.Contains("Connection reset by peer");
        }

        /// <summary>
        /// Helper method to safely parse JSON and remove common prefixes.
        /// </summary>
        /// <param name="jsonLine">The JSON line to parse.</param>
        /// <returns>The parsed JsonNode or null if parsing fails.</returns>
        protected JsonNode? ParseJsonLine(string jsonLine)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jsonLine))
                    return null;

                // Line starts with "data: " so we need to remove that prefix
                if (jsonLine.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
                {
                    jsonLine = jsonLine.Substring(6).Trim();
                }

                return JsonNode.Parse(jsonLine);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Simplified extraction for simple JSON with few fields and 1-2 layers deep.
        /// Converts all values to strings for consistent data handling.
        /// </summary>
        /// <param name="node">The JSON node to extract fields from.</param>
        /// <param name="prefix">The prefix for the field names (hierarchy path).</param>
        public void ExtractAllFields(JsonNode? node, string prefix)
        {
            if (node is not JsonObject jsonObject) return;

            foreach (var (key, value) in jsonObject)
            {
                if (value == null) continue;

                var fieldName = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";

                switch (value)
                {
                    case JsonValue jsonValue:
                        data[fieldName] = jsonValue.ToString();
                        break;

                    case JsonObject nestedObject:
                        ExtractAllFields(nestedObject, fieldName);

                        break;

                    case JsonArray jsonArray:
                        for (int i = 0; i < jsonArray.Count; i++)
                        {
                            if (jsonArray[i] is JsonObject arrayObject)
                            {
                                foreach (var (arrayKey, arrayValue) in arrayObject)
                                {
                                    if (arrayValue is JsonValue arrayJsonValue)
                                        data[$"{fieldName}[{i}].{arrayKey}"] = arrayJsonValue.ToString();
                                }
                            }
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Helper method to safely extract token values from usage objects.
        /// </summary>
        /// <param name="usage">The usage JSON node.</param>
        /// <param name="tokenField">The token field name (e.g., "completion_tokens").</param>
        /// <returns>The token count as a string, or "0" if not found.</returns>
        protected string ExtractTokenCount(JsonNode? usage, string tokenField)
        {
            return usage?[tokenField]?.GetValue<int>().ToString() ?? "0";
        }

        /// <summary>
        /// Enhanced disposal to clean up the data dictionary.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    data.Clear();
                }
                base.Dispose(disposing);
            }
        }
    }
}
