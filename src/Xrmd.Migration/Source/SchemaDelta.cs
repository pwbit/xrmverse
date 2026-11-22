using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Source;

public sealed record DeltaColumn(string Table, string Column, string AttributeType);

/// <param name="Kind"><c>table</c>, <c>column</c> or <c>relationship</c>.</param>
public sealed record UnsupportedItem(string Kind, string Name, string Reason);

/// <summary>
/// The custom data schema of the source that no exported solution carries, and what xrmverse cannot carry at all.
/// Columns of uncovered tables are listed in <see cref="Columns"/> too. Local option and status values are listed only for
/// columns the delta does not create (a created column brings its own values); global option set values whenever no solution carries them.
/// </summary>
public sealed record SchemaDeltaPlan(IReadOnlyList<string> Tables, IReadOnlyList<DeltaColumn> Columns, IReadOnlyList<string> Relationships,
    IReadOnlyList<SourceOption> OptionValues, IReadOnlyList<SourceOption> StatusValues, IReadOnlyList<UnsupportedItem> Unsupported);

/// <summary>Customizations outside solutions (spec decision 12): data schema only.</summary>
public static class SchemaDelta
{
    public const string DeltaPath = "schema/delta.json";
    public const string UnsupportedPath = "schema/unsupported.json";

    /// <summary>Option values at or above this are custom (publisher prefix × 10 000); lower ones are Microsoft's.</summary>
    internal const int FirstCustomOptionValue = 100_000_000;

    private const int RollupSourceType = 2;

    private const int FormulaSourceType = 3;

    /// <summary>The reason a rollup column no exported solution carries is not migrated.</summary>
    public const string RollupOutsideSolutions = "rollup column outside the exported solutions: import it with its solution";

    /// <summary>The reason a formula column (Power Fx, plan 2.12c) no exported solution carries is not migrated.</summary>
    public const string FormulaOutsideSolutions = "formula column outside the exported solutions: import it with its solution";

    private static readonly Dictionary<int, string> UnsupportedSourceTypes = new()
    {
        [4] = "prompt column: not supported by xrmverse",
    };

    public static SchemaDeltaPlan Compute(IReadOnlyList<SourceTable> tables, IReadOnlyList<SourceRelationship> nn, IReadOnlyList<SourceOption> options,
        IEnumerable<Stream> solutionZips)
    {
        var covered = new SolutionCoverage();
        foreach (var zip in solutionZips) covered.Add(zip);
        return Compute(tables, nn, options, covered);
    }

    public static SchemaDeltaPlan Compute(IReadOnlyList<SourceTable> tables, IReadOnlyList<SourceRelationship> nn, IReadOnlyList<SourceOption> options,
        SolutionCoverage covered)
    {
        var deltaTables = new List<string>();
        var columns = new List<DeltaColumn>();
        var unsupported = new List<UnsupportedItem>();
        // Intersect tables come with their N:N relationship.
        foreach (var table in tables.Where(t => !t.IsIntersect))
        {
            if (table.IsCustom && table.TableType == "Virtual")
            {
                unsupported.Add(new("table", table.LogicalName, "virtual table: its data stays in the external source"));
                continue;
            }
            // Managed components come from a managed solution: an exported one (it carries them) or a Microsoft one (not carried).
            // First-party (msdyn_ …) tables and columns are never rebuilt from metadata.
            if (table.IsCustom && ExclusionCatalog.IsFirstParty(table.LogicalName)) continue;
            var tableCovered = !table.IsCustom || table.IsManaged || covered.Tables.Contains(table.LogicalName);
            if (!tableCovered) deltaTables.Add(table.LogicalName);
            foreach (var column in table.Columns.Where(c => c.IsCustom && !c.IsManaged && !ExclusionCatalog.IsFirstParty(c.LogicalName)))
            {
                var name = $"{table.LogicalName}.{column.LogicalName}";
                var columnCovered = tableCovered && covered.Columns.Contains(name);
                // A rollup column (its definition and jobs, plan 2.12b) or a formula column (its Power Fx file, plan 2.12c) comes with
                // the solution that carries it; outside the exported solutions it cannot be rebuilt as a simple column.
                if (column.SourceType is RollupSourceType or FormulaSourceType)
                {
                    if (!columnCovered)
                        unsupported.Add(new("column", name, column.SourceType == RollupSourceType ? RollupOutsideSolutions : FormulaOutsideSolutions));
                }
                else if (UnsupportedSourceTypes.TryGetValue(column.SourceType, out var reason))
                    unsupported.Add(new("column", name, reason));
                else if (column.AttributeType == "Lookup" && column.Targets.Count > 1 && column.LogicalName != "regardingobjectid")
                    unsupported.Add(new("column", name, "multi-table lookup: only Customer, Owner and regarding lookups can point to several tables"));
                else if (!columnCovered)
                    columns.Add(new DeltaColumn(table.LogicalName, column.LogicalName, column.AttributeType));
            }
        }

        var created = columns.Select(c => $"{c.Table}.{c.Column}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var optionValues = new List<SourceOption>();
        var statusValues = new List<SourceOption>();
        var globalSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in options.Where(o => o.Value >= FirstCustomOptionValue && !IsManaged(o.Metadata)))
        {
            // A global option set is shared: its values are checked whatever column uses it (the importer skips values that exist).
            if (o.OptionSet is null && created.Contains($"{o.Table}.{o.Column}")) continue;
            if (o.State is not null)
            {
                if (!covered.Values.Contains($"{o.Table}.{o.Column}:{o.Value}")) statusValues.Add(o);
            }
            else if (o.OptionSet is { } set)
            {
                if (!covered.Values.Contains($"{set}:{o.Value}") && globalSeen.Add($"{set}:{o.Value}")) optionValues.Add(o);
            }
            else if (!covered.Values.Contains($"{o.Table}.{o.Column}:{o.Value}"))
            {
                optionValues.Add(o);
            }
        }

        var relationships = nn.Where(r => r.IsCustom && !r.IsManaged && !covered.Relationships.Contains(r.SchemaName)).Select(r => r.SchemaName).ToList();
        return new SchemaDeltaPlan(deltaTables, columns, relationships, optionValues, statusValues, unsupported);
    }

    /// <summary>Writes <see cref="DeltaPath"/> (with the source metadata of each item) and <see cref="UnsupportedPath"/>; GET requests only.</summary>
    public static async Task WriteAsync(DataverseClient client, PackageWriter writer, SchemaDeltaPlan plan, CancellationToken ct)
    {
        var tables = new List<object>();
        foreach (var t in plan.Tables)
            tables.Add(new { table = t, metadata = await Json(client, $"EntityDefinitions(LogicalName='{t}')?$select=LogicalName,SchemaName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute,DisplayName,DisplayCollectionName,Description,OwnershipType,IsActivity,TableType,HasNotes,HasActivities,IsValidForQueue,ChangeTrackingEnabled,IsAuditEnabled", ct) });

        var columns = new List<object>();
        foreach (var c in plan.Columns)
        {
            var path = $"EntityDefinitions(LogicalName='{c.Table}')/Attributes(LogicalName='{c.Column}')";
            var metadata = c.AttributeType switch
            {
                "Picklist" => await Json(client, $"{path}/Microsoft.Dynamics.CRM.PicklistAttributeMetadata?$expand=OptionSet,GlobalOptionSet", ct),
                "MultiSelectPicklist" => await Json(client, $"{path}/Microsoft.Dynamics.CRM.MultiSelectPicklistAttributeMetadata?$expand=OptionSet,GlobalOptionSet", ct),
                "Status" => await Json(client, $"{path}/Microsoft.Dynamics.CRM.StatusAttributeMetadata?$expand=OptionSet", ct),
                "State" => await Json(client, $"{path}/Microsoft.Dynamics.CRM.StateAttributeMetadata?$expand=OptionSet", ct),
                "Boolean" => await Json(client, $"{path}/Microsoft.Dynamics.CRM.BooleanAttributeMetadata?$expand=OptionSet", ct),
                _ => await Json(client, path, ct),
            };
            JsonElement? relationship = null;
            if (c.AttributeType is "Lookup" or "Customer")
            {
                var rows = await client.GetAllAsync($"EntityDefinitions(LogicalName='{c.Table}')/ManyToOneRelationships?$filter=ReferencingAttribute eq '{c.Column}'", ct);
                relationship = rows.Count > 0 ? rows[0] : null;
            }
            columns.Add(new { table = c.Table, column = c.Column, type = c.AttributeType, metadata, relationship });
        }

        var relationships = new List<object>();
        foreach (var r in plan.Relationships)
            relationships.Add(new { schemaName = r, metadata = await Json(client, $"RelationshipDefinitions(SchemaName='{r}')", ct) });

        Write(writer, DeltaPath, new
        {
            tables,
            columns,
            relationships,
            optionValues = plan.OptionValues.Select(o => new { table = o.Table, column = o.Column, optionSet = o.OptionSet, value = o.Value, metadata = o.Metadata }),
            statusValues = plan.StatusValues.Select(o => new { table = o.Table, column = o.Column, value = o.Value, state = o.State, metadata = o.Metadata }),
        });
        Write(writer, UnsupportedPath, plan.Unsupported);
    }

    private static bool IsManaged(JsonElement option) =>
        option.ValueKind == JsonValueKind.Object && option.TryGetProperty("IsManaged", out var m) && m.ValueKind == JsonValueKind.True;

    private static async Task<JsonElement> Json(DataverseClient client, string relative, CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync(relative, ct);
        return doc.RootElement.Clone();
    }

    private static void Write(PackageWriter writer, string path, object content)
    {
        using var entry = writer.CreateEntry(path);
        JsonSerializer.Serialize(entry, content, PackageJson.Options);
        entry.Commit();
    }
}

/// <summary>What the exported solutions' <c>customizations.xml</c> files carry (names compared without case).</summary>
public sealed class SolutionCoverage
{
    internal HashSet<string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> Relationships { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>table.column:value</c> for local option sets and status values, <c>optionset:value</c> for global ones.</summary>
    internal HashSet<string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds the <c>customizations.xml</c> of a solution zip (the stream is left open).</summary>
    public void Add(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("customizations.xml");
        if (entry is null) return;
        using var s = entry.Open();
        var root = XDocument.Load(s).Root!;
        foreach (var e in root.Elements("Entities").Elements("Entity"))
        {
            var table = e.Element("Name")?.Value;
            if (string.IsNullOrEmpty(table)) continue;
            Tables.Add(table);
            foreach (var a in e.Elements("EntityInfo").Elements("entity").Elements("attributes").Elements("attribute"))
            {
                var column = a.Element("LogicalName")?.Value ?? a.Attribute("PhysicalName")?.Value;
                if (string.IsNullOrEmpty(column)) continue;
                Columns.Add($"{table}.{column}");
                var set = a.Element("optionset");
                if (set is null) continue;
                foreach (var v in set.Elements("options").Elements("option").Concat(set.Elements("statuses").Elements("status")))
                    if (v.Attribute("value")?.Value is { } value) Values.Add($"{table}.{column}:{value}");
            }
        }
        foreach (var set in root.Elements("optionsets").Elements("optionset"))
            foreach (var v in set.Elements("options").Elements("option"))
                if (set.Attribute("Name")?.Value is { } name && v.Attribute("value")?.Value is { } value) Values.Add($"{name}:{value}");
        foreach (var r in root.Elements("EntityRelationships").Elements("EntityRelationship"))
            if (r.Attribute("Name")?.Value is { } name) Relationships.Add(name);
    }
}
