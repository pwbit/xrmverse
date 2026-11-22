using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Xrmd.TestSupport;

/// <summary>A <c>ProcessTrigger</c> of a <see cref="TestProcess"/> (a business rule's, plan 2.14 ruling W15).</summary>
/// <param name="Event">The <c>event</c>: <c>load</c>, <c>change</c> or <c>save</c>.</param>
/// <param name="Entity">The <c>primaryentitytypecode</c>.</param>
/// <param name="Scope">1 Form, 2 Entity.</param>
/// <param name="ControlName">The <c>controlname</c> (the column of a change event), if any.</param>
public sealed record TestProcessTrigger(string Event, string Entity, int Scope = 2, string? ControlName = null);

/// <summary>
/// A process of a <see cref="WorkflowSolutionBuilder"/> solution. Its id derives from <paramref name="Name"/>, so two builds of the
/// same process are the same process.
/// </summary>
/// <param name="Name">The process name (unique per test run).</param>
/// <param name="Fixture">The XAML fixture of <c>tests/fixtures/workflows</c> (without <c>.xaml</c>); null for a modern flow
/// (category 5), which carries a <c>JsonFileName</c> instead, as in OpenProject365.</param>
/// <param name="Category">0 Workflow, 2 Business Rule, 3 Action, 5 Modern Flow, …</param>
/// <param name="Mode">0 Background, 1 Real-time.</param>
/// <param name="PrimaryEntity">The <c>PrimaryEntity</c>.</param>
/// <param name="StateCode">0 Draft, 1 Activated (the state the file asks for).</param>
/// <param name="TriggerOnCreate">The <c>TriggerOnCreate</c> flag.</param>
/// <param name="TriggerOnUpdate">The <c>TriggerOnUpdateAttributeList</c>; null writes no element.</param>
/// <param name="CreateStage">The <c>CreateStage</c> (20 or 40, real-time only); null writes no element.</param>
/// <param name="UniqueName">The <c>UniqueName</c> (an action's message name); null writes no element.</param>
/// <param name="Triggers">The <c>ProcessTriggers</c>; null or empty writes no element.</param>
/// <param name="Scope">The <c>Scope</c>: 1 User, 2 Business Unit, 3 Parent: Child Business Units, 4 Organization.</param>
public sealed record TestProcess(string Name, string? Fixture, int Category = 0, int Mode = 0, string PrimaryEntity = "account",
    int StateCode = 1, bool TriggerOnCreate = true, string? TriggerOnUpdate = null, int? CreateStage = null, string? UniqueName = null,
    IReadOnlyList<TestProcessTrigger>? Triggers = null, int Scope = 4)
{
    /// <summary>The <c>WorkflowId</c>.</summary>
    public Guid Id => WorkflowSolutionBuilder.IdOf(Name);

    /// <summary>The path of the process's file in the archive (no leading <c>/</c>): its XAML, or its JSON for a modern flow.</summary>
    public string FilePath => $"Workflows/{new string(Name.Where(char.IsLetterOrDigit).ToArray())}-{Id.ToString("D").ToUpperInvariant()}.{(Fixture is null ? "json" : "xaml")}";
}

/// <summary>
/// Builds an unmanaged (or managed) solution file holding classic processes (plan 2.14, task 3) with the hand-written XAML fixtures of
/// <c>tests/fixtures/workflows</c> (provisional, ruling W25), and one JScript web resource, so that a test can see the other
/// components of the file imported. The <c>Workflow</c> element follows the shape of OpenProject365's modern flows and our memory of
/// classic exports (checklist 1h): attributes <c>WorkflowId</c> and <c>Name</c>, then <c>XamlFileName</c>, <c>Type</c>, …,
/// <c>ProcessTriggers</c>. Linked into Xrmd.Solution.Tests and Xrmd.Integration.Tests; the project must copy
/// <c>tests/fixtures/workflows/*.xaml</c> to <c>fixtures/workflows</c>.
/// </summary>
public static class WorkflowSolutionBuilder
{
    /// <summary>The publisher of the built solutions.</summary>
    public const string PublisherName = "xrmverseflows";

    /// <summary>The customization prefix of <see cref="PublisherName"/>.</summary>
    public const string Prefix = "xw";

    /// <summary>The <c>WorkflowId</c> of the process named <paramref name="name"/>.</summary>
    public static Guid IdOf(string name) => Derived("workflow:" + name);

    /// <summary>The <c>processtriggerid</c> of the <paramref name="index"/>th trigger of the process named <paramref name="process"/>.</summary>
    public static Guid TriggerIdOf(string process, int index) => Derived($"processtrigger:{process}:{index}");

    /// <summary>The id of the web resource of the solution <paramref name="solutionName"/>.</summary>
    public static Guid WebResourceIdOf(string solutionName) => Derived("webresource:" + solutionName);

    /// <summary>The name of the web resource of the solution <paramref name="solutionName"/>.</summary>
    public static string WebResourceNameOf(string solutionName) => $"{Prefix}_/scripts/{solutionName}.js";

    /// <summary>The text of the XAML fixture <paramref name="fixture"/>.</summary>
    public static string Xaml(string fixture) => Encoding.UTF8.GetString(XamlBytes(fixture));

    /// <summary>The bytes of the XAML fixture <paramref name="fixture"/>, as the file holds them.</summary>
    public static byte[] XamlBytes(string fixture) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", fixture + ".xaml"));

    /// <summary>The unmanaged solution file of <paramref name="processes"/>.</summary>
    public static byte[] Build(string solutionName, params TestProcess[] processes) => Build(solutionName, false, null, processes);

    /// <summary>The solution file of <paramref name="processes"/>, managed or not.</summary>
    public static byte[] Build(string solutionName, bool managed, params TestProcess[] processes) => Build(solutionName, managed, null, processes);

    /// <summary>
    /// The solution file of <paramref name="processes"/>; <paramref name="edit"/> may change the archive entries first (keyed by their
    /// path: add, remove or rewrite any of them).
    /// </summary>
    public static byte[] Build(string solutionName, bool managed, Action<IDictionary<string, byte[]>>? edit, params TestProcess[] processes)
    {
        var (solution, customizations, files) = Parts(solutionName, managed, processes);
        var entries = new SortedDictionary<string, byte[]>(files, StringComparer.Ordinal)
        {
            ["solution.xml"] = Bytes(solution),
            ["customizations.xml"] = Bytes(customizations),
            ["[Content_Types].xml"] = Encoding.UTF8.GetBytes(
                """<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/octet-stream" /><Default Extension="xaml" ContentType="application/octet-stream" /><Default Extension="js" ContentType="application/octet-stream" /></Types>"""),
        };
        edit?.Invoke(entries);

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(content);
            }
        }
        return stream.ToArray();
    }

    /// <summary>
    /// The <c>solution.xml</c>, the <c>customizations.xml</c> and the other archive entries (the process files and the web resource,
    /// by their path without a leading <c>/</c>) of the solution, for a reader test.
    /// </summary>
    public static (XDocument Solution, XDocument Customizations, Dictionary<string, byte[]> Files) Parts(string solutionName, bool managed,
        params TestProcess[] processes)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var process in processes)
            files[process.FilePath] = process.Fixture is { } fixture ? XamlBytes(fixture) : Encoding.UTF8.GetBytes("""{"properties":{}}""");
        var webResourceId = WebResourceIdOf(solutionName);
        var webResourcePath = $"WebResources/{WebResourceNameOf(solutionName)}{webResourceId.ToString("D").ToUpperInvariant()}";
        files[webResourcePath] = Encoding.UTF8.GetBytes("function onLoad() { }");

        var customizations = new XDocument(new XElement("ImportExportXml",
            new XElement("Entities"), new XElement("Roles"),
            new XElement("Workflows", processes.Select(Workflow)),
            new XElement("FieldSecurityProfiles"), new XElement("Templates"), new XElement("EntityMaps"), new XElement("EntityRelationships"),
            new XElement("OrganizationSettings"), new XElement("optionsets"), new XElement("CustomControls"),
            new XElement("SolutionPluginAssemblies"), new XElement("SdkMessageProcessingSteps"),
            new XElement("WebResources", new XElement("WebResource",
                new XElement("WebResourceId", webResourceId.ToString("B")),
                new XElement("Name", WebResourceNameOf(solutionName)),
                new XElement("DisplayName", solutionName + ".js"),
                new XElement("WebResourceType", 3),
                new XElement("IntroducedVersion", "1.0.0.0"),
                new XElement("IsEnabledForMobileClient", 0),
                new XElement("IsAvailableForMobileOffline", 0),
                new XElement("IsCustomizable", 1),
                new XElement("CanBeDeleted", 1),
                new XElement("IsHidden", 0),
                new XElement("FileName", "/" + webResourcePath))),
            new XElement("Languages", new XElement("Language", 1033))));
        return (Solution(solutionName, managed, processes, webResourceId), customizations, files);
    }

    /// <summary>The <c>Workflow</c> element of <paramref name="process"/>.</summary>
    public static XElement Workflow(TestProcess process) => new("Workflow",
        new XAttribute("WorkflowId", process.Id.ToString("B")),
        new XAttribute("Name", process.Name),
        new XAttribute("Description", $"The test process {process.Name}"),
        new XElement(process.Fixture is null ? "JsonFileName" : "XamlFileName", "/" + process.FilePath),
        new XElement("Type", 1),
        new XElement("Subprocess", 0),
        new XElement("Category", process.Category),
        new XElement("Mode", process.Mode),
        new XElement("Scope", process.Scope),
        new XElement("OnDemand", 0),
        new XElement("TriggerOnCreate", process.TriggerOnCreate ? 1 : 0),
        new XElement("TriggerOnDelete", 0),
        process.TriggerOnUpdate is { } update ? new XElement("TriggerOnUpdateAttributeList", update) : null,
        new XElement("AsyncAutodelete", 0),
        new XElement("SyncWorkflowLogOnFailure", 0),
        new XElement("StateCode", process.StateCode),
        new XElement("StatusCode", process.StateCode == 1 ? 2 : 1),
        process.CreateStage is { } stage ? new XElement("CreateStage", stage) : null,
        new XElement("RunAs", 1),
        new XElement("IsTransacted", 1),
        new XElement("IntroducedVersion", "1.0.0.0"),
        new XElement("IsCustomizable", 1),
        new XElement("BusinessProcessType", 0),
        new XElement("IsCustomProcessingStepAllowedForOtherPublishers", 1),
        process.UniqueName is { } unique ? new XElement("UniqueName", unique) : null,
        new XElement("PrimaryEntity", process.PrimaryEntity),
        new XElement("LocalizedNames", new XElement("LocalizedName", new XAttribute("languagecode", 1033), new XAttribute("description", process.Name))),
        process.Triggers is { Count: > 0 } triggers
            ? new XElement("ProcessTriggers", triggers.Select((t, i) => new XElement("ProcessTrigger",
                new XAttribute("processtriggerid", TriggerIdOf(process.Name, i).ToString("B")),
                t.ControlName is { } control ? new XElement("controlname", control) : null,
                new XElement("event", t.Event),
                new XElement("iscustomizable", 1),
                new XElement("primaryentitytypecode", t.Entity),
                new XElement("scope", t.Scope))))
            : null);

    private static XDocument Solution(string solutionName, bool managed, IEnumerable<TestProcess> processes, Guid webResourceId) => new(
        new XElement("ImportExportXml",
            new XAttribute("version", "9.2.24093.200"), new XAttribute("SolutionPackageVersion", "9.2"), new XAttribute("languagecode", 1033),
            new XAttribute("generatedBy", "CrmLive"),
            new XElement("SolutionManifest",
                new XElement("UniqueName", solutionName),
                new XElement("LocalizedNames", new XElement("LocalizedName", new XAttribute("description", solutionName), new XAttribute("languagecode", 1033))),
                new XElement("Descriptions"),
                new XElement("Version", "1.0.0.0"),
                new XElement("Managed", managed ? 1 : 0),
                new XElement("Publisher",
                    new XElement("UniqueName", PublisherName),
                    new XElement("LocalizedNames", new XElement("LocalizedName", new XAttribute("description", "xrmverse process tests"), new XAttribute("languagecode", 1033))),
                    new XElement("Descriptions"),
                    new XElement("CustomizationPrefix", Prefix),
                    new XElement("CustomizationOptionValuePrefix", 10002)),
                new XElement("RootComponents",
                    processes.Select(p => new XElement("RootComponent", new XAttribute("type", 29), new XAttribute("id", p.Id.ToString("B")),
                        new XAttribute("behavior", 0))),
                    new XElement("RootComponent", new XAttribute("type", 61), new XAttribute("schemaName", WebResourceNameOf(solutionName)),
                        new XAttribute("behavior", 0))),
                new XElement("MissingDependencies"))));

    private static Guid Derived(string text) => new(MD5.HashData(Encoding.UTF8.GetBytes("xrmverse-workflow-tests:" + text)));

    private static byte[] Bytes(XDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }
}
