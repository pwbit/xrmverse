using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Model.Messages;
using Xrmd.Pipeline;
using Record = Xrmd.Model.Records.Record;
using AssignRequest = Microsoft.Crm.Sdk.Messages.AssignRequest;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3c3b1: probes of what the core's SDK callers see, through the test-side <see cref="PipelineCoreSdk"/> shim over
/// <see cref="CoreSdkBoundary"/> (<c>core.ExecuteCoreAsync(request, caller, session, ct)</c>), and of the elastic mark the core leaves on a
/// model Target (<see cref="CoreTargetMarks"/>). Each runs in a rolled-back transaction.
/// </summary>
[Collection("xrmd")]
public sealed class CoreSdkBoundaryTests(XrmdFixture fx)
{
    private static readonly Guid MissingOwner = new("5b0c0de0-0000-4000-8000-00000000b0b0");

    private static string Names(Entity entity) => string.Join(",", entity.Attributes.Keys);

    /// <summary>
    /// S.2e3c3b1 (Decision 4, Review Focus 1): a Create of an elastic table by key and one by id leave the caller's <see cref="Entity"/>
    /// with the stored id and its <c>partitionid</c>, the attribute names in the order seen on today's code.
    /// </summary>
    [Fact]
    public async Task Elastic_create_fills_the_callers_entity()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await kit.EnsureElasticTableAsync();
        var key = Guid.NewGuid();
        var byKey = new Entity("new_crgelastic") { ["new_name"] = "sb probe key", KeyAttributes = { ["new_crgelasticid"] = key, ["partitionid"] = "sb-p" } };
        var byId = Guid.NewGuid();
        var withId = new Entity("new_crgelastic", byId) { ["new_name"] = "sb probe id", ["partitionid"] = "sb-p2" };
        var noPartitionId = Guid.NewGuid();
        var withoutPartition = new Entity("new_crgelastic", noPartitionId) { ["new_name"] = "sb probe id only" };
        var outcome = "none";
        await InRolledBackAsync(kit, async (core, session) =>
        {
            try { await core.ExecuteCoreAsync(new CreateRequest { Target = withoutPartition }, kit.Caller, session, default); }
            catch (XrmdFaultException e) { outcome = $"0x{e.ErrorCode:X8}|{e.HttpStatus}|{e.Message}"; }
        });
        // Observed: the Create succeeds, the id is kept, and the core fills no partitionid into the caller's entity.
        $"{outcome}|{withoutPartition.Id == noPartitionId}|{Names(withoutPartition)}".Should().Be("none|True|new_name");
        await InRolledBackAsync(kit, async (core, session) =>
        {
            await core.ExecuteCoreAsync(new CreateRequest { Target = byKey }, kit.Caller, session, default);
            await core.ExecuteCoreAsync(new CreateRequest { Target = withId }, kit.Caller, session, default);
        });
        // Observed on today's code (eff573e8): the key gives the id and the partitionid; the names stay in the order sent.
        byKey.Id.Should().Be(key);
        Names(byKey).Should().Be("new_name,partitionid");
        byKey["partitionid"].Should().Be("sb-p");
        withId.Id.Should().Be(byId);
        Names(withId).Should().Be("new_name,partitionid");
        withId["partitionid"].Should().Be("sb-p2");
    }

    /// <summary>
    /// S.2e3c3b1 (Decision 4, Review Focus 2): a Create on the calculated table that fails (a calculated value and an owner that does not exist)
    /// still drops the calculated value from the caller's <see cref="Entity"/>; the fault is today's. The fault is an owner that does not
    /// exist, which is raised after the validation has removed the value (an unknown column is refused before it, and the value then stays).
    /// </summary>
    [Fact]
    public async Task Failed_create_still_drops_the_calculated_value()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var calculated = await CoreCrudModelTests.CalculatedTableAsync(kit);
        var entity = new Entity(calculated)
        {
            ["new_name"] = "sb probe", ["new_total"] = new Money(9m),
            ["ownerid"] = new EntityReference("systemuser", MissingOwner),
        };
        XrmdFaultException? fault = null;
        await InRolledBackAsync(kit, async (core, session) =>
        {
            try
            {
                await core.ExecuteCoreAsync(new CreateRequest { Target = entity }, kit.Caller, session, default);
            }
            catch (XrmdFaultException e)
            {
                fault = e;
            }
        });
        fault.Should().NotBeNull();
        $"0x{fault!.ErrorCode:X8}|{fault.HttpStatus}|{fault.Message}".Should().Be($"0x80040217|404|systemuser With Id = {MissingOwner} Does Not Exist");
        entity.Contains("new_total").Should().BeFalse();
        Names(entity).Should().Be("new_name,ownerid");
    }

    /// <summary>
    /// S.2e3c3b1 (Decision 4, Review Focus 3): an Assign whose <c>Assignee</c> is a <c>systemuser</c> reference by key. Today's code
    /// refuses it (alternate keys exist only on elastic tables, so the probe cannot create one on <c>systemuser</c>): the fault is pinned and
    /// the caller's reference is left unchanged (no id filled, its key attribute kept). The fill is therefore reachable only through the
    /// model; the probe keeps the SDK caller's side of that refusal.
    /// </summary>
    [Fact]
    public async Task Assign_by_key_is_refused_and_leaves_the_assignee()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var account = Guid.NewGuid(); // the Assign is refused on the key attributes before it reaches the account
        var assignee = new EntityReference("systemuser") { KeyAttributes = { ["internalemailaddress"] = "sb-probe@example.test" } };
        XrmdFaultException? fault = null;
        await InRolledBackAsync(kit, async (core, session) =>
        {
            try
            {
                await core.ExecuteCoreAsync(new AssignRequest { Target = new EntityReference("account", account), Assignee = assignee }, kit.Caller, session, default);
            }
            catch (XrmdFaultException e)
            {
                fault = e;
            }
        });
        fault.Should().NotBeNull();
        $"0x{fault!.ErrorCode:X8}|{fault.HttpStatus}|{fault.Message}".Should().Be(
            "0x80040203|400|Key attributes (internalemailaddress) are not supported for entity 'systemuser': xrmverse supports alternate keys only for elastic tables.");
        $"{assignee.Id}|{assignee.KeyAttributes.Count}".Should().Be("00000000-0000-0000-0000-000000000000|1");
    }

    /// <summary>
    /// S.2e3c3b1 (Decision 4): after a model Create or Update on an elastic table the Target carries the elastic mark
    /// (<see cref="CoreTargetMarks.IsElastic"/>), which the SDK boundary reads instead of the metadata store.
    /// </summary>
    [Theory]
    [InlineData("Create")]
    [InlineData("Update")]
    public async Task Model_write_on_an_elastic_table_marks_the_target(string message)
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await kit.EnsureElasticTableAsync();
        var target = new Record("new_crgelastic", Guid.NewGuid()) { ["new_name"] = "sb mark", ["partitionid"] = "sb-mark" };
        CoreTargetMarks.IsElastic(target).Should().BeFalse("nothing has marked it yet");
        await InRolledBackAsync(kit, async (core, session) =>
        {
            var call = new CoreCall(kit.Caller, session);
            if (message == "Update")
                await core.ExecuteCoreAsync(new Request { Name = "Create", Parameters = { ["Target"] = target } }, call, default);
            var request = new Request { Name = message, Parameters = { ["Target"] = target } };
            await core.ExecuteCoreAsync(request, call, default);
        });
        CoreTargetMarks.IsElastic(target).Should().BeTrue();
    }

    /// <summary>S.2e3c3b1 (Decision 4): a model Create or Update on a standard table leaves the Target unmarked.</summary>
    [Fact]
    public async Task Model_write_on_a_standard_table_leaves_the_target_unmarked()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var target = new Record("account", Guid.NewGuid()) { ["name"] = "sb mark standard" };
        await InRolledBackAsync(kit, async (core, session) =>
        {
            var call = new CoreCall(kit.Caller, session);
            await core.ExecuteCoreAsync(new Request { Name = "Create", Parameters = { ["Target"] = target } }, call, default);
            await core.ExecuteCoreAsync(new Request { Name = "Update", Parameters = { ["Target"] = target } }, call, default);
        });
        CoreTargetMarks.IsElastic(target).Should().BeFalse();
    }

    /// <summary>
    /// S.2e3c3b1 (Decision 4): the mark is set before anything can fail, so an Update that carries server values (refused with an
    /// <see cref="InvalidOperationException"/>: server values are written on a Create only) still leaves the Target marked.
    /// </summary>
    [Fact]
    public async Task Refused_model_update_on_an_elastic_table_still_marks_the_target()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await kit.EnsureElasticTableAsync();
        var target = new Record("new_crgelastic", Guid.NewGuid()) { ["new_name"] = "sb mark refused", ["partitionid"] = "sb-mark" };
        InvalidOperationException? refused = null;
        await InRolledBackAsync(kit, async (core, session) =>
        {
            var call = new CoreCall(kit.Caller, session, null, new Dictionary<string, object?> { ["new_name"] = "server" });
            try { await core.ExecuteCoreAsync(new Request { Name = "Update", Parameters = { ["Target"] = target } }, call, default); }
            catch (InvalidOperationException e) { refused = e; }
        });
        refused.Should().NotBeNull();
        refused!.Message.Should().Be("Server values are written on a Create only, not on 'Update'.");
        CoreTargetMarks.IsElastic(target).Should().BeTrue();
    }

    private static async Task InRolledBackAsync(CoreScenarioKit kit, Func<IPipelineCore, DataSession, Task> body)
    {
        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<IPipelineCore>();
        await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await body(core, new DataSession(conn, tx));
        }
        finally
        {
            await tx.RollbackAsync();
            kit.Cache.Invalidate();
        }
    }

    /// <summary>
    /// S.2e3c3b1 Task 3 (Decision 3): the SDK crossing is named at the call. No type of the pipeline assembly declares an extension
    /// method on <see cref="IPipelineCore"/>; a caller names <see cref="CoreSdkBoundary"/> or calls the model members.
    /// </summary>
    [Fact]
    public void No_sdk_extension_methods_on_the_core()
    {
        var found = typeof(IPipelineCore).Assembly.GetTypes()
            .Where(t => t.IsSealed && t.IsAbstract && !t.IsGenericType)
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            .Where(m => m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)
                        && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(IPipelineCore))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();
        found.Should().BeEmpty();
    }
}
