namespace SimpleL7Proxy.Backend
{
    public struct ParsedConfig
    {
        public string Audience;
        public bool DirectMode;
        public string Host;
        public string? IpAddr;
        public string PartialPath;
        public string ProbePath;
        public string Processor;
        public bool UseOAuth;
        public bool UsesRetryAfter;
    }
}