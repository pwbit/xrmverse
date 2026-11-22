using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Computed;
using Xrmd.Solution.Layers;
using Xrmd.TestSupport;

namespace Xrmd.Solution.Tests;

/// <summary>
/// Formula columns (Power Fx, plan 2.12c, ruling F3) in solution files: the attribute has <c>SourceType</c> 3 and a
/// <c>FormulaDefinitionFileName</c> naming its table's <c>Formulas/&lt;table&gt;-FormulaDefinitions.yaml</c>, which holds one line
/// <c>&lt;column&gt;: =&lt;formula&gt;</c> per formula column of the table. Built from the parity export <c>xrmCalcTest_1_0_0_4.zip</c>
/// (three formula columns on <c>xrmv_calcorder</c>) and the RealExportShapes overlay.
/// </summary>
public class FormulaFilesTests
{
    private const string Order = "xrmv_calcorder";

    private const string YamlPath = "Formulas/xrmv_calcorder-FormulaDefinitions.yaml";

    private static readonly string[] FormulaColumns = ["xrmv_fxlabel", "xrmv_fxnodue", "xrmv_fxplus3"];

    private static byte[] Zip() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "xrmCalcTest_1_0_0_4.zip"));

    private static (XDocument Solution, XDocument Customizations, IReadOnlyDictionary<string, byte[]> Files) Export() => SolutionZip.Read(Zip());

    private static SolutionPackage Read(Func<IReadOnlyDictionary<string, byte[]>, IReadOnlyDictionary<string, byte[]>>? files = null)
    {
        var (solution, customizations, all) = Export();
        return CustomizationsReader.Read(solution, customizations, files?.Invoke(all) ?? all);
    }

    private static XElement Element(XDocument customizations, string logicalName) =>
        customizations.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == logicalName);

    private static AttributeMetadata Column(SolutionPackage package, string entity, string column) =>
        package.Entities.Single(e => e.LogicalName == entity).Attributes.Single(a => a.LogicalName == column);

    [Fact]
    public void Reads_parity_yaml()
    {
        var (_, _, files) = Export();

        var formulas = FormulaFiles.Read(Encoding.UTF8.GetString(files[YamlPath]), "/" + YamlPath);

        // The formulas without their leading '=' (ruling F2: the FormulaDefinition is stored without it).
        formulas.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["xrmv_fxlabel"] = "Upper(xrmv_code) & \"-\" & Text(xrmv_qty, \"0\")",
            ["xrmv_fxnodue"] = "IsBlank(xrmv_duedate)",
            ["xrmv_fxplus3"] = "DateAdd(xrmv_startedon,3,TimeUnit.Days)",
        });

        // The reader gives each formula column its SourceType and formula; the file is read, not kept as an unread entry, and is
        // the source file of each of its columns (layers).
        var package = Read();
        foreach (var (column, formula) in formulas)
        {
            var attribute = Column(package, Order, column);
            attribute.SourceType.Should().Be(ComputedColumnRules.Formula, column);
            ComputedColumnRules.FormulaOf(attribute).Should().Be(formula, column);
            package.Sources![Layers.LayerPayload.Key(ComponentType.Attribute, $"{Order}.{column}")].Files.Keys.Should().Equal(YamlPath);
        }
        Column(package, Order, "xrmv_fxlabel").Should().BeOfType<StringAttributeMetadata>();
        Column(package, Order, "xrmv_fxnodue").Should().BeOfType<BooleanAttributeMetadata>();
        Column(package, Order, "xrmv_fxplus3").Should().BeOfType<DateTimeAttributeMetadata>()
            .Which.DateTimeBehavior.Should().Be(DateTimeBehavior.UserLocal);
        package.KeptFiles!.Keys.Should().NotContain(YamlPath);
    }

    [Fact]
    public void Missing_key_is_refused()
    {
        var act = () => Read(files =>
        {
            var copy = files.ToDictionary(f => f.Key, f => f.Value);
            var lines = Encoding.UTF8.GetString(copy[YamlPath]).Split("\r\n").Where(l => !l.StartsWith("xrmv_fxnodue:", StringComparison.Ordinal));
            copy[YamlPath] = Encoding.UTF8.GetBytes(string.Join("\r\n", lines));
            return copy;
        });

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400)
            .WithMessage("The formula file '/Formulas/xrmv_calcorder-FormulaDefinitions.yaml' has no formula for the formula column 'xrmv_calcorder.xrmv_fxnodue'.");
    }

    [Fact]
    public void A_missing_formula_file_is_refused()
    {
        var act = () => Read(files => files.Where(f => f.Key != YamlPath).ToDictionary(f => f.Key, f => f.Value));

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument)
            .WithMessage("The solution file is missing 'Formulas/xrmv_calcorder-FormulaDefinitions.yaml' of formula column 'xrmv_calcorder.xrmv_fxlabel'.");
    }

    [Fact]
    public void A_formula_column_without_a_formula_file_name_is_refused()
    {
        var (solution, customizations, files) = Export();
        Element(customizations, "xrmv_fxlabel").Element("FormulaDefinitionFileName")!.Remove();

        var act = () => CustomizationsReader.Read(solution, customizations, files);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument)
            .WithMessage("Formula column 'xrmv_calcorder.xrmv_fxlabel' has no FormulaDefinitionFileName.");
    }

    [Fact]
    public void A_formula_column_of_a_type_without_a_formula_is_refused()
    {
        var (solution, customizations, files) = Export();
        var label = Element(customizations, "xrmv_fxlabel");
        label.Element("Type")!.Value = "ntext";
        label.Element("Format")!.Remove();

        var act = () => CustomizationsReader.Read(solution, customizations, files);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument)
            .WithMessage("A formula column cannot be of type Memo: 'xrmv_calcorder.xrmv_fxlabel'.");
    }

    [Fact]
    public void Quoted_and_block_scalars_read()
    {
        const string yaml = "\uFEFFxv_a: \"=Upper(xv_code) & \\\"-\\\"\"\r\n"
                            + "xv_b: '=If(xv_qty > 1, \"many\", ''one'')'\n"
                            + "xv_c: |\n  =If(xv_qty > 1,\n    \"many\",\n    \"one\")\n"
                            + "xv_d: =xv_qty # a comment\n"
                            + "xv_e: IsBlank(xv_due)\n";

        var formulas = FormulaFiles.Read(yaml, "/Formulas/xv_t-FormulaDefinitions.yaml");

        formulas.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["xv_a"] = "Upper(xv_code) & \"-\"",
            ["xv_b"] = "If(xv_qty > 1, \"many\", 'one')",
            ["xv_c"] = "If(xv_qty > 1,\n  \"many\",\n  \"one\")",
            ["xv_d"] = "xv_qty",
            // The API takes a formula with or without its '=' (F2); so does the file.
            ["xv_e"] = "IsBlank(xv_due)",
        });
        FormulaFiles.Read("", "/Formulas/xv_t-FormulaDefinitions.yaml").Should().BeEmpty();
    }

    [Theory]
    [InlineData("- =xv_a\n", "is not a mapping of column names to formulas")]
    [InlineData("xv_a: [1, 2]\n", "has no text formula for 'xv_a'")]
    [InlineData("xv_a:\n  b: =1\n", "has no text formula for 'xv_a'")]
    [InlineData("xv_a: =1\nxv_a: =2\n", "is not valid YAML")]
    [InlineData("xv_a: \"=1\n", "is not valid YAML")]
    [InlineData("xv_a: =1\n---\nxv_b: =2\n", "holds more than one YAML document")]
    public void Other_shapes_are_refused(string yaml, string reason)
    {
        var act = () => FormulaFiles.Read(yaml, "/Formulas/xv_t-FormulaDefinitions.yaml");

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400)
            .WithMessage($"The formula file '/Formulas/xv_t-FormulaDefinitions.yaml' {reason}*");
    }

    [Fact]
    public void Columns_differing_only_in_case_are_refused_as_a_duplicate()
    {
        var act = () => FormulaFiles.Read("xv_a: =1\r\nXV_A: =2\r\n", "/Formulas/xv_t-FormulaDefinitions.yaml");

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400)
            .WithMessage("The formula file '/Formulas/xv_t-FormulaDefinitions.yaml' names the column 'XV_A' more than once (column names ignore case).");
    }

    [Fact]
    public void A_formula_column_layer_carries_only_its_own_line()
    {
        var package = Read();

        foreach (var (column, line) in new[]
                 {
                     ("xrmv_fxlabel", "xrmv_fxlabel: =Upper(xrmv_code) & \"-\" & Text(xrmv_qty, \"0\")\r\n"),
                     ("xrmv_fxnodue", "xrmv_fxnodue: =IsBlank(xrmv_duedate)\r\n"),
                 })
        {
            var files = package.Sources![Layers.LayerPayload.Key(ComponentType.Attribute, $"{Order}.{column}")].Files;
            files.Keys.Should().Equal(YamlPath);
            Encoding.UTF8.GetString(files[YamlPath]).Should().Be(line, "the layer of {0} restores its own formula only", column);
        }
    }

    [Fact]
    public void Layers_of_two_solutions_on_one_table_restore_each_column_its_own_formula()
    {
        // Two solutions with the same formula columns of xrmv_calcorder: B changes xrmv_fxnodue. A restore that combines A's layer of
        // xrmv_fxlabel with B's layer of xrmv_fxnodue gives each column the formula of its own layer, whatever the payload order.
        var a = Read();
        var b = Read(files =>
        {
            var copy = files.ToDictionary(f => f.Key, f => f.Value);
            copy[YamlPath] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(copy[YamlPath])
                .Replace("=IsBlank(xrmv_duedate)", "=Not(IsBlank(xrmv_duedate))", StringComparison.Ordinal)
                .Replace("=Upper(xrmv_code)", "=Lower(xrmv_code)", StringComparison.Ordinal));
            return copy;
        });
        var label = LayerPayload.From(a, ComponentType.Attribute, $"{Order}.xrmv_fxlabel");
        var noDue = LayerPayload.From(b, ComponentType.Attribute, $"{Order}.xrmv_fxnodue");

        foreach (var payloads in new[] { new[] { label, noDue }, [noDue, label] })
        {
            var restored = LayerPayload.ToPackage(payloads.Select(p => (ComponentType.Attribute, Guid.NewGuid(), p)).ToList());
            ComputedColumnRules.FormulaOf(Column(restored, Order, "xrmv_fxlabel")).Should().Be("Upper(xrmv_code) & \"-\" & Text(xrmv_qty, \"0\")");
            ComputedColumnRules.FormulaOf(Column(restored, Order, "xrmv_fxnodue")).Should().Be("Not(IsBlank(xrmv_duedate))");
        }
    }

    [Fact]
    public void Export_round_trips_parity_yaml_byte_for_byte()
    {
        var (_, original, files) = Export();
        var package = Read();

        var (_, written) = CustomizationsWriter.Write(package);
        var writtenFiles = CustomizationsWriter.Files(package);

        // One file for the table, the formula columns in attribute order, byte for byte (CRLF, no byte order mark).
        writtenFiles[YamlPath].Should().Equal(files[YamlPath], "the formula file is written back byte for byte");
        writtenFiles.Keys.Where(k => k.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)).Should().Equal(YamlPath);
        foreach (var column in FormulaColumns)
        {
            var element = Element(written, column);
            element.Element("SourceType")!.Value.Should().Be("3", column);
            element.Element("FormulaDefinitionFileName")!.Value.Should().Be("/" + YamlPath, column);
            element.Element("ValidForCreateApi")!.Value.Should().Be(Element(original, column).Element("ValidForCreateApi")!.Value, column);
        }

        // What xrmverse writes reads back the same way.
        var reread = CustomizationsReader.Read(CustomizationsWriter.Write(package).Solution, written, writtenFiles);
        foreach (var column in FormulaColumns)
            ComputedColumnRules.FormulaOf(Column(reread, Order, column)).Should().Be(ComputedColumnRules.FormulaOf(Column(package, Order, column)));
    }

    [Fact]
    public void Write_gives_one_line_per_column_and_quotes_what_a_plain_scalar_cannot_hold()
    {
        var text = FormulaFiles.Write([
            ("xv_a", "Upper(xv_code) & \"-\""),
            ("xv_b", "If(xv_qty > 1,\n  \"x: y\", \"z #1\")"),
            ("xv_c", "xv_a & \"\\\" \t\u0001"),
        ]);

        text.Should().StartWith("xv_a: =Upper(xv_code) & \"-\"\r\nxv_b: \"").And.EndWith("\r\n");
        text.Split("\r\n").Should().HaveCount(4, "one line per column, each ended by CRLF");
        FormulaFiles.Read(text, "/Formulas/xv_t-FormulaDefinitions.yaml").Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["xv_a"] = "Upper(xv_code) & \"-\"",
            ["xv_b"] = "If(xv_qty > 1,\n  \"x: y\", \"z #1\")",
            ["xv_c"] = "xv_a & \"\\\" \t\u0001",
        });
        FormulaFiles.Write([]).Should().BeEmpty();
    }

    [Fact]
    public void RealExportShapes_formula_is_read()
    {
        // The overlay's Formulas/xv_project-FormulaDefinitions.yaml, read for the formula column xv_project.xv_label that names it.
        var (solution, customizations, files) = SolutionZip.Read(SolutionFixtureZip.BuildWithRealShapes(SolutionFixtureZip.AddFormulaLabel));

        var package = CustomizationsReader.Read(solution, customizations, files);

        var label = Column(package, "xv_project", "xv_label");
        label.SourceType.Should().Be(ComputedColumnRules.Formula);
        ComputedColumnRules.FormulaOf(label).Should().Be("Concatenate(xv_name, \" (project)\")");
        package.KeptFiles!.Keys.Should().NotContain(SolutionFixtureZip.RealShapesFormulaPath);
        // Written back as it was read (LF in the overlay: the export writes CRLF, as Dataverse does).
        Encoding.UTF8.GetString(CustomizationsWriter.Files(package)[SolutionFixtureZip.RealShapesFormulaPath])
            .Should().Be("xv_label: =Concatenate(xv_name, \" (project)\")\r\n");
    }
}
