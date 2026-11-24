using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.5, task 5: the record-level rights a manager gets through the manager hierarchy (ruling H4) — Read, Write, Append
/// and AppendTo on the records of direct reports, Read only below them, never Create, Delete, Assign or Share, and at most the
/// rights of a share with a report. Every test builds its own users and managers; the organization settings, the exclusions
/// and the manager links are reset after each test.
/// </summary>
[Collection("xrmd")]
public sealed class HierarchyWriteTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Enabled = "ishierarchicalsecuritymodelenabled";
    private const string Depth = "maxdepthforhierarchicalsecuritymodel";
    private const string Doc = "new_doc";

    private static readonly (string, DepthMask)[] AccountBasic =
    [
        ("prvReadAccount", DepthMask.Basic), ("prvWriteAccount", DepthMask.Basic), ("prvAppendAccount", DepthMask.Basic),
        ("prvAppendToAccount", DepthMask.Basic), ("prvDeleteAccount", DepthMask.Basic), ("prvAssignAccount", DepthMask.Basic),
        ("prvShareAccount", DepthMask.Basic), ("prvCreateAccount", DepthMask.Basic),
    ];

    private readonly string _tag = "HW" + Guid.NewGuid().ToString("N");
    private readonly List<Guid> _reports = [];
    private OrganizationService _admin = null!;
    private Guid _root;

    public async Task InitializeAsync()
    {
        _admin = ServiceFor(fx, fx.AdminObjectId);
        _root = await RootBusinessUnitAsync(fx);
    }

    public async Task DisposeAsync()
    {
        foreach (var report in _reports)
            await ExecAsync("UPDATE data.systemuser SET parentsystemuserid = NULL WHERE systemuserid = @u", ("@u", report));
        await ExecAsync($"UPDATE data.organization SET {Enabled} = 0, {Depth} = 3; DELETE FROM meta.hierarchy_security_exclusion;");
        Cache(fx).Invalidate();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SettingsAsync(bool enabled, int depth = 3)
    {
        await ExecAsync($"UPDATE data.organization SET {Enabled} = @e, {Depth} = @d", ("@e", enabled), ("@d", depth));
        Cache(fx).Invalidate();
    }

    private async Task ManagerAsync((Guid Id, Guid Oid) report, (Guid Id, Guid Oid) manager)
    {
        _reports.Add(report.Id);
        await ExecAsync("UPDATE data.systemuser SET parentsystemuserid = @m WHERE systemuserid = @u", ("@m", manager.Id), ("@u", report.Id));
        Cache(fx).Invalidate();
    }

    private async Task<(Guid Id, Guid Oid)> UserAsync(params (string Privilege, DepthMask Depth)[] privileges)
    {
        var oid = Guid.NewGuid();
        var id = await CreateUserAsync(fx, _root, oid);
        if (privileges.Length > 0) await GrantAsync(fx, id, team: false, privileges);
        return (id, oid);
    }

    private Task<Guid> AccountAsync(string label, Guid owner, string ownerEntity = "systemuser") =>
        _admin.CreateAsync(new Entity("account") { ["name"] = $"{_tag}-{label}", ["ownerid"] = new EntityReference(ownerEntity, owner) });

    private Task ShareAsync(Guid recordId, Guid principalId, AccessRights rights) => ExecAsync("""
        INSERT INTO data.principalobjectaccess (principalobjectaccessid, principalid, principaltypecode, objectid, objecttypecode, accessrightsmask, inheritedaccessrightsmask)
        VALUES (NEWID(), @principal, 8, @object, @otc, @mask, 0)
        """, ("@principal", principalId), ("@object", recordId), ("@otc", Store.GetEntity("account")!.ObjectTypeCode!.Value),
        ("@mask", (int)rights));

    private OrganizationService As((Guid Id, Guid Oid) user) => TestServices.NewOrganizationService(fx.App.Services, user.Oid);

    private Task RenameAsync((Guid Id, Guid Oid) user, Guid account, string name) =>
        As(user).UpdateAsync(new Entity("account", account) { ["name"] = $"{_tag}-{name}" });

    private async Task<string?> NameAsync(Guid account) =>
        (await _admin.RetrieveAsync("account", account, new ColumnSet("name"))).GetAttributeValue<string>("name");

    private static async Task DeniedAsync(Func<Task> act, string because = "")
    {
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>(because)).Which;
        fault.HttpStatus.Should().Be(403, because);
    }

    private Task<HttpResponseMessage> PatchAsync((Guid Id, Guid Oid) user, string path, string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, Api + path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(user.Oid, fx.BaseUrl));
        req.Headers.Add("If-Match", "*");
        return fx.Client.SendAsync(req);
    }

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Manager_updates_direct_reports_records()
    {
        var m = await UserAsync(AccountBasic);
        var r = await UserAsync(AccountBasic);
        var peer = await UserAsync(AccountBasic);
        await ManagerAsync(r, m);
        var team = await CreateTeamAsync(fx, _root, 0, r.Id);
        var ofR = await AccountAsync("r", r.Id);
        var ofTeam = await AccountAsync("team", team, "team");

        // Disabled model: no reach.
        await DeniedAsync(() => RenameAsync(m, ofR, "off"), "the model is disabled");

        await SettingsAsync(enabled: true);

        // SDK, on the report's record and on a record of the report's team.
        await RenameAsync(m, ofR, "sdk");
        (await NameAsync(ofR)).Should().Be($"{_tag}-sdk");
        await RenameAsync(m, ofTeam, "team-sdk");
        (await NameAsync(ofTeam)).Should().Be($"{_tag}-team-sdk");
        await DeniedAsync(() => RenameAsync(peer, ofR, "peer"), "a peer is not the report's manager");

        // Web API PATCH.
        var res = await PatchAsync(m, $"accounts({ofR})", $$"""{ "name": "{{_tag}}-web" }""");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        (await NameAsync(ofR)).Should().Be($"{_tag}-web");
        res = await PatchAsync(peer, $"accounts({ofR})", $$"""{ "name": "{{_tag}}-peer" }""");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync());

        // The manager must hold the privilege itself.
        var noWrite = await UserAsync(("prvReadAccount", DepthMask.Basic));
        var r2 = await UserAsync(AccountBasic);
        await ManagerAsync(r2, noWrite);
        var ofR2 = await AccountAsync("r2", r2.Id);
        await DeniedAsync(() => RenameAsync(noWrite, ofR2, "x"), "no prvWriteAccount");

        // An excluded table gives no reach.
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Cache(fx).SetHierarchyExclusionsAsync(s, ["account"], default));
        await DeniedAsync(() => RenameAsync(m, ofR, "excluded"), "account is excluded");
    }

    [Fact]
    public async Task Non_direct_and_forbidden_rights_are_refused()
    {
        // gm ← l1 ← l2, depth 2.
        var gm = await UserAsync(AccountBasic);
        var l1 = await UserAsync(AccountBasic);
        var l2 = await UserAsync(AccountBasic);
        var other = await UserAsync(AccountBasic);
        await ManagerAsync(l1, gm);
        await ManagerAsync(l2, l1);
        var of1 = await AccountAsync("l1", l1.Id);
        var of2 = await AccountAsync("l2", l2.Id);
        await SettingsAsync(enabled: true, depth: 2);

        // The grand-manager reads the level-2 record but cannot update it; the direct manager can.
        (await As(gm).RetrieveAsync("account", of2, new ColumnSet("name"))).Id.Should().Be(of2);
        await DeniedAsync(() => RenameAsync(gm, of2, "gm"), "level 2 is read only");
        await RenameAsync(l1, of2, "l1");
        await RenameAsync(gm, of1, "gm");

        // Never Delete, Assign or Share, even on a direct report's record.
        await DeniedAsync(() => As(gm).DeleteAsync("account", of1), "never Delete");
        await DeniedAsync(() => As(gm).ExecuteAsync(new AssignRequest
        {
            Target = new EntityReference("account", of1), Assignee = new EntityReference("systemuser", other.Id),
        }), "never Assign");
        await DeniedAsync(() => As(gm).ExecuteAsync(new GrantAccessRequest
        {
            Target = new EntityReference("account", of1),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", other.Id), AccessMask = AccessRights.ReadAccess },
        }), "never Share");
        (await NameAsync(of1)).Should().Be($"{_tag}-gm");
    }

    [Fact]
    public async Task Grant_needs_share_from_outside_the_hierarchy()
    {
        // DemandRightsAsync's hierarchy branch: a manager who holds Share on a report's record by a share of its own may grant
        // the Write it gets only through the hierarchy; without that share, the hierarchy alone never allows a grant.
        var m = await UserAsync(AccountBasic);
        var r = await UserAsync(AccountBasic);
        var other = await UserAsync(AccountBasic);
        await ManagerAsync(r, m);
        var shared = await AccountAsync("shared", r.Id);
        var notShared = await AccountAsync("not-shared", r.Id);
        await ShareAsync(shared, m.Id, AccessRights.ShareAccess);
        await SettingsAsync(enabled: true);

        GrantAccessRequest Grant(Guid record, AccessRights mask) => new()
        {
            Target = new EntityReference("account", record),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", other.Id), AccessMask = mask },
        };

        await As(m).ExecuteAsync(Grant(shared, AccessRights.ReadAccess | AccessRights.WriteAccess));
        await DeniedAsync(() => As(m).ExecuteAsync(Grant(shared, AccessRights.DeleteAccess)), "Delete never comes from the hierarchy");
        await DeniedAsync(() => As(m).ExecuteAsync(Grant(notShared, AccessRights.ReadAccess)), "Share never comes from the hierarchy");
        await DeniedAsync(() => As(m).ExecuteAsync(new ModifyAccessRequest
        {
            Target = new EntityReference("account", notShared),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", other.Id), AccessMask = AccessRights.ReadAccess },
        }), "ModifyAccess needs Share too");

        // The grant landed: the other user now reads and writes the report's record.
        await RenameAsync(other, shared, "other");
        (await NameAsync(shared)).Should().Be($"{_tag}-other");
    }

    [Fact]
    public async Task Read_only_share_gives_read_only_to_manager()
    {
        var m = await UserAsync(AccountBasic);
        var r = await UserAsync(AccountBasic);
        var outsider = await UserAsync(AccountBasic);
        await ManagerAsync(r, m);
        var team = await CreateTeamAsync(fx, _root, 1, r.Id);
        var readOnly = await AccountAsync("read-only", outsider.Id);
        var readWrite = await AccountAsync("read-write", outsider.Id);
        var teamShare = await AccountAsync("team-share", outsider.Id);
        await ShareAsync(readOnly, r.Id, AccessRights.ReadAccess);
        await ShareAsync(readWrite, r.Id, AccessRights.ReadAccess | AccessRights.WriteAccess);
        await ShareAsync(teamShare, team, AccessRights.ReadAccess | AccessRights.WriteAccess);
        await SettingsAsync(enabled: true);

        (await As(m).RetrieveAsync("account", readOnly, new ColumnSet("name"))).Id.Should().Be(readOnly);
        await DeniedAsync(() => RenameAsync(m, readOnly, "m"), "the share with the report is read only");
        await RenameAsync(m, readWrite, "m");
        await RenameAsync(m, teamShare, "m-team");
        await DeniedAsync(() => As(m).DeleteAsync("account", readWrite), "a share never gives the manager Delete");
    }

    [Fact]
    public async Task Associate_with_reports_record_uses_append_rights()
    {
        var gm = await UserAsync(AccountBasic);
        var l1 = await UserAsync(AccountBasic);
        var l2 = await UserAsync(AccountBasic);
        await ManagerAsync(l1, gm);
        await ManagerAsync(l2, l1);
        var mine = await AccountAsync("gm", gm.Id);
        var parent = await AccountAsync("gm-parent", gm.Id);
        var of1 = await AccountAsync("l1", l1.Id);
        var of2 = await AccountAsync("l2", l2.Id);
        await SettingsAsync(enabled: true);

        // AppendTo on the direct report's record: the lookup can point to it.
        await As(gm).UpdateAsync(new Entity("account", mine) { ["parentaccountid"] = new EntityReference("account", of1) });
        (await _admin.RetrieveAsync("account", mine, new ColumnSet("parentaccountid")))
            .GetAttributeValue<EntityReference>("parentaccountid").Id.Should().Be(of1);

        // Level 2 gives Read only: no AppendTo.
        await DeniedAsync(() => As(gm).UpdateAsync(new Entity("account", mine) { ["parentaccountid"] = new EntityReference("account", of2) }),
            "level 2 gives no AppendTo");

        // Append on the direct report's record: its lookup can be set (Write and Append on it, AppendTo on the manager's own).
        await As(gm).UpdateAsync(new Entity("account", of1) { ["parentaccountid"] = new EntityReference("account", parent) });
        // The Associate message checks the same rights.
        await As(gm).ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("account", parent), Relationship = new Relationship("account_parent_account"),
            RelatedEntities = [new EntityReference("account", of1)],
        });
        await DeniedAsync(() => As(gm).ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("account", parent), Relationship = new Relationship("account_parent_account"),
            RelatedEntities = [new EntityReference("account", of2)],
        }), "level 2 gives no Append");
    }

    [Fact]
    public async Task Manager_downloads_files_of_reports_records()
    {
        var table = "new_hw" + Guid.NewGuid().ToString("N")[..10];
        var entity = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await _admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = table, Attribute = new FileAttributeMetadata { LogicalName = Doc, SchemaName = Doc },
        });
        (string, DepthMask)[] read = [($"prvRead{table}", DepthMask.Basic)];
        var m = await UserAsync(read);
        var r = await UserAsync(read);
        var peer = await UserAsync(read);
        await ManagerAsync(r, m);
        var id = await _admin.CreateAsync(new Entity(table) { ["new_name"] = _tag, ["ownerid"] = new EntityReference("systemuser", r.Id) });
        var content = Encoding.UTF8.GetBytes("hierarchy file " + _tag);
        var token = ((InitializeFileBlocksUploadResponse)await _admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = Doc, FileName = "a.txt",
        })).FileContinuationToken;
        var block = Convert.ToBase64String(Encoding.ASCII.GetBytes("block-000000"));
        await _admin.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = block, BlockData = content });
        await _admin.ExecuteAsync(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = [block], FileName = "a.txt", MimeType = "text/plain",
        });
        await SettingsAsync(enabled: true);

        var init = (InitializeFileBlocksDownloadResponse)await As(m).ExecuteAsync(new InitializeFileBlocksDownloadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = Doc,
        });
        var data = ((DownloadBlockResponse)await As(m).ExecuteAsync(new DownloadBlockRequest
        {
            FileContinuationToken = init.FileContinuationToken, Offset = 0, BlockLength = init.FileSizeInBytes,
        })).Data;
        data.Should().Equal(content);

        // The file's fileattachment row follows the record: readable by the manager, not by a peer.
        var attachments = new QueryExpression("fileattachment") { ColumnSet = new ColumnSet("objectid") };
        attachments.Criteria.AddCondition("objectid", ConditionOperator.Equal, id);
        (await As(m).RetrieveMultipleAsync(attachments)).Entities.Should().ContainSingle();
        (await As(peer).RetrieveMultipleAsync(attachments)).Entities.Should().BeEmpty();

        await DeniedAsync(() => As(peer).ExecuteAsync(new InitializeFileBlocksDownloadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = Doc,
        }), "a peer is not the report's manager");
        await SettingsAsync(enabled: false);
        await DeniedAsync(() => As(m).ExecuteAsync(new InitializeFileBlocksDownloadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = Doc,
        }), "the model is disabled");
    }
}
