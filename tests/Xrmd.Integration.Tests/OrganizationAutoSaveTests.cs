using Xrmd.Abstractions;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests;

/// <summary>
/// #117 (ruling R4): <c>organization.isautosaveenabled</c> is on for a new organization and for one seeded before the column.
/// </summary>
[Collection("xrmd")]
public sealed class OrganizationAutoSaveTests(XrmdFixture fx)
{
    [Fact]
    public async Task Autosave_is_on_for_new_and_existing_organizations()
    {
        var database = "xrmd_autosave_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        try
        {
            var factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
            await new SchemaMigrator(factory).MigrateAsync();
            var options = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
            await new SystemSeed(await StoreAsync(factory), factory).EnsureAsync(options);
            (await ScalarAsync(factory, "SELECT CAST(isautosaveenabled AS int) FROM data.organization")).Should().Be(1);

            // An organization seeded before #117: no column, so no value.
            await ScalarAsync(factory, """
                DELETE FROM meta.attribute WHERE entity_logical_name = 'organization' AND logical_name = 'isautosaveenabled';
                ALTER TABLE data.organization DROP COLUMN isautosaveenabled;
                """);
            var stale = await StoreAsync(factory);
            stale.GetEntity("organization")!.Attributes.Should().NotContain(a => a.LogicalName == AppModuleSchema.AutoSaveEnabled);

            await new SystemSeed(stale, factory).EnsureAsync(options);
            (await ScalarAsync(factory, "SELECT CAST(isautosaveenabled AS int) FROM data.organization")).Should().Be(1);
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

    private static async Task<object?> ScalarAsync(IDbConnectionFactory factory, string sql)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }
}
