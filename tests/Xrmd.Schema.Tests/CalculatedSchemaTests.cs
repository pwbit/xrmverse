using FluentAssertions;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Computed;

namespace Xrmd.Schema.Tests;

/// <summary>
/// Calculated columns in the schema YAML (plan 2.12a, Task 5, ruling K3): the column key <c>calculated:</c> holds the XAML of the
/// column's <c>FormulaDefinition</c>; export writes it, apply passes it to CreateAttribute and UpdateAttribute with <c>SourceType</c> 1.
/// </summary>
public class CalculatedSchemaTests
{
    /// <summary>arithmetic-money.xaml (tests/fixtures/calculated) on the table <c>xv_order</c>: <c>xv_total = xv_amount * xv_quantity</c>.</summary>
    private static string Xaml() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "calculated", "arithmetic-money.xaml"))
            .Replace("xrmv_calcorder", "xv_order").Replace("xrmv_amount", "xv_amount").Replace("xrmv_quantity", "xv_quantity")
            .Replace("xrmv_total", "xv_total");

    private static SchemaDocument Document(string xaml) => new()
    {
        Publisher = "xv",
        Tables =
        [
            new SchemaTable { Name = "xv_customer", Label = "Customer", PrimaryName = new SchemaPrimaryName { Name = "xv_name", Label = "Name" } },
            new SchemaTable
            {
                Name = "xv_order", Label = "Order", PrimaryName = new SchemaPrimaryName { Name = "xv_name", Label = "Name" },
                Columns =
                [
                    new SchemaColumn { Name = "xv_amount", Type = SchemaColumnTypes.Money, Label = "Amount" },
                    new SchemaColumn { Name = "xv_quantity", Type = SchemaColumnTypes.Int, Label = "Quantity" },
                    new SchemaColumn { Name = "xv_total", Type = SchemaColumnTypes.Money, Label = "Total", Calculated = xaml },
                    new SchemaColumn { Name = "xv_customerid", Type = SchemaColumnTypes.Lookup, Target = "xv_customer", Label = "Customer" },
                ],
            },
        ],
    };

    private static SchemaPlan Diff(SchemaDocument document, InMemoryOrg org) => SchemaDiff.Compute(document, org.Entities, org.OptionSets);

    [Fact]
    public void Schema_export_and_apply_round_trip_calculated()
    {
        var xaml = Xaml();
        var yaml = SchemaYaml.Write(Document(xaml));
        yaml.Should().Contain("calculated: |", "the XAML is written as a literal block");
        var read = SchemaYaml.Read(yaml);
        read.Tables[1].Columns.Single(c => c.Name == "xv_total").Calculated.Should().Be(xaml);
        read.Should().BeEquivalentTo(Document(xaml));

        var org = new InMemoryOrg();
        var plan = Diff(read, org);
        plan.Rejections.Should().BeEmpty();
        var create = plan.Changes.OfType<CreateAttributeRequest>().Single(r => r.Attribute.LogicalName == "xv_total");
        create.Attribute.SourceType.Should().Be(ComputedColumnRules.Calculated);
        ComputedColumnRules.FormulaOf(create.Attribute).Should().Be(xaml);
        // Last: after every simple column and lookup, so a reference to a column created by the same plan resolves.
        var changes = plan.Changes.ToList();
        changes.IndexOf(create).Should().BeGreaterThan(changes.FindIndex(r => r is CreateOneToManyRequest));
        changes.IndexOf(create).Should().BeGreaterThan(changes.FindIndex(r => r is CreateAttributeRequest { Attribute.LogicalName: "xv_quantity" }));
        org.Apply(plan.Changes);

        var exported = SchemaExporter.Export(org.Entities, org.OptionSets, "xv");
        exported.Tables.Single(t => t.Name == "xv_order").Columns.Single(c => c.Name == "xv_total").Calculated.Should().Be(xaml);
        Diff(SchemaYaml.Read(SchemaYaml.Write(exported)), org).Changes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\uFEFF")]
    [InlineData("\r\n")]
    public void A_formula_a_literal_block_cannot_hold_reads_back_exactly(string special)
    {
        // A byte order mark kept from a solution file, or CRLF line ends: written double-quoted, read back as they were.
        var xaml = special == "\uFEFF" ? special + Xaml() : Xaml().ReplaceLineEndings(special);

        var yaml = SchemaYaml.Write(Document(xaml));
        // A byte order mark is not allowed inside a YAML document (and a raw CR is not text a literal block keeps): escaped.
        yaml.Should().NotContain("\uFEFF").And.NotContain("\r");
        var read = SchemaYaml.Read(yaml);

        read.Tables[1].Columns.Single(c => c.Name == "xv_total").Calculated.Should().Be(xaml);
    }

    [Fact]
    public void A_changed_formula_is_an_update()
    {
        var org = new InMemoryOrg();
        org.Apply(Diff(Document(Xaml()), org).Changes);
        var changed = Xaml().Replace(">Multiply<", ">Add<", StringComparison.Ordinal);

        var plan = Diff(Document(changed), org);

        plan.Rejections.Should().BeEmpty();
        var update = plan.Changes.OfType<UpdateAttributeRequest>().Should().ContainSingle().Subject;
        update.Attribute.LogicalName.Should().Be("xv_total");
        update.Attribute.SourceType.Should().Be(ComputedColumnRules.Calculated);
        ComputedColumnRules.FormulaOf(update.Attribute).Should().Be(changed);
        org.Apply(plan.Changes);
        ComputedColumnRules.FormulaOf(org.Attribute("xv_order", "xv_total")).Should().Be(changed);
    }

    [Fact]
    public void A_column_cannot_switch_between_simple_and_calculated()
    {
        var org = new InMemoryOrg();
        org.Apply(Diff(Document(Xaml()), org).Changes);
        var document = Document(Xaml());
        var columns = document.Tables[1].Columns;
        columns.Single(c => c.Name == "xv_total").Calculated = null;
        columns.Single(c => c.Name == "xv_amount").Calculated = Xaml();

        var plan = Diff(document, org);

        plan.Rejections.Should().BeEquivalentTo([
            "Column 'xv_order.xv_amount' is a simple column: it cannot become a calculated column (the SourceType of a column cannot change).",
            "Column 'xv_order.xv_total' is a calculated column: it cannot become a simple column (the SourceType of a column cannot change).",
        ]);
    }

    [Theory]
    [InlineData("memo")]
    [InlineData("choices")]
    [InlineData("customer")]
    [InlineData("file")]
    [InlineData("image")]
    public void Calculated_applies_only_to_the_types_that_can_be_calculated(string type)
    {
        var yaml = $$"""
            publisher: xv
            tables:
              - name: xv_order
                label: Order
                ownership: user
                primaryName: { name: xv_name, label: Name }
                columns:
                  - { name: xv_x, type: {{type}}, label: X, calculated: "<Activity />"{{(type == "choices" ? ", options: [{ value: 1, label: A }]" : "")}} }
            """;

        var act = () => SchemaYaml.Read(yaml);

        act.Should().Throw<SchemaFormatException>().WithMessage(
            $"*Column 'xv_order.xv_x': calculated applies only to the types string, int, decimal, money, bool, datetime and choice, not to '{type}'.*");
    }

    [Fact]
    public void An_autonumber_format_cannot_be_calculated()
    {
        var column = new SchemaColumn { Name = "xv_code", Type = SchemaColumnTypes.String, AutoNumberFormat = "C-{SEQNUM:4}", Calculated = "<Activity />" };

        SchemaColumnTypes.Validate(column).Should().Be("Column 'xv_code': a calculated column has no autoNumberFormat.");
    }

    [Fact]
    public void An_empty_formula_is_refused()
    {
        var column = new SchemaColumn { Name = "xv_code", Type = SchemaColumnTypes.String, Calculated = "  " };

        SchemaColumnTypes.Validate(column).Should().Be("Column 'xv_code': calculated cannot be empty.");
    }
}
