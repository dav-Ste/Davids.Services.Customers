using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Davids.Services.Customers.Infrastructure.Auth;

public class OidcAuthoritySettings
{
    public string? Authority { get; set; }
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public string? PublicKeyPem { get; set; }

    public string? MetadataAddress
        => string.IsNullOrWhiteSpace(Authority)
            ? null
            : Authority.TrimEnd('/') + "/.well-known/openid-configuration";

    public string? EffectiveIssuer
    {
        get
        {
            var raw = !string.IsNullOrWhiteSpace(Issuer) ? Issuer : Authority;
            return string.IsNullOrWhiteSpace(raw) ? null : raw.TrimEnd('/') + "/";
        }
    }

    public RsaSecurityKey? ValidateAndResolveStaticPublicKey(string sectionName)
    {
        if (string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException($"{sectionName}:Audience must be configured.");
        }

        var hasAuthority = !string.IsNullOrWhiteSpace(Authority);
        var hasPublicKey = !string.IsNullOrWhiteSpace(PublicKeyPem);
        if (!hasAuthority && !hasPublicKey)
        {
            throw new InvalidOperationException(
                $"{sectionName}:Authority (OIDC discovery URL) or {sectionName}:PublicKeyPem (RSA public key) must be configured.");
        }

        if (hasAuthority &&
            (!Uri.TryCreate(Authority, UriKind.Absolute, out var authorityUri) ||
             (authorityUri.Scheme != Uri.UriSchemeHttp && authorityUri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException($"{sectionName}:Authority must be an absolute HTTP(S) URI.");
        }

        if (!hasPublicKey)
        {
            return null;
        }

        var trimmed = PublicKeyPem!.Trim();
        if (trimmed.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{sectionName}:PublicKeyPem must contain an RSA PUBLIC key only; resource servers never hold private signing keys.");
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(trimmed);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException($"{sectionName}:PublicKeyPem is not a valid PEM RSA public key.", ex);
        }

        if (rsa.KeySize < 2048)
        {
            throw new InvalidOperationException($"{sectionName}:PublicKeyPem must be at least 2048 bits.");
        }

        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var kid = Base64UrlEncoder.Encode(SHA256.HashData(spki))[..16];
        return new RsaSecurityKey(parameters) { KeyId = kid };
    }
}
