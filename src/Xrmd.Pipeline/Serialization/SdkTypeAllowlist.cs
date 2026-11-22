using System.Reflection;
using System.Runtime.Serialization;
using System.Xml;
using Microsoft.Xrm.Sdk;

namespace Xrmd.Pipeline.Serialization;

/// <summary>
/// The types the data-contract deserialization of untrusted XML (the <c>asyncoperation.data</c> payload, SOAP requests)
/// accepts: .NET primitives (<see cref="string"/>, <see cref="bool"/>, the integer types, <see cref="float"/>,
/// <see cref="double"/>, <see cref="decimal"/>, <see cref="char"/>, <see cref="Guid"/>, <see cref="DateTime"/>,
/// <see cref="DateTimeOffset"/>, <see cref="TimeSpan"/>, <see cref="Uri"/>), the types and enums of the
/// <c>Microsoft.Xrm.Sdk</c> and <c>Microsoft.Crm.Sdk.Proxy</c> assemblies, arrays of allowed types (so <c>byte[]</c>) and
/// early-bound subclasses of <see cref="Entity"/>, <see cref="OrganizationRequest"/> and <see cref="OrganizationResponse"/>.
/// </summary>
/// <remarks>
/// Pure reflection over an already loaded <see cref="Type"/>: no lock, no assembly load (it is called from
/// <see cref="DataContractResolver"/> callbacks, see <c>PluginAssemblyLoader</c>'s remarks).
/// </remarks>
public static class SdkTypeAllowlist
{
    private static readonly Assembly SdkAssembly = typeof(Entity).Assembly;
    private static readonly Assembly ProxyAssembly = typeof(Microsoft.Crm.Sdk.Messages.WhoAmIRequest).Assembly;

    private static readonly HashSet<Type> Primitives =
    [
        typeof(string), typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
        typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal), typeof(char), typeof(Guid), typeof(DateTime),
        typeof(DateTimeOffset), typeof(TimeSpan), typeof(Uri),
    ];

    /// <summary>True when <paramref name="type"/> may be read from (or written to) a data-contract payload.</summary>
    public static bool IsAllowed(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        while (type.IsArray) type = type.GetElementType()!;
        if (Primitives.Contains(type)) return true;
        if (type.Assembly == SdkAssembly || type.Assembly == ProxyAssembly)
            return !type.IsConstructedGenericType || type.GenericTypeArguments.All(a => a == typeof(object) || IsAllowed(a));
        return typeof(Entity).IsAssignableFrom(type) || typeof(OrganizationRequest).IsAssignableFrom(type)
               || typeof(OrganizationResponse).IsAssignableFrom(type);
    }
}

/// <summary>
/// The SDK's <see cref="KnownTypesResolver"/> restricted to <see cref="SdkTypeAllowlist"/>: a type outside it, read or
/// written, is a <see cref="SerializationException"/> <c>"Type '{name}' is not allowed."</c>.
/// </summary>
public sealed class SdkTypeAllowlistResolver : DataContractResolver
{
    private readonly KnownTypesResolver _inner = new();

    /// <inheritdoc />
    /// <exception cref="SerializationException">The name resolves to a type outside the allowlist.</exception>
    public override Type? ResolveName(string typeName, string? typeNamespace, Type? declaredType, DataContractResolver knownTypeResolver)
    {
        var type = _inner.ResolveName(typeName, typeNamespace, declaredType, knownTypeResolver);
        return type is null || SdkTypeAllowlist.IsAllowed(type) ? type : throw NotAllowed(type);
    }

    /// <inheritdoc />
    /// <exception cref="SerializationException"><paramref name="type"/> is outside the allowlist.</exception>
    public override bool TryResolveType(Type type, Type? declaredType, DataContractResolver knownTypeResolver,
        out XmlDictionaryString? typeName, out XmlDictionaryString? typeNamespace)
    {
        if (!SdkTypeAllowlist.IsAllowed(type)) throw NotAllowed(type);
        return _inner.TryResolveType(type, declaredType, knownTypeResolver, out typeName, out typeNamespace);
    }

    private static SerializationException NotAllowed(Type type) => new($"Type '{type.FullName}' is not allowed.");
}
