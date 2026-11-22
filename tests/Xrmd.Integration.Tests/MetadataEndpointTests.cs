using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public sealed class MetadataEndpointTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";

    private static string NewName() => "pb_md" + Guid.NewGuid().ToString("N")[..10];

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? json = null, bool authenticated = true, Guid? objectId = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        if (authenticated)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private async Task<JsonObject> GetJsonAsync(string path)
    {
        var res = await SendAsync(HttpMethod.Get, path);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private async Task<IEdmModel> GetMetadataAsync()
    {
        var res = await SendAsync(HttpMethod.Get, "$metadata");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
        using var reader = XmlReader.Create(await res.Content.ReadAsStreamAsync());
        CsdlReader.TryParse(reader, out var model, out var errors)
            .Should().BeTrue(string.Join("; ", errors?.Select(e => e.ErrorMessage) ?? []));
        return model;
    }

    private static string EntityBody(string logicalName, string? setName = null, bool typeFirst = true, string typePrefix = "") =>
        $$"""
        {
          {{(typeFirst ? $"\"@odata.type\": \"{typePrefix}Microsoft.Dynamics.CRM.EntityMetadata\"," : "")}}
          "SchemaName": "{{logicalName}}",
          "LogicalName": "{{logicalName}}",
          {{(setName is null ? "" : $"\"EntitySetName\": \"{setName}\",")}}
          "DisplayName": { "LocalizedLabels": [ { "Label": "Test table", "LanguageCode": 1033 } ] },
          "OwnershipType": "UserOwned",
          "Attributes": [
            {
              {{(typeFirst ? "" : "\"LogicalName\": \"pb_name\", \"IsPrimaryName\": true,")}}
              "@odata.type": "{{typePrefix}}Microsoft.Dynamics.CRM.StringAttributeMetadata"
              {{(typeFirst ? ", \"LogicalName\": \"pb_name\", \"IsPrimaryName\": true" : "")}},
              "SchemaName": "pb_Name",
              "MaxLength": 100
            }
          ]
          {{(typeFirst ? "" : $", \"@odata.type\": \"{typePrefix}Microsoft.Dynamics.CRM.EntityMetadata\"")}}
        }
        """;

    private static async Task AssertErrorAsync(HttpResponseMessage res, HttpStatusCode status, string code)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(status, body);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var error = JsonNode.Parse(body)!["error"]!;
        error["code"]!.GetValue<string>().Should().Be(code);
        error["message"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Metadata_document_is_valid_csdl()
    {
        var model = await GetMetadataAsync();

        var systemUser = (IEdmEntityType)model.FindDeclaredType("Microsoft.Dynamics.CRM.systemuser");
        systemUser.Should().NotBeNull();
        systemUser.Key().Single().Name.Should().Be("systemuserid");
        systemUser.FindProperty("_businessunitid_value").Should().NotBeNull();
        systemUser.FindProperty("businessunitid").Should().BeAssignableTo<IEdmNavigationProperty>();
        model.EntityContainer.Name.Should().Be("System");
        model.EntityContainer.FindEntitySet("systemusers").Should().NotBeNull();
    }

    [Fact]
    public async Task Metadata_requires_a_token()
    {
        (await SendAsync(HttpMethod.Get, "$metadata", authenticated: false)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EntityDefinitions_filter_by_logicalname_returns_one()
    {
        var json = await GetJsonAsync("EntityDefinitions?$select=LogicalName,EntitySetName&$filter=LogicalName eq 'systemuser'");

        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#EntityDefinitions(LogicalName,EntitySetName)");
        var value = json["value"]!.AsArray();
        value.Should().HaveCount(1);
        var entity = value[0]!.AsObject();
        entity["LogicalName"]!.GetValue<string>().Should().Be("systemuser");
        entity["EntitySetName"]!.GetValue<string>().Should().Be("systemusers");
        entity.Select(p => p.Key).Should().BeEquivalentTo(["LogicalName", "EntitySetName", "MetadataId"]);
    }

    [Fact]
    public async Task EntityDefinitions_includes_attributes_only_when_expanded()
    {
        var plain = await GetJsonAsync("EntityDefinitions?$filter=LogicalName eq 'systemuser'");
        plain["value"]![0]!.AsObject().ContainsKey("Attributes").Should().BeFalse();
        plain["value"]![0]!["SchemaName"]!.GetValue<string>().Should().Be("SystemUser");

        var expanded = await GetJsonAsync("EntityDefinitions?$filter=LogicalName eq 'systemuser'&$expand=Attributes");
        var attributes = expanded["value"]![0]!["Attributes"]!.AsArray();
        var fullName = attributes.Single(a => a!["LogicalName"]!.GetValue<string>() == "fullname")!;
        fullName["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.StringAttributeMetadata");
    }

    [Fact]
    public async Task EntityDefinitions_by_logicalname_and_its_attributes()
    {
        var entity = await GetJsonAsync("EntityDefinitions(LogicalName='businessunit')?$select=LogicalName,PrimaryIdAttribute");
        entity["@odata.context"]!.GetValue<string>().Should().Contain("$metadata#EntityDefinitions");
        entity["PrimaryIdAttribute"]!.GetValue<string>().Should().Be("businessunitid");
        entity.ContainsKey("Attributes").Should().BeFalse();

        var attributes = await GetJsonAsync("EntityDefinitions(LogicalName='businessunit')/Attributes");
        var value = attributes["value"]!.AsArray();
        value.Select(a => a!["LogicalName"]!.GetValue<string>()).Should().Contain(["businessunitid", "name", "versionnumber"]);
        value.Should().OnlyContain(a => a!["@odata.type"]!.GetValue<string>().StartsWith("#Microsoft.Dynamics.CRM."));

        var byId = await GetJsonAsync($"EntityDefinitions({entity["MetadataId"]!.GetValue<string>()})?$select=LogicalName");
        byId["LogicalName"]!.GetValue<string>().Should().Be("businessunit");
    }

    [Fact]
    public async Task Get_unknown_entity_returns_404_with_error_envelope()
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, "EntityDefinitions(LogicalName='pb_doesnotexist')"),
            HttpStatusCode.NotFound, "0x80040217");
    }

    [Fact]
    public async Task Unsupported_filter_returns_400_with_error_envelope()
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, "EntityDefinitions?$filter=IsCustomEntity eq true"),
            HttpStatusCode.BadRequest, "0x80040203");
    }

    [Fact]
    public async Task Post_EntityDefinitions_creates_table_and_is_visible_in_metadata()
    {
        var name = NewName();

        var res = await SendAsync(HttpMethod.Post, "EntityDefinitions", EntityBody(name));

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var entityId = res.Headers.GetValues("OData-EntityId").Single();
        var created = await GetJsonAsync($"EntityDefinitions(LogicalName='{name}')?$select=LogicalName,EntitySetName,PrimaryNameAttribute");
        entityId.Should().Be($"{fx.BaseUrl.TrimEnd('/')}{Api}EntityDefinitions({created["MetadataId"]!.GetValue<string>()})");
        created["EntitySetName"]!.GetValue<string>().Should().Be(name + "s");
        created["PrimaryNameAttribute"]!.GetValue<string>().Should().Be("pb_name");

        var type = (IEdmEntityType)(await GetMetadataAsync()).FindDeclaredType("Microsoft.Dynamics.CRM." + name);
        type.Should().NotBeNull();
        type.FindProperty("pb_name").Should().NotBeNull();
        (await ScalarAsync($"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'data' AND TABLE_NAME = '{name}'"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Post_EntityDefinitions_accepts_odata_type_anywhere_and_with_hash()
    {
        var name = NewName();

        var res = await SendAsync(HttpMethod.Post, "EntityDefinitions", EntityBody(name, name + "_set", typeFirst: false, typePrefix: "#"));

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var attributes = await GetJsonAsync($"EntityDefinitions(LogicalName='{name}')/Attributes");
        var primaryName = attributes["value"]!.AsArray().Single(a => a!["LogicalName"]!.GetValue<string>() == "pb_name")!;
        primaryName["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.StringAttributeMetadata");
        primaryName["MaxLength"]!.GetValue<int>().Should().Be(100);
        primaryName["IsPrimaryName"]!.GetValue<bool>().Should().BeTrue();
        (await GetMetadataAsync()).EntityContainer.FindEntitySet(name + "_set").Should().NotBeNull();
    }

    [Theory]
    [InlineData("PB_Upper")]
    [InlineData("pb_bad;drop")]
    [InlineData("1pb_digit")]
    public async Task Post_invalid_logicalname_returns_400_with_error_envelope(string logicalName)
    {
        var res = await SendAsync(HttpMethod.Post, "EntityDefinitions", EntityBody(logicalName, "pb_validset"));

        await AssertErrorAsync(res, HttpStatusCode.BadRequest, "0x80040203");
        (await ScalarAsync($"SELECT COUNT(*) FROM meta.entity WHERE logical_name = N'{logicalName.Replace("'", "''")}'")).Should().Be(0);
    }

    [Fact]
    public async Task Post_invalid_entitysetname_returns_400_with_error_envelope()
    {
        var res = await SendAsync(HttpMethod.Post, "EntityDefinitions", EntityBody(NewName(), "Bad Set"));

        await AssertErrorAsync(res, HttpStatusCode.BadRequest, "0x80040203");
    }

    [Fact]
    public async Task Post_without_primary_name_returns_400_with_error_envelope()
    {
        var body = EntityBody(NewName()).Replace("\"IsPrimaryName\": true", "\"IsPrimaryName\": false");

        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", body), HttpStatusCode.BadRequest, "0x80040203");
    }

    [Fact]
    public async Task Post_malformed_json_returns_400_with_error_envelope()
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", "{ not json"), HttpStatusCode.BadRequest, "0x80040203");
    }

    [Fact]
    public async Task Post_duplicate_entity_or_entitysetname_returns_412_with_error_envelope()
    {
        var name = NewName();
        (await SendAsync(HttpMethod.Post, "EntityDefinitions", EntityBody(name))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", EntityBody(name)),
            HttpStatusCode.PreconditionFailed, "0x80040237");
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", EntityBody(NewName(), name + "s")),
            HttpStatusCode.PreconditionFailed, "0x80040237");
    }

    [Fact]
    public async Task Post_attribute_adds_column_and_property()
    {
        var name = NewName();
        (await SendAsync(HttpMethod.Post, "EntityDefinitions", EntityBody(name))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        const string attribute = """
            { "LogicalName": "pb_count", "SchemaName": "pb_Count", "@odata.type": "#Microsoft.Dynamics.CRM.IntegerAttributeMetadata" }
            """;

        var res = await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{name}')/Attributes", attribute);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        res.Headers.GetValues("OData-EntityId").Single().Should().MatchRegex(@"/EntityDefinitions\([0-9a-f-]{36}\)/Attributes\([0-9a-f-]{36}\)$");
        var type = (IEdmEntityType)(await GetMetadataAsync()).FindDeclaredType("Microsoft.Dynamics.CRM." + name);
        type.FindProperty("pb_count").Type.PrimitiveKind().Should().Be(EdmPrimitiveTypeKind.Int32);

        await AssertErrorAsync(await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{name}')/Attributes", attribute),
            HttpStatusCode.PreconditionFailed, "0x80040237");
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions(LogicalName='pb_nosuchentity')/Attributes", attribute),
            HttpStatusCode.NotFound, "0x80040217");
    }

    [Fact]
    public async Task Caller_without_systemuser_gets_403_with_error_envelope()
    {
        var stranger = Guid.NewGuid();
        var name = NewName();

        await AssertErrorAsync(await SendAsync(HttpMethod.Get, "EntityDefinitions", objectId: stranger),
            HttpStatusCode.Forbidden, "0x80072560");
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, "$metadata", objectId: stranger),
            HttpStatusCode.Forbidden, "0x80072560");
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", EntityBody(name), objectId: stranger),
            HttpStatusCode.Forbidden, "0x80072560");
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions(LogicalName='systemuser')/Attributes",
                """{ "@odata.type": "Microsoft.Dynamics.CRM.IntegerAttributeMetadata", "LogicalName": "pb_x" }""", objectId: stranger),
            HttpStatusCode.Forbidden, "0x80072560");
        (await ScalarAsync($"SELECT COUNT(*) FROM meta.entity WHERE logical_name = '{name}'")).Should().Be(0);
    }

    [Fact]
    public async Task Post_duplicate_attribute_names_in_body_returns_400()
    {
        var name = NewName();
        var body = JsonNode.Parse(EntityBody(name))!.AsObject();
        body["Attributes"]!.AsArray().Add(JsonNode.Parse(
            """{ "@odata.type": "Microsoft.Dynamics.CRM.IntegerAttributeMetadata", "LogicalName": "pb_count" }"""));
        body["Attributes"]!.AsArray().Add(JsonNode.Parse(
            """{ "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "pb_count" }"""));

        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", body.ToJsonString()),
            HttpStatusCode.BadRequest, "0x80040203");
        (await ScalarAsync($"SELECT COUNT(*) FROM meta.entity WHERE logical_name = '{name}'")).Should().Be(0);
    }

    [Fact]
    public async Task Post_ignores_client_supplied_MetadataId_and_ObjectTypeCode()
    {
        var systemUser = await GetJsonAsync("EntityDefinitions(LogicalName='systemuser')?$select=LogicalName");
        var takenId = systemUser["MetadataId"]!.GetValue<string>();
        var name = NewName();
        var body = JsonNode.Parse(EntityBody(name))!.AsObject();
        body["MetadataId"] = takenId;
        body["ObjectTypeCode"] = 8;
        body["Attributes"]![0]!["MetadataId"] = takenId;

        var res = await SendAsync(HttpMethod.Post, "EntityDefinitions", body.ToJsonString());

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var created = await GetJsonAsync($"EntityDefinitions(LogicalName='{name}')?$select=ObjectTypeCode&$expand=Attributes($select=LogicalName)");
        created["MetadataId"]!.GetValue<string>().Should().NotBe(takenId);
        created["ObjectTypeCode"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(10000);
        created["Attributes"]!.AsArray().Should().OnlyContain(a => a!["MetadataId"]!.GetValue<string>() != takenId);

        var attribute = $$"""
            { "@odata.type": "Microsoft.Dynamics.CRM.IntegerAttributeMetadata", "LogicalName": "pb_count", "MetadataId": "{{takenId}}" }
            """;
        var added = await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{name}')/Attributes", attribute);
        added.StatusCode.Should().Be(HttpStatusCode.NoContent, await added.Content.ReadAsStringAsync());
        added.Headers.GetValues("OData-EntityId").Single().Should().NotContain(takenId);
    }

    [Theory]
    [InlineData("whoami")]
    [InlineData("retrievecurrentorganization")]
    [InlineData("entitydefinitions")]
    public async Task Post_reserved_entitysetname_returns_400(string setName)
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", EntityBody(NewName(), setName)),
            HttpStatusCode.BadRequest, "0x80040203");
    }
    [Fact]
    public async Task Post_ignores_client_supplied_PrimaryIdAttribute()
    {
        var name = NewName();
        var body = JsonNode.Parse(EntityBody(name))!.AsObject();
        body["PrimaryIdAttribute"] = "createdon";

        var res = await SendAsync(HttpMethod.Post, "EntityDefinitions", body.ToJsonString());

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var created = await GetJsonAsync($"EntityDefinitions(LogicalName='{name}')?$select=PrimaryIdAttribute");
        created["PrimaryIdAttribute"]!.GetValue<string>().Should().Be(name + "id");
        var record = await SendAsync(HttpMethod.Post, name + "s", """{ "pb_name": "first" }""");
        record.StatusCode.Should().Be(HttpStatusCode.NoContent, await record.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_redefining_a_system_attribute_returns_400()
    {
        var name = NewName();
        var body = JsonNode.Parse(EntityBody(name))!.AsObject();
        body["Attributes"]!.AsArray().Add(JsonNode.Parse(
            """{ "@odata.type": "Microsoft.Dynamics.CRM.LookupAttributeMetadata", "LogicalName": "createdby", "Targets": [ "systemuser" ] }"""));

        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", body.ToJsonString()),
            HttpStatusCode.BadRequest, "0x80040203");
        (await ScalarAsync($"SELECT COUNT(*) FROM meta.entity WHERE logical_name = '{name}'")).Should().Be(0);
    }

    [Fact]
    public async Task String_attribute_without_MaxLength_defaults_to_100_and_longer_values_return_400()
    {
        var name = NewName();
        var body = JsonNode.Parse(EntityBody(name))!.AsObject();
        body["Attributes"]!.AsArray().Add(JsonNode.Parse(
            """{ "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "pb_code" }"""));
        (await SendAsync(HttpMethod.Post, "EntityDefinitions", body.ToJsonString())).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await AssertErrorAsync(await SendAsync(HttpMethod.Post, name + "s", $$"""{ "pb_name": "x", "pb_code": "{{new string('a', 150)}}" }"""),
            HttpStatusCode.BadRequest, "0x80040203");
        var ok = await SendAsync(HttpMethod.Post, name + "s", $$"""{ "pb_name": "x", "pb_code": "{{new string('a', 100)}}" }""");
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent, await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metadata_writes_require_prvCreateEntity_and_prvCreateAttribute()
    {
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await AssignRoleAsync(fx, user, StandardRoles.BasicUser);
        var name = NewName();

        var res = await SendAsync(HttpMethod.Post, "EntityDefinitions", EntityBody(name), objectId: oid);
        var body = await res.Content.ReadAsStringAsync();
        await AssertErrorAsync(res, HttpStatusCode.Forbidden, "0x80040220");
        body.Should().Contain("prvCreateEntity");
        (await ScalarAsync($"SELECT COUNT(*) FROM meta.entity WHERE logical_name = '{name}'")).Should().Be(0);

        const string attribute = """{ "@odata.type": "Microsoft.Dynamics.CRM.IntegerAttributeMetadata", "LogicalName": "pb_denied" }""";
        res = await SendAsync(HttpMethod.Post, "EntityDefinitions(LogicalName='systemuser')/Attributes", attribute, objectId: oid);
        body = await res.Content.ReadAsStringAsync();
        await AssertErrorAsync(res, HttpStatusCode.Forbidden, "0x80040220");
        body.Should().Contain("prvCreateAttribute");

        // The System Customizer holds both.
        await AssignRoleAsync(fx, user, StandardRoles.SystemCustomizer);
        (await SendAsync(HttpMethod.Post, "EntityDefinitions", EntityBody(name), objectId: oid)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{name}')/Attributes",
            """{ "@odata.type": "Microsoft.Dynamics.CRM.IntegerAttributeMetadata", "LogicalName": "pb_count" }""", objectId: oid))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Post_ignores_client_supplied_IsIntersect_and_IsManaged()
    {
        var name = NewName();
        var body = JsonNode.Parse(EntityBody(name))!.AsObject();
        body["IsIntersect"] = true;
        body["IsManaged"] = true;
        body["Attributes"]![0]!["IsManaged"] = true;

        var res = await SendAsync(HttpMethod.Post, "EntityDefinitions", body.ToJsonString());

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var created = await GetJsonAsync($"EntityDefinitions(LogicalName='{name}')?$select=IsIntersect,IsManaged&$expand=Attributes($select=IsManaged)");
        created["IsIntersect"]?.GetValue<bool>().Should().NotBe(true);
        created["IsManaged"]?.GetValue<bool>().Should().NotBe(true);
        created["Attributes"]!.AsArray().Should().OnlyContain(a => a!["IsManaged"] == null || a["IsManaged"]!.GetValue<bool>() != true);
        // Not an intersect: it has its own privileges.
        (await ScalarAsync($"SELECT COUNT(*) FROM data.privilege WHERE name = 'prvCreate{name}'")).Should().Be(1);
    }

    [Fact]
    public async Task Post_duplicate_SchemaName_ignoring_case_returns_400()
    {
        var first = NewName();
        (await SendAsync(HttpMethod.Post, "EntityDefinitions", EntityBody(first))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var second = NewName();
        var body = JsonNode.Parse(EntityBody(second))!.AsObject();
        body["SchemaName"] = first.ToUpperInvariant();

        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", body.ToJsonString()),
            HttpStatusCode.BadRequest, "0x80040203");
        (await ScalarAsync($"SELECT COUNT(*) FROM meta.entity WHERE logical_name = '{second}'")).Should().Be(0);

        // Without SchemaName the LogicalName is the SchemaName: a clash with an existing SchemaName is refused too.
        body = JsonNode.Parse(EntityBody(second))!.AsObject();
        body["SchemaName"] = "SystemUser";
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", body.ToJsonString()),
            HttpStatusCode.BadRequest, "0x80040203");
    }
}
