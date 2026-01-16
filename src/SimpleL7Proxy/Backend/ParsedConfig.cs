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
                // Handle both full URIs (with scheme) and host-only strings
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) || 
                    Uri.TryCreate($"https://{value}", UriKind.Absolute, out uri))
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
        public bool UseOAuth;
        public bool UsesRetryAfter;
    }
}