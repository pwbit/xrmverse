using FluentAssertions;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata.Computed;

namespace Xrmd.Schema.Tests;

/// <summary>
/// Rollup columns in the schema YAML (plan 2.12b, Task 6, ruling R15): the column key <c>rollup:</c> holds the XAML of the column's
/// <c>FormulaDefinition</c>; export writes it, apply passes it to CreateAttribute and UpdateAttribute with <c>SourceType</c> 2, in the
/// computed phase (after every simple column and lookup, in the document's order with the calculated columns).
/// </summary>
public class RollupSchemaTests
{
    /// <summary>The real <c>account-xrmv_ordertotal.xaml</c> (tests/fixtures/rollup/real) on the tables of the document, with LF line ends.</summary>
    private static string Xaml() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "rollup", "real", "account-xrmv_ordertotal.xaml"))
            .Replace("xrmv_calcorder_AccountId_account", "xv_order_xv_customerid", StringComparison.Ordinal)
            .Replace("xrmv_calcorder", "xv_order", StringComparison.Ordinal)
            .Replace("xrmv_accountid", "xv_customerid", StringComparison.Ordinal)
            .Replace("xrmv_status", "xv_status", StringComparison.Ordinal)
            .Replace("xrmv_amount", "xv_amount", StringComparison.Ordinal)
            .ReplaceLineEndings("\n"); // the export's CRLF would be written double-quoted (see the last test)

    /// <summary>A calculated column reading the rollup (its XAML is not checked here: the server does).</summary>
    private const string Half = "<Activity><!-- xv_ordertotal / 2 --></Activity>";

    private static SchemaDocument Document(string xaml) => new()
    {
        Publisher = "xv",
        Tables =
        [
            new SchemaTable
            {
                Name = "xv_customer", Label = "Customer", PrimaryName = new SchemaPrimaryName { Name = "xv_name", Label = "Name" },
                Columns =
                [
                    new SchemaColumn { Name = "xv_ordertotal", Type = SchemaColumnTypes.Decimal, Label = "Order Total", Rollup = xaml },
                    new SchemaColumn { Name = "xv_half", Type = SchemaColumnTypes.Decimal, Label = "Half", Calculated = Half },
                ],
            },
            new SchemaTable
            {
                Name = "xv_order", Label = "Order", PrimaryName = new SchemaPrimaryName { Name = "xv_name", Label = "Name" },
                Columns =
                [
                    new SchemaColumn { Name = "xv_amount", Type = SchemaColumnTypes.Decimal, Label = "Amount" },
                    new SchemaColumn { Name = "xv_customerid", Type = SchemaColumnTypes.Lookup, Target = "xv_customer", Label = "Customer" },
                ],
            },
        ],
    };

    private static SchemaPlan Diff(SchemaDocument document, InMemoryOrg org) => SchemaDiff.Compute(document, org.Entities, org.OptionSets);

    [Fact]
    public void Schema_export_and_apply_round_trip_rollup()
    {
        var xaml = Xaml();
        var yaml = SchemaYaml.Write(Document(xaml));
        yaml.Should().Contain("rollup: |", "the XAML is written as a literal block");
        var read = SchemaYaml.Read(yaml);
        read.Tables[0].Columns.Single(c => c.Name == "xv_ordertotal").Rollup.Should().Be(xaml);
        read.Should().BeEquivalentTo(Document(xaml));

        var org = new InMemoryOrg();
        var plan = Diff(read, org);
        plan.Rejections.Should().BeEmpty();
        var create = plan.Changes.OfType<CreateAttributeRequest>().Single(r => r.Attribute.LogicalName == "xv_ordertotal");
        create.Attribute.SourceType.Should().Be(RollupColumns.Rollup);
        ComputedColumnRules.FormulaOf(create.Attribute).Should().Be(xaml);
        // In the computed phase: after every simple column and lookup (the relationship it aggregates over), and in the document's
        // order with the calculated columns (a calculated column reading the rollup comes after it).
        var changes = plan.Changes.ToList();
        changes.IndexOf(create).Should().BeGreaterThan(changes.FindIndex(r => r is CreateOneToManyRequest));
        changes.IndexOf(create).Should().BeGreaterThan(changes.FindIndex(r => r is CreateAttributeRequest { Attribute.LogicalName: "xv_amount" }));
        changes.IndexOf(create).Should().BeLessThan(changes.FindIndex(r => r is CreateAttributeRequest { Attribute.LogicalName: "xv_half" }));
        org.Apply(plan.Changes);

        var exported = SchemaExporter.Export(org.Entities, org.OptionSets, "xv");
        var column = exported.Tables.Single(t => t.Name == "xv_customer").Columns.Single(c => c.Name == "xv_ordertotal");
        column.Rollup.Should().Be(xaml);
        column.Calculated.Should().BeNull();
        Diff(SchemaYaml.Read(SchemaYaml.Write(exported)), org).Changes.Should().BeEmpty();
    }

    [Fact]
    public void A_changed_rollup_definition_is_an_update()
    {
        var org = new InMemoryOrg();
        org.Apply(Diff(Document(Xaml()), org).Changes);
        var changed = Xaml().Replace(">Sum<", ">Maximum<", StringComparison.Ordinal);

        var plan = Diff(Document(changed), org);

        plan.Rejections.Should().BeEmpty();
        var update = plan.Changes.OfType<UpdateAttributeRequest>().Should().ContainSingle().Subject;
        update.Attribute.LogicalName.Should().Be("xv_ordertotal");
        update.Attribute.SourceType.Should().Be(RollupColumns.Rollup);
        ComputedColumnRules.FormulaOf(update.Attribute).Should().Be(changed);
    }

    [Fact]
    public void A_column_cannot_switch_between_simple_calculated_and_rollup()
    {
        var org = new InMemoryOrg();
        org.Apply(Diff(Document(Xaml()), org).Changes);
        var document = Document(Xaml());
        var customer = document.Tables[0].Columns;
        customer.Single(c => c.Name == "xv_ordertotal").Rollup = null;
        customer.Single(c => c.Name == "xv_half").Calculated = null;
        customer.Single(c => c.Name == "xv_half").Rollup = Xaml();
        document.Tables[1].Columns.Single(c => c.Name == "xv_amount").Rollup = Xaml();

        var plan = Diff(document, org);

        plan.Rejections.Should().BeEquivalentTo([
            "Column 'xv_customer.xv_ordertotal' is a rollup column: it cannot become a simple column (the SourceType of a column cannot change).",
            "Column 'xv_customer.xv_half' is a calculated column: it cannot become a rollup column (the SourceType of a column cannot change).",
            "Column 'xv_order.xv_amount' is a simple column: it cannot become a rollup column (the SourceType of a column cannot change).",
        ]);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("memo")]
    [InlineData("bool")]
    [InlineData("choice")]
    [InlineData("lookup")]
    [InlineData("file")]
    public void Rollup_applies_only_to_the_types_a_rollup_can_have(string type)
    {
        var extra = type switch
        {
            "choice" => ", options: [{ value: 1, label: A }]",
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
                  - { name: xv_x, type: {{type}}, label: X, rollup: "<Activity />"{{extra}} }
            """;

        var act = () => SchemaYaml.Read(yaml);

        act.Should().Throw<SchemaFormatException>().WithMessage(
            $"*Column 'xv_order.xv_x': rollup applies only to the types int, decimal, money and datetime, not to '{type}'.*");
    }

    [Fact]
    public void A_column_is_not_calculated_and_rollup_at_once()
    {
        var column = new SchemaColumn { Name = "xv_total", Type = SchemaColumnTypes.Decimal, Calculated = "<Activity />", Rollup = "<Activity />" };

        SchemaColumnTypes.Validate(column).Should().Be("Column 'xv_total': a column is either calculated or rollup, not both.");
    }

    [Fact]
    public void An_empty_rollup_definition_is_refused()
    {
        var column = new SchemaColumn { Name = "xv_total", Type = SchemaColumnTypes.Decimal, Rollup = " " };

        SchemaColumnTypes.Validate(column).Should().Be("Column 'xv_total': rollup cannot be empty.");
    }

    [Fact]
    public void A_rollup_definition_a_literal_block_cannot_hold_reads_back_exactly()
    {
        var xaml = "﻿" + Xaml().ReplaceLineEndings("\r\n");

        var yaml = SchemaYaml.Write(Document(xaml));

        yaml.Should().NotContain("﻿").And.NotContain("\r");
        SchemaYaml.Read(yaml).Tables[0].Columns.Single(c => c.Name == "xv_ordertotal").Rollup.Should().Be(xaml);
    }
}
