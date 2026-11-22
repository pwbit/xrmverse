using FluentAssertions;
using Xrmd.Migration.Source;

namespace Xrmd.Migration.Tests.Source;

public class SourceMetadataTests
{
    internal const string Definitions = """
    {"value":[
      {"LogicalName":"account","EntitySetName":"accounts","PrimaryIdAttribute":"accountid","IsCustomEntity":false,"IsActivity":false,"IsIntersect":false,
       "Attributes":[
         {"LogicalName":"accountid","AttributeType":"Uniqueidentifier","IsCustomAttribute":false,"IsValidForRead":true,"AttributeOf":null},
         {"LogicalName":"name","AttributeType":"String","IsCustomAttribute":false,"IsValidForRead":true,"AttributeOf":null},
         {"LogicalName":"parentaccountid","AttributeType":"Lookup","IsCustomAttribute":false,"IsValidForRead":true,"AttributeOf":null},
         {"LogicalName":"parentaccountidname","AttributeType":"String","IsCustomAttribute":false,"IsValidForRead":true,"AttributeOf":"parentaccountid"},
         {"LogicalName":"xv_founded","AttributeType":"DateTime","IsCustomAttribute":true,"IsValidForRead":true,"AttributeOf":null},
         {"LogicalName":"xv_secret","AttributeType":"String","IsCustomAttribute":true,"IsValidForRead":false,"AttributeOf":null}
       ]},
      {"LogicalName":"msdyn_project","EntitySetName":"msdyn_projects","PrimaryIdAttribute":"msdyn_projectid","IsCustomEntity":true,"IsActivity":false,"IsIntersect":false,"Attributes":[]},
      {"LogicalName":"xv_account_contact","EntitySetName":"xv_account_contactset","PrimaryIdAttribute":"xv_account_contactid","IsCustomEntity":true,"IsActivity":false,"IsIntersect":true,"Attributes":[]}
    ]}
    """;

    internal static FakeDataverse WithMetadata(FakeDataverse fake) => fake
        .GetJson(@"^/api/data/v9\.2/EntityDefinitions\?", Definitions)
        .GetJson(@"EntityDefinitions\(LogicalName='account'\)/Attributes/Microsoft\.Dynamics\.CRM\.DateTimeAttributeMetadata",
            """{"value":[{"LogicalName":"xv_founded","DateTimeBehavior":{"Value":"DateOnly"}}]}""")
        .GetJson(@"EntityDefinitions\(LogicalName='account'\)/Attributes/Microsoft\.Dynamics\.CRM\.LookupAttributeMetadata",
            """{"value":[{"LogicalName":"parentaccountid","Targets":["account"]}]}""")
        .GetJson(@"EntityDefinitions\(LogicalName='[a-z_]+'\)/Attributes/", """{"value":[]}""")
        .GetJson(@"RelationshipDefinitions/Microsoft\.Dynamics\.CRM\.ManyToManyRelationshipMetadata", """
            {"value":[{"SchemaName":"xv_account_contact","IntersectEntityName":"xv_account_contact","Entity1LogicalName":"account","Entity1IntersectAttribute":"accountid","Entity2LogicalName":"contact","Entity2IntersectAttribute":"contactid"}]}
            """);

    private static DataverseClient Client(FakeDataverse fake) => new(fake, FakeDataverse.Source, _ => Task.FromResult("t"), (_, _) => Task.CompletedTask);

    [Fact]
    public void Catalog_finds_exact_and_prefix_rules()
    {
        ExclusionCatalog.Find("asyncoperation")!.Category.Should().Be("runtime");
        ExclusionCatalog.Find("msdyn_project")!.Category.Should().Be("first-party");
        ExclusionCatalog.Find("msdyn_project")!.Reason.Should().Be("Microsoft first-party app");
        ExclusionCatalog.Find("account").Should().BeNull();
        ExclusionCatalog.Find("xv_project").Should().BeNull();
    }

    [Fact]
    public void Not_supported_yet_tables_are_marked_and_not_optional()
    {
        var queue = ExclusionCatalog.Find("queue")!;
        queue.Category.Should().Be("not-supported-yet");
        queue.Optional.Should().BeFalse();
    }

    /// <summary>
    /// Plan 3.5 UI-1g: the three post tables are not migrated yet; <c>postregarding</c> above all, which the server keeps and every
    /// Create of which it refuses (an imported row would fail one by one).
    /// </summary>
    [Theory]
    [InlineData("post")]
    [InlineData("postcomment")]
    [InlineData("postregarding")]
    public void Post_tables_are_not_supported_yet(string table)
    {
        var exclusion = ExclusionCatalog.Find(table);
        exclusion.Should().NotBeNull(table);
        exclusion!.Category.Should().Be(ExclusionCatalog.NotSupportedYet);
        exclusion.Optional.Should().BeFalse();
    }

    [Fact]
    public async Task Reads_tables_with_columns_targets_and_date_behavior()
    {
        using var client = Client(WithMetadata(new FakeDataverse()));
        var tables = await SourceMetadata.ReadTablesAsync(client, t => ExclusionCatalog.Find(t) is null, CancellationToken.None);
        var account = tables.Single(t => t.LogicalName == "account");
        account.EntitySetName.Should().Be("accounts");
        account.PrimaryKey.Should().Be("accountid");
        account.Columns.Single(c => c.LogicalName == "parentaccountid").Targets.Should().Equal("account");
        account.Columns.Single(c => c.LogicalName == "xv_founded").DateBehavior.Should().Be("DateOnly");
        tables.Single(t => t.LogicalName == "xv_account_contact").IsIntersect.Should().BeTrue();
    }

    [Fact]
    public async Task Drops_companion_and_unreadable_columns()
    {
        using var client = Client(WithMetadata(new FakeDataverse()));
        var account = (await SourceMetadata.ReadTablesAsync(client, _ => true, CancellationToken.None)).Single(t => t.LogicalName == "account");
        account.Columns.Select(c => c.LogicalName).Should().NotContain(["parentaccountidname", "xv_secret"]);
    }

    [Fact]
    public async Task Typed_attribute_details_are_read_only_for_tables_in_scope()
    {
        var fake = WithMetadata(new FakeDataverse());
        using var client = Client(fake);
        await SourceMetadata.ReadTablesAsync(client, t => ExclusionCatalog.Find(t) is null, CancellationToken.None);
        fake.Requests.Should().NotContain(r => r.Url.Contains("msdyn_project"));
    }

    [Fact]
    public async Task Reads_many_to_many_intersects()
    {
        using var client = Client(WithMetadata(new FakeDataverse()));
        var nn = await SourceMetadata.ReadManyToManyAsync(client, CancellationToken.None);
        nn.Should().ContainSingle().Which.Should().Be(new SourceRelationship("xv_account_contact", "xv_account_contact", "accountid", "contactid", "account", "contact"));
    }

    [Fact]
    public async Task Reads_local_global_and_status_option_values()
    {
        var fake = new FakeDataverse()
            .GetJson(@"Attributes/Microsoft\.Dynamics\.CRM\.PicklistAttributeMetadata", """
                {"value":[
                  {"LogicalName":"xv_type","OptionSet":{"Name":"account_xv_type","IsGlobal":false,"Options":[{"Value":100000000,"Label":{}}]},"GlobalOptionSet":null},
                  {"LogicalName":"xv_region","OptionSet":{"Name":"xv_region","IsGlobal":true,"Options":[{"Value":100000001}]},"GlobalOptionSet":{"Name":"xv_region","Options":[{"Value":100000001}]}}
                ]}
                """)
            .GetJson(@"Attributes/Microsoft\.Dynamics\.CRM\.StatusAttributeMetadata", """
                {"value":[{"LogicalName":"statuscode","OptionSet":{"Name":"account_statuscode","IsGlobal":false,"Options":[{"Value":1,"State":0},{"Value":100000002,"State":1}]}}]}
                """)
            .GetJson(@"Attributes/Microsoft\.Dynamics\.CRM\.MultiSelectPicklistAttributeMetadata", """{"value":[]}""");
        using var client = Client(fake);
        var table = new SourceTable("account", "accounts", "accountid", false, false, false, []);
        var options = await SourceMetadata.ReadOptionsAsync(client, [table], CancellationToken.None);
        options.Select(o => $"{o.Column}|{o.OptionSet}|{o.Value}|{o.State}").Should().Equal(
            "xv_type||100000000|", "xv_region|xv_region|100000001|", "statuscode||1|0", "statuscode||100000002|1");
    }

    [Fact]
    public async Task Local_option_set_expanded_as_global_option_set_stays_local()
    {
        // M6 run: the trial returned the local option set of a choice under GlobalOptionSet as well, with IsGlobal false.
        var fake = new FakeDataverse()
            .GetJson(@"Attributes/Microsoft\.Dynamics\.CRM\.PicklistAttributeMetadata", """
                {"value":[
                  {"LogicalName":"xv_type","OptionSet":{"Name":"account_xv_type","IsGlobal":false,"Options":[{"Value":100000000}]},
                   "GlobalOptionSet":{"Name":"account_xv_type","IsGlobal":false,"Options":[{"Value":100000000}]}}
                ]}
                """)
            .GetJson(@"Attributes/Microsoft\.Dynamics\.CRM\.StatusAttributeMetadata", """{"value":[]}""")
            .GetJson(@"Attributes/Microsoft\.Dynamics\.CRM\.MultiSelectPicklistAttributeMetadata", """{"value":[]}""");
        using var client = Client(fake);
        var table = new SourceTable("account", "accounts", "accountid", false, false, false, []);
        var options = await SourceMetadata.ReadOptionsAsync(client, [table], CancellationToken.None);
        options.Should().ContainSingle().Which.OptionSet.Should().BeNull();
    }
}
