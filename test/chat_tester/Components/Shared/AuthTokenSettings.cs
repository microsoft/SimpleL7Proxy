namespace chat_tester.Components.Shared;

public class AuthTokenSettings
{
    public string ServerBaseUrl { get; set; } = string.Empty;
    public bool UseAuthorization { get; set; }
    public string HeaderName { get; set; } = string.Empty;
    public string HeaderValuePrefix { get; set; } = "Bearer";
    public string AuthMode { get; set; } = "OAuth";
    public string KeyValue { get; set; } = string.Empty;
    public string TokenSource { get; set; } = "Manual";
    public string TokenValue { get; set; } = string.Empty;
    public string TokenFetchUrl { get; set; } = string.Empty;
    public string TokenFetchMethod { get; set; } = "GET";
    public string TokenFetchBody { get; set; } = string.Empty;
    public string TokenResponseProperty { get; set; } = "access_token";

    /// <summary>
    /// Applies the configured header name and value prefix only when they have not
    /// already been set, so a user's edits survive navigation between pages.
    /// </summary>
    public void ApplyDefaultsIfMissing(string headerName, string headerValuePrefix)
    {
        if (string.IsNullOrWhiteSpace(HeaderName))
        {
            HeaderName = headerName;
        }

        if (string.IsNullOrWhiteSpace(HeaderValuePrefix))
        {
            HeaderValuePrefix = headerValuePrefix;
        }
    }
}
