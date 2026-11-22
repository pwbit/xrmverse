using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;

namespace Xrmd.Solution.Tests;

/// <summary>
/// Rollup columns in solution files (plan 2.12b, Task 6, ruling R14): the reader takes <c>SourceType</c> 2 and the XAML of the file
/// <c>FormulaDefinitionFileName</c> names, verbatim, the <c>&lt;money&gt;_base</c> of a Currency rollup (<c>SourceType</c> 2,
/// <c>CalculationOf</c>, no file) as the generated base, and skips the generated <c>_date</c>/<c>_state</c>; the writer gives them back
/// as the export has them, with no companions. Built from the parity export <c>xrmCalcTest_1_0_0_4.zip</c> as it is (its formula
/// columns, plan 2.12c, are read too).
/// </summary>
public class RollupSolutionTests
{
    private static readonly string[] Rollups = ["xrmv_lastorder", "xrmv_ordercount", "xrmv_ordertotal", "xrmv_treebudget"];

    private static byte[] Zip() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "xrmCalcTest_1_0_0_4.zip"));

    /// <summary>The export, <paramref name="edit"/> applied to customizations.xml.</summary>
    private static (XDocument Solution, XDocument Customizations, IReadOnlyDictionary<string, byte[]> Files) Export(Action<XDocument>? edit = null)
    {
        var (solution, customizations, files) = SolutionZip.Read(Zip());
        edit?.Invoke(customizations);
        return (solution, customizations, files);
    }

    private static SolutionPackage Read(Action<XDocument>? edit = null)
    {
        var (solution, customizations, files) = Export(edit);
        return CustomizationsReader.Read(solution, customizations, files);
    }

    private static XElement Element(XDocument customizations, string logicalName) =>
        customizations.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == logicalName);

    private static AttributeMetadata Column(SolutionPackage package, string column) =>
        package.Entities.Single(e => e.LogicalName == "account").Attributes.Single(a => a.LogicalName == column);

    /// <summary>The element without whitespace-only text (indentation) and the children <paramref name="without"/> names, as one line.</summary>
    private static string Canonical(XElement element, params string[] without)
    {
        var copy = new XElement(element);
        copy.Elements().Where(e => without.Contains(e.Name.LocalName)).ToList().ForEach(e => e.Remove());
        copy.DescendantNodesAndSelf().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value) && t.Parent!.HasElements).ToList()
            .ForEach(t => t.Remove());
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    [Fact]
    public void Reads_rollup_columns_and_base_of_real_export()
    {
        var (_, _, files) = Export();
        var package = Read();

        foreach (var name in Rollups)
        {
            var column = Column(package, name);
            column.SourceType.Should().Be(RollupColumns.Rollup, name);
            ComputedColumnRules.FormulaOf(column).Should().Be(Encoding.UTF8.GetString(files[$"Formulas/account-{name}.xaml"]), name);
            // The formula file is read: the column's source carries it (layers), it is not kept as an unread entry.
            package.KeptFiles!.Keys.Should().NotContain($"Formulas/account-{name}.xaml");
            package.Sources![Layers.LayerPayload.Key(ComponentType.Attribute, $"account.{name}")].Files.Keys
                .Should().Equal($"Formulas/account-{name}.xaml");
        }
        Column(package, "xrmv_ordertotal").Should().BeOfType<DecimalAttributeMetadata>();
        Column(package, "xrmv_ordercount").Should().BeOfType<IntegerAttributeMetadata>();
        Column(package, "xrmv_lastorder").Should().BeOfType<DateTimeAttributeMetadata>();
        Column(package, "xrmv_treebudget").Should().BeOfType<MoneyAttributeMetadata>();

        // The _base of the Currency rollup is read as the generated base, not as a rollup of its own.
        var treeBase = Column(package, "xrmv_treebudget_base").Should().BeOfType<MoneyAttributeMetadata>().Subject;
        treeBase.IsBaseCurrency.Should().BeTrue();
        (treeBase.SourceType ?? 0).Should().Be(0, "the server generates it with its money column");
        ComputedColumnRules.FormulaOf(treeBase).Should().BeNull();

        // No companion is read: the export has none, the server generates them.
        package.Entities.Single(e => e.LogicalName == "account").Attributes
            .Should().NotContain(a => a.LogicalName.EndsWith("_date", StringComparison.Ordinal) || a.LogicalName.EndsWith("_state", StringComparison.Ordinal));
    }

    [Fact]
    public void Companions_a_file_carries_are_skipped_as_generated()
    {
        // Assumed (R14): a file that carries <rollup>_date / <rollup>_state has them skipped, the server generates its own.
        var package = Read(d =>
        {
            var total = Element(d, "xrmv_ordertotal");
            foreach (var (suffix, type) in new[] { ("_date", "datetime"), ("_state", "int") })
            {
                var companion = new XElement(total);
                companion.SetAttributeValue("PhysicalName", "xrmv_OrderTotal" + (suffix == "_date" ? "_Date" : "_State"));
                companion.Element("Type")!.Value = type;
                companion.Element("Name")!.Value = "xrmv_ordertotal" + suffix;
                companion.Element("LogicalName")!.Value = "xrmv_ordertotal" + suffix;
                companion.Element("SourceType")!.Value = "0";
                companion.Element("FormulaDefinitionFileName")!.Remove();
                foreach (var name in new[] { "MinValue", "MaxValue", "Accuracy" }) companion.Element(name)?.Remove();
                total.AddAfterSelf(companion);
            }
        });

        var account = package.Entities.Single(e => e.LogicalName == "account");
        account.Attributes.Should().NotContain(a => a.LogicalName == "xrmv_ordertotal_date" || a.LogicalName == "xrmv_ordertotal_state");
        package.Unmodelled!.Keys.Should().NotContain(UnmodelledXml.AttributeKey("account", "xrmv_ordertotal_date"));
        Column(package, "xrmv_ordertotal").SourceType.Should().Be(RollupColumns.Rollup);
    }

    [Fact]
    public void A_rollup_without_a_formula_file_name_is_refused()
    {
        var act = () => Read(d => Element(d, "xrmv_ordertotal").Element("FormulaDefinitionFileName")!.Remove());

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400)
            .WithMessage("Rollup column 'account.xrmv_ordertotal' has no FormulaDefinitionFileName.");
    }

    [Fact]
    public void A_missing_rollup_formula_file_is_refused()
    {
        var (solution, customizations, files) = Export();
        var without = files.Where(f => f.Key != "Formulas/account-xrmv_ordercount.xaml").ToDictionary(f => f.Key, f => f.Value);

        var act = () => CustomizationsReader.Read(solution, customizations, without);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument
                                                             && e.Message.Contains("'Formulas/account-xrmv_ordercount.xaml'", StringComparison.Ordinal)
                                                             && e.Message.Contains("account.xrmv_ordercount", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rollup_of_a_type_without_a_definition_is_refused_with_its_code()
    {
        var act = () => Read(d =>
        {
            var column = Element(d, "xrmv_ordercount");
            column.Element("Type")!.Value = "ntext";
            foreach (var name in new[] { "Format", "MinValue", "MaxValue" }) column.Element(name)?.Remove();
        });

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.RollupFieldsDataTypeNotValid && e.HttpStatus == 400)
            .WithMessage("A rollup column cannot be of type Memo: 'account.xrmv_ordercount'.");
    }

    [Fact]
    public void A_rollup_base_of_a_money_column_that_is_not_a_rollup_is_refused()
    {
        // xrmv_budget is a simple Currency column of the export: a _base marked as a rollup's companion does not belong to it.
        var act = () => Read(d =>
        {
            var copy = new XElement(Element(d, "xrmv_treebudget_base"));
            copy.SetAttributeValue("PhysicalName", "xrmv_budget_Base");
            copy.Element("Name")!.Value = "xrmv_budget_base";
            copy.Element("LogicalName")!.Value = "xrmv_budget_base";
            copy.Element("CalculationOf")!.Value = "xrmv_Budget";
            Element(d, "xrmv_budget_base").ReplaceWith(copy);
        });

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument)
            .WithMessage("Rollup column 'account.xrmv_budget_base' has no FormulaDefinitionFileName.");
    }

    [Fact]
    public void Export_round_trips_rollup_xaml_and_base_byte_for_byte()
    {
        var (_, original, files) = Export();
        var package = Read();

        var (_, written) = CustomizationsWriter.Write(package);
        var writtenFiles = CustomizationsWriter.Files(package);

        // Every element as the export has it, but Descriptions: an empty description label is not written back (any column, not only
        // rollups: the writer leaves out empty labels).
        foreach (var name in Rollups.Append("xrmv_treebudget_base"))
            Canonical(Element(written, name), "Descriptions").Should().Be(Canonical(Element(original, name), "Descriptions"),
                "the element of {0} is written as the export has it", name);
        foreach (var name in Rollups)
            writtenFiles[$"Formulas/account-{name}.xaml"].Should().Equal(files[$"Formulas/account-{name}.xaml"], "the XAML of {0} is written byte for byte", name);
        written.Descendants("attribute").Select(a => a.Element("LogicalName")?.Value)
            .Should().NotContain(n => n != null && (n.EndsWith("_date", StringComparison.Ordinal) || n.EndsWith("_state", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_rollup_base_read_without_CalculationOf_is_written_with_it_once()
    {
        // A file whose _base has no CalculationOf (assumed shape, read the same way): the export writes it, after the type's elements.
        var package = Read(d => Element(d, "xrmv_treebudget_base").Element("CalculationOf")!.Remove());

        var (_, customizations) = CustomizationsWriter.Write(package);

        var treeBase = Element(customizations, "xrmv_treebudget_base");
        treeBase.Elements("CalculationOf").Should().ContainSingle().Which.Value.Should().Be("xrmv_TreeBudget");
        treeBase.Elements().Select(e => e.Name.LocalName).SkipWhile(n => n != "AccuracySource").Skip(1).First().Should().Be("CalculationOf");
        // Kept from the file, it is written once, where the file has it.
        Element(CustomizationsWriter.Write(Read()).Customizations, "xrmv_treebudget_base").Elements("CalculationOf").Should().ContainSingle();
    }

    [Fact]
    public void A_rollup_created_in_xrmverse_is_written_with_its_file_its_base_and_no_companions()
    {
        var package = Read();
        // Nothing kept for the Currency rollup and its base (created through the SDK), and the companions the store generates.
        var unmodelled = new Dictionary<(int, string), XElement>(package.Unmodelled!);
        unmodelled.Remove(UnmodelledXml.AttributeKey("account", "xrmv_treebudget"));
        unmodelled.Remove(UnmodelledXml.AttributeKey("account", "xrmv_treebudget_base"));
        var account = package.Entities.Single(e => e.LogicalName == "account");
        var tree = Column(package, "xrmv_treebudget");
        tree.MetadataId = Guid.NewGuid();
        account.SetAttributes([.. account.Attributes, .. RollupColumns.Companions(tree)]);
        var created = package with { Unmodelled = unmodelled };

        var (_, customizations) = CustomizationsWriter.Write(created);

        var element = Element(customizations, "xrmv_treebudget");
        element.Element("SourceType")!.Value.Should().Be("2");
        element.Element("FormulaDefinitionFileName")!.Value.Should().Be("/Formulas/account-xrmv_treebudget.xaml");
        var treeBase = Element(customizations, "xrmv_treebudget_base");
        treeBase.Element("SourceType")!.Value.Should().Be("2", "the base of a rollup is written as the export has it");
        treeBase.Element("FormulaDefinitionFileName").Should().BeNull();
        treeBase.Element("CalculationOf")!.Value.Should().Be("xrmv_TreeBudget");
        customizations.Descendants("attribute").Select(a => a.Element("LogicalName")?.Value)
            .Should().NotContain(["xrmv_treebudget_date", "xrmv_treebudget_state"], "the companions are the server's");
        Encoding.UTF8.GetString(CustomizationsWriter.Files(created)["Formulas/account-xrmv_treebudget.xaml"])
            .Should().Be(ComputedColumnRules.FormulaOf(tree));

        // What xrmverse writes is read back the same way.
        var reread = CustomizationsReader.Read(CustomizationsWriter.Write(created).Solution, customizations, CustomizationsWriter.Files(created));
        Column(reread, "xrmv_treebudget").SourceType.Should().Be(RollupColumns.Rollup);
        Column(reread, "xrmv_treebudget_base").Should().BeOfType<MoneyAttributeMetadata>().Which.IsBaseCurrency.Should().BeTrue();
    }
}
