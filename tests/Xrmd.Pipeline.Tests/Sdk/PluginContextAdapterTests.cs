using System.Reflection;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using MR = Xrmd.Model.Records;

namespace Xrmd.Pipeline.Tests.Sdk;

public class PluginContextAdapterTests
{
    private static readonly Guid G1 = new("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid G2 = new("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid G3 = new("cccccccc-0000-0000-0000-000000000003");
    private static readonly DateTime When = new(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    private static StepContext Step(int level)
    {
        var c = new StepContext
        {
            MessageName = "Update" + level, PrimaryEntityName = "account", PrimaryEntityId = G1, SecondaryEntityName = "contact",
            Stage = 40 - level, Mode = 1, Depth = 4 - level, IsolationMode = 1, UserId = G1, InitiatingUserId = G2, BusinessUnitId = G3,
            OrganizationId = G2, OrganizationName = "contoso", CorrelationId = G3, RequestId = G1, OperationId = G2, OperationCreatedOn = When,
            OwningExtension = new MR.RecordReference { LogicalName = "sdkmessageprocessingstep", Id = G1, Name = "step" + level },
            IsInTransaction = true, IsExecutingOffline = true, IsOfflinePlayback = true, InitiatingUserAgent = "agent",
            InitiatingUserAzureActiveDirectoryObjectId = G3, UserAzureActiveDirectoryObjectId = G1,
            AuthenticatedUserId = G1, InitiatingUserApplicationId = G2, PortalsContactId = G3, TenantId = G1, IsPortalsClientCall = true,
            IsApplicationUser = true, EnvironmentId = "env",
        };
        c.InputParameters["Target"] = new MR.Record("account", G1) { ["name"] = "n" + level };
        c.InputParameters["flag"] = true;
        c.OutputParameters["id"] = G2;
        c.SharedVariables["counter"] = level;
        c.PreEntityImages["pre"] = new MR.Record("account", G1) { ["name"] = "old" + level };
        c.PostEntityImages["post"] = new MR.Record("account", G1) { ["name"] = "new" + level };
        c.PreEntityImagesCollection.Add(new Dictionary<string, MR.Record> { ["a"] = new MR.Record("account", G2) { ["name"] = "c0" } });
        c.PostEntityImagesCollection.Add(new Dictionary<string, MR.Record> { ["b"] = new MR.Record("account", G3) { ["name"] = "c1" } });
        if (level < 2) c.ParentContext = Step(level + 1);
        return c;
    }

    private static IEnumerable<PropertyInfo> Properties() =>
        typeof(IPluginExecutionContext7).GetInterfaces().Append(typeof(IPluginExecutionContext7))
            .SelectMany(i => i.GetProperties()).Where(p => p.Name != nameof(IPluginExecutionContext.ParentContext)).DistinctBy(p => p.Name);

    // The IPluginExecutionContext members of level i of Step(0)'s chain.
    private static void AssertMembers(IPluginExecutionContext level, int i)
    {
        level.MessageName.Should().Be("Update" + i);
        level.PrimaryEntityName.Should().Be("account");
        level.SecondaryEntityName.Should().Be("contact");
        level.PrimaryEntityId.Should().Be(G1);
        level.Stage.Should().Be(40 - i);
        level.Mode.Should().Be(1);
        level.Depth.Should().Be(4 - i);
        level.IsolationMode.Should().Be(1);
        level.UserId.Should().Be(G1);
        level.InitiatingUserId.Should().Be(G2);
        level.BusinessUnitId.Should().Be(G3);
        level.OrganizationId.Should().Be(G2);
        level.OrganizationName.Should().Be("contoso");
        level.CorrelationId.Should().Be(G3);
        level.RequestId.Should().Be(G1);
        level.OperationId.Should().Be(G2);
        level.OperationCreatedOn.Should().Be(When);
        level.OwningExtension.LogicalName.Should().Be("sdkmessageprocessingstep");
        level.OwningExtension.Id.Should().Be(G1);
        level.OwningExtension.Name.Should().Be("step" + i);
        level.IsInTransaction.Should().BeTrue();
        level.IsExecutingOffline.Should().BeTrue();
        level.IsOfflinePlayback.Should().BeTrue();
        ((Entity)level.InputParameters["Target"]).GetAttributeValue<string>("name").Should().Be("n" + i);
        level.InputParameters["flag"].Should().Be(true);
        level.OutputParameters["id"].Should().Be(G2);
        level.SharedVariables["counter"].Should().Be(i);
        level.PreEntityImages["pre"]["name"].Should().Be("old" + i);
        level.PostEntityImages["post"]["name"].Should().Be("new" + i);
    }

    [Fact]
    public void Default_view_equals_a_default_sdk_context()
    {
        var mirror = new SdkMirror();
        var view = PluginContextAdapter.Lend(new StepContext(), mirror);
        var expected = new XrmdPluginExecutionContext();
        var names = Properties().ToList();
        names.Should().Contain(p => p.Name == "OwningExtension").And.Contain(p => p.Name == "AuthenticatedUserId")
            .And.Contain(p => p.Name == "PreEntityImagesCollection");
        foreach (var p in names)
            p.GetValue(view).Should().BeEquivalentTo(p.GetValue(expected), p.Name);
        view.OwningExtension.LogicalName.Should().BeNull();
        view.ParentContext.Should().BeNull();
        view.Model.Should().NotBeNull();
    }

    [Fact]
    public void Every_member_reaches_the_view()
    {
        var mirror = new SdkMirror();
        var model = Step(0);
        var view = PluginContextAdapter.Lend(model, mirror);
        var level = view;
        for (var i = 0; i < 3; i++)
        {
            var m = i == 0 ? model : i == 1 ? model.ParentContext! : model.ParentContext!.ParentContext!;
            level.Should().NotBeNull();
            level!.MessageName.Should().Be("Update" + i);
            level.PrimaryEntityName.Should().Be("account");
            level.SecondaryEntityName.Should().Be("contact");
            level.PrimaryEntityId.Should().Be(G1);
            level.Stage.Should().Be(40 - i);
            level.Mode.Should().Be(1);
            level.Depth.Should().Be(4 - i);
            level.IsolationMode.Should().Be(1);
            level.UserId.Should().Be(G1);
            level.InitiatingUserId.Should().Be(G2);
            level.BusinessUnitId.Should().Be(G3);
            level.OrganizationId.Should().Be(G2);
            level.OrganizationName.Should().Be("contoso");
            level.CorrelationId.Should().Be(G3);
            level.RequestId.Should().Be(G1);
            level.OperationId.Should().Be(G2);
            level.OperationCreatedOn.Should().Be(When);
            level.OwningExtension.LogicalName.Should().Be("sdkmessageprocessingstep");
            level.OwningExtension.Id.Should().Be(G1);
            level.OwningExtension.Name.Should().Be("step" + i);
            level.IsInTransaction.Should().BeTrue();
            level.IsExecutingOffline.Should().BeTrue();
            level.IsOfflinePlayback.Should().BeTrue();
            level.InitiatingUserAgent.Should().Be("agent");
            level.InitiatingUserAzureActiveDirectoryObjectId.Should().Be(G3);
            level.UserAzureActiveDirectoryObjectId.Should().Be(G1);
            level.AuthenticatedUserId.Should().Be(G1);
            level.InitiatingUserApplicationId.Should().Be(G2);
            level.PortalsContactId.Should().Be(G3);
            level.TenantId.Should().Be(G1);
            level.IsPortalsClientCall.Should().BeTrue();
            level.IsApplicationUser.Should().BeTrue();
            level.EnvironmentId.Should().Be("env");
            ((Entity)level.InputParameters["Target"]).GetAttributeValue<string>("name").Should().Be("n" + i);
            level.InputParameters["flag"].Should().Be(true);
            level.OutputParameters["id"].Should().Be(G2);
            level.SharedVariables["counter"].Should().Be(i);
            level.PreEntityImages["pre"]["name"].Should().Be("old" + i);
            level.PostEntityImages["post"]["name"].Should().Be("new" + i);
            level.PreEntityImagesCollection.Should().HaveCount(1);
            level.PreEntityImagesCollection[0]["a"]["name"].Should().Be("c0");
            level.PostEntityImagesCollection[0]["b"]["name"].Should().Be("c1");
            level = (XrmdPluginExecutionContext?)level.ParentContext;
            m.Should().NotBeNull();
        }
        level.Should().BeNull();
    }

    [Fact]
    public void Target_changes_map_back()
    {
        var mirror = new SdkMirror();
        var model = Step(0);
        var target = (MR.Record)model.InputParameters["Target"]!;
        var view = PluginContextAdapter.Lend(model, mirror);

        var sdk = (Entity)view.InputParameters["Target"];
        sdk["name"] = "changed";
        sdk["extra"] = 5;
        PluginContextAdapter.Return(view, mirror);
        model.InputParameters["Target"].Should().BeSameAs(target);
        target["name"].Should().Be("changed");
        target["extra"].Should().Be(5);

        PluginContextAdapter.Lend(model, mirror);
        sdk.Attributes.Remove("extra");
        PluginContextAdapter.Return(view, mirror);
        target.Contains("extra").Should().BeFalse();

        PluginContextAdapter.Lend(model, mirror);
        var replacement = new Entity("account", G1) { ["name"] = "replaced" };
        view.InputParameters["Target"] = replacement;
        PluginContextAdapter.Return(view, mirror);
        var now = (MR.Record)model.InputParameters["Target"]!;
        now.Should().NotBeSameAs(target);
        now["name"].Should().Be("replaced");
    }

    [Fact]
    public void Output_and_shared_changes_map_back()
    {
        var mirror = new SdkMirror();
        var model = Step(0);
        var view = PluginContextAdapter.Lend(model, mirror);
        view.OutputParameters["result"] = "done";
        view.SharedVariables["seen"] = 7;
        view.ParentContext!.SharedVariables["fromChild"] = "x";
        view.ParentContext.ParentContext!.SharedVariables["deep"] = 1;
        view.InputParameters["added"] = G3;
        PluginContextAdapter.Return(view, mirror);
        model.OutputParameters["result"].Should().Be("done");
        model.SharedVariables["seen"].Should().Be(7);
        model.ParentContext!.SharedVariables["fromChild"].Should().Be("x");
        model.ParentContext.ParentContext!.SharedVariables["deep"].Should().Be(1);
        model.InputParameters["added"].Should().Be(G3);
        PluginContextAdapter.IsRunning(model, mirror).Should().BeFalse();
    }

    [Fact]
    public void Aliased_record_is_one_entity()
    {
        var mirror = new SdkMirror();
        var single = new StepContext { MessageName = "Create" };
        var record = new MR.Record("account", G1) { ["name"] = "a" };
        single.InputParameters["Target"] = record;
        var merged = single.Clone(20);
        merged.InputParameters = new ParameterBag { ["Targets"] = new MR.RecordCollection { Records = { record } } };
        var view = PluginContextAdapter.Lend(merged, mirror);
        var viaTargets = ((EntityCollection)view.InputParameters["Targets"]).Entities[0];
        viaTargets["name"] = "b";
        viaTargets["more"] = 1;
        PluginContextAdapter.Return(view, mirror);
        ((MR.RecordCollection)merged.InputParameters["Targets"]!).Records[0].Should().BeSameAs(record);
        record["name"].Should().Be("b");
        record["more"].Should().Be(1);

        // The single context's own view shows the same SDK entity as the merged view's Targets[0].
        var singleView = PluginContextAdapter.Lend(single, mirror);
        singleView.InputParameters["Target"].Should().BeSameAs(viaTargets);
    }

    [Fact]
    public void Lend_reuses_the_view_and_the_parent_view()
    {
        var mirror = new SdkMirror();
        var model = Step(0);
        var first = PluginContextAdapter.Lend(model, mirror);
        var parent = first.ParentContext;
        var input = first.InputParameters;
        var pre = first.PreEntityImages;
        PluginContextAdapter.Lend(model, mirror).Should().BeSameAs(first);
        first.ParentContext.Should().BeSameAs(parent);
        first.InputParameters.Should().BeSameAs(input);
        first.PreEntityImages.Should().BeSameAs(pre);
        PluginContextAdapter.ViewOf(model, mirror).Should().BeSameAs(first);
        PluginContextAdapter.ViewOf(model.ParentContext!, mirror).Should().BeSameAs(parent);
        PluginContextAdapter.ViewOf(Step(0), mirror).Should().BeNull();

        // A sibling stage shares the parent's view and the bags.
        var sibling = PluginContextAdapter.Lend(model.Clone(10), mirror);
        sibling.Should().NotBeSameAs(first);
        sibling.ParentContext.Should().BeSameAs(parent);
        sibling.InputParameters.Should().BeSameAs(first.InputParameters);
        sibling.SharedVariables.Should().BeSameAs(first.SharedVariables);
        sibling.PreEntityImages.Should().NotBeSameAs(first.PreEntityImages);
    }

    [Fact]
    public void Running_flag_sync_and_refresh_around_a_nested_call()
    {
        var mirror = new SdkMirror();
        var model = Step(0);
        var view = PluginContextAdapter.Lend(model, mirror);
        PluginContextAdapter.IsRunning(model, mirror).Should().BeTrue();

        view.SharedVariables["before"] = 1;
        PluginContextAdapter.Sync(model, mirror);
        model.SharedVariables["before"].Should().Be(1);
        PluginContextAdapter.IsRunning(model, mirror).Should().BeTrue();

        // A nested step sets one in the parent context.
        model.SharedVariables["nested"] = "n";
        PluginContextAdapter.Refresh(model, mirror);
        view.SharedVariables["nested"].Should().Be("n");
        view.SharedVariables["before"].Should().Be(1);
        PluginContextAdapter.Return(view, mirror);
        PluginContextAdapter.IsRunning(model, mirror).Should().BeFalse();
    }

    [Fact]
    public void Image_changes_are_not_written_back()
    {
        var mirror = new SdkMirror();
        var model = Step(0);
        var image = model.PreEntityImages["pre"];
        var view = PluginContextAdapter.Lend(model, mirror);
        view.PreEntityImages["pre"]["name"] = "tampered";
        view.PreEntityImages["added"] = new Entity("account");
        view.PreEntityImagesCollection[0]["a"]["name"] = "tampered";
        PluginContextAdapter.Return(view, mirror);
        image["name"].Should().Be("old0");
        model.PreEntityImages.Should().ContainSingle();
        model.PreEntityImagesCollection[0]["a"]["name"].Should().Be("c0");
    }

    private sealed class Early : Entity
    {
        public Early() : base("account") { }
    }

    [Fact]
    public void Pass_through_values_keep_their_instance()
    {
        var mirror = new SdkMirror();
        var model = new StepContext();
        var flag = new BooleanManagedProperty(true);
        var ints = new[] { 1, 2, 3 };
        model.SharedVariables["flag"] = flag;
        model.InputParameters["ints"] = ints;
        var view = PluginContextAdapter.Lend(model, mirror);
        view.SharedVariables["flag"].Should().BeSameAs(flag);
        view.InputParameters["ints"].Should().BeSameAs(ints);
        PluginContextAdapter.Return(view, mirror);
        model.SharedVariables["flag"].Should().BeSameAs(flag);
        model.InputParameters["ints"].Should().BeSameAs(ints);

        // An early-bound Entity subclass handed in by a plug-in as Target keeps its CLR type and instance.
        var early = new Early { ["name"] = "e" };
        view.InputParameters["Target"] = early;
        PluginContextAdapter.Return(view, mirror);
        var record = (MR.Record)model.InputParameters["Target"]!;
        PluginContextAdapter.Lend(model, mirror);
        view.InputParameters["Target"].Should().BeSameAs(early);
        ((Entity)view.InputParameters["Target"]).Should().BeOfType<Early>();
        record["name"].Should().Be("e");
    }

    [Fact]
    public void Service_provider_serves_the_view()
    {
        var mirror = new SdkMirror();
        var view = PluginContextAdapter.Lend(Step(0), mirror);
        var sp = new PluginServiceProvider(view, new TracingService(), null!,
            new PluginLogger(new TracingService(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        foreach (var t in new[] { typeof(IExecutionContext), typeof(IPluginExecutionContext), typeof(IPluginExecutionContext2),
                     typeof(IPluginExecutionContext3), typeof(IPluginExecutionContext4), typeof(IPluginExecutionContext5),
                     typeof(IPluginExecutionContext6), typeof(IPluginExecutionContext7) })
            sp.GetService(t).Should().BeSameAs(view);
    }

    [Fact]
    public void A_from_remote_level_is_lent_as_a_real_remote_execution_context()
    {
        var model = StepContext.FromRemote(Step(0).ToRemote());
        model.FromRemoteLevel.Should().BeFalse();
        model.ParentContext!.FromRemoteLevel.Should().BeTrue();
        model.ParentContext.ParentContext!.FromRemoteLevel.Should().BeTrue();
        var mirror = new SdkMirror();

        // The top of a FromRemote chain is an ordinary level: castable.
        var top = PluginContextAdapter.Lend(model, mirror);
        top.Should().BeAssignableTo<IPluginExecutionContext7>();

        var level = top.ParentContext;
        for (var i = 1; i <= 2; i++)
        {
            level.Should().BeOfType<RemoteExecutionContext>();
            var remote = (RemoteExecutionContext)level!;
            ((IPluginExecutionContext)remote).Should().NotBeAssignableTo<IPluginExecutionContext2>();
            level.Should().NotBeAssignableTo<IPluginExecutionContext7>();
            AssertMembers(remote, i);
            remote.InitiatingUserAgent.Should().Be("agent");
            remote.InitiatingUserAzureActiveDirectoryObjectId.Should().Be(G3);
            remote.UserAzureActiveDirectoryObjectId.Should().Be(G1);
            new System.Runtime.Serialization.DataContractSerializer(typeof(RemoteExecutionContext))
                .WriteObject(new MemoryStream(), remote);
            level = remote.ParentContext;
        }
        level.Should().BeNull();

        // Its bags map back (the instance's own collections are the pair of the model bags) and the view is reused.
        var parent = (RemoteExecutionContext)top.ParentContext!;
        parent.SharedVariables["fromChild"] = 1;
        parent.InputParameters["Target"] = new Entity("account", G1) { ["name"] = "viaRemote" };
        PluginContextAdapter.Return(top, mirror);
        model.ParentContext!.SharedVariables["fromChild"].Should().Be(1);
        ((MR.Record)model.ParentContext.InputParameters["Target"]!)["name"].Should().Be("viaRemote");
        PluginContextAdapter.Lend(model, mirror).ParentContext.Should().BeSameAs(parent);
        PluginContextAdapter.ViewOf(model.ParentContext, mirror).Should().BeNull("ViewOf answers plug-in views only");
    }

    [Fact]
    public void A_nested_call_keeps_the_callers_images_and_owning_extension()
    {
        var mirror = new SdkMirror();
        var caller = new StepContext { MessageName = "Update", Stage = 40, PrimaryEntityName = "account" };
        caller.InputParameters["Target"] = new MR.Record("account", G1) { ["name"] = "t" };
        caller.PreEntityImages["pre"] = new MR.Record("account", G1) { ["name"] = "old" };
        caller.OwningExtension = new MR.RecordReference { LogicalName = "sdkmessageprocessingstep", Id = G1, Name = "step" };
        var view = PluginContextAdapter.Lend(caller, mirror);
        var image = view.PreEntityImages["pre"];
        image["name"] = "merged";
        view.PreEntityImages["extra"] = new Entity("account");
        view.OwningExtension.Name = "renamed";
        var preImages = view.PreEntityImages;
        var owner = view.OwningExtension;

        // What the executor does around a nested call.
        PluginContextAdapter.Sync(caller, mirror);
        var nested = new StepContext { MessageName = "Create", Stage = 20, Depth = 2, ParentContext = caller };
        var nestedView = PluginContextAdapter.Lend(nested, mirror);
        nestedView.ParentContext.Should().BeSameAs(view);
        nestedView.ParentContext!.PreEntityImages.Should().BeSameAs(preImages);
        nestedView.ParentContext.PreEntityImages["pre"].Should().BeSameAs(image);
        nestedView.ParentContext.PreEntityImages["pre"]["name"].Should().Be("merged");
        PluginContextAdapter.Return(nestedView, mirror);
        PluginContextAdapter.Refresh(caller, mirror);

        view.PreEntityImages.Should().BeSameAs(preImages);
        view.PreEntityImages["pre"].Should().BeSameAs(image);
        image["name"].Should().Be("merged");
        view.PreEntityImages.Contains("extra").Should().BeTrue();
        view.OwningExtension.Should().BeSameAs(owner);
        view.OwningExtension.Name.Should().Be("renamed");
    }

    [Fact]
    public void A_replaced_bag_on_the_concrete_view_maps_back()
    {
        var mirror = new SdkMirror();
        var model = new StepContext { MessageName = "Update" };
        var view = PluginContextAdapter.Lend(model, mirror);
        var replaced = new ParameterCollection { ["x"] = 1 };
        view.SharedVariables = replaced;
        PluginContextAdapter.Return(view, mirror);
        model.SharedVariables["x"].Should().Be(1);
        PluginContextAdapter.Lend(model, mirror).SharedVariables.Should().BeSameAs(replaced);
    }

    [Fact]
    public void A_from_remote_level_cannot_be_the_lent_top()
    {
        var model = StepContext.FromRemote(new RemoteContext { ParentContext = new RemoteContext() }).ParentContext!;
        var act = () => PluginContextAdapter.Lend(model, new SdkMirror());
        act.Should().Throw<InvalidOperationException>();
    }

    // ---- Task 5 fix round (review I1): the parent chain is lent when first read ---------------------------------------

    [Fact]
    public void A_parent_is_lent_when_first_read_from_the_model_as_it_is_then()
    {
        var mirror = new SdkMirror();
        var model = Step(0);
        var lent = new List<StepContext>();
        var view = PluginContextAdapter.Lend(model, mirror, lent.Add);

        lent.Should().Equal([model], "only the lent level is written");
        PluginContextAdapter.ViewOf(model.ParentContext!, mirror).Should().BeNull("the parent is not lent before it is read");
        mirror.TryGetSdk(model.ParentContext!.InputParameters, out _).Should().BeFalse();

        model.ParentContext.SharedVariables["late"] = "l";
        var parent = view.ParentContext!;

        lent.Should().Equal([model, model.ParentContext], "the read lends that level only");
        parent.SharedVariables["late"].Should().Be("l", "the parent is written from the model when it is read");
        parent.MessageName.Should().Be("Update1");
        view.ParentContext.Should().BeSameAs(parent, "a read parent is kept");
        PluginContextAdapter.ViewOf(model.ParentContext, mirror).Should().BeSameAs(parent);
        parent.ParentContext!.MessageName.Should().Be("Update2");
        lent.Should().Equal([model, model.ParentContext, model.ParentContext.ParentContext!], "each level is lent when it is read");
    }

    [Fact]
    public void An_unread_parent_is_neither_written_nor_read_back()
    {
        var mirror = new SdkMirror();
        var model = Step(0);
        var view = PluginContextAdapter.Lend(model, mirror);
        view.SharedVariables["mine"] = 1;

        PluginContextAdapter.Sync(model, mirror);
        PluginContextAdapter.Refresh(model, mirror);
        PluginContextAdapter.Return(view, mirror);

        model.SharedVariables["mine"].Should().Be(1);
        PluginContextAdapter.ViewOf(model.ParentContext!, mirror).Should().BeNull("no read, no view of the parent");
        mirror.TryGetSdk(model.ParentContext!.InputParameters, out _).Should().BeFalse("its bags never crossed the mirror");
        mirror.TryGetSdk(model.ParentContext.SharedVariables, out _).Should().BeFalse();
    }

    [Fact]
    public void A_read_parent_stays_lent_across_a_nested_call_and_is_read_back()
    {
        var mirror = new SdkMirror();
        var model = Step(0);
        var view = PluginContextAdapter.Lend(model, mirror);
        var parent = view.ParentContext!;
        parent.SharedVariables["fromChild"] = "c";

        // Around a nested call, whose own step lends the caller as its parent again.
        PluginContextAdapter.Sync(model, mirror);
        model.ParentContext!.SharedVariables["fromChild"].Should().Be("c");
        var nested = new StepContext { MessageName = "Create", ParentContext = model };
        PluginContextAdapter.Lend(nested, mirror).ParentContext.Should().BeSameAs(view);
        model.ParentContext.SharedVariables["nested"] = "n";
        PluginContextAdapter.Refresh(model, mirror);

        view.ParentContext.Should().BeSameAs(parent);
        parent.SharedVariables["nested"].Should().Be("n", "a parent the plug-in holds is written again");
        parent.SharedVariables["after"] = "a";
        PluginContextAdapter.Return(view, mirror);
        model.ParentContext.SharedVariables["after"].Should().Be("a", "a parent the plug-in holds is read back");
    }

    /// <summary>
    /// Review C2: a running caller passes its own Target to a nested call; the nested step changes that entity, then reads its parent.
    /// The parent is lent after the step's own changes are read back, so they are kept.
    /// </summary>
    [Fact]
    public void Reading_the_parent_keeps_the_steps_changes_to_a_shared_entity()
    {
        var mirror = new SdkMirror();
        var sdkTarget = new Entity("account", G1) { ["name"] = "a" };
        var parentBag = mirror.In(new ParameterCollection { ["Target"] = sdkTarget })!;
        var parent = new StepContext { MessageName = "Update", InputParameters = parentBag, Stage = 40 };
        PluginContextAdapter.Lend(parent, mirror);
        PluginContextAdapter.Sync(parent, mirror);
        var nestedRequest = new ParameterCollection { ["Target"] = sdkTarget };
        var nestedBag = mirror.In(nestedRequest)!;
        mirror.Pair(nestedBag, nestedRequest);
        var child = new StepContext { MessageName = "Update", InputParameters = nestedBag, Stage = 20, ParentContext = parent, Depth = 2 };
        var view = PluginContextAdapter.Lend(child, mirror);
        var target = (Entity)view.InputParameters["Target"];
        target.Should().BeSameAs(sdkTarget);

        target["x"] = 1;
        var parentView = view.ParentContext!;

        target.Contains("x").Should().BeTrue("reading the parent does not undo the step's change");
        ((Entity)parentView.InputParameters["Target"]).Should().BeSameAs(sdkTarget);
        PluginContextAdapter.Return(view, mirror);
        sdkTarget["x"].Should().Be(1);
        ((MR.Record)nestedBag["Target"]!)["x"].Should().Be(1);
    }

    /// <summary>
    /// Final review M2: a view read after <see cref="PluginContextAdapter.Return"/> (a plug-in thread that outlived its step, a context
    /// kept in a static) is inert. Reading its parent late still gives the parent, but writes nothing of the returned view into the model.
    /// </summary>
    [Fact]
    public void A_parent_read_after_return_does_not_read_the_returned_view_back()
    {
        var mirror = new SdkMirror();
        var parent = new StepContext { MessageName = "Create" };
        var context = new StepContext { MessageName = "Update", ParentContext = parent, Depth = 2 };
        context.SharedVariables["x"] = 1;
        var view = PluginContextAdapter.Lend(context, mirror);
        PluginContextAdapter.Return(view, mirror);

        view.SharedVariables["x"] = 99;
        var parentView = view.ParentContext;

        parentView!.MessageName.Should().Be("Create");
        context.SharedVariables["x"].Should().Be(1, "a returned view is not read back into the model");
    }

    /// <summary>
    /// Final review M4: a clone of a lent view is no view of the model (no <c>Model</c>, not running, so the executor neither syncs nor
    /// refreshes it), and reading its parent late reads back neither the clone nor the original.
    /// </summary>
    [Fact]
    public void A_clone_of_a_lent_view_is_not_a_lent_view()
    {
        var mirror = new SdkMirror();
        var parent = new StepContext { MessageName = "Create" };
        var context = new StepContext { MessageName = "Update", ParentContext = parent, Depth = 2 };
        context.SharedVariables["x"] = 1;
        var view = PluginContextAdapter.Lend(context, mirror);

        var clone = view.Clone(40);
        PluginContextAdapter.Return(view, mirror);
        view.SharedVariables["x"] = 99;

        clone.Model.Should().BeNull();
        clone.ParentContext!.MessageName.Should().Be("Create");
        context.SharedVariables["x"].Should().Be(1, "the clone's parent read is no read-back of a returned view");
    }
}
