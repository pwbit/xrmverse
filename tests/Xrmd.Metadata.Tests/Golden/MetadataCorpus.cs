using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests.Golden;

/// <summary>
/// The golden metadata corpus (plan S.2b1, Task 2): the JSON the current serializer writes over the SDK metadata types.
/// <see cref="Entries"/> reads the committed files; <see cref="Generate"/> rebuilds the same entries in memory from the
/// seed tables and from reflection-filled instances (every public property set, any setter visibility).
/// </summary>
public static class MetadataCorpus
{
    public const string Table = "table";
    public const string Column = "column";
    public const string Choiceset = "choiceset";
    public const string Relationship = "relationship";

    private const int MaxDepth = 5;
    private const string SeedPrefix = "Xrmd.Metadata.Seed.";

    private static readonly Assembly SdkAssembly = typeof(EntityMetadata).Assembly;

    // Preferred concrete types of each polymorphic root, in the order a single member / the two elements of a collection take them.
    private static readonly Dictionary<Type, string[]> Preferred = new()
    {
        [typeof(AttributeMetadata)] = ["StringAttributeMetadata", "LookupAttributeMetadata"],
        [typeof(OptionSetMetadataBase)] = ["OptionSetMetadata", "BooleanOptionSetMetadata"],
        [typeof(RelationshipMetadataBase)] = ["OneToManyRelationshipMetadata", "ManyToManyRelationshipMetadata"],
        [typeof(OptionMetadata)] = ["OptionMetadata", "StateOptionMetadata"],
    };

    /// <summary>
    /// The subdirectory of the stored-row golden files (S.2b2, <c>Stored_json_is_unchanged</c> in the integration tests): not
    /// part of this corpus, never rewritten by its update mode.
    /// </summary>
    public const string StoredPrefix = "stored/";

    /// <summary>The directory of the committed golden files (copied to the test output).</summary>
    public static string GoldenDirectory => Path.Combine(AppContext.BaseDirectory, "fixtures", "metadata-golden");

    /// <summary>The directory of the golden files in the source tree (written by the update mode).</summary>
    public static string SourceDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("xrmverse.sln not found."),
                "tests", "fixtures", "metadata-golden");
        }
    }

    /// <summary>Types the reflection could not fill or instantiate, with the reason (filled by <see cref="Generate"/>).</summary>
    public static IReadOnlyList<string> Skipped => SkippedList;

    private static readonly List<string> SkippedList = [];

    // The types being filled right now: a type nested in itself more than once is cut (EntitySetting.ChildSettings).
    private static readonly List<Type> Filling = [];
    private static readonly object Gate = new();

    /// <summary>The committed golden files: name (<c>tables/account</c>, <c>full/StringAttributeMetadata</c>), root and exact text.</summary>
    public static IReadOnlyList<(string Name, string Root, string Json)> Entries => _entries.Value;

    private static readonly Lazy<List<(string, string, string)>> _entries = new(Load);

    private static List<(string, string, string)> Load()
    {
        var root = GoldenDirectory;
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Golden directory not found: {root}");
        return Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Select(rel => rel[..^".json".Length])
            .Where(n => !n.StartsWith(StoredPrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => (n, RootOf(n), File.ReadAllText(Path.Combine(root, n + ".json"))))
            .ToList();
    }

    private static string RootOf(string name)
    {
        if (name.StartsWith("tables/", StringComparison.Ordinal)) return Table;
        var type = name["full/".Length..];
        var dot = type.IndexOf('.');
        if (dot >= 0) type = type[..dot];
        var sdk = SdkAssembly.GetType("Microsoft.Xrm.Sdk.Metadata." + type)
                  ?? throw new InvalidOperationException($"No SDK type for {name}.");
        if (sdk == typeof(EntityMetadata)) return Table;
        if (typeof(AttributeMetadata).IsAssignableFrom(sdk)) return Column;
        if (typeof(OptionSetMetadataBase).IsAssignableFrom(sdk) || typeof(OptionMetadata).IsAssignableFrom(sdk)) return Choiceset;
        if (typeof(RelationshipMetadataBase).IsAssignableFrom(sdk)) return Relationship;
        throw new InvalidOperationException($"No root for {name}.");
    }

    /// <summary>Rebuilds every entry in memory with the current serializer.</summary>
    public static IReadOnlyList<(string Name, string Root, string Json)> Generate()
    {
        lock (Gate) return GenerateCore();
    }

    private static IReadOnlyList<(string Name, string Root, string Json)> GenerateCore()
    {
        SkippedList.Clear();
        Filling.Clear();
        var result = new List<(string, string, string)>();

        var seedAssembly = typeof(MetadataSerializer).Assembly;
        foreach (var resource in seedAssembly.GetManifestResourceNames()
                     .Where(r => r.StartsWith(SeedPrefix, StringComparison.Ordinal) && r.EndsWith(".json", StringComparison.Ordinal))
                     .OrderBy(r => r, StringComparer.Ordinal))
        {
            using var stream = seedAssembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var entity = MetadataSerializer.Deserialize(reader.ReadToEnd());
            var name = resource[SeedPrefix.Length..^".json".Length];
            result.Add(("tables/" + name, Table, MetadataSerializer.Serialize(entity)));
        }

        foreach (var mode in new[] { "", ".empty", ".omitted" })
        {
            var empty = mode == ".empty";
            var omitted = mode == ".omitted";
            var suffix = mode;
            result.Add(("full/EntityMetadata" + suffix, Table, MetadataSerializer.Serialize((EntityMetadata)(omitted ? Omitted(typeof(EntityMetadata)) : Full(typeof(EntityMetadata), empty)))));
            foreach (var type in Concrete(typeof(AttributeMetadata)))
                if (TryFull(type, empty, omitted) is AttributeMetadata a)
                    result.Add(("full/" + type.Name + suffix, Column, MetadataSerializer.SerializeAttribute(a)));
            foreach (var type in Concrete(typeof(OptionSetMetadataBase)))
                if (TryFull(type, empty, omitted) is OptionSetMetadataBase s)
                    result.Add(("full/" + type.Name + suffix, Choiceset, MetadataSerializer.SerializeOptionSet(s)));
            foreach (var type in Concrete(typeof(RelationshipMetadataBase)))
                if (TryFull(type, empty, omitted) is RelationshipMetadataBase r)
                    result.Add(("full/" + type.Name + suffix, Relationship, MetadataSerializer.SerializeRelationship(r)));
            foreach (var type in Concrete(typeof(OptionMetadata)))
                if (TryFull(type, empty, omitted) is OptionMetadata o)
                {
                    var wrapper = new OptionSetMetadata { Name = "Wrapper." + type.Name };
                    wrapper.Options.Add(o);
                    result.Add(("full/" + type.Name + suffix, Choiceset, MetadataSerializer.SerializeOptionSet(wrapper)));
                }
        }

        return result.OrderBy(e => e.Item1, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<Type> Concrete(Type root) =>
        SdkAssembly.GetTypes()
            .Where(t => t.IsPublic && !t.IsAbstract && root.IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    private static object? TryFull(Type type, bool empty, bool omitted)
    {
        try { return omitted ? Omitted(type) : Full(type, empty); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SkippedList.Add($"{type.Name}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static readonly string[] IdentifyingMembers = ["LogicalName", "SchemaName", "Name", "MetadataId"];

    /// <summary>A bare instance with only the identifying members set: every label, collection and nested object stays as the constructor left it (null).</summary>
    private static object Omitted(Type type)
    {
        var instance = Instantiate(type, 0, false, type.Name);
        foreach (var member in IdentifyingMembers)
        {
            var property = type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || SetterOf(property) is not { } setter) continue;
            if (Value(property.PropertyType, type.Name + "." + member, type.Name, 0, 0, false, 0, "") is { } value)
                setter.Invoke(instance, [value]);
        }
        return instance;
    }

    private static object Full(Type type, bool empty)
    {
        var instance = Instantiate(type, 0, empty, type.Name);
        Fill(instance, type, 0, empty, "");
        return instance;
    }

    private static object Instantiate(Type type, int depth, bool empty, string owner)
    {
        var ctor = type.GetConstructor(Type.EmptyTypes);
        if (ctor is not null) return ctor.Invoke(null);
        ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes);
        if (ctor is not null) return ctor.Invoke(null);
        ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).FirstOrDefault()
               ?? throw new InvalidOperationException("no public constructor");
        var args = ctor.GetParameters().Select(p => Value(p.ParameterType, owner + "." + p.Name, owner, 0, depth + 1, empty, 0, "")).ToArray();
        return ctor.Invoke(args);
    }

    private static MethodInfo? SetterOf(PropertyInfo property) =>
        property.GetSetMethod(nonPublic: true)
        ?? property.DeclaringType?.GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod(nonPublic: true);

    private static void Fill(object target, Type type, int depth, bool empty, string tag)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetGetMethod() is not null && p.PropertyType.Name != "ExtensionDataObject")
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < properties.Count; i++)
        {
            var property = properties[i];
            var where = type.Name + "." + property.Name;
            var setter = SetterOf(property);
            try
            {
                if (setter is not null)
                {
                    var value = Value(property.PropertyType, where, type.Name, i, depth + 1, empty, 0, tag);
                    if (value is null) { SkippedList.Add($"{where}: no value for {property.PropertyType.Name}"); continue; }
                    setter.Invoke(target, [value]);
                }
                else
                {
                    // Read-only: fill a mutable collection the object owns, skip everything else.
                    var current = property.GetValue(target);
                    var element = CollectionElement(property.PropertyType);
                    if (current is null || element is null || current is Array || IsReadOnly(current))
                    {
                        SkippedList.Add($"{where}: read-only, not a mutable collection");
                        continue;
                    }
                    if (!empty) AddElements(current, element, where, type.Name, depth + 1, empty, tag);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                SkippedList.Add($"{where}: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            }
        }
    }

    private static bool IsReadOnly(object collection) =>
        collection.GetType().GetProperty("IsReadOnly")?.GetValue(collection) is true;

    private static Type? CollectionElement(Type type)
    {
        if (type == typeof(string) || typeof(IDictionary).IsAssignableFrom(type)) return null;
        if (type.IsArray) return type.GetElementType();
        var generic = type.IsGenericType && type.GetGenericTypeDefinition() is var d
                      && (d == typeof(IEnumerable<>) || d == typeof(ICollection<>) || d == typeof(IList<>) || d == typeof(List<>))
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>));
        return generic?.GetGenericArguments()[0];
    }

    private static void AddElements(object collection, Type element, string where, string owner, int depth, bool empty, string tag)
    {
        var add = typeof(ICollection<>).MakeGenericType(element).GetMethod("Add")!;
        for (var n = 0; n < 2; n++)
        {
            var value = Value(element, where, owner, n, depth, empty, n, tag + (n == 0 ? "" : "#2"));
            if (value is not null) add.Invoke(collection, [value]);
        }
    }

    private static object? Value(Type type, string name, string owner, int index, int depth, bool empty, int slot, string tag)
    {
        var bump = tag.Length;
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string)) return name + tag;
        if (underlying == typeof(bool)) return true;
        if (underlying == typeof(int)) return index + 1 + bump;
        if (underlying == typeof(long)) return (long)index + 1 + bump;
        if (underlying == typeof(short)) return (short)(index + 1 + bump);
        if (underlying == typeof(byte)) return (byte)(index + 1 + bump);
        if (underlying == typeof(double)) return index + 1.5 + bump;
        if (underlying == typeof(float)) return index + 1.5f + bump;
        if (underlying == typeof(decimal)) return index + 1.5m + bump;
        if (underlying == typeof(Guid)) return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(name + tag)));
        if (underlying == typeof(DateTime)) return new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        if (underlying == typeof(DateTimeOffset)) return new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        if (underlying.IsEnum)
            return Enum.Parse(underlying, underlying.GetFields(BindingFlags.Public | BindingFlags.Static).Last().Name);

        if (depth > MaxDepth) return null;

        var element = CollectionElement(type);
        if (element is not null)
        {
            if (type.IsArray)
            {
                if (empty) return Array.CreateInstance(element, 0);
                var items = new List<object>();
                for (var n = 0; n < 2; n++)
                    if (Value(element, name, owner, n, depth, empty, n, tag + (n == 0 ? "" : "#2")) is { } v) items.Add(v);
                var array = Array.CreateInstance(element, items.Count);
                for (var n = 0; n < items.Count; n++) array.SetValue(items[n], n);
                return array;
            }
            var listType = type.IsInterface || type.IsAbstract ? typeof(List<>).MakeGenericType(element) : type;
            if (listType.GetConstructor(Type.EmptyTypes) is null) return null;
            var list = Activator.CreateInstance(listType)!;
            if (!empty) AddElements(list, element, name, owner, depth, empty, tag);
            return list;
        }

        if (!type.IsClass) return null;
        if (type == typeof(Label) && empty) return new Label();

        var concrete = type;
        if (Preferred.TryGetValue(type, out var names))
            concrete = SdkAssembly.GetType("Microsoft.Xrm.Sdk.Metadata." + names[Math.Min(slot, names.Length - 1)])!;
        else if (type.IsAbstract)
            concrete = Concrete(type).First();
        if (type == typeof(object) || type.IsAbstract && concrete == type) return null;

        // Entity is a data record, not metadata: it is reached through EntitySetting.Value and left out.
        if (concrete == typeof(Entity)) { SkippedList.Add($"{name}: Entity (a data record) is not filled"); return null; }
        if (Filling.Count(t => t == concrete) >= 2) return null;

        var instance = Instantiate(concrete, depth, empty, concrete.Name);
        Filling.Add(concrete);
        try { Fill(instance, concrete, depth, empty, tag); }
        finally { Filling.RemoveAt(Filling.Count - 1); }
        return instance;
    }
}
