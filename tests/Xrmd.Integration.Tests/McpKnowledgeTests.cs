using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Mcp;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>D3, task 3: <c>describe_table</c> gives descriptions, column synonyms and glossary terms; <c>list_glossary_terms</c>.</summary>
[Collection("xrmd")]
public sealed class McpKnowledgeTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly List<Guid> _sources = [];
    private Func<Task>? _restoreDescriptions;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => TestEntities.EnsureProjectAsync(Store);

    /// <summary>
    /// Deletes the test's sources (cascading to their rows), so that no test sees another's synonyms of <c>pb_name</c>, and puts
    /// back the descriptions of <c>pb_project</c> and <c>pb_name</c> (shared by the collection) when the test changed them.
    /// </summary>
    public async Task DisposeAsync()
    {
        foreach (var source in _sources) await Admin.DeleteAsync(KnowledgeSchema.Source, source);
        if (_restoreDescriptions is not null) await _restoreDescriptions();
    }

    /// <summary>Sets the descriptions of <c>pb_project</c> and <c>pb_name</c>; <see cref="DisposeAsync"/> restores the previous ones.</summary>
    private async Task DescribeProjectAsync(string table, string column)
    {
        var entity = Store.GetEntity(TestEntities.Project)!;
        string? Text(Label? label) => label?.UserLocalizedLabel?.Label ?? label?.LocalizedLabels?.FirstOrDefault()?.Label;
        var tableBefore = Text(entity.Description) ?? "";
        var columnBefore = Text(entity.Attributes.Single(a => a.LogicalName == "pb_name").Description) ?? "";
        _restoreDescriptions = () => SetDescriptionsAsync(tableBefore, columnBefore);
        await SetDescriptionsAsync(table, column);
    }

    private async Task SetDescriptionsAsync(string table, string column)
    {
        await Admin.ExecuteAsync(new UpdateEntityRequest
        {
            Entity = new EntityMetadata { LogicalName = TestEntities.Project, Description = Label(table) },
        });
        await Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = TestEntities.Project, Attribute = new StringAttributeMetadata { LogicalName = "pb_name", Description = Label(column) },
        });
    }

    private async Task<McpClient> ConnectAsync(Guid oid)
    {
        var http = fx.App.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(fx.BaseUrl + "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<JsonObject> CallJsonAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        result.IsError.Should().NotBe(true, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    private async Task<JsonObject> DescribeAsync(Guid? oid = null)
    {
        await using var client = await ConnectAsync(oid ?? fx.AdminObjectId);
        return await CallJsonAsync(client, "describe_table", new() { ["table"] = TestEntities.Project });
    }

    private async Task<JsonObject> ResourceAsync()
    {
        await using var client = await ConnectAsync(fx.AdminObjectId);
        var read = await client.ReadResourceAsync("xrmverse://tables/" + TestEntities.Project);
        return JsonNode.Parse(read.Contents.OfType<TextResourceContents>().Single().Text)!.AsObject();
    }

    private async Task<string[]> TermNamesAsync(Dictionary<string, object?> args, Guid? oid = null)
    {
        await using var client = await ConnectAsync(oid ?? fx.AdminObjectId);
        return (await CallJsonAsync(client, "list_glossary_terms", args))["terms"]!.AsArray().Select(t => t!["term"]!.GetValue<string>()).ToArray();
    }

    private async Task<Guid> RestrictedUserAsync(params (string Privilege, DepthMask Depth)[] more)
    {
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await AssignRoleAsync(fx, user, await CreateRoleAsync(fx, "mcpk-" + Guid.NewGuid().ToString("N")[..8],
            [("prvReadpb_Project", DepthMask.Global), .. more]));
        return oid;
    }

    private static JsonObject ColumnOf(JsonObject table, string name) =>
        table["columns"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!.AsObject();

    private static string[] Strings(JsonNode? array) => array?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];

    private async Task<Guid> SourceAsync(string name)
    {
        var id = await Admin.CreateAsync(new Entity(KnowledgeSchema.Source) { [KnowledgeSchema.Name] = name });
        _sources.Add(id);
        return id;
    }

    private Task<Guid> SourceEntityAsync(Guid source, string table) => Admin.CreateAsync(new Entity(KnowledgeSchema.SourceEntity)
    {
        [KnowledgeSchema.Name] = table + " " + _run, [KnowledgeSchema.SourceLookup] = new EntityReference(KnowledgeSchema.Source, source),
        [KnowledgeSchema.EntityLogicalName] = table,
    });

    private Task<Guid> SynonymsAsync(Guid sourceEntity, string column, string text) => Admin.CreateAsync(new Entity(KnowledgeSchema.Synonyms)
    {
        [KnowledgeSchema.SkillEntity] = new EntityReference(KnowledgeSchema.SourceEntity, sourceEntity),
        [KnowledgeSchema.ColumnLogicalName] = column, [KnowledgeSchema.SynonymsText] = text,
    });

    private Task<Guid> TermAsync(Guid source, string term, string? description = null) => Admin.CreateAsync(new Entity(KnowledgeSchema.GlossaryTerm)
    {
        [KnowledgeSchema.Skill] = new EntityReference(KnowledgeSchema.Source, source), [KnowledgeSchema.Term] = term,
        [KnowledgeSchema.Description] = description,
    });

    private Task DeactivateAsync(string table, Guid id) =>
        Admin.UpdateAsync(new Entity(table, id) { ["statecode"] = new OptionSetValue(1), ["statuscode"] = new OptionSetValue(2) });

    [Fact]
    public async Task Describe_table_includes_descriptions_synonyms_and_glossary()
    {
        var source = await SourceAsync("Projects " + _run);
        var entity = await SourceEntityAsync(source, TestEntities.Project);
        await SynonymsAsync(entity, "pb_name", "title, project name");
        await TermAsync(source, "PO", "purchase order");
        await DescribeProjectAsync("Projects of the company " + _run, "The project's title " + _run);

        var table = await DescribeAsync();

        table["description"]!.GetValue<string>().Should().Be("Projects of the company " + _run);
        var name = ColumnOf(table, "pb_name");
        name["description"]!.GetValue<string>().Should().Be("The project's title " + _run);
        Strings(name["synonyms"]).Should().Equal("title", "project name");
        table["glossary"]!.AsArray().Should().Contain(t => t!["term"]!.GetValue<string>() == "PO" && t["description"]!.GetValue<string>() == "purchase order");
        // A column without a description or synonyms carries neither.
        ColumnOf(table, "pb_budget").ContainsKey("synonyms").Should().BeFalse();
        ColumnOf(table, "pb_budget").ContainsKey("description").Should().BeFalse();
    }

    [Fact]
    public async Task Synonyms_of_another_table_are_not_shown()
    {
        var source = await SourceAsync("Accounts " + _run);
        var entity = await SourceEntityAsync(source, "account");
        await SynonymsAsync(entity, "pb_name", "elsewhere " + _run);

        var table = await DescribeAsync();

        Strings(ColumnOf(table, "pb_name")["synonyms"]).Should().NotContain("elsewhere " + _run);
    }

    [Fact]
    public async Task Inactive_rows_are_ignored()
    {
        var source = await SourceAsync("Inactive " + _run);
        var entity = await SourceEntityAsync(source, TestEntities.Project);
        var synonyms = await SynonymsAsync(entity, "pb_name", "dormant " + _run);
        var term = await TermAsync(source, "Dormant " + _run, "inactive");
        var before = await DescribeAsync();
        Strings(ColumnOf(before, "pb_name")["synonyms"]).Should().Contain("dormant " + _run);

        await DeactivateAsync(KnowledgeSchema.Synonyms, synonyms);
        await DeactivateAsync(KnowledgeSchema.GlossaryTerm, term);
        var table = await DescribeAsync();

        Strings(ColumnOf(table, "pb_name")["synonyms"]).Should().NotContain("dormant " + _run);
        (table["glossary"]?.AsArray() ?? []).Should().NotContain(t => t!["term"]!.GetValue<string>() == "Dormant " + _run);
    }

    [Fact]
    public async Task Describe_without_knowledge_privileges_still_answers()
    {
        var source = await SourceAsync("Hidden " + _run);
        var entity = await SourceEntityAsync(source, TestEntities.Project);
        await SynonymsAsync(entity, "pb_name", "hidden " + _run);
        await TermAsync(source, "Hidden " + _run);
        var table = await DescribeAsync(await RestrictedUserAsync());

        table["logicalName"]!.GetValue<string>().Should().Be(TestEntities.Project);
        ColumnOf(table, "pb_name").ContainsKey("synonyms").Should().BeFalse();
        table.ContainsKey("glossary").Should().BeFalse();
    }

    [Fact]
    public async Task Describe_with_secured_knowledge_column_still_answers()
    {
        var source = await SourceAsync("Secured " + _run);
        var entity = await SourceEntityAsync(source, TestEntities.Project);
        await SynonymsAsync(entity, "pb_name", "secured " + _run);
        await TermAsync(source, "Secured " + _run);
        // Every knowledge table is readable; only the filter column entitylogicalname is secured, with no profile for the user.
        var oid = await RestrictedUserAsync(("prvReadDVTableSearch", DepthMask.Global), ("prvReadDVTableSearchEntity", DepthMask.Global),
            ("prvReadCopilotSynonyms", DepthMask.Global), ("prvReadCopilotGlossaryTerm", DepthMask.Global));
        // Securing through metadata is not offered on a seeded table: the in-memory definition is marked for this test.
        var column = Store.GetEntity(KnowledgeSchema.SourceEntity)!.Attributes.Single(a => a.LogicalName == KnowledgeSchema.EntityLogicalName);
        column.IsSecured = true;
        Cache(fx).Invalidate();
        try
        {
            var table = await DescribeAsync(oid);

            table["logicalName"]!.GetValue<string>().Should().Be(TestEntities.Project);
            ColumnOf(table, "pb_name").ContainsKey("synonyms").Should().BeFalse();
            table.ContainsKey("glossary").Should().BeFalse();
        }
        finally
        {
            column.IsSecured = false;
            Cache(fx).Invalidate();
        }
    }

    [Fact]
    public async Task List_glossary_terms_filters_by_table_and_source()
    {
        var projects = await SourceAsync("Projects " + _run);
        await SourceEntityAsync(projects, TestEntities.Project);
        var accounts = await SourceAsync("Accounts " + _run);
        await SourceEntityAsync(accounts, "account");
        await TermAsync(projects, "Milestone " + _run, "a project checkpoint");
        await TermAsync(accounts, "Key account " + _run);
        await using var client = await ConnectAsync(fx.AdminObjectId);

        JsonObject[] Mine(JsonObject result) => result["terms"]!.AsArray().Select(t => t!.AsObject())
            .Where(t => t["term"]!.GetValue<string>().EndsWith(_run, StringComparison.Ordinal)).ToArray();

        var all = Mine(await CallJsonAsync(client, "list_glossary_terms", new()));
        all.Select(t => t["term"]!.GetValue<string>()).Should().Equal("Key account " + _run, "Milestone " + _run);
        Strings(all[0]["sources"]).Should().Equal("Accounts " + _run);
        all[0].ContainsKey("description").Should().BeFalse();
        all[1]["description"]!.GetValue<string>().Should().Be("a project checkpoint");
        Strings(all[1]["sources"]).Should().Equal("Projects " + _run);

        var byTable = Mine(await CallJsonAsync(client, "list_glossary_terms", new() { ["table"] = TestEntities.Project }));
        byTable.Select(t => t["term"]!.GetValue<string>()).Should().Equal("Milestone " + _run);

        var bySource = Mine(await CallJsonAsync(client, "list_glossary_terms", new() { ["source"] = "Accounts " + _run }));
        bySource.Select(t => t["term"]!.GetValue<string>()).Should().Equal("Key account " + _run);
        Strings(bySource[0]["sources"]).Should().Equal("Accounts " + _run);
    }

    [Fact]
    public async Task Inactive_source_hides_its_terms_and_synonyms()
    {
        var source = await SourceAsync("Retired " + _run);
        var entity = await SourceEntityAsync(source, TestEntities.Project);
        await SynonymsAsync(entity, "pb_name", "retired " + _run);
        await TermAsync(source, "Retired " + _run);
        Strings(ColumnOf(await DescribeAsync(), "pb_name")["synonyms"]).Should().Contain("retired " + _run);
        (await TermNamesAsync(new())).Should().Contain("Retired " + _run);

        await DeactivateAsync(KnowledgeSchema.Source, source);

        var table = await DescribeAsync();
        Strings(ColumnOf(table, "pb_name")["synonyms"]).Should().NotContain("retired " + _run);
        (table["glossary"]?.AsArray() ?? []).Should().NotContain(t => t!["term"]!.GetValue<string>() == "Retired " + _run);
        (await TermNamesAsync(new())).Should().NotContain("Retired " + _run);
        (await TermNamesAsync(new() { ["table"] = TestEntities.Project })).Should().NotContain("Retired " + _run);
        (await TermNamesAsync(new() { ["source"] = "Retired " + _run })).Should().BeEmpty();
    }

    [Fact]
    public async Task List_glossary_terms_without_privileges_returns_empty()
    {
        var source = await SourceAsync("Private " + _run);
        await SourceEntityAsync(source, TestEntities.Project);
        await TermAsync(source, "Private " + _run);

        (await TermNamesAsync(new(), await RestrictedUserAsync())).Should().BeEmpty();
    }

    [Fact]
    public async Task List_glossary_terms_combines_table_and_source()
    {
        var both = await SourceAsync("Both " + _run);
        await SourceEntityAsync(both, TestEntities.Project);
        var other = await SourceAsync("Other " + _run);
        await SourceEntityAsync(other, "account");
        await TermAsync(both, "Both " + _run);
        await TermAsync(other, "Other " + _run);

        (await TermNamesAsync(new() { ["table"] = TestEntities.Project, ["source"] = "Both " + _run })).Should().Equal("Both " + _run);
        (await TermNamesAsync(new() { ["table"] = TestEntities.Project, ["source"] = "Other " + _run })).Should().BeEmpty();
    }

    [Fact]
    public async Task Table_resource_includes_synonyms_and_glossary()
    {
        var source = await SourceAsync("Resource " + _run);
        var entity = await SourceEntityAsync(source, TestEntities.Project);
        await SynonymsAsync(entity, "pb_name", "headline " + _run);
        await TermAsync(source, "Resource " + _run, "read as a resource");

        var table = await ResourceAsync();

        Strings(ColumnOf(table, "pb_name")["synonyms"]).Should().Equal("headline " + _run);
        table["glossary"]!.AsArray().Should().Contain(t => t!["term"]!.GetValue<string>() == "Resource " + _run
            && t["description"]!.GetValue<string>() == "read as a resource");
    }

    [Fact]
    public async Task Synonyms_keep_creation_order_and_first_spelling()
    {
        var source = await SourceAsync("Order " + _run);
        var entity = await SourceEntityAsync(source, TestEntities.Project);
        await SynonymsAsync(entity, "pb_budget", "Cost, amount, COST");
        await Task.Delay(1100); // a later createdon even if it is stored to the second
        await SynonymsAsync(entity, "pb_budget", "AMOUNT; spend");

        Strings(ColumnOf(await DescribeAsync(), "pb_budget")["synonyms"]).Should().Equal("Cost", "amount", "spend");
    }

    [Fact]
    public void Splits_synonyms()
    {
        KnowledgeReader.SplitSynonyms(" VP , vice president;; ").Should().Equal("VP", "vice president");
        KnowledgeReader.SplitSynonyms(null).Should().BeEmpty();
    }
}
