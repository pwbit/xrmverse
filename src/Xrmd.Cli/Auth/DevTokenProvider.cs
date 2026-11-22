using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Xrmd.Cli.Config;

namespace Xrmd.Cli.Auth;

/// <summary>
/// Mode <c>dev</c>: mints the same HS256 token as <c>docker/dev-token.sh</c> (iss <c>xrmverse-dev</c>, aud = server
/// URL, oid, name, nbf = now, exp = now + 3600) with the key from the profile's environment variable.
/// </summary>
public sealed class DevTokenProvider(Profile profile, CliEnvironment env) : ITokenProvider
{
    public const string DefaultSigningKeyEnv = "XRMD_DEV_SIGNING_KEY";
    /// <summary>The seed administrator of <c>docker/.env.example</c>.</summary>
    public const string DefaultObjectId = "00000000-0000-0000-0000-000000000001";
    public const string Issuer = "xrmverse-dev";
    public const string Name = "Administrator";
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(3600);

    public Task<Profile> LoginAsync(CancellationToken cancellationToken)
    {
        _ = CreateToken(); // fails now, not at the first command, when the key is missing
        return Task.FromResult(profile);
    }

    public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult(CreateToken());

    public Task LogoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string CreateToken()
    {
        var variable = profile.SigningKeyEnv ?? DefaultSigningKeyEnv;
        var key = env.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(key))
            throw new CliException(ExitCodes.Auth, $"The dev signing key is missing: set the environment variable {variable}.");

        var now = env.Clock.GetUtcNow();
        // Whole seconds, like `date +%s` in dev-token.sh.
        var notBefore = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()).UtcDateTime;
        var token = new JwtSecurityToken(
            Issuer,
            profile.Url,
            [new Claim("oid", profile.ObjectId ?? DefaultObjectId), new Claim("name", Name)],
            notBefore,
            notBefore + Lifetime,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
