namespace SimpleL7Proxy.User
{
public interface IUserPriorityService
{
    string GetState();
    Guid addRequest(string userId);
    void addRequest(Guid requestId, string userId);
    bool removeRequest(string userId, Guid requestId);
    public bool boostIndicator(string userId, out float boostValue);
    public float threshold { get; set; }
}
}