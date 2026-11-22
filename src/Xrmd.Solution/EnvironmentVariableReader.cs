using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Solution;

/// <summary>
/// Reads the <c>environmentvariabledefinitions/</c> folder of a solution file (#167, Global Constraints "File shape"): for each
/// definition <c>environmentvariabledefinitions/{schemaname}/environmentvariabledefinition.xml</c> and, when the solution
/// carries the current value, <c>environmentvariablevalues.json</c> in the same folder. Values are child elements with text
/// (<c>0</c>/<c>1</c> flags, numeric choices); an empty optional element is no value. A file of the folder on another path is
/// not read: it stays a kept file. No database access.
/// </summary>
/// <remarks>
/// The value file holds one value with two keys, <c>value</c> and <c>iscustomizable</c> ("1" in an unmanaged export, "0" in a
/// managed one), both read and written back. xrmverse has no place for another key, so one is refused rather than dropped.
/// </remarks>
internal static class EnvironmentVariableReader
{
    internal const string Folder = "environmentvariabledefinitions";
    internal const string DefinitionFile = "environmentvariabledefinition.xml";
    internal const string ValueFile = "environmentvariablevalues.json";
    private const string Root = "environmentvariabledefinition";

    /// <summary>
    /// What xrmverse models of <c>environmentvariabledefinition</c>. <c>description</c> and <c>displayname</c> are kept as they are,
    /// so that their full label sets travel opaque (as for custom APIs); <c>iscustomizable</c> is kept (a managed property).
    /// </summary>
    internal static UnmodelledShape Shape { get; } = new(
        [EnvironmentVariableSchema.DefaultValue, EnvironmentVariableSchema.Hint, EnvironmentVariableSchema.IntroducedVersion,
            EnvironmentVariableSchema.IsRequired, EnvironmentVariableSchema.SecretStore, EnvironmentVariableSchema.Type,
            EnvironmentVariableSchema.ValueSchema],
        [EnvironmentVariableSchema.SchemaName]);

    /// <summary>
    /// The kept XML of a definition that did not come from a file: <c>iscustomizable</c> 1 and a marker for each child always
    /// written, in the export order (alphabetical, as in a Dataverse export). The optional children have no marker, so that a
    /// missing value writes no element.
    /// </summary>
    internal static XElement NewSource() => UnmodelledXml.Template(Root,
        (EnvironmentVariableSchema.IntroducedVersion, null), ("iscustomizable", "1"), (EnvironmentVariableSchema.IsRequired, null),
        (EnvironmentVariableSchema.SecretStore, null), (EnvironmentVariableSchema.Type, null));

    /// <summary>The definitions of <paramref name="files"/>, by schema name, and the paths read.</summary>
    /// <param name="files">The archive entries.</param>
    /// <param name="languageCode">The base language of the file (kept for the labels, read by <see cref="BaseLabel"/>).</param>
    /// <exception cref="XrmdFaultException">
    /// 400 InvalidArgument naming the path for a definition that is not valid XML, has another root, no <c>schemaname</c> or one
    /// that does not name its folder (ignoring case) or that another definition has (ignoring case), a flag other than 0 or 1,
    /// a <c>type</c> or <c>secretstore</c> outside its choices; and for a value file that is not the export's JSON shape or
    /// whose folder has no definition.
    /// </exception>
    public static (List<PackageEnvironmentVariable> Variables, List<string> Read) Read(IReadOnlyDictionary<string, byte[]> files, int languageCode)
    {
        ArgumentNullException.ThrowIfNull(files);
        _ = languageCode;
        var definitions = new List<(string Folder, string Path)>();
        var values = new List<(string Folder, string Path)>();
        foreach (var path in files.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            var parts = path.Split('/');
            if (parts.Length != 3 || !Is(parts[0], Folder) || parts[1].Length == 0) continue;
            if (Is(parts[2], DefinitionFile)) definitions.Add((parts[1], path));
            else if (Is(parts[2], ValueFile)) values.Add((parts[1], path));
        }

        foreach (var (folder, path) in values)
            if (!definitions.Any(d => Is(d.Folder, folder)))
                throw Invalid(path, $"belongs to no environment variable: the solution file has no '{Folder}/{folder}/{DefinitionFile}'");

        var read = new List<(PackageEnvironmentVariable Variable, string Path)>();
        foreach (var (folder, path) in definitions)
        {
            var root = SolutionZip.Xml(files[path], $"'{path}'").Root!;
            if (root.Name.LocalName != Root) throw Invalid(path, $"has the root element '{root.Name.LocalName}' instead of '{Root}'");
            var name = ((string?)root.Attribute(EnvironmentVariableSchema.SchemaName))?.Trim() is { Length: > 0 } text
                ? text : throw Invalid(path, "has no schemaname attribute");
            if (!Is(folder, name)) throw Invalid(path, $"is in the folder '{folder}' but has the schemaname '{name}'");
            if (read.FirstOrDefault(r => Is(r.Variable.SchemaName, name)) is { Path: not null } other)
                throw Invalid(path, $"has the schemaname '{name}' of '{other.Path}' (ignoring case): two environment variables cannot share it");

            var valuePath = values.FirstOrDefault(v => Is(v.Folder, folder)).Path;
            var (value, customizable) = valuePath is null ? (null, null) : Value(files[valuePath], valuePath);
            read.Add((new PackageEnvironmentVariable(
                name,
                Choice<VariableType>(root, EnvironmentVariableSchema.Type, path) ?? (int)VariableType.String,
                Text(root, EnvironmentVariableSchema.DefaultValue),
                Flag(root, EnvironmentVariableSchema.IsRequired, path) ?? false,
                SecretStore(root, path),
                Text(root, EnvironmentVariableSchema.ValueSchema),
                Text(root, EnvironmentVariableSchema.IntroducedVersion),
                Text(root, EnvironmentVariableSchema.Hint),
                value,
                valuePath is not null,
                UnmodelledXml.Capture(root, Shape)) { ValueIsCustomizable = customizable }, path));
        }
        return (read.Select(r => r.Variable).OrderBy(v => v.SchemaName, StringComparer.Ordinal).ToList(),
            [.. definitions.Select(d => d.Path), .. values.Select(v => v.Path)]);
    }

    /// <summary>
    /// The text of the <paramref name="language"/> label of the definition's <c>displayname</c> or <c>description</c> (else its
    /// <c>default</c> when it has no labels); null when it has none. These label sets travel in
    /// <see cref="PackageEnvironmentVariable.Source"/>.
    /// </summary>
    internal static string? BaseLabel(PackageEnvironmentVariable variable, string element, int language)
    {
        ArgumentNullException.ThrowIfNull(variable);
        if (variable.Source.Element(element) is not { } container || UnmodelledXml.IsMarker(container)) return null;
        return CustomApiReader.Labels(container, language).UserLocalizedLabel?.Label;
    }

    /// <summary>
    /// The value of <c>environmentvariablevalues.json</c>: <c>{"environmentvariablevalues":{"environmentvariablevalue":{…}}}</c>
    /// with a string or null <c>value</c> (a missing one is null) and <c>iscustomizable</c> "0" or "1" when given (null when not);
    /// a one-item array stands for the object.
    /// </summary>
    private static (string? Value, string? IsCustomizable) Value(byte[] content, string path)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(content).TrimStart('﻿'));
            root = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw Invalid(path, $"is not valid JSON ({e.Message})");
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("environmentvariablevalues", out var container)
            || container.ValueKind != JsonValueKind.Object || !container.TryGetProperty("environmentvariablevalue", out var item))
            throw Invalid(path, "is not an environment variable value file: it has no environmentvariablevalues.environmentvariablevalue");
        if (item.ValueKind == JsonValueKind.Array)
            item = item.GetArrayLength() == 1 ? item[0]
                : throw Invalid(path, $"has {item.GetArrayLength()} values: an environment variable has at most one value in xrmverse");
        if (item.ValueKind != JsonValueKind.Object) throw Invalid(path, "has an environmentvariablevalue that is not an object");

        string? value = null, customizable = null;
        foreach (var property in item.EnumerateObject())
        {
            switch (property.Name)
            {
                case "value" when property.Value.ValueKind == JsonValueKind.String:
                    value = property.Value.GetString();
                    break;
                case "value" when property.Value.ValueKind == JsonValueKind.Null:
                    break;
                case "value":
                    throw Invalid(path, $"has a value that is a JSON {property.Value.ValueKind.ToString().ToLowerInvariant()}, not a string");
                case "iscustomizable" when property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "0" or "1":
                    customizable = property.Value.GetString();
                    break;
                case "iscustomizable":
                    throw Invalid(path, $"has the iscustomizable {property.Value.GetRawText()}, which is not \"0\" or \"1\"");
                default:
                    throw Invalid(path, $"has the key '{property.Name}', which xrmverse does not read: a value file has only 'value' and 'iscustomizable'");
            }
        }
        return (value, customizable);
    }

    private static bool Is(string segment, string name) => string.Equals(segment, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The trimmed text of a child element; null when it is missing, empty or <c>xsi:nil</c>.</summary>
    private static string? Text(XElement parent, string name) => CustomizationsReader.Value(parent, name);

    private static int? Choice<TEnum>(XElement parent, string name, string path) where TEnum : struct, Enum
    {
        if (Text(parent, name) is not { } text) return null;
        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) && Enum.IsDefined(typeof(TEnum), number))
            return number;
        throw Invalid(path, $"has the {name} '{text}', which is not one of its choices ({string.Join(", ", Enum.GetValues<TEnum>().Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)))})");
    }

    /// <summary>The <c>secretstore</c>: 0 Azure Key Vault (also when missing), 1 Microsoft Dataverse.</summary>
    private static int SecretStore(XElement parent, string path) => Text(parent, EnvironmentVariableSchema.SecretStore) switch
    {
        null or "0" => 0,
        "1" => 1,
        var text => throw Invalid(path, $"has the secretstore '{text}', which is not one of its choices (0, 1)"),
    };

    private static bool? Flag(XElement parent, string name, string path) => Text(parent, name) switch
    {
        null => null,
        "0" => false,
        "1" => true,
        var value => throw Invalid(path, $"has the {name} '{value}', which is not 0 or 1"),
    };

    private static XrmdFaultException Invalid(string path, string problem) =>
        XrmdFaultException.InvalidArgument($"The solution file entry '{path}' {problem}.");
}
