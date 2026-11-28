using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Pipeline;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.Workflows;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3c3a, Tasks 3 and 4: the merged messages (standard-table <c>Upsert</c> and the bulk messages) run their stage 30 on the
/// model request. These probes pin what moves (no held window, inner requests assembled from the SDK twins the chain mirror holds,
/// fewer conversions) and what must not change (today's typed response, the create-only faults); the merge goldens
/// (<see cref="MergeGoldenTests"/>) pin the rest.
/// </summary>
[Collection("xrmd")]
public sealed class MergeModelTests(XrmdFixture fx)
{
    private const string Order = TestEntities.Order;
    private const string Project = TestEntities.Project;
    private const string DumpLog = TestEntities.PipeLog2;

    /// <summary>The probe "Upsert that creates" on the base commit (<c>tests/fixtures/merge-golden/README.md</c>).</summary>
    private const long UpsertCreatesBase = 18;

    /// <summary>The probe "CreateMultiple of 10" on the base commit, unchanged by Task 3 (<c>tests/fixtures/merge-golden/README.md</c>).</summary>
    private const long CreateMultipleOfTenBase = 156;

    /// <summary>
    /// The probe "UpsertMultiple of 2" after Task 3 (pre-flight ruling F6): its base count 39 less the window's <c>Out</c> of each of the
    /// two inner Upserts (2 each), which Task 3 removed.
    /// </summary>
    private const long UpsertMultipleOfTwoAfterTask3 = 35;

    // ---- Task 3: the standard Upsert ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Decision 7: an Upsert opens no held window at the core (<see cref="CoreHandOff.Held"/> never fires), whether it creates, updates
    /// or refuses a create-only write of an existing row (412); <c>ExecuteWorkflow</c> keeps its window (pre-flight ruling F14: whatever
    /// its fault, only that the window opened is asserted).
    /// </summary>
    [Fact]
    public async Task Upsert_holds_no_window()
    {
        var held = new List<string?>();
        await using (var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false))
        {
            var m = await MergeKit.StartAsync(kit);
            var existing = await m.OrderAsync("mrg upsert existing", "order");
            CoreHandOff.Held.Value = r => held.Add(r.Name);
            try
            {
                var (created, createFault) = await CoreScenarios.ViaPipelineAsync(kit, new UpsertRequest { Target = MergeKit.Order(Guid.NewGuid(), "mrg upsert new") });
                createFault.Should().BeNull();
                ((UpsertResponse)created!).RecordCreated.Should().BeTrue();
                var (updated, updateFault) = await CoreScenarios.ViaPipelineAsync(kit, new UpsertRequest { Target = MergeKit.Order(existing, "mrg upsert updated") });
                updateFault.Should().BeNull();
                ((UpsertResponse)updated!).RecordCreated.Should().BeFalse();
                var duplicate = await FluentActions.Awaiting(() => kit.Admin.UpsertAsync(MergeKit.Order(existing, "mrg upsert again"), createOnly: true))
                    .Should().ThrowAsync<XrmdFaultException>();
                duplicate.Which.HttpStatus.Should().Be(412);
            }
            finally
            {
                CoreHandOff.Held.Value = null;
            }
            held.Should().BeEmpty("an Upsert and its inner Create or Update run on the model request");
        }

        // The scenario's process does not exist, so the guard refuses it (404) before stage 20 on today's code too: its WorkflowId
        // names a saved Draft definition instead, which the guard lets through and the workflows' hook then refuses at stage 30.
        var scenario = CoreScenarios.All.Single(s => s.Message == "ExecuteWorkflow");
        await using (var kit = await CoreScenarioKit.StartAsync(fx, scenario.OwnDatabase))
        {
            var request = (ExecuteWorkflowRequest)await scenario.Arrange(kit);
            var draft = await WorkflowTestHelper.SaveAsync(fx, WorkflowTestHelper.Realtime("mrgm draft " + Guid.NewGuid().ToString("N")[..8],
                WorkflowFixtures.Xaml("realtime-create-update"), TestEntities.Pipe));
            kit.OnCleanup(async () =>
            {
                await kit.ExecAsync($"DELETE FROM data.{WorkflowSchema.Table} WHERE {WorkflowSchema.WorkflowId} = '{draft.Id}'");
                kit.Services.GetRequiredService<WorkflowRegistry>().Invalidate();
            });
            request.WorkflowId = draft.Id;
            CoreHandOff.Held.Value = r => held.Add(r.Name);
            try
            {
                await CoreScenarios.ViaPipelineAsync(kit, request);
            }
            finally
            {
                CoreHandOff.Held.Value = null;
            }
            held.Should().Contain("ExecuteWorkflow", "ExecuteWorkflow keeps its window (S.2h)");
        }
    }

    /// <summary>
    /// Decision 6: the inner request of an Upsert is assembled from the SDK twins the chain mirror holds, as the SDK <c>Copy</c> built it.
    /// The request the <see cref="PipelineExecutor.InnerRequest"/> seam sees is a <see cref="CreateRequest"/> (an
    /// <see cref="UpdateRequest"/> for an existing row) whose Target holds, key by key in the caller's order (without the primary id
    /// column on an Update), the caller's own instances: an <see cref="OptionSetValue"/>, a <see cref="Money"/>, an
    /// <see cref="EntityReference"/> and a <c>string</c>. A PreOperation step on the inner message (<c>ContextDumpPlugin</c>, which
    /// writes an identity token after each instance) sees the same instances in its Target as in its <c>ParentContext</c>'s, which is
    /// the caller's Target.
    /// </summary>
    [Fact]
    public async Task Upsert_inner_request_is_built_from_twins()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await TestEntities.EnsureProjectAsync(kit.Store);
        var m = await MergeKit.StartAsync(kit);
        var clear = $"DELETE FROM data.{DumpLog}; DELETE FROM data.{Project} WHERE pb_name LIKE 'mrgm %';";
        await kit.ExecAsync(clear);
        kit.OnCleanup(() => kit.ExecAsync(clear));
        await m.StepAsync("ContextDumpPlugin", "Create", Project, 20, DumpLog);
        await m.StepAsync("ContextDumpPlugin", "Update", Project, 20, DumpLog);
        var existing = await kit.Admin.CreateAsync(new Entity(Project) { ["pb_name"] = "mrgm existing" });
        await kit.ExecAsync($"DELETE FROM data.{DumpLog}");

        var create = new Entity(Project, Guid.NewGuid())
        {
            ["pb_name"] = new string("mrgm created".ToCharArray()), ["pb_priority"] = new OptionSetValue(2), ["pb_budget"] = new Money(10m),
            ["pb_customerid"] = new EntityReference("systemuser", kit.Caller.UserId),
        };
        await AssertBuiltFromTwinsAsync(kit, create, typeof(CreateRequest), [.. create.Attributes.Keys], "Create|20|0|1");

        var update = new Entity(Project, existing)
        {
            ["pb_name"] = new string("mrgm updated".ToCharArray()), [Project + "id"] = existing, ["pb_priority"] = new OptionSetValue(3),
            ["pb_budget"] = new Money(20m), ["pb_customerid"] = new EntityReference("systemuser", kit.Caller.UserId),
        };
        await AssertBuiltFromTwinsAsync(kit, update, typeof(UpdateRequest), ["pb_name", "pb_priority", "pb_budget", "pb_customerid"], "Update|20|0|1");
    }

    /// <summary>
    /// Runs an Upsert of <paramref name="target"/> and checks the inner request the seam saw (its type, its Target's keys in order and
    /// the identity of each value with the caller's) and the identity tokens the inner PreOperation step <paramref name="step"/> dumped.
    /// </summary>
    private static async Task AssertBuiltFromTwinsAsync(CoreScenarioKit kit, Entity target, Type innerType, string[] keys, string step)
    {
        var seen = new List<(Type Type, List<(string Key, bool Same)> Values)>();
        PipelineExecutor.InnerRequest.Value = r =>
        {
            var inner = (Entity)r.Parameters["Target"];
            seen.Add((r.GetType(), [.. inner.Attributes.Select(a => (a.Key, ReferenceEquals(a.Value, target[a.Key])))]));
        };
        try
        {
            (await CoreScenarios.ViaPipelineAsync(kit, new UpsertRequest { Target = target })).Fault.Should().BeNull();
        }
        finally
        {
            PipelineExecutor.InnerRequest.Value = null;
        }

        seen.Should().ContainSingle();
        seen[0].Type.Should().Be(innerType);
        seen[0].Values.Select(v => v.Key).Should().Equal(keys, "the caller's column order");
        seen[0].Values.Should().OnlyContain(v => v.Same, "every value is the caller's instance");

        var dump = (string)(await kit.ScalarAsync($"SELECT pb_data FROM data.{DumpLog} WHERE pb_name = '{step}'"))!;
        await kit.ExecAsync($"DELETE FROM data.{DumpLog}");
        var parentAt = dump.IndexOf("\n  ParentContext: Context ", StringComparison.Ordinal);
        parentAt.Should().BePositive("the dump holds the parent context");
        var own = AttributeTokens(dump[..parentAt], 6);
        var parent = AttributeTokens(dump[parentAt..], 8);
        foreach (var key in new[] { "pb_priority", "pb_budget", "pb_customerid" })
        {
            own[key].Should().StartWith("@obj", key);
            own[key].Should().Be(parent[key], $"the step's {key} is the caller's instance");
        }
        own.Keys.Should().BeEquivalentTo(keys, "the step's Target holds the keys of the inner request");
    }

    /// <summary>
    /// The identity tokens (or the scalar text) of the attributes of the first entity whose <c>Attributes</c> line is indented by
    /// <paramref name="indent"/> spaces in a <c>ContextDumpPlugin</c> dump.
    /// </summary>
    private static Dictionary<string, string> AttributeTokens(string dump, int indent)
    {
        var lines = dump.Split('\n');
        var head = new string(' ', indent) + "Attributes: AttributeCollection";
        var start = Array.FindIndex(lines, l => l.StartsWith(head, StringComparison.Ordinal));
        start.Should().BeGreaterThanOrEqualTo(0, "the dump holds a Target");
        var child = new string(' ', indent + 2);
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = start + 1; i < lines.Length && lines[i].StartsWith(child, StringComparison.Ordinal); i++)
        {
            if (lines[i].Length > child.Length && lines[i][child.Length] == ' ') continue; // a member of a value
            var line = lines[i][child.Length..];
            var name = line[..line.IndexOf(':', StringComparison.Ordinal)];
            var at = line.LastIndexOf(" @obj", StringComparison.Ordinal);
            tokens[name] = at < 0 ? line[(name.Length + 2)..] : line[(at + 1)..];
        }
        return tokens;
    }

    /// <summary>
    /// Decision 5: the response of an Upsert is today's typed <see cref="UpsertResponse"/> (<c>RecordCreated</c>, then <c>Target</c> as
    /// an <see cref="EntityReference"/> of the table and the id), built on the SDK side; nothing observes it, so it is never read into
    /// the model (the chain mirror holds no model twin of it).
    /// </summary>
    [Fact]
    public async Task Upsert_response_is_todays()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var m = await MergeKit.StartAsync(kit);
        var existing = await m.OrderAsync("mrg upsert existing", "order");
        var newId = Guid.NewGuid();

        foreach (var (id, created) in new[] { (newId, true), (existing, false) })
        {
            var mirror = new SdkMirror();
            var response = await ExecuteAsync(kit, new UpsertRequest { Target = MergeKit.Order(id, "mrg upsert response") }, mirror);
            response.GetType().Should().Be(typeof(UpsertResponse));
            response.ResponseName.Should().Be("Upsert");
            response.Results.Keys.Should().Equal("RecordCreated", "Target");
            response.Results["RecordCreated"].Should().Be(created);
            var reference = response.Results["Target"].Should().BeOfType<EntityReference>().Subject;
            reference.LogicalName.Should().Be(Order);
            reference.Id.Should().Be(id);
            mirror.TryGetModel(response, out _).Should().BeFalse("an unobserved response is never read in");
        }
    }

    /// <summary>
    /// Decision 9 (pre-flight rulings F1 and F15): the Task 1 probe "Upsert that creates" converts exactly its base count minus what the
    /// window's opening <c>Out</c> of the Upsert request wrote (the size of that request).
    /// </summary>
    [Fact]
    public async Task Upsert_conversions_go_down()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var window = PipelineModelBoundaryTests.SizeOf(new UpsertRequest { Target = new Entity("account", Guid.NewGuid()) { ["name"] = "mrg-probe-upsert" } });

        var count = await MergeGoldenTests.UpsertCreatesProbeAsync(kit, new SdkMirror());

        count.Should().Be(UpsertCreatesBase - window, $"the base count {UpsertCreatesBase} less the window's Out ({window})");
    }

    /// <summary>Runs <paramref name="request"/> through the executor as the administrator at depth 1 with <paramref name="mirror"/>.</summary>
    private static async Task<OrganizationResponse> ExecuteAsync(CoreScenarioKit kit, OrganizationRequest request, SdkMirror mirror)
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

    /// <summary>
    /// Review Focus 4 (controller ruling on Task 1): a create-only Upsert by a caller without Create privilege answers the 403 of the
    /// Create it would run, also for a row that does not exist, with the same fault as for an existing row; nothing is stored and the
    /// caller's Target is unchanged (its values the instances it sent).
    /// </summary>
    [Fact]
    public async Task Upsert_create_only_without_create_privilege_is_403_for_a_new_row()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var m = await MergeKit.StartAsync(kit);
        var existing = await m.OrderAsync("mrg create-only existing", "order");
        var (service, _) = await m.UserAsync(("prvReadxrmv_Order", DepthMask.Global), ("prvWritexrmv_Order", DepthMask.Global));

        async Task<XrmdFaultException> RefusedAsync(Entity target)
        {
            var option = target["xrmv_status"];
            var request = new UpsertRequest { Target = target };
            var before = CoreDump.RequestText(request);
            var fault = (await FluentActions.Awaiting(() => service.UpsertAsync(target, createOnly: true))
                .Should().ThrowAsync<XrmdFaultException>()).Which;
            CoreDump.RequestText(request).Should().Be(before, "the caller's Target is unchanged");
            target["xrmv_status"].Should().BeSameAs(option, "the Target keeps the instance the caller sent");
            return fault;
        }

        var forExisting = await RefusedAsync(MergeKit.Order(existing, "mrg create-only sent"));
        var newId = Guid.NewGuid();
        var forNew = await RefusedAsync(MergeKit.Order(newId, "mrg create-only new"));

        forNew.HttpStatus.Should().Be(403);
        forNew.ErrorCode.Should().Be(ErrorCodes.PrivilegeDenied);
        forNew.Message.Should().Contain("is missing prvCreatexrmv_Order privilege");
        forNew.Message.Should().Be(forExisting.Message, "the caller gets the same 403 whether the row exists or not");
        Convert.ToInt32(await kit.ScalarAsync($"SELECT COUNT(*) FROM data.{Order} WHERE {Order}id = '{newId}'"), CultureInfo.InvariantCulture)
            .Should().Be(0, "nothing is stored");
        (await kit.ScalarAsync($"SELECT xrmv_name FROM data.{Order} WHERE {Order}id = '{existing}'")).Should().Be("mrg create-only existing");
    }

    // ---- Task 4: the bulk messages ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Decision 7: a CreateMultiple, an UpdateMultiple and an UpsertMultiple open no held window at the core
    /// (<see cref="CoreHandOff.Held"/> never fires), and neither do their inner Create, Update and Upsert runs.
    /// </summary>
    [Fact]
    public async Task Bulk_holds_no_window()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var m = await MergeKit.StartAsync(kit);
        var (a, b) = await m.TwoOrdersAsync();
        var held = new List<string?>();
        var inner = new List<string>();
        CoreHandOff.Held.Value = r => held.Add(r.Name);
        PipelineExecutor.InnerRequest.Value = r => inner.Add(r.RequestName);
        try
        {
            var (created, createFault) = await CoreScenarios.ViaPipelineAsync(kit, new CreateMultipleRequest
            {
                Targets = MergeKit.Orders(MergeKit.Order(null, "mrg bulk c1"), MergeKit.Order(null, "mrg bulk c2")),
            });
            createFault.Should().BeNull();
            ((Guid[])created!.Results["Ids"]).Should().HaveCount(2);
            var (_, updateFault) = await CoreScenarios.ViaPipelineAsync(kit, new UpdateMultipleRequest
            {
                Targets = MergeKit.Orders(MergeKit.Order(a, "mrg bulk u1"), MergeKit.Order(b, "mrg bulk u2")),
            });
            updateFault.Should().BeNull();
            var (upserted, upsertFault) = await CoreScenarios.ViaPipelineAsync(kit, new UpsertMultipleRequest
            {
                Targets = MergeKit.Orders(MergeKit.Order(a, "mrg bulk s1"), MergeKit.Order(Guid.NewGuid(), "mrg bulk s2")),
            });
            upsertFault.Should().BeNull();
            ((UpsertMultipleResponse)upserted!).Results.Select(r => r.RecordCreated).Should().Equal(false, true);
        }
        finally
        {
            CoreHandOff.Held.Value = null;
            PipelineExecutor.InnerRequest.Value = null;
        }
        held.Should().BeEmpty("a bulk message and its inner Create, Update and Upsert run on the model request");
        inner.Should().Equal(["Create", "Create", "Update", "Update", "Upsert", "Update", "Upsert", "Create"],
            "each target ran as an inner execution, an inner Upsert running its own inner Update or Create");
    }

    /// <summary>
    /// Decision 6: the inner request of each target of a CreateMultiple is a <see cref="CreateRequest"/> whose Target is the caller's
    /// <see cref="Entity"/> of that row (the <see cref="PipelineExecutor.InnerRequest"/> seam, <c>ReferenceEquals</c>), and a PreOperation
    /// step on the inner Create (<c>ContextDumpPlugin</c>, which writes an identity token after each instance) sees as its Target the
    /// instance its <c>ParentContext</c> holds at that row of <c>Targets</c>, which is the caller's.
    /// </summary>
    [Fact]
    public async Task Bulk_inner_target_is_the_callers_entity()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await TestEntities.EnsureProjectAsync(kit.Store);
        var m = await MergeKit.StartAsync(kit);
        var clear = $"DELETE FROM data.{DumpLog}; DELETE FROM data.{Project} WHERE pb_name LIKE 'mrgm %';";
        await kit.ExecAsync(clear);
        kit.OnCleanup(() => kit.ExecAsync(clear));
        await m.StepAsync("ContextDumpPlugin", "Create", Project, 20, DumpLog);

        var rows = new[]
        {
            new Entity(Project) { ["pb_name"] = "mrgm bulk 0", ["pb_priority"] = new OptionSetValue(2) },
            new Entity(Project) { ["pb_name"] = "mrgm bulk 1", ["pb_priority"] = new OptionSetValue(3) },
        };
        var seen = new List<(Type Type, bool Same)>();
        PipelineExecutor.InnerRequest.Value = r => seen.Add((r.GetType(), ReferenceEquals(r.Parameters["Target"], rows[seen.Count])));
        try
        {
            (await CoreScenarios.ViaPipelineAsync(kit, new CreateMultipleRequest
            {
                Targets = new EntityCollection([.. rows]) { EntityName = Project },
            })).Fault.Should().BeNull();
        }
        finally
        {
            PipelineExecutor.InnerRequest.Value = null;
        }

        seen.Should().Equal([(typeof(CreateRequest), true), (typeof(CreateRequest), true)], "each inner Target is the caller's Entity of its row");
        var dumps = new List<string>();
        await using (var conn = await TestProvider.OpenAsync(kit.ConnectionString))
        await using (var cmd = TestSql.Command(conn, $"SELECT pb_data FROM data.{DumpLog} WHERE pb_name = 'Create|20|0|1'"))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) dumps.Add(rd.GetString(0));
        dumps.Should().HaveCount(2);
        var byName = dumps.ToDictionary(d => Regex.Match(d, "pb_name: String:\"(mrgm bulk \\d)\"").Groups[1].Value);
        for (var i = 0; i < rows.Length; i++)
        {
            var dump = byName[$"mrgm bulk {i}"];
            var parentAt = dump.IndexOf("\n  ParentContext: Context ", StringComparison.Ordinal);
            parentAt.Should().BePositive("the dump holds the parent context");
            var own = Regex.Match(dump[..parentAt], "\n    Target: Entity\\(" + Project + "\\) (@obj\\S+)").Groups[1].Value;
            var parent = Regex.Matches(dump[parentAt..], "\n          \\[(\\d+)\\]: Entity\\(" + Project + "\\) (@obj\\S+)")
                .ToDictionary(x => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture), x => x.Groups[2].Value);
            own.Should().StartWith("@obj");
            parent.Should().ContainKey(i);
            own.Should().Be(parent[i], $"the step on row {i} sees the instance at that row of its parent's Targets");
        }
    }

    /// <summary>
    /// Decision 3 and pre-flight ruling F2: stage 30 reads <c>Targets</c> from the model request as the PreOperation steps left it. A step
    /// on UpdateMultiple that removes the second target leaves one inner Update, of the first row; the second row is unchanged.
    /// </summary>
    [Fact]
    public async Task Bulk_reads_the_targets_the_steps_left()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var m = await MergeKit.StartAsync(kit);
        var call = await m.EditedUpdateMultipleAsync("removesecond");
        var first = ((EntityCollection)call.Request["Targets"]).Entities[0];
        var second = ((EntityCollection)call.Request["Targets"]).Entities[1].Id;
        var inner = new List<OrganizationRequest>();
        PipelineExecutor.InnerRequest.Value = inner.Add;
        try
        {
            (await m.RunAsAdminAsync(call.Request)).Item2.Should().BeNull();
        }
        finally
        {
            PipelineExecutor.InnerRequest.Value = null;
        }

        inner.Should().ContainSingle().Which.Should().BeOfType<UpdateRequest>().Which.Target.Should().BeSameAs(first);
        (await kit.ScalarAsync($"SELECT xrmv_name FROM data.{Order} WHERE {Order}id = '{first.Id}'")).Should().Be("mrg um a changed");
        (await kit.ScalarAsync($"SELECT xrmv_name FROM data.{Order} WHERE {Order}id = '{second}'")).Should().Be("mrg um b", "the removed target never ran");
    }

    /// <summary>
    /// Decision 8 and pre-flight ruling F2: the image collections of a bulk run take their ids from the model request. A PreOperation
    /// step on UpdateMultiple sets the second target's id to a third existing row: the pre image collection (read before the step)
    /// holds the second row, the post image collection the third, exactly as the Task 1 golden
    /// <c>UpdateMultipleImagesTargetIdChanged.txt</c> (today's SDK read of <c>Targets</c>) records them.
    /// </summary>
    [Fact]
    public async Task Bulk_images_use_the_model_ids()
    {
        const string name = "UpdateMultipleImagesTargetIdChanged";
        var scenario = MergeScenarios.All.Single(s => s.Name == name);
        await using var kit = await CoreScenarioKit.StartAsync(fx, scenario.OwnDatabase);
        var m = await MergeKit.StartAsync(kit);
        var call = await scenario.Arrange(m);
        await kit.ExecAsync($"DELETE FROM data.{TestEntities.PipeLog}");

        (await m.RunAsAdminAsync(call.Request)).Item2.Should().BeNull();

        var steps = GuardKit.Normalize(kit, await m.StepsTextAsync());
        steps.Should().Contain("[1] post: Entity(xrmv_order) id={a:c}", "the post image follows the id the step set");
        var golden = File.ReadAllText(Path.Combine(GoldenDirectory(), name + ".txt"));
        var from = golden.IndexOf("--- stage 40 ---\n", StringComparison.Ordinal) + "--- stage 40 ---\n".Length;
        var to = golden.IndexOf("--- async ---\n", from, StringComparison.Ordinal);
        steps.Should().Be(golden[from..to], "the stage-40 dump of the Task 1 golden");
    }

    /// <summary>
    /// Decision 9 (pre-flight rulings F1, F6 and F15): the Task 1 probes "CreateMultiple of 10" and "UpsertMultiple of 2" convert exactly
    /// their count before this task (the README's base for CreateMultiple; the Task 3 count for UpsertMultiple) less what the window's
    /// opening <c>Out</c> of the outer request wrote (the size of that request).
    /// </summary>
    [Fact]
    public async Task Bulk_conversions_go_down()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var createWindow = PipelineModelBoundaryTests.SizeOf(new CreateMultipleRequest
        {
            Targets = new EntityCollection([.. Enumerable.Range(0, 10).Select(i => new Entity("account") { ["name"] = $"mrg-probe-cm-{i}" })])
            {
                EntityName = "account",
            },
        });
        var upsertWindow = PipelineModelBoundaryTests.SizeOf(new UpsertMultipleRequest
        {
            Targets = new EntityCollection(
            [
                new Entity("account", Guid.NewGuid()) { ["name"] = "mrg-probe-um-0" },
                new Entity("account", Guid.NewGuid()) { ["name"] = "mrg-probe-um-1" },
            ]) { EntityName = "account" },
        });

        var create = await MergeGoldenTests.CreateMultipleOfTenProbeAsync(kit, new SdkMirror());
        var upsert = await MergeGoldenTests.UpsertMultipleOfTwoProbeAsync(kit, new SdkMirror());

        create.Should().Be(CreateMultipleOfTenBase - createWindow, $"the base count {CreateMultipleOfTenBase} less the window's Out ({createWindow})");
        upsert.Should().Be(UpsertMultipleOfTwoAfterTask3 - upsertWindow,
            $"the Task 3 count {UpsertMultipleOfTwoAfterTask3} less the window's Out ({upsertWindow})");
    }

    /// <summary><c>tests/fixtures/merge-golden</c> in the repository.</summary>
    private static string GoldenDirectory()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        dir.Should().NotBeNull("the repository root holds xrmverse.sln");
        return Path.Combine(dir!.FullName, "tests", "fixtures", MergeKit.Directory);
    }
}
