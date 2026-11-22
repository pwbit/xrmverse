using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Host.Idp;
using Xrmd.Security;
using Xrmd.Tds;

namespace Xrmd.Integration.Tests.Tds;

/// <summary>
/// Logins to the SQL endpoint (plan 3.3, Task 4; rulings T4 and T5; Review Focus 5). A SQL login (user name and password) works
/// only in <c>Auth:Mode=Local</c>, checked by <see cref="CredentialService"/> with its lockout; a TOTP user, a wrong password, a
/// disabled user and a user without roles all get 18456 with the same message. Access token logins are refused explicitly
/// (<see cref="TdsConnectivityTests.Access_token_login_is_refused_explicitly"/>): the TDS server does not read FEDAUTH (T5), so the
/// brief's token tests (admin token accepted, expired or foreign audience refused) collapse into that refusal.
/// </summary>
[Collection("tds-query")]
public sealed class TdsAuthenticationTests(TdsQueryFixture fx)
{
    private const string Password = "acme-user-tds-pass";

    private TdsTestHost Host => fx.Host;

    private static string NewUserName() => "acme-tds-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A System Administrator with a local credential; returns its systemuserid.</summary>
    private async Task<Guid> AdminUserAsync(string userName, string password = Password)
    {
        var userId = await Host.App.Services.GetRequiredService<SecurityBootstrap>().EnsureSystemAdministratorAsync(Guid.NewGuid(), userName, default);
        await Host.SetPasswordAsync(userId, password);
        return userId;
    }

    private static async Task OpenAsync(TdsTestHost host, string userName, string password)
    {
        await using var connection = await TestProvider.OpenAsync(host.SqlLoginConnectionString(userName, password).ConnectionString);
        await using var command = TestSql.Command(connection, "SELECT TOP 1 fullname FROM systemuser");
        (await command.ExecuteScalarAsync()).Should().NotBeNull();
    }

    private static async Task RefusedAsync(TdsTestHost host, string userName, string password)
    {
        await using var connection = new SqlConnection(host.SqlLoginConnectionString(userName, password).ConnectionString);
        var error = (await FluentActions.Awaiting(() => connection.OpenAsync()).Should().ThrowAsync<SqlException>()).Which;
        error.Number.Should().Be((int)TdsErrors.LoginFailed);
        error.Message.Should().Be("Login failed for the SQL endpoint.");
    }

    private async Task<DateTime?> LockedUntilAsync(Guid userId)
    {
        await using var conn = await Host.App.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        await using var cmd = TestSql.Command(conn, "SELECT [LockedUntilUtc] FROM auth.credential WHERE [SystemUserId] = @id");
        cmd.AddParameter("@id", userId);
        return await cmd.ExecuteScalarAsync() as DateTime?;
    }

    [Fact]
    public async Task Sql_login_in_local_mode()
    {
        var userName = NewUserName();
        await AdminUserAsync(userName);

        await OpenAsync(Host, userName, Password);
        // The user name is case-insensitive, as for the web sign-in (L6).
        await OpenAsync(Host, userName.ToUpperInvariant(), Password);
    }

    [Fact]
    public async Task Sql_login_refused_outside_local_mode()
    {
        // The same organization database, served in Development mode: no local credentials there, every SQL login fails.
        await using var host = await TdsTestHost.StartAsync(new TdsTestOrg(await fx.NewDatabaseAsync(), fx.AdminObjectId,
            new Dictionary<string, string?> { ["Auth:Mode"] = "Development" }));

        await RefusedAsync(host, TdsTestHost.AdminUserName, TdsTestHost.AdminPassword);
    }

    [Fact]
    public async Task Totp_user_cannot_use_sql_login()
    {
        var userName = NewUserName();
        var userId = await AdminUserAsync(userName);
        await Host.WithCredentialsAsync(c => c.EnableTotpAsync(userId, default));

        for (var i = 0; i < CredentialService.MaxFailures; i++) await RefusedAsync(Host, userName, Password);

        (await LockedUntilAsync(userId)).Should().NotBeNull("each refused TOTP login counts as a failure");
    }

    [Fact]
    public async Task Wrong_password_counts_toward_lockout()
    {
        var userName = NewUserName();
        var userId = await AdminUserAsync(userName);

        for (var i = 0; i < CredentialService.MaxFailures; i++) await RefusedAsync(Host, userName, Password + "-wrong");

        await RefusedAsync(Host, userName, Password);
        (await LockedUntilAsync(userId)).Should().BeCloseTo(DateTime.UtcNow + CredentialService.LockoutDuration, TimeSpan.FromMinutes(1));

        // The reason is logged with the user name, never the password.
        Host.Logs.Lines.Should().Contain(l => l.Contains(userName, StringComparison.Ordinal) && l.Contains("refused", StringComparison.Ordinal));
        Host.Logs.Lines.Should().NotContain(l => l.Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabled_user_or_no_roles()
    {
        var disabled = NewUserName();
        var disabledId = await AdminUserAsync(disabled);
        await using (var conn = await Host.App.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync())
        await using (var cmd = TestSql.Command(conn, "UPDATE data.systemuser SET isdisabled = 1 WHERE systemuserid = @id"))
        {
            cmd.AddParameter("@id", disabledId);
            await cmd.ExecuteNonQueryAsync();
        }
        Host.App.Services.GetRequiredService<SecurityCache>().Invalidate();

        var noRoles = NewUserName();
        var noRolesId = await SecurityTestHelper.CreateUserAsync(fx.ConnectionString, Host.App.Services.GetRequiredService<SecurityCache>(),
            await SecurityTestHelper.RootBusinessUnitAsync(fx.ConnectionString), userName: noRoles);
        await Host.SetPasswordAsync(noRolesId, Password);

        await RefusedAsync(Host, disabled, Password);
        await RefusedAsync(Host, noRoles, Password);
    }
}
