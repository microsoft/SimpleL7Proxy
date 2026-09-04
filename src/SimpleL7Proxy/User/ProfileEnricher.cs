using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Proxy;

namespace SimpleL7Proxy.User;

/// <summary>
/// Applies cached profile headers and rules to an incoming request.
/// </summary>
public sealed class ProfileEnricher
{
    private readonly ProxyConfig _options;
    private readonly IUserProfileService _userProfiles;
    private readonly ILogger<ProfileEnricher> _logger;

    public ProfileEnricher(
        ProxyConfig options,
        IUserProfileService userProfiles,
        ILogger<ProfileEnricher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(userProfiles);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _userProfiles = userProfiles;
        _logger = logger;
    }

    /// <summary>
    /// Applies the selected profile and computes the request user ID before evaluating profile rules.
    /// Returns applied rule names, suffixing applied else branches with "-else".
    /// </summary>
    public string[] Enrich(RequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        UserProfileSnapshot? snapshot = null;

        if (_options.UseProfiles)
        {
            var requestUser = request.Headers[_options.UserProfileHeader];
            if (!string.IsNullOrEmpty(requestUser))
            {
                request.profileUserId = requestUser;

                if (_userProfiles.IsUserSuspended(requestUser))
                {
                    throw new ProxyErrorException(
                        ProxyErrorException.ErrorType.UnknownProfile,
                        System.Net.HttpStatusCode.Forbidden,
                        "User is suspended: " + requestUser);
                }

                snapshot = _userProfiles.GetUserProfileSnapshot(requestUser);

                if (snapshot is null)
                {
                    if (request.Debug)
                    {
                        _logger.LogInformation("User profile for {User} not found.", requestUser);
                    }

                    throw new ProxyErrorException(
                        ProxyErrorException.ErrorType.UnknownProfile,
                        System.Net.HttpStatusCode.Forbidden,
                        "User profile not found: " + requestUser);
                }

                foreach (var header in snapshot.Headers)
                {
                    request.Headers.Set(header.Key, header.Value);
                    if (request.Debug)
                    {
                        _logger.LogInformation("Add Header: {Header} = {Value}", header.Key, header.Value);
                    }
                }
            }
            else if (_options.UserConfigRequired)
            {
                throw new ProxyErrorException(
                    ProxyErrorException.ErrorType.UnknownProfile,
                    System.Net.HttpStatusCode.Forbidden,
                    "User profile not found: " + requestUser);
            }
        }

        SetUserId(request);

        if (snapshot?.RuleProcessor is { } ruleProcessor)
        {
            return ApplyRules(request, ruleProcessor);
        }

        return [];
    }

    private void SetUserId(RequestData request)
    {
        request.UserID = string.Empty;

        foreach (var header in _options.UniqueUserHeaders)
        {
            request.UserID += request.Headers[header] ?? string.Empty;
        }

        if (string.IsNullOrEmpty(request.UserID))
        {
            request.UserID = "defaultUser";
        }
    }

    private string[] ApplyRules(RequestData request, Rules.RuleProcessor ruleProcessor)
    {
        var context = new Dictionary<string, string>(request.Headers.Count + 4, StringComparer.OrdinalIgnoreCase);
        var matchedRuleNames = new List<string>();

        foreach (var key in request.Headers.AllKeys)
        {
            if (key is not null && request.Headers[key] is { } value)
            {
                context[key] = value;
            }
        }

        context["Path"] = request.Path;
        context["Method"] = request.Method;
        context["UserID"] = request.UserID;
        context["ProfileUserID"] = request.profileUserId;

        foreach (var result in ruleProcessor.Process(context, request.S7PHash, matchedRuleNames))
        {
            foreach (var pair in result)
            {
                request.Headers.Set(pair.Key, pair.Value);
                if (request.Debug)
                {
                    _logger.LogInformation("Add Rule Header: {Header} = {Value}", pair.Key, pair.Value);
                }
            }
        }

        return matchedRuleNames.ToArray();
    }
}