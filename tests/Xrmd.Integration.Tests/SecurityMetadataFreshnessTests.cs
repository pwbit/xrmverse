using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Query;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2d, review focus 4: the security module reads table metadata through the model, so it must see a metadata change at once.
/// A user-owned custom table and a user with Basic read: the read filter is the same SQL before and after a column is added, the new
/// column is readable at once; deleting the table takes its privileges, their grants, the shares of its records and its hierarchy
/// exclusion with it, and drops the cached security entries (<see cref="SecurityCache"/>, <see cref="PrivilegeCatalog"/>).
/// </summary>
[Collection("xrmd")]
public sealed class SecurityMetadataFreshnessTests(XrmdFixture fx)
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The SQL of a query of <paramref name="table"/>'s name column under the read filter of <paramref name="userId"/>.</summary>
    private async Task<string> ReadSqlAsync(string table, Guid userId)
    {
        var cache = Cache(fx);
        var security = new ReadSecurity([await cache.GetProfileAsync(userId, default)], await cache.GetBusinessUnitTreeAsync(default), Store, userId);
        var root = new EntityNode { EntityName = table };
        root.Columns.Add(new ColumnNode("new_name"));
        var context = new QueryContext(userId, Guid.Empty, new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc)) with { Security = security };
        return new SqlQueryCompiler(Store, TestSql.Dialect).Compile(new QueryModel { Root = root }, context).Sql;
    }

    [Fact]
    public async Task Security_sees_a_new_column_and_forgets_a_deleted_table()
    {
        var name = $"new_fresh{_run}";
        var schema = $"new_Fresh{_run}";
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = TestHelpers.Entity(name, schema, "Fresh " + _run),
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        });

        // A user with Basic read on the table (its privilege exists: the catalog created it with the table).
        var root = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, root, oid);
        var role = await CreateRoleAsync(fx, "fresh-" + _run, ($"prvRead{schema}", DepthMask.Basic));
        await AssignRoleAsync(fx, user, role);
        var mine = await Admin.CreateAsync(new Entity(name) { ["new_name"] = "mine", ["ownerid"] = new EntityReference("systemuser", user) });
        await Admin.CreateAsync(new Entity(name) { ["new_name"] = "not mine" });

        var before = await ReadSqlAsync(name, user);

        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new StringAttributeMetadata { LogicalName = "new_note", SchemaName = "new_Note", MaxLength = 50, DisplayName = Label("Note") },
        });
        await Admin.UpdateAsync(new Entity(name, mine) { ["new_note"] = "fresh" });

        (await ReadSqlAsync(name, user)).Should().Be(before);
        var query = new QueryExpression(name) { ColumnSet = new ColumnSet("new_name", "new_note") };
        var rows = (await ServiceFor(fx, oid).RetrieveMultipleAsync(query)).Entities;
        rows.Should().ContainSingle().Which.GetAttributeValue<string>("new_note").Should().Be("fresh");

        // A share of the record and a hierarchy exclusion of the table: both go with it.
        var otc = Store.GetTable(name)!.ObjectTypeCode!.Value;
        await ExecAsync("""
            INSERT INTO data.principalobjectaccess (principalobjectaccessid, principalid, principaltypecode, objectid, objecttypecode, accessrightsmask, inheritedaccessrightsmask)
            VALUES (NEWID(), @principal, 8, @object, @otc, 2, 0)
            """, ("@principal", user), ("@object", mine), ("@otc", otc));
        var excluded = (await Cache(fx).GetHierarchySettingsAsync(default)).ExcludedTables;
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Cache(fx).SetHierarchyExclusionsAsync(s, [.. excluded, name], default));
        (await Cache(fx).GetHierarchySettingsAsync(default)).ExcludedTables.Should().Contain(name);
        (await Cache(fx).GetProfileAsync(user, default)).DepthOf($"prvRead{schema}").Should().Be(DepthMask.Basic);
        var version = Cache(fx).Version;

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name });

        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name LIKE @name", ("@name", $"prv%{schema}"))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilegeobjecttypecodes WHERE objecttypecode = @name", ("@name", name))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = @role", ("@role", role))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.principalobjectaccess WHERE objecttypecode = @otc", ("@otc", otc))).Should().Be(0);
        Cache(fx).Version.Should().BeGreaterThan(version);
        (await Cache(fx).GetHierarchySettingsAsync(default)).ExcludedTables.Should().NotContain(name);
        (await Cache(fx).GetProfileAsync(user, default)).DepthOf($"prvRead{schema}").Should().Be(DepthMask.None);
    }
}
