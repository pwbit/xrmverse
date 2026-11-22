using System.Globalization;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;

namespace Xrmd.Solution;

/// <summary>
/// Reads the <c>customapis/</c> folder of a solution file (plan 15, Global Constraints "Solution file shape"): for each API
/// <c>customapis/{api}/customapi.xml</c>, its request parameters at
/// <c>customapis/{api}/customapirequestparameters/{name}/customapirequestparameter.xml</c> and its response properties at
/// <c>customapis/{api}/customapiresponseproperties/{name}/customapiresponseproperty.xml</c>. Values are child elements with
/// text (<c>0</c>/<c>1</c> flags, numeric choices); an empty optional element is no value. The ranges, names and
/// references are checked by the import, not here. A file under <c>customapis/</c> on another path is not read: it stays a
/// kept file. No database access.
/// </summary>
/// <remarks>
/// Accepted edge (review of task 2, minor 3): a <c>description</c> or <c>displayname</c> with a <c>default</c> but no
/// <c>label</c> is read as a base-language label and written back with that <c>label</c> added, and a <c>default</c> that
/// differs from the base-language label is written as the label; real exports always carry matching labels.
/// </remarks>
internal static class CustomApiReader
{
    internal const string Folder = "customapis";
    internal const string ApiFile = "customapi.xml";
    internal const string ParametersFolder = "customapirequestparameters";
    internal const string ParameterFile = "customapirequestparameter.xml";
    internal const string PropertiesFolder = "customapiresponseproperties";
    internal const string PropertyFile = "customapiresponseproperty.xml";

    /// <summary>The export order of an API's children (Global Constraints).</summary>
    internal static readonly string[] ApiOrder =
    [
        "allowedcustomprocessingsteptype", "bindingtype", "boundentitylogicalname", "description", "displayname", "executeprivilegename",
        "iscustomizable", "isfunction", "isprivate", "name", "plugintypeid", "workflowsdkstepenabled",
    ];

    /// <summary>The export order of a request parameter's or response property's children.</summary>
    internal static readonly string[] FieldOrder =
        ["description", "displayname", "iscustomizable", "isoptional", "logicalentityname", "name", "type"];

    /// <summary>
    /// What xrmverse models of <c>customapi</c>. <c>description</c> and <c>displayname</c> are read but kept as they are, so
    /// that their full label sets travel opaque (C11); <c>iscustomizable</c> is kept (a managed property, C11).
    /// </summary>
    internal static UnmodelledShape ApiShape { get; } = new(
        ["allowedcustomprocessingsteptype", "bindingtype", "boundentitylogicalname", "executeprivilegename", "isfunction", "isprivate",
            "name", "plugintypeid", "workflowsdkstepenabled"],
        ["uniquename"]);

    /// <summary>What xrmverse models of <c>customapirequestparameter</c>.</summary>
    internal static UnmodelledShape ParameterShape { get; } = new(["isoptional", "logicalentityname", "name", "type"], ["uniquename"]);

    /// <summary>What xrmverse models of <c>customapiresponseproperty</c> (no <c>isoptional</c>: a file's one stays kept).</summary>
    internal static UnmodelledShape PropertyShape { get; } = new(["logicalentityname", "name", "type"], ["uniquename"]);

    /// <summary>The APIs of <paramref name="files"/>, by unique name, and the paths read.</summary>
    /// <param name="files">The archive entries.</param>
    /// <param name="languageCode">The base language of the file (the <c>UserLocalizedLabel</c> of the labels).</param>
    /// <exception cref="XrmdFaultException">
    /// 400 InvalidArgument naming the path for a file that is not valid XML, has another root, no <c>uniquename</c>, no
    /// <c>name</c> or <c>type</c>, a flag other than 0 or 1, a non-numeric choice or plug-in type id; and for a parameter or
    /// property folder whose API has no <c>customapi.xml</c>.
    /// </exception>
    public static (List<PackageCustomApi> Apis, List<string> Read) Read(IReadOnlyDictionary<string, byte[]> files, int languageCode)
    {
        var apiFiles = new List<(string Folder, string Path)>();
        var fields = new List<(string Api, string Folder, string Path, bool Request)>();
        foreach (var path in files.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            var parts = path.Split('/');
            if (parts.Length < 3 || !Is(parts[0], Folder) || parts[1].Length == 0) continue;
            if (parts.Length == 3 && Is(parts[2], ApiFile)) apiFiles.Add((parts[1], path));
            else if (parts.Length == 5 && parts[3].Length > 0 && Is(parts[2], ParametersFolder) && Is(parts[4], ParameterFile))
                fields.Add((parts[1], parts[3], path, true));
            else if (parts.Length == 5 && parts[3].Length > 0 && Is(parts[2], PropertiesFolder) && Is(parts[4], PropertyFile))
                fields.Add((parts[1], parts[3], path, false));
        }

        foreach (var (api, _, path, _) in fields)
            if (!apiFiles.Any(a => Is(a.Folder, api)))
                throw Invalid(path, $"belongs to no custom API: the solution file has no '{Folder}/{api}/{ApiFile}'");

        var apis = new List<(PackageCustomApi Api, string Path)>();
        foreach (var (folder, path) in apiFiles)
        {
            var root = Root(files, path, "customapi");
            DemandFolder(root, folder, path);
            var mine = fields.Where(f => Is(f.Api, folder)).ToList();
            var parameters = ReadFields(files, mine.Where(f => f.Request), "customapirequestparameter", true, languageCode);
            var properties = ReadFields(files, mine.Where(f => !f.Request), "customapiresponseproperty", false, languageCode);
            var api = Api(root, path, languageCode, parameters, properties);
            if (apis.FirstOrDefault(a => Is(a.Api.UniqueName, api.UniqueName)) is { Path: not null } other)
                throw Invalid(path, $"has the uniquename '{api.UniqueName}' of '{other.Path}' (ignoring case): two custom APIs cannot share it");
            apis.Add((api, path));
        }
        return (apis.Select(a => a.Api).OrderBy(a => a.UniqueName, StringComparer.Ordinal).ToList(),
            [.. apiFiles.Select(a => a.Path), .. fields.Select(f => f.Path)]);
    }

    /// <summary>
    /// The request parameters or response properties of one API, by unique name; two of one kind sharing a unique name
    /// (ignoring case) are refused.
    /// </summary>
    private static List<PackageCustomApiField> ReadFields(IReadOnlyDictionary<string, byte[]> files,
        IEnumerable<(string Api, string Folder, string Path, bool Request)> entries, string rootName, bool request, int language)
    {
        var read = new List<(PackageCustomApiField Field, string Path)>();
        foreach (var (_, folder, path, _) in entries)
        {
            var root = Root(files, path, rootName);
            DemandFolder(root, folder, path);
            var field = Field(root, path, request, language);
            if (read.FirstOrDefault(r => Is(r.Field.UniqueName, field.UniqueName)) is { Path: not null } other)
                throw Invalid(path, $"has the uniquename '{field.UniqueName}' of '{other.Path}' (ignoring case): two {(request ? "request parameters" : "response properties")} of one custom API cannot share it");
            read.Add((field, path));
        }
        return read.Select(r => r.Field).OrderBy(f => f.UniqueName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Refuses a file whose folder is not named by its <c>uniquename</c> (ignoring case): the export writes each file in the
    /// folder of its unique name, so it would come back on another path.
    /// </summary>
    private static void DemandFolder(XElement root, string folder, string path)
    {
        var name = UniqueName(root, path);
        if (!Is(folder, name)) throw Invalid(path, $"is in the folder '{folder}' but has the uniquename '{name}'");
    }

    private static bool Is(string segment, string name) => string.Equals(segment, name, StringComparison.OrdinalIgnoreCase);

    private static XElement Root(IReadOnlyDictionary<string, byte[]> files, string path, string name)
    {
        var root = SolutionZip.Xml(files[path], $"'{path}'").Root!;
        if (root.Name.LocalName != name) throw Invalid(path, $"has the root element '{root.Name.LocalName}' instead of '{name}'");
        return root;
    }

    private static PackageCustomApi Api(XElement root, string path, int language, IReadOnlyList<PackageCustomApiField> parameters,
        IReadOnlyList<PackageCustomApiField> properties) => new(
        UniqueName(root, path),
        Required(root, "name", path),
        Int(root, "bindingtype", path) ?? 0,
        Value(root, "boundentitylogicalname"),
        Int(root, "allowedcustomprocessingsteptype", path) ?? 0,
        Flag(root, "isfunction", path) ?? false,
        Flag(root, "isprivate", path) ?? false,
        Flag(root, "workflowsdkstepenabled", path) ?? false,
        Value(root, "executeprivilegename"),
        PluginType(root, path),
        Labels(root.Element("displayname"), language),
        Labels(root.Element("description"), language),
        parameters,
        properties,
        UnmodelledXml.Capture(root, ApiShape));

    private static PackageCustomApiField Field(XElement root, string path, bool request, int language) => new(
        UniqueName(root, path),
        Required(root, "name", path),
        Int(root, "type", path) ?? throw Invalid(path, "has no type"),
        Value(root, "logicalentityname"),
        request ? Flag(root, "isoptional", path) ?? false : null,
        Labels(root.Element("displayname"), language),
        Labels(root.Element("description"), language),
        UnmodelledXml.Capture(root, request ? ParameterShape : PropertyShape));

    private static string UniqueName(XElement root, string path) =>
        ((string?)root.Attribute("uniquename"))?.Trim() is { Length: > 0 } name ? name : throw Invalid(path, "has no uniquename attribute");

    /// <summary>
    /// The plug-in type: <c>plugintypeid/plugintypeexportkey</c> (an export), <c>plugintypeid/plugintypeid</c> (Learn's
    /// form), or a GUID as the element's own text; null when the element is missing or empty.
    /// </summary>
    private static Guid? PluginType(XElement root, string path)
    {
        if (root.Element("plugintypeid") is not { } element || CustomizationsReader.IsNil(element)) return null;
        if (!element.HasElements) return Key(element.Value.Trim());
        if (element.Elements().FirstOrDefault(e => e.Name.LocalName is not ("plugintypeexportkey" or "plugintypeid")) is { } unknown)
            throw Invalid(path, $"has a plugintypeid with the child element '{unknown.Name.LocalName}', which xrmverse does not read: only plugintypeexportkey or plugintypeid can name the plug-in type");
        var exportKey = Key(Value(element, "plugintypeexportkey"));
        var id = Key(Value(element, "plugintypeid"));
        if (exportKey is not null && id is not null && exportKey != id)
            throw Invalid(path, $"has a plugintypeid naming two plug-in types: plugintypeexportkey '{exportKey}' and plugintypeid '{id}'");
        return exportKey ?? id;

        Guid? Key(string? text) => string.IsNullOrEmpty(text) ? null
            : Guid.TryParse(text, out var key) ? key : throw Invalid(path, $"has the plugintypeid '{text}', which is not a GUID");
    }

    /// <summary>The trimmed text of a child element; null when it is missing, empty or <c>xsi:nil</c> (the rule of <see cref="CustomizationsReader"/>).</summary>
    private static string? Value(XElement parent, string name) => CustomizationsReader.Value(parent, name);

    private static string Required(XElement parent, string name, string path) =>
        Value(parent, name) ?? throw Invalid(path, $"has no {name}");

    private static int? Int(XElement parent, string name, string path) =>
        Value(parent, name) is not { } value ? null
        : int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number
        : throw Invalid(path, $"has the {name} '{value}', which is not a number");

    private static bool? Flag(XElement parent, string name, string path) => Value(parent, name) switch
    {
        null => null,
        "0" => false,
        "1" => true,
        var value => throw Invalid(path, $"has the {name} '{value}', which is not 0 or 1"),
    };

    /// <summary>
    /// The <c>label</c> items of a <c>description</c> or <c>displayname</c> (an empty one is no label), else its
    /// <c>default</c> as the label of the base language. <c>UserLocalizedLabel</c> is the base language's (else the first).
    /// </summary>
    internal static Label Labels(XElement? container, int language)
    {
        var labels = (container?.Elements("label") ?? [])
            .Select(e => (Text: (string?)e.Attribute("description"),
                Language: int.TryParse((string?)e.Attribute("languagecode"), NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : (int?)null))
            .Where(l => !string.IsNullOrEmpty(l.Text) && l.Language is not null)
            .Select(l => new LocalizedLabel(l.Text, l.Language!.Value))
            .ToArray();
        if (labels.Length == 0)
        {
            if ((string?)container?.Attribute("default") is not { Length: > 0 } text) return new Label();
            var label = new LocalizedLabel(text, language);
            return new Label(label, new[] { label });
        }
        // No label in the base language: no UserLocalizedLabel, so that no other language's text passes for the base one.
        return new Label(labels.FirstOrDefault(l => l.LanguageCode == language)!, labels);
    }

    private static XrmdFaultException Invalid(string path, string problem) =>
        XrmdFaultException.InvalidArgument($"The solution file entry '{path}' {problem}.");
}
