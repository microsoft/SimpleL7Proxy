using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace SimpleL7Proxy.Config;

public sealed class IncomingAuthValidator
{
    public string[] Audiences { get; set; } = [];
    public int ClockSkewMinutes { get; set; } = 1;
    public bool Enabled { get; set; }
    public string Header { get; set; } = "S7P-KEY";
    public string Issuer { get; set; } = string.Empty;
    public string MetadataAddress { get; set; } = string.Empty;
    public string Mode { get; set; } = "key";
    public bool RequireSignedTokens { get; set; } = true;
    public Dictionary<string, string> RequiredClaims { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ValidateAudience { get; set; } = true;
    public bool ValidateIssuer { get; set; } = true;
    public bool ValidateIssuerSigningKey { get; set; } = true;
    public bool ValidateLifetime { get; set; } = true;
    public IncomingAuthModeEnum ValidateAuthMode { get; set; } = IncomingAuthModeEnum.Key;
    public bool ValidateAuthViaKey { get; set; } = false;
    public bool ValidateAuthViaOauthHeader { get; set; } = false;
    public string ValidateAuthViaKeyHeader { get; set; } = "S7P-KEY";

    public TokenValidationParameters validationParameters { get; set; } = null!;

    public void Parse(string ValidateAuthConfig)
    {

        if (string.IsNullOrWhiteSpace(ValidateAuthConfig))
        {
            Enabled = false;
            ValidateAuthMode = IncomingAuthModeEnum.None;
            ValidateAuthViaKey = false;
            ValidateAuthViaOauthHeader = false;
            validationParameters = ToTokenValidationParameters();
            return;
        }

        try
        {

            FromConfigString(ValidateAuthConfig);
            ValidateAuthViaKeyHeader = Header;

            var normalizedMode = Mode.Trim().ToLowerInvariant();

            ValidateAuthMode = normalizedMode switch
            {
                "key" => IncomingAuthModeEnum.Key,
                "mixed" => IncomingAuthModeEnum.Mixed,
                "none" => IncomingAuthModeEnum.None,
                "oauth2" or "oauth" => IncomingAuthModeEnum.OAuth2,
                _ => throw new InvalidOperationException($"Unsupported authentication mode: {Mode}")
            };

            if (Enabled && ValidateAuthMode == IncomingAuthModeEnum.None)
            {
                throw new InvalidOperationException("Authentication cannot be enabled with mode=none");
            }

            ValidateAuthViaKey = Enabled && (normalizedMode is "key" or "mixed");
            ValidateAuthViaOauthHeader = Enabled && (normalizedMode is "oauth2" or "oauth" or "mixed");

            if (ValidateAuthViaOauthHeader)
            {
                ValidateOAuthSettings();
            }

            validationParameters = ToTokenValidationParameters();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to parse authentication configuration", ex);
        }
    }

    private void FromConfigString(string? rawConfig)
    {
        if (string.IsNullOrWhiteSpace(rawConfig))
            return;

        var separator = rawConfig.Contains(';') ? ';' : ',';
        var parts = rawConfig.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part) || !part.Contains('='))
                continue;

            var idx = part.IndexOf('=');
            var key = part[..idx].Trim();
            var value = part[(idx + 1)..].Trim();

            switch (key.ToLowerInvariant())
            {
                case "enabled":
                    Enabled = bool.TryParse(value, out var enabled) && enabled;
                    break;
                case "mode":
                    Mode = value;
                    break;
                case "header":
                    Header = value;
                    break;
                case "issuer":
                    Issuer = value;
                    break;
                case "metadataaddress":
                    MetadataAddress = value;
                    break;
                case "audience":
                case "audiences":
                    Audiences = ParseList(value);
                    break;
                case "claim":
                case "requiredclaim":
                    var claimKey = value;
                    var claimValue = string.Empty;
                    var claimIdx = value.IndexOf(':');
                    if (claimIdx >= 0)
                    {
                        claimKey = value[..claimIdx].Trim();
                        claimValue = value[(claimIdx + 1)..].Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(claimKey))
                        RequiredClaims[claimKey] = claimValue;
                    break;
                case "validatelifetime":
                    ValidateLifetime = bool.TryParse(value, out var validateLifetime) && validateLifetime;
                    break;
                case "validateissuer":
                    ValidateIssuer = bool.TryParse(value, out var validateIssuer) && validateIssuer;
                    break;
                case "validateaudience":
                    ValidateAudience = bool.TryParse(value, out var validateAudience) && validateAudience;
                    break;
                case "validatesignature":
                case "requiresignedtokens":
                    RequireSignedTokens = bool.TryParse(value, out var requireSignedTokens) && requireSignedTokens;
                    break;
                case "clockskewminutes":
                    ClockSkewMinutes = int.TryParse(value, out var skew) ? skew : ClockSkewMinutes;
                    break;
            }
        }

        return;
    }

    public IncomingAuthValidator DeepClone()
    {
        return new IncomingAuthValidator
        {
            Enabled = Enabled,
            Mode = Mode,
            Header = Header,
            Issuer = Issuer,
            MetadataAddress = MetadataAddress,
            Audiences = (string[])Audiences.Clone(),
            RequiredClaims = new Dictionary<string, string>(RequiredClaims, StringComparer.OrdinalIgnoreCase),
            ValidateIssuer = ValidateIssuer,
            ValidateAudience = ValidateAudience,
            ValidateLifetime = ValidateLifetime,
            ValidateIssuerSigningKey = ValidateIssuerSigningKey,
            RequireSignedTokens = RequireSignedTokens,
            ClockSkewMinutes = ClockSkewMinutes
        };
    }

    public TokenValidationParameters ToTokenValidationParameters()
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = ValidateIssuer && !string.IsNullOrWhiteSpace(Issuer),
            ValidIssuer = string.IsNullOrWhiteSpace(Issuer) ? null : Issuer,
            ValidateAudience = ValidateAudience && Audiences.Length > 0,
            ValidAudiences = Audiences.Length > 0 ? Audiences : null,
            ValidateLifetime = ValidateLifetime,
            ClockSkew = TimeSpan.FromMinutes(ClockSkewMinutes),
            ValidateIssuerSigningKey = ValidateIssuerSigningKey,
            RequireSignedTokens = RequireSignedTokens,
            ValidAlgorithms = ValidateAuthViaOauthHeader ? [SecurityAlgorithms.RsaSha256] : null,
            ConfigurationManager = ValidateAuthViaOauthHeader
                ? new ConfigurationManager<OpenIdConnectConfiguration>(
                    MetadataAddress,
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever { RequireHttps = true })
                : null
        };
    }

    private void ValidateOAuthSettings()
    {
        if (!RequireSignedTokens)
        {
            throw new InvalidOperationException("OAuth signature validation cannot be disabled");
        }

        if (!ValidateIssuer || string.IsNullOrWhiteSpace(Issuer))
        {
            throw new InvalidOperationException("OAuth authentication requires issuer validation and a non-empty issuer");
        }

        if (!ValidateAudience || Audiences.Length == 0)
        {
            throw new InvalidOperationException("OAuth authentication requires audience validation and at least one audience");
        }

        if (!ValidateLifetime)
        {
            throw new InvalidOperationException("OAuth lifetime validation cannot be disabled");
        }

        MetadataAddress = string.IsNullOrWhiteSpace(MetadataAddress)
            ? $"{Issuer.TrimEnd('/')}/.well-known/openid-configuration"
            : MetadataAddress;

        if (!Uri.TryCreate(MetadataAddress, UriKind.Absolute, out var metadataUri) ||
            metadataUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("OAuth metadataAddress must be an absolute HTTPS URL");
        }

        MetadataAddress = metadataUri.AbsoluteUri;
        ValidateIssuerSigningKey = true;
    }

    private static string[] ParseList(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            trimmed = trimmed[1..^1];

        return trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim().Trim('"'))
            .Where(x => x.Length > 0)
            .ToArray();
    }
}