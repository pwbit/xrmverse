using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Packaging;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// The custom data schema of the source that no solution carries (<c>schema/delta.json</c>, spec decision 12; plan M4b task 2),
/// applied after the solutions, item by item through the core's metadata messages, each in a transaction of its own: tables, columns
/// (a lookup with its relationship), N:N relationships, then option values. An item that already exists is skipped (info
/// <c>schema-exists</c>); one that fails is a warning <c>schema-item-failed</c> with its error code, and fails the phase with the
/// option <c>strict</c>. Each item is marked in the ledger (<c>schema/delta/&lt;kind&gt;/&lt;name&gt;</c>) once handled. The
/// metadata is the Web API JSON of the source: its annotations are dropped and the <c>@odata.type</c> the serializer needs is set
/// from the item when a cast GET left it out.
/// <para>
/// With maps (migration table mapping spec §8.3, R7): the items of a source table mapped to another table are applied to the target
/// table. Its custom columns are created there when absent (one that exists is kept: the map check has refused one of another type),
/// unless the map lists them (they are written to their listed column) or the entry says <c>customColumns: drop</c> (warning
/// <c>column-dropped</c> per column); a lookup or N:N to a mapped table points to the target table (R5); an N:N the map names is the
/// target's own and is not created; the table itself is never created, and its local option and status values are left to the value
/// and state translation (R3, R4).
/// </para>
/// </summary>
public sealed class SchemaDeltaApplier(MigrationPhaseContext context, IServiceProvider services, bool strict)
{
    public const string DeltaPath = "schema/delta.json", UnsupportedPath = "schema/unsupported.json";

    private const int Phase = SchemaPhase.PhaseOrder;

    /// <summary>The Web API properties the source's server owns: never sent back to a create.</summary>
    private static readonly HashSet<string> ServerOwned = new(StringComparer.Ordinal)
    {
        "MetadataId", "IsManaged", "HasChanged", "ColumnNumber", "CreatedOn", "ModifiedOn", "IntroducedVersion", "IsCustomAttribute",
        "IsCustomEntity", "IsCustomRelationship", "ObjectTypeCode", "AttributeOf", "LinkedAttributeId", "InheritsFrom",
    };

    private static readonly Dictionary<string, string> AttributeTypes = new(StringComparer.Ordinal)
    {
        ["String"] = "StringAttributeMetadata", ["Memo"] = "MemoAttributeMetadata", ["Integer"] = "IntegerAttributeMetadata",
        ["BigInt"] = "BigIntAttributeMetadata", ["Decimal"] = "DecimalAttributeMetadata", ["Double"] = "DoubleAttributeMetadata",
        ["Money"] = "MoneyAttributeMetadata", ["Boolean"] = "BooleanAttributeMetadata", ["DateTime"] = "DateTimeAttributeMetadata",
        ["Picklist"] = "PicklistAttributeMetadata", ["MultiSelectPicklist"] = "MultiSelectPicklistAttributeMetadata",
        ["Lookup"] = "LookupAttributeMetadata", ["Customer"] = "LookupAttributeMetadata", ["Status"] = "StatusAttributeMetadata",
        ["State"] = "StateAttributeMetadata", ["Uniqueidentifier"] = "UniqueIdentifierAttributeMetadata",
    };

    private readonly MetadataOperations _metadata = services.GetRequiredService<MetadataOperations>();

    /// <summary>
    /// Reports <c>schema/unsupported.json</c> (optional) and, with the option <c>strict</c>, fails before anything is imported when it
    /// lists an item. Each item is reported once, however often the phase runs. An item of a mapped table is reported under its
    /// target table (<paramref name="maps"/>).
    /// </summary>
    public async Task ReportUnsupportedAsync(MapSet maps, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var package = context.Package;
        if (package.Manifest.Entries.Any(e => e.Path == UnsupportedPath) && await ReadAsync(package, UnsupportedPath, ct) is JsonArray unsupported)
        {
            var names = new List<string>();
            foreach (var item in unsupported.OfType<JsonObject>())
            {
                var kind = (string?)item["kind"] ?? "item";
                var name = (string?)item["name"] ?? "";
                var dot = name.IndexOf('.');
                names.Add(name);
                var table = dot > 0 ? name[..dot] : name;
                await context.Ledger.IssueAsync(new MigrationIssue(Phase, MigrationIssue.Warning, "unsupported", maps.TargetOf(table) ?? table, null,
                    dot > 0 ? name[(dot + 1)..] : null, $"The {kind} {name} is not imported: {(string?)item["reason"]}."), ct);
            }
            if (strict && names.Count > 0)
                throw new MigrationPhaseException($"The package has items xrmverse cannot import (option strict): {string.Join(", ", names)}.");
        }
    }

    /// <summary>
    /// The tables <see cref="ApplyAsync"/> would create from <c>schema/delta.json</c> (optional): those of its <c>tables</c> that no
    /// entry of <paramref name="maps"/> sends to another table. For the dry run (table mapping spec §8.6), which creates nothing.
    /// </summary>
    public static async Task<IReadOnlyList<string>> DeltaTablesAsync(PackageReader package, MapSet maps, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(maps);
        if (!package.Manifest.Entries.Any(e => e.Path == DeltaPath) || await ReadAsync(package, DeltaPath, ct) is not JsonObject delta) return [];
        return [.. Items(delta, "tables").Select(i => (string?)i["table"]).OfType<string>().Where(t => maps.TargetOf(t) is null)];
    }

    /// <summary>The (source table, column) pairs of the <c>columns</c> of <c>schema/delta.json</c> (optional), for the map check (R9).</summary>
    public static async Task<IReadOnlySet<(string Table, string Column)>> DeltaColumnsAsync(PackageReader package, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.Manifest.Entries.Any(e => e.Path == DeltaPath) || await ReadAsync(package, DeltaPath, ct) is not JsonObject delta)
            return new HashSet<(string, string)>();
        return Items(delta, "columns").Select(i => ((string?)i["table"], (string?)i["column"]))
            .Where(c => c.Item1 is not null && c.Item2 is not null).Select(c => (c.Item1!, c.Item2!)).ToHashSet();
    }

    /// <summary>Applies <c>schema/delta.json</c> (optional), after the solutions, under the target names of <paramref name="maps"/>.</summary>
    public async Task ApplyAsync(MapSet maps, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var package = context.Package;
        if (!package.Manifest.Entries.Any(e => e.Path == DeltaPath) || await ReadAsync(package, DeltaPath, ct) is not JsonObject delta) return;
        string TableOf(string table) => maps.TargetOf(table) ?? table;

        var columns = Items(delta, "columns");
        // The (source table, column) pairs a renamed table carries to its target (R7).
        var carried = new HashSet<(string Table, string Column)>();
        foreach (var item in Items(delta, "tables"))
        {
            var table = (string)item["table"]!;
            // A table mapped to another table lands in that one: it is not created (its columns go to the target below).
            if (maps.TargetOf(table) is not null) continue;
            await ApplyAsync("table", table, table, null, () =>
            {
                var entity = JsonSerializer.Deserialize<EntityMetadata>(Clean(item["metadata"], null)!, MetadataSerializer.Options)!;
                var primaryName = entity.PrimaryNameAttribute ?? "";
                var primary = columns.FirstOrDefault(c => (string?)c["table"] == table && (string?)c["column"] == primaryName) is { } p
                    ? Attribute(p) as StringAttributeMetadata : null;
                primary ??= new StringAttributeMetadata { LogicalName = primaryName, SchemaName = primaryName, MaxLength = 100,
                    RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired) };
                // The table settings of the source (IsValidForQueue too, plan 4.1a) travel on the entity; the store applies their rules.
                return new CreateEntityRequest
                {
                    Entity = entity, PrimaryAttribute = primary, HasActivities = entity.HasActivities ?? false, HasNotes = entity.HasNotes ?? false,
                };
            }, ct);
        }

        foreach (var item in columns)
        {
            var source = (string)item["table"]!;
            var column = (string)item["column"]!;
            var type = (string?)item["type"];
            var table = TableOf(source);
            if (maps.BySource(source) is { IsRenamed: true } entry)
            {
                // A listed column is written to its target column (R3): nothing to create.
                if (entry.Columns.ContainsKey(column)) continue;
                if (!entry.CarryCustomColumns)
                {
                    await DroppedAsync(source, table, column, ct);
                    continue;
                }
                carried.Add((source, column));
            }
            if (type is "Customer")
            {
                await FailedAsync("column", $"{table}.{column}", table, column, "0x80040203",
                    "a Customer column is created with its two relationships: create it in a solution, the delta has only one", ct);
                continue;
            }
            if (type is "Lookup")
            {
                if (item["relationship"] is not JsonObject relationship)
                {
                    await FailedAsync("column", $"{table}.{column}", table, column, "0x80040203", "the lookup has no relationship in the package", ct);
                    continue;
                }
                await ApplyAsync("column", $"{table}.{column}", table, column, () =>
                {
                    var lookup = (LookupAttributeMetadata)Attribute(item, table);
                    var oneToMany = (OneToManyRelationshipMetadata)JsonSerializer.Deserialize<RelationshipMetadataBase>(
                        Clean(relationship, "OneToManyRelationshipMetadata")!, MetadataSerializer.Options)!;
                    // R5: both ends under their target names; a referenced table that is mapped is referenced by its own key.
                    oneToMany.ReferencingEntity = table;
                    if (oneToMany.ReferencedEntity is { } referenced && maps.TargetOf(referenced) is { } target)
                    {
                        oneToMany.ReferencedEntity = target;
                        oneToMany.ReferencedAttribute = services.GetRequiredService<IMetadataStore>().GetTable(target)?.PrimaryIdAttribute
                            ?? throw new InvalidOperationException($"The target table '{target}' of '{referenced}' does not exist.");
                    }
                    if (lookup.Targets is { } targets) lookup.Targets = [.. targets.Select(TableOf)];
                    return new CreateOneToManyRequest { Lookup = lookup, OneToManyRelationship = oneToMany };
                }, ct);
            }
            else await ApplyAsync("column", $"{table}.{column}", table, column, () => new CreateAttributeRequest { EntityName = table, Attribute = Attribute(item, table) }, ct);

            // R7 "with its options": a carried choice the target already had gets the source's options it lacks.
            if (carried.Contains((source, column)) && type is "Picklist" or "MultiSelectPicklist") await AddMissingOptionsAsync(item, table, column, ct);
        }

        foreach (var item in Items(delta, "relationships"))
        {
            var name = (string)item["schemaName"]!;
            // An N:N the map names goes into the target's own relationship (checked by the map check): nothing to create.
            if (maps.RelationshipTargetOf(name) is not null) continue;
            var first = (string?)item["metadata"]?["Entity1LogicalName"];
            await ApplyAsync("relationship", name, first is null ? null : TableOf(first), null, () =>
            {
                var relationship = (ManyToManyRelationshipMetadata)JsonSerializer.Deserialize<RelationshipMetadataBase>(
                    Clean(item["metadata"], "ManyToManyRelationshipMetadata")!, MetadataSerializer.Options)!;
                // R5: an end that is mapped is the target table (the intersect columns are the server's).
                if (relationship.Entity1LogicalName is { } one) relationship.Entity1LogicalName = TableOf(one);
                if (relationship.Entity2LogicalName is { } two) relationship.Entity2LogicalName = TableOf(two);
                return new CreateManyToManyRequest { ManyToManyRelationship = relationship, IntersectEntitySchemaName = relationship.IntersectEntityName ?? name };
            }, ct);
        }

        foreach (var item in Items(delta, "optionValues"))
        {
            var table = (string?)item["table"];
            var column = (string?)item["column"];
            var set = (string?)item["optionSet"];
            var value = (int)item["value"]!;
            // A local value of a mapped table's column that is not carried is left to the value translation (R3): the column is not
            // on the target. A carried column's value goes to the target's column; a global option set's value is the set's.
            var isCarried = table is not null && column is not null && carried.Contains((table, column));
            if (table is not null && maps.TargetOf(table) is not null && !isCarried
                && (set is null || services.GetRequiredService<IMetadataStore>().GetOptionSet(set) is null)) continue;
            var option = item["metadata"] as JsonObject;
            await ApplyAsync("option", $"{set ?? $"{table}.{column}"}:{value}", table is null ? null : TableOf(table), column, () =>
            {
                var request = new InsertOptionValueRequest
                {
                    Value = value,
                    Label = option?["Label"] is JsonObject label ? JsonSerializer.Deserialize<Label>(label.ToJsonString(), MetadataSerializer.Options)! : new Label(value.ToString(), 1033),
                };
                // The Web API can name a local option set too (M6 run): only a global set the organization has is one.
                if (set is not null && (table is null || column is null || services.GetRequiredService<IMetadataStore>().GetOptionSet(set) is not null))
                    request.OptionSetName = set;
                else
                {
                    request.EntityLogicalName = table is null ? null : TableOf(table);
                    request.AttributeLogicalName = column;
                }
                return request;
            }, ct);
        }

        // xrmverse has no InsertStatusValue: a status value the delta needs is reported, as the unsupported items are.
        foreach (var item in Items(delta, "statusValues"))
        {
            var table = (string?)item["table"];
            // The status of a mapped table goes through the state translation (R4).
            if (table is not null && maps.TargetOf(table) is not null) continue;
            var column = (string?)item["column"];
            var name = $"{table}.{column}:{(int?)item["value"]}";
            await context.Ledger.IssueAsync(new MigrationIssue(Phase, MigrationIssue.Warning, "unsupported", table, null, column,
                $"The status value {name} (state {(int?)item["state"]}) is not imported: xrmverse cannot add a status value outside a solution."), ct);
            if (strict) throw new MigrationPhaseException($"The status value {name} cannot be imported (option strict).");
        }
    }

    /// <summary>Builds the item's request with <paramref name="build"/> and runs it; a request that cannot be built fails the item, not the run.</summary>
    private async Task ApplyAsync(string kind, string name, string? table, string? column, Func<OrganizationRequest> build, CancellationToken ct)
    {
        var key = $"schema/delta/{kind}/{name}";
        if (await context.Store.IsChunkDoneAsync(context.RunId, key, ct)) return;
        try
        {
            var request = build();
            // Checked first: the create's own checks (a custom name's publisher prefix) would fail an item that exists.
            if (Exists(request)) throw new XrmdFaultException(ErrorCodes.DuplicateRecord, "exists", 412);
            await _metadata.ExecuteAsync(request, context.Caller, null, ct);
        }
        catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.DuplicateRecord)
        {
            await context.Ledger.IssueAsync(new MigrationIssue(Phase, MigrationIssue.Info, "schema-exists", table, null, column,
                $"The {kind} {name} already exists in the organization: it is kept as it is."), ct);
        }
        catch (Exception ex) when (ex is XrmdFaultException or ArgumentException or InvalidOperationException or NotSupportedException or JsonException
            or InvalidCastException or FormatException or NullReferenceException)
        {
            await FailedAsync(kind, name, table, column, ex is XrmdFaultException f ? $"0x{f.ErrorCode:x8}" : "0x80040203", ex.Message, ct);
            return;
        }
        await MarkAsync(key, ct);
    }

    /// <summary>Whether the published metadata already has what <paramref name="request"/> creates.</summary>
    private bool Exists(OrganizationRequest request)
    {
        var store = services.GetRequiredService<IMetadataStore>();
        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        switch (request)
        {
            case CreateEntityRequest r:
                return store.GetEntity(r.Entity.LogicalName ?? r.Entity.SchemaName?.ToLowerInvariant() ?? "") is not null;
            case CreateAttributeRequest r:
                return store.GetEntity(r.EntityName)?.Attributes?.Any(a => Same(a.LogicalName, r.Attribute.LogicalName ?? r.Attribute.SchemaName)) == true;
            case CreateOneToManyRequest r:
                return store.GetRelationship(r.OneToManyRelationship.SchemaName) is not null
                    || store.GetEntity(r.OneToManyRelationship.ReferencingEntity)?.Attributes?.Any(a => Same(a.LogicalName, r.Lookup.LogicalName ?? r.Lookup.SchemaName)) == true;
            case CreateManyToManyRequest r:
                return store.GetRelationship(r.ManyToManyRelationship.SchemaName) is not null;
            case InsertOptionValueRequest r when r.OptionSetName is null:
                return store.GetEntity(r.EntityLogicalName)?.Attributes?.FirstOrDefault(a => Same(a.LogicalName, r.AttributeLogicalName)) is EnumAttributeMetadata e
                    && e.OptionSet?.Options.Any(o => o.Value == r.Value) == true;
            case InsertOptionValueRequest r:
                return store.GetOptionSet(r.OptionSetName) is OptionSetMetadata set && set.Options.Any(o => o.Value == r.Value);
            default:
                return false;
        }
    }

    /// <summary>
    /// Adds to <paramref name="table"/>.<paramref name="column"/> each option of the source column (<paramref name="item"/>'s metadata)
    /// it lacks, each one an item of its own. Nothing happens for a column the delta has just created (it has them all).
    /// </summary>
    private async Task AddMissingOptionsAsync(JsonObject item, string table, string column, CancellationToken ct)
    {
        var store = services.GetRequiredService<IMetadataStore>();
        if (store.GetEntity(table)?.Attributes?.FirstOrDefault(a => a.LogicalName == column) is not EnumAttributeMetadata existing) return;
        var have = existing.OptionSet?.Options.Select(o => o.Value).ToHashSet() ?? [];
        var global = existing.OptionSet is { IsGlobal: true, Name: { } name } ? name : null;
        var options = (item["metadata"]?["OptionSet"] as JsonObject ?? item["metadata"]?["GlobalOptionSet"] as JsonObject)?["Options"] as JsonArray;
        foreach (var option in options?.OfType<JsonObject>() ?? [])
        {
            if ((int?)option["Value"] is not int value || have.Contains(value)) continue;
            await ApplyAsync("option", $"{table}.{column}:{value}", table, column, () => new InsertOptionValueRequest
            {
                Value = value,
                Label = option["Label"] is JsonObject label ? JsonSerializer.Deserialize<Label>(Clean(label, null)!, MetadataSerializer.Options)! : new Label(value.ToString(), 1033),
                OptionSetName = global,
                EntityLogicalName = global is null ? table : null,
                AttributeLogicalName = global is null ? column : null,
            }, ct);
        }
    }

    /// <summary>A custom column of a mapped table the map drops (<c>customColumns: drop</c>): reported once, not created.</summary>
    private async Task DroppedAsync(string source, string table, string column, CancellationToken ct)
    {
        var key = $"schema/delta/column/{table}.{column}";
        if (await context.Store.IsChunkDoneAsync(context.RunId, key, ct)) return;
        // Information: the census of phase 1 counts the rows whose value is dropped (warning).
        await context.Ledger.IssueAsync(new MigrationIssue(Phase, MigrationIssue.Info, "column-dropped", table, null, column,
            $"The custom column {source}.{column} is not created on {table} (customColumns: drop)."), ct);
        await MarkAsync(key, ct);
    }

    private async Task FailedAsync(string kind, string name, string? table, string? column, string code, string reason, CancellationToken ct)
    {
        await context.Ledger.IssueAsync(new MigrationIssue(Phase, MigrationIssue.Warning, "schema-item-failed", table, null, column,
            $"The {kind} {name} could not be created ({code}): {reason}"), ct);
        if (strict) throw new MigrationPhaseException($"The {kind} {name} could not be created ({code}, option strict): {reason}");
        await MarkAsync($"schema/delta/{kind}/{name}", ct);
    }

    private async Task MarkAsync(string key, CancellationToken ct)
    {
        await using var conn = await services.GetRequiredService<IDbConnectionFactory>().OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        await context.Store.MarkChunkAsync(context.RunId, Phase, key, 1, 0, new DataSession(conn, tx), ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>The column of <paramref name="item"/>, on <paramref name="table"/> (the target table of a mapped one).</summary>
    private static AttributeMetadata Attribute(JsonObject item, string? table = null)
    {
        var type = (string?)item["type"];
        var json = Clean(item["metadata"], type is not null ? AttributeTypes.GetValueOrDefault(type) : null)!;
        if (table is not null && JsonNode.Parse(json) is JsonObject metadata && metadata.ContainsKey("EntityLogicalName"))
        {
            metadata["EntityLogicalName"] = table;
            json = metadata.ToJsonString();
        }
        return JsonSerializer.Deserialize<AttributeMetadata>(json, MetadataSerializer.Options)!;
    }

    /// <summary>
    /// The metadata JSON without its OData annotations and the source server's own properties (at every depth), with
    /// <c>@odata.type</c> first (as the serializer requires), taken from the JSON or else from <paramref name="defaultType"/>.
    /// </summary>
    internal static string? Clean(JsonNode? metadata, string? defaultType)
    {
        if (metadata is not JsonObject source) return null;
        var type = ((string?)source[MetadataSerializer.ODataTypeProperty])?.TrimStart('#');
        if (type is not null && type.StartsWith(MetadataSerializer.ODataTypePrefix, StringComparison.Ordinal)) type = type[MetadataSerializer.ODataTypePrefix.Length..];
        type ??= defaultType;
        var clean = new JsonObject();
        if (type is not null) clean[MetadataSerializer.ODataTypeProperty] = MetadataSerializer.ODataTypePrefix + type;
        foreach (var (key, value) in source)
            if (!key.Contains("@odata.", StringComparison.Ordinal) && !key.StartsWith('@') && !ServerOwned.Contains(key))
                clean[key] = Strip(value);
        return clean.ToJsonString();
    }

    private static JsonNode? Strip(JsonNode? node) => node switch
    {
        JsonObject o when o[MetadataSerializer.ODataTypeProperty] is not null => JsonNode.Parse(Clean(o, null)!),
        JsonObject o => new JsonObject(o.Where(p => !p.Key.Contains("@odata.", StringComparison.Ordinal) && !ServerOwned.Contains(p.Key))
            .Select(p => KeyValuePair.Create(p.Key, Strip(p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(Strip)]),
        _ => node?.DeepClone(),
    };

    private static List<JsonObject> Items(JsonObject delta, string name) => delta[name] is JsonArray a ? [.. a.OfType<JsonObject>()] : [];

    private static async Task<JsonNode?> ReadAsync(PackageReader package, string path, CancellationToken ct)
    {
        await using var s = package.OpenEntry(path);
        return await JsonNode.ParseAsync(s, cancellationToken: ct);
    }
}

/// <summary>
/// The SDK requests of the archive tables (<see cref="Mapping.ArchiveSchema"/>, GAP-27): their metadata (CreateEntity, CreateAttribute,
/// CreateOneToMany, CreateCustomerRelationships) and their publisher, solution, form and view rows. They live beside the schema delta's
/// metadata requests so that the archive's own code stays free of the Microsoft SDK (S.2); the archive decides what to create, this
/// class only spells it as requests.
/// </summary>
internal static class ArchiveSchemaRequests
{
    /// <summary>The language of the labels the archive writes: the organization's base language (xrmverse labels in 1033).</summary>
    private const int Language = Mapping.MetadataMapTarget.BaseLanguage;

    private const string SolutionName = Mapping.ArchiveSchema.SolutionName;

    /// <summary>The archive table, with its primary name column: <paramref name="primary"/> (a copied text column of the source) when
    /// given, <c>subject</c> for an activity, else a text column <paramref name="primaryName"/> of its own.</summary>
    public static OrganizationRequest CreateEntity(string table, string displayName, SourceCatalogTable source, ArchiveColumn? primary,
        string primaryName, SourceCatalog catalog)
    {
        var activity = source.IsActivity;
        var entity = new EntityMetadata
        {
            LogicalName = table,
            SchemaName = table,
            DisplayName = Label(displayName),
            DisplayCollectionName = Label(displayName),
            OwnershipType = source.Ownership == "organization" ? OwnershipTypes.OrganizationOwned : OwnershipTypes.UserOwned,
        };
        if (activity) entity.IsActivity = true;
        StringAttributeMetadata primaryAttribute;
        if (activity)
        {
            var subject = source.Columns.FirstOrDefault(c => c.LogicalName == "subject");
            primaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "subject", SchemaName = "Subject", MaxLength = Length(subject?.MaxLength, 200, 4000), DisplayName = Label(subject?.DisplayName ?? "subject"),
            };
        }
        else if (primary is { } column) primaryAttribute = (StringAttributeMetadata)Attribute(source, column, catalog);
        else primaryAttribute = new StringAttributeMetadata { LogicalName = primaryName, SchemaName = primaryName, MaxLength = 100, DisplayName = Label("Name") };
        primaryAttribute.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None);
        // Notes and activities of other tables can then regard an archived row (R5 points them here).
        return new CreateEntityRequest
        {
            Entity = entity, PrimaryAttribute = primaryAttribute, HasNotes = true, HasActivities = !activity, SolutionUniqueName = SolutionName,
        };
    }

    /// <summary>The archive column of a copied source column, or the text column of a lookup the archive does not keep.</summary>
    public static OrganizationRequest CreateAttribute(string table, SourceCatalogTable source, ArchiveColumn column, SourceCatalog catalog) =>
        new CreateAttributeRequest { EntityName = table, Attribute = Attribute(source, column, catalog), SolutionUniqueName = SolutionName };

    /// <summary>A lookup the archive keeps: to <paramref name="referenced"/> of each of its source's targets, by that table's key.</summary>
    public static OrganizationRequest Lookup(string table, SourceCatalogTable source, ArchiveColumn column, Func<string, string> referenced,
        Func<string, string> keyOf)
    {
        var c = column.Source;
        var lookup = Named(new LookupAttributeMetadata(), column.Target!, c.DisplayName ?? c.LogicalName);
        OneToManyRelationshipMetadata Relationship(string target, string name) => new()
        {
            SchemaName = name, ReferencedEntity = referenced(target), ReferencedAttribute = keyOf(target), ReferencingEntity = table,
        };
        if (c.AttributeType == "Customer")
            return new CreateCustomerRelationshipsRequest
            {
                Lookup = lookup,
                OneToManyRelationships = [.. ArchiveColumns.CustomerTargets.Select(t => Relationship(t, ArchiveNames.Relationship(source.LogicalName, c.LogicalName + "_" + t)))],
                SolutionUniqueName = SolutionName,
            };
        return new CreateOneToManyRequest
        {
            Lookup = lookup,
            OneToManyRelationship = Relationship(c.Targets[0], ArchiveNames.Relationship(source.LogicalName, c.LogicalName)),
            SolutionUniqueName = SolutionName,
        };
    }

    public static OrganizationRequest CreatePublisher(Guid id, string uniqueName, string friendlyName, string prefix, int optionValuePrefix) =>
        new CreateRequest
        {
            Target = new Entity("publisher", id)
            {
                ["uniquename"] = uniqueName, ["friendlyname"] = friendlyName, ["customizationprefix"] = prefix,
                ["customizationoptionvalueprefix"] = optionValuePrefix,
            },
        };

    public static OrganizationRequest CreateSolution(string uniqueName, string friendlyName, string version, Guid publisherId, string description) =>
        new CreateRequest
        {
            Target = new Entity("solution")
            {
                ["uniquename"] = uniqueName, ["friendlyname"] = friendlyName, ["version"] = version,
                ["publisherid"] = new EntityReference("publisher", publisherId),
                ["description"] = description,
            },
        };

    /// <summary>The default main form (type 2, active) of <paramref name="table"/>, in the archive solution.</summary>
    public static OrganizationRequest CreateMainForm(Guid formId, string name, string table, string formXml) =>
        new CreateRequest
        {
            Target = new Entity("systemform", formId)
            {
                ["name"] = name, ["objecttypecode"] = table, ["type"] = new OptionSetValue(2), ["isdefault"] = true,
                ["formactivationstate"] = new OptionSetValue(1), ["formxml"] = formXml,
            },
            ["SolutionUniqueName"] = SolutionName,
        };

    /// <summary>The default public view (query type 0) of <paramref name="table"/>, in the archive solution.</summary>
    public static OrganizationRequest CreateView(string name, string table, string fetchXml, string layoutXml) =>
        new CreateRequest
        {
            Target = new Entity("savedquery")
            {
                ["name"] = name, ["returnedtypecode"] = table, ["querytype"] = 0, ["isdefault"] = true,
                ["fetchxml"] = fetchXml, ["layoutxml"] = layoutXml,
            },
            ["SolutionUniqueName"] = SolutionName,
        };

    public static OrganizationRequest UpdateFormXml(Guid formId, string formXml) =>
        new UpdateRequest { Target = new Entity("systemform", formId) { ["formxml"] = formXml } };

    private static AttributeMetadata Attribute(SourceCatalogTable source, ArchiveColumn column, SourceCatalog catalog)
    {
        var c = column.Source;
        if (column.Kind == ArchiveColumnKind.LookupAsText)
            return Named(new StringAttributeMetadata { MaxLength = 4000 }, column.TextTarget!, (c.DisplayName ?? c.LogicalName) + " (name)");
        AttributeMetadata attribute = c.AttributeType switch
        {
            "String" => new StringAttributeMetadata { MaxLength = Length(c.MaxLength, 100, 4000), Format = Enum<StringFormat>(c.Format, StringFormats) },
            "Memo" => new MemoAttributeMetadata { MaxLength = Length(c.MaxLength, 2000, 1_048_576) },
            "Integer" => new IntegerAttributeMetadata
            {
                MinValue = Whole(c.MinValue), MaxValue = Whole(c.MaxValue), Format = Enum<IntegerFormat>(c.Format, IntegerFormats),
            },
            "BigInt" => new BigIntAttributeMetadata(),
            "Decimal" => new DecimalAttributeMetadata
            {
                Precision = c.Precision, MinValue = c.MinValue is double min ? (decimal)min : null, MaxValue = c.MaxValue is double max ? (decimal)max : null,
            },
            "Double" => new DoubleAttributeMetadata { Precision = c.Precision, MinValue = c.MinValue, MaxValue = c.MaxValue },
            "Money" => new MoneyAttributeMetadata { Precision = c.Precision, MinValue = c.MinValue, MaxValue = c.MaxValue },
            "Boolean" => new BooleanAttributeMetadata(new BooleanOptionSetMetadata(
                new OptionMetadata(Label(OptionLabel(catalog, source, c, 1) ?? "Yes"), 1), new OptionMetadata(Label(OptionLabel(catalog, source, c, 0) ?? "No"), 0))),
            "DateTime" => new DateTimeAttributeMetadata
            {
                Format = c.Format == "DateOnly" || c.DateBehavior == "DateOnly" ? DateTimeFormat.DateOnly : DateTimeFormat.DateAndTime,
                DateTimeBehavior = c.DateBehavior switch
                {
                    "DateOnly" => DateTimeBehavior.DateOnly,
                    "TimeZoneIndependent" => DateTimeBehavior.TimeZoneIndependent,
                    _ => DateTimeBehavior.UserLocal,
                },
            },
            "Picklist" or "State" or "Status" => new PicklistAttributeMetadata { OptionSet = Options(catalog, source, c) },
            "Virtual" when column.IsMultiSelect => new MultiSelectPicklistAttributeMetadata { OptionSet = Options(catalog, source, c) },
            _ => throw new NotSupportedException($"The column '{source.LogicalName}.{c.LogicalName}' of type {c.AttributeType} has no archive column."),
        };
        return Named(attribute, column.Target!, c.DisplayName ?? c.LogicalName);
    }

    private static T Named<T>(T attribute, string name, string label) where T : AttributeMetadata
    {
        attribute.LogicalName = name;
        attribute.SchemaName = name;
        attribute.DisplayName = Label(label);
        attribute.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None);
        return attribute;
    }

    private static readonly string[] StringFormats = ["Text", "TextArea", "Email", "Url", "Phone", "TickerSymbol"];
    private static readonly string[] IntegerFormats = ["None", "Duration", "TimeZone", "Language", "Locale"];

    /// <summary>A format the archive can take (the source's name of it); null (the default) for another or none.</summary>
    private static T? Enum<T>(string? name, string[] allowed) where T : struct, Enum =>
        name is not null && allowed.Contains(name, StringComparer.OrdinalIgnoreCase) && System.Enum.TryParse<T>(name, true, out var value) ? value : null;

    private static int Length(int? given, int fallback, int max) => Math.Clamp(given is > 0 ? given.Value : fallback, 1, max);

    private static int? Whole(double? value) => value is double v ? (int)Math.Clamp(v, int.MinValue, int.MaxValue) : null;

    private static OptionSetMetadata Options(SourceCatalog catalog, SourceCatalogTable table, SourceCatalogColumn column) =>
        new(new OptionMetadataCollection([.. catalog.Options
            .Where(o => o.Table == table.LogicalName && o.Column == column.LogicalName)
            .DistinctBy(o => o.Value).OrderBy(o => o.Value)
            .Select(o => new OptionMetadata(Label(string.IsNullOrWhiteSpace(o.Label) ? o.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : o.Label!), o.Value))]))
        {
            IsGlobal = false, OptionSetType = OptionSetType.Picklist,
        };

    private static string? OptionLabel(SourceCatalog catalog, SourceCatalogTable table, SourceCatalogColumn column, int value) =>
        catalog.Options.FirstOrDefault(o => o.Table == table.LogicalName && o.Column == column.LogicalName && o.Value == value)?.Label is { Length: > 0 } label
            ? label : null;

    private static Label Label(string text) => new(text, Language);
}
