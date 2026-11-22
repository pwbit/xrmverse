using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Pipeline.CustomApis;

/// <summary>
/// Checks a custom API call against its definition (plan 15): the request before anything of the message runs (C6 steps 1–4;
/// step 5, the execute privilege, is the <see cref="PipelineExecutor"/>'s), and the response after the main operation and
/// after the synchronous PostOperation steps (C7). Values are judged by <see cref="CustomApiTypes.SdkType"/>, with no
/// conversion: an <c>int</c> is not a Picklist value.
/// </summary>
public static class CustomApiValidator
{
    private const string Target = "Target";

    /// <summary>The request parameter every message accepts (it travels to the plug-ins as <c>tag</c>).</summary>
    private const string Tag = "tag";

    /// <summary>
    /// Checks <paramref name="request"/> against <paramref name="api"/>, in this order: the <c>Target</c> of an entity-bound
    /// API (an <see cref="EntityReference"/> to a record of the bound table; the record itself is not read); every required
    /// parameter present and not null; every present value of its type, and of the declared table for the record types;
    /// no other parameter than the declared ones, <c>tag</c>, and <c>Target</c> on an entity-bound API.
    /// Parameter names match exactly, as the SDK's parameter collection matches them.
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 <c>0x80040203</c> naming the parameter.</exception>
    public static void Validate(CustomApiDefinition api, OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(api);
        ValidateRequest(api.BindingType == 1 ? api.BoundEntity : null, api.RequestParameters, request, problem => Invalid(api, problem));
    }

    /// <summary>
    /// The checks of <see cref="Validate"/> on a request of any message whose parameters are declared as <paramref name="parameters"/>
    /// (a custom API, or a XAML action, plan 2.14 W14): with <paramref name="boundEntity"/> set, a <c>Target</c> referencing a
    /// record of that table; then the required parameters, the types and the undeclared parameters.
    /// </summary>
    /// <param name="invalid">The fault of a problem (its text says which parameter and how).</param>
    public static void ValidateRequest(string? boundEntity, IReadOnlyList<CustomApiField> parameters, OrganizationRequest request,
        Func<string, Exception> invalid)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invalid);
        var given = request.Parameters;
        var entityBound = boundEntity is not null;

        if (entityBound)
        {
            if (!given.TryGetValue(Target, out var target) || target is not EntityReference reference)
                throw invalid($"it is bound to the table '{boundEntity}', so it needs a '{Target}' parameter holding an "
                              + $"EntityReference to a '{boundEntity}' record.");
            if (!string.Equals(reference.LogicalName, boundEntity, StringComparison.OrdinalIgnoreCase))
                throw invalid($"its '{Target}' must reference a '{boundEntity}' record, not a '{reference.LogicalName}' record.");
        }

        foreach (var field in parameters)
            if (!field.IsOptional && (!given.TryGetValue(field.UniqueName, out var value) || value is null))
                throw invalid($"the required parameter '{field.UniqueName}' has no value.");

        foreach (var field in parameters)
        {
            if (!given.TryGetValue(field.UniqueName, out var value) || value is null) continue;
            if (!Fits(field, value))
                throw invalid($"the parameter '{field.UniqueName}' takes a value of type {Describe(field.Type)}, not {value.GetType().Name}.");
            if (field.LogicalEntityName is { Length: > 0 } table && OtherTable(value, table) is { } other)
                throw invalid($"the parameter '{field.UniqueName}' takes '{table}' records only, not a '{other}' record.");
        }

        foreach (var name in given.Keys)
        {
            if (string.Equals(name, Tag, StringComparison.Ordinal) || (entityBound && string.Equals(name, Target, StringComparison.Ordinal)))
                continue;
            if (!parameters.Any(f => string.Equals(f.UniqueName, name, StringComparison.Ordinal)))
                throw invalid($"it has no request parameter named '{name}'.");
        }
    }

    /// <summary>True when <paramref name="value"/> (not null) is of the SDK type of <paramref name="field"/>, with no conversion.</summary>
    public static bool Fits(CustomApiField field, object value)
    {
        ArgumentNullException.ThrowIfNull(field);
        return CustomApiTypes.SdkType(field.Type).IsInstanceOfType(value);
    }

    /// <summary>The type of <paramref name="type"/> as the validator's messages name it, e.g. <c>Decimal (Decimal)</c>.</summary>
    public static string Describe(CustomApiType type) => $"{type} ({CustomApiTypes.SdkType(type).Name})";

    /// <summary>
    /// Brings the main operation's <paramref name="outputs"/> to the API's declared response properties: a value of another
    /// type fails the call; an unset property gets the default of its type (null for a reference type); a key the API does not
    /// declare is removed and logged as a warning naming the API and the key.
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">500 <c>0x80040224</c> naming the property of the wrong type.</exception>
    public static void CompleteResponse(CustomApiDefinition api, ParameterCollection outputs, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var dropped = Complete(api, outputs);
        if (dropped.Count > 0) LogDropped(logger, api, dropped);
    }

    /// <summary><see cref="CompleteResponse"/> without the log: the keys removed, for the caller to log once.</summary>
    internal static IReadOnlyList<string> Complete(CustomApiDefinition api, ParameterCollection outputs)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(outputs);

        foreach (var field in api.ResponseProperties)
        {
            if (!outputs.TryGetValue(field.UniqueName, out var value)) continue;
            var expected = CustomApiTypes.SdkType(field.Type);
            var fits = value is null ? !expected.IsValueType : expected.IsInstanceOfType(value);
            if (!fits)
                throw XrmdFaultException.CustomApiResponseTypeMismatch(api.UniqueName, field.UniqueName, Describe(field.Type),
                    value is null ? "null" : value.GetType().Name);
        }

        var dropped = outputs.Keys
            .Where(key => !api.ResponseProperties.Any(f => string.Equals(f.UniqueName, key, StringComparison.Ordinal)))
            .ToList();
        foreach (var key in dropped) outputs.Remove(key);

        foreach (var field in api.ResponseProperties)
            if (!outputs.ContainsKey(field.UniqueName))
                outputs[field.UniqueName] = CustomApiTypes.DefaultValue(field.Type);
        return dropped;
    }

    /// <summary>One warning naming the API and every response key its plug-ins set without the API declaring it.</summary>
    internal static void LogDropped(ILogger logger, CustomApiDefinition api, IEnumerable<string> keys) =>
        logger.LogWarning("Custom API {CustomApi} does not declare the response key(s) {Keys} that its plug-ins set; they are left out of the response.",
            api.UniqueName, string.Join(", ", keys));

    private static XrmdFaultException Invalid(CustomApiDefinition api, string problem) =>
        XrmdFaultException.CustomApiInvalidArgument(api.UniqueName, problem);

    /// <summary>The table of the first record in <paramref name="value"/> that is not of <paramref name="table"/>; null when all are.</summary>
    private static string? OtherTable(object value, string table)
    {
        static string? Check(string? name, string table) =>
            string.Equals(name, table, StringComparison.OrdinalIgnoreCase) ? null : name ?? "(none)";

        return value switch
        {
            EntityReference r => Check(r.LogicalName, table),
            Entity e => Check(e.LogicalName, table),
            EntityCollection c => c.Entities.Select(e => Check(e.LogicalName, table)).FirstOrDefault(n => n is not null),
            _ => null,
        };
    }
}
