using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// File and image columns (ruling F1): the companion attributes a column brings, the <c>MaxSizeInKB</c> limits and the names of
/// the <c>fileattachment</c> table and of its relationship to the table that holds the column.
/// </summary>
public static class FileColumns
{
    /// <summary>The system table that holds the files and full-size images of every file or image column.</summary>
    public const string AttachmentEntity = "fileattachment";

    /// <summary>The polymorphic lookup of <see cref="AttachmentEntity"/> to the record that owns the file.</summary>
    public const string AttachmentLookup = "objectid";

    /// <summary>
    /// The tables that are targets of <see cref="AttachmentLookup"/> whatever their columns (plan 2.11, ruling A16): <c>annotation</c>,
    /// whose body (<c>documentbody</c>) is a <c>fileattachment</c> row and a blob. Its target and its relationship are added at startup
    /// (the activity model's links) and never removed with a file column.
    /// </summary>
    public static IReadOnlySet<string> PermanentTargets { get; } = new HashSet<string>(StringComparer.Ordinal) { ActivitySchema.Annotation };

    /// <summary>True for a table in <see cref="PermanentTargets"/>.</summary>
    public static bool IsPermanentTarget(string? entityLogicalName) => entityLogicalName is not null && PermanentTargets.Contains(entityLogicalName);

    /// <summary>The smallest <c>MaxSizeInKB</c> of a file or image column.</summary>
    public const int MinSizeInKB = 1;

    public const int DefaultFileMaxSizeInKB = 32768, MaxFileSizeInKB = 10485760;
    public const int DefaultImageMaxSizeInKB = 10240, MaxImageSizeInKB = 30720;

    /// <summary>Side of the square thumbnail an image column stores (Dataverse's <c>MaxHeight</c> and <c>MaxWidth</c>, read-only).</summary>
    public const int ThumbnailSize = 144;

    /// <summary>MaxLength of the file name companion of a file column.</summary>
    public const int FileNameLength = 255;

    /// <summary>MaxLength of the computed url companion of an image column.</summary>
    public const int UrlLength = 200;

    /// <summary>The name of the one-to-many relationship from <paramref name="entityLogicalName"/> to <see cref="AttachmentEntity"/>.</summary>
    public static string RelationshipName(string entityLogicalName) => entityLogicalName + "_FileAttachments";

    /// <summary>True for <c>fileattachment.objectid</c>, the one lookup whose targets the server maintains.</summary>
    public static bool IsAttachmentObjectId(AttributeMetadata a) =>
        a is LookupAttributeMetadata && a.LogicalName == AttachmentLookup && a.EntityLogicalName == AttachmentEntity;

    /// <summary>The message of the 400 InvalidArgument (0x80040203) for a public write of the server-maintained objectid and its relationships.</summary>
    public static string ManagedMessage(string what) =>
        $"{what} is managed by the file and image columns of its table: create or delete those columns instead.";

    public static bool IsFileOrImage(AttributeMetadata a) => a is FileAttributeMetadata or ImageAttributeMetadata;

    /// <summary>
    /// True for a companion attribute that has a column of its own (the file name, the image id and timestamp): one the server
    /// made with <see cref="Companions"/>, which names it after its main column (<c>AttributeOf</c>) and links it to the main
    /// column's MetadataId (<c>LinkedAttributeId</c>). An attribute sees only itself here, so the link is what stands for "the
    /// main column is a file or image column": callers cannot set AttributeOf or LinkedAttributeId (both are refused on
    /// every public write), so only <see cref="Companions"/> makes one. The url companion is computed on read and has no column.
    /// </summary>
    public static bool IsStoredCompanion(AttributeMetadata a) =>
        a.AttributeOf is { } main && a.LinkedAttributeId is not null
        && ((a is StringAttributeMetadata && a.LogicalName == main + "_name")
            || (a is UniqueIdentifierAttributeMetadata && a.LogicalName == main + "id")
            || (a is BigIntAttributeMetadata && a.LogicalName == main + "_timestamp"));

    /// <summary>
    /// True for the url companion of an image column (<c>&lt;image&gt;_url</c>), made by <see cref="Companions"/> like the stored
    /// ones: it has no column, its value is computed on read from the image id and timestamp:
    /// <c>/Image/download.aspx?Entity=&lt;entity&gt;&amp;Attribute=&lt;image&gt;&amp;Id=&lt;image id&gt;&amp;Timestamp=&lt;timestamp&gt;</c>
    /// (Learn, image column data, "Download URL"), null when the image id is.
    /// </summary>
    public static bool IsUrlCompanion(AttributeMetadata a) =>
        a is StringAttributeMetadata && a.AttributeOf is { } main && a.LinkedAttributeId is not null && a.LogicalName == main + UrlSuffix;

    /// <summary>The suffixes of the companions of an image column: the image id, the timestamp and the url.</summary>
    public const string ImageIdSuffix = "id", TimestampSuffix = "_timestamp", UrlSuffix = "_url";

    /// <summary>The path of the image download endpoint (ruling F10).</summary>
    public const string DownloadPath = "/Image/download.aspx";

    /// <summary>
    /// <see cref="IsStoredCompanion(AttributeMetadata)"/> checked against the attributes of its table: the main column exists, is
    /// a file or image column and is the one the companion is linked to.
    /// </summary>
    public static bool IsStoredCompanion(AttributeMetadata a, IEnumerable<AttributeMetadata> siblings) =>
        IsStoredCompanion(a)
        && siblings.FirstOrDefault(x => x.LogicalName == a.AttributeOf) is { } main
        && IsFileOrImage(main) && main.MetadataId == a.LinkedAttributeId;

    /// <summary>
    /// The companions of the file or image column <paramref name="main"/> (its MetadataId set), all read-only with
    /// <c>AttributeOf</c> and <c>LinkedAttributeId</c> set.
    /// </summary>
    public static IReadOnlyList<AttributeMetadata> Companions(AttributeMetadata main)
    {
        var schema = main.SchemaName ?? main.LogicalName;
        AttributeMetadata Companion(AttributeMetadata a, string logicalName, string schemaName)
        {
            a.LogicalName = logicalName;
            a.SchemaName = schemaName;
            a.IsValidForCreate = false;
            a.IsValidForUpdate = false;
            SdkProperty.Set(a, nameof(AttributeMetadata.AttributeOf), main.LogicalName);
            SdkProperty.Set(a, nameof(AttributeMetadata.LinkedAttributeId), main.MetadataId);
            return a;
        }

        return main switch
        {
            FileAttributeMetadata => [Companion(new StringAttributeMetadata { MaxLength = FileNameLength }, main.LogicalName + "_name", schema + "_Name")],
            ImageAttributeMetadata =>
            [
                Companion(new UniqueIdentifierAttributeMetadata(), main.LogicalName + "id", schema + "Id"),
                Companion(new BigIntAttributeMetadata(), main.LogicalName + "_timestamp", schema + "_Timestamp"),
                Companion(new StringAttributeMetadata { MaxLength = UrlLength }, main.LogicalName + "_url", schema + "_URL"),
            ],
            _ => [],
        };
    }
}
