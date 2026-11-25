using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Pipeline.Async;
using MR = Xrmd.Model.Records;

namespace Xrmd.Pipeline.Tests.Sdk;

public class StepContextParityTests
{
    private static readonly Guid G1 = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid G2 = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid G3 = new("bbbbbbbb-0000-0000-0000-000000000003");
    private static readonly DateTime When = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    // The same values twice, once as SDK objects and once as model objects, level 0 being the context and 1..3 its parents.
    private static XrmdPluginExecutionContext Sdk(int level)
    {
        var c = new XrmdPluginExecutionContext
        {
            MessageName = "Update" + level, PrimaryEntityName = "account", PrimaryEntityId = G1, SecondaryEntityName = "contact",
            Stage = 40 - level, Mode = 1, Depth = 4 - level, IsolationMode = 1, UserId = G1, InitiatingUserId = G2, BusinessUnitId = G3,
            OrganizationId = G2, OrganizationName = "contoso", CorrelationId = G3, RequestId = G1, OperationId = G2, OperationCreatedOn = When,
            OwningExtension = new EntityReference("sdkmessageprocessingstep", G1) { Name = "step" + level }, IsInTransaction = true,
            IsExecutingOffline = true, IsOfflinePlayback = true, InitiatingUserAgent = "agent", InitiatingUserAzureActiveDirectoryObjectId = G3,
            UserAzureActiveDirectoryObjectId = G1,
        };
        c.InputParameters["Target"] = new Entity("account", G1) { ["name"] = "n" + level, ["revenue"] = new Money(1.5m) };
        c.InputParameters["flag"] = true;
        c.OutputParameters["id"] = G2;
        c.SharedVariables["counter"] = level;
        c.PreEntityImages["pre"] = new Entity("account", G1) { ["name"] = "old" + level };
        c.PostEntityImages["post"] = new Entity("account", G1) { ["name"] = "new" + level, ["owner"] = new EntityReference("systemuser", G2) };
        if (level < 3) c.ParentContext = Sdk(level + 1);
        return c;
    }

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
            // Members the 30 of the remote context do not carry: set so the projection must leave them out.
            AuthenticatedUserId = G1, InitiatingUserApplicationId = G2, PortalsContactId = G3, TenantId = G1, IsPortalsClientCall = true,
            IsApplicationUser = true, EnvironmentId = "env",
        };
        var target = new MR.Record("account", G1);
        target["name"] = "n" + level;
        target["revenue"] = new MR.MoneyValue(1.5m);
        c.InputParameters["Target"] = target;
        c.InputParameters["flag"] = true;
        c.OutputParameters["id"] = G2;
        c.SharedVariables["counter"] = level;
        var pre = new MR.Record("account", G1);
        pre["name"] = "old" + level;
        c.PreEntityImages["pre"] = pre;
        var post = new MR.Record("account", G1);
        post["name"] = "new" + level;
        post["owner"] = new MR.RecordReference { LogicalName = "systemuser", Id = G2 };
        c.PostEntityImages["post"] = post;
        c.PreEntityImagesCollection.Add(new Dictionary<string, MR.Record> { ["x"] = pre });
        if (level < 3) c.ParentContext = Step(level + 1);
        return c;
    }

    // A deserialized remote context used as the parent of a fresh context: FromRemote returns its level unflagged, so the caller
    // that makes it a parent marks it.
    private static StepContext Remote(RemoteExecutionContext parent)
    {
        var level = StepContext.FromRemote(parent.ToModel());
        level.FromRemoteLevel = true;
        return level;
    }

    [Fact]
    public void ToRemote_matches_the_sdk_ToRemote()
    {
        var expected = AsyncContextSerializer.ToRemote(Sdk(0)).ToModel();
        var actual = Step(0).ToRemote();

        DeepEqual.AssertStrict(expected, actual);
        Assert.NotNull(actual.ParentContext?.ParentContext?.ParentContext);
    }

    [Fact]
    public void Default_context_and_its_clone_match_the_sdk_ToRemote()
    {
        DeepEqual.AssertStrict(AsyncContextSerializer.ToRemote(new XrmdPluginExecutionContext()).ToModel(), new StepContext().ToRemote());
        DeepEqual.AssertStrict(AsyncContextSerializer.ToRemote(new XrmdPluginExecutionContext().Clone(40)).ToModel(), new StepContext().Clone(40).ToRemote());
        DeepEqual.AssertStrict(AsyncContextSerializer.ToRemote(Sdk(0).Clone(40)).ToModel(), Step(0).Clone(40).ToRemote());
        DeepEqual.AssertStrict(
            AsyncContextSerializer.ToRemote(new XrmdPluginExecutionContext { ParentContext = new XrmdPluginExecutionContext() }).ToModel(),
            new StepContext { ParentContext = new StepContext() }.ToRemote());
    }

    [Fact]
    public void A_deserialized_remote_parent_loses_agent_and_aad_ids_as_today()
    {
        var remote = new RemoteExecutionContext { MessageName = "Create", InitiatingUserAgent = "agent", UserAzureActiveDirectoryObjectId = G1,
            InitiatingUserAzureActiveDirectoryObjectId = G2, Stage = 40,
            OwningExtension = new EntityReference("sdkmessageprocessingstep", G3) };
        remote.SharedVariables["k"] = 1;
        var parent = AsyncContextSerializer.Deserialize(AsyncContextSerializer.Serialize(remote));
        var sdk = new XrmdPluginExecutionContext { MessageName = "Update", ParentContext = parent, InitiatingUserAgent = "child" };

        var step = new StepContext { MessageName = "Update", InitiatingUserAgent = "child", ParentContext = Remote(parent) };

        var expected = AsyncContextSerializer.ToRemote(sdk);
        Assert.Null(expected.ParentContext!.InitiatingUserAgent);
        DeepEqual.AssertStrict(expected.ToModel(), step.ToRemote());
    }

    [Fact]
    public void Worker_shaped_context_matches_the_sdk_ToRemote()
    {
        var remote = new RemoteExecutionContext { MessageName = "Create", InitiatingUserAgent = "parent-agent", UserAzureActiveDirectoryObjectId = G1,
            InitiatingUserAzureActiveDirectoryObjectId = G2, OwningExtension = new EntityReference("sdkmessageprocessingstep", G3) };
        var parent = AsyncContextSerializer.Deserialize(AsyncContextSerializer.Serialize(remote));
        var sdk = Sdk(3);
        sdk.ParentContext = parent;
        var step = Step(3);
        step.ParentContext = Remote(parent);

        var expected = AsyncContextSerializer.ToRemote(sdk);
        Assert.Equal("agent", expected.InitiatingUserAgent);
        Assert.Null(expected.ParentContext!.InitiatingUserAgent);
        DeepEqual.AssertStrict(expected.ToModel(), step.ToRemote());
    }

    [Fact]
    public void FromRemote_round_trips()
    {
        var remote = RemoteContextGoldenTests.Build().ToModel();

        var back = StepContext.FromRemote(remote).ToRemote();

        DeepEqual.AssertStrict(remote, back);
        Assert.NotNull(back.ParentContext);

        remote.InitiatingUserAgent = "agent";
        remote.UserAzureActiveDirectoryObjectId = G1;
        DeepEqual.AssertStrict(remote, StepContext.FromRemote(remote).ToRemote());
    }

    [Fact]
    public void FromRemote_defaults_what_the_remote_context_lacks()
    {
        var step = StepContext.FromRemote(new RemoteContext());
        Assert.NotNull(step.OwningExtension);
        Assert.Empty(step.PreEntityImagesCollection);
        Assert.Equal(Guid.Empty, step.TenantId);
    }
}
