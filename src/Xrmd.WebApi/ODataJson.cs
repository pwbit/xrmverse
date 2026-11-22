using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi;

/// <summary>
/// Converts records between the Dataverse Web API JSON shape (OData minimal metadata) and SDK <see cref="Entity"/> values,
/// typed by the entity's attribute metadata.
/// </summary>
public static class ODataJson
{
    public const string LookupLogicalNameAnnotation = "@Microsoft.Dynamics.CRM.lookuplogicalname";
    public const string AssociatedNavigationPropertyAnnotation = "@Microsoft.Dynamics.CRM.associatednavigationproperty";
    public const string FormattedValueAnnotation = "@OData.Community.Display.V1.FormattedValue";
    private const string BindSuffix = "@odata.bind";
    private const string JsonContentType = "application/json; odata.metadata=minimal";
    private const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>
    /// The record as Web API JSON: <c>@odata.context</c> (<c>{baseUrl}/api/data/v9.2/$metadata#{entityset}[(select)]/$entity</c>),
    /// <c>@odata.etag</c> <c>W/"{versionnumber}"</c> when the version is known (<see cref="Entity.RowVersion"/> or
    /// <c>versionnumber</c>), then one property per attribute: lookups as <c>_{name}_value</c> preceded by their
    /// <c>lookuplogicalname</c> and <c>associatednavigationproperty</c> annotations, Money/Decimal as decimals without
    /// trailing zeros, option sets as integers, multi-select option sets as <c>"1,2"</c>, DateTime as ISO 8601 UTC
    /// (<c>Z</c>, seconds), Guid as string. Null attributes are written as null.
    /// </summary>
    /// <param name="baseUrl">Scheme, host and path base of the server (a trailing slash is ignored).</param>
    /// <param name="select">The <c>$select</c> list, echoed in <c>@odata.context</c> as Dataverse does.</param>
    /// <param name="includeFormattedValues">Writes <c>{property}@OData.Community.Display.V1.FormattedValue</c> before each
    /// property with a formatted value (lookups on <c>_{name}_value</c>), as requested by
    /// <c>Prefer: odata.include-annotations</c>.</param>
    /// <param name="expands">The <c>$expand</c> links by alias (the lookup name) with their target entity: the
    /// <see cref="AliasedValue"/>s keyed <c>{alias}.{attr}</c> become the nested record <c>{alias}</c> (null when the join
    /// found no row, i.e. the target's primary key is absent). Other <see cref="AliasedValue"/>s (FetchXML link columns,
    /// aggregates) are written as is under their key with their plain value (a lookup as its GUID).</param>
    /// <param name="unreadable">Field security (plan 14, ruling F3): the secured columns the caller cannot read, by table
    /// (<see cref="Xrmd.Core.OrganizationService.UnreadableColumnsAsync"/>). A null value of one of them (the organization
    /// service's "secured" null, plain or aliased) is omitted, as Dataverse does; any other null is written as null.</param>
    public static JsonObject ToJson(Entity e, EntityMetadata m, string baseUrl, IReadOnlyList<string>? select = null,
        bool includeFormattedValues = false, IReadOnlyDictionary<string, ODataExpand>? expands = null,
        IReadOnlyList<OneToManyRelationshipMetadata>? relationships = null, IReadOnlyDictionary<string, IReadOnlySet<string>>? unreadable = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(m);
        var selectClause = select is { Count: > 0 } ? $"({string.Join(',', select)})" : "";
        return Write(e, m, $"{baseUrl.TrimEnd('/')}{MetadataController.ServiceRootPath}$metadata#{m.EntitySetName}{selectClause}/$entity",
            includeFormattedValues, expands, relationships, unreadable);
    }

    /// <summary>The record as <see cref="ToJson"/> writes it, without <c>@odata.context</c> (a member of a collection).</summary>
    internal static JsonObject ToRecordJson(Entity e, EntityMetadata m, bool includeFormattedValues,
        IReadOnlyDictionary<string, ODataExpand>? expands = null, IReadOnlyList<OneToManyRelationshipMetadata>? relationships = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? unreadable = null) =>
        Write(e, m, null, includeFormattedValues, expands, relationships, unreadable);

    /// <summary>True for a null value of a secured column the caller cannot read: the property is omitted (ruling F3).</summary>
    private static bool IsSecuredNull(object? value, IReadOnlyDictionary<string, IReadOnlySet<string>>? unreadable, string entity, string attribute) =>
        value is null && unreadable is not null && unreadable.TryGetValue(entity, out var columns) && columns.Contains(attribute);

    private static JsonObject Write(Entity e, EntityMetadata m, string? context, bool includeFormattedValues,
        IReadOnlyDictionary<string, ODataExpand>? expands = null, IReadOnlyList<OneToManyRelationshipMetadata>? relationships = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? unreadable = null)
    {
        var json = new JsonObject();
        if (context is not null) json["@odata.context"] = context;
        var version = e.RowVersion
                      ?? (e.Attributes.TryGetValue(TypeMapping.VersionNumber, out var v) && v is long n ? n.ToString(CultureInfo.InvariantCulture) : null);
        if (version is not null) json["@odata.etag"] = $"W/\"{version}\"";

        var attrs = (m.Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal);
        var expandAliases = expands is null ? null : new HashSet<string>(ODataExpand.Aliases(expands), StringComparer.Ordinal);
        foreach (var (name, value) in e.Attributes)
        {
            if (value is AliasedValue aliased)
            {
                // A hidden column read through all-attributes of the root or a link-entity: never written.
                if (WebApiHiddenAttributes.IsHidden(aliased.EntityLogicalName, aliased.AttributeLogicalName)) continue;
                if (IsSecuredNull(aliased.Value, unreadable, aliased.EntityLogicalName, aliased.AttributeLogicalName)) continue;
                var dot = name.IndexOf('.', StringComparison.Ordinal);
                if (expandAliases is not null && dot > 0 && expandAliases.Contains(name[..dot])) continue; // nested record below
                if (includeFormattedValues && aliased.Value is not null && e.FormattedValues.TryGetValue(name, out var aliasedFormatted))
                    json[name + FormattedValueAnnotation] = aliasedFormatted;
                json[name] = aliased.Value is EntityReference r ? r.Id.ToString() : ToJsonValue(aliased.Value);
                continue;
            }
            if (WebApiHiddenAttributes.IsHidden(m.LogicalName, name)) continue; // not part of the Web API entity type
            if (IsSecuredNull(value, unreadable, m.LogicalName, name)) continue;
            attrs.TryGetValue(name, out var a);
            // A party list (plan 2.11 A8) is not a property: its rows are read with $expand={activity}_activity_parties.
            if (ActivitySchema.IsPartyList(a) || value is EntityCollection) continue;
            var isLookup = a is LookupAttributeMetadata || value is EntityReference;
            var property = isLookup ? ODataEdmModelBuilder.LookupValueProperty(name) : name;
            if (includeFormattedValues && value is not null && e.FormattedValues.TryGetValue(name, out var formatted))
                json[property + FormattedValueAnnotation] = formatted;
            if (isLookup)
            {
                var reference = value as EntityReference;
                if (reference is not null)
                {
                    var navigation = a is LookupAttributeMetadata lookup ? NavigationOf(m, lookup, relationships, reference.LogicalName) : name;
                    if (navigation is not null) json[property + AssociatedNavigationPropertyAnnotation] = navigation;
                    json[property + LookupLogicalNameAnnotation] = reference.LogicalName;
                }
                json[property] = reference?.Id.ToString();
                continue;
            }
            json[name] = ToJsonValue(value);
        }
        foreach (var (navigation, expand) in expands ?? new Dictionary<string, ODataExpand>())
            json[navigation] = Nested(e, expand, includeFormattedValues, unreadable);
        return json;
    }

    /// <summary>
    /// The record of an expanded link from the <c>{alias}.{attr}</c> values of <paramref name="e"/>, with its own nested expands
    /// (issue #190); null without the target's primary key.
    /// </summary>
    private static JsonObject? Nested(Entity e, ODataExpand expand, bool includeFormattedValues,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? unreadable)
    {
        var target = expand.Target;
        var prefix = expand.Alias + ".";
        var nested = new Entity(target.LogicalName);
        foreach (var (key, value) in e.Attributes)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || value is not AliasedValue aliased) continue;
            var attribute = key[prefix.Length..];
            nested[attribute] = aliased.Value;
            if (e.FormattedValues.TryGetValue(key, out var formatted)) nested.FormattedValues[attribute] = formatted;
        }
        if (!nested.Attributes.TryGetValue(SystemAttributes.PrimaryIdName(target), out var id) || id is not Guid guid) return null;
        nested.Id = guid;
        var json = Write(nested, target, null, includeFormattedValues, unreadable: unreadable);
        foreach (var (navigation, child) in expand.Children)
            json[navigation] = Nested(e, child, includeFormattedValues, unreadable);
        return json;
    }

    /// <summary>
    /// Reads a request body into an <see cref="Entity"/> of <paramref name="m"/>: <c>{lookup}@odata.bind</c> with
    /// <c>/set(guid)</c>, <c>set(guid)</c> or a full URL becomes an <see cref="EntityReference"/>; values are converted to
    /// the attribute's SDK type (Money, OptionSetValue, OptionSetValueCollection from <c>"1,2"</c>, DateTime UTC from ISO
    /// strings, Guid from strings, numbers to int/long/decimal/double); null stays null. Other annotations
    /// (keys containing <c>@</c>) are skipped. Values are not validated against metadata constraints (length, options):
    /// the organization service does that.
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument naming the property: unknown property, read-only
    /// <c>_{name}_value</c>, lookup without <c>@odata.bind</c>, malformed bind, or a value of the wrong JSON type.</exception>
    public static Entity FromJson(JsonObject body, EntityMetadata m, IMetadataStore store)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(store);
        var relationships = store.GetOneToManyRelationships();
        var attrs = (m.Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal);
        var entity = new Entity(m.LogicalName);
        var boundCustomers = new Dictionary<string, string>(StringComparer.Ordinal); // per-target lookup -> the navigation property bound
        var partyNavigation = PartyNavigation(m, relationships);

        foreach (var (key, node) in body)
        {
            if (key.EndsWith(BindSuffix, StringComparison.Ordinal))
            {
                var navigation = key[..^BindSuffix.Length];
                var (a, reference) = ReadLookupBind(m, attrs, navigation, node, relationships, store, boundCustomers);
                entity[a.LogicalName] = reference;
                continue;
            }
            if (key.Contains('@')) continue; // instance or property annotation

            // Plan 2.11 A8: the party rows of an activity, by participation type, become its party list columns.
            if (key == partyNavigation)
            {
                foreach (var (column, parties) in ReadParties(m, key, node, store)) entity[column] = parties;
                continue;
            }
            if (IsCollectionNavigation(m, key, relationships, store))
                throw XrmdFaultException.InvalidArgument(
                    $"'{key}' is a collection-valued navigation property of type '{ODataEdmModelBuilder.Namespace}.{m.LogicalName}': " +
                    "creating or changing related records in the body of a request (deep insert) is not supported; write them with their own requests.");

            // AttributeOf: not a Web API property, except a file or image companion (read-only: the Core ignores its value).
            if (!attrs.TryGetValue(key, out var attribute) || !ODataEdmModelBuilder.IsExposed(attribute) || WebApiHiddenAttributes.IsHidden(m.LogicalName, key))
            {
                if (IsLookupValueProperty(key, attrs, out var lookup))
                    throw XrmdFaultException.InvalidArgument(
                        $"Property '{key}' is read-only: set the lookup with {BindHint(m, (LookupAttributeMetadata)attrs[lookup], relationships)}: \"/<entityset>(<id>)\".");
                throw XrmdFaultException.InvalidArgument(
                    $"Could not find a property named '{key}' on type '{ODataEdmModelBuilder.Namespace}.{m.LogicalName}'.");
            }
            if (attribute is LookupAttributeMetadata lookupAttribute)
                throw XrmdFaultException.InvalidArgument(
                    $"'{key}' is a navigation property: set it with {BindHint(m, lookupAttribute, relationships)}: \"/<entityset>(<id>)\".");

            entity[key] = node is null ? null : ReadValue(m, attribute, JsonSerializer.SerializeToElement(node));
        }
        return entity;
    }

    /// <summary>
    /// The lookup and record of <c>{navigation}@odata.bind</c>: exactly the navigation property of <c>$metadata</c>, as Dataverse (no
    /// logical-name or case-insensitive fallback). A lookup with one navigation property per target (a Customer column, or a lookup
    /// of the activity model such as <c>regardingobjectid</c>, <c>objectid</c> or <c>partyid</c>) is refused under its bare name and
    /// when bound through two of them.
    /// </summary>
    private static (LookupAttributeMetadata Lookup, EntityReference? Reference) ReadLookupBind(EntityMetadata m,
        IReadOnlyDictionary<string, AttributeMetadata> attrs, string navigation, JsonNode? node, IReadOnlyList<OneToManyRelationshipMetadata> relationships,
        IMetadataStore store, Dictionary<string, string> boundPerTarget)
    {
        var found = ODataEdmModelBuilder.FindNavigation(m, navigation, relationships);
        if (found is null)
        {
            if (attrs.TryGetValue(navigation, out var bare) && bare is LookupAttributeMetadata lookup
                && ODataEdmModelBuilder.NavigationNames(m, lookup, relationships).Any(n => n.Target is not null))
                throw XrmdFaultException.InvalidArgument(
                    $"'{navigation}{BindSuffix}' is ambiguous: the {ColumnKind(lookup)} '{navigation}' has one navigation property per target; " +
                    $"bind it with {BindHint(m, lookup, relationships)}.");
            throw XrmdFaultException.InvalidArgument(
                $"'{navigation}' is not a single-valued navigation property of type '{ODataEdmModelBuilder.Namespace}.{m.LogicalName}'.");
        }
        var (a, expected) = found.Value;
        // Such a lookup holds one record: binding it through two navigation properties is a conflict, not a choice.
        if (expected is not null && !boundPerTarget.TryAdd(a.LogicalName, navigation))
            throw XrmdFaultException.InvalidArgument(
                $"The {ColumnKind(a)} '{a.LogicalName}' is bound through both '{boundPerTarget[a.LogicalName]}{BindSuffix}' and " +
                $"'{navigation}{BindSuffix}': bind one record, through the navigation property of its table.");
        return (a, node is null ? null : ReadBind(navigation, node, store, expected));
    }

    /// <summary>How a lookup with one navigation property per target is named in a message.</summary>
    private static string ColumnKind(LookupAttributeMetadata lookup) => CustomerColumns.IsCustomer(lookup) ? "Customer column" : "lookup";

    /// <summary>The collection-valued navigation property of the party rows of <paramref name="m"/> (<c>{activity}_activity_parties</c>); null for a table without one.</summary>
    private static string? PartyNavigation(EntityMetadata m, IEnumerable<OneToManyRelationshipMetadata> relationships) =>
        relationships.FirstOrDefault(r => r.ReferencingEntity == ActivitySchema.Party && r.ReferencingAttribute == ActivitySchema.ActivityId
                                          && r.ReferencedEntity == m.LogicalName)?.ReferencedEntityNavigationPropertyName;

    /// <summary>True when <paramref name="name"/> names a one-to-many or many-to-many relationship of <paramref name="m"/> from its side.</summary>
    private static bool IsCollectionNavigation(EntityMetadata m, string name, IEnumerable<OneToManyRelationshipMetadata> relationships, IMetadataStore store) =>
        relationships.Any(r => r.ReferencedEntity == m.LogicalName && (r.ReferencedEntityNavigationPropertyName == name || r.SchemaName == name))
        || store.GetManyToManyRelationships().Any(r =>
            (r.Entity1LogicalName == m.LogicalName && (r.Entity1NavigationPropertyName == name || r.SchemaName == name))
            || (r.Entity2LogicalName == m.LogicalName && (r.Entity2NavigationPropertyName == name || r.SchemaName == name)));

    /// <summary>
    /// The items of <c>{activity}_activity_parties</c> in a POST or PATCH body (plan 2.11, A8, A9), grouped into the party list
    /// columns of <paramref name="m"/> by their <c>participationtypemask</c>, in the body's order. Each item takes
    /// <c>participationtypemask</c> (required), <c>partyid_{table}@odata.bind</c> and <c>addressused</c>; the Core validates the
    /// rest (existence, an address alone only on email, at most one sender). Only the types present are written: on PATCH, the
    /// other types keep their rows, and an empty array changes nothing (from memory: checklist 3a).
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400): not an array of objects, a missing or unknown participation type,
    /// a type the server maintains (Regarding, Owner), or another property.</exception>
    private static Dictionary<string, EntityCollection> ReadParties(EntityMetadata m, string navigation, JsonNode? node, IMetadataStore store)
    {
        if (node is not JsonArray items)
            throw XrmdFaultException.InvalidArgument($"'{navigation}' must be an array of {ActivitySchema.Party} records.");
        var party = store.GetEntity(ActivitySchema.Party)
                    ?? throw XrmdFaultException.InvalidArgument($"The table '{ActivitySchema.Party}' does not exist.");
        var partyAttributes = (party.Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal);
        var relationships = store.GetOneToManyRelationships();
        var columns = ActivitySchema.PartyLists(m).ToDictionary(p => (int)p.Type, p => p.Attribute);
        var lists = new Dictionary<string, EntityCollection>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is not JsonObject fields)
                throw XrmdFaultException.InvalidArgument($"Each item of '{navigation}' must be an {ActivitySchema.Party} record (a JSON object).");
            var entity = new Entity(ActivitySchema.Party);
            int? type = null;
            var bound = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in fields)
            {
                if (key.EndsWith(BindSuffix, StringComparison.Ordinal))
                {
                    var (lookup, reference) = ReadLookupBind(party, partyAttributes, key[..^BindSuffix.Length], value, relationships, store, bound);
                    if (lookup.LogicalName != ActivitySchema.PartyId)
                        throw XrmdFaultException.InvalidArgument(
                            $"'{key}' cannot be set in an item of '{navigation}': only '{ActivitySchema.PartyId}' is bound there.");
                    entity[ActivitySchema.PartyId] = reference;
                    continue;
                }
                if (key.Contains('@')) continue; // annotation
                switch (key)
                {
                    case ActivitySchema.ParticipationTypeMask:
                        type = value is JsonValue v && v.TryGetValue<int>(out var mask)
                            ? mask
                            : throw XrmdFaultException.InvalidArgument($"'{ActivitySchema.ParticipationTypeMask}' of an item of '{navigation}' must be an integer.");
                        break;
                    case ActivitySchema.AddressUsed:
                        entity[ActivitySchema.AddressUsed] = value is null ? null
                            : value is JsonValue text && text.TryGetValue<string>(out var address) ? address
                            : throw XrmdFaultException.InvalidArgument($"'{ActivitySchema.AddressUsed}' of an item of '{navigation}' must be a string.");
                        break;
                    default:
                        throw XrmdFaultException.InvalidArgument(
                            $"Property '{key}' cannot be set in an item of '{navigation}': an item takes '{ActivitySchema.ParticipationTypeMask}', " +
                            $"'{ActivitySchema.PartyId}_<table>{BindSuffix}' and '{ActivitySchema.AddressUsed}'.");
                }
            }
            if (type is not { } t)
                throw XrmdFaultException.InvalidArgument($"Each item of '{navigation}' needs a '{ActivitySchema.ParticipationTypeMask}'.");
            if (t is (int)ParticipationType.Regarding or (int)ParticipationType.Owner)
                throw XrmdFaultException.InvalidArgument(
                    $"Participation type {t} ({(ParticipationType)t}) of '{navigation}' is maintained by the server and cannot be written.");
            if (!columns.TryGetValue(t, out var column))
                throw XrmdFaultException.InvalidArgument(
                    $"Participation type {t} is not a party list of '{m.LogicalName}'" +
                    (columns.Count == 0 ? "." : $": use one of {string.Join(", ", columns.Keys.Order())}."));
            if (!lists.TryGetValue(column, out var list)) lists[column] = list = new EntityCollection { EntityName = ActivitySchema.Party };
            list.Entities.Add(entity);
        }
        return lists;
    }

    /// <summary>
    /// A deleted record in a delta response (plan 2.13, ruling C12): exactly
    /// <c>{"@odata.context": "{baseUrl}/api/data/v9.2/$metadata#{entityset}/$deletedEntity", "id": "{guid}", "reason": "deleted"}</c>.
    /// </summary>
    /// <param name="baseUrl">Scheme, host and path base of the server (a trailing slash is ignored).</param>
    public static JsonObject DeletedEntryJson(string baseUrl, string entitySetName, Guid id) => new()
    {
        ["@odata.context"] = $"{baseUrl.TrimEnd('/')}{MetadataController.ServiceRootPath}$metadata#{entitySetName}/$deletedEntity",
        ["id"] = id.ToString(),
        ["reason"] = "deleted",
    };

    /// <summary>Scheme, host and path base of the request (no trailing slash): the base of every URL the Web API writes.</summary>
    internal static string BaseUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    /// <summary>A JSON response with OData minimal metadata content type and <c>OData-Version: 4.0</c>.</summary>
    internal static IResult Result(HttpContext context, JsonObject json, int status = StatusCodes.Status200OK)
    {
        context.Response.Headers["OData-Version"] = "4.0";
        return Results.Text(json.ToJsonString(), JsonContentType, Encoding.UTF8, status);
    }

    /// <summary>Reads the request body as a JSON object; with <paramref name="allowEmpty"/>, an empty body is an empty object.</summary>
    /// <exception cref="XrmdFaultException">InvalidArgument when the body is not a JSON object.</exception>
    internal static async Task<JsonObject> ReadBodyAsync(HttpRequest request, bool allowEmpty = false)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        if (allowEmpty && string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try
        {
            var body = JsonNode.Parse(text) as JsonObject ?? throw XrmdFaultException.InvalidArgument("The request body must be a JSON object.");
            _ = body.Count; // JsonObject reads its properties lazily: a duplicate key throws here, not at the caller's first access
            return body;
        }
        catch (JsonException ex)
        {
            throw XrmdFaultException.InvalidArgument("Invalid request body: " + ex.Message);
        }
        catch (ArgumentException ex)
        {
            // Duplicate property names ("An item with the same key has already been added. Key: …").
            throw XrmdFaultException.InvalidArgument("Invalid request body: " + ex.Message);
        }
    }

    /// <summary>Splits <c>name(key)</c> into its name and key text; false when the segment has no parenthesized key.</summary>
    internal static bool TrySplitKeySegment(string segment, out string name, out string key)
    {
        var open = segment.IndexOf('(');
        if (open > 0 && segment.EndsWith(')'))
        {
            name = segment[..open];
            key = segment[(open + 1)..^1];
            return true;
        }
        name = segment;
        key = "";
        return false;
    }

    /// <summary>
    /// The navigation property of <paramref name="lookup"/> that stands for a value of <paramref name="target"/>: a Customer column's
    /// or an activity model lookup's per target, the lookup's one otherwise. Null when the lookup has one navigation property per
    /// target and none for <paramref name="target"/> (e.g. the Owner party row of a team-owned activity names a team, which is not a
    /// target of <c>partyid</c>): the annotation is then left out rather than naming another table's navigation property.
    /// </summary>
    /// <exception cref="InvalidOperationException">A party list, which has no navigation property (plan 2.11 A8): it is never
    /// written as a lookup.</exception>
    internal static string? NavigationOf(EntityMetadata m, LookupAttributeMetadata lookup, IEnumerable<OneToManyRelationshipMetadata>? relationships,
        string target)
    {
        if (ActivitySchema.IsPartyList(lookup))
            throw new InvalidOperationException($"The party list '{m.LogicalName}.{lookup.LogicalName}' has no navigation property.");
        var names = ODataEdmModelBuilder.NavigationNames(m, lookup, relationships);
        if (names.Count == 0) return null; // a lookup of the activity model without targets yet: no navigation property to name
        if (names.FirstOrDefault(n => n.Target == target).Name is { } exact) return exact;
        return names.All(n => n.Target is null) ? names[0].Name : null;
    }

    /// <summary>The <c>@odata.bind</c> names that set <paramref name="lookup"/>, quoted: one, or one per target of a Customer column.</summary>
    private static string BindHint(EntityMetadata m, LookupAttributeMetadata lookup, IEnumerable<OneToManyRelationshipMetadata>? relationships) =>
        string.Join(" or ", ODataEdmModelBuilder.NavigationNames(m, lookup, relationships).Select(n => $"'{n.Name}{BindSuffix}'"));

    /// <param name="expected">The table the navigation property takes (a Customer column's); null when it takes any target.</param>
    private static EntityReference ReadBind(string navigation, JsonNode node, IMetadataStore store, string? expected = null)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var url))
            throw XrmdFaultException.InvalidArgument($"'{navigation}{BindSuffix}' must be a string such as \"/<entityset>(<id>)\".");

        // The key segment is the last one (a partition id never holds a '/', plan 3.4).
        var segment = url[(url.LastIndexOf('/') + 1)..];
        if (!TrySplitKeySegment(segment, out var setName, out var key))
            throw XrmdFaultException.InvalidArgument(
                $"Invalid '{navigation}{BindSuffix}' value '{url}': expected \"/<entityset>(<id>)\".");
        var isGuid = Guid.TryParseExact(key, "D", out var id);
        var target = store.GetEntityBySetName(setName);
        // Plan 3.4: a row of an elastic table by its key, (<pk>=<id>,partitionid='<p>'); any other non-GUID key is refused as before.
        if (!isGuid && (target is null || !ElasticTables.IsElastic(target)))
            throw XrmdFaultException.InvalidArgument(
                $"Invalid '{navigation}{BindSuffix}' value '{url}': expected \"/<entityset>(<id>)\".");
        if (target is null)
            throw XrmdFaultException.InvalidArgument(
                $"Invalid '{navigation}{BindSuffix}' value '{url}': entity set '{setName}' does not exist.");
        if (expected is not null && target.LogicalName != expected)
            throw XrmdFaultException.InvalidArgument(
                $"Invalid '{navigation}{BindSuffix}' value '{url}': the navigation property takes a '{expected}' record, not a '{target.LogicalName}' one.");
        if (isGuid) return new EntityReference(target.LogicalName, id);

        // The lookup and its partition column ({lookup}pid) are set by the organization service from the key attributes.
        var (elasticId, partition) = EntitySetController.ParseElasticKey(target, setName, key);
        var keys = new KeyAttributeCollection { { SystemAttributes.PrimaryIdName(target), elasticId } };
        if (partition is not null) keys.Add(ElasticTables.PartitionId, partition);
        return new EntityReference(target.LogicalName, keys) { Id = elasticId };
    }

    private static bool IsLookupValueProperty(string key, IReadOnlyDictionary<string, AttributeMetadata> attrs, out string lookup)
    {
        lookup = key.Length > "__value".Length && key.StartsWith('_') && key.EndsWith("_value", StringComparison.Ordinal)
            ? key[1..^"_value".Length]
            : "";
        return attrs.TryGetValue(lookup, out var a) && a is LookupAttributeMetadata;
    }

    private static object ReadValue(EntityMetadata m, AttributeMetadata a, JsonElement el)
    {
        object? value = a switch
        {
            StringAttributeMetadata or MemoAttributeMetadata => el.ValueKind == JsonValueKind.String ? el.GetString() : null,
            MultiSelectPicklistAttributeMetadata => el.ValueKind == JsonValueKind.String ? ParseOptions(el.GetString()!) : null,
            EntityNameAttributeMetadata => el.ValueKind == JsonValueKind.String ? el.GetString() : null, // a table's logical name (A7)
            IntegerAttributeMetadata => el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i) ? i : null,
            BigIntAttributeMetadata => el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var l) ? l : null,
            DecimalAttributeMetadata => el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d) ? d : null,
            MoneyAttributeMetadata => el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var money) ? new Money(money) : null,
            DoubleAttributeMetadata => el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var f) && double.IsFinite(f) ? f : null,
            BooleanAttributeMetadata => el.ValueKind is JsonValueKind.True or JsonValueKind.False ? el.GetBoolean() : null,
            DateTimeAttributeMetadata => el.ValueKind == JsonValueKind.String ? ParseDateTime(el.GetString()!) : null,
            UniqueIdentifierAttributeMetadata => el.ValueKind == JsonValueKind.String && Guid.TryParseExact(el.GetString(), "D", out var g) ? g : null,
            // Edm.Binary: the image as a base64 string (Learn, image column data).
            ImageAttributeMetadata => el.ValueKind == JsonValueKind.String && el.TryGetBytesFromBase64(out var image) ? image : null,
            EnumAttributeMetadata => el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var option) ? new OptionSetValue(option) : null,
            _ => throw XrmdFaultException.InvalidArgument(
                $"Property '{a.LogicalName}' of type '{ODataEdmModelBuilder.Namespace}.{m.LogicalName}' ({a.AttributeType}) cannot be written."),
        };
        return value ?? throw XrmdFaultException.InvalidArgument(
            $"Cannot convert the literal '{el.GetRawText()}' to the type of property '{a.LogicalName}' ({a.AttributeType}).");
    }

    /// <summary>ISO 8601 only: a date, or a date and time (minutes, seconds, fractions) with Z, an offset or no zone (UTC).</summary>
    private static readonly string[] DateTimeFormats =
        ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];

    private static DateTime? ParseDateTime(string text) =>
        DateTimeOffset.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;

    private static OptionSetValueCollection? ParseOptions(string csv)
    {
        var options = new OptionSetValueCollection();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) return null;
            options.Add(new OptionSetValue(value));
        }
        return options;
    }

    /// <summary>A column value as Web API JSON (the primitive forms <see cref="ToJson"/> writes).</summary>
    internal static JsonNode? ToJsonValue(object? value) => value switch
    {
        null => null,
        Money money => JsonValue.Create(Normalize(money.Value)),
        decimal d => JsonValue.Create(Normalize(d)),
        OptionSetValueCollection options => string.Join(",", options.Select(o => o.Value.ToString(CultureInfo.InvariantCulture))),
        OptionSetValue option => option.Value,
        DateTime dt => (dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt).ToString(DateTimeFormat, CultureInfo.InvariantCulture),
        Guid g => g.ToString(),
        byte[] bytes => Convert.ToBase64String(bytes), // Edm.Binary (an image column's thumbnail)
        string s => s,
        bool b => b,
        int i => i,
        long l => l,
        double f => f,
        _ => value.ToString(),
    };

    /// <summary>Drops trailing zeros (decimal(19,4) reads back as 1234.5000; Dataverse writes 1234.5).</summary>
    private static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;
}
