using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Registration;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.Solution.Layers;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The uninstall of a managed solution (plan 2.6, Task 6, rulings M9, M10 and M15): SDK and Web API Delete of the solution row,
/// <c>UninstallSolutionAsync</c> (system job 208) and the Admin API's <c>POST /admin/solutions/{uniquename}/uninstall</c>, with
/// the data-loss guard and the restore of the layer below.
/// </summary>
[Collection("solution-import")]
public sealed class SolutionUninstallTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Table = ManagedTestSolution.Table;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    /// <summary>The seed state of <c>account</c> before a test layered it, restored by <see cref="DisposeAsync"/>.</summary>
    private (Label? Name, Label? Collection, Label? Description, string? Unmodelled)? _account;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await CleanAsync();
    }

    public async Task DisposeAsync()
    {
        if (_account is { } account)
            await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
            {
                if (Store.GetEntity("account")!.Attributes.Any(a => a.LogicalName == "xm_rating"))
                    await Store.DeleteAttributeAsync(s, "account", "xm_rating", default);
                await Store.UpdateEntityAsync(s, new EntityMetadata
                {
                    LogicalName = "account", DisplayName = account.Name ?? new Label(), DisplayCollectionName = account.Collection ?? new Label(),
                    Description = account.Description ?? new Label(),
                }, replaceLabels: true, default);
                await Store.SetUnmodelledXmlAsync(s, ComponentType.Entity, "account", account.Unmodelled, default);
                await Store.SetIsManagedAsync(s, "account", null, null, default);
            });
        await CleanAsync();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task CleanAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx);
        await ExecAsync("""
            IF OBJECT_ID(N'data.xm_item') IS NOT NULL DELETE FROM data.xm_item;
            DELETE FROM meta.component_layer; DELETE FROM meta.solution_rank;
            DELETE FROM data.role WHERE name IN (N'xm_reader', N'xm_writer');
            DELETE FROM data.solutioncomponent WHERE solutionid IN (SELECT solutionid FROM data.solution WHERE uniquename LIKE N'xm[_]%');
            DELETE FROM data.solution WHERE uniquename LIKE N'xm[_]%';
            DELETE FROM data.publisher WHERE uniquename = N'xrmversemanaged';
            DELETE FROM data.asyncoperation WHERE operationtype = 208;
            """);
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            if (Store.GetEntity(Table) is not null) await Store.DeleteEntityAsync(s, Table, default);
            if (Store.GetOptionSet("xm_status") is not null) await Store.DeleteOptionSetAsync(s, "xm_status", default);
        });
        await PluginTestHelper.ClearAsync(fx.ConnectionString);
        fx.App.Services.GetRequiredService<StepRegistry>().Invalidate();
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
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

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters) => Convert.ToInt32(await ScalarAsync(sql, parameters));

    private Task ImportAsync(ManagedSpec spec) => ImportAsync(ManagedTestSolution.Build(spec));

    private Task ImportAsync(byte[] file) =>
        Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = file, ImportJobId = Guid.NewGuid() });

    private async Task<Guid> SolutionIdAsync(string uniqueName) =>
        (Guid)(await ScalarAsync("SELECT solutionid FROM data.solution WHERE uniquename = @n", ("@n", uniqueName)))!;

    private Task<int> SolutionRowsAsync(string uniqueName) => CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", uniqueName));

    private Task<int> AllLayerRowsAsync() => CountAsync("SELECT COUNT(*) FROM meta.component_layer");

    private Task<int> RoleRowsAsync(string name = "xm_reader") => CountAsync("SELECT COUNT(*) FROM data.role WHERE name = @n", ("@n", name));

    private Task<int> StepRowsAsync() => CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE name LIKE N'Xrmd.TestPlugins.ContextRecorderPlugin: % of xm_item'");

    private Task<int> WebResourceRowsAsync() => CountAsync("SELECT COUNT(*) FROM data.webresource WHERE name = N'xm_/scripts/item.js'");

    private string[] Columns() => Store.GetEntity(Table)?.Attributes.Select(a => a.LogicalName).Order().ToArray() ?? [];

    private AttributeMetadata Column(string column) => Store.GetEntity(Table)!.Attributes.Single(a => a.LogicalName == column);

    private Task<Guid> CreateItemAsync(string name, string? legacy = null)
    {
        var item = new Entity(Table) { ["xm_name"] = name };
        if (legacy is not null) item["xm_legacy"] = legacy;
        return Admin.CreateAsync(item);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> AdminUninstallAsync(string uniqueName, bool dropData, bool dryRun) =>
        await SendAsync(HttpMethod.Post, $"/admin/solutions/{uniqueName}/uninstall",
            $$"""{ "dropData": {{(dropData ? "true" : "false")}}, "dryRun": {{(dryRun ? "true" : "false")}} }""");

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode status)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(status, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static IEnumerable<string> Names(JsonObject body, string list) =>
        body[list]!.AsArray().Select(n => n is JsonValue v ? v.GetValue<string>() : (string)n!["name"]!);

    /// <summary>The state an uninstall that is refused must leave as it was.</summary>
    private async Task<string> SnapshotAsync() => string.Join("|",
        await SolutionRowsAsync("xm_base"), await AllLayerRowsAsync(), string.Join(",", Columns()), await RoleRowsAsync(),
        await StepRowsAsync(), await WebResourceRowsAsync(), await CountAsync("SELECT COUNT(*) FROM data.xm_item"),
        await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid IN (SELECT solutionid FROM data.solution WHERE uniquename = N'xm_base')"),
        Store.GetOptionSet("xm_status") is not null);

    /// <summary>The rows the step of <see cref="ManagedTestSolution.V1"/> (post-operation Update of xm_item) wrote.</summary>
    private Task<int> PipeLogRowsAsync() => CountAsync("SELECT COUNT(*) FROM data.pb_pipelog");

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Uninstall_with_data_is_refused_and_changes_nothing()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await CreateItemAsync("one", legacy: "kept");
        var before = await SnapshotAsync();

        // SDK: Delete of the managed solution row.
        var solution = await SolutionIdAsync("xm_base");
        var sdk = await BadRequestAsync(() => Admin.DeleteAsync("solution", solution));
        sdk.Should().Contain("table xm_item (1 row)").And.Contain("/admin/solutions/xm_base/uninstall").And.Contain("--drop-data");
        (await SnapshotAsync()).Should().Be(before);

        // Web API: DELETE solutions(id).
        var web = await SendAsync(HttpMethod.Delete, $"{Api}solutions({solution})");
        var body = await web.Content.ReadAsStringAsync();
        web.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("xm_item").And.Contain("dropData");
        (await SnapshotAsync()).Should().Be(before);
        Store.GetEntity(Table)!.IsManaged.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_uninstall_dropData_drops()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await CreateItemAsync("one", legacy: "lost");

        var refused = await AdminUninstallAsync("xm_base", dropData: false, dryRun: false);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("table xm_item (1 row)");
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await JsonAsync(await AdminUninstallAsync("xm_base", dropData: true, dryRun: false), HttpStatusCode.OK);

        Names(body, "deleted").Should().Contain(["xm_item", "xm_item.xm_legacy", "xm_status", "xm_reader", "xm_/scripts/item.js"]);
        body["dataLoss"]!.AsArray().Should().ContainSingle();
        body["dataLoss"]![0]!["table"]!.GetValue<string>().Should().Be("xm_item");
        body["dataLoss"]![0]!["rows"]!.GetValue<long>().Should().Be(1);
        Store.GetEntity(Table).Should().BeNull();
        Store.GetOptionSet("xm_status").Should().BeNull();
        (await ScalarAsync("SELECT OBJECT_ID(N'data.xm_item')")).Should().BeNull();
        (await SolutionRowsAsync("xm_base")).Should().Be(0);
        (await AllLayerRowsAsync()).Should().Be(0);
        (await RoleRowsAsync()).Should().Be(0);
        (await StepRowsAsync()).Should().Be(0);
        (await WebResourceRowsAsync()).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.publisher WHERE uniquename = N'xrmversemanaged'")).Should().Be(1, "the publisher stays");
    }

    /// <summary>
    /// Final review M1: the confirmation carries the data loss the dry run showed; a loss the plan under the lock adds (a row
    /// written since) is a 409 and nothing changes; the loss shown confirms.
    /// </summary>
    [Fact]
    public async Task Admin_uninstall_confirm_refuses_loss_not_shown_by_dry_run()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var plan = await JsonAsync(await AdminUninstallAsync("xm_base", dropData: false, dryRun: true), HttpStatusCode.OK);
        plan["dataLoss"]!.AsArray().Should().BeEmpty();
        await CreateItemAsync("one", legacy: "written after the dry run");
        var before = await SnapshotAsync();

        var res = await SendAsync(HttpMethod.Post, "/admin/solutions/xm_base/uninstall", """{ "dropData": true, "expectedDataLoss": [] }""");

        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, text);
        text.Should().Contain("table xm_item (1 row)").And.Contain("did not show");
        (await SnapshotAsync()).Should().Be(before);

        var bad = await SendAsync(HttpMethod.Post, "/admin/solutions/xm_base/uninstall", """{ "dropData": true, "expectedDataLoss": "xm_item" }""");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SnapshotAsync()).Should().Be(before);

        await JsonAsync(await SendAsync(HttpMethod.Post, "/admin/solutions/xm_base/uninstall",
            """{ "dropData": true, "expectedDataLoss": [ { "table": "xm_item", "column": null, "rows": 1 } ] }"""), HttpStatusCode.OK);
        (await SolutionRowsAsync("xm_base")).Should().Be(0);
    }

    [Fact]
    public async Task Admin_uninstall_dryRun_writes_nothing()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await CreateItemAsync("one", legacy: "kept");
        var before = await SnapshotAsync();

        var body = await JsonAsync(await AdminUninstallAsync("xm_base", dropData: false, dryRun: true), HttpStatusCode.OK);

        Names(body, "deleted").Should().Contain(["xm_item", "xm_reader"]);
        body["dataLoss"]!.AsArray().Should().ContainSingle().Which!["table"]!.GetValue<string>().Should().Be("xm_item");
        (await SnapshotAsync()).Should().Be(before);

        // With dropData as well, a dry run still writes nothing.
        await JsonAsync(await AdminUninstallAsync("xm_base", dropData: true, dryRun: true), HttpStatusCode.OK);
        (await SnapshotAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Uninstall_upper_restores_lower()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(ManagedTestSolution.Other);
        Column("xm_code").DisplayName.UserLocalizedLabel!.Label.Should().Be("Code (other)");

        var body = await JsonAsync(await AdminUninstallAsync("xm_other", dropData: false, dryRun: false), HttpStatusCode.OK);

        Names(body, "restored").Should().Contain("xm_item.xm_code");
        Names(body, "deleted").Should().BeEmpty("every component of xm_other is still layered by xm_base");
        Column("xm_code").DisplayName.UserLocalizedLabel!.Label.Should().Be("Code", "xm_base's layer shows again");
        Column("xm_code").IsManaged.Should().BeTrue("xm_base still layers it");
        (await SolutionRowsAsync("xm_other")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM meta.component_layer WHERE solution_id = @s", ("@s", await SolutionIdAsync("xm_base"))))
            .Should().BeGreaterThan(0);
        (await CountAsync("SELECT COUNT(*) FROM meta.solution_rank")).Should().Be(1, "xm_other's rank is gone");
    }

    [Fact]
    public async Task Uninstall_base_under_other_solution_is_refused()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(ManagedTestSolution.Other);
        var layers = await AllLayerRowsAsync();

        var solution = await SolutionIdAsync("xm_base");
        var fault = await FaultAsync(() => Admin.DeleteAsync("solution", solution));
        fault.ErrorCode.Should().Be(unchecked((int)0x8004F01D));
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xm_other").And.Contain("xm_item");

        var admin = await AdminUninstallAsync("xm_base", dropData: true, dryRun: false);
        admin.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.Content.ReadAsStringAsync()).Should().ContainEquivalentOf("0x8004f01d");

        (await AllLayerRowsAsync()).Should().Be(layers);
        (await SolutionRowsAsync("xm_base")).Should().Be(1);
        Column("xm_code").DisplayName.UserLocalizedLabel!.Label.Should().Be("Code (other)");
    }

    [Fact]
    public async Task Uninstall_deletes_component_with_active_marker()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var code = Column("xm_code").MetadataId!.Value;
        var relabel = await SendAsync(HttpMethod.Put, $"{Api}EntityDefinitions(LogicalName='{Table}')/Attributes(LogicalName='xm_code')", """
            { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "xm_code",
              "DisplayName": { "LocalizedLabels": [ { "Label": "Customized", "LanguageCode": 1033 } ] } }
            """);
        relabel.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await CountAsync("SELECT COUNT(*) FROM meta.component_layer WHERE object_id = @o AND solution_id = @a",
            ("@o", code), ("@a", SystemSeed.ActiveSolutionId))).Should().Be(1);

        await Admin.DeleteAsync("solution", await SolutionIdAsync("xm_base"));

        Store.GetEntity(Table).Should().BeNull("an Active marker does not keep a component whose managed layers are gone");
        (await CountAsync("SELECT COUNT(*) FROM meta.component_layer WHERE object_id = @o", ("@o", code))).Should().Be(0);
        (await AllLayerRowsAsync()).Should().Be(0);
        (await SolutionRowsAsync("xm_base")).Should().Be(0);
    }

    [Fact]
    public async Task Seeded_component_survives_and_is_restored()
    {
        var account = Store.GetEntity("account")!;
        var seedLabel = account.DisplayName?.UserLocalizedLabel?.Label;
        var seedManaged = account.IsManaged;
        await TestEntities.InSessionAsync(fx.ConnectionString, s =>
        {
            _account = (account.DisplayName, account.DisplayCollectionName, account.Description,
                Store.GetUnmodelledXml(s, ComponentType.Entity, "account"));
            return Task.CompletedTask;
        });
        await ImportAsync(AccountFile(account));
        Store.GetEntity("account")!.DisplayName?.UserLocalizedLabel?.Label.Should().Be("Client");
        Store.GetEntity("account")!.IsManaged.Should().BeTrue();

        var body = await JsonAsync(await AdminUninstallAsync("xm_acct", dropData: false, dryRun: false), HttpStatusCode.OK);

        Names(body, "restored").Should().Contain("account");
        Names(body, "deleted").Should().Contain("account.xm_rating");
        var restored = Store.GetEntity("account")!;
        restored.DisplayName?.UserLocalizedLabel?.Label.Should().Be(seedLabel, "the System layer is restored");
        restored.IsManaged.Should().Be(seedManaged, "no managed layer is left");
        restored.Attributes.Should().NotContain(a => a.LogicalName == "xm_rating");
        (await ScalarAsync("SELECT OBJECT_ID(N'data.account')")).Should().NotBeNull("a seeded table is never dropped");
        var stack = await CountAsync("SELECT COUNT(*) FROM meta.component_layer WHERE object_id = @o AND solution_id = @s",
            ("@o", account.MetadataId!.Value), ("@s", ComponentLayerStore.SystemLayerSolutionId));
        stack.Should().Be(1, "the System layer stays");
        (await SolutionRowsAsync("xm_acct")).Should().Be(0);
    }

    [Fact]
    public async Task UninstallSolutionAsync_queues_208_and_completes()
    {
        await ImportAsync(ManagedTestSolution.V1);

        var response = (UninstallSolutionAsyncResponse)await Admin.ExecuteAsync(new UninstallSolutionAsyncRequest { SolutionUniqueName = "xm_base" });

        var jobId = response.AsyncOperationId;
        jobId.Should().NotBeEmpty();
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = @id AND operationtype = 208 AND statecode = 0 AND data = N'xm_base'",
            ("@id", jobId))).Should().Be(1);
        (await SolutionRowsAsync("xm_base")).Should().Be(1, "queued, not run");

        var worker = new AsyncOperationWorker(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Options.Create(new PipelineOptions()));
        (await worker.RunOnceAsync(default)).Should().BeTrue();

        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = @id AND statecode = 3 AND statuscode = 30",
            ("@id", jobId))).Should().Be(1, (string?)await ScalarAsync("SELECT message FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", jobId)));
        (await SolutionRowsAsync("xm_base")).Should().Be(0);
        Store.GetEntity(Table).Should().BeNull();

        // Through the Web API, an unknown solution is refused before anything is queued.
        var unknown = await SendAsync(HttpMethod.Post, $"{Api}UninstallSolutionAsync", """{ "SolutionUniqueName": "xm_missing" }""");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UninstallSolutionAsync_through_the_Web_API_returns_the_job()
    {
        await ImportAsync(ManagedTestSolution.V1);

        var body = await JsonAsync(await SendAsync(HttpMethod.Post, $"{Api}UninstallSolutionAsync", """{ "SolutionUniqueName": "xm_base" }"""),
            HttpStatusCode.OK);

        var id = Guid.Parse(body["AsyncOperationId"]!.GetValue<string>());
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = @id AND operationtype = 208", ("@id", id))).Should().Be(1);
    }

    [Fact]
    public async Task Failed_uninstall_keeps_caches()
    {
        await ImportAsync(ManagedTestSolution.V1);
        // xm_late layers xm_code above xm_base with a longer MaxLength, and brings a role of its own: its uninstall deletes the
        // role, then fails restoring xm_code (the store cannot shorten a column), so everything is rolled back.
        await ImportAsync(new ManagedSpec("xm_late", "1.0.0.0", true,
            [new(Table, "Item", [new("xm_name", "Name", PrimaryName: true), new("xm_code", "Code (late)", MaxLength: 300)])],
            Roles: [new("xm_writer", [("prvWritexm_Item", "Global")])]));
        Column("xm_code").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(300);
        (await RoleRowsAsync("xm_writer")).Should().Be(1);
        var item = await CreateItemAsync("one");
        var layers = await AllLayerRowsAsync();

        var late = await SolutionIdAsync("xm_late");
        var message = await BadRequestAsync(() => Admin.DeleteAsync("solution", late));

        message.Should().Contain("xm_code");
        (await RoleRowsAsync("xm_writer")).Should().Be(1, "the role's delete was rolled back");
        (await SolutionRowsAsync("xm_late")).Should().Be(1);
        (await AllLayerRowsAsync()).Should().Be(layers);
        Column("xm_code").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(300, "the metadata cache is unchanged");
        Column("xm_code").DisplayName.UserLocalizedLabel!.Label.Should().Be("Code (late)");
        var role = (Guid)(await ScalarAsync("SELECT roleid FROM data.role WHERE name = N'xm_writer'"))!;
        (await Admin.RetrieveAsync("role", role, new Microsoft.Xrm.Sdk.Query.ColumnSet("name"))).GetAttributeValue<string>("name").Should().Be("xm_writer");

        // The step of xm_base still fires (the step registry was not invalidated into a state without it).
        var logged = await PipeLogRowsAsync();
        await Admin.UpdateAsync(new Entity(Table, item) { ["xm_code"] = "x" });
        (await PipeLogRowsAsync()).Should().BeGreaterThan(logged);
    }

    [Fact]
    public async Task Unmanaged_delete_still_removes_container_only()
    {
        await ImportAsync(ManagedTestSolution.V1 with { Name = "xm_plain", Managed = false });
        var solution = await SolutionIdAsync("xm_plain");

        await Admin.DeleteAsync("solution", solution);

        (await SolutionRowsAsync("xm_plain")).Should().Be(0);
        Store.GetEntity(Table).Should().NotBeNull("the container delete keeps the components");
        (await RoleRowsAsync()).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @d AND objectid = @o",
            ("@d", SystemSeed.DefaultSolutionId), ("@o", Store.GetEntity(Table)!.MetadataId!.Value))).Should().Be(1, "Default tracks them");

        // The Admin uninstall is for managed solutions only.
        await ImportAsync(ManagedTestSolution.V1 with { Name = "xm_plain", Managed = false });
        var admin = await AdminUninstallAsync("xm_plain", dropData: false, dryRun: false);
        admin.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.Content.ReadAsStringAsync()).Should().Contain("unmanaged");
        (await SolutionRowsAsync("xm_plain")).Should().Be(1);
    }

    [Fact]
    public async Task Uninstall_without_Delete_on_solution_is_403_and_changes_nothing()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var before = await SnapshotAsync();
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ("prvReadSolution", DepthMask.Global));

        await AssertAllRefusedAsync(objectId, "xm_base");

        (await SnapshotAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Uninstall_without_the_metadata_Delete_privilege_is_403_and_changes_nothing()
    {
        // Only a table and an option set: deleting them needs prvDeleteEntity and prvDeleteOptionSet, which the user lacks.
        await ImportAsync(ManagedTestSolution.V1 with { Views = [], Roles = [], WebResources = [], Steps = [] });
        var before = await SnapshotAsync();
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ("prvReadSolution", DepthMask.Global), ("prvDeleteSolution", DepthMask.Global));

        await AssertAllRefusedAsync(objectId, "xm_base", "prvDeleteEntity");

        (await SnapshotAsync()).Should().Be(before);
    }

    /// <summary>
    /// The SDK Delete, <c>UninstallSolutionAsync</c> (nothing queued) and the Admin route (System Administrators only) all answer
    /// 403 to the user <paramref name="objectId"/>.
    /// </summary>
    private async Task AssertAllRefusedAsync(Guid objectId, string uniqueName, string? privilege = null)
    {
        var service = ServiceFor(fx, objectId);
        var solution = await SolutionIdAsync(uniqueName);
        var delete = await FaultAsync(() => service.DeleteAsync("solution", solution));
        delete.HttpStatus.Should().Be(403, delete.Message);
        if (privilege is not null) delete.Message.Should().Contain(privilege);

        var queued = await FaultAsync(() => service.ExecuteAsync(new UninstallSolutionAsyncRequest { SolutionUniqueName = uniqueName }));
        queued.HttpStatus.Should().Be(403, queued.Message);
        if (privilege is not null) queued.Message.Should().Contain(privilege);
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE operationtype = 208")).Should().Be(0);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/admin/solutions/{uniqueName}/uninstall")
        {
            Content = new StringContent("""{ "dropData": true }""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, fx.BaseUrl));
        (await fx.Client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UninstallSolutionAsync_of_an_unmanaged_solution_is_400()
    {
        await ImportAsync(ManagedTestSolution.V1 with { Name = "xm_plain", Managed = false });

        var fault = await FaultAsync(() => Admin.ExecuteAsync(new UninstallSolutionAsyncRequest { SolutionUniqueName = "xm_plain" }));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("unmanaged");
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE operationtype = 208")).Should().Be(0);
    }

    [Fact]
    public async Task Plugin_cannot_uninstall()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var before = await SnapshotAsync();
        var me = (WhoAmIResponse)await Admin.ExecuteAsync(new WhoAmIRequest());
        var caller = new Caller(me.UserId, me.BusinessUnitId);
        await using var scope = fx.App.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);

        // UninstallSolutionAsync from a plug-in's service: 400.
        var queued = await FaultAsync(() => core.ExecuteNonPipelineAsync(
            new UninstallSolutionAsyncRequest { SolutionUniqueName = "xm_base" }, caller, new DataSession(conn, null), default));
        queued.HttpStatus.Should().Be(400);
        queued.Message.Should().Contain("plug-in");

        // A plug-in's Delete of the managed solution row reaches the Core's Delete (stage 30): the managed refusal.
        await using (var tx = await conn.BeginTransactionAsync())
        {
            var solution = await SolutionIdAsync("xm_base");
            var fault = await FaultAsync(() => core.ExecuteCoreAsync(
                new DeleteRequest { Target = new EntityReference("solution", solution) }, caller, new DataSession(conn, tx), default));
            fault.HttpStatus.Should().Be(400);
            fault.Message.Should().Be(SolutionDeleter.ManagedNotSupported);
            await tx.RollbackAsync();
        }
        (await SnapshotAsync()).Should().Be(before);
    }

    /// <summary>A managed solution <c>xm_acct</c> relabelling the seeded <c>account</c> ("Client") and adding <c>xm_rating</c>.</summary>
    private static byte[] AccountFile(EntityMetadata account)
    {
        var spec = new ManagedSpec("xm_acct", "1.0.0.0", true, [new TestTable("account", "Client", [new TestColumn("xm_rating", "Rating")])]);
        var (solutionXml, customizations, files) = SolutionZip.Read(ManagedTestSolution.Build(spec));
        var entity = customizations.Root!.Descendants("entity").Single();
        entity.Element("OwnershipTypeMask")!.Value = "UserOwned";
        entity.Element("IsAuditEnabled")!.Value = account.IsAuditEnabled?.Value == true ? "1" : "0"; // the seed's, unchanged
        entity.Element("attributes")!.Elements("attribute").Single(a => (string?)a.Element("LogicalName") == "accountid").Remove();
        var kept = files.Where(f => !f.Key.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
        return SolutionZip.Write(solutionXml, customizations, kept);
    }

    // ---- plan 2.11: a custom activity in a managed solution (ruling A19) ----------------------------------------------------

    [Fact]
    public async Task Managed_import_and_uninstall_of_custom_activity()
    {
        await ImportAsync(CustomActivitySolution.Build(managed: true));
        var sms = Store.GetEntity(CustomActivitySolution.Table)!;
        sms.IsActivity.Should().BeTrue();
        sms.IsManaged.Should().BeTrue();
        var regarding = ActivitySchema.RegardingRelationship(Store.GetEntity("account")!, sms).SchemaName;
        Store.GetRelationship(regarding).Should().NotBeNull();

        var accountId = await Admin.CreateAsync(new Entity("account") { ["name"] = "Uninstall target" });
        try
        {
            var smsId = await Admin.CreateAsync(new Entity(CustomActivitySolution.Table)
            {
                ["subject"] = "Bye", ["regardingobjectid"] = new EntityReference("account", accountId),
                ["to"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("account", accountId) }]),
            });
            (await CountAsync("SELECT COUNT(*) FROM data.activitypointer WHERE activityid = @id", ("@id", smsId))).Should().Be(1);
            (await CountAsync("SELECT COUNT(*) FROM data.activityparty WHERE activityid = @id", ("@id", smsId))).Should().BeGreaterThan(0);

            var body = await JsonAsync(await AdminUninstallAsync(SolutionFixtureZip.FixtureName, dropData: true, dryRun: false), HttpStatusCode.OK);

            Names(body, "deleted").Should().Contain(CustomActivitySolution.Table);
            Store.GetEntity(CustomActivitySolution.Table).Should().BeNull();
            (await ScalarAsync("SELECT OBJECT_ID(N'data.xv_sms')")).Should().BeNull();
            (await CountAsync("SELECT COUNT(*) FROM data.activitypointer WHERE activityid = @id", ("@id", smsId))).Should().Be(0);
            (await CountAsync("SELECT COUNT(*) FROM data.activityparty WHERE activityid = @id", ("@id", smsId))).Should().Be(0);
            Store.GetRelationship(regarding).Should().BeNull();
            ((LookupAttributeMetadata)Store.GetEntity("annotation")!.Attributes.Single(a => a.LogicalName == "objectid")).Targets
                .Should().NotContain(CustomActivitySolution.Table);
            ((LookupAttributeMetadata)Store.GetEntity("activityparty")!.Attributes.Single(a => a.LogicalName == "activityid")).Targets
                .Should().NotContain(CustomActivitySolution.Table);
        }
        finally
        {
            await Admin.DeleteAsync("account", accountId);
        }
    }

    [Fact]
    public async Task Uninstall_of_custom_activity_with_notes_on_its_records_is_400_also_in_the_dry_run()
    {
        // Ruling Z6: an uninstall does not cascade notes (2.11); the dry run predicts the 400 of the real uninstall.
        await ImportAsync(CustomActivitySolution.Build(managed: true));
        var smsId = await Admin.CreateAsync(new Entity(CustomActivitySolution.Table) { ["subject"] = "With a note" });
        var noteId = await Admin.CreateAsync(new Entity("annotation")
        {
            ["subject"] = "Note", ["objectid"] = new EntityReference(CustomActivitySolution.Table, smsId),
        });

        foreach (var dryRun in new[] { true, false })
        {
            var refused = await AdminUninstallAsync(SolutionFixtureZip.FixtureName, dropData: true, dryRun: dryRun);
            var text = await refused.Content.ReadAsStringAsync();
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
            text.Should().Contain("notes are attached").And.Contain(CustomActivitySolution.Table);
        }
        Store.GetEntity(CustomActivitySolution.Table).Should().NotBeNull("nothing was changed");
        (await CountAsync("SELECT COUNT(*) FROM data.annotation WHERE annotationid = @id", ("@id", noteId))).Should().Be(1);

        await Admin.DeleteAsync("annotation", noteId);
        await JsonAsync(await AdminUninstallAsync(SolutionFixtureZip.FixtureName, dropData: true, dryRun: false), HttpStatusCode.OK);
        Store.GetEntity(CustomActivitySolution.Table).Should().BeNull();
    }

    [Fact]
    public async Task Unauthorized_uninstall_is_403_before_the_notes_refusal_and_shows_no_counts()
    {
        // Final review I1: the activity and notes refusal of the plan names row counts, so it is checked only after the caller's
        // privileges; a caller without Delete on the solution gets 403 whatever blocks the uninstall.
        await ImportAsync(CustomActivitySolution.Build(managed: true));
        var smsId = await Admin.CreateAsync(new Entity(CustomActivitySolution.Table) { ["subject"] = "With a note" });
        var noteId = await Admin.CreateAsync(new Entity("annotation")
        {
            ["subject"] = "Note", ["objectid"] = new EntityReference(CustomActivitySolution.Table, smsId),
        });
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ("prvReadSolution", DepthMask.Global));
        var service = ServiceFor(fx, objectId);

        var solution = await SolutionIdAsync(SolutionFixtureZip.FixtureName);
        var delete = await FaultAsync(() => service.DeleteAsync("solution", solution));
        delete.HttpStatus.Should().Be(403, delete.Message);
        delete.Message.Should().NotContain("notes");
        var queued = await FaultAsync(() => service.ExecuteAsync(new UninstallSolutionAsyncRequest { SolutionUniqueName = SolutionFixtureZip.FixtureName }));
        queued.HttpStatus.Should().Be(403, queued.Message);
        queued.Message.Should().NotContain("notes");
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE operationtype = 208")).Should().Be(0);
        Store.GetEntity(CustomActivitySolution.Table).Should().NotBeNull("nothing was changed");

        await Admin.DeleteAsync("annotation", noteId);
        await JsonAsync(await AdminUninstallAsync(SolutionFixtureZip.FixtureName, dropData: true, dryRun: false), HttpStatusCode.OK);
    }
}
