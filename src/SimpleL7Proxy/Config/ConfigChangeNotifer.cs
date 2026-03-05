using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Config;

/// <summary>
/// Singleton service that manages config-change subscriptions.
/// Subscribers specify which fields they care about; the notifier filters
/// and only calls them when those fields change.
/// <para>
/// Usage:
/// <code>
/// var notifier = serviceProvider.GetRequiredService&lt;ConfigChangeNotifier&gt;();
///
/// // Subscribe to specific fields:
/// notifier.Subscribe(mySubscriber, "LogConsole", "Workers");
///
/// // Or with a lambda for specific fields:
/// notifier.Subscribe((changes, opts, ct) =>
/// {
///     Console.WriteLine($"{changes.Count} setting(s) changed");
///     return Task.CompletedTask;
/// }, "LogConsole", "Workers");
///
/// // Subscribe to ALL changes (no filter):
/// notifier.Subscribe(mySubscriber);
///
/// // Unsubscribe when done:
/// notifier.Unsubscribe(mySubscriber);
/// </code>
/// </para>
/// </summary>
public class ConfigChangeNotifier
{
    private readonly List<Subscription> _subscriptions = [];
    private readonly object _lock = new();
    private readonly ILogger<ConfigChangeNotifier> _logger;

    public ConfigChangeNotifier(ILogger<ConfigChangeNotifier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Register a subscriber for changes to specific fields.
    /// Pass field names (ConfigName / env var names, e.g. "LogConsole", "Workers").
    /// If no fields are specified, the subscriber receives all changes.
    /// </summary>
    public void Subscribe(IConfigChangeSubscriber subscriber, params string[] fields)
    {
        var filter = fields.Length > 0
            ? new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase)
            : null; // null = wildcard (all changes)

        lock (_lock)
        {
            _subscriptions.Add(new Subscription(subscriber, filter));
        }

        var fieldDesc = filter != null ? string.Join(", ", filter) : "*";
        _logger.LogInformation("[CONFIG] Subscriber registered: {Name} for fields: [{Fields}]",
            subscriber.GetType().Name, fieldDesc);
    }

    /// <summary>
    /// Register a callback for changes to specific fields.
    /// Returns a handle that can be passed to <see cref="Unsubscribe"/>.
    /// </summary>
    public IConfigChangeSubscriber Subscribe(
        Func<IReadOnlyList<ConfigChange>, BackendOptions, CancellationToken, Task> callback,
        params string[] fields)
    {
        var wrapper = new DelegateSubscriber(callback);
        Subscribe(wrapper, fields);
        return wrapper;
    }

    /// <summary>Remove a previously registered subscriber.</summary>
    public void Unsubscribe(IConfigChangeSubscriber subscriber)
    {
        lock (_lock)
        {
            _subscriptions.RemoveAll(s => s.Subscriber == subscriber);
        }
        _logger.LogInformation("[CONFIG] Subscriber removed: {Name}", subscriber.GetType().Name);
    }

    /// <summary>
    /// Returns a precomputed view of subscribed fields.
    /// If <c>HasWildcardSubscriber</c> is true, all fields are considered subscribed.
    /// </summary>
    public (bool HasWildcardSubscriber, HashSet<string> SubscribedFields) GetSubscribedFieldSet()
    {
        Subscription[] snapshot;
        lock (_lock)
        {
            if (_subscriptions.Count == 0)
            {
                return (false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            snapshot = [.. _subscriptions];
        }

        var subscribedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in snapshot)
        {
            if (sub.Filter == null)
            {
                return (true, subscribedFields);
            }

            subscribedFields.UnionWith(sub.Filter);
        }

        return (false, subscribedFields);
    }

    /// <summary>
    /// Called by the refresh service to fan out notifications.
    /// Each subscriber only receives changes matching its field filter.
    /// Failures are logged but don't stop other subscribers.
    /// </summary>
    internal async Task NotifyAsync(
        IReadOnlyList<ConfigChange> changes,
        BackendOptions backendOptions,
        CancellationToken cancellationToken)
    {
        if (changes.Count == 0) return;

        Subscription[] snapshot;
        lock (_lock)
        {
            if (_subscriptions.Count == 0) return;
            snapshot = [.. _subscriptions];
        }

        // Multiple subscriptions can point to the same subscriber instance.
        // Merge filters and notify each subscriber only once per refresh cycle.
        var subscribers = snapshot
            .GroupBy(s => s.Subscriber)
            .Select(group => new
            {
                Subscriber = group.Key,
                MergedFilter = MergeFilters(group.Select(s => s.Filter))
            });

        foreach (var sub in subscribers)
        {
            // Filter changes to only those the subscriber cares about
            var relevant = sub.MergedFilter != null
                ? changes.Where(c => sub.MergedFilter.Contains(c.PropertyName)).ToList()
                : (IReadOnlyList<ConfigChange>)changes;

            if (relevant.Count == 0) continue;

            try
            {
                _logger.LogDebug("[CONFIG] Notifying {Name} of {Count} change(s)",
                    sub.Subscriber.GetType().Name, relevant.Count);
                await sub.Subscriber.OnConfigChangedAsync(relevant, backendOptions, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CONFIG] Subscriber {Name} failed", sub.Subscriber.GetType().Name);
            }
        }
    }

    private static HashSet<string>? MergeFilters(IEnumerable<HashSet<string>?> filters)
    {
        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filter in filters)
        {
            // null means wildcard: subscriber wants all fields.
            if (filter == null)
            {
                return null;
            }

            merged.UnionWith(filter);
        }

        return merged;
    }

    /// <summary>Tracks a subscriber and its optional field filter.</summary>
    private sealed record Subscription(IConfigChangeSubscriber Subscriber, HashSet<string>? Filter);

    /// <summary>Wraps a lambda/delegate as an <see cref="IConfigChangeSubscriber"/>.</summary>
    private sealed class DelegateSubscriber(
        Func<IReadOnlyList<ConfigChange>, BackendOptions, CancellationToken, Task> callback)
        : IConfigChangeSubscriber
    {
        public Task OnConfigChangedAsync(
            IReadOnlyList<ConfigChange> changes,
            BackendOptions backendOptions,
            CancellationToken cancellationToken) => callback(changes, backendOptions, cancellationToken);
    }
}
