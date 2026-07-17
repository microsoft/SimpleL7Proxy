namespace chat_tester.Components.Shared;

/// <summary>
/// Shared, server-lifetime settings for request debugging. Registered as a singleton
/// so the choice is remembered across pages and reloads.
/// </summary>
public class RequestDebugSettings
{
    /// <summary>When true, requests include the <c>S7PDEBUG: true</c> header.</summary>
    public bool DebugEnabled { get; set; }

    public const string DebugHeaderName = "S7PDEBUG";
    public const string DebugHeaderValue = "true";
}
