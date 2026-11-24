using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Query.Cosmos;

/// <summary>
/// The SDK twin of <see cref="JsonPaths.IsJson(Xrmd.Model.Metadata.ColumnDefinition)"/> (S.2c, R1). It keeps its SDK body: it runs for
/// every column of a Cosmos result, and a conversion would cost more than the test. Transitional: removed with the SDK view (S.2i).
/// </summary>
public static partial class JsonPaths
{
    /// <summary>True for a string column with the JSON format.</summary>
    public static bool IsJson(AttributeMetadata attribute) =>
        attribute is StringAttributeMetadata { FormatName.Value: { } f } && f == StringFormatName.Json.Value;
}
