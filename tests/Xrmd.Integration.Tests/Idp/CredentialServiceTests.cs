using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Host.Idp;
using static Xrmd.Integration.Tests.Idp.LocalIdpTestHost;
using SignInResult = Xrmd.Host.Idp.SignInResult;

namespace Xrmd.Integration.Tests.Idp;

/// <summary>Plan 2.8, Task 4: local credentials (L5, L6, L7) and the brute-force rules of Review Focus 4.</summary>
[Collection("xrmd")]
public sealed class CredentialServiceTests(XrmdFixture fx)
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task A_short_password_is_refused()
    {
        await using var app = Create(fx);
        var (userId, _) = await SystemUserAsync(app, "short@example.com");

        var act = () => WithCredentialsAsync(app, c => c.SetPasswordAsync(userId, "elevenchars", default));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("The password must have at least 12 characters.");
    }

    [Fact]
    public async Task The_right_password_signs_in_with_any_case_of_the_user_name()
    {
        await using var app = Create(fx);
        var (userId, _) = await LocalUserAsync(app, "Dana@Example.com", Password);

        var exact = await WithCredentialsAsync(app, c => c.SignInAsync("Dana@Example.com", Password, null, default));
        var other = await WithCredentialsAsync(app, c => c.SignInAsync("DANA@example.COM", Password, null, default));

        exact.Should().Be(new SignInResult.Succeeded(userId));
        other.Should().Be(new SignInResult.Succeeded(userId));
    }

    [Fact]
    public async Task An_unknown_user_and_a_wrong_password_fail_alike()
    {
        var hasher = new CountingHasher();
        await using var app = fx.WithOwnDatabase(b =>
        {
            b.UseSetting("Auth:Mode", "Local");
            b.UseSetting("Auth:Audience", Audience);
            b.ConfigureTestServices(s => s.AddSingleton<IPasswordHasher<LocalCredential>>(hasher));
        });
        await LocalUserAsync(app, "erin@example.com", Password);
        await WithCredentialsAsync(app, c => c.SignInAsync("warmup@example.com", "whatever", null, default)); // JIT, pools

        // The two kinds of sign-in alternate, so that a slow moment of a busy machine falls on both alike.
        var wrong = new List<TimeSpan>();
        var unknown = new List<TimeSpan>();
        for (var i = 0; i < 3; i++)
        {
            var before = hasher.Verifications;
            var clock = Stopwatch.StartNew();
            (await WithCredentialsAsync(app, c => c.SignInAsync("erin@example.com", "wrong password!", null, default)))
                .Should().Be(SignInResult.Failed);
            wrong.Add(clock.Elapsed);
            hasher.Verifications.Should().Be(before + 1, "a wrong password is verified once");

            clock.Restart();
            (await WithCredentialsAsync(app, c => c.SignInAsync("nobody@example.com", Password, null, default)))
                .Should().Be(SignInResult.Failed);
            unknown.Add(clock.Elapsed);
            hasher.Verifications.Should().Be(before + 2, "the hasher still runs for an unknown user");
        }

        // The hasher (PBKDF2) dominates both: without it the unknown user would answer in a few milliseconds. The fastest call of
        // each kind is compared, as a pause of the machine only ever makes a call slower; half leaves room for the database
        // writes of a wrong password (the attempt and the failure), which an unknown user does not make.
        unknown.Min().Should().BeGreaterThan(wrong.Min() * 0.5, "the hasher still runs for an unknown user");
    }

    /// <summary>The real hasher, counting its verifications.</summary>
    private sealed class CountingHasher : IPasswordHasher<LocalCredential>
    {
        private readonly PasswordHasher<LocalCredential> _inner = new();
        private int _verifications;

        public int Verifications => Volatile.Read(ref _verifications);

        public string HashPassword(LocalCredential user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(LocalCredential user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref _verifications);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    [Fact]
    public async Task Five_failures_of_password_or_totp_lock_the_credential_for_15_minutes()
    {
        await using var app = Create(fx);
        var (userId, _) = await LocalUserAsync(app, "frank@example.com", Password);
        await WithCredentialsAsync(app, c => c.EnableTotpAsync(userId, default));
        var key = (byte[])(await ScalarAsync(app, "SELECT [TotpKey] FROM auth.credential WHERE [SystemUserId] = @id", ("@id", userId)))!;
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        string Code(int drift = 0) => Totp.Compute(key, clock.Now.ToUnixTimeSeconds() / 30 + drift);

        async Task<SignInResult> SignIn(string password, string? totp)
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await ActivatorUtilities.CreateInstance<CredentialService>(scope.ServiceProvider, (TimeProvider)clock)
                .SignInAsync("frank@example.com", password, totp, default);
        }

        (await SignIn(Password, null)).Should().Be(SignInResult.TotpRequired);
        (await SignIn("wrong password!", null)).Should().Be(SignInResult.Failed);
        (await SignIn("wrong password!", Code())).Should().Be(SignInResult.Failed);
        (await SignIn(Password, Code(5))).Should().Be(SignInResult.Failed);
        (await SignIn("wrong password!", null)).Should().Be(SignInResult.Failed);
        (await SignIn(Password, Code(-5))).Should().Be(SignInResult.Failed);

        (await SignIn(Password, Code())).Should().Be(SignInResult.LockedOut, "the right password and code after 5 failures");
        clock.Now += TimeSpan.FromMinutes(14);
        (await SignIn(Password, Code())).Should().Be(SignInResult.LockedOut);
        clock.Now += TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1);
        (await SignIn(Password, Code())).Should().Be(new SignInResult.Succeeded(userId));
    }

    [Fact]
    public async Task Concurrent_failures_cannot_exceed_the_limit()
    {
        await using var app = Create(fx);
        var (userId, _) = await LocalUserAsync(app, "kurt@example.com", Password);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            WithCredentialsAsync(app, c => c.SignInAsync("kurt@example.com", "wrong password!", null, default))));

        results.Count(r => r == SignInResult.Failed).Should().BeLessThanOrEqualTo(CredentialService.MaxFailures);
        results.Should().OnlyContain(r => r == SignInResult.Failed || r == SignInResult.LockedOut);
        (await WithCredentialsAsync(app, c => c.SignInAsync("kurt@example.com", Password, null, default)))
            .Should().Be(SignInResult.LockedOut);
        (await ScalarAsync(app, "SELECT [LockedUntilUtc] FROM auth.credential WHERE [SystemUserId] = @id", ("@id", userId)))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task A_right_password_in_flight_does_not_clear_a_lock_set_meanwhile()
    {
        await using var app = Create(fx);
        var (userId, _) = await LocalUserAsync(app, "lena@example.com", Password);
        var connectionString = app.Services.GetRequiredService<IConfiguration>().GetConnectionString("Org")!;
        // A parallel failure locks the credential while this attempt's hasher runs.
        var hasher = new HookedHasher(() =>
        {
            TestSql.ExecAsync(connectionString,
                "UPDATE auth.credential SET [LockedUntilUtc] = DATEADD(minute, 15, SYSUTCDATETIME()) WHERE [SystemUserId] = @id",
                ("@id", userId)).GetAwaiter().GetResult();
        });

        await using var scope = app.Services.CreateAsyncScope();
        var result = await ActivatorUtilities.CreateInstance<CredentialService>(scope.ServiceProvider, (IPasswordHasher<LocalCredential>)hasher)
            .SignInAsync("lena@example.com", Password, null, default);

        result.Should().Be(SignInResult.LockedOut);
        (await ScalarAsync(app, "SELECT [LockedUntilUtc] FROM auth.credential WHERE [SystemUserId] = @id", ("@id", userId)))
            .Should().NotBeNull("the success must not clear the lock");
    }

    private sealed class HookedHasher(Action onVerify) : IPasswordHasher<LocalCredential>
    {
        private readonly PasswordHasher<LocalCredential> _inner = new();
        public string HashPassword(LocalCredential user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(LocalCredential user, string hashedPassword, string providedPassword)
        {
            onVerify();
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    [Fact]
    public async Task A_used_totp_code_is_refused()
    {
        await using var app = Create(fx);
        var (userId, _) = await LocalUserAsync(app, "gail@example.com", Password);
        var uri = await WithCredentialsAsync(app, c => c.EnableTotpAsync(userId, default));
        uri.Should().StartWith("otpauth://totp/").And.Contain("secret=").And.Contain("issuer=");
        var key = (byte[])(await ScalarAsync(app, "SELECT [TotpKey] FROM auth.credential WHERE [SystemUserId] = @id", ("@id", userId)))!;
        var code = Totp.Compute(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);

        (await WithCredentialsAsync(app, c => c.SignInAsync("gail@example.com", Password, code, default)))
            .Should().Be(new SignInResult.Succeeded(userId));
        (await WithCredentialsAsync(app, c => c.SignInAsync("gail@example.com", Password, code, default)))
            .Should().Be(SignInResult.Failed);

        await WithCredentialsAsync(app, c => c.DisableTotpAsync(userId, default));
        (await WithCredentialsAsync(app, c => c.SignInAsync("gail@example.com", Password, null, default)))
            .Should().Be(new SignInResult.Succeeded(userId));
    }

    [Fact]
    public async Task A_disabled_user_or_a_removed_credential_fails()
    {
        await using var app = Create(fx);
        var (disabled, _) = await LocalUserAsync(app, "hank@example.com", Password);
        var (removed, _) = await LocalUserAsync(app, "ivy@example.com", Password);
        await ExecAsync(app, "UPDATE data.systemuser SET isdisabled = 1 WHERE systemuserid = @id", ("@id", disabled));
        await WithCredentialsAsync(app, c => c.RemoveAsync(removed, default));

        (await WithCredentialsAsync(app, c => c.SignInAsync("hank@example.com", Password, null, default))).Should().Be(SignInResult.Failed);
        (await WithCredentialsAsync(app, c => c.SignInAsync("ivy@example.com", Password, null, default))).Should().Be(SignInResult.Failed);
    }

    [Fact]
    public async Task Setting_a_password_gives_a_user_without_object_id_a_new_one()
    {
        await using var app = Create(fx);
        var (userId, _) = await SystemUserAsync(app, "jill@example.com");
        await ExecAsync(app, "UPDATE data.systemuser SET azureactivedirectoryobjectid = NULL WHERE systemuserid = @id", ("@id", userId));

        await WithCredentialsAsync(app, c => c.SetPasswordAsync(userId, Password, default));

        var oid = await ScalarAsync(app, "SELECT azureactivedirectoryobjectid FROM data.systemuser WHERE systemuserid = @id", ("@id", userId));
        oid.Should().BeOfType<Guid>().Which.Should().NotBeEmpty();
        var stored = await ScalarAsync(app, "SELECT [PasswordHash] FROM auth.credential WHERE [SystemUserId] = @id", ("@id", userId));
        stored.Should().BeOfType<string>().Which.Should().NotContain(Password);
    }

    /// <summary>
    /// Plan 2.7b, Task 8: user names are compared with the engine's text key (ruling Q1), so a user name that differs from an
    /// existing credential's only in case or width is refused on both engines.
    /// </summary>
    [Fact]
    public async Task A_user_name_equal_by_the_text_key_is_refused()
    {
        await using var app = Create(fx);
        await LocalUserAsync(app, "wren@example.com", Password);
        var (other, _) = await SystemUserAsync(app, "\uFF37REN@example.com"); // fullwidth W

        var act = () => WithCredentialsAsync(app, c => c.SetPasswordAsync(other, Password, default));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Another user already has a local credential with this user name.");
        (await WithCredentialsAsync(app, c => c.SignInAsync("WREN@example.com ", Password, null, default))).Should().BeOfType<SignInResult.Succeeded>();
    }

    [Fact]
    public async Task A_user_name_longer_than_the_column_matches_no_credential()
    {
        // Final review M3: a name longer than auth.credential.UserName (256) is not cut to the column's length and matched to the
        // credential whose name it starts with, so an attempt with it counts against no one.
        await using var app = Create(fx);
        var (userId, _) = await LocalUserAsync(app, "longname@example.com", Password);
        var name = "longname" + new string('a', 256 - "longname@example.com".Length) + "@example.com";
        name.Length.Should().Be(256);
        // A user name as long as the column (longer than a systemuser's fullname, so written here).
        await ExecAsync(app, "UPDATE auth.credential SET [UserName] = @name WHERE [SystemUserId] = @id", ("@name", name), ("@id", userId));
        await ExecAsync(app, "UPDATE data.systemuser SET domainname = @name WHERE systemuserid = @id", ("@name", name), ("@id", userId));
        (await WithCredentialsAsync(app, c => c.SignInAsync(name, Password, null, default))).Should().BeOfType<SignInResult.Succeeded>();

        (await WithCredentialsAsync(app, c => c.SignInAsync(name + "zz", Password, null, default))).Should().Be(SignInResult.Failed);
        Convert.ToInt32(await ScalarAsync(app, "SELECT [FailedCount] FROM auth.credential WHERE [SystemUserId] = @id", ("@id", userId)))
            .Should().Be(0, "the longer name matched no credential, so no attempt was taken");
    }

    /// <summary>
    /// Plan 2.7b, Task 8, the interleaving: a credential whose user name equals this one by the text key is being written in a
    /// transaction that has not committed. Setting the password waits for it (on PostgreSQL the writers of credentials take turns,
    /// on SQL Server the unique index waits for the key), then refuses: one credential holds the name.
    /// </summary>
    [Fact]
    public async Task Setting_a_password_waits_for_a_parallel_credential_of_the_same_name()
    {
        await using var app = Create(fx);
        var (first, _) = await SystemUserAsync(app, "yara@example.com");
        var (second, _) = await SystemUserAsync(app, "\uFF39ARA@example.com"); // fullwidth Y
        var connectionString = app.Services.GetRequiredService<IConfiguration>().GetConnectionString("Org")!;

        await using var conn = await TestProvider.OpenAsync(connectionString);
        await using var tx = await conn.BeginTransactionAsync();
        await using (var insert = TestSql.Command(conn, tx, """
            INSERT INTO auth.credential ([SystemUserId], [UserName], [PasswordHash], [FailedCount], [ChangedOnUtc])
            VALUES (@id, 'yara@example.com', 'not a hash', 0, SYSUTCDATETIME())
            """, ("@id", first)))
            await insert.ExecuteNonQueryAsync();

        var setting = Task.Run(() => WithCredentialsAsync(app, c => c.SetPasswordAsync(second, Password, default)));
        await Task.Delay(TimeSpan.FromSeconds(1));
        setting.IsCompleted.Should().BeFalse("the parallel credential is not committed yet");
        await tx.CommitAsync();

        var failure = (await FluentActions.Awaiting(() => setting).Should().ThrowAsync<Exception>()).Which;
        // PostgreSQL: the check after the wait. SQL Server: the check reads around the uncommitted row, the unique index refuses.
        (failure is InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException).Should().BeTrue(failure.ToString());
        Convert.ToInt32(await ScalarAsync(app, "SELECT COUNT(*) FROM auth.credential WHERE [SystemUserId] = @id", ("@id", second))).Should().Be(0);
    }
}
