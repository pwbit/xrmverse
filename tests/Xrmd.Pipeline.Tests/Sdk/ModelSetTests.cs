using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Serialization;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Webhooks;
using MR = Xrmd.Model.Records;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>
/// S.2e2c, Task 6: the payloads of the asynchronous queue, the worker and the webhooks written from the model context.
/// <see cref="ModelSet.CanCarry(RemoteContext)"/> is the S.2e2b rule on model values; with the mirror's
/// <see cref="SdkMirror.IsPlainTwin"/> it also refuses a value whose SDK twin is not plain (an early-bound entity, a value read with
/// extension data), so the bytes stay today's (review I2 of Task 5). Every expected payload here is computed by today's code path:
/// the SDK serializers over the view the mirror lends, and the worker as it rebuilt the context before this task.
/// </summary>
public class ModelSetTests
{
    private static readonly Guid G1 = new("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid G2 = new("dddddddd-0000-0000-0000-000000000002");
    private static readonly Guid G3 = new("dddddddd-0000-0000-0000-000000000003");
    private static readonly DateTime When = new(2026, 11, 25, 7, 8, 9, DateTimeKind.Utc);

    public static IEnumerable<object[]> Contexts() => ContextCorpus.ContextNames.Select(n => new object[] { n });

    public static IEnumerable<object[]> Outside() => OutsideContexts().Select(e => new object[] { e.Name });

    private static Entity EarlyBound()
    {
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Xrmd.TestPlugins.EarlyBound.dll"));
        var entity = (Entity)Activator.CreateInstance(assembly.GetType("Xrmd.TestPlugins.EarlyBound.PipeLog")!)!;
        entity.Id = G1;
        entity["pb_name"] = "n";
        return entity;
    }

    /// <summary>A plain entity read by the SDK reader from a payload with a member its contract does not list: it has extension data.</summary>
    private static Entity WithExtensionData()
    {
        var context = new RemoteExecutionContext();
        context.InputParameters["Target"] = new Entity("account", G1) { ["name"] = "x" };
        var xml = SdkReferenceSerializers.SerializeContext(context);
        var at = xml.IndexOf("<RowVersion i:nil=\"true\" /></d2p1:value></KeyValuePairOfstringanyType></InputParameters>", StringComparison.Ordinal);
        Assert.True(at > 0, xml);
        xml = xml.Insert(at + "<RowVersion i:nil=\"true\" />".Length, "<Zzz>unknown</Zzz>");
        var read = (Entity)SdkReferenceSerializers.DeserializeContext(xml).InputParameters["Target"];
        Assert.NotNull(read.ExtensionData);
        return read;
    }

    private static RemoteExecutionContext With(Action<RemoteExecutionContext> fill)
    {
        var context = new RemoteExecutionContext { MessageName = "Create" };
        fill(context);
        return context;
    }

    private static Entity Nested(int levels)
    {
        var e = new Entity("account", G1) { ["name"] = "leaf" };
        for (var i = 0; i < levels; i++) e = new Entity("account", G1) { ["child"] = e };
        return e;
    }

    private static RemoteExecutionContext Chain(int length)
    {
        var context = new RemoteExecutionContext { MessageName = "Create", Depth = 1 };
        for (var i = 0; i < length; i++) context = new RemoteExecutionContext { MessageName = "Update", Depth = i + 2, ParentContext = context };
        return context;
    }

    private static QueryExpression Filters(int levels)
    {
        var query = new QueryExpression("account");
        var filter = query.Criteria;
        for (var i = 0; i < levels; i++) filter = filter.AddFilter(LogicalOperator.And);
        filter.AddCondition("name", ConditionOperator.Equal, "x");
        return query;
    }

    /// <summary>
    /// Contexts on both sides of the rule beyond the corpus: values outside the model set, the nesting bound, null elements, typed
    /// arrays where a query does and does not take them, and the values whose SDK form only the twin check sees (early-bound, extension
    /// data). <c>PlainToModel</c> false: <c>ToModel()</c> cannot stand for the SDK object (it throws, or rebuilds a plain type).
    /// </summary>
    private static IEnumerable<(string Name, RemoteExecutionContext Context, bool PlainToModel)> OutsideContexts()
    {
        yield return ("managed-property", With(c => c.SharedVariables["flag"] = new BooleanManagedProperty(true)), true);
        yield return ("int-array", With(c => c.InputParameters["ids"] = new[] { 1, 2 }), true);
        yield return ("object-array", With(c => c.InputParameters["o"] = new object[] { "a" }), true);
        yield return ("guid-array", With(c => c.InputParameters["g"] = new[] { G1 }), true);
        yield return ("paging-value", With(c => c.InputParameters["p"] = new PagingInfo()), true);
        yield return ("inner-request", With(c => c.InputParameters["Request"] = new OrganizationRequest("WhoAmI")), true);
        yield return ("managed-property-in-attribute", With(c => c.InputParameters["Target"] = new Entity("a") { ["m"] = new BooleanManagedProperty(true) }), true);
        yield return ("managed-property-in-query", With(c =>
        {
            var q = new QueryExpression("account");
            q.Criteria.AddCondition("a", ConditionOperator.Equal, new BooleanManagedProperty(true));
            c.InputParameters["Query"] = q;
        }), true);
        yield return ("money-array-in-query-in-context", With(c =>
        {
            var q = new QueryExpression("account");
            q.Criteria.AddCondition("a", ConditionOperator.In, new Money[] { new(1m) });
            c.InputParameters["Query"] = q;
        }), true);
        yield return ("entity-array-aliased-in-query-in-context", With(c =>
        {
            var q = new QueryByAttribute("account");
            q.Attributes.Add("a");
            q.Values.Add(new AliasedValue("a", "b", new Entity[] { new("x") }));
            c.InputParameters["Query"] = q;
        }), true);
        yield return ("entity-array-in-key", With(c =>
        {
            var r = new EntityReference("account");
            r.KeyAttributes["k"] = new Entity[] { new("x") };
            c.InputParameters["Ref"] = r;
        }), true);
        foreach (var n in new[] { 20, 60, 90, 130 })
        {
            yield return ($"nested-entities-{n}", With(c => c.SharedVariables["tree"] = Nested(n)), true);
            yield return ($"parent-chain-{n}", Chain(n), true);
            yield return ($"nested-filters-{n}", With(c => c.InputParameters["Query"] = Filters(n)), true);
        }
        yield return ("null-in-collection", With(c => c.InputParameters["page"] = new EntityCollection(new List<Entity> { null! })), false);
        yield return ("null-image", With(c => c.PreEntityImages["pre"] = null!), false);
        yield return ("early-bound-target", With(c => c.InputParameters["Target"] = EarlyBound()), false);
        yield return ("early-bound-image", With(c => c.PostEntityImages["post"] = EarlyBound()), false);
        yield return ("early-bound-in-collection", With(c => c.InputParameters["c"] = new EntityCollection(new List<Entity> { EarlyBound() })), false);
        yield return ("early-bound-in-array", With(c => c.InputParameters["a"] = new Entity[] { EarlyBound() }), false);
        yield return ("early-bound-aliased", With(c => c.InputParameters["al"] = new AliasedValue("a", "b", EarlyBound())), false);
        yield return ("early-bound-in-parent", new RemoteExecutionContext { ParentContext = With(c => c.InputParameters["Target"] = EarlyBound()) }, false);
        yield return ("extension-data", With(c => c.InputParameters["Target"] = WithExtensionData()), false);
    }

    private static RemoteExecutionContext OutsideContext(string name) => OutsideContexts().First(e => e.Name == name).Context;

    // ---- the rule ---------------------------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(Contexts))]
    public void CanCarry_agrees_with_the_sdk_check(string name)
    {
        var context = ContextCorpus.Context(name);
        var expected = SdkRemoteContext.ModelCanCarry(context);
        Assert.Equal(expected, ModelSet.CanCarry(context.ToModel()));
        var mirror = new SdkMirror();
        Assert.Equal(expected, ModelSet.CanCarry(context.ToModel(mirror), mirror.IsPlainTwin));
    }

    [Theory, MemberData(nameof(Outside))]
    public void CanCarry_agrees_with_the_sdk_check_outside_the_corpus(string name)
    {
        var (_, context, plainToModel) = OutsideContexts().First(e => e.Name == name);
        var expected = SdkRemoteContext.ModelCanCarry(context);
        // Paired through a mirror, the model context stands for the SDK one exactly: same answer, with the twin check.
        var mirror = new SdkMirror();
        Assert.Equal(expected, ModelSet.CanCarry(context.ToModel(mirror), mirror.IsPlainTwin));
        if (plainToModel) Assert.Equal(expected, ModelSet.CanCarry(context.ToModel()));
    }

    [Fact]
    public void The_outside_list_has_both_answers()
    {
        var answers = OutsideContexts().Select(e => SdkRemoteContext.ModelCanCarry(e.Context)).ToList();
        Assert.Contains(true, answers);
        Assert.Contains(false, answers);
    }

    [Fact]
    public void An_early_bound_entity_is_plain_in_the_model_and_refused_only_by_its_twin()
    {
        var context = OutsideContext("early-bound-target");
        Assert.False(SdkRemoteContext.ModelCanCarry(context));
        // A plain conversion rebuilds a plain Entity: the model set alone cannot tell (review I2 of Task 5).
        Assert.True(ModelSet.CanCarry(context.ToModel()));
        var mirror = new SdkMirror();
        var model = context.ToModel(mirror);
        Assert.True(ModelSet.CanCarry(model));
        Assert.False(ModelSet.CanCarry(model, mirror.IsPlainTwin));
    }

    [Fact]
    public void IsPlainTwin_admits_an_unpaired_object_and_a_plain_twin_only()
    {
        var mirror = new SdkMirror();
        Assert.True(mirror.IsPlainTwin(new MR.Record("account", G1)));
        var plain = (MR.Record)mirror.In(new Entity("account", G1))!;
        Assert.True(mirror.IsPlainTwin(plain));
        Assert.False(mirror.IsPlainTwin(mirror.In(EarlyBound())!));
        Assert.False(mirror.IsPlainTwin(mirror.In(WithExtensionData())!));
        var option = mirror.In(new OptionSetValue(3))!;
        Assert.True(mirror.IsPlainTwin(option));
    }

    // ---- the model overloads --------------------------------------------------------------------------------------------

    private static (T? Value, Exception? Error) Run<T>(Func<T> action)
    {
        try { return (action(), null); }
        catch (Exception e) { return (default, e); }
    }

    private static void AssertSameError(Exception? expected, Exception? actual, string what)
    {
        Assert.True(expected?.GetType() == actual?.GetType() && expected?.Message == actual?.Message,
            $"{what}: expected {expected?.GetType().Name} '{expected?.Message}', got {actual?.GetType().Name} '{actual?.Message}'");
    }

    [Theory, MemberData(nameof(Contexts))]
    public void Model_overloads_write_the_same_bytes(string name)
    {
        var context = ContextCorpus.Context(name);
        var model = context.ToModel();

        var (xml, xmlError) = Run(() => AsyncContextSerializer.Serialize(context));
        var (modelXml, modelXmlError) = Run(() => AsyncContextSerializer.Serialize(model));
        AssertSameError(xmlError, modelXmlError, name + " xml");
        Assert.Equal(xml, modelXml);

        var (json, jsonError) = Run(() => WebhookPayload.Serialize(context));
        var (modelJson, modelJsonError) = Run(() => WebhookPayload.Serialize(model));
        AssertSameError(jsonError, modelJsonError, name + " json");
        Assert.Equal(json, modelJson);

        foreach (var limit in new[] { WebhookPayload.SizeLimit, 1 })
        {
            var (built, builtError) = Run(() => WebhookPayload.Build(context, limit));
            var (modelBuilt, modelBuiltError) = Run(() => WebhookPayload.Build(model, limit));
            AssertSameError(builtError, modelBuiltError, $"{name} build {limit}");
            Assert.Equal(built.Trimmed, modelBuilt.Trimmed);
            Assert.Equal(built.Body, modelBuilt.Body);
        }
    }

    [Theory]
    [InlineData("managed-property")]
    [InlineData("parent-chain-130")]
    [InlineData("nested-entities-130")]
    public void Model_overloads_of_a_context_outside_the_set_take_the_sdk_serializers(string name)
    {
        var context = OutsideContext(name);
        var model = context.ToModel();
        Assert.False(ModelSet.CanCarry(model));
        Assert.Equal(SdkReferenceSerializers.SerializeContext(context), AsyncContextSerializer.Serialize(model));
        Assert.Equal(SdkReferenceSerializers.SerializeContextJson(context), WebhookPayload.Serialize(model));
        foreach (var limit in new[] { WebhookPayload.SizeLimit, 1 })
        {
            var expected = WebhookPayload.Build(context, limit);
            var actual = WebhookPayload.Build(model, limit);
            Assert.Equal(expected.Trimmed, actual.Trimmed);
            Assert.Equal(expected.Body, actual.Body);
        }
    }

    private static IEnumerable<string> Payloads()
    {
        foreach (var (_, context) in ContextCorpus.All()) yield return SdkReferenceSerializers.SerializeContext(context);
        yield return SdkReferenceSerializers.SerializeContext(OutsideContext("managed-property"));
        yield return SdkReferenceSerializers.SerializeContext(OutsideContext("parent-chain-130"));
        yield return SdkReferenceSerializers.SerializeContext(OutsideContext("nested-entities-130"));
        yield return SdkReferenceSerializers.SerializeContext(With(c => c.InputParameters["Target"] = new Entity("account", G1) { ["name"] = "x" }))
            .Replace("<RowVersion i:nil=\"true\" /></d2p1:value></KeyValuePairOfstringanyType></InputParameters>", "<RowVersion i:nil=\"true\" /><Zzz>unknown</Zzz></d2p1:value></KeyValuePairOfstringanyType></InputParameters>", StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeModel_matches()
    {
        var count = 0;
        foreach (var xml in Payloads())
        {
            var expected = AsyncContextSerializer.Deserialize(xml);
            DeepEqual.AssertStrict(expected, AsyncContextSerializer.DeserializeModel(xml).ToSdk());
            var mirror = new SdkMirror();
            DeepEqual.AssertStrict(expected, AsyncContextSerializer.DeserializeModel(xml, mirror).ToSdk());
            count++;
        }
        Assert.True(count > 100, $"{count} payloads");
    }

    [Fact]
    public void DeserializeModel_of_a_fallback_payload_pairs_the_deserialized_instances()
    {
        var xml = SdkReferenceSerializers.SerializeContext(With(c =>
        {
            c.SharedVariables["flag"] = new BooleanManagedProperty(true);
            c.InputParameters["Target"] = new Entity("account", G1) { ["name"] = "x" };
        })).Replace("<RowVersion i:nil=\"true\" /></d2p1:value></KeyValuePairOfstringanyType></InputParameters>", "<RowVersion i:nil=\"true\" /><Zzz>unknown</Zzz></d2p1:value></KeyValuePairOfstringanyType></InputParameters>", StringComparison.Ordinal);
        Assert.False(ContextXml.TryRead(xml, out _));

        var mirror = new SdkMirror();
        var model = AsyncContextSerializer.DeserializeModel(xml, mirror);

        Assert.IsType<BooleanManagedProperty>(model.SharedVariables["flag"]);
        var target = Assert.IsType<MR.Record>(model.InputParameters["Target"]);
        Assert.True(mirror.TryGetSdk(target, out var sdk));
        Assert.NotNull(((Entity)sdk!).ExtensionData);
        Assert.False(ModelSet.CanCarry(model, mirror.IsPlainTwin), "the twin read with extension data goes back through the SDK writer");
    }

    // ---- the step context payload ----------------------------------------------------------------------------------------

    /// <summary>Today's payload of a step context: the SDK serializer over the view a fresh mirror lends (the queue before this task).</summary>
    private static string TodaysPayload(StepContext context, SdkMirror mirror)
    {
        var view = PluginContextAdapter.Lend(context, mirror);
        try
        {
            return AsyncContextSerializer.Serialize(AsyncContextSerializer.ToRemote(view));
        }
        finally
        {
            PluginContextAdapter.Return(view, mirror);
        }
    }

    private static StepContext Level(int level, string agent, Guid userAad, Guid initiatingAad)
    {
        var c = new StepContext
        {
            MessageName = "Update" + level, PrimaryEntityName = "account", PrimaryEntityId = G1, SecondaryEntityName = "none",
            Stage = 40, Mode = level == 0 ? 1 : 0, Depth = 3 - level, IsolationMode = 2, UserId = G1, InitiatingUserId = G2,
            BusinessUnitId = G3, OrganizationId = G2, OrganizationName = "contoso", CorrelationId = G3, RequestId = G1,
            OperationId = level == 0 ? G2 : Guid.Empty, OperationCreatedOn = When, IsInTransaction = level != 0,
            OwningExtension = new MR.RecordReference { LogicalName = "sdkmessageprocessingstep", Id = G1, Name = "step" + level },
            InitiatingUserAgent = agent, UserAzureActiveDirectoryObjectId = userAad, InitiatingUserAzureActiveDirectoryObjectId = initiatingAad,
        };
        var target = new MR.Record("account", G1) { ["name"] = "n" + level, ["choice"] = new MR.ChoiceValue(level), ["price"] = new MR.MoneyValue(1.5m) };
        c.InputParameters["Target"] = target;
        c.OutputParameters["id"] = G2;
        c.SharedVariables["counter"] = level;
        c.PreEntityImages["pre"] = new MR.Record("account", G1) { ["name"] = "old" + level };
        c.PostEntityImages["post"] = new MR.Record("account", G1) { ["name"] = "new" + level };
        return c;
    }

    private static readonly Guid UserAad = new("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid InitiatingAad = new("eeeeeeee-0000-0000-0000-000000000002");

    /// <summary>A queued chain three levels deep (the step's context and two parents), every level with an agent and Azure AD ids.</summary>
    private static StepContext QueuedChain(Action<StepContext>? configure = null)
    {
        var top = Level(0, "agent-0", UserAad, InitiatingAad);
        top.ParentContext = Level(1, "agent-1", UserAad, InitiatingAad);
        top.ParentContext.ParentContext = Level(2, "agent-2", UserAad, InitiatingAad);
        configure?.Invoke(top);
        return top;
    }

    [Fact]
    public void A_carried_step_context_is_written_by_the_model_with_todays_bytes()
    {
        var expected = TodaysPayload(QueuedChain(), new SdkMirror());
        var before = AsyncContextSerializer.SdkFallbackCount;
        var actual = AsyncContextSerializer.Serialize(QueuedChain(), new SdkMirror());
        Assert.Equal(before, AsyncContextSerializer.SdkFallbackCount);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void An_early_bound_twin_keeps_its_contract_in_the_payload()
    {
        var mirror = new SdkMirror();
        var context = QueuedChain(c => c.InputParameters["Target"] = mirror.In(EarlyBound()));
        var reference = new SdkMirror();
        var expected = TodaysPayload(QueuedChain(c => c.InputParameters["Target"] = reference.In(EarlyBound())), reference);
        Assert.Contains("PipeLog", expected);

        var before = AsyncContextSerializer.SdkFallbackCount;
        var actual = AsyncContextSerializer.Serialize(context, mirror);

        Assert.Equal(before + 1, AsyncContextSerializer.SdkFallbackCount);
        Assert.Contains("PipeLog", actual);
        Assert.Equal(TodaysPayload(context, mirror), actual);
    }

    [Fact]
    public void Pass_through_shared_variable_takes_the_fallback_once()
    {
        var mirror = new SdkMirror();
        var context = QueuedChain(c => c.SharedVariables["flag"] = new BooleanManagedProperty(true));
        var expected = TodaysPayload(QueuedChain(c => c.SharedVariables["flag"] = new BooleanManagedProperty(true)), new SdkMirror());

        var before = AsyncContextSerializer.SdkFallbackCount;
        var actual = AsyncContextSerializer.Serialize(context, mirror);
        Assert.Equal(before + 1, AsyncContextSerializer.SdkFallbackCount);
        Assert.Equal(expected, actual);

        // The model overload of a remote context takes the fallback once too.
        before = AsyncContextSerializer.SdkFallbackCount;
        Assert.Equal(expected, AsyncContextSerializer.Serialize(context.ToRemote()));
        Assert.Equal(before + 1, AsyncContextSerializer.SdkFallbackCount);
    }

    // ---- the worker ------------------------------------------------------------------------------------------------------

    private static readonly Guid Owner = new("ffffffff-0000-0000-0000-000000000001");
    private static readonly Guid OwnerBu = new("ffffffff-0000-0000-0000-000000000002");
    private static readonly Guid Creator = new("ffffffff-0000-0000-0000-000000000003");
    private static readonly Guid OwnerAad = new("ffffffff-0000-0000-0000-000000000004");
    private static readonly Guid CreatorAad = new("ffffffff-0000-0000-0000-000000000005");
    private static readonly Guid JobId = new("ffffffff-0000-0000-0000-000000000006");
    private static readonly Guid StepId = new("ffffffff-0000-0000-0000-000000000007");

    private static PluginStep AsyncStep() => new(StepId, "async step", "Update", "account", 40, 1, 1, null, null, null, null,
        Guid.Empty, "T", Guid.Empty, 0, []);

    private static AsyncOperationWorker.Job JobOf(string data) => new(JobId, StepId, data, 5, 0, G1, Owner, Creator, 1);

    private static readonly IReadOnlyDictionary<Guid, Guid> ObjectIds = new Dictionary<Guid, Guid> { [Owner] = OwnerAad, [Creator] = CreatorAad };

    /// <summary>
    /// The worker's rebuild before this task (commit 2c3b4a53), kept here as the reference: the SDK reader, <c>FromRemote(ToModel())</c>,
    /// the deserialized instances adopted through the mirror, then the same overrides.
    /// </summary>
    private static StepContext TodaysRebuild(string data, SdkMirror mirror)
    {
        var queued = AsyncContextSerializer.Deserialize(data);
        var job = JobOf(data);
        var step = AsyncStep();
        var context = StepContext.FromRemote(queued.ToModel());
        context.InputParameters = mirror.In(queued.InputParameters) ?? new ParameterBag();
        context.OutputParameters = mirror.In(queued.OutputParameters) ?? new ParameterBag();
        context.SharedVariables = mirror.In(queued.SharedVariables) ?? new ParameterBag();
        Images(context.PreEntityImages, queued.PreEntityImages, mirror);
        Images(context.PostEntityImages, queued.PostEntityImages, mirror);
        for ((StepContext? level, RemoteExecutionContext? remote) = (context.ParentContext, queued.ParentContext);
             level is not null && remote is not null; (level, remote) = (level.ParentContext, remote.ParentContext))
        {
            Values(level.InputParameters, remote.InputParameters, mirror);
            Values(level.OutputParameters, remote.OutputParameters, mirror);
            Values(level.SharedVariables, remote.SharedVariables, mirror);
            Images(level.PreEntityImages, remote.PreEntityImages, mirror);
            Images(level.PostEntityImages, remote.PostEntityImages, mirror);
        }
        context.Stage = 40;
        context.Mode = 1;
        context.Depth = job.Depth ?? queued.Depth;
        context.UserId = Owner;
        context.InitiatingUserId = Creator;
        context.BusinessUnitId = OwnerBu;
        context.CorrelationId = job.CorrelationId ?? queued.CorrelationId;
        context.OperationId = job.Id;
        context.OwningExtension = new MR.RecordReference { LogicalName = "sdkmessageprocessingstep", Id = step.Id, Name = step.Name };
        context.IsInTransaction = false;
        context.PostEntityImagesCollection = context.PreEntityImagesCollection;
        context.UserAzureActiveDirectoryObjectId = ObjectIds.GetValueOrDefault(Owner);
        context.InitiatingUserAzureActiveDirectoryObjectId = ObjectIds.GetValueOrDefault(Creator);
        ReservedSharedVariables.SnapshotChain(context);
        return context;

        static void Values(ParameterBag into, ParameterCollection? from, SdkMirror mirror)
        {
            into.Clear();
            if (mirror.In(from) is not { } twin) return;
            foreach (var (key, value) in twin) into[key] = value;
        }

        static void Images(IDictionary<string, MR.Record> into, EntityImageCollection? from, SdkMirror mirror)
        {
            into.Clear();
            foreach (var (key, image) in from ?? []) into[key] = (MR.Record)mirror.In(image)!;
        }
    }

    private static StepContext NewRebuild(string data, SdkMirror mirror) =>
        AsyncOperationWorker.Rebuild(AsyncContextSerializer.DeserializeModel(data, mirror), JobOf(data), AsyncStep(),
            new Caller(Owner, OwnerBu), Creator, ObjectIds);

    /// <summary>The context of a step of a nested call the job's plug-in makes: its parent is the job's context.</summary>
    private static StepContext NestedStep(StepContext job)
    {
        var child = Level(0, "agent-nested", UserAad, InitiatingAad);
        child.Depth = job.Depth + 1;
        child.ParentContext = job;
        return child;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Worker_shaped_chain_writes_todays_bytes_as_queued_and_after_the_worker_read(bool fallback)
    {
        Action<StepContext>? configure = fallback ? c => c.ParentContext!.ParentContext!.SharedVariables["flag"] = new BooleanManagedProperty(true) : null;

        // Position 1: as queued.
        var queued = TodaysPayload(QueuedChain(configure), new SdkMirror());
        foreach (var marker in new[] { "agent-0", "agent-1", "agent-2", UserAad.ToString(), InitiatingAad.ToString() })
            Assert.Contains(marker, queued);
        Assert.Equal(queued, AsyncContextSerializer.Serialize(QueuedChain(configure), new SdkMirror()));

        // Position 2: read by the worker, re-serialized as the parent of a nested step's context.
        var todaysMirror = new SdkMirror();
        var todays = TodaysRebuild(queued, todaysMirror);
        var expected = TodaysPayload(NestedStep(todays), todaysMirror);

        var mirror = new SdkMirror();
        var rebuilt = NewRebuild(queued, mirror);
        var before = AsyncContextSerializer.SdkFallbackCount;
        var actual = AsyncContextSerializer.Serialize(NestedStep(rebuilt), mirror);

        Assert.Equal(expected, actual);
        Assert.Equal(before + (fallback ? 1 : 0), AsyncContextSerializer.SdkFallbackCount);
        // The job's level keeps the payload's agent and gets the owner's and creator's object ids; the levels read from the payload drop theirs.
        Assert.Contains("agent-0", actual);
        Assert.Contains(OwnerAad.ToString(), actual);
        Assert.Contains(CreatorAad.ToString(), actual);
        Assert.DoesNotContain("agent-1", actual);
        Assert.DoesNotContain("agent-2", actual);
        Assert.Equal(1, Count(actual, UserAad.ToString()));
    }

    [Fact]
    public void Worker_overrides_exactly_todays_members()
    {
        var queued = TodaysPayload(QueuedChain(), new SdkMirror());
        var todays = TodaysRebuild(queued, new SdkMirror());
        var rebuilt = NewRebuild(queued, new SdkMirror());
        // Every member, the chain included, as the reference rebuild has it.
        DeepEqual.AssertStrict(todays.ToRemote().ToSdk(), rebuilt.ToRemote().ToSdk());
        for ((StepContext? a, StepContext? b) = (todays, rebuilt); a is not null || b is not null; (a, b) = (a?.ParentContext, b?.ParentContext))
        {
            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.Equal(a.FromRemoteLevel, b.FromRemoteLevel);
            Assert.Equal(a.InitiatingUserAgent, b.InitiatingUserAgent);
            Assert.Equal(a.UserAzureActiveDirectoryObjectId, b.UserAzureActiveDirectoryObjectId);
            Assert.Equal(a.InitiatingUserAzureActiveDirectoryObjectId, b.InitiatingUserAzureActiveDirectoryObjectId);
            Assert.Equal(a.OwningExtension.LogicalName, b.OwningExtension.LogicalName);
            Assert.Equal(a.PreEntityImagesCollection.Count, b.PreEntityImagesCollection.Count);
            Assert.Equal(ReferenceEquals(a.PreEntityImagesCollection, a.PostEntityImagesCollection),
                ReferenceEquals(b.PreEntityImagesCollection, b.PostEntityImagesCollection));
        }
    }

    [Fact]
    public void A_null_owning_extension_in_a_payload_becomes_a_reference_without_a_logical_name()
    {
        var xml = SdkReferenceSerializers.SerializeContext(new RemoteExecutionContext
        {
            MessageName = "Create", OwningExtension = null!, ParentContext = new RemoteExecutionContext { OwningExtension = null! },
        });
        var rebuilt = NewRebuild(xml, new SdkMirror());
        Assert.Null(rebuilt.ParentContext!.OwningExtension.LogicalName);
    }

    private static int Count(string text, string value)
    {
        var n = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
