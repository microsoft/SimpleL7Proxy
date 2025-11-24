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
                // remove the protocol from the hostname if present
                var uri = new Uri(value);
                _hostname = uri.Host;
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