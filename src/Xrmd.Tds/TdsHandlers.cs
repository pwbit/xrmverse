using Meziantou.Framework.Tds.Handler;

namespace Xrmd.Tds;

/// <summary>Checks a TDS login (LOGIN7). Resolved in a new DI scope for each login.</summary>
public interface ITdsAuthenticator
{
    ValueTask<TdsAuthenticationResult> AuthenticateAsync(TdsAuthenticationContext context, CancellationToken cancellationToken);
}

/// <summary>Answers a TDS batch or RPC call of an authenticated session. Resolved in a new DI scope for each request.</summary>
public interface ITdsQueryHandler
{
    ValueTask<TdsQueryResult> HandleAsync(TdsQueryContext context, CancellationToken cancellationToken);
}

/// <summary>The SQL Server error numbers the endpoint answers with (generic SQL Server numbers).</summary>
public static class TdsErrors
{
    /// <summary>Login failed.</summary>
    public const uint LoginFailed = 18456;

    /// <summary>An unsupported statement or construct.</summary>
    public const uint Unsupported = 40000;

    /// <summary>A failure of the server itself while it answered a request.</summary>
    public const uint Internal = 50000;

    public const string LoginFailedMessage = "Login failed for the SQL endpoint.";

    public static TdsAuthenticationResult LoginFailedResult() => TdsAuthenticationResult.Fail(LoginFailedMessage, LoginFailed, 1, 14);

    /// <summary>Ruling T5: access token (FEDAUTH) logins are not supported by the TDS server library.</summary>
    public const string AccessTokenNotSupportedMessage =
        "Login failed: the SQL endpoint does not accept access tokens (Microsoft Entra sign-in).";

    public static TdsAuthenticationResult AccessTokenNotSupportedResult() =>
        TdsAuthenticationResult.Fail(AccessTokenNotSupportedMessage, LoginFailed, 2, 14);

    /// <summary>The message of a batch that ran longer than <paramref name="limit"/> (ruling T8; error <see cref="Unsupported"/>).</summary>
    public static string TimeoutMessage(TimeSpan limit) =>
        $"The query exceeded the {limit.TotalMinutes.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} minute limit.";

    public static TdsQueryResult Error(uint number, string message) =>
        TdsQueryResult.FromError(new TdsQueryError { Number = number, State = 1, Class = 16, Message = message });
}

/// <summary>The fail-closed default when no authenticator is registered (the host registers <c>TdsAuthenticator</c>): every login fails.</summary>
internal sealed class RefuseAllTdsAuthenticator : ITdsAuthenticator
{
    public ValueTask<TdsAuthenticationResult> AuthenticateAsync(TdsAuthenticationContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(TdsErrors.LoginFailedResult());
}

/// <summary>The production query handler until the real one exists (plan 3.3, Task 3): every request is refused.</summary>
internal sealed class UnavailableTdsQueryHandler : ITdsQueryHandler
{
    public ValueTask<TdsQueryResult> HandleAsync(TdsQueryContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(TdsErrors.Error(TdsErrors.Unsupported, "The SQL endpoint does not run queries yet."));
}
