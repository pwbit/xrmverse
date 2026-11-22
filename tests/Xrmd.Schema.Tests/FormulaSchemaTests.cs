using FluentAssertions;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Computed;

namespace Xrmd.Schema.Tests;

/// <summary>
/// Formula columns (Power Fx, plan 2.12c) in the schema YAML: the column key <c>formula:</c> holds the Power Fx text of the column's
/// <c>FormulaDefinition</c>; export writes it, apply passes it to CreateAttribute and UpdateAttribute with <c>SourceType</c> 3, in the
/// computed phase with the calculated and rollup columns.
/// </summary>
public class FormulaSchemaTests
{
    private const string Label = "Upper(xv_code) & \"-\" & Text(xv_qty, \"0\")";

    private const string NoDue = "IsBlank(xv_duedate)";

    private static SchemaDocument Document(string label = Label) => new()
    {
        Publisher = "xv",
        Tables =
        [
            new SchemaTable
            {
                Name = "xv_order", Label = "Order", PrimaryName = new SchemaPrimaryName { Name = "xv_name", Label = "Name" },
                Columns =
                [
                    new SchemaColumn { Name = "xv_code", Type = SchemaColumnTypes.String, MaxLength = 100, Label = "Code" },
                    new SchemaColumn { Name = "xv_qty", Type = SchemaColumnTypes.Int, Label = "Quantity" },
                    new SchemaColumn { Name = "xv_duedate", Type = SchemaColumnTypes.DateTime, Label = "Due" },
                    new SchemaColumn { Name = "xv_fxlabel", Type = SchemaColumnTypes.String, MaxLength = 4000, Label = "Fx Label", Formula = label },
                    new SchemaColumn { Name = "xv_fxnodue", Type = SchemaColumnTypes.Bool, Label = "Fx No Due", Formula = NoDue },
                    new SchemaColumn { Name = "xv_fxplus3", Type = SchemaColumnTypes.DateTime, Label = "Fx Plus 3", Formula = "DateAdd(xv_duedate,\n  3, TimeUnit.Days)" },
                ],
            },
        ],
    };

    private static SchemaPlan Diff(SchemaDocument document, InMemoryOrg org) => SchemaDiff.Compute(document, org.Entities, org.OptionSets);

    [Fact]
    public void Schema_export_and_apply_round_trip_formula()
    {
        var yaml = SchemaYaml.Write(Document());
        yaml.Split('\n').Single(l => l.Contains("name: xv_fxlabel", StringComparison.Ordinal))
            .Should().Contain("formula: ", "a one-line formula stays in the flow mapping of its column");
        yaml.Should().Contain("formula: |", "a formula of several lines is a literal block");
        var read = SchemaYaml.Read(yaml);
        read.Should().BeEquivalentTo(Document());

        var org = new InMemoryOrg();
        var plan = Diff(read, org);
        plan.Rejections.Should().BeEmpty();
        var create = plan.Changes.OfType<CreateAttributeRequest>().Single(r => r.Attribute.LogicalName == "xv_fxlabel");
        create.Attribute.SourceType.Should().Be(ComputedColumnRules.Formula);
        ComputedColumnRules.FormulaOf(create.Attribute).Should().Be(Label);
        // In the computed phase: after every simple column.
        var changes = plan.Changes.ToList();
        changes.IndexOf(create).Should().BeGreaterThan(changes.FindIndex(r => r is CreateAttributeRequest { Attribute.LogicalName: "xv_duedate" }));
        org.Apply(plan.Changes);

        var exported = SchemaExporter.Export(org.Entities, org.OptionSets, "xv");
        var columns = exported.Tables.Single(t => t.Name == "xv_order").Columns;
        columns.Single(c => c.Name == "xv_fxlabel").Formula.Should().Be(Label);
        columns.Single(c => c.Name == "xv_fxnodue").Formula.Should().Be(NoDue);
        columns.Single(c => c.Name == "xv_fxlabel").Calculated.Should().BeNull();
        columns.Single(c => c.Name == "xv_code").Formula.Should().BeNull();
        Diff(SchemaYaml.Read(SchemaYaml.Write(exported)), org).Changes.Should().BeEmpty();
    }

    [Fact]
    public void A_changed_formula_is_an_update_and_a_leading_equals_sign_is_no_change()
    {
        var org = new InMemoryOrg();
        org.Apply(Diff(Document(), org).Changes);

        // The server stores a formula without its leading '=' (ruling F2): written with it, the formula is the same.
        Diff(Document("=" + Label), org).Changes.Should().BeEmpty();

        var changed = Label.Replace("\"-\"", "\"/\"", StringComparison.Ordinal);
        var plan = Diff(Document(changed), org);

        plan.Rejections.Should().BeEmpty();
        var update = plan.Changes.OfType<UpdateAttributeRequest>().Should().ContainSingle().Subject;
        update.Attribute.LogicalName.Should().Be("xv_fxlabel");
        update.Attribute.SourceType.Should().Be(ComputedColumnRules.Formula);
        ComputedColumnRules.FormulaOf(update.Attribute).Should().Be(changed);

        // The server refuses a FormulaDefinition starting with '=' (Q1 finding 8): a schema file may keep it, the request has none.
        var withEquals = Diff(Document("=" + changed), new InMemoryOrg());
        ComputedColumnRules.FormulaOf(withEquals.Changes.OfType<CreateAttributeRequest>().Single(r => r.Attribute.LogicalName == "xv_fxlabel")
            .Attribute).Should().Be(changed);
        ComputedColumnRules.FormulaOf(Diff(Document("=" + changed), org).Changes.OfType<UpdateAttributeRequest>().Single().Attribute)
            .Should().Be(changed);
    }

    [Fact]
    public void A_column_cannot_switch_between_simple_calculated_and_formula()
    {
        var org = new InMemoryOrg();
        org.Apply(Diff(Document(), org).Changes);
        var document = Document();
        var columns = document.Tables[0].Columns;
        columns.Single(c => c.Name == "xv_fxlabel").Formula = null;
        columns.Single(c => c.Name == "xv_fxnodue").Formula = null;
        columns.Single(c => c.Name == "xv_fxnodue").Calculated = "<Activity />";
        columns.Single(c => c.Name == "xv_qty").Formula = "1 + 1";

        var plan = Diff(document, org);

        plan.Rejections.Should().BeEquivalentTo([
            "Column 'xv_order.xv_fxlabel' is a formula column: it cannot become a simple column (the SourceType of a column cannot change).",
            "Column 'xv_order.xv_fxnodue' is a formula column: it cannot become a calculated column (the SourceType of a column cannot change).",
            "Column 'xv_order.xv_qty' is a simple column: it cannot become a formula column (the SourceType of a column cannot change).",
        ]);
    }

    [Theory]
    [InlineData("memo")]
    [InlineData("money")]
    [InlineData("choice")]
    [InlineData("choices")]
    [InlineData("lookup")]
    [InlineData("file")]
    public void Formula_applies_only_to_the_types_a_formula_column_can_have(string type)
    {
        var extra = type switch
        {
            "choice" or "choices" => ", options: [{ value: 1, label: A }]",
            "lookup" => ", target: xv_order",
            _ => "",
        };
        var yaml = $$"""
            publisher: xv
            tables:
              - name: xv_order
                label: Order
                ownership: user
                primaryName: { name: xv_name, label: Name }
                columns:
                  - { name: xv_x, type: {{type}}, label: X, formula: "1 + 1"{{extra}} }
            """;

        var act = () => SchemaYaml.Read(yaml);

        act.Should().Throw<SchemaFormatException>().WithMessage(
            $"*Column 'xv_order.xv_x': formula applies only to the types string, int, decimal, bool and datetime, not to '{type}'.*");
    }

    [Theory]
    [InlineData("calculated")]
    [InlineData("rollup")]
    public void A_formula_column_is_neither_calculated_nor_rollup(string other)
    {
        var column = new SchemaColumn { Name = "xv_total", Type = SchemaColumnTypes.Decimal, Formula = "1 + 1" };
        if (other == "calculated") column.Calculated = "<Activity />";
        else column.Rollup = "<Activity />";

        SchemaColumnTypes.Validate(column).Should().Be("Column 'xv_total': a column is either calculated, rollup or formula, not more than one.");
    }

    [Fact]
    public void An_empty_formula_or_an_autonumber_format_is_refused()
    {
        SchemaColumnTypes.Validate(new SchemaColumn { Name = "xv_code", Type = SchemaColumnTypes.String, Formula = " " })
            .Should().Be("Column 'xv_code': formula cannot be empty.");
        SchemaColumnTypes.Validate(new SchemaColumn { Name = "xv_code", Type = SchemaColumnTypes.String, AutoNumberFormat = "C-{SEQNUM:4}", Formula = "\"x\"" })
            .Should().Be("Column 'xv_code': a formula column has no autoNumberFormat.");
    }

    [Theory]
    [InlineData("xv_a & \"\r\n\"")]
    [InlineData("\uFEFFxv_a")]
    [InlineData("'quoted' & xv_a")]
    [InlineData("- 1")]
    public void A_formula_reads_back_exactly(string formula)
    {
        var yaml = SchemaYaml.Write(Document(formula));

        yaml.Should().NotContain("\uFEFF").And.NotContain("\r");
        SchemaYaml.Read(yaml).Tables[0].Columns.Single(c => c.Name == "xv_fxlabel").Formula.Should().Be(formula);
    }
}
