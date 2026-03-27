namespace SimpleL7Proxy.Backend
{
    public struct ParsedConfig
    {
        public string Audience;
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
        public bool UseOAuth;
        public bool UsesRetryAfter;
    }
}