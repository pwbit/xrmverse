using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Abstractions.Sdk;
using Xrmd.TestSupport;

namespace Xrmd.Integration.Tests.SdkFree;

/// <summary>Plan S.2a, Task 4: an SDK-free server refuses SOAP, SDK plug-in registration and migration from Dataverse; a standard server is unchanged.</summary>
[Collection("xrmd")]
public sealed class SdkFreeRefusalTests(XrmdFixture fx)
{
    private const string SoapPath = "XRMServices/2011/Organization.svc/web";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Contracts = "http://schemas.microsoft.com/xrm/2011/Contracts";
    private const string FeatureNotAvailableHex = "0x80090900";

    [Fact]
    public async Task Soap_is_refused_with_a_soap_fault()
    {
        await using var app = NewSdkFreeApp();
        using var client = app.CreateClient();

        var res = await client.SendAsync(SoapRequest(client.BaseAddress!, authenticated: true));

        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.NotImplemented, text);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/xml");
        res.Content.Headers.ContentType.CharSet.Should().Be("utf-8");
        var fault = XDocument.Parse(text).Root!.Element(Soap + "Body")!.Element(Soap + "Fault")!;
        ((string)fault.Element("faultcode")!).Should().Be("s:Client");
        var faultString = fault.Element("faultstring")!;
        ((string)faultString).Should().Be(SdkFreeMessages.Soap);
        ((string)faultString.Attribute(XNamespace.Xml + "lang")!).Should().Be("en-US");
        var detail = fault.Element("detail")!.Element(Contracts + "OrganizationServiceFault")!;
        ((string)detail.Element(Contracts + "ErrorCode")!).Should().Be("-2146891520");
        ((string)detail.Element(Contracts + "Message")!).Should().Be(SdkFreeMessages.Soap);
        // The SDK's own data contract reads the hand-written detail (what ServiceClient raises as FaultException<OrganizationServiceFault>).
        using var reader = detail.CreateReader();
        var parsed = (OrganizationServiceFault)new DataContractSerializer(typeof(OrganizationServiceFault)).ReadObject(reader)!;
        parsed.ErrorCode.Should().Be(unchecked((int)0x80090900));
        parsed.Message.Should().Be(SdkFreeMessages.Soap);
    }

    [Fact]
    public async Task Soap_without_token_is_still_401()
    {
        await using var app = NewSdkFreeApp();
        using var client = app.CreateClient();

        var res = await client.SendAsync(SoapRequest(client.BaseAddress!, authenticated: false));

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Plugin_assembly_create_is_refused_and_writes_nothing()
    {
        await using var app = NewSdkFreeApp();
        using var client = app.CreateClient();
        var name = "SdkFree" + Guid.NewGuid().ToString("N")[..8];

        var res = await SendAsync(client, HttpMethod.Post, "api/data/v9.2/pluginassemblies", new JsonObject
        {
            ["name"] = name,
            ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes),
            ["sourcetype"] = 0,
            ["isolationmode"] = 2,
        });

        await ShouldBeRefusedAsync(res, SdkFreeMessages.PluginAssembly);
        var list = await SendAsync(client, HttpMethod.Get, $"api/data/v9.2/pluginassemblies?$select=name&$filter=name eq '{name}'");
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        JsonNode.Parse(await list.Content.ReadAsStringAsync())!["value"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Plugin_type_create_is_refused()
    {
        await using var app = NewSdkFreeApp();
        using var client = app.CreateClient();
        // An assembly registered while the server was standard: written by the core operation, past the registration rules.
        var assemblyId = Guid.NewGuid();
        var caller = await TestServices.CallerAsync(app.Services, fx.AdminObjectId);
        await using (var conn = await app.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync())
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CoreOperations>().CreateAsync(new DataSession(conn, null), caller,
                new Entity("pluginassembly", assemblyId)
                {
                    ["name"] = "Xrmd.TestPlugins",
                    ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes),
                    ["sourcetype"] = new OptionSetValue(0),
                    ["isolationmode"] = new OptionSetValue(2),
                }, default);
        }

        var res = await SendAsync(client, HttpMethod.Post, "api/data/v9.2/plugintypes", new JsonObject
        {
            ["typename"] = "Xrmd.TestPlugins.SetAttributePlugin",
            ["pluginassemblyid@odata.bind"] = $"/pluginassemblies({assemblyId})",
        });

        await ShouldBeRefusedAsync(res, SdkFreeMessages.PluginAssembly);
        var list = await SendAsync(client, HttpMethod.Get, $"api/data/v9.2/plugintypes?$select=typename&$filter=_pluginassemblyid_value eq {assemblyId}");
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        JsonNode.Parse(await list.Content.ReadAsStringAsync())!["value"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Migration_package_and_run_are_refused_and_write_nothing()
    {
        await using var app = NewSdkFreeApp();
        using var client = app.CreateClient();

        await ShouldBeRefusedAsync(await SendAsync(client, HttpMethod.Post, "admin/migrate/packages"), SdkFreeMessages.Migration);
        await ShouldBeRefusedAsync(await SendAsync(client, HttpMethod.Post, "admin/migrate/runs", new JsonObject
        {
            ["packageId"] = Guid.NewGuid().ToString(),
            ["passphrase"] = "a long enough passphrase",
        }), SdkFreeMessages.Migration);

        var runs = await SendAsync(client, HttpMethod.Get, "admin/migrate/runs");
        var runsText = await runs.Content.ReadAsStringAsync();
        runs.StatusCode.Should().Be(HttpStatusCode.OK, runsText);
        JsonNode.Parse(runsText)!["value"]!.AsArray().Should().BeEmpty();
        await using var conn = await app.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM meta.migration_package";
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Solution_import_carrying_a_plugin_assembly_is_refused_and_writes_nothing()
    {
        await using var app = NewSdkFreeApp();
        using var client = app.CreateClient();
        var spec = ManagedTestSolution.V1;
        spec.Steps.Should().NotBeNullOrEmpty("the built solution must carry the plug-in assembly");

        var res = await SendAsync(client, HttpMethod.Post, "api/data/v9.2/ImportSolution", new JsonObject
        {
            ["CustomizationFile"] = Convert.ToBase64String(ManagedTestSolution.Build(spec)),
            ["ImportJobId"] = Guid.NewGuid().ToString(),
        });

        await ShouldBeRefusedAsync(res, SdkFreeMessages.PluginAssembly);
        var assemblies = await SendAsync(client, HttpMethod.Get, "api/data/v9.2/pluginassemblies?$select=name");
        assemblies.StatusCode.Should().Be(HttpStatusCode.OK, await assemblies.Content.ReadAsStringAsync());
        JsonNode.Parse(await assemblies.Content.ReadAsStringAsync())!["value"]!.AsArray().Should().BeEmpty();
        var solutions = await SendAsync(client, HttpMethod.Get, $"api/data/v9.2/solutions?$select=uniquename&$filter=uniquename eq '{spec.Name}'");
        solutions.StatusCode.Should().Be(HttpStatusCode.OK, await solutions.Content.ReadAsStringAsync());
        JsonNode.Parse(await solutions.Content.ReadAsStringAsync())!["value"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Standard_server_is_unchanged()
    {
        var soap = await fx.Client.SendAsync(SoapRequest(fx.Client.BaseAddress!, authenticated: true));
        soap.StatusCode.Should().Be(HttpStatusCode.OK, await soap.Content.ReadAsStringAsync());

        await PluginTestHelper.ClearAsync(fx);
        try
        {
            var res = await SendAsync(fx.Client, HttpMethod.Post, "api/data/v9.2/pluginassemblies", new JsonObject
            {
                ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes),
                ["sourcetype"] = 0,
                ["isolationmode"] = 2,
            });
            res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
    }

    private WebApplicationFactory<Program> NewSdkFreeApp() => fx.WithOwnDatabase(b => b.UseSetting("Sdk:Mode", "none"));

    private static async Task ShouldBeRefusedAsync(HttpResponseMessage res, string message)
    {
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        var error = JsonNode.Parse(text)!["error"]!;
        error["code"]!.GetValue<string>().Should().Be(FeatureNotAvailableHex);
        error["message"]!.GetValue<string>().Should().Be(message);
    }

    private HttpRequestMessage SoapRequest(Uri baseUrl, bool authenticated)
    {
        var body = OrganizationSoapEndpointTests.Envelope(OrganizationSoapEndpointTests.Execute(new WhoAmIRequest()));
        var req = new HttpRequestMessage(HttpMethod.Post, SoapPath) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
        if (authenticated)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, baseUrl.ToString()));
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, JsonObject? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, client.BaseAddress!.ToString()));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await client.SendAsync(req);
    }
}
