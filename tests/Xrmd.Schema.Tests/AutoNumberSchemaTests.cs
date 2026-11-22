using FluentAssertions;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Schema.Tests;

/// <summary>The <c>autoNumberFormat</c> of a string column and of a primary name in the schema YAML (autonumber plan, ruling N8).</summary>
public class AutoNumberSchemaTests
{
    private const string Yaml = """
        publisher: xv
        tables:
          - name: xv_order
            label: Order
            pluralLabel: Orders
            ownership: user
            primaryName: { name: xv_number, label: Number, maxLength: 20, autoNumberFormat: "ORD-{SEQNUM:5}" }
            columns:
              - { name: xv_code, type: string, maxLength: 50, label: Code, autoNumberFormat: "C-{RANDSTRING:4}" }
        """;

    private static SchemaPlan Diff(SchemaDocument document, InMemoryOrg org) => SchemaDiff.Compute(document, org.Entities, org.OptionSets);

    [Fact]
    public void Read_and_write_keep_the_format()
    {
        var document = SchemaYaml.Read(Yaml);

        document.Tables[0].PrimaryName.AutoNumberFormat.Should().Be("ORD-{SEQNUM:5}");
        document.Tables[0].Columns[0].AutoNumberFormat.Should().Be("C-{RANDSTRING:4}");
        SchemaYaml.Read(SchemaYaml.Write(document)).Should().BeEquivalentTo(document);
    }

    [Fact]
    public void Apply_creates_the_formats_and_export_reads_them_back()
    {
        var org = new InMemoryOrg();
        var plan = Diff(SchemaYaml.Read(Yaml), org);
        plan.Rejections.Should().BeEmpty();
        plan.Changes.OfType<CreateEntityRequest>().Single().PrimaryAttribute.AutoNumberFormat.Should().Be("ORD-{SEQNUM:5}");
        plan.Changes.OfType<CreateAttributeRequest>().Single().Attribute.AutoNumberFormat.Should().Be("C-{RANDSTRING:4}");
        org.Apply(plan.Changes);

        var exported = SchemaExporter.Export(org.Entities, org.OptionSets, "xv");

        exported.Tables[0].PrimaryName.AutoNumberFormat.Should().Be("ORD-{SEQNUM:5}");
        exported.Tables[0].Columns.Single(c => c.Name == "xv_code").AutoNumberFormat.Should().Be("C-{RANDSTRING:4}");
        Diff(SchemaYaml.Read(SchemaYaml.Write(exported)), org).Changes.Should().BeEmpty();
    }

    [Fact]
    public void A_changed_format_is_an_update()
    {
        var org = new InMemoryOrg();
        org.Apply(Diff(SchemaYaml.Read(Yaml), org).Changes);

        var plan = Diff(SchemaYaml.Read(Yaml.Replace("ORD-{SEQNUM:5}", "O-{SEQNUM:7}", StringComparison.Ordinal)), org);

        plan.Rejections.Should().BeEmpty();
        var update = plan.Changes.OfType<UpdateAttributeRequest>().Should().ContainSingle().Subject;
        update.Attribute.LogicalName.Should().Be("xv_number");
        update.Attribute.AutoNumberFormat.Should().Be("O-{SEQNUM:7}");
        org.Apply(plan.Changes);
        org.Attribute("xv_order", "xv_number").AutoNumberFormat.Should().Be("O-{SEQNUM:7}");
    }

    [Fact]
    public void A_format_on_another_type_is_an_error()
    {
        var act = () => SchemaYaml.Read(Yaml.Replace("type: string, maxLength: 50", "type: int", StringComparison.Ordinal));

        act.Should().Throw<SchemaFormatException>().WithMessage("*autoNumberFormat applies only to the type string*");
    }
}
