using System.Globalization;
using System.Text.Json;
using Xrmd.Migration.Records;

namespace Xrmd.Migration.Source;

/// <summary>Turns a Web API row into a package record, typed from the column metadata.</summary>
public static class ValueMapper
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "String", "Memo", "EntityName", "Integer", "BigInt", "Decimal", "Double", "Money", "Boolean", "DateTime",
        "Picklist", "State", "Status", "MultiSelectPicklist", "Uniqueidentifier", "Lookup", "Customer", "Owner", "File", "Image",
    };

    /// <summary>False for column types the package does not carry (party lists, virtual columns, calendar rules, managed properties).</summary>
    public static bool IsSupported(string attributeType) => Supported.Contains(attributeType);

    /// <summary>
    /// Maps <paramref name="row"/>. Every supported column is written; one missing from the row is written as null, because
    /// FetchXML through the Web API leaves out empty columns. <paramref name="files"/> gives the value of a non-empty file or image
    /// column once its content is in the package, or null to leave the column out.
    /// </summary>
    /// <param name="unresolved">Called with a lookup column whose target table the response does not tell; the column is left out.</param>
    /// <param name="invalid">Called with a column whose value has another JSON type than its metadata says (seen on system tables);
    /// the column is left out. Without it, such a value throws an <see cref="InvalidDataException"/> naming the column.</param>
    /// <param name="tableOfCode">The source table of an object type code, or null: a whole number <c>objecttypecode</c> column is
    /// written as the table's logical name, because a custom table's number is the source's own (M6 run).</param>
    /// <param name="lookupNames">For a table an archive may be made from (spec §8.7): each non-empty lookup also writes
    /// <c>&lt;column&gt;name</c>, the referenced row's name as the source formats it, or its id when the source gives none. An archive
    /// keeps a lookup it cannot create as that text.</param>
    public static MigrationRecord Map(SourceTable table, JsonElement row, Func<SourceColumn, Guid, MigrationValue?> files, Action<string>? unresolved = null,
        Action<string>? invalid = null, Func<int, string?>? tableOfCode = null, bool lookupNames = false)
    {
        var id = row.GetProperty(table.PrimaryKey).GetGuid();
        var values = new Dictionary<string, MigrationValue>(StringComparer.Ordinal);
        foreach (var column in table.Columns)
        {
            if (column.LogicalName == table.PrimaryKey || !IsSupported(column.AttributeType)) continue;
            var value = column.AttributeType switch
            {
                "Lookup" or "Customer" or "Owner" => Lookup(table, column, row, unresolved),
                "File" or "Image" => Present(row, column.LogicalName, out _) ? files(column, id) : NullValue.Instance,
                _ => Present(row, column.LogicalName, out var v) ? Checked(table, column, v, invalid) : NullValue.Instance,
            };
            if (value is IntValue { Value: >= int.MinValue and <= int.MaxValue } code && tableOfCode is not null
                && column.LogicalName.EndsWith("objecttypecode", StringComparison.Ordinal) && tableOfCode((int)code.Value) is { } name)
                value = new StringValue(name);
            if (value is not null) values[column.LogicalName] = value;
            if (lookupNames && value is LookupValue lookup && !table.Columns.Any(c => c.LogicalName == column.LogicalName + "name"))
                values[column.LogicalName + "name"] = new StringValue(LookupName(row, column) ?? lookup.Id.ToString("D"));
        }
        return new MigrationRecord(id, values);
    }

    /// <summary>The formatted value of a lookup (the referenced row's primary name); null when the response has none.</summary>
    private static string? LookupName(JsonElement row, SourceColumn column) =>
        row.TryGetProperty($"_{column.LogicalName}_value@OData.Community.Display.V1.FormattedValue", out var name)
        && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text ? text : null;

    private static bool Present(JsonElement row, string name, out JsonElement value) =>
        row.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;

    private static MigrationValue? Lookup(SourceTable table, SourceColumn column, JsonElement row, Action<string>? unresolved)
    {
        var key = $"_{column.LogicalName}_value";
        if (!Present(row, key, out var v)) return NullValue.Instance;
        string target;
        if (row.TryGetProperty(key + "@Microsoft.Dynamics.CRM.lookuplogicalname", out var t) && t.ValueKind == JsonValueKind.String)
            target = t.GetString()!;
        else if (column.Targets.Count == 1)
            target = column.Targets[0];
        else if (table.LogicalName == "principalobjectaccess" && column.LogicalName == "objectid"
            && row.TryGetProperty("objecttypecode", out var type) && type.ValueKind == JsonValueKind.String)
            target = type.GetString()!;
        else if (unresolved is not null)
        {
            unresolved(column.LogicalName);
            return null;
        }
        else
            throw new InvalidDataException($"The target table of {table.LogicalName}.{column.LogicalName} is not in the response.");
        return new LookupValue(target, v.GetGuid());
    }

    private static MigrationValue? Checked(SourceTable table, SourceColumn column, JsonElement v, Action<string>? invalid)
    {
        try
        {
            return Scalar(column, v);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            if (invalid is null)
                throw new InvalidDataException($"The value of {table.LogicalName}.{column.LogicalName} ({column.AttributeType}) is a JSON {v.ValueKind}: {e.Message}", e);
            invalid(column.LogicalName);
            return null;
        }
    }

    private static MigrationValue Scalar(SourceColumn column, JsonElement v) => column.AttributeType switch
    {
        "String" or "Memo" or "EntityName" => new StringValue(v.GetString()!),
        "Integer" or "BigInt" => new IntValue(v.GetInt64()),
        "Decimal" => new DecimalValue(v.GetDecimal()),
        "Double" => new DoubleValue(v.GetDouble()),
        "Money" => new MoneyValue(v.GetDecimal()),
        "Boolean" => new BoolValue(v.GetBoolean()),
        "DateTime" when column.DateBehavior == "DateOnly" =>
            new DateOnlyValue(DateOnly.ParseExact(v.GetString()!.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture)),
        "DateTime" => new DateTimeValue(DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)),
        "Picklist" or "State" or "Status" => new OptionValue(v.GetInt32()),
        "MultiSelectPicklist" => new OptionsValue(v.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList()),
        "Uniqueidentifier" => new GuidValue(v.GetGuid()),
        _ => throw new InvalidOperationException($"Unexpected column type {column.AttributeType}."),
    };
}
