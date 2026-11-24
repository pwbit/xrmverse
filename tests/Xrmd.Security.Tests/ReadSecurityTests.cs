using System.Reflection;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;
using Xrmd.Query.Model;

namespace Xrmd.Security.Tests;

/// <summary>The filters <see cref="ReadSecurity.ReadFilter"/> builds and the ids <see cref="ReadSecurity.Expand"/> returns.</summary>
public sealed class ReadSecurityTests
{
    private static readonly Guid R = new("00000000-0000-0000-0000-0000000000a0");
    private static readonly Guid A = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid A1 = new("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid A2 = new("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid B = new("00000000-0000-0000-0000-0000000000b0");
    private static readonly Guid Me = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Other = new("00000000-0000-0000-0000-000000000002");

    private static readonly BusinessUnitTree Tree = new(new Dictionary<Guid, Guid?> { [R] = null, [A] = R, [A1] = A, [A2] = A1, [B] = R });

    private static readonly EntityMetadata Project = Entity("pb_project", "pb_Project", OwnershipTypes.UserOwned, 10001);
    private static readonly EntityMetadata Team = Entity("team", "Team", OwnershipTypes.BusinessOwned, 9);
    private static readonly EntityMetadata Setting = Entity("pb_setting", "pb_Setting", OwnershipTypes.OrganizationOwned, 10002);
    private static readonly EntityMetadata BusinessUnit = Entity("businessunit", "BusinessUnit", OwnershipTypes.BusinessOwned, 10);

    private static EntityMetadata Entity(string logicalName, string schemaName, OwnershipTypes? ownership, int otc, bool intersect = false)
    {
        var e = new EntityMetadata { LogicalName = logicalName, SchemaName = schemaName, OwnershipType = ownership };
        Set(e, nameof(EntityMetadata.ObjectTypeCode), otc);
        if (intersect) Set(e, nameof(EntityMetadata.IsIntersect), true);
        return e;
    }

    private static void Set(object target, string property, object value) =>
        target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.GetSetMethod(nonPublic: true)!.Invoke(target, [value]);

    private static SecurityProfile Profile(Guid user, Guid businessUnit, bool admin = false, params (string Name, DepthMask Depth)[] privileges) =>
        new(user, businessUnit, IsDisabled: false, IsSystemAdministrator: admin, HasRoles: true,
            OwnerTeamIds: new HashSet<Guid>(), TeamIds: new HashSet<Guid>(),
            Privileges: privileges.ToDictionary(p => p.Name, p => p.Depth, StringComparer.OrdinalIgnoreCase),
            DirectPrivileges: new HashSet<string>());

    private static ReadSecurity For(params SecurityProfile[] principals) =>
        new(principals, Tree, new Store(BusinessUnit, Project, Team), principals.FirstOrDefault()?.UserId ?? Me);

    private static ReadSecurity ReadingProject(DepthMask depth) => For(Profile(Me, A, privileges: ("prvReadpb_Project", depth)));

    // ---- ReadFilter -------------------------------------------------------------------------------------------------

    [Fact]
    public void No_read_privilege_is_PrivilegeDenied()
    {
        var act = () => For(Profile(Me, A)).ReadFilter(Project);
        act.Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.PrivilegeDenied && e.HttpStatus == 403
                        && e.Message.Contains("prvReadpb_Project") && e.Message.Contains("OTC=10001"));
    }

    [Fact]
    public void Unrestricted_cases_have_no_filter()
    {
        ReadingProject(DepthMask.Global).ReadFilter(Project).Should().BeNull();
        For(Profile(Me, A, admin: true)).ReadFilter(Project).Should().BeNull();
        For(Profile(Me, A, privileges: ("prvReadpb_Setting", DepthMask.Basic))).ReadFilter(Setting).Should().BeNull();
        // Intersect entities have no privilege and are readable by every member.
        For(Profile(Me, A)).ReadFilter(Entity("teammembership", "TeamMembership", OwnershipTypes.OrganizationOwned, 23, intersect: true))
            .Should().BeNull();
    }

    [Fact]
    public void Basic_is_owner_or_shared()
    {
        var f = ReadingProject(DepthMask.Basic).ReadFilter(Project)!;
        f.Operator.Should().Be(LogicalOperator.Or);
        f.Conditions.Should().ContainSingle().Which.Should().Match<ConditionNode>(c =>
            c.EntityAlias == null && c.Attribute == "ownerid" && c.Operator == ConditionOperator.EqualUserOrUserTeams && c.Values.Count == 0);
        f.Shared.Should().Equal(new SharedRecordsNode(Me, 10001, 1));
        f.Filters.Should().BeEmpty();
    }

    [Theory]
    [InlineData(DepthMask.Local, ConditionOperator.Equal)]
    [InlineData(DepthMask.Deep, ConditionOperator.UnderOrEqual)]
    public void Local_and_deep_add_the_business_unit(DepthMask depth, ConditionOperator op)
    {
        var f = ReadingProject(depth).ReadFilter(Project)!;
        f.Operator.Should().Be(LogicalOperator.Or);
        f.Conditions.Select(c => c.Operator).Should().Equal(ConditionOperator.EqualUserOrUserTeams, op);
        f.Conditions[1].Attribute.Should().Be("owningbusinessunit");
        f.Conditions[1].Values.Should().Equal(A);
        f.Shared.Should().ContainSingle();
    }

    [Theory]
    [InlineData(DepthMask.Basic, ConditionOperator.Equal)]
    [InlineData(DepthMask.Local, ConditionOperator.Equal)]
    [InlineData(DepthMask.Deep, ConditionOperator.UnderOrEqual)]
    public void Business_owned_is_judged_on_the_business_unit(DepthMask depth, ConditionOperator op)
    {
        var f = For(Profile(Me, A, privileges: ("prvReadTeam", depth))).ReadFilter(Team)!;
        f.Conditions.Should().ContainSingle().Which.Should().Match<ConditionNode>(c => c.Attribute == "owningbusinessunit" && c.Operator == op);
        f.Shared.Should().BeEmpty();
    }

    // ---- plan 2.11: activitypointer and activityparty ----

    private static readonly EntityMetadata Pointer = Entity("activitypointer", "ActivityPointer", OwnershipTypes.UserOwned, 4200);

    private static EntityMetadata PartyTable()
    {
        var party = Entity("activityparty", "ActivityParty", OwnershipTypes.None, 135);
        Xrmd.Metadata.EntityMetadataExtensions.SetAttributes(party, [new LookupAttributeMetadata { LogicalName = "activityid", Targets = ["activitypointer", "email"] }]);
        return party;
    }

    private static ReadSecurity ForActivities(params SecurityProfile[] principals) =>
        new(principals, Tree, new Store(BusinessUnit, Pointer, PartyTable()), principals[0].UserId);

    [Fact]
    public void Pointer_shares_match_the_code_of_the_activity_table()
    {
        // RF1: a share is recorded on the activity with its table's code; the pointer row has the activity's id and holds that code
        // in activitytypecode. Review C1: never the id alone, or a share of another table's record with the same id would match.
        var f = ForActivities(Profile(Me, A, privileges: ("prvReadActivity", DepthMask.Basic))).ReadFilter(Pointer)!;
        f.Shared.Should().Equal(new SharedRecordsNode(Me, 4200, 1, TypeAttribute: "activitytypecode"));
    }

    [Fact]
    public void Excluded_activity_table_without_a_type_code_fails_closed()
    {
        // Final review M1: an activity table left out of hierarchy security is kept out of the pointer's hierarchy branches by
        // its type code; without one it cannot be, so the read fails instead of opening its rows.
        var untyped = new EntityMetadata { LogicalName = "xv_fax", SchemaName = "xv_Fax", OwnershipType = OwnershipTypes.UserOwned };
        Set(untyped, nameof(EntityMetadata.IsActivity), true);
        var typed = Entity("email", "Email", OwnershipTypes.UserOwned, 4202);
        Set(typed, nameof(EntityMetadata.IsActivity), true);
        SecurityProfile Reader(params string[] excluded) =>
            Profile(Me, A, false, ("prvReadActivity", DepthMask.Basic)) with
            {
                Hierarchy = new HierarchyReach(new HashSet<Guid> { Report }, new HashSet<Guid> { Report },
                    new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase)),
            };
        ReadSecurity With(params string[] excluded) =>
            new([Reader(excluded)], Tree, new Store(BusinessUnit, Pointer, PartyTable(), untyped, typed), Me);

        var filter = With("email").ReadFilter(Pointer)!;
        filter.Filters.SelectMany(f => f.Conditions).Concat(filter.Conditions)
            .Should().Contain(c => c.Attribute == "activitytypecode" && c.Operator == ConditionOperator.NotIn);
        var act = () => With("email", "xv_fax").ReadFilter(Pointer);
        act.Should().Throw<InvalidOperationException>().WithMessage("*xv_fax*");
    }

    [Fact]
    public void Party_rows_follow_the_pointer_without_a_privilege_of_their_own()
    {
        var party = PartyTable();
        var f = ForActivities(Profile(Me, A, privileges: ("prvReadActivity", DepthMask.Basic))).ReadFilter(party)!;
        var parents = f.Parents.Should().ContainSingle().Subject;
        parents.LookupAttribute.Should().Be("activityid");
        var target = parents.Targets.Should().ContainSingle().Subject;
        target.EntityName.Should().Be("activitypointer");
        target.ObjectTypeCode.Should().BeNull("the type column holds the activity's own table");
        target.TypeAttribute.Should().Be("activitytypecode");
        target.ReadFilter!.Shared.Should().Equal(new SharedRecordsNode(Me, 4200, 1, TypeAttribute: "activitytypecode"));

        // No activity privilege: no row, and no demand for a privilege activityparty does not have.
        ForActivities(Profile(Me, A, privileges: ("prvReadAccount", DepthMask.Basic))).ReadFilter(party)!.Parents.Single().Targets.Should().BeEmpty();
        ForActivities(Profile(Me, A, privileges: ("prvReadActivity", DepthMask.Global))).ReadFilter(party).Should().BeNull();
        ForActivities(Profile(Me, A, admin: true)).ReadFilter(party).Should().BeNull();
    }

    [Fact]
    public void Several_principals_are_anded()
    {
        var security = For(Profile(Me, A, privileges: ("prvReadpb_Project", DepthMask.Basic)),
            Profile(Other, B, privileges: ("prvReadpb_Project", DepthMask.Local)));
        var f = security.ReadFilter(Project)!;
        f.Operator.Should().Be(LogicalOperator.And);
        f.Filters.Should().HaveCount(2);
        f.Filters[1].Conditions[1].Values.Should().Equal(B);

        // A principal without restriction adds nothing; one without privilege denies.
        For(Profile(Me, A, privileges: ("prvReadpb_Project", DepthMask.Basic)), Profile(Other, B, admin: true)).ReadFilter(Project)!
            .Operator.Should().Be(LogicalOperator.Or);
        var act = () => For(Profile(Me, A, admin: true), Profile(Other, B)).ReadFilter(Project);
        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.PrivilegeDenied && e.Message.Contains(Other.ToString()));
    }

    // ---- manager hierarchy (plan 2.5, H4) ----------------------------------------------------------------------------

    private static readonly Guid Report = new("00000000-0000-0000-0000-000000000010");
    private static readonly Guid ReportTeam = new("00000000-0000-0000-0000-000000000011");

    private static SecurityProfile Manager(DepthMask depth, bool admin = false, params string[] excluded) =>
        Profile(Me, A, admin, ("prvReadpb_Project", depth), ("prvReadTeam", depth)) with
        {
            Hierarchy = new HierarchyReach(new HashSet<Guid> { Report, ReportTeam }, new HashSet<Guid> { Report },
                new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase)),
        };

    [Theory]
    [InlineData(DepthMask.Basic)]
    [InlineData(DepthMask.Local)]
    [InlineData(DepthMask.Deep)]
    public void Manager_reads_owned_and_shared_records_of_its_reach(DepthMask depth)
    {
        var f = For(Manager(depth)).ReadFilter(Project)!;
        f.Operator.Should().Be(LogicalOperator.Or);
        f.Conditions.Should().NotContain(c => c.Operator == ConditionOperator.In, "the reach is one JSON parameter, not inline values");
        f.Owned.Should().ContainSingle().Which.Principals.Should().BeEquivalentTo([Report, ReportTeam]);
        f.Shared.Should().HaveCount(2);
        f.Shared[0].Should().Be(new SharedRecordsNode(Me, 10001, 1));
        f.Shared[1].Principals.Should().BeEquivalentTo([Report, ReportTeam]);
        f.Shared[1].AccessMask.Should().Be(1);
        f.Shared[1].ObjectTypeCode.Should().Be(10001);
    }

    [Fact]
    public void Hierarchy_adds_nothing_where_it_does_not_apply()
    {
        // Excluded table, business-owned table, Global depth, System Administrator.
        For(Manager(DepthMask.Basic, excluded: "PB_PROJECT")).ReadFilter(Project)!.Shared.Should().ContainSingle();
        For(Manager(DepthMask.Basic, excluded: "pb_project")).ReadFilter(Project)!.Conditions.Should().ContainSingle()
            .Which.Operator.Should().Be(ConditionOperator.EqualUserOrUserTeams);
        For(Manager(DepthMask.Basic)).ReadFilter(Team)!.Conditions.Should().ContainSingle()
            .Which.Attribute.Should().Be("owningbusinessunit");
        For(Manager(DepthMask.Global)).ReadFilter(Project).Should().BeNull();
        For(Manager(DepthMask.Basic, admin: true)).ReadFilter(Project).Should().BeNull();
    }

    [Fact]
    public void Impersonation_ands_the_reach_with_the_other_principal()
    {
        var f = For(Manager(DepthMask.Basic), Profile(Other, B, privileges: ("prvReadpb_Project", DepthMask.Basic))).ReadFilter(Project)!;
        f.Operator.Should().Be(LogicalOperator.And);
        f.Filters[0].Shared.Should().HaveCount(2);
        f.Filters[1].Shared.Should().ContainSingle();
        f.Filters[1].Conditions.Should().ContainSingle();
    }

    // ---- user hierarchy operators (plan 2.5, H7) --------------------------------------------------------------------

    [Fact]
    public void Reporting_hierarchy_returns_the_loaded_ids_or_fails()
    {
        var store = new Store(BusinessUnit, Project, Team);
        var loaded = new ReadSecurity([], Tree, store, Me, userHierarchy: new UserHierarchyIds(true, [Me, Report], [Me, Report, ReportTeam]));
        loaded.ReportingHierarchy(includeTeams: false).Should().BeEquivalentTo([Me, Report]);
        loaded.ReportingHierarchy(includeTeams: true).Should().BeEquivalentTo([Me, Report, ReportTeam]);

        var disabled = () => new ReadSecurity([], Tree, store, Me, userHierarchy: UserHierarchyIds.Disabled).ReportingHierarchy(false);
        disabled.Should().Throw<XrmdValidationException>().WithMessage("*hierarchy security model*not enabled*");

        var notLoaded = () => new ReadSecurity([], Tree, store, Me).ReportingHierarchy(true);
        notLoaded.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Query_user_is_recognized_by_id_not_by_position()
    {
        // The impersonator listed first: only the query's user is matched through eq-useroruserteams.
        var me = Profile(Me, A, privileges: ("prvReadpb_Project", DepthMask.Basic));
        var other = Profile(Other, B, privileges: ("prvReadpb_Project", DepthMask.Basic));
        var f = new ReadSecurity([other, me], Tree, new Store(BusinessUnit, Project, Team), Me).ReadFilter(Project)!;

        f.Filters.Should().HaveCount(2);
        f.Filters[0].Conditions[0].Should().Match<ConditionNode>(c => c.Operator == ConditionOperator.In && c.Values.Contains(Other));
        f.Filters[1].Conditions[0].Operator.Should().Be(ConditionOperator.EqualUserOrUserTeams);
    }

    [Fact]
    public void Fails_closed_without_principals_or_on_an_unknown_ownership()
    {
        var none = () => For().ReadFilter(Project);
        none.Should().Throw<InvalidOperationException>();
        var unknown = () => For(Profile(Me, A, privileges: ("prvReadpb_Odd", DepthMask.Basic)))
            .ReadFilter(Entity("pb_odd", "pb_Odd", (OwnershipTypes)999, 10003));
        unknown.Should().Throw<InvalidOperationException>().WithMessage("*'pb_odd'*ownership type*");
    }

    // ---- Expand -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ConditionOperator.Under, new[] { "A1", "A2" })]
    [InlineData(ConditionOperator.UnderOrEqual, new[] { "A", "A1", "A2" })]
    [InlineData(ConditionOperator.Above, new[] { "R" })]
    [InlineData(ConditionOperator.AboveOrEqual, new[] { "A", "R" })]
    [InlineData(ConditionOperator.NotUnder, new[] { "A", "A1", "A2" })] // the ids to exclude: the subtree, A included
    public void Expand_business_unit_hierarchy(ConditionOperator op, string[] expected)
    {
        var names = new Dictionary<Guid, string> { [R] = "R", [A] = "A", [A1] = "A1", [A2] = "A2", [B] = "B" };
        For().Expand("businessunit", A, op).Select(id => names[id]).Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData("pb_project")]
    [InlineData("team")]
    [InlineData("pb_unknown")]
    public void Expand_rejects_other_entities(string entity)
    {
        foreach (var op in new[] { ConditionOperator.Under, ConditionOperator.NotUnder })
        {
            var act = () => For().Expand(entity, A, op);
            act.Should().Throw<XrmdValidationException>().WithMessage($"Operator {op}: this resolver expands only the business unit tree, not '{entity}'.");
        }
    }

    private sealed class Store(params EntityMetadata[] entities) : IMetadataStore
    {
        public long VersionStamp => 1;
        // The model of each SDK fixture (converted once per instance); the SDK view of it is the fixture itself. A fixture
        // mutated after its first read is not seen.
        private static TableDefinition? Model(EntityMetadata? e) => e is null ? null : SdkMetadataView.ModelOf(e);
        public TableDefinition? GetTable(string logicalName) => Model(entities.FirstOrDefault(e => e.LogicalName == logicalName));
        public TableDefinition? GetTableBySetName(string entitySetName) => null;
        public TableDefinition? GetTableByObjectTypeCode(int objectTypeCode) => Model(entities.FirstOrDefault(e => e.ObjectTypeCode == objectTypeCode));
        public IReadOnlyList<TableDefinition> GetAllTables() => entities.Select(e => Model(e)!).ToList();
        public Task LoadAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateEntityAsync(TableDefinition entity, ColumnDefinition primaryName, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAttributeAsync(string entityLogicalName, ColumnDefinition attribute, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task CreateEntityAsync(DataSession session, TableDefinition entity, ColumnDefinition primaryName, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task AddAttributeAsync(DataSession session, string entityLogicalName, ColumnDefinition attribute, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task UpdateEntityAsync(DataSession session, TableDefinition changes, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task UpdateAttributeAsync(DataSession session, string entityLogicalName, ColumnDefinition changes, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteAttributeAsync(DataSession session, string entityLogicalName, string attributeLogicalName, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteEntityAsync(DataSession session, string entityLogicalName, CancellationToken ct) =>
            throw new NotSupportedException();
        public ChoiceSetBase? GetChoiceSet(string name) => null;
        public IReadOnlyList<ChoiceSetBase> GetAllChoiceSets() => [];
        public Task CreateOptionSetAsync(DataSession session, ChoiceSetBase optionSet, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task UpdateOptionSetAsync(DataSession session, ChoiceSetBase changes, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteOptionSetAsync(DataSession session, string name, CancellationToken ct) =>
            throw new NotSupportedException();
        public RelationshipDefinition? GetRelationshipDefinition(string schemaName) => null;
        public IReadOnlyList<ManyToManyRelationship> GetManyToManyDefinitions() => [];
        public IReadOnlyList<OneToManyRelationship> GetOneToManyDefinitions() => [];
        public Task CreateOneToManyAsync(DataSession session, OneToManyRelationship relationship, LookupColumn lookup,
            CancellationToken ct) => throw new NotSupportedException();
        public Task CreateCustomerRelationshipsAsync(DataSession session, LookupColumn lookup,
            IReadOnlyList<OneToManyRelationship> relationships, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateManyToManyAsync(DataSession session, ManyToManyRelationship relationship, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteRelationshipAsync(DataSession session, string schemaName, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
