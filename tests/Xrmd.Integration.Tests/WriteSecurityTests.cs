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
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The per-message privilege and access checks of the <see cref="SecurityGuard"/>, which run before the pipeline at every
/// depth, and the caller rules for disabled users and users without roles. Business units R (root) → A → A1 and R → B;
/// users uR, uA, uA1, uB, each owning one <c>pb_project</c>; one role per grant.
/// </summary>
[Collection("xrmd")]
public sealed class WriteSecurityTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Project = TestEntities.Project;
    private const string Pipe = TestEntities.Pipe;
    private const string PipeLog = TestEntities.PipeLog;
    private const string PipeParent = TestEntities.PipeParent;
    private const string Plugins = "Xrmd.TestPlugins.";

    private readonly string _tag = "WS" + Guid.NewGuid().ToString("N");
    private OrganizationService _admin = null!;
    private Guid _root, _a, _a1, _b;
    private (Guid Id, Guid Oid) _uR, _uA, _uA1, _uB;
    private Guid _pA, _pB;

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeParentAsync(store);
        await TestEntities.EnsureProjectAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        _admin = ServiceFor(fx, fx.AdminObjectId);
        _root = await RootBusinessUnitAsync(fx);
        _a = await CreateBusinessUnitAsync(fx, _root, "A-" + _tag);
        _a1 = await CreateBusinessUnitAsync(fx, _a, "A1-" + _tag);
        _b = await CreateBusinessUnitAsync(fx, _root, "B-" + _tag);
        _uR = await UserAsync(_root);
        _uA = await UserAsync(_a);
        _uA1 = await UserAsync(_a1);
        _uB = await UserAsync(_b);
        await ProjectAsync("R", _uR.Id);
        _pA = await ProjectAsync("A", _uA.Id);
        await ProjectAsync("A1", _uA1.Id);
        _pB = await ProjectAsync("B", _uB.Id);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<(Guid Id, Guid Oid)> UserAsync(Guid businessUnit, bool disabled = false)
    {
        var oid = Guid.NewGuid();
        return (await CreateUserAsync(fx, businessUnit, oid, disabled), oid);
    }

    private Task<Guid> ProjectAsync(string label, Guid owner) =>
        _admin.CreateAsync(new Entity(Project) { ["pb_name"] = $"{_tag}-{label}", ["ownerid"] = new EntityReference("systemuser", owner) });

    /// <summary>Gives <paramref name="userId"/> a new role with the privileges (none: a role without privileges).</summary>
    private async Task GrantAsync(Guid userId, params (string Privilege, DepthMask Depth)[] privileges) =>
        await AssignRoleAsync(fx, userId, await CreateRoleAsync(fx, "ws-" + Guid.NewGuid().ToString("N")[..8], privileges));

    private OrganizationService As((Guid Id, Guid Oid) user) => ServiceFor(fx, user.Oid);

    private Task<HttpResponseMessage> SendAsync((Guid Id, Guid Oid) user, HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(user.Oid, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return fx.Client.SendAsync(req);
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<int> CountNamedAsync(string entity, string name) =>
        Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM data.{entity} WHERE pb_name = @name", ("@name", name)));

    private static Task PrivilegeDeniedAsync(Func<Task> act, string privilege) =>
        act.Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.PrivilegeDenied && e.HttpStatus == 403 && e.Message.Contains(privilege));

    private static Task AccessDeniedAsync(Func<Task> act, AccessRights right, Guid userId, Guid recordId, string entity) =>
        act.Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.AccessDenied && e.HttpStatus == 403
                        && e.Message == $"Principal user with id {userId} does not have {right} right(s) for record with id {recordId} of entity {entity}.");

    private async Task<Guid> StepAsync(string plugin, string message, string entity, int stage, string config)
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(_admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(_admin, assembly, Plugins + plugin);
        return await PluginTestHelper.RegisterStepAsync(_admin, type, message, entity, stage, config: config);
    }

    // ---- Create -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_without_privilege_is_403()
    {
        await GrantAsync(_uA.Id, ("prvReadpb_Project", DepthMask.Global));
        var name = _tag + "-denied";

        await PrivilegeDeniedAsync(() => As(_uA).CreateAsync(new Entity(Project) { ["pb_name"] = name }), "prvCreatepb_Project");

        var res = await SendAsync(_uA, HttpMethod.Post, TestEntities.ProjectSet, $$"""{ "pb_name": "{{name}}" }""");
        (await ErrorCodeAsync(res, HttpStatusCode.Forbidden)).Should().Be("0x80040220");

        (await CountNamedAsync(Project, name)).Should().Be(0);
    }

    [Fact]
    public async Task Create_for_another_owner_respects_depth()
    {
        var colleague = await UserAsync(_a);
        await GrantAsync(_uA.Id, ("prvCreatepb_Project", DepthMask.Basic));

        // Basic: only for itself (or one of its teams).
        (await As(_uA).CreateAsync(new Entity(Project) { ["pb_name"] = _tag + "-own" })).Should().NotBeEmpty();
        var id = Guid.NewGuid();
        await AccessDeniedAsync(() => As(_uA).CreateAsync(new Entity(Project, id)
        {
            ["pb_name"] = _tag + "-basic", ["ownerid"] = new EntityReference("systemuser", colleague.Id),
        }), AccessRights.CreateAccess, _uA.Id, id, Project);
        (await CountNamedAsync(Project, _tag + "-basic")).Should().Be(0);

        // Local: for a colleague of the business unit, not for a user of another one.
        var local = await UserAsync(_a);
        await GrantAsync(local.Id, ("prvCreatepb_Project", DepthMask.Local));
        var created = await As(local).CreateAsync(new Entity(Project)
        {
            ["pb_name"] = _tag + "-local", ["ownerid"] = new EntityReference("systemuser", colleague.Id),
        });
        (await ScalarAsync($"SELECT ownerid FROM data.{Project} WHERE {Project}id = @id", ("@id", created))).Should().Be(colleague.Id);
        await FluentActions.Awaiting(() => As(local).CreateAsync(new Entity(Project)
            {
                ["pb_name"] = _tag + "-other", ["ownerid"] = new EntityReference("systemuser", _uB.Id),
            }))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.AccessDenied && e.Message.Contains("CreateAccess"));
    }

    // ---- Update, Delete, Assign -------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_and_Delete_respect_depth()
    {
        await GrantAsync(_uA.Id, ("prvWritepb_Project", DepthMask.Local), ("prvDeletepb_Project", DepthMask.Local));
        var colleague = await UserAsync(_a);
        var pColleague = await ProjectAsync("A2", colleague.Id);

        await As(_uA).UpdateAsync(new Entity(Project, pColleague) { ["pb_count"] = 7 });
        (await ScalarAsync($"SELECT pb_count FROM data.{Project} WHERE {Project}id = @id", ("@id", pColleague))).Should().Be(7);

        await AccessDeniedAsync(() => As(_uA).UpdateAsync(new Entity(Project, _pB) { ["pb_count"] = 7 }),
            AccessRights.WriteAccess, _uA.Id, _pB, Project);
        await AccessDeniedAsync(() => As(_uA).DeleteAsync(Project, _pB), AccessRights.DeleteAccess, _uA.Id, _pB, Project);
        (await ScalarAsync($"SELECT pb_count FROM data.{Project} WHERE {Project}id = @id", ("@id", _pB))).Should().BeNull();

        // SetState runs as an Update: Write on the record.
        await AccessDeniedAsync(() => As(_uA).ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(Project, _pB), State = new OptionSetValue(1), Status = new OptionSetValue(-1),
        }), AccessRights.WriteAccess, _uA.Id, _pB, Project);

        await As(_uA).DeleteAsync(Project, pColleague);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{Project} WHERE {Project}id = @id", ("@id", pColleague))).Should().Be(0);

        // Without the privilege at all.
        var reader = await UserAsync(_a);
        await GrantAsync(reader.Id, ("prvReadpb_Project", DepthMask.Global));
        await PrivilegeDeniedAsync(() => As(reader).UpdateAsync(new Entity(Project, _pA) { ["pb_count"] = 1 }), "prvWritepb_Project");
        await PrivilegeDeniedAsync(() => As(reader).DeleteAsync(Project, _pA), "prvDeletepb_Project");
    }

    [Fact]
    public async Task Assign_requires_assign_and_target_read()
    {
        var assignee = await UserAsync(_a);
        await GrantAsync(_uA.Id, ("prvWritepb_Project", DepthMask.Global));
        AssignRequest Assign(Guid to) => new() { Target = new EntityReference(Project, _pA), Assignee = new EntityReference("systemuser", to) };

        await PrivilegeDeniedAsync(() => As(_uA).ExecuteAsync(Assign(assignee.Id)), "prvAssignpb_Project");

        await GrantAsync(_uA.Id, ("prvAssignpb_Project", DepthMask.Local));
        await FluentActions.Awaiting(() => As(_uA).ExecuteAsync(Assign(assignee.Id)))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.PrincipalPrivilegeDenied && e.HttpStatus == 403
                        && e.Message == "The target user or team lacks a privilege the operation needs.");

        // An Update of ownerid follows the rules of Assign.
        await FluentActions.Awaiting(() => As(_uA).UpdateAsync(new Entity(Project, _pA) { ["ownerid"] = new EntityReference("systemuser", assignee.Id) }))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.PrincipalPrivilegeDenied);

        await GrantAsync(assignee.Id, ("prvReadpb_Project", DepthMask.Basic));
        await As(_uA).ExecuteAsync(Assign(assignee.Id));
        (await ScalarAsync($"SELECT ownerid FROM data.{Project} WHERE {Project}id = @id", ("@id", _pA))).Should().Be(assignee.Id);

        // Assign depth: a record of another business unit.
        await AccessDeniedAsync(() => As(_uA).ExecuteAsync(new AssignRequest
        {
            Target = new EntityReference(Project, _pB), Assignee = new EntityReference("systemuser", assignee.Id),
        }), AccessRights.AssignAccess, _uA.Id, _pB, Project);
    }

    [Fact]
    public async Task Assign_to_team_without_read_is_0x80040231()
    {
        var team = await CreateTeamAsync(fx, _a, teamType: 0);
        AssignRequest Assign() => new() { Target = new EntityReference(Project, _pA), Assignee = new EntityReference("team", team) };

        // Even a System Administrator cannot hand a record to a team that could not read it.
        await FluentActions.Awaiting(() => _admin.ExecuteAsync(Assign()))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.PrincipalPrivilegeDenied && e.HttpStatus == 403
                        && e.Message == "The target user or team lacks a privilege the operation needs.");
        (await ScalarAsync($"SELECT ownerid FROM data.{Project} WHERE {Project}id = @id", ("@id", _pA))).Should().Be(_uA.Id);

        await SecurityTestHelper.GrantAsync(fx, team, team: true, ("prvReadpb_Project", DepthMask.Basic));
        await _admin.ExecuteAsync(Assign());
        (await ScalarAsync($"SELECT ownerid FROM data.{Project} WHERE {Project}id = @id", ("@id", _pA))).Should().Be(team);
    }

    // ---- lookups and relationships ----------------------------------------------------------------------------------

    [Fact]
    public async Task Lookup_requires_append_and_appendto()
    {
        var parentB = await _admin.CreateAsync(new Entity(PipeParent)
        {
            ["pb_name"] = _tag + "-parentB", ["ownerid"] = new EntityReference("systemuser", _uB.Id),
        });
        var parentA = await _admin.CreateAsync(new Entity(PipeParent)
        {
            ["pb_name"] = _tag + "-parentA", ["ownerid"] = new EntityReference("systemuser", _uA.Id),
        });
        Entity NewPipe(Guid parent) => new(Pipe) { ["pb_name"] = _tag + "-pipe", ["pb_parentid"] = new EntityReference(PipeParent, parent) };

        await GrantAsync(_uA.Id, ("prvCreatepb_Pipe", DepthMask.Global), ("prvWritepb_Pipe", DepthMask.Global),
            ("prvAppendTopb_PipeParent", DepthMask.Local));
        await PrivilegeDeniedAsync(() => As(_uA).CreateAsync(NewPipe(parentA)), "prvAppendpb_Pipe");

        await GrantAsync(_uA.Id, ("prvAppendpb_Pipe", DepthMask.Global));
        await AccessDeniedAsync(() => As(_uA).CreateAsync(NewPipe(parentB)), AccessRights.AppendToAccess, _uA.Id, parentB, PipeParent);
        (await CountNamedAsync(Pipe, _tag + "-pipe")).Should().Be(0);

        var pipe = await As(_uA).CreateAsync(NewPipe(parentA));
        await AccessDeniedAsync(() => As(_uA).UpdateAsync(new Entity(Pipe, pipe) { ["pb_parentid"] = new EntityReference(PipeParent, parentB) }),
            AccessRights.AppendToAccess, _uA.Id, parentB, PipeParent);
        (await ScalarAsync($"SELECT pb_parentid FROM data.{Pipe} WHERE {Pipe}id = @id", ("@id", pipe))).Should().Be(parentA);

        // An unchanged lookup needs no Append/AppendTo.
        await As(_uA).UpdateAsync(new Entity(Pipe, pipe) { ["pb_count"] = 1, ["pb_parentid"] = new EntityReference(PipeParent, parentA) });
    }

    [Fact]
    public async Task Associate_and_Disassociate_check_append_and_read_as_caller()
    {
        var parentB = await _admin.CreateAsync(new Entity(PipeParent)
        {
            ["pb_name"] = _tag + "-parentB", ["ownerid"] = new EntityReference("systemuser", _uB.Id),
        });
        var linked = await _admin.CreateAsync(new Entity(Pipe)
        {
            ["pb_name"] = _tag + "-linked", ["ownerid"] = new EntityReference("systemuser", _uA.Id),
            ["pb_parentid"] = new EntityReference(PipeParent, parentB),
        });
        var free = await _admin.CreateAsync(new Entity(Pipe)
        {
            ["pb_name"] = _tag + "-free", ["ownerid"] = new EntityReference("systemuser", _uA.Id),
        });
        var relationship = new Relationship(TestEntities.PipeParentRelationship);

        await GrantAsync(_uA.Id, ("prvReadpb_Pipe", DepthMask.Global), ("prvAppendTopb_PipeParent", DepthMask.Local),
            ("prvReadpb_PipeParent", DepthMask.Basic));

        // Append on the referencing record.
        await PrivilegeDeniedAsync(() => Task.Run(() => As(_uA).Associate(PipeParent, parentB, relationship,
            new EntityReferenceCollection { new(Pipe, free) })), "prvAppendpb_Pipe");

        // AppendTo on the referenced record (of another business unit).
        await GrantAsync(_uA.Id, ("prvAppendpb_Pipe", DepthMask.Global));
        await AccessDeniedAsync(() => Task.Run(() => As(_uA).Associate(PipeParent, parentB, relationship,
            new EntityReferenceCollection { new(Pipe, free) })), AccessRights.AppendToAccess, _uA.Id, parentB, PipeParent);
        (await ScalarAsync($"SELECT pb_parentid FROM data.{Pipe} WHERE {Pipe}id = @id", ("@id", free))).Should().BeNull();

        // Disassociate reads the parent as the caller: not readable → ReadAccess, the lookup is unchanged.
        await GrantAsync(_uA.Id, ("prvAppendTopb_PipeParent", DepthMask.Global));
        await AccessDeniedAsync(() => Task.Run(() => As(_uA).Disassociate(PipeParent, parentB, relationship,
            new EntityReferenceCollection { new(Pipe, linked) })), AccessRights.ReadAccess, _uA.Id, parentB, PipeParent);
        (await ScalarAsync($"SELECT pb_parentid FROM data.{Pipe} WHERE {Pipe}id = @id", ("@id", linked))).Should().Be(parentB);

        // Readable parent: the Disassociate goes through.
        await GrantAsync(_uA.Id, ("prvReadpb_PipeParent", DepthMask.Global));
        await Task.Run(() => As(_uA).Disassociate(PipeParent, parentB, relationship, new EntityReferenceCollection { new(Pipe, linked) }));
        (await ScalarAsync($"SELECT pb_parentid FROM data.{Pipe} WHERE {Pipe}id = @id", ("@id", linked))).Should().BeNull();
    }

    // ---- plug-ins ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Denied_message_runs_no_plugin()
    {
        var log = _tag + "-log";
        await StepAsync("ContextRecorderPlugin", "Create", Project, 10, PipeLog);
        // The plug-in could write its log if it ran: only the guard keeps it from running.
        await GrantAsync(_uA.Id, ("prvCreatepb_PipeLog", DepthMask.Global));

        await PrivilegeDeniedAsync(() => As(_uA).CreateAsync(new Entity(Project) { ["pb_name"] = log }), "prvCreatepb_Project");
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{PipeLog} WHERE createdby = @user", ("@user", _uA.Id))).Should().Be(0);

        await GrantAsync(_uA.Id, ("prvCreatepb_Project", DepthMask.Basic));
        await As(_uA).CreateAsync(new Entity(Project) { ["pb_name"] = log });
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{PipeLog} WHERE createdby = @user", ("@user", _uA.Id))).Should().Be(1);
    }

    [Fact]
    public async Task Plugin_nested_call_uses_caller_security()
    {
        var step = await StepAsync("CreateRecordPlugin", "Create", Pipe, 40, PipeLog);
        await GrantAsync(_uA.Id, ("prvCreatepb_Pipe", DepthMask.Global));
        var name = _tag + "-nested";

        await FluentActions.Awaiting(() => As(_uA).CreateAsync(new Entity(Pipe) { ["pb_name"] = name }))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.PrivilegeDenied && e.Message.Contains("prvCreatepb_PipeLog"));
        (await CountNamedAsync(Pipe, name)).Should().Be(0);

        // The same plug-in acting as SYSTEM is unrestricted.
        await _admin.UpdateAsync(new Entity("sdkmessageprocessingstep", step) { ["configuration"] = PipeLog + "|system" });
        var id = await As(_uA).CreateAsync(new Entity(Pipe) { ["pb_name"] = name });
        (await CountNamedAsync(Pipe, name)).Should().Be(1);
        (await CountNamedAsync(PipeLog, $"Create|40|1|{id}")).Should().Be(1);
    }

    // ---- systemuser identity -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_a_System_Administrator_rebinds_the_Entra_object_id()
    {
        var adminId = (Guid)(await ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid",
            ("@oid", fx.AdminObjectId)))!;
        await GrantAsync(_uR.Id, ("prvWriteSystemUser", DepthMask.Local), ("prvReadSystemUser", DepthMask.Global));
        var stolen = Guid.NewGuid();

        await FluentActions.Awaiting(() => As(_uR).UpdateAsync(new Entity("systemuser", adminId) { ["azureactivedirectoryobjectid"] = stolen }))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.PrivilegeDenied && e.HttpStatus == 403 && e.Message.Contains("azureactivedirectoryobjectid"));
        var res = await SendAsync(_uR, HttpMethod.Patch, $"systemusers({adminId})", $$"""{ "azureactivedirectoryobjectid": "{{stolen}}" }""");
        (await ErrorCodeAsync(res, HttpStatusCode.Forbidden)).Should().Be("0x80040220");
        (await ScalarAsync("SELECT azureactivedirectoryobjectid FROM data.systemuser WHERE systemuserid = @id", ("@id", adminId)))
            .Should().Be(fx.AdminObjectId);

        // Other columns stay writable with the privilege; a System Administrator may rebind.
        await As(_uR).UpdateAsync(new Entity("systemuser", _uR.Id) { ["fullname"] = _tag + "-renamed" });
        var rebound = Guid.NewGuid();
        await _admin.UpdateAsync(new Entity("systemuser", _uR.Id) { ["azureactivedirectoryobjectid"] = rebound });
        (await ScalarAsync("SELECT azureactivedirectoryobjectid FROM data.systemuser WHERE systemuserid = @id", ("@id", _uR.Id)))
            .Should().Be(rebound);
    }

    [Fact]
    public async Task Duplicate_Entra_object_id_is_412()
    {
        await FluentActions.Awaiting(() => _admin.CreateAsync(new Entity("systemuser")
            {
                ["fullname"] = _tag + "-dup", ["domainname"] = _tag + "-dup", ["azureactivedirectoryobjectid"] = _uA.Oid,
                ["businessunitid"] = new EntityReference("businessunit", _a),
            }))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.DuplicateRecord && e.HttpStatus == 412);
        await FluentActions.Awaiting(() => _admin.UpdateAsync(new Entity("systemuser", _uB.Id) { ["azureactivedirectoryobjectid"] = _uA.Oid }))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.DuplicateRecord && e.HttpStatus == 412);

        // Users without an Entra object id are not duplicates of each other.
        foreach (var label in new[] { "-nooid1", "-nooid2" })
            await _admin.CreateAsync(new Entity("systemuser")
            {
                ["fullname"] = _tag + label, ["domainname"] = _tag + label, ["businessunitid"] = new EntityReference("businessunit", _a),
            });
    }

    // ---- caller rules -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task User_without_roles_is_403_UserNotAssignedRoles()
    {
        var user = await UserAsync(_a);

        (await ErrorCodeAsync(await SendAsync(user, HttpMethod.Get, "WhoAmI"), HttpStatusCode.Forbidden)).Should().Be("0x80042f09");
        await FluentActions.Awaiting(() => As(user).RetrieveMultipleAsync(new QueryExpression(Project)))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.UserNotAssignedRoles && e.HttpStatus == 403
                        && e.Message == $"User {user.Id} has no security role, neither directly nor through a team.");

        // A role through an owner team counts.
        var team = await CreateTeamAsync(fx, _a, teamType: 0, user.Id);
        await AssignRoleAsync(fx, team, await CreateRoleAsync(fx, "ws-team-" + _tag[..8]), team: true);
        (await SendAsync(user, HttpMethod.Get, "WhoAmI")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Disabled_user_is_403_UserDisabled()
    {
        var user = await UserAsync(_a, disabled: true);
        await GrantAsync(user.Id, ("prvReadpb_Project", DepthMask.Global));

        (await ErrorCodeAsync(await SendAsync(user, HttpMethod.Get, "WhoAmI"), HttpStatusCode.Forbidden)).Should().Be("0x80040225");
        await FluentActions.Awaiting(() => As(user).RetrieveAsync(Project, _pA, new ColumnSet("pb_name")))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.UserDisabled && e.HttpStatus == 403
                        && e.Message == $"User {user.Id} is disabled.");
    }
}
