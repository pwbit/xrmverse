using Xrmd.Pipeline.CustomApis;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Solution;

/// <summary>
/// The rules a custom API of a solution file must follow to be imported (plan 15, C1, C2 and C11) that need no database:
/// the definition itself, the shape of its unique name and, on a re-import, the properties that cannot change. Each refusal
/// is a 400 InvalidArgument naming the API, and the property or parameter at fault. The rules that read the organization
/// (an existing message, the plug-in type, the existing API) are the importer's.
/// </summary>
internal static class CustomApiImportRules
{
    /// <summary>The elements of a low-code (Power Fx) custom API, which xrmverse does not support (C1).</summary>
    private static readonly string[] PowerFxMarkers = ["powerfxruleid", "fxexpressionid"];

    /// <summary>The API's state in the organization, for a re-import (C11).</summary>
    /// <param name="Parameters">The stored request parameters, by unique name (ignoring case).</param>
    /// <param name="Properties">The stored response properties, by unique name (ignoring case).</param>
    internal sealed record Existing(Guid Id, string UniqueName, Guid? MessageId, int BindingType, string? BoundEntity, bool IsFunction,
        bool WorkflowEnabled, int AllowedStepType, IReadOnlyDictionary<string, ExistingField> Parameters,
        IReadOnlyDictionary<string, ExistingField> Properties);

    /// <summary>A stored request parameter or response property.</summary>
    internal sealed record ExistingField(Guid Id, int Type, bool IsOptional, string? LogicalEntityName);

    /// <summary>
    /// C1: the definition is one xrmverse can run. Refused: a Power Fx API; a function enabled for workflows; <c>isoptional</c>
    /// on a response property; an out-of-range binding, step type or parameter type; a bound table missing for an entity or
    /// collection binding, set for a global one, or unknown; a <c>logicalentityname</c> on a type that takes none or naming an
    /// unknown table; two request parameters, or two response properties, with one unique name (ignoring case); a
    /// <c>Target</c> request parameter on an entity-bound API (the server adds it, C2); a request parameter or response property
    /// whose unique name holds a character other than a letter, digit or <c>_</c>. The first fault is the one reported, in this
    /// order: Power Fx, the API itself, then each field (request parameters before response properties; per field the
    /// <c>isoptional</c>, type, <c>logicalentityname</c>, name-character and <c>Target</c> checks), and last the duplicate names.
    /// </summary>
    /// <param name="tableExists">True for a table of the organization or of the solution file.</param>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the API.</exception>
    public static void DemandSupported(PackageCustomApi api, Func<string, bool> tableExists)
    {
        var name = api.UniqueName;
        foreach (var (owner, kept) in KeptElements(api))
            if (kept?.Elements().FirstOrDefault(e => PowerFxMarkers.Contains(e.Name.LocalName) && !UnmodelledXml.IsMarker(e)) is { } marker)
                throw Refused(name, $"{owner} has '{marker.Name.LocalName}': it is a low-code (Power Fx) custom API, which xrmverse does not support");
        var shape = ToShape(api);
        if (CustomApiRules.ApiProblem(shape, tableExists) is { } problem) throw Refused(name, problem);
        foreach (var (field, request) in shape.RequestParameters.Select(p => (p, true)).Concat(shape.ResponseProperties.Select(p => (p, false))))
            if (CustomApiRules.FieldProblem(shape, field, request, tableExists) is { } fieldProblem) throw Refused(name, fieldProblem);
        if (CustomApiRules.DuplicateFieldProblem(shape) is { } duplicate) throw Refused(name, duplicate);
    }

    /// <summary>C2, the shape of the name: letters, digits and <c>_</c> only.</summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the API.</exception>
    public static void DemandNameCharacters(PackageCustomApi api)
    {
        if (!CustomApiRules.HasValidNameCharacters(api.UniqueName))
            throw Refused(api.UniqueName, "its uniquename may hold letters, digits and '_' only");
    }

    /// <summary>
    /// C11: a re-import may change the names, labels, <c>isprivate</c>, <c>executeprivilegename</c> and the plug-in type, and
    /// add parameters and properties; any other difference from <paramref name="existing"/> is refused, naming the property.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the API and the property.</exception>
    public static void DemandUnchangedImmutables(PackageCustomApi api, Existing existing)
    {
        void Same<T>(string property, T stored, T file)
        {
            if (!EqualityComparer<T>.Default.Equals(stored, file))
                throw Immutable(api.UniqueName, property, Show(stored), Show(file));
        }

        Same("allowedcustomprocessingsteptype", existing.AllowedStepType, api.AllowedStepType);
        Same("bindingtype", existing.BindingType, api.BindingType);
        Same("boundentitylogicalname", existing.BoundEntity?.ToLowerInvariant(), api.BoundEntity?.ToLowerInvariant());
        Same("isfunction", existing.IsFunction, api.IsFunction);
        Same("workflowsdkstepenabled", existing.WorkflowEnabled, api.WorkflowEnabled);
        foreach (var parameter in api.RequestParameters)
        {
            if (!existing.Parameters.TryGetValue(parameter.UniqueName, out var stored)) continue;
            var owner = $"{api.UniqueName}' request parameter '{parameter.UniqueName}";
            if (stored.IsOptional != (parameter.IsOptional == true))
                throw Immutable(owner, "isoptional", Show(stored.IsOptional), Show(parameter.IsOptional == true));
            if (!SameTable(stored.LogicalEntityName, parameter.LogicalEntityName))
                throw Immutable(owner, "logicalentityname", Show(stored.LogicalEntityName), Show(parameter.LogicalEntityName));
            if (stored.Type != parameter.Type) throw Immutable(owner, "type", Show(stored.Type), Show(parameter.Type));
        }
        foreach (var property in api.ResponseProperties)
        {
            if (!existing.Properties.TryGetValue(property.UniqueName, out var stored)) continue;
            var owner = $"{api.UniqueName}' response property '{property.UniqueName}";
            if (!SameTable(stored.LogicalEntityName, property.LogicalEntityName))
                throw Immutable(owner, "logicalentityname", Show(stored.LogicalEntityName), Show(property.LogicalEntityName));
            if (stored.Type != property.Type) throw Immutable(owner, "type", Show(stored.Type), Show(property.Type));
        }
    }

    /// <summary>
    /// A global API is not named like an entity set (exactly, as the Web API compares them): its Web API name would be the
    /// set's, which the entity set routes keep, and <c>$metadata</c> cannot declare both in its container.
    /// </summary>
    /// <param name="entitySets">The table of each entity set of the organization and of the solution file, by set name.</param>
    /// <exception cref="XrmdFaultException">400 InvalidArgument naming the API and the table.</exception>
    public static void DemandNotAnEntitySet(PackageCustomApi api, IReadOnlyDictionary<string, string> entitySets)
    {
        if (CustomApiRules.EntitySetProblem(ToShape(api), entitySets) is { } problem) throw Refused(api.UniqueName, problem);
    }

    /// <summary>A refusal of the API <paramref name="name"/>: 400 InvalidArgument.</summary>
    public static XrmdFaultException Refused(string name, string reason) =>
        XrmdFaultException.InvalidArgument($"Custom API '{name}' cannot be imported: {reason}.");

    private static XrmdFaultException Immutable(string owner, string property, string stored, string file) =>
        XrmdFaultException.InvalidArgument(
            $"Custom API '{owner}' cannot be changed by a solution import: {property} is {stored} in the organization and {file} in the file, and it cannot change once created.");

    private static bool SameTable(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    private static string Show<T>(T value) => value switch
    {
        null => "(none)",
        bool b => b ? "1" : "0",
        string s => $"'{s}'",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    private static IEnumerable<(string Owner, System.Xml.Linq.XElement? Kept)> KeptElements(PackageCustomApi api) =>
        new[] { ("it", api.Kept) }
            .Concat(api.RequestParameters.Select(p => ($"request parameter '{p.UniqueName}'", p.Kept)))
            .Concat(api.ResponseProperties.Select(p => ($"response property '{p.UniqueName}'", p.Kept)));

    private static CustomApiShape ToShape(PackageCustomApi api) =>
        new(api.UniqueName, api.BindingType, api.BoundEntity, api.IsFunction, api.WorkflowEnabled, api.AllowedStepType,
            api.RequestParameters.Select(ToShape).ToList(), api.ResponseProperties.Select(ToShape).ToList());

    private static CustomApiFieldShape ToShape(PackageCustomApiField field) =>
        new(field.UniqueName, field.Type, field.LogicalEntityName,
            field.Kept?.Element("isoptional") is { } optional && !UnmodelledXml.IsMarker(optional));
}
