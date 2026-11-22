using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Security;

namespace Xrmd.Security.Tests;

public sealed class AccessEvaluatorTests
{
    private static readonly Guid R = new("00000000-0000-0000-0000-0000000000a0");
    private static readonly Guid A = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid A1 = new("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid B = new("00000000-0000-0000-0000-0000000000b0");
    private static readonly Guid Me = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Other = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid MyTeam = new("00000000-0000-0000-0000-000000000010");

    private static readonly BusinessUnitTree Tree = new(new Dictionary<Guid, Guid?> { [R] = null, [A] = R, [A1] = A, [B] = R });

    private static readonly EntityMetadata UserOwned = new() { LogicalName = "pb_project", OwnershipType = OwnershipTypes.UserOwned };
    private static readonly EntityMetadata OrganizationOwned = new() { LogicalName = "pb_setting", OwnershipType = OwnershipTypes.OrganizationOwned };
    private static readonly EntityMetadata BusinessOwned = new() { LogicalName = "team", OwnershipType = OwnershipTypes.BusinessOwned };

    private static SecurityProfile Profile(bool admin = false) => new(Me, A, IsDisabled: false, IsSystemAdministrator: admin, HasRoles: true,
        OwnerTeamIds: new HashSet<Guid> { MyTeam }, TeamIds: new HashSet<Guid> { MyTeam },
        Privileges: new Dictionary<string, DepthMask>(StringComparer.OrdinalIgnoreCase), DirectPrivileges: new HashSet<string>());

    private static Guid Bu(string name) => name switch { "R" => R, "A" => A, "A1" => A1, "B" => B, _ => throw new ArgumentException(name) };

    private static bool Read(EntityMetadata entity, DepthMask depth, RecordOwnership owner, AccessRights shared = 0, bool admin = false) =>
        AccessEvaluator.HasAccess(Profile(admin), Tree, entity, depth, owner, AccessRights.ReadAccess, shared);

    [Theory]
    [InlineData(DepthMask.Global, "B", false, true)]
    [InlineData(DepthMask.Deep, "A1", false, true)]
    [InlineData(DepthMask.Deep, "B", false, false)]
    [InlineData(DepthMask.Local, "A", false, true)]
    [InlineData(DepthMask.Local, "A1", false, false)]
    [InlineData(DepthMask.Basic, "A", true, true)]
    [InlineData(DepthMask.Basic, "A", false, false)]
    public void Depth_covers_owner(DepthMask depth, string ownerBu, bool ownedByMe, bool expected)
    {
        var owner = new RecordOwnership(ownedByMe ? Me : Other, null, Bu(ownerBu));

        Read(UserOwned, depth, owner).Should().Be(expected);
    }

    [Fact]
    public void Every_depth_above_basic_also_covers_own_records_elsewhere()
    {
        var mineInB = new RecordOwnership(Me, null, B);

        Read(UserOwned, DepthMask.Local, mineInB).Should().BeTrue();
        Read(UserOwned, DepthMask.Deep, mineInB).Should().BeTrue();
    }

    [Fact]
    public void Basic_covers_member_team_records()
    {
        Read(UserOwned, DepthMask.Basic, new RecordOwnership(null, MyTeam, B)).Should().BeTrue();
        Read(UserOwned, DepthMask.Basic, new RecordOwnership(null, Guid.NewGuid(), A)).Should().BeFalse();
    }

    [Fact]
    public void None_denies_even_when_shared()
    {
        Read(UserOwned, DepthMask.None, new RecordOwnership(Me, null, A), AccessRights.ReadAccess).Should().BeFalse();
        Read(OrganizationOwned, DepthMask.None, new RecordOwnership(null, null, R)).Should().BeFalse();
    }

    [Fact]
    public void Share_grants_right_with_any_depth()
    {
        var foreign = new RecordOwnership(Other, null, B);

        Read(UserOwned, DepthMask.Basic, foreign, AccessRights.ReadAccess | AccessRights.WriteAccess).Should().BeTrue();
        Read(UserOwned, DepthMask.Basic, foreign, AccessRights.WriteAccess).Should().BeFalse();
    }

    [Fact]
    public void Organization_owned_any_depth_grants()
    {
        foreach (var depth in new[] { DepthMask.Basic, DepthMask.Local, DepthMask.Deep, DepthMask.Global })
            Read(OrganizationOwned, depth, new RecordOwnership(null, null, B)).Should().BeTrue(depth.ToString());
    }

    [Fact]
    public void Ownership_none_grants_and_unknown_ownership_fails_closed()
    {
        var none = new EntityMetadata { LogicalName = "pb_none", OwnershipType = OwnershipTypes.None };
        Read(none, DepthMask.Basic, new RecordOwnership(null, null, B)).Should().BeTrue();

        foreach (var unknown in new EntityMetadata[]
                 {
                     new() { LogicalName = "pb_unknown", OwnershipType = (OwnershipTypes)999 },
                     new() { LogicalName = "pb_unset" },
                 })
        {
            var act = () => Read(unknown, DepthMask.Basic, new RecordOwnership(Other, null, B));
            act.Should().Throw<InvalidOperationException>().WithMessage($"*'{unknown.LogicalName}'*ownership type*");
        }
    }

    [Fact]
    public void Business_owned_basic_behaves_as_local()
    {
        Read(BusinessOwned, DepthMask.Basic, new RecordOwnership(null, null, A)).Should().BeTrue();
        Read(BusinessOwned, DepthMask.Basic, new RecordOwnership(null, null, A1)).Should().BeFalse();
        Read(BusinessOwned, DepthMask.Deep, new RecordOwnership(null, null, A1)).Should().BeTrue();
        Read(BusinessOwned, DepthMask.Deep, new RecordOwnership(null, null, B)).Should().BeFalse();
    }

    [Fact]
    public void System_administrator_has_everything()
    {
        Read(UserOwned, DepthMask.None, new RecordOwnership(Other, null, B), admin: true).Should().BeTrue();
        Profile(admin: true).DepthOf("prvReadpb_Project").Should().Be(DepthMask.Global);
    }

    [Fact]
    public void Depth_of_is_the_highest_granted_depth_case_insensitively()
    {
        var p = Profile() with
        {
            Privileges = new Dictionary<string, DepthMask>(StringComparer.OrdinalIgnoreCase) { ["prvReadpb_Project"] = DepthMask.Basic | DepthMask.Deep },
        };

        p.DepthOf("PRVREADPB_PROJECT").Should().Be(DepthMask.Deep);
        p.DepthOf("prvWritepb_Project").Should().Be(DepthMask.None);
    }

    [Theory]
    [InlineData(PrivilegeDepth.Basic, DepthMask.Basic)]
    [InlineData(PrivilegeDepth.Local, DepthMask.Local)]
    [InlineData(PrivilegeDepth.Deep, DepthMask.Deep)]
    [InlineData(PrivilegeDepth.Global, DepthMask.Global)]
    public void Sdk_depth_maps_to_mask(PrivilegeDepth sdk, DepthMask expected) => DepthMasks.FromSdk(sdk).Should().Be(expected);
}
