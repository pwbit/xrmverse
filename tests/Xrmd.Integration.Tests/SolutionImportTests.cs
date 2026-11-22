using Xrmd.Abstractions.Storage;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline.Registration;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>The solution import tests run on a database of their own: the fixture's tables are created and dropped by every test.</summary>
[CollectionDefinition("solution-import")]
public class SolutionImportCollection : ICollectionFixture<XrmdFixture>;

/// <summary>
/// ImportSolution of the hand-written fixture (<see cref="SolutionFixtureZip"/>): every component applied in one transaction,
/// the <c>importjob</c> log, the rollback of a failed import, the idempotent re-import, the dependency check before any
/// write, managed solutions, the privileges and the system table rules.
/// </summary>
[Collection("solution-import")]
public sealed class SolutionImportTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Fixture = SolutionFixtureZip.FixtureName;
    private static readonly Guid RoleId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e01");
    private static readonly Guid AssemblyId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e10");
    private static readonly Guid CreateStepId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e21");
    private static readonly Guid UpdateStepId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e22");
    private static readonly Guid ImageId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e23");
    private static readonly Guid ViewId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e31");
    private static readonly Guid FormId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e32");
    private static readonly Guid WebResourceId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e41");
    private static readonly Guid[] TypeIds = [new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e11"), new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e12")];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await ClearImportAsync(fx);
    }

    public Task DisposeAsync() => ClearImportAsync(fx);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static Task<OrganizationResponse> ImportAsync(OrganizationService service, byte[] zip, Guid? jobId = null) =>
        service.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = jobId ?? Guid.NewGuid() });

    /// <summary>Removes what an import of the fixture creates (metadata through the store, rows with SQL).</summary>
    internal static Task ClearImportAsync(XrmdFixture fx) => ClearImportAsync(fx.App.Services, fx.ConnectionString);

    /// <summary>
    /// <see cref="ClearImportAsync(XrmdFixture)"/> for any host (<paramref name="services"/>) on the database
    /// <paramref name="connectionString"/>; the host must have <c>pb_pipelog</c> (<see cref="TestEntities.EnsurePipeAsync"/>).
    /// </summary>
    internal static async Task ClearImportAsync(IServiceProvider services, string connectionString)
    {
        var store = services.GetRequiredService<IMetadataStore>();
        await TestEntities.InSessionAsync(connectionString, async s =>
        {
            if (store.GetRelationship("xv_project_xv_tag") is not null) await store.DeleteRelationshipAsync(s, "xv_project_xv_tag", default);
            foreach (var entity in new[] { CustomActivitySolution.Table, "xv_task", "xv_tag", "xv_project" })
                if (store.GetEntity(entity) is not null) await store.DeleteEntityAsync(s, entity, default);
            if (store.GetOptionSet("xv_priority") is not null) await store.DeleteOptionSetAsync(s, "xv_priority", default);
        });
        // Plan 2.7a, Task 6: provider-neutral (the custom API, ribbon and secured-column classes clear with it on PostgreSQL too).
        await using (var conn = await TestProvider.OpenAsync(connectionString))
        {
            var d = SqlDialects.For(conn);
            string[] statements =
            [
                "DELETE FROM data.roleprivileges WHERE roleid = @role", "DELETE FROM data.systemuserroles WHERE roleid = @role",
                "DELETE FROM data.role WHERE roleid = @role",
                "DELETE FROM data.savedquery", "DELETE FROM data.systemform", "DELETE FROM data.sitemap", "DELETE FROM data.appmodule",
                "DELETE FROM data.webresource", "DELETE FROM data.solutioncomponent", "DELETE FROM data.importjob",
                "DELETE FROM data.environmentvariablevalue", "DELETE FROM data.environmentvariabledefinition",
                "DELETE FROM meta.customcontrol_file", "DELETE FROM data.customcontrol", "DELETE FROM data.savedqueryvisualization",
                "DELETE FROM data.solution WHERE uniquename NOT IN ('Default', 'Active')",
                "DELETE FROM data.publisher WHERE uniquename IN ('xrmverse', 'xrmverseapis')",
                "DELETE FROM data.pb_pipelog",
                d.When(d.TableExists("meta", "component_layer"), "DELETE FROM meta.component_layer"),
                d.When(d.TableExists("meta", "solution_rank"), "DELETE FROM meta.solution_rank"),
            ];
            foreach (var statement in statements)
            {
                await using var cmd = TestSql.Command(conn, statement, ("@role", RoleId));
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await PluginTestHelper.ClearAsync(connectionString);
        services.GetRequiredService<StepRegistry>().Invalidate();
        services.GetRequiredService<Xrmd.Pipeline.CustomApis.CustomApiRegistry>().Invalidate();
        services.GetRequiredService<SecurityCache>().Invalidate();
    }

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt32(await ScalarAsync(sql, parameters));

    private async Task<Guid> FixtureSolutionIdAsync() =>
        (Guid)(await ScalarAsync("SELECT solutionid FROM data.solution WHERE uniquename = @n", ("@n", Fixture)))!;

    private async Task<(double Progress, DateTime? CompletedOn, string? SolutionName, string Data)> ImportJobAsync(Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("SELECT progress, completedon, solutionname, data FROM data.importjob WHERE importjobid = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue("the import job {0} must exist", id);
        return (rd.GetDouble(0), rd.IsDBNull(1) ? null : rd.GetDateTime(1), rd.IsDBNull(2) ? null : rd.GetString(2), rd.GetString(3));
    }

    /// <summary>The <c>xv_*</c> tables of the database.</summary>
    private async Task<int> FixtureTablesAsync() =>
        await CountAsync("SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = N'data' AND TABLE_TYPE = N'BASE TABLE' AND TABLE_NAME LIKE N'xv[_]%'");

    private static XElement EntityInfo(XDocument customizations, string schemaName) =>
        customizations.Root!.Element("Entities")!.Elements("Entity").Single(e => e.Element("Name")!.Value == schemaName)
            .Element("EntityInfo")!.Element("entity")!;

    private static XElement Attribute(XDocument customizations, string entity, string physicalName) =>
        EntityInfo(customizations, entity).Element("attributes")!.Elements("attribute")
            .Single(a => (string?)a.Attribute("PhysicalName") == physicalName);

    private static XElement Step(XDocument customizations, Guid id) =>
        customizations.Root!.Element("SdkMessageProcessingSteps")!.Elements("SdkMessageProcessingStep")
            .Single(s => Guid.Parse((string)s.Attribute("SdkMessageProcessingStepId")!) == id);

    private static XElement Relationship(XDocument customizations, string name) =>
        customizations.Root!.Element("EntityRelationships")!.Elements("EntityRelationship").Single(r => (string?)r.Attribute("Name") == name);

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Fixture_import_creates_every_component_in_order()
    {
        var jobId = Guid.NewGuid();
        var response = await ImportAsync(Admin, SolutionFixtureZip.Build(), jobId);
        response.ResponseName.Should().Be("ImportSolution");

        // Metadata.
        var project = Store.GetEntity("xv_project")!;
        project.Should().NotBeNull();
        project.SchemaName.Should().Be("xv_Project");
        project.DisplayName.LocalizedLabels.Select(l => l.LanguageCode).Should().BeEquivalentTo([1033, 1040]);
        project.Attributes.Select(a => a.LogicalName).Should().Contain(
            ["xv_name", "xv_description", "xv_count", "xv_ratio", "xv_budget", "xv_budget_base", "xv_isactive", "xv_startdate", "xv_priority",
             "xv_stage", "ownerid"]);
        ((PicklistAttributeMetadata)project.Attributes.Single(a => a.LogicalName == "xv_priority")).OptionSet.IsGlobal.Should().BeTrue();
        Store.GetEntity("xv_tag")!.OwnershipType.Should().Be(OwnershipTypes.OrganizationOwned);
        Store.GetEntity("xv_task")!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "xv_projectid")
            .Targets.Should().Equal("xv_project");
        Store.GetOptionSet("xv_priority").Should().NotBeNull();
        Store.GetRelationship("xv_project_xv_tag").Should().BeOfType<ManyToManyRelationshipMetadata>();
        Store.GetRelationship("xv_project_xv_task_ProjectId").Should().BeOfType<OneToManyRelationshipMetadata>();
        // System relationships of the file are not created: xrmverse derives its own.
        Store.GetRelationship("xv_project_SyncErrors").Should().BeNull();

        // Rows.
        (await CountAsync("SELECT COUNT(*) FROM data.publisher WHERE uniquename = N'xrmverse' AND customizationprefix = N'xv'")).Should().Be(1);
        (await ScalarAsync("SELECT version FROM data.solution WHERE uniquename = @n", ("@n", Fixture))).Should().Be("1.0.0.0");
        (await ScalarAsync("SELECT name FROM data.role WHERE roleid = @id", ("@id", RoleId))).Should().Be("Xrmverse Fixture User");
        (await ScalarAsync("SELECT returnedtypecode FROM data.savedquery WHERE savedqueryid = @id", ("@id", ViewId))).Should().Be("xv_project");
        (await ScalarAsync("SELECT type FROM data.systemform WHERE systemformid = @id", ("@id", FormId))).Should().Be(2);
        (await ScalarAsync("SELECT webresourcetype FROM data.webresource WHERE webresourceid = @id", ("@id", WebResourceId))).Should().Be(3);
        (await CountAsync("SELECT COUNT(*) FROM data.sitemap WHERE sitemapnameunique = N'xv_fixtureapp' AND sitemapxml LIKE N'<SiteMap%'"))
            .Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.appmodule WHERE uniquename = N'xv_fixtureapp'")).Should().Be(1);
        (await ScalarAsync("SELECT publickeytoken FROM data.pluginassembly WHERE pluginassemblyid = @id", ("@id", AssemblyId)))
            .Should().Be("a15f04ad73430aaf");
        (await CountAsync("SELECT COUNT(*) FROM data.plugintype WHERE pluginassemblyid = @id", ("@id", AssemblyId))).Should().Be(2);
        (await ScalarAsync("SELECT sdkmessageid FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", CreateStepId)))
            .Should().Be(SdkMessageCatalog.MessageId("Create"));
        (await ScalarAsync("SELECT sdkmessagefilterid FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", UpdateStepId)))
            .Should().Be(SdkMessageCatalog.FilterId("Update", "xv_project"));
        (await ScalarAsync("SELECT entityalias FROM data.sdkmessageprocessingstepimage WHERE sdkmessageprocessingstepimageid = @id", ("@id", ImageId)))
            .Should().Be("PreImage");
        var keptXml = XElement.Parse((string)(await ScalarAsync("SELECT xrmverse_unsupportedxml FROM data.solution WHERE uniquename = @n", ("@n", Fixture)))!);
        keptXml.Elements("Entity").Should().BeEmpty("the fixture's entity ribbons are the empty template");
        (keptXml.Element("ImportExportXml")?.Elements() ?? []).Should().BeEmpty("the fixture's unsupported root nodes are empty");
        keptXml.Element("SolutionManifest").Should().NotBeNull("the solution's names and its publisher are kept for the export");

        // solutioncomponent: every component in the fixture solution (and in Default).
        var solutionId = await FixtureSolutionIdAsync();
        async Task<int> Rows(int type, Guid? objectId = null) => objectId is { } id
            ? await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @s AND componenttype = @t AND objectid = @o",
                ("@s", solutionId), ("@t", type), ("@o", id))
            : await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @s AND componenttype = @t",
                ("@s", solutionId), ("@t", type));
        (await Rows(ComponentType.Entity, project.MetadataId)).Should().Be(1);
        (await Rows(ComponentType.Entity)).Should().Be(3);
        (await Rows(ComponentType.Attribute)).Should().Be(12, "9 custom attributes of xv_project (the generated xv_budget_base is not tracked, as with CreateAttribute), the primary names of xv_tag and xv_task, the lookup");
        (await Rows(ComponentType.OptionSet, MetadataStore.OptionSetId("xv_priority"))).Should().Be(1);
        (await Rows(ComponentType.EntityRelationship)).Should().Be(2);
        (await Rows(ComponentType.Relationship)).Should().Be(0, "type 3 is recorded as 10");
        (await Rows(ComponentType.Role, RoleId)).Should().Be(1);
        (await Rows(ComponentType.SavedQuery, ViewId)).Should().Be(1);
        (await Rows(ComponentType.SystemForm, FormId)).Should().Be(1);
        (await Rows(ComponentType.WebResource, WebResourceId)).Should().Be(1);
        (await Rows(ComponentType.SiteMap)).Should().Be(1);
        (await Rows(ComponentType.AppModule)).Should().Be(1);
        (await Rows(ComponentType.PluginAssembly, AssemblyId)).Should().Be(1);
        (await Rows(ComponentType.PluginType)).Should().Be(2);
        (await Rows(ComponentType.SdkMessageProcessingStep)).Should().Be(2);
        (await Rows(ComponentType.SdkMessageProcessingStepImage, ImageId)).Should().Be(1);
        (await CountAsync("""
            SELECT COUNT(*) FROM data.solutioncomponent AS a
            JOIN data.solutioncomponent AS e ON e.solutioncomponentid = a.rootsolutioncomponentid
            WHERE a.solutionid = @s AND a.componenttype = 2 AND e.componenttype = 1
            """, ("@s", solutionId))).Should().Be(12, "every attribute is linked to its entity's row");
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @s AND objectid = @o",
            ("@s", SystemSeed.DefaultSolutionId), ("@o", project.MetadataId!.Value))).Should().Be(1);

        // The import job.
        var job = await ImportJobAsync(jobId);
        job.Progress.Should().Be(100);
        job.CompletedOn.Should().NotBeNull();
        job.SolutionName.Should().Be(Fixture);
        var log = XElement.Parse(job.Data);
        log.Name.LocalName.Should().Be("importexportxml");
        log.Attribute("progress")!.Value.Should().Be("100");
        log.Element("entities")!.Elements("entity").Select(e => (string)e.Attribute("id")!).Should().Contain(["xv_project", "xv_tag", "xv_task"]);
        // §7 order in the log: option sets, entities, relationships, roles, opaque nodes, web resources, assemblies, steps.
        log.Elements().Select(e => e.Name.LocalName).Should().ContainInOrder("solutionManifests", "optionSets", "entities",
            "entityRelationships", "roles", "nodes", "webResources", "SolutionPluginAssemblies", "SdkMessageProcessingSteps");
        // The money column brings the currency pair (plan 2.10, X13): the file's pair is not reported as not created.
        job.Data.Should().NotContain("System attribute 'xv_project.transactioncurrencyid' is not created");
        job.Data.Should().NotContain("System attribute 'xv_project.exchangerate' is not created");
        job.Data.Should().Contain("System attribute 'xv_project.utcconversiontimezonecode' is not created: xrmverse does not generate it.");
        var imported = Store.GetEntity("xv_project")!;
        CurrencySchema.HasCurrencyColumns(imported).Should().BeTrue();
        Store.GetRelationship("TransactionCurrency_xv_project").Should().BeOfType<OneToManyRelationshipMetadata>()
            .Which.CascadeConfiguration.Delete.Should().Be(CascadeType.Restrict);
    }

    [Fact]
    public async Task Solution_file_over_95_MB_is_refused_by_every_import_message_before_anything_is_written()
    {
        var file = new byte[SolutionImporter.MaxCustomizationFileBytes + 1];
        OrganizationRequest[] requests =
        [
            new ImportSolutionRequest { CustomizationFile = file, ImportJobId = Guid.NewGuid() },
            new ImportSolutionAsyncRequest { CustomizationFile = file, ImportJobId = Guid.NewGuid() },
            new Microsoft.Xrm.Sdk.Messages.ExecuteAsyncRequest { Request = new ImportSolutionRequest { CustomizationFile = file } },
        ];

        foreach (var request in requests)
        {
            var fault = await FaultAsync(() => Admin.ExecuteAsync(request));
            fault.ErrorCode.Should().Be(ErrorCodes.ImportFileTooLargeToUpload, request.RequestName);
            fault.Message.Should().Be("The import file is too large to upload.");
            fault.HttpStatus.Should().Be(400);
        }

        (await CountAsync("SELECT COUNT(*) FROM data.importjob")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE operationtype = 203")).Should().Be(0);
    }

    [Fact]
    public async Task Imported_plugins_fire_on_create_and_update()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build());

        var id = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "Apollo" });
        var created = await Admin.RetrieveAsync("xv_project", id, new ColumnSet("xv_description"));
        created.GetAttributeValue<string>("xv_description").Should().Be("set by the fixture pre-operation create step");

        await Admin.UpdateAsync(new Entity("xv_project", id) { ["xv_name"] = "Apollo 2" });
        var logs = await Admin.RetrieveMultipleAsync(new QueryExpression(TestEntities.PipeLog) { ColumnSet = new ColumnSet("pb_name", "pb_data") });
        var record = logs.Entities.Should().ContainSingle().Which;
        record.GetAttributeValue<string>("pb_name").Should().Be("Update|40|0|1");
        record.GetAttributeValue<string>("pb_data").Should().Contain("pre=PreImage:").And.Contain("xv_name");

        // The filtering attributes hold: an update of another column fires nothing.
        await Admin.UpdateAsync(new Entity("xv_project", id) { ["xv_count"] = 3 });
        (await Admin.RetrieveMultipleAsync(new QueryExpression(TestEntities.PipeLog))).Entities.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_of_an_assembly_whose_name_exists_under_another_id_fails_before_writing(bool managed)
    {
        // Issue #70: a plug-in assembly name is unique; the import does not merge the file's assembly into another row.
        var existing = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes, "xrmd.testplugins");
        var stamp = Store.VersionStamp;
        var zip = managed
            ? SolutionFixtureZip.Build(solution: s => s.Root!.Element("SolutionManifest")!.Element("Managed")!.Value = "1")
            : SolutionFixtureZip.Build();

        var fault = await FaultAsync(() => ImportAsync(Admin, zip));

        fault.HttpStatus.Should().Be(412);
        fault.ErrorCode.Should().Be(ErrorCodes.DuplicateRecord);
        fault.Message.Should().Contain("'Xrmd.TestPlugins'").And.Contain(AssemblyId.ToString("D")).And.Contain(existing.ToString("D"));
        (await FixtureTablesAsync()).Should().Be(0);
        Store.VersionStamp.Should().Be(stamp);
        (await CountAsync("SELECT COUNT(*) FROM data.pluginassembly")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.plugintype")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", Fixture))).Should().Be(0);
    }

    [Fact]
    public async Task Failure_rolls_back_everything_and_logs_the_importjob()
    {
        var stamp = Store.VersionStamp;
        var entities = Store.GetAllEntities().Count;
        var jobId = Guid.NewGuid();
        var zip = SolutionFixtureZip.Build(c =>
        {
            var missing = Guid.NewGuid().ToString("D");
            Step(c, CreateStepId).Element("PluginTypeId")!.Value = missing;
        });

        var fault = await FaultAsync(() => ImportAsync(Admin, zip, jobId));

        fault.HttpStatus.Should().Be(404);
        (await FixtureTablesAsync()).Should().Be(0, "the tables created before the failing step are rolled back");
        Store.GetEntity("xv_project").Should().BeNull();
        Store.GetOptionSet("xv_priority").Should().BeNull();
        Store.GetAllEntities().Should().HaveCount(entities);
        Store.VersionStamp.Should().Be(stamp, "the metadata cache is unchanged");
        (await CountAsync("SELECT COUNT(*) FROM data.privilege WHERE name LIKE N'prv%xv[_]project'")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", Fixture))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.role WHERE roleid = @id", ("@id", RoleId))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.pluginassembly")).Should().Be(0);

        var job = await ImportJobAsync(jobId);
        job.Progress.Should().Be(0);
        job.CompletedOn.Should().NotBeNull();
        job.SolutionName.Should().Be(Fixture);
        job.Data.Should().Contain(fault.Message);
        XElement.Parse(job.Data).Descendants("result").Should().Contain(r => (string?)r.Attribute("result") == "failure");
    }

    /// <summary>The first option (100000000) of the fixture's global choice <c>xv_priority</c>.</summary>
    private static XElement PriorityOption(XDocument customizations) =>
        customizations.Root!.Element("optionsets")!.Elements("optionset").Single(o => (string?)o.Attribute("Name") == "xv_priority")
            .Element("options")!.Elements("option").First(o => (string?)o.Attribute("value") == "100000000");

    private string? PriorityColor() =>
        ((OptionSetMetadata)Store.GetOptionSet("xv_priority")!).Options.Single(o => o.Value == 100000000).Color;

    [Fact]
    public async Task Import_updates_the_colour_of_an_existing_option()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build());
        PriorityColor().Should().BeNullOrEmpty();

        await ImportAsync(Admin, SolutionFixtureZip.Build(c => PriorityOption(c).SetAttributeValue("Color", "#FFB900"),
            s => s.Root!.Element("SolutionManifest")!.Element("Version")!.Value = "1.1.0.0"));
        PriorityColor().Should().Be("#FFB900");

        // An upgrade that leaves Color out keeps the colour, as it keeps a label left empty ...
        await ImportAsync(Admin, SolutionFixtureZip.Build(null, s => s.Root!.Element("SolutionManifest")!.Element("Version")!.Value = "1.2.0.0"));
        PriorityColor().Should().Be("#FFB900");

        // ... while a replacing update (a restore of a layer) takes the option as the whole option: no Color clears it.
        var layer = new OptionSetMetadata([new OptionMetadata(Label("High"), 100000000)]) { Name = "xv_priority" };
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.UpdateOptionSetAsync(s, layer, replaceLabels: true, default));
        PriorityColor().Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Import_refuses_a_bad_colour()
    {
        var message = await BadRequestAsync(() => ImportAsync(Admin,
            SolutionFixtureZip.Build(c => PriorityOption(c).SetAttributeValue("Color", "blue"))));

        message.Should().Contain("100000000").And.Contain("'blue'");
    }

    [Fact]
    public async Task Reimport_is_an_idempotent_upgrade()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build());
        var project = Store.GetEntity("xv_project")!;
        var projectId = project.MetadataId;
        var countId = project.Attributes.Single(a => a.LogicalName == "xv_count").MetadataId;

        var upgrade = SolutionFixtureZip.Build(c =>
        {
            // A new column, a new option in the global set, a new label of an existing column.
            var count = Attribute(c, "xv_Project", "xv_Count");
            var extra = new XElement(count);
            extra.SetAttributeValue("PhysicalName", "xv_Extra");
            extra.Element("Name")!.Value = "xv_extra";
            extra.Element("LogicalName")!.Value = "xv_extra";
            count.AddAfterSelf(extra);
            var name = Attribute(c, "xv_Project", "xv_Name");
            name.Element("displaynames")!.Elements("displayname").First(d => (string?)d.Attribute("languagecode") == "1033")
                .SetAttributeValue("description", "Project title");
            var priority = c.Root!.Element("optionsets")!.Elements("optionset").Single(o => (string?)o.Attribute("Name") == "xv_priority");
            var options = priority.Element("options")!;
            var option = new XElement(options.Elements("option").Last());
            option.SetAttributeValue("value", "100000099");
            options.Add(option);
        }, s => s.Root!.Element("SolutionManifest")!.Element("Version")!.Value = "1.1.0.0");
        await ImportAsync(Admin, upgrade);

        project = Store.GetEntity("xv_project")!;
        project.MetadataId.Should().Be(projectId);
        project.Attributes.Should().ContainSingle(a => a.LogicalName == "xv_extra");
        project.Attributes.Single(a => a.LogicalName == "xv_count").MetadataId.Should().Be(countId);
        project.Attributes.GroupBy(a => a.LogicalName).Should().OnlyContain(g => g.Count() == 1);
        project.Attributes.Single(a => a.LogicalName == "xv_name").DisplayName.LocalizedLabels.Single(l => l.LanguageCode == 1033)
            .Label.Should().Be("Project title");
        ((OptionSetMetadata)Store.GetOptionSet("xv_priority")!).Options.Select(o => o.Value).Should().Contain(100000099).And.OnlyHaveUniqueItems();
        (await TestSql.ColumnExistsAsync(fx.ConnectionString, "data", "xv_project", "xv_extra")).Should().BeTrue();

        (await ScalarAsync("SELECT version FROM data.solution WHERE uniquename = @n", ("@n", Fixture))).Should().Be("1.1.0.0");
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", Fixture))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.publisher WHERE uniquename = N'xrmverse'")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.role WHERE roleid = @id", ("@id", RoleId))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = @id", ("@id", RoleId))).Should().Be(6);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep")).Should().Be(2);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstepimage")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.sitemap")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.appmodule")).Should().Be(1);
        var solutionId = await FixtureSolutionIdAsync();
        (await CountAsync("""
            SELECT COUNT(*) FROM (SELECT componenttype, objectid FROM data.solutioncomponent WHERE solutionid = @s
            GROUP BY componenttype, objectid HAVING COUNT(*) > 1) AS d
            """, ("@s", solutionId))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @s AND componenttype = 2", ("@s", solutionId)))
            .Should().Be(13);
    }

    [Fact]
    public async Task Reimport_updates_the_behavior_of_a_root_component()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build());
        await ImportAsync(Admin, SolutionFixtureZip.Build(solution: s =>
            s.Descendants("RootComponent").Single(r => (string?)r.Attribute("schemaName") == "xv_tag").SetAttributeValue("behavior", "1")));

        (await ScalarAsync("SELECT rootcomponentbehavior FROM data.solutioncomponent WHERE solutionid = @s AND objectid = @o",
            ("@s", await FixtureSolutionIdAsync()), ("@o", Store.GetEntity("xv_tag")!.MetadataId!.Value))).Should().Be(1);
    }

    [Fact]
    public async Task Missing_dependency_fails_before_writing()
    {
        var stamp = Store.VersionStamp;
        var zip = SolutionFixtureZip.Build(c =>
            Relationship(c, "xv_project_xv_task_ProjectId").Element("ReferencedEntityName")!.Value = "xv_missing");

        var fault = await FaultAsync(() => ImportAsync(Admin, zip));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xv_missing");
        (await FixtureTablesAsync()).Should().Be(0);
        Store.VersionStamp.Should().Be(stamp);
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", Fixture))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.importjob WHERE progress = 0")).Should().Be(1, "the failure is logged");
    }

    [Fact]
    public async Task Managed_import_is_layered()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, SolutionFixtureZip.Build(solution: s =>
            s.Root!.Element("SolutionManifest")!.Element("Managed")!.Value = "1"), jobId);

        (await ScalarAsync("SELECT ismanaged FROM data.solution WHERE uniquename = @n", ("@n", Fixture))).Should().Be(true);
        Store.GetEntity("xv_project")!.IsManaged.Should().BeTrue();
        var job = await ImportJobAsync(jobId);
        job.Progress.Should().Be(100);
        job.Data.Should().NotContain("imported as unmanaged");
        // One Managed layer per component of the solution, and no Active marker left by the import's own writes.
        const string components = "SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid WHERE s.uniquename = @n";
        const string layers = """
            SELECT COUNT(*) FROM meta.component_layer l JOIN data.solution s ON s.solutionid = l.solution_id
            WHERE s.uniquename = @n AND EXISTS (SELECT 1 FROM data.solutioncomponent sc WHERE sc.solutionid = s.solutionid
                AND sc.componenttype = l.component_type AND sc.objectid = l.object_id)
            """;
        var count = Convert.ToInt32(await ScalarAsync(components, ("@n", Fixture)));
        count.Should().BeGreaterThan(0);
        (await ScalarAsync(layers, ("@n", Fixture))).Should().Be(count);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.component_layer WHERE solution_id = @active", ("@active", SystemSeed.ActiveSolutionId))).Should().Be(0);
    }

    [Fact]
    public async Task Real_export_shapes_import_with_warnings_and_unread_entries_round_trip()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, SolutionFixtureZip.BuildWithRealShapes(), jobId);

        var job = await ImportJobAsync(jobId);
        job.Progress.Should().Be(100);
        job.Data.Should().Contain("The solution declares 5 missing dependencies: xrmverse does not verify the declared list")
            .And.NotContain("Root component '6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e01'", "the parentId root is the fixture role");
        foreach (var folder in new[] { "bots", "botcomponents", "Formulas", "Workflows" })
            job.Data.Should().Contain($"The '{folder}/' entries of the solution file (");
        job.Data.Should().NotContain("The 'customapis/' entries", "custom APIs are read since plan 15");
        job.Data.Should().NotContain("The 'environmentvariabledefinitions/' entries", "environment variables are applied since #167");
        (await ScalarAsync("""
            SELECT sc.rootcomponentbehavior FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
            WHERE s.uniquename = @n AND sc.objectid = @role
            """, ("@n", Fixture), ("@role", RoleId))).Should().Be(0, "the role is a root component of the solution");

        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = Fixture, Managed = false }))
            .ExportSolutionFile;
        var (exportedSolution, exportedCustomizations, files) = SolutionZip.Read(exported);
        foreach (var (path, content) in SolutionFixtureZip.RealShapesEntries().Where(e => !e.Key.StartsWith("customapis/", StringComparison.Ordinal)
                     && !e.Key.StartsWith("environmentvariabledefinitions/", StringComparison.Ordinal)))
            files.Should().ContainKey(path).WhoseValue.Should().Equal(content, "entry {0} is kept byte for byte", path);
        // The environment variables are applied (#167) and written again from the rows: the same definitions, fields and values.
        var (originalSolution, originalCustomizations, originalFiles) = SolutionZip.Read(SolutionFixtureZip.BuildWithRealShapes());
        var before = CustomizationsReader.Read(originalSolution, originalCustomizations, originalFiles).EnvironmentVariables;
        var after = CustomizationsReader.Read(exportedSolution, exportedCustomizations, files).EnvironmentVariables;
        before.Should().ContainSingle().Which.Value.Should().Be("https://service.example.org");
        after.Should().BeEquivalentTo(before, o => o.Excluding(v => v.Source));
        exportedSolution.Descendants("RootComponent").Should().Contain(r => (string?)r.Attribute("type") == "380"
            && (string?)r.Attribute("schemaName") == "xv_ServiceUrl");
        // The custom API is read (plan 15) and written again by the writer, with the elements it leaves out given their defaults.
        var api = XElement.Parse(System.Text.Encoding.UTF8.GetString(files.Should().ContainKey("customapis/xv_PickNextTask/customapi.xml").WhoseValue));
        ((string?)api.Attribute("uniquename")).Should().Be("xv_PickNextTask");
        ((string?)api.Element("bindingtype")).Should().Be("0");
        ((string?)api.Element("isfunction")).Should().Be("0");
        ((string?)api.Element("name")).Should().Be("xv_PickNextTask");
        files.Keys.Where(k => k.StartsWith("WebResources/", StringComparison.Ordinal)).Should().ContainSingle("the export writes its own web resource once");
    }

    [Fact]
    public async Task Import_of_a_microsoft_publisher_solution_warns_and_succeeds()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, SolutionFixtureZip.Build(solution: s =>
            s.Root!.Element("SolutionManifest")!.Element("Publisher")!.Element("CustomizationPrefix")!.Value = "msdyn"), jobId);

        Store.GetEntity("xv_project").Should().NotBeNull("the import is never blocked");
        var job = await ImportJobAsync(jobId);
        job.Progress.Should().Be(100);
        job.Data.Should().Contain("The solution 'XrmverseFixture' comes from a Microsoft publisher ('xrmverse', prefix 'msdyn'). "
            + "Microsoft first-party solutions are not supported by xrmverse.");
    }

    [Fact]
    public async Task Import_requires_prvImportCustomization()
    {
        var root = await RootBusinessUnitAsync(fx);
        var basicOid = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, root, basicOid), StandardRoles.BasicUser);
        var customizerOid = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, root, customizerOid), StandardRoles.SystemCustomizer);

        var fault = await FaultAsync(() => ImportAsync(ServiceFor(fx, basicOid), SolutionFixtureZip.Build()));
        fault.HttpStatus.Should().Be(403);
        fault.Message.Should().Contain("prvImportCustomization");
        (await FixtureTablesAsync()).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.importjob")).Should().Be(0, "a refused caller writes nothing, not even the log");

        await ImportAsync(ServiceFor(fx, customizerOid), SolutionFixtureZip.Build());
        Store.GetEntity("xv_project").Should().NotBeNull();
    }

    [Fact]
    public async Task Imported_step_impersonating_another_user_requires_act_on_behalf()
    {
        var root = await RootBusinessUnitAsync(fx);
        var other = await CreateUserAsync(fx, root);
        var customizerOid = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, root, customizerOid), StandardRoles.SystemCustomizer);
        var zip = SolutionFixtureZip.Build(c =>
            Step(c, CreateStepId).Element("PrimaryEntity")!.AddAfterSelf(new XElement("ImpersonatingUserId", other.ToString("B"))));

        var fault = await FaultAsync(() => ImportAsync(ServiceFor(fx, customizerOid), zip));
        fault.HttpStatus.Should().Be(403);
        fault.ErrorCode.Should().Be(ErrorCodes.CannotActOnBehalfOfAnotherUser);
        (await FixtureTablesAsync()).Should().Be(0);

        await ImportAsync(Admin, zip);
        (await ScalarAsync("SELECT impersonatinguserid FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id",
            ("@id", CreateStepId))).Should().Be(other);
    }

    [Fact]
    public async Task Role_privileges_resolve_by_name_and_unknown_ones_are_warnings()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, SolutionFixtureZip.Build(c =>
            c.Root!.Element("Roles")!.Element("Role")!.Element("RolePrivileges")!
                .Add(new XElement("RolePrivilege", new XAttribute("name", "prvReadopportunity"), new XAttribute("level", "Global")))), jobId);

        var granted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command("""
                SELECT p.name, rp.privilegedepthmask FROM data.roleprivileges AS rp JOIN data.privilege AS p ON p.privilegeid = rp.privilegeid
                WHERE rp.roleid = @role
                """, conn);
            cmd.Parameters.AddWithValue("@role", RoleId);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) granted[rd.GetString(0)] = rd.GetInt32(1);
        }
        granted.Should().BeEquivalentTo(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["prvCreatexv_Project"] = (int)DepthMask.Basic,
            ["prvReadxv_Project"] = (int)DepthMask.Basic,
            ["prvWritexv_Project"] = (int)DepthMask.Basic,
            ["prvReadxv_Tag"] = (int)DepthMask.Global,
            ["prvReadxv_Task"] = (int)DepthMask.Local,
            ["prvAppendToxv_Project"] = (int)DepthMask.Deep,
        });
        (await ImportJobAsync(jobId)).Data.Should().Contain("Privilege 'prvReadopportunity' of role 'Xrmverse Fixture User' does not exist in xrmverse and was skipped.");

        // The imported role works: a user holding it creates an xv_project.
        var oid = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid), RoleId);
        await ServiceFor(fx, oid).CreateAsync(new Entity("xv_project") { ["xv_name"] = "by the role" });
    }

    [Theory]
    [InlineData("a1bdbb1b-ea3e-db11-86a7-000a3a5473e8", "Delete")]
    [InlineData("f2bdbb1b-ea3e-db11-86a7-000a3a5473e8", "Retrieve")]
    [InlineData("03bebb1b-ea3e-db11-86a7-000a3a5473e8", "RetrieveMultiple")]
    public async Task Step_with_a_dataverse_sdk_message_id_seen_in_a_real_export_binds_to_that_message(string dataverseId, string message)
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build(c =>
            Step(c, CreateStepId).Element("SdkMessageId")!.Value = Guid.Parse(dataverseId).ToString("B")));

        (await ScalarAsync("SELECT sdkmessageid FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", CreateStepId)))
            .Should().Be(SdkMessageCatalog.MessageId(message));
    }

    [Fact]
    public async Task Step_with_an_unknown_sdk_message_id_is_400_naming_the_id()
    {
        var unknown = Guid.NewGuid();
        var fault = await FaultAsync(() => ImportAsync(Admin, SolutionFixtureZip.Build(c =>
            Step(c, CreateStepId).Element("SdkMessageId")!.Value = unknown.ToString("B"))));
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(unknown.ToString());
        (await FixtureTablesAsync()).Should().Be(0);
    }

    /// <summary>
    /// Plan 2.10, task 2 (review I1): the import applies the labels of a money <c>_base</c> from the file, and only those: a generated
    /// companion of another kind (a file column's <c>_name</c>, <c>AttributeOf</c>) keeps xrmverse's labels and the difference is a warning.
    /// </summary>
    [Fact]
    public async Task Import_applies_base_money_labels_but_not_other_generated_labels()
    {
        var file = SolutionFixtureZip.Build(c =>
        {
            var baseMoney = Attribute(c, "xv_Project", "xv_budget_Base");
            baseMoney.Element("displaynames")!.Elements("displayname").First(d => (string?)d.Attribute("languagecode") == "1033")
                .SetAttributeValue("description", "Budget in euro");
            var count = Attribute(c, "xv_Project", "xv_Count");
            var document = new XElement(count);
            document.SetAttributeValue("PhysicalName", "xv_Doc");
            document.Element("Type")!.Value = "file";
            document.Element("Name")!.Value = "xv_doc";
            document.Element("LogicalName")!.Value = "xv_doc";
            foreach (var element in new[] { "MinValue", "MaxValue", "Format" }) document.Element(element)?.Remove();
            var fileName = new XElement(Attribute(c, "xv_Project", "xv_Description"));
            fileName.SetAttributeValue("PhysicalName", "xv_Doc_Name");
            fileName.Element("Type")!.Value = "nvarchar";
            fileName.Element("Name")!.Value = "xv_doc_name";
            fileName.Element("LogicalName")!.Value = "xv_doc_name";
            foreach (var label in fileName.Element("displaynames")!.Elements("displayname")) label.SetAttributeValue("description", "File name (file)");
            count.AddAfterSelf(document, fileName);
        });
        // Twice: the generated companions are compared once the table exists (as for any re-import).
        await ImportAsync(Admin, file);
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, file, jobId);

        var project = Store.GetEntity("xv_project")!;
        project.Attributes.Single(a => a.LogicalName == "xv_budget_base").DisplayName.LocalizedLabels
            .Select(l => (l.LanguageCode, l.Label)).Should().Contain([(1033, "Budget in euro"), (1040, "Budget (base)")]);
        var companion = project.Attributes.Single(a => a.LogicalName == "xv_doc_name");
        companion.AttributeOf.Should().Be("xv_doc");
        (companion.DisplayName?.LocalizedLabels ?? []).Should().NotContain(l => l.Label == "File name (file)");
        var data = (await ImportJobAsync(jobId)).Data;
        data.Should().Contain("Attribute 'xv_project.xv_doc_name' is generated by xrmverse and differs from the file in DisplayName");
        data.Should().NotContain("Attribute 'xv_project.xv_budget_base' is generated by xrmverse and differs");
    }

    [Fact]
    public async Task Ribbon_diff_warning_says_what_the_ui_applies()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, SolutionFixtureZip.Build(c =>
            c.Root!.Element("Entities")!.Elements("Entity").Single(e => e.Element("Name")!.Value == "xv_Project").Element("RibbonDiffXml")!
                .Element("CustomActions")!.Add(new XElement("CustomAction", new XAttribute("Id", "xv.project.Hello.CustomAction"),
                    new XAttribute("Location", "Mscrm.Form.xv_project.MainTab.Actions.Controls._children"), new XAttribute("Sequence", "10")))), jobId);

        (await ImportJobAsync(jobId)).Data.Should().Contain(
            "The 'RibbonDiffXml' of entity 'xv_project' is applied to forms; grid commands, HideCustomAction and Url actions are kept for export but not applied.");
    }

    [Fact]
    public async Task Unsupported_entity_nodes_and_skipped_attributes_are_kept_and_logged()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, SolutionFixtureZip.Build(c =>
        {
            var project = c.Root!.Element("Entities")!.Elements("Entity").Single(e => e.Element("Name")!.Value == "xv_Project");
            // An entity node xrmverse does not apply (charts are applied since plan 3.5 UI-1f).
            project.Add(new XElement("CustomControlDefaultConfigs", new XElement("CustomControlDefaultConfig", new XElement("PrimaryEntityTypeCode", "xv_project"))));
            var image = new XElement(Attribute(c, "xv_Project", "VersionNumber"));
            image.SetAttributeValue("PhysicalName", "EntityImage");
            image.Element("Type")!.Value = "image";
            image.Element("Name")!.Value = "entityimage";
            image.Element("LogicalName")!.Value = "entityimage";
            Attribute(c, "xv_Project", "VersionNumber").AddAfterSelf(image);
            var parties = new XElement(Attribute(c, "xv_Project", "VersionNumber"));
            parties.SetAttributeValue("PhysicalName", "Parties");
            parties.Element("Type")!.Value = "partylist";
            parties.Element("Name")!.Value = "parties";
            parties.Element("LogicalName")!.Value = "parties";
            Attribute(c, "xv_Project", "VersionNumber").AddAfterSelf(parties);
        }), jobId);

        var kept = XElement.Parse((string)(await ScalarAsync("SELECT xrmverse_unsupportedxml FROM data.solution WHERE uniquename = @n", ("@n", Fixture)))!);
        kept.Elements("Entity").Single(e => (string?)e.Attribute("Name") == "xv_project").Element("CustomControlDefaultConfigs").Should().NotBeNull();
        var data = (await ImportJobAsync(jobId)).Data;
        data.Should().Contain("The 'CustomControlDefaultConfigs' of entity 'xv_project' is kept for export but not applied: xrmverse does not support it.");
        data.Should().Contain("System attribute 'xv_project.parties' of type 'partylist' is not created: xrmverse does not support the type.");
        // Image columns are supported (Plan 8): a system one xrmverse does not generate is logged as such.
        data.Should().Contain("System attribute 'xv_project.entityimage' is not created: xrmverse does not generate it.");
    }

    [Fact]
    public async Task Unknown_root_component_type_is_logged()
    {
        const string id = "{6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e98}";
        var zip = SolutionFixtureZip.Build(solution: s => s.Root!.Element("SolutionManifest")!.Element("RootComponents")!
            .Add(new XElement("RootComponent", new XAttribute("type", "10123"), new XAttribute("id", id), new XAttribute("behavior", "0"))));
        using (var stream = new MemoryStream())
        {
            stream.Write(zip);
            using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
            {
                using var entry = archive.CreateEntry($"copilotglossaryterms/{id.Trim('{', '}')}/copilotglossaryterm.xml").Open();
                entry.Write("<copilotglossaryterm />"u8);
            }
            zip = stream.ToArray();
        }

        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, zip, jobId);

        (await ImportJobAsync(jobId)).Data.Should().Contain($"Root component of type 10123 ({id}) is not applied: xrmverse does not support the component type.");
    }

    [Fact]
    public async Task Reused_import_job_id_is_400()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, SolutionFixtureZip.Build(), jobId);
        var fault = await FaultAsync(() => ImportAsync(Admin, SolutionFixtureZip.Build(), jobId));
        fault.HttpStatus.Should().Be(400);
        (await ImportJobAsync(jobId)).Progress.Should().Be(100, "the earlier job is left as it was");
    }


    // ---- fix round 1: system attributes, re-import differences, publisher prefixes -----------------------------------

    [Fact]
    public async Task System_status_options_and_labels_are_applied()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build(c =>
            Attribute(c, "xv_Project", "statuscode").Element("optionset")!.Element("statuses")!.Add(
                new XElement("status", new XAttribute("value", "100000002"), new XAttribute("state", "0"),
                    new XElement("labels",
                        new XElement("label", new XAttribute("description", "On hold"), new XAttribute("languagecode", "1033")),
                        new XElement("label", new XAttribute("description", "In attesa"), new XAttribute("languagecode", "1040")))))));

        var status = (StatusAttributeMetadata)Store.GetEntity("xv_project")!.Attributes.Single(a => a.LogicalName == "statuscode");
        status.DisplayName.LocalizedLabels.Should().Contain(l => l.LanguageCode == 1040 && l.Label == "Motivo stato");
        var options = status.OptionSet.Options.Cast<StatusOptionMetadata>().ToList();
        options.Select(o => o.Value).Should().BeEquivalentTo([1, 2, 100000002]);
        options.Single(o => o.Value == 1).Label.LocalizedLabels.Should().Contain(l => l.LanguageCode == 1040 && l.Label == "Attivo");
        options.Single(o => o.Value == 100000002).State.Should().Be(0);

        // The merged status reason is a valid value of the table.
        var id = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "On hold", ["statuscode"] = new OptionSetValue(100000002) });
        var row = await Admin.RetrieveAsync("xv_project", id, new ColumnSet("statuscode"));
        row.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(100000002);
    }

    [Fact]
    public async Task System_attribute_differences_are_warnings_naming_the_property()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, SolutionFixtureZip.Build(c =>
            Attribute(c, "xv_Project", "CreatedOn").Element("Format")!.Value = "date"), jobId);

        var createdOn = (DateTimeAttributeMetadata)Store.GetEntity("xv_project")!.Attributes.Single(a => a.LogicalName == "createdon");
        createdOn.Format.Should().Be(DateTimeFormat.DateAndTime, "the generated system attribute is kept");
        var data = (await ImportJobAsync(jobId)).Data;
        data.Should().Contain("System attribute 'xv_project.createdon' differs from the xrmverse one in Format");
    }

    [Fact]
    public async Task Reimport_with_a_custom_attribute_precision_change_is_400()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build());
        var stamp = Store.VersionStamp;

        var fault = await FaultAsync(() => ImportAsync(Admin, SolutionFixtureZip.Build(c =>
            Attribute(c, "xv_Project", "xv_Ratio").Element("Accuracy")!.Value = "2")));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xv_project.xv_ratio").And.Contain("Precision");
        ((DecimalAttributeMetadata)Store.GetEntity("xv_project")!.Attributes.Single(a => a.LogicalName == "xv_ratio")).Precision.Should().Be(4);
        Store.VersionStamp.Should().Be(stamp);
    }

    [Fact]
    public async Task Reimport_with_a_many_to_many_on_other_entities_is_400()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build());

        var fault = await FaultAsync(() => ImportAsync(Admin, SolutionFixtureZip.Build(c =>
            Relationship(c, "xv_project_xv_tag").Element("SecondEntityName")!.Value = "xv_task")));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xv_project_xv_tag").And.Contain("xv_task");
        ((ManyToManyRelationshipMetadata)Store.GetRelationship("xv_project_xv_tag")!).Entity2LogicalName.Should().Be("xv_tag");
    }

    [Fact]
    public async Task Reimport_with_a_cascade_change_is_400()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build());

        var fault = await FaultAsync(() => ImportAsync(Admin, SolutionFixtureZip.Build(c =>
            Relationship(c, "xv_project_xv_task_ProjectId").Element("CascadeDelete")!.Value = "Cascade")));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xv_project_xv_task_ProjectId").And.Contain("Cascade");
    }

    [Fact]
    public async Task Existing_publisher_with_other_prefixes_is_kept_with_a_warning()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build());
        await ExecAsync("UPDATE data.publisher SET customizationprefix = N'zz', customizationoptionvalueprefix = 12345 WHERE uniquename = N'xrmverse'");

        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, SolutionFixtureZip.Build(), jobId);

        (await ScalarAsync("SELECT customizationprefix FROM data.publisher WHERE uniquename = N'xrmverse'")).Should().Be("zz");
        var data = (await ImportJobAsync(jobId)).Data;
        data.Should().Contain("customization prefix 'zz'").And.Contain("option value prefix 12345");
    }


    // ---- fix round 2: relationship menus, entity properties ---------------------------------------------------------

    private async Task<string?> MenuXmlAsync(string schemaName) =>
        (string?)await ScalarAsync("SELECT associated_menu_xml FROM meta.relationship WHERE schema_name = @n", ("@n", schemaName));

    private static XElement MenuRole(XDocument customizations) =>
        Relationship(customizations, "xv_project_xv_task_ProjectId").Element("EntityRelationshipRoles")!.Elements("EntityRelationshipRole")
            .Single(r => r.Element("NavPaneOrder") is not null);

    [Fact]
    public async Task Relationship_menu_configuration_survives_the_import()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build());

        var stored = await MenuXmlAsync("xv_project_xv_task_ProjectId");
        stored.Should().NotBeNull();
        var role = XElement.Parse(stored!).Elements("EntityRelationshipRole").Single(r => r.Element("NavPaneOrder") is not null);
        role.Element("NavPaneDisplayOption")!.Value.Should().Be("UseCollectionName");
        role.Element("NavPaneArea")!.Value.Should().Be("Details");
        role.Element("NavPaneOrder")!.Value.Should().Be("10000");
        Store.GetRelationshipMenuXml("xv_project_xv_task_ProjectId").Should().Be(stored);
    }

    [Fact]
    public async Task Reimport_with_a_changed_menu_configuration_stores_it()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build());

        await ImportAsync(Admin, SolutionFixtureZip.Build(c =>
        {
            MenuRole(c).Element("NavPaneOrder")!.Value = "20000";
            MenuRole(c).Element("NavPaneDisplayOption")!.Value = "DoNotDisplay";
        }));

        var role = XElement.Parse((await MenuXmlAsync("xv_project_xv_task_ProjectId"))!).Elements("EntityRelationshipRole")
            .Single(r => r.Element("NavPaneOrder") is not null);
        role.Element("NavPaneOrder")!.Value.Should().Be("20000");
        role.Element("NavPaneDisplayOption")!.Value.Should().Be("DoNotDisplay");
        Store.GetRelationshipMenuXml("xv_project_xv_task_ProjectId").Should().Contain("20000");
    }

    [Fact]
    public async Task Reimport_with_an_ownership_change_is_400()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build());
        var stamp = Store.VersionStamp;

        var fault = await FaultAsync(() => ImportAsync(Admin, SolutionFixtureZip.Build(c =>
            EntityInfo(c, "xv_Project").Element("OwnershipTypeMask")!.Value = "OrgOwned")));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xv_project").And.Contain("OwnershipType");
        Store.GetEntity("xv_project")!.OwnershipType.Should().Be(OwnershipTypes.UserOwned);
        Store.VersionStamp.Should().Be(stamp);
    }

    [Fact]
    public async Task A_known_status_reason_with_another_state_is_a_warning()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, SolutionFixtureZip.Build(c =>
            Attribute(c, "xv_Project", "statuscode").Element("optionset")!.Element("statuses")!.Elements("status")
                .Single(e => (string?)e.Attribute("value") == "2").SetAttributeValue("state", "0")), jobId);

        var status = (StatusAttributeMetadata)Store.GetEntity("xv_project")!.Attributes.Single(a => a.LogicalName == "statuscode");
        status.OptionSet.Options.Cast<StatusOptionMetadata>().Single(o => o.Value == 2).State.Should().Be(1);
        (await ImportJobAsync(jobId)).Data.Should().Contain("status reason 2 has the state 0 in the file and 1 in xrmverse");
    }

    // ---- plan 2.11: activity tables and the HasActivities/HasNotes flags (ruling A19) ---------------------------------------

    [Fact]
    public async Task Import_custom_activity()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, CustomActivitySolution.Build(), jobId);

        var sms = Store.GetEntity(CustomActivitySolution.Table)!;
        sms.IsActivity.Should().BeTrue();
        sms.ActivityTypeMask.Should().Be(1);
        sms.HasNotes.Should().BeTrue();
        sms.HasActivities.Should().NotBe(true);
        sms.PrimaryIdAttribute.Should().Be("activityid");
        sms.PrimaryNameAttribute.Should().Be("subject");
        var columns = sms.Attributes.Select(a => a.LogicalName).ToList();
        columns.Should().Contain(ActivitySchema.CustomActivityAttributes().Select(a => a.LogicalName));
        columns.Should().Contain(["activityid", "subject", "xv_phonenumber", "ownerid"]);
        MetadataStore.IsCustomAttribute(sms, sms.Attributes.Single(a => a.LogicalName == "xv_phonenumber")).Should().BeTrue();
        MetadataStore.IsCustomAttribute(sms, sms.Attributes.Single(a => a.LogicalName == "to")).Should().BeFalse();
        sms.Attributes.Where(ActivitySchema.IsPartyList).Select(a => a.LogicalName).Should().HaveCount(9);
        ((StringAttributeMetadata)sms.Attributes.Single(a => a.LogicalName == "subject")).MaxLength.Should().Be(400);
        ((LookupAttributeMetadata)sms.Attributes.Single(a => a.LogicalName == "regardingobjectid")).Targets.Should().Contain(["account", "contact"]);
        Store.GetRelationship(ActivitySchema.RegardingRelationship(Store.GetEntity("account")!, sms).SchemaName).Should().NotBeNull();
        Store.GetRelationship(ActivitySchema.PartiesRelationship(sms).SchemaName).Should().NotBeNull();
        Store.GetRelationship(ActivitySchema.NotesRelationship(sms).SchemaName).Should().NotBeNull();
        ((LookupAttributeMetadata)Store.GetEntity("annotation")!.Attributes.Single(a => a.LogicalName == "objectid")).Targets.Should().Contain("xv_sms");
        ((LookupAttributeMetadata)Store.GetEntity("activityparty")!.Attributes.Single(a => a.LogicalName == "activityid")).Targets.Should().Contain("xv_sms");
        // The relationships of the activity model in the file are derived, not imported (whatever IsCustomRelationship says).
        // Two of them have the derived names (xrmverse's own, not custom); the third one has a name of its own and is not created.
        Store.GetRelationship("xv_sms_regarding_account").Should().BeNull();
        Store.GetRelationship("xv_sms_activity_parties")!.IsCustomRelationship.Should().NotBe(true);
        Store.GetRelationship("xv_sms_Annotations")!.IsCustomRelationship.Should().NotBe(true);

        var log = (await ImportJobAsync(jobId)).Data;
        foreach (var column in ActivitySchema.CustomActivityAttributes().Select(a => a.LogicalName).Append("activityid").Append("subject"))
        {
            log.Should().NotContain($"System attribute 'xv_sms.{column}' is not created", "the import derives the columns of an activity");
            log.Should().NotContain($"System attribute 'xv_sms.{column}' of type", "the import derives the columns of an activity");
        }
        // Ruling Z5: each model relationship of the file that is marked custom or named otherwise is a warning naming both names.
        var regarding = ActivitySchema.RegardingRelationship(Store.GetEntity("account")!, sms).SchemaName;
        log.Should().Contain($"Relationship 'xv_sms_regarding_account' (custom in the file) is of the activity model: xrmverse derives it as '{regarding}' and does not import the file's.");
        log.Should().Contain($"Relationship 'xv_sms_activity_parties' (custom in the file) is of the activity model: xrmverse derives it as '{ActivitySchema.PartiesRelationship(sms).SchemaName}' and does not import the file's.");
        log.Should().Contain($"Relationship 'xv_sms_Annotations' (custom in the file) is of the activity model: xrmverse derives it as '{ActivitySchema.NotesRelationship(sms).SchemaName}' and does not import the file's.");
        // A system column the platform has and xrmverse does not generate is still reported.
        log.Should().Contain("System attribute 'xv_sms.timezoneruleversionnumber' is not created");

        var accountId = await Admin.CreateAsync(new Entity("account") { ["name"] = "SMS target" });
        try
        {
            var smsId = await Admin.CreateAsync(new Entity(CustomActivitySolution.Table)
            {
                ["subject"] = "Hello", ["xv_phonenumber"] = "+39 02 1234", ["regardingobjectid"] = new EntityReference("account", accountId),
            });
            var pointer = await Admin.RetrieveAsync("activitypointer", smsId, new ColumnSet("activitytypecode", "regardingobjectid", "subject"));
            pointer.GetAttributeValue<string>("activitytypecode").Should().Be(CustomActivitySolution.Table);
            pointer.GetAttributeValue<EntityReference>("regardingobjectid").Id.Should().Be(accountId);
            pointer.GetAttributeValue<string>("subject").Should().Be("Hello");
            await Admin.DeleteAsync(CustomActivitySolution.Table, smsId);
        }
        finally
        {
            await Admin.DeleteAsync("account", accountId);
        }
    }

    [Fact]
    public async Task Import_of_model_relationship_with_the_derived_name_and_not_custom_is_not_a_warning()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, CustomActivitySolution.Build(relationships: r => r.Elements("EntityRelationship")
            .Single(e => (string)e.Attribute("Name")! == "xv_sms_activity_parties").Element("IsCustomRelationship")!.Remove()), jobId);

        var log = (await ImportJobAsync(jobId)).Data;
        log.Should().NotContain("Relationship 'xv_sms_activity_parties'");
        log.Should().Contain("Relationship 'xv_sms_Annotations' (custom in the file)");
    }

    [Fact]
    public async Task Import_of_relabelled_regarding_column_is_a_warning_and_not_applied()
    {
        await ImportAsync(Admin, CustomActivitySolution.Build());
        var before = Store.GetEntity(CustomActivitySolution.Table)!.Attributes.Single(a => a.LogicalName == "regardingobjectid")
            .DisplayName.LocalizedLabels.Single(l => l.LanguageCode == 1033).Label;

        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, CustomActivitySolution.Build(info => Relabel(info, "regardingobjectid", "Related to")), jobId);

        Store.GetEntity(CustomActivitySolution.Table)!.Attributes.Single(a => a.LogicalName == "regardingobjectid")
            .DisplayName.LocalizedLabels.Single(l => l.LanguageCode == 1033).Label.Should().Be(before);
        var log = (await ImportJobAsync(jobId)).Data;
        log.Should().Contain("System attribute 'xv_sms.regardingobjectid' differs from the xrmverse one in DisplayName")
            .And.Contain("Related to")
            .And.Contain("not applied (a column of the activity model is the server's)");
    }

    [Fact]
    public async Task Import_of_relabelled_party_list_and_EntityName_columns_is_a_warning()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, CustomActivitySolution.Build(info =>
        {
            Relabel(info, "to", "Recipients");
            Relabel(info, "activitytypecode", "Kind of activity");
        }), jobId);

        var sms = Store.GetEntity(CustomActivitySolution.Table)!;
        sms.Attributes.Single(a => a.LogicalName == "to").DisplayName.LocalizedLabels.Should().NotContain(l => l.Label == "Recipients");
        var log = (await ImportJobAsync(jobId)).Data;
        log.Should().Contain("System attribute 'xv_sms.to' differs from the xrmverse one in DisplayName").And.Contain("Recipients");
        log.Should().Contain("System attribute 'xv_sms.activitytypecode' differs from the xrmverse one in DisplayName").And.Contain("Kind of activity");
    }

    [Fact]
    public async Task Reimport_of_custom_activity_keeps_it()
    {
        await ImportAsync(Admin, CustomActivitySolution.Build());
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, CustomActivitySolution.Build(), jobId);

        var sms = Store.GetEntity(CustomActivitySolution.Table)!;
        sms.IsActivity.Should().BeTrue();
        sms.PrimaryNameAttribute.Should().Be("subject");
        sms.HasNotes.Should().BeTrue();
        sms.Attributes.Should().Contain(a => a.LogicalName == "xv_phonenumber");
        (await ImportJobAsync(jobId)).Data.Should().NotContain("cannot be switched off");
    }

    [Fact]
    public async Task Import_of_new_table_with_both_flags_on_creates_it_with_them()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build(c =>
        {
            EntityInfo(c, "xv_Tag").Element("HasRelatedNotes")!.Value = "True";
            EntityInfo(c, "xv_Tag").Element("HasRelatedActivities")!.Value = "True";
        }));

        var tag = Store.GetEntity("xv_tag")!;
        tag.HasNotes.Should().BeTrue();
        tag.HasActivities.Should().BeTrue();
        ((LookupAttributeMetadata)Store.GetEntity("annotation")!.Attributes.Single(a => a.LogicalName == "objectid")).Targets.Should().Contain("xv_tag");
        ((LookupAttributeMetadata)Store.GetEntity("activitypointer")!.Attributes.Single(a => a.LogicalName == "regardingobjectid")).Targets
            .Should().Contain("xv_tag");
    }

    [Fact]
    public async Task Import_of_organization_owned_activity_is_400()
    {
        var stamp = Store.VersionStamp;

        var fault = await FaultAsync(() => ImportAsync(Admin, CustomActivitySolution.Build(info =>
            info.Element("OwnershipTypeMask")!.Value = "OrgOwned")));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xv_sms");
        Store.GetEntity(CustomActivitySolution.Table).Should().BeNull();
        Store.VersionStamp.Should().Be(stamp);
    }

    private static void Relabel(XElement info, string column, string label)
    {
        var attribute = info.Element("attributes")!.Elements("attribute").Single(a => a.Element("LogicalName")!.Value == column);
        foreach (var name in attribute.Element("displaynames")!.Elements()) name.SetAttributeValue("description", label);
    }

    [Fact]
    public async Task Import_sets_HasActivities_and_HasNotes_one_way()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build());
        Store.GetEntity("xv_project")!.HasNotes.Should().NotBe(true);

        await ImportAsync(Admin, SolutionFixtureZip.Build(c =>
        {
            EntityInfo(c, "xv_Project").Element("HasRelatedNotes")!.Value = "True";
            EntityInfo(c, "xv_Project").Element("HasRelatedActivities")!.Value = "1";
        }));

        var project = Store.GetEntity("xv_project")!;
        project.HasNotes.Should().BeTrue();
        project.HasActivities.Should().BeTrue();
        ((LookupAttributeMetadata)Store.GetEntity("annotation")!.Attributes.Single(a => a.LogicalName == "objectid")).Targets.Should().Contain("xv_project");
        ((LookupAttributeMetadata)Store.GetEntity("activitypointer")!.Attributes.Single(a => a.LogicalName == "regardingobjectid")).Targets
            .Should().Contain("xv_project");

        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, SolutionFixtureZip.Build(), jobId);

        project = Store.GetEntity("xv_project")!;
        project.HasNotes.Should().BeTrue("a table keeps its notes");
        project.HasActivities.Should().BeTrue("a table keeps its activities");
        var log = (await ImportJobAsync(jobId)).Data;
        log.Should().Contain("HasRelatedNotes of 'xv_project' is on in xrmverse and cannot be switched off: the file's 0 is not applied.");
        log.Should().Contain("HasRelatedActivities of 'xv_project' is on in xrmverse and cannot be switched off: the file's 0 is not applied.");
    }

    [Fact]
    public async Task Import_of_custom_activity_with_HasRelatedNotes_0_keeps_notes_with_a_warning()
    {
        var jobId = Guid.NewGuid();
        await ImportAsync(Admin, CustomActivitySolution.Build(info => info.Element("HasRelatedNotes")!.Value = "False"), jobId);

        Store.GetEntity(CustomActivitySolution.Table)!.HasNotes.Should().BeTrue();
        (await ImportJobAsync(jobId)).Data.Should()
            .Contain("HasRelatedNotes of custom activity 'xv_sms' is 0 in the file, but an activity always has notes: it is created with notes.");
    }

    [Fact]
    public async Task Import_of_team_owned_activity_is_400()
    {
        var stamp = Store.VersionStamp;

        var fault = await FaultAsync(() => ImportAsync(Admin, CustomActivitySolution.Build(info =>
            info.Element("OwnershipTypeMask")!.Value = "TeamOwned")));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xv_sms").And.Contain("TeamOwned");
        Store.GetEntity(CustomActivitySolution.Table).Should().BeNull();
        Store.VersionStamp.Should().Be(stamp);
    }

    [Fact]
    public async Task Reimport_that_makes_a_table_an_activity_is_400()
    {
        await ImportAsync(Admin, SolutionFixtureZip.Build());

        var fault = await FaultAsync(() => ImportAsync(Admin, SolutionFixtureZip.Build(c =>
            EntityInfo(c, "xv_Project").Element("IsActivity")!.Value = "1")));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xv_project").And.Contain("IsActivity");
        Store.GetEntity("xv_project")!.IsActivity.Should().NotBe(true);
    }
}
