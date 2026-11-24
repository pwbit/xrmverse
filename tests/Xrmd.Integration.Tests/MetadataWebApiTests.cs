using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.Edm;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The metadata writes of the Web API: <c>POST RelationshipDefinitions</c>, <c>POST GlobalOptionSetDefinitions</c>, <c>PUT</c>
/// of entities and attributes, <c>DELETE</c> of entities, attributes, relationships and global option sets, with their
/// privileges.
/// </summary>
[Collection("xrmd")]
public sealed class MetadataWebApiTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    public Task InitializeAsync() => TestEntities.EnsureProjectTagAsync(Store, fx.ConnectionString);

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null, Guid? objectId = null,
        string? solution = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl));
        if (solution is not null) req.Headers.Add("MSCRM.SolutionUniqueName", solution);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private async Task NoContentAsync(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
    }

    private async Task<JsonObject> GetJsonAsync(string path)
    {
        var res = await SendAsync(HttpMethod.Get, path);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static async Task<JsonNode> ErrorAsync(HttpResponseMessage res, HttpStatusCode status)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(status, body);
        return JsonNode.Parse(body)!["error"]!;
    }

    private static string Label(string text) => $$"""{ "LocalizedLabels": [ { "Label": "{{text}}", "LanguageCode": 1033 } ] }""";

    private async Task<string> NewEntityAsync(string label)
    {
        var name = $"new_{label}{_run}";
        var body = $$"""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata",
              "LogicalName": "{{name}}", "SchemaName": "{{name}}", "DisplayName": {{Label("Table")}}, "OwnershipType": "UserOwned",
              "Attributes": [ { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "new_name",
                                "SchemaName": "new_Name", "MaxLength": 100, "IsPrimaryName": true } ]
            }
            """;
        await NoContentAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", body));
        return name;
    }

    [Fact]
    public async Task Post_RelationshipDefinitions_creates_a_many_to_many_relationship()
    {
        var left = await NewEntityAsync("wl");
        var right = await NewEntityAsync("wr");
        var name = $"new_web_mm_{_run}";

        var res = await SendAsync(HttpMethod.Post, "RelationshipDefinitions", $$"""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.ManyToManyRelationshipMetadata",
              "SchemaName": "{{name}}", "Entity1LogicalName": "{{left}}", "Entity2LogicalName": "{{right}}",
              "IntersectEntityName": "{{name}}"
            }
            """, solution: "Default");

        await NoContentAsync(res);
        var relationship = Store.GetRelationship(name).Should().BeOfType<ManyToManyRelationshipMetadata>().Subject;
        res.Headers.GetValues("OData-EntityId").Single().Should().EndWith($"{Api}RelationshipDefinitions({relationship.MetadataId})");
        var json = await GetJsonAsync($"RelationshipDefinitions({relationship.MetadataId})");
        json["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.ManyToManyRelationshipMetadata");
        json["SchemaName"]!.GetValue<string>().Should().Be(name);
        (await GetJsonAsync($"RelationshipDefinitions(SchemaName='{name}')"))["IntersectEntityName"]!.GetValue<string>().Should().Be(name);

        await NoContentAsync(await SendAsync(HttpMethod.Delete, $"RelationshipDefinitions(SchemaName='{name}')"));
        Store.GetRelationship(name).Should().BeNull();
        await ErrorAsync(await SendAsync(HttpMethod.Delete, $"RelationshipDefinitions(SchemaName='{name}')"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_RelationshipDefinitions_creates_a_one_to_many_relationship_with_its_lookup()
    {
        var parent = await NewEntityAsync("wp");
        var child = await NewEntityAsync("wc");
        var name = $"new_web_otm_{_run}";

        var res = await SendAsync(HttpMethod.Post, "RelationshipDefinitions", $$"""
            {
              "SchemaName": "{{name}}", "ReferencedEntity": "{{parent}}", "ReferencingEntity": "{{child}}",
              "ReferencedAttribute": "{{parent}}id",
              "Lookup": { "@odata.type": "Microsoft.Dynamics.CRM.LookupAttributeMetadata", "LogicalName": "new_parentid",
                          "SchemaName": "new_ParentId", "DisplayName": {{Label("Parent")}} },
              "@odata.type": "#Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata"
            }
            """);

        await NoContentAsync(res);
        var relationship = Store.GetRelationship(name).Should().BeOfType<OneToManyRelationshipMetadata>().Subject;
        relationship.ReferencingAttribute.Should().Be("new_parentid");
        res.Headers.GetValues("OData-EntityId").Single().Should().EndWith($"RelationshipDefinitions({relationship.MetadataId})");
        Store.GetEntity(child)!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "new_parentid")
            .Targets.Should().Equal(parent);

        // Deleting the parent is refused while the lookup exists.
        var refused = await ErrorAsync(await SendAsync(HttpMethod.Delete, $"EntityDefinitions(LogicalName='{parent}')"), HttpStatusCode.BadRequest);
        refused["message"]!.GetValue<string>().Should().Be($"The component cannot be deleted because it is referenced by attribute '{child}.new_parentid'.");
    }

    [Fact]
    public async Task Post_and_delete_GlobalOptionSetDefinitions()
    {
        var name = $"new_webcolors{_run}";

        var res = await SendAsync(HttpMethod.Post, "GlobalOptionSetDefinitions", $$"""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.OptionSetMetadata", "Name": "{{name}}", "DisplayName": {{Label("Colors")}},
              "Options": [ { "Value": 100000000, "Label": {{Label("Red")}} } ]
            }
            """);

        await NoContentAsync(res);
        var optionSet = Store.GetOptionSet(name)!;
        res.Headers.GetValues("OData-EntityId").Single().Should().EndWith($"GlobalOptionSetDefinitions({optionSet.MetadataId})");
        (await GetJsonAsync($"GlobalOptionSetDefinitions(Name='{name}')"))["Options"]!.AsArray().Should().HaveCount(1);

        await NoContentAsync(await SendAsync(HttpMethod.Delete, $"GlobalOptionSetDefinitions({optionSet.MetadataId})"));
        Store.GetOptionSet(name).Should().BeNull();
    }

    [Fact]
    public async Task Created_table_and_column_are_custom_and_system_ones_are_not()
    {
        var name = await NewEntityAsync("wcust");
        await NoContentAsync(await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{name}')/Attributes", """
            { "@odata.type": "Microsoft.Dynamics.CRM.BooleanAttributeMetadata", "LogicalName": "new_flag", "SchemaName": "new_Flag",
              "IsCustomAttribute": false }
            """));

        // The flags are server-owned: the client's false for the column is ignored.
        (await GetJsonAsync($"EntityDefinitions(LogicalName='{name}')"))["IsCustomEntity"]!.GetValue<bool>().Should().BeTrue();
        (await GetJsonAsync($"EntityDefinitions(LogicalName='{name}')/Attributes(LogicalName='new_flag')"))["IsCustomAttribute"]!.GetValue<bool>().Should().BeTrue();
        (await GetJsonAsync($"EntityDefinitions(LogicalName='{name}')/Attributes(LogicalName='{name}id')"))["IsCustomAttribute"]!.GetValue<bool>().Should().BeFalse();

        (await GetJsonAsync("EntityDefinitions(LogicalName='systemuser')"))["IsCustomEntity"]!.GetValue<bool>().Should().BeFalse();
        (await GetJsonAsync("EntityDefinitions(LogicalName='systemuser')/Attributes(LogicalName='fullname')"))["IsCustomAttribute"]!.GetValue<bool>().Should().BeFalse();
        var list = await GetJsonAsync("EntityDefinitions?$select=LogicalName,IsCustomEntity");
        list["value"]!.AsArray().Single(e => e!["LogicalName"]!.GetValue<string>() == name)!["IsCustomEntity"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Put_updates_entity_and_attribute()
    {
        var name = await NewEntityAsync("wput");

        await NoContentAsync(await SendAsync(HttpMethod.Put, $"EntityDefinitions(LogicalName='{name}')", $$"""
            { "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "LogicalName": "{{name}}", "DisplayName": {{Label("Renamed")}} }
            """));
        Store.GetEntity(name)!.DisplayName.LocalizedLabels.Single().Label.Should().Be("Renamed");

        await NoContentAsync(await SendAsync(HttpMethod.Put, $"EntityDefinitions(LogicalName='{name}')/Attributes(LogicalName='new_name')", """
            { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "new_name", "MaxLength": 300 }
            """));
        ((StringAttributeMetadata)Store.GetEntity(name)!.Attributes.Single(a => a.LogicalName == "new_name")).MaxLength.Should().Be(300);

        // The body names the entity of the URL, or none.
        await ErrorAsync(await SendAsync(HttpMethod.Put, $"EntityDefinitions(LogicalName='{name}')",
            """{ "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "LogicalName": "systemuser" }"""), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_EntityDefinitions_and_attribute()
    {
        var name = await NewEntityAsync("wdel");
        await NoContentAsync(await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{name}')/Attributes", """
            { "@odata.type": "Microsoft.Dynamics.CRM.BooleanAttributeMetadata", "LogicalName": "new_flag", "SchemaName": "new_Flag" }
            """));

        // An attribute without option set: $expand=OptionSet gives null.
        var flag = await GetJsonAsync($"EntityDefinitions(LogicalName='{name}')/Attributes(LogicalName='new_flag')?$expand=OptionSet");
        flag.ContainsKey("OptionSet").Should().BeTrue();
        flag["OptionSet"].Should().BeNull();

        await NoContentAsync(await SendAsync(HttpMethod.Delete, $"EntityDefinitions(LogicalName='{name}')/Attributes(LogicalName='new_flag')"));
        Store.GetEntity(name)!.Attributes.Should().NotContain(a => a.LogicalName == "new_flag");

        await NoContentAsync(await SendAsync(HttpMethod.Delete, $"EntityDefinitions(LogicalName='{name}')"));
        Store.GetEntity(name).Should().BeNull();
        await ErrorAsync(await SendAsync(HttpMethod.Delete, $"EntityDefinitions(LogicalName='{name}')"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_EntityDefinitions_refuses_a_reserved_EntitySetName()
    {
        var name = $"new_wres{_run}";
        var body = $$"""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata",
              "LogicalName": "{{name}}", "SchemaName": "{{name}}", "EntitySetName": "relationshipdefinitions", "OwnershipType": "UserOwned",
              "Attributes": [ { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "new_name",
                                "SchemaName": "new_Name", "MaxLength": 100, "IsPrimaryName": true } ]
            }
            """;

        (await ErrorAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", body), HttpStatusCode.BadRequest))["code"]!
            .GetValue<string>().Should().Be("0x80040203");
        Store.GetEntity(name).Should().BeNull();
    }

    [Fact]
    public async Task Metadata_writes_require_their_privilege()
    {
        var name = await NewEntityAsync("wprv");
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await AssignRoleAsync(fx, user, StandardRoles.BasicUser);

        var error = await ErrorAsync(await SendAsync(HttpMethod.Delete, $"EntityDefinitions(LogicalName='{name}')", objectId: objectId),
            HttpStatusCode.Forbidden);
        error["message"]!.GetValue<string>().Should().Contain("prvDeleteEntity");
        error = await ErrorAsync(await SendAsync(HttpMethod.Post, "GlobalOptionSetDefinitions",
            $$"""{ "@odata.type": "Microsoft.Dynamics.CRM.OptionSetMetadata", "Name": "new_x{{_run}}" }""", objectId: objectId),
            HttpStatusCode.Forbidden);
        error["message"]!.GetValue<string>().Should().Contain("prvCreateOptionSet");
        Store.GetEntity(name).Should().NotBeNull();
    }

    [Fact]
    public async Task Duplicate_keys_in_a_metadata_body_are_400()
    {
        var error = await ErrorAsync(await SendAsync(HttpMethod.Post, "GlobalOptionSetDefinitions",
            $$"""{ "@odata.type": "Microsoft.Dynamics.CRM.OptionSetMetadata", "Name": "new_d{{_run}}", "Name": "new_e{{_run}}" }"""),
            HttpStatusCode.BadRequest);
        error["message"]!.GetValue<string>().Should().Contain("Name");
    }

    /// <summary>
    /// Plan 2.11, task 2: the activity tables' navigation properties come from their relationships (one per regarding target), the
    /// party relationship is a collection-valued navigation property, and a party list is no property (pre-flight C9).
    /// </summary>
    [Fact]
    public async Task Metadata_declares_activity_navigations_and_no_party_list_property()
    {
        var res = await SendAsync(HttpMethod.Get, "$metadata");
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var reader = new StringReader(text);
        using var xml = System.Xml.XmlReader.Create(reader);
        Microsoft.OData.Edm.Csdl.CsdlReader.TryParse(xml, out var model, out var errors).Should().BeTrue(string.Join("; ", errors ?? []));
        var email = (Microsoft.OData.Edm.IEdmEntityType)model.FindDeclaredType("Microsoft.Dynamics.CRM.email");

        var regarding = email.FindProperty("regardingobjectid_account_email").Should().BeAssignableTo<Microsoft.OData.Edm.IEdmNavigationProperty>().Subject;
        regarding.ToEntityType().Name.Should().Be("account");
        email.FindProperty("regardingobjectid_contact_email").Should().NotBeNull();
        email.FindProperty("regardingobjectid").Should().BeNull();
        var parties = email.FindProperty("email_activity_parties").Should().BeAssignableTo<Microsoft.OData.Edm.IEdmNavigationProperty>().Subject;
        parties.Type.IsCollection().Should().BeTrue();
        parties.ToEntityType().Name.Should().Be("activityparty");
        email.FindProperty("to").Should().BeNull();
        email.FindProperty("_to_value").Should().BeNull();
        email.FindProperty("_regardingobjectid_value").Should().NotBeNull();

        var party = (Microsoft.OData.Edm.IEdmEntityType)model.FindDeclaredType("Microsoft.Dynamics.CRM.activityparty");
        party.FindProperty("activityid_email").Should().NotBeNull();
        party.FindProperty("partyid_account").Should().NotBeNull();
        party.FindProperty("partyid_systemuser").Should().NotBeNull();
        var pointer = (Microsoft.OData.Edm.IEdmEntityType)model.FindDeclaredType("Microsoft.Dynamics.CRM.activitypointer");
        pointer.FindProperty("regardingobjectid_account").Should().NotBeNull();
        var note = (Microsoft.OData.Edm.IEdmEntityType)model.FindDeclaredType("Microsoft.Dynamics.CRM.annotation");
        note.FindProperty("objectid_account").Should().NotBeNull();
    }
}
