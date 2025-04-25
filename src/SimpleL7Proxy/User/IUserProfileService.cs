namespace SimpleL7Proxy.User;
public interface IUserProfileService
{
    public Dictionary<string, string> GetUserProfile(string userId);
    public bool IsAuthAppIDValid(string? authAppId);

}