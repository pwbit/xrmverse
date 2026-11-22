using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Query.Model;

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

/// <summary>The read restriction of the caller, compiled into the query (never applied to rows afterwards).</summary>
public interface IQuerySecurity
{
    /// <summary>
    /// The filter the caller's readable records of <paramref name="entity"/> satisfy, compiled in the scope of that entity
    /// (conditions without an entity alias refer to it); null: no restriction.
    /// </summary>
    FilterNode? ReadFilter(EntityMetadata entity);

    /// <summary>
    /// The secured columns of <paramref name="entity"/> the caller cannot read through its field security profiles (plan 14,
    /// ruling F3); empty when field security does not restrict it. The compiler refuses them in a condition, an order, an aggregate
    /// or a link join (403 0x8004F504) and marks them in the projection (<see cref="Sql.ResultColumn.Unreadable"/>), so that the
    /// executor returns them as null unless a field share grants them on the row.
    /// </summary>
    IReadOnlySet<string> UnreadableColumns(EntityMetadata entity);
}

/// <summary>Expands the hierarchy operators into the matching record ids.</summary>
public interface IHierarchyResolver
{
    /// <summary>
    /// The ids for <see cref="ConditionOperator.Under"/>/<see cref="ConditionOperator.UnderOrEqual"/>/
    /// <see cref="ConditionOperator.Above"/>/<see cref="ConditionOperator.AboveOrEqual"/>/<see cref="ConditionOperator.NotUnder"/>
    /// of <paramref name="id"/> in the hierarchy of <paramref name="entityName"/> (for <c>NotUnder</c>: the ids it excludes).
    /// </summary>
    /// <exception cref="Xrmd.Abstractions.XrmdValidationException">"Operator {op}: this resolver expands only the business unit
    /// tree" for other entities (the compiler walks the other tables itself, by their hierarchical relationship).</exception>
    IReadOnlyCollection<Guid> Expand(string entityName, Guid id, ConditionOperator op);

    /// <summary>
    /// The ids for <see cref="ConditionOperator.EqualUserOrUserHierarchy"/> (<paramref name="includeTeams"/> false) and
    /// <see cref="ConditionOperator.EqualUserOrUserHierarchyAndTeams"/> (true): the query's user and its reports down to the
    /// hierarchy security depth, with the teams of all of them on request (plan 2.5, ruling H7).
    /// </summary>
    /// <exception cref="Xrmd.Abstractions.XrmdValidationException">The hierarchy security model is not enabled.</exception>
    IReadOnlyCollection<Guid> ReportingHierarchy(bool includeTeams);
}
