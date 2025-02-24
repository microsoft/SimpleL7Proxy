using System;
using System.Collections.Concurrent;
using System.Threading;

public class UserPriority : IUserPriority
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> userRequests = 
        new ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>>();
    private int total = 0;

    public float threshold { get; set; }

    public string GetState()
    {
        return $"Users: {userRequests.Count} Total Requests: {getTotalRequests()} ";
    }

    public UserPriority()
    {
    }

    /// <summary>
    /// Adds a new request for the specified user.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <returns>The unique identifier (GUID) of the added request.</returns>
    public Guid addRequest(string userId)
    {
        Guid requestId = Guid.NewGuid();

        // Get or create the inner ConcurrentDictionary for the user
        var requests = userRequests.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, byte>());

        // Add the new request
        requests.TryAdd(requestId, 0);

        // Atomically increment the total count
        Interlocked.Increment(ref total);

        return requestId;
    }

    /// <summary>
    /// Removes a specific request for the specified user.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="requestId">The unique identifier (GUID) of the request to remove.</param>
    /// <returns>True if the request was successfully removed; otherwise, false.</returns>
    public bool removeRequest(string userId, Guid requestId)
    {
        if (userRequests.TryGetValue(userId, out var requests))
        {
            // Attempt to remove the request
            if (requests.TryRemove(requestId, out _))
            {
                // If no more requests exist for the user, remove the user entry
                if (requests.IsEmpty)
                {
                    userRequests.TryRemove(userId, out _);
                }

                // Atomically decrement the total count
                Interlocked.Decrement(ref total);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Determines whether the user's request load requires boosting based on the threshold.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="boostValue">The calculated boost value.</param>
    /// <returns>True if boosting is necessary; otherwise, false.</returns>
    public bool boostIndicator(string userId, out float boostValue)
    {
        boostValue = 0.0f;

        // Attempt to retrieve the user's requests
        if (!userRequests.TryGetValue(userId, out var requests))
        {
            // If the user has no existing requests, apply boost
            return true;
        }

        // Read the current total atomically
        int currentTotal = Volatile.Read(ref total);

        // Prevent division by zero
        if (currentTotal == 0)
        {
            return false;
        }

        // Calculate the boost value as the proportion of the user's requests
        boostValue = requests.Count / (float)currentTotal;
        
        // Determine if boosting is necessary based on the threshold
        return boostValue < threshold;
    }

    /// <summary>
    /// Retrieves the current total number of requests.
    /// </summary>
    /// <returns>The total number of requests.</returns>
    public int getTotalRequests()
    {
        return Volatile.Read(ref total);
    }

    /// <summary>
    /// Retrieves all user IDs currently tracked.
    /// </summary>
    /// <returns>A collection of user IDs.</returns>
    public IEnumerable<string> GetAllUserIds()
    {
        return userRequests.Keys;
    }

    /// <summary>
    /// Retrieves all request IDs for a specified user.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <returns>A collection of request GUIDs for the user.</returns>
    public IEnumerable<Guid> getUserRequests(string userId)
    {
        if (userRequests.TryGetValue(userId, out var requests))
        {
            return requests.Keys;
        }
        return Enumerable.Empty<Guid>();
    }
}