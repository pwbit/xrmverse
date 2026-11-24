using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core;
using Xrmd.Abstractions;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using Xrmd.Tds.Execution;

namespace Xrmd.Integration.Tests.Tds;

/// <summary>
/// The caller's security over the SQL endpoint, end to end with real SQL logins (plan 3.3, Task 4; Review Focus 1 and 2): a user
/// reads only the records its roles reach, a secured column it cannot read is null and refused in a condition, a table without
/// read privilege is 229, and write statements never run.
/// </summary>
[Collection("tds-query")]
public sealed class TdsSecurityTests(TdsQueryFixture fx)
{
    private const string Password = "acme-user-tds-pass";

    /// <summary>A secured money column on account (the seeded account table has no <c>creditlimit</c>, so a custom one stands in).</summary>
    private const string CreditLimit = "new_creditlimit";

    private static readonly SemaphoreSlim ColumnLock = new(1, 1);

    private readonly string _prefix = "acme-" + Guid.NewGuid().ToString("N")[..8] + "-";

    private IServiceProvider Services => fx.Host.App.Services;

    private SecurityCache Cache => Services.GetRequiredService<SecurityCache>();

    private OrganizationService Admin => TestServices.NewOrganizationService(Services, fx.AdminObjectId);

    /// <summary>A user in <paramref name="businessUnitId"/> (the root when null) with a role holding <paramref name="privileges"/> and a local password; returns its user name and id.</summary>
    private async Task<(string UserName, Guid UserId)> UserAsync(Guid? businessUnitId, params (string Privilege, DepthMask Depth)[] privileges)
    {
        var userName = "acme-tds-" + Guid.NewGuid().ToString("N")[..8];
        var bu = businessUnitId ?? await SecurityTestHelper.RootBusinessUnitAsync(fx.ConnectionString);
        var userId = await SecurityTestHelper.CreateUserAsync(fx.ConnectionString, Cache, bu, userName: userName);
        var role = await SecurityTestHelper.CreateRoleAsync(fx.ConnectionString, Cache, "tds-" + userName, privileges);
        await SecurityTestHelper.AssignRoleAsync(fx.ConnectionString, Cache, userId, role);
        await fx.Host.SetPasswordAsync(userId, Password);
        return (userName, userId);
    }

    private async Task<SqlConnection> OpenAsync(string userName, string password)
    {
        var connection = new SqlConnection(fx.Host.SqlLoginConnectionString(userName, password).ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<List<object?[]>> RowsAsync(string userName, string password, string sql)
    {
        await using var connection = await OpenAsync(userName, password);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private async Task<SqlException> ErrorAsync(string userName, string password, string sql) =>
        (await FluentActions.Awaiting(() => RowsAsync(userName, password, sql)).Should().ThrowAsync<SqlException>()).Which;

    [Fact]
    public async Task User_sees_only_readable_records()
    {
        var root = await SecurityTestHelper.RootBusinessUnitAsync(fx.ConnectionString);
        var sales = await SecurityTestHelper.CreateBusinessUnitAsync(fx.ConnectionString, Cache, root, _prefix + "sales");
        var (userName, userId) = await UserAsync(sales, ("prvReadAccount", DepthMask.Local));
        await Admin.CreateAsync(new Entity("account") { ["name"] = _prefix + "mine", ["ownerid"] = new EntityReference("systemuser", userId) });
        await Admin.CreateAsync(new Entity("account") { ["name"] = _prefix + "other" });

        var rows = await RowsAsync(userName, Password, $"SELECT name FROM account WHERE name LIKE '{_prefix}%'");

        rows.Select(r => r.Single()).Should().Equal(_prefix + "mine");
    }

    [Fact]
    public async Task Secured_column_is_null_and_refused_in_where()
    {
        await EnsureCreditLimitAsync();
        await Admin.CreateAsync(new Entity("account") { ["name"] = _prefix + "limited", [CreditLimit] = new Money(1000m) });
        // No field security profile for this user.
        var (userName, _) = await UserAsync(null, ("prvReadAccount", DepthMask.Global));

        // The System Administrator reads every secured column.
        (await RowsAsync(TdsTestHost.AdminUserName, TdsTestHost.AdminPassword, $"SELECT {CreditLimit} FROM account WHERE name LIKE '{_prefix}%'"))
            .Should().ContainSingle().Which.Should().Equal(1000m);

        var rows = await RowsAsync(userName, Password, $"SELECT name, {CreditLimit} FROM account WHERE name LIKE '{_prefix}%'");
        rows.Should().ContainSingle().Which.Should().Equal(new object?[] { _prefix + "limited", null });

        (await ErrorAsync(userName, Password, $"SELECT name FROM account WHERE {CreditLimit} > 0")).Number
            .Should().Be(TdsQueryHandler.PermissionDenied);
    }

    [Fact]
    public async Task No_read_privilege_is_229()
    {
        var (userName, _) = await UserAsync(null, ("prvReadAccount", DepthMask.Global));

        (await RowsAsync(userName, Password, "SELECT TOP 1 name FROM account")).Should().NotBeNull("the user reads accounts");
        (await ErrorAsync(userName, Password, "SELECT lastname FROM contact")).Number.Should().Be(TdsQueryHandler.PermissionDenied);
    }

    [Fact]
    public async Task Writes_never_run()
    {
        foreach (var name in new[] { "a", "b" }) await Admin.CreateAsync(new Entity("account") { ["name"] = _prefix + name });
        var before = await CountAsync();

        foreach (var sql in new[] { "DELETE FROM account", "SELECT 1; DELETE FROM account", "DROP TABLE account" })
            (await ErrorAsync(TdsTestHost.AdminUserName, TdsTestHost.AdminPassword, sql)).Number.Should().Be(40000, sql);

        (await CountAsync()).Should().Be(before).And.BeGreaterOrEqualTo(2);
    }

    private async Task<int> CountAsync()
    {
        return Convert.ToInt32(await TestSql.ScalarAsync(fx.ConnectionString, "SELECT COUNT(*) FROM data.account"));
    }

    private async Task EnsureCreditLimitAsync()
    {
        await ColumnLock.WaitAsync();
        try
        {
            if (Services.GetRequiredService<IMetadataStore>().GetEntity("account")!.Attributes.Any(a => a.LogicalName == CreditLimit)) return;
            await Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = "account",
                Attribute = new MoneyAttributeMetadata { LogicalName = CreditLimit, SchemaName = "new_CreditLimit", IsSecured = true },
            });
        }
        finally
        {
            ColumnLock.Release();
        }
    }
}
