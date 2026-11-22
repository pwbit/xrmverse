using Microsoft.Xrm.Sdk;

namespace Xrmd.Metadata;

/// <summary>
/// The type table of custom API parameters and response properties (plan 15, Global Constraints): for each
/// <see cref="CustomApiType"/>, the SDK type of its values, its Edm type in <c>$metadata</c> and the default value a response
/// property takes when the main operation leaves it unset. The validator and the Web API parsers use it, and <c>EdmTypeName</c> is the text of their messages with Dataverse's <c>mscrm</c> alias; the
/// <c>$metadata</c> declarations (<c>CustomApiOperations</c>) are built apart, in the model's namespace, and a test keeps them equal.
/// </summary>
public static class CustomApiTypes
{
    private const string CrmBaseEntity = "mscrm.crmbaseentity";

    /// <summary>The SDK type a value of <paramref name="type"/> must have, with no conversion.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value outside 0–12.</exception>
    public static Type SdkType(CustomApiType type) => type switch
    {
        CustomApiType.Boolean => typeof(bool),
        CustomApiType.DateTime => typeof(DateTime),
        CustomApiType.Decimal => typeof(decimal),
        CustomApiType.Entity => typeof(Entity),
        CustomApiType.EntityCollection => typeof(EntityCollection),
        CustomApiType.EntityReference => typeof(EntityReference),
        CustomApiType.Float => typeof(double),
        CustomApiType.Integer => typeof(int),
        CustomApiType.Money => typeof(Money),
        CustomApiType.Picklist => typeof(OptionSetValue),
        CustomApiType.String => typeof(string),
        CustomApiType.StringArray => typeof(string[]),
        CustomApiType.Guid => typeof(Guid),
        _ => throw OutOfRange(type),
    };

    /// <summary>
    /// The Edm type name of <paramref name="type"/>. An Entity or EntityReference with a <paramref name="logicalEntityName"/> is
    /// that table's entity type; otherwise <c>mscrm.crmbaseentity</c>. An EntityCollection is always a collection of
    /// <c>crmbaseentity</c>, its <c>logicalentityname</c> notwithstanding.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A value outside 0–12.</exception>
    public static string EdmTypeName(CustomApiType type, string? logicalEntityName) => type switch
    {
        CustomApiType.Boolean => "Edm.Boolean",
        CustomApiType.DateTime => "Edm.DateTimeOffset",
        CustomApiType.Decimal or CustomApiType.Money => "Edm.Decimal",
        CustomApiType.Entity or CustomApiType.EntityReference =>
            string.IsNullOrEmpty(logicalEntityName) ? CrmBaseEntity : "mscrm." + logicalEntityName,
        CustomApiType.EntityCollection => $"Collection({CrmBaseEntity})",
        CustomApiType.Float => "Edm.Double",
        CustomApiType.Integer or CustomApiType.Picklist => "Edm.Int32",
        CustomApiType.String => "Edm.String",
        CustomApiType.StringArray => "Collection(Edm.String)",
        CustomApiType.Guid => "Edm.Guid",
        _ => throw OutOfRange(type),
    };

    /// <summary>The default of the SDK type: <c>default</c> for a value type, null for a reference type.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value outside 0–12.</exception>
    public static object? DefaultValue(CustomApiType type) => type switch
    {
        CustomApiType.Boolean => false,
        CustomApiType.DateTime => DateTime.MinValue,
        CustomApiType.Decimal => 0m,
        CustomApiType.Float => 0d,
        CustomApiType.Integer => 0,
        CustomApiType.Guid => Guid.Empty,
        CustomApiType.Entity or CustomApiType.EntityCollection or CustomApiType.EntityReference or CustomApiType.Money
            or CustomApiType.Picklist or CustomApiType.String or CustomApiType.StringArray => null,
        _ => throw OutOfRange(type),
    };

    private static ArgumentOutOfRangeException OutOfRange(CustomApiType type) =>
        new(nameof(type), (int)type, "A custom API type is a value from 0 to 12.");
}
