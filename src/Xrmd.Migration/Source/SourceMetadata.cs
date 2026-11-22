using System.Text.Json;

namespace Xrmd.Migration.Source;

/// <param name="TableType"><c>Standard</c>, <c>Activity</c>, <c>Elastic</c> or <c>Virtual</c>.</param>
public sealed record SourceTable(string LogicalName, string EntitySetName, string PrimaryKey, bool IsCustom, bool IsActivity, bool IsIntersect,
    IReadOnlyList<SourceColumn> Columns, string TableType = "Standard", bool IsManaged = false, int? ObjectTypeCode = null);

/// <param name="DateBehavior">For DateTime columns: <c>UserLocal</c>, <c>DateOnly</c> or <c>TimeZoneIndependent</c>.</param>
/// <param name="Targets">For lookups: the tables they can point to.</param>
/// <param name="SourceType">0 simple, 1 calculated, 2 rollup, 3 formula, 4 prompt.</param>
public sealed record SourceColumn(string LogicalName, string AttributeType, string? DateBehavior, IReadOnlyList<string> Targets, bool IsCustom, bool ValidForRead,
    int SourceType = 0, bool IsManaged = false);

public sealed record SourceRelationship(string SchemaName, string IntersectEntity, string Entity1Attribute, string Entity2Attribute, string Entity1, string Entity2,
    bool IsCustom = false, bool IsManaged = false);

/// <summary>
/// A value of a choice or status column. <see cref="OptionSet"/> is the global option set name, or null for a local one.
/// <see cref="State"/> is set for status values. <see cref="Metadata"/> is the option as the Web API returned it (labels included).
/// </summary>
public sealed record SourceOption(string Table, string Column, string? OptionSet, int Value, int? State, JsonElement Metadata);

/// <summary>Reads the tables, columns and N:N relationships of the source environment from the metadata endpoints.</summary>
public static class SourceMetadata
{
    private const string Select =
        "EntityDefinitions?$select=LogicalName,EntitySetName,PrimaryIdAttribute,IsCustomEntity,IsActivity,IsIntersect,TableType,IsManaged,ObjectTypeCode" +
        "&$expand=Attributes($select=LogicalName,AttributeType,IsCustomAttribute,IsValidForRead,AttributeOf,SourceType,IsManaged)";

    /// <summary>
    /// Every table, with its readable columns (name companions dropped). Date behaviors and lookup targets are read only
    /// for the tables <paramref name="inScope"/> keeps, which saves two requests per excluded table.
    /// </summary>
    public static async Task<IReadOnlyList<SourceTable>> ReadTablesAsync(DataverseClient client, Func<string, bool> inScope, CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync(Select, ct);
        var tables = new List<SourceTable>();
        foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var name = e.GetProperty("LogicalName").GetString()!;
            Dictionary<string, string> behaviors = [];
            Dictionary<string, List<string>> targets = [];
            if (inScope(name))
            {
                behaviors = await TypedAsync(client, name, "DateTimeAttributeMetadata", "LogicalName,DateTimeBehavior",
                    a => a.TryGetProperty("DateTimeBehavior", out var b) && b.ValueKind == JsonValueKind.Object ? b.GetProperty("Value").GetString() : null, ct);
                targets = (await TypedAsync(client, name, "LookupAttributeMetadata", "LogicalName,Targets",
                        a => a.TryGetProperty("Targets", out var t) && t.ValueKind == JsonValueKind.Array ? string.Join(',', t.EnumerateArray().Select(x => x.GetString())) : null, ct))
                    .ToDictionary(p => p.Key, p => p.Value.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());
            }
            var columns = new List<SourceColumn>();
            foreach (var a in e.GetProperty("Attributes").EnumerateArray())
            {
                if (a.TryGetProperty("AttributeOf", out var of) && of.ValueKind == JsonValueKind.String) continue;
                if (!(a.TryGetProperty("IsValidForRead", out var r) && r.ValueKind == JsonValueKind.True)) continue;
                var column = a.GetProperty("LogicalName").GetString()!;
                columns.Add(new SourceColumn(column, a.GetProperty("AttributeType").GetString()!, behaviors.GetValueOrDefault(column),
                    targets.TryGetValue(column, out var t) ? t : [], Bool(a, "IsCustomAttribute"), true,
                    a.TryGetProperty("SourceType", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0, Bool(a, "IsManaged")));
            }
            tables.Add(new SourceTable(name, e.GetProperty("EntitySetName").GetString() ?? "", e.GetProperty("PrimaryIdAttribute").GetString() ?? "",
                Bool(e, "IsCustomEntity"), Bool(e, "IsActivity"), Bool(e, "IsIntersect"), columns,
                e.TryGetProperty("TableType", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString()! : "Standard", Bool(e, "IsManaged"),
                e.TryGetProperty("ObjectTypeCode", out var otc) && otc.ValueKind == JsonValueKind.Number ? otc.GetInt32() : null));
        }
        return tables;
    }

    public static async Task<IReadOnlyList<SourceRelationship>> ReadManyToManyAsync(DataverseClient client, CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync(
            "RelationshipDefinitions/Microsoft.Dynamics.CRM.ManyToManyRelationshipMetadata?$select=SchemaName,IntersectEntityName,Entity1LogicalName,Entity1IntersectAttribute,Entity2LogicalName,Entity2IntersectAttribute,IsCustomRelationship,IsManaged", ct);
        return [.. doc.RootElement.GetProperty("value").EnumerateArray().Select(r => new SourceRelationship(
            r.GetProperty("SchemaName").GetString()!, r.GetProperty("IntersectEntityName").GetString()!,
            r.GetProperty("Entity1IntersectAttribute").GetString()!, r.GetProperty("Entity2IntersectAttribute").GetString()!,
            r.GetProperty("Entity1LogicalName").GetString()!, r.GetProperty("Entity2LogicalName").GetString()!, Bool(r, "IsCustomRelationship"), Bool(r, "IsManaged")))];
    }

    /// <summary>
    /// The values of the choice, multi-select choice and status columns of <paramref name="tables"/> (three requests per table).
    /// </summary>
    public static async Task<IReadOnlyList<SourceOption>> ReadOptionsAsync(DataverseClient client, IEnumerable<SourceTable> tables, CancellationToken ct)
    {
        var options = new List<SourceOption>();
        foreach (var table in tables)
        {
            foreach (var type in new[] { "PicklistAttributeMetadata", "MultiSelectPicklistAttributeMetadata", "StatusAttributeMetadata" })
            {
                using var doc = await client.GetJsonAsync(
                    $"EntityDefinitions(LogicalName='{table.LogicalName}')/Attributes/Microsoft.Dynamics.CRM.{type}?$select=LogicalName" +
                    "&$expand=OptionSet($select=Name,IsGlobal,Options),GlobalOptionSet($select=Name,IsGlobal,Options)", ct);
                foreach (var a in doc.RootElement.GetProperty("value").EnumerateArray())
                {
                    var column = a.GetProperty("LogicalName").GetString()!;
                    var set = a.TryGetProperty("GlobalOptionSet", out var g) && g.ValueKind == JsonValueKind.Object ? g
                        : a.TryGetProperty("OptionSet", out var l) && l.ValueKind == JsonValueKind.Object ? l : default;
                    if (set.ValueKind != JsonValueKind.Object || !set.TryGetProperty("Options", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                    // The trial returned a local option set under GlobalOptionSet too (M6 run): IsGlobal tells them apart, and a
                    // GlobalOptionSet without it is global.
                    var global = Bool(set, "IsGlobal") || (g.ValueKind == JsonValueKind.Object && !g.TryGetProperty("IsGlobal", out _))
                        ? set.GetProperty("Name").GetString() : null;
                    foreach (var o in list.EnumerateArray())
                        options.Add(new SourceOption(table.LogicalName, column, global, o.GetProperty("Value").GetInt32(),
                            o.TryGetProperty("State", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null, o.Clone()));
                }
            }
        }
        return options;
    }

    private static async Task<Dictionary<string, string>> TypedAsync(DataverseClient client, string table, string type, string select,
        Func<JsonElement, string?> value, CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync($"EntityDefinitions(LogicalName='{table}')/Attributes/Microsoft.Dynamics.CRM.{type}?$select={select}", ct);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in doc.RootElement.GetProperty("value").EnumerateArray())
            if (value(a) is { } v) result[a.GetProperty("LogicalName").GetString()!] = v;
        return result;
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
