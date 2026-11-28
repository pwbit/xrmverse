using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Audit;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Query.FetchXml;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e2c, Task 5: the boundaries of the pipeline on the model context. The SDK request crosses into the model and back through
/// one <see cref="SdkMirror"/> per execution chain (the caller may pass its own in <see cref="InvocationContext.Mirror"/>), so the
/// request instance and the marks keyed on it reach stage 30 (Review Focus 1); a plug-in's view is read back before and written again
/// after each of its service calls (Review Focus 3); a message nobody observes after stage 30 never converts its response (Decision 3).
/// </summary>
[Collection("xrmd")]
public sealed class PipelineModelBoundaryTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Pipe = TestEntities.Pipe;
    private const string PipeLog = TestEntities.PipeLog;
    private const string DumpLog = TestEntities.PipeLog2;
    private const string Account = "account";
    private const string Plugins = "Xrmd.TestPlugins.";
    private const string Reserved = MailSchema.MailboxDeliveryVariable;

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<string, Guid> _types = new();
    private Guid _assemblyId;

    private OrganizationService Admin => SecurityTestHelper.ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{Pipe}; DELETE FROM data.{PipeLog}; DELETE FROM data.{DumpLog};");
        _assemblyId = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
    }

    public async Task DisposeAsync() => await PluginTestHelper.ClearAsync(fx);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<Guid> StepAsync(string plugin, string message, string entity, int stage, string? config = null, int rank = 1)
    {
        if (!_types.TryGetValue(plugin, out var typeId))
            _types[plugin] = typeId = await PluginTestHelper.RegisterTypeAsync(Admin, _assemblyId, Plugins + plugin);
        return await PluginTestHelper.RegisterStepAsync(Admin, typeId, message, entity, stage, rank: rank, config: config);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> ColumnAsync(string sql, Guid? id = null)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        if (id is { } value) cmd.Parameters.AddWithValue("@id", value);
        var values = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) values.Add(rd.IsDBNull(0) ? "<null>" : Convert.ToString(rd.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        return values;
    }

    /// <summary>Runs <paramref name="request"/> through the executor as the admin, at the first level.</summary>
    private async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, SdkMirror? mirror = null)
    {
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        await using var scope = fx.App.Services.CreateAsyncScope();
        var invocation = new InvocationContext(caller, caller.UserId, 1, null, null, Guid.NewGuid()) { Mirror = mirror };
        return await scope.ServiceProvider.GetRequiredService<PipelineExecutor>().ExecuteAsync(request, invocation, default);
    }

    private async Task SetOrganizationAuditAsync(bool enabled)
    {
        var organization = Guid.Parse((await ColumnAsync("SELECT organizationid FROM data.organization")).Single());
        await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = enabled });
    }

    // ---- Review Focus 1: request identity at stage 30 ---------------------------------------------------------------

    /// <summary>
    /// A Web API <c>$filter</c> read is a request whose <c>Query</c> is a placeholder: the core runs the model query only while the
    /// placeholder is the same instance. With RetrieveMultiple steps at 20 and 40 the rows are the filtered ones, and the request
    /// that reaches the core is the caller's own (paired in the mirror, its placeholder still in place).
    /// </summary>
    [Fact]
    public async Task Web_api_filter_survives_a_RetrieveMultiple_step()
    {
        foreach (var name in new[] { "a", "b", "other" })
            await Admin.CreateAsync(new Entity(Account) { ["name"] = $"bnd-{_run}-{name}" });
        var fetch = $"""
            <fetch><entity name="account"><attribute name="name" /><order attribute="name" />
              <filter type="or"><condition attribute="name" operator="eq" value="bnd-{_run}-a" />
                <condition attribute="name" operator="eq" value="bnd-{_run}-b" /></filter></entity></fetch>
            """;
        await StepAsync("ContextDumpPlugin", "RetrieveMultiple", Account, 20, "-");
        await StepAsync("ContextDumpPlugin", "RetrieveMultiple", Account, 40, "-");
        var mirror = new SdkMirror();
        var request = new RetrieveMultipleModelRequest(FetchXmlParser.Parse(fetch));

        var rows = (EntityCollection)(await ExecuteAsync(request, mirror)).Results["EntityCollection"];

        rows.Entities.Select(e => e.GetAttributeValue<string>("name")).Should().Equal($"bnd-{_run}-a", $"bnd-{_run}-b");
        request.UsesModel.Should().BeTrue("the placeholder query is still the request's own");
        mirror.TryGetModel(request, out _).Should().BeTrue("the request crossed the mirror and kept its instance");
    }

    /// <summary>
    /// SetState runs as an Update marked with the Set State audit action, a mark keyed on the request instance: with an Update step
    /// registered (so the request crosses into a plug-in's view and back) the audit row is still a Set State.
    /// </summary>
    [Fact]
    public async Task Set_state_with_an_update_step_is_audited_as_set_state()
    {
        await SetOrganizationAuditAsync(true);
        try
        {
            var id = await Admin.CreateAsync(new Entity(Account) { ["name"] = $"bnd-{_run}-state" });
            await StepAsync("SharedVariablePlugin", "Update", Account, 20, $"record|{PipeLog}|none");
            var mirror = new SdkMirror();

            await ExecuteAsync(new SetStateRequest
            {
                EntityMoniker = new EntityReference(Account, id), State = new OptionSetValue(1), Status = new OptionSetValue(2),
            }, mirror);

            (await ColumnAsync($"SELECT pb_name FROM data.{PipeLog}")).Should().Equal("Update|20|1|<none>");
            (await ColumnAsync("SELECT action FROM data.audit WHERE objectid = @id ORDER BY createdon", id))
                .Should().Equal($"{AuditActions.Create}", $"{AuditActions.SetState}");
            (await ColumnAsync("SELECT statecode FROM data.account WHERE accountid = @id", id)).Should().Equal("1");
            mirror.SyncCount.Should().BePositive("the Update crossed the mirror the caller passed");
        }
        finally
        {
            await SetOrganizationAuditAsync(false);
        }
    }

    // ---- Review Focus 3: shared variables around nested calls ------------------------------------------------------

    /// <summary>A shared variable a plug-in sets before it calls its service is in the nested execution's <c>ParentContext</c>.</summary>
    [Fact]
    public async Task Shared_variable_set_before_a_nested_call_is_in_the_nested_parent()
    {
        await StepAsync("SharedVariablePlugin", "Create", Pipe, 20, $"setandcreate|k|v|{PipeLog}");
        await StepAsync("ContextDumpPlugin", "Create", PipeLog, 20, DumpLog);

        await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" });

        var dump = (await ColumnAsync($"SELECT pb_data FROM data.{DumpLog}")).Should().ContainSingle().Subject;
        var parent = dump[dump.IndexOf("ParentContext: ", StringComparison.Ordinal)..];
        parent.Should().Contain("k: String:\"v\"", "the parent's shared variables hold what the plug-in set before its call");
        dump[..dump.IndexOf("ParentContext: ", StringComparison.Ordinal)].Should().NotContain("k: String:\"v\"",
            "the child's own shared variables do not");
    }

    /// <summary>A reserved shared variable a plug-in forges before it calls its service fails the nested request, before it runs.</summary>
    [Fact]
    public async Task Reserved_variable_forged_before_a_nested_call_is_refused()
    {
        await StepAsync("SharedVariablePlugin", "Create", Pipe, 20, $"setandcreate|{Reserved}|{Guid.NewGuid():D}|{PipeLog}");

        var fault = await TestHelpers.FaultAsync(() => Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" }));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain($"The plug-in or workflow that issued this 'Create' request added, changed or removed a shared " +
                                       $"variable only the server sets: the shared variable '{Reserved}' is reserved.");
        (await ColumnAsync($"SELECT pb_name FROM data.{PipeLog}")).Should().BeEmpty("the nested Create is refused before it runs");
        (await ColumnAsync($"SELECT pb_name FROM data.{Pipe}")).Should().BeEmpty();
    }

    // ---- Decision 3: the fast path ----------------------------------------------------------------------------------

    /// <summary>
    /// S.2e3b, ruling 8a: a read with no stage-40 observer converts no row through the mirror. The core answers RetrieveMultiple on the
    /// model and its adapter fills the typed SDK response with plain copies of the rows, as the SDK handler did before S.2e3b, so 1 row and
    /// 50 rows cost the mirror the same (the request crossing) and a returned row has no model twin. (Task 8 first wrote each row out
    /// through the mirror, 49 rows more for 50 rows than for 1; ruling 8a took that back for the Web API's read time.)
    /// </summary>
    [Fact]
    public async Task Read_without_stage_40_observer_converts_no_row()
    {
        for (var i = 0; i < 50; i++) await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = $"row{i:D2}" });

        async Task<long> CountAsync(int rows)
        {
            var mirror = new SdkMirror();
            var query = new QueryExpression(Pipe) { ColumnSet = new ColumnSet("pb_name"), TopCount = rows };
            var response = await ExecuteAsync(new RetrieveMultipleRequest { Query = query }, mirror);
            var entities = ((EntityCollection)response.Results["EntityCollection"]).Entities;
            entities.Should().HaveCount(rows);
            entities.Where(e => mirror.TryGetModel(e, out _)).Should().BeEmpty("the rows are plain copies, not mirror twins");
            return mirror.SyncCount;
        }

        var one = await CountAsync(1);
        var fifty = await CountAsync(50);

        fifty.Should().Be(one, "no row crosses the mirror");
        one.Should().BePositive("the request crosses the mirror");
    }

    /// <summary>
    /// A bulk message on a standard table whose inner Create has a step: each inner execution has the outer context (the core holds its
    /// request) as its parent. The conversions grow linearly with the targets: the inner steps do not convert every outer target each.
    /// </summary>
    [Fact]
    public async Task Bulk_with_an_inner_step_converts_linearly()
    {
        await StepAsync("SetAttributePlugin", "Create", Account, 20, "telephone1=x");

        async Task<long> CountAsync(int targets)
        {
            var mirror = new SdkMirror();
            var entities = Enumerable.Range(0, targets).Select(i => new Entity(Account) { ["name"] = $"bnd-{_run}-{targets}-{i}" }).ToList();
            var response = await ExecuteAsync(new CreateMultipleRequest { Targets = new EntityCollection(entities) { EntityName = Account } }, mirror);
            ((Guid[])response.Results["Ids"]).Should().HaveCount(targets);
            return mirror.SyncCount;
        }

        var ten = await CountAsync(10);
        var forty = await CountAsync(40);

        forty.Should().BeLessThanOrEqualTo(4 * ten, "4 times the targets may cost at most 4 times the conversions (linear growth)");
        (await ColumnAsync($"SELECT telephone1 FROM data.{Account} WHERE name LIKE 'bnd-{_run}-40-%'")).Should().HaveCount(40)
            .And.OnlyContain(v => v == "x", "the inner step ran on every target");
    }

    /// <summary>
    /// Final review M1: a value a step puts into <c>OutputParameters</c> reaches an in-process caller's response as that instance, as
    /// with the SDK pipeline (an <c>OptionSetValue</c> the step placed in two slots is one instance in the response).
    /// </summary>
    [Fact]
    public async Task Response_keeps_the_instance_a_step_set()
    {
        await StepAsync("OutputChoicePlugin", "Create", Pipe, 40);

        var response = await ExecuteAsync(new CreateRequest { Target = new Entity(Pipe) { ["pb_name"] = "m1" } });

        var choice = response.Results["Choice"].Should().BeOfType<OptionSetValue>().Subject;
        choice.Value.Should().Be(3);
        ((Entity)response.Results["Holder"])["c"].Should().BeSameAs(choice);
    }

    // ---- a nested request on the caller's own collection (final review I1) --------------------------------------------

    /// <summary>
    /// A plug-in executes an Update on its own <c>InputParameters</c> collection, and the nested step adds a key to it. As with the
    /// SDK pipeline, the caller's collection stays the context's, the nested key is visible to the caller and to the outer execution
    /// after stage 30, and a key the caller writes into the held collection later still reaches the outer execution.
    /// </summary>
    [Fact]
    public async Task Nested_request_on_the_callers_own_parameters_shares_them()
    {
        var id = await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" });
        await StepAsync("ForwardInputsPlugin", "Update", Pipe, 20, PipeLog);
        await StepAsync("ForwardInputsPlugin", "Update", Pipe, 40, PipeLog);

        await Admin.UpdateAsync(new Entity(Pipe, id) { ["pb_name"] = "q" });

        (await ColumnAsync($"SELECT pb_data FROM data.{Pipe} WHERE {Pipe}id = @id", id)).Should().Equal("same=True;key=True;view=True");
        var post = (await ColumnAsync($"SELECT pb_data FROM data.{PipeLog} WHERE pb_name = 'post'")).Should().ContainSingle().Subject;
        post.Split(',').Should().Contain(["AddedByNested", "Late", "Target"], "the outer execution holds the nested key and the late write");
    }

    /// <summary>
    /// Final review M5: a nested call carries its chain's mirror. A <see cref="InvocationContext.Parent"/> without a
    /// <see cref="InvocationContext.Mirror"/> is a server bug (the caller's view would be neither read back nor refreshed), refused
    /// before anything runs.
    /// </summary>
    [Fact]
    public async Task Parent_without_a_mirror_is_refused()
    {
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        await using var scope = fx.App.Services.CreateAsyncScope();
        var parent = new Xrmd.Model.Messages.StepContext { MessageName = "Create", Depth = 1 };
        var invocation = new InvocationContext(caller, caller.UserId, 2, parent, null, Guid.NewGuid());

        var act = () => scope.ServiceProvider.GetRequiredService<PipelineExecutor>()
            .ExecuteAsync(new CreateRequest { Target = new Entity(Pipe) { ["pb_name"] = "m5" } }, invocation, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*mirror*");
        (await ColumnAsync($"SELECT pb_name FROM data.{Pipe}")).Should().BeEmpty();
    }

    // ---- the guard's writes reach the core -------------------------------------------------------------------------

    private const string Secured = "pb_bndsecure", SecuredColumn = "pb_bndsecret";
    private static readonly SemaphoreSlim SecuredLock = new(1, 1);

    /// <summary>
    /// The guard (before stage 10) normalizes the Target of a field share on the SDK request: <c>objecttypecode</c> from
    /// <c>objectid</c>, the unset access flag as false. The request crosses into the model after the guard, so the stage-20/30 boundary
    /// writes those values to the core instead of the request as the caller sent it.
    /// </summary>
    [Fact]
    public async Task Guard_normalization_of_the_target_reaches_the_core()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await SecuredLock.WaitAsync();
        try
        {
            if (store.GetEntity(Secured) is null)
            {
                var entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata
                {
                    LogicalName = Secured, SchemaName = "pb_BndSecure", OwnershipType = Microsoft.Xrm.Sdk.Metadata.OwnershipTypes.UserOwned,
                };
                entity.SetAttributes([new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
                    { LogicalName = SecuredColumn, SchemaName = "pb_BndSecret", MaxLength = 100, IsSecured = true }]);
                await store.CreateEntityAsync(entity, new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
                    { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 100 });
            }
        }
        finally
        {
            SecuredLock.Release();
        }
        var record = await Admin.CreateAsync(new Entity(Secured) { ["pb_name"] = $"bnd-{_run}", [SecuredColumn] = "s" });
        var user = await SecurityTestHelper.CreateUserAsync(fx, await SecurityTestHelper.RootBusinessUnitAsync(fx), Guid.NewGuid());
        var attributeId = store.GetEntity(Secured)!.Attributes.Single(a => a.LogicalName == SecuredColumn).MetadataId!.Value;

        var share = await Admin.CreateAsync(new Entity(FieldSecurityTables.Share)
        {
            [FieldSecurityTables.ShareObjectId] = new EntityReference(Secured, record),
            [FieldSecurityTables.ShareAttributeId] = attributeId,
            [FieldSecurityTables.SharePrincipalId] = new EntityReference("systemuser", user),
            [FieldSecurityTables.ShareReadAccess] = true,
        });

        (await ColumnAsync($"SELECT {FieldSecurityTables.ShareObjectTypeCodeColumn} FROM data.{FieldSecurityTables.Share} " +
            $"WHERE {FieldSecurityTables.Share}id = @id", share)).Should().Equal([Secured], "the guard filled it from objectid");
        (await ColumnAsync($"SELECT {FieldSecurityTables.ShareUpdateAccess} FROM data.{FieldSecurityTables.Share} " +
            $"WHERE {FieldSecurityTables.Share}id = @id", share)).Should().ContainSingle().Which.Should().BeOneOf("False", "0");
    }

    // ---- an early-bound Target ---------------------------------------------------------------------------------------

    /// <summary>
    /// An early-bound plug-in creates an early-bound <c>pb_pipelog</c> through its service; a stage 20 step of that Create changes
    /// the Target. The stored row has the plug-in's name and the step's change, as today.
    /// </summary>
    [Fact]
    public async Task Early_bound_target_reaches_stage_30()
    {
        var assemblyId = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.EarlyBoundPluginBytes, "Xrmd.TestPlugins.EarlyBound");
        var typeId = await PluginTestHelper.RegisterTypeAsync(Admin, assemblyId, "Xrmd.TestPlugins.EarlyBound.EarlyBoundLogPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, typeId, "Create", Pipe, 40);
        await StepAsync("SetAttributePlugin", "Create", PipeLog, 20, "pb_data=set-at-20");

        await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" });

        (await ColumnAsync($"SELECT pb_name FROM data.{PipeLog}")).Should().Equal("early|p|1");
        (await ColumnAsync($"SELECT pb_data FROM data.{PipeLog}")).Should().Equal("set-at-20");
    }
    // ---- S.2e3a Task 4: the pipeline calls the core on the model ---------------------------------------------------

    // The SyncCount values below were measured with the same scenarios on 8d421c0f, before S.2e3a changed src/ (BASE): the
    // pipeline path must not convert anything more than BASE did.

    /// <summary>Runs <paramref name="request"/> as the admin with a mirror of its own and returns the objects that mirror wrote.</summary>
    private async Task<long> CountAsync(OrganizationRequest request)
    {
        var mirror = new SdkMirror();
        await ExecuteAsync(request, mirror);
        return mirror.SyncCount;
    }

    /// <summary>The objects one crossing of <paramref name="request"/> writes.</summary>
    internal static long SizeOf(OrganizationRequest request)
    {
        var mirror = new SdkMirror();
        mirror.In(request);
        return mirror.SyncCount;
    }

    /// <summary>
    /// A write with no step crosses its request once: in at entry (5 objects for this Associate). S.2e3b (Decision 8): the core answers
    /// the CRUD messages on the model request, so the boundary no longer writes it out and reads it back (BASE wrote it 3 times, 15
    /// objects); the core's typed response, empty for an Associate, is written once (1 object). <c>WrittenEntities</c> (which reads the
    /// name and the relationship only) converts nothing.
    /// </summary>
    [Fact]
    public async Task Write_message_converts_only_its_request()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsureProjectTagAsync(store, fx.ConnectionString);
        var project = await Admin.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = $"bnd-{_run}" });
        var tag = await Admin.CreateAsync(new Entity(TestEntities.Tag) { ["new_name"] = $"bnd-{_run}" });
        AssociateRequest Associate() => new()
        {
            Target = new EntityReference(TestEntities.Project, project), Relationship = new Relationship(TestEntities.ProjectTag),
            RelatedEntities = [new EntityReference(TestEntities.Tag, tag)],
        };

        var count = await CountAsync(Associate());

        count.Should().Be(SizeOf(Associate()) + 1).And.Be(6, "the request crosses once (5) and the empty typed response is written once; BASE wrote 15");
    }

    /// <summary>
    /// The pre and post images of a step are each read and converted once, into the chain's mirror, where the record's SDK twin is
    /// the core's own <c>Entity</c> (not a copy). The whole Update writes 19 objects: BASE wrote 23, and S.2e3b (Decision 8) no longer
    /// writes the request out and reads it back at the boundary of a CRUD message.
    /// </summary>
    [Fact]
    public async Task Images_are_converted_once_into_the_chain_mirror()
    {
        var row = await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "img", ["pb_count"] = 1 });
        var step = await StepAsync("ThrowOnNamePlugin", "Update", Pipe, 40, "never|x");
        await PluginTestHelper.RegisterImageAsync(Admin, step, 0, "pre", "pb_name,pb_count");
        await PluginTestHelper.RegisterImageAsync(Admin, step, 1, "post", "pb_name,pb_count");
        var reads = new List<(Entity Image, Xrmd.Model.Records.Record Record)>();
        var mirror = new SdkMirror();

        CoreSdkBoundary.ImageRead.Value = (image, record) => reads.Add((image, record));
        try
        {
            await ExecuteAsync(new UpdateRequest { Target = new Entity(Pipe, row) { ["pb_name"] = "img2", ["pb_count"] = 2 } }, mirror);
        }
        finally
        {
            CoreSdkBoundary.ImageRead.Value = null;
        }

        reads.Should().HaveCount(2, "one pre image and one post image, each read once");
        foreach (var (image, record) in reads)
        {
            mirror.TryGetSdk(record, out var twin).Should().BeTrue("the image was converted into the chain's mirror");
            twin.Should().BeSameAs(image, "the record's twin is the core's own Entity");
        }
        reads.Select(r => r.Image.GetAttributeValue<string>("pb_name")).Should().Equal("img", "img2");
        mirror.SyncCount.Should().Be(19, "BASE wrote 23 objects for this Update; S.2e3b crosses the request once instead of 3 times");
    }

    /// <summary>
    /// S.2e3b, ruling 8a: a RetrieveMultiple of 1,000 rows. The core answers it on the model and its adapter fills the typed SDK response
    /// with plain copies of the rows (as before S.2e3b), so an unobserved read converts no row through the mirror: a read of one row and a
    /// read of 1,000 rows each write 7 objects, the request crossing once (BASE: 21 for both, the request crossed 3 times). With one
    /// post-operation step the rows cross only where the step's view and the final response need them: 33 objects for one row and 3,030
    /// for 1,000 rows, at most BASE (50 and 4,046).
    /// (Task 8 first measured 10, 1,009, 36 and 4,032: its adapter wrote the response and each row out through the mirror, 3 objects for
    /// the response and 1 per row more, observed or not; ruling 8a replaced that write with the plain copy.)
    /// </summary>
    [Fact]
    public async Task Unobserved_retrieve_multiple_converts_no_row()
    {
        for (var batch = 0; batch < 10; batch++)
            await Admin.ExecuteAsync(new CreateMultipleRequest
            {
                Targets = new EntityCollection([.. Enumerable.Range(0, 100).Select(i => new Entity(Pipe) { ["pb_name"] = $"bnd1k-{batch * 100 + i:D4}" })])
                    { EntityName = Pipe },
            });
        RetrieveMultipleRequest Read(int top) => new()
        {
            Query = new QueryExpression(Pipe)
            {
                ColumnSet = new ColumnSet("pb_name"), TopCount = top,
                Criteria = { Conditions = { new ConditionExpression("pb_name", ConditionOperator.BeginsWith, "bnd1k-") } },
                Orders = { new OrderExpression("pb_name", OrderType.Ascending) },
            },
        };

        var unobserved = (One: await CountAsync(Read(1)), Thousand: await CountAsync(Read(1000)));
        await StepAsync("ThrowOnNamePlugin", "RetrieveMultiple", Pipe, 40, "never|x");
        var observed = (One: await CountAsync(Read(1)), Thousand: await CountAsync(Read(1000)));

        (unobserved.One, unobserved.Thousand, observed.One, observed.Thousand).Should().Be(
            (UnobservedOne, UnobservedThousand, ObservedOne, ObservedThousand), "the counts measured on S.2e3b (see the summary)");
        observed.One.Should().BeLessThanOrEqualTo(50, "BASE wrote 50 objects for an observed read of one row");
        observed.Thousand.Should().BeLessThanOrEqualTo(4046, "BASE wrote 4,046 objects for an observed read of 1,000 rows");
    }

    // Measured on S.2e3b Task 8, fix round 1 (ruling 8a; see the summary of Unobserved_retrieve_multiple_converts_no_row).
    private const long UnobservedOne = 7;
    private const long UnobservedThousand = 7;
    private const long ObservedOne = 33;
    private const long ObservedThousand = 3030;

    /// <summary>
    /// A stage-40 step of a read sees the rows the core read (the shell is read in before the step's view is lent), and its change
    /// reaches the caller's response.
    /// </summary>
    [Fact]
    public async Task Stage_40_view_of_a_read_sees_the_core_rows()
    {
        for (var i = 0; i < 3; i++) await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = $"view{i}" });
        await StepAsync("OutputEditorPlugin", "RetrieveMultiple", Pipe, 40, "pb_data=edited");

        var response = (RetrieveMultipleResponse)await ExecuteAsync(new RetrieveMultipleRequest
        {
            Query = new QueryExpression(Pipe) { ColumnSet = new ColumnSet("pb_name") },
        });

        response.EntityCollection.Entities.Should().HaveCount(3)
            .And.OnlyContain(e => e.GetAttributeValue<string>("pb_data") == "edited");
    }

    /// <summary>
    /// A plug-in's stage-40 view of a Create holds the core's id, and the typed responses its service returns cast as before:
    /// <c>CreateResponse</c> with the stored row's id and <c>RetrieveMultipleResponse</c> with the row.
    /// </summary>
    [Fact]
    public async Task Plugin_cast_of_typed_responses_still_works()
    {
        await StepAsync("TypedResponsesPlugin", "Create", Pipe, 40, PipeLog);

        await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "typed-trigger" });

        var typed = (await ColumnAsync($"SELECT {PipeLog}id FROM data.{PipeLog} WHERE pb_name = 'typed'")).Should().ContainSingle().Subject;
        (await ColumnAsync($"SELECT pb_data FROM data.{PipeLog} WHERE pb_name = 'typed-result'")).Should()
            .Equal($"output=True;created={Guid.Parse(typed)};rows=1;name=typed");
    }

    /// <summary>
    /// A request the core issues gets its response filled: AddToQueue creates its queue item through the pipeline and answers with the
    /// item's id, whether nothing observes the nested Create (its response stays an unread shell) or a stage-40 step does.
    /// </summary>
    [Fact]
    public async Task Core_issued_nested_request_gets_a_filled_response()
    {
        async Task<(Guid Answered, Guid Stored)> AddAsync()
        {
            var queue = await Admin.CreateAsync(new Entity("queue")
            {
                ["name"] = $"bnd-{_run}", ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1),
            });
            var email = await Admin.CreateAsync(new Entity("email") { ["subject"] = $"bnd-{_run}" });
            var response = (AddToQueueResponse)await ExecuteAsync(new AddToQueueRequest
            {
                Target = new EntityReference("email", email), DestinationQueueId = queue,
            });
            var stored = (await ColumnAsync("SELECT queueitemid FROM data.queueitem WHERE objectid = @id", email)).Should().ContainSingle().Subject;
            return (response.QueueItemId, Guid.Parse(stored));
        }

        var unobserved = await AddAsync();
        unobserved.Answered.Should().Be(unobserved.Stored);

        await StepAsync("ThrowOnNamePlugin", "Create", "queueitem", 40, "never|x");
        var observed = await AddAsync();
        observed.Answered.Should().Be(observed.Stored);
    }

    /// <summary>
    /// The Delete of an email with an attachment: the core issues the attachment's Delete as a marked cascade, the handler of that Delete
    /// runs on the core's own request (its mark kept; S.2e3b: the model request, seen through <see cref="CoreOperations.CrudRequest"/>,
    /// whose SDK twin in the chain mirror is a <c>DeleteRequest</c> carrying the SDK mark), the child's step sees the email's Delete as its
    /// parent, and a shared variable the child's step sets on its parent reaches the outer stage 40.
    /// </summary>
    [Fact]
    public async Task Core_cascade_nested_step_sees_the_core_message_as_parent()
    {
        const string Attachment = "activitymimeattachment";
        var email = await Admin.CreateAsync(new Entity("email") { ["subject"] = $"bnd-{_run}" });
        await Admin.CreateAsync(new Entity(Attachment)
        {
            ["objectid"] = new EntityReference("email", email), ["subject"] = "a", ["filename"] = "a.txt",
            ["body"] = Convert.ToBase64String("bnd"u8.ToArray()),
        });
        await StepAsync("ContextRecorderPlugin", "Delete", Attachment, 20, DumpLog);
        await StepAsync("SharedVariablePlugin", "Delete", Attachment, 40, "setparent|k|v");
        await StepAsync("SharedVariablePlugin", "Delete", "email", 40, $"record|{PipeLog}|k");
        var handled = new List<Xrmd.Model.Messages.Request>();

        CoreOperations.CrudRequest.Value = handled.Add;
        try
        {
            await ExecuteAsync(new DeleteRequest { Target = new EntityReference("email", email) });
        }
        finally
        {
            CoreOperations.CrudRequest.Value = null;
        }

        var child = handled.Should().ContainSingle(r => r.Name == "Delete"
            && ((Xrmd.Model.Records.RecordReference)r.Parameters["Target"]!).LogicalName == Attachment).Subject;
        CascadeHint.IsMarked(child).Should().BeTrue("the core's cascade reaches its handler as the instance it marked");
        SdkMirror.For(child)!.TryGetSdk(child, out var twin).Should().BeTrue();
        CascadeHint.IsMarked(twin.Should().BeOfType<DeleteRequest>().Subject).Should().BeTrue("the guard and the plug-ins see today's DeleteRequest");
        (await ColumnAsync($"SELECT pb_data FROM data.{DumpLog}")).Should().ContainSingle()
            .Which.Should().Contain("\nparent=Delete\n");
        (await ColumnAsync($"SELECT pb_name FROM data.{PipeLog}")).Should().Equal("Delete|40|1|v");
    }

    /// <summary>
    /// S.2e3b (Decision 8): the early-bound <c>PipeLog</c> that <c>EarlyBoundLogPlugin</c> creates reaches the core's model Create handler
    /// as the model record of that instance, holding the change a stage-20 step made in place (<see cref="CoreOperations.CrudRequest"/>);
    /// the core no longer holds the plug-in's early-bound <c>Entity</c> itself (it ran on it before S.2e3b). What callers see is today's:
    /// the stored row here, the queued payload in <see cref="Early_bound_target_is_queued_with_todays_payload"/>.
    /// </summary>
    [Fact]
    public async Task Early_bound_target_reaches_the_core_with_the_steps_change()
    {
        await EarlyBoundLogOnPipeCreateAsync();
        await StepAsync("SetAttributePlugin", "Create", PipeLog, 20, "pb_data=set-at-20");
        var handled = new List<Xrmd.Model.Messages.Request>();

        CoreOperations.CrudRequest.Value = handled.Add;
        try
        {
            await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" });
        }
        finally
        {
            CoreOperations.CrudRequest.Value = null;
        }

        var target = handled.Where(r => r.Name == "Create").Select(r => (Xrmd.Model.Records.Record)r.Parameters["Target"]!)
            .Should().ContainSingle(e => e.LogicalName == PipeLog).Subject;
        target.GetValue<string>("pb_data").Should().Be("set-at-20");
        SdkMirror.For(handled.Single(r => ReferenceEquals(r.Parameters["Target"], target)))!.TryGetSdk(target, out var twin).Should().BeTrue();
        twin!.GetType().Name.Should().Be("PipeLog", "the record's twin is still the plug-in's early-bound instance");
        (await ColumnAsync($"SELECT pb_name FROM data.{PipeLog}")).Should().Equal("early|p|1");
        (await ColumnAsync($"SELECT pb_data FROM data.{PipeLog}")).Should().Equal("set-at-20");
    }

    // ---- S.2e2c Task 6: payloads of an early-bound Target (review I2 of Task 5) -----------------------------------------

    private static readonly System.Text.RegularExpressions.Regex PayloadGuid =
        new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    private static readonly System.Text.RegularExpressions.Regex PayloadDate =
        new(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?|\\/Date\(-?\d+([+-]\d{4})?\)\\/");

    private static readonly System.Text.RegularExpressions.Regex PayloadRowVersion =
        new(@"(<RowVersion>|""RowVersion"":"")[^<""]*");

    /// <summary>
    /// A payload with every id numbered <c>{idN}</c> in first-seen order (<c>{empty}</c> for the empty id), dates as <c>{date}</c> and row
    /// versions as <c>{rowversion}</c>; everything else byte for byte.
    /// </summary>
    private static string NormalizedPayload(string payload)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = PayloadGuid.Replace(payload, m =>
        {
            if (Guid.Parse(m.Value) == Guid.Empty) return "{empty}";
            if (!ids.TryGetValue(m.Value, out var n)) ids[m.Value] = n = "{id" + (ids.Count + 1) + "}";
            return n;
        });
        text = PayloadDate.Replace(text, "{date}");
        return PayloadRowVersion.Replace(text, m => m.Groups[1].Value + "{rowversion}");
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        dir.Should().NotBeNull("the repository root holds xrmverse.sln");
        return dir!.FullName;
    }

    /// <summary>
    /// Compares <paramref name="payload"/>, normalized, with <c>tests/fixtures/pipeline-payload-golden/{name}</c>, captured from the code
    /// before Task 6 (commit 2c3b4a53). A missing file is written once and the test fails; an existing file is never rewritten.
    /// </summary>
    private static void AssertPayloadGolden(string name, string payload)
    {
        var path = Path.Combine(RepositoryRoot(), "tests", "fixtures", "pipeline-payload-golden", name);
        var text = NormalizedPayload(payload);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));
            Assert.Fail($"golden file {name} written, review and commit");
        }
        System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Should().Be(text);
    }

    private async Task<Guid> EarlyBoundLogOnPipeCreateAsync()
    {
        var assemblyId = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.EarlyBoundPluginBytes, "Xrmd.TestPlugins.EarlyBound");
        var typeId = await PluginTestHelper.RegisterTypeAsync(Admin, assemblyId, "Xrmd.TestPlugins.EarlyBound.EarlyBoundLogPlugin");
        return await PluginTestHelper.RegisterStepAsync(Admin, typeId, "Create", Pipe, 40);
    }

    /// <summary>
    /// An asynchronous step on the Create of <c>pb_pipelog</c> that <c>EarlyBoundLogPlugin</c> issues with an early-bound
    /// <c>PipeLog</c>: the queued <c>asyncoperation.data</c> is today's, the early-bound entity written with its own contract (the model
    /// would rebuild a plain <c>Entity</c>: its mirror twin is not plain, so the view is serialized as before).
    /// </summary>
    [Fact]
    public async Task Early_bound_target_is_queued_with_todays_payload()
    {
        await ExecAsync("DELETE FROM data.asyncoperation;");
        await EarlyBoundLogOnPipeCreateAsync();
        var typeId = await PluginTestHelper.RegisterTypeAsync(Admin, _assemblyId, Plugins + "SetAttributePlugin");
        var asyncStep = await PluginTestHelper.RegisterStepAsync(Admin, typeId, "Create", PipeLog, 40, mode: 1, config: "pb_data=async");

        await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" });

        var data = (await ColumnAsync("SELECT data FROM data.asyncoperation WHERE owningextensionid = @id", asyncStep)).Should().ContainSingle().Subject;
        data.Should().Contain("PipeLog", "the early-bound entity keeps its contract in the payload");
        AssertPayloadGolden("early_bound_async.xml", data);
        await ExecAsync("DELETE FROM data.asyncoperation;");
    }

    /// <summary>
    /// The same early-bound Create with a synchronous webhook step: today the JSON serializer does not know the early-bound type, so
    /// the step fails final (500, the endpoint named, never the value), nothing is posted and the Create rolls back. The model would
    /// rebuild a plain <c>Entity</c> and post it: the twin check keeps today's outcome.
    /// </summary>
    [Fact]
    public async Task Early_bound_target_fails_a_webhook_step_as_today()
    {
        var options = fx.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PipelineOptions>>().Value;
        options.WebhookAllowAnyPort = true;
        using var receiver = new WebhookDeliveryTests.Receiver();
        try
        {
            await EarlyBoundLogOnPipeCreateAsync();
            var endpoint = await Admin.CreateAsync(new Entity(ServiceEndpointSchema.Table)
            {
                ["name"] = "payload hook", ["contract"] = new OptionSetValue(ServiceEndpointSchema.WebhookContract),
                ["authtype"] = new OptionSetValue(ServiceEndpointSchema.WebhookKey), ["url"] = receiver.Url,
                [ServiceEndpointSchema.AuthValue] = "p4yload-k3y",
            });
            await WebhookDeliveryTests.CreateWebhookStepAsync(Admin, endpoint, "Create", PipeLog, 40);

            var act = () => Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "p" });

            (await act.Should().ThrowAsync<Xrmd.Core.Errors.XrmdFaultException>()).Which.Message.Should().Be(
                "Endpoint unavailable: webhook 'payload hook' could not be sent the context: it holds a value that cannot be serialized.");
            receiver.Requests.Should().BeEmpty();
            (await ColumnAsync($"SELECT pb_name FROM data.{PipeLog}")).Should().BeEmpty();
            (await ColumnAsync($"SELECT pb_name FROM data.{Pipe}")).Should().BeEmpty();
        }
        finally
        {
            options.WebhookAllowAnyPort = false;
            await ExecAsync("DELETE FROM data.serviceendpoint; DELETE FROM meta.serviceendpoint_secret;");
        }
    }
}
