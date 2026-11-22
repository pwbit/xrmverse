using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// #122: <c>systemuser.applicationid</c>, the Dataverse column of an application user's app id. It is a plain column (null for
/// interactive users), and an organization seeded before it gets it at the next start.
/// </summary>
[Collection("xrmd")]
public sealed class SystemUserApplicationIdTests(XrmdFixture fx)
{
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    [Fact]
    public async Task Applicationid_round_trips_and_filters()
    {
        var appId = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        await Admin.UpdateAsync(new Entity("systemuser", userId) { ["applicationid"] = appId });

        (await Admin.RetrieveAsync("systemuser", userId, new ColumnSet("applicationid")))
            .GetAttributeValue<Guid?>("applicationid").Should().Be(appId);

        var byAppId = new QueryExpression("systemuser") { ColumnSet = new ColumnSet("systemuserid") };
        byAppId.Criteria.AddCondition("applicationid", ConditionOperator.Equal, appId);
        (await Admin.RetrieveMultipleAsync(byAppId)).Entities.Select(e => e.Id).Should().Equal(userId);

        var interactive = new QueryExpression("systemuser") { ColumnSet = new ColumnSet("systemuserid") };
        interactive.Criteria.AddCondition("applicationid", ConditionOperator.Null);
        var ids = (await Admin.RetrieveMultipleAsync(interactive)).Entities.Select(e => e.Id).ToList();
        ids.Should().NotContain(userId);
        ids.Should().NotBeEmpty("the seed administrator is an interactive user");
    }

    [Fact]
    public async Task Seed_upgrade_adds_applicationid_to_an_existing_database()
    {
        var database = "xrmd_appid_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        try
        {
            var factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
            await new SchemaMigrator(factory).MigrateAsync();
            var options = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
            await new SystemSeed(await StoreAsync(factory), factory).EnsureAsync(options);

            // An organization seeded before #122.
            await ExecuteAsync(factory, """
                DELETE FROM meta.attribute WHERE entity_logical_name = 'systemuser' AND logical_name = 'applicationid';
                ALTER TABLE data.systemuser DROP COLUMN applicationid;
                """);
            var stale = await StoreAsync(factory);
            stale.GetEntity("systemuser")!.Attributes.Should().NotContain(a => a.LogicalName == "applicationid");

            await new SystemSeed(stale, factory).EnsureAsync(options);

            (await StoreAsync(factory)).GetEntity("systemuser")!.Attributes.Should().Contain(a => a.LogicalName == "applicationid");
            (await ExecuteAsync(factory, "SELECT COUNT(*) FROM data.systemuser WHERE applicationid IS NOT NULL")).Should().Be(0);
        }
        finally
        {
            await fx.DropDatabaseAsync(database);
        }
    }

    private static async Task<MetadataStore> StoreAsync(IDbConnectionFactory factory)
    {
        var store = new MetadataStore(factory);
        await store.LoadAsync();
        return store;
    }

    private static async Task<object?> ExecuteAsync(IDbConnectionFactory factory, string sql)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        return await cmd.ExecuteScalarAsync();
    }
}
