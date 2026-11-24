namespace Xrmd.Query;

/// <summary>Caller context of a query compilation.</summary>
/// <param name="UserId">The caller (<c>eq-userid</c>, team operators).</param>
/// <param name="BusinessUnitId">The caller's business unit (<c>eq-businessid</c>).</param>
/// <param name="UtcNow">The current time (relative date operators).</param>
public sealed record QueryContext(Guid UserId, Guid BusinessUnitId, DateTime UtcNow)
{
    /// <summary>Read restriction ANDed to every entity of the query; null: no restriction.</summary>
    public IQuerySecurity? Security { get; init; }

    /// <summary>Resolves the hierarchy operators (<c>under</c>, <c>above</c>...); null: they are rejected.</summary>
    public IHierarchyResolver? Hierarchy { get; init; }

    /// <summary>
    /// The caller's UI language (<c>usersettings.uilanguageid</c>, #109): the labels a choice is ordered by, and its formatted
    /// value, are taken in it when the option has one. 1033 when the caller has none.
    /// </summary>
    public int LanguageCode { get; init; } = 1033;
}
