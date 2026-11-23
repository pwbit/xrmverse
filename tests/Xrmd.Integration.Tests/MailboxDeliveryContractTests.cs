using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Webhooks;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1c, task 7 ("Contract for the contact center"): <see cref="MailSchema.MailboxDeliveryVariable"/> cannot come from an API
/// caller (no request parameter, query option or SOAP element maps to a shared variable), and a webhook body carries it, a trimmed
/// one included.
/// </summary>
[Collection("xrmd")]
public sealed class MailboxDeliveryContractTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Key = MailSchema.MailboxDeliveryVariable;

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{TestEntities.PipeLog}; DELETE FROM data.{TestEntities.Pipe};");
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{TestEntities.PipeLog}; DELETE FROM data.{TestEntities.Pipe};");
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> LogAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_name FROM data.{TestEntities.PipeLog}");
        var names = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) names.Add(rd.GetString(0));
        return names;
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path, HttpContent content)
    {
        var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return req;
    }

    [Fact]
    public async Task ApiCaller_CannotSetSharedVariable()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.SharedVariablePlugin");
        foreach (var stage in new[] { 10, 20, 40 })
            await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", TestEntities.Pipe, stage, config: $"record|{TestEntities.PipeLog}|{Key}");
        var forged = Guid.NewGuid().ToString("D");

        // SDK: tag (a shared variable in Dataverse) and a parameter named after the variable.
        var sdk = new CreateRequest { Target = new Entity(TestEntities.Pipe) { ["pb_name"] = "sdk" } };
        sdk.Parameters["tag"] = forged;
        sdk.Parameters[Key] = forged;
        await Admin.ExecuteAsync(sdk);

        // Web API: ?tag= and a header named after the variable.
        using (var req = Authorized(HttpMethod.Post, $"/api/data/v9.2/{TestEntities.PipeSet}?tag={forged}",
                   new StringContent("""{"pb_name":"webapi"}""", Encoding.UTF8, "application/json")))
        {
            req.Headers.Add(Key, forged);
            req.Headers.Add("MSCRM." + Key, forged);
            (await fx.Client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // SOAP: the same request as the SDK client serializes it.
        var soap = new CreateRequest { Target = new Entity(TestEntities.Pipe) { ["pb_name"] = "soap" } };
        soap.Parameters["tag"] = forged;
        soap.Parameters[Key] = forged;
        using (var req = Authorized(HttpMethod.Post, "XRMServices/2011/Organization.svc/web",
                   new StringContent(OrganizationSoapEndpointTests.Envelope(OrganizationSoapEndpointTests.Execute(soap)), Encoding.UTF8, "text/xml")))
        {
            var res = await fx.Client.SendAsync(req);
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        }

        // Web API $batch: a change set whose POST has ?tag= and a header named after the variable.
        const string batch = "batch_delivery", changeSet = "changeset_delivery";
        var part = "Content-Type: application/http\r\nContent-Transfer-Encoding: binary\r\nContent-ID: 1\r\n\r\n" +
                   $"POST /api/data/v9.2/{TestEntities.PipeSet}?tag={forged} HTTP/1.1\r\nContent-Type: application/json; type=entry\r\n" +
                   $"{Key}: {forged}\r\n\r\n" + """{"pb_name":"batch"}""" + "\r\n";
        var body = $"--{batch}\r\nContent-Type: multipart/mixed; boundary={changeSet}\r\n\r\n--{changeSet}\r\n{part}--{changeSet}--\r\n--{batch}--\r\n";
        using (var req = Authorized(HttpMethod.Post, "/api/data/v9.2/$batch", new StringContent(body, Encoding.UTF8)))
        {
            req.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed; boundary=\"{batch}\"");
            var res = await fx.Client.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(HttpStatusCode.OK, text);
            text.Should().Contain("HTTP/1.1 204", text);
        }

        var log = await LogAsync();
        log.Should().HaveCount(12, "four Creates, three stages each");
        log.Should().OnlyContain(l => l.EndsWith("|<none>", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WebhookBody_CarriesTheVariable_EvenWhenTrimmed(bool trimmed)
    {
        var mailbox = Guid.NewGuid().ToString("D");
        var context = new XrmdPluginExecutionContext
        {
            MessageName = "AddToQueue", PrimaryEntityName = "email", PrimaryEntityId = Guid.NewGuid(), Stage = 20, Depth = 1,
            OrganizationName = "org", OperationId = Guid.NewGuid(),
        };
        context.SharedVariables[Key] = mailbox;
        context.InputParameters["Target"] = new EntityReference("email", context.PrimaryEntityId);
        if (trimmed) context.InputParameters["Padding"] = new string('x', WebhookPayload.SizeLimit);

        var (body, wasTrimmed) = WebhookPayload.Build(AsyncContextSerializer.ToRemote(context));

        wasTrimmed.Should().Be(trimmed);
        var shared = JsonDocument.Parse(body).RootElement.GetProperty("SharedVariables");
        shared.ValueKind.Should().Be(JsonValueKind.Array, "SharedVariables is the RemoteExecutionContext's array of key/value pairs");
        shared.EnumerateArray().Should().ContainSingle(p => p.GetProperty("key").GetString() == Key)
            .Which.GetProperty("value").GetString().Should().Be(mailbox);
    }
}
