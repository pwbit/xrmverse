using FluentAssertions;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Record = Xrmd.Model.Records.Record;

namespace Xrmd.Model.Tests.Messages;

public class StepContextTests
{
    [Fact]
    public void Clone_shares_bags_and_not_images()
    {
        var ctx = new StepContext { Stage = 20 };
        ctx.PreEntityImages["pre"] = new Record("account");
        ctx.PreEntityImagesCollection.Add(new Dictionary<string, Record>());
        var owner = ctx.OwningExtension;

        var clone = ctx.Clone(40);

        clone.SharedVariables["k"] = 1;
        ctx.SharedVariables["k"].Should().Be(1);
        clone.InputParameters.Should().BeSameAs(ctx.InputParameters);
        clone.OutputParameters.Should().BeSameAs(ctx.OutputParameters);
        clone.SharedVariables.Should().BeSameAs(ctx.SharedVariables);
        clone.Stage.Should().Be(40);
        ctx.Stage.Should().Be(20);
        clone.PreEntityImages.Should().NotBeSameAs(ctx.PreEntityImages).And.BeEmpty();
        clone.PostEntityImages.Should().NotBeSameAs(ctx.PostEntityImages);
        clone.PreEntityImagesCollection.Should().NotBeSameAs(ctx.PreEntityImagesCollection).And.BeEmpty();
        clone.PostEntityImagesCollection.Should().NotBeSameAs(ctx.PostEntityImagesCollection);
        clone.OwningExtension.Should().NotBeSameAs(owner);
        clone.OwningExtension.LogicalName.Should().BeNull();
        ctx.PreEntityImages.Should().ContainKey("pre");
        ctx.PreEntityImagesCollection.Should().HaveCount(1);
    }

    [Fact]
    public void Clone_keeps_the_scalars_and_the_parent()
    {
        var parent = new StepContext { MessageName = "Create" };
        var ctx = new StepContext { MessageName = "Update", Depth = 3, ParentContext = parent, PrimaryEntityId = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var clone = ctx.Clone(40);
        clone.MessageName.Should().Be("Update");
        clone.Depth.Should().Be(3);
        clone.ParentContext.Should().BeSameAs(parent);
        clone.PrimaryEntityId.Should().Be(ctx.PrimaryEntityId);
        clone.TenantId.Should().Be(ctx.TenantId);
    }

    [Fact]
    public void New_context_defaults()
    {
        var ctx = new StepContext();
        ctx.InputParameters.Should().NotBeNull().And.BeEmpty();
        ctx.OutputParameters.Should().NotBeNull().And.BeEmpty();
        ctx.SharedVariables.Should().NotBeNull().And.BeEmpty();
        ctx.PreEntityImages.Should().NotBeNull().And.BeEmpty();
        ctx.PostEntityImages.Should().NotBeNull().And.BeEmpty();
        ctx.PreEntityImagesCollection.Should().NotBeNull().And.BeEmpty();
        ctx.PostEntityImagesCollection.Should().NotBeNull().And.BeEmpty();
        ctx.IsolationMode.Should().Be(2);
        ctx.Depth.Should().Be(1);
        ctx.MessageName.Should().Be("");
        ctx.PrimaryEntityName.Should().Be("none");
        ctx.SecondaryEntityName.Should().Be("none");
        ctx.OrganizationName.Should().Be("");
        ctx.OwningExtension.Should().NotBeNull();
        ctx.OwningExtension.LogicalName.Should().BeNull("like a new EntityReference");
        ctx.OwningExtension.Id.Should().BeEmpty();
        ctx.OwningExtension.Name.Should().BeNull();
        ctx.FromRemoteLevel.Should().BeFalse();
        ctx.ParentContext.Should().BeNull();
        ctx.RequestId.Should().BeNull();
        ctx.InitiatingUserAgent.Should().BeNull();
        ctx.EnvironmentId.Should().BeNull();
        ctx.Stage.Should().Be(0);
        ctx.Mode.Should().Be(0);
        ctx.IsInTransaction.Should().BeFalse();
        ctx.IsApplicationUser.Should().BeFalse();
        ctx.IsPortalsClientCall.Should().BeFalse();
    }

    [Fact]
    public void ToRemote_copies_bags_images_and_the_parent_chain()
    {
        var parent = new StepContext { MessageName = "Create", Depth = 1 };
        parent.SharedVariables["p"] = "x";
        var ctx = new StepContext { MessageName = "Update", ParentContext = parent };
        var image = new Record("account", Guid.NewGuid());
        ctx.PreEntityImages["pre"] = image;
        ctx.InputParameters["Target"] = image;

        var remote = ctx.ToRemote();

        remote.ParentContext.Should().NotBeNull();
        remote.ParentContext!.SharedVariables["p"].Should().Be("x");
        remote.InputParameters.Should().NotBeSameAs(ctx.InputParameters);
        remote.InputParameters["Target"].Should().BeSameAs(image, "values are shared, entries copied");
        remote.PreEntityImages.Should().NotBeSameAs(ctx.PreEntityImages);
        remote.PreEntityImages["pre"].Should().BeSameAs(image);
        ctx.SharedVariables["later"] = 1;
        remote.SharedVariables.Should().NotContainKey("later");
    }

    [Fact]
    public void FromRemote_of_a_null_owner_has_the_null_name_default()
    {
        var step = StepContext.FromRemote(new RemoteContext { OwningExtension = null });
        step.OwningExtension.LogicalName.Should().BeNull();
        step.FromRemoteLevel.Should().BeFalse("the level FromRemote returns is the caller's");
    }

    [Fact]
    public void FromRemote_leaves_the_top_unflagged_and_marks_every_parent_level()
    {
        var id = Guid.NewGuid();
        var remote = new RemoteContext { InitiatingUserAgent = "a", UserAzureActiveDirectoryObjectId = id, InitiatingUserAzureActiveDirectoryObjectId = id,
            ParentContext = new RemoteContext { InitiatingUserAgent = "b", UserAzureActiveDirectoryObjectId = id,
                ParentContext = new RemoteContext { InitiatingUserAgent = "c" } } };
        var step = StepContext.FromRemote(remote);
        step.FromRemoteLevel.Should().BeFalse();
        step.ParentContext!.FromRemoteLevel.Should().BeTrue();
        step.ParentContext.ParentContext!.FromRemoteLevel.Should().BeTrue();
        var back = step.ToRemote();
        back.InitiatingUserAgent.Should().Be("a");
        back.UserAzureActiveDirectoryObjectId.Should().Be(id);
        back.InitiatingUserAzureActiveDirectoryObjectId.Should().Be(id);
        back.ParentContext!.InitiatingUserAgent.Should().BeNull();
        back.ParentContext.UserAzureActiveDirectoryObjectId.Should().BeEmpty();
        back.ParentContext.ParentContext!.InitiatingUserAgent.Should().BeNull();
    }
}
