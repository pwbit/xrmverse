using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions.Sdk;
using Xrmd.Cli.Http;
using Xrmd.Metadata;

namespace Xrmd.Cli.Commands;

/// <summary>
/// What <c>data import/export</c> and <c>dev seed</c> need to know about one table, read from the Web API metadata
/// endpoints as the signed-in user: entity set, primary id, the typed columns, and the navigation property of each lookup
/// (<c>ReferencingEntityNavigationPropertyName</c> of its relationship, never guessed from the column name).
/// </summary>
internal sealed class DataTable
{
    private readonly Dictionary<string, string> _navigation;
    private readonly Dictionary<(string Column, string Target), string> _targetNavigation;

    private DataTable(string name, string entitySet, string primaryId, string? primaryName, IReadOnlyList<AttributeMetadata> columns,
        Dictionary<string, string> navigation, Dictionary<(string, string), string> targetNavigation, IReadOnlyDictionary<string, string> unsupported,
        string? partyNavigation)
    {
        PartyNavigation = partyNavigation;
        _targetNavigation = targetNavigation;
        Unsupported = unsupported;
        Name = name;
        EntitySet = entitySet;
        PrimaryId = primaryId;
        PrimaryName = primaryName;
        Columns = columns;
        _navigation = navigation;
    }

    /// <summary>
    /// Writable-looking columns whose type xrmverse cannot read as typed metadata, or whose values are not plain data (image,
    /// file, ...), by name → server AttributeType. They are never exported, imported or seeded; commands name them rather
    /// than dropping them silently.
    /// </summary>
    public IReadOnlyDictionary<string, string> Unsupported { get; }

    /// <summary>The navigation property of the party rows of an activity (<c>{activity}_activity_parties</c>); null for another table.</summary>
    public string? PartyNavigation { get; }

    /// <summary>
    /// The line that names a skipped column (<see cref="Unsupported"/>) for <paramref name="command"/> (e.g. "data export"); for a
    /// party list it says that its parties (the recipients, attendees, ...) are left out, and where they live.
    /// </summary>
    public string SkipMessage(string column, string type, string command, string participle) =>
        type == nameof(AttributeTypeCode.PartyList)
            ? $"Skipped column '{column}' (type PartyList): its parties (recipients, attendees, ...) are rows of " +
              $"'{PartyNavigation ?? ActivitySchema.Party}' and are {participle} by {command}."
            : $"Skipped column '{column}' (type {type}): {command} does not support it.";

    public string Name { get; }
    public string EntitySet { get; }
    public string PrimaryId { get; }
    public string? PrimaryName { get; }

    /// <summary>The columns the server describes, in logical-name order; columns xrmverse cannot read as typed metadata are left out.</summary>
    public IReadOnlyList<AttributeMetadata> Columns { get; }

    public AttributeMetadata? Find(string logicalName) => Columns.FirstOrDefault(c => c.LogicalName == logicalName);

    /// <summary>
    /// A column that data can be written to on create: not stored by another column (money base, formatted names) and not
    /// read-only for create. The primary id counts: a row can be created with its id.
    /// </summary>
    public bool IsWritable(AttributeMetadata column) =>
        column.LogicalName == PrimaryId || (column.AttributeOf is null && column.IsValidForCreate != false && IsDataType(column));

    /// <summary>
    /// A column type whose values are plain data (the types of <see cref="TypeMapping.ToSql"/>). A party list (plan 2.11, A8) is
    /// not: it is not a property of the Web API entity type (its rows are the <c>{activity}_activity_parties</c> navigation), so
    /// it is named as unsupported rather than selected or bound.
    /// </summary>
    public static bool IsDataType(AttributeMetadata column) =>
        !ActivitySchema.IsPartyList(column) && column is StringAttributeMetadata or MemoAttributeMetadata or IntegerAttributeMetadata or BigIntAttributeMetadata
            or DecimalAttributeMetadata or DoubleAttributeMetadata or MoneyAttributeMetadata or BooleanAttributeMetadata
            or DateTimeAttributeMetadata or PicklistAttributeMetadata or MultiSelectPicklistAttributeMetadata
            or StateAttributeMetadata or StatusAttributeMetadata or LookupAttributeMetadata or UniqueIdentifierAttributeMetadata;

    /// <summary>The navigation property that <c>@odata.bind</c> uses for the lookup <paramref name="column"/>.</summary>
    /// <exception cref="InvalidOperationException">A Customer column, which has one navigation property per target: use
    /// <see cref="Navigation(LookupAttributeMetadata, string)"/>.</exception>
    public string Navigation(LookupAttributeMetadata column) =>
        column.AttributeType == AttributeTypeCode.Customer
            ? throw new InvalidOperationException(
                $"The Customer column '{column.LogicalName}' has one navigation property per target table: name the target.")
        : _navigation.TryGetValue(column.LogicalName, out var name) ? name
        // No relationship row: the default of Dataverse and of the server (SchemaName of a custom lookup, else the logical name).
        : column.IsCustomAttribute == true && column.SchemaName is { Length: > 0 } schema ? schema : column.LogicalName;

    /// <summary>
    /// The navigation property that <c>@odata.bind</c> uses for the lookup <paramref name="column"/> and a record of
    /// <paramref name="target"/>: a Customer column has one per target (<c>&lt;column&gt;_account</c>, <c>&lt;column&gt;_contact</c>), and so
    /// does any other lookup with a relationship per target (an activity's <c>regardingobjectid</c>:
    /// <c>regardingobjectid_account_email</c>, …); a lookup without one, the one of <see cref="Navigation(LookupAttributeMetadata)"/>.
    /// </summary>
    public string Navigation(LookupAttributeMetadata column, string target) =>
        column.AttributeType == AttributeTypeCode.Customer
            ? _targetNavigation.TryGetValue((column.LogicalName, target), out var name) ? name : CustomerColumns.DefaultNavigation(column.LogicalName, target)
            : _targetNavigation.TryGetValue((column.LogicalName, target), out var perTarget) ? perTarget
            : Navigation(column);

    public static async Task<DataTable> LoadAsync(XrmdClient client, string table, CancellationToken ct)
    {
        var path = $"EntityDefinitions(LogicalName={UserRoleLookup.Literal(table)})";
        var entity = await client.GetJsonAsync($"{path}?$select=LogicalName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute", ct);
        var set = UserRoleLookup.Str(entity, "EntitySetName")
                  // Decided here from the metadata, not refused by the server: a usage problem (the table cannot be used with this command).
                  ?? throw new CliException(ExitCodes.Usage, $"The table '{table}' has no entity set: it cannot hold data.");
        var primaryId = UserRoleLookup.Str(entity, "PrimaryIdAttribute")
                        ?? throw new CliException(ExitCodes.Unexpected, $"The server did not report the primary id column of '{table}'.");

        var columns = new List<AttributeMetadata>();
        var unsupported = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in await client.GetAllPagesAsync($"{path}/Attributes", ct))
        {
            var typed = Parse(raw);
            if (typed is not null) columns.Add(typed);
            // Settable on create but not plain data (unreadable, or e.g. an image or file column): named, never dropped silently.
            if ((typed is null || !IsDataType(typed)) && UserRoleLookup.Str(raw, "LogicalName") is { } unusable && SettableOnCreate(raw))
                unsupported[unusable] = UserRoleLookup.Str(raw, "AttributeType") ?? "unknown";
        }
        columns.Sort((a, b) => string.CompareOrdinal(a.LogicalName, b.LogicalName));

        var navigation = new Dictionary<string, string>(StringComparer.Ordinal);
        var targetNavigation = new Dictionary<(string, string), string>();
        string? partyNavigation = null;
        foreach (var r in await client.GetAllPagesAsync("RelationshipDefinitions", ct))
        {
            if (UserRoleLookup.Str(r, "ReferencingEntity") == ActivitySchema.Party && UserRoleLookup.Str(r, "ReferencingAttribute") == ActivitySchema.ActivityId
                && UserRoleLookup.Str(r, "ReferencedEntity") == table)
                partyNavigation = UserRoleLookup.Str(r, "ReferencedEntityNavigationPropertyName");
            if (UserRoleLookup.Str(r, "ReferencingEntity") == table && UserRoleLookup.Str(r, "ReferencingAttribute") is { } column
                && UserRoleLookup.Str(r, "ReferencingEntityNavigationPropertyName") is { Length: > 0 } nav)
            {
                navigation.TryAdd(column, nav);
                if (UserRoleLookup.Str(r, "ReferencedEntity") is { } referenced) targetNavigation.TryAdd((column, referenced), nav);
            }
        }

        return new DataTable(table, set, primaryId, UserRoleLookup.Str(entity, "PrimaryNameAttribute"), columns, navigation, targetNavigation, unsupported, partyNavigation);
    }

    /// <summary>True unless the raw attribute is stored by another one (AttributeOf) or read-only on create.</summary>
    private static bool SettableOnCreate(JsonElement raw) =>
        !(raw.TryGetProperty("AttributeOf", out var of) && of.ValueKind == JsonValueKind.String)
        && !(raw.TryGetProperty("IsValidForCreate", out var create) && create.ValueKind == JsonValueKind.False);

    /// <summary>The entity set of <paramref name="table"/> (exit 4 when there is no such table).</summary>
    public static async Task<string> EntitySetAsync(XrmdClient client, string table, CancellationToken ct)
    {
        var entity = await client.GetJsonAsync($"EntityDefinitions(LogicalName={UserRoleLookup.Literal(table)})?$select=EntitySetName", ct);
        return UserRoleLookup.Str(entity, "EntitySetName")
               ?? throw new CliException(ExitCodes.Usage, $"The table '{table}' has no entity set: it cannot be queried.");
    }

    /// <summary>Typed metadata from a Web API attribute (the deserializer needs <c>@odata.type</c> first and without the leading '#'); null for a type it cannot read.</summary>
    private static AttributeMetadata? Parse(JsonElement attribute)
    {
        try
        {
            var node = JsonNode.Parse(attribute.GetRawText())!.AsObject();
            var typed = new JsonObject { ["@odata.type"] = node["@odata.type"]!.GetValue<string>().TrimStart('#') };
            foreach (var (key, value) in node.ToList())
            {
                if (key == "@odata.type") continue;
                node.Remove(key);
                typed[key] = value;
            }
            return MetadataSerializer.DeserializeAttribute(typed.ToJsonString());
        }
        // A missing Microsoft SDK is not "an unreadable column": it reaches CliApp, which prints the missing-SDK message (exit 1).
        catch (Exception ex) when (SdkAssemblyResolver.MissingAssembly(ex) is null)
        {
            return null;
        }
    }
}
