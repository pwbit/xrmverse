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
    /// lists an item. Each item is reported once, however often the phase runs.
    /// </summary>
    public async Task ReportUnsupportedAsync(CancellationToken ct)
    {
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
                await context.Ledger.IssueAsync(new MigrationIssue(Phase, MigrationIssue.Warning, "unsupported", dot > 0 ? name[..dot] : name, null,
                    dot > 0 ? name[(dot + 1)..] : null, $"The {kind} {name} is not imported: {(string?)item["reason"]}."), ct);
            }
            if (strict && names.Count > 0)
                throw new MigrationPhaseException($"The package has items xrmverse cannot import (option strict): {string.Join(", ", names)}.");
        }
    }

    /// <summary>Applies <c>schema/delta.json</c> (optional), after the solutions.</summary>
    public async Task ApplyAsync(CancellationToken ct)
    {
        var package = context.Package;
        if (!package.Manifest.Entries.Any(e => e.Path == DeltaPath) || await ReadAsync(package, DeltaPath, ct) is not JsonObject delta) return;

        var columns = Items(delta, "columns");
        foreach (var item in Items(delta, "tables"))
        {
            var table = (string)item["table"]!;
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
            var table = (string)item["table"]!;
            var column = (string)item["column"]!;
            var type = (string?)item["type"];
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
                await ApplyAsync("column", $"{table}.{column}", table, column, () => new CreateOneToManyRequest
                {
                    Lookup = (LookupAttributeMetadata)Attribute(item),
                    OneToManyRelationship = (OneToManyRelationshipMetadata)JsonSerializer.Deserialize<RelationshipMetadataBase>(
                        Clean(relationship, "OneToManyRelationshipMetadata")!, MetadataSerializer.Options)!,
                }, ct);
            }
            else await ApplyAsync("column", $"{table}.{column}", table, column, () => new CreateAttributeRequest { EntityName = table, Attribute = Attribute(item) }, ct);
        }

        foreach (var item in Items(delta, "relationships"))
        {
            var name = (string)item["schemaName"]!;
            await ApplyAsync("relationship", name, (string?)item["metadata"]?["Entity1LogicalName"], null, () =>
            {
                var relationship = (ManyToManyRelationshipMetadata)JsonSerializer.Deserialize<RelationshipMetadataBase>(
                    Clean(item["metadata"], "ManyToManyRelationshipMetadata")!, MetadataSerializer.Options)!;
                return new CreateManyToManyRequest { ManyToManyRelationship = relationship, IntersectEntitySchemaName = relationship.IntersectEntityName ?? name };
            }, ct);
        }

        foreach (var item in Items(delta, "optionValues"))
        {
            var table = (string?)item["table"];
            var column = (string?)item["column"];
            var set = (string?)item["optionSet"];
            var value = (int)item["value"]!;
            var option = item["metadata"] as JsonObject;
            await ApplyAsync("option", $"{set ?? $"{table}.{column}"}:{value}", table, column, () =>
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
                    request.EntityLogicalName = table;
                    request.AttributeLogicalName = column;
                }
                return request;
            }, ct);
        }

        // xrmverse has no InsertStatusValue: a status value the delta needs is reported, as the unsupported items are.
        foreach (var item in Items(delta, "statusValues"))
        {
            var table = (string?)item["table"];
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

    private static AttributeMetadata Attribute(JsonObject item)
    {
        var type = (string?)item["type"];
        return JsonSerializer.Deserialize<AttributeMetadata>(Clean(item["metadata"], type is not null ? AttributeTypes.GetValueOrDefault(type) : null)!,
            MetadataSerializer.Options)!;
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
