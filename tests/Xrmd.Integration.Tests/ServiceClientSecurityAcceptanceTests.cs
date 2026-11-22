using System.Net;
using System.Net.Http.Headers;
using System.ServiceModel;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.PowerPlatform.Dataverse.Client.Exceptions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4 acceptance: security seen by the real Dataverse <see cref="ServiceClient"/>. Users, roles and role privileges
/// are set up through the client of the seeded System Administrator, as the Plugin Registration Tool or an admin
/// script would; each user connects with its own development token (its own <c>oid</c>).
/// </summary>
/// <remarks>Binds Kestrel to 127.0.0.1:80; skip with <c>dotnet test --filter Category!=Acceptance</c>.</remarks>
[Collection("serviceclient")]
[Trait("Category", "Acceptance")]
public sealed class ServiceClientSecurityAcceptanceTests(ServiceClientFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private const string Project = TestEntities.Project;
    private readonly string _tag = "SC" + Guid.NewGuid().ToString("N")[..8];
    private Guid _rootBusinessUnit;

    public async Task InitializeAsync()
    {
        fx.DrainRequestLog();
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        // Through TestSql, the connection of the run's provider (plan 2.7b, Task 11: a SqlConnection failed on PostgreSQL).
        _rootBusinessUnit = (Guid)(await TestSql.ScalarAsync(fx.ConnectionString, "SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL"))!;
    }

    public Task DisposeAsync()
    {
        foreach (var line in fx.DrainRequestLog()) output.WriteLine(line);
        return Task.CompletedTask;
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private ServiceClient Connect(Guid objectId) =>
        new(new Uri(ServiceClientFixture.BaseUrl), _ => Task.FromResult(DevTokenFactory.Create(objectId, ServiceClientFixture.BaseUrl)),
            useUniqueInstance: true, new TestOutputLogger(output));

    private ServiceClient ConnectAdmin()
    {
        var client = Connect(fx.AdminObjectId);
        client.IsReady.Should().BeTrue(client.LastError);
        return client;
    }

    /// <summary>A new user in the root business unit with a new Entra object id; returns its systemuserid and oid.</summary>
    private (Guid Id, Guid Oid) CreateUser(ServiceClient admin, string label)
    {
        var oid = Guid.NewGuid();
        var id = admin.Create(new Entity("systemuser")
        {
            ["fullname"] = $"{_tag} {label}",
            ["domainname"] = $"{_tag}-{label}@example.test",
            ["azureactivedirectoryobjectid"] = oid,
            ["businessunitid"] = new EntityReference("businessunit", _rootBusinessUnit),
        });
        return (id, oid);
    }

    private static void AddPrivileges(ServiceClient admin, Guid roleId, params string[] privilegeNames) =>
        admin.Execute(new AddPrivilegesRoleRequest
        {
            RoleId = roleId,
            Privileges = privilegeNames.Select(name => new RolePrivilege { PrivilegeName = name, Depth = PrivilegeDepth.Global }).ToArray(),
        });

    /// <summary>User L holding the role "Project Reader" (<c>prvReadpb_Project</c> Global); returns L and the role.</summary>
    private ((Guid Id, Guid Oid) User, Guid Role) CreateProjectReader(ServiceClient admin)
    {
        var user = CreateUser(admin, "L");
        var role = admin.Create(new Entity("role") { ["name"] = $"Project Reader {_tag}" });
        AddPrivileges(admin, role, "prvReadpb_Project");
        admin.Associate("systemuser", user.Id, new Relationship("systemuserroles_association"),
            new EntityReferenceCollection { new EntityReference("role", role) });
        return (user, role);
    }

    private Entity NewProject(string label) => new(Project) { ["pb_name"] = $"{_tag}-{label}" };

    private static void ShouldFaultWith(Action act, int errorCode) =>
        act.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.ErrorCode.Should().Be(errorCode);

    /// <summary>
    /// A denied Create: over SOAP a fault with <see cref="ErrorCodes.PrivilegeDenied"/>; over the Web API (UseWebApi)
    /// ServiceClient surfaces the 403 as an <see cref="HttpOperationException"/> carrying the OData error body.
    /// </summary>
    private static void ShouldBePrivilegeDenied(Action act, bool overWebApi)
    {
        if (!overWebApi)
        {
            ShouldFaultWith(act, ErrorCodes.PrivilegeDenied);
            return;
        }
        var response = act.Should().Throw<HttpOperationException>().Which.Response;
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        JsonNode.Parse(response.Content)!["error"]!["code"]!.GetValue<string>().Should().Be("0x80040220");
    }

    private static async Task<HttpResponseMessage> SendAsync(Guid objectId, HttpMethod method, string path, string? json = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri(ServiceClientFixture.BaseUrl) };
        var req = new HttpRequestMessage(method, "api/data/v9.2/" + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, ServiceClientFixture.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var res = await http.SendAsync(req);
        await res.Content.LoadIntoBufferAsync();
        return res;
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>();
    }

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Limited_role_is_denied_with_PrivilegeDenied()
    {
        using var admin = ConnectAdmin();
        var (l, _) = CreateProjectReader(admin);
        var projectId = admin.Create(NewProject("admin"));

        using var client = Connect(l.Oid);
        client.IsReady.Should().BeTrue(client.LastError);

        client.Retrieve(Project, projectId, new ColumnSet("pb_name")).GetAttributeValue<string>("pb_name").Should().Be($"{_tag}-admin");
        ShouldFaultWith(() => client.Create(NewProject("denied")), unchecked((int)0x80040220)); // PrivilegeDenied

        var res = await SendAsync(l.Oid, HttpMethod.Post, TestEntities.ProjectSet, $$"""{ "pb_name": "{{_tag}}-denied-webapi" }""");
        (await ErrorCodeAsync(res, HttpStatusCode.Forbidden)).Should().Be("0x80040220");
    }

    [Fact]
    public async Task User_without_roles_cannot_connect()
    {
        Guid oid;
        using (var admin = ConnectAdmin()) oid = CreateUser(admin, "noroles").Oid;

        var res = await SendAsync(oid, HttpMethod.Get, "WhoAmI");
        (await ErrorCodeAsync(res, HttpStatusCode.Forbidden)).Should().Be("0x80042f09");

        // ServiceClient connects lazily (IsReady can be true before any call); the first call that reaches xrmd, WhoAmI
        // here or the login flow's own, faults with 0x80042F09.
        using var client = Connect(oid);
        if (client.IsReady)
            ShouldFaultWith(() => client.Execute(new WhoAmIRequest()), ErrorCodes.UserNotAssignedRoles);
        else
            client.LastException.Should().BeOfType<FaultException<OrganizationServiceFault>>()
                .Which.Detail.ErrorCode.Should().Be(ErrorCodes.UserNotAssignedRoles);
    }

    [Theory]
    [InlineData(false)] // SOAP CallerId header
    [InlineData(true)]  // Web API MSCRMCallerID header
    public void Impersonation_with_CallerId(bool useWebApi)
    {
        using var admin = ConnectAdmin();
        var (l, role) = CreateProjectReader(admin);

        using var client = ConnectAdmin(); // System Administrator: holds prvActOnBehalfOfAnotherUser
        client.CallerId = l.Id;
        // UseWebApi lives in a process-wide ConfigurationOptions singleton (see ServiceClientAcceptanceTests): reset in finally.
        client.UseWebApi = useWebApi;
        try
        {
            ((WhoAmIResponse)client.Execute(new WhoAmIRequest())).UserId.Should().Be(l.Id);
            ShouldBePrivilegeDenied(() => client.Create(NewProject("impersonated-denied")), useWebApi);

            AddPrivileges(admin, role, "prvCreatepb_Project");

            var id = client.Create(NewProject("impersonated"));
            client.UseWebApi = false;
            admin.Retrieve(Project, id, new ColumnSet("createdby")).GetAttributeValue<EntityReference>("createdby").Id.Should().Be(l.Id);
        }
        finally
        {
            client.UseWebApi = false;
        }
    }
}
