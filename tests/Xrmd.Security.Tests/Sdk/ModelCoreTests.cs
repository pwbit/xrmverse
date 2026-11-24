using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;
using Xrmd.Model.Security;
using Xrmd.Security.Sdk;
using Xunit;

namespace Xrmd.Security.Tests.Sdk;

/// <summary>The security core on the model (S.2d Task 2): the model members agree with their SDK twins.</summary>
public class ModelCoreTests
{
    private static EntityMetadata SdkTable(string name, string schema, bool? isActivity = null)
    {
        var e = new EntityMetadata { LogicalName = name, SchemaName = schema };
        if (isActivity is { } flag) typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.IsActivity))!.GetSetMethod(true)!.Invoke(e, [flag]);
        return e;
    }

    public static TheoryData<string, string, bool?> Tables => new()
    {
        { "email", "Email", true },
        { "xv_sms", "xv_Sms", true },
        { "activitypointer", "ActivityPointer", false },
        { "annotation", "Annotation", null },
        { "account", "Account", null },
        { "audit", "Audit", null },
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public void PrivilegeNames_model_matches_the_sdk_twin(string name, string schema, bool? isActivity)
    {
        var sdk = SdkTable(name, schema, isActivity);
        var model = sdk.ToModel();
        Assert.Equal(PrivilegeNames.Stem(sdk), PrivilegeNames.Stem(model));
        foreach (var right in Enum.GetValues<AccessRights>().Where(r => r != AccessRights.None))
            Assert.Equal(PrivilegeNames.For(right, sdk), PrivilegeNames.For(right.ToModel(), model));
    }

    [Fact]
    public void PrivilegeNames_model_rejects_a_right_without_verb()
    {
        var model = SdkTable("account", "Account").ToModel();
        Assert.Throws<Xrmd.Abstractions.XrmdValidationException>(() => PrivilegeNames.For(AccessMask.ReadAccess | AccessMask.WriteAccess, model));
    }

    [Fact]
    public void HasAccess_fails_closed_on_a_filtered_table()
    {
        var table = new TableDefinition { LogicalName = "x", OwnershipType = TableOwnership.Filtered };
        var profile = new SecurityProfile(Guid.NewGuid(), Guid.NewGuid(), IsDisabled: false, IsSystemAdministrator: false, HasRoles: true,
            OwnerTeamIds: new HashSet<Guid>(), TeamIds: new HashSet<Guid>(),
            Privileges: new Dictionary<string, DepthMask>(), DirectPrivileges: new HashSet<string>());
        var tree = new BusinessUnitTree(new Dictionary<Guid, Guid?> { [profile.BusinessUnitId] = null });
        var ex = Assert.Throws<InvalidOperationException>(() => AccessEvaluator.HasAccess(profile, tree, table, DepthMask.Basic,
            new RecordOwnership(null, null, null), AccessMask.ReadAccess, AccessMask.None));
        Assert.Equal("Entity 'x' has an unsupported ownership type Filtered.", ex.Message);
    }

    [Fact]
    public void HierarchyRights_match_the_sdk_value()
    {
        Assert.Equal(AccessMask.ReadAccess | AccessMask.WriteAccess | AccessMask.AppendAccess | AccessMask.AppendToAccess, AccessEvaluator.HierarchyRights);
    }

    [Fact]
    public void HierarchyReach_Covers_model_matches_the_sdk_twin()
    {
        var reach = new HierarchyReach(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "excluded" });
        foreach (var (name, ownership, intersect) in new[]
        {
            ("account", TableOwnership.UserOwned, false), ("team2", TableOwnership.TeamOwned, false), ("teammembership", TableOwnership.UserOwned, true),
            ("role", TableOwnership.BusinessOwned, false), ("fileattachment", TableOwnership.UserOwned, false), ("excluded", TableOwnership.UserOwned, false),
        })
        {
            var model = new TableDefinition { LogicalName = name, OwnershipType = ownership, IsIntersect = intersect };
            Assert.Equal(reach.Covers(model.SdkView()), reach.Covers(model));
        }
    }

    [Fact]
    public void DepthMasks_From_throws_for_record_filter_and_undefined_values()
    {
        Assert.Equal(DepthMask.Global, DepthMasks.From(PrivilegeScope.Global));
        Assert.Throws<ArgumentOutOfRangeException>(() => DepthMasks.From(PrivilegeScope.RecordFilter));
        Assert.Throws<ArgumentOutOfRangeException>(() => DepthMasks.From((PrivilegeScope)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => DepthMasks.FromSdk(PrivilegeDepth.RecordFilter));
        Assert.Throws<ArgumentOutOfRangeException>(() => DepthMasks.FromSdk((PrivilegeDepth)99));
    }

    [Fact]
    public void DepthMasks_FromSdk_undefined_value_throws_with_the_depth_name_and_text()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => DepthMasks.FromSdk((PrivilegeDepth)99));
        Assert.Equal("depth", ex.ParamName);
        Assert.Contains("Unknown privilege depth.", ex.Message);
    }

    [Fact]
    public void CombineGrants_gives_the_rows_of_Combine()
    {
        var unit = Guid.NewGuid();
        var read = PrivilegeNames.IdOf("prvReadAccount");
        var write = PrivilegeNames.IdOf("prvWriteAccount");
        UserPrivileges.Row[] rows =
        [
            new(read, "prvReadAccount", DepthMask.Local, true), new(read, "prvReadAccount", DepthMask.Global, true),
            new(write, "prvWriteAccount", DepthMask.Global, false), new(Guid.NewGuid(), "prvNone", DepthMask.None, true),
        ];
        var grants = UserPrivileges.CombineGrants(unit, rows);
        Assert.Equal(UserPrivileges.Combine(unit, rows).Select(p => p.ToModel()), grants);
        Assert.Equal(new[] { PrivilegeScope.Global, PrivilegeScope.Basic }, grants.Select(g => g.Depth));
    }
}
