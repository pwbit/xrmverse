using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>The data a Web API or SOAP record team test needs: a template and a record of <c>account</c> (flag on), and a reader.</summary>
internal sealed class RecordTeamSetup(XrmdFixture fx) : IAsyncDisposable
{
    private readonly List<Guid> _templates = [];
    public OrganizationService Admin { get; } = ServiceFor(fx, fx.AdminObjectId);
    public EntityReference Record { get; private set; } = null!;
    public Guid Template { get; private set; }
    public Guid User { get; private set; }
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private int AccountCode => Store.GetEntity("account")!.ObjectTypeCode!.Value;

    public static async Task<RecordTeamSetup> CreateAsync(XrmdFixture fx)
    {
        var s = new RecordTeamSetup(fx);
        if (s.Store.GetEntity("account")!.AutoCreateAccessTeams != true)
            await s.Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = "account", AutoCreateAccessTeams = true } });
        var tag = "RTA" + Guid.NewGuid().ToString("N")[..10];
        s.Template = await s.Admin.CreateAsync(new Entity("teamtemplate")
        {
            ["teamtemplatename"] = tag, ["objecttypecode"] = s.AccountCode, ["defaultaccessrightsmask"] = (int)AccessRights.ReadAccess,
        });
        s._templates.Add(s.Template);
        s.Record = new EntityReference("account", await s.Admin.CreateAsync(new Entity("account") { ["name"] = tag }));
        var root = await RootBusinessUnitAsync(fx);
        s.User = await CreateUserAsync(fx, root);
        await GrantAsync(fx, s.User, team: false, ("prvReadAccount", DepthMask.Basic));
        return s;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var template in _templates)
            await Admin.DeleteAsync("teamtemplate", template);
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT COUNT(*) FROM data.teamtemplate WHERE objecttypecode = @otc");
        cmd.Parameters.AddWithValue("@otc", AccountCode);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
            await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = "account", AutoCreateAccessTeams = false } });
    }
}

/// <summary>Plan 2.16, task 4: the Web API actions <c>AddUserToRecordTeam</c> and <c>RemoveUserFromRecordTeam</c> bound to systemuser (A10).</summary>
[Collection("xrmd")]
public sealed class RecordTeamActionTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private const string Bound = "Microsoft.Dynamics.CRM.";

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonNode? body = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private static JsonObject Body(RecordTeamSetup s) => new()
    {
        ["Record"] = new JsonObject { ["@odata.type"] = Bound + "account", ["accountid"] = s.Record.Id.ToString() },
        ["TeamTemplate"] = new JsonObject { ["teamtemplateid"] = s.Template.ToString() },
    };

    [Fact]
    public async Task WebApi_add_and_remove_return_AccessTeamId()
    {
        await using var s = await RecordTeamSetup.CreateAsync(fx);
        var add = await SendAsync(HttpMethod.Post, $"systemusers({s.User})/{Bound}AddUserToRecordTeam", Body(s));
        var addText = await add.Content.ReadAsStringAsync();
        add.StatusCode.Should().Be(HttpStatusCode.OK, addText);
        var added = JsonNode.Parse(addText)!.AsObject();
        added["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.AddUserToRecordTeamResponse");
        var team = Guid.Parse(added["AccessTeamId"]!.GetValue<string>());
        team.Should().NotBeEmpty();

        var remove = await SendAsync(HttpMethod.Post, $"systemusers({s.User})/{Bound}RemoveUserFromRecordTeam", Body(s));
        var removeText = await remove.Content.ReadAsStringAsync();
        remove.StatusCode.Should().Be(HttpStatusCode.OK, removeText);
        var removed = JsonNode.Parse(removeText)!.AsObject();
        removed["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.RemoveUserFromRecordTeamResponse");
        Guid.Parse(removed["AccessTeamId"]!.GetValue<string>()).Should().Be(team);
    }

    [Fact]
    public async Task WebApi_add_grants_and_remove_revokes_access_of_the_url_user()
    {
        await using var s = await RecordTeamSetup.CreateAsync(fx);
        var oid = (Guid)(await s.Admin.RetrieveAsync("systemuser", s.User, new Microsoft.Xrm.Sdk.Query.ColumnSet("azureactivedirectoryobjectid")))["azureactivedirectoryobjectid"];
        var user = ServiceFor(fx, oid);
        async Task<bool> CanReadAsync()
        {
            try { await user.RetrieveAsync("account", s.Record.Id, new Microsoft.Xrm.Sdk.Query.ColumnSet("name")); return true; }
            catch (Xrmd.Core.Errors.XrmdFaultException) { return false; }
        }
        (await CanReadAsync()).Should().BeFalse();
        // extra properties on Record and TeamTemplate are ignored
        var body = Body(s);
        body["Record"]!["name"] = "ignored";
        body["TeamTemplate"]!["teamtemplatename"] = "ignored";
        (await SendAsync(HttpMethod.Post, $"systemusers({s.User})/{Bound}AddUserToRecordTeam", body)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CanReadAsync()).Should().BeTrue();
        (await SendAsync(HttpMethod.Post, $"systemusers({s.User})/{Bound}RemoveUserFromRecordTeam", Body(s))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CanReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task WebApi_record_without_odata_type_is_400()
    {
        await using var s = await RecordTeamSetup.CreateAsync(fx);
        var untyped = Body(s);
        untyped["Record"]!.AsObject().Remove("@odata.type");
        (await SendAsync(HttpMethod.Post, $"systemusers({s.User})/{Bound}AddUserToRecordTeam", untyped)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        var missing = Body(s);
        missing.Remove("Record");
        (await SendAsync(HttpMethod.Post, $"systemusers({s.User})/{Bound}AddUserToRecordTeam", missing)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        var noId = Body(s);
        noId["Record"]!.AsObject().Remove("accountid");
        (await SendAsync(HttpMethod.Post, $"systemusers({s.User})/{Bound}RemoveUserFromRecordTeam", noId)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WebApi_missing_template_is_404()
    {
        await using var s = await RecordTeamSetup.CreateAsync(fx);
        var body = Body(s);
        body["TeamTemplate"]!["teamtemplateid"] = Guid.NewGuid().ToString();
        (await SendAsync(HttpMethod.Post, $"systemusers({s.User})/{Bound}AddUserToRecordTeam", body)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Metadata_declares_bound_actions_and_response_types()
    {
        var res = await SendAsync(HttpMethod.Get, "$metadata");
        var csdl = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var name in new[] { "AddUserToRecordTeam", "RemoveUserFromRecordTeam" })
        {
            var start = csdl.IndexOf($"<Action Name=\"{name}\"", StringComparison.Ordinal);
            start.Should().BeGreaterThan(-1, name);
            var action = csdl[start..csdl.IndexOf("</Action>", start, StringComparison.Ordinal)];
            action.Should().Contain("IsBound=\"true\"")
                .And.Contain("<Parameter Name=\"entity\" Type=\"Microsoft.Dynamics.CRM.systemuser\"")
                .And.Contain("<Parameter Name=\"Record\" Type=\"Microsoft.Dynamics.CRM.crmbaseentity\"")
                .And.Contain("<Parameter Name=\"TeamTemplate\" Type=\"Microsoft.Dynamics.CRM.teamtemplate\"")
                .And.Contain($"<ReturnType Type=\"Microsoft.Dynamics.CRM.{name}Response\"");
            csdl.Should().Contain($"<ComplexType Name=\"{name}Response\"");
        }
        var type = csdl[csdl.IndexOf("<ComplexType Name=\"AddUserToRecordTeamResponse\"", StringComparison.Ordinal)..];
        type[..type.IndexOf("</ComplexType>", StringComparison.Ordinal)].Should().Contain("Name=\"AccessTeamId\"").And.Contain("Edm.Guid");
    }
}
