using Microsoft.Xrm.Sdk;

namespace Xrmd.Metadata;

/// <summary>The member of <see cref="CustomApiTypes"/> that names SDK record types (S.2b2: SDK use stays in the Sdk folder).</summary>
public static partial class CustomApiTypes
{
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
}
