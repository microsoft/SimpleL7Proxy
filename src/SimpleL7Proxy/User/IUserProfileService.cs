using Azure.Storage.Blobs.Models;
using SimpleL7Proxy.Rules;

namespace SimpleL7Proxy.User;

/// <summary>
/// Immutable profile data published as one unit after a successful profile refresh.
/// </summary>
public sealed record UserProfileSnapshot(
    IReadOnlyDictionary<string, string> Headers,
    RuleConfig? Rules,
    RuleProcessor? RuleProcessor,
    bool IsSoftDeleted,
    DateTime? ExpiresAt);

public interface IUserProfileService
{
    public (Dictionary<string, string> profile, bool isSoftDeleted, bool isStale) GetUserProfile(string userId);
    public UserProfileSnapshot? GetUserProfileSnapshot(string userId);
    public bool IsUserSuspended(string userId);
    public bool IsAuthAppIDValid(string authAppId);
    public AsyncClientInfo? GetAsyncParams(string UserId);
}