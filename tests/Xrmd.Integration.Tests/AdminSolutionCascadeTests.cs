using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// <c>DELETE /admin/solutions/{uniquename}?cascade=true</c>: the solution's components that no other solution holds are
/// deleted with it, in one transaction, through the Core's delete operations and their checks.
/// </summary>
[Collection("solution-import")]
public sealed class AdminSolutionCascadeTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Fixture = SolutionFixtureZip.FixtureName;
    private const string Outside = "pb_cascadeoutside";
    private const string OtherSolution = "xv_cascadeother";
    private const string PipeColumn = "pb_cascadecolumn";
    private const string AdminRequiredBody =
        """{"error":{"code":"0x80040220","message":"The Admin API requires the System Administrator role."}}""";
    private static readonly Guid RoleId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e01");
    private static readonly Guid AssemblyId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e10");
    private static readonly Guid CreateStepId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e21");
    private static readonly Guid UpdateStepId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e22");
    private static readonly Guid SetAttributeTypeId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e11");
    private static readonly Guid RecorderTypeId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e12");
    private const string CreateStepName = "Xrmd.TestPlugins.SetAttributePlugin: Create of xv_project";
    private const string UpdateStepName = "Xrmd.TestPlugins.ContextRecorderPlugin: Update of xv_project";
    private static readonly Guid ImageId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e23");
    private static readonly Guid WebResourceId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e41");

    private Guid _nonAdminOid;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await DropOutsideAsync();
        await SolutionImportTests.ClearImportAsync(fx);
        _nonAdminOid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), _nonAdminOid);
        await AssignRoleAsync(fx, user, await CreateRoleAsync(fx, "cascade-" + Guid.NewGuid().ToString("N")[..8],
            ("prvReadSolution", DepthMask.Global)));
    }

    public async Task DisposeAsync()
    {
        await DropOutsideAsync();
        await SolutionImportTests.ClearImportAsync(fx);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private Task ImportAsync() =>
        Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = Guid.NewGuid() });

    private async Task DropOutsideAsync()
    {
        if (Store.GetEntity(Outside) is not null)
            await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteEntityAsync(s, Outside, default));
        if (Store.GetEntity(TestEntities.Pipe)?.Attributes.Any(a => a.LogicalName == PipeColumn) == true)
            await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteAttributeAsync(s, TestEntities.Pipe, PipeColumn, default));
    }

    private Task<HttpResponseMessage> DeleteAsync(string path, Guid? oid = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid ?? fx.AdminObjectId, fx.BaseUrl));
        return fx.Client.SendAsync(req);
    }

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    /// <summary>The <c>error.message</c> of an error answer with status <paramref name="expected"/>.</summary>
    private static async Task<string> ErrorAsync(HttpResponseMessage res, HttpStatusCode expected) =>
        (await JsonAsync(res, expected))["error"]!["message"]!.GetValue<string>();

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

    private async Task<int> TableCountAsync(string table) => await TestSql.TableExistsAsync(fx.ConnectionString, "data", table) ? 1 : 0;

    private async Task<Guid?> FixtureSolutionIdAsync() =>
        (Guid?)await ScalarAsync("SELECT solutionid FROM data.solution WHERE uniquename = @n", ("@n", Fixture));

    private static List<(int Type, string Name, string Reason)> KeptEntries(JsonObject body) =>
        [.. body["kept"]!.AsArray().Select(k => (k!["type"]!.GetValue<int>(), k["name"]!.GetValue<string>(), k["reason"]!.GetValue<string>()))];

    /// <summary>Creates the second solution <see cref="OtherSolution"/> (same publisher as the fixture's).</summary>
    private async Task CreateOtherSolutionAsync()
    {
        var publisherId = (Guid)(await ScalarAsync("SELECT publisherid FROM data.solution WHERE uniquename = @n", ("@n", Fixture)))!;
        await Admin.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = OtherSolution, ["friendlyname"] = "Other", ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", publisherId),
        });
    }

    private Task AddToOtherSolutionAsync(int type, Guid id) =>
        Admin.ExecuteAsync(new AddSolutionComponentRequest { ComponentType = type, ComponentId = id, SolutionUniqueName = OtherSolution });

    private Task<int> HeldByOtherSolutionAsync(Guid id) => CountAsync("""
        SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
        WHERE s.uniquename = @n AND sc.objectid = @o
        """, ("@n", OtherSolution), ("@o", id));

    private Task<int> RowCountAsync(string table, Guid id) =>
        CountAsync($"SELECT COUNT(*) FROM data.{table} WHERE {table}id = @id", ("@id", id));

    private static List<(int Type, string Name)> Entries(JsonObject body, string list) =>
        [.. body[list]!.AsArray().Select(e => (e!["type"]!.GetValue<int>(), e["name"]!.GetValue<string>()))];

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cascade_deletes_the_solution_tables_columns_steps_and_data()
    {
        await ImportAsync();
        var solutionId = (await FixtureSolutionIdAsync())!.Value;
        var publisherId = (Guid)(await ScalarAsync("SELECT publisherid FROM data.solution WHERE solutionid = @s", ("@s", solutionId)))!;
        await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "Data" });
        (await TableCountAsync("xv_project")).Should().Be(1);
        var versionBefore = Store.VersionStamp;
        var componentCount = await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @s", ("@s", solutionId));

        var body = await JsonAsync(await DeleteAsync($"/admin/solutions/{Fixture}?cascade=true"), HttpStatusCode.OK);

        // The tables, their data, the option set and the relationship are gone (published after the commit).
        Store.GetEntity("xv_project").Should().BeNull();
        Store.GetEntity("xv_task").Should().BeNull();
        Store.GetEntity("xv_tag").Should().BeNull();
        Store.GetOptionSet("xv_priority").Should().BeNull();
        Store.GetRelationship("xv_project_xv_tag").Should().BeNull();
        Store.VersionStamp.Should().BeGreaterThan(versionBefore);
        (await TableCountAsync("xv_project")).Should().Be(0);
        (await TableCountAsync("xv_project_xv_tag")).Should().Be(0);
        var metadata = await fx.Client.SendAsync(Authorized(HttpMethod.Get, "/api/data/v9.2/EntityDefinitions(LogicalName='xv_project')"));
        metadata.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // The rows.
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", CreateStepId))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstepimage WHERE sdkmessageprocessingstepimageid = @id", ("@id", ImageId))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.plugintype WHERE pluginassemblyid = @id", ("@id", AssemblyId))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.pluginassembly WHERE pluginassemblyid = @id", ("@id", AssemblyId))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.webresource WHERE webresourceid = @id", ("@id", WebResourceId))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.role WHERE roleid = @id", ("@id", RoleId))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = @id", ("@id", RoleId))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.savedquery")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.systemform")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.sitemap")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.appmodule")).Should().Be(0);
        // The solution and its components; the publisher stays.
        (await FixtureSolutionIdAsync()).Should().BeNull();
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @s", ("@s", solutionId))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.publisher WHERE publisherid = @p", ("@p", publisherId))).Should().Be(1);
        // Default no longer tracks what was deleted.
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE objectid IN (@a, @b, @c)",
            ("@a", CreateStepId), ("@b", RoleId), ("@c", WebResourceId))).Should().Be(0);

        var deleted = Entries(body, "deleted");
        deleted.Should().Contain((ComponentType.Entity, "xv_project"));
        deleted.Should().Contain((ComponentType.Entity, "xv_tag"));
        deleted.Should().Contain((ComponentType.OptionSet, "xv_priority"));
        deleted.Should().Contain((ComponentType.EntityRelationship, "xv_project_xv_tag"));
        deleted.Should().Contain(d => d.Type == ComponentType.SdkMessageProcessingStep);
        deleted.Should().Contain(d => d.Type == ComponentType.PluginAssembly);
        deleted.Should().Contain(d => d.Type == ComponentType.Role && d.Name == "Xrmverse Fixture User");
        deleted.Should().Contain(d => d.Type == ComponentType.WebResource && d.Name == "xv_/scripts/project.js");
        body["kept"]!.AsArray().Should().BeEmpty();
        deleted.Should().HaveCount(componentCount, "each component of the solution is listed once").And.OnlyHaveUniqueItems();
        deleted.Should().Contain((ComponentType.Attribute, "xv_project.xv_name"), "the columns of a deleted table are deleted with it");
    }

    /// <summary>Plan 3.5 UI-1f: a chart goes before the web resource it names (webresource_savedqueryvisualizations is Restrict).</summary>
    [Fact]
    public async Task Cascade_deletes_a_chart_and_the_web_resource_it_names()
    {
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(SolutionFixtureZip.AddChartsWithWebResource), ImportJobId = Guid.NewGuid(),
        });

        var body = await JsonAsync(await DeleteAsync($"/admin/solutions/{Fixture}?cascade=true"), HttpStatusCode.OK);

        body.Should().NotBeNull();
        (await CountAsync("SELECT COUNT(*) FROM data.savedqueryvisualization")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.webresource WHERE webresourceid = @w",
            ("@w", Guid.Parse(SolutionFixtureZip.WebResourceIdAsWritten)))).Should().Be(0);
    }

    /// <summary>Plan 3.5 UI-1f (ruling F1): the dashboards (forms of no table) go, with the app that lists one.</summary>
    [Fact]
    public async Task Cascade_deletes_the_dashboards()
    {
        await Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = SolutionFixtureZip.Build(c =>
            {
                SolutionFixtureZip.AddCharts(c);
                SolutionFixtureZip.AddDashboards(c);
                c.Descendants("AppModuleComponents").Single().Add(new XElement("AppModuleComponent", new XAttribute("type", "60"),
                    new XAttribute("id", SolutionFixtureZip.ChartsDashboardId.ToString("B"))));
            }, SolutionFixtureZip.AddDashboardRoots),
            ImportJobId = Guid.NewGuid(),
        });
        (await CountAsync("SELECT COUNT(*) FROM data.systemform WHERE type = 0")).Should().Be(2);

        var body = await JsonAsync(await DeleteAsync($"/admin/solutions/{Fixture}?cascade=true"), HttpStatusCode.OK);

        body.Should().NotBeNull();
        (await CountAsync("SELECT COUNT(*) FROM data.systemform WHERE type = 0")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.appmodulecomponent WHERE objectid = @d", ("@d", SolutionFixtureZip.ChartsDashboardId))).Should().Be(0);
    }

    [Fact]
    public async Task Cascade_deletes_its_columns_of_a_table_that_remains()
    {
        await ImportAsync();
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = TestEntities.Pipe, SolutionUniqueName = Fixture,
            Attribute = new IntegerAttributeMetadata { LogicalName = PipeColumn, SchemaName = PipeColumn },
        });

        var body = await JsonAsync(await DeleteAsync($"/admin/solutions/{Fixture}?cascade=true"), HttpStatusCode.OK);

        Entries(body, "deleted").Should().Contain((ComponentType.Attribute, $"{TestEntities.Pipe}.{PipeColumn}"));
        Store.GetEntity(TestEntities.Pipe).Should().NotBeNull();
        Store.GetEntity(TestEntities.Pipe)!.Attributes.Should().NotContain(a => a.LogicalName == PipeColumn);
        (await TestSql.ColumnExistsAsync(fx.ConnectionString, "data", TestEntities.Pipe, PipeColumn)).Should().BeFalse();
    }

    [Fact]
    public async Task Cascade_keeps_components_shared_with_another_solution()
    {
        await ImportAsync();
        var publisherId = (Guid)(await ScalarAsync("SELECT publisherid FROM data.solution WHERE uniquename = @n", ("@n", Fixture)))!;
        await Admin.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = OtherSolution, ["friendlyname"] = "Other", ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", publisherId),
        });
        await Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentType = ComponentType.Entity, ComponentId = Store.GetEntity("xv_tag")!.MetadataId!.Value, SolutionUniqueName = OtherSolution,
        });
        var tagId = await Admin.CreateAsync(new Entity("xv_tag") { ["xv_name"] = "Kept" });

        var body = await JsonAsync(await DeleteAsync($"/admin/solutions/{Fixture}?cascade=true"), HttpStatusCode.OK);

        Store.GetEntity("xv_tag").Should().NotBeNull();
        (await TableCountAsync("xv_tag")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.xv_tag WHERE xv_tagid = @id", ("@id", tagId))).Should().Be(1);
        Store.GetEntity("xv_project").Should().BeNull();
        var kept = body["kept"]!.AsArray().Select(k => (k!["type"]!.GetValue<int>(), k["name"]!.GetValue<string>(), k["reason"]!.GetValue<string>()));
        kept.Should().Contain((ComponentType.Entity, "xv_tag", $"in solution {OtherSolution}"));
        Entries(body, "deleted").Should().NotContain((ComponentType.Entity, "xv_tag"));
        Entries(body, "deleted").Should().Contain((ComponentType.Entity, "xv_project"));
        // The other solution still holds it.
        (await CountAsync("""
            SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
            WHERE s.uniquename = @n AND sc.objectid = @o
            """, ("@n", OtherSolution), ("@o", Store.GetEntity("xv_tag")!.MetadataId!.Value))).Should().Be(1);
    }

    /// <summary>
    /// Task 3 fix round 1: a component another solution adds while the cascade runs is kept. The add (open transaction) keeps
    /// the cascade waiting until it commits: the key-range lock of the cascade's read on SQL Server, its table write lock on
    /// PostgreSQL. The cascade then reads the new holder and keeps the web resource.
    /// </summary>
    [Fact]
    public async Task Cascade_waits_for_a_component_another_solution_is_adding_and_keeps_it()
    {
        await ImportAsync();
        await CreateOtherSolutionAsync();
        var otherId = (Guid)(await ScalarAsync("SELECT solutionid FROM data.solution WHERE uniquename = @n", ("@n", OtherSolution)))!;
        var tracker = fx.App.Services.GetRequiredService<SolutionComponentTracker>();

        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        await tracker.AddAsync(new DataSession(conn, tx), otherId, ComponentType.WebResource, WebResourceId, null, default);

        var cascade = DeleteAsync($"/admin/solutions/{Fixture}?cascade=true");
        var first = await Task.WhenAny(cascade, Task.Delay(TimeSpan.FromSeconds(3)));
        first.Should().NotBe(cascade, "the cascade waits for the add of another solution to end");
        await tx.CommitAsync();

        var body = await JsonAsync(await cascade.WaitAsync(TimeSpan.FromSeconds(60)), HttpStatusCode.OK);
        KeptEntries(body).Should().Contain(k => k.Type == ComponentType.WebResource && k.Reason == $"in solution {OtherSolution}");
        (await RowCountAsync("webresource", WebResourceId)).Should().Be(1);
        (await HeldByOtherSolutionAsync(WebResourceId)).Should().Be(1);
    }

    [Fact]
    public async Task Cascade_keeps_the_parents_of_a_column_and_an_image_held_by_another_solution()
    {
        await ImportAsync();
        await CreateOtherSolutionAsync();
        var columnId = Store.GetEntity("xv_project")!.Attributes.Single(a => a.LogicalName == "xv_description").MetadataId!.Value;
        await AddToOtherSolutionAsync(ComponentType.Attribute, columnId);
        await AddToOtherSolutionAsync(ComponentType.SdkMessageProcessingStepImage, ImageId);
        var recordId = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "Kept" });

        var body = await JsonAsync(await DeleteAsync($"/admin/solutions/{Fixture}?cascade=true"), HttpStatusCode.OK);

        // The held components and everything whose delete would destroy them survive.
        Store.GetEntity("xv_project")!.Attributes.Should().Contain(a => a.LogicalName == "xv_description");
        (await CountAsync("SELECT COUNT(*) FROM data.xv_project WHERE xv_projectid = @id", ("@id", recordId))).Should().Be(1);
        (await RowCountAsync("sdkmessageprocessingstepimage", ImageId)).Should().Be(1);
        (await RowCountAsync("sdkmessageprocessingstep", UpdateStepId)).Should().Be(1);
        (await RowCountAsync("plugintype", RecorderTypeId)).Should().Be(1);
        (await RowCountAsync("pluginassembly", AssemblyId)).Should().Be(1);
        var kept = KeptEntries(body);
        var viaColumn = $"has component xv_project.xv_description in solution {OtherSolution}";
        var viaImage = $"has component PreImage in solution {OtherSolution}";
        kept.Should().Contain((ComponentType.Attribute, "xv_project.xv_description", $"in solution {OtherSolution}"));
        kept.Should().Contain((ComponentType.Entity, "xv_project", viaColumn));
        kept.Should().Contain((ComponentType.SdkMessageProcessingStepImage, "PreImage", $"in solution {OtherSolution}"));
        kept.Should().Contain((ComponentType.SdkMessageProcessingStep, UpdateStepName, viaImage));
        kept.Should().Contain(k => k.Type == ComponentType.PluginType && k.Reason == viaImage);
        kept.Should().Contain(k => k.Type == ComponentType.PluginAssembly && k.Reason == viaImage);
        // The other solution still tracks them; the fixture solution is gone.
        (await HeldByOtherSolutionAsync(columnId)).Should().Be(1);
        (await HeldByOtherSolutionAsync(ImageId)).Should().Be(1);
        (await FixtureSolutionIdAsync()).Should().BeNull();
        // What nothing else holds went: another column of the kept table, the other tables, the other step.
        Store.GetEntity("xv_project")!.Attributes.Should().NotContain(a => a.LogicalName == "xv_count");
        Store.GetEntity("xv_task").Should().BeNull();
        (await RowCountAsync("sdkmessageprocessingstep", CreateStepId)).Should().Be(0);
        var listed = Entries(body, "deleted").Concat(kept.Select(k => (k.Type, k.Name))).ToList();
        listed.Should().OnlyHaveUniqueItems("each component is listed once");
    }

    [Fact]
    public async Task Cascade_keeps_the_plugin_type_of_a_step_held_by_another_solution()
    {
        await ImportAsync();
        await CreateOtherSolutionAsync();
        await AddToOtherSolutionAsync(ComponentType.SdkMessageProcessingStep, CreateStepId);

        var body = await JsonAsync(await DeleteAsync($"/admin/solutions/{Fixture}?cascade=true"), HttpStatusCode.OK);

        (await RowCountAsync("sdkmessageprocessingstep", CreateStepId)).Should().Be(1);
        (await RowCountAsync("plugintype", SetAttributeTypeId)).Should().Be(1);
        (await RowCountAsync("pluginassembly", AssemblyId)).Should().Be(1);
        Store.GetEntity("xv_project").Should().NotBeNull("the step is registered on it");
        var viaStep = $"has component {CreateStepName} in solution {OtherSolution}";
        var kept = KeptEntries(body);
        kept.Should().Contain((ComponentType.SdkMessageProcessingStep, CreateStepName, $"in solution {OtherSolution}"));
        kept.Should().Contain(k => k.Type == ComponentType.PluginType && k.Reason == viaStep);
        kept.Should().Contain(k => k.Type == ComponentType.PluginAssembly && k.Reason == viaStep);
        kept.Should().Contain((ComponentType.Entity, "xv_project", viaStep));
        (await HeldByOtherSolutionAsync(CreateStepId)).Should().Be(1);
        // The other plug-in type (its step is not held) and its step went.
        (await RowCountAsync("plugintype", RecorderTypeId)).Should().Be(0);
        (await RowCountAsync("sdkmessageprocessingstep", UpdateStepId)).Should().Be(0);
    }

    [Fact]
    public async Task Cascade_keeps_a_component_another_solution_takes_after_the_plan()
    {
        await ImportAsync();
        await CreateOtherSolutionAsync();
        var tagId = Store.GetEntity("xv_tag")!.MetadataId!.Value;
        using var scope = fx.App.Services.CreateScope();
        var deleter = scope.ServiceProvider.GetRequiredService<Xrmd.Admin.SolutionCascadeDeleter>();
        var ran = false;
        // Planned as deleted (nothing else holds xv_tag yet); another solution takes it before the transaction.
        deleter.BeforeApply = async _ =>
        {
            ran = true;
            await AddToOtherSolutionAsync(ComponentType.Entity, tagId);
        };

        var result = await deleter.DeleteAsync(await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId), Fixture, default);

        ran.Should().BeTrue();
        Store.GetEntity("xv_tag").Should().NotBeNull();
        (await TableCountAsync("xv_tag")).Should().Be(1);
        result.Kept.Should().Contain(new Xrmd.Core.Solutions.CascadeKept(ComponentType.Entity, "xv_tag", $"in solution {OtherSolution}"));
        result.Deleted.Should().NotContain(new Xrmd.Core.Solutions.CascadeDeleted(ComponentType.Entity, "xv_tag"));
        (await HeldByOtherSolutionAsync(tagId)).Should().Be(1);
        Store.GetEntity("xv_project").Should().BeNull();
    }

    [Fact]
    public async Task Cascade_refused_by_an_external_reference_changes_nothing()
    {
        await ImportAsync();
        var solutionId = (await FixtureSolutionIdAsync())!.Value;
        var outside = new EntityMetadata { LogicalName = Outside, SchemaName = Outside, OwnershipType = OwnershipTypes.UserOwned };
        outside.SetAttributes([new LookupAttributeMetadata { LogicalName = "pb_projectid", SchemaName = "pb_ProjectId", Targets = ["xv_project"] }]);
        await Store.CreateEntityAsync(outside, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 100 });
        var recordId = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "Stays" });
        var components = await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent");
        var version = Store.VersionStamp;

        var res = await DeleteAsync($"/admin/solutions/{Fixture}?cascade=true");

        (await ErrorAsync(res, HttpStatusCode.BadRequest))
            .Should().Be($"The component cannot be deleted because it is referenced by attribute '{Outside}.pb_projectid'.");
        (await FixtureSolutionIdAsync()).Should().Be(solutionId);
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent")).Should().Be(components);
        Store.GetEntity("xv_project").Should().NotBeNull();
        Store.GetEntity("xv_tag").Should().NotBeNull();
        Store.GetOptionSet("xv_priority").Should().NotBeNull();
        Store.GetRelationship("xv_project_xv_tag").Should().NotBeNull();
        Store.VersionStamp.Should().Be(version);
        (await CountAsync("SELECT COUNT(*) FROM data.xv_project WHERE xv_projectid = @id", ("@id", recordId))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", CreateStepId))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.role WHERE roleid = @id", ("@id", RoleId))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.webresource WHERE webresourceid = @id", ("@id", WebResourceId))).Should().Be(1);
        // The step cache was not invalidated into a state without the step: the fixture's Create step still fires.
        var id = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "After" });
        (await Admin.RetrieveAsync("xv_project", id, new Microsoft.Xrm.Sdk.Query.ColumnSet("xv_description")))
            .GetAttributeValue<string>("xv_description").Should().Be("set by the fixture pre-operation create step");
    }

    [Fact]
    public async Task Cascade_on_Default_or_Active_is_403()
    {
        foreach (var name in new[] { "Default", "Active" })
        {
            var res = await DeleteAsync($"/admin/solutions/{name}?cascade=true");
            (await ErrorAsync(res, HttpStatusCode.Forbidden)).Should().Be(SolutionDeleter.SystemSolutionFixed);
            (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", name))).Should().Be(1);
        }
    }

    [Fact]
    public async Task Cascade_on_unknown_solution_is_404()
    {
        var res = await DeleteAsync("/admin/solutions/xv_nosuchsolution?cascade=true");
        (await ErrorAsync(res, HttpStatusCode.NotFound)).Should().Be("The solution with unique name 'xv_nosuchsolution' does not exist.");
    }

    [Fact]
    public async Task Cascade_on_managed_is_400()
    {
        await ImportAsync();
        var solutionId = (await FixtureSolutionIdAsync())!.Value;
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command("UPDATE data.solution SET ismanaged = 1 WHERE solutionid = @s", conn);
            cmd.Parameters.AddWithValue("@s", solutionId);
            await cmd.ExecuteNonQueryAsync();
        }

        var res = await DeleteAsync($"/admin/solutions/{Fixture}?cascade=true");

        (await ErrorAsync(res, HttpStatusCode.BadRequest)).Should().Be(SolutionDeleter.ManagedNotSupported);
        (await FixtureSolutionIdAsync()).Should().Be(solutionId);
        Store.GetEntity("xv_project").Should().NotBeNull();
    }

    [Fact]
    public async Task Cascade_absent_or_false_is_400()
    {
        await ImportAsync();
        foreach (var query in new[] { "", "?cascade=false" })
        {
            var res = await DeleteAsync($"/admin/solutions/{Fixture}{query}");
            (await ErrorAsync(res, HttpStatusCode.BadRequest)).Should().Be(
                "Use the Web API or SDK to delete only the solution container; the Admin API deletes with cascade=true only.");
        }
        (await ErrorAsync(await DeleteAsync($"/admin/solutions/{Fixture}?cascade=yes"), HttpStatusCode.BadRequest))
            .Should().Be("The query option 'cascade' must be true or false; got 'yes'.");
        (await FixtureSolutionIdAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task Cascade_for_a_non_admin_is_403_before_input_validation()
    {
        foreach (var path in new[] { $"/admin/solutions/{Fixture}", $"/admin/solutions/{Fixture}?cascade=true", "/admin/solutions/Default?cascade=junk" })
        {
            var res = await DeleteAsync(path, _nonAdminOid);
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await res.Content.ReadAsStringAsync()).Should().Be(AdminRequiredBody);
        }
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return req;
    }
}
