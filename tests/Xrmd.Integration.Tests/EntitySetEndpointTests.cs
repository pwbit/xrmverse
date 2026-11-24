using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public sealed class EntitySetEndpointTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Set = TestEntities.ProjectSet;

    private Guid _adminId;
    private Guid _rootBuId;
    private Guid _orgId;
    private string _orgName = "";

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using (var cmd = TestSql.Command(conn, "SELECT systemuserid, businessunitid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid"))
        {
            cmd.Parameters.Add("@oid", System.Data.SqlDbType.UniqueIdentifier).Value = fx.AdminObjectId;
            await using var rd = await cmd.ExecuteReaderAsync();
            (await rd.ReadAsync()).Should().BeTrue();
            _adminId = rd.GetGuid(0);
            _rootBuId = rd.GetGuid(1);
        }
        await using (var cmd = TestSql.Command(conn, "SELECT TOP 1 organizationid, name FROM data.organization"))
        {
            await using var rd = await cmd.ExecuteReaderAsync();
            (await rd.ReadAsync()).Should().BeTrue();
            _orgId = rd.GetGuid(0);
            _orgName = rd.GetString(1);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? json = null, Guid? objectId = null, Action<HttpRequestMessage>? configure = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        configure?.Invoke(req);
        return await fx.Client.SendAsync(req);
    }

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static async Task AssertErrorAsync(HttpResponseMessage res, HttpStatusCode status, string code, string? messageContains = null)
    {
        var json = await JsonAsync(res, status);
        json["error"]!["code"]!.GetValue<string>().Should().Be(code);
        if (messageContains is not null) json["error"]!["message"]!.GetValue<string>().Should().Contain(messageContains);
    }

    private string ProjectBody(string name = "Apollo") => $$"""
        {
          "pb_name": "{{name}}",
          "pb_budget": 1234.5,
          "pb_count": 7,
          "pb_priority": 2,
          "pb_startdate": "2026-11-07T10:30:15Z",
          "pb_CustomerId@odata.bind": "/systemusers({{_adminId}})"
        }
        """;

    private async Task<Guid> CreateProjectAsync(string name = "Apollo")
    {
        var res = await SendAsync(HttpMethod.Post, Set, ProjectBody(name));
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var entityId = res.Headers.GetValues("OData-EntityId").Single();
        return Guid.Parse(entityId[(entityId.LastIndexOf('(') + 1)..^1]);
    }

    [Fact]
    public async Task Post_returns_204_and_entity_id_header()
    {
        var res = await SendAsync(HttpMethod.Post, Set, ProjectBody());

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var entityId = res.Headers.GetValues("OData-EntityId").Single();
        entityId.Should().MatchRegex($@"^{Regex.Escape(fx.BaseUrl)}api/data/v9\.2/pb_projects\([0-9a-f-]{{36}}\)$");
        res.Headers.Location.Should().Be(new Uri(entityId));
        var id = Guid.Parse(entityId[(entityId.LastIndexOf('(') + 1)..^1]);
        (await JsonAsync(await SendAsync(HttpMethod.Get, $"{Set}({id})?$select=pb_name"), HttpStatusCode.OK))["pb_name"]!
            .GetValue<string>().Should().Be("Apollo");
    }

    [Fact]
    public async Task Post_with_return_representation_returns_201_body()
    {
        var res = await SendAsync(HttpMethod.Post, Set + "?$select=pb_name,pb_budget", ProjectBody("Gemini"),
            configure: r => r.Headers.Add("Prefer", "return=representation"));

        var json = await JsonAsync(res, HttpStatusCode.Created);
        res.Headers.GetValues("Preference-Applied").Single().Should().Be("return=representation");
        res.Headers.GetValues("OData-EntityId").Single().Should().StartWith($"{fx.BaseUrl}api/data/v9.2/pb_projects(");
        res.Headers.Location!.ToString().Should().Be(res.Headers.GetValues("OData-EntityId").Single());
        json["@odata.context"]!.GetValue<string>().Should().StartWith($"{fx.BaseUrl}api/data/v9.2/$metadata#pb_projects");
        json["@odata.etag"]!.GetValue<string>().Should().MatchRegex("^W/\"[0-9]+\"$");
        json["pb_name"]!.GetValue<string>().Should().Be("Gemini");
        json["pb_budget"]!.GetValue<decimal>().Should().Be(1234.5m);
        json["pb_projectid"]!.GetValue<Guid>().Should().NotBeEmpty();
        json.ContainsKey("pb_count").Should().BeFalse();
    }

    [Fact]
    public async Task Lookup_navigation_property_uses_the_relationship_SchemaName()
    {
        // $metadata: the single-valued navigation property of a custom lookup is named after the lookup's SchemaName (the
        // default ReferencingEntityNavigationPropertyName of its relationship), as Dataverse does; a system lookup keeps its logical name.
        var metadata = await SendAsync(HttpMethod.Get, "$metadata");
        metadata.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var reader = System.Xml.XmlReader.Create(await metadata.Content.ReadAsStreamAsync()))
        {
            Microsoft.OData.Edm.Csdl.CsdlReader.TryParse(reader, out var model, out _).Should().BeTrue();
            var project = (Microsoft.OData.Edm.IEdmEntityType)model.FindDeclaredType("Microsoft.Dynamics.CRM.pb_project");
            project.DeclaredProperties.OfType<Microsoft.OData.Edm.IEdmNavigationProperty>().Select(n => n.Name).Should().Contain("pb_CustomerId").And.NotContain("pb_customerid");
            project.DeclaredProperties.OfType<Microsoft.OData.Edm.IEdmNavigationProperty>().Select(n => n.Name).Should().Contain("ownerid");
        }

        // @odata.bind, the associated navigation annotation and $expand use the same name.
        var res = await SendAsync(HttpMethod.Post, Set, $$"""{ "pb_name": "nav", "pb_CustomerId@odata.bind": "/systemusers({{_adminId}})" }""");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var entityId = res.Headers.GetValues("OData-EntityId").Single();
        var id = Guid.Parse(entityId[(entityId.LastIndexOf('(') + 1)..^1]);

        var json = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Set}({id})?$expand=pb_CustomerId($select=fullname)"), HttpStatusCode.OK);
        json["_pb_customerid_value"]!.GetValue<Guid>().Should().Be(_adminId);
        json["_pb_customerid_value@Microsoft.Dynamics.CRM.associatednavigationproperty"]!.GetValue<string>().Should().Be("pb_CustomerId");
        json["pb_CustomerId"]!["fullname"]!.GetValue<string>().Should().Be(XrmdFixture.AdminName);
    }

    [Fact]
    public async Task Lookup_navigation_property_honors_the_relationship_custom_name_and_bind_is_exact()
    {
        var admin = SecurityTestHelper.ServiceFor(fx, fx.AdminObjectId);
        var child = "pb_nv" + Guid.NewGuid().ToString("N")[..10];
        await admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.CreateEntityRequest
        {
            Entity = TestHelpers.Entity(child, child, "Nav child"),
            PrimaryAttribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata
            {
                LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 100, DisplayName = TestHelpers.Label("Name"),
            },
        });
        await admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.CreateOneToManyRequest
        {
            OneToManyRelationship = new Microsoft.Xrm.Sdk.Metadata.OneToManyRelationshipMetadata
            {
                SchemaName = child + "_project", ReferencedEntity = "pb_project", ReferencingEntity = child, ReferencedAttribute = "pb_projectid",
                ReferencingEntityNavigationPropertyName = "pb_CustomParent",
            },
            Lookup = new Microsoft.Xrm.Sdk.Metadata.LookupAttributeMetadata
            {
                LogicalName = "pb_parentid", SchemaName = "pb_ParentId", DisplayName = TestHelpers.Label("Parent"),
            },
        });
        var childSet = fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(child)!.EntitySetName;

        var metadata = await SendAsync(HttpMethod.Get, "$metadata");
        using (var reader = System.Xml.XmlReader.Create(await metadata.Content.ReadAsStreamAsync()))
        {
            Microsoft.OData.Edm.Csdl.CsdlReader.TryParse(reader, out var model, out _).Should().BeTrue();
            var type = (Microsoft.OData.Edm.IEdmEntityType)model.FindDeclaredType("Microsoft.Dynamics.CRM." + child);
            type.DeclaredProperties.OfType<Microsoft.OData.Edm.IEdmNavigationProperty>().Select(n => n.Name).Should().Contain("pb_CustomParent")
                .And.NotContain("pb_ParentId");
        }

        // @odata.bind is exact: only the navigation property of $metadata (Dataverse answers 400 to the logical name or the SchemaName).
        foreach (var wrong in new[] { "pb_ParentId", "pb_parentid", "pb_customparent" })
            await AssertErrorAsync(await SendAsync(HttpMethod.Post, childSet, $$"""{ "pb_name": "x", "{{wrong}}@odata.bind": "/pb_projects({{Guid.NewGuid()}})" }"""),
                HttpStatusCode.BadRequest, "0x80040203", $"'{wrong}' is not a single-valued navigation property");
        var project = await CreateProjectAsync();
        var res = await SendAsync(HttpMethod.Post, childSet, $$"""{ "pb_name": "x", "pb_CustomParent@odata.bind": "/pb_projects({{project}})" }""");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var entityId = res.Headers.GetValues("OData-EntityId").Single();
        var id = Guid.Parse(entityId[(entityId.LastIndexOf('(') + 1)..^1]);

        var json = await JsonAsync(await SendAsync(HttpMethod.Get, $"{childSet}({id})?$expand=pb_CustomParent($select=pb_name)"), HttpStatusCode.OK);
        json["_pb_parentid_value@Microsoft.Dynamics.CRM.associatednavigationproperty"]!.GetValue<string>().Should().Be("pb_CustomParent");
        json["pb_CustomParent"]!["pb_name"]!.GetValue<string>().Should().Be("Apollo");

        // Issue #190: a nested $expand of single-valued navigation properties, on a record and on a collection.
        const string nestedExpand = "pb_CustomParent($select=pb_name;$expand=createdby($select=fullname))";
        var single = await JsonAsync(await SendAsync(HttpMethod.Get, $"{childSet}({id})?$select=pb_name&$expand={nestedExpand}"), HttpStatusCode.OK);
        single["@odata.context"]!.GetValue<string>().Should().EndWith($"#{childSet}(pb_name,pb_CustomParent(pb_name,createdby(fullname)))/$entity");
        var parent = single["pb_CustomParent"]!.AsObject();
        parent["pb_name"]!.GetValue<string>().Should().Be("Apollo");
        parent["pb_projectid"]!.GetValue<Guid>().Should().Be(project);
        parent["createdby"]!["fullname"]!.GetValue<string>().Should().Be(XrmdFixture.AdminName);
        parent["createdby"]!["systemuserid"]!.GetValue<Guid>().Should().Be(_adminId);
        single.ContainsKey("createdby").Should().BeFalse("the nested record is written inside its parent only");
        single.Select(p => p.Key).Where(k => !k.StartsWith('@')).Should().NotContain(k => k.Contains('.'), "no aliased value of a link leaks to the root");

        var collection = await JsonAsync(await SendAsync(HttpMethod.Get, $"{childSet}?$select=pb_name&$filter={Uri.EscapeDataString("pb_name eq 'x'")}&$expand={nestedExpand}"), HttpStatusCode.OK);
        collection["@odata.context"]!.GetValue<string>().Should().EndWith($"#{childSet}(pb_name,pb_CustomParent(pb_name,createdby(fullname)))");
        var row = collection["value"]!.AsArray().Should().ContainSingle().Subject!.AsObject();
        row["pb_CustomParent"]!["createdby"]!["fullname"]!.GetValue<string>().Should().Be(XrmdFixture.AdminName);

        // Options Learn documents for collection-valued navigation properties only stay refused inside a single-valued expand.
        var refused = await SendAsync(HttpMethod.Get, $"{childSet}?$expand=pb_CustomParent($expand=createdby($filter=fullname eq 'x'))");
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bind_with_the_logical_name_of_a_custom_lookup_is_400()
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, Set, $$"""{ "pb_name": "x", "pb_customerid@odata.bind": "/systemusers({{_adminId}})" }"""),
            HttpStatusCode.BadRequest, "0x80040203", "'pb_customerid' is not a single-valued navigation property");
    }

    [Fact]
    public async Task Get_returns_lookup_annotations()
    {
        var id = await CreateProjectAsync();

        var json = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Set}({id})"), HttpStatusCode.OK);

        json["@odata.etag"]!.GetValue<string>().Should().MatchRegex("^W/\"[0-9]+\"$");
        json["@odata.context"]!.GetValue<string>().Should().Be($"{fx.BaseUrl}api/data/v9.2/$metadata#pb_projects/$entity");
        json["pb_projectid"]!.GetValue<Guid>().Should().Be(id);
        json["_pb_customerid_value"]!.GetValue<Guid>().Should().Be(_adminId);
        json["_pb_customerid_value@Microsoft.Dynamics.CRM.lookuplogicalname"]!.GetValue<string>().Should().Be("systemuser");
        json["_pb_customerid_value@Microsoft.Dynamics.CRM.associatednavigationproperty"]!.GetValue<string>().Should().Be("pb_CustomerId");
        json["_ownerid_value"]!.GetValue<Guid>().Should().Be(_adminId);
        json["_ownerid_value@Microsoft.Dynamics.CRM.lookuplogicalname"]!.GetValue<string>().Should().Be("systemuser");
        json["_owningbusinessunit_value"]!.GetValue<Guid>().Should().Be(_rootBuId);
        json["pb_budget"]!.GetValue<decimal>().Should().Be(1234.5m);
        json["pb_priority"]!.GetValue<int>().Should().Be(2);
        json["pb_count"]!.GetValue<int>().Should().Be(7);
        json["pb_startdate"]!.GetValue<string>().Should().Be("2026-11-07T10:30:15Z");
        json["statecode"]!.GetValue<int>().Should().Be(0);
        json["createdon"]!.GetValue<string>().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$");
    }

    [Fact]
    public async Task Get_with_select_returns_only_selected_columns_and_etag()
    {
        var id = await CreateProjectAsync();

        var json = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Set}({id})?$select=pb_name,_pb_customerid_value"), HttpStatusCode.OK);

        json.Select(p => p.Key).Where(k => !k.StartsWith('@') && !k.Contains("_value@")).Should()
            .BeEquivalentTo("pb_name", "_pb_customerid_value", "pb_projectid");
        json["@odata.etag"]!.GetValue<string>().Should().MatchRegex("^W/\"[0-9]+\"$");
    }

    [Fact]
    public async Task Patch_updates_field()
    {
        var id = await CreateProjectAsync();
        var before = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Set}({id})?$select=pb_name"), HttpStatusCode.OK);

        var res = await SendAsync(HttpMethod.Patch, $"{Set}({id})", """{ "pb_name": "Renamed", "pb_count": 9 }""",
            configure: r => r.Headers.IfMatch.Add(EntityTagHeaderValue.Any));

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var after = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Set}({id})?$select=pb_name,pb_count"), HttpStatusCode.OK);
        after["pb_name"]!.GetValue<string>().Should().Be("Renamed");
        after["pb_count"]!.GetValue<int>().Should().Be(9);
        after["@odata.etag"]!.GetValue<string>().Should().NotBe(before["@odata.etag"]!.GetValue<string>());
    }

    [Fact]
    public async Task Patch_if_match_star_missing_is_404()
    {
        var id = Guid.NewGuid();

        await AssertErrorAsync(await SendAsync(HttpMethod.Patch, $"{Set}({id})", """{ "pb_name": "Ghost" }""",
                configure: r => r.Headers.IfMatch.Add(EntityTagHeaderValue.Any)),
            HttpStatusCode.NotFound, "0x80040217");
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, $"{Set}({id})"), HttpStatusCode.NotFound, "0x80040217");
    }

    [Fact]
    public async Task Patch_missing_row_creates_it()
    {
        var id = Guid.NewGuid();

        var res = await SendAsync(HttpMethod.Patch, $"{Set}({id})", """{ "pb_name": "Born by patch", "pb_count": 3 }""");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var row = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Set}({id})?$select=pb_name,pb_count"), HttpStatusCode.OK);
        row["pb_projectid"]!.GetValue<Guid>().Should().Be(id);
        row["pb_name"]!.GetValue<string>().Should().Be("Born by patch");
    }

    [Fact]
    public async Task Patch_existing_row_updates()
    {
        var id = await CreateProjectAsync();

        var res = await SendAsync(HttpMethod.Patch, $"{Set}({id})", """{ "pb_name": "Upserted" }""");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var row = await JsonAsync(await SendAsync(HttpMethod.Get, $"{Set}({id})?$select=pb_name,pb_count"), HttpStatusCode.OK);
        row["pb_name"]!.GetValue<string>().Should().Be("Upserted");
        row["pb_count"]!.GetValue<int>().Should().Be(7);
    }

    [Fact]
    public async Task Patch_if_none_match_star_existing_is_412()
    {
        var id = await CreateProjectAsync("Keeper");

        var res = await SendAsync(HttpMethod.Patch, $"{Set}({id})", """{ "pb_name": "Overwritten" }""",
            configure: r => r.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any));

        await AssertErrorAsync(res, HttpStatusCode.PreconditionFailed, "0x80040237");
        (await JsonAsync(await SendAsync(HttpMethod.Get, $"{Set}({id})?$select=pb_name"), HttpStatusCode.OK))["pb_name"]!
            .GetValue<string>().Should().Be("Keeper");
    }

    [Fact]
    public async Task Patch_if_none_match_star_missing_creates()
    {
        var id = Guid.NewGuid();

        var res = await SendAsync(HttpMethod.Patch, $"{Set}({id})", """{ "pb_name": "Create only" }""",
            configure: r => r.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any));

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        (await JsonAsync(await SendAsync(HttpMethod.Get, $"{Set}({id})?$select=pb_name"), HttpStatusCode.OK))["pb_name"]!
            .GetValue<string>().Should().Be("Create only");
    }

    [Fact]
    public async Task Patch_if_none_match_star_without_create_privilege_is_403_whether_the_row_exists_or_not()
    {
        var oid = Guid.NewGuid();
        var user = await SecurityTestHelper.CreateUserAsync(fx, await SecurityTestHelper.RootBusinessUnitAsync(fx), oid);
        await SecurityTestHelper.GrantAsync(fx, user, false,
            ("prvReadpb_Project", Xrmd.Security.DepthMask.Global), ("prvWritepb_Project", Xrmd.Security.DepthMask.Global));
        var existing = await CreateProjectAsync("Hidden");

        foreach (var id in new[] { existing, Guid.NewGuid() })
        {
            var res = await SendAsync(HttpMethod.Patch, $"{Set}({id})", """{ "pb_name": "x" }""", objectId: oid,
                configure: r => r.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any));
            await AssertErrorAsync(res, HttpStatusCode.Forbidden, "0x80040220", "prvCreatepb_Project");
        }
    }

    [Fact]
    public async Task Patch_body_primary_key_other_than_the_url_id_is_refused()
    {
        var id = Guid.NewGuid();

        var res = await SendAsync(HttpMethod.Patch, $"{Set}({id})", $$"""{ "pb_projectid": "{{Guid.NewGuid()}}", "pb_name": "x" }""");

        await AssertErrorAsync(res, HttpStatusCode.BadRequest, "0x80040203", "The primary key in the body does not match the URL.");
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, $"{Set}({id})"), HttpStatusCode.NotFound, "0x80040217");
    }

    [Fact]
    public async Task Patch_etag_values_are_refused()
    {
        var id = await CreateProjectAsync();

        await AssertErrorAsync(await SendAsync(HttpMethod.Patch, $"{Set}({id})", """{ "pb_name": "x" }""",
            configure: r => r.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""))), HttpStatusCode.BadRequest, "0x80040203");
        await AssertErrorAsync(await SendAsync(HttpMethod.Patch, $"{Set}({id})", """{ "pb_name": "x" }""",
            configure: r => r.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"1\""))), HttpStatusCode.BadRequest, "0x80040203");
    }

    [Fact]
    public async Task Patch_if_match_star_with_return_representation_is_200()
    {
        var id = await CreateProjectAsync();

        var res = await SendAsync(HttpMethod.Patch, $"{Set}({id})?$select=pb_name", """{ "pb_name": "Rep" }""", configure: r =>
        {
            r.Headers.IfMatch.Add(EntityTagHeaderValue.Any);
            r.Headers.Add("Prefer", "return=representation");
        });

        (await JsonAsync(res, HttpStatusCode.OK))["pb_name"]!.GetValue<string>().Should().Be("Rep");
    }

    [Fact]
    public async Task Patch_both_headers_is_400()
    {
        var id = await CreateProjectAsync();

        var res = await SendAsync(HttpMethod.Patch, $"{Set}({id})", """{ "pb_name": "x" }""", configure: r =>
        {
            r.Headers.IfMatch.Add(EntityTagHeaderValue.Any);
            r.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        });

        await AssertErrorAsync(res, HttpStatusCode.BadRequest, "0x80040203");
    }

    [Fact]
    public async Task Patch_return_representation_201_then_200()
    {
        var id = Guid.NewGuid();
        void Prefer(HttpRequestMessage r) => r.Headers.Add("Prefer", "return=representation");

        var created = await SendAsync(HttpMethod.Patch, $"{Set}({id})?$select=pb_name", """{ "pb_name": "First" }""", configure: Prefer);
        var first = await JsonAsync(created, HttpStatusCode.Created);
        first["pb_name"]!.GetValue<string>().Should().Be("First");
        created.Headers.GetValues("Preference-Applied").Single().Should().Be("return=representation");

        var updated = await SendAsync(HttpMethod.Patch, $"{Set}({id})?$select=pb_name", """{ "pb_name": "Second" }""", configure: Prefer);
        (await JsonAsync(updated, HttpStatusCode.OK))["pb_name"]!.GetValue<string>().Should().Be("Second");
    }

    [Fact]
    public async Task Delete_then_Get_returns_404_with_ObjectDoesNotExist_code()
    {
        var id = await CreateProjectAsync();

        (await SendAsync(HttpMethod.Delete, $"{Set}({id})")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await AssertErrorAsync(await SendAsync(HttpMethod.Get, $"{Set}({id})"), HttpStatusCode.NotFound, "0x80040217", id.ToString());
        await AssertErrorAsync(await SendAsync(HttpMethod.Delete, $"{Set}({id})"), HttpStatusCode.NotFound, "0x80040217");
    }

    [Fact]
    public async Task Post_string_over_maxlength_returns_400()
    {
        var res = await SendAsync(HttpMethod.Post, Set, $$"""{ "pb_name": "{{new string('x', 201)}}" }""");

        await AssertErrorAsync(res, HttpStatusCode.BadRequest, "0x80040203", "pb_name");
    }

    [Theory]
    [InlineData("pb_ratio", "1e400")]
    [InlineData("pb_budget", "1e20")]
    [InlineData("pb_budget", "922337203685477.5808")]
    [InlineData("pb_count", "2147483648")]
    public async Task Post_numeric_overflow_returns_400_naming_the_attribute(string attribute, string literal)
    {
        var res = await SendAsync(HttpMethod.Post, Set, $$"""{ "pb_name": "x", "{{attribute}}": {{literal}} }""");

        await AssertErrorAsync(res, HttpStatusCode.BadRequest, "0x80040203", attribute);
    }

    [Fact]
    public async Task Post_double_and_money_at_limits_round_trip()
    {
        // The largest money a record in the base currency holds: its _base (value / 1) reaches the base money limit (plan 2.10, X10).
        var res = await SendAsync(HttpMethod.Post, Set + "?$select=pb_ratio,pb_budget",
            """{ "pb_name": "x", "pb_ratio": 1e300, "pb_budget": 922337203685477 }""",
            configure: r => r.Headers.Add("Prefer", "return=representation"));

        var json = await JsonAsync(res, HttpStatusCode.Created);
        json["pb_ratio"]!.GetValue<double>().Should().Be(1e300);
        json["pb_budget"]!.GetValue<decimal>().Should().Be(922337203685477m);

        // Beyond it the value is rounded to the currency's 2 decimals, then its _base is refused as an overflow.
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, Set, """{ "pb_name": "x", "pb_budget": 922337203685477.5807 }"""),
            HttpStatusCode.BadRequest, "0x80048cec", "pb_budget_base");
    }

    [Fact]
    public async Task Post_non_ISO_date_returns_400()
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, Set, """{ "pb_name": "x", "pb_startdate": "11/07/2026" }"""),
            HttpStatusCode.BadRequest, "0x80040203", "pb_startdate");
    }

    [Fact]
    public async Task Post_unknown_attribute_returns_400_naming_it()
    {
        var res = await SendAsync(HttpMethod.Post, Set, """{ "pb_name": "x", "pb_nosuchfield": 1 }""");

        await AssertErrorAsync(res, HttpStatusCode.BadRequest, "0x80040203", "pb_nosuchfield");
    }

    [Fact]
    public async Task Post_lookup_to_missing_record_returns_404_ObjectDoesNotExist()
    {
        var missing = Guid.NewGuid();

        var res = await SendAsync(HttpMethod.Post, Set, $$"""{ "pb_name": "x", "pb_CustomerId@odata.bind": "/systemusers({{missing}})" }""");

        await AssertErrorAsync(res, HttpStatusCode.NotFound, "0x80040217", missing.ToString());
    }

    [Fact]
    public async Task Malformed_body_returns_400()
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, Set, "{ not json"), HttpStatusCode.BadRequest, "0x80040203");
        await AssertErrorAsync(await SendAsync(HttpMethod.Post, Set, "[1]"), HttpStatusCode.BadRequest, "0x80040203");
    }

    [Theory]
    [InlineData("GET", "pb_nosuchset(00000000-0000-0000-0000-000000000001)")]
    [InlineData("POST", "pb_nosuchset")]
    [InlineData("DELETE", "pb_nosuchset(00000000-0000-0000-0000-000000000001)")]
    public async Task Unknown_entity_set_returns_404_with_error_envelope(string method, string path)
    {
        var res = await SendAsync(new HttpMethod(method), path, method == "POST" ? "{}" : null);

        await AssertErrorAsync(res, HttpStatusCode.NotFound, "0x80060888", "pb_nosuchset");
    }

    [Theory]
    [InlineData("pb_projects(notaguid)")]
    [InlineData("pb_projects(pb_name='x')")]
    [InlineData("pb_projects({0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70})")]
    [InlineData("pb_projects(0b6c7f2e91d44f0e8a112b3c4d5e6f70)")]
    public async Task Invalid_key_returns_400(string path)
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, path), HttpStatusCode.BadRequest, "0x80040203");
    }

    [Theory]
    [InlineData("GET", "pb_projects(00000000-0000-0000-0000-000000000001)")]
    [InlineData("POST", "pb_projects")]
    [InlineData("GET", "WhoAmI")]
    [InlineData("GET", "RetrieveCurrentOrganization(AccessType=@p)?@p=Microsoft.Dynamics.CRM.EndpointAccessType'Default'")]
    public async Task Caller_without_systemuser_gets_403(string method, string path)
    {
        var res = await SendAsync(new HttpMethod(method), path, method == "POST" ? """{ "pb_name": "x" }""" : null, objectId: Guid.NewGuid());

        await AssertErrorAsync(res, HttpStatusCode.Forbidden, "0x80072560");
    }

    [Fact]
    public async Task WhoAmI_returns_ids()
    {
        var res = await SendAsync(HttpMethod.Get, "WhoAmI");

        var json = await JsonAsync(res, HttpStatusCode.OK);
        json["@odata.context"]!.GetValue<string>().Should().Be($"{fx.BaseUrl}api/data/v9.2/$metadata#Microsoft.Dynamics.CRM.WhoAmIResponse");
        json["UserId"]!.GetValue<Guid>().Should().Be(_adminId);
        json["BusinessUnitId"]!.GetValue<Guid>().Should().Be(_rootBuId);
        json["OrganizationId"]!.GetValue<Guid>().Should().Be(_orgId);
    }

    [Theory]
    [InlineData("RetrieveCurrentOrganization(AccessType=@p)?@p=Microsoft.Dynamics.CRM.EndpointAccessType'Default'")]
    [InlineData("RetrieveCurrentOrganization(AccessType=Microsoft.Dynamics.CRM.EndpointAccessType'Internet')")]
    public async Task RetrieveCurrentOrganization_returns_detail(string path)
    {
        var res = await SendAsync(HttpMethod.Get, path);

        var json = await JsonAsync(res, HttpStatusCode.OK);
        json["@odata.context"]!.GetValue<string>().Should()
            .Be($"{fx.BaseUrl}api/data/v9.2/$metadata#Microsoft.Dynamics.CRM.RetrieveCurrentOrganizationResponse");
        var detail = json["Detail"]!.AsObject();
        detail["OrganizationId"]!.GetValue<Guid>().Should().Be(_orgId);
        detail["FriendlyName"]!.GetValue<string>().Should().Be(_orgName);
        detail["OrganizationVersion"]!.GetValue<string>().Should().Be("9.2.0.0");
        detail["State"]!.GetValue<string>().Should().Be("Enabled");
        _orgName.Should().Be(XrmdFixture.OrgName); // "Test Org"
        detail["UrlName"]!.GetValue<string>().Should().Be("testorg");
        detail["UniqueName"]!.GetValue<string>().Should().Be("testorg");
        detail["EnvironmentId"]!.GetValue<string>().Should().Be(_orgId.ToString());
        detail["Geo"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        detail["TenantId"]!.GetValue<Guid>().Should().Be(Guid.Empty);

        var endpoints = detail["Endpoints"]!.AsObject();
        endpoints["Count"]!.GetValue<int>().Should().Be(3);
        endpoints["IsReadOnly"]!.GetValue<bool>().Should().BeFalse();
        endpoints["Keys"]!.AsArray().Select(k => k!.GetValue<string>()).Should()
            .Equal("WebApplication", "OrganizationService", "OrganizationDataService");
        endpoints["Values"]!.AsArray().Select(v => v!.GetValue<string>()).Should().Equal(
            fx.BaseUrl,
            $"{fx.BaseUrl}XRMServices/2011/Organization.svc",
            $"{fx.BaseUrl}XRMServices/2011/OrganizationData.svc");
    }

    [Theory]
    [InlineData("RetrieveCurrentOrganization(AccessType=@p)?@p=Microsoft.Dynamics.CRM.EndpointAccessType'Bogus'")]
    [InlineData("RetrieveCurrentOrganization(AccessType=@p)")]
    [InlineData("RetrieveCurrentOrganization(Other=1)")]
    public async Task RetrieveCurrentOrganization_with_invalid_AccessType_returns_400(string path)
    {
        await AssertErrorAsync(await SendAsync(HttpMethod.Get, path), HttpStatusCode.BadRequest, "0x80040203");
    }
}
