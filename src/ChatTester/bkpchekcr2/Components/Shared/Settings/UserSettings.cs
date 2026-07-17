namespace chat_tester.Components.Shared;

/// <summary>
/// Settings that control how a user identity header is attached to each request.
/// Registered as a singleton so the values are remembered across page navigation
/// and across every request in a burst.
/// </summary>
public class UserSettings
{
    /// <summary>Header used to carry the user identity (e.g. x-user-id).</summary>
    public string HeaderName { get; set; } = string.Empty;

    /// <summary>Header used to carry the per-user priority key (e.g. S7PPriorityKey).</summary>
    public string PriorityHeaderName { get; set; } = string.Empty;

    /// <summary>"None", "Selected", "Random", or "Rotating".</summary>
    public string SelectionMode { get; set; } = "None";

    /// <summary>The chosen user name when <see cref="SelectionMode"/> is "Selected".</summary>
    public string SelectedUser { get; set; } = string.Empty;

    /// <summary>
    /// Newline-separated candidate users. Each line is a user name with an optional
    /// priority key after a comma, e.g. <c>alice, 12345</c>.
    /// </summary>
    public string UserListText { get; set; } = string.Empty;

    /// <summary>A single configured user and its optional priority key.</summary>
    public sealed record UserEntry(string Name, string Priority);

    /// <summary>
    /// Applies the configured header names and user list only when they have not
    /// already been set, so a user's edits survive navigation between pages.
    /// </summary>
    public void ApplyDefaultsIfMissing(string headerName, string priorityHeaderName, string[] userNames)
    {
        if (string.IsNullOrWhiteSpace(HeaderName))
        {
            HeaderName = headerName;
        }

        if (string.IsNullOrWhiteSpace(PriorityHeaderName))
        {
            PriorityHeaderName = priorityHeaderName;
        }

        if (string.IsNullOrWhiteSpace(UserListText) && userNames is { Length: > 0 })
        {
            UserListText = string.Join(Environment.NewLine, userNames);
        }
    }

    /// <summary>Parses the user list into trimmed, non-empty entries with optional priority keys.</summary>
    public IReadOnlyList<UserEntry> GetUsers()
        => UserListText
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(ParseEntry)
            .ToList();

    private static UserEntry ParseEntry(string line)
    {
        var separator = line.IndexOf(',');
        if (separator < 0)
        {
            return new UserEntry(line.Trim(), string.Empty);
        }

        var name = line[..separator].Trim();
        var priority = line[(separator + 1)..].Trim();
        return new UserEntry(name, priority);
    }

    /// <summary>
    /// Resolves the user to send for a given request index. Returns <c>null</c>
    /// when user identity is disabled or no users are configured.
    /// </summary>
    public UserEntry? ResolveUser(int requestIndex)
    {
        if (SelectionMode.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var users = GetUsers();
        if (users.Count == 0)
        {
            return null;
        }

        if (SelectionMode.Equals("Selected", StringComparison.OrdinalIgnoreCase))
        {
            return users.FirstOrDefault(u => u.Name.Equals(SelectedUser, StringComparison.OrdinalIgnoreCase))
                ?? users[0];
        }

        if (SelectionMode.Equals("Random", StringComparison.OrdinalIgnoreCase))
        {
            return users[Random.Shared.Next(users.Count)];
        }

        var index = ((requestIndex % users.Count) + users.Count) % users.Count;
        return users[index];
    }

    /// <summary>Resolves the user name to send for the given request index.</summary>
    public string ResolveUserName(int requestIndex) => ResolveUser(requestIndex)?.Name ?? string.Empty;

    /// <summary>Applies the resolved user and priority headers to the request when enabled.</summary>
    public void ApplyUser(HttpRequestMessage request, int requestIndex)
    {
        if (request is null)
        {
            return;
        }

        var entry = ResolveUser(requestIndex);
        if (entry is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(HeaderName) && !string.IsNullOrWhiteSpace(entry.Name))
        {
            request.Headers.Remove(HeaderName);
            request.Headers.TryAddWithoutValidation(HeaderName, entry.Name);
        }

        if (!string.IsNullOrWhiteSpace(PriorityHeaderName) && !string.IsNullOrWhiteSpace(entry.Priority))
        {
            request.Headers.Remove(PriorityHeaderName);
            request.Headers.TryAddWithoutValidation(PriorityHeaderName, entry.Priority);
        }
    }
}

