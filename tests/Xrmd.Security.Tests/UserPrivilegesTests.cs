using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;

namespace Xrmd.Security.Tests;

/// <summary>RetrieveUserPrivileges' computation (plan 3.5 UI-1a task 3): own roles keep their depth, team roles give Basic.</summary>
public class UserPrivilegesTests
{
    private static readonly Guid Unit = Guid.NewGuid();
    private static readonly Guid Read = PrivilegeNames.IdOf("prvReadAccount");
    private static readonly Guid Write = PrivilegeNames.IdOf("prvWriteAccount");

    private static UserPrivileges.Row Own(Guid id, string name, DepthMask mask) => new(id, name, mask, Direct: true);

    private static UserPrivileges.Row Team(Guid id, string name, DepthMask mask) => new(id, name, mask, Direct: false);

    [Fact]
    public void Own_role_keeps_depth()
    {
        var result = UserPrivileges.Combine(Unit, [Own(Read, "prvReadAccount", DepthMask.Deep)]);

        var privilege = result.Should().ContainSingle().Which;
        (privilege.PrivilegeId, privilege.PrivilegeName, privilege.Depth, privilege.BusinessUnitId)
            .Should().Be((Read, "prvReadAccount", PrivilegeDepth.Deep, Unit));
    }

    [Fact]
    public void Team_role_is_basic()
    {
        var result = UserPrivileges.Combine(Unit, [Team(Read, "prvReadAccount", DepthMask.Global)]);

        result.Should().ContainSingle().Which.Depth.Should().Be(PrivilegeDepth.Basic);
    }

    [Fact]
    public void Highest_depth_wins()
    {
        var result = UserPrivileges.Combine(Unit,
        [
            Own(Read, "prvReadAccount", DepthMask.Local),
            Own(Read, "prvReadAccount", DepthMask.Global),
            Team(Read, "prvReadAccount", DepthMask.Global),
            Own(Write, "prvWriteAccount", DepthMask.Basic),
        ]);

        result.Should().HaveCount(2);
        result.Single(p => p.PrivilegeId == Read).Depth.Should().Be(PrivilegeDepth.Global);
        result.Single(p => p.PrivilegeId == Write).Depth.Should().Be(PrivilegeDepth.Basic);
    }

    [Fact]
    public void Own_depth_is_kept_when_a_team_role_grants_the_same_privilege()
    {
        var result = UserPrivileges.Combine(Unit, [Team(Read, "prvReadAccount", DepthMask.Global), Own(Read, "prvReadAccount", DepthMask.Local)]);

        result.Should().ContainSingle().Which.Depth.Should().Be(PrivilegeDepth.Local);
    }

    [Fact]
    public void A_privilege_without_depth_is_not_returned()
    {
        UserPrivileges.Combine(Unit, [Own(Read, "prvReadAccount", DepthMask.None)]).Should().BeEmpty();
    }
}
