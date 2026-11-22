using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Custom APIs authored over the Web API and SOAP (plan 3.1, task 4): an API created with a POST is callable in the next
/// request and listed in <c>$metadata</c>; a deleted one leaves both; the deep insert of the Learn sample and
/// <c>iscustomizable</c> are refused with a 400 that writes nothing.
/// </summary>
[Collection("solution-import")]
public sealed class CustomApiAuthoringWebApiTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Echo = "Xrmd.TestPlugins.CustomApiEchoPlugin";
    private const string Soap = "XRMServices/2011/Organization.svc/web";

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
    }

    public Task DisposeAsync() => SolutionImportTests.ClearImportAsync(fx);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<(HttpStatusCode Status, JsonNode? Body, string Text, HttpResponseMessage Response)> SendAsync(
        HttpMethod method, string path, JsonNode? body = null, string? solution = null)
    {
        var request = new HttpRequestMessage(method, "/api/data/v9.2/" + path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (solution is not null) request.Headers.Add("MSCRM.SolutionUniqueName", solution);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 || !text.TrimStart().StartsWith('{') ? null : JsonNode.Parse(text), text, response);
    }

    /// <summary>A POST that creates a record; the id of the new record.</summary>
    private async Task<Guid> PostAsync(string set, JsonObject body, string? solution = null)
    {
        var (status, _, text, response) = await SendAsync(HttpMethod.Post, set, body, solution);
        status.Should().Be(HttpStatusCode.NoContent, $"POST {set}: {text}");
        var url = response.Headers.GetValues("OData-EntityId").Single();
        return Guid.Parse(url[(url.LastIndexOf('(') + 1)..^1]);
    }

    private async Task<string> MetadataAsync()
    {
        var (status, _, text, _) = await SendAsync(HttpMethod.Get, "$metadata");
        status.Should().Be(HttpStatusCode.OK, text);
        return text;
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<Guid> PluginTypeAsync()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        return await PluginTestHelper.RegisterTypeAsync(Admin, assembly, Echo);
    }

    private static JsonObject ApiBody(string name, Guid? plugin = null, bool isFunction = false)
    {
        var body = new JsonObject
        {
            ["uniquename"] = name, ["name"] = name, ["displayname"] = name, ["bindingtype"] = 0, ["isfunction"] = isFunction,
            ["allowedcustomprocessingsteptype"] = 0,
        };
        if (plugin is { } id) body["plugintypeid@odata.bind"] = $"/plugintypes({id})";
        return body;
    }

    private async Task<Guid> CreateEchoFunctionAsync(string name)
    {
        var api = await PostAsync("customapis", ApiBody(name, await PluginTypeAsync(), isFunction: true));
        await PostAsync("customapirequestparameters", new JsonObject
        {
            ["uniquename"] = "Text", ["name"] = "Text", ["displayname"] = "Text", ["type"] = 10, ["isoptional"] = false,
            ["CustomAPIId@odata.bind"] = $"/customapis({api})",
        });
        // The echo plug-in copies each input to the output of the same name plus Out.
        await PostAsync("customapiresponseproperties", new JsonObject
        {
            ["uniquename"] = "TextOut", ["name"] = "TextOut", ["displayname"] = "TextOut", ["type"] = 10,
            ["CustomAPIId@odata.bind"] = $"/customapis({api})",
        });
        return api;
    }

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Created_api_is_callable_at_once()
    {
        await CreateEchoFunctionAsync("new_Echo");

        // A new request: no restart, no import.
        var (status, body, text, _) = await SendAsync(HttpMethod.Get, "new_Echo(Text=@t)?@t='hi'");
        status.Should().Be(HttpStatusCode.OK, text);
        body!["TextOut"]!.GetValue<string>().Should().Be("hi");

        var csdl = await MetadataAsync();
        csdl.Should().Contain("<Function Name=\"new_Echo\"").And.Contain("Name=\"new_EchoResponse\"");
    }

    [Fact]
    public async Task Deleted_api_leaves_metadata()
    {
        var api = await CreateEchoFunctionAsync("new_Echo");
        (await MetadataAsync()).Should().Contain("<Function Name=\"new_Echo\"");

        var (deleted, _, deletedText, _) = await SendAsync(HttpMethod.Delete, $"customapis({api})");
        deleted.Should().Be(HttpStatusCode.NoContent, deletedText);

        (await MetadataAsync()).Should().NotContain("new_Echo");
        var (status, body, text, _) = await SendAsync(HttpMethod.Get, "new_Echo(Text=@t)?@t='hi'");
        status.Should().Be(HttpStatusCode.NotFound, text);
        body!["error"]!["code"]!.GetValue<string>().Should().Be("0x80060888");
    }

    [Fact]
    public async Task Web_api_deep_insert_is_refused()
    {
        var body = ApiBody("new_Deep");
        body["CustomAPIRequestParameters"] = new JsonArray(new JsonObject
        {
            ["uniquename"] = "Text", ["name"] = "Text", ["displayname"] = "Text", ["type"] = 10, ["isoptional"] = false,
        });

        var (status, json, text, _) = await SendAsync(HttpMethod.Post, "customapis", body);

        status.Should().Be(HttpStatusCode.BadRequest, text);
        json!["error"]!["code"]!.GetValue<string>().Should().Be("0x80040203");
        (await CountAsync("SELECT COUNT(*) FROM data.customapi")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.customapirequestparameter")).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessage WHERE name = N'new_Deep'")).Should().Be(0);
    }

    [Fact]
    public async Task Web_api_iscustomizable_is_refused()
    {
        var body = ApiBody("new_Custom");
        body["iscustomizable"] = new JsonObject { ["Value"] = false };

        var (status, json, text, _) = await SendAsync(HttpMethod.Post, "customapis", body);

        status.Should().Be(HttpStatusCode.BadRequest, text);
        json!["error"]!["code"]!.GetValue<string>().Should().Be("0x80040203");
        (await CountAsync("SELECT COUNT(*) FROM data.customapi")).Should().Be(0);
    }

    [Fact]
    public async Task Soap_create_and_execute()
    {
        var plugin = await PluginTypeAsync();
        var api = new Entity("customapi")
        {
            ["uniquename"] = "new_SoapEcho", ["name"] = "new_SoapEcho", ["displayname"] = "new_SoapEcho",
            ["bindingtype"] = new OptionSetValue(0), ["isfunction"] = false, ["allowedcustomprocessingsteptype"] = new OptionSetValue(0),
            ["plugintypeid"] = new EntityReference("plugintype", plugin),
        };
        var apiId = await SoapAsync<Microsoft.Xrm.Sdk.Messages.CreateResponse>(new Microsoft.Xrm.Sdk.Messages.CreateRequest { Target = api });
        await SoapAsync<Microsoft.Xrm.Sdk.Messages.CreateResponse>(new Microsoft.Xrm.Sdk.Messages.CreateRequest
        {
            Target = new Entity("customapirequestparameter")
            {
                ["uniquename"] = "Text", ["name"] = "Text", ["displayname"] = "Text", ["type"] = new OptionSetValue(10), ["isoptional"] = false,
                ["customapiid"] = new EntityReference("customapi", apiId.id),
            },
        });
        await SoapAsync<Microsoft.Xrm.Sdk.Messages.CreateResponse>(new Microsoft.Xrm.Sdk.Messages.CreateRequest
        {
            Target = new Entity("customapiresponseproperty")
            {
                ["uniquename"] = "TextOut", ["name"] = "TextOut", ["displayname"] = "TextOut", ["type"] = new OptionSetValue(10),
                ["customapiid"] = new EntityReference("customapi", apiId.id),
            },
        });

        var response = await OrganizationSoapEndpointTests.ExecuteResultAsync(await PostSoapAsync(
            new OrganizationRequest("new_SoapEcho") { ["Text"] = "hi" }));

        response.ResponseName.Should().Be("new_SoapEcho");
        response.Results["TextOut"].Should().Be("hi");
    }

    private async Task<HttpResponseMessage> PostSoapAsync(OrganizationRequest request)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Soap)
        {
            Content = new StringContent(OrganizationSoapEndpointTests.Envelope(OrganizationSoapEndpointTests.Execute(request)), Encoding.UTF8, "text/xml"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    private async Task<(Guid id, T Response)> SoapAsync<T>(OrganizationRequest request) where T : OrganizationResponse
    {
        var response = (T)await OrganizationSoapEndpointTests.ExecuteResultAsync(await PostSoapAsync(request));
        return (response.Results.TryGetValue("id", out var id) ? (Guid)id : Guid.Empty, response);
    }
}
