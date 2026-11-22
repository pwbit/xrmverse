using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The rules of the system-maintained tables (<c>asyncoperation</c>, <c>plugintracelog</c>, <c>principalobjectaccess</c>,
/// <c>privilege</c>, the intersects), of the SYSTEM user's row, of <c>role.roletemplateid</c> and of steps that set an
/// impersonating user. Every request runs as the System Administrator unless stated: the rules hold for every caller.
/// </summary>
[Collection("xrmd")]
public sealed class SystemTableRulesTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Pipe = TestEntities.Pipe;
    private const string Maintained = "is not allowed: the table is maintained by the system.";
    private const string OnlyCancel = "Only the cancellation of a system job (statecode 3, statuscode 32) is supported.";
    private const string StepImpersonating = "sdkmessageprocessingstep_impersonatinguserid_systemuser";
    private const string StepPluginType = "sdkmessageprocessingstep_plugintypeid_plugintype";
    private const string UserBusinessUnit = "systemuser_businessunitid_businessunit";
    private const string JobStep = "asyncoperation_owningextensionid_sdkmessageprocessingstep";

    private OrganizationService _admin = null!;
    private Guid _adminId;
    private Guid _root;
    private Guid _system;
    private Guid? _assembly;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync("DELETE FROM data.asyncoperation; DELETE FROM data.plugintracelog;");
        _admin = ServiceFor(fx, fx.AdminObjectId);
        _adminId = await ScalarAsync<Guid>("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid",
            ("@oid", fx.AdminObjectId));
        _root = await RootBusinessUnitAsync(fx);
        _system = await Cache(fx).SystemUserIdAsync(default);
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync("DELETE FROM data.asyncoperation; DELETE FROM data.plugintracelog;");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        // A COUNT(*) is bigint on PostgreSQL.
        return result is T typed ? typed : (T)Convert.ChangeType(result, typeof(T), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    /// <summary>A system job in the given state, inserted as the queue does (direct SQL), owned by the admin.</summary>
    private async Task<Guid> JobAsync(int state, int status)
    {
        var id = Guid.NewGuid();
        await ExecAsync("""
            INSERT INTO data.asyncoperation (asyncoperationid, name, operationtype, statecode, statuscode,
                createdon, createdby, modifiedon, modifiedby, ownerid, owneridtype, owninguser, owningbusinessunit)
            VALUES (@id, N'job', 1, @state, @status, SYSUTCDATETIME(), @user, SYSUTCDATETIME(), @user, @user, 8, @user, @bu)
            """, ("@id", id), ("@state", state), ("@status", status), ("@user", _adminId), ("@bu", _root));
        return id;
    }

    private async Task<(int State, int Status)> JobStateAsync(Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT statecode, statuscode FROM data.asyncoperation WHERE asyncoperationid = @id");
        cmd.Parameters.AddWithValue("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue();
        return (rd.GetInt32(0), rd.GetInt32(1));
    }

    private static Entity Cancel(Guid job) => new("asyncoperation", job)
    {
        ["statecode"] = new OptionSetValue(3), ["statuscode"] = new OptionSetValue(32),
    };

    private static Task FaultAsync(Func<Task> act, int code, int status, string? message = null) =>
        act.Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == code && e.HttpStatus == status && (message == null || e.Message == message));

    private static Task MaintainedAsync(Func<Task> act, string message, string entity) =>
        FaultAsync(act, ErrorCodes.PrivilegeDenied, 403, $"{message} of {entity} {Maintained}");

    private static Task SystemUserAsync(Func<Task> act) =>
        FaultAsync(act, ErrorCodes.PrivilegeDenied, 403, "The SYSTEM user cannot be modified.");

    private static Task ActOnBehalfAsync(Func<Task> act) =>
        FaultAsync(act, ErrorCodes.CannotActOnBehalfOfAnotherUser, 403, "The caller lacks prvActOnBehalfOfAnotherUser, which impersonating another user requires.");

    private static AssociateRequest Associate(EntityReference target, string relationship, params EntityReference[] related) => new()
    {
        Target = target, Relationship = new Relationship(relationship), RelatedEntities = new EntityReferenceCollection(related),
    };

    private static DisassociateRequest Disassociate(EntityReference target, string relationship, params EntityReference[] related) => new()
    {
        Target = target, Relationship = new Relationship(relationship), RelatedEntities = new EntityReferenceCollection(related),
    };

    private async Task<(Guid Id, Guid Oid)> UserAsync(params Guid[] roles)
    {
        var oid = Guid.NewGuid();
        var id = await CreateUserAsync(fx, _root, oid);
        foreach (var role in roles) await AssignRoleAsync(fx, id, role);
        return (id, oid);
    }

    /// <summary>A plug-in type registered by the admin (in one test assembly per test).</summary>
    private async Task<Guid> PluginTypeAsync(string name = "Xrmd.TestPlugins.ContextRecorderPlugin")
    {
        _assembly ??= await PluginTestHelper.RegisterAssemblyAsync(_admin, PluginTestHelper.TestPluginBytes);
        return await PluginTestHelper.RegisterTypeAsync(_admin, _assembly.Value, name);
    }

    private async Task<Guid?> StepImpersonatingAsync(Guid step) =>
        await ScalarAsync<object>("SELECT impersonatinguserid FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id",
            ("@id", step)) as Guid?;

    // ---- asyncoperation ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Asyncoperation_cannot_be_created()
    {
        await MaintainedAsync(() => _admin.CreateAsync(new Entity("asyncoperation") { ["name"] = "forged" }), "Create", "asyncoperation");
        (await ScalarAsync<int>("SELECT COUNT(*) FROM data.asyncoperation")).Should().Be(0);
    }

    [Fact]
    public async Task Asyncoperation_can_only_be_cancelled()
    {
        const string onlyCancel = "Only the cancellation of a system job (statecode 3, statuscode 32) is supported.";
        var job = await JobAsync(0, 0);

        await FaultAsync(() => _admin.UpdateAsync(new Entity("asyncoperation", job) { ["name"] = "renamed" }),
            ErrorCodes.InvalidArgument, 400, onlyCancel);
        await FaultAsync(() => _admin.UpdateAsync(new Entity("asyncoperation", job)
            {
                ["statecode"] = new OptionSetValue(3), ["statuscode"] = new OptionSetValue(30),
            }),
            ErrorCodes.InvalidArgument, 400, onlyCancel);
        // A cancellation that also writes another column is refused as a whole.
        await FaultAsync(() =>
            {
                var update = Cancel(job);
                update["name"] = "renamed";
                return _admin.UpdateAsync(update);
            },
            ErrorCodes.InvalidArgument, 400, onlyCancel);
        await FaultAsync(() => _admin.UpdateAsync(new Entity("asyncoperation", job)
            {
                ["statecode"] = new OptionSetValue(3), ["statuscode"] = new OptionSetValue(32),
                ["ownerid"] = new EntityReference("systemuser", _adminId),
            }),
            ErrorCodes.InvalidArgument, 400, onlyCancel);
        (await JobStateAsync(job)).Should().Be((0, 0));

        // Ready (0) → Canceled; system columns in the target are ignored.
        var cancel = Cancel(job);
        cancel["asyncoperationid"] = job;
        cancel["modifiedon"] = DateTime.UtcNow;
        await _admin.UpdateAsync(cancel);
        (await JobStateAsync(job)).Should().Be((3, 32));

        // A completed job cannot be cancelled again.
        await FaultAsync(() => _admin.UpdateAsync(Cancel(job)), ErrorCodes.InvalidArgument, 400, onlyCancel);

        // Suspended (1) is cancelled through SetState; Locked (2) is not.
        var suspended = await JobAsync(1, 10);
        await _admin.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference("asyncoperation", suspended), State = new OptionSetValue(3), Status = new OptionSetValue(32),
        });
        (await JobStateAsync(suspended)).Should().Be((3, 32));
        var locked = await JobAsync(2, 20);
        await FaultAsync(() => _admin.UpdateAsync(Cancel(locked)), ErrorCodes.InvalidArgument, 400, onlyCancel);
        (await JobStateAsync(locked)).Should().Be((2, 20));

        // A missing job is the core operation's 404.
        await FaultAsync(() => _admin.UpdateAsync(Cancel(Guid.NewGuid())), ErrorCodes.ObjectDoesNotExist, 404);
    }

    /// <summary>Review I3: a one-to-many Associate writes the job's lookup, an Update the cancel-only rule refuses.</summary>
    [Fact]
    public async Task Asyncoperation_lookup_cannot_be_associated()
    {
        var type = await PluginTypeAsync();
        var step = await PluginTestHelper.RegisterStepAsync(_admin, type, "Create", Pipe, 40, mode: 1);
        var job = await JobAsync(0, 0);
        var jobRef = new EntityReference("asyncoperation", job);
        var stepRef = new EntityReference("sdkmessageprocessingstep", step);

        await FaultAsync(() => _admin.ExecuteAsync(Associate(jobRef, JobStep, stepRef)), ErrorCodes.InvalidArgument, 400, OnlyCancel);
        await FaultAsync(() => _admin.ExecuteAsync(Associate(stepRef, JobStep, jobRef)), ErrorCodes.InvalidArgument, 400, OnlyCancel);
        await FaultAsync(() => _admin.ExecuteAsync(Disassociate(stepRef, JobStep, jobRef)), ErrorCodes.InvalidArgument, 400, OnlyCancel);

        (await ScalarAsync<object>("SELECT owningextensionid FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", job)))
            .Should().Be(DBNull.Value);
    }

    /// <summary>Review minor 6: a primary key that is not a Guid is the core operation's 400, not an InvalidCastException.</summary>
    [Fact]
    public async Task Update_with_a_malformed_primary_key_is_400()
    {
        var job = Cancel(Guid.Empty);
        job.Id = Guid.Empty;
        job["asyncoperationid"] = "not-a-guid";
        await FaultAsync(() => _admin.UpdateAsync(job), ErrorCodes.InvalidArgument, 400);
        await FaultAsync(() => _admin.UpdateAsync(new Entity("systemuser") { ["systemuserid"] = "not-a-guid", ["fullname"] = "x" }),
            ErrorCodes.InvalidArgument, 400);
    }

    [Fact]
    public async Task Asyncoperation_delete_only_when_completed()
    {
        var waiting = await JobAsync(0, 0);
        var done = await JobAsync(3, 30);

        await FaultAsync(() => _admin.DeleteAsync("asyncoperation", waiting), ErrorCodes.InvalidArgument, 400,
            "Only completed system jobs can be deleted.");
        await _admin.DeleteAsync("asyncoperation", done);

        (await ScalarAsync<int>("SELECT COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", waiting))).Should().Be(1);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", done))).Should().Be(0);
    }

    [Fact]
    public async Task Asyncoperation_cannot_be_assigned()
    {
        var job = await JobAsync(3, 30);
        var other = await UserAsync(StandardRoles.BasicUser);

        await MaintainedAsync(() => _admin.ExecuteAsync(new AssignRequest
            {
                Target = new EntityReference("asyncoperation", job), Assignee = new EntityReference("systemuser", other.Id),
            }),
            "Assign", "asyncoperation");
        (await ScalarAsync<Guid>("SELECT owninguser FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", job)))
            .Should().Be(_adminId);
    }

    // ---- plugintracelog ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Plugintracelog_is_read_and_delete_only()
    {
        var trace = Guid.NewGuid();
        await ExecAsync("""
            INSERT INTO data.plugintracelog (plugintracelogid, typename, messagename, createdon, createdby, modifiedon, modifiedby,
                owningbusinessunit, statecode, statuscode)
            VALUES (@id, N'Some.Plugin', N'Create', SYSUTCDATETIME(), @user, SYSUTCDATETIME(), @user, @bu, 0, 1)
            """, ("@id", trace), ("@user", _adminId), ("@bu", _root));

        await MaintainedAsync(() => _admin.CreateAsync(new Entity("plugintracelog") { ["typename"] = "Forged" }), "Create", "plugintracelog");
        await MaintainedAsync(() => _admin.UpdateAsync(new Entity("plugintracelog", trace) { ["typename"] = "Forged" }),
            "Update", "plugintracelog");

        (await _admin.RetrieveAsync("plugintracelog", trace, new ColumnSet("typename"))).GetAttributeValue<string>("typename")
            .Should().Be("Some.Plugin");
        await _admin.DeleteAsync("plugintracelog", trace);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM data.plugintracelog")).Should().Be(0);
    }

    // ---- security tables --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("principalobjectaccess")]
    [InlineData("privilege")]
    [InlineData("roleprivileges")]
    [InlineData("systemuserroles")]
    [InlineData("teamroles")]
    [InlineData("teammembership")]
    [InlineData("privilegeobjecttypecodes")]
    public async Task Security_tables_reject_direct_writes(string table)
    {
        // An existing row when there is one (the rule precedes the existence check either way).
        var row = await ScalarAsync<object?>($"SELECT TOP 1 {table}id FROM data.{table}") as Guid? ?? Guid.NewGuid();
        var count = await ScalarAsync<int>($"SELECT COUNT(*) FROM data.{table}");

        await MaintainedAsync(() => _admin.CreateAsync(new Entity(table)), "Create", table);
        await MaintainedAsync(() => _admin.UpdateAsync(new Entity(table, row) { ["statecode"] = new OptionSetValue(0) }), "Update", table);
        await MaintainedAsync(() => _admin.DeleteAsync(table, row), "Delete", table);
        await MaintainedAsync(() => _admin.ExecuteAsync(new AssignRequest
            {
                Target = new EntityReference(table, row), Assignee = new EntityReference("systemuser", _adminId),
            }),
            "Assign", table);

        (await ScalarAsync<int>($"SELECT COUNT(*) FROM data.{table}")).Should().Be(count);
    }

    // ---- SYSTEM user ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task System_user_cannot_be_modified()
    {
        var system = new EntityReference("systemuser", _system);
        var fullName = await ScalarAsync<string>("SELECT fullname FROM data.systemuser WHERE systemuserid = @id", ("@id", _system));
        var team = await CreateTeamAsync(fx, _root);

        await SystemUserAsync(() => _admin.UpdateAsync(new Entity("systemuser", _system) { ["fullname"] = "Not SYSTEM" }));
        await SystemUserAsync(() => _admin.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = system, State = new OptionSetValue(1), Status = new OptionSetValue(2),
        }));
        await SystemUserAsync(() => _admin.DeleteAsync("systemuser", _system));
        await SystemUserAsync(() => _admin.ExecuteAsync(new AssignRequest
        {
            Target = system, Assignee = new EntityReference("systemuser", _adminId),
        }));
        await SystemUserAsync(() => _admin.ExecuteAsync(Associate(system, SecurityAdministration.SystemUserRoles,
            new EntityReference("role", StandardRoles.BasicUser))));
        await SystemUserAsync(() => _admin.ExecuteAsync(Associate(new EntityReference("role", StandardRoles.SystemAdministrator),
            SecurityAdministration.SystemUserRoles, system)));
        await SystemUserAsync(() => _admin.ExecuteAsync(Disassociate(new EntityReference("role", StandardRoles.BasicUser),
            SecurityAdministration.SystemUserRoles, system)));
        await SystemUserAsync(() => _admin.ExecuteAsync(Associate(new EntityReference("team", team),
            SecurityAdministration.TeamMembership, system)));
        // Review I2: the one-to-many relationship of its business unit, with SYSTEM as target or as related record.
        var unit = await CreateBusinessUnitAsync(fx, _root, "SYS-" + Guid.NewGuid().ToString("N")[..8]);
        var unitRef = new EntityReference("businessunit", unit);
        await SystemUserAsync(() => _admin.ExecuteAsync(Associate(system, UserBusinessUnit, unitRef)));
        await SystemUserAsync(() => _admin.ExecuteAsync(Associate(unitRef, UserBusinessUnit, system)));
        await SystemUserAsync(() => _admin.ExecuteAsync(Disassociate(new EntityReference("businessunit", _root), UserBusinessUnit, system)));
        (await ScalarAsync<Guid>("SELECT businessunitid FROM data.systemuser WHERE systemuserid = @id", ("@id", _system)))
            .Should().NotBe(unit);

        (await ScalarAsync<string>("SELECT fullname FROM data.systemuser WHERE systemuserid = @id", ("@id", _system))).Should().Be(fullName);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM data.systemuserroles WHERE systemuserid = @id", ("@id", _system))).Should().Be(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM data.teammembership WHERE systemuserid = @id", ("@id", _system))).Should().Be(0);
    }

    // ---- role template ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Role_template_cannot_be_written()
    {
        var customizer = await UserAsync(StandardRoles.SystemCustomizer);
        var asCustomizer = ServiceFor(fx, customizer.Oid);
        const string message = "The attribute roletemplateid of role is maintained by the system and cannot be set.";

        // The customizer's own role turned into System Administrator.
        await FaultAsync(() => asCustomizer.UpdateAsync(new Entity("role", StandardRoles.SystemCustomizer)
            {
                ["roletemplateid"] = StandardRoles.SystemAdministrator,
            }),
            ErrorCodes.InvalidArgument, 400, message);
        // A new role of the customizer's (assigning it would pass: it has no roleprivileges rows).
        await FaultAsync(() => asCustomizer.CreateAsync(new Entity("role")
            {
                ["name"] = "Forged administrator",
                ["roletemplateid"] = StandardRoles.SystemAdministrator,
            }),
            ErrorCodes.InvalidArgument, 400, message);
        var plain = await asCustomizer.CreateAsync(new Entity("role")
        {
            ["name"] = "Plain role",
        });
        await FaultAsync(() => asCustomizer.UpdateAsync(new Entity("role", plain) { ["roletemplateid"] = StandardRoles.SystemAdministrator }),
            ErrorCodes.InvalidArgument, 400, message);
        // Not even a System Administrator writes it, nor clears it.
        await FaultAsync(() => _admin.UpdateAsync(new Entity("role", plain) { ["roletemplateid"] = StandardRoles.SystemAdministrator }),
            ErrorCodes.InvalidArgument, 400, message);
        await FaultAsync(() => _admin.UpdateAsync(new Entity("role", StandardRoles.SystemCustomizer) { ["roletemplateid"] = null }),
            ErrorCodes.InvalidArgument, 400, message);

        (await ScalarAsync<object>("SELECT roletemplateid FROM data.role WHERE roleid = @id", ("@id", plain))).Should().Be(DBNull.Value);
        (await ScalarAsync<Guid>("SELECT roletemplateid FROM data.role WHERE roleid = @id", ("@id", StandardRoles.SystemCustomizer)))
            .Should().Be(StandardRoles.SystemCustomizer);
        Cache(fx).Invalidate();
        (await Cache(fx).GetProfileAsync(customizer.Id, default)).IsSystemAdministrator.Should().BeFalse();
    }

    // ---- standard roles -----------------------------------------------------------------------------------------------

    private static readonly Guid[] StandardRoleIds =
        [StandardRoles.SystemAdministrator, StandardRoles.SystemCustomizer, StandardRoles.BasicUser, StandardRoles.Delegate];

    [Fact]
    public async Task Standard_roles_cannot_be_deleted()
    {
        var customizer = await UserAsync(StandardRoles.SystemCustomizer);
        foreach (var role in StandardRoleIds)
        {
            await FaultAsync(() => _admin.DeleteAsync("role", role), ErrorCodes.PrivilegeDenied, 403, "Standard roles cannot be deleted.");
            await FaultAsync(() => ServiceFor(fx, customizer.Oid).DeleteAsync("role", role),
                ErrorCodes.PrivilegeDenied, 403, "Standard roles cannot be deleted.");
            (await ScalarAsync<int>("SELECT COUNT(*) FROM data.role WHERE roleid = @id AND roletemplateid = @id", ("@id", role))).Should().Be(1);
        }

        // Any other role can be deleted.
        var plain = await _admin.CreateAsync(new Entity("role") { ["name"] = "Disposable role" });
        await _admin.DeleteAsync("role", plain);
    }

    [Fact]
    public async Task System_Administrator_role_updates_only_name_and_description()
    {
        const string message = "Only the name and description of the System Administrator role can be changed.";
        var original = await ScalarAsync<object>("SELECT description FROM data.role WHERE roleid = @id", ("@id", StandardRoles.SystemAdministrator));
        try
        {
            await _admin.UpdateAsync(new Entity("role", StandardRoles.SystemAdministrator) { ["description"] = "Changed description" });
            (await ScalarAsync<string>("SELECT description FROM data.role WHERE roleid = @id", ("@id", StandardRoles.SystemAdministrator)))
                .Should().Be("Changed description");
        }
        finally
        {
            await ExecAsync("UPDATE data.role SET description = @d WHERE roleid = @id", ("@d", original), ("@id", StandardRoles.SystemAdministrator));
        }

        await FaultAsync(() => _admin.UpdateAsync(new Entity("role", StandardRoles.SystemAdministrator) { ["isinherited"] = new OptionSetValue(0) }),
            ErrorCodes.PrivilegeDenied, 403, message);
        await FaultAsync(() => _admin.UpdateAsync(new Entity("role", StandardRoles.SystemAdministrator)
            {
                ["statecode"] = new OptionSetValue(1), ["statuscode"] = new OptionSetValue(2),
            }),
            ErrorCodes.PrivilegeDenied, 403, message);
        var bu = await CreateBusinessUnitAsync(fx, _root, "sysadmin-move-" + Guid.NewGuid().ToString("N")[..8]);
        await FaultAsync(() => _admin.UpdateAsync(new Entity("role", StandardRoles.SystemAdministrator)
            {
                ["businessunitid"] = new EntityReference("businessunit", bu),
            }),
            ErrorCodes.PrivilegeDenied, 403, message);

        // Other standard roles follow the normal rules.
        var basicDescription = await ScalarAsync<object>("SELECT description FROM data.role WHERE roleid = @id", ("@id", StandardRoles.BasicUser));
        await _admin.UpdateAsync(new Entity("role", StandardRoles.BasicUser) { ["description"] = "Basic", ["isinherited"] = new OptionSetValue(1) });
        await ExecAsync("UPDATE data.role SET description = @d WHERE roleid = @id", ("@d", basicDescription), ("@id", StandardRoles.BasicUser));
    }

    [Fact]
    public async Task Role_with_a_reserved_template_id_cannot_be_created()
    {
        foreach (var role in StandardRoleIds)
        {
            await FaultAsync(() => _admin.CreateAsync(new Entity("role", role) { ["name"] = "Fake standard role" }),
                ErrorCodes.InvalidArgument, 400);
            await FaultAsync(() => _admin.CreateAsync(new Entity("role") { ["roleid"] = role, ["name"] = "Fake standard role" }),
                ErrorCodes.InvalidArgument, 400);
        }
    }

    // ---- impersonating step -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Impersonating_step_requires_act_on_behalf()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(_admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(_admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
        var customizer = await UserAsync(StandardRoles.SystemCustomizer);
        var asCustomizer = ServiceFor(fx, customizer.Oid);

        await ActOnBehalfAsync(() => PluginTestHelper.RegisterStepAsync(asCustomizer, type, "Create", Pipe, 20, rank: 1,
            impersonatingUserId: _adminId));
        // Itself is not another user.
        await PluginTestHelper.RegisterStepAsync(asCustomizer, type, "Create", Pipe, 20, rank: 2, impersonatingUserId: customizer.Id);
        var step = await PluginTestHelper.RegisterStepAsync(asCustomizer, type, "Create", Pipe, 20, rank: 3);
        await ActOnBehalfAsync(() => asCustomizer.UpdateAsync(new Entity("sdkmessageprocessingstep", step)
        {
            ["impersonatinguserid"] = new EntityReference("systemuser", _adminId),
        }));
        // Review C1: the one-to-many Associate of the lookup, from either side.
        var stepRef = new EntityReference("sdkmessageprocessingstep", step);
        var adminRef = new EntityReference("systemuser", _adminId);
        await ActOnBehalfAsync(() => asCustomizer.ExecuteAsync(Associate(stepRef, StepImpersonating, adminRef)));
        await ActOnBehalfAsync(() => asCustomizer.ExecuteAsync(Associate(adminRef, StepImpersonating, stepRef)));
        (await StepImpersonatingAsync(step)).Should().BeNull();

        // Delegate assigned directly.
        await AssignRoleAsync(fx, customizer.Id, StandardRoles.Delegate);
        await PluginTestHelper.RegisterStepAsync(asCustomizer, type, "Create", Pipe, 20, rank: 4, impersonatingUserId: _adminId);
        await asCustomizer.UpdateAsync(new Entity("sdkmessageprocessingstep", step)
        {
            ["impersonatinguserid"] = new EntityReference("systemuser", _adminId),
        });

        // Delegate only through an owner team does not count.
        var teamCustomizer = await UserAsync(StandardRoles.SystemCustomizer);
        var team = await CreateTeamAsync(fx, _root, 0, teamCustomizer.Id);
        await AssignRoleAsync(fx, team, StandardRoles.Delegate, team: true);
        (await Cache(fx).GetProfileAsync(teamCustomizer.Id, default)).DepthOf(PrivilegeNames.ActOnBehalfOfAnotherUser)
            .Should().Be(DepthMask.Global);
        await ActOnBehalfAsync(() => PluginTestHelper.RegisterStepAsync(ServiceFor(fx, teamCustomizer.Oid), type, "Create", Pipe, 20,
            rank: 5, impersonatingUserId: _adminId));

        (await ScalarAsync<int>("SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE impersonatinguserid = @id", ("@id", _adminId)))
            .Should().Be(2);
    }

    /// <summary>
    /// Review minor 4: an Update of a step keeps the impersonating user stored when it does not set one, so changing any
    /// column of a step that impersonates another user needs <c>prvActOnBehalfOfAnotherUser</c> held directly.
    /// </summary>
    [Fact]
    public async Task Changing_an_impersonating_step_requires_act_on_behalf()
    {
        var type = await PluginTypeAsync();
        var other = await PluginTypeAsync("Xrmd.TestPlugins.CreateRecordPlugin");
        var customizer = await UserAsync(StandardRoles.SystemCustomizer);
        var asCustomizer = ServiceFor(fx, customizer.Oid);
        var step = await PluginTestHelper.RegisterStepAsync(_admin, type, "Create", Pipe, 20, rank: 1, impersonatingUserId: _adminId);
        var stepRef = new EntityReference("sdkmessageprocessingstep", step);

        await ActOnBehalfAsync(() => asCustomizer.UpdateAsync(new Entity("sdkmessageprocessingstep", step)
        {
            ["plugintypeid"] = new EntityReference("plugintype", other), ["eventhandler"] = new EntityReference("plugintype", other),
        }));
        await ActOnBehalfAsync(() => asCustomizer.UpdateAsync(new Entity("sdkmessageprocessingstep", step) { ["rank"] = 2 }));
        await ActOnBehalfAsync(() => asCustomizer.ExecuteAsync(Associate(stepRef, StepPluginType, new EntityReference("plugintype", other))));
        (await ScalarAsync<Guid>("SELECT plugintypeid FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", step)))
            .Should().Be(type);

        // Clearing the impersonation needs nothing; a step impersonating the caller itself neither.
        await asCustomizer.UpdateAsync(new Entity("sdkmessageprocessingstep", step) { ["impersonatinguserid"] = null });
        (await StepImpersonatingAsync(step)).Should().BeNull();
        var own = await PluginTestHelper.RegisterStepAsync(asCustomizer, type, "Create", Pipe, 20, rank: 3, impersonatingUserId: customizer.Id);
        await asCustomizer.UpdateAsync(new Entity("sdkmessageprocessingstep", own) { ["rank"] = 4 });

        // A lookup that points to SYSTEM (SYSTEM on the referenced side) is not a change of the SYSTEM row.
        await _admin.ExecuteAsync(Associate(new EntityReference("systemuser", _system), StepImpersonating, stepRef));
        (await StepImpersonatingAsync(step)).Should().Be(_system);
    }

    /// <summary>
    /// With impersonation, a step impersonating another user needs <c>prvActOnBehalfOfAnotherUser</c> held directly by the
    /// impersonator as well as by the caller.
    /// </summary>
    [Fact]
    public async Task Impersonating_step_requires_act_on_behalf_of_the_impersonator_too()
    {
        var customizer = await UserAsync(StandardRoles.SystemCustomizer, StandardRoles.Delegate);
        var withoutDelegate = await UserAsync(StandardRoles.SystemCustomizer);
        var withDelegate = await UserAsync(StandardRoles.SystemCustomizer, StandardRoles.Delegate);
        var create = new CreateRequest
        {
            Target = new Entity("sdkmessageprocessingstep") { ["impersonatinguserid"] = new EntityReference("systemuser", _adminId) },
        };
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var session = new DataSession(conn, null);
        Task EnforceAsync(Guid? impersonator, OrganizationRequest request) =>
            SystemTableRules.EnforceAsync(request, new Caller(customizer.Id, _root, impersonator), _system, Cache(fx), store, session, default);

        await EnforceAsync(null, create);
        await EnforceAsync(withDelegate.Id, create);
        await ActOnBehalfAsync(() => EnforceAsync(withoutDelegate.Id, create));
        // The step impersonates the caller itself: the impersonator still acts on behalf of another user.
        var self = new CreateRequest
        {
            Target = new Entity("sdkmessageprocessingstep") { ["impersonatinguserid"] = new EntityReference("systemuser", customizer.Id) },
        };
        await ActOnBehalfAsync(() => EnforceAsync(withoutDelegate.Id, self));
        // ... but not when the step impersonates the impersonator.
        var impersonatorItself = new CreateRequest
        {
            Target = new Entity("sdkmessageprocessingstep") { ["impersonatinguserid"] = new EntityReference("systemuser", withoutDelegate.Id) },
        };
        await EnforceAsync(withoutDelegate.Id, impersonatorItself);
    }
}
