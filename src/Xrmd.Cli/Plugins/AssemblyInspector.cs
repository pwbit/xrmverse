using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xrmd.Abstractions.Sdk;

namespace Xrmd.Cli.Plugins;

/// <summary>What <see cref="AssemblyInspector.Inspect"/> reads from a plug-in assembly.</summary>
/// <param name="Name">Simple assembly name.</param>
/// <param name="Version">Assembly version.</param>
/// <param name="Culture"><c>neutral</c> or the culture name.</param>
/// <param name="PublicKeyToken">Lower-case hex public key token; empty for an unsigned assembly.</param>
/// <param name="PluginTypes">Full names of the public, concrete classes that implement <c>Microsoft.Xrm.Sdk.IPlugin</c>.</param>
public sealed record InspectedAssembly(string Name, Version Version, string Culture, string PublicKeyToken, IReadOnlyList<string> PluginTypes);

/// <summary>
/// Reads the identity and the <c>IPlugin</c> types of an assembly with a <see cref="MetadataLoadContext"/>: nothing of it is
/// loaded into the default context and none of its code runs. References are resolved from the assembly's directory and the
/// runtime directory; <c>IPlugin</c> is recognised by its full name, so the exact version of the SDK assembly does not matter.
/// </summary>
public static class AssemblyInspector
{
    private const string PluginInterface = "Microsoft.Xrm.Sdk.IPlugin";

    public static InspectedAssembly Inspect(string path) => Inspect(path, withSdk: true);

    /// <param name="path">The plug-in assembly.</param>
    /// <param name="withSdk">False leaves every <c>Microsoft.Xrm.Sdk.dll</c> out of the resolver paths (forces the metadata fallback; used by tests).</param>
    internal static InspectedAssembly Inspect(string path, bool withSdk) =>
        Inspect(path, withSdk, AppContext.BaseDirectory, SdkAssemblyResolver.Folder ?? SdkFolder.Resolve(null));

    /// <summary>The copies of <c>Microsoft.Xrm.Sdk.dll</c> the resolver may use: the CLI's own folder (tests), then the SDK folder.</summary>
    internal static IReadOnlyList<string> SdkCandidates(string appDirectory, string sdkFolder) =>
        new[] { appDirectory, sdkFolder }
            .Select(folder => Path.Combine(folder, "Microsoft.Xrm.Sdk.dll"))
            .Where(File.Exists)
            .ToList();

    /// <param name="path">The plug-in assembly.</param>
    /// <param name="withSdk">See <see cref="Inspect(string, bool)"/>.</param>
    /// <param name="appDirectory">The CLI's folder (<see cref="AppContext.BaseDirectory"/>).</param>
    /// <param name="sdkFolder">The SDK folder (<c>XRMD_SDK_DIR</c>, else <c>~/.xrmd/sdk</c>).</param>
    internal static InspectedAssembly Inspect(string path, bool withSdk, string appDirectory, string sdkFolder)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new CliException(ExitCodes.Usage, $"File '{full}' not found.");

        // In a single-file app the runtime assemblies are inside the bundle, so a MetadataLoadContext has no core assembly
        // to resolve: the assembly is then read from its metadata alone.
#pragma warning disable IL3000 // Location is empty in single-file apps; that is what is tested here.
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
#pragma warning restore IL3000
        if (string.IsNullOrEmpty(runtimeDir)) return InspectByMetadata(full);

        var paths = new List<string>(Directory.GetFiles(Path.GetDirectoryName(full)!, "*.dll"));
        paths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));
        // The SDK assembly is usually not next to a plug-in: use the one installed in the SDK folder (or, in tests, the copy
        // next to the CLI). Without any copy, the metadata fallback below decides.
        paths.AddRange(SdkCandidates(appDirectory, sdkFolder));
        if (!paths.Contains(full, StringComparer.OrdinalIgnoreCase)) paths.Add(full);
        if (!withSdk) paths.RemoveAll(p => string.Equals(Path.GetFileName(p), "Microsoft.Xrm.Sdk.dll", StringComparison.OrdinalIgnoreCase));

        try
        {
            using var context = new MetadataLoadContext(new PathAssemblyResolver(paths.DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)), coreAssemblyName: CoreAssembly(full));
            var assembly = context.LoadFromAssemblyPath(full);
            var name = assembly.GetName();
            var token = name.GetPublicKeyToken();
            var types = new List<string>();
            foreach (var type in SafeTypes(assembly))
            {
                if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition || !type.IsVisible) continue;
                if (ImplementsPlugin(type)) types.Add(type.FullName!);
            }

            types.Sort(StringComparer.Ordinal);
            return new InspectedAssembly(name.Name ?? Path.GetFileNameWithoutExtension(full), name.Version ?? new Version(0, 0, 0, 0),
                string.IsNullOrEmpty(name.CultureName) ? "neutral" : name.CultureName,
                token is { Length: > 0 } ? Convert.ToHexString(token).ToLowerInvariant() : "", types);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or InvalidOperationException or ArgumentException)
        {
            throw new CliException(ExitCodes.Usage, $"'{full}' is not a .NET assembly: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The same result as <see cref="Inspect(string, bool)"/> from the PE metadata only (no type loading): used where there
    /// is no runtime directory to resolve a core assembly from (single-file executables).
    /// </summary>
    internal static InspectedAssembly InspectByMetadata(string full)
    {
        try
        {
            using var pe = new PEReader(File.OpenRead(full));
            if (!pe.HasMetadata) throw new BadImageFormatException("no .NET metadata.");
            var reader = pe.GetMetadataReader();
            var definition = reader.GetAssemblyDefinition();
            var name = definition.GetAssemblyName();
            var token = name.GetPublicKeyToken();
            var types = new List<string>();
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                var attributes = type.Attributes;
                if ((attributes & TypeAttributes.Interface) != 0 || (attributes & TypeAttributes.Abstract) != 0) continue;
                if (type.GetGenericParameters().Count > 0 || !IsVisible(reader, type)) continue;
                if (Implements(reader, handle, new HashSet<TypeDefinitionHandle>())) types.Add(FullName(reader, type));
            }

            types.Sort(StringComparer.Ordinal);
            return new InspectedAssembly(name.Name ?? Path.GetFileNameWithoutExtension(full), name.Version ?? new Version(0, 0, 0, 0),
                string.IsNullOrEmpty(name.CultureName) ? "neutral" : name.CultureName,
                token is { Length: > 0 } ? Convert.ToHexString(token).ToLowerInvariant() : "", types);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            throw new CliException(ExitCodes.Usage, $"'{full}' is not a .NET assembly: {ex.Message}", ex);
        }
    }

    private static bool IsVisible(MetadataReader reader, TypeDefinition type)
    {
        while (true)
        {
            var visibility = type.Attributes & TypeAttributes.VisibilityMask;
            if (visibility == TypeAttributes.Public) return true;
            if (visibility != TypeAttributes.NestedPublic) return false;
            type = reader.GetTypeDefinition(type.GetDeclaringType());
        }
    }

    /// <summary>The core assembly a plug-in references (<c>mscorlib</c> for .NET Framework builds, else <c>System.Private.CoreLib</c>).</summary>
    private static string CoreAssembly(string path)
    {
        try
        {
            using var pe = new PEReader(File.OpenRead(path));
            if (!pe.HasMetadata) return "System.Private.CoreLib";
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.AssemblyReferences)
            {
                var name = reader.GetString(reader.GetAssemblyReference(handle).Name);
                if (name is "mscorlib" or "System.Runtime" or "netstandard" or "System.Private.CoreLib") return name;
            }
        }
        catch (BadImageFormatException)
        {
            // reported by the caller with the load error
        }

        return "System.Private.CoreLib";
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }

    private static bool ImplementsPlugin(Type type)
    {
        try
        {
            return type.GetInterfaces().Any(i => i.FullName == PluginInterface);
        }
        catch (FileNotFoundException)
        {
            // A reference (typically Microsoft.Xrm.Sdk) cannot be resolved: decide from the names recorded in the metadata.
            return ImplementsPluginByMetadata(type);
        }
    }

    /// <summary>
    /// Reads the type definitions of the assembly directly: a type implements <c>IPlugin</c> when it, an interface it declares
    /// (in this assembly), or a base class that is defined in this assembly declares an interface reference named
    /// <c>Microsoft.Xrm.Sdk.IPlugin</c>. A base class in another assembly cannot be followed without resolving it.
    /// </summary>
    private static bool ImplementsPluginByMetadata(Type type)
    {
        // type.Assembly comes from the MetadataLoadContext: its Location is the file on disk, also in a single-file app.
#pragma warning disable IL3000
        var assemblyPath = type.Assembly.Location;
#pragma warning restore IL3000
        using var pe = new PEReader(File.OpenRead(assemblyPath));
        var reader = pe.GetMetadataReader();
        var definitions = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
        foreach (var handle in reader.TypeDefinitions)
            definitions[FullName(reader, reader.GetTypeDefinition(handle))] = handle;

        return definitions.TryGetValue(type.FullName!, out var start) && Implements(reader, start, new HashSet<TypeDefinitionHandle>());
    }

    private static bool Implements(MetadataReader reader, TypeDefinitionHandle handle, HashSet<TypeDefinitionHandle> seen)
    {
        if (!seen.Add(handle)) return false;
        var definition = reader.GetTypeDefinition(handle);
        foreach (var implementation in definition.GetInterfaceImplementations())
        {
            var @interface = reader.GetInterfaceImplementation(implementation).Interface;
            switch (@interface.Kind)
            {
                case HandleKind.TypeReference:
                    var reference = reader.GetTypeReference((TypeReferenceHandle)@interface);
                    if (reader.GetString(reference.Namespace) + "." + reader.GetString(reference.Name) == PluginInterface) return true;
                    break;
                case HandleKind.TypeDefinition when Implements(reader, (TypeDefinitionHandle)@interface, seen):
                    return true;
            }
        }

        return definition.BaseType.Kind == HandleKind.TypeDefinition && Implements(reader, (TypeDefinitionHandle)definition.BaseType, seen);
    }

    private static string FullName(MetadataReader reader, TypeDefinition definition)
    {
        var name = reader.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil) return FullName(reader, reader.GetTypeDefinition(declaring)) + "+" + name;
        return reader.GetString(definition.Namespace) is { Length: > 0 } ns ? ns + "." + name : name;
    }
}
