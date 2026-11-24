using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Xrmd.Model.Security;
using Xrmd.Security.Sdk;
using Xunit;

namespace Xrmd.Security.Tests.Sdk;

public class SecurityTypeParityTests
{
    private static void AssertParity<TSdk, TModel>() where TSdk : struct, Enum where TModel : struct, Enum
    {
        Assert.Equal(Enum.GetNames<TSdk>().OrderBy(n => n, StringComparer.Ordinal), Enum.GetNames<TModel>().OrderBy(n => n, StringComparer.Ordinal));
        foreach (var name in Enum.GetNames<TSdk>())
            Assert.Equal(Convert.ToInt32(Enum.Parse<TSdk>(name)), Convert.ToInt32(Enum.Parse<TModel>(name)));
    }

    [Fact] public void AccessMask_matches_AccessRights() => AssertParity<AccessRights, AccessMask>();
    [Fact] public void PrivilegeScope_matches_PrivilegeDepth() => AssertParity<PrivilegeDepth, PrivilegeScope>();

    [Fact]
    public void Converters_round_trip_every_sdk_member()
    {
        foreach (var x in Enum.GetValues<AccessRights>()) Assert.Equal(x, x.ToModel().ToSdk());
        foreach (var x in Enum.GetValues<PrivilegeDepth>()) Assert.Equal(x, x.ToModel().ToSdk());
    }

    [Fact]
    public void All_access_bits_round_trip()
    {
        var all = (AccessRights)0x7FFFFFFF;
        Assert.Equal(all, all.ToModel().ToSdk());
        Assert.Equal(0x7FFFFFFF, (int)all.ToModel());
    }

    [Fact]
    public void Undefined_depth_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((PrivilegeDepth)99).ToModel());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((PrivilegeScope)99).ToSdk());
    }

    [Fact]
    public void RolePrivilege_round_trips_through_PrivilegeGrant()
    {
        var sdk = new RolePrivilege { PrivilegeId = Guid.NewGuid(), PrivilegeName = "prvReadAccount", BusinessUnitId = Guid.NewGuid(), Depth = PrivilegeDepth.Deep };
        var grant = sdk.ToModel();
        Assert.Equal(new PrivilegeGrant(sdk.PrivilegeId, "prvReadAccount", sdk.BusinessUnitId, PrivilegeScope.Deep), grant);
        var back = grant.ToSdk();
        Assert.Equal(sdk.PrivilegeId, back.PrivilegeId);
        Assert.Equal(sdk.PrivilegeName, back.PrivilegeName);
        Assert.Equal(sdk.BusinessUnitId, back.BusinessUnitId);
        Assert.Equal(sdk.Depth, back.Depth);
    }

    [Fact]
    public void RolePrivilege_public_properties_are_the_four_mapped_plus_two_unmodelled()
    {
        var names = typeof(RolePrivilege).GetProperties().Select(p => p.Name).Except(new[] { "ExtensionData" }).OrderBy(n => n);
        // RecordFilterId and RecordFilterUniqueName exist on the SDK type but are deliberately not modelled.
        Assert.Equal(new[] { "BusinessUnitId", "Depth", "PrivilegeId", "PrivilegeName", "RecordFilterId", "RecordFilterUniqueName" }, names);
    }
}
