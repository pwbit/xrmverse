using System.Globalization;
using System.Security.Claims;
using Meziantou.Framework.Tds.Handler;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Host.Idp;
using Xrmd.Security;
using Xrmd.Tds;
using Xrmd.Tds.Execution;

namespace Xrmd.Host.Tds;

/// <summary>
/// Checks a SQL endpoint login (plan 3.3, Task 4; rulings T4 and T5). Registered in every mode, it accepts only a SQL login (user
/// name and password) in <c>Auth:Mode=Local</c>, checked by <see cref="CredentialService.SignInForSqlLoginAsync"/> (its lockout
/// counts these attempts; a TOTP user is refused), and only for a user the organization would serve over HTTP: the principal it
/// returns carries the user's <c>oid</c> (<c>systemuser.azureactivedirectoryobjectid</c>), and <see cref="CallerResolver"/> must
/// accept it at login (member, enabled, with a role), so a disabled user or a user without roles cannot connect at all.
/// <para>
/// Every other login fails with 18456 and one generic message, whatever the reason: another mode (no local credentials), a login
/// without user name or password (SSPI, or an access token, which the TDS server cannot pass on: ruling T5). The reason goes to
/// the log at Information with the user name, never the password.
/// </para>
/// </summary>
public sealed class TdsAuthenticator(IServiceProvider services, IConfiguration configuration, IDbConnectionFactory factory,
    SecurityCache security, ILogger<TdsAuthenticator> logger) : ITdsAuthenticator
{
    public async ValueTask<TdsAuthenticationResult> AuthenticateAsync(TdsAuthenticationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var userName = context.UserName;
        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(context.Password))
            return Refused(context, "it is not a SQL login with a user name and a password");
        if (configuration["Auth:Mode"] != "Local")
            return Refused(context, "SQL logins are accepted only in Auth:Mode=Local");

        var credentials = services.GetRequiredService<CredentialService>();
        var result = await credentials.SignInForSqlLoginAsync(userName, context.Password, cancellationToken);
        if (result is not SignInResult.Succeeded succeeded)
            return Refused(context, result == SignInResult.LockedOut ? "the credential is locked out" : "the credentials were refused");

        if (await credentials.FindActiveUserAsync(succeeded.SystemUserId, cancellationToken) is not { } user)
            return Refused(context, "the user has no object id or is disabled");

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("oid", user.ObjectId.ToString()),
                new Claim("name", user.Name),
                new Claim("preferred_username", userName),
            ],
            authenticationType: "xrmd-sql-login", nameType: "preferred_username", roleType: "roles"));
        try
        {
            await new CallerResolver(factory, new TdsCurrentUser(principal), security).ResolveAsync(cancellationToken);
        }
        catch (XrmdFaultException ex)
        {
            return Refused(context, "the user is not a usable user of the organization (error 0x"
                                    + ex.ErrorCode.ToString("x8", CultureInfo.InvariantCulture) + ")");
        }

        logger.LogInformation("SQL endpoint login of {UserName} (user {UserId}) from {RemoteEndPoint}.", userName,
            succeeded.SystemUserId, context.RemoteEndPoint);
        return TdsAuthenticationResult.Success(Database, principal);
    }

    /// <summary>The database the session reports: the organization database's name.</summary>
    private string Database =>
        StorageConnectionStrings.DatabaseName(configuration.GetConnectionString("Org")) is { Length: > 0 } name ? name : "xrmverse";

    private TdsAuthenticationResult Refused(TdsAuthenticationContext context, string reason)
    {
        // The user name may be logged; the password and any token never are.
        logger.LogInformation("SQL endpoint login of {UserName} from {RemoteEndPoint} refused: {Reason}.", context.UserName,
            context.RemoteEndPoint, reason);
        return TdsErrors.LoginFailedResult();
    }
}
