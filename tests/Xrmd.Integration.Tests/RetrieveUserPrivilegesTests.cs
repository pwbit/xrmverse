using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Xrmd.Core.Errors;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>RetrieveUserPrivileges (plan 3.5 UI-1a task 3): the SDK message over SOAP and the function bound to systemuser.</summary>
[Collection("xrmd")]
public sealed class RetrieveUserPrivilegesTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private const string Function = "/Microsoft.Dynamics.CRM.RetrieveUserPrivileges()";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Services = "http://schemas.microsoft.com/xrm/2011/Contracts/Services";

    private async Task<(Guid Oid, Guid User)> UserWithAsync(params (string, DepthMask)[] own)
    {
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        if (own.Length > 0) await GrantAsync(fx, user, team: false, own);
        return (oid, user);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, Guid objectId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    private static string DepthOf(JsonNode body, string privilege) =>
        body["RolePrivileges"]!.AsArray().Single(p => p!["PrivilegeName"]!.GetValue<string>() == privilege)!["Depth"]!.GetValue<string>();

    [Fact]
    public async Task Web_api_returns_own_depths_and_team_privileges_as_basic()
    {
        var (oid, user) = await UserWithAsync(("prvReadAccount", DepthMask.Deep), ("prvWriteAccount", DepthMask.Local), ("prvReadSystemUser", DepthMask.Global));
        var team = await CreateTeamAsync(fx, await RootBusinessUnitAsync(fx), 0, user);
        await GrantAsync(fx, team, team: true, ("prvDeleteAccount", DepthMask.Global), ("prvWriteAccount", DepthMask.Global));

        var res = await GetAsync($"systemusers({user}){Function}", oid);

        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!;
        body["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.RetrieveUserPrivilegesResponse");
        DepthOf(body, "prvReadAccount").Should().Be("Deep");
        DepthOf(body, "prvWriteAccount").Should().Be("Local"); // own depth kept, the team's Global does not widen it
        DepthOf(body, "prvDeleteAccount").Should().Be("Basic"); // team role only
        var item = body["RolePrivileges"]!.AsArray().Single(p => p!["PrivilegeName"]!.GetValue<string>() == "prvReadAccount")!;
        item["PrivilegeId"]!.GetValue<Guid>().Should().Be(PrivilegeNames.IdOf("prvReadAccount"));
        item["BusinessUnitId"]!.GetValue<Guid>().Should().Be(await RootBusinessUnitAsync(fx));
    }

    [Fact]
    public async Task System_administrator_has_every_privilege_at_global()
    {
        var res = await GetAsync($"systemusers({await AdminUserIdAsync()}){Function}", fx.AdminObjectId);

        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        DepthOf(JsonNode.Parse(text)!, "prvReadAccount").Should().Be("Global");
    }

    private async Task<Guid> AdminUserIdAsync()
    {
        var res = await GetAsync("WhoAmI", fx.AdminObjectId);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!["UserId"]!.GetValue<Guid>();
    }

    [Fact]
    public async Task Unknown_user_is_404()
    {
        var res = await GetAsync($"systemusers({Guid.NewGuid()}){Function}", fx.AdminObjectId);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Caller_without_read_on_user_is_403()
    {
        var (oid, _) = await UserWithAsync(); // no role: no read on systemuser
        var (_, other) = await UserWithAsync(("prvReadAccount", DepthMask.Basic));

        var res = await GetAsync($"systemusers({other}){Function}", oid);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RetrieveUserPrivileges_for_a_user_in_another_business_unit_is_403()
    {
        // Read on systemuser at Local depth reaches the users of the caller's own business unit only (#110).
        var (oid, _) = await UserWithAsync(("prvReadSystemUser", DepthMask.Local));
        var (_, colleague) = await UserWithAsync();
        var unit = await CreateBusinessUnitAsync(fx, await RootBusinessUnitAsync(fx), "Privileges " + Guid.NewGuid().ToString("N")[..8]);
        var stranger = await CreateUserAsync(fx, unit, Guid.NewGuid());

        (await GetAsync($"systemusers({colleague}){Function}", oid)).StatusCode.Should().Be(HttpStatusCode.OK);
        var res = await GetAsync($"systemusers({stranger}){Function}", oid);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Another_entity_set_is_404()
    {
        var res = await GetAsync($"accounts({Guid.NewGuid()}){Function}", fx.AdminObjectId);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sdk_request_round_trips()
    {
        var (oid, user) = await UserWithAsync(("prvReadAccount", DepthMask.Deep), ("prvReadSystemUser", DepthMask.Global));

        var response = (RetrieveUserPrivilegesResponse)await ServiceFor(fx, oid)
            .ExecuteAsync(new RetrieveUserPrivilegesRequest { UserId = user });

        response.RolePrivileges.Single(p => p.PrivilegeName == "prvReadAccount").Depth.Should().Be(PrivilegeDepth.Deep);

        var missing = async () => await ServiceFor(fx, oid).ExecuteAsync(new RetrieveUserPrivilegesRequest { UserId = Guid.NewGuid() });
        (await missing.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task Soap_request_returns_a_RetrieveUserPrivilegesResponse()
    {
        var (_, user) = await UserWithAsync(("prvReadAccount", DepthMask.Deep));
        var body = OrganizationSoapEndpointTests.Envelope(OrganizationSoapEndpointTests.Execute(new RetrieveUserPrivilegesRequest { UserId = user }));
        using var req = new HttpRequestMessage(HttpMethod.Post, "XRMServices/2011/Organization.svc/web")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));

        var res = await fx.Client.SendAsync(req);

        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var result = XDocument.Parse(text).Root!.Element(Soap + "Body")!.Element(Services + "ExecuteResponse")!.Element(Services + "ExecuteResult")!;
        ((string)result.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))!).Should().EndWith(":RetrieveUserPrivilegesResponse");
        text.Should().Contain("prvReadAccount");
    }
}
