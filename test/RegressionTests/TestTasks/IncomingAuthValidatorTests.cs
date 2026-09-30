using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SimpleL7Proxy.Config;
using System.Security.Cryptography;

namespace SimpleL7Proxy.Test;

[TestClass]
public sealed class IncomingAuthValidatorTests : IRegressionTestMetadata
{
    private const string Audience = "api://simple-l7-proxy";
    private const string Issuer = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0";

    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature>
        {
            ["inbound-oauth-security"] = new(
                "Security",
                "Inbound OAuth validation",
                "Ensures OAuth mode rejects unsigned or untrusted tokens and cannot silently bypass authentication.")
        };

    [TestMethod]
    [RegressionTestCase("inbound-oauth-security", "OAuth mode enables signed-token validation", "OAuth mode must use HTTPS OIDC metadata, validate issuer and audience, and require RS256 signatures.")]
    public void Parse_OAuth2Mode_ConfiguresSignatureValidation()
    {
        var validator = CreateOAuthValidator();

        Assert.IsTrue(validator.Enabled);
        Assert.IsFalse(validator.ValidateAuthViaKey);
        Assert.IsTrue(validator.ValidateAuthViaOauthHeader);
        Assert.IsTrue(validator.validationParameters.RequireSignedTokens);
        Assert.IsTrue(validator.validationParameters.ValidateIssuerSigningKey);
        Assert.IsNull(validator.validationParameters.SignatureValidator);
        Assert.IsNotNull(validator.validationParameters.ConfigurationManager);
        CollectionAssert.Contains(
            validator.validationParameters.ValidAlgorithms.ToList(),
            SecurityAlgorithms.RsaSha256);
    }

    [TestMethod]
    [RegressionTestCase("inbound-oauth-security", "Disabled OAuth configuration remains disabled", "Setting enabled=false must not activate either the key or OAuth authentication path.")]
    public void Parse_DisabledOAuth2Mode_DoesNotEnableAuthentication()
    {
        var validator = new IncomingAuthValidator();

        validator.Parse($"enabled=false;mode=oauth2;issuer={Issuer};audience={Audience}");

        Assert.IsFalse(validator.ValidateAuthViaKey);
        Assert.IsFalse(validator.ValidateAuthViaOauthHeader);
        Assert.IsNull(validator.validationParameters.ConfigurationManager);
    }

    [TestMethod]
    [RegressionTestCase("inbound-oauth-security", "OAuth cannot disable signatures", "An OAuth configuration that disables signature validation must fail during configuration parsing.")]
    public void Parse_OAuth2WithoutSignatureValidation_Throws()
    {
        var validator = new IncomingAuthValidator();

        Assert.ThrowsException<InvalidOperationException>(() => validator.Parse(
            $"enabled=true;mode=oauth2;issuer={Issuer};audience={Audience};validatesignature=false"));
    }

    [TestMethod]
    [RegressionTestCase("inbound-oauth-security", "Only trusted signatures are accepted", "Token validation must accept a trusted RS256 signature and reject unsigned or attacker-signed tokens.")]
    public async Task ValidateToken_RequiresTrustedSignature()
    {
        var validator = CreateOAuthValidator();
        using var trustedRsa = RSA.Create(2048);
        using var untrustedRsa = RSA.Create(2048);
        var trustedKey = new RsaSecurityKey(trustedRsa) { KeyId = "signing-key" };
        var untrustedKey = new RsaSecurityKey(untrustedRsa) { KeyId = "signing-key" };
        var handler = new JsonWebTokenHandler();
        var validationParameters = validator.validationParameters.Clone();
        validationParameters.ConfigurationManager = null;
        validationParameters.IssuerSigningKey = trustedKey;

        var trustedToken = CreateToken(handler, new SigningCredentials(trustedKey, SecurityAlgorithms.RsaSha256));
        var untrustedToken = CreateToken(handler, new SigningCredentials(untrustedKey, SecurityAlgorithms.RsaSha256));
        var unsignedToken = CreateToken(handler, null);

        var trustedResult = await handler.ValidateTokenAsync(trustedToken, validationParameters);
        var untrustedResult = await handler.ValidateTokenAsync(untrustedToken, validationParameters);
        var unsignedResult = await handler.ValidateTokenAsync(unsignedToken, validationParameters);

        Assert.IsTrue(trustedResult.IsValid);
        Assert.IsFalse(untrustedResult.IsValid);
        Assert.IsFalse(unsignedResult.IsValid);
    }

    private static IncomingAuthValidator CreateOAuthValidator()
    {
        var validator = new IncomingAuthValidator();
        validator.Parse($"enabled=true;mode=oauth2;header=Authorization;issuer={Issuer};audience={Audience}");
        return validator;
    }

    private static string CreateToken(JsonWebTokenHandler handler, SigningCredentials? signingCredentials)
    {
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Issuer = Issuer,
            SigningCredentials = signingCredentials
        });
    }
}