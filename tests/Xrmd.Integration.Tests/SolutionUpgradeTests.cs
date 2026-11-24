using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Registration;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The holding solution, <c>DeleteAndPromote</c> and <c>StageAndUpgrade</c> (plan 2.6, Task 7, ruling M11), with their async
/// forms (system jobs 207 and 211), the Admin API's promote and the data-loss guard of an upgrade (ruling M10).
/// </summary>
[Collection("solution-import")]
public sealed class SolutionUpgradeTests(XrmdFixture fx) : IAsyncLifetime
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
        await ExecAsync("""
            IF OBJECT_ID(N'data.xm_item') IS NOT NULL DELETE FROM data.xm_item;
            DELETE FROM meta.component_layer; DELETE FROM meta.solution_rank;
            DELETE FROM data.role WHERE name = N'xm_reader';
            DELETE FROM data.solutioncomponent WHERE solutionid IN (SELECT solutionid FROM data.solution WHERE uniquename LIKE N'xm[_]%');
            DELETE FROM data.solution WHERE uniquename LIKE N'xm[_]%';
            DELETE FROM data.publisher WHERE uniquename = N'xrmversemanaged';
            DELETE FROM data.asyncoperation WHERE operationtype IN (203, 207, 211);
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
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.AddParameter(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters) => Convert.ToInt32(await ScalarAsync(sql, parameters));

    private Task ImportAsync(ManagedSpec spec, bool holding = false) =>
        Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = ManagedTestSolution.Build(spec), ImportJobId = Guid.NewGuid(), HoldingSolution = holding });

    private async Task<Guid> StageAndUpgradeAsync(ManagedSpec spec) =>
        ((StageAndUpgradeResponse)await Admin.ExecuteAsync(new StageAndUpgradeRequest
        {
            CustomizationFile = ManagedTestSolution.Build(spec), ImportJobId = Guid.NewGuid(),
        })).SolutionId;

    private async Task<Guid> PromoteAsync(string uniqueName) =>
        ((DeleteAndPromoteResponse)await Admin.ExecuteAsync(new DeleteAndPromoteRequest { UniqueName = uniqueName })).SolutionId;

    private async Task<Guid?> SolutionIdAsync(string uniqueName) =>
        (Guid?)await ScalarAsync("SELECT solutionid FROM data.solution WHERE uniquename = @n", ("@n", uniqueName));

    private Task<int> SolutionRowsAsync(string uniqueName) => CountAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename = @n", ("@n", uniqueName));

    private async Task<string?> VersionAsync(string uniqueName) =>
        (string?)await ScalarAsync("SELECT version FROM data.solution WHERE uniquename = @n", ("@n", uniqueName));

    /// <summary>The rank of the solution: (segment, sub) of <c>meta.solution_rank</c>.</summary>
    private async Task<(int Segment, int Sub)> RankAsync(string uniqueName)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT r.segment, r.sub FROM meta.solution_rank r JOIN data.solution s ON s.solutionid = r.solution_id WHERE s.uniquename = @n");
        cmd.AddParameter("@n", uniqueName);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"'{uniqueName}' has a rank");
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private Task<int> LayerRowsAsync(string uniqueName) => CountAsync(
        "SELECT COUNT(*) FROM meta.component_layer WHERE solution_id IN (SELECT solutionid FROM data.solution WHERE uniquename = @n)", ("@n", uniqueName));

    private Task<int> AllLayerRowsAsync() => CountAsync("SELECT COUNT(*) FROM meta.component_layer");

    private Task<int> RoleRowsAsync() => CountAsync("SELECT COUNT(*) FROM data.role WHERE name = N'xm_reader'");

    private Task<int> PipeLogRowsAsync() => CountAsync("SELECT COUNT(*) FROM data.pb_pipelog");

    private string[] Columns() => Store.GetEntity(Table)?.Attributes.Select(a => a.LogicalName).Order().ToArray() ?? [];

    private string? Label(string column) =>
        Store.GetEntity(Table)!.Attributes.Single(a => a.LogicalName == column).DisplayName.UserLocalizedLabel?.Label;

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

    private Task<HttpResponseMessage> AdminPromoteAsync(string uniqueName, bool dropData, bool dryRun) =>
        SendAsync(HttpMethod.Post, $"/admin/solutions/{uniqueName}/promote",
            $$"""{ "dropData": {{(dropData ? "true" : "false")}}, "dryRun": {{(dryRun ? "true" : "false")}} }""");

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode status)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(status, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static IEnumerable<string> Names(JsonObject body, string list) =>
        body[list]!.AsArray().Select(n => n is JsonValue v ? v.GetValue<string>() : (string)n!["name"]!);

    private AsyncOperationWorker NewWorker() =>
        new(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(), Options.Create(new PipelineOptions()));

    private async Task<(int State, int Status, string? Message)> JobAsync(Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT statecode, statuscode, message FROM data.asyncoperation WHERE asyncoperationid = @id");
        cmd.AddParameter("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <summary>The state a refused upgrade must leave as it was.</summary>
    private async Task<string> SnapshotAsync() => string.Join("|",
        await SolutionRowsAsync("xm_base"), await SolutionRowsAsync("xm_base_Upgrade"), await VersionAsync("xm_base"), await AllLayerRowsAsync(),
        string.Join(",", Columns()), await RoleRowsAsync(), await CountAsync("SELECT COUNT(*) FROM data.xm_item"),
        await CountAsync("SELECT COUNT(*) FROM data.xm_item WHERE xm_legacy IS NOT NULL"), Label("xm_name"));

    // ---- stage ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Holding_import_creates_upgrade_solution_above_base()
    {
        await ImportAsync(ManagedTestSolution.V1);

        await ImportAsync(ManagedTestSolution.V2, holding: true);

        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(1);
        (await CountAsync("SELECT CAST(ismanaged AS int) FROM data.solution WHERE uniquename = N'xm_base_Upgrade'")).Should().Be(1);
        (await VersionAsync("xm_base_Upgrade")).Should().Be("2.0.0.0");
        (await VersionAsync("xm_base")).Should().Be("1.0.0.0", "the base keeps its version until the promote");
        (await ScalarAsync("SELECT publisherid FROM data.solution WHERE uniquename = N'xm_base_Upgrade'"))
            .Should().Be(await ScalarAsync("SELECT publisherid FROM data.solution WHERE uniquename = N'xm_base'"));
        var (segment, sub) = await RankAsync("xm_base");
        sub.Should().Be(0);
        (await RankAsync("xm_base_Upgrade")).Should().Be((segment, 1023), "the holding solution sits directly above its base");
        (await CountAsync("""
            SELECT COUNT(*) FROM meta.component_layer l JOIN data.solution s ON s.solutionid = l.solution_id
            WHERE s.uniquename = N'xm_base_Upgrade' AND l.layer_order <> @order
            """, ("@order", (long)segment * 1024 + 1023))).Should().Be(0);
        (await LayerRowsAsync("xm_base_Upgrade")).Should().BeGreaterThan(0);

        // The upgrade is on top: its changes show; what it drops is still there until the promote.
        Label("xm_name").Should().Be("Title");
        Columns().Should().Contain(["xm_extra", "xm_legacy"]);
        (await RoleRowsAsync()).Should().Be(1);
        Store.GetEntity(Table)!.Attributes.Single(a => a.LogicalName == "xm_extra").IsManaged.Should().BeTrue();
    }

    /// <summary>
    /// Final review I3: in the operation's transaction, the data-loss count of a table keeps a shared table lock to the
    /// commit, so a row cannot be inserted between the count (no loss) and the drop.
    /// </summary>
    [Fact]
    public async Task Data_loss_count_blocks_inserts_until_commit()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var guard = fx.App.Services.GetRequiredService<Xrmd.Core.Solutions.DataLossGuard>();
        var table = Store.GetEntity(Table)!.MetadataId!.Value;
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();

        var losses = await guard.FindAsync(new DataSession(conn, tx), [(Xrmd.Solution.ComponentType.Entity, table)], default);

        losses.Should().BeEmpty();
        var insert = async () =>
        {
            await using var other = await TestProvider.OpenAsync(fx.ConnectionString);
            var timeout = TestProvider.IsPostgres ? "SET lock_timeout = 500" : "SET LOCK_TIMEOUT 500";
            await using var cmd = TestSql.Command(other, $"{timeout}; INSERT INTO data.{Table} (xm_itemid, xm_name) VALUES (NEWID(), N'late')");
            await cmd.ExecuteNonQueryAsync();
        };
        var refused = (await insert.Should().ThrowAsync<System.Data.Common.DbException>()).Which;
        if (refused is Npgsql.PostgresException pg) pg.SqlState.Should().Be("55P03", "the insert waits for the guard's table lock");
        else refused.Should().BeOfType<SqlException>().Which.Number.Should().Be(1222, "the insert waits for the guard's table lock");
        await tx.RollbackAsync();
        (await CountAsync($"SELECT COUNT(*) FROM data.{Table}")).Should().Be(0);
    }

    /// <summary>
    /// Final review I2: a request cannot forge the holding solution of a managed solution. Creating <c>xm_base_Upgrade</c>
    /// with <c>ismanaged</c> true is refused, and so is creating it unmanaged (or renaming a row to it); DeleteAndPromote then
    /// finds no pending upgrade and nothing of xm_base is removed.
    /// </summary>
    [Fact]
    public async Task Forged_holding_solution_is_refused_at_create()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var publisher = (Guid)(await ScalarAsync("SELECT publisherid FROM data.solution WHERE uniquename = N'xm_base'"))!;
        Entity Holding(bool? managed, string name = "xm_base_Upgrade")
        {
            var row = new Entity("solution")
            {
                ["uniquename"] = name, ["friendlyname"] = "Forged", ["version"] = "9.0.0.0",
                ["publisherid"] = new EntityReference("publisher", publisher),
            };
            if (managed is { } m) row["ismanaged"] = m;
            return row;
        }

        var fault = await FaultAsync(() => Admin.CreateAsync(Holding(true)));
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("ismanaged");
        fault = await FaultAsync(() => Admin.CreateAsync(Holding(false)));
        fault.HttpStatus.Should().Be(400, "ismanaged is set by no request, false included");
        fault = await FaultAsync(() => Admin.CreateAsync(Holding(null)));
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xm_base_Upgrade").And.Contain("holding");
        var other = await Admin.CreateAsync(Holding(null, "xm_forged"));
        fault = await FaultAsync(() => Admin.UpdateAsync(new Entity("solution", other) { ["uniquename"] = "xm_base_Upgrade" }));
        fault.HttpStatus.Should().Be(400);
        fault = await FaultAsync(() => Admin.UpdateAsync(new Entity("solution", other) { ["ismanaged"] = true }));
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("ismanaged");
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE solutionid = @id AND (ismanaged IS NULL OR ismanaged = 0)", ("@id", other))).Should().Be(1);

        var layers = await LayerRowsAsync("xm_base");
        (await BadRequestAsync(() => PromoteAsync("xm_base"))).Should().Contain("no pending upgrade");
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(0);
        (await LayerRowsAsync("xm_base")).Should().Be(layers);
        Columns().Should().Contain("xm_legacy");
    }

    /// <summary>Final review I2: the holding solution's row (managed) is not renamed by a request (ruling M14).</summary>
    [Fact]
    public async Task Holding_solution_row_is_not_renamed()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(ManagedTestSolution.V2, holding: true);
        var holding = (await SolutionIdAsync("xm_base_Upgrade"))!.Value;

        var fault = await FaultAsync(() => Admin.UpdateAsync(new Entity("solution", holding) { ["uniquename"] = "xm_renamed" }));

        fault.HttpStatus.Should().Be(400);
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(1);
    }

    [Fact]
    public async Task Holding_needs_higher_version()
    {
        await ImportAsync(ManagedTestSolution.V1);

        var fault = await FaultAsync(() => ImportAsync(ManagedTestSolution.V1, holding: true));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("HoldingSolution").And.Contain("1.0.0.0");
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(0);
    }

    [Fact]
    public async Task Second_holding_is_refused()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(ManagedTestSolution.V2, holding: true);

        var fault = await FaultAsync(() => ImportAsync(ManagedTestSolution.V2 with { Version = "3.0.0.0" }, holding: true));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("xm_base_Upgrade");
        (await VersionAsync("xm_base_Upgrade")).Should().Be("2.0.0.0");
    }

    [Fact]
    public async Task Import_while_holding_exists_is_refused()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(ManagedTestSolution.V2, holding: true);

        var update = await FaultAsync(() => ImportAsync(ManagedTestSolution.V2 with { Version = "3.0.0.0" }));
        update.HttpStatus.Should().Be(400);
        update.Message.Should().Contain("xm_base_Upgrade");

        var upgrade = await FaultAsync(() => StageAndUpgradeAsync(ManagedTestSolution.V2 with { Version = "3.0.0.0" }));
        upgrade.HttpStatus.Should().Be(400);
        upgrade.Message.Should().Contain("xm_base_Upgrade");

        (await VersionAsync("xm_base")).Should().Be("1.0.0.0");
        (await VersionAsync("xm_base_Upgrade")).Should().Be("2.0.0.0");
    }

    // ---- promote ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Promote_renames_holding_and_keeps_its_id()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var (segment, _) = await RankAsync("xm_base");
        await ImportAsync(ManagedTestSolution.V2, holding: true);
        var holdingId = (await SolutionIdAsync("xm_base_Upgrade"))!.Value;
        var holdingComponents = await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @id", ("@id", holdingId));
        var holdingLayers = await LayerRowsAsync("xm_base_Upgrade");

        var promoted = await PromoteAsync("xm_base");

        promoted.Should().Be(holdingId);
        (await SolutionIdAsync("xm_base")).Should().Be(holdingId);
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(0);
        (await VersionAsync("xm_base")).Should().Be("2.0.0.0");
        (await RankAsync("xm_base")).Should().Be((segment, 0), "the promoted solution takes its base's place");
        (await CountAsync("SELECT COUNT(*) FROM meta.component_layer WHERE solution_id = @id AND layer_order <> @order",
            ("@id", holdingId), ("@order", (long)segment * 1024))).Should().Be(0);
        (await LayerRowsAsync("xm_base")).Should().Be(holdingLayers);
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @id", ("@id", holdingId))).Should().Be(holdingComponents);
        (await CountAsync("SELECT COUNT(*) FROM meta.solution_rank")).Should().Be(1, "the base's rank is gone");

        // Without a pending upgrade there is nothing to promote.
        var again = await FaultAsync(() => PromoteAsync("xm_base"));
        again.HttpStatus.Should().Be(400);
        again.Message.Should().Contain("xm_base_Upgrade");
        (await FaultAsync(() => PromoteAsync("xm_missing"))).HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task Promote_deletes_components_missing_from_holding()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(ManagedTestSolution.V2, holding: true);
        var before = await SnapshotAsync();

        // The dry run reports the plan and writes nothing.
        var plan = await JsonAsync(await AdminPromoteAsync("xm_base", dropData: false, dryRun: true), HttpStatusCode.OK);
        Names(plan, "deleted").Should().Contain(["xm_item.xm_legacy", "xm_reader"]).And.NotContain("xm_item.xm_code");
        plan["dataLoss"]!.AsArray().Should().BeEmpty();
        (await SnapshotAsync()).Should().Be(before);

        var body = await JsonAsync(await AdminPromoteAsync("xm_base", dropData: false, dryRun: false), HttpStatusCode.OK);

        Names(body, "deleted").Should().Contain(["xm_item.xm_legacy", "xm_reader"]);
        Columns().Should().NotContain("xm_legacy").And.Contain(["xm_code", "xm_extra", "xm_name"]);
        (await RoleRowsAsync()).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.savedquery WHERE name = N'Active Items'")).Should().Be(1, "the upgrade keeps the view");
        Store.GetOptionSet("xm_status").Should().NotBeNull();
        Label("xm_name").Should().Be("Title");
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(0);
        (await VersionAsync("xm_base")).Should().Be("2.0.0.0");
        (await CountAsync("""
            SELECT COUNT(*) FROM meta.component_layer l WHERE NOT EXISTS (SELECT 1 FROM data.solution s WHERE s.solutionid = l.solution_id)
                AND l.solution_id NOT IN ('FD140AAD-4DF4-11DD-BD17-0019B9312238', 'FD140AAE-4DF4-11DD-BD17-0019B9312238')
            """)).Should().Be(0, "no layer of the old base is left");
    }

    [Fact]
    public async Task Promote_publishes_caches()
    {
        // v1 brings a step (post-operation Update of xm_item) and a role; v2 drops both.
        await ImportAsync(ManagedTestSolution.V1);
        var item = await CreateItemAsync("one");
        var logged = await PipeLogRowsAsync();
        await Admin.UpdateAsync(new Entity(Table, item) { ["xm_code"] = "a" });
        (await PipeLogRowsAsync()).Should().BeGreaterThan(logged, "v1's step fires");
        var role = (Guid)(await ScalarAsync("SELECT roleid FROM data.role WHERE name = N'xm_reader'"))!;
        (await Admin.RetrieveAsync("role", role, new ColumnSet("name"))).GetAttributeValue<string>("name").Should().Be("xm_reader");

        await StageAndUpgradeAsync(ManagedTestSolution.V2 with { Steps = [] });

        logged = await PipeLogRowsAsync();
        await Admin.UpdateAsync(new Entity(Table, item) { ["xm_code"] = "b" });
        (await PipeLogRowsAsync()).Should().Be(logged, "the step v2 dropped no longer fires, without a restart");
        (await FaultAsync(() => Admin.RetrieveAsync("role", role, new ColumnSet("name")))).HttpStatus.Should().Be(404);
        (await RoleRowsAsync()).Should().Be(0);
        Store.GetEntity(Table)!.Attributes.Should().Contain(a => a.LogicalName == "xm_extra", "the metadata cache has v2");
        Columns().Should().NotContain("xm_legacy");
    }

    [Fact]
    public async Task Upgrade_dropping_a_column_with_data_is_refused()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var item = await CreateItemAsync("one", legacy: "kept");
        var role = (Guid)(await ScalarAsync("SELECT roleid FROM data.role WHERE name = N'xm_reader'"))!;
        var before = await SnapshotAsync();

        // StageAndUpgrade: 400 naming the column and the way to confirm; nothing changes.
        var message = await BadRequestAsync(() => StageAndUpgradeAsync(ManagedTestSolution.V2));
        message.Should().Contain("xm_legacy").And.Contain("StageAndUpgrade").And.Contain("--mode stage").And.Contain("dropData")
            .And.NotContain("HoldingSolution");
        (await SnapshotAsync()).Should().Be(before);
        Columns().Should().NotContain("xm_extra");
        await AssertV1RuntimeAsync(item, role);

        // StageAndUpgradeAsync is queued (ruling R15: no upgrade at request time); its job fails with the same refusal and
        // changes nothing.
        var queued = (StageAndUpgradeAsyncResponse)await Admin.ExecuteAsync(new StageAndUpgradeAsyncRequest
        {
            CustomizationFile = ManagedTestSolution.Build(ManagedTestSolution.V2), ImportJobId = Guid.NewGuid(),
        });
        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();
        var job = await JobAsync(queued.AsyncOperationId);
        (job.State, job.Status).Should().Be((3, 31));
        job.Message.Should().Contain("xm_legacy").And.Contain("StageAndUpgrade");
        (await SnapshotAsync()).Should().Be(before);
        Columns().Should().NotContain("xm_extra");
        await AssertV1RuntimeAsync(item, role);

        // Staged, the promote is refused by the SDK (sync and queued) and by the Admin API without the confirmation.
        await ImportAsync(ManagedTestSolution.V2, holding: true);
        before = await SnapshotAsync();
        var promote = await BadRequestAsync(() => PromoteAsync("xm_base"));
        promote.Should().Contain("xm_legacy").And.Contain("DeleteAndPromote").And.NotContain("holding").And.NotContain("--mode stage");
        (await BadRequestAsync(() => Admin.ExecuteAsync(new DeleteAndPromoteAsyncRequest { UniqueName = "xm_base" }))).Should().Contain("xm_legacy");
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE operationtype = 207")).Should().Be(0);
        (await AdminPromoteAsync("xm_base", dropData: false, dryRun: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SnapshotAsync()).Should().Be(before);
        await AssertV1RuntimeAsync(item, role);
        var plan = await JsonAsync(await AdminPromoteAsync("xm_base", dropData: false, dryRun: true), HttpStatusCode.OK);
        plan["dataLoss"]!.AsArray().Should().ContainSingle().Which!["column"]!.GetValue<string>().Should().Be("xm_legacy");

        // Confirmed through the Admin API, the column and its values go.
        await JsonAsync(await AdminPromoteAsync("xm_base", dropData: true, dryRun: false), HttpStatusCode.OK);
        Columns().Should().NotContain("xm_legacy");
        (await CountAsync("SELECT COUNT(*) FROM data.xm_item")).Should().Be(1, "the rows stay, only the column goes");
        (await VersionAsync("xm_base")).Should().Be("2.0.0.0");
    }

    /// <summary>Final review M1: the Admin promote's confirmation carries the dry run's data loss; more is a 409.</summary>
    [Fact]
    public async Task Admin_promote_confirm_refuses_loss_not_shown_by_dry_run()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var item = await CreateItemAsync("one");
        await ImportAsync(ManagedTestSolution.V2, holding: true);
        var plan = await JsonAsync(await AdminPromoteAsync("xm_base", dropData: false, dryRun: true), HttpStatusCode.OK);
        plan["dataLoss"]!.AsArray().Should().BeEmpty();
        await Admin.UpdateAsync(new Entity(Table, item) { ["xm_legacy"] = "written after the dry run" });
        var before = await SnapshotAsync();

        var res = await SendAsync(HttpMethod.Post, "/admin/solutions/xm_base/promote", """{ "dropData": true, "expectedDataLoss": [] }""");

        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, text);
        text.Should().Contain("xm_item.xm_legacy");
        (await SnapshotAsync()).Should().Be(before);
        await JsonAsync(await SendAsync(HttpMethod.Post, "/admin/solutions/xm_base/promote",
            """{ "dropData": true, "expectedDataLoss": [ { "table": "xm_item", "column": "xm_legacy" } ] }"""), HttpStatusCode.OK);
        (await VersionAsync("xm_base")).Should().Be("2.0.0.0");
    }

    /// <summary>
    /// Final review M4: a promote whose plan under the lock deletes more than the plan made before the transaction (the holding
    /// solution lost a layer meanwhile) is a 409, as an uninstall's; nothing changes.
    /// </summary>
    [Fact]
    public async Task Promote_refuses_plan_changed_before_the_lock()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(ManagedTestSolution.V2, holding: true);
        var code = Store.GetEntity(Table)!.Attributes.Single(a => a.LogicalName == "xm_code").MetadataId!.Value;
        var holding = (await SolutionIdAsync("xm_base_Upgrade"))!.Value;
        var locked = new TaskCompletionSource();
        var change = new TaskCompletionSource();
        var holder = Task.Run(() => TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            await Store.BeginWriteAsync(s, default);
            locked.SetResult();
            await change.Task;
            // The upgrade no longer layers xm_code: the promote would now delete it.
            await using var cmd = s.Command("DELETE FROM meta.component_layer WHERE solution_id = @s AND object_id = @o");
            cmd.AddParameter("@s", holding);
            cmd.AddParameter("@o", code);
            (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
        }));
        await locked.Task;

        var promote = Task.Run(async () =>
        {
            try
            {
                await PromoteAsync("xm_base");
                return (XrmdFaultException?)null;
            }
            catch (XrmdFaultException fault)
            {
                return fault;
            }
        });
        // Long enough for the promote to plan outside the transaction and wait for the lock.
        await Task.Delay(TimeSpan.FromSeconds(3));
        change.SetResult();
        await holder;
        var outcome = await promote;

        outcome.Should().NotBeNull("the plan under the lock deletes xm_code, which the first plan kept");
        outcome!.HttpStatus.Should().Be(409, outcome.Message);
        outcome.Message.Should().Contain("changed while its upgrade was being applied");
        Columns().Should().Contain("xm_code");
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(1);
        (await VersionAsync("xm_base")).Should().Be("1.0.0.0");
    }

    /// <summary>After a refused upgrade, v1's step still fires and its role is still served (caches unchanged).</summary>
    private async Task AssertV1RuntimeAsync(Guid item, Guid role)
    {
        var logged = await PipeLogRowsAsync();
        await Admin.UpdateAsync(new Entity(Table, item) { ["xm_code"] = Guid.NewGuid().ToString("N")[..8] });
        (await PipeLogRowsAsync()).Should().BeGreaterThan(logged, "v1's step still fires");
        (await Admin.RetrieveAsync("role", role, new ColumnSet("name"))).GetAttributeValue<string>("name").Should().Be("xm_reader");
    }

    [Fact]
    public async Task Promote_restores_lower_layer()
    {
        // xm_other is installed first: its layer of xm_code ("Code (other)") sits below xm_base's ("Code").
        await ImportAsync(ManagedTestSolution.Other);
        await ImportAsync(ManagedTestSolution.V1);
        Label("xm_code").Should().Be("Code");
        // v2 drops xm_code (and the view's use of it).
        var v2 = ManagedTestSolution.V2 with
        {
            Tables = [new(Table, "Item", [new("xm_name", "Title", PrimaryName: true), new("xm_extra", "Extra")])],
            Views = [new("Active Items", Table, ["xm_name"])],
        };
        await ImportAsync(v2, holding: true);
        Label("xm_code").Should().Be("Code", "xm_base's layer is still the top one of xm_code");

        var body = await JsonAsync(await AdminPromoteAsync("xm_base", dropData: false, dryRun: false), HttpStatusCode.OK);

        Names(body, "restored").Should().Contain("xm_item.xm_code");
        Names(body, "deleted").Should().NotContain("xm_item.xm_code", "xm_other still holds it");
        Label("xm_code").Should().Be("Code (other)", "the lower layer shows again");
        Columns().Should().Contain("xm_code").And.NotContain("xm_legacy");
        var codeId = Store.GetEntity(Table)!.Attributes.Single(a => a.LogicalName == "xm_code").MetadataId!.Value;
        (await CountAsync("SELECT COUNT(*) FROM meta.component_layer WHERE object_id = @id", ("@id", codeId))).Should().Be(1, "only xm_other's layer is left");
    }

    [Fact]
    public async Task Holding_name_over_65_characters_is_refused()
    {
        var name = "xm_" + new string('a', 60); // 63 characters: the holding name would have 71
        await ImportAsync(ManagedTestSolution.V1 with { Name = name });

        var holding = await FaultAsync(() => ImportAsync(ManagedTestSolution.V2 with { Name = name }, holding: true));
        holding.HttpStatus.Should().Be(400);
        holding.Message.Should().Contain("65");
        var upgrade = await FaultAsync(() => StageAndUpgradeAsync(ManagedTestSolution.V2 with { Name = name }));
        upgrade.HttpStatus.Should().Be(400);
        upgrade.Message.Should().Contain("65");
        (await VersionAsync(name)).Should().Be("1.0.0.0");
    }

    // ---- StageAndUpgrade --------------------------------------------------------------------------------------------

    [Fact]
    public async Task StageAndUpgrade_on_new_solution_installs()
    {
        var id = await StageAndUpgradeAsync(ManagedTestSolution.V1);

        (await SolutionIdAsync("xm_base")).Should().Be(id);
        (await CountAsync("SELECT CAST(ismanaged AS int) FROM data.solution WHERE uniquename = N'xm_base'")).Should().Be(1);
        (await VersionAsync("xm_base")).Should().Be("1.0.0.0");
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(0);
        (await RankAsync("xm_base")).Sub.Should().Be(0);
        Columns().Should().Contain(["xm_code", "xm_legacy", "xm_name"]);

        // Then an upgrade through the Web API action: the id of the upgraded solution changes to the holding row's.
        var body = await JsonAsync(await SendAsync(HttpMethod.Post, $"{Api}StageAndUpgrade",
            new JsonObject { ["CustomizationFile"] = Convert.ToBase64String(ManagedTestSolution.Build(ManagedTestSolution.V2)) }.ToJsonString()),
            HttpStatusCode.OK);
        var upgraded = Guid.Parse(body["SolutionId"]!.GetValue<string>());
        (await SolutionIdAsync("xm_base")).Should().Be(upgraded);
        upgraded.Should().NotBe(id);
        (await VersionAsync("xm_base")).Should().Be("2.0.0.0");
        Columns().Should().NotContain("xm_legacy");

        // An unmanaged file is refused.
        (await BadRequestAsync(() => StageAndUpgradeAsync(ManagedTestSolution.V2 with { Name = "xm_plain", Managed = false })))
            .Should().Contain("managed");
    }

    [Fact]
    public async Task StageAndUpgradeAsync_queues_211()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var importJobId = Guid.NewGuid();
        var before = await SnapshotAsync();

        // The cheap checks answer the request: a version not higher is refused before anything is queued.
        var lower = await FaultAsync(() => Admin.ExecuteAsync(new StageAndUpgradeAsyncRequest { CustomizationFile = ManagedTestSolution.Build(ManagedTestSolution.V1) }));
        lower.HttpStatus.Should().Be(400);
        lower.Message.Should().Contain("StageAndUpgrade").And.NotContain("HoldingSolution");
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE operationtype = 211")).Should().Be(0);

        var response = (StageAndUpgradeAsyncResponse)await Admin.ExecuteAsync(new StageAndUpgradeAsyncRequest
        {
            CustomizationFile = ManagedTestSolution.Build(ManagedTestSolution.V2), ImportJobId = importJobId,
        });

        response.ImportJobKey.Should().Be(importJobId.ToString());
        (await SnapshotAsync()).Should().Be(before, "nothing runs at request time (ruling R15)");
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = @id AND operationtype = 211 AND statecode = 0 AND xrmverse_importjobid = @job",
            ("@id", response.AsyncOperationId), ("@job", importJobId))).Should().Be(1);
        (await VersionAsync("xm_base")).Should().Be("1.0.0.0", "queued, not run");
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.importjob WHERE importjobid = @id", ("@id", importJobId))).Should().Be(0);

        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var job = await JobAsync(response.AsyncOperationId);
        (job.State, job.Status).Should().Be((3, 30), job.Message);
        (await VersionAsync("xm_base")).Should().Be("2.0.0.0");
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(0);
        Columns().Should().NotContain("xm_legacy").And.Contain("xm_extra");
        (await CountAsync("SELECT CAST(progress AS int) FROM data.importjob WHERE importjobid = @id", ("@id", importJobId))).Should().Be(100);

        // ExecuteAsync of StageAndUpgrade queues the same job.
        var executed = (ExecuteAsyncResponse)await Admin.ExecuteAsync(new ExecuteAsyncRequest
        {
            Request = new StageAndUpgradeRequest { CustomizationFile = ManagedTestSolution.Build(ManagedTestSolution.V2 with { Version = "3.0.0.0" }) },
        });
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = @id AND operationtype = 211", ("@id", executed.AsyncJobId)))
            .Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAndPromote_queues_207()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(ManagedTestSolution.V2, holding: true);
        var holdingId = (await SolutionIdAsync("xm_base_Upgrade"))!.Value;

        var response = (ExecuteAsyncResponse)await Admin.ExecuteAsync(new ExecuteAsyncRequest
        {
            Request = new DeleteAndPromoteRequest { UniqueName = "xm_base" },
        });

        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = @id AND operationtype = 207 AND statecode = 0 AND data = N'xm_base'",
            ("@id", response.AsyncJobId))).Should().Be(1);
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(1, "queued, not run");

        (await NewWorker().RunOnceAsync(default)).Should().BeTrue();

        var job = await JobAsync(response.AsyncJobId);
        (job.State, job.Status).Should().Be((3, 30), job.Message);
        (await SolutionIdAsync("xm_base")).Should().Be(holdingId);
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(0);

        // DeleteAndPromoteAsync, through the Web API: refused before anything is queued when there is nothing to promote.
        var refused = await SendAsync(HttpMethod.Post, $"{Api}DeleteAndPromoteAsync", """{ "UniqueName": "xm_base" }""");
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE operationtype = 207")).Should().Be(1);

        await ImportAsync(ManagedTestSolution.V2 with { Version = "3.0.0.0" }, holding: true);
        var body = await JsonAsync(await SendAsync(HttpMethod.Post, $"{Api}DeleteAndPromoteAsync", """{ "UniqueName": "xm_base" }"""), HttpStatusCode.OK);
        var queued = Guid.Parse(body["AsyncOperationId"]!.GetValue<string>());
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = @id AND operationtype = 207", ("@id", queued))).Should().Be(1);
    }

    // ---- privileges and plug-ins ------------------------------------------------------------------------------------

    [Fact]
    public async Task Upgrade_without_prvImportCustomization_is_403()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(ManagedTestSolution.V2, holding: true);
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ("prvReadSolution", DepthMask.Global));
        var service = ServiceFor(fx, objectId);

        (await FaultAsync(() => service.ExecuteAsync(new DeleteAndPromoteRequest { UniqueName = "xm_base" }))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => service.ExecuteAsync(new DeleteAndPromoteAsyncRequest { UniqueName = "xm_base" }))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => service.ExecuteAsync(new StageAndUpgradeRequest
        {
            CustomizationFile = ManagedTestSolution.Build(ManagedTestSolution.V2 with { Version = "3.0.0.0" }),
        }))).HttpStatus.Should().Be(403);
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE operationtype IN (207, 211)")).Should().Be(0);
    }

    [Fact]
    public async Task Plugin_cannot_upgrade()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(ManagedTestSolution.V2, holding: true);
        var me = (WhoAmIResponse)await Admin.ExecuteAsync(new WhoAmIRequest());
        var caller = new Caller(me.UserId, me.BusinessUnitId);
        await using var scope = fx.App.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);

        foreach (var request in new OrganizationRequest[]
                 {
                     new DeleteAndPromoteRequest { UniqueName = "xm_base" }, new DeleteAndPromoteAsyncRequest { UniqueName = "xm_base" },
                     new StageAndUpgradeRequest { CustomizationFile = [1] }, new StageAndUpgradeAsyncRequest { CustomizationFile = [1] },
                 })
        {
            var fault = await FaultAsync(() => core.ExecuteNonPipelineAsync(request, caller, new DataSession(conn, null), default));
            fault.HttpStatus.Should().Be(400, request.RequestName);
            fault.Message.Should().Contain("plug-in");
        }
        (await SolutionRowsAsync("xm_base_Upgrade")).Should().Be(1);
    }
}
