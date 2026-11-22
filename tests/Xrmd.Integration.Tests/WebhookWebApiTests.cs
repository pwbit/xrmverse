using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.2, task 5: webhooks authored and delivered end to end over the Web API and SOAP: the POSTs that create an endpoint and a
/// step, delivery of a Create to a local receiver with the configured header, the write-only <c>authvalue</c>, the navigation
/// properties and <c>$metadata</c>.
/// </summary>
[Collection("xrmd")]
public sealed class WebhookWebApiTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Soap = "XRMServices/2011/Organization.svc/web";
    private const string HeaderName = "x-test-token";
    private const string HeaderValue = "w3b-hook-abc";

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private PipelineOptions HostOptions => fx.App.Services.GetRequiredService<IOptions<PipelineOptions>>().Value;
    private readonly List<Receiver> _receivers = [];

    public async Task InitializeAsync()
    {
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync("DELETE FROM data.serviceendpoint; DELETE FROM meta.serviceendpoint_secret;");
        HostOptions.WebhookAllowAnyPort = true;
    }

    public async Task DisposeAsync()
    {
        HostOptions.WebhookAllowAnyPort = false;
        foreach (var receiver in _receivers) receiver.Dispose();
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync("DELETE FROM data.serviceendpoint; DELETE FROM meta.serviceendpoint_secret;");
    }

    // ---- receiver ----

    private sealed record Received(string Query, IReadOnlyDictionary<string, string> Headers);

    /// <summary>An <see cref="HttpListener"/> on 127.0.0.1 and a free port; answers 200 to every request.</summary>
    private sealed class Receiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public ConcurrentQueue<Received> Requests { get; } = new();
        public string Url { get; }

        public Receiver()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/hook/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                using (var reader = new StreamReader(ctx.Request.InputStream)) await reader.ReadToEndAsync();
                var headers = ctx.Request.Headers.AllKeys.Where(k => k is not null)
                    .ToDictionary(k => k!, k => ctx.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase);
                Requests.Enqueue(new Received(ctx.Request.Url!.Query, headers));
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
    }

    private Receiver NewReceiver()
    {
        var receiver = new Receiver();
        _receivers.Add(receiver);
        return receiver;
    }

    // ---- helpers ----

    private async Task<(HttpStatusCode Status, JsonNode? Body, string Text, HttpResponseMessage Response)> SendAsync(
        HttpMethod method, string path, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/data/v9.2/" + path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 || !text.TrimStart().StartsWith('{') ? null : JsonNode.Parse(text), text, response);
    }

    private async Task<Guid> PostAsync(string set, JsonObject body)
    {
        var (status, _, text, response) = await SendAsync(HttpMethod.Post, set, body);
        status.Should().Be(HttpStatusCode.NoContent, $"POST {set}: {text}");
        var url = response.Headers.GetValues("OData-EntityId").Single();
        return Guid.Parse(url[(url.LastIndexOf('(') + 1)..^1]);
    }

    private static string HeaderSettings => $"<settings><setting name=\"{HeaderName}\" value=\"{HeaderValue}\" /></settings>";

    private static JsonObject EndpointBody(string name, string url) => new()
    {
        ["name"] = name, ["contract"] = 8, ["authtype"] = 5, ["url"] = url, ["authvalue"] = HeaderSettings,
    };

    private static JsonObject StepBody(string name, Guid endpoint) => new()
    {
        ["name"] = name, ["stage"] = 40, ["mode"] = 0, ["rank"] = 1,
        ["eventhandler_serviceendpoint@odata.bind"] = $"/serviceendpoints({endpoint})",
        ["sdkmessageid@odata.bind"] = $"/sdkmessages({SdkMessageCatalog.MessageId("Create")})",
        ["sdkmessagefilterid@odata.bind"] = $"/sdkmessagefilters({SdkMessageCatalog.FilterId("Create", "account")})",
    };

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(Guid Endpoint, Guid Step)> CreateHookAsync(string name, string url)
    {
        var endpoint = await PostAsync("serviceendpoints", EndpointBody(name, url));
        return (endpoint, await PostAsync("sdkmessageprocessingsteps", StepBody(name + " step", endpoint)));
    }

    // ---- tests ----

    [Fact]
    public async Task WebApi_creates_webhook_and_step()
    {
        var receiver = NewReceiver();
        await CreateHookAsync("web api hook", receiver.Url);

        await Admin.CreateAsync(new Entity("account") { ["name"] = "Thornwick Dyers" });

        var request = receiver.Requests.Should().ContainSingle().Which;
        request.Headers[HeaderName].Should().Be(HeaderValue);
        request.Headers["x-ms-dynamics-request-name"].Should().Be("Create");
    }

    [Fact]
    public async Task WebApi_select_authvalue_is_400()
    {
        var (endpoint, _) = await CreateHookAsync("select hook", NewReceiver().Url);

        var (status, json, text, _) = await SendAsync(HttpMethod.Get, "serviceendpoints?$select=authvalue");
        status.Should().Be(HttpStatusCode.BadRequest, text);
        json!["error"]!["code"]!.GetValue<string>().Should().Be("0x80040203");
        text.Should().NotContain(HeaderValue);

        var (plainStatus, plain, plainText, _) = await SendAsync(HttpMethod.Get, $"serviceendpoints({endpoint})");
        plainStatus.Should().Be(HttpStatusCode.OK, plainText);
        plain!.AsObject().ContainsKey("authvalue").Should().BeFalse();
        plain["isauthvalueset"]!.GetValue<bool>().Should().BeTrue();
        plainText.Should().NotContain(HeaderValue);
    }

    [Fact]
    public async Task WebApi_expand_eventhandler_serviceendpoint()
    {
        var (_, step) = await CreateHookAsync("expand hook", NewReceiver().Url);

        var (status, json, text, _) = await SendAsync(HttpMethod.Get,
            $"sdkmessageprocessingsteps({step})?$select=name&$expand=eventhandler_serviceendpoint($select=name)");

        status.Should().Be(HttpStatusCode.OK, text);
        json!["eventhandler_serviceendpoint"]!["name"]!.GetValue<string>().Should().Be("expand hook");
    }

    [Fact]
    public async Task WebApi_navigation_from_endpoint()
    {
        var (endpoint, _) = await CreateHookAsync("nav hook", NewReceiver().Url);

        var (pathStatus, pathJson, pathText, _) = await SendAsync(HttpMethod.Get, $"serviceendpoints({endpoint})/serviceendpoint_sdkmessageprocessingstep");
        pathStatus.Should().Be(HttpStatusCode.BadRequest, pathText);
        pathJson!["error"]!["code"]!.GetValue<string>().Should().Be("0x80040203");

        // The one-to-many navigation property is a stated limit: it is not in the model, so $expand names it in a 400.
        var (expandStatus, expandJson, expandText, _) = await SendAsync(HttpMethod.Get,
            $"serviceendpoints({endpoint})?$expand=serviceendpoint_sdkmessageprocessingstep");
        expandStatus.Should().Be(HttpStatusCode.BadRequest, expandText);
        expandJson!["error"]!["code"]!.GetValue<string>().Should().Be("0x80040203");
        expandJson["error"]!["message"]!.GetValue<string>().Should().Contain("serviceendpoint_sdkmessageprocessingstep");
    }

    [Fact]
    public async Task Metadata_lists_serviceendpoint()
    {
        var (status, _, text, _) = await SendAsync(HttpMethod.Get, "$metadata");

        status.Should().Be(HttpStatusCode.OK, text);
        text.Should().Contain("<EntityType Name=\"serviceendpoint\"")
            .And.Contain("Name=\"eventhandler_serviceendpoint\"")
            .And.NotContain("serviceendpoint_sdkmessageprocessingstep", "collection-valued navigation is a stated limit");
    }

    [Fact]
    public async Task Soap_creates_endpoint()
    {
        var receiver = NewReceiver();
        var endpoint = new Entity("serviceendpoint")
        {
            ["name"] = "soap hook", ["contract"] = new OptionSetValue(8), ["authtype"] = new OptionSetValue(5),
            ["url"] = receiver.Url, ["authvalue"] = HeaderSettings,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, Soap)
        {
            Content = new StringContent(OrganizationSoapEndpointTests.Envelope(OrganizationSoapEndpointTests.Execute(
                new Microsoft.Xrm.Sdk.Messages.CreateRequest { Target = endpoint })), Encoding.UTF8, "text/xml"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = (Microsoft.Xrm.Sdk.Messages.CreateResponse)await OrganizationSoapEndpointTests.ExecuteResultAsync(await fx.Client.SendAsync(req));
        var id = (Guid)response.Results["id"];

        var stored = await Admin.RetrieveAsync("serviceendpoint", id, new Microsoft.Xrm.Sdk.Query.ColumnSet("name", "isauthvalueset"));
        stored.GetAttributeValue<string>("name").Should().Be("soap hook");
        stored.GetAttributeValue<bool>("isauthvalueset").Should().BeTrue();
        stored.Contains("authvalue").Should().BeFalse();

        await PostAsync("sdkmessageprocessingsteps", StepBody("soap hook step", id));
        await Admin.CreateAsync(new Entity("account") { ["name"] = "Halloway Ironworks" });
        // The step is synchronous: the receiver already has the POST when the Create returns.
        receiver.Requests.Should().ContainSingle().Which.Headers[HeaderName].Should().Be(HeaderValue);
    }
}
