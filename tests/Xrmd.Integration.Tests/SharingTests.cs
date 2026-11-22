using Xrmd.Abstractions.Storage;
using System.Data.Common;
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
/// GrantAccess, ModifyAccess and RevokeAccess on <c>principalobjectaccess</c>, the shares in the read and write checks, and
/// the shares of a deleted record. Business units R (root) → A and R → B; users uA and uB, each owning one <c>pb_project</c>.
/// </summary>
[Collection("xrmd")]
public sealed class SharingTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Project = TestEntities.Project;
    private const string PipeLog = TestEntities.PipeLog;

    private readonly string _tag = "SH" + Guid.NewGuid().ToString("N");
    private OrganizationService _admin = null!;
    private Guid _a, _b;
    private (Guid Id, Guid Oid) _uA, _uB;
    private Guid _pA, _pB;

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await TestEntities.EnsureProjectAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        _admin = ServiceFor(fx, fx.AdminObjectId);
        var root = await RootBusinessUnitAsync(fx);
        _a = await CreateBusinessUnitAsync(fx, root, "A-" + _tag);
        _b = await CreateBusinessUnitAsync(fx, root, "B-" + _tag);
        _uA = await UserAsync(_a);
        _uB = await UserAsync(_b);
        _pA = await ProjectAsync("A", _uA.Id);
        _pB = await ProjectAsync("B", _uB.Id);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<(Guid Id, Guid Oid)> UserAsync(Guid businessUnit)
    {
        var oid = Guid.NewGuid();
        return (await CreateUserAsync(fx, businessUnit, oid), oid);
    }

    private Task<Guid> ProjectAsync(string label, Guid owner) =>
        _admin.CreateAsync(new Entity(Project) { ["pb_name"] = $"{_tag}-{label}", ["ownerid"] = new EntityReference("systemuser", owner) });

    private Task GrantAsync(Guid principalId, params (string Privilege, DepthMask Depth)[] privileges) =>
        SecurityTestHelper.GrantAsync(fx, principalId, team: false, privileges);

    private OrganizationService As((Guid Id, Guid Oid) user) => ServiceFor(fx, user.Oid);

    private static GrantAccessRequest Grant(Guid record, EntityReference principal, AccessRights mask) => new()
    {
        Target = new EntityReference(Project, record),
        PrincipalAccess = new PrincipalAccess { Principal = principal, AccessMask = mask },
    };

    private static ModifyAccessRequest Modify(Guid record, EntityReference principal, AccessRights mask) => new()
    {
        Target = new EntityReference(Project, record),
        PrincipalAccess = new PrincipalAccess { Principal = principal, AccessMask = mask },
    };

    private static EntityReference User(Guid id) => new("systemuser", id);

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    /// <summary>The share masks of the record with the principal (one entry per row).</summary>
    private async Task<List<int>> MasksAsync(Guid record, Guid principal)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT accessrightsmask FROM data.principalobjectaccess WHERE objectid = @record AND principalid = @principal");
        cmd.Parameters.AddWithValue("@record", record);
        cmd.Parameters.AddWithValue("@principal", principal);
        var masks = new List<int>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) masks.Add(rd.GetInt32(0));
        return masks;
    }

    private static Task AccessDeniedAsync(Func<Task> act, AccessRights right, Guid userId, Guid recordId) =>
        act.Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.AccessDenied && e.HttpStatus == 403
                        && e.Message == $"Principal user with id {userId} does not have {right} right(s) for record with id {recordId} of entity {Project}.");

    private static Task InvalidArgumentAsync(Func<Task> act, string message) =>
        act.Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400 && e.Message == message);

    // ---- shares in the checks ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Grant_read_makes_record_readable_and_write_needs_write()
    {
        await GrantAsync(_uB.Id, ("prvReadpb_Project", DepthMask.Basic), ("prvWritepb_Project", DepthMask.Basic));
        await AccessDeniedAsync(() => As(_uB).RetrieveAsync(Project, _pA, new ColumnSet("pb_name")), AccessRights.ReadAccess, _uB.Id, _pA);

        await _admin.ExecuteAsync(Grant(_pA, User(_uB.Id), AccessRights.ReadAccess));

        (await As(_uB).RetrieveAsync(Project, _pA, new ColumnSet("pb_name"))).GetAttributeValue<string>("pb_name").Should().Be($"{_tag}-A");
        var visible = await As(_uB).RetrieveMultipleAsync(new QueryExpression(Project)
        {
            Criteria = { Conditions = { new ConditionExpression("pb_name", ConditionOperator.BeginsWith, _tag) } },
        });
        visible.Entities.Select(e => e.Id).Should().BeEquivalentTo([_pA, _pB]);
        await AccessDeniedAsync(() => As(_uB).UpdateAsync(new Entity(Project, _pA) { ["pb_count"] = 1 }), AccessRights.WriteAccess, _uB.Id, _pA);

        await _admin.ExecuteAsync(Grant(_pA, User(_uB.Id), AccessRights.WriteAccess));

        await As(_uB).UpdateAsync(new Entity(Project, _pA) { ["pb_count"] = 1 });
        (await ScalarAsync($"SELECT pb_count FROM data.{Project} WHERE {Project}id = @id", ("@id", _pA))).Should().Be(1);
    }

    /// <summary>The share path of the write checks: a Write share alone (no Read share) opens a record the depth denies.</summary>
    [Fact]
    public async Task Write_share_makes_record_denied_by_depth_writable()
    {
        // prvRead: the principal of a share must hold it; at Basic it does not reach _pA.
        await GrantAsync(_uB.Id, ("prvReadpb_Project", DepthMask.Basic), ("prvWritepb_Project", DepthMask.Basic),
            ("prvDeletepb_Project", DepthMask.Basic));
        await AccessDeniedAsync(() => As(_uB).UpdateAsync(new Entity(Project, _pA) { ["pb_count"] = 2 }), AccessRights.WriteAccess, _uB.Id, _pA);

        await _admin.ExecuteAsync(Grant(_pA, User(_uB.Id), AccessRights.WriteAccess));

        await As(_uB).UpdateAsync(new Entity(Project, _pA) { ["pb_count"] = 2 });
        (await ScalarAsync($"SELECT pb_count FROM data.{Project} WHERE {Project}id = @id", ("@id", _pA))).Should().Be(2);
        // A share opens only its rights.
        await AccessDeniedAsync(() => As(_uB).DeleteAsync(Project, _pA), AccessRights.DeleteAccess, _uB.Id, _pA);
    }

    [Fact]
    public async Task Grant_to_team_shares_with_members()
    {
        // An owner team: its roles hold for its members.
        var team = await CreateTeamAsync(fx, _a, teamType: 0, _uB.Id);
        await SecurityTestHelper.GrantAsync(fx, team, team: true, ("prvReadpb_Project", DepthMask.Basic));
        var outsider = await UserAsync(_b);
        await GrantAsync(outsider.Id, ("prvReadpb_Project", DepthMask.Basic));

        await _admin.ExecuteAsync(Grant(_pA, new EntityReference("team", team), AccessRights.ReadAccess));

        (await ScalarAsync("SELECT principaltypecode FROM data.principalobjectaccess WHERE objectid = @id AND principalid = @team",
            ("@id", _pA), ("@team", team))).Should().Be(9);
        // The team's role gives prvRead to its members, the share gives the record.
        (await As(_uB).RetrieveAsync(Project, _pA, new ColumnSet("pb_name"))).Id.Should().Be(_pA);
        await AccessDeniedAsync(() => As(outsider).RetrieveAsync(Project, _pA, new ColumnSet("pb_name")), AccessRights.ReadAccess, outsider.Id, _pA);
    }

    // ---- the messages -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Grant_merges_Modify_replaces_Revoke_removes()
    {
        await GrantAsync(_uB.Id, ("prvReadpb_Project", DepthMask.Basic));
        var uB = User(_uB.Id);

        // ModifyAccess without a share creates it.
        var modified = await _admin.ExecuteAsync(Modify(_pA, uB, AccessRights.AppendAccess));
        modified.Should().BeOfType<ModifyAccessResponse>();
        (await MasksAsync(_pA, _uB.Id)).Should().Equal((int)AccessRights.AppendAccess);
        (await ScalarAsync("SELECT principaltypecode FROM data.principalobjectaccess WHERE objectid = @id", ("@id", _pA))).Should().Be(8);
        (await ScalarAsync($"SELECT objecttypecode FROM data.principalobjectaccess WHERE objectid = @id", ("@id", _pA)))
            .Should().Be(fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(Project)!.ObjectTypeCode);

        var granted = await _admin.ExecuteAsync(Grant(_pA, uB, AccessRights.ReadAccess | AccessRights.WriteAccess));
        granted.Should().BeOfType<GrantAccessResponse>();
        (await MasksAsync(_pA, _uB.Id)).Should().Equal((int)(AccessRights.AppendAccess | AccessRights.ReadAccess | AccessRights.WriteAccess));

        await _admin.ExecuteAsync(Grant(_pA, uB, AccessRights.ReadAccess));
        (await MasksAsync(_pA, _uB.Id)).Should().Equal((int)(AccessRights.AppendAccess | AccessRights.ReadAccess | AccessRights.WriteAccess));

        await _admin.ExecuteAsync(Modify(_pA, uB, AccessRights.DeleteAccess | AccessRights.ShareAccess));
        (await MasksAsync(_pA, _uB.Id)).Should().Equal((int)(AccessRights.DeleteAccess | AccessRights.ShareAccess));

        var revoked = await _admin.ExecuteAsync(new RevokeAccessRequest { Target = new EntityReference(Project, _pA), Revokee = uB });
        revoked.Should().BeOfType<RevokeAccessResponse>();
        (await MasksAsync(_pA, _uB.Id)).Should().BeEmpty();

        // Revoking a share that does not exist is not an error.
        await _admin.ExecuteAsync(new RevokeAccessRequest { Target = new EntityReference(Project, _pA), Revokee = uB });
    }

    [Fact]
    public async Task Share_requires_share_access_and_cannot_exceed_callers_rights()
    {
        await GrantAsync(_uB.Id, ("prvReadpb_Project", DepthMask.Basic));
        await GrantAsync(_uA.Id, ("prvReadpb_Project", DepthMask.Basic));
        var uB = User(_uB.Id);

        await FluentActions.Awaiting(() => As(_uA).ExecuteAsync(Grant(_pA, uB, AccessRights.ReadAccess)))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.PrivilegeDenied && e.HttpStatus == 403 && e.Message.Contains("prvSharepb_Project"));
        await FluentActions.Awaiting(() => As(_uA).ExecuteAsync(new RevokeAccessRequest { Target = new EntityReference(Project, _pA), Revokee = uB }))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.PrivilegeDenied && e.Message.Contains("prvSharepb_Project"));

        await GrantAsync(_uA.Id, ("prvSharepb_Project", DepthMask.Basic));
        // uA has no Write on its own record: it cannot give it.
        await AccessDeniedAsync(() => As(_uA).ExecuteAsync(Grant(_pA, uB, AccessRights.ReadAccess | AccessRights.WriteAccess)),
            AccessRights.WriteAccess, _uA.Id, _pA);
        await AccessDeniedAsync(() => As(_uA).ExecuteAsync(Modify(_pA, uB, AccessRights.AssignAccess)), AccessRights.AssignAccess, _uA.Id, _pA);
        (await MasksAsync(_pA, _uB.Id)).Should().BeEmpty();
        // Share at Basic does not reach a record of another user.
        await AccessDeniedAsync(() => As(_uA).ExecuteAsync(Grant(_pB, User(_uA.Id), AccessRights.ReadAccess)), AccessRights.ShareAccess, _uA.Id, _pB);

        await As(_uA).ExecuteAsync(Grant(_pA, uB, AccessRights.ReadAccess));
        (await MasksAsync(_pA, _uB.Id)).Should().Equal((int)AccessRights.ReadAccess);

        // Rights the caller holds through a share count too: uB re-shares the Read it was given.
        await GrantAsync(_uB.Id, ("prvSharepb_Project", DepthMask.Basic));
        await _admin.ExecuteAsync(Grant(_pA, uB, AccessRights.ShareAccess));
        var third = await UserAsync(_b);
        await GrantAsync(third.Id, ("prvReadpb_Project", DepthMask.Basic));
        await As(_uB).ExecuteAsync(Grant(_pA, User(third.Id), AccessRights.ReadAccess));
        (await MasksAsync(_pA, third.Id)).Should().Equal((int)AccessRights.ReadAccess);

        await As(_uA).ExecuteAsync(new RevokeAccessRequest { Target = new EntityReference(Project, _pA), Revokee = uB });
        (await MasksAsync(_pA, _uB.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Principal_without_read_privilege_is_0x80040231()
    {
        var team = await CreateTeamAsync(fx, _b, teamType: 1);
        foreach (var principal in new[] { User(_uB.Id), new EntityReference("team", team) })
        {
            await FluentActions.Awaiting(() => _admin.ExecuteAsync(Grant(_pA, principal, AccessRights.ReadAccess)))
                .Should().ThrowAsync<XrmdFaultException>()
                .Where(e => e.ErrorCode == ErrorCodes.PrincipalPrivilegeDenied && e.HttpStatus == 403
                            && e.Message == "The target user or team lacks a privilege the operation needs.");
            await FluentActions.Awaiting(() => _admin.ExecuteAsync(Modify(_pA, principal, AccessRights.ReadAccess)))
                .Should().ThrowAsync<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.PrincipalPrivilegeDenied);
            (await MasksAsync(_pA, principal.Id)).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Organization_owned_entity_cannot_be_shared()
    {
        await GrantAsync(_uB.Id, ("prvReadpb_Project", DepthMask.Basic));
        var message = new EntityReference("sdkmessage", SdkMessageCatalog.MessageId("Create"));

        await InvalidArgumentAsync(() => _admin.ExecuteAsync(new GrantAccessRequest
        {
            Target = message, PrincipalAccess = new PrincipalAccess { Principal = User(_uB.Id), AccessMask = AccessRights.ReadAccess },
        }), "Entity 'sdkmessage' is not user or team owned and cannot be shared.");
        await InvalidArgumentAsync(() => _admin.ExecuteAsync(new RevokeAccessRequest { Target = message, Revokee = User(_uB.Id) }),
            "Entity 'sdkmessage' is not user or team owned and cannot be shared.");
    }

    [Fact]
    public async Task Invalid_mask_is_400()
    {
        await GrantAsync(_uB.Id, ("prvReadpb_Project", DepthMask.Basic));
        await GrantAsync(_uA.Id, ("prvReadpb_Project", DepthMask.Basic), ("prvSharepb_Project", DepthMask.Basic));
        var uB = User(_uB.Id);

        await InvalidArgumentAsync(() => _admin.ExecuteAsync(Grant(_pA, uB, AccessRights.CreateAccess)), "Invalid access mask CreateAccess.");
        await InvalidArgumentAsync(() => _admin.ExecuteAsync(Modify(_pA, uB, AccessRights.None)), "Invalid access mask None.");
        // The guard leaves an invalid mask to the core operation: 400, not 403.
        await InvalidArgumentAsync(() => As(_uA).ExecuteAsync(Grant(_pA, uB, AccessRights.ReadAccess | AccessRights.CreateAccess)),
            "Invalid access mask ReadAccess, CreateAccess.");
        (await MasksAsync(_pA, _uB.Id)).Should().BeEmpty();

        // Principals of another type and missing records.
        await InvalidArgumentAsync(() => _admin.ExecuteAsync(Grant(_pA, new EntityReference("businessunit", _a), AccessRights.ReadAccess)),
            "Principal of type 'businessunit' is not supported: a share must name a systemuser or a team.");
        await FluentActions.Awaiting(() => _admin.ExecuteAsync(Grant(Guid.NewGuid(), uB, AccessRights.ReadAccess)))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist && e.HttpStatus == 404);
        await FluentActions.Awaiting(() => _admin.ExecuteAsync(Grant(_pA, User(Guid.NewGuid()), AccessRights.ReadAccess)))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist && e.HttpStatus == 404);
    }

    [Fact]
    public async Task Concurrent_grants_keep_one_row()
    {
        await GrantAsync(_uB.Id, ("prvReadpb_Project", DepthMask.Basic));

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => ServiceFor(fx, fx.AdminObjectId)
            .ExecuteAsync(Grant(_pA, User(_uB.Id), i % 2 == 0 ? AccessRights.ReadAccess : AccessRights.WriteAccess)))));

        (await MasksAsync(_pA, _uB.Id)).Should().Equal((int)(AccessRights.ReadAccess | AccessRights.WriteAccess));
    }

    [Fact]
    public async Task Deleting_record_removes_its_shares()
    {
        await GrantAsync(_uB.Id, ("prvReadpb_Project", DepthMask.Basic));
        var team = await CreateTeamAsync(fx, _b, teamType: 1);
        await SecurityTestHelper.GrantAsync(fx, team, team: true, ("prvReadpb_Project", DepthMask.Basic));
        await _admin.ExecuteAsync(Grant(_pA, User(_uB.Id), AccessRights.ReadAccess));
        await _admin.ExecuteAsync(Grant(_pA, new EntityReference("team", team), AccessRights.ReadAccess));
        await GrantAsync(_uA.Id, ("prvReadpb_Project", DepthMask.Basic));
        await _admin.ExecuteAsync(Grant(_pB, User(_uA.Id), AccessRights.ReadAccess));

        await _admin.DeleteAsync(Project, _pA);

        (await ScalarAsync("SELECT COUNT(*) FROM data.principalobjectaccess WHERE objectid = @id", ("@id", _pA))).Should().Be(0);
        (await MasksAsync(_pB, _uA.Id)).Should().Equal((int)AccessRights.ReadAccess);
    }

    [Fact]
    public async Task Plugins_fire_on_GrantAccess()
    {
        await GrantAsync(_uB.Id, ("prvReadpb_Project", DepthMask.Basic));
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(_admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(_admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
        await PluginTestHelper.RegisterStepAsync(_admin, type, "GrantAccess", Project, 40, config: PipeLog);
        var admin = (Guid)(await ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid",
            ("@oid", fx.AdminObjectId)))!;
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _admin.ExecuteAsync(Grant(_pA, User(_uB.Id), AccessRights.ReadAccess));

        (await ScalarAsync($"SELECT COUNT(*) FROM data.{PipeLog} WHERE pb_name LIKE 'GrantAccess|40|%' AND createdby = @admin AND createdon >= @before",
            ("@admin", admin), ("@before", before))).Should().Be(1);
    }
}
