using System.Text.Json.Nodes;
using Xrmd.Metadata;
using Xrmd.Pipeline.CustomApis;

namespace Xrmd.WebApi.Connector;

/// <summary>
/// X.12a: the JSON Schema of a custom API for the custom connector's designer: the request parameters (a non-optional one is
/// required) or the response properties. Entity-valued types are free objects in the Web API shape.
/// </summary>
public static class CustomApiSchemaBuilder
{
    public static JsonObject Build(CustomApiDefinition api, bool request)
    {
        ArgumentNullException.ThrowIfNull(api);
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in request ? api.RequestParameters : api.ResponseProperties)
        {
            properties[field.UniqueName] = Property(field);
            if (request && !field.IsOptional) required.Add(field.UniqueName);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static JsonObject Property(CustomApiField field)
    {
        var p = new JsonObject();
        switch (field.Type)
        {
            case CustomApiType.Boolean: p["type"] = "boolean"; break;
            case CustomApiType.DateTime: p["type"] = "string"; p["format"] = "date-time"; break;
            case CustomApiType.Decimal or CustomApiType.Float or CustomApiType.Money: p["type"] = "number"; break;
            case CustomApiType.Integer: p["type"] = "integer"; p["format"] = "int32"; break;
            case CustomApiType.Picklist: p["type"] = "integer"; break;
            case CustomApiType.StringArray: p["type"] = "array"; p["items"] = new JsonObject { ["type"] = "string" }; break;
            case CustomApiType.Guid: p["type"] = "string"; p["format"] = "uuid"; break;
            case CustomApiType.Entity or CustomApiType.EntityCollection or CustomApiType.EntityReference:
                p["type"] = "object";
                p["additionalProperties"] = true;
                p["x-ms-summary"] = field.UniqueName;
                p["description"] = "The Web API shape of the value is expected" +
                    (field.LogicalEntityName is { } table ? $" (table '{table}')." : ".");
                return p;
            default: p["type"] = "string"; break;
        }
        p["x-ms-summary"] = field.UniqueName;
        return p;
    }
}
