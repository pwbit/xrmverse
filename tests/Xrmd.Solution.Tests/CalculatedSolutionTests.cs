using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Computed;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests;

/// <summary>
/// Calculated columns in solution files (plan 2.12a, Task 5, ruling K13): the reader takes <c>SourceType</c> 1 and the XAML of the
/// file <c>FormulaDefinitionFileName</c> names, verbatim, into <c>FormulaDefinition</c>; the writer gives them back with the XAML
/// file byte for byte. Built from <see cref="CalculatedSolutionFixture"/> (the provisional XAML fixtures).
/// </summary>
public class CalculatedSolutionTests
{
    private static SolutionPackage Read(byte[] zip)
    {
        var (solution, customizations, files) = SolutionZip.Read(zip);
        return CustomizationsReader.Read(solution, customizations, files);
    }

    private static AttributeMetadata Column(SolutionPackage package, string entity, string column) =>
        package.Entities.Single(e => e.LogicalName == entity).Attributes.Single(a => a.LogicalName == column);

    [Fact]
    public void Reads_calculated_column_and_its_xaml()
    {
        var package = Read(CalculatedSolutionFixture.Build());

        var label = Column(package, "xv_task", "xv_label").Should().BeOfType<StringAttributeMetadata>().Subject;
        label.SourceType.Should().Be(1);
        ComputedColumnRules.FormulaOf(label).Should().Be(CalculatedSolutionFixture.LabelXaml());
        var total = Column(package, "xv_project", "xv_total").Should().BeOfType<MoneyAttributeMetadata>().Subject;
        total.SourceType.Should().Be(1);
        ComputedColumnRules.FormulaOf(total).Should().Be(CalculatedSolutionFixture.TotalXaml());
        // The formula files are read, so they are not kept as unread entries; the column's source carries its file (layers).
        package.KeptFiles!.Keys.Should().NotContain([CalculatedSolutionFixture.LabelPath, CalculatedSolutionFixture.TotalPath]);
        package.Sources![Layers.LayerPayload.Key(ComponentType.Attribute, "xv_task.xv_label")].Files.Keys
            .Should().Equal(CalculatedSolutionFixture.LabelPath);
    }

    [Fact]
    public void A_simple_column_stays_simple()
    {
        var package = Read(CalculatedSolutionFixture.Build());

        var count = Column(package, "xv_project", "xv_count");
        (count.SourceType ?? 0).Should().Be(0);
        ComputedColumnRules.FormulaOf(count).Should().BeNull();
    }

    [Fact]
    public void Missing_formula_file_is_refused()
    {
        var act = () => Read(CalculatedSolutionFixture.Build(omit: CalculatedSolutionFixture.LabelPath));

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400
                                                             && e.Message.Contains("'Formulas/xv_task-xv_label.xaml'", StringComparison.Ordinal)
                                                             && e.Message.Contains("xv_task.xv_label", StringComparison.Ordinal));
    }

    /// <summary>
    /// Adds <c>&lt;money&gt;_base</c> as the 2.12a parity export writes the base of a calculated Currency column: a copy of the money
    /// column's element, <c>SourceType</c> 1, no <c>FormulaDefinitionFileName</c>, <c>CalculationOf</c> the money column.
    /// </summary>
    private static void AddCalculatedBase(XDocument d, string moneySchemaName)
    {
        var money = CalculatedSolutionFixture.Column(d, moneySchemaName);
        var copy = new XElement(money);
        var logical = moneySchemaName.ToLowerInvariant() + "_base";
        copy.SetAttributeValue("PhysicalName", moneySchemaName + "_Base");
        copy.Element("Name")!.Value = logical;
        copy.Element("LogicalName")!.Value = logical;
        copy.Element("SourceType")!.Value = "1";
        copy.Element("FormulaDefinitionFileName")?.Remove();
        copy.Add(new XElement("CalculationOf", moneySchemaName));
        money.AddAfterSelf(copy);
    }

    [Fact]
    public void The_base_of_a_calculated_money_column_is_its_companion()
    {
        var package = Read(CalculatedSolutionFixture.Build(edit: d => AddCalculatedBase(d, "xv_Total")));

        var totalBase = Column(package, "xv_project", "xv_total_base").Should().BeOfType<MoneyAttributeMetadata>().Subject;
        totalBase.IsBaseCurrency.Should().BeTrue();
        (totalBase.SourceType ?? 0).Should().Be(0, "the server generates it with its money column");
        ComputedColumnRules.FormulaOf(totalBase).Should().BeNull();
        Column(package, "xv_project", "xv_total").SourceType.Should().Be(1);

        // The writer gives it back as the export has it.
        var (_, written) = CustomizationsWriter.Write(package);
        var element = written.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == "xv_total_base");
        element.Element("SourceType")!.Value.Should().Be("1");
        element.Element("FormulaDefinitionFileName").Should().BeNull();

        // What xrmverse writes (the kept element, here with CalculationOf) is read back the same way.
        var reread = CustomizationsReader.Read(CustomizationsWriter.Write(package).Solution, written, CustomizationsWriter.Files(package));
        Column(reread, "xv_project", "xv_total_base").Should().BeOfType<MoneyAttributeMetadata>().Which.IsBaseCurrency.Should().BeTrue();
    }

    [Fact]
    public void A_calculated_base_of_a_simple_money_column_is_refused()
    {
        var act = () => Read(CalculatedSolutionFixture.Build(edit: d => AddCalculatedBase(d, "xv_Budget")));

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument)
            .WithMessage("Calculated column 'xv_project.xv_budget_base' has no FormulaDefinitionFileName.");
    }

    [Fact]
    public void A_calculated_column_without_a_formula_file_is_refused()
    {
        var act = () => Read(CalculatedSolutionFixture.Build(edit: d =>
            CalculatedSolutionFixture.Column(d, "xv_Label").Element("FormulaDefinitionFileName")!.Remove()));

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument)
            .WithMessage("Calculated column 'xv_task.xv_label' has no FormulaDefinitionFileName.");
    }

    [Fact]
    public void A_formula_file_on_a_simple_column_is_refused()
    {
        var act = () => Read(CalculatedSolutionFixture.Build(edit: d =>
            CalculatedSolutionFixture.Column(d, "xv_Label").Element("SourceType")!.Value = "0"));

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument)
            .WithMessage("Column 'xv_task.xv_label' has a FormulaDefinitionFileName but SourceType 0: only a calculated (SourceType 1), rollup (SourceType 2) or formula (SourceType 3) column has a formula.");
    }

    [Fact]
    public void A_calculated_column_of_a_type_that_cannot_be_calculated_is_refused()
    {
        var act = () => Read(CalculatedSolutionFixture.Build(edit: d =>
        {
            var column = CalculatedSolutionFixture.Column(d, "xv_Label");
            column.Element("Type")!.Value = "ntext";
            column.Element("Length")!.Remove();
        }));

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument)
            .WithMessage("A calculated column cannot be of type Memo: 'xv_task.xv_label'.");
    }

    [Fact]
    public void A_formula_file_that_is_not_utf8_is_refused()
    {
        var files = CalculatedSolutionFixture.Files();
        files[CalculatedSolutionFixture.LabelPath] = [0x3C, 0xC3, 0x28, 0x3E];
        var act = () => Read(SolutionFixtureZip.Build(CalculatedSolutionFixture.AddColumns, files));

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument)
            .WithMessage("The formula file 'Formulas/xv_task-xv_label.xaml' of calculated column 'xv_task.xv_label' is not UTF-8 text.");
    }

    [Fact]
    public void Export_round_trips_calculated_xaml_byte_for_byte()
    {
        // A byte order mark and CRLF line ends: the file is written back exactly as it was read.
        var files = CalculatedSolutionFixture.Files();
        var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(CalculatedSolutionFixture.LabelXaml().ReplaceLineEndings("\r\n"))).ToArray();
        files[CalculatedSolutionFixture.LabelPath] = original;
        var package = Read(SolutionFixtureZip.Build(CalculatedSolutionFixture.AddColumns, files));

        var (_, customizations) = CustomizationsWriter.Write(package);
        var written = CustomizationsWriter.Files(package);

        written[CalculatedSolutionFixture.LabelPath].Should().Equal(original);
        written[CalculatedSolutionFixture.TotalPath].Should().Equal(CalculatedSolutionFixture.Files()[CalculatedSolutionFixture.TotalPath]);
        var label = CalculatedSolutionFixture.Column(customizations, "xv_Label");
        label.Element("SourceType")!.Value.Should().Be("1");
        label.Element("FormulaDefinitionFileName")!.Value.Should().Be("/" + CalculatedSolutionFixture.LabelPath);
        label.Elements().Select(e => e.Name.LocalName).SkipWhile(n => n != "SourceType").Skip(1).First()
            .Should().Be("FormulaDefinitionFileName", "the file's place is kept");
        label.Elements("FormulaDefinitionFileName").Should().ContainSingle();
        // A simple column writes no formula file name.
        CalculatedSolutionFixture.Column(customizations, "xv_Count").Element("FormulaDefinitionFileName").Should().BeNull();
        CalculatedSolutionFixture.Column(customizations, "xv_Count").Element("SourceType")!.Value.Should().Be("0");

        var reread = CustomizationsReader.Read(CustomizationsWriter.Write(package).Solution, customizations, written);
        ComputedColumnRules.FormulaOf(Column(reread, "xv_task", "xv_label")).Should().Be(ComputedColumnRules.FormulaOf(Column(package, "xv_task", "xv_label")));
    }

    [Fact]
    public void A_calculated_column_created_in_xrmverse_is_written_with_its_formula_file()
    {
        var package = Read(CalculatedSolutionFixture.Build());
        // Nothing kept: the column was created through the SDK, not imported.
        var unmodelled = new Dictionary<(int, string), XElement>(package.Unmodelled!);
        unmodelled.Remove(UnmodelledXml.AttributeKey("xv_task", "xv_label"));
        var created = package with { Unmodelled = unmodelled };

        var (_, customizations) = CustomizationsWriter.Write(created);

        var label = CalculatedSolutionFixture.Column(customizations, "xv_Label");
        label.Element("SourceType")!.Value.Should().Be("1");
        label.Element("FormulaDefinitionFileName")!.Value.Should().Be("/Formulas/xv_task-xv_label.xaml");
        label.Elements().Select(e => e.Name.LocalName).SkipWhile(n => n != "SourceType").Skip(1).First().Should().Be("FormulaDefinitionFileName");
        Encoding.UTF8.GetString(CustomizationsWriter.Files(created)[CalculatedSolutionFixture.LabelPath]).Should().Be(CalculatedSolutionFixture.LabelXaml());
    }
}
