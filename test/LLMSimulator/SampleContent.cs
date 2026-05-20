using System.Collections.Concurrent;

namespace Company.Function
{
    /// <summary>Lazy in-memory cache for the Samples/*.txt files \u2014 read once, served many times.</summary>
    internal static class SampleContent
    {
        private static readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Returns the contents of <c>Samples/<paramref name="fileName"/></c>, or an empty body if missing.</summary>
        public static string Get(string fileName)
        {
            return _cache.GetOrAdd(fileName, key =>
            {
                // Function host runs from the build/publish output; Samples/ is copied there via csproj.
                var path = Path.Combine(AppContext.BaseDirectory, "Samples", key);
                return File.Exists(path) ? File.ReadAllText(path) : $"[sample '{key}' not found]";
            });
        }
    }
}
