namespace SimpleL7Proxy.Backend
{
    /// <summary>
    /// Result of path matching operation, containing both match status and stripped path.
    /// </summary>
    public readonly struct PathMatchResult
    {
        public bool IsMatch { get; init; }
        public string StrippedPath { get; init; }

        public static PathMatchResult NoMatch(string originalPath) => 
            new() { IsMatch = false, StrippedPath = originalPath };
        
        public static PathMatchResult Match(string strippedPath) => 
            new() { IsMatch = true, StrippedPath = strippedPath };
    }
}