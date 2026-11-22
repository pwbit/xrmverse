using System.Text.Json.Nodes;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Schema;

/// <summary>
/// The Web API request bodies (<c>EntityDefinitions</c>, <c>Attributes</c>, <c>RelationshipDefinitions</c>,
/// <c>GlobalOptionSetDefinitions</c>) for the same tables, columns and choices the schema YAML describes. They are the
/// SDK metadata of <see cref="SchemaColumnTypes"/> serialized in the Web API shape, so the CLI keeps no second type mapping.
/// </summary>
public static class SchemaWebApiBodies
{
    /// <summary>A new table with its primary name column (<c>POST EntityDefinitions</c>), with <c>IsActivity</c>, <c>HasNotes</c> and <c>HasActivities</c> when true.</summary>
    public static JsonObject Entity(SchemaTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var primary = SchemaColumnTypes.ToPrimaryAttribute(table);
        var entity = new EntityMetadata
        {
            LogicalName = table.Name,
            SchemaName = table.SchemaName ?? table.Name,
            DisplayName = SchemaColumnTypes.Label(table.Label),
            OwnershipType = table.Ownership == SchemaOwnership.Organization ? OwnershipTypes.OrganizationOwned : OwnershipTypes.UserOwned,
        };
        if (ElasticTables.NormalizeTableType(table.TableType) == ElasticTables.TableTypeElastic) entity.TableType = ElasticTables.TableTypeElastic;
        if (table.PluralLabel is { } plural) entity.DisplayCollectionName = SchemaColumnTypes.Label(plural);
        if (table.Description is { } description) entity.Description = SchemaColumnTypes.Label(description);
        // Plan 2.11: only true is stated (false is the default of a new table).
        if (table.IsActivity) entity.IsActivity = true;
        if (table.HasNotes == true) entity.HasNotes = true;
        if (table.HasActivities == true) entity.HasActivities = true;
        // The entity type is not polymorphic, so the serializer writes no @odata.type: state it, as the Web API bodies do.
        var json = new JsonObject { [MetadataSerializer.ODataTypeProperty] = MetadataSerializer.ODataTypePrefix + nameof(EntityMetadata) };
        foreach (var (name, value) in Parse(MetadataSerializer.Serialize(entity))) json[name] = value?.DeepClone();
        // IsPrimaryName has no public setter on the SDK type: the Web API body states it.
        var primaryJson = Parse(MetadataSerializer.SerializeAttribute(primary));
        primaryJson["IsPrimaryName"] = true;
        json["Attributes"] = new JsonArray(primaryJson);
        return json;
    }

    /// <summary>A new column that is not a lookup (<c>POST EntityDefinitions(...)/Attributes</c>).</summary>
    /// <exception cref="ArgumentException">The column is not valid, or is a lookup (<see cref="Lookup"/>) or a customer (<see cref="Customer"/>).</exception>
    public static JsonObject Attribute(SchemaColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Type == SchemaColumnTypes.Lookup)
            throw new ArgumentException("A lookup is created with its relationship (see Lookup).", nameof(column));
        if (column.Type == SchemaColumnTypes.Customer)
            throw new ArgumentException("A customer column is created with its two relationships (see Customer).", nameof(column));
        return Parse(MetadataSerializer.SerializeAttribute(SchemaColumnTypes.ToAttribute(column)));
    }

    /// <summary>
    /// Makes an image column the table's primary image (<c>PUT EntityDefinitions(...)/Attributes(...)</c>): a create
    /// ignores IsPrimaryImage = true when the table already has an image column, an update moves the flag.
    /// </summary>
    public static JsonObject PrimaryImage(string columnName) =>
        Parse(MetadataSerializer.SerializeAttribute(new ImageAttributeMetadata { LogicalName = columnName, IsPrimaryImage = true }));

    /// <summary>A lookup column with its one-to-many relationship (<c>POST RelationshipDefinitions</c>).</summary>
    public static JsonObject Lookup(SchemaColumn column, string referencingTable, string relationshipName, string referencedAttribute)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Type != SchemaColumnTypes.Lookup) throw new ArgumentException("The column is not a lookup.", nameof(column));
        var relationship = new OneToManyRelationshipMetadata
        {
            SchemaName = relationshipName,
            ReferencingEntity = referencingTable,
            ReferencedEntity = column.Target,
            ReferencedAttribute = referencedAttribute,
        };
        var json = Parse(MetadataSerializer.SerializeRelationship(relationship));
        json["Lookup"] = Parse(MetadataSerializer.SerializeAttribute(SchemaColumnTypes.ToAttribute(column)));
        return json;
    }

    /// <summary>The SchemaName of the relationship of a Customer column to <paramref name="target"/> (<c>{table}_{column}_{target}</c>, as a lookup's).</summary>
    public static string CustomerRelationshipName(string table, string column, string target) => $"{table}_{column}_{target}";

    /// <summary>
    /// A Customer column with its two one-to-many relationships, one to account and one to contact
    /// (<c>POST CreateCustomerRelationships</c>, the only way to create one): <c>{ Lookup, OneToManyRelationships }</c>.
    /// </summary>
    public static JsonObject Customer(SchemaColumn column, string referencingTable)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Type != SchemaColumnTypes.Customer) throw new ArgumentException("The column is not a customer column.", nameof(column));
        var relationships = new JsonArray();
        foreach (var target in CustomerColumns.Targets)
        {
            relationships.Add(Parse(MetadataSerializer.SerializeRelationship(new OneToManyRelationshipMetadata
            {
                SchemaName = CustomerRelationshipName(referencingTable, column.Name, target),
                ReferencingEntity = referencingTable,
                ReferencedEntity = target,
                ReferencedAttribute = target + "id",
            })));
        }
        return new JsonObject
        {
            ["Lookup"] = Parse(MetadataSerializer.SerializeAttribute(SchemaColumnTypes.ToAttribute(column))),
            ["OneToManyRelationships"] = relationships,
        };
    }

    /// <summary>A many-to-many relationship; its intersect table is named like the relationship.</summary>
    public static JsonObject ManyToMany(string relationshipName, string table, string with) =>
        Parse(MetadataSerializer.SerializeRelationship(new ManyToManyRelationshipMetadata
        {
            SchemaName = relationshipName,
            Entity1LogicalName = table,
            Entity2LogicalName = with,
            IntersectEntityName = relationshipName,
        }));

    /// <summary>A global choice (<c>POST GlobalOptionSetDefinitions</c>).</summary>
    public static JsonObject Choice(string name, string label, IEnumerable<SchemaOption> options)
    {
        var set = new OptionSetMetadata(new OptionMetadataCollection(options.Select(o => new OptionMetadata(SchemaColumnTypes.Label(o.Label), o.Value)).ToList()))
        {
            Name = name,
            IsGlobal = true,
            OptionSetType = OptionSetType.Picklist,
            DisplayName = SchemaColumnTypes.Label(label),
        };
        return Parse(MetadataSerializer.SerializeOptionSet(set));
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
}
