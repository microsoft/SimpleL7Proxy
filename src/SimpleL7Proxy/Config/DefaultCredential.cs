using Microsoft.Extensions.Options;
using Azure.Identity;

namespace SimpleL7Proxy.Config;

public class DefaultCredential(BackendOptions options)
{
    public DefaultAzureCredential Credential { get; } = 
        new(options.UseOAuthGov == true
            ? new DefaultAzureCredentialOptions { AuthorityHost = AzureAuthorityHosts.AzureGovernment }
            : new DefaultAzureCredentialOptions());
}
