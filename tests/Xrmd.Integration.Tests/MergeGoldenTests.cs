using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Sandbox;
using Xrmd.Security;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3c3a, Task 1: golden dumps of the merged messages, standard-table <c>Upsert</c> and <c>CreateMultiple</c>,
/// <c>UpdateMultiple</c> and <c>UpsertMultiple</c>, whose stage 30 runs inner single-record executions (<c>StandardUpsert</c>,
/// <c>BulkStage</c>), captured on the code before that stage moves onto the model request (plan S.2e3c3a, Review Focus 1–5). Each
/// scenario arranges its rows and steps as the administrator, then runs one request through <see cref="PipelineExecutor.ExecuteAsync"/>
/// as the System Administrator at depth 1 with no session (or, where it says so, through <see cref="OrganizationService"/>), and
/// writes the request, the fault (or <c>none</c>), the request as the caller holds it afterwards (with the identity of the value
/// instances the scenario shares), the response, the rows written, what the recorder steps saw and the payloads of the asynchronous
/// jobs the call queued. The conventions are in <c>tests/fixtures/merge-golden/README.md</c>; a missing file is written once and the
/// test fails, an existing file is never rewritten.
/// </summary>
[Collection("xrmd")]
public sealed class MergeGoldenTests(XrmdFixture fx, ITestOutputHelper output)
{
    public static TheoryData<string> Names => [.. MergeScenarios.All.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Merged_messages_do_what_the_golden_file_says(string name)
    {
        var scenario = MergeScenarios.All.Single(s => s.Name == name);
        await using var kit = await CoreScenarioKit.StartAsync(fx, scenario.OwnDatabase);
        var m = await MergeKit.StartAsync(kit);
        var call = await scenario.Arrange(m);

        var jobsBefore = await m.Stage20.AsyncOperationIdsAsync();
        await kit.ExecAsync($"DELETE FROM data.{TestEntities.PipeLog}");

        var file = new StringBuilder();
        file.Append("=== ").Append(call.Via).Append(" as ").Append(call.CallerText).Append(" ===\n");
        file.Append("--- request ---\n").Append(CoreDump.RequestText(call.Request, fixedDates: true));
        var (response, fault) = await (call.Run ?? m.RunAsAdminAsync)(call.Request);
        file.Append("--- fault ---\n").Append(fault is null ? "none\n" : CoreDump.Response(null, fault));
        file.Append("--- request after ---\n").Append(CoreDump.RequestText(call.Request, fixedDates: true));
        foreach (var slot in call.Shared ?? [])
            file.Append("same instance ").Append(slot.Label).Append(": ").Append(ReferenceEquals(slot.Read(call.Request), slot.Instance) ? "True" : "False").Append('\n');
        file.Append("--- response ---\n").Append(response is null ? "none\n" : CoreDump.Response(response, null, fixedDates: true));
        file.Append("--- rows ---\n");
        foreach (var (label, sql) in call.Rows) file.Append(await m.Stage20.RowsTextAsync(label, sql));
        file.Append("--- stage 40 ---\n").Append(await m.StepsTextAsync());
        file.Append("--- async ---\n").Append(await m.JobsTextAsync(jobsBefore));

        CoreDump.AssertGolden(name + ".txt", GuardKit.Normalize(kit, file.ToString()), MergeKit.Directory);
    }

    // ---- the base conversion counts (Step 2; pre-flight ruling F1) ---------------------------------------------------------------

    /// <summary>
    /// Prints the <see cref="SdkMirror.SyncCount"/> of the three probes on today's code (not asserted): Tasks 3 and 4 compare their
    /// counts with these, which <c>tests/fixtures/merge-golden/README.md</c> records. Each probe runs twice, on a fresh mirror each
    /// time, to show the count does not depend on what ran before.
    /// </summary>
    [Fact]
    public async Task Base_conversion_counts()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        for (var run = 1; run <= 2; run++)
        {
            output.WriteLine($"run {run}: Upsert that creates = {await UpsertCreatesProbeAsync(kit, new SdkMirror())}");
            output.WriteLine($"run {run}: CreateMultiple of 10 = {await CreateMultipleOfTenProbeAsync(kit, new SdkMirror())}");
            output.WriteLine($"run {run}: UpsertMultiple of 2 (one existing) = {await UpsertMultipleOfTwoProbeAsync(kit, new SdkMirror())}");
        }
    }

    /// <summary>
    /// The probe "Upsert that creates": a standard-table Upsert of a new <c>account</c> (its id given), with one inner Create step at
    /// 20 (<see cref="ProbeStepAsync"/>), through the executor as the administrator at depth 1 with <paramref name="mirror"/>; returns
    /// the objects that mirror wrote.
    /// </summary>
    internal static async Task<long> UpsertCreatesProbeAsync(CoreScenarioKit kit, SdkMirror mirror)
    {
        await ProbeStepAsync(kit);
        var response = await ProbeExecuteAsync(kit, new UpsertRequest
        {
            Target = new Entity("account", Guid.NewGuid()) { ["name"] = "mrg-probe-upsert" },
        }, mirror);
        if (response is not UpsertResponse { RecordCreated: true }) throw new InvalidOperationException("The probe Upsert did not create.");
        return mirror.SyncCount;
    }

    /// <summary>The probe "CreateMultiple of 10": ten new <c>account</c> rows, with the inner Create step of <see cref="ProbeStepAsync"/>.</summary>
    internal static async Task<long> CreateMultipleOfTenProbeAsync(CoreScenarioKit kit, SdkMirror mirror)
    {
        await ProbeStepAsync(kit);
        var targets = Enumerable.Range(0, 10).Select(i => new Entity("account") { ["name"] = $"mrg-probe-cm-{i}" }).ToList();
        var response = await ProbeExecuteAsync(kit, new CreateMultipleRequest { Targets = new EntityCollection(targets) { EntityName = "account" } }, mirror);
        if (((Guid[])response.Results["Ids"]).Length != 10) throw new InvalidOperationException("The probe CreateMultiple did not create 10 rows.");
        return mirror.SyncCount;
    }

    /// <summary>
    /// The probe "UpsertMultiple of 2": an existing <c>account</c> (created as the administrator before the call, outside the mirror)
    /// and a new one (its id given), with the inner Create step of <see cref="ProbeStepAsync"/> (which runs for the new one only).
    /// </summary>
    internal static async Task<long> UpsertMultipleOfTwoProbeAsync(CoreScenarioKit kit, SdkMirror mirror)
    {
        await ProbeStepAsync(kit);
        var existing = await kit.Admin.CreateAsync(new Entity("account") { ["name"] = "mrg-probe-um-old" });
        var response = await ProbeExecuteAsync(kit, new UpsertMultipleRequest
        {
            Targets = new EntityCollection(
            [
                new Entity("account", existing) { ["name"] = "mrg-probe-um-0" },
                new Entity("account", Guid.NewGuid()) { ["name"] = "mrg-probe-um-1" },
            ]) { EntityName = "account" },
        }, mirror);
        if (((UpsertMultipleResponse)response).Results.Length != 2) throw new InvalidOperationException("The probe UpsertMultiple did not run 2 targets.");
        return mirror.SyncCount;
    }

    /// <summary>
    /// The one inner step of the probes: <c>SetAttributePlugin</c> (<c>telephone1=x</c>) on PreOperation Create of <c>account</c>, as
    /// <c>PipelineModelBoundaryTests.Bulk_with_an_inner_step_converts_linearly</c>; registered once per kit.
    /// </summary>
    private static async Task ProbeStepAsync(CoreScenarioKit kit)
    {
        if (kit.Last.ContainsKey("mrg probe step")) return;
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(kit.Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(kit.Admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
        kit.Last["mrg probe step"] = await PluginTestHelper.RegisterStepAsync(kit.Admin, type, "Create", "account", 20, config: "telephone1=x");
    }

    private static async Task<OrganizationResponse> ProbeExecuteAsync(CoreScenarioKit kit, OrganizationRequest request, SdkMirror mirror)
    {
        await using var scope = kit.Services.CreateAsyncScope();
        var invocation = new InvocationContext(kit.Caller, kit.Caller.UserId, 1, null, null, Guid.NewGuid()) { Mirror = mirror };
        try
        {
            return await scope.ServiceProvider.GetRequiredService<PipelineExecutor>().ExecuteAsync(request, invocation, default);
        }
        finally
        {
            kit.Cache.Invalidate();
        }
    }
}

/// <summary>One scenario: its arrangement, which returns the call to run, and whether it needs a database of its own.</summary>
internal sealed record MergeScenario(string Name, Func<MergeKit, Task<MergeCall>> Arrange, bool OwnDatabase = false);

/// <summary>
/// The call of a scenario: the request, the queries of the rows written (<paramref name="Rows"/>: label and SQL), the value
/// instances of the request the scenario shares (<paramref name="Shared"/>), how the request runs (<paramref name="Run"/>; null: through
/// the executor as the System Administrator) and how the golden names that way and its caller.
/// </summary>
internal sealed record MergeCall(
    OrganizationRequest Request, IReadOnlyList<(string Label, string Sql)> Rows, IReadOnlyList<SharedSlot>? Shared = null,
    Func<OrganizationRequest, Task<(OrganizationResponse?, Exception?)>>? Run = null, string Via = "PipelineExecutor.ExecuteAsync",
    string CallerText = "the System Administrator");

/// <summary>A slot of the caller's request that held <paramref name="Instance"/> when it was sent, read again after the call.</summary>
internal sealed record SharedSlot(string Label, object Instance, Func<OrganizationRequest, object?> Read);

/// <summary>The scenarios of the merge goldens (plan S.2e3c3a, Task 1; pre-flight rulings F2 and F8).</summary>
internal static class MergeScenarios
{
    private const string Order = TestEntities.Order;
    private const string Name = "xrmv_name";
    private const string Status = "xrmv_status";
    private const string Amount = "xrmv_amount";
    private const int New = 128830000, NewAction = 128830001;

    public static IReadOnlyList<MergeScenario> All { get; } =
    [
        // ---- Upsert ----
        new("UpsertCreate", async g =>
        {
            await g.RecordAsync("Create", Order, 40);
            var id = g.Named(Guid.NewGuid(), "order");
            return new MergeCall(new UpsertRequest { Target = MergeKit.Order(id, "mrg upsert created") }, [MergeKit.OrderRows]);
        }),
        // The inner Update's Target is a copy without the primary id column; the caller's Target keeps it.
        new("UpsertUpdate", async g =>
        {
            var id = await g.OrderAsync("mrg upsert existing", "order");
            await g.RecordAsync("Update", Order, 40);
            var target = MergeKit.Order(id, "mrg upsert updated");
            target["xrmv_orderid"] = id;
            return new MergeCall(new UpsertRequest { Target = target }, [MergeKit.OrderRows]);
        }),
        // A calculated value and rollup values are dropped from the inner Create's Target (stage 20 sees it), kept in the caller's.
        new("UpsertCalculatedAndRollup", async g =>
        {
            await g.ComputedAccountAsync();
            await g.RecordAsync("Create", "account", 20);
            var id = g.Named(Guid.NewGuid(), "account");
            return new MergeCall(new UpsertRequest
            {
                Target = new Entity("account", id)
                {
                    ["name"] = "mrg computed", [MergeKit.CalculatedColumn] = "sent", ["xrmv_ordertotal"] = 999m, ["xrmv_ordertotal_state"] = 5,
                },
            }, [("accounts named 'mrg computed'",
                "SELECT accountid, name, xrmv_ordertotal, xrmv_ordertotal_state FROM data.account WHERE name = 'mrg computed'")]);
        }, OwnDatabase: true),
        // Key attributes stay on the inner Create's Target, whose core refuses them on a standard table.
        new("UpsertKeyAttributes", _ =>
        {
            var target = new Entity(Order) { [Name] = "mrg upsert by key" };
            target.KeyAttributes[Name] = "mrg upsert by key";
            return Task.FromResult(new MergeCall(new UpsertRequest { Target = target }, [MergeKit.OrderRows]));
        }),
        new("UpsertWithoutTarget", _ => Task.FromResult(new MergeCall(new UpsertRequest(), [MergeKit.OrderRows]))),
        // The inner request carries the Upsert's optional parameters.
        new("UpsertSuppressDuplicateDetection", async g =>
        {
            await g.RecordAsync("Create", Order, 40);
            var id = g.Named(Guid.NewGuid(), "order");
            return new MergeCall(new UpsertRequest { Target = MergeKit.Order(id, "mrg upsert suppressed"), ["SuppressDuplicateDetection"] = true },
                [MergeKit.OrderRows]);
        }),
        new("UpsertInnerStepThrows", async g =>
        {
            await g.StepAsync("ThrowPlugin", "Create", Order, 20, "invalid:refused by the inner step");
            var id = g.Named(Guid.NewGuid(), "order");
            return new MergeCall(new UpsertRequest { Target = MergeKit.Order(id, "mrg upsert refused") }, [MergeKit.OrderRows]);
        }),
        // Review Focus 1: an inner PreOperation step changes in place the OptionSetValue the caller's Target holds.
        new("UpsertInnerStepChangesOptionInPlace", async g =>
        {
            await g.StepAsync("InPlaceOptionPlugin", "Create", Order, 20, $"{Status}={NewAction}");
            await g.RecordAsync("Create", Order, 40);
            var id = g.Named(Guid.NewGuid(), "order");
            var target = MergeKit.Order(id, "mrg upsert in place");
            var status = target[Status]!;
            return new MergeCall(new UpsertRequest { Target = target }, [MergeKit.OrderRows],
                [new SharedSlot($"Target[{Status}]", status, r => ((Entity)r["Target"])[Status]), new SharedSlot("Target", target, r => r["Target"])]);
        }),
        // Review Focus 4: an existing row answers 412 after the lock; the caller's Target is unchanged.
        new("UpsertCreateOnlyExisting", async g =>
        {
            var id = await g.OrderAsync("mrg create-only existing", "order");
            var target = MergeKit.Order(id, "mrg create-only sent");
            return new MergeCall(new UpsertRequest { Target = target }, [MergeKit.OrderRows],
                Run: r => MergeKit.CatchAsync(() => g.Kit.Admin.UpsertAsync((Entity)r["Target"], createOnly: true)),
                Via: "OrganizationService.UpsertAsync(target, createOnly: true)");
        }),
        // Review Focus 4: a caller without Create privilege answers 403 first, even for an existing row.
        new("UpsertCreateOnlyWithoutCreatePrivilege", async g =>
        {
            var id = await g.OrderAsync("mrg create-only existing", "order");
            var (service, text) = await g.UserAsync(("prvReadxrmv_Order", DepthMask.Global), ("prvWritexrmv_Order", DepthMask.Global));
            var target = MergeKit.Order(id, "mrg create-only sent");
            return new MergeCall(new UpsertRequest { Target = target }, [MergeKit.OrderRows],
                Run: r => MergeKit.CatchAsync(() => service.UpsertAsync((Entity)r["Target"], createOnly: true)),
                Via: "OrganizationService.UpsertAsync(target, createOnly: true)", CallerText: text);
        }),
        // Review Focus 5: the payload of an asynchronous step on the inner Update.
        new("UpsertAsyncStepOnInnerUpdate", async g =>
        {
            var id = await g.OrderAsync("mrg upsert existing", "order");
            await g.AsyncStepAsync("Update", Order);
            return new MergeCall(new UpsertRequest { Target = MergeKit.Order(id, "mrg upsert updated async") }, [MergeKit.OrderRows]);
        }),

        // ---- CreateMultiple ----
        new("CreateMultipleTwoRows", async g =>
        {
            await g.RecordAsync("Create", Order, 40);
            await g.RecordAsync("CreateMultiple", Order, 40);
            return new MergeCall(new CreateMultipleRequest
            {
                Targets = MergeKit.Orders(MergeKit.Order(null, "mrg cm 1"), MergeKit.Order(null, "mrg cm 2")),
            }, [MergeKit.OrderRows]);
        }),
        // Review Focus 1: three rows share one OptionSetValue; the inner PreOperation step of the first row changes it in place, and
        // the recorder of each inner Create reads ParentContext.
        new("CreateMultipleSharedOptionChangedInPlace", async g =>
        {
            await g.StepAsync("InPlaceOptionPlugin", "Create", Order, 20, $"{Status}={NewAction}|{Name}=mrg cm shared 1");
            await g.RecordAsync("Create", Order, 40);
            await g.RecordAsync("CreateMultiple", Order, 40);
            var shared = new OptionSetValue(New);
            var rows = Enumerable.Range(1, 3).Select(i => new Entity(Order) { [Name] = $"mrg cm shared {i}", [Status] = shared }).ToArray();
            return new MergeCall(new CreateMultipleRequest { Targets = MergeKit.Orders(rows) }, [MergeKit.OrderRows],
            [
                .. rows.SelectMany((row, i) => new[]
                {
                    new SharedSlot($"Targets[{i}]", row, r => ((EntityCollection)r["Targets"]).Entities.ElementAtOrDefault(i)),
                    new SharedSlot($"Targets[{i}][{Status}]", shared,
                        r => ((EntityCollection)r["Targets"]).Entities.ElementAtOrDefault(i)?.GetAttributeValue<object>(Status)),
                }),
            ]);
        }),
        // Review Focus 5: an asynchronous step on the inner Create and one on CreateMultiple.
        new("CreateMultipleAsyncSteps", async g =>
        {
            await g.AsyncStepAsync("Create", Order);
            await g.AsyncStepAsync("CreateMultiple", Order);
            return new MergeCall(new CreateMultipleRequest
            {
                Targets = MergeKit.Orders(MergeKit.Order(null, "mrg cm async 1"), MergeKit.Order(null, "mrg cm async 2")),
            }, [MergeKit.OrderRows]);
        }),
        // Review Focus 5: early-bound Targets (the SDK fallback of the model) with an asynchronous inner step.
        new("CreateMultipleEarlyBoundAsyncStep", async g =>
        {
            await g.AsyncStepAsync("Create", TestEntities.Pipe);
            var rows = new[] { g.EarlyBoundPipe("mrg early 1"), g.EarlyBoundPipe("mrg early 2") };
            return new MergeCall(new CreateMultipleRequest { Targets = new EntityCollection([.. rows]) { EntityName = TestEntities.Pipe } },
                [MergeKit.PipeRows], [.. rows.Select((row, i) => new SharedSlot($"Targets[{i}]", row,
                    r => ((EntityCollection)r["Targets"]).Entities.ElementAtOrDefault(i)))]);
        }),

        // ---- UpdateMultiple ----
        // A repeated id is skipped (the first target naming it wins).
        new("UpdateMultipleRepeatedId", async g =>
        {
            var (a, b) = await g.TwoOrdersAsync();
            await g.RecordAsync("Update", Order, 40);
            return new MergeCall(new UpdateMultipleRequest
            {
                Targets = MergeKit.Orders(MergeKit.Order(a, "mrg um a changed"), MergeKit.Order(b, "mrg um b changed"), MergeKit.Order(a, "mrg um a repeated")),
            }, [MergeKit.OrderRows]);
        }),
        // The image collections of the steps on UpdateMultiple: pre images at 20, pre and post images at 40.
        new("UpdateMultipleImages", async g =>
        {
            var (a, b) = await g.TwoOrdersAsync();
            await g.ImagesAsync();
            return new MergeCall(new UpdateMultipleRequest
            {
                Targets = MergeKit.Orders(MergeKit.Order(a, "mrg um a changed"), MergeKit.Order(b, "mrg um b changed")),
            }, [MergeKit.OrderRows]);
        }),
        // Ruling F2: a PreOperation step on UpdateMultiple changes the second target's id to a third existing row; the post image
        // collection follows Targets as the steps left them.
        new("UpdateMultipleImagesTargetIdChanged", async g =>
        {
            var (a, b) = await g.TwoOrdersAsync();
            var c = await g.OrderAsync("mrg um c", "c");
            await g.StepAsync("TargetsEditPlugin", "UpdateMultiple", Order, 20, $"setid:1:{c}");
            await g.ImagesAsync(rank: 2);
            return new MergeCall(new UpdateMultipleRequest
            {
                Targets = MergeKit.Orders(MergeKit.Order(a, "mrg um a changed"), MergeKit.Order(b, "mrg um b changed")),
            }, [MergeKit.OrderRows]);
        }),
        // Review Focus 2: a PreOperation step on UpdateMultiple empties Targets.
        new("UpdateMultipleStepEmptiesTargets", g => g.EditedUpdateMultipleAsync("empty")),
        // Review Focus 2: a PreOperation step on UpdateMultiple adds a target of another table.
        new("UpdateMultipleStepAddsOtherTable", g => g.EditedUpdateMultipleAsync($"add:{TestEntities.Pipe}:pb_name=mrg added")),
        // Review Focus 2: a PreOperation step on UpdateMultiple repeats the first target's id (skipped again).
        new("UpdateMultipleStepRepeatsId", g => g.EditedUpdateMultipleAsync($"repeat:{Name}=mrg um a repeated")),
        // Ruling F2: a PreOperation step on UpdateMultiple removes the second target.
        new("UpdateMultipleStepRemovesSecond", g => g.EditedUpdateMultipleAsync("removesecond")),

        // ---- UpsertMultiple ----
        new("UpsertMultipleUpdateAndCreate", async g =>
        {
            var a = await g.OrderAsync("mrg ums existing", "a");
            await g.RecordAsync("Create", Order, 40);
            await g.RecordAsync("Update", Order, 40);
            var b = g.Named(Guid.NewGuid(), "b");
            return new MergeCall(new UpsertMultipleRequest
            {
                Targets = MergeKit.Orders(MergeKit.Order(a, "mrg ums updated"), MergeKit.Order(b, "mrg ums created")),
            }, [MergeKit.OrderRows]);
        }),
        // Review Focus 3: the second target fails in its inner PreOperation step; nothing is stored.
        new("UpsertMultipleSecondTargetFails", async g =>
        {
            var a = await g.OrderAsync("mrg ums existing", "a");
            await g.StepAsync("ThrowOnValuePlugin", "Create", Order, 20, $"{Name}=mrg ums refused|refused by the inner step");
            await g.RecordAsync("Update", Order, 40);
            var b = g.Named(Guid.NewGuid(), "b");
            var rows = new[] { MergeKit.Order(a, "mrg ums updated"), MergeKit.Order(b, "mrg ums refused") };
            return new MergeCall(new UpsertMultipleRequest { Targets = MergeKit.Orders(rows) }, [MergeKit.OrderRows],
            [
                .. rows.SelectMany((row, i) => new[]
                {
                    new SharedSlot($"Targets[{i}]", row, r => ((EntityCollection)r["Targets"]).Entities.ElementAtOrDefault(i)),
                    new SharedSlot($"Targets[{i}][{Status}]", row[Status]!,
                        r => ((EntityCollection)r["Targets"]).Entities.ElementAtOrDefault(i)?.GetAttributeValue<object>(Status)),
                }),
            ]);
        }),

        // ---- DeleteMultiple ----
        // A standard table: OrganizationService refuses it with Dataverse's text; it never reaches the pipeline as a bulk message.
        new("DeleteMultipleStandardTable", async g =>
        {
            var (a, _) = await g.TwoOrdersAsync();
            var request = new OrganizationRequest("DeleteMultiple") { ["Targets"] = new EntityReferenceCollection { new EntityReference(Order, a) } };
            return new MergeCall(request, [MergeKit.OrderRows], Run: r => MergeKit.CatchAsync(() => g.Kit.Admin.ExecuteAsync(r)),
                Via: "OrganizationService.ExecuteAsync");
        }),
    ];
}

/// <summary>
/// What the merge scenarios need beyond <see cref="CoreScenarioKit"/>: the order table <c>xrmv_order</c> (rows named <c>mrg ...</c>,
/// deleted now and on dispose), the steps of the test plug-in assembly, the recorder (<c>MergeRecorderPlugin</c>, into
/// <c>pb_pipelog</c>) and its dump, the dump of the asynchronous payloads, a caller without Create privilege, the computed columns
/// of <c>account</c> (own database) and the early-bound <c>pb_pipe</c>. Fixed names only.
/// </summary>
internal sealed class MergeKit
{
    public const string Directory = "merge-golden";
    public const string CalculatedColumn = "xrmv_mrgcode";

    public static readonly (string, string) OrderRows =
        ("orders named 'mrg ...'", $"SELECT xrmv_orderid, xrmv_name, xrmv_status, xrmv_amount FROM data.{TestEntities.Order} WHERE xrmv_name LIKE 'mrg %' ORDER BY xrmv_name");

    public static readonly (string, string) PipeRows =
        ("pipes named 'mrg ...'", $"SELECT pb_name FROM data.{TestEntities.Pipe} WHERE pb_name LIKE 'mrg %' ORDER BY pb_name");

    private static readonly Regex PayloadDate =
        new(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?|\\/Date\(-?\d+([+-]\d{4})?\)\\/");

    private static readonly Regex PayloadRowVersion = new(@"(<RowVersion>|""RowVersion"":"")[^<""]*");

    /// <summary>The id under which <see cref="EarlyBoundPipe"/> asks the loader for the early-bound assembly (no registration has it).</summary>
    private static readonly Guid EarlyBoundLoadId = new("6d726700-0000-4000-8000-0000000eb001");

    private readonly Dictionary<string, Guid> _types = [];

    private MergeKit(CoreScenarioKit kit)
    {
        Kit = kit;
        Stage20 = new Stage20Kit(kit);
    }

    public CoreScenarioKit Kit { get; }

    /// <summary>The row and job readers of the stage-20 goldens, used unchanged.</summary>
    public Stage20Kit Stage20 { get; }

    public static async Task<MergeKit> StartAsync(CoreScenarioKit kit)
    {
        await kit.EnsurePipeAsync();
        await TestEntities.EnsureOrderAsync(kit.Store);
        await kit.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        const string clear = "DELETE FROM data.xrmv_order WHERE xrmv_name LIKE 'mrg %'; DELETE FROM data.pb_pipe WHERE pb_name LIKE 'mrg %';";
        await kit.ExecAsync(clear);
        kit.OnCleanup(() => kit.ExecAsync(clear + $" DELETE FROM data.{TestEntities.PipeLog};"));
        return new MergeKit(kit);
    }

    // ---- requests and rows ----

    public static Entity Order(Guid? id, string name) => new(TestEntities.Order, id ?? Guid.Empty)
    {
        ["xrmv_name"] = name, ["xrmv_status"] = new OptionSetValue(128830000), ["xrmv_amount"] = 12.5m,
    };

    public static EntityCollection Orders(params Entity[] rows) => new([.. rows]) { EntityName = TestEntities.Order };

    /// <summary>Names <paramref name="id"/> <c>{a:label}</c>; returns it.</summary>
    public Guid Named(Guid id, string label)
    {
        Kit.Name(id, label);
        return id;
    }

    /// <summary>An order named <paramref name="name"/> created as the administrator, named <c>{a:label}</c>.</summary>
    public async Task<Guid> OrderAsync(string name, string label) =>
        Named(await Kit.Admin.CreateAsync(new Entity(TestEntities.Order) { ["xrmv_name"] = name, ["xrmv_status"] = new OptionSetValue(128830000) }), label);

    public async Task<(Guid A, Guid B)> TwoOrdersAsync() => (await OrderAsync("mrg um a", "a"), await OrderAsync("mrg um b", "b"));

    /// <summary>
    /// Two orders, a <c>TargetsEditPlugin</c> step with <paramref name="edit"/> on PreOperation UpdateMultiple (rank 1) and the recorder
    /// on PreOperation UpdateMultiple (rank 2) and on PostOperation Update; the UpdateMultiple of both.
    /// </summary>
    public async Task<MergeCall> EditedUpdateMultipleAsync(string edit)
    {
        var (a, b) = await TwoOrdersAsync();
        await StepAsync("TargetsEditPlugin", "UpdateMultiple", TestEntities.Order, 20, edit);
        await RecordAsync("UpdateMultiple", TestEntities.Order, 20, rank: 2);
        await RecordAsync("Update", TestEntities.Order, 40);
        return new MergeCall(new UpdateMultipleRequest
        {
            Targets = Orders(Order(a, "mrg um a changed"), Order(b, "mrg um b changed")),
        }, [OrderRows]);
    }

    /// <summary>The recorder on PreOperation UpdateMultiple with a pre image, and on PostOperation UpdateMultiple with a pre and a post image.</summary>
    public async Task ImagesAsync(int rank = 1)
    {
        var pre = await RecordAsync("UpdateMultiple", TestEntities.Order, 20, rank);
        await PluginTestHelper.RegisterImageAsync(Kit.Admin, pre, 0, "pre", "xrmv_name,xrmv_status");
        var post = await RecordAsync("UpdateMultiple", TestEntities.Order, 40);
        await PluginTestHelper.RegisterImageAsync(Kit.Admin, post, 0, "pre", "xrmv_name");
        await PluginTestHelper.RegisterImageAsync(Kit.Admin, post, 1, "post", "xrmv_name,xrmv_status");
    }

    // ---- runs ----

    /// <summary>Through the executor as the System Administrator at depth 1 with no session (the pipeline opens one).</summary>
    public async Task<(OrganizationResponse?, Exception?)> RunAsAdminAsync(OrganizationRequest request)
    {
        var (response, fault) = await CoreScenarios.ViaPipelineAsync(Kit, request);
        return (response, fault);
    }

    public static async Task<(OrganizationResponse?, Exception?)> CatchAsync<T>(Func<Task<T>> call) where T : OrganizationResponse
    {
        try
        {
            return (await call(), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    /// <summary>A user in the root business unit with only a role holding <paramref name="privileges"/>, named <c>{a:caller}</c>, and its service.</summary>
    public async Task<(OrganizationService Service, string Text)> UserAsync(params (string Privilege, DepthMask Depth)[] privileges)
    {
        var oid = Guid.NewGuid();
        var root = await SecurityTestHelper.RootBusinessUnitAsync(Kit.ConnectionString);
        var id = await SecurityTestHelper.CreateUserAsync(Kit.ConnectionString, Kit.Cache, root, oid, userName: "mrg caller");
        Kit.Name(id, "caller");
        await SecurityTestHelper.AssignRoleAsync(Kit.ConnectionString, Kit.Cache, id,
            await SecurityTestHelper.CreateRoleAsync(Kit.ConnectionString, Kit.Cache, "mrg caller grant", privileges));
        return (TestServices.NewOrganizationService(Kit.Services, oid),
            "a user with only " + string.Join(", ", privileges.Select(p => p.Privilege + " " + p.Depth)));
    }

    // ---- steps ----

    /// <summary>A step of <c>Xrmd.TestPlugins.{plugin}</c>, synchronous unless <paramref name="mode"/> is 1.</summary>
    public async Task<Guid> StepAsync(string plugin, string message, string table, int stage, string? config = null, int mode = 0, int rank = 1)
    {
        if (!_types.TryGetValue(plugin, out var type))
        {
            var assembly = await PluginTestHelper.EnsureAssemblyAsync(Kit.Admin, PluginTestHelper.TestPluginBytes);
            _types[plugin] = type = await PluginTestHelper.RegisterTypeAsync(Kit.Admin, assembly, "Xrmd.TestPlugins." + plugin);
        }
        return await PluginTestHelper.RegisterStepAsync(Kit.Admin, type, message, table, stage, mode, rank, config);
    }

    /// <summary>The recorder (<c>MergeRecorderPlugin</c> into <c>pb_pipelog</c>), synchronous.</summary>
    public Task<Guid> RecordAsync(string message, string table, int stage, int rank = 1) =>
        StepAsync("MergeRecorderPlugin", message, table, stage, TestEntities.PipeLog, rank: rank);

    /// <summary>An asynchronous PostOperation step (<c>SetAttributePlugin</c>, which changes nothing stored) whose payload is dumped.</summary>
    public Task<Guid> AsyncStepAsync(string message, string table) =>
        StepAsync("SetAttributePlugin", message, table, 40, "description=async", mode: 1);

    /// <summary>
    /// What the recorder steps saw, in the order they ran: per step <c>step Message|Stage|Mode|Depth</c> and the recorder's lines.
    /// </summary>
    public async Task<string> StepsTextAsync()
    {
        var steps = new List<(int Seq, string Text)>();
        await using (var conn = await TestProvider.OpenAsync(Kit.ConnectionString))
        await using (var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data FROM data.{TestEntities.PipeLog}"))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync())
            {
                var data = rd.GetString(1);
                var first = data.IndexOf('\n');
                var seq = int.Parse(data[..first]["seq=".Length..], CultureInfo.InvariantCulture);
                steps.Add((seq, "step " + rd.GetString(0) + "\n" + data[(first + 1)..] + "\n"));
            }
        return steps.Count == 0 ? "none\n" : string.Concat(steps.OrderBy(s => s.Seq).Select(s => s.Text));
    }

    /// <summary>
    /// The <c>asyncoperation.data</c> of each job the call queued (not in <paramref name="jobsBefore"/>), one per line, its dates
    /// written <c>{date}</c> and its row versions <c>{rowversion}</c> (the ids are named with the rest of the file), ordered by that
    /// text with every unnamed id masked; the jobs are deleted afterwards.
    /// </summary>
    public async Task<string> JobsTextAsync(HashSet<Guid> jobsBefore)
    {
        var jobs = new List<(Guid Id, string Data)>();
        await using (var conn = await TestProvider.OpenAsync(Kit.ConnectionString))
        await using (var cmd = TestSql.Command(conn, "SELECT asyncoperationid, data FROM data.asyncoperation"))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync())
            {
                var id = rd.GetGuid(0);
                if (jobsBefore.Contains(id)) continue;
                var data = rd.IsDBNull(1) ? "null" : rd.GetString(1);
                data = PayloadRowVersion.Replace(PayloadDate.Replace(data, "{date}"), m => m.Groups[1].Value + "{rowversion}");
                jobs.Add((id, data));
            }
        foreach (var (id, _) in jobs) await Kit.ExecAsync($"DELETE FROM data.asyncoperation WHERE asyncoperationid = '{id}'");
        return jobs.Count == 0
            ? "none\n"
            : string.Concat(jobs.Select(j => j.Data).OrderBy(Kit.Mask, StringComparer.Ordinal).Select(d => "job: " + d + "\n"));
    }

    // ---- computed columns (own database) ----

    /// <summary>
    /// On the kit's own database: the rollup <c>xrmv_ordertotal</c> of <c>account</c> (<see cref="CoreScenarioKit.RollupAsync"/>) and
    /// the calculated <see cref="CalculatedColumn"/> (the <c>trim.xaml</c> fixture over <c>name</c>).
    /// </summary>
    public async Task ComputedAccountAsync()
    {
        await Kit.RollupAsync();
        var xaml = File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "calculated", "trim.xaml"))
            .Replace("xrmv_calcorder", "account").Replace("\"xrmv_name\"", "\"name\"").Replace("xrmv_code", CalculatedColumn);
        var column = new StringAttributeMetadata
        {
            LogicalName = CalculatedColumn, SchemaName = CalculatedColumn, MaxLength = 100, SourceType = ComputedColumnRules.Calculated,
            DisplayName = SystemAttributes.CreateLabel(CalculatedColumn, 1033),
        };
        ComputedColumnRules.SetFormula(column, xaml);
        await ((MetadataStore)Kit.Store).AddAttributeAsync("account", column);
    }

    // ---- early-bound ----

    /// <summary>
    /// A new early-bound <c>Xrmd.TestPlugins.EarlyBound.Pipe</c> named <paramref name="name"/>, of the process's one copy of that
    /// assembly (<see cref="PluginAssemblyLoader"/> keys its copies by content, so the pipeline's steps get the same one).
    /// </summary>
    public Entity EarlyBoundPipe(string name)
    {
        var assembly = Kit.Services.GetRequiredService<PluginAssemblyLoader>().Load(EarlyBoundLoadId, 0, () => PluginTestHelper.EarlyBoundPluginBytes);
        var pipe = (Entity)Activator.CreateInstance(assembly.GetType("Xrmd.TestPlugins.EarlyBound.Pipe", throwOnError: true)!)!;
        pipe["pb_name"] = name;
        return pipe;
    }
}
