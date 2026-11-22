using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Security;

/// <summary>
/// The hierarchy security settings (plan 2.5): the two <c>organization</c> columns and the excluded tables
/// (<c>meta.hierarchy_security_exclusion</c>, an xrmverse extension).
/// </summary>
/// <param name="Enabled"><c>organization.ishierarchicalsecuritymodelenabled</c>.</param>
/// <param name="Depth"><c>organization.maxdepthforhierarchicalsecuritymodel</c>.</param>
/// <param name="ExcludedTables">The logical names of the excluded tables (case-insensitive).</param>
public sealed record HierarchySettings(bool Enabled, int Depth, IReadOnlySet<string> ExcludedTables)
{
    /// <summary>The default of <c>maxdepthforhierarchicalsecuritymodel</c>, used when the column is NULL.</summary>
    public const int DefaultDepth = 3;
}

/// <summary>
/// What a manager reaches through the manager hierarchy (plan 2.5, ruling H4): the principals (reports and their teams)
/// whose records it may read, and those whose records it may also write, append and append to.
/// </summary>
/// <param name="ReadPrincipals">The ids of every counted report up to the depth and of their teams.</param>
/// <param name="WritePrincipals">The ids of the counted direct reports and of their teams (a subset of
/// <paramref name="ReadPrincipals"/>).</param>
/// <param name="ExcludedTables">The tables the hierarchy never applies to (case-insensitive logical names).</param>
public sealed record HierarchyReach(IReadOnlySet<Guid> ReadPrincipals, IReadOnlySet<Guid> WritePrincipals, IReadOnlySet<string> ExcludedTables)
{
    /// <summary>The table whose records follow their parent record, never covered.</summary>
    public const string FileAttachment = "fileattachment";

    /// <summary>
    /// True when the hierarchy applies to <paramref name="entity"/> (ruling H3): a user- or team-owned table that is not an
    /// intersect table, not <c>fileattachment</c> and not excluded.
    /// </summary>
    public bool Covers(EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity.OwnershipType is OwnershipTypes.UserOwned or OwnershipTypes.TeamOwned
               && entity.IsIntersect != true
               && entity.LogicalName is { } name
               && !string.Equals(name, FileAttachment, StringComparison.OrdinalIgnoreCase)
               && !ExcludedTables.Contains(name);
    }
}

/// <summary>
/// The ids the operators <c>eq-useroruserhierarchy</c> and <c>eq-useroruserhierarchyandteams</c> match for the user of a
/// query (plan 2.5, ruling H7), loaded with the query context (<see cref="SecurityCache.ReportingHierarchyAsync"/>).
/// </summary>
/// <param name="Enabled">False: the hierarchy security model is disabled, and the operators are refused.</param>
/// <param name="Users">The user and its reports down to the depth.</param>
/// <param name="UsersAndTeams">Those and the teams of all of them.</param>
public sealed record UserHierarchyIds(bool Enabled, IReadOnlyCollection<Guid> Users, IReadOnlyCollection<Guid> UsersAndTeams)
{
    /// <summary>The model is disabled.</summary>
    public static readonly UserHierarchyIds Disabled = new(false, [], []);
}
