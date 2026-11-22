using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Custom APIs on the Web API (plan 15, task 5, C8): global, entity-bound and collection-bound actions and functions, their
/// request bodies and function parameters read into the SDK request of the API, and their responses (204, the record or
/// the collection of a lone Entity or EntityCollection property, otherwise the <c>{name}Response</c> complex type).
/// The errors are the SDK path's.
/// </summary>
[Collection("solution-import")]
public sealed class CustomApiWebApiTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Solution = "XtCustomApiWeb";
    private const string Echo = "Xrmd.TestPlugins.CustomApiEchoPlugin";
    private const string Ns = "Microsoft.Dynamics.CRM.";

    /// <summary>One request parameter of each of the 13 types, by name and type.</summary>
    private static readonly (string Name, int Type)[] AllTypes =
    [
        ("Bool", 0), ("Date", 1), ("Dec", 2), ("Ent", 3), ("Ents", 4), ("Ref", 5), ("Flt", 6), ("Int", 7), ("Cash", 8), ("Pick", 9),
        ("Text", 10), ("Texts", 11), ("Id", 12),
    ];

    private static readonly TestApi EchoApi = new("xt_Echo", AllowedStepType: 2, PluginTypeName: Echo,
        Requests: AllTypes.Select(t => (t.Name, t.Type, t.Name != "Text", (string?)null)).ToArray(),
        Responses: AllTypes.Select(t => (t.Name + "Out", t.Type)).Append(("Context", 10)).ToArray());

    private static readonly TestApi GetApi = new("xt_Get", IsFunction: true, PluginTypeName: Echo,
        Requests: [("Name", 10, false, null), ("Count", 7, true, null), ("Ref", 5, true, null), ("Texts", 11, true, null)],
        Responses: [("NameOut", 10), ("CountOut", 7), ("RefOut", 5), ("TextsOut", 11)]);

    private static readonly TestApi NoResponseApi = new("xt_NoResponse", Requests: [("Text", 10, true, null)]);

    private static readonly TestApi BoundApi = new("xt_Bound", BindingType: 1, BoundEntity: TestEntities.Pipe, PluginTypeName: Echo,
        Requests: [("Text", 10, false, null)], Responses: [("TextOut", 10), ("Context", 10)]);

    private static readonly TestApi BoundFunction = BoundApi with { UniqueName = "xt_BoundGet", IsFunction = true };

    private static readonly TestApi CollectionApi = BoundApi with { UniqueName = "xt_Many", BindingType = 2 };

    private static readonly TestApi CollectionFunction = CollectionApi with { UniqueName = "xt_ManyGet", IsFunction = true };

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
    }

    public Task DisposeAsync() => SolutionImportTests.ClearImportAsync(fx);

    /// <summary>
    /// Defines <paramref name="apis"/> through the SDK (plan 2.7a, Task 6: as the solution <see cref="Solution"/> would import them;
    /// the class runs on both providers, solution import is 2.7b on PostgreSQL).
    /// </summary>
    private Task ImportAsync(params TestApi[] apis) => CustomApiTestSolution.CreateAsync(Admin, apis);

    private async Task<Guid> AdminUserIdAsync() => (await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId)).UserId;

    private async Task<(HttpStatusCode Status, JsonNode? Body, string Text)> SendAsync(HttpMethod method, string path, JsonNode? body = null,
        string? contentType = null)
    {
        var request = new HttpRequestMessage(method, "/api/data/v9.2/" + path);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/json");
        }
        else if (method == HttpMethod.Post) request.Content = new StringContent("", Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text), text);
    }

    private async Task<JsonNode> OkAsync(HttpMethod method, string path, JsonNode? body = null)
    {
        var (status, json, text) = await SendAsync(method, path, body);
        status.Should().Be(HttpStatusCode.OK, $"{method} {path}: {text}");
        return json!;
    }

    private async Task<string> ErrorAsync(HttpMethod method, string path, HttpStatusCode expected, string code, JsonNode? body = null)
    {
        var (status, json, text) = await SendAsync(method, path, body);
        status.Should().Be(expected, $"{method} {path}: {text}");
        json!["error"]!["code"]!.GetValue<string>().Should().Be(code, text);
        return json["error"]!["message"]!.GetValue<string>();
    }

    private Task<string> InvalidAsync(HttpMethod method, string path, JsonNode? body = null) =>
        ErrorAsync(method, path, HttpStatusCode.BadRequest, "0x80040203", body);

    private Task<string> UnknownSegmentAsync(HttpMethod method, string path, JsonNode? body = null) =>
        ErrorAsync(method, path, HttpStatusCode.NotFound, "0x80060888", body);

    // ---- $metadata ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Metadata_declares_the_imported_apis_and_follows_a_later_import()
    {
        await ImportAsync(EchoApi, BoundApi, CollectionFunction, NoResponseApi with { IsPrivate = true, UniqueName = "xt_Hidden" });

        async Task<string> MetadataAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/data/v9.2/$metadata");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
            var response = await fx.Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, text);
            return text;
        }
        var csdl = await MetadataAsync();
        csdl.Should().Contain("Name=\"xt_Echo\"").And.Contain("Name=\"xt_EchoResponse\"").And.Contain("ActionImport Name=\"xt_Echo\"");
        using (var reader = System.Xml.XmlReader.Create(new StringReader(csdl)))
        {
            Microsoft.OData.Edm.Csdl.CsdlReader.TryParse(reader, out var model, out var errors).Should().BeTrue(string.Join("; ", errors?.Select(e => e.ErrorMessage) ?? []));
            model.FindDeclaredOperations(Ns + "xt_Bound").Single().Parameters.First().Name.Should().Be("entity");
            model.FindDeclaredOperations(Ns + "xt_ManyGet").Single().Parameters.First().Name.Should().Be("entityset");
        }
        csdl.Should().NotContain("xt_Hidden").And.NotContain("xt_GetResponse");

        await ImportAsync(EchoApi, BoundApi, CollectionFunction, GetApi);
        (await MetadataAsync()).Should().Contain("Name=\"xt_Get\"", "$metadata shows the APIs committed by the latest import");
    }

    // ---- global ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Web_api_calls_global_action_and_function()
    {
        await ImportAsync(EchoApi, GetApi, NoResponseApi);
        var entityId = Guid.NewGuid();
        var refId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var body = new JsonObject
        {
            ["Bool"] = true,
            ["Date"] = "2026-11-10T09:30:00+01:00",
            ["Dec"] = 12.345m,
            ["Ent"] = new JsonObject { ["@odata.type"] = Ns + TestEntities.Pipe, ["pb_pipeid"] = entityId.ToString(), ["pb_name"] = "Ada" },
            ["Ents"] = new JsonArray(
                new JsonObject { ["@odata.type"] = Ns + TestEntities.Pipe, ["pb_name"] = "One" },
                new JsonObject { ["@odata.type"] = "#" + Ns + TestEntities.Pipe, ["pb_name"] = "Two" }),
            ["Ref"] = new JsonObject { ["@odata.type"] = Ns + TestEntities.Pipe, ["pb_pipeid"] = refId.ToString() },
            ["Flt"] = 2.5,
            ["Int"] = 42,
            ["Cash"] = 9.99m,
            ["Pick"] = 3,
            ["Text"] = "abc",
            ["Texts"] = new JsonArray("a", "b"),
            ["Id"] = id.ToString(),
        };

        var echo = await OkAsync(HttpMethod.Post, "xt_Echo", body);

        echo["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.xt_EchoResponse");
        echo["BoolOut"]!.GetValue<bool>().Should().BeTrue();
        echo["DateOut"]!.GetValue<string>().Should().Be("2026-11-10T08:30:00Z");
        echo["DecOut"]!.GetValue<decimal>().Should().Be(12.345m);
        echo["EntOut"]!["@odata.type"]!.GetValue<string>().Should().Be("#" + Ns + TestEntities.Pipe);
        echo["EntOut"]!["pb_pipeid"]!.GetValue<string>().Should().Be(entityId.ToString());
        echo["EntOut"]!["pb_name"]!.GetValue<string>().Should().Be("Ada");
        echo["EntsOut"]!.AsArray().Select(e => e!["pb_name"]!.GetValue<string>()).Should().Equal("One", "Two");
        echo["EntsOut"]![0]!["@odata.type"]!.GetValue<string>().Should().Be("#" + Ns + TestEntities.Pipe);
        echo["RefOut"]!["@odata.type"]!.GetValue<string>().Should().Be("#" + Ns + TestEntities.Pipe);
        echo["RefOut"]!["pb_pipeid"]!.GetValue<string>().Should().Be(refId.ToString());
        echo["FltOut"]!.GetValue<double>().Should().Be(2.5);
        echo["IntOut"]!.GetValue<int>().Should().Be(42);
        echo["CashOut"]!.GetValue<decimal>().Should().Be(9.99m);
        echo["PickOut"]!.GetValue<int>().Should().Be(3);
        echo["TextOut"]!.GetValue<string>().Should().Be("abc");
        echo["TextsOut"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("a", "b");
        echo["IdOut"]!.GetValue<string>().Should().Be(id.ToString());
        echo["Context"]!.GetValue<string>().Should()
            .Be($"Stage=30;PrimaryEntityName=none;PrimaryEntityId={Guid.Empty};UserId={await AdminUserIdAsync()};Outputs=");

        // The qualified name, and the optional parameters left out: their outputs are the defaults of their types.
        var minimal = await OkAsync(HttpMethod.Post, Ns + "xt_Echo", new JsonObject { ["Text"] = "only" });
        minimal["TextOut"]!.GetValue<string>().Should().Be("only");
        minimal["IntOut"]!.GetValue<int>().Should().Be(0);
        minimal["RefOut"].Should().BeNull();

        // A function: inline values, aliases (the alias value URL-encoded), the qualified name.
        var aliased = await OkAsync(HttpMethod.Get, "xt_Get(Name=@p1)?@p1='a%26b'");
        aliased["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.xt_GetResponse");
        aliased["NameOut"]!.GetValue<string>().Should().Be("a&b");
        var inline = await OkAsync(HttpMethod.Get, $"{Ns}xt_Get(Name='it''s',Count=7,Ref=@r,Texts=@t)?@r={{'@odata.id':'{TestEntities.PipeSet}({refId})'}}&@t=[\"x\",\"y\"]");
        inline["NameOut"]!.GetValue<string>().Should().Be("it's");
        inline["CountOut"]!.GetValue<int>().Should().Be(7);
        inline["RefOut"]!["pb_pipeid"]!.GetValue<string>().Should().Be(refId.ToString());
        inline["TextsOut"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("x", "y");

        // An action without response properties: 204.
        var (status, _, text) = await SendAsync(HttpMethod.Post, "xt_NoResponse", new JsonObject { ["Text"] = "x" });
        status.Should().Be(HttpStatusCode.NoContent, text);
        (await SendAsync(HttpMethod.Post, "xt_NoResponse")).Status.Should().Be(HttpStatusCode.NoContent);
    }

    // ---- bound -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Web_api_calls_bound_operations()
    {
        await ImportAsync(BoundApi, BoundFunction, CollectionApi, CollectionFunction);
        var id = Guid.NewGuid();
        var user = await AdminUserIdAsync();
        string Context(string entity, Guid primary) => $"Stage=30;PrimaryEntityName={entity};PrimaryEntityId={primary};UserId={user};Outputs=";

        var bound = await OkAsync(HttpMethod.Post, $"{TestEntities.PipeSet}({id})/{Ns}xt_Bound", new JsonObject { ["Text"] = "b" });
        bound["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.xt_BoundResponse");
        bound["TextOut"]!.GetValue<string>().Should().Be("b");
        bound["Context"]!.GetValue<string>().Should().Be(Context(TestEntities.Pipe, id));

        var boundGet = await OkAsync(HttpMethod.Get, $"{TestEntities.PipeSet}({id})/{Ns}xt_BoundGet(Text=@p1)?@p1='g'");
        boundGet["TextOut"]!.GetValue<string>().Should().Be("g");
        boundGet["Context"]!.GetValue<string>().Should().Be(Context(TestEntities.Pipe, id));

        // Collection-bound: no Target.
        var many = await OkAsync(HttpMethod.Post, $"{TestEntities.PipeSet}/{Ns}xt_Many", new JsonObject { ["Text"] = "m" });
        many["TextOut"]!.GetValue<string>().Should().Be("m");
        many["Context"]!.GetValue<string>().Should().Be(Context(TestEntities.Pipe, Guid.Empty));

        var manyGet = await OkAsync(HttpMethod.Get, $"{TestEntities.PipeSet}/{Ns}xt_ManyGet(Text='n')");
        manyGet["TextOut"]!.GetValue<string>().Should().Be("n");
        manyGet["Context"]!.GetValue<string>().Should().Be(Context(TestEntities.Pipe, Guid.Empty));

        // The record of an entity-bound call is the one in the URL: a Target in the body is refused.
        (await InvalidAsync(HttpMethod.Post, $"{TestEntities.PipeSet}({id})/{Ns}xt_Bound",
            new JsonObject { ["Text"] = "b", ["Target"] = new JsonObject { ["@odata.type"] = Ns + TestEntities.Pipe, ["pb_pipeid"] = id.ToString() } }))
            .Should().Contain("'Target'");
    }

    [Fact]
    public async Task Bound_operation_on_wrong_set_is_not_found()
    {
        await ImportAsync(BoundApi, BoundFunction, CollectionApi, EchoApi, GetApi);
        var id = Guid.NewGuid();
        var text = new JsonObject { ["Text"] = "x" };

        (await UnknownSegmentAsync(HttpMethod.Post, $"systemusers({id})/{Ns}xt_Bound", text)).Should().Contain("xt_Bound");
        await UnknownSegmentAsync(HttpMethod.Get, $"systemusers({id})/{Ns}xt_BoundGet(Text='x')");
        await UnknownSegmentAsync(HttpMethod.Post, $"systemusers/{Ns}xt_Many", text);
        // The binding must match the form: entity-bound, collection-bound and global are not interchangeable.
        await UnknownSegmentAsync(HttpMethod.Post, $"{TestEntities.PipeSet}/{Ns}xt_Bound", text);
        await UnknownSegmentAsync(HttpMethod.Post, $"{TestEntities.PipeSet}({id})/{Ns}xt_Many", text);
        await UnknownSegmentAsync(HttpMethod.Post, "xt_Bound", text);
        await UnknownSegmentAsync(HttpMethod.Post, $"{TestEntities.PipeSet}({id})/{Ns}xt_Echo", text);
        // A bound operation is named with its namespace.
        await UnknownSegmentAsync(HttpMethod.Get, $"{TestEntities.PipeSet}({id})/xt_BoundGet(Text='x')");
        // An unknown set, an unknown operation.
        await UnknownSegmentAsync(HttpMethod.Post, $"nothings({id})/{Ns}xt_Bound", text);
        await UnknownSegmentAsync(HttpMethod.Post, "xt_Nothing", text);
        await UnknownSegmentAsync(HttpMethod.Post, $"{TestEntities.PipeSet}({id})/{Ns}xt_Nothing", text);
        // A GET of an action and a POST of a function.
        await UnknownSegmentAsync(HttpMethod.Get, "xt_Echo(Text='x')");
        await UnknownSegmentAsync(HttpMethod.Get, "xt_Echo");
        await UnknownSegmentAsync(HttpMethod.Post, "xt_Get", new JsonObject { ["Name"] = "x" });
    }

    // ---- errors ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Web_api_validation_matches_sdk()
    {
        await ImportAsync(EchoApi, GetApi);

        // Action body: missing, wrong type, undeclared.
        (await InvalidAsync(HttpMethod.Post, "xt_Echo", new JsonObject { ["Int"] = 1 })).Should().Contain("'Text'");
        (await InvalidAsync(HttpMethod.Post, "xt_Echo", new JsonObject { ["Text"] = null })).Should().Contain("'Text'");
        (await InvalidAsync(HttpMethod.Post, "xt_Echo", new JsonObject { ["Text"] = "x", ["Int"] = "one" })).Should().Contain("'Int'");
        (await InvalidAsync(HttpMethod.Post, "xt_Echo", new JsonObject { ["Text"] = 5 })).Should().Contain("'Text'");
        (await InvalidAsync(HttpMethod.Post, "xt_Echo", new JsonObject { ["Text"] = "x", ["Pick"] = "3" })).Should().Contain("'Pick'");
        (await InvalidAsync(HttpMethod.Post, "xt_Echo", new JsonObject { ["Text"] = "x", ["Id"] = "not a guid" })).Should().Contain("'Id'");
        (await InvalidAsync(HttpMethod.Post, "xt_Echo", new JsonObject { ["Text"] = "x", ["Texts"] = new JsonArray(1) })).Should().Contain("'Texts'");
        (await InvalidAsync(HttpMethod.Post, "xt_Echo", new JsonObject { ["Text"] = "x", ["Other"] = 1 })).Should().Contain("'Other'");
        // An EntityReference of an action is a parameter of an operation, not of a function.
        foreach (var reference in new JsonNode[]
                 {
                     "not an object", new JsonObject { ["@odata.id"] = "nowhere" }, new JsonObject { ["@odata.id"] = $"pb_nosuchsets({Guid.NewGuid()})" },
                     new JsonObject { ["@odata.id"] = $"{TestEntities.PipeSet}({Guid.NewGuid()})", ["extra"] = 1 },
                 })
            (await InvalidAsync(HttpMethod.Post, "xt_Echo", new JsonObject { ["Text"] = "x", ["Ref"] = reference }))
                .Should().Contain("'Ref' of operation 'xt_Echo'").And.NotContain("function");

        // Function parameters, inline and aliased: missing, wrong type, undeclared.
        (await InvalidAsync(HttpMethod.Get, "xt_Get()")).Should().Contain("'Name'");
        (await InvalidAsync(HttpMethod.Get, "xt_Get(Count=@c)?@c=1")).Should().Contain("'Name'");
        (await InvalidAsync(HttpMethod.Get, "xt_Get(Name=@p1)?@p1=5")).Should().Contain("'Name'");
        (await InvalidAsync(HttpMethod.Get, "xt_Get(Name='a',Count=@c)?@c='seven'")).Should().Contain("'Count'");
        (await InvalidAsync(HttpMethod.Get, "xt_Get(Name=abc)")).Should().Contain("'Name'");
        (await InvalidAsync(HttpMethod.Get, "xt_Get(Name='a',Other=@o)?@o='x'")).Should().Contain("'Other'");
        (await InvalidAsync(HttpMethod.Get, "xt_Get(Name=@p1)")).Should().Contain("@p1");
        (await InvalidAsync(HttpMethod.Get, "xt_Get(Name='a',Name='b')")).Should().Contain("'Name'");
    }

    [Fact]
    public async Task Navigation_property_with_a_key_is_refused_as_a_path_segment()
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
            (await InvalidAsync(method, $"accounts({Guid.NewGuid()})/contact_customer_accounts({Guid.NewGuid()})"))
                .Should().Contain("navigation property 'contact_customer_accounts' is not supported as a path segment");
    }

    [Fact]
    public async Task Tag_travels_in_the_query_string_only()
    {
        var tagged = new TestApi("xt_Tagged", PluginTypeName: Echo, Requests: [("Text", 10, false, null)], Responses: [("tagOut", 10)]);
        var taggedGet = tagged with { UniqueName = "xt_TaggedGet", IsFunction = true };
        await ImportAsync(tagged, taggedGet);

        // ?tag= reaches the plug-ins as the request parameter tag, unquoted, on an action and on a function.
        (await OkAsync(HttpMethod.Post, "xt_Tagged?tag=from%20the%20query", new JsonObject { ["Text"] = "x" }))["tagOut"]!
            .GetValue<string>().Should().Be("from the query");
        (await OkAsync(HttpMethod.Get, "xt_TaggedGet(Text='x')?tag=it's"))["tagOut"]!.GetValue<string>().Should().Be("it's");

        // Anywhere else, or twice, it is refused rather than passed on raw.
        (await InvalidAsync(HttpMethod.Post, "xt_Tagged", new JsonObject { ["Text"] = "x", ["tag"] = "y" })).Should().Contain("?tag=");
        (await InvalidAsync(HttpMethod.Get, "xt_TaggedGet(Text='x',tag='y')")).Should().Contain("?tag=");
        (await InvalidAsync(HttpMethod.Get, "xt_TaggedGet(Text='x')?tag=a&tag=b")).Should().Contain("'tag'");
    }

    [Fact]
    public async Task Ieee754_compatible_body_takes_decimal_and_money_as_strings()
    {
        await ImportAsync(EchoApi);
        var body = new JsonObject { ["Text"] = "x", ["Cash"] = "9.99", ["Dec"] = "1.5" };

        var (status, json, text) = await SendAsync(HttpMethod.Post, "xt_Echo", body, "application/json;IEEE754Compatible=true");
        status.Should().Be(HttpStatusCode.OK, text);
        decimal.Parse(json!["CashOut"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(9.99m);
        decimal.Parse(json["DecOut"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1.5m);

        // Without the option, a string is not a Decimal or Money.
        (await InvalidAsync(HttpMethod.Post, "xt_Echo", body)).Should().Contain("'Cash'");
    }

    [Fact]
    public async Task Function_without_response_is_not_found()
    {
        await ImportAsync(new TestApi("xt_Silent", IsFunction: true, Requests: [("Text", 10, true, null)]));

        (await ErrorAsync(HttpMethod.Get, "xt_Silent()", HttpStatusCode.NotFound, "0x8006088a")).Should().Contain("xt_Silent");
        await ErrorAsync(HttpMethod.Get, $"{Ns}xt_Silent(Text='x')", HttpStatusCode.NotFound, "0x8006088a");
    }

    // ---- response shapes ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Entity_and_collection_responses_use_their_own_shape()
    {
        await ImportAsync(
            new TestApi("xt_One", PluginTypeName: Echo, Requests: [("Ent", 3, true, TestEntities.Pipe)], Responses: [("EntOut", 3)]),
            new TestApi("xt_Some", PluginTypeName: Echo, Requests: [("Ents", 4, true, null)], Responses: [("EntsOut", 4)]));
        var id = Guid.NewGuid();

        var one = await OkAsync(HttpMethod.Post, "xt_One",
            new JsonObject { ["Ent"] = new JsonObject { ["pb_pipeid"] = id.ToString(), ["pb_name"] = "Ada" } });
        one["@odata.context"]!.GetValue<string>().Should().EndWith($"$metadata#{TestEntities.PipeSet}/$entity");
        one["pb_pipeid"]!.GetValue<string>().Should().Be(id.ToString());
        one["pb_name"]!.GetValue<string>().Should().Be("Ada");
        one.AsObject().ContainsKey("EntOut").Should().BeFalse();

        // An unset Entity is no content.
        (await SendAsync(HttpMethod.Post, "xt_One")).Status.Should().Be(HttpStatusCode.NoContent);

        var some = await OkAsync(HttpMethod.Post, "xt_Some", new JsonObject
        {
            ["Ents"] = new JsonArray(
                new JsonObject { ["@odata.type"] = Ns + TestEntities.Pipe, ["pb_name"] = "One" },
                new JsonObject { ["@odata.type"] = Ns + TestEntities.Pipe, ["pb_name"] = "Two" }),
        });
        some["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Collection(Microsoft.Dynamics.CRM.crmbaseentity)");
        some["value"]!.AsArray().Select(e => e!["pb_name"]!.GetValue<string>()).Should().Equal("One", "Two");
        some["value"]![0]!["@odata.type"]!.GetValue<string>().Should().Be("#" + Ns + TestEntities.Pipe);

        // An unset EntityCollection is an empty collection.
        (await OkAsync(HttpMethod.Post, "xt_Some"))["value"]!.AsArray().Should().BeEmpty();
    }
}
