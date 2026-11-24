using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// Metadata properties the server assigns, ignored when a client creates a component (Web API body or SDK request): a
/// client value could collide, bypass the rules on system components or change how the table is secured
/// (<c>IsIntersect</c> makes a table readable by every member, without privileges). The <c>CanBeSecuredFor*</c> flags of a
/// column are computed by every read (<see cref="FieldSecurability"/>), and so is <c>CanChangeTrackingBeEnabled</c> of a table
/// (<see cref="ChangeTrackingSchema"/>).
/// </summary>
public static partial class ServerOwnedProperties
{
    /// <summary>The server-owned properties of <see cref="TableDefinition"/>.</summary>
    public static IReadOnlyList<string> Entity { get; } =
    [
        nameof(TableDefinition.MetadataId), nameof(TableDefinition.ObjectTypeCode), nameof(TableDefinition.IsIntersect),
        nameof(TableDefinition.IsManaged), nameof(TableDefinition.IsCustomEntity), nameof(TableDefinition.IsLogicalEntity),
        nameof(TableDefinition.IsPrivate), nameof(TableDefinition.IntroducedVersion), nameof(TableDefinition.CanChangeTrackingBeEnabled),
    ];

    /// <summary>The server-owned properties of <see cref="ColumnDefinition"/>.</summary>
    public static IReadOnlyList<string> Attribute { get; } =
    [
        nameof(ColumnDefinition.MetadataId), nameof(ColumnDefinition.IsManaged), nameof(ColumnDefinition.IsCustomAttribute),
        nameof(ColumnDefinition.IsLogical), nameof(ColumnDefinition.AttributeOf), nameof(ColumnDefinition.IntroducedVersion),
        nameof(ColumnDefinition.CanBeSecuredForCreate), nameof(ColumnDefinition.CanBeSecuredForRead), nameof(ColumnDefinition.CanBeSecuredForUpdate),
    ];

    /// <summary>Sets the server-owned properties of <paramref name="entity"/> and of its attributes to null.</summary>
    public static void Clear(TableDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        foreach (var name in Entity) typeof(TableDefinition).GetProperty(name)!.SetValue(entity, null);
        foreach (var attribute in entity.Attributes ?? []) Clear(attribute);
    }

    /// <summary>Sets the server-owned properties of <paramref name="attribute"/> to null.</summary>
    public static void Clear(ColumnDefinition attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        foreach (var name in Attribute) typeof(ColumnDefinition).GetProperty(name)!.SetValue(attribute, null);
        // The model, like the SDK, declares SourceTypeMask only on the column types that can be calculated (plan 2.12a, ruling K12).
        attribute.GetType().GetProperty(SourceTypeMask)?.SetValue(attribute, null);
    }

    /// <summary>The server-owned <c>SourceTypeMask</c> of a column that can be calculated; the rules set it from the definition.</summary>
    public const string SourceTypeMask = nameof(SourceTypeMask);
}
