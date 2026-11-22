using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Mcp.Tools;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Listing custom APIs (plan 15, task 7), against an imported solution: the Web API query of <c>xrmd customapi list|show</c>
/// with the navigation property names the server really serves, and the MCP tool <c>list_custom_apis</c>.
/// </summary>
[Collection("solution-import")]
public sealed class CustomApiListingTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Solution = "XtCustomApiListing";
    private const string Echo = "Xrmd.TestPlugins.EchoActionPlugin";

    /// <summary>The three queries <c>xrmd customapi list</c> sends (see <c>CustomApiCommands</c>).</summary>
    private const string ApiQuery =
        "customapis?$select=customapiid,uniquename,name,displayname,description,bindingtype,boundentitylogicalname,isfunction,isprivate" +
        "&$expand=plugintypeid($select=typename)&$orderby=uniquename";
    private const string ParameterQuery =
        "customapirequestparameters?$select=uniquename,name,type,isoptional,logicalentityname,_customapiid_value&$orderby=uniquename";
    private const string PropertyQuery =
        "customapiresponseproperties?$select=uniquename,name,type,logicalentityname,_customapiid_value&$orderby=uniquename";

    private static readonly TestApi EchoApi = new("xt_Echo", AllowedStepType: 2, PluginTypeName: Echo,
        Requests: [("Text", 10, false, null), ("Count", 7, true, null)], Responses: [("Echoed", 10)]);

    private static readonly TestApi PlainApi = new("xt_Plain", IsFunction: true, IsPrivate: true);

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
        // Plan 2.7a, Task 6: defined through the SDK (solution import is 2.7b on PostgreSQL).
        await CustomApiTestSolution.CreateAsync(ServiceFor(fx, fx.AdminObjectId), EchoApi, PlainApi);
    }

    public Task DisposeAsync() => SolutionImportTests.ClearImportAsync(fx);

    private async Task<JsonArray> GetAsync(string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/data/v9.2/" + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"{query}: {text}");
        return JsonNode.Parse(text)!["value"]!.AsArray();
    }

    [Fact]
    public async Task The_cli_queries_are_served_with_the_plugin_type_and_the_fields_joined_by_api()
    {
        var apis = await GetAsync(ApiQuery);
        apis.Select(r => r!["uniquename"]!.GetValue<string>()).Should().Equal("xt_Echo", "xt_Plain");
        apis[0]!["plugintypeid"]!["typename"]!.GetValue<string>().Should().Be(Echo);
        apis[1]!["plugintypeid"].Should().BeNull();
        apis[1]!["isfunction"]!.GetValue<bool>().Should().BeTrue();
        var id = apis[0]!["customapiid"]!.GetValue<Guid>();

        var parameters = await GetAsync(ParameterQuery);
        parameters.Select(p => (p!["uniquename"]!.GetValue<string>(), p["type"]!.GetValue<int>(), p["isoptional"]!.GetValue<bool>(), p["_customapiid_value"]!.GetValue<Guid>()))
            .Should().BeEquivalentTo([("Count", 7, true, id), ("Text", 10, false, id)]);
        (await GetAsync(PropertyQuery)).Select(p => (p!["uniquename"]!.GetValue<string>(), p["_customapiid_value"]!.GetValue<Guid>()))
            .Should().BeEquivalentTo([("Echoed", id)]);

        // The filters of `customapi show`.
        (await GetAsync(ApiQuery + "&$filter=uniquename eq 'xt_Echo'")).Should().ContainSingle();
        (await GetAsync(ParameterQuery + $"&$filter=_customapiid_value eq {id}")).Should().HaveCount(2);
    }

    private async Task<JsonArray> ListAsync(Dictionary<string, object?> args)
    {
        var http = fx.App.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(fx.BaseUrl + "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport);
        var result = await client.CallToolAsync("list_custom_apis", args);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        result.IsError.Should().NotBe(true, text);
        return JsonNode.Parse(text)!["customApis"]!.AsArray();
    }

    [Fact]
    public async Task Mcp_list_custom_apis_returns_parameters_with_type_names_and_filters_by_name()
    {
        var all = await ListAsync([]);
        all.Select(a => a!["uniqueName"]!.GetValue<string>()).Should().Equal("xt_Echo", "xt_Plain");
        var echo = all[0]!;
        echo["binding"]!.GetValue<string>().Should().Be("global");
        echo["kind"]!.GetValue<string>().Should().Be("action");
        echo["private"]!.GetValue<bool>().Should().BeFalse();
        echo["pluginType"]!.GetValue<string>().Should().Be(Echo);
        echo["parameters"]!.AsArray().Select(p => (p!["uniqueName"]!.GetValue<string>(), p["type"]!.GetValue<string>(), p["optional"]!.GetValue<bool>()))
            .Should().BeEquivalentTo([("Text", "String", false), ("Count", "Integer", true)]);
        echo["properties"]!.AsArray().Select(p => (p!["uniqueName"]!.GetValue<string>(), p["type"]!.GetValue<string>()))
            .Should().BeEquivalentTo([("Echoed", "String")]);
        all[1]!["kind"]!.GetValue<string>().Should().Be("function");
        all[1]!["pluginType"].Should().BeNull();

        var one = await ListAsync(new() { ["name"] = "xt_Plain" });
        one.Select(a => a!["uniqueName"]!.GetValue<string>()).Should().Equal("xt_Plain");
        (await ListAsync(new() { ["name"] = "xt_Missing" })).Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp_list_custom_apis_reads_every_page_of_each_query()
    {
        var before = MetadataTools.PageSize;
        MetadataTools.PageSize = 1; // two APIs, three parameters and one property now span several pages
        try
        {
            var all = await ListAsync([]);
            all.Select(a => a!["uniqueName"]!.GetValue<string>()).Should().Equal("xt_Echo", "xt_Plain");
            all[0]!["parameters"]!.AsArray().Select(p => p!["uniqueName"]!.GetValue<string>()).Should().Equal("Count", "Text");
            all[0]!["properties"]!.AsArray().Should().ContainSingle();
            all[0]!["pluginType"]!.GetValue<string>().Should().Be(Echo);
        }
        finally
        {
            MetadataTools.PageSize = before;
        }
    }

    [Fact]
    public async Task Mcp_list_custom_apis_is_refused_for_a_caller_who_cannot_read_plugin_types()
    {
        // The tool reads as the caller: a Basic User reads custom APIs but not plug-in types, and gets the server's refusal
        // (naming the privilege) rather than APIs with a silently missing plug-in type.
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await AssignRoleAsync(fx, user, StandardRoles.BasicUser);
        var http = fx.App.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(fx.BaseUrl + "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport);
        var result = await client.CallToolAsync("list_custom_apis", new Dictionary<string, object?>());
        result.IsError.Should().BeTrue();
        string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text)).Should().Contain("prvReadplugintype");
    }
}
