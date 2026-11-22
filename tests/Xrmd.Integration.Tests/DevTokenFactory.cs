using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Xrmd.Integration.Tests;

public static class DevTokenFactory
{
    /// <summary>Shared with <see cref="XrmdFixture"/> as Auth:DevSigningKey.</summary>
    public const string SigningKey = "test-signing-key-0123456789-abcdefghijklmnop";
    public const string Issuer = "xrmverse-dev";

    public static string Create(Guid oid, string audience, string name = "Test User", TimeSpan? lifetime = null)
    {
        var life = lifetime ?? TimeSpan.FromHours(1);
        var now = DateTime.UtcNow;
        // Negative lifetime: shift the whole validity window into the past (notBefore must stay <= expires).
        var expires = now + life;
        var notBefore = life < TimeSpan.Zero ? expires - TimeSpan.FromMinutes(5) : now;
        var token = new JwtSecurityToken(
            Issuer,
            audience,
            [new Claim("oid", oid.ToString()), new Claim("name", name)],
            notBefore,
            expires,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
