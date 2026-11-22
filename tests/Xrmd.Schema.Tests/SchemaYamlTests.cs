using FluentAssertions;

namespace Xrmd.Schema.Tests;

public class SchemaYamlTests
{
    internal const string Sample = """
        publisher: xv            # prefix; every name must start with "xv_"
        solution: XrmverseFixture   # optional: SolutionUniqueName for the creates
        choices:
          - name: xv_priority
            label: Priority
            options: [{ value: 100000000, label: Low }, { value: 100000001, label: High }]
        tables:
          - name: xv_project
            label: Project
            pluralLabel: Projects
            ownership: user        # user | organization
            primaryName: { name: xv_name, label: Name, maxLength: 200 }
            columns:
              - { name: xv_budget, type: money, label: Budget }
              - { name: xv_priority, type: choice, choice: xv_priority }
              - { name: xv_parent, type: lookup, target: xv_project, label: Parent }
            manyToMany:
              - { name: xv_project_xv_tag, with: xv_tag }
          - name: xv_tag
            schemaName: xv_Tag
            label: Tag
            pluralLabel: Tags
            description: "A label: free text"
            ownership: organization
            primaryName: { name: xv_name, label: Name }
            columns:
              - { name: xv_code, schemaName: xv_Code, type: string, maxLength: 20, label: Code, description: Short code }
              - { name: xv_notes, type: memo, maxLength: 2000, label: Notes }
              - { name: xv_rank, type: int, label: Rank }
              - { name: xv_weight, type: decimal, precision: 3, label: Weight }
              - { name: xv_active, type: bool, label: Active }
              - { name: xv_since, type: datetime, label: Since }
              - { name: xv_color, type: choice, label: Color, options: [{ value: 1, label: Red }, { value: 2, label: Green }] }
              - { name: xv_sizes, type: choices, label: Sizes, options: [{ value: 1, label: S }, { value: 2, label: M }] }
        """;

    [Fact]
    public void Read_parses_the_documented_format()
    {
        var document = SchemaYaml.Read(Sample);

        document.Publisher.Should().Be("xv");
        document.Solution.Should().Be("XrmverseFixture");
        document.Choices.Should().ContainSingle().Which.Options.Select(o => (o.Value, o.Label))
            .Should().Equal((100000000, "Low"), (100000001, "High"));
        var project = document.Tables[0];
        (project.Name, project.Label, project.PluralLabel, project.Ownership).Should().Be(("xv_project", "Project", "Projects", SchemaOwnership.User));
        (project.PrimaryName.Name, project.PrimaryName.Label, project.PrimaryName.MaxLength).Should().Be(("xv_name", "Name", 200));
        project.Columns.Select(c => (c.Name, c.Type)).Should().Equal(("xv_budget", "money"), ("xv_priority", "choice"), ("xv_parent", "lookup"));
        project.Columns[1].Choice.Should().Be("xv_priority");
        project.Columns[2].Target.Should().Be("xv_project");
        project.ManyToMany.Should().ContainSingle().Which.With.Should().Be("xv_tag");
        var tag = document.Tables[1];
        (tag.SchemaName, tag.Ownership, tag.Description).Should().Be(("xv_Tag", SchemaOwnership.Organization, "A label: free text"));
        tag.Columns[0].SchemaName.Should().Be("xv_Code");
        tag.Columns[0].MaxLength.Should().Be(20);
        tag.Columns[3].Precision.Should().Be(3);
        tag.Columns[6].Options!.Select(o => o.Label).Should().Equal("Red", "Green");
    }

    [Fact]
    public void Elastic_table_type_round_trips_and_standard_is_omitted()
    {
        var yaml = """
            publisher: xv
            tables:
              - name: xv_sensor
                label: Sensor
                tableType: ELASTIC
                primaryName: { name: xv_name }
              - name: xv_plain
                label: Plain
                tableType: standard
                primaryName: { name: xv_name }
              - name: xv_default
                label: Default
                primaryName: { name: xv_name }
            """;

        var document = SchemaYaml.Read(yaml);

        document.Tables.Select(t => t.TableType).Should().Equal("Elastic", "Standard", "Standard");
        var written = SchemaYaml.Write(document);
        written.Should().Contain("tableType: Elastic");
        written.Split("tableType").Length.Should().Be(2, "only the elastic table writes the key");
        SchemaYaml.Read(written).Should().BeEquivalentTo(document, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Unknown_table_type_is_an_error_with_line()
    {
        var yaml = """
            publisher: xv
            tables:
              - name: xv_sensor
                label: Sensor
                tableType: Virtual
                primaryName: { name: xv_name }
            """;

        var error = Assert.Throws<SchemaFormatException>(() => SchemaYaml.Read(yaml));

        error.Message.Should().Contain("Virtual");
        error.Line.Should().Be(5);
    }

    [Fact]
    public void Read_write_roundtrip()
    {
        var document = SchemaYaml.Read(Sample);

        var written = SchemaYaml.Write(document);
        var again = SchemaYaml.Read(written);

        again.Should().BeEquivalentTo(document, o => o.WithStrictOrdering());
        SchemaYaml.Write(again).Should().Be(written);
    }

    [Fact]
    public void Write_quotes_labels_that_would_read_as_something_else()
    {
        var document = SchemaYaml.Read(Sample);
        document.Tables[0].Label = "Project: #1, [draft]";
        document.Tables[0].PluralLabel = "true";
        document.Choices[0].Options[0].Label = "100";

        SchemaYaml.Read(SchemaYaml.Write(document)).Should().BeEquivalentTo(document, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Unknown_key_is_an_error_with_line()
    {
        var yaml = """
            publisher: xv
            tables:
              - name: xv_project
                label: Project
                primaryName: { name: xv_name }
                colums: []
            """;

        var error = Assert.Throws<SchemaFormatException>(() => SchemaYaml.Read(yaml));

        error.Message.Should().Contain("colums");
        (error.Line, error.Column).Should().Be((6, 5));
        error.Message.Should().StartWith("(line 6, column 5)");
    }

    [Fact]
    public void Name_without_publisher_prefix_is_rejected()
    {
        var yaml = """
            publisher: xv
            tables:
              - name: xv_project
                label: Project
                primaryName: { name: xv_name }
                columns:
                  - { name: new_budget, type: money }
            """;

        var error = Assert.Throws<SchemaFormatException>(() => SchemaYaml.Read(yaml));

        error.Message.Should().Contain("new_budget").And.Contain("xv_");
        error.Line.Should().Be(7);
    }

    [Theory]
    [InlineData("{ name: xv_x, type: double }", "double")]
    [InlineData("{ name: xv_x, type: String }", "String")]
    [InlineData("{ name: xv_x, type: money, maxLength: 10 }", "maxLength")]
    [InlineData("{ name: xv_x, type: string, precision: 2 }", "precision")]
    [InlineData("{ name: xv_x, type: lookup }", "target")]
    [InlineData("{ name: xv_x, type: choice }", "choice")]
    [InlineData("{ name: xv_x, type: choice, choice: xv_c, options: [] }", "not both")]
    [InlineData("{ name: xv_x, type: int, target: xv_project }", "target")]
    [InlineData("{ name: xv_x, type: string, maxLength: big }", "big")]
    [InlineData("{ name: xv_x, type: string, maxLength: 0 }", "positive")]
    [InlineData("{ name: xv_X, type: int }", "xv_X")]
    [InlineData("{ name: xv_x }", "type")]
    [InlineData("{ name: xv_x, type: int, name: xv_y }", "name")]
    [InlineData("[xv_x]", "mapping")]
    public void Invalid_column_is_an_error_naming_it(string column, string named)
    {
        var yaml = $"""
            publisher: xv
            tables:
              - name: xv_project
                label: Project
                primaryName: {"{"} name: xv_name {"}"}
                columns:
                  - {column}
            """;

        var error = Assert.Throws<SchemaFormatException>(() => SchemaYaml.Read(yaml));

        error.Message.Should().Contain(named);
        error.Line.Should().Be(7);
    }

    [Theory]
    [InlineData("tables: []", "publisher")]
    [InlineData("publisher: xv\ntables:\n  - name: xv_a\n    primaryName: { name: xv_name }", "label")]
    [InlineData("publisher: xv\ntables:\n  - name: xv_a\n    label: A", "primaryName")]
    [InlineData("publisher: xv\ntables:\n  - name: xv_a\n    label: A\n    ownership: team\n    primaryName: { name: xv_name }", "team")]
    [InlineData("publisher: xv\nchoices:\n  - name: xv_c\n    options: [{ value: 1 }]", "label")]
    [InlineData("publisher: xv\nchoices:\n  - name: xv_c\n    options: [{ value: 1, label: A }, { value: 1, label: B }]", "1")]
    [InlineData("publisher: xv\ntables:\n  - { name: xv_a, label: A, primaryName: { name: xv_name } }\n  - { name: xv_a, label: B, primaryName: { name: xv_name } }", "xv_a")]
    [InlineData("publisher: xv\ntables:\n  - name: xv_a\n    label: A\n    primaryName: { name: xv_name }\n    columns:\n      - { name: xv_name, type: int }", "xv_name")]
    [InlineData("publisher: xv\ntables:\n  - name: xv_a\n    label: A\n    primaryName: { name: xv_name }\n    manyToMany:\n      - { name: new_ab, with: xv_b }", "new_ab")]
    [InlineData("publisher: xv\npublisher: xw", "publisher")]
    [InlineData("publisher: xv\ntables: [", "node content")]
    [InlineData("- a\n- b", "mapping")]
    [InlineData("", "empty")]
    public void Invalid_document_is_an_error(string yaml, string named)
    {
        var error = Assert.Throws<SchemaFormatException>(() => SchemaYaml.Read(yaml));

        error.Message.Should().Contain(named);
        error.Line.Should().BePositive();
        error.Column.Should().BePositive();
    }

    internal const string ActivitySample = """
        publisher: xv
        tables:
          - name: xv_sms
            label: SMS
            pluralLabel: SMS messages
            ownership: user
            isActivity: true
            hasNotes: true
            primaryName: { name: subject, label: Subject }
            columns:
              - { name: xv_number, type: string, maxLength: 20, label: Number }
          - name: xv_site
            label: Site
            ownership: user
            hasNotes: true
            hasActivities: true
            primaryName: { name: xv_name, label: Name }
          - name: xv_plain
            label: Plain
            ownership: user
            isActivity: false
            hasNotes: false
            hasActivities: false
            primaryName: { name: xv_name, label: Name }
        """;

    [Fact]
    public void Yaml_round_trips_isActivity_and_flags()
    {
        var document = SchemaYaml.Read(ActivitySample);

        var sms = document.Tables[0];
        (sms.IsActivity, sms.HasNotes, sms.HasActivities).Should().Be((true, true, (bool?)null));
        // The primary column of an activity is 'subject', without the publisher prefix (SchemaName 'Subject').
        sms.PrimaryName.Name.Should().Be("subject");
        var site = document.Tables[1];
        (site.IsActivity, site.HasNotes, site.HasActivities).Should().Be((false, true, true));
        var plain = document.Tables[2];
        (plain.IsActivity, plain.HasNotes, plain.HasActivities).Should().Be((false, false, false));

        var written = SchemaYaml.Write(document);
        written.Should().Contain("isActivity: true").And.Contain("hasNotes: true").And.Contain("hasActivities: true").And.Contain("hasNotes: false");
        // isActivity false is the default and is not written.
        written.Should().NotContain("isActivity: false");
        var again = SchemaYaml.Read(written);
        again.Should().BeEquivalentTo(document, o => o.WithStrictOrdering());
        SchemaYaml.Write(again).Should().Be(written);
    }

    [Theory]
    [InlineData("isActivity: true\n    primaryName: { name: xv_name }", "subject")]
    [InlineData("isActivity: true\n    hasActivities: true\n    primaryName: { name: subject }", "hasActivities")]
    [InlineData("isActivity: true\n    hasNotes: false\n    primaryName: { name: subject }", "hasNotes")]
    [InlineData("isActivity: true\n    ownership: organization\n    primaryName: { name: subject }", "ownership")]
    [InlineData("primaryName: { name: subject }", "xv_")]
    [InlineData("isActivity: maybe\n    primaryName: { name: xv_name }", "isActivity")]
    public void Activity_settings_that_cannot_be_applied_are_format_errors(string settings, string named)
    {
        var yaml = "publisher: xv\ntables:\n  - name: xv_t\n    label: T\n    " + settings + "\n";

        var error = Assert.Throws<SchemaFormatException>(() => SchemaYaml.Read(yaml));

        error.Message.Should().Contain(named);
    }
}
