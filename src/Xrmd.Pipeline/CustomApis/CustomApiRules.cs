using System.Text.RegularExpressions;
using Xrmd.Metadata;

namespace Xrmd.Pipeline.CustomApis;

/// <summary>
/// A custom API as the shared rules see it, whatever its source (a solution file or an authoring request). Only what the rules
/// read is here.
/// </summary>
/// <param name="BoundEntity">The <c>boundentitylogicalname</c>; null when none.</param>
/// <param name="AllowedStepType">The <c>allowedcustomprocessingsteptype</c>.</param>
public sealed record CustomApiShape(string UniqueName, int BindingType, string? BoundEntity, bool IsFunction, bool WorkflowEnabled,
    int AllowedStepType, IReadOnlyList<CustomApiFieldShape> RequestParameters, IReadOnlyList<CustomApiFieldShape> ResponseProperties);

/// <summary>A request parameter or a response property as the shared rules see it.</summary>
/// <param name="Type">The <c>type</c> value (0 to 12).</param>
/// <param name="LogicalEntityName">The <c>logicalentityname</c>; null when none.</param>
/// <param name="HasIsOptional">True when the source sets <c>isoptional</c>; only a request parameter may.</param>
public sealed record CustomApiFieldShape(string UniqueName, int Type, string? LogicalEntityName, bool HasIsOptional);

/// <summary>
/// The rules a custom API definition must follow (plan 15 C1 and C2), applied by the solution import and by authoring alike.
/// Each method returns the reason a definition is refused, or null; the caller wraps it in its own message
/// (import: <c>Custom API 'x' cannot be imported: {reason}.</c>). The Power Fx check and the rules about an existing API
/// belong to the import.
/// </summary>
public static partial class CustomApiRules
{
    /// <summary>
    /// The reason the API itself is unsupported, or null: a function enabled for workflows; an out-of-range binding or step
    /// type; a bound table missing for an entity or collection binding, set for a global one, or unknown.
    /// </summary>
    /// <param name="tableExists">True for a table of the organization (or, on import, of the solution file).</param>
    public static string? ApiProblem(CustomApiShape api, Func<string, bool> tableExists)
    {
        if (api.IsFunction && api.WorkflowEnabled)
            return "it is a function (isfunction 1) enabled for workflows (workflowsdkstepenabled 1), a combination Dataverse does not allow";
        if (api.BindingType is < 0 or > 2)
            return $"bindingtype {api.BindingType} is not 0 (Global), 1 (Entity) or 2 (Entity Collection)";
        if (api.AllowedStepType is < 0 or > 2)
            return $"allowedcustomprocessingsteptype {api.AllowedStepType} is not 0 (None), 1 (Async Only) or 2 (Sync and Async)";
        return (api.BindingType, api.BoundEntity) switch
        {
            (0, { } bound) => $"it is global (bindingtype 0) but has the boundentitylogicalname '{bound}'",
            (not 0, null) => $"bindingtype {api.BindingType} needs a boundentitylogicalname",
            (not 0, { } bound) when !tableExists(bound) => $"its boundentitylogicalname '{bound}' is a table neither in the solution nor in the organization",
            _ => null,
        };
    }

    /// <summary>
    /// The reason a request parameter (<paramref name="request"/>) or response property is unsupported, or null:
    /// <c>isoptional</c> on a property; a type outside 0 to 12; a <c>logicalentityname</c> on a type that takes none or
    /// naming an unknown table; a name with a character other than a letter, digit or <c>_</c>; a <c>Target</c> parameter
    /// on an entity-bound API (the server adds it).
    /// </summary>
    public static string? FieldProblem(CustomApiShape api, CustomApiFieldShape field, bool request, Func<string, bool> tableExists)
    {
        var what = $"{(request ? "request parameter" : "response property")} '{field.UniqueName}'";
        if (!request && field.HasIsOptional)
            return $"response property '{field.UniqueName}' has isoptional, which only request parameters can have";
        if (field.Type is < 0 or > 12) return $"{what} has the type {field.Type}, which is not a value from 0 to 12";
        if (field.LogicalEntityName is { } table)
        {
            var type = (CustomApiType)field.Type;
            var takesTable = type is CustomApiType.Entity or CustomApiType.EntityReference || (request && type == CustomApiType.EntityCollection);
            if (!takesTable) return $"{what} has the logicalentityname '{table}', but a {type} value names no table";
            if (!tableExists(table))
                return $"{what} has the logicalentityname '{table}', a table neither in the solution nor in the organization";
        }
        if (!HasValidNameCharacters(field.UniqueName)) return $"{what} may hold letters, digits and '_' only";
        if (request && api.BindingType == 1 && string.Equals(field.UniqueName, "Target", StringComparison.OrdinalIgnoreCase))
            return $"it is bound to a table and declares the request parameter '{field.UniqueName}': the server adds Target to an entity-bound API itself";
        return null;
    }

    /// <summary>The reason two request parameters, or two response properties, share a unique name (ignoring case), or null.</summary>
    public static string? DuplicateFieldProblem(CustomApiShape api)
    {
        foreach (var (fields, what) in new[] { (api.RequestParameters, "request parameter"), (api.ResponseProperties, "response property") })
            if (fields.GroupBy(f => f.UniqueName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
                return $"it declares the {what} '{twice.Key}' more than once (unique names ignore case)";
        return null;
    }

    /// <summary>True when <paramref name="name"/> holds letters, digits and <c>_</c> only (and is not empty).</summary>
    public static bool HasValidNameCharacters(string name) => NameCharacters().IsMatch(name);

    /// <summary>
    /// The reason a global API is named like an entity set (exactly, as the Web API compares them), or null: its Web API name
    /// would be the set's, which the entity set routes keep, and <c>$metadata</c> cannot declare both in its container.
    /// </summary>
    /// <param name="entitySets">The table of each entity set, by set name.</param>
    public static string? EntitySetProblem(CustomApiShape api, IReadOnlyDictionary<string, string> entitySets) =>
        api.BindingType == 0 && entitySets.TryGetValue(api.UniqueName, out var table)
            ? $"it is global and '{api.UniqueName}' is the entity set of table '{table}', so the Web API could not call it"
            : null;

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex NameCharacters();
}
