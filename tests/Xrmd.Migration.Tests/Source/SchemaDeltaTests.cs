using System.Text.Json;
using FluentAssertions;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Source;

namespace Xrmd.Migration.Tests.Source;

public sealed class SchemaDeltaTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmpkg-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Customizations = """
        <ImportExportXml>
          <Entities>
            <Entity>
              <Name LocalizedName="Project" OriginalName="Project">xv_Project</Name>
              <EntityInfo><entity Name="xv_Project"><attributes>
                <attribute PhysicalName="xv_Type"><LogicalName>xv_type</LogicalName>
                  <optionset Name="xv_project_xv_type"><options><option value="100000000"/></options></optionset>
                </attribute>
              </attributes></entity></EntityInfo>
            </Entity>
            <Entity>
              <Name>Account</Name>
              <EntityInfo><entity Name="Account"><attributes>
                <attribute PhysicalName="xv_Score"><LogicalName>xv_score</LogicalName></attribute>
                <attribute PhysicalName="xv_CoveredTotal"><LogicalName>xv_coveredtotal</LogicalName></attribute>
                <attribute PhysicalName="xv_CoveredLabel"><LogicalName>xv_coveredlabel</LogicalName></attribute>
                <attribute PhysicalName="StatusCode"><LogicalName>statuscode</LogicalName>
                  <optionset Name="account_statuscode"><statuses><status value="100000000" state="0"/></statuses></optionset>
                </attribute>
              </attributes></entity></EntityInfo>
            </Entity>
          </Entities>
          <optionsets><optionset Name="xv_region"><options><option value="100000000"/></options></optionset></optionsets>
          <EntityRelationships><EntityRelationship Name="xv_covered_nn"/></EntityRelationships>
        </ImportExportXml>
        """;

    private static SourceColumn Col(string name, string type, bool custom = true, int sourceType = 0, params string[] targets) =>
        new(name, type, null, targets, custom, true, sourceType);

    private static readonly SourceTable Account = new("account", "accounts", "accountid", false, false, false,
    [
        Col("accountid", "Uniqueidentifier", false), Col("name", "String", false), Col("statuscode", "Status", false),
        Col("xv_score", "Integer"), Col("xv_rating", "Picklist"), Col("xv_parentprojectid", "Lookup", true, 0, "xv_project"),
        Col("xv_total", "Money", true, 2), Col("xv_label", "String", true, 3), Col("xv_double", "Decimal", true, 1),
        Col("xv_anything", "Lookup", true, 0, "account", "contact"), Col("xv_coveredtotal", "Money", true, 2),
        Col("xv_coveredlabel", "String", true, 3),
    ]);

    private static readonly SourceTable Project = new("xv_project", "xv_projects", "xv_projectid", true, false, false,
        [Col("xv_projectid", "Uniqueidentifier"), Col("xv_name", "String"), Col("xv_type", "Picklist"), Col("xv_region", "Picklist")]);

    private static readonly SourceTable Orphan = new("xv_orphan", "xv_orphans", "xv_orphanid", true, false, false,
        [Col("xv_orphanid", "Uniqueidentifier"), Col("xv_name", "String")]);

    private static readonly SourceTable External = new("xv_external", "xv_externals", "xv_externalid", true, false, false,
        [Col("xv_externalid", "Uniqueidentifier")], "Virtual");

    private static SourceOption Option(string table, string column, int value, string? set = null, int? state = null) =>
        new(table, column, set, value, state, JsonDocument.Parse("{\"Value\":" + value + ",\"Label\":{\"UserLocalizedLabel\":{\"Label\":\"v" + value + "\"}}}").RootElement.Clone());

    private static readonly SourceOption[] Options =
    [
        Option("account", "statuscode", 1, state: 0),
        Option("account", "statuscode", 100000000, state: 0),
        Option("account", "statuscode", 100000001, state: 0),
        Option("xv_project", "xv_type", 100000000),
        Option("xv_project", "xv_type", 100000005),
        Option("xv_project", "xv_region", 100000000, "xv_region"),
        Option("xv_project", "xv_region", 100000002, "xv_region"),
        Option("account", "xv_rating", 100000000),
        Option("account", "industrycode", 3),
    ];

    private static readonly SourceRelationship[] NN =
    [
        new("xv_account_contact", "xv_account_contact", "accountid", "contactid", "account", "contact", true),
        new("xv_covered_nn", "xv_covered_nn", "accountid", "xv_projectid", "account", "xv_project", true),
        new("accountleads_association", "accountleads", "accountid", "leadid", "account", "lead", false),
    ];

    private static SchemaDeltaPlan Compute() =>
        SchemaDelta.Compute([Account, Project, Orphan, External], NN, Options, [new MemoryStream(SolutionZip.Build("xv_core", "1.0.0.0", false, Customizations))]);

    [Fact]
    public void Delta_contains_only_uncovered_customizations()
    {
        var plan = Compute();
        plan.Tables.Should().Equal("xv_orphan");
        plan.Columns.Select(c => $"{c.Table}.{c.Column}").Should().BeEquivalentTo(
            "account.xv_rating", "account.xv_parentprojectid", "account.xv_double",
            "xv_project.xv_projectid", "xv_project.xv_name", "xv_project.xv_region",
            "xv_orphan.xv_orphanid", "xv_orphan.xv_name");
        plan.Relationships.Should().Equal("xv_account_contact");
        plan.StatusValues.Select(o => o.Value).Should().Equal(100000001);
        plan.OptionValues.Select(o => $"{o.Table}.{o.Column}:{o.Value}").Should().BeEquivalentTo("xv_project.xv_type:100000005", "xv_project.xv_region:100000002");
    }

    [Fact]
    public void Rollup_formula_virtual_and_multi_table_lookups_go_to_unsupported()
    {
        var plan = Compute();
        plan.Unsupported.Select(u => $"{u.Kind}:{u.Name}").Should().BeEquivalentTo(
            "column:account.xv_total", "column:account.xv_label", "table:xv_external", "column:account.xv_anything");
        plan.Unsupported.Single(u => u.Name == "account.xv_total").Reason
            .Should().Be("rollup column outside the exported solutions: import it with its solution");
        plan.Unsupported.Single(u => u.Name == "account.xv_label").Reason
            .Should().Be("formula column outside the exported solutions: import it with its solution");
        plan.Columns.Should().NotContain(c => c.Column == "xv_total" || c.Column == "xv_label" || c.Column == "xv_anything");
        // A rollup or formula column an exported solution carries comes with it (plan 2.12c): neither unsupported nor in the delta.
        plan.Columns.Should().NotContain(c => c.Column == "xv_coveredtotal" || c.Column == "xv_coveredlabel");
    }

    [Fact]
    public void Intersect_tables_come_with_their_relationship()
    {
        var intersect = new SourceTable("xv_account_contact", "xv_account_contactset", "xv_account_contactid", true, false, true,
            [Col("xv_account_contactid", "Uniqueidentifier"), Col("accountid", "Uniqueidentifier")]);
        var plan = SchemaDelta.Compute([intersect], NN, [], new SolutionCoverage());
        plan.Tables.Should().BeEmpty();
        plan.Columns.Should().BeEmpty();
        plan.Relationships.Should().Contain("xv_account_contact");
    }

    [Fact]
    public void Calculated_columns_are_carried()
    {
        Compute().Columns.Should().Contain(c => c.Column == "xv_double");
    }

    [Fact]
    public async Task Writes_delta_with_source_metadata()
    {
        var fake = new FakeDataverse()
            .GetJson(@"EntityDefinitions\(LogicalName='xv_orphan'\)\?", """{"LogicalName":"xv_orphan","SchemaName":"xv_Orphan","PrimaryNameAttribute":"xv_name"}""")
            .GetJson(@"EntityDefinitions\(LogicalName='account'\)/Attributes\(LogicalName='xv_parentprojectid'\)$",
                """{"@odata.type":"#Microsoft.Dynamics.CRM.LookupAttributeMetadata","LogicalName":"xv_parentprojectid","Targets":["xv_project"]}""")
            .GetJson(@"EntityDefinitions\(LogicalName='account'\)/ManyToOneRelationships\?\$filter=ReferencingAttribute eq 'xv_parentprojectid'",
                """{"value":[{"SchemaName":"xv_project_account","ReferencedEntity":"xv_project"}]}""")
            .GetJson(@"EntityDefinitions\(LogicalName='account'\)/Attributes\(LogicalName='xv_rating'\)/Microsoft\.Dynamics\.CRM\.PicklistAttributeMetadata\?\$expand=OptionSet,GlobalOptionSet",
                """{"LogicalName":"xv_rating","OptionSet":{"Options":[{"Value":100000000}]}}""")
            .GetJson(@"EntityDefinitions\(LogicalName='[a-z_]+'\)/Attributes\(LogicalName='[a-z_]+'\)", """{"LogicalName":"other"}""")
            .GetJson(@"RelationshipDefinitions\(SchemaName='xv_account_contact'\)", """{"SchemaName":"xv_account_contact"}""");
        var folder = Path.Combine(_root, "pkg");
        using (var writer = PackageWriter.Create(folder, SyntheticPackage.Passphrase))
        using (var client = new DataverseClient(fake, FakeDataverse.Source, _ => Task.FromResult("t"), (_, _) => Task.CompletedTask))
        {
            await SchemaDelta.WriteAsync(client, writer, Compute(), CancellationToken.None);
            writer.Complete(new PackageManifest());
        }
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
        using var reader = PackageReader.Open(folder, SyntheticPackage.Passphrase);
        using var delta = await JsonDocument.ParseAsync(reader.OpenEntry(SchemaDelta.DeltaPath));
        var root = delta.RootElement;
        root.GetProperty("tables")[0].GetProperty("metadata").GetProperty("SchemaName").GetString().Should().Be("xv_Orphan");
        var lookup = root.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("column").GetString() == "xv_parentprojectid");
        lookup.GetProperty("relationship").GetProperty("SchemaName").GetString().Should().Be("xv_project_account");
        var rating = root.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("column").GetString() == "xv_rating");
        rating.GetProperty("metadata").GetProperty("OptionSet").GetProperty("Options").GetArrayLength().Should().Be(1);
        root.GetProperty("relationships")[0].GetProperty("metadata").GetProperty("SchemaName").GetString().Should().Be("xv_account_contact");
        root.GetProperty("statusValues")[0].GetProperty("value").GetInt32().Should().Be(100000001);
        root.GetProperty("statusValues")[0].GetProperty("state").GetInt32().Should().Be(0);
        root.GetProperty("optionValues").GetArrayLength().Should().Be(2);
        using var unsupported = await JsonDocument.ParseAsync(reader.OpenEntry(SchemaDelta.UnsupportedPath));
        unsupported.RootElement.GetArrayLength().Should().Be(4);
    }

    [Fact]
    public void Managed_and_first_party_schema_is_never_rebuilt()
    {
        var table = new SourceTable("account", "accounts", "accountid", false, false, false,
        [
            Col("accountid", "Uniqueidentifier", false),
            new("isv_score", "Integer", null, [], true, true, 0, IsManaged: true),
            Col("msdyn_segment", "String"),
            Col("xv_mine", "String"),
        ]);
        var isvTable = new SourceTable("isv_thing", "isv_things", "isv_thingid", true, false, false, [new("isv_thingid", "Uniqueidentifier", null, [], true, true, 0, IsManaged: true)], IsManaged: true);
        var managedOption = new SourceOption("account", "industrycode", null, 192350000, null,
            JsonDocument.Parse("""{"Value":192350000,"IsManaged":true}""").RootElement.Clone());
        var plan = SchemaDelta.Compute([table, isvTable], [], [managedOption], new SolutionCoverage());
        plan.Tables.Should().BeEmpty();
        plan.Columns.Select(c => c.Column).Should().Equal("xv_mine");
        plan.OptionValues.Should().BeEmpty();
    }
}
