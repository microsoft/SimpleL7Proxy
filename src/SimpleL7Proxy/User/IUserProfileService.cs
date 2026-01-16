using Azure.Storage.Blobs.Models;

namespace SimpleL7Proxy.User;

public interface IUserProfileService
{
    public Dictionary<string, string> GetUserProfile(string userId);
    public bool IsAuthAppIDValid(string? authAppId);
    //public bool AsyncAllowed(string UserId);
    public AsyncClientInfo? GetAsyncParams(string UserId);
    public bool ServiceIsReady();

}