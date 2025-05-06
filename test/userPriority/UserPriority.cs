using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

public class UserPriority1
{
    private readonly int threshold;
    private readonly int expirationTimeInSeconds;
    private readonly Dictionary<string, List<(Guid requestId, DateTime timestamp)>> userRequests;

    public UserPriority1(int threshold, int expirationTimeInSeconds)
    {
        this.threshold = threshold;
        this.expirationTimeInSeconds = expirationTimeInSeconds;
        userRequests = new Dictionary<string, List<(Guid requestId, DateTime timestamp)>>();
    }

    public (int priority, Guid requestId) AddRequest(string userId)
    {
        if (!userRequests.ContainsKey(userId))
        {
            userRequests[userId] = new List<(Guid requestId, DateTime timestamp)>();
        }

        var requestId = Guid.NewGuid();
        userRequests[userId].Add((requestId, DateTime.Now));
        int priority = CalculatePriority(userId);
        return (priority, requestId);
    }

    private int CalculatePriority(string userId)
    {
        int requestCount = userRequests[userId].Count;
        if (requestCount > threshold)
        {
            return requestCount;
        }
        return -requestCount;
    }

    public void RemoveRequest(string userId, Guid requestId)
    {
        if (userRequests.ContainsKey(userId))
        {
            userRequests[userId].RemoveAll(request => request.requestId == requestId);
        }
    }

    public void ResetUserRequestCount(string userId)
    {
        if (userRequests.ContainsKey(userId))
        {
            userRequests[userId].Clear();
        }
    }
}

public class UserPriority
{
    private readonly int threshold;
    private readonly int expirationTimeInSeconds;
    private readonly Dictionary<string, List<(Guid requestId, DateTime timestamp)>> userRequests;

    public UserPriority(int threshold, int expirationTimeInSeconds)
    {
        this.threshold = threshold;
        this.expirationTimeInSeconds = expirationTimeInSeconds;
        userRequests = new Dictionary<string, List<(Guid requestId, DateTime timestamp)>>();
    }

    public (int priority, Guid requestId, DateTime timestamp) AddRequest(string userId)
    {
        if (!userRequests.ContainsKey(userId))
        {
            userRequests[userId] = new List<(Guid requestId, DateTime timestamp)>();
        }

        var requestId = Guid.NewGuid();
        var timestamp = DateTime.Now;
        userRequests[userId].Add((requestId, timestamp));
        int priority = CalculatePriority(userId);
        return (priority, requestId, timestamp);
    }

    private int CalculatePriority(string userId)
    {
        int requestCount = userRequests[userId].Count;
        DateTime lastRequestTime = userRequests[userId].Last().timestamp;
        TimeSpan timeSinceLastRequest = DateTime.Now - lastRequestTime;

        if (requestCount > threshold)
        {
            return requestCount + (int)timeSinceLastRequest.TotalSeconds;
        }
        return -requestCount + (int)timeSinceLastRequest.TotalSeconds;
    }

    public void RemoveRequest(string userId, Guid requestId)
    {
        if (userRequests.ContainsKey(userId))
        {
            userRequests[userId].RemoveAll(request => request.requestId == requestId);
        }
    }

    public void ResetUserRequestCount(string userId)
    {
        if (userRequests.ContainsKey(userId))
        {
            userRequests[userId].Clear();
        }
    }
}