using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Query.Cosmos;
using Xrmd.Query.Sql;

namespace Xrmd.Core.Elastic;

/// <summary>
/// Stage 30 of <c>ExecuteCosmosSqlQuery</c> (plan #78, rulings J1 to J9): validates the six request parameters, parses the query
/// text, translates it to an ordinary query model on the elastic table, runs it through the core query path (read privilege,
/// row and field security, partition filter, paging) and writes the rows as the JSON array of the response. Paths inside a JSON
/// column are read in C# from the column value, so a column the caller cannot read simply yields "undefined" (omitted).
/// </summary>
public static class ElasticCosmosQuery
{
    /// <summary>The message name.</summary>
    public const string MessageName = "ExecuteCosmosSqlQuery";

    /// <summary>The longest <c>QueryText</c> accepted, in characters.</summary>
    public const int MaxQueryTextLength = 32768;

    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    private static XrmdFaultException Invalid(string detail) => XrmdFaultException.InvalidArgument($"{MessageName}: {detail}");

    /// <summary>Runs the message.</summary>
    /// <returns>A response whose <c>Result</c> is an <c>expando</c> entity with <c>HasMore</c>, <c>Result</c> (a JSON array as
    /// a string) and, only when more rows follow, <c>PagingCookie</c>.</returns>
    /// <exception cref="XrmdFaultException">400 for a bad parameter, an unsupported construct or a table that is not elastic;
    /// 403 for the read denials of the query itself.</exception>
    public static async Task<OrganizationResponse> ExecuteAsync(CoreOperations core, DataSession s, Caller caller,
        OrganizationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(request);
        var text = StringParameter(request, "QueryText");
        var tableName = StringParameter(request, "EntityLogicalName");
        if (text.Length > MaxQueryTextLength) throw Invalid($"QueryText must be at most {MaxQueryTextLength} characters.");
        var pageSize = PageSizeParameter(request);
        var cookie = OptionalString(request, "PagingCookie");
        var partition = OptionalString(request, "PartitionId");
        var parameters = Parameters(request);

        var table = core.EntityOf(tableName);
        if (!ElasticTables.IsElastic(table))
            throw XrmdFaultException.InvalidArgument($"{MessageName} applies to elastic tables only; '{table.LogicalName}' is not an elastic table.");

        CosmosQueryPlan plan;
        try
        {
            plan = CosmosQueryTranslator.Translate(CosmosSqlParser.Parse(text), table, parameters, pageSize, cookie);
        }
        catch (XrmdValidationException ex)
        {
            throw XrmdFaultException.InvalidArgument(ex.Message);
        }
        var model = partition is null ? plan.Model : ElasticQueryRules.WithPartition(plan.Model, partition);
        var rows = await core.RetrieveMultipleAsync(s, caller, model, ct);

        var result = new Entity("expando")
        {
            ["HasMore"] = rows.MoreRecords,
            ["Result"] = WriteResult(rows.Entities, plan.Projection, table),
        };
        if (rows.MoreRecords)
        {
            // Keyset paging always hands back a cookie with more rows: without one the client could not continue.
            if (string.IsNullOrEmpty(rows.PagingCookie))
                throw new InvalidOperationException("The query reported more rows but returned no paging cookie.");
            result["PagingCookie"] = CosmosPagingCookie.Encode(rows.PagingCookie);
        }
        return new OrganizationResponse { ResponseName = MessageName, Results = { ["Result"] = result } };
    }

    /// <summary>
    /// The rows as a JSON array, one object per row with the properties in projection order (ruling J5). A value that is
    /// undefined is left out: a null column, a column the caller cannot read (it is absent from the row), a missing path, a path
    /// step on a value of the wrong kind, or a column value that is not valid JSON.
    /// </summary>
    /// <param name="table">When given, a projected JSON column without steps is embedded as JSON rather than as a string.</param>
    public static string WriteResult(IReadOnlyList<Entity> rows, IReadOnlyList<CosmosProjection> projection, EntityMetadata? table = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(projection);
        // Per projected item, once: is its column a JSON column (so a whole-column item is embedded as JSON)?
        var json = projection.Select(item => table?.Attributes.FirstOrDefault(a =>
            string.Equals(a.LogicalName, item.Attribute, StringComparison.OrdinalIgnoreCase)) is { } column && JsonPaths.IsJson(column)).ToArray();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                // One parsed document per (row, column), shared by every path into that column.
                var documents = new Dictionary<string, JsonDocument?>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    for (var n = 0; n < projection.Count; n++)
                    {
                        var item = projection[n];
                        if (!row.Attributes.TryGetValue(item.Attribute, out var value) || value is null) continue;
                        if (item.JsonSteps.Count == 0 && !json[n])
                        {
                            WriteValue(writer, item.Name, value);
                        }
                        else if (value is string text)
                        {
                            if (!documents.TryGetValue(item.Attribute, out var document)) documents[item.Attribute] = document = Parse(text);
                            if (document is not null && Walk(document.RootElement, item.JsonSteps) is JsonElement element)
                            {
                                writer.WritePropertyName(item.Name);
                                element.WriteTo(writer);
                            }
                        }
                    }
                }
                finally
                {
                    foreach (var document in documents.Values) document?.Dispose();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static JsonDocument? Parse(string text)
    {
        try
        {
            return JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 });
        }
        catch (JsonException)
        {
            return null; // not JSON: undefined
        }
    }

    /// <summary>
    /// The element a path reaches from <paramref name="root"/>; null when it is undefined. A property step applies to an object
    /// only, an index step to an array only.
    /// </summary>
    private static JsonElement? Walk(JsonElement root, IReadOnlyList<CosmosPathStep> steps)
    {
        var current = root;
        foreach (var step in steps)
        {
            switch (step)
            {
                case PropertyStep p when current.ValueKind == JsonValueKind.Object:
                    // With duplicate keys the last one is used here; duplicate keys are outside the supported subset.
                    JsonElement found = default;
                    var has = false;
                    foreach (var property in current.EnumerateObject())
                        if (property.NameEquals(p.Name))
                        {
                            found = property.Value;
                            has = true;
                        }
                    if (!has) return null;
                    current = found;
                    break;
                case IndexStep i when current.ValueKind == JsonValueKind.Array:
                    if (i.Index >= current.GetArrayLength()) return null;
                    current = current[i.Index];
                    break;
                default:
                    return null;
            }
        }
        return current;
    }

    /// <summary>Writes <paramref name="name"/> and the value; a value that has no JSON form (NaN, infinity, an unlisted type) is undefined: omitted.</summary>
    private static void WriteValue(Utf8JsonWriter writer, string name, object value)
    {
        switch (value)
        {
            case string v: writer.WriteString(name, v); break;
            case bool v: writer.WriteBoolean(name, v); break;
            case int v: writer.WriteNumber(name, v); break;
            case long v: writer.WriteNumber(name, v); break;
            case decimal v: writer.WriteNumber(name, v); break;
            case double v when double.IsFinite(v): writer.WriteNumber(name, v); break;
            case float v when float.IsFinite(v): writer.WriteNumber(name, v); break;
            case DateTime v:
                writer.WriteString(name, (v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v).ToString(DateFormat, CultureInfo.InvariantCulture));
                break;
            case Guid v: writer.WriteString(name, v.ToString("D")); break;
            case EntityReference v: writer.WriteString(name, v.Id.ToString("D")); break;
            case OptionSetValue v: writer.WriteNumber(name, v.Value); break;
            case Money v: writer.WriteNumber(name, v.Value); break;
            case OptionSetValueCollection v:
                writer.WriteStartArray(name);
                foreach (var option in v) writer.WriteNumberValue(option.Value);
                writer.WriteEndArray();
                break;
        }
    }

    // ---- request parameters -----------------------------------------------------------------------------------------------

    private static string StringParameter(OrganizationRequest request, string name)
    {
        if (!request.Parameters.TryGetValue(name, out var value) || value is null) throw Invalid($"{name} is required.");
        if (value is not string text) throw Invalid($"{name} must be a string.");
        if (string.IsNullOrWhiteSpace(text)) throw Invalid($"{name} is required.");
        return text;
    }

    private static string? OptionalString(OrganizationRequest request, string name)
    {
        if (!request.Parameters.TryGetValue(name, out var value) || value is null) return null;
        return value as string ?? throw Invalid($"{name} must be a string.");
    }

    private static int? PageSizeParameter(OrganizationRequest request)
    {
        if (!request.Parameters.TryGetValue("PageSize", out var value) || value is null) return null;
        long size = value switch
        {
            int i => i,
            long l => l,
            _ => throw Invalid("PageSize must be an integer."),
        };
        if (size is < 1 or > SqlQueryCompiler.MaxPageSize) throw Invalid($"PageSize must be between 1 and {SqlQueryCompiler.MaxPageSize}.");
        return (int)size;
    }

    /// <summary>The <c>QueryParameters</c> with every key starting with <c>@</c> (ruling J6) and a value of an accepted type.</summary>
    private static Dictionary<string, object?> Parameters(OrganizationRequest request)
    {
        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!request.Parameters.TryGetValue("QueryParameters", out var raw) || raw is null) return normalized;
        if (raw is not ParameterCollection collection) throw Invalid("QueryParameters must be a ParameterCollection.");
        foreach (var (key, value) in collection)
        {
            if (value is not (string or int or long or double or decimal or bool))
                throw Invalid($"the parameter '{key}' has an unsupported type '{value?.GetType().FullName ?? "null"}'.");
            var name = key.StartsWith('@') ? key : "@" + key;
            if (!normalized.TryAdd(name, value)) throw Invalid($"the parameter '{name}' is given twice.");
        }
        return normalized;
    }
}
