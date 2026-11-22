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

namespace Xrmd.Integration.Tests;

/// <summary>Assign, SetState (via Update), 1:N Associate/Disassociate and custom actions through the pipeline.</summary>
[Collection("xrmd")]
public sealed class PipelineMessagesTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Pipe = TestEntities.Pipe;
    private const string PipeLog = TestEntities.PipeLog;
    private const string PipeParent = TestEntities.PipeParent;
    private const string Relationship = TestEntities.PipeParentRelationship;
    private const string Plugins = "Xrmd.TestPlugins.";

    private OrganizationService _svc = null!;
    private Guid _assemblyId;
    private Guid _adminId;
    private Guid _businessUnitId;
    private readonly Dictionary<string, Guid> _types = new();
    private readonly List<Guid> _users = [];

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeParentAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{Pipe}; DELETE FROM data.{PipeLog}; DELETE FROM data.{PipeParent};");
        _svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        _assemblyId = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.TestPluginBytes);

        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT systemuserid, businessunitid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid");
        cmd.Parameters.AddWithValue("@oid", fx.AdminObjectId);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        (_adminId, _businessUnitId) = (rd.GetGuid(0), rd.GetGuid(1));
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        foreach (var id in _users)
            await ExecAsync($"DELETE FROM data.{Pipe} WHERE ownerid = '{id}'; DELETE FROM data.{PipeLog} WHERE ownerid = '{id}'; " +
                            $"DELETE FROM data.systemuser WHERE systemuserid = '{id}';");
    }

    // ---- helpers ----

    private async Task<Guid> StepAsync(string plugin, string message, string? entity, int stage, string? config = null,
        string? filteringAttributes = null)
    {
        if (!_types.TryGetValue(plugin, out var typeId))
            _types[plugin] = typeId = await PluginTestHelper.RegisterTypeAsync(_svc, _assemblyId, Plugins + plugin);
        return await PluginTestHelper.RegisterStepAsync(_svc, typeId, message, entity, stage, config: config,
            filteringAttributes: filteringAttributes);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The <c>pb_pipelog</c> rows written by ContextRecorderPlugin: name <c>Message|Stage|Mode|Depth</c> → data lines.</summary>
    private async Task<Dictionary<string, string[]>> LogsAsync()
    {
        var logs = new Dictionary<string, string[]>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data FROM data.{PipeLog}");
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) logs[rd.GetString(0)] = rd.IsDBNull(1) ? [] : rd.GetString(1).Split('\n');
        return logs;
    }

    private async Task<Guid> NewUserAsync()
    {
        var id = Guid.NewGuid();
        _users.Add(id);
        await ExecAsync($"""
            INSERT INTO data.systemuser (systemuserid, fullname, domainname, azureactivedirectoryobjectid, businessunitid,
                owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode)
            VALUES ('{id}', N'Assignee', N'assignee', NULL, '{_businessUnitId}', '{_businessUnitId}', SYSUTCDATETIME(),
                '{_adminId}', SYSUTCDATETIME(), '{_adminId}', 0, 1)
            """);
        // A new owner must hold prvRead on the entity (rule of Assign).
        await SecurityTestHelper.GrantAsync(fx, id, team: false, ("prvReadpb_Pipe", DepthMask.Basic));
        return id;
    }

    private Task<Guid> CreatePipeAsync(string name = "p") => _svc.CreateAsync(new Entity(Pipe) { ["pb_name"] = name });

    private async Task<Guid?> ParentOfAsync(Guid pipeId) =>
        (await _svc.RetrieveAsync(Pipe, pipeId, new ColumnSet("pb_parentid"))).GetAttributeValue<EntityReference>("pb_parentid")?.Id;

    /// <summary>Creates the custom <c>sdkmessage</c> row <paramref name="name"/> (with the catalog's stable id for that name).</summary>
    private Task<Guid> CustomMessageAsync(string name) =>
        _svc.CreateAsync(new Entity("sdkmessage", SdkMessageCatalog.MessageId(name)) { ["name"] = name, ["categoryname"] = "CustomAction" });

    // ---- tests ----

    [Fact]
    public async Task Assign_changes_owner_and_fires_assign_steps()
    {
        var step = await StepAsync("ContextRecorderPlugin", "Assign", Pipe, 40, PipeLog);
        await PluginTestHelper.RegisterImageAsync(_svc, step, 2, "img", "ownerid");
        var id = await CreatePipeAsync();
        var assignee = await NewUserAsync();

        var response = await _svc.ExecuteAsync(new AssignRequest
        {
            Target = new EntityReference(Pipe, id),
            Assignee = new EntityReference("systemuser", assignee),
        });

        response.Should().BeOfType<AssignResponse>();
        var record = await _svc.RetrieveAsync(Pipe, id, new ColumnSet("ownerid", "owninguser"));
        record.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(assignee);
        record.GetAttributeValue<EntityReference>("owninguser").Id.Should().Be(assignee);
        var logs = await LogsAsync();
        logs.Should().ContainKey("Assign|40|0|1");
        logs["Assign|40|0|1"].Should().Contain("preowner=" + _adminId).And.Contain("postowner=" + assignee);
    }

    [Fact]
    public async Task Assign_to_missing_user_is_404()
    {
        var id = await CreatePipeAsync();

        var act = () => _svc.ExecuteAsync(new AssignRequest
        {
            Target = new EntityReference(Pipe, id),
            Assignee = new EntityReference("systemuser", Guid.NewGuid()),
        });

        await act.Should().ThrowAsync<XrmdFaultException>().Where(f => f.HttpStatus == 404);
    }

    [Fact]
    public async Task SetState_runs_update_pipeline()
    {
        await StepAsync("ContextRecorderPlugin", "Update", Pipe, 20, PipeLog, filteringAttributes: "statecode");
        var id = await CreatePipeAsync();

        var response = await _svc.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(Pipe, id),
            State = new OptionSetValue(1),
            Status = new OptionSetValue(2),
        });

        response.Should().BeOfType<SetStateResponse>();
        var record = await _svc.RetrieveAsync(Pipe, id, new ColumnSet("statecode", "statuscode"));
        record.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
        record.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(2);
        var logs = await LogsAsync();
        logs.Should().ContainKey("Update|20|0|1");
        logs["Update|20|0|1"].Should().Contain("target=statecode,statuscode");
    }

    [Fact]
    public async Task SetState_with_invalid_state_status_pair_is_400()
    {
        var id = await CreatePipeAsync();

        var act = () => _svc.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(Pipe, id),
            State = new OptionSetValue(0),
            Status = new OptionSetValue(2),
        });

        await act.Should().ThrowAsync<XrmdFaultException>()
            .Where(f => f.HttpStatus == 400 && f.ErrorCode == ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task SetState_with_status_minus_one_uses_the_default_status()
    {
        var id = await CreatePipeAsync();

        await _svc.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(Pipe, id),
            State = new OptionSetValue(1),
            Status = new OptionSetValue(-1),
        });

        var record = await _svc.RetrieveAsync(Pipe, id, new ColumnSet("statecode", "statuscode"));
        record.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
        record.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(2);
    }

    [Fact]
    public async Task Update_of_statecode_alone_sets_its_default_status()
    {
        var id = await CreatePipeAsync();

        await _svc.UpdateAsync(new Entity(Pipe, id) { ["statecode"] = new OptionSetValue(1) });

        var record = await _svc.RetrieveAsync(Pipe, id, new ColumnSet("statecode", "statuscode"));
        record.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
        record.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(2);
    }

    [Fact]
    public async Task Associate_one_to_many_sets_lookup()
    {
        await StepAsync("ContextRecorderPlugin", "Associate", null, 40, PipeLog);
        var parent = await _svc.CreateAsync(new Entity(PipeParent) { ["pb_name"] = "parent" });
        var a = await CreatePipeAsync("a");
        var b = await CreatePipeAsync("b");

        _svc.Associate(PipeParent, parent, new Relationship(Relationship),
            new EntityReferenceCollection { new(Pipe, a), new(Pipe, b) });

        (await ParentOfAsync(a)).Should().Be(parent);
        (await ParentOfAsync(b)).Should().Be(parent);
        (await LogsAsync()).Should().ContainKey("Associate|40|0|1");
    }

    [Fact]
    public async Task Associate_failure_rolls_back_every_related_record()
    {
        var parent = await _svc.CreateAsync(new Entity(PipeParent) { ["pb_name"] = "parent" });
        var a = await CreatePipeAsync("a");

        var act = () => _svc.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference(PipeParent, parent),
            Relationship = new Relationship(Relationship),
            RelatedEntities = new EntityReferenceCollection { new(Pipe, a), new(Pipe, Guid.NewGuid()) },
        });

        await act.Should().ThrowAsync<XrmdFaultException>().Where(f => f.HttpStatus == 404);
        (await ParentOfAsync(a)).Should().BeNull();
    }

    [Fact]
    public async Task Disassociate_clears_lookup()
    {
        await StepAsync("ContextRecorderPlugin", "Disassociate", null, 40, PipeLog);
        var parent = await _svc.CreateAsync(new Entity(PipeParent) { ["pb_name"] = "parent" });
        var a = await _svc.CreateAsync(new Entity(Pipe) { ["pb_name"] = "a", ["pb_parentid"] = new EntityReference(PipeParent, parent) });

        _svc.Disassociate(PipeParent, parent, new Relationship(Relationship), new EntityReferenceCollection { new(Pipe, a) });

        (await ParentOfAsync(a)).Should().BeNull();
        (await LogsAsync()).Should().ContainKey("Disassociate|40|0|1");
    }

    [Fact]
    public async Task Disassociate_leaves_lookups_to_another_record_untouched()
    {
        var parent = await _svc.CreateAsync(new Entity(PipeParent) { ["pb_name"] = "parent" });
        var other = await _svc.CreateAsync(new Entity(PipeParent) { ["pb_name"] = "other" });
        var a = await _svc.CreateAsync(new Entity(Pipe) { ["pb_name"] = "a", ["pb_parentid"] = new EntityReference(PipeParent, parent) });
        var b = await _svc.CreateAsync(new Entity(Pipe) { ["pb_name"] = "b", ["pb_parentid"] = new EntityReference(PipeParent, other) });

        _svc.Disassociate(PipeParent, parent, new Relationship(Relationship),
            new EntityReferenceCollection { new(Pipe, a), new(Pipe, b) });

        (await ParentOfAsync(a)).Should().BeNull();
        (await ParentOfAsync(b)).Should().Be(other);
    }

    [Fact]
    public async Task Disassociate_of_missing_target_is_404()
    {
        var parent = await _svc.CreateAsync(new Entity(PipeParent) { ["pb_name"] = "parent" });
        var a = await _svc.CreateAsync(new Entity(Pipe) { ["pb_name"] = "a", ["pb_parentid"] = new EntityReference(PipeParent, parent) });

        var act = () => _svc.ExecuteAsync(new DisassociateRequest
        {
            Target = new EntityReference(PipeParent, Guid.NewGuid()),
            Relationship = new Relationship(Relationship),
            RelatedEntities = new EntityReferenceCollection { new(Pipe, a) },
        });

        await act.Should().ThrowAsync<XrmdFaultException>()
            .Where(f => f.HttpStatus == 404 && f.ErrorCode == ErrorCodes.ObjectDoesNotExist);
        (await ParentOfAsync(a)).Should().Be(parent);
    }

    [Fact]
    public async Task Associate_owner_relationship_is_400()
    {
        var a = await CreatePipeAsync("a");
        var assignee = await NewUserAsync();
        const string owner = $"{Pipe}_ownerid_systemuser";

        var act = () => _svc.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("systemuser", assignee),
            Relationship = new Relationship(owner),
            RelatedEntities = new EntityReferenceCollection { new(Pipe, a) },
        });

        await act.Should().ThrowAsync<XrmdFaultException>()
            .Where(f => f.HttpStatus == 400 && f.Message == $"Relationship '{owner}' is not supported");
    }

    [Fact]
    public async Task Core_message_names_are_case_insensitive()
    {
        var id = await CreatePipeAsync();
        var assignee = await NewUserAsync();

        await _svc.ExecuteAsync(new OrganizationRequest("assign")
        {
            ["Target"] = new EntityReference(Pipe, id),
            ["Assignee"] = new EntityReference("systemuser", assignee),
        });

        (await _svc.RetrieveAsync(Pipe, id, new ColumnSet("ownerid"))).GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(assignee);
    }

    [Fact]
    public async Task Associate_unknown_relationship_is_400()
    {
        var parent = await _svc.CreateAsync(new Entity(PipeParent) { ["pb_name"] = "parent" });
        var a = await CreatePipeAsync("a");

        var act = () => _svc.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference(PipeParent, parent),
            Relationship = new Relationship("pb_no_such_relationship"),
            RelatedEntities = new EntityReferenceCollection { new(Pipe, a) },
        });

        await act.Should().ThrowAsync<XrmdFaultException>()
            .Where(f => f.HttpStatus == 400 && f.Message == "Relationship 'pb_no_such_relationship' is not supported");
    }

    [Fact]
    public async Task Custom_action_returns_plugin_outputs()
    {
        await CustomMessageAsync("pb_Echo");
        await StepAsync("EchoActionPlugin", "pb_Echo", null, 40);

        var response = await _svc.ExecuteAsync(new OrganizationRequest("pb_Echo") { ["Text"] = "abc" });

        response.ResponseName.Should().Be("pb_Echo");
        response.Results["Result"].Should().Be("cba");
    }

    [Fact]
    public async Task Unknown_message_is_still_not_supported()
    {
        var act = () => _svc.ExecuteAsync(new OrganizationRequest("pb_NoSuchMessage"));

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    // ---- plan 2.11, A18: the activity and note messages xrmverse refuses ----

    [Fact]
    public async Task Book_is_refused()
    {
        var act = () => _svc.ExecuteAsync(new OrganizationRequest("Book") { ["Target"] = new Entity("appointment") });

        (await act.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain("'Book'").And.Contain("not supported");
    }

    [Fact]
    public async Task DeliverPromoteEmail_is_refused()
    {
        var act = () => _svc.ExecuteAsync(new OrganizationRequest("DeliverPromoteEmail") { ["Subject"] = "x" });

        (await act.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain("'DeliverPromoteEmail'").And.Contain("not supported");
    }

    [Fact]
    public async Task InitializeAnnotationBlocksUpload_is_refused()
    {
        // Ruling C14: the refusal test of the chunked note upload lives here (A16, A18); notes take documentbody instead.
        var act = () => _svc.ExecuteAsync(new OrganizationRequest("InitializeAnnotationBlocksUpload")
        {
            ["Target"] = new Entity("annotation") { ["subject"] = "x" },
        });

        (await act.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain("documentbody");
    }

    [Fact]
    public async Task Every_refused_activity_message_is_not_supported()
    {
        var executor = fx.App.Services.CreateScope().ServiceProvider.GetRequiredService<Xrmd.Pipeline.PipelineExecutor>();
        EmailMessages.Refused.Should().Contain(["Book", "Reschedule", "DeliverPromoteEmail", "DeliverIncomingEmail", "CheckIncomingEmail",
            "CheckPromoteEmail", "BackgroundSendEmail", "SendEmailFromTemplate", "SendTemplate", "InstantiateTemplate", "GetTrackingTokenEmail",
            "AddRecurrence", "Rollup", "Restore", "AppendRelatedParty", "InitializeAnnotationBlocksUpload", "InitializeAnnotationBlocksDownload"]);
        EmailMessages.Refused.Should().NotContain(EmailMessages.SendEmail);
        foreach (var name in EmailMessages.Refused)
        {
            // Even when an organization holds an sdkmessage row of that name, the message stays refused.
            var act = () => _svc.ExecuteAsync(new OrganizationRequest(name));
            (await act.Should().ThrowAsync<NotSupportedException>(name)).Which.Message.Should().Contain($"'{name}'");
            // No custom API may take the name (pre-flight C15).
            executor.IsReservedMessageName(name).Should().BeTrue(name);
            executor.IsReservedMessageName(name.ToLowerInvariant()).Should().BeTrue(name);
        }
        executor.IsReservedMessageName(EmailMessages.SendEmail).Should().BeTrue();
    }

    [Fact]
    public async Task Refused_message_stays_refused_with_a_custom_sdkmessage_row()
    {
        await CustomMessageAsync("Rollup");
        await StepAsync("EchoActionPlugin", "Rollup", null, 40);

        var act = () => _svc.ExecuteAsync(new OrganizationRequest("Rollup") { ["Text"] = "abc" });

        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
