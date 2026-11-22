using System.Text;
using System.Xml.Linq;

namespace Xrmd.TestSupport;

/// <summary>
/// The fixture solution (<see cref="SolutionFixtureZip"/>) with two calculated columns (plan 2.12a, Task 5, ruling K13), built from
/// the provisional XAML fixtures of <c>tests/fixtures/calculated</c> renamed to the fixture's tables:
/// <list type="bullet">
/// <item><c>xv_project.xv_total</c> (money) = <c>xv_budget * xv_count</c> (<c>arithmetic-money.xaml</c>): two columns of its own record;</item>
/// <item><c>xv_task.xv_label</c> (text) = <c>CONCAT(xv_name, separator, xv_projectid.xv_name)</c> (<c>concat-related.xaml</c>): a
/// column of the parent record through the lookup <c>xv_projectid</c>, which the same solution creates.</item>
/// </list>
/// The <c>attribute</c> elements and the <c>/Formulas/&lt;entity&gt;-&lt;column&gt;.xaml</c> file names follow ruling K13's assumption
/// (not yet compared with a real export, Q1). Linked into Xrmd.Solution.Tests and Xrmd.Integration.Tests; the project must copy
/// <c>tests/fixtures/calculated/*.xaml</c> to <c>fixtures/calculated</c>.
/// </summary>
public static class CalculatedSolutionFixture
{
    /// <summary>The archive path (no leading <c>/</c>) of <c>xv_project.xv_total</c>'s formula.</summary>
    public const string TotalPath = "Formulas/xv_project-xv_total.xaml";

    /// <summary>The archive path (no leading <c>/</c>) of <c>xv_task.xv_label</c>'s formula.</summary>
    public const string LabelPath = "Formulas/xv_task-xv_label.xaml";

    /// <summary>The separator of <c>xv_label</c> in <c>concat-related.xaml</c>.</summary>
    public const string DefaultSeparator = " / ";

    /// <summary>The text of a fixture of <c>tests/fixtures/calculated</c>.</summary>
    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "calculated", name + ".xaml"), new UTF8Encoding(false));

    /// <summary>The XAML of <c>xv_project.xv_total</c>.</summary>
    public static string TotalXaml() => Fixture("arithmetic-money")
        .Replace("xrmv_calcorder", "xv_project", StringComparison.Ordinal)
        .Replace("xrmv_amount", "xv_budget", StringComparison.Ordinal)
        .Replace("xrmv_quantity", "xv_count", StringComparison.Ordinal)
        .Replace("xrmv_total", "xv_total", StringComparison.Ordinal);

    /// <summary>The XAML of <c>xv_task.xv_label</c>, joining the task's and its project's names with <paramref name="separator"/>.</summary>
    public static string LabelXaml(string separator = DefaultSeparator) => Fixture("concat-related")
        .Replace("related_xrmv_accountid#account", "related_xv_projectid#xv_project", StringComparison.Ordinal)
        .Replace("EntityName=\"account\"", "EntityName=\"xv_project\"", StringComparison.Ordinal)
        .Replace("Attribute=\"name\"", "Attribute=\"xv_name\"", StringComparison.Ordinal)
        .Replace("xrmv_calcorder", "xv_task", StringComparison.Ordinal)
        .Replace("xrmv_name", "xv_name", StringComparison.Ordinal)
        .Replace("xrmv_label", "xv_label", StringComparison.Ordinal)
        .Replace($"\"{DefaultSeparator}\"", $"\"{separator}\"", StringComparison.Ordinal);

    /// <summary>The formula files of the solution, by archive path, as UTF-8 without a byte order mark.</summary>
    public static Dictionary<string, byte[]> Files(string separator = DefaultSeparator) => new(StringComparer.Ordinal)
    {
        [TotalPath] = new UTF8Encoding(false).GetBytes(TotalXaml()),
        [LabelPath] = new UTF8Encoding(false).GetBytes(LabelXaml(separator)),
    };

    /// <summary>Adds the two calculated <c>attribute</c> elements to the fixture's <c>customizations.xml</c>.</summary>
    public static void AddColumns(XDocument customizations)
    {
        ArgumentNullException.ThrowIfNull(customizations);
        Attributes(customizations, "xv_Project").Add(Column("xv_Total", "money", "Total", "/" + TotalPath,
            new XElement("Accuracy", 2), new XElement("AccuracySource", 0)));
        Attributes(customizations, "xv_Task").Add(Column("xv_Label", "nvarchar", "Label", "/" + LabelPath,
            new XElement("Format", "text"), new XElement("MaxLength", 300), new XElement("Length", 600)));
    }

    /// <summary>The <c>attributes</c> element of the fixture's table <paramref name="entityName"/> (its <c>entity Name</c>).</summary>
    public static XElement Attributes(XDocument customizations, string entityName) =>
        customizations.Descendants("entity").Single(e => (string?)e.Attribute("Name") == entityName).Element("attributes")!;

    /// <summary>The <c>attribute</c> element of the calculated column <paramref name="schemaName"/> of <paramref name="customizations"/>.</summary>
    public static XElement Column(XDocument customizations, string schemaName) =>
        customizations.Descendants("attribute").Single(a => (string?)a.Attribute("PhysicalName") == schemaName);

    /// <summary>
    /// The fixture solution with the two calculated columns; <paramref name="separator"/> changes <c>xv_label</c>'s formula,
    /// <paramref name="edit"/> may change <c>customizations.xml</c> and <paramref name="omit"/> leaves out archive entries.
    /// </summary>
    public static byte[] Build(string separator = DefaultSeparator, Action<XDocument>? edit = null, params string[] omit) =>
        Build(separator, edit, null, omit);

    /// <summary>
    /// <see cref="Build(string, Action{XDocument}?, string[])"/> with <paramref name="solution"/> changing <c>solution.xml</c> too
    /// (managed, another version).
    /// </summary>
    public static byte[] Build(string separator, Action<XDocument>? edit, Action<XDocument>? solution, params string[] omit) =>
        SolutionFixtureZip.BuildWithFiles(d =>
        {
            AddColumns(d);
            edit?.Invoke(d);
        }, solution, Files(separator), omit);

    /// <summary>A <c>solution.xml</c> change: the solution is managed, with <paramref name="version"/>.</summary>
    public static Action<XDocument> Managed(string version = "1.0.0.0") => d =>
    {
        d.Descendants("Managed").Single().Value = "1";
        d.Descendants("Version").First().Value = version;
    };

    private static XElement Column(string schemaName, string type, string label, string formulaFile, params XElement[] typeSpecific)
    {
        var name = schemaName.ToLowerInvariant();
        return new XElement("attribute", new XAttribute("PhysicalName", schemaName),
            new XElement("Type", type),
            new XElement("Name", name),
            new XElement("LogicalName", name),
            new XElement("RequiredLevel", "none"),
            new XElement("DisplayMask", "ValidForAdvancedFind|ValidForForm|ValidForGrid"),
            new XElement("ImeMode", "auto"),
            new XElement("ValidForUpdateApi", 0),
            new XElement("ValidForReadApi", 1),
            new XElement("ValidForCreateApi", 0),
            new XElement("IsCustomField", 1),
            new XElement("IsAuditEnabled", 0),
            new XElement("IsSecured", 0),
            new XElement("IntroducedVersion", "1.0.0.0"),
            new XElement("IsCustomizable", 1),
            new XElement("IsRenameable", 1),
            new XElement("CanModifySearchSettings", 1),
            new XElement("CanModifyRequirementLevelSettings", 1),
            new XElement("CanModifyAdditionalSettings", 1),
            new XElement("SourceType", 1),
            new XElement("FormulaDefinitionFileName", formulaFile),
            new XElement("IsGlobalFilterEnabled", 0),
            new XElement("IsSortableEnabled", 0),
            new XElement("AutoNumberFormat", ""),
            typeSpecific,
            new XElement("displaynames", new XElement("displayname", new XAttribute("description", label), new XAttribute("languagecode", 1033))),
            new XElement("Descriptions", new XElement("Description", new XAttribute("description", ""), new XAttribute("languagecode", 1033))));
    }
}
