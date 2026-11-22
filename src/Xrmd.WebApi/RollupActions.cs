using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.OData.Edm;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi;

/// <summary>
/// The rollup function of the Web API (plan 2.12b, ruling R10): <c>GET CalculateRollupField(Target=@t,FieldName=@f)?@t={'@odata.id':'accounts(…)'}&amp;@f='…'</c>,
/// each parameter inline or an <c>@alias</c> of the query string, executed as the SDK's <c>CalculateRollupField</c> request through
/// <see cref="OrganizationService"/>, so its privilege check (Write on the row, <c>0x8004E027</c>), its refusals and its field
/// masking are the SDK path's. It answers 200 with the row as an entity of its table (<c>$metadata#{set}/$entity</c>): the primary
/// key, the rollup's value, <c>_date</c> and <c>_state</c> (and <c>_base</c> for Currency), a column the caller cannot read as null.
/// </summary>
public static partial class RollupActions
{
    private const string Ns = ODataEdmModelBuilder.Namespace;
    private const string Function = CoreOperations.CalculateRollupFieldMessage;
    private static readonly string[] Parameters = ["Target", "FieldName"];

    /// <summary>The unbound operation names (reserved as entity set names, see <see cref="ActionController.OperationNames"/>).</summary>
    public static IReadOnlyList<string> OperationNames { get; } = [Function];

    public static IEndpointRouteBuilder MapRollupEndpoints(this IEndpointRouteBuilder parent)
    {
        parent.MapGroup("").RequireOrganizationMember().MapGet("/" + Function + "({parameters})", CalculateAsync);
        return parent;
    }

    private static async Task<IResult> CalculateAsync(HttpContext context, IMetadataStore store, OrganizationService service, string parameters)
    {
        var request = ParseRequest(parameters, alias => context.Request.Query[alias].ToString() is { Length: > 0 } v ? v : null, store);
        var response = await service.ExecuteAsync(request, context.RequestAborted);
        var entity = response.Results.TryGetValue("Entity", out var value) && value is Entity e
            ? e
            : throw new XrmdFaultException(ErrorCodes.Unexpected, $"{Function} returned no Entity.", 500);
        var m = store.GetEntity(entity.LogicalName)
                ?? throw new XrmdFaultException(ErrorCodes.Unexpected, $"The table '{entity.LogicalName}' of {Function} does not exist.", 500);
        var json = new JsonObject
        {
            ["@odata.context"] = $"{MetadataController.ServiceRoot(context.Request)}$metadata#{m.EntitySetName}/$entity",
            [SystemAttributes.PrimaryIdName(m)] = entity.Id.ToString(),
        };
        foreach (var (name, column) in entity.Attributes) json[name] = ODataJson.ToJsonValue(column);
        return ODataJson.Result(context, json);
    }

    /// <summary>
    /// The SDK request of <c>CalculateRollupField(…)</c> for its parameter list (<c>Name=value</c> pairs, each value inline or an
    /// <c>@alias</c> that <paramref name="alias"/> resolves). <c>Target</c> is <c>{'@odata.id':'&lt;set&gt;(&lt;id&gt;)'}</c> (single or
    /// double quotes; the id may be a full URL); <c>FieldName</c> a single-quoted column name.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown, repeated or missing parameter, an alias without a value or a value of the
    /// wrong shape, naming it; 404 for an unknown entity set.</exception>
    public static CalculateRollupFieldRequest ParseRequest(string parameters, Func<string, string?> alias, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(store);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in AuditEndpoints.SplitTopLevel(parameters ?? ""))
        {
            var eq = pair.IndexOf('=');
            var name = eq > 0 ? pair[..eq].Trim() : pair.Trim();
            if (eq <= 0 || !Parameters.Contains(name, StringComparer.Ordinal))
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' is not a valid parameter for the function '{Function}'.");
            if (values.ContainsKey(name))
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of function '{Function}' is given more than once.");
            var text = pair[(eq + 1)..].Trim();
            if (text.StartsWith('@'))
                text = alias(text)?.Trim() ?? throw XrmdFaultException.InvalidArgument($"Parameter alias '{text}' of function '{Function}' has no value.");
            values[name] = text;
        }
        foreach (var required in Parameters)
            if (!values.TryGetValue(required, out var given) || given.Length == 0)
                throw XrmdFaultException.InvalidArgument($"Required parameter '{required}' of function '{Function}' is missing.");

        return new CalculateRollupFieldRequest { Target = ReadTarget(values["Target"], store), FieldName = ReadFieldName(values["FieldName"]) };
    }

    private static EntityReference ReadTarget(string text, IMetadataStore store)
    {
        var id = text.StartsWith('{') ? ODataId().Match(text) : Match.Empty;
        string? setName = null, keyText = null;
        if (id.Success)
        {
            var path = id.Groups[1].Value;
            ODataJson.TrySplitKeySegment(path[(path.LastIndexOf('/') + 1)..], out setName, out keyText);
        }
        if (setName is null || !Guid.TryParse(keyText, out var key))
            throw XrmdFaultException.InvalidArgument(
                $"The parameter 'Target' of function '{Function}' must be a record reference of the form {{'@odata.id':'<entity set>(<guid>)'}}.");
        var entity = store.GetEntityBySetName(setName)
                     ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{setName}'.", 404);
        return new EntityReference(entity.LogicalName, key);
    }

    private static string ReadFieldName(string text) =>
        QuotedName().Match(text) is { Success: true } quoted
            ? quoted.Groups[1].Value
            : throw XrmdFaultException.InvalidArgument($"The parameter 'FieldName' of function '{Function}' must be a column logical name in single quotes ('<name>').");

    [GeneratedRegex(@"@odata\.id['""]\s*:\s*['""]([^'""]+)['""]", RegexOptions.CultureInvariant)]
    private static partial Regex ODataId();

    [GeneratedRegex(@"\A'([A-Za-z_][A-Za-z0-9_]*)'\z", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedName();

    /// <summary>
    /// Declares <c>CalculateRollupField</c> (unbound, <c>Target</c> a <c>crmbaseentity</c> and <c>FieldName</c> a string, returning a
    /// <c>crmbaseentity</c>, Learn's signature). Runs after <see cref="ActionController"/>'s operations, which declare <c>crmbaseentity</c>.
    /// </summary>
    internal static void AddOperations(EdmModel model, EdmEntityContainer container)
    {
        var crmBase = (IEdmEntityType)(model.FindDeclaredType($"{Ns}.crmbaseentity")
                                       ?? throw new InvalidOperationException("crmbaseentity must be declared before the rollup function."));
        var function = new EdmFunction(Ns, Function, new EdmEntityTypeReference(crmBase, false), isBound: false,
            entitySetPathExpression: null, isComposable: false);
        function.AddParameter("Target", new EdmEntityTypeReference(crmBase, false));
        function.AddParameter("FieldName", EdmCoreModel.Instance.GetString(false));
        model.AddElement(function);
        container.AddFunctionImport(Function, function, null, includeInServiceDocument: false);
    }
}
