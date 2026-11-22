using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.16, task 1: the <c>teamtemplate</c> seed table, the three new <c>team</c> columns and the table flag
/// <c>AutoCreateAccessTeams</c> (ruling A1: where it can be set, the ownership rule, the template and table limits; ruling A5:
/// the targets of <c>team.regardingobjectid</c>).
/// </summary>
[Collection("xrmd")]
public sealed class AccessTeamMetadataTests(XrmdFixture fx)
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

    private Task SetFlagAsync(string name, bool value) =>
        Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = name, AutoCreateAccessTeams = value } });

    private string[] RegardingTargets() =>
        ((LookupAttributeMetadata)Store.GetEntity("team")!.Attributes.Single(a => a.LogicalName == "regardingobjectid")).Targets;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private async Task<JsonObject> GetJsonAsync(string path)
    {
        var res = await SendAsync(HttpMethod.Get, path);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.AddParameter(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A template row for the table of <paramref name="objectTypeCode"/>, written directly (the template rules are task 2's).</summary>
    private async Task<Guid> InsertTemplateAsync(int objectTypeCode)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync("""
            INSERT INTO data.teamtemplate (teamtemplateid, teamtemplatename, objecttypecode, defaultaccessrightsmask, issystem)
            VALUES (@id, @name, @otc, 1, 0)
            """, ("@id", id), ("@name", "Template " + _run), ("@otc", objectTypeCode));
        return id;
    }

    // ---- seed ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Seed_creates_teamtemplate_and_team_columns()
    {
        var template = Store.GetEntity("teamtemplate")!;
        template.Should().NotBeNull();
        template.ObjectTypeCode.Should().Be(92);
        template.EntitySetName.Should().Be("teamtemplates");
        template.OwnershipType.Should().Be(OwnershipTypes.OrganizationOwned);
        template.PrimaryIdAttribute.Should().Be("teamtemplateid");
        template.PrimaryNameAttribute.Should().Be("teamtemplatename");
        Store.GetEntityByObjectTypeCode(92)!.LogicalName.Should().Be("teamtemplate");
        AttributeMetadata Column(EntityMetadata e, string name) => e.Attributes.Single(a => a.LogicalName == name);
        var name = Column(template, "teamtemplatename").Should().BeOfType<StringAttributeMetadata>().Subject;
        name.MaxLength.Should().Be(100);
        name.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.ApplicationRequired);
        Column(template, "objecttypecode").Should().BeOfType<IntegerAttributeMetadata>().Which.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.ApplicationRequired);
        Column(template, "defaultaccessrightsmask").Should().BeOfType<IntegerAttributeMetadata>().Which.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.ApplicationRequired);
        Column(template, "description").Should().BeOfType<MemoAttributeMetadata>().Which.MaxLength.Should().Be(2000);
        var isSystem = Column(template, "issystem").Should().BeOfType<BooleanAttributeMetadata>().Subject;
        isSystem.DefaultValue.Should().BeFalse();
        (isSystem.IsValidForCreate, isSystem.IsValidForUpdate).Should().Be((false, false), "issystem is read-only");

        var team = Store.GetEntity("team")!;
        Column(team, "teamtemplateid").Should().BeOfType<LookupAttributeMetadata>().Which.Targets.Should().Equal("teamtemplate");
        Column(team, "regardingobjectid").Should().BeOfType<LookupAttributeMetadata>();
        TypeMapping.ToColumns(Column(team, "regardingobjectid")).Select(c => c.Name).Should().Equal("regardingobjectid", "regardingobjectidtype");
        Column(team, "systemmanaged").Should().BeOfType<BooleanAttributeMetadata>().Which.DefaultValue.Should().BeFalse();

        var relationship = (OneToManyRelationshipMetadata)Store.GetRelationship("teamtemplate_Teams")!;
        relationship.Should().NotBeNull();
        (relationship.ReferencedEntity, relationship.ReferencingEntity, relationship.ReferencingAttribute).Should().Be(("teamtemplate", "team", "teamtemplateid"));
        var cascade = relationship.CascadeConfiguration;
        cascade.Delete.Should().Be(CascadeType.Cascade);
        foreach (var other in new[] { cascade.Assign, cascade.Merge, cascade.Reparent, cascade.Share, cascade.Unshare, cascade.Archive, cascade.RollupView })
            other.Should().Be(CascadeType.NoCascade);
    }

    [Fact]
    public async Task Older_organization_gets_teamtemplate_and_the_team_columns()
    {
        var database = "xrmd_at_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
        await new SchemaMigrator(factory).MigrateAsync();
        var options = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
        async Task<MetadataStore> NewStoreAsync()
        {
            var store = new MetadataStore(factory);
            await store.LoadAsync();
            return store;
        }
        await new SystemSeed(await NewStoreAsync(), factory).EnsureAsync(options);

        // An organization seeded before this plan: no teamtemplate, no new team columns.
        await using (var conn = await factory.OpenAsync())
        await using (var cmd = TestSql.Command(conn, """
            DELETE FROM meta.relationship WHERE schema_name = 'teamtemplate_Teams';
            DELETE FROM meta.attribute WHERE entity_logical_name = 'team' AND logical_name IN ('teamtemplateid', 'regardingobjectid', 'systemmanaged');
            ALTER TABLE data.team DROP COLUMN teamtemplateid, regardingobjectid, regardingobjectidtype, systemmanaged;
            DELETE FROM meta.attribute WHERE entity_logical_name = 'teamtemplate';
            DELETE FROM meta.entity WHERE logical_name = 'teamtemplate';
            DROP TABLE data.teamtemplate;
            """))
            await cmd.ExecuteNonQueryAsync();
        var stale = await NewStoreAsync();
        stale.GetEntity("teamtemplate").Should().BeNull();
        stale.GetEntity("team")!.Attributes.Should().NotContain(a => a.LogicalName == "regardingobjectid");

        await new SystemSeed(stale, factory).EnsureAsync(options);
        var upgraded = await NewStoreAsync();
        upgraded.GetEntity("teamtemplate")!.ObjectTypeCode.Should().Be(92);
        var team = upgraded.GetEntity("team")!;
        team.Attributes.Select(a => a.LogicalName).Should().Contain(["teamtemplateid", "regardingobjectid", "systemmanaged"]);
        ((LookupAttributeMetadata)team.Attributes.Single(a => a.LogicalName == "regardingobjectid")).Targets.Should().BeEmpty();
        ((OneToManyRelationshipMetadata)upgraded.GetRelationship("teamtemplate_Teams")!).CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);
        await using (var conn = await factory.OpenAsync())
        await using (var cmd = TestSql.Command(conn, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'data' AND table_name = 'team' AND column_name IN ('teamtemplateid', 'regardingobjectid', 'regardingobjectidtype', 'systemmanaged')"))
            (Convert.ToInt32(await cmd.ExecuteScalarAsync())).Should().Be(4);
    }

    // ---- the table flag -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AutoCreateAccessTeams_round_trips_on_update()
    {
        var name = Name("rt");
        await Admin.ExecuteAsync(CreateEntity(name));
        (await RetrieveEntityAsync(name)).AutoCreateAccessTeams.Should().BeFalse("a created table starts without access teams");

        await SetFlagAsync(name, false);
        (await RetrieveEntityAsync(name)).AutoCreateAccessTeams.Should().BeFalse();
        await SetFlagAsync(name, true);
        (await RetrieveEntityAsync(name)).AutoCreateAccessTeams.Should().BeTrue();
        Store.AccessTeamTables.Should().Contain(name);

        // Web API: shown on EntityDefinitions, settable through PUT (the path of the other table settings).
        var path = $"EntityDefinitions(LogicalName='{name}')";
        var body = await GetJsonAsync(path);
        body["AutoCreateAccessTeams"]!.GetValue<bool>().Should().BeTrue();
        body["AutoCreateAccessTeams"] = false;
        var put = await SendAsync(HttpMethod.Put, path, body.ToJsonString());
        put.StatusCode.Should().Be(HttpStatusCode.NoContent, await put.Content.ReadAsStringAsync());
        (await RetrieveEntityAsync(name)).AutoCreateAccessTeams.Should().BeFalse();
        Store.AccessTeamTables.Should().NotContain(name);
        (await GetJsonAsync(path))["AutoCreateAccessTeams"]!.GetValue<bool>().Should().BeFalse();

        // CreateEntity takes it too.
        var created = Name("rc");
        await Admin.ExecuteAsync(CreateEntity(created, e => e.AutoCreateAccessTeams = true));
        (await RetrieveEntityAsync(created)).AutoCreateAccessTeams.Should().BeTrue();
        RegardingTargets().Should().Contain(created);
    }

    [Fact]
    public async Task AutoCreateAccessTeams_refused_on_organization_owned_table()
    {
        var name = Name("org");
        await Admin.ExecuteAsync(CreateEntity(name, e => e.OwnershipType = OwnershipTypes.OrganizationOwned));
        (await BadRequestAsync(() => SetFlagAsync(name, true))).Should().Contain("AutoCreateAccessTeams");
        (await RetrieveEntityAsync(name)).AutoCreateAccessTeams.Should().BeFalse();

        var created = Name("orc");
        (await BadRequestAsync(() => Admin.ExecuteAsync(CreateEntity(created, e =>
        {
            e.OwnershipType = OwnershipTypes.OrganizationOwned;
            e.AutoCreateAccessTeams = true;
        })))).Should().Contain("AutoCreateAccessTeams");
        Store.GetEntity(created).Should().BeNull();

        (await BadRequestAsync(() => SetFlagAsync("teamtemplate", true))).Should().Contain("AutoCreateAccessTeams");
        RegardingTargets().Should().NotContain([name, created, "teamtemplate"]);
    }

    [Fact]
    public async Task Enabling_adds_regarding_target()
    {
        var name = Name("tg");
        await Admin.ExecuteAsync(CreateEntity(name));
        RegardingTargets().Should().NotContain(name);

        await SetFlagAsync(name, true);
        RegardingTargets().Should().Contain(name);
        Store.GetOneToManyRelationships().Should().Contain(r => r.ReferencingEntity == "team" && r.ReferencingAttribute == "regardingobjectid"
                                                                && r.ReferencedEntity == name);
        // The target stays when the table is disabled: no team can refer to it then (no template, so no team).
        await SetFlagAsync(name, false);
        RegardingTargets().Should().Contain(name);
        await SetFlagAsync(name, true);
        RegardingTargets().Count(t => t == name).Should().Be(1);

        // A table that can be deleted takes its target with it.
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name });
        RegardingTargets().Should().NotContain(name);
        Store.AccessTeamTables.Should().NotContain(name);
    }

    [Fact]
    public async Task Disabling_with_templates_is_refused()
    {
        var name = Name("tp");
        await Admin.ExecuteAsync(CreateEntity(name, e => e.AutoCreateAccessTeams = true));
        var template = await InsertTemplateAsync(Store.GetEntity(name)!.ObjectTypeCode!.Value);

        (await BadRequestAsync(() => SetFlagAsync(name, false))).Should().Contain("template");
        (await RetrieveEntityAsync(name)).AutoCreateAccessTeams.Should().BeTrue();
        (await BadRequestAsync(() => Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name }))).Should().Contain("template");
        Store.GetEntity(name).Should().NotBeNull();

        await ExecuteAsync("DELETE FROM data.teamtemplate WHERE teamtemplateid = @id", ("@id", template));
        await SetFlagAsync(name, false);
        (await RetrieveEntityAsync(name)).AutoCreateAccessTeams.Should().BeFalse();
    }

    [Fact]
    public async Task Hundred_and_first_table_is_refused()
    {
        // A database of its own: the limit counts every enabled table of the organization.
        var database = "xrmd_at100_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
        await new SchemaMigrator(factory).MigrateAsync();
        var store = new MetadataStore(factory);
        await store.LoadAsync();
        await new SystemSeed(store, factory).EnsureAsync(new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada"));

        EntityMetadata Table(string logicalName, bool enabled) =>
            new() { LogicalName = logicalName, SchemaName = logicalName, OwnershipType = OwnershipTypes.UserOwned, AutoCreateAccessTeams = enabled };
        StringAttributeMetadata PrimaryName() => new() { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 };

        for (var i = 0; i < 100; i++)
            await store.CreateEntityAsync(Table($"new_at{i:D3}", enabled: true), PrimaryName());
        store.AccessTeamTables.Should().HaveCount(100);

        (await BadRequestAsync(() => store.CreateEntityAsync(Table("new_at100", enabled: true), PrimaryName()))).Should().Contain("100");
        store.GetEntity("new_at100").Should().BeNull();

        await store.CreateEntityAsync(Table("new_atoff", enabled: false), PrimaryName());
        async Task UpdateAsync(EntityMetadata changes)
        {
            await using var conn = await factory.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var session = new DataSession(conn, tx);
            try
            {
                await store.UpdateEntityAsync(session, changes, CancellationToken.None);
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                session.RolledBack();
                throw;
            }
            session.Committed();
        }
        (await BadRequestAsync(() => UpdateAsync(new EntityMetadata { LogicalName = "new_atoff", AutoCreateAccessTeams = true }))).Should().Contain("100");
        store.AccessTeamTables.Should().HaveCount(100).And.NotContain("new_atoff");

        // One disabled frees a place.
        await UpdateAsync(new EntityMetadata { LogicalName = "new_at000", AutoCreateAccessTeams = false });
        await UpdateAsync(new EntityMetadata { LogicalName = "new_atoff", AutoCreateAccessTeams = true });
        store.AccessTeamTables.Should().HaveCount(100).And.Contain("new_atoff");
    }

    // ---- solutions ----------------------------------------------------------------------------------------------------

    private Task ImportFixtureAsync(string projectFlag) => Admin.ExecuteAsync(new ImportSolutionRequest
    {
        CustomizationFile = SolutionFixtureZip.Build(c => c.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project")
            .Element("AutoCreateAccessTeams")!.Value = projectFlag),
        ImportJobId = Guid.NewGuid(),
    });

    private async Task<XElement> ExportedProjectAsync()
    {
        var zip = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = "XrmverseFixture" })).ExportSolutionFile;
        var (_, customizations, _) = SolutionZip.Read(zip);
        return customizations.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project");
    }

    [Fact]
    public async Task Import_sets_AutoCreateAccessTeams_and_upgrade_moves_kept_values()
    {
        await ImportFixtureAsync("1");
        Store.GetEntity("xv_project")!.AutoCreateAccessTeams.Should().BeTrue();
        RegardingTargets().Should().Contain("xv_project");
        (await ExportedProjectAsync()).Element("AutoCreateAccessTeams")!.Value.Should().Be("1");
        await ImportFixtureAsync("0");
        Store.GetEntity("xv_project")!.AutoCreateAccessTeams.Should().BeFalse("a re-import applies the file's setting");
        (await ExportedProjectAsync()).Element("AutoCreateAccessTeams")!.Value.Should().Be("0");

        // An organization that imported the table before the setting was modelled: the value in the kept XML, none in the metadata.
        var kept = XElement.Parse(Store.GetUnmodelledXml(UnmodelledXml.EntityKey("xv_project").Item1, "xv_project")!);
        UnmodelledXml.IsMarker(kept.Element("AutoCreateAccessTeams")!).Should().BeTrue();
        kept.Element("AutoCreateAccessTeams")!.ReplaceWith(new XElement("AutoCreateAccessTeams", "1"));
        await ExecuteAsync("UPDATE meta.entity SET unmodelled_xml = @x, definition = JSON_MODIFY(definition, '$.AutoCreateAccessTeams', NULL) WHERE logical_name = N'xv_project'",
            ("@x", kept.ToString(SaveOptions.DisableFormatting)));
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        var old = new MetadataStore(factory);
        await old.LoadAsync();
        old.GetEntity("xv_project")!.AutoCreateAccessTeams.Should().BeNull();

        await AccessTeamSettingsUpgrade.EnsureAsync(old);
        old.GetEntity("xv_project")!.AutoCreateAccessTeams.Should().BeTrue("the kept 1 is applied");
        var after = XElement.Parse(old.GetUnmodelledXml(UnmodelledXml.EntityKey("xv_project").Item1, "xv_project")!);
        UnmodelledXml.IsMarker(after.Element("AutoCreateAccessTeams")!).Should().BeTrue("the element is a marker now");
        var stamp = old.VersionStamp;
        await AccessTeamSettingsUpgrade.EnsureAsync(old);
        old.VersionStamp.Should().Be(stamp, "a second run changes nothing");

        await Store.LoadAsync();
        (await ExportedProjectAsync()).Element("AutoCreateAccessTeams")!.Value.Should().Be("1");
        (await RetrieveEntityAsync("xv_project")).AutoCreateAccessTeams.Should().BeTrue();
        await SetFlagAsync("xv_project", false);
    }
}
