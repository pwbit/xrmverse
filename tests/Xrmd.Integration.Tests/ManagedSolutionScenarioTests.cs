using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.Registration;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The managed solutions scenarios of plan 2.6 (spec §7 risk: tests before code), written first against the SDK request types,
/// the Web API and the Admin API. Each test was skipped until the task that implements what it needs un-skipped it; the
/// bodies are the real assertions. The solutions come from <see cref="ManagedTestSolution"/>.
/// </summary>
[Collection("solution-import")]
[Trait("Category", "ManagedScenario")]
public sealed class ManagedSolutionScenarioTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Table = ManagedTestSolution.Table;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await CleanAsync();
    }

    public Task DisposeAsync() => CleanAsync();

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task CleanAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx);
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command("""
                IF OBJECT_ID(N'data.xm_item') IS NOT NULL DELETE FROM data.xm_item;
                IF OBJECT_ID(N'meta.component_layer') IS NOT NULL DELETE FROM meta.component_layer
                    WHERE solution_id IN (SELECT solutionid FROM data.solution WHERE uniquename LIKE N'xm[_]%')
                       OR object_id IN (SELECT objectid FROM data.solutioncomponent WHERE solutionid IN (SELECT solutionid FROM data.solution WHERE uniquename LIKE N'xm[_]%'));
                DELETE FROM data.role WHERE name = N'xm_reader';
                DELETE FROM data.solutioncomponent WHERE solutionid IN (SELECT solutionid FROM data.solution WHERE uniquename LIKE N'xm[_]%');
                DELETE FROM data.solution WHERE uniquename LIKE N'xm[_]%';
                DELETE FROM data.publisher WHERE uniquename = N'xrmversemanaged';
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            if (Store.GetEntity(Table) is not null) await Store.DeleteEntityAsync(s, Table, default);
            if (Store.GetOptionSet("xm_status") is not null) await Store.DeleteOptionSetAsync(s, "xm_status", default);
        });
        await PluginTestHelper.ClearAsync(fx.ConnectionString);
        fx.App.Services.GetRequiredService<StepRegistry>().Invalidate();
    }

    private Task<OrganizationResponse> ImportAsync(ManagedSpec spec, bool holding = false, bool overwrite = false) =>
        Admin.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = ManagedTestSolution.Build(spec), ImportJobId = Guid.NewGuid(), HoldingSolution = holding,
            OverwriteUnmanagedCustomizations = overwrite,
        });

    /// <summary><c>StageAndUpgrade</c> by name (the request type arrives with its task).</summary>
    private Task<OrganizationResponse> StageAndUpgradeAsync(ManagedSpec spec) =>
        Admin.ExecuteAsync(new OrganizationRequest("StageAndUpgrade")
        {
            ["CustomizationFile"] = ManagedTestSolution.Build(spec), ["ImportJobId"] = Guid.NewGuid(),
            ["OverwriteUnmanagedCustomizations"] = false, ["PublishWorkflows"] = false,
        });

    private Task<OrganizationResponse> DeleteAndPromoteAsync(string uniqueName) =>
        Admin.ExecuteAsync(new OrganizationRequest("DeleteAndPromote") { ["UniqueName"] = uniqueName });

    /// <summary>Uninstall from the SDK: <c>Delete</c> of the managed <c>solution</c> row.</summary>
    private async Task UninstallAsync(string uniqueName) => await Admin.DeleteAsync("solution", await SolutionIdAsync(uniqueName) ?? throw new InvalidOperationException(uniqueName));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    /// <summary>The Web API relabel of a column: an unmanaged customization.</summary>
    private async Task<HttpResponseMessage> RelabelAsync(string column, string label) =>
        await SendAsync(HttpMethod.Put, $"{Api}EntityDefinitions(LogicalName='{Table}')/Attributes(LogicalName='{column}')", $$"""
            { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "{{column}}",
              "DisplayName": { "LocalizedLabels": [ { "Label": "{{label}}", "LanguageCode": 1033 } ] } }
            """);

    private async Task<HttpResponseMessage> AdminUninstallAsync(string uniqueName, bool dropData, bool dryRun) =>
        await SendAsync(HttpMethod.Post, $"/admin/solutions/{uniqueName}/uninstall", $$"""{ "dropData": {{(dropData ? "true" : "false")}}, "dryRun": {{(dryRun ? "true" : "false")}} }""");

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode status)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(status, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters) => Convert.ToInt32(await ScalarAsync(sql, parameters));

    private async Task<Guid?> SolutionIdAsync(string uniqueName) =>
        (Guid?)await ScalarAsync("SELECT solutionid FROM data.solution WHERE uniquename = @n", ("@n", uniqueName));

    private Task<int> SolutionRowsAsync(string uniqueName) => CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", uniqueName));

    /// <summary>The layer rows of the solution (<c>meta.component_layer</c>, plan 2.6 ruling M1).</summary>
    private async Task<int> LayerRowsAsync(string uniqueName) => await CountAsync(
        "SELECT COUNT(*) FROM meta.component_layer WHERE solution_id IN (SELECT solutionid FROM data.solution WHERE uniquename = @n)", ("@n", uniqueName));

    /// <summary>The object ids of the components the xm_ solutions track (collected before an uninstall).</summary>
    private async Task<List<Guid>> ComponentIdsAsync()
    {
        var ids = new List<Guid>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("SELECT DISTINCT objectid FROM data.solutioncomponent WHERE solutionid IN (SELECT solutionid FROM data.solution WHERE uniquename LIKE N'xm[_]%')", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        return ids;
    }

    /// <summary>The layer rows (any owner: Managed, System, Active) of the given components.</summary>
    private Task<int> LayerRowsOfAsync(IEnumerable<Guid> ids) =>
        CountAsync($"SELECT COUNT(*) FROM meta.component_layer WHERE object_id IN ({string.Join(",", ids.Select(i => $"'{i}'").DefaultIfEmpty("'00000000-0000-0000-0000-000000000000'"))})");

    private Task<int> RoleRowsAsync() => CountAsync("SELECT COUNT(*) FROM data.role WHERE name = N'xm_reader'");

    private Task<int> WebResourceRowsAsync() => CountAsync("SELECT COUNT(*) FROM data.webresource WHERE name = N'xm_/scripts/item.js'");

    private Task<int> StepRowsAsync() => CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE name LIKE N'Xrmd.TestPlugins.ContextRecorderPlugin: % of xm_item'");

    private Task<bool> TableExistsAsync() => ScalarAsync("SELECT OBJECT_ID(N'data.xm_item')").ContinueWith(t => t.Result is not null);

    private string[] Columns() => Store.GetEntity(Table)?.Attributes.Select(a => a.LogicalName).Order().ToArray() ?? [];

    private async Task<AttributeMetadata> AttributeAsync(string column) =>
        ((RetrieveAttributeResponse)await Admin.ExecuteAsync(new RetrieveAttributeRequest { EntityLogicalName = Table, LogicalName = column })).AttributeMetadata;

    private async Task<string?> LabelAsync(string column) => (await AttributeAsync(column)).DisplayName.UserLocalizedLabel?.Label;


    private Task<Guid> CreateItemAsync(string name, string? legacy = null)
    {
        var item = new Entity(Table) { ["xm_name"] = name };
        if (legacy is not null) item["xm_legacy"] = legacy;
        return Admin.CreateAsync(item);
    }

    /// <summary>
    /// Every component the install of <see cref="ManagedTestSolution.V1"/> tracks has exactly one layer row of the solution, and so
    /// does each system column of the file (ruling R11: layered, but not a row of <c>solutioncomponent</c>): the primary key.
    /// </summary>
    private async Task AssertOneManagedLayerPerComponentAsync(string uniqueName)
    {
        var components = await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid WHERE s.uniquename = @n", ("@n", uniqueName));
        components.Should().BeGreaterThan(0);
        var primaryKey = Store.GetEntity(Table)!.Attributes.Single(a => a.LogicalName == Table + "id").MetadataId!.Value;
        (await CountAsync("""
            SELECT COUNT(*) FROM meta.component_layer l JOIN data.solution s ON s.solutionid = l.solution_id
            WHERE s.uniquename = @n AND l.component_type = 2 AND l.object_id = @pk
            """, ("@n", uniqueName), ("@pk", primaryKey))).Should().Be(1, "the file's system column is layered");
        (await LayerRowsAsync(uniqueName)).Should().Be(components + 1, "one Managed layer per component, and one for the primary key");
        (await CountAsync("""
            SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
            WHERE s.uniquename = @n AND NOT EXISTS (SELECT 1 FROM meta.component_layer l WHERE l.solution_id = s.solutionid
                  AND l.component_type = sc.componenttype AND l.object_id = sc.objectid)
            """, ("@n", uniqueName))).Should().Be(0);
    }

    // ---- scenarios --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Install_v1_creates_layers()
    {
        await ImportAsync(ManagedTestSolution.V1);

        (await CountAsync("SELECT CAST(ismanaged AS int) FROM data.solution WHERE uniquename = N'xm_base'")).Should().Be(1);
        await AssertOneManagedLayerPerComponentAsync("xm_base");
        var table = Store.GetEntity(Table)!;
        table.IsManaged.Should().BeTrue();
        table.Attributes.Where(a => a.LogicalName.StartsWith("xm_", StringComparison.Ordinal)).Should().OnlyContain(a => a.IsManaged == true);
        Columns().Should().Contain(["xm_code", "xm_legacy", "xm_name"]);
        Store.GetOptionSet("xm_status")!.IsManaged.Should().BeTrue();
        (await RoleRowsAsync()).Should().Be(1);
        (await WebResourceRowsAsync()).Should().Be(1);
        (await StepRowsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Customize_then_update_v2_keeps_customization()
    {
        await ImportAsync(ManagedTestSolution.V1);
        (await RelabelAsync("xm_code", "Customized code")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await ImportAsync(ManagedTestSolution.V2); // no flag: an update

        (await LabelAsync("xm_code")).Should().Be("Customized code", "the unmanaged layer stays on top");
        (await LabelAsync("xm_name")).Should().Be("Title", "v2 was applied");
        Columns().Should().Contain("xm_legacy", "an update keeps what v2 no longer has");
        Columns().Should().Contain("xm_extra");
        (await RoleRowsAsync()).Should().Be(1, "an update keeps the role v2 dropped");
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(0);
        (await ScalarAsync("SELECT version FROM data.solution WHERE uniquename = N'xm_base'")).Should().Be("2.0.0.0");
    }

    [Fact]
    public async Task Acceptance_v1_customize_upgrade_v2_uninstall()
    {
        await ImportAsync(ManagedTestSolution.V1);
        (await RelabelAsync("xm_code", "Customized code")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var item = await CreateItemAsync("first"); // xm_legacy stays empty: the upgrade may drop the column

        await StageAndUpgradeAsync(ManagedTestSolution.V2);

        (await LabelAsync("xm_code")).Should().Be("Customized code", "the customization survives the upgrade");
        (await LabelAsync("xm_name")).Should().Be("Title");
        Columns().Should().NotContain("xm_legacy").And.Contain("xm_extra");
        (await RoleRowsAsync()).Should().Be(0, "v2 dropped xm_reader");
        (await StepRowsAsync()).Should().Be(1, "v2 keeps the step");
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(0);
        (await ScalarAsync("SELECT version FROM data.solution WHERE uniquename = N'xm_base'")).Should().Be("2.0.0.0");
        (await Admin.RetrieveAsync(Table, item, new Microsoft.Xrm.Sdk.Query.ColumnSet("xm_name"))).GetAttributeValue<string>("xm_name").Should().Be("first");

        await Admin.DeleteAsync(Table, item); // the table holds no data when the solution goes
        var components = await ComponentIdsAsync();
        components.Should().NotBeEmpty();
        await UninstallAsync("xm_base");

        Store.GetEntity(Table).Should().BeNull();
        Store.GetOptionSet("xm_status").Should().BeNull();
        (await TableExistsAsync()).Should().BeFalse();
        (await RoleRowsAsync()).Should().Be(0);
        (await WebResourceRowsAsync()).Should().Be(0);
        (await StepRowsAsync()).Should().Be(0);
        (await SolutionRowsAsync("xm_base")).Should().Be(0);
        (await LayerRowsOfAsync(components)).Should().Be(0, "no layer of the solution's components is left");
        (await CountAsync("SELECT COUNT(*) FROM data.savedquery WHERE name = N'Active Items'")).Should().Be(0, "the view is gone");
        (await CountAsync("SELECT COUNT(*) FROM data.publisher WHERE uniquename = N'xrmversemanaged'")).Should().Be(1, "the publisher stays");
    }

    [Fact]
    public async Task Stage_then_promote()
    {
        await ImportAsync(ManagedTestSolution.V1);

        await ImportAsync(ManagedTestSolution.V2, holding: true);

        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(1, "the stage creates the holding solution");
        (await SolutionRowsAsync("xm_base")).Should().Be(1);
        (await CountAsync("SELECT CAST(ismanaged AS int) FROM data.solution WHERE uniquename = N'xm_base_Upgrade'")).Should().Be(1);
        (await ScalarAsync("SELECT version FROM data.solution WHERE uniquename = N'xm_base'")).Should().Be("1.0.0.0", "the base keeps its version until the promote");
        var holdingId = (await SolutionIdAsync("xm_base_Upgrade"))!.Value;

        var promoted = await DeleteAndPromoteAsync("xm_base");

        promoted.Results["SolutionId"].Should().Be(holdingId, "the holding solution's id lives on");
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(0);
        (await SolutionIdAsync("xm_base")).Should().Be(holdingId);
        (await ScalarAsync("SELECT version FROM data.solution WHERE uniquename = N'xm_base'")).Should().Be("2.0.0.0");
        Columns().Should().NotContain("xm_legacy").And.Contain("xm_extra");
        (await RoleRowsAsync()).Should().Be(0);
        await AssertOneManagedLayerPerComponentAsync("xm_base");
    }

    [Fact]
    public async Task Other_layers_above_base()
    {
        await ImportAsync(ManagedTestSolution.V1);
        (await LabelAsync("xm_code")).Should().Be("Code");

        await ImportAsync(ManagedTestSolution.Other);

        (await LabelAsync("xm_code")).Should().Be("Code (other)", "the solution installed last is on top");

        await UninstallAsync("xm_other");

        (await LabelAsync("xm_code")).Should().Be("Code", "the layer below shows again");
        (await SolutionRowsAsync("xm_other")).Should().Be(0);
        Columns().Should().Contain(["xm_code", "xm_legacy", "xm_name"], "v1's components are not touched");
    }

    [Fact]
    public async Task Uninstall_with_data_is_refused_and_changes_nothing()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await CreateItemAsync("one", legacy: "kept");
        var layers = await LayerRowsAsync("xm_base");
        var columns = Columns();

        var message = await BadRequestAsync(() => UninstallAsync("xm_base"));

        message.Should().Contain("xm_item").And.ContainEquivalentOf("uninstall");
        (await SolutionRowsAsync("xm_base")).Should().Be(1);
        (await LayerRowsAsync("xm_base")).Should().Be(layers);
        Columns().Should().Equal(columns);
        (await CountAsync("SELECT COUNT(*) FROM data.xm_item")).Should().Be(1);
        (await RoleRowsAsync()).Should().Be(1);
        (await WebResourceRowsAsync()).Should().Be(1);
        (await StepRowsAsync()).Should().Be(1);

        // The Admin dry run reports the same loss and writes nothing.
        var plan = await JsonAsync(await AdminUninstallAsync("xm_base", dropData: false, dryRun: true), HttpStatusCode.OK);
        plan["dataLoss"]!.AsArray().Should().NotBeEmpty();
        (await SolutionRowsAsync("xm_base")).Should().Be(1);
        Store.GetEntity(Table).Should().NotBeNull();
    }

    [Fact]
    public async Task Uninstall_with_dropData_drops()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await CreateItemAsync("one", legacy: "lost");

        // Without the confirmation the Admin API refuses too.
        (await AdminUninstallAsync("xm_base", dropData: false, dryRun: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Store.GetEntity(Table).Should().NotBeNull();

        var body = await JsonAsync(await AdminUninstallAsync("xm_base", dropData: true, dryRun: false), HttpStatusCode.OK);

        body["deleted"]!.AsArray().Should().NotBeEmpty();
        Store.GetEntity(Table).Should().BeNull();
        (await TableExistsAsync()).Should().BeFalse("the table and its rows are dropped");
        (await SolutionRowsAsync("xm_base")).Should().Be(0);
        (await LayerRowsAsync("xm_base")).Should().Be(0);
        (await RoleRowsAsync()).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.publisher WHERE uniquename = N'xrmversemanaged'")).Should().Be(1);
    }

    [Fact]
    public async Task Uninstall_base_under_other_solution_is_refused()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(ManagedTestSolution.Other);

        var fault = await FaultAsync(() => UninstallAsync("xm_base"));

        fault.ErrorCode.Should().Be(unchecked((int)0x8004F01D));
        fault.Message.Should().Contain("xm_other");
        new[] { "xm_code", "xm_name", "xm_item" }.Should().Contain(c => fault.Message.Contains(c), "the message names a component the two solutions share");
        (await SolutionRowsAsync("xm_base")).Should().Be(1);
        (await SolutionRowsAsync("xm_other")).Should().Be(1);
        (await LabelAsync("xm_code")).Should().Be("Code (other)");
        Store.GetEntity(Table).Should().NotBeNull();
    }

    [Fact]
    public async Task NonCustomizable_component_refuses_update()
    {
        var locked = ManagedTestSolution.V1 with
        {
            Tables = [new(Table, "Item", [
                new("xm_name", "Name", PrimaryName: true),
                new("xm_code", "Code", Props: new(IsCustomizable: false, IsRenameable: false)),
                new("xm_legacy", "Legacy"),
            ])],
        };
        await ImportAsync(locked);

        var response = await RelabelAsync("xm_code", "Nope");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("xm_code");
        (await LabelAsync("xm_code")).Should().Be("Code");

        // A column of the same table that is customizable still changes.
        (await RelabelAsync("xm_legacy", "Old")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LabelAsync("xm_legacy")).Should().Be("Old");
    }
}
