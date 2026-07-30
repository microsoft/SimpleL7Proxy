using System.Threading.Tasks;

namespace SimpleL7Proxy.Auth
{
    /// <summary>
    /// Defines operations for registering backend audiences and retrieving their access tokens.
    /// </summary>
    public interface IBackendTokenProvider
    {
        /// <summary>
        /// Registers an OAuth 2.0 audience for token refresh.
        /// </summary>
        /// <param name="audience">The audience to register.</param>
        void AddAudience(string audience);

        /// <summary>
        /// Returns a valid OAuth 2.0 token for an audience.
        /// </summary>
        /// <param name="audience">The audience whose token is required.</param>
        /// <returns>The access token, or an empty string when no audience is provided.</returns>
        Task<string> OAuth2Token(string? audience = null);

        /// <summary>
        /// Ensures token refresh is running for every registered audience.
        /// </summary>
        void StartTokenRefresh();
    }
}