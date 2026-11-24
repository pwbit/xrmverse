using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.CustomApis;

namespace Xrmd.WebApi;

/// <summary>
/// The JSON of custom API calls on the Web API (plan 15, C8, ruling X9): an action body or a function's parameters read
/// into the API's request parameters, and the response written back.
/// <para>
/// Each declared parameter is read to exactly the SDK type <see cref="CustomApiTypes.SdkType"/> gives its type (an
/// <see cref="OptionSetValue"/> for a Picklist, a <see cref="Money"/> for Money, …), and a JSON value that does not fit
/// that type is 400 <c>0x80040203</c> naming the parameter. Everything else is left to <see cref="CustomApiValidator"/>,
/// which the organization service runs on the request: a missing or null required parameter, an undeclared parameter (kept
/// here as its raw text) and the table of a record.
/// </para>
/// </summary>
public static class CustomApiJson
{
    private const string Ns = ODataEdmModelBuilder.Namespace;
    private const string ODataType = "@odata.type";
    private const string Target = "Target";
    private const string Tag = "tag";

    /// <summary>The request parameters of an action call of <paramref name="api"/> from its JSON <paramref name="body"/>.</summary>
    /// <param name="ieee754Compatible">The content type carries <c>IEEE754Compatible=true</c>: a Decimal or Money value may
    /// then also be a string.</param>
    /// <exception cref="XrmdFaultException">400 <c>0x80040203</c> naming a parameter whose value does not fit its type, or a
    /// <c>Target</c> sent to an entity-bound API (its record is the one of the URL).</exception>
    public static ParameterCollection ReadBody(CustomApiDefinition api, JsonObject body, IMetadataStore store, bool ieee754Compatible)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(store);
        var parameters = new ParameterCollection();
        foreach (var (name, node) in body)
        {
            if (name.StartsWith('@')) continue; // instance annotations
            Add(api, parameters, name, node, raw: node, store, ieee754Compatible);
        }
        return parameters;
    }

    /// <summary>
    /// The request parameters of a function call of <paramref name="api"/>: the <c>Name=value</c> pairs of
    /// <paramref name="inline"/> (the text between the parentheses), each value an OData literal (<c>'text'</c>, a number,
    /// <c>true</c>, a GUID, a date and time, <c>null</c>) or JSON (a record as <c>{'@odata.id':'accounts(id)'}</c> or with
    /// <c>@odata.type</c>, an array of strings), given inline or as an <c>@alias</c> of the query string.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>0x80040203</c> naming the parameter: a value that does not fit its type, a
    /// pair without a value, a parameter given twice, an alias the query does not give, or a <c>Target</c> sent to an
    /// entity-bound API.</exception>
    public static ParameterCollection ReadFunctionParameters(CustomApiDefinition api, string inline, IQueryCollection aliases, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(inline);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(store);
        var parameters = new ParameterCollection();
        if (inline.Trim().Length == 0) return parameters;
        foreach (var pair in AuditEndpoints.SplitTopLevel(inline))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) throw Invalid(api, $"the function parameter '{pair.Trim()}' has no value: write each parameter as Name=value.");
            var name = pair[..eq].Trim();
            if (parameters.ContainsKey(name)) throw Invalid(api, $"the parameter '{name}' is given more than once.");
            var text = pair[(eq + 1)..].Trim();
            if (text.StartsWith('@'))
                text = aliases[text].ToString() is { Length: > 0 } value
                    ? value
                    : throw Invalid(api, $"the parameter '{name}' refers to the alias '{text}', which the query string does not give.");
            var field = Field(api, name);
            Add(api, parameters, name, field is null ? null : Literal(api, field, text), raw: JsonValue.Create(text), store, ieee754Compatible: false);
        }
        return parameters;
    }

    /// <summary>
    /// Adds the <c>tag</c> of the query string (<c>?tag=value</c>, the Web API's way to pass it) to
    /// <paramref name="parameters"/>, as the text it decodes to.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>0x80040203</c> when the query gives <c>tag</c> more than once, or when the API
    /// declares a parameter named <c>tag</c> (the two would be one parameter).</exception>
    public static void ReadTag(CustomApiDefinition api, ParameterCollection parameters, IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(query);
        if (!query.TryGetValue(Tag, out var values)) return;
        if (values.Count != 1) throw Invalid(api, $"the query string gives '{Tag}' more than once.");
        if (Field(api, Tag) is not null)
            throw Invalid(api, $"it declares a parameter named '{Tag}', so '?{Tag}=' cannot be told apart from it: pass the parameter in the call.");
        parameters[Tag] = values[0];
    }

    /// <summary>
    /// The Web API response of a call of <paramref name="api"/>: 204 when it declares no response property; when its only
    /// property is an Entity, that record (<c>$metadata#{set}/$entity</c>, 204 when unset); when its only property is an
    /// EntityCollection, <c>{ "value": [...] }</c> (<c>$metadata#Collection(Microsoft.Dynamics.CRM.crmbaseentity)</c>, empty
    /// when unset); otherwise 200 with <c>$metadata#Microsoft.Dynamics.CRM.{name}Response</c> and one property per response
    /// property. Records carry their <c>@odata.type</c> unless the property declares their table.
    /// </summary>
    /// <param name="baseUrl">Scheme, host and path base of the server (<see cref="ODataJson.BaseUrl"/>).</param>
    /// <param name="store">The tables of the records written.</param>
    public static IResult WriteResponse(CustomApiDefinition api, OrganizationResponse response, string baseUrl, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(store);
        var metadata = $"{baseUrl.TrimEnd('/')}{MetadataController.ServiceRootPath}$metadata#";
        var properties = api.ResponseProperties;
        if (properties.Count == 0) return new ODataResult(null);

        object? ValueOf(CustomApiField field) => response.Results.TryGetValue(field.UniqueName, out var value) ? value : null;

        if (properties is [{ Type: CustomApiType.Entity } single])
        {
            if (ValueOf(single) is not Entity entity) return new ODataResult(null);
            var m = store.GetEntity(entity.LogicalName);
            var json = new JsonObject
            {
                ["@odata.context"] = m?.EntitySetName is { } set ? $"{metadata}{set}/$entity" : $"{metadata}{Ns}.crmbaseentity/$entity",
            };
            foreach (var (key, value) in Record(entity, store, typed: string.IsNullOrEmpty(single.LogicalEntityName)))
                json[key] = value?.DeepClone();
            return new ODataResult(json);
        }

        if (properties is [{ Type: CustomApiType.EntityCollection } many])
        {
            var records = new JsonArray();
            foreach (var entity in (ValueOf(many) as EntityCollection)?.Entities ?? Enumerable.Empty<Entity>()) records.Add(Record(entity, store, typed: true));
            return new ODataResult(new JsonObject
            {
                ["@odata.context"] = $"{metadata}Collection({Ns}.crmbaseentity)",
                ["value"] = records,
            });
        }

        var result = new JsonObject { ["@odata.context"] = $"{metadata}{Ns}.{api.UniqueName}Response" };
        foreach (var field in properties) result[field.UniqueName] = Value(ValueOf(field), store, typed: string.IsNullOrEmpty(field.LogicalEntityName));
        return new ODataResult(result);
    }

    // ---- reading ----------------------------------------------------------------------------------------------------

    private static CustomApiField? Field(CustomApiDefinition api, string name) =>
        api.RequestParameters.FirstOrDefault(f => string.Equals(f.UniqueName, name, StringComparison.Ordinal));

    /// <summary>
    /// Adds the parameter <paramref name="name"/>: a declared one read to its SDK type (a null of an optional one left out, of
    /// a required one kept for the validator to refuse); an undeclared one as the raw text of <paramref name="raw"/>, which the
    /// validator refuses naming it. An undeclared <c>tag</c> is refused here: the Web API takes it from the query string only
    /// (<see cref="ReadTag"/>).
    /// </summary>
    private static void Add(CustomApiDefinition api, ParameterCollection parameters, string name, JsonNode? node, JsonNode? raw,
        IMetadataStore store, bool ieee754Compatible)
    {
        if (api.BindingType == 1 && string.Equals(name, Target, StringComparison.Ordinal))
            throw Invalid(api, $"the '{Target}' of an entity-bound API is the record of the URL; it is not a parameter of the call.");
        var field = Field(api, name);
        if (field is null && string.Equals(name, Tag, StringComparison.Ordinal))
            throw Invalid(api, $"'{Tag}' is not taken from the body or the parameter list on the Web API: send it in the query string as '?{Tag}=value'.");
        if (field is null)
        {
            parameters[name] = raw is JsonValue text && text.TryGetValue<string>(out var s) ? s : raw?.ToJsonString();
            return;
        }
        if (node is null)
        {
            if (!field.IsOptional) parameters[name] = null;
            return;
        }
        parameters[name] = Read(api, field, node, store, ieee754Compatible);
    }

    /// <summary>
    /// <paramref name="node"/> as a value of the SDK type of <paramref name="field"/> (<see cref="CustomApiTypes.SdkType"/>):
    /// JSON true/false, numbers (integral for Integer and Picklist), strings (GUID, ISO 8601 date and time), arrays of
    /// strings, and records (<c>@odata.type</c> with the columns or the primary key, or <c>@odata.id</c> for a reference).
    /// </summary>
    private static object Read(CustomApiDefinition api, CustomApiField field, JsonNode node, IMetadataStore store, bool ieee754Compatible)
    {
        var type = CustomApiTypes.SdkType(field.Type);
        var value = node as JsonValue;
        var kind = node.GetValueKind();

        if (type == typeof(Entity)) return ActionController.ReadEntity(api.UniqueName, field.UniqueName, node, store, field.LogicalEntityName);
        if (type == typeof(EntityCollection))
            return ActionController.ReadEntities(api.UniqueName, field.UniqueName, node, store, field.LogicalEntityName);
        if (type == typeof(EntityReference)) return AuditEndpoints.ReadReference(api.UniqueName, field.UniqueName, node, store);
        if (type == typeof(DateTime)) return AuditEndpoints.ReadDateTimeOffset(api.UniqueName, field.UniqueName, node);
        if (type == typeof(bool) && kind is JsonValueKind.True or JsonValueKind.False) return kind == JsonValueKind.True;
        if (type == typeof(int) && kind == JsonValueKind.Number && value!.TryGetValue<int>(out var integer)) return integer;
        if (type == typeof(OptionSetValue) && kind == JsonValueKind.Number && value!.TryGetValue<int>(out var option)) return new OptionSetValue(option);
        if (type == typeof(double) && kind == JsonValueKind.Number && value!.TryGetValue<double>(out var real)) return real;
        if (type == typeof(decimal) && Decimal(value, kind, ieee754Compatible) is { } number) return number;
        if (type == typeof(Money) && Decimal(value, kind, ieee754Compatible) is { } amount) return new Money(amount);
        if (type == typeof(string) && kind == JsonValueKind.String) return value!.GetValue<string>();
        if (type == typeof(Guid) && kind == JsonValueKind.String && Guid.TryParseExact(value!.GetValue<string>(), "D", out var guid)) return guid;
        if (type == typeof(string[]) && node is JsonArray items && items.All(i => i?.GetValueKind() == JsonValueKind.String))
            return items.Select(i => i!.GetValue<string>()).ToArray();
        throw Invalid(api, $"the parameter '{field.UniqueName}' takes a value of type {field.Type} "
                           + $"({CustomApiTypes.EdmTypeName(field.Type, field.LogicalEntityName)}), which {Describe(node)} is not.");
    }

    private static decimal? Decimal(JsonValue? value, JsonValueKind kind, bool ieee754Compatible)
    {
        if (value is null) return null;
        if (kind == JsonValueKind.Number && value.TryGetValue<decimal>(out var number)) return number;
        return ieee754Compatible && kind == JsonValueKind.String
               && decimal.TryParse(value.GetValue<string>(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// A function parameter's OData literal as JSON: <c>null</c> as null; <c>'text'</c> (<c>''</c> for a quote) as a string;
    /// a number, <c>true</c>/<c>false</c>, or JSON (single-quoted strings allowed) as itself; any other bare word as a string
    /// for the types whose literals are bare (Guid, DateTime), and refused for the others.
    /// </summary>
    private static JsonNode? Literal(CustomApiDefinition api, CustomApiField field, string text)
    {
        if (text == "null") return null;
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
            return JsonValue.Create(text[1..^1].Replace("''", "'", StringComparison.Ordinal));
        try
        {
            return JsonNode.Parse(AuditEndpoints.DoubleQuoted(text)) ?? throw new JsonException("null");
        }
        catch (JsonException)
        {
            var type = CustomApiTypes.SdkType(field.Type);
            if (type == typeof(Guid) || type == typeof(DateTime)) return JsonValue.Create(text);
            throw Invalid(api, $"the value '{text}' of the parameter '{field.UniqueName}' is not an OData literal of type {field.Type} "
                               + $"({CustomApiTypes.EdmTypeName(field.Type, field.LogicalEntityName)}); quote a string as 'text'.");
        }
    }

    private static string Describe(JsonNode node) => node.GetValueKind() switch
    {
        JsonValueKind.Object => "a JSON object",
        JsonValueKind.Array => "a JSON array",
        JsonValueKind.String => "the JSON string " + node.ToJsonString(),
        JsonValueKind.Number => "the JSON number " + node.ToJsonString(),
        _ => "the JSON value " + node.ToJsonString(),
    };

    private static XrmdFaultException Invalid(CustomApiDefinition api, string problem) =>
        XrmdFaultException.CustomApiInvalidArgument(api.UniqueName, problem);

    // ---- writing ----------------------------------------------------------------------------------------------------

    /// <summary>A response value as JSON: records as objects, collections and string arrays as arrays, the rest as columns are.</summary>
    private static JsonNode? Value(object? value, IMetadataStore store, bool typed) => value switch
    {
        null => null,
        Entity entity => Record(entity, store, typed),
        EntityCollection collection => new JsonArray(collection.Entities.Select(e => (JsonNode)Record(e, store, typed: true)).ToArray()),
        EntityReference reference => Reference(reference, store, typed),
        string[] texts => new JsonArray(texts.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
        _ => ODataJson.ToJsonValue(value),
    };

    /// <summary>
    /// A record: <c>@odata.type</c> when <paramref name="typed"/>, then its columns as a record of its table is written (any
    /// value as a plain column when the table is unknown), and its primary key from <see cref="Entity.Id"/> when the columns
    /// lack it.
    /// </summary>
    private static JsonObject Record(Entity entity, IMetadataStore store, bool typed)
    {
        var m = string.IsNullOrEmpty(entity.LogicalName) ? null : store.GetEntity(entity.LogicalName);
        var json = new JsonObject();
        if (typed && !string.IsNullOrEmpty(entity.LogicalName)) json[ODataType] = $"#{Ns}.{entity.LogicalName}";
        var columns = m is not null ? ODataJson.ToRecordJson(entity, m, includeFormattedValues: false) : Plain(entity, store);
        foreach (var (key, value) in columns) json[key] = value?.DeepClone();
        if (entity.Id != Guid.Empty && PrimaryKey(entity.LogicalName, m) is { } primaryKey && !json.ContainsKey(primaryKey))
            json[primaryKey] = entity.Id.ToString();
        return json;
    }

    private static JsonObject Plain(Entity entity, IMetadataStore store)
    {
        var json = new JsonObject();
        foreach (var (name, value) in entity.Attributes) json[name] = Value(value, store, typed: true);
        return json;
    }

    /// <summary>A reference as a record of its table holding its primary key: <c>{ "@odata.type", "{table}id": "{guid}" }</c>.</summary>
    private static JsonObject Reference(EntityReference reference, IMetadataStore store, bool typed)
    {
        var json = new JsonObject();
        if (typed) json[ODataType] = $"#{Ns}.{reference.LogicalName}";
        if (PrimaryKey(reference.LogicalName, store.GetEntity(reference.LogicalName ?? "")) is { } key) json[key] = reference.Id.ToString();
        return json;
    }

    private static string? PrimaryKey(string? table, Microsoft.Xrm.Sdk.Metadata.EntityMetadata? m) =>
        m is not null ? SystemAttributes.PrimaryIdName(m) : string.IsNullOrEmpty(table) ? null : table + "id";

    /// <summary>A Web API JSON response (<see cref="ODataJson.Result"/>), or 204 with <c>OData-Version</c> when there is no body.</summary>
    private sealed class ODataResult(JsonObject? json) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            if (json is not null) return ODataJson.Result(httpContext, json).ExecuteAsync(httpContext);
            httpContext.Response.Headers["OData-Version"] = "4.0";
            httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }
    }
}
