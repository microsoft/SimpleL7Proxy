using System.Text.Json.Nodes;
using System.Net.Http.Headers;
using SimpleL7Proxy.Events;

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
 * - Captures last line and second-to-last line for statistics extraction
 * - Handles "data: [DONE]" termination pattern common in streaming APIs
 * - Uses consistent exception handling and error data storage
 * 
 * DATA MANAGEMENT:
 * - Maintains protected Dictionary<string, string> for statistics
 * - Provides helper methods for storing error information
 * - Handles disposal of data dictionary properly
 * 
 * TEMPLATE METHODS:
 * - ProcessLastLine: Override to implement specific JSON parsing logic
 * - PopulateEventData: Override to customize how statistics are transferred to events
 * - ShouldIgnoreException: Override to customize exception handling logic
 */

namespace SimpleL7Proxy.StreamProcessor
{
    /// <summary>
    /// Abstract base class for stream processors that handle JSON-based streaming APIs
    /// with last-line statistics extraction patterns.
    /// </summary>
    public abstract class JsonStreamProcessor : BaseStreamProcessor
    {
        protected Dictionary<string, string> data = new();

        /// <summary>
        /// Implements the common streaming pattern used by JSON-based processors.
        /// </summary>
        public override async Task CopyToAsync(System.Net.Http.HttpContent sourceContent, Stream outputStream, CancellationToken? cancellationToken)
        {
            string lastLine = string.Empty;
            string last2ndLine = string.Empty;

            try
            {
                using var sourceStream = await sourceContent.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new StreamReader(sourceStream);
                using var writer = new StreamWriter(outputStream) { AutoFlush = true };

                string? currentLine;

                // Stream all content immediately while collecting lines for last line detection
                while ((currentLine = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    cancellationToken?.ThrowIfCancellationRequested();

                    // Write each line immediately - no delays
                    await writer.WriteLineAsync(currentLine).ConfigureAwait(false);

                    // Keep track of lines for last line processing
                    if (currentLine?.Length > 6)
                    {
                        last2ndLine = lastLine;
                        lastLine = currentLine;
                    }
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
                var lineToProcess = lastLine.Contains("data: [DONE]") ? last2ndLine : lastLine;

                // Only process the last line for statistics/parsing after stream has terminated
                if (!string.IsNullOrEmpty(lineToProcess))
                {
                    try
                    {
                        ProcessLastLine(lineToProcess);
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
        /// Implements the common pattern of transferring data dictionary to event data.
        /// </summary>
        public override void GetStats(ProxyEvent eventData, HttpResponseHeaders headers)
        {
            PopulateEventData(eventData, headers);
        }

        /// <summary>
        /// Template method for processing the last line of JSON content.
        /// Derived classes must implement this to extract specific statistics.
        /// </summary>
        /// <param name="lastLine">The last significant line from the stream.</param>
        protected abstract void ProcessLastLine(string lastLine);

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
