namespace SimpleL7Proxy.Backend
{

    public enum AuthModeEnum
    {
        None,
        ApiKey,
        OAuth2,
        GcpAuth
    }

    public struct ParsedConfig
    {
        public string Audience;
        public string ApiKey;
        public string ApiKeyHeader;
        public bool DirectMode;
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
        public string PartialPath;
        public string ProbePath;
        public string Processor;
        public bool StripPrefix;
        public bool UseGcpAuth;
        public string GcpProject;        // project name for backend path (e.g. a208790-ellms-preprod)
        public string GcpProjectNumber;  // project number for WIF audience (e.g. 753819451045)
        public string GcpRegion;         // e.g. us-east1
        public string GcpPool;           // WIF pool ID
        public string GcpProvider;       // WIF provider ID
        public string GcpServiceAccount; // GCP SA email to impersonate
        public string GcpAzureClientId;  // Azure resource URI for subject token
        public bool UseOAuth;
        public bool UsesRetryAfter;
        public AuthModeEnum AuthMode;
        public bool Enabled;
    }
}