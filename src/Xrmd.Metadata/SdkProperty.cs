using System.Reflection;

namespace Xrmd.Metadata;

/// <summary>
/// Sets SDK metadata properties whose setters are internal/private (e.g. <c>AttributeMetadata.AttributeType</c>,
/// <c>EntityMetadata.Attributes</c>): the server owns these values, the SDK only exposes them read-only to clients.
/// </summary>
internal static class SdkProperty
{
    public static void Set(object target, string propertyName, object? value) =>
        SetterOf(target.GetType(), propertyName).Invoke(target, [value]);

    /// <summary>The setter (any visibility) of a public instance property, resolved on its declaring type.</summary>
    public static MethodInfo SetterOf(Type type, string propertyName)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                       ?? throw new InvalidOperationException($"{type.Name} has no property {propertyName}.");
        return TryGetSetter(property)
               ?? throw new InvalidOperationException($"{type.Name}.{propertyName} has no setter.");
    }

    public static MethodInfo? TryGetSetter(PropertyInfo property) =>
        property.GetSetMethod(nonPublic: true)
        ?? property.DeclaringType?.GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod(nonPublic: true);
}
