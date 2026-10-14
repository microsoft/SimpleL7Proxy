namespace SimpleL7Proxy.StreamProcessor
{
    /// <summary>
    /// Stream processor for OpenAI/Azure OpenAI streaming responses that extracts the token
    /// usage and content-safety signals needed for Tokenomics accounting: prompt, completion,
    /// and cached token counts, plus jailbreak detection and content-filter flags.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="OpenAIProcessor"/>, this processor captures every line of the stream
    /// (<see cref="CaptureAllLines"/> is true) because the jailbreak signal is only present in
    /// the prompt filter results of the first streamed chunk, and content-filter results are
    /// spread across every chunk. Capturing all lines increases per-request memory usage, so
    /// this processor should be selected only when Tokenomics accounting requires these signals.
    /// </remarks>
    public class TokenomicsProcessor : UsageStreamProcessor
    {
        public TokenomicsProcessor()
            : base(includeSafetySignals: true)
        {
        }
    }
}
