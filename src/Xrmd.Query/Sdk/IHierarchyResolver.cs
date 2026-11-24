using Microsoft.Xrm.Sdk.Query;

namespace Xrmd.Query;

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
