namespace SimpleL7Proxy.Config;

public class AppConfigurationSnapshot
{
    private readonly object _lock = new();
    private IReadOnlyDictionary<string, string> _snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public void Replace(IDictionary<string, string> values)
    {
        lock (_lock)
        {
            _snapshot = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyDictionary<string, string> GetSnapshot()
    {
        lock (_lock)
        {
            return _snapshot;
        }
    }
}
