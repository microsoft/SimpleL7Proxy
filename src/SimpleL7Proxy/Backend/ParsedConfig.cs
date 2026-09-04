namespace SimpleL7Proxy.Backend
{

    public enum AuthModeEnum
    {
        None,
        ApiKey,
        OAuth2
    }

    public enum HostModeEnum
    {
        Apim,
        Direct,
        Indirect
    }

    public struct ParsedConfig
    {
        public string Audience;
        public string AuthProvider;
        public string ApiKey;
        public string ApiKeyHeader;
        public HostModeEnum Mode;
        public string Host;
        private string _hostname;
        public string Hostname
        {
            get { return _hostname; }
            set
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);

                // Accept explicit http/https URLs or a bare host value.
                var normalizedValue = value.Trim();
                if ((Uri.TryCreate(normalizedValue, UriKind.Absolute, out var uri) &&
                     (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) ||
                    Uri.TryCreate($"https://{normalizedValue}", UriKind.Absolute, out uri))
                {
                    _hostname = uri.Host;
                }
                else
                {
                    throw new ArgumentException($"Invalid hostname: {value}", nameof(value));
                }
            }
        }
        public string? IpAddr;
        public int[] AcceptablePriorities;
        public string PartialPath;
        public int PriorityGroup;
        public string ProbePath;
        public string Processor;
        public bool StripPrefix;
        public bool UsesRetryAfter;
        public string Via;
        public AuthModeEnum AuthMode;
        public bool Enabled;
    }
}