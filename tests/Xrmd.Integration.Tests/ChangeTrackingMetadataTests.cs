using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.13, task 1: the table flag <c>ChangeTrackingEnabled</c> (one-way, eligibility C2, index C11), the tracking tables of
/// <c>meta</c>, the organization column <c>expirechangetrackingindays</c> (C1), solutions and the startup upgrade (C13), and the
/// cleanup of a deleted table (C14).
/// </summary>
[Collection("xrmd")]
public sealed class ChangeTrackingMetadataTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private string Name(string label) => $"new_{label}{_run}";

    private static CreateEntityRequest CreateEntity(string logicalName, Action<EntityMetadata>? configure = null)
    {
        var entity = TestHelpers.Entity(logicalName, logicalName, "Table " + logicalName);
        configure?.Invoke(entity);
        return new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        };
    }

    private async Task<EntityMetadata> RetrieveEntityAsync(string name) =>
        ((RetrieveEntityResponse)await Admin.ExecuteAsync(new RetrieveEntityRequest { LogicalName = name, EntityFilters = EntityFilters.Entity })).EntityMetadata;

    private Task SetTrackingAsync(string name, bool value) =>
        Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = name, ChangeTrackingEnabled = value } });

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<long?> EnabledVersionAsync(string table) =>
        (long?)await ScalarAsync("SELECT enabled_version FROM meta.change_tracking_table WHERE entity_logical_name = @t", ("@t", table));

    private Task<bool> IndexExistsAsync(string table) =>
        TestSql.IndexExistsAsync(fx.ConnectionString, "data", table, $"IX_{table}_versionnumber");

    // ---- the table flag -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateEntity_enables_tracking_and_records_enabled_version()
    {
        var name = Name("ct");
        await Admin.ExecuteAsync(CreateEntity(name));
        var created = await RetrieveEntityAsync(name);
        created.ChangeTrackingEnabled.Should().BeFalse("a created table starts without change tracking");
        created.CanChangeTrackingBeEnabled.Value.Should().BeTrue();
        (await EnabledVersionAsync(name)).Should().BeNull();
        (await IndexExistsAsync(name)).Should().BeFalse();
        Store.ChangeTrackingTables.Should().NotContain(name);

        await SetTrackingAsync(name, true);

        (await RetrieveEntityAsync(name)).ChangeTrackingEnabled.Should().BeTrue();
        (await EnabledVersionAsync(name)).Should().BeGreaterThan(0);
        (await IndexExistsAsync(name)).Should().BeTrue();
        Store.ChangeTrackingTables.Should().Contain(name);
        // Computed, never stored.
        ((string?)await ScalarAsync("SELECT JSON_QUERY(definition, '$.CanChangeTrackingBeEnabled') FROM meta.entity WHERE logical_name = @n", ("@n", name)))
            .Should().BeNull();

        // Web API: shown on EntityDefinitions.
        var req = new HttpRequestMessage(HttpMethod.Get, Api + $"EntityDefinitions(LogicalName='{name}')");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!;
        json["ChangeTrackingEnabled"]!.GetValue<bool>().Should().BeTrue();
        json["CanChangeTrackingBeEnabled"]!["Value"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Disabling_tracking_is_refused()
    {
        var name = Name("off");
        await Admin.ExecuteAsync(CreateEntity(name));
        await SetTrackingAsync(name, false); // false on a table without tracking: nothing to change
        await SetTrackingAsync(name, true);
        var version = await EnabledVersionAsync(name);

        (await BadRequestAsync(() => SetTrackingAsync(name, false))).Should().Contain("ChangeTrackingEnabled");
        (await RetrieveEntityAsync(name)).ChangeTrackingEnabled.Should().BeTrue();

        // Re-sending true is a no-op: the enabled version stays.
        await SetTrackingAsync(name, true);
        (await EnabledVersionAsync(name)).Should().Be(version);
        // A retrieved definition sent back passes.
        var entity = await RetrieveEntityAsync(name);
        entity.DisplayName = Label("Relabelled");
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = entity });
    }

    [Fact]
    public async Task Intersect_and_ineligible_tables_cannot_be_tracked()
    {
        var left = Name("l");
        var right = Name("r");
        await Admin.ExecuteAsync(CreateEntity(left));
        await Admin.ExecuteAsync(CreateEntity(right));
        var intersect = Name("lr");
        await Admin.ExecuteAsync(new CreateManyToManyRequest
        {
            ManyToManyRelationship = new ManyToManyRelationshipMetadata { SchemaName = intersect, Entity1LogicalName = left, Entity2LogicalName = right },
            IntersectEntitySchemaName = intersect,
        });

        foreach (var table in new[] { intersect, "principalobjectaccess", "savedquery" })
        {
            (await RetrieveEntityAsync(table)).CanChangeTrackingBeEnabled.Value.Should().BeFalse(table);
            (await BadRequestAsync(() => SetTrackingAsync(table, true))).Should().Contain("ChangeTrackingEnabled", table);
            (await RetrieveEntityAsync(table)).ChangeTrackingEnabled.Should().BeFalse(table);
            (await EnabledVersionAsync(table)).Should().BeNull(table);
        }
        (await RetrieveEntityAsync("account")).CanChangeTrackingBeEnabled.Value.Should().BeTrue();
        Store.ChangeTrackingTables.Should().NotContain("account", "no seed table is tracked out of the box (C3)");
    }

    [Fact]
    public async Task CreateEntity_with_tracking_on_works()
    {
        var name = Name("cr");
        await Admin.ExecuteAsync(CreateEntity(name, e => e.ChangeTrackingEnabled = true));

        (await RetrieveEntityAsync(name)).ChangeTrackingEnabled.Should().BeTrue();
        (await EnabledVersionAsync(name)).Should().BeGreaterThan(0);
        (await IndexExistsAsync(name)).Should().BeTrue();
        Store.ChangeTrackingTables.Should().Contain(name);

        // A value sent for the computed CanChangeTrackingBeEnabled is ignored on create (server-owned).
        var other = Name("co");
        await Admin.ExecuteAsync(CreateEntity(other, e => e.CanChangeTrackingBeEnabled = new BooleanManagedProperty(false)));
        (await RetrieveEntityAsync(other)).CanChangeTrackingBeEnabled.Value.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteEntity_removes_tracking_rows()
    {
        var name = Name("del");
        await Admin.ExecuteAsync(CreateEntity(name, e => e.ChangeTrackingEnabled = true));
        await ScalarAsync("INSERT INTO meta.deleted_record (entity_logical_name, object_id, deleted_on) VALUES (@t, NEWID(), SYSUTCDATETIME())", ("@t", name));

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name });

        (await EnabledVersionAsync(name)).Should().BeNull();
        (Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM meta.deleted_record WHERE entity_logical_name = @t", ("@t", name)))).Should().Be(0);
        Store.ChangeTrackingTables.Should().NotContain(name);
    }

    [Fact]
    public async Task Meta_tables_exist_with_their_shape()
    {
        (Convert.ToInt64(await ScalarAsync("SELECT purged_version FROM meta.change_tracking_state"))).Should().BeGreaterOrEqualTo(0);
        (Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM meta.change_tracking_state"))).Should().Be(1);
        // The engine's types (ruling P4): datetime2 / rowversion on SQL Server, timestamptz / bigint on PostgreSQL.
        (await TestSql.ColumnTypeAsync(fx.ConnectionString, "meta", "change_tracking_state", "restored_at"))!.Value.Type
            .Should().Be(TestProvider.IsPostgres ? "timestamp with time zone" : "datetime2");
        (await ScalarAsync($"""
            SELECT is_nullable FROM information_schema.columns
            WHERE table_schema = 'meta' AND table_name = 'change_tracking_state' AND column_name = 'restored_at'
            """)).Should().Be("YES");
        (await TestSql.ColumnTypeAsync(fx.ConnectionString, "meta", "deleted_record", "version"))!.Value.Type
            .Should().Be(TestProvider.IsPostgres ? "bigint" : "timestamp");
        // Deleted rows are read by version and purged by deletion time.
        (await TestSql.IndexAsync(fx.ConnectionString, "meta", "deleted_record", "CX_meta_deleted_record_version"))!.Keys.Should().Equal("version");
        (await TestSql.IndexAsync(fx.ConnectionString, "meta", "deleted_record", "IX_meta_deleted_record_deleted_on"))!.Keys.Should().Equal("deleted_on");
    }

    [SqlServerFact("migration 0007 is a T-SQL script; PostgreSQL's schema is one baseline")]
    public async Task Migration_0007_can_be_applied_again()
    {
        // Every CREATE is guarded: a re-applied script (also over a state table that lacks restored_at) changes nothing and
        // does not fail. Run in a transaction that is rolled back, so the shared database is left as it was.
        string script;
        await using (var stream = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("Xrmd.Metadata.Migrations.0007_change_tracking.sql")!)
        using (var reader = new StreamReader(stream))
            script = await reader.ReadToEndAsync();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        async Task<object?> RunAsync(string sql)
        {
            await using var cmd = TestSql.Command(conn, tx, sql);
            return await cmd.ExecuteScalarAsync();
        }

        await RunAsync("ALTER TABLE meta.change_tracking_state DROP COLUMN restored_at");
        foreach (var batch in System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$",
                     System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await RunAsync(batch);

        ((int)(await RunAsync("SELECT COUNT(*) FROM meta.change_tracking_state"))!).Should().Be(1);
        (await RunAsync("SELECT COL_LENGTH('meta.change_tracking_state', 'restored_at')")).Should().NotBeOfType<DBNull>();
        ((int)(await RunAsync("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('meta.deleted_record') AND name IS NOT NULL"))!)
            .Should().Be(2);
        await tx.RollbackAsync();
    }

    // ---- the organization setting -------------------------------------------------------------------------------------

    [Fact]
    public async Task Organization_expirechangetrackingindays_defaults_to_7()
    {
        var column = Store.GetEntity("organization")!.Attributes.Single(a => a.LogicalName == "expirechangetrackingindays");
        column.Should().BeOfType<IntegerAttributeMetadata>();
        column.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.SystemRequired);
        var org = (Guid)(await ScalarAsync("SELECT TOP 1 organizationid FROM data.organization"))!;
        async Task<int> DaysAsync() => (await Admin.RetrieveMultipleAsync(new QueryExpression("organization")
            {
                ColumnSet = new ColumnSet("expirechangetrackingindays"),
            })).Entities.Single().GetAttributeValue<int>("expirechangetrackingindays");
        (await DaysAsync()).Should().Be(ChangeTrackingSchema.DefaultExpireDays);

        // An organization made before this plan has the column empty: the seed fills it.
        await ScalarAsync("UPDATE data.organization SET expirechangetrackingindays = NULL");
        await new SystemSeed(Store, fx.App.Services.GetRequiredService<IDbConnectionFactory>())
            .EnsureAsync(new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), fx.AdminObjectId, XrmdFixture.AdminName));
        (await DaysAsync()).Should().Be(7);

        foreach (var bad in new object?[] { 366, -1, null })
        {
            var act = async () => await Admin.UpdateAsync(new Entity("organization", org) { ["expirechangetrackingindays"] = bad });
            var failure = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
            failure.HttpStatus.Should().Be(400, $"{bad}");
            failure.Message.Should().Contain("expirechangetrackingindays");
        }
        try
        {
            await Admin.UpdateAsync(new Entity("organization", org) { ["expirechangetrackingindays"] = 0 });
            (await DaysAsync()).Should().Be(0);
            await Admin.UpdateAsync(new Entity("organization", org) { ["expirechangetrackingindays"] = 365 });
            (await DaysAsync()).Should().Be(365);
        }
        finally
        {
            await Admin.UpdateAsync(new Entity("organization", org) { ["expirechangetrackingindays"] = 7 });
        }
    }

    // ---- solutions ----------------------------------------------------------------------------------------------------

    private async Task<string> ImportFixtureAsync(string projectFlag)
    {
        var jobId = Guid.NewGuid();
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(c => c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project")
                .Element("ChangeTrackingEnabled")!.Value = projectFlag),
            ImportJobId = jobId,
        });
        return (string)(await ScalarAsync("SELECT data FROM data.importjob WHERE importjobid = @id", ("@id", jobId)))!;
    }

    private async Task<XElement> ExportedProjectAsync()
    {
        var zip = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = "XrmverseFixture" })).ExportSolutionFile;
        var (_, customizations, _) = SolutionZip.Read(zip);
        return customizations.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project");
    }

    [Fact]
    public async Task Startup_upgrade_turns_on_tables_imported_with_ChangeTrackingEnabled_1()
    {
        await ImportFixtureAsync("1");
        Store.ChangeTrackingTables.Should().Contain("xv_project");
        (await EnabledVersionAsync("xv_project")).Should().BeGreaterThan(0);
        var exported = await ExportedProjectAsync();
        exported.Element("ChangeTrackingEnabled")!.Value.Should().Be("1");
        exported.Element("CanChangeTrackingBeEnabled")!.Value.Should().Be("1");

        // Import of 0 never switches tracking off: kept, with a warning.
        var log = await ImportFixtureAsync("0");
        Store.ChangeTrackingTables.Should().Contain("xv_project");
        log.Should().Contain("ChangeTrackingEnabled of 'xv_project'");
        (await ExportedProjectAsync()).Element("ChangeTrackingEnabled")!.Value.Should().Be("1");

        // An organization that imported the table before the setting was modelled: the value in the kept XML, none in the
        // metadata, no tracking row and no index.
        var kept = XElement.Parse(Store.GetUnmodelledXml(UnmodelledXml.EntityKey("xv_project").Item1, "xv_project")!);
        UnmodelledXml.IsMarker(kept.Element("ChangeTrackingEnabled")!).Should().BeTrue();
        UnmodelledXml.IsMarker(kept.Element("CanChangeTrackingBeEnabled")!).Should().BeTrue();
        kept.Element("ChangeTrackingEnabled")!.ReplaceWith(new XElement("ChangeTrackingEnabled", "1"));
        kept.Element("CanChangeTrackingBeEnabled")!.ReplaceWith(new XElement("CanChangeTrackingBeEnabled", "1"));
        await ScalarAsync("""
            UPDATE meta.entity SET unmodelled_xml = @x, definition = JSON_MODIFY(definition, '$.ChangeTrackingEnabled', NULL) WHERE logical_name = N'xv_project';
            DELETE FROM meta.change_tracking_table WHERE entity_logical_name = N'xv_project';
            """ + TestSql.Dialect.DropIndexIfExists("data", "xv_project", "IX_xv_project_versionnumber"), ("@x", kept.ToString(SaveOptions.DisableFormatting)));
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        var old = new MetadataStore(factory);
        await old.LoadAsync();
        old.GetEntity("xv_project")!.ChangeTrackingEnabled.Should().BeNull();

        await ChangeTrackingSettingsUpgrade.EnsureAsync(old);
        old.GetEntity("xv_project")!.ChangeTrackingEnabled.Should().BeTrue("the kept 1 is applied");
        old.ChangeTrackingTables.Should().Contain("xv_project");
        (await EnabledVersionAsync("xv_project")).Should().BeGreaterThan(0);
        (await IndexExistsAsync("xv_project")).Should().BeTrue();
        var after = XElement.Parse(old.GetUnmodelledXml(UnmodelledXml.EntityKey("xv_project").Item1, "xv_project")!);
        UnmodelledXml.IsMarker(after.Element("ChangeTrackingEnabled")!).Should().BeTrue("the element is a marker now");
        UnmodelledXml.IsMarker(after.Element("CanChangeTrackingBeEnabled")!).Should().BeTrue();
        var stamp = old.VersionStamp;
        await ChangeTrackingSettingsUpgrade.EnsureAsync(old);
        old.VersionStamp.Should().Be(stamp, "a second run changes nothing");

        // A tracked table whose index is missing gets it back at startup (C11).
        await ScalarAsync(TestSql.Dialect.DropIndexIfExists("data", "xv_project", "IX_xv_project_versionnumber"));
        await ChangeTrackingSettingsUpgrade.EnsureAsync(old);
        (await IndexExistsAsync("xv_project")).Should().BeTrue();

        await Store.LoadAsync();
        (await ExportedProjectAsync()).Element("ChangeTrackingEnabled")!.Value.Should().Be("1");
        (await RetrieveEntityAsync("xv_project")).ChangeTrackingEnabled.Should().BeTrue();
    }
}
