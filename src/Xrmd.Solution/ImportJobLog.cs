using System.Globalization;
using System.Xml.Linq;

namespace Xrmd.Solution;

/// <summary>
/// The log of a solution import, written to <c>importjob.data</c> in the shape of the Dataverse import log (root
/// <c>importexportxml</c> with <c>start</c>/<c>stop</c>/<c>progress</c>, the <c>solutionManifests</c> and one element per
/// processed component, grouped by kind, each with its <c>result</c> elements: <c>success</c>, <c>warning</c>, <c>info</c>
/// (xrmverse's own) or <c>failure</c> with <c>errorcode</c> and <c>errortext</c>).
/// </summary>
/// <remarks>
/// The element and attribute names follow the Dataverse log as remembered from real import jobs; Learn does not document
/// the format, so they are not verified. Warnings are extra <c>result</c> elements (<c>result="warning"</c>) of the component
/// they concern; the warnings about the whole solution belong to its <c>solutionManifest</c>.
/// </remarks>
public sealed class ImportJobLog(DateTime startedOn)
{
    /// <summary>The group of the solution manifest (warnings about the whole solution).</summary>
    public const string Manifest = "solutionManifests";

    /// <summary>The groups of the log, in the order they are written, with the element name of their items.</summary>
    private static readonly (string Group, string Item)[] Groups =
    [
        ("optionSets", "optionSet"), ("entities", "entity"), ("entityRelationships", "entityRelationship"), ("roles", "role"),
        ("fieldSecurityProfiles", "fieldSecurityProfile"), ("nodes", "node"), ("webResources", "webResource"), ("customControls", "customControl"),
        ("SolutionPluginAssemblies", "PluginAssembly"),
        ("customApis", "customApi"), ("environmentVariables", "environmentVariable"), ("workflows", "workflow"), ("SdkMessageProcessingSteps", "SdkMessageProcessingStep"),
    ];

    /// <summary>The group names of <see cref="Add"/>.</summary>
    public const string OptionSets = "optionSets", Entities = "entities", Relationships = "entityRelationships", Roles = "roles",
        FieldSecurityProfiles = "fieldSecurityProfiles", Nodes = "nodes", WebResources = "webResources", Assemblies = "SolutionPluginAssemblies", Steps = "SdkMessageProcessingSteps";

    /// <summary>The group of the custom APIs (plan 15; xrmverse's own name, the Dataverse log is not documented).</summary>
    public const string CustomApis = "customApis";

    /// <summary>The group of the custom controls (plan 3.5 UI-1d; xrmverse's own name, the Dataverse log is not documented).</summary>
    public const string CustomControls = "customControls";

    /// <summary>The group of the environment variable definitions (#167; xrmverse's own name, the Dataverse log is not documented).</summary>
    public const string EnvironmentVariables = "environmentVariables";

    /// <summary>
    /// The group of the classic processes (plan 2.14, ruling W22): each process imported, with the warnings about it (the steps
    /// xrmverse cannot run, an activation refused, a category not run). The name is xrmverse's own: the Dataverse log is not documented.
    /// </summary>
    public const string Processes = "workflows";

    private sealed record Result(string Kind, string? Text, DateTime At);

    private sealed record Item(string Group, string Id, string? Name, List<Result> Results);

    private readonly List<Item> _items = [];
    private readonly List<Result> _manifestResults = [];
    private readonly List<string> _warnings = [];

    /// <summary>When the import started.</summary>
    public DateTime StartedOn { get; } = startedOn;

    /// <summary>The manifest of the solution being imported, once read.</summary>
    public PackageSolution? Solution { get; set; }

    /// <summary>Every warning, in the order it was logged.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Records a component of <paramref name="group"/> as processed successfully.</summary>
    public void Add(string group, string id, string? name) => ItemOf(group, id, name).Results.Add(new("success", null, DateTime.UtcNow));

    /// <summary>
    /// Records a warning about the component (<paramref name="group"/>, <paramref name="id"/>), or about the whole solution
    /// when <paramref name="group"/> is <see cref="Manifest"/>.
    /// </summary>
    public void Warn(string group, string id, string? name, string text)
    {
        var result = new Result("warning", text, DateTime.UtcNow);
        if (group == Manifest) _manifestResults.Add(result);
        else ItemOf(group, id, name).Results.Add(result);
        _warnings.Add(text);
    }

    /// <summary>
    /// Records an information line about the component (<paramref name="group"/>, <paramref name="id"/>), or about the whole
    /// solution when <paramref name="group"/> is <see cref="Manifest"/>: a <c>result</c> of kind <c>info</c> (xrmverse's own;
    /// the Dataverse log is not documented), such as a managed component kept under a layer above it (plan 2.6, ruling M7). It
    /// is not a warning: <see cref="Warnings"/> does not list it.
    /// </summary>
    public void Info(string group, string id, string? name, string text)
    {
        var result = new Result("info", text, DateTime.UtcNow);
        if (group == Manifest) _manifestResults.Add(result);
        else ItemOf(group, id, name).Results.Add(result);
    }

    private Item ItemOf(string group, string id, string? name)
    {
        var item = _items.FirstOrDefault(i => i.Group == group && string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (item is null) _items.Add(item = new Item(group, id, name, []));
        return item;
    }

    /// <summary>The length of <c>importjob.data</c> (a Memo of 1 MB): the longest log <see cref="ToXml"/> writes.</summary>
    public const int MaxLength = 1048576;

    /// <summary>
    /// The log as XML: <paramref name="progress"/> (100 after a commit, 0 after a failure), the stop time and, for a failed
    /// import, the <c>failure</c> result of the manifest with <paramref name="errorCode"/> and <paramref name="errorText"/>.
    /// </summary>
    /// <remarks>
    /// A log longer than <paramref name="maxLength"/> keeps its first entries (the warnings and successes, in the order they
    /// are written) and a <c>warning</c> of the manifest <c>[log truncated: N entries omitted]</c>; the final success or
    /// failure result of the manifest is always written (an error text too long for the limit is cut).
    /// </remarks>
    public string ToXml(int progress, DateTime stoppedOn, int? errorCode = null, string? errorText = null, int maxLength = MaxLength)
    {
        if (errorText is { } text && text.Length > maxLength / 4) errorText = text[..(maxLength / 4)] + " [truncated]";
        var total = _manifestResults.Count + _items.Sum(i => i.Results.Count);
        var xml = Build(progress, stoppedOn, errorCode, errorText, total);
        if (xml.Length <= maxLength) return xml;

        // The longest prefix of the entries that fits, with the truncation marker.
        int low = 0, high = total - 1;
        var best = Build(progress, stoppedOn, errorCode, errorText, 0);
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var candidate = Build(progress, stoppedOn, errorCode, errorText, mid);
            if (candidate.Length <= maxLength)
            {
                best = candidate;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return best;
    }

    /// <summary>The log with its first <paramref name="kept"/> entries (manifest results first, then the groups in order).</summary>
    private string Build(int progress, DateTime stoppedOn, int? errorCode, string? errorText, int kept)
    {
        var total = _manifestResults.Count + _items.Sum(i => i.Results.Count);
        var budget = kept;
        List<Result> Take(List<Result> results)
        {
            var taken = results.Take(Math.Max(0, budget)).ToList();
            budget -= taken.Count;
            return taken;
        }

        var processed = errorText is null ? "true" : "false";
        var manifestResults = Take(_manifestResults);
        if (kept < total)
            manifestResults.Add(new Result("warning", $"[log truncated: {total - kept} entries omitted]", stoppedOn));
        var manifest = new XElement("solutionManifest",
            new XAttribute("languagecode", Solution?.LanguageCode ?? 1033),
            new XAttribute("id", Solution?.UniqueName ?? ""),
            new XAttribute("LocalizedName", Solution?.FriendlyName?.UserLocalizedLabel?.Label ?? Solution?.UniqueName ?? ""),
            new XAttribute("processed", processed),
            new XElement("UniqueName", Solution?.UniqueName ?? ""),
            new XElement("Version", Solution?.Version ?? ""),
            new XElement("Managed", Solution?.Managed == true ? "1" : "0"),
            manifestResults.Select(r => ToXml(r)),
            ToXml(errorText is null
                ? new Result("success", null, stoppedOn)
                : new Result("failure", errorText, stoppedOn), errorCode));

        var root = new XElement("importexportxml",
            new XAttribute("start", StartedOn.Ticks.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("stop", stoppedOn.Ticks.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("progress", progress.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("processed", processed),
            new XElement("solutionManifests", manifest));
        foreach (var (group, element) in Groups)
        {
            var items = new List<XElement>();
            foreach (var i in _items.Where(i => i.Group == group))
            {
                var results = Take(i.Results);
                if (results.Count == 0) continue;
                items.Add(new XElement(element,
                    new XAttribute("id", i.Id),
                    new XAttribute("LocalizedName", i.Name ?? i.Id),
                    new XAttribute("OriginalName", i.Name ?? i.Id),
                    new XAttribute("processed", "true"),
                    results.Select(r => ToXml(r))));
            }
            root.Add(new XElement(group, items));
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement ToXml(Result r) => ToXml(r, null);

    private static XElement ToXml(Result r, int? errorCode) => new("result",
        new XAttribute("result", r.Kind),
        new XAttribute("errorcode", r.Kind == "failure" ? $"0x{errorCode ?? 0:X8}" : "0"),
        new XAttribute("errortext", r.Text ?? ""),
        new XAttribute("datetime", r.At.ToString("HH:mm:ss.ff", CultureInfo.InvariantCulture)),
        new XAttribute("datetimeticks", r.At.Ticks.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The SpreadsheetML namespace of <see cref="ToSpreadsheet"/>.</summary>
    private static readonly XNamespace Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    /// <summary>The columns of <see cref="ToSpreadsheet"/> (not verified against a real Dataverse result).</summary>
    private static readonly string[] SpreadsheetColumns = ["Type", "Name", "Id", "Result", "Error Code", "Description", "Time"];

    /// <summary>
    /// The formatted results of an import job (RetrieveFormattedImportJobResults): the log <paramref name="logXml"/>
    /// (<c>importjob.data</c>) as an Excel workbook in the XML Spreadsheet 2003 format (SpreadsheetML: the
    /// <c>mso-application</c> processing instruction, root <c>Workbook</c>), as Learn says Dataverse returns them ("in
    /// Microsoft Excel format"). One worksheet: a header row (<see cref="SpreadsheetColumns"/>) and one row per <c>result</c>
    /// of the log, in log order: the element of the component (<c>solutionManifest</c>, <c>entity</c>, …), its
    /// <c>LocalizedName</c> and <c>id</c>, then the result's <c>result</c>, <c>errorcode</c>, <c>errortext</c> and
    /// <c>datetime</c>. An empty log gives the header row only.
    /// </summary>
    /// <remarks>The column layout of Dataverse's workbook is not documented on Learn: this one is not verified.</remarks>
    /// <exception cref="System.Xml.XmlException"><paramref name="logXml"/> is not XML.</exception>
    public static string ToSpreadsheet(string? logXml)
    {
        static XElement Row(IEnumerable<string> values) => new(Ss + "Row",
            values.Select(v => new XElement(Ss + "Cell", new XElement(Ss + "Data", new XAttribute(Ss + "Type", "String"), v))));

        var rows = new List<XElement> { Row(SpreadsheetColumns) };
        if (!string.IsNullOrEmpty(logXml))
            foreach (var result in XElement.Parse(logXml).Descendants("result"))
            {
                var component = result.Parent!;
                rows.Add(Row([
                    component.Name.LocalName,
                    (string?)component.Attribute("LocalizedName") ?? "",
                    (string?)component.Attribute("id") ?? "",
                    (string?)result.Attribute("result") ?? "",
                    (string?)result.Attribute("errorcode") ?? "",
                    (string?)result.Attribute("errortext") ?? "",
                    (string?)result.Attribute("datetime") ?? "",
                ]));
            }

        var workbook = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XProcessingInstruction("mso-application", "progid=\"Excel.Sheet\""),
            new XElement(Ss + "Workbook",
                new XAttribute("xmlns", Ss.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "ss", Ss.NamespaceName),
                new XElement(Ss + "Worksheet",
                    new XAttribute(Ss + "Name", "Import results"),
                    new XElement(Ss + "Table", rows))));
        return workbook.Declaration + workbook.ToString(SaveOptions.DisableFormatting);
    }
}
