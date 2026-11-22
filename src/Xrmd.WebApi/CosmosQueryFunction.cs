using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Xrm.Sdk;
using Xrmd.Core;
using Xrmd.Core.Elastic;
using Xrmd.Core.Errors;

namespace Xrmd.WebApi;

/// <summary>
/// The Web API function <c>GET ExecuteCosmosSqlQuery(QueryText=@p1,EntityLogicalName=@p2,…)</c> of elastic tables (plan #78),
/// executed as the SDK message of the same name through <see cref="OrganizationService"/> (organization members only). Each
/// parameter is inline or an <c>@alias</c> resolved from the query string. <c>QueryParameters</c> is the Learn
/// <c>ParameterCollection</c> JSON <c>{"Keys":["@a"],"Values":[{"Type":"System.String","Value":"x"}]}</c>.
/// The response is the <c>expando</c> of the message: <c>HasMore</c>, <c>PagingCookie</c> only when present, and
/// <c>Result</c> as a JSON array of <c>expando</c> objects.
/// </summary>
public static class CosmosQueryFunction
{
    private const string Function = ElasticCosmosQuery.MessageName;
    private const string ODataType = "@odata.type";
    private const string Expando = "#Microsoft.Dynamics.CRM.expando";

    private static readonly string[] Allowed = ["QueryText", "EntityLogicalName", "QueryParameters", "PageSize", "PagingCookie", "PartitionId"];

    public static IEndpointRouteBuilder MapCosmosQueryFunction(this IEndpointRouteBuilder parent)
    {
        var routes = parent.MapGroup("").RequireOrganizationMember();
        routes.MapGet("/" + Function + "({parameters})", ExecuteAsync);
        return parent;
    }

    private static async Task<IResult> ExecuteAsync(HttpContext context, OrganizationService service, string parameters)
    {
        var request = ToRequest(parameters, alias =>
            context.Request.Query[alias] is { Count: > 1 }
                ? throw XrmdFaultException.InvalidArgument($"Parameter alias '{alias}' of function '{Function}' is given more than once.")
                : context.Request.Query[alias].ToString() is { Length: > 0 } v ? v : null);
        var response = await service.ExecuteAsync(request, context.RequestAborted);
        var expando = (Entity)response.Results["Result"];

        var items = new JsonArray();
        if (expando.GetAttributeValue<string>("Result") is { } text)
        {
            var rows = JsonNode.Parse(text) as JsonArray
                ?? throw new InvalidOperationException("The message returned a Result that is not a JSON array.");
            foreach (var row in rows)
            {
                var item = new JsonObject { [ODataType] = Expando };
                if (row is JsonObject properties)
                    foreach (var (name, value) in properties)
                        if (name != ODataType) item[name] = value?.DeepClone();
                items.Add(item);
            }
        }

        var json = new JsonObject
        {
            ["@odata.context"] = $"{MetadataController.ServiceRoot(context.Request)}$metadata#expando/$entity",
            [ODataType] = Expando,
        };
        if (expando.GetAttributeValue<string>("PagingCookie") is { } cookie) json["PagingCookie"] = cookie;
        json["HasMore"] = expando.GetAttributeValue<bool>("HasMore");
        json["Result@odata.type"] = "#Collection(Microsoft.Dynamics.CRM.expando)";
        json["Result"] = items;
        return ODataJson.Result(context, json);
    }

    /// <summary>
    /// The SDK request of the function for its parameter list (<c>Name=value</c> pairs, each value inline or an <c>@alias</c>
    /// that <paramref name="alias"/> resolves from the query string). Only the URL level is checked here; the message validates
    /// the values (length, page size bounds, duplicate keys).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an unknown, repeated or unreadable parameter, an alias without a value, or a
    /// <c>QueryParameters</c> value of a type outside the supported ones (naming the key and the type).</exception>
    public static OrganizationRequest ToRequest(string parameters, Func<string, string?> alias)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(alias);
        var request = new OrganizationRequest(Function);
        foreach (var pair in AuditEndpoints.SplitTopLevel(parameters))
        {
            var eq = pair.IndexOf('=');
            var name = eq > 0 ? pair[..eq].Trim() : pair.Trim();
            if (eq <= 0 || !Allowed.Contains(name, StringComparer.Ordinal))
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' is not a valid parameter for the function '{Function}'.");
            if (request.Parameters.ContainsKey(name))
                throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of function '{Function}' is given more than once.");
            var text = pair[(eq + 1)..].Trim();
            var aliased = text.StartsWith('@');
            if (aliased)
                text = alias(text) ?? throw XrmdFaultException.InvalidArgument($"Parameter alias '{text}' of function '{Function}' has no value.");
            if (text == "null") continue;
            switch (name)
            {
                case "QueryParameters":
                    request[name] = ReadQueryParameters(text);
                    break;
                case "PageSize":
                    request[name] = long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var size)
                        ? size
                        : throw XrmdFaultException.InvalidArgument($"The parameter 'PageSize' of function '{Function}' must be an Edm.Int64.");
                    break;
                default:
                    request[name] = ReadString(name, text, aliased);
                    break;
            }
        }
        foreach (var required in (string[])["QueryText", "EntityLogicalName"])
            if (!request.Parameters.ContainsKey(required))
                throw XrmdFaultException.InvalidArgument($"Required parameter '{required}' of function '{Function}' is missing.");
        return request;
    }

    private static string ReadString(string name, string text, bool aliased)
    {
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            var inner = text[1..^1];
            // Every quote inside must be doubled; otherwise the text is not one quoted string.
            if (inner.Replace("''", "", StringComparison.Ordinal).Contains('\''))
            {
                if (!aliased)
                    throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of function '{Function}' has an unescaped quote in its value.");
                return text;
            }
            return inner.Replace("''", "'", StringComparison.Ordinal);
        }
        // An alias value may also arrive unquoted (taken verbatim); an inline one must be a quoted Edm.String.
        if (aliased) return text;
        throw XrmdFaultException.InvalidArgument($"The parameter '{name}' of function '{Function}' must be an Edm.String ('text').");
    }

    private static XrmdFaultException BadCollection(string detail) =>
        XrmdFaultException.InvalidArgument($"The parameter 'QueryParameters' of function '{Function}' is not valid: {detail}.");

    private static ParameterCollection ReadQueryParameters(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(AuditEndpoints.DoubleQuoted(text));
        }
        catch (JsonException)
        {
            throw BadCollection("it is not valid JSON");
        }
        if (node is not JsonObject body || body["Keys"] is not JsonArray keys || body["Values"] is not JsonArray values)
            throw BadCollection("it must be an object with the arrays 'Keys' and 'Values'");
        if (keys.Count != values.Count) throw BadCollection("'Keys' and 'Values' must have the same length");

        var collection = new ParameterCollection();
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i] is JsonValue k && k.TryGetValue<string>(out var s) ? s : throw BadCollection($"key {i} is not a string");
            if (values[i] is not JsonObject entry || entry["Type"] is not JsonValue t || !t.TryGetValue<string>(out var type))
                throw BadCollection($"the value of '{key}' must be an object with a 'Type'");
            collection.Add(new KeyValuePair<string, object>(key, ReadValue(key, type, entry["Value"])));
        }
        return collection;
    }

    private static object ReadValue(string key, string type, JsonNode? value)
    {
        var raw = value switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
            _ => null,
        };
        object? parsed = null;
        var known = true;
        switch (type)
        {
            case "System.String":
                parsed = raw;
                break;
            case "System.Int32":
                parsed = int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i32) ? i32 : null;
                break;
            case "System.Int64":
                parsed = long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i64) ? i64 : null;
                break;
            case "System.Double":
                parsed = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
                break;
            case "System.Decimal":
                parsed = decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : null;
                break;
            case "System.Boolean":
                parsed = bool.TryParse(raw, out var b) ? b : null;
                break;
            default:
                known = false;
                break;
        }
        if (!known) throw BadCollection($"the value of '{key}' has the unsupported type '{type}'");
        return parsed ?? throw BadCollection($"the value of '{key}' is not a valid {type}");
    }
}
