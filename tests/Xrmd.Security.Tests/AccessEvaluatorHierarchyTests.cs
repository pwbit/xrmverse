using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Security;

namespace Xrmd.Security.Tests;

/// <summary>Plan 2.5, ruling H4: the rights a manager gets on a record through its <see cref="HierarchyReach"/>.</summary>
public sealed class AccessEvaluatorHierarchyTests
{
    private static readonly Guid Bu = new("00000000-0000-0000-0000-0000000000a0");
    private static readonly Guid Manager = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Direct = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid DirectTeam = new("00000000-0000-0000-0000-000000000012");
    private static readonly Guid Level2 = new("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Level2Team = new("00000000-0000-0000-0000-000000000013");
    private static readonly Guid Stranger = new("00000000-0000-0000-0000-000000000004");

    private static readonly EntityMetadata Account = new()
    {
        LogicalName = "account", SchemaName = "Account", OwnershipType = OwnershipTypes.UserOwned,
    };
    private static readonly EntityMetadata Setting = new()
    {
        LogicalName = "pb_setting", SchemaName = "pb_Setting", OwnershipType = OwnershipTypes.OrganizationOwned,
    };

    private static readonly AccessRights[] Granted =
        [AccessRights.ReadAccess, AccessRights.WriteAccess, AccessRights.AppendAccess, AccessRights.AppendToAccess];
    private static readonly AccessRights[] Never =
        [AccessRights.CreateAccess, AccessRights.DeleteAccess, AccessRights.AssignAccess, AccessRights.ShareAccess];

    private static readonly HierarchyReach Reach = new(
        ReadPrincipals: new HashSet<Guid> { Direct, DirectTeam, Level2, Level2Team },
        WritePrincipals: new HashSet<Guid> { Direct, DirectTeam },
        ExcludedTables: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The manager, holding every privilege on account at Basic unless <paramref name="without"/>.</summary>
    private static SecurityProfile Profile(HierarchyReach? reach = null, params string[] without)
    {
        var privileges = new Dictionary<string, DepthMask>(StringComparer.OrdinalIgnoreCase);
        foreach (var verb in new[] { "Create", "Read", "Write", "Delete", "Append", "AppendTo", "Assign", "Share" })
            foreach (var schema in new[] { "Account", "pb_Setting" })
                if (!without.Contains($"prv{verb}{schema}", StringComparer.OrdinalIgnoreCase))
                    privileges[$"prv{verb}{schema}"] = DepthMask.Basic;
        return new SecurityProfile(Manager, Bu, IsDisabled: false, IsSystemAdministrator: false, HasRoles: true,
            OwnerTeamIds: new HashSet<Guid>(), TeamIds: new HashSet<Guid>(), privileges, DirectPrivileges: new HashSet<string>())
        {
            Hierarchy = reach ?? Reach,
        };
    }

    private static RecordOwnership OwnedBy(Guid user) => new(user, null, Bu);

    private static RecordOwnership OwnedByTeam(Guid team) => new(null, team, Bu);

    private static bool Has(SecurityProfile p, RecordOwnership owner, AccessRights right, AccessRights shares = 0,
        EntityMetadata? entity = null) =>
        AccessEvaluator.HasHierarchyAccess(p, entity ?? Account, owner, right, shares);

    [Fact]
    public void Direct_report_records_give_read_write_append_and_append_to()
    {
        foreach (var right in Granted) Has(Profile(), OwnedBy(Direct), right).Should().BeTrue(right.ToString());
        foreach (var right in Never) Has(Profile(), OwnedBy(Direct), right).Should().BeFalse(right.ToString());
    }

    [Fact]
    public void Level_two_report_records_give_read_only()
    {
        Has(Profile(), OwnedBy(Level2), AccessRights.ReadAccess).Should().BeTrue();
        foreach (var right in Granted.Skip(1).Concat(Never))
            Has(Profile(), OwnedBy(Level2), right).Should().BeFalse(right.ToString());
    }

    [Fact]
    public void Report_team_records_count_as_the_reports()
    {
        foreach (var right in Granted) Has(Profile(), OwnedByTeam(DirectTeam), right).Should().BeTrue(right.ToString());
        Has(Profile(), OwnedByTeam(Level2Team), AccessRights.ReadAccess).Should().BeTrue();
        Has(Profile(), OwnedByTeam(Level2Team), AccessRights.WriteAccess).Should().BeFalse();
    }

    [Fact]
    public void Shares_with_reports_give_at_most_the_shared_rights()
    {
        var shares = AccessRights.ReadAccess | AccessRights.WriteAccess;

        Has(Profile(), OwnedBy(Stranger), AccessRights.ReadAccess, shares).Should().BeTrue();
        Has(Profile(), OwnedBy(Stranger), AccessRights.WriteAccess, shares).Should().BeTrue();
        foreach (var right in new[] { AccessRights.AppendAccess, AccessRights.AppendToAccess }.Concat(Never))
            Has(Profile(), OwnedBy(Stranger), right, shares).Should().BeFalse(right.ToString());
        Has(Profile(), OwnedBy(Stranger), AccessRights.WriteAccess, AccessRights.ReadAccess).Should().BeFalse("a read-only share");
        Has(Profile(), OwnedBy(Stranger), AccessRights.DeleteAccess, AccessRights.DeleteAccess).Should().BeFalse("never Delete");
    }

    [Fact]
    public void Stranger_records_give_nothing()
    {
        foreach (var right in Granted) Has(Profile(), OwnedBy(Stranger), right).Should().BeFalse(right.ToString());
    }

    [Fact]
    public void Manager_without_the_privilege_gets_nothing()
    {
        var p = Profile(without: "prvWriteAccount");

        Has(p, OwnedBy(Direct), AccessRights.WriteAccess).Should().BeFalse();
        Has(p, OwnedBy(Stranger), AccessRights.WriteAccess, AccessRights.WriteAccess).Should().BeFalse();
        Has(p, OwnedBy(Direct), AccessRights.ReadAccess).Should().BeTrue();
    }

    [Fact]
    public void Uncovered_tables_and_no_reach_give_nothing()
    {
        var excluded = Reach with { ExcludedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "account" } };
        var none = Profile() with { Hierarchy = null };

        Has(Profile(excluded), OwnedBy(Direct), AccessRights.ReadAccess).Should().BeFalse();
        Has(Profile(), OwnedBy(Direct), AccessRights.ReadAccess, entity: Setting).Should().BeFalse();
        Has(none, OwnedBy(Direct), AccessRights.ReadAccess).Should().BeFalse();
    }
}
