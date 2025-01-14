
public interface PriorityInterface
{
    (int priority, Guid requestId, DateTime timestamp) AddRequest(string userId);
    void RemoveRequest(string userId, Guid requestId);
    void ResetUserRequestCount(string userId);
}