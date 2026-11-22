using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.16, task 2: the writes of access team templates (ruling A2), their delete cascade (A3), the rules of system-managed
/// teams (A4, <see cref="Caller.Internal"/> exempt) and the unique index <c>UX_team_record_template</c>.
/// </summary>
[Collection("xrmd")]
public sealed class TeamTemplateRulesTests(XrmdFixture fx)
{
    private const int Read = 1, Write = 2;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A new user-owned table, with <c>AutoCreateAccessTeams</c> when <paramref name="enabled"/>; returns its name and OTC.</summary>
    private async Task<(string Name, int Code)> NewTableAsync(string label, bool enabled = true)
    {
        var name = $"new_{label}{_run}";
        var entity = TestHelpers.Entity(name, name, "Table " + name);
        entity.AutoCreateAccessTeams = enabled;
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        });
        return (name, Store.GetEntity(name)!.ObjectTypeCode!.Value);
    }

    /// <summary>One enabled table shared by the tests that only need some enabled table (each removes its templates).</summary>
    private static Task<(string Name, int Code)>? _shared;
    private static readonly object SharedLock = new();

    private Task<(string Name, int Code)> SharedTableAsync()
    {
        lock (SharedLock) return _shared ??= NewTableAsync("atshared");
    }

    private Task SetFlagAsync(string name, bool value) =>
        Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = name, AutoCreateAccessTeams = value } });

    private Entity Template(int code, int mask, string? label = null) => new("teamtemplate")
    {
        ["teamtemplatename"] = (label ?? "Template") + " " + _run,
        ["objecttypecode"] = code,
        ["defaultaccessrightsmask"] = mask,
    };

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var value1 = await cmd.ExecuteScalarAsync();
        return value1 is DBNull ? null : value1;
    }

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt32(await ScalarAsync(sql, parameters));

    private Task<int> TemplatesOnAsync(int code) =>
        CountAsync("SELECT COUNT(*) FROM data.teamtemplate WHERE objecttypecode = @otc", ("@otc", code));

    private Task DeleteTemplatesOnAsync(int code) =>
        ScalarAsync("DELETE FROM data.team WHERE teamtemplateid IN (SELECT teamtemplateid FROM data.teamtemplate WHERE objecttypecode = @otc); " +
                    "DELETE FROM data.teamtemplate WHERE objecttypecode = @otc", ("@otc", code));

    /// <summary>Runs <paramref name="request"/> through the pipeline as <see cref="Caller.Internal"/> (as Task 3's messages will).</summary>
    private async Task<OrganizationResponse> InternalAsync(OrganizationRequest request) =>
        await TestServices.NewPipelineExecutor(fx.App.Services)
            .ExecuteAsync(request, new InvocationContext(Caller.Internal, Guid.Empty, 1, null, null, Guid.NewGuid()), default);

    /// <summary>A system-managed team for <paramref name="record"/> and <paramref name="template"/>, created as <see cref="Caller.Internal"/>.</summary>
    private async Task<Guid> SystemManagedTeamAsync(EntityReference record, Guid template)
    {
        var root = await RootBusinessUnitAsync(fx);
        var response = await InternalAsync(new CreateRequest
        {
            Target = new Entity("team")
            {
                ["name"] = $"{record.Id:D}+{template:D}",
                ["teamtype"] = new OptionSetValue(1),
                ["businessunitid"] = new EntityReference("businessunit", root),
                ["systemmanaged"] = true,
                ["regardingobjectid"] = record,
                ["teamtemplateid"] = new EntityReference("teamtemplate", template),
            },
        });
        return (Guid)response.Results["id"];
    }

    private async Task<EntityReference> NewRecordAsync(string table) =>
        new(table, await Admin.CreateAsync(new Entity(table) { ["new_name"] = "Record " + _run }));

    private static async Task<string> Refused400Async(Func<Task> call)
    {
        var fault = await FaultAsync(call);
        fault.HttpStatus.Should().Be(400, fault.ToString());
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        return fault.Message;
    }

    // ---- A2: template writes ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Template_on_disabled_table_is_refused()
    {
        var (_, disabled) = await NewTableAsync("atoff", enabled: false);
        (await Refused400Async(() => Admin.CreateAsync(Template(disabled, Read)))).Should().Contain("AutoCreateAccessTeams");
        (await TemplatesOnAsync(disabled)).Should().Be(0);
        // A table code that names no table is refused too.
        await Refused400Async(() => Admin.CreateAsync(Template(987654, Read)));
        // So is a template without a table.
        var noTable = Template(disabled, Read);
        noTable.Attributes.Remove("objecttypecode");
        await Refused400Async(() => Admin.CreateAsync(noTable));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(8, false)]
    [InlineData(32, false)]
    [InlineData(1048576, false)]
    [InlineData(1, true)]
    [InlineData(1 | 2, true)]
    [InlineData(1 | 2 | 4 | 16 | 65536 | 262144 | 524288, true)]
    public async Task Template_mask_must_be_valid(int mask, bool valid)
    {
        AccessTeamRules.MaskIsValid(mask).Should().Be(valid);
        var (_, code) = await SharedTableAsync();
        if (!valid)
        {
            (await Refused400Async(() => Admin.CreateAsync(Template(code, mask, "Mask" + mask)))).Should().Contain("defaultaccessrightsmask");
            return;
        }
        var id = await Admin.CreateAsync(Template(code, mask, "Mask" + mask));
        try
        {
            (await ScalarAsync("SELECT defaultaccessrightsmask FROM data.teamtemplate WHERE teamtemplateid = @id", ("@id", id))).Should().Be(mask);
            // The same rule holds on Update.
            await Refused400Async(() => Admin.UpdateAsync(new Entity("teamtemplate", id) { ["defaultaccessrightsmask"] = 8 }));
            await Admin.UpdateAsync(new Entity("teamtemplate", id) { ["defaultaccessrightsmask"] = Read | Write });
        }
        finally
        {
            await Admin.DeleteAsync("teamtemplate", id);
        }
    }

    [Fact]
    public async Task Fifth_template_is_refused()
    {
        AccessTeamRules.MaxTemplatesPerTable.Should().Be(4);
        AccessTeamRules.MaxTables.Should().Be(AccessTeamSchema.MaxTables).And.Be(100);
        var (_, code) = await NewTableAsync("atfive");
        for (var i = 0; i < 4; i++) await Admin.CreateAsync(Template(code, Read, "T" + i));
        (await Refused400Async(() => Admin.CreateAsync(Template(code, Read, "T5")))).Should().Contain("4");
        (await TemplatesOnAsync(code)).Should().Be(4);
    }

    [Fact]
    public async Task Template_table_cannot_change()
    {
        var (_, code) = await SharedTableAsync();
        var (_, other) = await NewTableAsync("atother");
        var id = await Admin.CreateAsync(Template(code, Read));
        try
        {
            (await Refused400Async(() => Admin.UpdateAsync(new Entity("teamtemplate", id) { ["objecttypecode"] = other })))
                .Should().Contain("objecttypecode");
            // The stored value sent back is not a change.
            await Admin.UpdateAsync(new Entity("teamtemplate", id) { ["objecttypecode"] = code, ["description"] = "Same table" });
            (await ScalarAsync("SELECT objecttypecode FROM data.teamtemplate WHERE teamtemplateid = @id", ("@id", id))).Should().Be(code);
        }
        finally
        {
            await Admin.DeleteAsync("teamtemplate", id);
        }
    }

    [Fact]
    public async Task Issystem_is_not_writable()
    {
        var (_, code) = await SharedTableAsync();
        foreach (var value in new object?[] { true, false, null })
        {
            var template = Template(code, Read);
            template["issystem"] = value;
            (await Refused400Async(() => Admin.CreateAsync(template))).Should().Contain("issystem");
        }
        var id = await Admin.CreateAsync(Template(code, Read));
        try
        {
            // Written explicitly false: xrmverse does not apply column defaults on insert.
            (await ScalarAsync("SELECT issystem FROM data.teamtemplate WHERE teamtemplateid = @id", ("@id", id))).Should().Be(false);
            (await Refused400Async(() => Admin.UpdateAsync(new Entity("teamtemplate", id) { ["issystem"] = true }))).Should().Contain("issystem");
        }
        finally
        {
            await Admin.DeleteAsync("teamtemplate", id);
        }
    }

    // ---- A4: system-managed teams -------------------------------------------------------------------------------------

    [Fact]
    public async Task System_managed_columns_cannot_be_written()
    {
        var (table, code) = await SharedTableAsync();
        var record = await NewRecordAsync(table);
        var template = await Admin.CreateAsync(Template(code, Read));
        try
        {
            var columns = new (string Column, object Value)[]
            {
                ("teamtemplateid", new EntityReference("teamtemplate", template)), ("regardingobjectid", record), ("systemmanaged", true),
            };
            // Create.
            foreach (var (column, value) in columns)
                (await Refused400Async(() => Admin.CreateAsync(new Entity("team") { ["name"] = "Team " + _run, [column] = value })))
                    .Should().Contain(column);

            // A normal team: systemmanaged is written false.
            var team = await Admin.CreateAsync(new Entity("team") { ["name"] = "Normal " + _run, ["teamtype"] = new OptionSetValue(1) });
            (await ScalarAsync("SELECT systemmanaged FROM data.team WHERE teamid = @id", ("@id", team))).Should().Be(false);

            // Update.
            foreach (var (column, value) in columns)
                (await Refused400Async(() => Admin.UpdateAsync(new Entity("team", team) { [column] = value }))).Should().Contain(column);
            // The stored value sent back is not a change.
            await Admin.UpdateAsync(new Entity("team", team) { ["systemmanaged"] = false, ["description"] = "Same" });
            // Associate over the template lookup is its Update.
            await Refused400Async(() => Admin.ExecuteAsync(new AssociateRequest
            {
                Target = new EntityReference("teamtemplate", template), Relationship = new Relationship("teamtemplate_Teams"),
                RelatedEntities = [new EntityReference("team", team)],
            }));

            // The internal caller (AddUserToRecordTeam, task 3) creates and updates them.
            var managed = await SystemManagedTeamAsync(record, template);
            (await ScalarAsync("SELECT systemmanaged FROM data.team WHERE teamid = @id", ("@id", managed))).Should().Be(true);
            await InternalAsync(new UpdateRequest { Target = new Entity("team", managed) { ["systemmanaged"] = true } });
            // A system-managed team can be deleted with the normal team privilege.
            await Admin.DeleteAsync("team", managed);
            await Admin.DeleteAsync("team", team);
        }
        finally
        {
            await Admin.DeleteAsync("teamtemplate", template);
        }
    }

    [Fact]
    public async Task AddMembersTeam_on_system_managed_team_is_refused()
    {
        var (table, code) = await SharedTableAsync();
        var record = await NewRecordAsync(table);
        var template = await Admin.CreateAsync(Template(code, Read));
        try
        {
            var managed = await SystemManagedTeamAsync(record, template);
            var normal = await Admin.CreateAsync(new Entity("team") { ["name"] = "Normal " + _run });
            var cache = Cache(fx);
            var system = await cache.SystemUserIdAsync(default);
            var admin = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
            var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            var session = new DataSession(conn, null);

            foreach (var name in new[] { "AddMembersTeam", "RemoveMembersTeam" })
            {
                OrganizationRequest Request(Guid team) => new(name) { ["TeamId"] = team, ["MemberIds"] = new[] { user } };
                (await Refused400Async(() => SystemTableRules.EnforceAsync(Request(managed), admin, system, cache, Store, session, default)))
                    .Should().Contain("AddUserToRecordTeam").And.Contain("RemoveUserFromRecordTeam");
                await SystemTableRules.EnforceAsync(Request(normal), admin, system, cache, Store, session, default);
                await SystemTableRules.EnforceAsync(Request(managed), Caller.Internal, system, cache, Store, session, default);
            }
            await Admin.DeleteAsync("team", normal);
        }
        finally
        {
            await Admin.DeleteAsync("teamtemplate", template);
        }
    }

    [Fact]
    public async Task Associate_membership_on_system_managed_team_is_refused()
    {
        var (table, code) = await SharedTableAsync();
        var record = await NewRecordAsync(table);
        var template = await Admin.CreateAsync(Template(code, Read));
        try
        {
            var managed = await SystemManagedTeamAsync(record, template);
            var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
            var membership = new Relationship(SecurityAdministration.TeamMembership);
            AssociateRequest Associate(EntityReference target, EntityReference related) =>
                new() { Target = target, Relationship = membership, RelatedEntities = [related] };
            DisassociateRequest Disassociate(EntityReference target, EntityReference related) =>
                new() { Target = target, Relationship = membership, RelatedEntities = [related] };
            var teamRef = new EntityReference("team", managed);
            var userRef = new EntityReference("systemuser", user);

            // From either side.
            (await Refused400Async(() => Admin.ExecuteAsync(Associate(teamRef, userRef)))).Should().Contain("AddUserToRecordTeam");
            (await Refused400Async(() => Admin.ExecuteAsync(Associate(userRef, teamRef)))).Should().Contain("AddUserToRecordTeam");
            (await CountAsync("SELECT COUNT(*) FROM data.teammembership WHERE teamid = @id", ("@id", managed))).Should().Be(0);

            // The internal caller adds and removes members.
            await InternalAsync(Associate(teamRef, userRef));
            (await CountAsync("SELECT COUNT(*) FROM data.teammembership WHERE teamid = @id", ("@id", managed))).Should().Be(1);
            (await Refused400Async(() => Admin.ExecuteAsync(Disassociate(teamRef, userRef)))).Should().Contain("RemoveUserFromRecordTeam");
            await InternalAsync(Disassociate(teamRef, userRef));
            (await CountAsync("SELECT COUNT(*) FROM data.teammembership WHERE teamid = @id", ("@id", managed))).Should().Be(0);

            // A normal team is not concerned.
            var normal = await Admin.CreateAsync(new Entity("team") { ["name"] = "Normal " + _run });
            await Admin.ExecuteAsync(Associate(new EntityReference("team", normal), userRef));
            await Admin.ExecuteAsync(Disassociate(new EntityReference("team", normal), userRef));
            await Admin.DeleteAsync("team", normal);
        }
        finally
        {
            await Admin.DeleteAsync("teamtemplate", template);
        }
    }

    // ---- A3: template delete ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Template_delete_cascades_teams_and_poa()
    {
        var (table, code) = await SharedTableAsync();
        var record = await NewRecordAsync(table);
        var template = await Admin.CreateAsync(Template(code, Read | Write));
        var team = await SystemManagedTeamAsync(record, template);
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        await ScalarAsync("""
            INSERT INTO data.teammembership (teammembershipid, teamid, systemuserid, createdon, modifiedon, statecode, statuscode)
            VALUES (NEWID(), @team, @user, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1);
            INSERT INTO data.principalobjectaccess (principalobjectaccessid, principalid, principaltypecode, objectid, objecttypecode,
                accessrightsmask, inheritedaccessrightsmask)
            VALUES (NEWID(), @team, 9, @record, @otc, 3, 0);
            """, ("@team", team), ("@user", user), ("@record", record.Id), ("@otc", code));

        await Admin.DeleteAsync("teamtemplate", template);

        (await CountAsync("SELECT COUNT(*) FROM data.team WHERE teamid = @id", ("@id", team))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.teammembership WHERE teamid = @id", ("@id", team))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.principalobjectaccess WHERE principalid = @id", ("@id", team))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data." + table + " WHERE " + table + "id = @id", ("@id", record.Id))).Should().Be(1);
    }

    [Fact]
    public async Task Unique_index_rejects_second_team_for_record_template()
    {
        var index = await TestSql.IndexAsync(fx.ConnectionString, "data", "team", "UX_team_record_template");
        (index is { Unique: true, Filtered: true }).Should().BeTrue();
        var (table, code) = await SharedTableAsync();
        var record = await NewRecordAsync(table);
        var template = await Admin.CreateAsync(Template(code, Read));
        try
        {
            await SystemManagedTeamAsync(record, template);
            var duplicate = await FaultAsync(() => SystemManagedTeamAsync(record, template));
            (duplicate.ErrorCode, duplicate.HttpStatus).Should().Be((ErrorCodes.DuplicateRecord, 412), "the core's DuplicateUniqueValue fault");
            (await CountAsync("SELECT COUNT(*) FROM data.team WHERE regardingobjectid = @r AND teamtemplateid = @t", ("@r", record.Id), ("@t", template)))
                .Should().Be(1);
            // Teams without a template are not concerned.
            await Admin.CreateAsync(new Entity("team") { ["name"] = "A " + _run });
            await Admin.CreateAsync(new Entity("team") { ["name"] = "B " + _run });
        }
        finally
        {
            await Admin.DeleteAsync("teamtemplate", template);
        }
    }

    // ---- the race of disabling a table and creating a template --------------------------------------------------------

    [Fact]
    public async Task Disable_and_template_create_serialize()
    {
        var (table, code) = await NewTableAsync("atrace");
        var created = 0;
        var disabled = 0;
        for (var i = 0; i < 30; i++)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disable = Task.Run(async () => { await start.Task; await SetFlagAsync(table, false); });
            var create = Task.Run(async () => { await start.Task; await Admin.CreateAsync(Template(code, Read, "Race" + i)); });
            start.SetResult();
            foreach (var task in new[] { disable, create })
            {
                try
                {
                    await task;
                }
                catch (Exception ex) when (ex is XrmdFaultException { HttpStatus: 400 } or XrmdValidationException)
                {
                    // One of the two may lose: its 400 is expected.
                }
            }

            var enabled = Store.GetEntity(table)!.AutoCreateAccessTeams == true;
            var templates = await TemplatesOnAsync(code);
            (!enabled && templates > 0).Should().BeFalse($"iteration {i}: a template exists on a disabled table");
            if (templates > 0) created++;
            if (!enabled) disabled++;

            await DeleteTemplatesOnAsync(code);
            if (!enabled) await SetFlagAsync(table, true);
        }
        (created + disabled).Should().BeGreaterThan(0);
    }
}
