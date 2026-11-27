using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3c1: probes of the core messages on the model. Task 1 pins what plug-ins see of the access values (<c>PrincipalAccess</c> in,
/// <c>AccessRights</c> out) and the bytes of an async payload that holds one; they pass before and after the access and privilege
/// values get their model twins. Task 2 pins the one model dispatch every core message enters.
/// </summary>
[Collection("xrmd")]
public sealed class CoreMessagesModelTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Project = TestEntities.Project;
    private const string PipeLog = TestEntities.PipeLog;
    private const string Plugins = "Xrmd.TestPlugins.";

    private readonly string _tag = "CM" + Guid.NewGuid().ToString("N");
    private OrganizationService _admin = null!;
    private Guid _assemblyId, _typeId, _principal, _project;

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await TestEntities.EnsureProjectAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{PipeLog}; DELETE FROM data.asyncoperation;");
        _admin = ServiceFor(fx, fx.AdminObjectId);
        var root = await RootBusinessUnitAsync(fx);
        _principal = await CreateUserAsync(fx, await CreateBusinessUnitAsync(fx, root, "U-" + _tag), Guid.NewGuid());
        await GrantAsync(fx, _principal, team: false, ("prvReadpb_Project", DepthMask.Basic));
        _project = await _admin.CreateAsync(new Entity(Project) { ["pb_name"] = _tag });
        _assemblyId = await PluginTestHelper.EnsureAssemblyAsync(_admin, PluginTestHelper.TestPluginBytes);
        _typeId = await PluginTestHelper.RegisterTypeAsync(_admin, _assemblyId, Plugins + "AccessValuesProbePlugin");
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{PipeLog}; DELETE FROM data.asyncoperation;");
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> ColumnAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var values = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) values.Add(Convert.ToString(rd.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        return values;
    }

    private Task<List<string>> LogsAsync() => ColumnAsync($"SELECT pb_name FROM data.{PipeLog} ORDER BY createdon");

    private Task<List<string>> MasksAsync() => ColumnAsync(
        "SELECT accessrightsmask FROM data.principalobjectaccess WHERE objectid = @record AND principalid = @principal",
        ("@record", _project), ("@principal", _principal));

    private GrantAccessRequest Grant(AccessRights mask) => new()
    {
        Target = new EntityReference(Project, _project),
        PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", _principal), AccessMask = mask },
    };

    private Task StepAsync(string message, int stage, string config, int mode = 0) =>
        PluginTestHelper.RegisterStepAsync(_admin, _typeId, message, Project, stage, mode, config: config);

    [Fact]
    public async Task GrantAccess_pre_operation_step_sees_PrincipalAccess()
    {
        await StepAsync("GrantAccess", 20, PipeLog);

        await _admin.ExecuteAsync(Grant(AccessRights.ReadAccess | AccessRights.ShareAccess));

        (await LogsAsync()).Should().Equal(
            $"PrincipalAccess|{typeof(PrincipalAccess).FullName}|systemuser|{_principal}|{(int)(AccessRights.ReadAccess | AccessRights.ShareAccess)}");
        (await MasksAsync()).Should().Equal(((int)(AccessRights.ReadAccess | AccessRights.ShareAccess)).ToString());

        // A step that replaces the mask changes what is stored.
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{PipeLog}; DELETE FROM data.principalobjectaccess WHERE objectid = '{_project}';");
        _assemblyId = await PluginTestHelper.EnsureAssemblyAsync(_admin, PluginTestHelper.TestPluginBytes);
        _typeId = await PluginTestHelper.RegisterTypeAsync(_admin, _assemblyId, Plugins + "AccessValuesProbePlugin");
        await StepAsync("GrantAccess", 20, $"{PipeLog}|mask={(int)AccessRights.WriteAccess}");

        await _admin.ExecuteAsync(Grant(AccessRights.ReadAccess));

        (await MasksAsync()).Should().Equal(((int)AccessRights.WriteAccess).ToString());
    }

    [Fact]
    public async Task RetrievePrincipalAccess_post_operation_sees_AccessRights()
    {
        await _admin.ExecuteAsync(Grant(AccessRights.ReadAccess));

        // RetrievePrincipalAccess is no plug-in message (the catalog has no step for it): what a post-operation step sees is the
        // response a nested service call returns, here the one the pipeline answers, whose result is typed AccessRights.
        var response = (RetrievePrincipalAccessResponse)await _admin.ExecuteAsync(new RetrievePrincipalAccessRequest
        {
            Target = new EntityReference(Project, _project),
            Principal = new EntityReference("systemuser", _principal),
        });

        response.AccessRights.Should().Be(AccessRights.ReadAccess);
        response.Results["AccessRights"].Should().BeOfType<AccessRights>().Which.Should().Be(AccessRights.ReadAccess);
    }

    [Fact]
    public async Task Async_step_on_GrantAccess_payload_is_unchanged()
    {
        await StepAsync("GrantAccess", 40, PipeLog, mode: 1);

        await _admin.ExecuteAsync(Grant(AccessRights.ReadAccess | AccessRights.ShareAccess));

        var data = (await ColumnAsync("SELECT data FROM data.asyncoperation")).Should().ContainSingle().Subject;
        // A context holding a PrincipalAccess is outside the model set: the payload is the SDK serializer's, and writing the
        // context it holds again gives the same bytes.
        var context = AsyncContextSerializer.Deserialize(data);
        context.InputParameters["PrincipalAccess"].Should().BeOfType<PrincipalAccess>();
        Xrmd.Abstractions.Sdk.SdkRemoteContext.ModelCanCarry(context).Should().BeFalse();
        AsyncContextSerializer.Serialize(context).Should().Be(data);
        data.Should().Contain("PrincipalAccess");
    }

    /// <summary>The bridged messages of Decision 1: their bodies belong to later steps (S.2e4, S.2f, S.2e3c3).</summary>
    private static readonly string[] Decision1Bridged =
    [
        "SendEmail", "GetTrackingTokenEmail", "PublishDuplicateRule", "UnpublishDuplicateRule", "RetrieveDuplicates", "BulkDetectDuplicates",
        "Upsert", "ExecuteCosmosSqlQuery", "CalculateRollupField",
        "InitializeFileBlocksUpload", "UploadBlock", "CommitFileBlocksUpload", "DeleteFile", "InitializeFileBlocksDownload", "DownloadBlock",
        "RetrieveAuditDetails", "RetrieveRecordChangeHistory", "RetrieveAttributeChangeHistory", "DeleteRecordChangeHistory", "DeleteAuditData",
        "RetrieveEntity", "RetrieveAttribute", "RetrieveRelationship", "RetrieveOptionSet", "RetrieveAllOptionSets", "RetrieveAllEntities",
    ];

    /// <summary>
    /// S.2e3c1 Task 2 (Decision 2): every core message and every message answered outside the pipeline enters the model dispatch
    /// (<see cref="CoreOperations.MessageRequest"/> sees the model request); the CRUD messages stay on the model, every other one
    /// crosses to its SDK handler through the bridge (<see cref="CoreHandOff.HandlerRequest"/> sees its SDK request). The bridge holds
    /// exactly the Decision 1 list (Task 6: <see cref="Bridged_messages_are_exactly_the_later_steps_ones"/>), and a name that is no core message keeps today's
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    [Fact]
    public async Task Every_core_message_enters_the_model_dispatch()
    {
        string[] crud = ["Create", "Retrieve", "Update", "Delete", "RetrieveMultiple", "Assign", "Associate", "Disassociate"];
        CoreSdkMessages.Bridged.Should().Contain(Decision1Bridged);
        CoreSdkMessages.Bridged.Should().NotContain(crud);

        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        var entered = new List<string?>();
        var handled = new List<string?>();

        // Each message without its parameters, in a transaction rolled back: the handler refuses it or reads nothing of interest.
        async Task<Exception?> RunAsync(Func<DataSession, Task> run)
        {
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            var session = new DataSession(conn, tx);
            try
            {
                await run(session);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
            finally
            {
                await tx.RollbackAsync();
                session.RolledBack();
            }
        }

        CoreOperations.MessageRequest.Value = r => entered.Add(r.Name);
        CoreHandOff.HandlerRequest.Value = r => handled.Add(r.RequestName);
        try
        {
            foreach (var name in CoreOperations.CoreMessages)
            {
                entered.Clear();
                handled.Clear();
                await RunAsync(s => core.ExecuteCoreAsync(new Xrmd.Model.Messages.Request { Name = name }, new CoreCall(caller, s), default));
                entered.Should().Equal([name], "{0} enters the model dispatch once", name);
                handled.Should().Equal(CoreSdkMessages.Bridged.Contains(name) ? [name] : [], "{0} reaches an SDK handler only when bridged", name);
            }
            foreach (var name in CoreOperations.NonPipelineMessages)
            {
                entered.Clear();
                handled.Clear();
                await RunAsync(s => core.ExecuteNonPipelineAsync(new Xrmd.Model.Messages.Request { Name = name }, caller, s, default));
                entered.Should().Equal([name], "{0} enters the model dispatch once", name);
                handled.Should().Equal(CoreSdkMessages.Bridged.Contains(name) ? [name] : [], "{0} reaches an SDK handler only when bridged", name);
            }

            entered.Clear();
            var unknown = await RunAsync(s => core.ExecuteCoreAsync(new Xrmd.Model.Messages.Request { Name = "NoSuchMessage" },
                new CoreCall(caller, s), default));
            unknown.Should().BeOfType<NotSupportedException>().Which.Message.Should().Be("Request 'NoSuchMessage' is not supported yet.");
            var unknownOutside = await RunAsync(s => core.ExecuteNonPipelineAsync(new Xrmd.Model.Messages.Request { Name = "NoSuchMessage" },
                caller, s, default));
            unknownOutside.Should().BeOfType<NotSupportedException>().Which.Message.Should().Be("Request 'NoSuchMessage' is not supported yet.");
            entered.Should().Equal("NoSuchMessage", "NoSuchMessage");
        }
        finally
        {
            CoreOperations.MessageRequest.Value = null;
            CoreHandOff.HandlerRequest.Value = null;
        }
    }
    /// <summary>The messages of Task 3: access, sharing and record teams.</summary>
    private static readonly string[] AccessMessageNames =
    [
        "GrantAccess", "ModifyAccess", "RevokeAccess", "RetrievePrincipalAccess", "RetrieveSharedPrincipalsAndAccess",
        "AddUserToRecordTeam", "RemoveUserFromRecordTeam",
    ];

    /// <summary>
    /// S.2e3c1 Task 3 (Decisions 1, 5 and 7): the access, sharing and record team messages are no longer bridged and run on the model
    /// request: <see cref="CoreHandOff.HandlerRequest"/> never fires for them, through the pipeline or outside it, and the caller still
    /// gets today's typed responses.
    /// </summary>
    [Fact]
    public async Task Access_messages_run_on_the_model()
    {
        CoreSdkMessages.Bridged.Should().NotContain(AccessMessageNames);

        var handled = new List<string?>();
        CoreHandOff.HandlerRequest.Value = r => handled.Add(r.RequestName);
        try
        {
            await _admin.ExecuteAsync(Grant(AccessRights.ReadAccess));
            await _admin.ExecuteAsync(new ModifyAccessRequest
            {
                Target = new EntityReference(Project, _project),
                PrincipalAccess = new PrincipalAccess
                {
                    Principal = new EntityReference("systemuser", _principal), AccessMask = AccessRights.ReadAccess | AccessRights.WriteAccess,
                },
            });
            (await MasksAsync()).Should().Equal(((int)(AccessRights.ReadAccess | AccessRights.WriteAccess)).ToString());

            // Outside the pipeline, as a plug-in calls them: the core's non-pipeline entry.
            using var scope = fx.App.Services.CreateScope();
            var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
            var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
            await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
            {
                var session = new DataSession(conn, null);
                var rights = (RetrievePrincipalAccessResponse)await core.ExecuteNonPipelineAsync(new RetrievePrincipalAccessRequest
                {
                    Target = new EntityReference(Project, _project),
                    Principal = new EntityReference("systemuser", _principal),
                }, caller, session, default);
                // The share's Write needs prvWrite, which the principal does not hold.
                rights.AccessRights.Should().Be(AccessRights.ReadAccess);
                var shared = (RetrieveSharedPrincipalsAndAccessResponse)await core.ExecuteNonPipelineAsync(
                    new RetrieveSharedPrincipalsAndAccessRequest { Target = new EntityReference(Project, _project) }, caller, session, default);
                shared.PrincipalAccesses.Should().ContainSingle().Which.AccessMask.Should().Be(AccessRights.ReadAccess | AccessRights.WriteAccess);
                shared.Results["PrincipalAccesses"].Should().BeOfType<PrincipalAccess[]>();
            }

            await _admin.ExecuteAsync(new RevokeAccessRequest
            {
                Target = new EntityReference(Project, _project),
                Revokee = new EntityReference("systemuser", _principal),
            });
            (await MasksAsync()).Should().BeEmpty();
            handled.Should().BeEmpty("the access messages no longer cross to an SDK handler");

            // Each message without its parameters (refused by its model body), through the core's entries.
            foreach (var name in AccessMessageNames)
            {
                await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
                await using var tx = await conn.BeginTransactionAsync();
                var session = new DataSession(conn, tx);
                var request = new Xrmd.Model.Messages.Request { Name = name };
                var run = CoreOperations.NonPipelineMessages.Contains(name)
                    ? () => core.ExecuteNonPipelineAsync(request, caller, session, default)
                    : (Func<Task>)(() => core.ExecuteCoreAsync(request, new CoreCall(caller, session), default));
                (await run.Should().ThrowAsync<Xrmd.Core.Errors.XrmdFaultException>()).Which.Message.Should().StartWith("Required parameter");
                await tx.RollbackAsync();
                session.RolledBack();
            }
            handled.Should().BeEmpty();
        }
        finally
        {
            CoreHandOff.HandlerRequest.Value = null;
        }
    }

    /// <summary>The messages of Task 4: role privileges, exchange rate and change tracking.</summary>
    private static readonly string[] RoleCurrencyChangeNames =
    [
        "AddPrivilegesRole", "ReplacePrivilegesRole", "RemovePrivilegeRole", "RetrieveUserPrivileges", "RetrieveExchangeRate",
        "RetrieveEntityChanges",
    ];

    /// <summary>Sets the organization's and the <c>role</c> table's audit switches; answers the ones they had.</summary>
    private async Task<(bool Organization, bool Role)> SetAuditAsync(bool organization, bool role)
    {
        var id = Guid.Parse((await ColumnAsync("SELECT organizationid FROM data.organization")).First());
        var wasOn = (await ColumnAsync("SELECT isauditenabled FROM data.organization")).First() is "1" or "True";
        var roleWasOn = fx.App.Services.GetRequiredService<IMetadataStore>().GetTable("role")!.IsAuditEnabled?.Value == true;
        await _admin.UpdateAsync(new Entity("organization", id) { ["isauditenabled"] = organization });
        await _admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.UpdateEntityRequest
        {
            Entity = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata { LogicalName = "role", IsAuditEnabled = new BooleanManagedProperty(role) },
        });
        return (wasOn, roleWasOn);
    }

    private Task<List<string>> RolePrivilegeRowsAsync(Guid role) => ColumnAsync(
        "SELECT CONCAT(privilegeid, '|', privilegedepthmask) FROM data.roleprivileges WHERE roleid = @role ORDER BY privilegeid", ("@role", role));

    private Task<List<string>> RoleAuditRowsAsync(Guid role) => ColumnAsync(
        "SELECT CONCAT(action, '|', operation, '|', objecttypecode, '|', changedata) FROM data.audit WHERE objectid = @role ORDER BY createdon, auditid",
        ("@role", role));

    /// <summary>
    /// S.2e3c1 Task 4 (Decisions 1, 5 and 7): the role privilege, exchange rate and change tracking messages are no longer bridged and run
    /// on the model request: <see cref="CoreHandOff.HandlerRequest"/> never fires for them, the caller gets today's typed responses, and an
    /// AddPrivilegesRole writes the <c>roleprivileges</c> rows and the audit row that the SDK body wrote (replayed here on a second role
    /// with the same SDK members it called).
    /// </summary>
    [Fact]
    public async Task Role_currency_and_change_messages_run_on_the_model()
    {
        CoreSdkMessages.Bridged.Should().NotContain(RoleCurrencyChangeNames);

        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var security = scope.ServiceProvider.GetRequiredService<SecurityAdministration>();
        var audit = scope.ServiceProvider.GetRequiredService<Xrmd.Audit.AuditWriter>();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataStore>();
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        var onModel = await CreateRoleAsync(fx, "m-" + _tag, ("prvReadpb_Project", DepthMask.Basic));
        var onSdk = await CreateRoleAsync(fx, "s-" + _tag, ("prvReadpb_Project", DepthMask.Basic));
        RolePrivilege[] Privileges() =>
        [
            new() { PrivilegeName = "prvWritepb_Project", Depth = PrivilegeDepth.Deep },
            new() { PrivilegeId = PrivilegeNames.IdOf("prvReadpb_Project"), Depth = PrivilegeDepth.Local },
        ];

        var handled = new List<string?>();
        CoreHandOff.HandlerRequest.Value = r => handled.Add(r.RequestName);
        var (organizationAudit, roleAudit) = await SetAuditAsync(true, true);
        try
        {
            (await _admin.ExecuteAsync(new AddPrivilegesRoleRequest { RoleId = onModel, Privileges = Privileges() }))
                .Should().BeOfType<AddPrivilegesRoleResponse>();

            // The SDK body of AddPrivilegesRole before Task 4, on the second role (its managed check and component marker do nothing for
            // an unmanaged role).
            await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
            {
                await using var tx = await conn.BeginTransactionAsync();
                var session = new DataSession(conn, tx);
                var requested = SecurityAdministration.FromSdk(Privileges());
                var before = await audit.IsAuditedAsync(session, store.GetEntity("role")!, default)
                    ? await security.RolePrivilegesAsync(session, onSdk, default)
                    : null;
                await security.SetRolePrivilegesAsync(session, onSdk, requested, false, default);
                var after = await security.RolePrivilegesAsync(session, onSdk, default);
                before.Should().NotBeNull();
                await audit.WriteAsync(session, caller, Xrmd.Audit.AuditActions.AddPrivilegesToRole, Xrmd.Audit.AuditOperations.Update,
                    new EntityReference("role", onSdk), new Xrmd.Audit.RolePrivilegesChange(before!, after), default);
                await tx.CommitAsync();
                session.Committed();
            }

            (await RolePrivilegeRowsAsync(onModel)).Should().Equal(await RolePrivilegeRowsAsync(onSdk)).And.HaveCount(2);
            var auditRows = await RoleAuditRowsAsync(onModel);
            auditRows.Should().ContainSingle().And.Equal(await RoleAuditRowsAsync(onSdk));

            (await _admin.ExecuteAsync(new ReplacePrivilegesRoleRequest
            {
                RoleId = onModel, Privileges = [new RolePrivilege { PrivilegeName = "prvCreatepb_Project", Depth = PrivilegeDepth.Global }],
            })).Should().BeOfType<ReplacePrivilegesRoleResponse>();
            (await _admin.ExecuteAsync(new RemovePrivilegeRoleRequest { RoleId = onModel, PrivilegeId = PrivilegeNames.IdOf("prvCreatepb_Project") }))
                .Should().BeOfType<RemovePrivilegeRoleResponse>();
            (await RolePrivilegeRowsAsync(onModel)).Should().BeEmpty();
            (await RoleAuditRowsAsync(onModel)).Should().HaveCount(3);

            // A null privilege is refused by the core with today's 400 (no conversion error on the way).
            var refused = await FluentActions.Awaiting(() => _admin.ExecuteAsync(new AddPrivilegesRoleRequest { RoleId = onModel, Privileges = [null!] }))
                .Should().ThrowAsync<Xrmd.Core.Errors.XrmdFaultException>();
            refused.Which.ErrorCode.Should().Be(Xrmd.Core.Errors.XrmdFaultException.InvalidPrivilege(Guid.Empty).ErrorCode);

            var baseCurrency = Guid.Parse((await ColumnAsync("SELECT basecurrencyid FROM data.organization")).First());
            var rate = await _admin.ExecuteAsync(new RetrieveExchangeRateRequest { TransactionCurrencyId = baseCurrency });
            rate.Should().BeOfType<RetrieveExchangeRateResponse>().Which.ExchangeRate.Should().Be(1m);

            // Outside the pipeline, as a plug-in calls it: the core's non-pipeline entry.
            await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
            {
                var privileges = await core.ExecuteNonPipelineAsync(new RetrieveUserPrivilegesRequest { UserId = _principal }, caller,
                    new DataSession(conn, null), default);
                var typed = privileges.Should().BeOfType<RetrieveUserPrivilegesResponse>().Subject;
                typed.Results["RolePrivileges"].Should().BeOfType<RolePrivilege[]>();
                typed.RolePrivileges.Should().ContainSingle(p => p.PrivilegeName == "prvReadpb_Project" && p.Depth == PrivilegeDepth.Basic);
            }
            handled.Should().BeEmpty("these messages no longer cross to an SDK handler");

            // Each message without its parameters (refused by its model body), through the core's entries.
            foreach (var name in RoleCurrencyChangeNames)
            {
                await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
                await using var tx = await conn.BeginTransactionAsync();
                var session = new DataSession(conn, tx);
                var request = new Xrmd.Model.Messages.Request { Name = name };
                var run = CoreOperations.NonPipelineMessages.Contains(name)
                    ? () => core.ExecuteNonPipelineAsync(request, caller, session, default)
                    : (Func<Task>)(() => core.ExecuteCoreAsync(request, new CoreCall(caller, session), default));
                (await run.Should().ThrowAsync<Xrmd.Core.Errors.XrmdFaultException>()).Which.Message.Should().StartWith("Required parameter");
                await tx.RollbackAsync();
                session.RolledBack();
            }
            handled.Should().BeEmpty();
        }
        finally
        {
            CoreHandOff.HandlerRequest.Value = null;
            await SetAuditAsync(organizationAudit, roleAudit);
        }
    }
    /// <summary>The messages of Task 5: queues.</summary>
    private static readonly string[] QueueMessageNames =
    [
        "AddToQueue", "RemoveFromQueue", "RouteTo", "AddPrincipalToQueue", "PickFromQueue", "ReleaseToQueue", "RetrieveUserQueues",
    ];

    /// <summary>
    /// S.2e3c1 Task 5 (Decisions 1, 4 and 7): the queue messages are no longer bridged and run on the model request. The item and
    /// membership writes they issue through the nested delegate are model requests whose SDK twins are today's request classes
    /// (<see cref="CoreResponses.TypedRequest"/>): a pre-operation step on the <c>queueitem</c> Create during AddToQueue sees a
    /// <c>CreateRequest</c> whose Target is an <c>Entity</c> with today's columns and value types, a step on the Associate of
    /// <c>queuemembership_association</c> during AddPrincipalToQueue sees an <c>AssociateRequest</c>, and the caller gets today's typed
    /// responses. <see cref="CoreHandOff.HandlerRequest"/> never fires for them.
    /// </summary>
    [Fact]
    public async Task Queue_write_steps_see_todays_requests()
    {
        CoreSdkMessages.Bridged.Should().NotContain(QueueMessageNames);

        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        Task<Guid> NewQueueAsync(string name) => _admin.CreateAsync(new Entity("queue")
        {
            ["name"] = $"{name}-{_tag}", ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1),
        });
        var queue = await NewQueueAsync("q");
        var next = await NewQueueAsync("n");
        var email = await _admin.CreateAsync(new Entity("email") { ["subject"] = _tag });
        var recorder = await PluginTestHelper.RegisterTypeAsync(_admin, _assemblyId, Plugins + "ContextRecorderPlugin");
        await PluginTestHelper.RegisterStepAsync(_admin, recorder, "Create", "queueitem", 20, config: PipeLog);
        await PluginTestHelper.RegisterStepAsync(_admin, recorder, "Associate", null, 20, config: PipeLog);

        var nested = new List<Xrmd.Model.Messages.Request>();
        var handled = new List<string?>();
        CoreOperations.CrudRequest.Value = r =>
        {
            if (r.Parameters.TryGetValue("Target", out var t) && (t is Xrmd.Model.Records.Record { LogicalName: "queueitem" }
                    or Xrmd.Model.Records.RecordReference { LogicalName: "queueitem" or "queue" }))
                nested.Add(r);
        };
        CoreHandOff.HandlerRequest.Value = r => handled.Add(r.RequestName);
        try
        {
            var added = await _admin.ExecuteAsync(new AddToQueueRequest { Target = new EntityReference("email", email), DestinationQueueId = queue });
            var item = added.Should().BeOfType<AddToQueueResponse>().Subject.QueueItemId;
            (await _admin.ExecuteAsync(new RouteToRequest { Target = new EntityReference("queue", next), QueueItemId = item }))
                .Should().BeOfType<RouteToResponse>();
            (await _admin.ExecuteAsync(new AddPrincipalToQueueRequest { QueueId = queue, Principal = new Entity("systemuser", _principal) }))
                .Should().BeOfType<AddPrincipalToQueueResponse>();
            (await _admin.ExecuteAsync(new PickFromQueueRequest { QueueItemId = item, WorkerId = _principal }))
                .Should().BeOfType<PickFromQueueResponse>();
            (await _admin.ExecuteAsync(new ReleaseToQueueRequest { QueueItemId = item })).Should().BeOfType<ReleaseToQueueResponse>();
            var queues = await _admin.ExecuteAsync(new RetrieveUserQueuesRequest { UserId = _principal, IncludePublic = true });
            queues.Should().BeOfType<RetrieveUserQueuesResponse>().Which.EntityCollection.Entities.Select(e => e.Id)
                .Should().Contain([queue, next]);
            (await _admin.ExecuteAsync(new RemoveFromQueueRequest { QueueItemId = item })).Should().BeOfType<RemoveFromQueueResponse>();
            handled.Should().BeEmpty("the queue messages no longer cross to an SDK handler");

            // The nested writes: model requests whose SDK twins are today's request classes.
            nested.Select(r => r.Name).Should().Equal("Create", "Update", "Associate", "Update", "Update", "Delete");
            object Twin(Xrmd.Model.Messages.Request r)
            {
                SdkMirror.For(r)!.TryGetSdk(r, out var twin).Should().BeTrue();
                return twin!;
            }
            var create = Twin(nested[0]).Should().BeOfType<CreateRequest>().Subject;
            create.Target.Attributes.Keys.Should().BeEquivalentTo(["queueid", "objectid", "title", "enteredon"]);
            create.Target["queueid"].Should().BeOfType<EntityReference>();
            create.Target["objectid"].Should().BeOfType<EntityReference>();
            create.Target["enteredon"].Should().BeOfType<DateTime>();
            Twin(nested[1]).Should().BeOfType<UpdateRequest>().Which.Target.Attributes.Keys.Should().BeEquivalentTo(["queueid", "enteredon"]);
            var associate = Twin(nested[2]).Should().BeOfType<AssociateRequest>().Subject;
            associate.Relationship.SchemaName.Should().Be(QueueSchema.MembershipRelationshipName);
            associate.RelatedEntities.Should().ContainSingle().Which.Id.Should().Be(_principal);
            Twin(nested[3]).Should().BeOfType<UpdateRequest>().Which.Target["workerid"].Should().BeOfType<EntityReference>();
            Twin(nested[4]).Should().BeOfType<UpdateRequest>().Which.Target["workerid"].Should().BeNull();
            Twin(nested[5]).Should().BeOfType<DeleteRequest>().Which.Target.Id.Should().Be(item);

            // What the pre-operation steps saw, one level deeper, with the queue message as their parent.
            (await LogsAsync()).Should().Equal("Create|20|0|2", "Associate|20|0|2");
            var data = await ColumnAsync($"SELECT pb_data FROM data.{PipeLog} ORDER BY createdon");
            data[0].Should().StartWith("target=enteredon,objectid,queueid,title\n").And.Contain("\nparent=AddToQueue\n");
            data[1].Should().Contain("\nparent=AddPrincipalToQueue\n");

            // Each message without its parameters (refused by its model body), through the core's entry.
            foreach (var name in QueueMessageNames)
            {
                await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
                await using var tx = await conn.BeginTransactionAsync();
                var session = new DataSession(conn, tx);
                (await FluentActions.Awaiting(() => core.ExecuteCoreAsync(new Xrmd.Model.Messages.Request { Name = name }, new CoreCall(caller, session), default))
                    .Should().ThrowAsync<Xrmd.Core.Errors.XrmdFaultException>()).Which.Message.Should().StartWith("Required parameter");
                await tx.RollbackAsync();
                session.RolledBack();
            }
            handled.Should().BeEmpty();
        }
        finally
        {
            CoreOperations.CrudRequest.Value = null;
            CoreHandOff.HandlerRequest.Value = null;
        }
    }

    /// <summary>
    /// S.2e3c1 Task 6 (Decision 1): the SDK message bridge holds exactly the messages whose bodies belong to later steps (mail,
    /// duplicates, elastic and rollups: S.2e4; files and audit: S.2f; the metadata reads: S.2e3c3). Every other core message runs on
    /// the model.
    /// </summary>
    [Fact]
    public void Bridged_messages_are_exactly_the_later_steps_ones() =>
        CoreSdkMessages.Bridged.Should().BeEquivalentTo(Decision1Bridged);

    /// <summary>The messages answered outside the pipeline that Task 6 moves (the refusals are added in the test).</summary>
    private static readonly string[] NonPipelineReadNames =
    [
        "WhoAmI", "RetrieveUserPrivileges", "RetrieveAppComponents", "RetrieveUnpublished", "InitializeFrom", "RetrieveFormattedImportJobResults",
    ];

    /// <summary>
    /// S.2e3c1 Task 6 (Decisions 1 and 7, review focus 5): the non-pipeline reads (WhoAmI, RetrieveUserPrivileges, RetrieveAppComponents,
    /// RetrieveUnpublished, InitializeFrom, RetrieveFormattedImportJobResults), the refusals of the caller-only and metadata-write messages
    /// and ExecuteWorkflow are no longer bridged: <see cref="CoreHandOff.HandlerRequest"/> never fires for them. RetrievePrincipalAccess and
    /// WhoAmI from a plug-in run in its session and see its uncommitted row; the refusal of <c>ExportSolution</c> from a plug-in keeps its
    /// 400 text; the caller gets today's typed responses; ExecuteWorkflow keeps its <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public async Task Non_pipeline_reads_run_on_the_model()
    {
        var refusals = CoreOperations.CallerOnlyMessages.Concat(MetadataOperations.Messages.Where(MetadataOperations.IsWriteMessage))
            .Append("ImportSolution").ToArray();
        CoreSdkMessages.Bridged.Should().NotContain(NonPipelineReadNames);
        CoreSdkMessages.Bridged.Should().NotContain(refusals);
        CoreSdkMessages.Bridged.Should().NotContain(PipelineExecutor.ExecuteWorkflowMessage);

        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        var handled = new List<string?>();
        CoreHandOff.HandlerRequest.Value = r => handled.Add(r.RequestName);
        try
        {
            // From plug-ins, in their transaction: RetrievePrincipalAccess on the uncommitted Target and WhoAmI (on the pipe's Create),
            // and the refused ExportSolution (on the Create of the log row the first plug-in writes).
            var access = await PluginTestHelper.RegisterTypeAsync(_admin, _assemblyId, Plugins + "AccessMessagesPlugin");
            await PluginTestHelper.RegisterStepAsync(_admin, access, "Create", TestEntities.Pipe, 40, config: PipeLog);
            var calls = await PluginTestHelper.RegisterTypeAsync(_admin, _assemblyId, Plugins + "NonPipelineCallsPlugin");
            await PluginTestHelper.RegisterStepAsync(_admin, calls, "Create", TestEntities.Pipe, 20, config: "caller");
            var tryExecute = await PluginTestHelper.RegisterTypeAsync(_admin, _assemblyId, Plugins + "TryExecutePlugin");
            await PluginTestHelper.RegisterStepAsync(_admin, tryExecute, "Create", PipeLog, 20, config: "ExportSolution");
            var organizationId = Guid.Parse((await ColumnAsync("SELECT organizationid FROM data.organization")).Single());

            var pipe = await _admin.CreateAsync(new Entity(TestEntities.Pipe) { ["pb_name"] = _tag });

            var log = (await LogsAsync()).Should().ContainSingle().Subject;
            log.Should().StartWith("rights=").And.EndWith("|shares=0");
            ((AccessRights)int.Parse(log["rights=".Length..log.IndexOf('|', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture))
                .Should().HaveFlag(AccessRights.ReadAccess, "the plug-in's read sees the row its transaction has not committed");
            (await ColumnAsync($"SELECT pb_data FROM data.{TestEntities.Pipe} WHERE {TestEntities.Pipe}id = @id", ("@id", pipe))).Should().Equal(
                $"user={caller.UserId}|bu={caller.BusinessUnitId}|org={organizationId}|entity={TestEntities.Pipe}|attr=pb_name");
            (await ColumnAsync($"SELECT pb_data FROM data.{PipeLog}")).Should().Equal(
                $"caught {Xrmd.Core.Errors.ErrorCodes.InvalidArgument}|The message 'ExportSolution' cannot be executed from a plug-in in xrmverse.");
            handled.Should().NotContain(["WhoAmI", "RetrievePrincipalAccess", "RetrieveSharedPrincipalsAndAccess", "ExportSolution"],
                "these run on the model (RetrieveEntity and RetrieveAttribute, called by the same plug-in, stay bridged)");
            handled.Should().Contain(["RetrieveEntity", "RetrieveAttribute"]);
            handled.Clear();

            // Through the core's non-pipeline entry: today's typed responses.
            await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
            {
                var session = new DataSession(conn, null);
                var me = (WhoAmIResponse)await core.ExecuteNonPipelineAsync(new WhoAmIRequest(), caller, session, default);
                (me.UserId, me.BusinessUnitId, me.OrganizationId).Should().Be((caller.UserId, caller.BusinessUnitId, organizationId));
                var privileges = await core.ExecuteNonPipelineAsync(new RetrieveUserPrivilegesRequest { UserId = caller.UserId }, caller, session, default);
                privileges.Should().BeOfType<RetrieveUserPrivilegesResponse>().Which.RolePrivileges.Should().NotBeEmpty();
            }

            // Each message without its parameters (refused by its model body) and each refusal, through the core's entry.
            foreach (var name in NonPipelineReadNames.Where(n => n != "WhoAmI").Concat(refusals))
            {
                await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
                await using var tx = await conn.BeginTransactionAsync();
                var session = new DataSession(conn, tx);
                var fault = (await FluentActions.Awaiting(() => core.ExecuteNonPipelineAsync(new Xrmd.Model.Messages.Request { Name = name }, caller, session, default))
                    .Should().ThrowAsync<Xrmd.Core.Errors.XrmdFaultException>()).Which;
                fault.HttpStatus.Should().Be(400);
                fault.Message.Should().Be(name switch
                {
                    _ when refusals.Take(CoreOperations.CallerOnlyMessages.Length).Contains(name) =>
                        $"The message '{name}' cannot be executed from a plug-in in xrmverse.",
                    _ when refusals.Contains(name) => $"The metadata message '{name}' cannot be executed from a plug-in.",
                    "RetrieveUserPrivileges" => $"Required parameter 'UserId' of request '{name}' is missing.",
                    "RetrieveAppComponents" => $"Required parameter 'AppModuleId' of request '{name}' is missing.",
                    "RetrieveUnpublished" => $"Required parameter 'Target' of request '{name}' is missing.",
                    "InitializeFrom" => $"Required parameter 'EntityMoniker' of request '{name}' is missing.",
                    _ => $"Required parameter 'ImportJobId' of request '{name}' is missing.",
                });
                await tx.RollbackAsync();
                session.RolledBack();
            }

            // ExecuteWorkflow at the core: today's InvalidOperationException.
            await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
            {
                await using var tx = await conn.BeginTransactionAsync();
                var session = new DataSession(conn, tx);
                (await FluentActions.Awaiting(() => core.ExecuteCoreAsync(new Xrmd.Model.Messages.Request { Name = PipelineExecutor.ExecuteWorkflowMessage },
                        new CoreCall(caller, session), default)).Should().ThrowAsync<InvalidOperationException>())
                    .Which.Message.Should().Be("ExecuteWorkflow runs through the pipeline, which hands it to the classic workflows.");
                await tx.RollbackAsync();
                session.RolledBack();
            }
            handled.Should().BeEmpty("the non-pipeline reads, the refusals and ExecuteWorkflow no longer cross to an SDK handler");
        }
        finally
        {
            CoreHandOff.HandlerRequest.Value = null;
        }
    }
}
