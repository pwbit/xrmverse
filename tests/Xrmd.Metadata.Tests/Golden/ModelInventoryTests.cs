using System.Collections;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Model;

namespace Xrmd.Metadata.Tests.Golden;

/// <summary>
/// S.2b1, Task 3: the own metadata model (Xrmd.Model) covers every public SDK metadata type reachable from
/// EntityMetadata and the four polymorphic roots, with the same property names, enum members and abstractness.
/// The model types are reached by their <see cref="WireTypeAttribute"/> (the SDK type name), never by CLR name.
/// </summary>
public sealed class ModelInventoryTests
{
    private static readonly Assembly SdkAssembly = typeof(EntityMetadata).Assembly;
    private static readonly Assembly ModelAssembly = typeof(WireTypeAttribute).Assembly;

    private static readonly Type[] Roots =
    [
        typeof(EntityMetadata), typeof(AttributeMetadata), typeof(OptionSetMetadataBase),
        typeof(RelationshipMetadataBase), typeof(OptionMetadata),
    ];

    // The one SDK property whose type is deliberately not modelled: EntitySetting.Value is an Entity (a data record, not
    // metadata). The model keeps the property name but holds the raw JSON value. Neither the reachability walk nor the
    // type comparison descends into Entity.
    private static readonly (string Type, string Property) OpaqueProperty = ("EntitySetting", "Value");
    private const string OpaqueSdkType = "Entity";

    /// <summary>Every public SDK type reachable through public properties (collections count through their elements).</summary>
    private static IReadOnlyList<Type> Reachable()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        void Add(Type t) { if (t.Assembly == SdkAssembly && !IsCollection(t) && seen.Add(t)) queue.Enqueue(t); }
        void Walk(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (t.IsArray) { Walk(t.GetElementType()!); return; }
            if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) Walk(a);
            if (IsCollection(t) && ElementOf(t) is { } element) Walk(element);
            Add(t);
        }
        foreach (var root in Roots)
        {
            Add(root);
            foreach (var sub in SdkAssembly.GetTypes().Where(x => x.IsPublic && root.IsAssignableFrom(x))) Add(sub);
        }
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (type.IsEnum) continue;
            foreach (var property in PublicProperties(type))
                if (!IsOpaque(type, property)) Walk(property.PropertyType);
        }
        return seen.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    private static bool IsOpaque(Type owner, PropertyInfo property) =>
        owner.Name == OpaqueProperty.Type && property.Name == OpaqueProperty.Property;

    private static IEnumerable<PropertyInfo> PublicProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.PropertyType.Name != "ExtensionDataObject" && p.Name != "ExtensionData");

    private static bool IsCollection(Type t) =>
        t != typeof(string) && t != typeof(object) && (t.IsArray || typeof(IEnumerable).IsAssignableFrom(t));

    // KeyAttributeCollection only exposes IEnumerable<KeyValuePair<,>>, so a key/value collection is recognised by its element type.
    private static bool IsDictionary(Type t) =>
        IsCollection(t) && ElementOf(t) is { IsGenericType: true } element && element.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);

    private static Type? ElementOf(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        var generic = t.GetInterfaces().Concat([t]).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return generic?.GetGenericArguments()[0];
    }

    private static Dictionary<string, Type> ModelTypes()
    {
        var result = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in ModelAssembly.GetTypes().Where(t => t.IsPublic))
        {
            var wire = type.GetCustomAttribute<WireTypeAttribute>();
            if (wire is null) continue;
            result.TryAdd(wire.Name, type).Should().BeTrue($"WireType \"{wire.Name}\" must be unique (second: {type.Name})");
        }
        return result;
    }

    [Fact]
    public void Every_sdk_metadata_type_has_a_model_type()
    {
        var model = ModelTypes();
        var missing = Reachable().Where(t => !model.ContainsKey(t.Name)).Select(t => t.Name).ToList();
        missing.Should().BeEmpty("every reachable SDK metadata type needs a model type with [WireType(sdkName)]; missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void Model_types_have_the_same_property_names()
    {
        var model = ModelTypes();
        var problems = new List<string>();
        foreach (var sdk in Reachable().Where(t => !t.IsEnum && model.ContainsKey(t.Name)))
        {
            var own = model[sdk.Name];
            var sdkNames = PublicProperties(sdk).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var ownNames = PublicProperties(own).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var absent = sdkNames.Except(ownNames).ToList();
            var extra = ownNames.Except(sdkNames).ToList();
            if (absent.Count > 0) problems.Add($"{own.Name} lacks: {string.Join(", ", absent)}");
            if (extra.Count > 0) problems.Add($"{own.Name} has extra: {string.Join(", ", extra)}");
        }
        problems.Should().BeEmpty();
    }

    [Fact]
    public void Model_properties_have_matching_types()
    {
        var model = ModelTypes();
        var problems = new List<string>();
        foreach (var sdk in Reachable().Where(t => !t.IsEnum && model.ContainsKey(t.Name)))
        {
            var own = model[sdk.Name];
            var ownProperties = PublicProperties(own).ToDictionary(p => p.Name);
            foreach (var property in PublicProperties(sdk))
            {
                if (!ownProperties.TryGetValue(property.Name, out var mine)) continue; // reported by the name test
                var where = $"{own.Name}.{property.Name}";
                if (!(mine.CanRead && mine.CanWrite && mine.GetMethod!.IsPublic && mine.SetMethod!.IsPublic)) problems.Add(where + ": not public get/set");
                if (IsOpaque(sdk, property))
                {
                    // The explicit exception: Entity is held as raw JSON.
                    if (property.PropertyType.Name != OpaqueSdkType) problems.Add(where + ": exception no longer applies");
                    if (mine.PropertyType != typeof(JsonElement?)) problems.Add($"{where}: expected JsonElement?, found {mine.PropertyType.Name}");
                    continue;
                }
                if (Mismatch(property.PropertyType, mine.PropertyType, model) is { } why) problems.Add($"{where}: {why}");
            }
        }
        problems.Should().BeEmpty();
    }

    /// <summary>Null when the model type stands for the SDK type: same primitives, WireType-matched classes and enums, List of elements for collections.</summary>
    private static string? Mismatch(Type sdk, Type own, Dictionary<string, Type> model)
    {
        var sdkNullable = Nullable.GetUnderlyingType(sdk);
        var ownNullable = Nullable.GetUnderlyingType(own);
        if ((sdkNullable is null) != (ownNullable is null)) return $"nullability differs ({sdk.Name} vs {own.Name})";
        sdk = sdkNullable ?? sdk;
        own = ownNullable ?? own;

        // A SDK key/value collection (KeyAttributeCollection, an IDictionary) holds opaque values: the model uses
        // Dictionary<string, JsonElement> because data records are not modelled here (they come later).
        if (IsDictionary(sdk))
            return own == typeof(Dictionary<string, JsonElement>) ? null : $"a dictionary must be Dictionary<string, JsonElement> (found {own.Name})";
        if (IsCollection(sdk))
        {
            if (!own.IsGenericType || own.GetGenericTypeDefinition() != typeof(List<>)) return $"a collection must be List<T> (found {own.Name})";
            var element = ElementOf(sdk)!;
            return Mismatch(element, own.GetGenericArguments()[0], model);
        }
        if (sdk.Assembly == SdkAssembly)
        {
            if (!model.TryGetValue(sdk.Name, out var expected)) return $"no model type for {sdk.Name}";
            return expected == own ? null : $"expected {expected.Name} for {sdk.Name}, found {own.Name}";
        }
        return sdk == own ? null : $"expected {sdk.Name}, found {own.Name}";
    }

    [Fact]
    public void Model_types_mirror_the_sdk_abstractness_and_hierarchy()
    {
        var model = ModelTypes();
        var problems = new List<string>();
        foreach (var sdk in Reachable().Where(t => !t.IsEnum && model.ContainsKey(t.Name)))
        {
            var own = model[sdk.Name];
            if (sdk.IsAbstract != own.IsAbstract) problems.Add($"{own.Name}: abstract = {own.IsAbstract}, SDK {sdk.Name} abstract = {sdk.IsAbstract}");
            // Where the SDK base class is itself modelled, the model derives from the matching model type.
            if (sdk.BaseType is { } baseType && model.TryGetValue(baseType.Name, out var ownBase) && own.BaseType != ownBase)
                problems.Add($"{own.Name}: base {own.BaseType?.Name}, expected {ownBase.Name}");
        }
        problems.Should().BeEmpty();
    }

    [Fact]
    public void Model_enums_have_the_same_members()
    {
        var model = ModelTypes();
        var problems = new List<string>();
        foreach (var sdk in Reachable().Where(t => t.IsEnum && model.ContainsKey(t.Name)))
        {
            var own = model[sdk.Name];
            if (!own.IsEnum) { problems.Add($"{own.Name} must be an enum"); continue; }
            var expected = Members(sdk);
            var actual = Members(own);
            if (!expected.SequenceEqual(actual)) problems.Add($"{own.Name}: expected [{string.Join(", ", expected.Select(m => m.Key + "=" + m.Value))}] found [{string.Join(", ", actual.Select(m => m.Key + "=" + m.Value))}]");
        }
        problems.Should().BeEmpty();
    }

    private static List<KeyValuePair<string, long>> Members(Type enumType) =>
        Enum.GetNames(enumType).Select(n => new KeyValuePair<string, long>(n, Convert.ToInt64(Enum.Parse(enumType, n))))
            .OrderBy(m => m.Key, StringComparer.Ordinal).ToList();

    [Fact]
    public void No_model_type_shares_a_name_with_an_sdk_type()
    {
        var sdkNames = SdkAssembly.GetTypes().Where(t => t.IsPublic).Select(t => t.Name.Split('`')[0]).ToHashSet(StringComparer.Ordinal);
        var clashes = ModelAssembly.GetTypes().Where(t => t.IsPublic && sdkNames.Contains(t.Name.Split('`')[0])).Select(t => t.Name).ToList();
        clashes.Should().BeEmpty("model type names must not equal a public SDK type name");
    }
}
