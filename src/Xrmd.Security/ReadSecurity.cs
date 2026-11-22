using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Query;
using Xrmd.Query.Model;

namespace Xrmd.Security;

/// <summary>
/// The read restriction of a caller, as a <see cref="FilterNode"/> the query compiler ANDs to every entity of a query, and
/// the expansion of the business unit hierarchy operators.
/// </summary>
/// <param name="principals">The principals whose restrictions all apply (ANDed): the user of the query context and (with
/// impersonation) the impersonator, in any order. Empty: the instance only resolves hierarchies, and <see cref="ReadFilter"/> refuses
/// to run.</param>
/// <param name="tree">The business unit hierarchy.</param>
/// <param name="store">The metadata, which resolves the entity of a hierarchy operator.</param>
/// <param name="queryUserId">The <see cref="QueryContext.UserId"/> of the queries this restriction is compiled into.</param>
/// <param name="unreadable">Field security (plan 14): the secured columns the caller cannot read
/// (<see cref="FieldPermissionSet.UnreadableSecured"/> of <see cref="FieldSecurity.ForAsync"/>), by table, loaded for every table
/// of the query when the context is built; null when field security does not restrict the caller (ruling F1).</param>
/// <param name="userHierarchy">The ids of the user hierarchy operators for <paramref name="queryUserId"/> (plan 2.5, ruling H7),
/// loaded when the query uses one of them; null when it does not (<see cref="ReportingHierarchy"/> then fails closed).</param>
/// <remarks>
/// The owner condition of the principal whose id is <paramref name="queryUserId"/> uses
/// <see cref="ConditionOperator.EqualUserOrUserTeams"/>, which the compiler resolves against <see cref="QueryContext.UserId"/>;
/// the other principals' is an explicit <c>ownerid in (principal, its teams)</c>, from its <see cref="SecurityProfile.TeamIds"/>.
/// </remarks>
public sealed class ReadSecurity(IReadOnlyList<SecurityProfile> principals, BusinessUnitTree tree, IMetadataStore store, Guid queryUserId,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? unreadable = null, UserHierarchyIds? userHierarchy = null)
    : IQuerySecurity, IHierarchyResolver
{
    private static readonly IReadOnlySet<string> NoColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private const string BusinessUnit = "businessunit";
    private const string OwnerId = "ownerid";
    private const string OwningBusinessUnit = "owningbusinessunit";
    private const string QueueViewType = "queueviewtype";

    /// <inheritdoc />
    /// <remarks>
    /// Per principal, with <c>d</c> the depth of <c>prvRead{SchemaName}</c>: intersect entities → no restriction;
    /// <c>fileattachment</c> → the rows whose owning record (<c>objectid</c>) the principal can read (see <see cref="AttachmentFilter"/>);
    /// <c>queue</c> and <c>queueitem</c> → see <see cref="QueuePrivacy"/> and <see cref="QueueItemFilter"/>;
    /// <c>activityparty</c> → the rows whose activity the principal can read through <c>activitypointer</c> (see <see cref="PartyFilter"/>);
    /// <c>post</c>, <c>postregarding</c> and <c>postcomment</c> → <c>prvReadPost</c>, then the rows whose regarding record, or post, the
    /// principal can read (see <see cref="PostFilter"/>);
    /// <c>activitypointer</c> → as any user-owned table with <c>prvReadActivity</c>, shares matched by the activity's id and the code
    /// in its <c>activitytypecode</c> (see <see cref="SharedTypeAttribute"/>), hierarchy branches without the excluded activity
    /// tables;
    /// <c>d</c> None → PrivilegeDenied; organization-owned, Global or System Administrator → no restriction; user/team-owned
    /// → owned by the user or one of its teams, OR shared with it for Read, OR (Local) of its business unit, OR (Deep) of its
    /// business unit subtree; business-owned → of its business unit (Basic and Local) or its subtree (Deep).
    /// Manager hierarchy (plan 2.5, ruling H4): on a user/team-owned table its <see cref="SecurityProfile.Hierarchy"/> covers,
    /// a principal below Global also reads the records owned by one of its <see cref="HierarchyReach.ReadPrincipals"/> (reports
    /// and their teams) or shared with one of them for Read.
    /// Several principals → the AND of their restrictions (an impersonated manager never gets its reach alone).
    /// </remarks>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied when a principal has no <c>prvRead</c> on the entity.</exception>
    /// <exception cref="InvalidOperationException">No principal, or an ownership type without a rule (fails closed).</exception>
    public FilterNode? ReadFilter(EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (principals.Count == 0)
            throw new InvalidOperationException("A read restriction needs at least one principal.");

        var filters = new List<FilterNode>();
        foreach (var p in principals)
            if (PrincipalFilter(p, entity, isQueryUser: p.UserId == queryUserId) is { } filter) filters.Add(filter);
        if (filters.Count <= 1) return filters.SingleOrDefault();
        var all = new FilterNode { Operator = LogicalOperator.And };
        all.Filters.AddRange(filters);
        return all;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">A table whose permissions were not loaded with the context (fails closed).</exception>
    public IReadOnlySet<string> UnreadableColumns(EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (unreadable is null) return NoColumns;
        return unreadable.TryGetValue(entity.LogicalName, out var columns)
            ? columns
            : throw new InvalidOperationException($"The field permissions on '{entity.LogicalName}' were not loaded for this query.");
    }

    private FilterNode? PrincipalFilter(SecurityProfile p, EntityMetadata entity, bool isQueryUser)
    {
        if (entity.LogicalName == QueueSchema.QueueItem) return QueueItemFilter(p, entity, isQueryUser);
        var filter = OwnershipFilter(p, entity, isQueryUser);
        // A queue is also restricted by its privacy, below Global depth as well as at it (Basic User reads queues at Global).
        return entity.LogicalName == QueueSchema.Queue && !p.IsSystemAdministrator ? And(filter, QueuePrivacy(p, entity, isQueryUser)) : filter;
    }

    private static FilterNode And(FilterNode? left, FilterNode right)
    {
        if (left is null) return right;
        var and = new FilterNode { Operator = LogicalOperator.And };
        and.Filters.Add(left);
        and.Filters.Add(right);
        return and;
    }

    /// <summary>
    /// <c>queue</c> (plan 4.1a, spec §6): a public queue (<c>queueviewtype</c> 0) is readable by whoever holds Read Queue, a private
    /// one by the user or a team that owns it (so a team's members read the private queue the team owns, ruling R9), by a member
    /// (<c>queuemembership</c>) and by a principal it is shared with for Read. A System Administrator reads every queue. Our call
    /// where Learn is silent: the Global depth of Read Queue does not lift the privacy, because Basic User holds it. The security guard
    /// checks one queue with this same predicate before an item in it is worked or placed (ruling R23), so the rule is written once.
    /// </summary>
    private FilterNode QueuePrivacy(SecurityProfile p, EntityMetadata queue, bool isQueryUser)
    {
        var filter = new FilterNode { Operator = LogicalOperator.Or };
        filter.Conditions.Add(new ConditionNode(null, QueueViewType, ConditionOperator.Equal, [QueueSchema.PublicViewType]));
        filter.Conditions.Add(OwnedBy(p, isQueryUser));
        filter.QueueMembers.Add(new QueueMemberNode(p.UserId));
        filter.Shared.Add(new SharedRecordsNode(p.UserId, queue.ObjectTypeCode ?? 0, (int)AccessRights.ReadAccess));
        return filter;
    }

    /// <summary>
    /// <c>queueitem</c> (plan 4.1a, spec §6): an item has no owner; it is readable when its queue (<c>queueid</c>, with the queue's own
    /// rule above) and the record it points to (<c>objectid</c>, with that table's restriction) are, whatever the depth of
    /// <c>prvReadQueueItem</c> (PrivilegeDenied without it). A target table the principal has no read privilege on contributes no
    /// row. A System Administrator reads every item.
    /// </summary>
    private FilterNode? QueueItemFilter(SecurityProfile p, EntityMetadata item, bool isQueryUser)
    {
        var privilege = PrivilegeNames.For(AccessRights.ReadAccess, item);
        if (p.DepthOf(privilege) == DepthMask.None)
            throw XrmdFaultException.PrivilegeDenied(p.UserId, privilege, item.LogicalName, item.ObjectTypeCode ?? 0);
        if (p.IsSystemAdministrator) return null;
        var queue = store.GetEntity(QueueSchema.Queue)
                    ?? throw new InvalidOperationException($"The table '{QueueSchema.Queue}' does not exist."); // fail closed
        var record = (item.Attributes ?? []).OfType<LookupAttributeMetadata>().FirstOrDefault(a => a.LogicalName == QueueSchema.ObjectId)
                     ?? throw new InvalidOperationException($"'{item.LogicalName}' has no '{QueueSchema.ObjectId}' lookup."); // fail closed
        var targets = new List<ParentTarget>();
        foreach (var name in record.Targets ?? [])
        {
            if (store.GetEntity(name) is not { ObjectTypeCode: { } otc } parent) continue;
            if (p.DepthOf(PrivilegeNames.For(AccessRights.ReadAccess, parent)) == DepthMask.None) continue;
            targets.Add(new ParentTarget(parent.LogicalName, otc, PrincipalFilter(p, parent, isQueryUser)));
        }
        var filter = new FilterNode();
        filter.Parents.Add(new ParentRecordsNode(QueueSchema.QueueIdColumn,
            [new ParentTarget(queue.LogicalName, queue.ObjectTypeCode, PrincipalFilter(p, queue, isQueryUser))]));
        filter.Parents.Add(new ParentRecordsNode(QueueSchema.ObjectId, targets));
        return filter;
    }

    private FilterNode? OwnershipFilter(SecurityProfile p, EntityMetadata entity, bool isQueryUser)
    {
        if (entity.IsIntersect == true) return null; // readable by every member of the organization, no privilege
        if (entity.LogicalName == FileColumns.AttachmentEntity) return AttachmentFilter(p, entity, isQueryUser);
        if (entity.LogicalName == ActivitySchema.Party) return PartyFilter(p, entity, isQueryUser);
        if (PostSchema.IsPostTable(entity.LogicalName)) return PostFilter(p, entity, isQueryUser);
        var privilege = PrivilegeNames.For(AccessRights.ReadAccess, entity);
        var depth = p.DepthOf(privilege);
        if (depth == DepthMask.None)
            throw XrmdFaultException.PrivilegeDenied(p.UserId, privilege, entity.LogicalName, entity.ObjectTypeCode ?? 0);
        if (p.IsSystemAdministrator || depth == DepthMask.Global) return null;
        // The audit privileges are Global only: a narrower depth (from a hand-made role row) reads nothing.
        if (entity.LogicalName == AuditTable.Name)
            throw XrmdFaultException.PrivilegeDenied(p.UserId, privilege, entity.LogicalName, entity.ObjectTypeCode ?? 0);

        var filter = new FilterNode { Operator = LogicalOperator.Or };
        switch (entity.OwnershipType)
        {
            case OwnershipTypes.OrganizationOwned or OwnershipTypes.None:
                return null;
            case OwnershipTypes.UserOwned or OwnershipTypes.TeamOwned:
                filter.Conditions.Add(OwnedBy(p, isQueryUser));
                if (depth == DepthMask.Deep) filter.Conditions.Add(InSubtree(p));
                else if (depth == DepthMask.Local) filter.Conditions.Add(InUnit(p));
                var typeAttribute = SharedTypeAttribute(entity);
                filter.Shared.Add(new SharedRecordsNode(p.UserId, entity.ObjectTypeCode ?? 0, (int)AccessRights.ReadAccess, TypeAttribute: typeAttribute));
                if (p.Hierarchy is { } reach && reach.Covers(entity))
                {
                    var principals = reach.ReadPrincipals.Order().ToList();
                    var excluded = ExcludedActivityTypes(entity, reach);
                    var hierarchy = excluded is null ? filter : new FilterNode { Operator = LogicalOperator.Or };
                    hierarchy.Owned.Add(new OwnedRecordsNode(principals)); // one JSON parameter, like the shared branch
                    hierarchy.Shared.Add(new SharedRecordsNode(p.UserId, entity.ObjectTypeCode ?? 0, (int)AccessRights.ReadAccess, principals, typeAttribute));
                    if (excluded is not null)
                    {
                        var and = new FilterNode { Operator = LogicalOperator.And };
                        and.Conditions.Add(excluded);
                        and.Filters.Add(hierarchy);
                        filter.Filters.Add(and);
                    }
                }
                return filter;
            case OwnershipTypes.BusinessOwned:
                filter.Conditions.Add(depth == DepthMask.Deep ? InSubtree(p) : InUnit(p)); // Basic counts as Local
                return filter;
            default:
                throw new InvalidOperationException(
                    $"Entity '{entity.LogicalName}' has an unsupported ownership type {entity.OwnershipType}."); // fail closed
        }
    }

    /// <summary>
    /// <c>fileattachment</c>: a row is readable when the record that owns the file (<c>objectid</c>) is readable, whatever the
    /// privileges on <c>fileattachment</c> itself (the files of a record are part of the record). A System Administrator reads
    /// every row; a target entity the principal has no read privilege on contributes no row. Field security (final re-review
    /// N1): when it restricts the caller, the rows of a secured File or Image column the caller cannot read through its
    /// profiles are left out (<c>regardingfieldname</c> not among them; a row without one is left out too on such a table).
    /// Field shares are not consulted here: the row stays hidden even on a record where a share grants the column.
    /// </summary>
    private FilterNode? AttachmentFilter(SecurityProfile p, EntityMetadata attachment, bool isQueryUser)
    {
        if (p.IsSystemAdministrator) return null;
        var lookup = (attachment.Attributes ?? []).OfType<LookupAttributeMetadata>().FirstOrDefault(FileColumns.IsAttachmentObjectId)
                     ?? throw new InvalidOperationException($"'{attachment.LogicalName}' has no '{FileColumns.AttachmentLookup}' lookup."); // fail closed
        var targets = new List<ParentTarget>();
        foreach (var name in lookup.Targets ?? [])
        {
            if (store.GetEntity(name) is not { ObjectTypeCode: { } otc } parent) continue;
            if (p.DepthOf(PrivilegeNames.For(AccessRights.ReadAccess, parent)) == DepthMask.None) continue;
            targets.Add(new ParentTarget(parent.LogicalName, otc, PrincipalFilter(p, parent, isQueryUser), HiddenFiles(parent)));
        }
        var filter = new FilterNode();
        filter.Parents.Add(new ParentRecordsNode(lookup.LogicalName, targets));
        return filter;
    }

    /// <summary>
    /// The read rule of the timeline posts (plan 3.5 UI-1g, spec §3.5, our call where Learn is silent): on top of <c>prvReadPost</c>
    /// (PrivilegeDenied without it), a <c>post</c> or <c>postregarding</c> row is readable when its regarding record
    /// (<c>regardingobjectid</c>) is readable, with that table's restriction, and a <c>postcomment</c> row when its post
    /// (<c>postid</c>) is readable, with the post's rule. A target table the principal has no read privilege on contributes no row,
    /// as for <c>fileattachment</c>. A System Administrator reads every row. The same filter serves Retrieve (403 on a hidden row),
    /// RetrieveMultiple and the counts, so no count reveals a record the caller cannot read.
    /// </summary>
    /// <exception cref="XrmdFaultException">403 PrivilegeDenied without <c>prvReadPost</c>.</exception>
    private FilterNode? PostFilter(SecurityProfile p, EntityMetadata entity, bool isQueryUser)
    {
        var privilege = PrivilegeNames.For(AccessRights.ReadAccess, entity);
        if (p.DepthOf(privilege) == DepthMask.None)
            throw XrmdFaultException.PrivilegeDenied(p.UserId, privilege, entity.LogicalName, entity.ObjectTypeCode ?? 0);
        if (p.IsSystemAdministrator) return null;
        var filter = new FilterNode();
        if (entity.LogicalName == PostSchema.Comment)
        {
            var post = store.GetEntity(PostSchema.Post)
                       ?? throw new InvalidOperationException($"The table '{PostSchema.Post}' does not exist."); // fail closed
            filter.Parents.Add(new ParentRecordsNode(PostSchema.PostId,
                [new ParentTarget(post.LogicalName, post.ObjectTypeCode, PostFilter(p, post, isQueryUser))]));
            return filter;
        }
        var lookup = (entity.Attributes ?? []).OfType<LookupAttributeMetadata>().FirstOrDefault(a => a.LogicalName == ActivitySchema.RegardingObjectId)
                     ?? throw new InvalidOperationException($"'{entity.LogicalName}' has no '{ActivitySchema.RegardingObjectId}' lookup."); // fail closed
        var targets = new List<ParentTarget>();
        foreach (var name in lookup.Targets ?? [])
        {
            if (store.GetEntity(name) is not { ObjectTypeCode: { } otc } parent) continue;
            if (p.DepthOf(PrivilegeNames.For(AccessRights.ReadAccess, parent)) == DepthMask.None) continue;
            targets.Add(new ParentTarget(parent.LogicalName, otc, PrincipalFilter(p, parent, isQueryUser)));
        }
        filter.Parents.Add(new ParentRecordsNode(lookup.LogicalName, targets));
        return filter;
    }

    /// <summary>
    /// The attribute of the row a share's object type code is matched with, instead of the table's code: <c>activitytypecode</c>
    /// for <c>activitypointer</c> (plan 2.11, ruling A13, RF1): shares and access teams are recorded on the activity with the code
    /// of its table, and the pointer row has the activity's id and that code, so a share of another table's record with the same
    /// id never opens it. Null for any other table. The pointer itself cannot be shared (<c>SystemTableRules</c>).
    /// </summary>
    private static string? SharedTypeAttribute(EntityMetadata entity) =>
        entity.LogicalName == ActivitySchema.Pointer ? ActivitySchema.ActivityTypeCode : null;

    /// <summary>
    /// On <c>activitypointer</c>, the condition that keeps the rows of the activity tables the manager hierarchy excludes out of its
    /// branches (<c>activitytypecode</c> not in their codes), so excluding <c>email</c> also holds through the pointer (plan 2.11,
    /// review I1); null for any other table or without an excluded activity table. Excluding <c>activitypointer</c> itself is
    /// <see cref="HierarchyReach.Covers"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">An excluded activity table has no object type code (fail closed, final review
    /// M1: leaving it out of the condition would open its rows through the pointer's hierarchy branches).</exception>
    private ConditionNode? ExcludedActivityTypes(EntityMetadata entity, HierarchyReach reach)
    {
        if (entity.LogicalName != ActivitySchema.Pointer) return null;
        var excludedTables = reach.ExcludedTables.Select(store.GetEntity).Where(e => e is not null && ActivitySchema.IsActivityTable(e)).ToList();
        if (excludedTables.FirstOrDefault(e => e!.ObjectTypeCode is null) is { } untyped)
            throw new InvalidOperationException(
                $"The activity table '{untyped.LogicalName}' is excluded from hierarchy security but has no object type code: its pointer rows cannot be told apart.");
        var excluded = excludedTables.Select(e => (object?)e!.ObjectTypeCode!.Value).Distinct().ToList();
        return excluded.Count == 0 ? null : new ConditionNode(null, ActivitySchema.ActivityTypeCode, ConditionOperator.NotIn, excluded);
    }

    /// <summary>
    /// <c>activityparty</c> (plan 2.11, ruling A10, pre-flight C13): a row is readable when the <c>activitypointer</c> row of its
    /// activity (<c>activityid</c>, the activity's id) is readable, with the pointer's restriction (<c>prvReadActivity</c>, owner,
    /// depth, shares, hierarchy), whatever the privileges on <c>activityparty</c>, which has none. Without <c>prvReadActivity</c>
    /// no row is readable (no fault: the table has no privilege to name). A System Administrator, or a Global read of activities,
    /// reads every row. The party record itself is not consulted: a party row is part of its activity (ruling Y2 guards the
    /// writes, which never name a record the writer cannot read).
    /// </summary>
    private FilterNode? PartyFilter(SecurityProfile p, EntityMetadata party, bool isQueryUser)
    {
        if (p.IsSystemAdministrator) return null;
        var lookup = (party.Attributes ?? []).FirstOrDefault(a => a.LogicalName == ActivitySchema.ActivityId)
                     ?? throw new InvalidOperationException($"'{party.LogicalName}' has no '{ActivitySchema.ActivityId}' lookup."); // fail closed
        var pointer = store.GetEntity(ActivitySchema.Pointer)
                      ?? throw new InvalidOperationException($"The table '{ActivitySchema.Pointer}' does not exist."); // fail closed
        var targets = new List<ParentTarget>();
        var depth = p.DepthOf(PrivilegeNames.For(AccessRights.ReadAccess, pointer));
        if (depth == DepthMask.Global) return null;
        if (depth != DepthMask.None)
            targets.Add(new ParentTarget(pointer.LogicalName, ObjectTypeCode: null, PrincipalFilter(p, pointer, isQueryUser),
                TypeAttribute: ActivitySchema.ActivityTypeCode));
        var filter = new FilterNode();
        filter.Parents.Add(new ParentRecordsNode(lookup.LogicalName, targets));
        return filter;
    }

    /// <summary>
    /// The <c>fileattachment</c> rows of <paramref name="parent"/> the caller may not see: those of its secured File and Image
    /// columns it cannot read (null when there are none, or field security does not restrict the caller).
    /// </summary>
    /// <exception cref="InvalidOperationException">The field permissions on the table were not loaded (fails closed).</exception>
    private FilterNode? HiddenFiles(EntityMetadata parent)
    {
        if (unreadable is null) return null;
        var hidden = UnreadableColumns(parent);
        var columns = (parent.Attributes ?? []).Where(a => FileColumns.IsFileOrImage(a) && hidden.Contains(a.LogicalName))
            .Select(a => (object?)a.LogicalName).ToList();
        if (columns.Count == 0) return null;
        var filter = new FilterNode();
        filter.Conditions.Add(new ConditionNode(null, AttachmentColumn, ConditionOperator.NotIn, columns));
        return filter;
    }

    private const string AttachmentColumn = "regardingfieldname";

    /// <summary>Owned by the principal or one of its teams.</summary>
    private static ConditionNode OwnedBy(SecurityProfile p, bool isQueryUser) => isQueryUser
        ? new ConditionNode(null, OwnerId, ConditionOperator.EqualUserOrUserTeams, [])
        : new ConditionNode(null, OwnerId, ConditionOperator.In, [p.UserId, .. p.TeamIds.Cast<object?>()]);

    private static ConditionNode InUnit(SecurityProfile p) =>
        new(null, OwningBusinessUnit, ConditionOperator.Equal, [p.BusinessUnitId]);

    private static ConditionNode InSubtree(SecurityProfile p) =>
        new(null, OwningBusinessUnit, ConditionOperator.UnderOrEqual, [p.BusinessUnitId]);

    /// <inheritdoc />
    /// <exception cref="XrmdValidationException">The hierarchy security model is disabled.</exception>
    /// <exception cref="InvalidOperationException">The ids were not loaded with the context (fails closed).</exception>
    public IReadOnlyCollection<Guid> ReportingHierarchy(bool includeTeams)
    {
        if (userHierarchy is null)
            throw new InvalidOperationException("The user hierarchy was not loaded for this query.");
        if (!userHierarchy.Enabled)
            throw new XrmdValidationException(
                $"The operator {(includeTeams ? ConditionOperator.EqualUserOrUserHierarchyAndTeams : ConditionOperator.EqualUserOrUserHierarchy)} " +
                "needs the hierarchy security model, which is not enabled in this organization.");
        return includeTeams ? userHierarchy.UsersAndTeams : userHierarchy.Users;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Under: the units below <paramref name="id"/>; UnderOrEqual: those and <paramref name="id"/>; Above: the ancestors;
    /// AboveOrEqual: those and <paramref name="id"/>; NotUnder: the ids to exclude, the subtree including
    /// <paramref name="id"/> itself (the compiler puts them in <c>NOT IN</c>).
    /// </remarks>
    /// <exception cref="XrmdValidationException">An entity other than <c>businessunit</c>, or not a hierarchy operator.</exception>
    public IReadOnlyCollection<Guid> Expand(string entityName, Guid id, ConditionOperator op)
    {
        ArgumentNullException.ThrowIfNull(entityName);
        if (store.GetEntity(entityName)?.LogicalName is not BusinessUnit)
            throw new XrmdValidationException($"Operator {op}: this resolver expands only the business unit tree, not '{entityName}'.");
        return op switch
        {
            ConditionOperator.Under => tree.SubtreeOf(id).Where(u => u != id).ToList(),
            ConditionOperator.UnderOrEqual or ConditionOperator.NotUnder => tree.SubtreeOf(id).ToList(),
            ConditionOperator.Above => tree.AncestorsOf(id).ToList(),
            ConditionOperator.AboveOrEqual => [id, .. tree.AncestorsOf(id)],
            _ => throw new XrmdValidationException($"Operator {op} is not a hierarchy operator."),
        };
    }
}
