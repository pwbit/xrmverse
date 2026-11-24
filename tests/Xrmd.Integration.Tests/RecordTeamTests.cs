using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Data;
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
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Audit;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.16, task 3: AddUserToRecordTeam and RemoveUserFromRecordTeam (rulings A6–A9). Business units R (root) → A and R → B;
/// the owner of the records is in A, the members are in B with Basic read/write on <c>account</c>, so they never reach the
/// owner's records through their roles. <c>account</c> gets <c>AutoCreateAccessTeams</c> for each test and loses it after.
/// </summary>
[Collection("xrmd")]
public sealed class RecordTeamTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Account = "account";
    private const string Api = "/api/data/v9.2/";
    private const string FileColumn = "new_doc";
    private const AccessRights Read = AccessRights.ReadAccess;
    private const AccessRights Write = AccessRights.WriteAccess;

    private readonly string _tag = "RT" + Guid.NewGuid().ToString("N")[..10];
    private readonly List<Guid> _templates = [];
    private Guid _root, _a, _b;
    private (Guid Id, Guid Oid) _owner;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        if (Store.GetEntity(Account)!.AutoCreateAccessTeams != true) await SetFlagAsync(Account, true);
        _root = await RootBusinessUnitAsync(fx);
        _a = await CreateBusinessUnitAsync(fx, _root, "A-" + _tag);
        _b = await CreateBusinessUnitAsync(fx, _root, "B-" + _tag);
        _owner = await UserAsync(_a, ("prvReadAccount", DepthMask.Basic), ("prvWriteAccount", DepthMask.Basic),
            ("prvShareAccount", DepthMask.Basic), ("prvAppendAccount", DepthMask.Basic), ("prvAppendToAccount", DepthMask.Basic),
            ("prvDeleteAccount", DepthMask.Basic), ("prvAssignAccount", DepthMask.Basic));
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        foreach (var template in _templates)
            if (await CountAsync("SELECT COUNT(*) FROM data.teamtemplate WHERE teamtemplateid = @id", ("@id", template)) > 0)
                await Admin.DeleteAsync("teamtemplate", template);
        if (await CountAsync("SELECT COUNT(*) FROM data.teamtemplate WHERE objecttypecode = @otc", ("@otc", AccountCode)) == 0)
            await SetFlagAsync(Account, false);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private int AccountCode => Store.GetEntity(Account)!.ObjectTypeCode!.Value;

    private Task SetFlagAsync(string table, bool value) =>
        Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = table, AutoCreateAccessTeams = value } });

    private async Task<(Guid Id, Guid Oid)> UserAsync(Guid businessUnit, params (string Privilege, DepthMask Depth)[] privileges)
    {
        var oid = Guid.NewGuid();
        var id = await CreateUserAsync(fx, businessUnit, oid);
        if (privileges.Length > 0) await GrantAsync(fx, id, team: false, privileges);
        return (id, oid);
    }

    /// <summary>A member in B: Basic read/write on account.</summary>
    private Task<(Guid Id, Guid Oid)> MemberAsync(params (string Privilege, DepthMask Depth)[] more) =>
        UserAsync(_b, [("prvReadAccount", DepthMask.Basic), ("prvWriteAccount", DepthMask.Basic), .. more]);

    private OrganizationService As((Guid Id, Guid Oid) user) => ServiceFor(fx, user.Oid);

    private async Task<Guid> TemplateAsync(AccessRights mask, int? code = null)
    {
        var id = await Admin.CreateAsync(new Entity("teamtemplate")
        {
            ["teamtemplatename"] = $"{_tag} {mask}", ["objecttypecode"] = code ?? AccountCode, ["defaultaccessrightsmask"] = (int)mask,
        });
        _templates.Add(id);
        return id;
    }

    private async Task<EntityReference> AccountAsync(string label, Guid? owner = null) =>
        new(Account, await Admin.CreateAsync(new Entity(Account)
        {
            ["name"] = $"{_tag}-{label}", ["ownerid"] = new EntityReference("systemuser", owner ?? _owner.Id),
        }));

    private static AddUserToRecordTeamRequest Add(EntityReference record, Guid user, Guid template) =>
        new() { Record = record, SystemUserId = user, TeamTemplateId = template };

    private static RemoveUserFromRecordTeamRequest Remove(EntityReference record, Guid user, Guid template) =>
        new() { Record = record, SystemUserId = user, TeamTemplateId = template };

    private static async Task<Guid> AddAsync(OrganizationService service, EntityReference record, Guid user, Guid template) =>
        ((AddUserToRecordTeamResponse)await service.ExecuteAsync(Add(record, user, template))).AccessTeamId;

    private static async Task<Guid> RemoveAsync(OrganizationService service, EntityReference record, Guid user, Guid template) =>
        ((RemoveUserFromRecordTeamResponse)await service.ExecuteAsync(Remove(record, user, template))).AccessTeamId;

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

    private Task<int> TeamsOfAsync(Guid record) =>
        CountAsync("SELECT COUNT(*) FROM data.team WHERE regardingobjectid = @r", ("@r", record));

    private Task<int> MembersAsync(Guid team) =>
        CountAsync("SELECT COUNT(*) FROM data.teammembership WHERE teamid = @t", ("@t", team));

    private async Task<List<int>> TeamMasksAsync(Guid team)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT accessrightsmask FROM data.principalobjectaccess WHERE principalid = @t");
        cmd.Parameters.AddWithValue("@t", team);
        var masks = new List<int>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) masks.Add(rd.GetInt32(0));
        return masks;
    }

    private static async Task<bool> CanReadAsync(OrganizationService service, EntityReference record)
    {
        try
        {
            await service.RetrieveAsync(record.LogicalName, record.Id, new ColumnSet("name"));
            return true;
        }
        catch (XrmdFaultException ex) when (ex.HttpStatus == 403)
        {
            return false;
        }
    }

    private static async Task<XrmdFaultException> RefusedAsync(Func<Task> call, int status)
    {
        var fault = await FaultAsync(call);
        fault.HttpStatus.Should().Be(status, fault.ToString());
        return fault;
    }

    private Task<HttpResponseMessage> GetAsync((Guid Id, Guid Oid) user, string pathAndQuery)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api + pathAndQuery);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(user.Oid, fx.BaseUrl));
        return fx.Client.SendAsync(req);
    }

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    /// <summary>
    /// A user-owned table with <c>AutoCreateAccessTeams</c> and a file column, shared by the tests that need one.
    /// </summary>
    private static Task<string>? _fileTable;
    private static readonly object FileTableLock = new();

    private Task<string> FileTableAsync()
    {
        lock (FileTableLock) return _fileTable ??= CreateFileTableAsync();
    }

    private async Task<string> CreateFileTableAsync()
    {
        var name = "new_rtfile" + Guid.NewGuid().ToString("N")[..8];
        var entity = TestHelpers.Entity(name, name, "Record team files");
        entity.AutoCreateAccessTeams = true;
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name, Attribute = new FileAttributeMetadata { LogicalName = FileColumn, SchemaName = FileColumn },
        });
        return name;
    }

    private async Task UploadAsync(EntityReference record)
    {
        var token = ((InitializeFileBlocksUploadResponse)await Admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = record, FileAttributeName = FileColumn, FileName = "notes.txt",
        })).FileContinuationToken;
        var block = Convert.ToBase64String(Encoding.ASCII.GetBytes("block-000000"));
        await Admin.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = block, BlockData = Encoding.UTF8.GetBytes("hello") });
        await Admin.ExecuteAsync(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = [block], FileName = "notes.txt", MimeType = "text/plain",
        });
    }

    private static Task<OrganizationResponse> DownloadAsync(OrganizationService service, EntityReference record) =>
        service.ExecuteAsync(new InitializeFileBlocksDownloadRequest { Target = record, FileAttributeName = FileColumn });

    // ---- A6/A7: the effect -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Member_reads_record_after_add_and_not_after_remove()
    {
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var member = await MemberAsync();
        (await CanReadAsync(As(member), record)).Should().BeFalse("the member's Basic depth does not reach the owner's record");

        var team = await AddAsync(As(_owner), record, member.Id, template);
        team.Should().NotBeEmpty();
        (await CanReadAsync(As(member), record)).Should().BeTrue();
        var query = new QueryExpression(Account) { ColumnSet = new ColumnSet("name") };
        query.Criteria.AddCondition("name", ConditionOperator.BeginsWith, _tag);
        (await As(member).RetrieveMultipleAsync(query)).Entities.Select(e => e.Id).Should().Equal(record.Id);

        (await RemoveAsync(As(_owner), record, member.Id, template)).Should().Be(team);
        (await CanReadAsync(As(member), record)).Should().BeFalse();
        (await As(member).RetrieveMultipleAsync(query)).Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Add_returns_same_team_for_second_member_and_repeat_add()
    {
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var first = await MemberAsync();
        var second = await MemberAsync();

        var team = await AddAsync(As(_owner), record, first.Id, template);
        (await AddAsync(As(_owner), record, second.Id, template)).Should().Be(team);
        (await AddAsync(As(_owner), record, first.Id, template)).Should().Be(team, "adding a member again is a no-op");

        (await TeamsOfAsync(record.Id)).Should().Be(1);
        (await MembersAsync(team)).Should().Be(2);
        (await TeamMasksAsync(team)).Should().Equal((int)Read);
    }

    [Fact]
    public async Task Team_has_expected_shape()
    {
        var template = await TemplateAsync(Read | Write);
        var record = await AccountAsync("x");
        var member = await MemberAsync();
        var team = await AddAsync(As(_owner), record, member.Id, template);

        var row = await Admin.RetrieveAsync("team", team, new ColumnSet(true));
        row.GetAttributeValue<string>("name").Should().Be($"{record.Id.ToString("D").ToLowerInvariant()}+{template.ToString("D").ToLowerInvariant()}");
        row.GetAttributeValue<OptionSetValue>("teamtype").Value.Should().Be(1);
        row.GetAttributeValue<bool>("systemmanaged").Should().BeTrue();
        row.GetAttributeValue<EntityReference>("businessunitid").Id.Should().Be(_a, "the record's owning business unit");
        row.GetAttributeValue<EntityReference>("administratorid").Id.Should().Be(_owner.Id, "the caller");
        row.GetAttributeValue<EntityReference>("regardingobjectid").Should().Match<EntityReference>(r => r.LogicalName == Account && r.Id == record.Id);
        row.GetAttributeValue<EntityReference>("teamtemplateid").Id.Should().Be(template);

        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT principaltypecode, objectid, objecttypecode, accessrightsmask FROM data.principalobjectaccess WHERE principalid = @t");
        cmd.Parameters.AddWithValue("@t", team);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue();
        (rd.GetInt32(0), rd.GetGuid(1), rd.GetInt32(2), rd.GetInt32(3)).Should().Be((9, record.Id, AccountCode, (int)(Read | Write)));
        (await rd.ReadAsync()).Should().BeFalse("one POA row per team");
    }

    [Fact]
    public async Task Write_template_lets_member_update()
    {
        var template = await TemplateAsync(Read | Write);
        var record = await AccountAsync("x");
        var member = await MemberAsync();
        await RefusedAsync(() => As(member).UpdateAsync(new Entity(Account, record.Id) { ["name"] = _tag + "-before" }), 403);

        await AddAsync(As(_owner), record, member.Id, template);
        await As(member).UpdateAsync(new Entity(Account, record.Id) { ["name"] = _tag + "-by-member" });
        (await Admin.RetrieveAsync(Account, record.Id, new ColumnSet("name"))).GetAttributeValue<string>("name").Should().Be(_tag + "-by-member");
    }

    [Fact]
    public async Task Removed_member_loses_access_on_next_request()
    {
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var member = await MemberAsync();
        // $expand of contact.parentcustomerid (polymorphic) is not supported yet: a child account of the member's expands its parent.
        var child = await AccountAsync("child", member.Id);
        await Admin.UpdateAsync(new Entity(Account, child.Id) { ["parentaccountid"] = record });
        var expand = $"accounts({child.Id})?$select=name&$expand=parentaccountid($select=name)";

        await AddAsync(As(_owner), record, member.Id, template);
        (await CanReadAsync(As(member), record)).Should().BeTrue();
        (await JsonAsync(await GetAsync(member, expand)))["parentaccountid"]!["accountid"]!.GetValue<Guid>().Should().Be(record.Id);

        await RemoveAsync(As(_owner), record, member.Id, template);

        // The very next requests, with no restart: Retrieve, RetrieveMultiple and $expand.
        (await CanReadAsync(As(member), record)).Should().BeFalse();
        var query = new QueryExpression(Account) { ColumnSet = new ColumnSet("name") };
        query.Criteria.AddCondition("accountid", ConditionOperator.Equal, record.Id);
        (await As(member).RetrieveMultipleAsync(query)).Entities.Should().BeEmpty();
        (await JsonAsync(await GetAsync(member, expand)))["parentaccountid"].Should().BeNull();
    }

    [Fact]
    public async Task Removed_member_loses_the_file_download_on_next_request()
    {
        var files = await FileTableAsync();
        var fileTemplate = await TemplateAsync(Read, Store.GetEntity(files)!.ObjectTypeCode!.Value);
        var fileRecord = new EntityReference(files, await Admin.CreateAsync(new Entity(files)
        {
            ["new_name"] = _tag, ["ownerid"] = new EntityReference("systemuser", _owner.Id),
        }));
        await UploadAsync(fileRecord);
        var readFiles = PrivilegeNames.For(Read, Store.GetEntity(files)!);
        await GrantAsync(fx, _owner.Id, team: false, (readFiles, DepthMask.Basic), (PrivilegeNames.For(AccessRights.ShareAccess, Store.GetEntity(files)!), DepthMask.Basic));
        var member = await MemberAsync((readFiles, DepthMask.Basic));

        await AddAsync(As(_owner), fileRecord, member.Id, fileTemplate);
        await DownloadAsync(As(member), fileRecord);

        await RemoveAsync(As(_owner), fileRecord, member.Id, fileTemplate);

        await RefusedAsync(() => DownloadAsync(As(member), fileRecord), 403);
    }

    [Fact]
    public async Task Concurrent_adds_create_one_team()
    {
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var users = new[] { await MemberAsync(), await MemberAsync() };

        var ids = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            Task.Run(() => AddAsync(As(_owner), record, users[i % 2].Id, template))));

        ids.Distinct().Should().ContainSingle();
        (await TeamsOfAsync(record.Id)).Should().Be(1);
        (await TeamMasksAsync(ids[0])).Should().Equal((int)Read);
        (await MembersAsync(ids[0])).Should().Be(2);
        foreach (var user in users) (await CanReadAsync(As(user), record)).Should().BeTrue();
    }

    [Fact]
    public async Task Template_mask_change_affects_only_new_teams()
    {
        var template = await TemplateAsync(Read);
        var first = await AccountAsync("first");
        var second = await AccountAsync("second");
        var member = await MemberAsync();
        var oldTeam = await AddAsync(As(_owner), first, member.Id, template);

        await Admin.UpdateAsync(new Entity("teamtemplate", template) { ["defaultaccessrightsmask"] = (int)(Read | Write) });
        var newTeam = await AddAsync(As(_owner), second, member.Id, template);

        // Template_mask_update_keeps_existing_team_poa: the existing team keeps its mask, a new team gets the new one.
        (await TeamMasksAsync(oldTeam)).Should().Equal((int)Read);
        (await TeamMasksAsync(newTeam)).Should().Equal((int)(Read | Write));
        // Adding to the existing team again does not refresh its mask either.
        await AddAsync(As(_owner), first, (await MemberAsync()).Id, template);
        (await TeamMasksAsync(oldTeam)).Should().Equal((int)Read);

        await RefusedAsync(() => As(member).UpdateAsync(new Entity(Account, first.Id) { ["name"] = _tag + "-no" }), 403);
        await As(member).UpdateAsync(new Entity(Account, second.Id) { ["name"] = _tag + "-yes" });
    }

    [Fact]
    public async Task Template_delete_revokes_access()
    {
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var member = await MemberAsync();
        var team = await AddAsync(As(_owner), record, member.Id, template);
        (await CanReadAsync(As(member), record)).Should().BeTrue();

        await Admin.DeleteAsync("teamtemplate", template);

        (await CanReadAsync(As(member), record)).Should().BeFalse();
        (await TeamsOfAsync(record.Id)).Should().Be(0);
        (await MembersAsync(team)).Should().Be(0);
        (await TeamMasksAsync(team)).Should().BeEmpty();
    }

    // ---- A6 step 6 and 7: the caller and the added user ---------------------------------------------------------------

    [Fact]
    public async Task Caller_needs_template_rights_on_record()
    {
        var readWrite = await TemplateAsync(Read | Write);
        var readOnly = await TemplateAsync(Read);
        var member = await MemberAsync();

        // Share without Write: not through a Read+Write template (RF4), but through a Read one.
        var sharer = await UserAsync(_a, ("prvReadAccount", DepthMask.Basic), ("prvShareAccount", DepthMask.Basic));
        var ofSharer = await AccountAsync("sharer", sharer.Id);
        await RefusedAsync(() => AddAsync(As(sharer), ofSharer, member.Id, readWrite), 403);
        await RefusedAsync(() => RemoveAsync(As(sharer), ofSharer, member.Id, readWrite), 403);
        (await TeamsOfAsync(ofSharer.Id)).Should().Be(0);
        await AddAsync(As(sharer), ofSharer, member.Id, readOnly);

        // No Share at all.
        var writer = await UserAsync(_a, ("prvReadAccount", DepthMask.Basic), ("prvWriteAccount", DepthMask.Basic));
        var ofWriter = await AccountAsync("writer", writer.Id);
        await RefusedAsync(() => AddAsync(As(writer), ofWriter, member.Id, readOnly), 403);
        (await TeamsOfAsync(ofWriter.Id)).Should().Be(0);

        // Share, Read and Write at Basic, on a record the caller cannot read (another user's, in another business unit).
        var outsider = await UserAsync(_b, ("prvReadAccount", DepthMask.Basic), ("prvWriteAccount", DepthMask.Basic),
            ("prvShareAccount", DepthMask.Basic));
        var ofOwner = await AccountAsync("owner");
        await RefusedAsync(() => AddAsync(As(outsider), ofOwner, member.Id, readOnly), 403);
        (await TeamsOfAsync(ofOwner.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Added_user_needs_basic_read()
    {
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var noRead = await UserAsync(_b, ("prvReadContact", DepthMask.Global));

        var fault = await RefusedAsync(() => AddAsync(As(_owner), record, noRead.Id, template), 400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        (await TeamsOfAsync(record.Id)).Should().Be(0);
        // Not for an administrator either: the rule is about the added user.
        await RefusedAsync(() => AddAsync(Admin, record, noRead.Id, template), 400);
    }

    // ---- Final review C1: the rights of an existing team's share --------------------------------------------------------

    /// <summary>A record owned by a sharer who holds Share and Read on account at Basic, not Write.</summary>
    private async Task<((Guid Id, Guid Oid) Sharer, EntityReference Record)> ReadOnlySharerAsync()
    {
        var sharer = await UserAsync(_a, ("prvReadAccount", DepthMask.Basic), ("prvShareAccount", DepthMask.Basic));
        return (sharer, await AccountAsync("sharer", sharer.Id));
    }

    private Task ExecuteInternalAsync(OrganizationRequest request) =>
        TestServices.NewPipelineExecutor(fx.App.Services).ExecuteAsync(request,
            new InvocationContext(Caller.Internal, Guid.Empty, 1, null, null, Guid.NewGuid()), default);

    [Fact]
    public async Task Narrowed_template_does_not_let_caller_hand_out_existing_team_rights()
    {
        var template = await TemplateAsync(Read | Write);
        var (sharer, record) = await ReadOnlySharerAsync();
        var first = await MemberAsync();
        var team = await AddAsync(Admin, record, first.Id, template);
        await Admin.UpdateAsync(new Entity("teamtemplate", template) { ["defaultaccessrightsmask"] = (int)Read });

        // The template now grants Read only, but a new member would receive the team's Read+Write share.
        var second = await MemberAsync();
        await RefusedAsync(() => AddAsync(As(sharer), record, second.Id, template), 403);
        (await MembersAsync(team)).Should().Be(1);
        (await TeamMasksAsync(team)).Should().Equal((int)(Read | Write));
        await RefusedAsync(() => As(second).UpdateAsync(new Entity(Account, record.Id) { ["name"] = _tag + "-no" }), 403);
    }

    [Fact]
    public async Task Widened_team_share_does_not_let_caller_hand_out_its_rights()
    {
        var template = await TemplateAsync(Read);
        var (sharer, record) = await ReadOnlySharerAsync();
        var team = await AddAsync(As(sharer), record, (await MemberAsync()).Id, template);
        // Only the internal caller can still widen a system-managed team's share (final review I2).
        await ExecuteInternalAsync(new ModifyAccessRequest
        {
            Target = record, PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("team", team), AccessMask = Read | Write },
        });

        var second = await MemberAsync();
        await RefusedAsync(() => AddAsync(As(sharer), record, second.Id, template), 403);
        (await MembersAsync(team)).Should().Be(1);
    }

    [Fact]
    public async Task Caller_holding_every_right_of_existing_team_share_adds_member()
    {
        var template = await TemplateAsync(Read | Write);
        var record = await AccountAsync("x");
        var team = await AddAsync(As(_owner), record, (await MemberAsync()).Id, template);
        await Admin.UpdateAsync(new Entity("teamtemplate", template) { ["defaultaccessrightsmask"] = (int)Read });

        var second = await MemberAsync();
        (await AddAsync(As(_owner), record, second.Id, template)).Should().Be(team);
        (await MembersAsync(team)).Should().Be(2);
        await As(second).UpdateAsync(new Entity(Account, record.Id) { ["name"] = _tag + "-yes" });
    }

    [Fact]
    public async Task Core_refuses_team_share_wider_than_the_guard_checked()
    {
        // The handshake under the locks: the guard checked Read, but the team's share now grants Read and Write (as if widened
        // between the check and the write); the core refuses with 400 instead of handing Write to the new member.
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var team = await AddAsync(As(_owner), record, (await MemberAsync()).Id, template);
        await ExecuteInternalAsync(new ModifyAccessRequest
        {
            Target = record, PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("team", team), AccessMask = Read | Write },
        });
        var second = await MemberAsync();
        var request = Add(record, second.Id, template);
        AccessTeamRules.MarkChecked(request,
            new AccessTeamRules.RecordTeamTarget(Store.GetEntity(Account)!, record.Id, second.Id, template, Read, _a));
        var caller = await TestServices.CallerAsync(fx.App.Services, _owner.Oid);
        await using var scope = fx.App.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();

        var fault = await RefusedAsync(() => core.ExecuteCoreAsync(request, caller, new DataSession(conn, tx), default), 400);
        await tx.RollbackAsync();

        fault.Message.Should().Contain("Retry");
        (await MembersAsync(team)).Should().Be(1);
    }

    // ---- Final review I1: the added user holds the privilege of each right ---------------------------------------------

    [Fact]
    public async Task Added_user_needs_privilege_of_each_right()
    {
        var template = await TemplateAsync(Read | AccessRights.DeleteAccess);
        var record = await AccountAsync("x");
        var noDelete = await MemberAsync();

        var fault = await RefusedAsync(() => AddAsync(As(_owner), record, noDelete.Id, template), 400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain("prvDeleteAccount");
        (await TeamsOfAsync(record.Id)).Should().Be(0);
        await RefusedAsync(() => AddAsync(Admin, record, noDelete.Id, template), 400);

        var deleter = await MemberAsync(("prvDeleteAccount", DepthMask.Basic));
        await AddAsync(As(_owner), record, deleter.Id, template);
        (await TeamsOfAsync(record.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Added_user_needs_privileges_of_existing_team_share()
    {
        var template = await TemplateAsync(Read | Write);
        var record = await AccountAsync("x");
        var team = await AddAsync(As(_owner), record, (await MemberAsync()).Id, template);
        await Admin.UpdateAsync(new Entity("teamtemplate", template) { ["defaultaccessrightsmask"] = (int)Read });

        var readOnly = await UserAsync(_b, ("prvReadAccount", DepthMask.Basic));
        var fault = await RefusedAsync(() => AddAsync(As(_owner), record, readOnly.Id, template), 400);
        fault.Message.Should().Contain("prvWriteAccount");
        (await MembersAsync(team)).Should().Be(1);
    }

    [Fact]
    public async Task System_user_is_exempt_from_added_user_privileges()
    {
        var template = await TemplateAsync(Read | AccessRights.DeleteAccess | AccessRights.AssignAccess);
        var record = await AccountAsync("x");
        var system = await Cache(fx).SystemUserIdAsync(default);
        var team = await AddAsync(As(_owner), record, system, template);
        (await MembersAsync(team)).Should().Be(1);
    }

    // ---- Final review I2: a system-managed team is shared on its own record only, by the record team messages ----------

    [Fact]
    public async Task System_managed_team_cannot_be_granted_or_modified_access()
    {
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var other = await AccountAsync("other");
        var team = await AddAsync(As(_owner), record, (await MemberAsync()).Id, template);
        var principal = new EntityReference("team", team);

        foreach (var service in new[] { As(_owner), Admin })
        {
            var grant = await RefusedAsync(() => service.ExecuteAsync(new GrantAccessRequest
            {
                Target = other, PrincipalAccess = new PrincipalAccess { Principal = principal, AccessMask = Read },
            }), 400);
            grant.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
            grant.Message.Should().Contain("a system-managed access team gets access only to its own record through AddUserToRecordTeam");
            await RefusedAsync(() => service.ExecuteAsync(new GrantAccessRequest
            {
                Target = record, PrincipalAccess = new PrincipalAccess { Principal = principal, AccessMask = Write },
            }), 400);
            await RefusedAsync(() => service.ExecuteAsync(new ModifyAccessRequest
            {
                Target = record, PrincipalAccess = new PrincipalAccess { Principal = principal, AccessMask = Read | Write },
            }), 400);
        }
        (await TeamMasksAsync(team)).Should().Equal((int)Read);

    }

    // ---- An access team owns no record (final re-review, note 1) -----------------------------------------------------

    private const string AccessTeamNotOwner = "An access team cannot own records.";

    /// <summary>The two kinds of access team: the system-managed team of a record and a team created as an access team.</summary>
    private async Task<EntityReference[]> AccessTeamsAsync()
    {
        var template = await TemplateAsync(Read);
        var managed = await AddAsync(As(_owner), await AccountAsync("regarding"), (await MemberAsync()).Id, template);
        var created = await Admin.CreateAsync(new Entity("team")
        {
            ["name"] = $"{_tag}-access", ["businessunitid"] = new EntityReference("businessunit", _a), ["teamtype"] = new OptionSetValue(1),
        });
        return [new EntityReference("team", managed), new EntityReference("team", created)];
    }

    private static async Task AccessTeamRefusedAsync(Func<Task> call)
    {
        var fault = await RefusedAsync(call, 400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain(AccessTeamNotOwner);
    }

    private Task<object?> OwnerOfAsync(EntityReference record) =>
        ScalarAsync("SELECT ownerid FROM data.account WHERE accountid = @id", ("@id", record.Id));

    [Fact]
    public async Task Administrator_cannot_assign_record_to_access_team()
    {
        var record = await AccountAsync("x");
        foreach (var team in await AccessTeamsAsync())
            await AccessTeamRefusedAsync(() => Admin.ExecuteAsync(new AssignRequest { Target = record, Assignee = team }));
        (await OwnerOfAsync(record)).Should().Be(_owner.Id);
    }

    [Fact]
    public async Task Create_with_access_team_owner_is_refused()
    {
        foreach (var team in await AccessTeamsAsync())
        {
            var name = $"{_tag}-owned-{team.Id}";
            await AccessTeamRefusedAsync(() => Admin.CreateAsync(new Entity(Account) { ["name"] = name, ["ownerid"] = team }));
            (await CountAsync("SELECT COUNT(*) FROM data.account WHERE name = @n", ("@n", name))).Should().Be(0);
        }
    }

    [Fact]
    public async Task Update_of_owner_to_access_team_is_refused()
    {
        var record = await AccountAsync("x");
        foreach (var team in await AccessTeamsAsync())
            await AccessTeamRefusedAsync(() => Admin.UpdateAsync(new Entity(Account, record.Id) { ["ownerid"] = team }));
        (await OwnerOfAsync(record)).Should().Be(_owner.Id);
    }

    [Fact]
    public async Task Assign_to_owner_team_still_works()
    {
        var record = await AccountAsync("x");
        var team = await Admin.CreateAsync(new Entity("team")
        {
            ["name"] = $"{_tag}-owner", ["businessunitid"] = new EntityReference("businessunit", _a), ["teamtype"] = new OptionSetValue(0),
        });
        await GrantAsync(fx, team, team: true, ("prvReadAccount", DepthMask.Basic));

        await Admin.ExecuteAsync(new AssignRequest { Target = record, Assignee = new EntityReference("team", team) });

        (await OwnerOfAsync(record)).Should().Be(team);
        (await ScalarAsync("SELECT owningteam FROM data.account WHERE accountid = @id", ("@id", record.Id))).Should().Be(team);
    }

    // ---- A6: the order of the checks ----------------------------------------------------------------------------------

    [Fact]
    public async Task Template_table_mismatch_400()
    {
        var template = await TemplateAsync(Read);
        var member = await MemberAsync();
        var contact = new EntityReference("contact", Guid.NewGuid()); // missing too: the table is checked first
        var fault = await RefusedAsync(() => AddAsync(As(_owner), contact, member.Id, template), 400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        await RefusedAsync(() => RemoveAsync(As(_owner), contact, member.Id, template), 400);
    }

    [Fact]
    public async Task Missing_template_404()
    {
        var member = await MemberAsync();
        var missing = new EntityReference(Account, Guid.NewGuid());
        var fault = await RefusedAsync(() => AddAsync(As(_owner), missing, member.Id, Guid.NewGuid()), 404);
        fault.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        fault.Message.Should().Contain("teamtemplate");
        await RefusedAsync(() => RemoveAsync(As(_owner), missing, member.Id, Guid.NewGuid()), 404);
    }

    [Fact]
    public async Task Missing_record_404()
    {
        var template = await TemplateAsync(Read);
        var missing = new EntityReference(Account, Guid.NewGuid());
        // The record is checked before the user, and both before the caller's rights.
        var fault = await RefusedAsync(() => AddAsync(As(_owner), missing, Guid.NewGuid(), template), 404);
        fault.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        fault.Message.Should().Contain(Account);
        await RefusedAsync(() => RemoveAsync(As(_owner), missing, Guid.NewGuid(), template), 404);
    }

    [Fact]
    public async Task Disabled_user_400()
    {
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var disabled = await CreateUserAsync(fx, _b, disabled: true);
        await GrantAsync(fx, disabled, team: false, ("prvReadAccount", DepthMask.Basic));

        (await RefusedAsync(() => AddAsync(As(_owner), record, disabled, template), 400)).ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        var missingUser = await RefusedAsync(() => AddAsync(As(_owner), record, Guid.NewGuid(), template), 404);
        missingUser.Message.Should().Contain("systemuser");
        // The user is checked before the caller's rights.
        var stranger = await UserAsync(_b, ("prvReadContact", DepthMask.Basic));
        await RefusedAsync(() => AddAsync(As(stranger), record, disabled, template), 400);
        (await TeamsOfAsync(record.Id)).Should().Be(0);
    }

    // ---- A8: the record's lifecycle -----------------------------------------------------------------------------------

    [Fact]
    public async Task Record_delete_removes_access_teams()
    {
        var read = await TemplateAsync(Read);
        var write = await TemplateAsync(Read | Write);
        var record = await AccountAsync("x");
        var member = await MemberAsync();
        var teams = new[] { await AddAsync(As(_owner), record, member.Id, read), await AddAsync(As(_owner), record, member.Id, write) };

        await As(_owner).DeleteAsync(Account, record.Id);

        (await TeamsOfAsync(record.Id)).Should().Be(0);
        foreach (var team in teams)
        {
            (await CountAsync("SELECT COUNT(*) FROM data.team WHERE teamid = @t", ("@t", team))).Should().Be(0);
            (await MembersAsync(team)).Should().Be(0);
            (await TeamMasksAsync(team)).Should().BeEmpty();
        }
        (await CountAsync("SELECT COUNT(*) FROM data.principalobjectaccess WHERE objectid = @r", ("@r", record.Id))).Should().Be(0);
    }

    [Fact]
    public async Task Delete_of_another_tables_record_with_the_same_id_keeps_the_access_teams()
    {
        // Final re-review N2: a record's access teams are those of its id and its table. A Basic user's own record of another
        // table with access teams, created with the account's id and deleted, leaves the account's teams and shares alone.
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var member = await MemberAsync();
        var team = await AddAsync(As(_owner), record, member.Id, template);
        var files = Store.GetEntity(await FileTableAsync())!;
        var basic = await UserAsync(_b, (PrivilegeNames.For(AccessRights.CreateAccess, files), DepthMask.Basic),
            (PrivilegeNames.For(Read, files), DepthMask.Basic), (PrivilegeNames.For(AccessRights.DeleteAccess, files), DepthMask.Basic));
        await As(basic).CreateAsync(new Entity(files.LogicalName, record.Id) { ["new_name"] = _tag });

        await As(basic).DeleteAsync(files.LogicalName, record.Id);

        (await CountAsync("SELECT COUNT(*) FROM data.team WHERE teamid = @t", ("@t", team))).Should().Be(1);
        (await MembersAsync(team)).Should().Be(1);
        (await TeamMasksAsync(team)).Should().Equal((int)Read);
        (await CanReadAsync(As(member), record)).Should().BeTrue();
    }

    [Fact]
    public async Task Assign_keeps_access_team_rights()
    {
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var member = await MemberAsync();
        var team = await AddAsync(As(_owner), record, member.Id, template);
        var newOwner = await UserAsync(_root, ("prvReadAccount", DepthMask.Basic));

        await Admin.ExecuteAsync(new AssignRequest { Target = record, Assignee = new EntityReference("systemuser", newOwner.Id) });

        (await CanReadAsync(As(member), record)).Should().BeTrue();
        (await TeamMasksAsync(team)).Should().Equal((int)Read);
        (await ScalarAsync("SELECT businessunitid FROM data.team WHERE teamid = @t", ("@t", team))).Should().Be(_a);
    }

    [Fact]
    public async Task Last_member_removed_keeps_team()
    {
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var member = await MemberAsync();
        var other = await MemberAsync();

        (await RemoveAsync(As(_owner), record, member.Id, template)).Should().Be(Guid.Empty, "no team exists yet");
        (await TeamsOfAsync(record.Id)).Should().Be(0);

        var team = await AddAsync(As(_owner), record, member.Id, template);
        (await RemoveAsync(As(_owner), record, other.Id, template)).Should().Be(team, "removing a non-member is a no-op");
        (await MembersAsync(team)).Should().Be(1);
        (await RemoveAsync(As(_owner), record, member.Id, template)).Should().Be(team);

        (await MembersAsync(team)).Should().Be(0);
        (await TeamsOfAsync(record.Id)).Should().Be(1);
        (await TeamMasksAsync(team)).Should().Equal((int)Read);
    }

    // ---- A9: pipeline and audit ---------------------------------------------------------------------------------------

    private async Task<Guid> PluginTypeAsync(string typeName)
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        return await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins." + typeName);
    }

    /// <summary>The <c>pb_pipelog</c> rows written by ContextRecorderPlugin: name <c>Message|Stage|Mode|Depth</c> → data lines.</summary>
    private async Task<Dictionary<string, string[]>> LogsAsync()
    {
        var logs = new Dictionary<string, string[]>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data FROM data.{TestEntities.PipeLog}");
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) logs[rd.GetString(0)] = rd.IsDBNull(1) ? [] : rd.GetString(1).Split('\n');
        return logs;
    }

    [Fact]
    public async Task Plugin_step_runs_on_AddUserToRecordTeam()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        var recorder = await PluginTypeAsync("ContextRecorderPlugin");
        foreach (var message in new[] { "AddUserToRecordTeam", "RemoveUserFromRecordTeam" })
            foreach (var stage in new[] { 10, 20, 40 })
                await PluginTestHelper.RegisterStepAsync(Admin, recorder, message, null, stage, config: TestEntities.PipeLog);
        // The team, the share and the membership are written by the core itself, not by nested messages (ruling I1).
        await PluginTestHelper.RegisterStepAsync(Admin, recorder, "Create", "team", 40, config: TestEntities.PipeLog);
        await PluginTestHelper.RegisterStepAsync(Admin, recorder, "Associate", null, 40, config: TestEntities.PipeLog);
        await PluginTestHelper.RegisterStepAsync(Admin, recorder, "GrantAccess", Account, 40, config: TestEntities.PipeLog);

        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var member = await MemberAsync();
        var team = await AddAsync(Admin, record, member.Id, template);
        (await RemoveAsync(Admin, record, member.Id, template)).Should().Be(team);

        var logs = await LogsAsync();
        logs.Keys.Should().BeEquivalentTo(
            "AddUserToRecordTeam|10|0|1", "AddUserToRecordTeam|20|0|1", "AddUserToRecordTeam|40|0|1",
            "RemoveUserFromRecordTeam|10|0|1", "RemoveUserFromRecordTeam|20|0|1", "RemoveUserFromRecordTeam|40|0|1");
        logs["AddUserToRecordTeam|20|0|1"].Should().Contain("output=");
        logs["AddUserToRecordTeam|40|0|1"].Should().Contain("output=AccessTeamId").And.Contain("intx=True");
        logs["RemoveUserFromRecordTeam|40|0|1"].Should().Contain("output=AccessTeamId");
        var admin = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        logs["AddUserToRecordTeam|40|0|1"].Should().Contain("user=" + admin.UserId);
    }

    [Fact]
    public async Task Plugin_service_for_internal_caller_acts_as_system()
    {
        // A message run as the internal caller hands plug-ins UserId = Guid.Empty; the service built for it must be SYSTEM's,
        // which the internal-only rules (here: writing a system-managed team, ruling A4) do not exempt.
        await TestEntities.EnsurePipeAsync(Store);
        var type = await PluginTypeAsync("TryCreateSystemManagedTeamPlugin");
        // PreValidation: outside the transaction, so the refused nested call does not doom it.
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", TestEntities.PipeLog, 10, config: _root.ToString());
        var admin = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);

        var response = await TestServices.NewPipelineExecutor(fx.App.Services).ExecuteAsync(new CreateRequest
        {
            Target = new Entity(TestEntities.PipeLog) { ["pb_name"] = _tag, ["ownerid"] = new EntityReference("systemuser", admin.UserId) },
        }, new InvocationContext(Caller.Internal, Guid.Empty, 1, null, null, Guid.NewGuid()), default);

        var id = (Guid)response.Results["id"];
        (await ScalarAsync($"SELECT pb_data FROM data.{TestEntities.PipeLog} WHERE {TestEntities.PipeLog}id = @id", ("@id", id)))
            .Should().Be("caught " + ErrorCodes.InvalidArgument.ToString(System.Globalization.CultureInfo.InvariantCulture));
        (await CountAsync("SELECT COUNT(*) FROM data.team WHERE name LIKE 'plugin-%'")).Should().Be(0);
    }

    [Fact]
    public async Task Plugin_service_for_empty_guid_acts_as_the_calling_user()
    {
        // Learn: CreateOrganizationService(Guid.Empty) is the same user as context.UserId; never SYSTEM under a real user.
        await TestEntities.EnsurePipeAsync(Store);
        var log = Store.GetEntity(TestEntities.PipeLog)!;
        var user = await UserAsync(_root, (PrivilegeNames.For(AccessRights.CreateAccess, log), DepthMask.Basic),
            (PrivilegeNames.For(Read, log), DepthMask.Basic), (PrivilegeNames.For(Write, log), DepthMask.Basic));
        var ofAdmin = await Admin.CreateAsync(new Entity(TestEntities.PipeLog) { ["pb_name"] = _tag + "-admin" });
        var type = await PluginTypeAsync("TryUpdateAsSameUserPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", TestEntities.PipeLog, 10, config: $"{TestEntities.PipeLog}|{ofAdmin}");

        var created = await As(user).CreateAsync(new Entity(TestEntities.PipeLog) { ["pb_name"] = _tag + "-user" });

        var outcome = (string?)await ScalarAsync($"SELECT pb_data FROM data.{TestEntities.PipeLog} WHERE {TestEntities.PipeLog}id = @id", ("@id", created));
        outcome.Should().StartWith("caught ", "the plug-in acts as the user, who cannot write the administrator's record");
        (await ScalarAsync($"SELECT pb_name FROM data.{TestEntities.PipeLog} WHERE {TestEntities.PipeLog}id = @id", ("@id", ofAdmin)))
            .Should().Be(_tag + "-admin");
    }

    [Fact]
    public async Task Internal_caller_creates_team_as_system()
    {
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var member = await MemberAsync();
        var system = await Cache(fx).SystemUserIdAsync(default);

        var response = await TestServices.NewPipelineExecutor(fx.App.Services).ExecuteAsync(Add(record, member.Id, template),
            new InvocationContext(Caller.Internal, Guid.Empty, 1, null, null, Guid.NewGuid()), default);

        var team = await Admin.RetrieveAsync("team", (Guid)response.Results["AccessTeamId"],
            new ColumnSet("createdby", "modifiedby", "administratorid", "owningbusinessunit", "businessunitid"));
        foreach (var column in new[] { "createdby", "modifiedby", "administratorid" })
            team.GetAttributeValue<EntityReference>(column).Id.Should().Be(system, column);
        team.GetAttributeValue<EntityReference>("owningbusinessunit").Id.Should().Be(_a);
        team.GetAttributeValue<EntityReference>("businessunitid").Id.Should().Be(_a);
        (await CanReadAsync(As(member), record)).Should().BeTrue();
    }

    [Fact]
    public async Task Lost_create_race_returns_the_winning_team()
    {
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var member = await MemberAsync();
        await using var scope = fx.App.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var winner = Guid.Empty;
        // Between the team lookup and the Create, the same (record, template) gets its team: the Create hits UX_team_record_template.
        core.RecordTeams.BeforeCreateTeam = async s => winner = await core.CreateAsync(s, Caller.Internal, new Entity("team")
        {
            ["name"] = "winner " + _tag, ["teamtype"] = new OptionSetValue(1), ["businessunitid"] = new EntityReference("businessunit", _a),
            ["systemmanaged"] = true, ["regardingobjectid"] = record, ["teamtemplateid"] = new EntityReference("teamtemplate", template),
        }, default);
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();

        var response = await core.ExecuteCoreAsync(Add(record, member.Id, template), Caller.Internal, new DataSession(conn, tx), default);
        await tx.CommitAsync();

        winner.Should().NotBeEmpty();
        response.Results["AccessTeamId"].Should().Be(winner);
        (await TeamsOfAsync(record.Id)).Should().Be(1);
        (await MembersAsync(winner)).Should().Be(1);
    }

    [Fact]
    public async Task Template_delete_during_add_leaves_no_team()
    {
        // The race, timed rather than synchronized (not deterministic): the template Delete starts while the add sits between
        // its checks and the team's Create, and the 700 ms delay usually lets it reach the template's lock before the add goes on.
        // With the template locked by the add, the Delete waits for it and then cascades to the new team; without the lock, the
        // Delete would commit first and the add's Create would leave a team (and its share) pointing to no template. If the Delete
        // starts late, it runs after the add's commit and the test passes without exercising the wait.
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var member = await MemberAsync();
        await using var scope = fx.App.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        Task? delete = null;
        core.RecordTeams.BeforeCreateTeam = async _ =>
        {
            delete = Task.Run(() => Admin.DeleteAsync("teamtemplate", template));
            await Task.Delay(700);
        };
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await core.ExecuteCoreAsync(Add(record, member.Id, template), Caller.Internal, new DataSession(conn, tx), default);
            await tx.CommitAsync();
        }
        Cache(fx).Invalidate();
        await delete!;

        (await CountAsync("SELECT COUNT(*) FROM data.team WHERE teamtemplateid = @t", ("@t", template))).Should().Be(0);
        (await TeamsOfAsync(record.Id)).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.principalobjectaccess WHERE objectid = @r", ("@r", record.Id))).Should().Be(0);
        (await CanReadAsync(As(member), record)).Should().BeFalse();
    }

    [Fact]
    public async Task Template_delete_racing_adds_leaves_no_access()
    {
        var member = await MemberAsync();
        for (var i = 0; i < 15; i++)
        {
            var template = await TemplateAsync(Read);
            var record = await AccountAsync("race" + i);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var adds = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                try
                {
                    await AddAsync(As(_owner), record, member.Id, template);
                }
                catch (XrmdFaultException ex) when (ex.HttpStatus == 404)
                {
                    // The template was deleted first.
                }
            })).ToList();
            var delete = Task.Run(async () => { await start.Task; await Admin.DeleteAsync("teamtemplate", template); });
            start.SetResult();
            await Task.WhenAll([.. adds, delete]);

            (await CountAsync("""
                SELECT COUNT(*) FROM data.team AS t
                WHERE t.teamtemplateid IS NOT NULL AND NOT EXISTS (SELECT 1 FROM data.teamtemplate AS tt WHERE tt.teamtemplateid = t.teamtemplateid)
                """)).Should().Be(0, $"iteration {i}: a team outlived its template");
            (await TeamsOfAsync(record.Id)).Should().Be(0, $"iteration {i}");
            (await CountAsync("SELECT COUNT(*) FROM data.principalobjectaccess WHERE objectid = @r", ("@r", record.Id))).Should().Be(0);
            (await CanReadAsync(As(member), record)).Should().BeFalse($"iteration {i}: access survived the template delete");
        }
    }

    [Fact]
    public async Task Add_is_audited_as_a_share_of_the_record()
    {
        var organization = (Guid)(await ScalarAsync("SELECT TOP 1 organizationid FROM data.organization"))!;
        // Restored as found: other tests rely on the shared database's settings.
        var organizationAudited = await ScalarAsync("SELECT isauditenabled FROM data.organization WHERE organizationid = @id", ("@id", organization)) is true;
        var accountAudited = Store.GetEntity(Account)!.IsAuditEnabled?.Value == true;
        await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = true });
        await Admin.ExecuteAsync(new UpdateEntityRequest
        {
            Entity = new EntityMetadata { LogicalName = Account, IsAuditEnabled = new BooleanManagedProperty(true) },
        });
        try
        {
            var template = await TemplateAsync(Read | Write);
            var record = await AccountAsync("x");
            var team = await AddAsync(As(_owner), record, (await MemberAsync()).Id, template);
            await AddAsync(As(_owner), record, (await MemberAsync()).Id, template);
            await RemoveAsync(As(_owner), record, (await MemberAsync()).Id, template);

            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var cmd = TestSql.Command(conn, "SELECT action, changedata, userid FROM data.audit WHERE objectid = @id AND action <> @create");
            cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = record.Id;
            cmd.Parameters.Add("@create", SqlDbType.Int).Value = AuditActions.Create;
            var rows = new List<(int Action, AuditChange Change, Guid User)>();
            await using (var rd = await cmd.ExecuteReaderAsync())
                while (await rd.ReadAsync()) rows.Add((rd.GetInt32(0), ChangeData.Deserialize(rd.GetString(1)), rd.GetGuid(2)));

            // One Share row, written when the team and its share are created; membership changes are not audited (as for teams).
            rows.Should().ContainSingle();
            rows[0].Action.Should().Be(AuditActions.Share);
            rows[0].User.Should().Be(_owner.Id);
            var change = rows[0].Change.Should().BeOfType<ShareChange>().Subject;
            (change.Principal.LogicalName, change.Principal.Id, change.Old, change.New).Should().Be(("team", team, AccessRights.None, Read | Write));
        }
        finally
        {
            await Admin.ExecuteAsync(new UpdateEntityRequest
            {
                Entity = new EntityMetadata { LogicalName = Account, IsAuditEnabled = new BooleanManagedProperty(accountAudited) },
            });
            await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = organizationAudited });
        }
    }

    // ---- interaction with the manager hierarchy (plan 2.5) ------------------------------------------------------------

    [Fact]
    public async Task Hierarchy_manager_reaches_record_shared_with_report_access_team()
    {
        var template = await TemplateAsync(Read);
        var record = await AccountAsync("x");
        var manager = await UserAsync(_root, ("prvReadAccount", DepthMask.Basic));
        var report = await UserAsync(_root, ("prvReadAccount", DepthMask.Basic));
        await ScalarAsync("UPDATE data.systemuser SET parentsystemuserid = @m WHERE systemuserid = @u", ("@m", manager.Id), ("@u", report.Id));
        await ScalarAsync("UPDATE data.organization SET ishierarchicalsecuritymodelenabled = 1, maxdepthforhierarchicalsecuritymodel = 3");
        Cache(fx).Invalidate();
        try
        {
            (await CanReadAsync(As(manager), record)).Should().BeFalse();
            await AddAsync(As(_owner), record, report.Id, template);
            (await CanReadAsync(As(report), record)).Should().BeTrue();
            (await CanReadAsync(As(manager), record)).Should().BeTrue("a share with a report's team reaches the manager");
            await RemoveAsync(As(_owner), record, report.Id, template);
            (await CanReadAsync(As(manager), record)).Should().BeFalse();
        }
        finally
        {
            await ScalarAsync("UPDATE data.organization SET ishierarchicalsecuritymodelenabled = 0, maxdepthforhierarchicalsecuritymodel = 3");
            Cache(fx).Invalidate();
        }
    }
}
