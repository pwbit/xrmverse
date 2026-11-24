using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>D3, task 1: the five knowledge tables (source, source table, source column, column synonyms, glossary term).</summary>
[Collection("xrmd")]
public sealed class KnowledgeTablesTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private async Task<bool> ExistsAsync(string table, string idColumn, Guid id) =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression(table)
        {
            ColumnSet = new ColumnSet(idColumn),
            Criteria = { Conditions = { new ConditionExpression(idColumn, ConditionOperator.Equal, id) } },
        })).Entities.Count > 0;

    [Theory]
    [InlineData("dvtablesearch", "dvtablesearchs", "dvtablesearchid", 9960)]
    [InlineData("dvtablesearchentity", "dvtablesearchentities", "dvtablesearchentityid", 9961)]
    [InlineData("dvtablesearchattribute", "dvtablesearchattributes", "dvtablesearchattributeid", 9962)]
    [InlineData("copilotsynonyms", "copilotsynonyms", "copilotsynonymsid", 9963)]
    [InlineData("copilotglossaryterm", "copilotglossaryterms", "copilotglossarytermid", 9964)]
    public void Tables_exist_with_dataverse_names(string table, string set, string id, int otc)
    {
        var e = Store.GetEntity(table);
        e.Should().NotBeNull();
        e!.EntitySetName.Should().Be(set);
        e.PrimaryIdAttribute.Should().Be(id);
        e.PrimaryNameAttribute.Should().Be("name");
        e.OwnershipType.Should().Be(OwnershipTypes.UserOwned);
        e.ObjectTypeCode.Should().Be(otc);
        Store.GetEntityByObjectTypeCode(otc)!.LogicalName.Should().Be(table);
    }

    [Fact]
    public async Task Creates_a_source_with_entity_synonyms_and_term_over_sdk()
    {
        var source = await Admin.CreateAsync(new Entity(KnowledgeSchema.Source) { ["name"] = "Source " + _run, ["searchtype"] = new OptionSetValue(0) });
        var entity = await Admin.CreateAsync(new Entity(KnowledgeSchema.SourceEntity)
        {
            ["name"] = "Entity " + _run, [KnowledgeSchema.SourceLookup] = new EntityReference(KnowledgeSchema.Source, source), [KnowledgeSchema.EntityLogicalName] = "account",
        });
        var synonyms = await Admin.CreateAsync(new Entity(KnowledgeSchema.Synonyms)
        {
            [KnowledgeSchema.SkillEntity] = new EntityReference(KnowledgeSchema.SourceEntity, entity),
            [KnowledgeSchema.ColumnLogicalName] = "name", [KnowledgeSchema.SynonymsText] = "company, customer",
        });
        var term = await Admin.CreateAsync(new Entity(KnowledgeSchema.GlossaryTerm)
        {
            [KnowledgeSchema.Skill] = new EntityReference(KnowledgeSchema.Source, source), [KnowledgeSchema.Term] = "VP",
        });
        var attribute = await Admin.CreateAsync(new Entity(KnowledgeSchema.SourceAttribute)
        {
            ["name"] = "Attribute " + _run, [KnowledgeSchema.SourceEntity] = new EntityReference(KnowledgeSchema.SourceEntity, entity),
            ["attributelogicalname"] = "name", ["issearchable"] = true, ["isretrievable"] = false,
        });
        var gotAttribute = await Admin.RetrieveAsync(KnowledgeSchema.SourceAttribute, attribute, new ColumnSet(true));
        gotAttribute.GetAttributeValue<string>("attributelogicalname").Should().Be("name");
        gotAttribute.GetAttributeValue<bool>("issearchable").Should().BeTrue();
        gotAttribute.GetAttributeValue<bool>("isretrievable").Should().BeFalse();
        gotAttribute.GetAttributeValue<EntityReference>("dvtablesearchentity").Id.Should().Be(entity);

        var gotEntity = await Admin.RetrieveAsync(KnowledgeSchema.SourceEntity, entity, new ColumnSet(true));
        gotEntity.GetAttributeValue<string>("entitylogicalname").Should().Be("account");
        gotEntity.GetAttributeValue<EntityReference>("dvtablesearch").Id.Should().Be(source);
        var gotSyn = await Admin.RetrieveAsync(KnowledgeSchema.Synonyms, synonyms, new ColumnSet(true));
        gotSyn.GetAttributeValue<string>("columnlogicalname").Should().Be("name");
        gotSyn.GetAttributeValue<string>("synonyms").Should().Be("company, customer");
        gotSyn.GetAttributeValue<EntityReference>("skillentity").Id.Should().Be(entity);
        var gotTerm = await Admin.RetrieveAsync(KnowledgeSchema.GlossaryTerm, term, new ColumnSet(true));
        gotTerm.GetAttributeValue<string>("term").Should().Be("VP");
        gotTerm.GetAttributeValue<EntityReference>("skill").Id.Should().Be(source);
        (await Admin.RetrieveAsync(KnowledgeSchema.Source, source, new ColumnSet(true))).GetAttributeValue<OptionSetValue>("searchtype").Value.Should().Be(0);
    }

    [Fact]
    public async Task Creates_a_term_over_web_api()
    {
        var source = await Admin.CreateAsync(new Entity(KnowledgeSchema.Source) { ["name"] = "Source " + _run });
        var post = await SendAsync(HttpMethod.Post, "copilotglossaryterms",
            $$"""{ "term": "VP {{_run}}", "skill@odata.bind": "/dvtablesearchs({{source}})" }""");
        post.StatusCode.Should().Be(HttpStatusCode.NoContent, await post.Content.ReadAsStringAsync());
        var location = post.Headers.GetValues("OData-EntityId").Single();
        var get = await SendAsync(HttpMethod.Get, location[(location.IndexOf("/api/data/v9.2/", StringComparison.Ordinal) + Api.Length)..]);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await get.Content.ReadAsStringAsync())!["term"]!.GetValue<string>().Should().Be("VP " + _run);
    }

    [Theory]
    [InlineData("dvtablesearch", "dvtablesearchs", "knowledgeconfig")]
    [InlineData("dvtablesearch", "dvtablesearchs", "appid")]
    [InlineData("dvtablesearchentity", "dvtablesearchentities", "entity")]
    [InlineData("dvtablesearchattribute", "dvtablesearchattributes", "attribute")]
    [InlineData("dvtablesearchattribute", "dvtablesearchattributes", "isattributeenabledforvectorsearch")]
    public async Task Left_out_columns_are_refused(string table, string set, string column)
    {
        var sdk = await FaultAsync(() => Admin.CreateAsync(new Entity(table) { ["name"] = "Row " + _run, [column] = "{}" }));
        var web = await SendAsync(HttpMethod.Post, set, $$"""{ "name": "Row {{_run}}", "{{column}}": "{}" }""");
        web.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // The same fault the server gives for any unknown column.
        var other = await FaultAsync(() => Admin.CreateAsync(new Entity(table) { ["name"] = "Row " + _run, ["nosuchcolumn"] = "x" }));
        (sdk.HttpStatus, sdk.ErrorCode).Should().Be((other.HttpStatus, other.ErrorCode));
        JsonNode.Parse(await web.Content.ReadAsStringAsync())!["error"]!["code"]!.GetValue<string>().Should().Be("0x" + ((int)other.ErrorCode).ToString("X8"));
    }

    [Fact]
    public async Task Deleting_a_source_cascades()
    {
        var source = await Admin.CreateAsync(new Entity(KnowledgeSchema.Source) { ["name"] = "Source " + _run });
        var entity = await Admin.CreateAsync(new Entity(KnowledgeSchema.SourceEntity)
        {
            ["name"] = "Entity " + _run, [KnowledgeSchema.SourceLookup] = new EntityReference(KnowledgeSchema.Source, source),
        });
        var synonyms = await Admin.CreateAsync(new Entity(KnowledgeSchema.Synonyms)
        {
            [KnowledgeSchema.SkillEntity] = new EntityReference(KnowledgeSchema.SourceEntity, entity), [KnowledgeSchema.ColumnLogicalName] = "name",
        });
        var term = await Admin.CreateAsync(new Entity(KnowledgeSchema.GlossaryTerm)
        {
            [KnowledgeSchema.Skill] = new EntityReference(KnowledgeSchema.Source, source), [KnowledgeSchema.Term] = "VP",
        });

        await Admin.DeleteAsync(KnowledgeSchema.Source, source);

        (await ExistsAsync(KnowledgeSchema.Source, "dvtablesearchid", source)).Should().BeFalse();
        (await ExistsAsync(KnowledgeSchema.SourceEntity, "dvtablesearchentityid", entity)).Should().BeFalse();
        (await ExistsAsync(KnowledgeSchema.Synonyms, "copilotsynonymsid", synonyms)).Should().BeFalse();
        (await ExistsAsync(KnowledgeSchema.GlossaryTerm, "copilotglossarytermid", term)).Should().BeFalse();
    }

    [Theory]
    [InlineData("dvtablesearch", "name", AttributeRequiredLevel.ApplicationRequired)]
    [InlineData("dvtablesearchentity", "name", AttributeRequiredLevel.ApplicationRequired)]
    [InlineData("dvtablesearchattribute", "name", AttributeRequiredLevel.ApplicationRequired)]
    [InlineData("copilotsynonyms", "columnlogicalname", AttributeRequiredLevel.ApplicationRequired)]
    [InlineData("copilotsynonyms", "name", AttributeRequiredLevel.None)]
    [InlineData("copilotglossaryterm", "term", AttributeRequiredLevel.ApplicationRequired)]
    [InlineData("copilotglossaryterm", "name", AttributeRequiredLevel.None)]
    public void Required_columns_are_marked_required(string table, string column, AttributeRequiredLevel level)
    {
        // Like Dataverse, ApplicationRequired is metadata for clients: the server stores a row without the column (no fault exists to assert).
        var required = Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == column).RequiredLevel;
        if (level == AttributeRequiredLevel.None) (required?.Value ?? AttributeRequiredLevel.None).Should().Be(AttributeRequiredLevel.None);
        else
        {
            required.Should().NotBeNull();
            required!.Value.Should().Be(level);
        }
    }
}
