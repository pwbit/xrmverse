using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Security;

namespace Xrmd.Pipeline.Tests.Sdk;

public class SdkRecordsSecurityValuesTests
{
    [Fact]
    public void PrincipalAccess_round_trips()
    {
        var id = Guid.NewGuid();
        var sdk = new PrincipalAccess { Principal = new EntityReference("team", id), AccessMask = AccessRights.ReadAccess | AccessRights.ShareAccess };

        var model = Assert.IsType<PrincipalAccessValue>(SdkRecords.ValueToModel(sdk));
        Assert.Equal(AccessMask.ReadAccess | AccessMask.ShareAccess, model.AccessMask);
        Assert.Equal("team", model.Principal.LogicalName);
        Assert.Equal(id, model.Principal.Id);

        var back = Assert.IsType<PrincipalAccess>(SdkRecords.ValueToSdk(model));
        Assert.Equal("team", back.Principal.LogicalName);
        Assert.Equal(id, back.Principal.Id);
        Assert.Equal(AccessRights.ReadAccess | AccessRights.ShareAccess, back.AccessMask);
    }

    [Fact]
    public void PrincipalAccess_array_round_trips_with_its_exact_type()
    {
        var id = Guid.NewGuid();
        PrincipalAccess[] sdk = [new() { Principal = new EntityReference("systemuser", id), AccessMask = AccessRights.WriteAccess }];

        var model = Assert.IsType<PrincipalAccessValue[]>(SdkRecords.ValueToModel(sdk));
        Assert.Equal(id, model[0].Principal.Id);
        Assert.Equal(AccessMask.WriteAccess, model[0].AccessMask);

        var back = Assert.IsType<PrincipalAccess[]>(SdkRecords.ValueToSdk(model));
        Assert.Equal(id, back[0].Principal.Id);
        Assert.Equal(AccessRights.WriteAccess, back[0].AccessMask);
    }

    [Fact]
    public void RolePrivilege_array_round_trips()
    {
        var privilegeId = Guid.NewGuid();
        var unit = Guid.NewGuid();
        RolePrivilege[] sdk = [new(PrivilegeDepth.Global, privilegeId, unit) { PrivilegeName = "prvReadAccount" }];

        var model = Assert.IsType<PrivilegeGrant[]>(SdkRecords.ValueToModel(sdk));
        Assert.Equal(new PrivilegeGrant(privilegeId, "prvReadAccount", unit, PrivilegeScope.Global), model[0]);

        var back = Assert.IsType<RolePrivilege[]>(SdkRecords.ValueToSdk(model));
        Assert.Equal(PrivilegeDepth.Global, back[0].Depth);
        Assert.Equal(privilegeId, back[0].PrivilegeId);
        Assert.Equal("prvReadAccount", back[0].PrivilegeName);
        Assert.Equal(unit, back[0].BusinessUnitId);
    }

    [Fact]
    public void RolePrivilege_with_an_undefined_depth_is_refused()
    {
        RolePrivilege[] sdk = [new((PrivilegeDepth)99, Guid.NewGuid(), Guid.NewGuid())];
        Assert.Throws<ArgumentOutOfRangeException>(() => SdkRecords.ValueToModel(sdk));
    }

    [Fact]
    public void AccessRights_round_trips_as_an_enum()
    {
        var model = SdkRecords.ValueToModel(AccessRights.WriteAccess);
        Assert.Equal(AccessMask.WriteAccess, Assert.IsType<AccessMask>(model));
        var back = SdkRecords.ValueToSdk(model);
        Assert.Equal(AccessRights.WriteAccess, Assert.IsType<AccessRights>(back));
    }

    [Fact]
    public void ModelCanCarry_refuses_a_context_holding_PrincipalAccess()
    {
        var context = new RemoteExecutionContext();
        context.InputParameters["PrincipalAccess"] = new PrincipalAccess { Principal = new EntityReference("team", Guid.NewGuid()), AccessMask = AccessRights.ReadAccess };
        Assert.False(SdkRemoteContext.ModelCanCarry(context));
    }
}
