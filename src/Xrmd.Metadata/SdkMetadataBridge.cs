using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// Converts between the SDK metadata types and the own model types (<c>Xrmd.Model.Metadata</c>). Both serializers write
/// identical JSON (proven by the golden files), so each conversion is "serialize on one side, deserialize on the other";
/// runtime types are kept (<c>StringAttributeMetadata</c> ↔ <c>TextColumn</c>). This is a planned, temporary SDK edge:
/// only the SDK edges of the server call it, everything else works on the model.
/// </summary>
/// <remarks>
/// <c>ToSdk(TableDefinition)</c> fails for a table that carries <c>Privileges</c> or <c>Keys[].AsyncJob</c>: the SDK side
/// cannot read them (no usable constructor; an abstract <c>KeyAttributeCollection</c>). Stored metadata never carries them.
/// </remarks>
public static class SdkMetadataBridge
{
    public static TableDefinition ToModel(EntityMetadata entity) =>
        MetadataJson.DeserializeTable(MetadataSerializer.Serialize(entity));

    public static EntityMetadata ToSdk(TableDefinition table) =>
        MetadataSerializer.Deserialize(MetadataJson.Serialize(table));

    public static ColumnDefinition ToModel(AttributeMetadata attribute) =>
        MetadataJson.DeserializeColumn(MetadataSerializer.SerializeAttribute(attribute));

    public static AttributeMetadata ToSdk(ColumnDefinition column) =>
        MetadataSerializer.DeserializeAttribute(MetadataJson.SerializeColumn(column));

    public static ChoiceSetBase ToModel(OptionSetMetadataBase optionSet) =>
        MetadataJson.DeserializeChoiceSet(MetadataSerializer.SerializeOptionSet(optionSet));

    public static OptionSetMetadataBase ToSdk(ChoiceSetBase choiceSet) =>
        MetadataSerializer.DeserializeOptionSet(MetadataJson.SerializeChoiceSet(choiceSet));

    public static RelationshipDefinition ToModel(RelationshipMetadataBase relationship) =>
        MetadataJson.DeserializeRelationship(MetadataSerializer.SerializeRelationship(relationship));

    public static RelationshipMetadataBase ToSdk(RelationshipDefinition relationship) =>
        MetadataSerializer.DeserializeRelationship(MetadataJson.SerializeRelationship(relationship));
}
