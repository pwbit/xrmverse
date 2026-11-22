using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.Tokens;

namespace Xrmd.Host.Idp;

/// <summary>
/// The organization's key ring as the host sees it: loaded once at startup in Local mode and read from then on, by the token
/// validation (JwtBearer's <c>IssuerSigningKeyResolver</c> is synchronous) and by the OpenIddict server's options.
/// </summary>
public sealed class LocalSigningKeys
{
    private volatile SecurityKey[] _keys = [];
    private volatile Certificates? _certificates;

    public IReadOnlyCollection<SecurityKey> Keys => _keys;

    /// <summary>The signing and encryption certificates, or null before <see cref="LoadAsync"/>.</summary>
    public (X509Certificate2 Signing, X509Certificate2 Encryption)? Pair =>
        _certificates is { } c ? (c.Signing, c.Encryption) : null;

    public async Task LoadAsync(SigningKeyStore store, CancellationToken ct)
    {
        var (signing, encryption) = await store.GetOrCreateAsync(ct);
        _certificates = new Certificates(signing, encryption);
        _keys = [new X509SecurityKey(signing)];
    }

    private sealed record Certificates(X509Certificate2 Signing, X509Certificate2 Encryption);
}
