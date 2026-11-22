using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Host.Auth;
using Xrmd.Security;
using Hasher = Microsoft.AspNetCore.Identity.PasswordHasher<Xrmd.Host.Idp.LocalCredential>;
using IHasher = Microsoft.AspNetCore.Identity.IPasswordHasher<Xrmd.Host.Idp.LocalCredential>;
using PasswordVerification = Microsoft.AspNetCore.Identity.PasswordVerificationResult;

namespace Xrmd.Host.Idp;

/// <summary>The outcome of a local sign-in: <see cref="Succeeded"/>, <see cref="TotpRequired"/>, <see cref="Failed"/> or <see cref="LockedOut"/>.</summary>
public abstract record SignInResult
{
    private SignInResult() { }

    /// <summary>The password (and the TOTP code, when the user has one) matched.</summary>
    public sealed record Succeeded(Guid SystemUserId) : SignInResult;

    /// <summary>The password matched; the user has TOTP and no code was given.</summary>
    public static SignInResult TotpRequired { get; } = new Outcome(nameof(TotpRequired));

    /// <summary>Unknown user, wrong password or code, disabled user: the same answer for all of them.</summary>
    public static SignInResult Failed { get; } = new Outcome(nameof(Failed));

    /// <summary>Too many failures: the credential refuses every attempt until the lockout ends.</summary>
    public static SignInResult LockedOut { get; } = new Outcome(nameof(LockedOut));

    private sealed record Outcome(string Name) : SignInResult
    {
        public override string ToString() => Name;
    }
}

/// <summary>A user tokens may be issued for (<see cref="CredentialService.FindActiveUserAsync(Guid, CancellationToken)"/>).</summary>
public sealed record ActiveUser(Guid SystemUserId, string Name, Guid ObjectId, string Stamp);

/// <summary>
/// Local credentials of <c>systemuser</c> rows (plan 2.8): the user name is <c>domainname</c>, case-insensitive (L6); passwords are
/// hashed by ASP.NET Core Identity's <c>PasswordHasher</c> (PBKDF2), at least 12 characters; TOTP is optional (L7); 5 failures of
/// password or code lock the credential for 15 minutes. Secrets never leave this class except the one-time otpauth URI.
/// </summary>
public sealed class CredentialService(AuthDbContext db, IDbConnectionFactory database, SecurityCache securityCache,
    IConfiguration configuration, TimeProvider time, IHasher passwordHasher)
{
    public const int MinimumPasswordLength = 12;
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    // Verified for an unknown or locked user, so it takes as long as a known one (no user enumeration by timing).
    private static readonly Lazy<string> DummyHash = new(() =>
        new Hasher().HashPassword(new LocalCredential(), Convert.ToBase64String(Guid.NewGuid().ToByteArray())));

    /// <summary>
    /// Checks a password (and TOTP code). The lockout holds under concurrency: each attempt first takes one of the
    /// <see cref="MaxFailures"/> attempts with an atomic update (refused once they are all taken or while locked), before the
    /// hasher runs; a failure that reaches the limit locks the credential; a success gives the attempts back only while the
    /// credential is not locked, so it never clears a lock that a parallel failure set.
    /// </summary>
    public Task<SignInResult> SignInAsync(string userName, string password, string? totp, CancellationToken ct) =>
        SignInCoreAsync(userName, password, totp, sqlLogin: false, ct);

    /// <summary>
    /// Checks the password of a SQL endpoint login (plan 3.3, ruling T4), with the same attempts and lockout as
    /// <see cref="SignInAsync"/>. TDS carries no second factor, so a user with TOTP is refused once its password is checked, and
    /// that refusal counts as a failure toward the lockout: a SQL login can never bypass the second factor, nor probe passwords
    /// without limit. Never returns <see cref="SignInResult.TotpRequired"/>.
    /// </summary>
    public Task<SignInResult> SignInForSqlLoginAsync(string userName, string password, CancellationToken ct) =>
        SignInCoreAsync(userName, password, totp: null, sqlLogin: true, ct);

    private async Task<SignInResult> SignInCoreAsync(string userName, string password, string? totp, bool sqlLogin, CancellationToken ct)
    {
        var name = Normalize(userName);
        var credential = await FindCredentialAsync(name, ct);
        if (credential is null)
        {
            passwordHasher.VerifyHashedPassword(new LocalCredential(), DummyHash.Value, password ?? "");
            return SignInResult.Failed;
        }

        var id = credential.SystemUserId;
        var now = time.GetUtcNow().UtcDateTime;
        if (!await TakeAttemptAsync(id, now, ct))
        {
            passwordHasher.VerifyHashedPassword(new LocalCredential(), DummyHash.Value, password ?? "");
            await LockIfExhaustedAsync(id, now, ct); // all attempts taken but no lock yet (in flight, or a crash): lock now
            return SignInResult.LockedOut;
        }

        var verified = passwordHasher.VerifyHashedPassword(credential, credential.PasswordHash, password ?? "");
        if (verified == PasswordVerification.Failed) return await FailAsync(id, now, ct);

        // A disabled or renamed user cannot sign in (L6).
        var user = await FindUserAsync(id, ct);
        if (user is not { Disabled: false } || Normalize(user.DomainName ?? "") != name) return await FailAsync(id, now, ct);

        if (credential.TotpKey is { } key)
        {
            // A SQL login has no second factor: refused, and the attempt taken stays counted.
            if (sqlLogin) return await FailAsync(id, now, ct);
            if (string.IsNullOrWhiteSpace(totp))
            {
                // The password was right: this step is not a failure, its attempt is given back.
                await db.Credentials.Where(c => c.SystemUserId == id && c.LockedUntilUtc == null && c.FailedCount > 0)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.FailedCount, c => c.FailedCount - 1), ct);
                return SignInResult.TotpRequired;
            }
            // The step is claimed with a conditional update: two requests with the same code cannot both succeed.
            if (!Totp.Verify(key, totp.Trim(), time.GetUtcNow(), credential.TotpLastStep, out var step)
                || await db.Credentials.Where(c => c.SystemUserId == id && (c.TotpLastStep == null || c.TotpLastStep < step))
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.TotpLastStep, step), ct) == 0)
                return await FailAsync(id, now, ct);
        }

        var rehash = verified == PasswordVerification.SuccessRehashNeeded ? passwordHasher.HashPassword(credential, password!) : null;
        var reset = await db.Credentials.Where(c => c.SystemUserId == id && c.LockedUntilUtc == null).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.FailedCount, 0)
            .SetProperty(c => c.PasswordHash, c => rehash ?? c.PasswordHash), ct);
        return reset == 1 ? new SignInResult.Succeeded(id) : SignInResult.LockedOut;
    }

    // Takes an attempt: not locked and fewer than MaxFailures taken, or a lock that has ended (it starts a new count).
    private async Task<bool> TakeAttemptAsync(Guid id, DateTime now, CancellationToken ct) =>
        await db.Credentials
            .Where(c => c.SystemUserId == id
                        && (c.LockedUntilUtc == null ? c.FailedCount < MaxFailures : c.LockedUntilUtc <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.FailedCount, c => c.LockedUntilUtc == null ? c.FailedCount + 1 : 1)
                .SetProperty(c => c.LockedUntilUtc, (DateTime?)null), ct) == 1;

    // The attempt taken stays counted; reaching the limit locks the credential.
    private async Task<SignInResult> FailAsync(Guid id, DateTime now, CancellationToken ct)
    {
        await LockIfExhaustedAsync(id, now, ct);
        return SignInResult.Failed;
    }

    private Task LockIfExhaustedAsync(Guid id, DateTime now, CancellationToken ct)
    {
        DateTime? lockedUntil = now + LockoutDuration;
        return db.Credentials.Where(c => c.SystemUserId == id && c.LockedUntilUtc == null && c.FailedCount >= MaxFailures)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LockedUntilUtc, lockedUntil), ct);
    }

    /// <summary>
    /// Creates or replaces the user's credential (and clears its lockout). A user without <c>azureactivedirectoryobjectid</c> gets a
    /// new one, which becomes the <c>oid</c> of its tokens (L5).
    /// </summary>
    public async Task SetPasswordAsync(Guid systemUserId, string password, CancellationToken ct)
    {
        if (password is null || password.Length < MinimumPasswordLength)
            throw new ArgumentException($"The password must have at least {MinimumPasswordLength} characters.");

        var user = await FindUserAsync(systemUserId, ct) ?? throw new InvalidOperationException("The user does not exist.");
        if (string.IsNullOrWhiteSpace(user.DomainName))
            throw new InvalidOperationException("The user has no user name (domainname).");
        var name = Normalize(user.DomainName);
        // The PBKDF2 hash is computed before the transaction, so the table lock below is not held while it runs (final review M4).
        var hash = passwordHasher.HashPassword(new LocalCredential { SystemUserId = systemUserId, UserName = name }, password);
        // The check and the write are one transaction. On PostgreSQL the writers of credentials take turns first (a table lock;
        // nothing on SQL Server): the unique index of the user name compares citext, not the text key of the check, so it would
        // not stop a parallel credential whose name differs only in width or kana (plan 2.7b, Task 8).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var session = new DataSession(db.Database.GetDbConnection(), tx.GetDbTransaction());
        await session.Dialect.LockTableWritesAsync(session, "auth", "credential", ct);
        if ((await MatchingCredentialsAsync(session, name, ct)).Any(m => m.SystemUserId != systemUserId))
            throw new InvalidOperationException("Another user already has a local credential with this user name.");

        if (user.ObjectId is null)
        {
            await using var conn = await database.OpenAsync(ct);
            await using var cmd = conn.Command($"""
                UPDATE data.systemuser SET azureactivedirectoryobjectid = {database.Dialect.NewGuid}
                WHERE systemuserid = @id AND azureactivedirectoryobjectid IS NULL
                """);
            cmd.AddParameter("@id", DbType.Guid, systemUserId);
            await cmd.ExecuteNonQueryAsync(ct);
            securityCache.Invalidate(); // a cached caller profile keyed by object id must see the new one
        }

        var credential = await db.Credentials.FindAsync([systemUserId], ct);
        if (credential is null)
        {
            credential = new LocalCredential { SystemUserId = systemUserId };
            db.Credentials.Add(credential);
        }
        credential.UserName = name;
        credential.PasswordHash = hash;
        credential.FailedCount = 0;
        credential.LockedUntilUtc = null;
        credential.ChangedOnUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Gives the user a new TOTP key and returns its <c>otpauth://</c> URI, to show once. The next sign-in needs a code.</summary>
    public async Task<string> EnableTotpAsync(Guid systemUserId, CancellationToken ct)
    {
        var credential = await RequireAsync(systemUserId, ct);
        var key = Totp.NewKey();
        credential.TotpKey = key;
        credential.TotpLastStep = null;
        credential.ChangedOnUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        var issuer = new Uri(JwtSetup.LocalAudience(configuration.GetSection("Auth"))).Host;
        return Totp.ProvisioningUri(key, issuer, credential.UserName);
    }

    public async Task DisableTotpAsync(Guid systemUserId, CancellationToken ct)
    {
        var credential = await RequireAsync(systemUserId, ct);
        credential.TotpKey = null;
        credential.TotpLastStep = null;
        credential.ChangedOnUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes the credential: the user can no longer sign in, and its refresh tokens stop working.</summary>
    public async Task RemoveAsync(Guid systemUserId, CancellationToken ct) =>
        await db.Credentials.Where(c => c.SystemUserId == systemUserId).ExecuteDeleteAsync(ct);

    /// <summary>
    /// The user a token may be issued for: enabled, with an object id and a local credential; null otherwise. Its
    /// <see cref="ActiveUser.Stamp"/> changes with every change of the credential (password, TOTP).
    /// </summary>
    public async Task<ActiveUser?> FindActiveUserAsync(Guid systemUserId, CancellationToken ct)
    {
        var changedOn = await db.Credentials.Where(c => c.SystemUserId == systemUserId)
            .Select(c => (DateTime?)c.ChangedOnUtc).SingleOrDefaultAsync(ct);
        if (changedOn is null) return null;
        return await FindUserAsync(systemUserId, ct) is { Disabled: false, ObjectId: { } objectId } user
            ? new ActiveUser(systemUserId, user.FullName ?? user.DomainName ?? "", objectId,
                changedOn.Value.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>
    /// <see cref="FindActiveUserAsync(Guid, CancellationToken)"/> for a session, code or refresh token that carries the stamp of
    /// the credential it was issued with: null when the stamp is missing or the credential changed since (password reset, TOTP
    /// enabled or disabled). The authorize, device verification and token endpoints check it on every use.
    /// </summary>
    public async Task<ActiveUser?> FindActiveUserAsync(Guid systemUserId, string? stamp, CancellationToken ct) =>
        await FindActiveUserAsync(systemUserId, ct) is { } user && stamp is not null && user.Stamp == stamp ? user : null;

    private async Task<LocalCredential> RequireAsync(Guid systemUserId, CancellationToken ct) =>
        await db.Credentials.FindAsync([systemUserId], ct) ?? throw new InvalidOperationException("The user has no local credential.");

    private static string Normalize(string userName) => userName.Trim().ToLowerInvariant();

    /// <summary>
    /// The credential of the user name <paramref name="name"/> (normalized), or null. The name is compared with the text key of
    /// the engine (plan 2.7b, ruling Q1): the database's <c>_CI_AS</c> comparison on SQL Server, the same comparison on
    /// PostgreSQL, whose <c>citext</c> column alone would tell width and kana apart. Of names equal by that key, the one written
    /// exactly as <paramref name="name"/> comes first.
    /// </summary>
    private async Task<LocalCredential?> FindCredentialAsync(string name, CancellationToken ct)
    {
        IReadOnlyList<(Guid SystemUserId, string UserName)> matches;
        await using (var conn = await database.OpenAsync(ct))
            matches = await MatchingCredentialsAsync(new DataSession(conn, null), name, ct);
        if (matches.Count == 0) return null;
        var id = matches.OrderBy(m => string.Equals(m.UserName, name, StringComparison.Ordinal) ? 0 : 1).First().SystemUserId;
        return await db.Credentials.AsNoTracking().SingleOrDefaultAsync(c => c.SystemUserId == id, ct);
    }

    /// <summary>The credentials whose user name equals <paramref name="name"/> by the engine's text key (<see cref="FindCredentialAsync"/>).</summary>
    /// <summary>The length of <c>auth.credential.UserName</c>.</summary>
    private const int UserNameMaxLength = 256;

    private static async Task<IReadOnlyList<(Guid SystemUserId, string UserName)>> MatchingCredentialsAsync(DataSession session, string name, CancellationToken ct)
    {
        // A longer name than the column holds matches nothing; the parameter would cut it on SQL Server (final review M3).
        if (name.Length > UserNameMaxLength) return [];
        var d = session.Dialect;
        await using var cmd = session.Connection.Command(
            $"SELECT {d.Quote("SystemUserId")}, {d.Quote("UserName")} FROM auth.credential WHERE {d.TextKey(d.Quote("UserName"))} = {d.TextKey("@name")}",
            session.Transaction);
        cmd.AddParameter("@name", DbType.String, name, UserNameMaxLength);
        var rows = new List<(Guid, string)>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) rows.Add((rd.GetGuid(0), rd.GetString(1)));
        return rows;
    }

    private sealed record User(string? DomainName, string? FullName, bool Disabled, Guid? ObjectId);

    private async Task<User?> FindUserAsync(Guid systemUserId, CancellationToken ct)
    {
        var d = database.Dialect;
        await using var conn = await database.OpenAsync(ct);
        await using var cmd = conn.Command($"""
            SELECT domainname, fullname, CAST({d.IsNull("isdisabled", d.BooleanLiteral(false))} AS {d.ColumnType(SqlColumnKind.Boolean)}), azureactivedirectoryobjectid
            FROM data.systemuser WHERE systemuserid = @id
            """);
        cmd.AddParameter("@id", DbType.Guid, systemUserId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new User(rd.IsDBNull(0) ? null : rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetBoolean(2),
            rd.IsDBNull(3) ? null : rd.GetGuid(3));
    }
}
