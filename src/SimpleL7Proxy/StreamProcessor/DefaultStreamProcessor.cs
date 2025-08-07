
using System.Text.Json.Nodes;
using System.Net.Http.Headers;
using SimpleL7Proxy.Events; 

namespace SimpleL7Proxy.StreamProcessor
{
    /// <summary>
    /// Stream processor implementation for Anthropic-specific stream processing.
    /// </summary>
    public class DefaultStreamProcessor : IStreamProcessor
    {
        private Dictionary<string, string> data;

        public DefaultStreamProcessor()
        {
            data = new();
        }

        /// <summary>
        /// Copies content from the source stream to the destination output stream.
        /// </summary>
        /// <param name="sourceStream">The source stream to read from.</param>
        /// <param name="outputStream">The destination stream to write to.</param>
        /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
        /// <returns>A task representing the asynchronous copy operation.</returns>
        public async Task CopyToAsync(System.Net.Http.HttpContent sourceContent, Stream outputStream, CancellationToken? cancellationToken)
        {
            var lastLineBuilder = new List<string>();
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
                    //lastLineBuilder.Add(currentLine);
                    if (currentLine?.Length > 6) {
                        last2ndLine = lastLine;
                        lastLine = currentLine;
                    }
                }
            }
            catch (IOException e)
            {
                if (!e.Message.Contains("Connection reset by peer"))
                {
                    data["LastError"] = e.Message;
                    throw;
                }
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
        //// Note: This class needs to be updated to handle Antro-specific processing logic
        /// </summary>



        /// <summary>
        /// Processes the last line to extract statistics and information.
        /// </summary>
        /// <param name="lastLine">The last line from the stream.</param>
        private void ProcessLastLine(string lastLine)
        {
            try
            {
                // Parse JSON to extract OpenAI usage information
                if (!string.IsNullOrWhiteSpace(lastLine))
                {
                    // Line starts with data: so we need to remove that prefix
                    if (lastLine.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
                    {
                        lastLine = lastLine.Substring(6).Trim();
                    }
                    var jsonNode = JsonNode.Parse(lastLine);
                    if (jsonNode != null)
                    {
                        // Extract usage statistics
                        var usage = jsonNode["usage"];
                        if (usage != null)
                        {
                            data["CompletionTokens"] = usage["completion_tokens"]?.GetValue<int>().ToString() ?? "0";
                            data["PromptTokens"] = usage["prompt_tokens"]?.GetValue<int>().ToString() ?? "0";
                            data["TotalTokens"] = usage["total_tokens"]?.GetValue<int>().ToString() ?? "0";
                        }
                        else
                        {
                            data["CompletionTokens"] = "0";
                            data["PromptTokens"] = "0";
                            data["TotalTokens"] = "0";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Not able to parse the last line, log the error
                data["ParseError"] = ex.Message;
            }
            
            throw new Exception("Antro-specific processing logic needs to be implemented here.");
        }

        /// <summary>
        /// Gets statistics about the stream processing operation.
        /// </summary>
        /// <returns>A dictionary containing processing statistics.</returns>
        public void GetStats(ProxyEvent eventData, HttpResponseHeaders headers)
        {
            eventData["CompletionTokens"] = data["CompletionTokens"];
            eventData["PromptTokens"] = data["PromptTokens"];
            eventData["TotalTokens"] = data["TotalTokens"];
        }
    }
}