using System.Data.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Core.Rollups;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;
using Xrmd.Pipeline.Mail;
using Record = Xrmd.Model.Records.Record;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3b Task 4 (plan Decision 5): each write bridge of <see cref="CoreSdkBridges"/> skips its SDK helper when the helper's own guard
/// says it does nothing (no crossing counted, the row untouched) and otherwise crosses once, the helper's change read back into the
/// same <see cref="Record"/> (or its fault raised). The crossings are counted for this test's flow only
/// (<see cref="CoreSdkBridges.CountCrossings"/>), so tests running in parallel do not disturb them. Task 5 adds the bridges after the write
/// (mail, audit) and the claim of the mail secrets.
/// </summary>
[Collection("xrmd")]
public sealed class CoreSdkBridgeTests(XrmdFixture fx)
{
    private const string FileTable = "new_crbfile";

    private static readonly Guid Unknown = CoreScenarioKit.UnknownId;

    /// <summary>The crossings <paramref name="call"/> makes in this flow; a fault it raises is returned, not thrown.</summary>
    private static async Task<(long Crossings, Exception? Fault)> CrossingsOf(Func<Task> call)
    {
        using var count = CoreSdkBridges.CountCrossings();
        try
        {
            await call();
            return (count.Value, null);
        }
        catch (XrmdFaultException ex)
        {
            return (count.Value, ex);
        }
    }

    private static Task<(long Crossings, Exception? Fault)> CrossingsOf(Action call) => CrossingsOf(() =>
    {
        call();
        return Task.CompletedTask;
    });

    /// <summary>A session in a transaction that is never committed.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private DbConnection _connection = null!;
        private DbTransaction _transaction = null!;

        public DataSession S { get; private set; } = null!;

        public static async Task<Session> OpenAsync(string connectionString)
        {
            var session = new Session { _connection = await TestProvider.OpenAsync(connectionString) };
            session._transaction = await session._connection.BeginTransactionAsync();
            session.S = new DataSession(session._connection, session._transaction);
            return session;
        }

        public async ValueTask DisposeAsync()
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private static void ShouldBeRefused((long Crossings, Exception? Fault) result, int status = 400)
    {
        result.Crossings.Should().Be(1);
        result.Fault.Should().BeOfType<XrmdFaultException>().Which.HttpStatus.Should().Be(status);
    }

    private static void ShouldBeSkipped((long Crossings, Exception? Fault) result)
    {
        result.Fault.Should().BeNull();
        result.Crossings.Should().Be(0);
    }

    private static void ShouldHaveRun((long Crossings, Exception? Fault) result)
    {
        result.Fault.Should().BeNull();
        result.Crossings.Should().Be(1);
    }

    // ---- validation ----

    [Fact]
    public async Task Bridge_skips_NormalizeElasticKeys_when_standard_table_without_keys()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var record = new Record("contact") { ["lastname"] = "crb" };
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.NormalizeElasticKeys(kit.Store.GetTable("contact")!, record, true, kit.Store)));
        record.Id.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task Bridge_runs_NormalizeElasticKeys_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await kit.EnsureElasticTableAsync();
        var table = kit.Store.GetTable("new_crgelastic")!;
        var id = Guid.NewGuid();
        var record = new Record("new_crgelastic") { ["new_name"] = "crb" };
        record.KeyAttributes[SystemAttributes.PrimaryIdName(table)] = id;
        record.KeyAttributes[ElasticTables.PartitionId] = "crb-partition";
        ShouldHaveRun(await CrossingsOf(() => CoreSdkBridges.NormalizeElasticKeys(table, record, true, kit.Store)));
        record.Id.Should().Be(id);
        record[ElasticTables.PartitionId].Should().Be("crb-partition");
    }

    [Fact]
    public async Task Bridge_skips_DemandRateInRange_when_not_a_currency_rate()
    {
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.DemandRateInRange(new Record("contact") { [CurrencySchema.ExchangeRate] = 0m })));
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.DemandRateInRange(new Record(CurrencySchema.Table) { ["currencyname"] = "x" })));
    }

    [Fact]
    public async Task Bridge_runs_DemandRateInRange_when_it_applies()
    {
        var result = await CrossingsOf(() => CoreSdkBridges.DemandRateInRange(new Record(CurrencySchema.Table) { [CurrencySchema.ExchangeRate] = 0m }));
        ShouldBeRefused(result);
        ((XrmdFaultException)result.Fault!).ErrorCode.Should().Be(ErrorCodes.InvalidExchangeRate);
    }

    [Fact]
    public async Task Bridge_skips_DemandElasticWrite_when_standard_table()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        ShouldBeSkipped(await CrossingsOf(() =>
            CoreSdkBridges.DemandElasticWrite(kit.Store.GetTable("contact")!, new Record("contact") { [ElasticTables.TtlInSeconds] = 0 }, true)));
    }

    [Fact]
    public async Task Bridge_runs_DemandElasticWrite_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await kit.EnsureElasticTableAsync();
        ShouldBeRefused(await CrossingsOf(() => CoreSdkBridges.DemandElasticWrite(kit.Store.GetTable("new_crgelastic")!,
            new Record("new_crgelastic") { [ElasticTables.TtlInSeconds] = 0 }, true)));
    }

    // ---- Security row rules ----

    [Fact]
    public async Task Bridge_skips_EnforceCurrencyRules_when_other_table()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        ShouldBeSkipped(await CrossingsOf(() =>
            CoreSdkBridges.EnforceCurrencyRulesAsync(new Record("contact") { ["lastname"] = "crb" }, Unknown, true, kit.Store, null!, default)));
    }

    [Fact]
    public async Task Bridge_runs_EnforceCurrencyRules_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var row = new Record("organization") { ["name"] = "crb" };
        ShouldHaveRun(await CrossingsOf(() => CoreSdkBridges.EnforceCurrencyRulesAsync(row, Unknown, true, kit.Store, session.S, default)));
        row["name"].Should().Be("crb");
    }

    [Fact]
    public async Task Bridge_skips_DemandAccessTeamTemplate_when_other_table()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        ShouldBeSkipped(await CrossingsOf(() =>
            CoreSdkBridges.DemandAccessTeamTemplateAsync(null!, kit.Store, new Record("team") { ["name"] = "crb" }, true, Unknown, default)));
    }

    [Fact]
    public async Task Bridge_runs_DemandAccessTeamTemplate_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        ShouldBeRefused(await CrossingsOf(() => CoreSdkBridges.DemandAccessTeamTemplateAsync(session.S, kit.Store,
            new Record(AccessTeamSchema.TeamTemplate) { [AccessTeamSchema.DefaultAccessRightsMask] = 8 }, true, Unknown, default)));
    }

    [Fact]
    public async Task Bridge_skips_ApplyDuplicateRuleColumns_when_other_table()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var row = new Record("contact");
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.ApplyDuplicateRuleColumnsAsync(null!, kit.Store, "contact", row, true, Unknown, default)));
        row.Attributes.Should().BeEmpty();
    }

    [Fact]
    public async Task Bridge_runs_ApplyDuplicateRuleColumns_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var row = new Record(DuplicateDetectionSchema.Condition);
        ShouldHaveRun(await CrossingsOf(() =>
            CoreSdkBridges.ApplyDuplicateRuleColumnsAsync(session.S, kit.Store, DuplicateDetectionSchema.Condition, row, true, Unknown, default)));
        row["ignoreblankvalues"].Should().Be(false);
    }

    [Fact]
    public async Task Bridge_skips_ApplyEnvironmentVariableRow_when_other_table()
    {
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.ApplyEnvironmentVariableRowAsync(null!, "contact", new Record("contact"), true, Unknown, default)));
    }

    [Fact]
    public async Task Bridge_runs_ApplyEnvironmentVariableRow_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        ShouldBeRefused(await CrossingsOf(() => CoreSdkBridges.ApplyEnvironmentVariableRowAsync(session.S, EnvironmentVariableSchema.Definition,
            new Record(EnvironmentVariableSchema.Definition), true, Unknown, default)));
    }

    [Fact]
    public async Task Bridge_skips_ApplyOrganizationSettingRow_when_update_without_definition()
    {
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.ApplyOrganizationSettingRowAsync(null!, AppModuleSchema.OrganizationSetting,
            new Record(AppModuleSchema.OrganizationSetting) { ["value"] = "x" }, false, Unknown, default)));
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.ApplyOrganizationSettingRowAsync(null!, "contact", new Record("contact"), true, Unknown, default)));
    }

    [Fact]
    public async Task Bridge_runs_ApplyOrganizationSettingRow_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var row = new Record(AppModuleSchema.OrganizationSetting)
        {
            ["settingdefinitionid"] = new RecordReference { LogicalName = AppModuleSchema.SettingDefinition, Id = Guid.Empty },
        };
        ShouldHaveRun(await CrossingsOf(() =>
            CoreSdkBridges.ApplyOrganizationSettingRowAsync(session.S, AppModuleSchema.OrganizationSetting, row, true, Unknown, default)));
        row.GetValue<RecordReference>("settingdefinitionid")!.Id.Should().Be(Guid.Empty);
    }

    // ---- Mail ----

    [Fact]
    public async Task Bridge_skips_TakeMailSecrets_when_no_secret_column()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var record = new Record(MailSchema.Mailbox) { ["name"] = "crb" };
        var original = record;
        IReadOnlyDictionary<string, string?> taken = null!;
        ShouldBeSkipped(await CrossingsOf(() =>
        {
            taken = CoreSdkBridges.TakeMailSecrets(kit.Store.GetTable(MailSchema.Mailbox)!, ref record, new Dictionary<string, string?>());
        }));
        taken.Should().BeEmpty();
        record.Should().BeSameAs(original);
    }

    [Fact]
    public async Task Bridge_runs_TakeMailSecrets_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var record = new Record(MailSchema.Mailbox) { ["name"] = "crb", ["password"] = "crb-secret" };
        var original = record;
        IReadOnlyDictionary<string, string?> taken = null!;
        ShouldHaveRun(await CrossingsOf(() =>
        {
            taken = CoreSdkBridges.TakeMailSecrets(kit.Store.GetTable(MailSchema.Mailbox)!, ref record, new Dictionary<string, string?>());
        }));
        taken.Should().Equal(new Dictionary<string, string?> { ["password"] = "crb-secret" });
        record.Should().NotBeSameAs(original);
        record.Attributes.Keys.Should().Equal("name");
        original.Contains("password").Should().BeTrue();
    }

    /// <summary>
    /// S.2e3b Task 5: the claim of the secrets the pipeline stripped from a Target, by the SDK instance (the SDK twins) or by the model
    /// record a mirror paired with it (the seam for the model dispatch), once; nothing is converted.
    /// </summary>
    [Fact]
    public async Task Bridge_claims_mail_secrets_by_the_sdk_instance()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var view = kit.Store.GetTable(MailSchema.Mailbox)!.SdkView();
        var stripped = new Entity(MailSchema.Mailbox) { ["name"] = "crb", ["password"] = "crb-secret" };
        PendingMailSecrets.Strip(stripped, view);
        var mirror = new SdkMirror();
        var record = (Record)mirror.In(stripped)!;
        IReadOnlyDictionary<string, string?> none = null!, claimed = null!, again = null!;
        ShouldBeSkipped(await CrossingsOf(() =>
        {
            none = CoreSdkBridges.ClaimMailSecrets(record, null);
            claimed = CoreSdkBridges.ClaimMailSecrets(record, mirror);
            again = CoreSdkBridges.ClaimMailSecrets(stripped);
        }));
        none.Should().BeEmpty("without the mirror the SDK instance is unknown");
        claimed.Should().Equal(new Dictionary<string, string?> { ["password"] = "crb-secret" });
        again.Should().BeEmpty("the values are claimed once");

        var direct = new Entity(MailSchema.Mailbox) { ["password"] = "crb-direct" };
        PendingMailSecrets.Strip(direct, view);
        CoreSdkBridges.ClaimMailSecrets(direct).Should().Equal(new Dictionary<string, string?> { ["password"] = "crb-direct" });
        CoreSdkBridges.ClaimMailSecrets(new Entity("contact")).Should().BeEmpty();
    }

    [Fact]
    public async Task Bridge_skips_EnqueueMailboxTest_when_not_scheduled()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var row = new Record(MailSchema.Mailbox) { [MailSchema.TestScheduled] = false };
        ShouldBeSkipped(await CrossingsOf(() =>
            CoreSdkBridges.EnqueueMailboxTestAsync(session.S, kit.Caller, core.JobQueue, MailSchema.Mailbox, Unknown, row, default)));
        ShouldBeSkipped(await CrossingsOf(() =>
            CoreSdkBridges.EnqueueMailboxTestAsync(session.S, Caller.Internal, core.JobQueue, MailSchema.Mailbox, Unknown,
                new Record(MailSchema.Mailbox) { [MailSchema.TestScheduled] = true }, default)));
    }

    [Fact]
    public async Task Bridge_runs_EnqueueMailboxTest_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var mailbox = Guid.NewGuid();
        var row = new Record(MailSchema.Mailbox) { [MailSchema.TestScheduled] = true };
        ShouldHaveRun(await CrossingsOf(() =>
            CoreSdkBridges.EnqueueMailboxTestAsync(session.S, kit.Caller, core.JobQueue, MailSchema.Mailbox, mailbox, row, default)));
        await using var cmd = session.S.Command("SELECT COUNT(*) FROM data.asyncoperation WHERE regardingobjectid = @id");
        cmd.AddParameter("@id", System.Data.DbType.Guid, mailbox);
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).Should().Be(1, "the test job is queued");
        row[MailSchema.TestScheduled].Should().Be(true);
    }

    [Fact]
    public async Task Bridge_skips_SyncMailbox_when_nothing_to_set()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var row = new Record(QueueSchema.Queue) { ["description"] = "crb", ["name"] = "" };
        ShouldBeSkipped(await CrossingsOf(() =>
            CoreSdkBridges.SyncMailboxAsync(session.S, QueueSchema.Queue, Unknown, row, false, kit.Store, new HashSet<string>(), default)));
    }

    [Fact]
    public async Task Bridge_runs_SyncMailbox_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        ShouldHaveRun(await CrossingsOf(() =>
            CoreSdkBridges.SyncMailboxAsync(session.S, QueueSchema.Queue, Unknown, new Record(QueueSchema.Queue) { ["name"] = "crb" }, false,
                kit.Store, null, default)));
        ShouldHaveRun(await CrossingsOf(() =>
            CoreSdkBridges.SyncMailboxAsync(session.S, QueueSchema.Queue, Unknown, new Record(QueueSchema.Queue), true, kit.Store, null, default)));
    }

    [Fact]
    public async Task Bridge_skips_WriteAudit_when_not_audited()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var scope = kit.Services.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<Xrmd.Audit.AuditWriter>();
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        await TestEntities.EnsurePipeAsync(kit.Store);
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.WriteAuditAsync(audit, session.S, kit.Caller, 1, 1,
            kit.Store.GetTable(TestEntities.Pipe)!, Unknown, null, new Record(TestEntities.Pipe) { ["pb_name"] = "crb" }, default)));
        var audited = (await kit.AuditedRowAsync()).Row;
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.WriteAuditAsync(audit, session.S, kit.Caller, 1, 1,
            kit.Store.GetTable(audited.LogicalName)!, audited.Id, null, null, default)));
    }

    [Fact]
    public async Task Bridge_runs_WriteAudit_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var audited = (await kit.AuditedRowAsync()).Row;
        await using var scope = kit.Services.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<Xrmd.Audit.AuditWriter>();
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        ShouldHaveRun(await CrossingsOf(() => CoreSdkBridges.WriteAuditAsync(audit, session.S, kit.Caller, 2, 2,
            kit.Store.GetTable(audited.LogicalName)!, audited.Id, new Record(audited.LogicalName) { ["new_name"] = "crb before" },
            new Record(audited.LogicalName) { ["new_name"] = "crb after" }, default)));
        await using var cmd = session.S.Command("SELECT COUNT(*) FROM data.audit WHERE objectid = @id AND changedata LIKE '%crb after%'");
        cmd.AddParameter("@id", System.Data.DbType.Guid, audited.Id);
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).Should().Be(1, "the audit row of the change is written");
    }

    [Fact]
    public async Task Bridge_skips_ApplyProfileRules_when_other_table()
    {
        var row = new Record("contact") { ["servertype"] = new ChoiceValue(4) };
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.ApplyProfileRules(row, null)));
        row.Attributes.Keys.Should().Equal("servertype");
    }

    [Fact]
    public async Task Bridge_runs_ApplyProfileRules_when_it_applies()
    {
        var row = new Record(MailSchema.EmailServerProfile) { ["name"] = "crb", ["servertype"] = new ChoiceValue(MailSchema.ServerTypeImapSmtp) };
        ShouldHaveRun(await CrossingsOf(() => CoreSdkBridges.ApplyProfileRules(row, null)));
        row["outgoingauthenticationprotocol"].Should().Be(new ChoiceValue(MailSchema.ProtocolBasic));
    }

    [Fact]
    public async Task Bridge_skips_DemandSupportedDeliveryMethods_when_no_delivery_column()
    {
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.DemandSupportedDeliveryMethods(new Record(MailSchema.Mailbox) { ["name"] = "crb" }, MailSchema.Mailbox)));
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.DemandSupportedDeliveryMethods(
            new Record("contact") { ["incomingemaildeliverymethod"] = new ChoiceValue(1) }, "contact")));
    }

    [Fact]
    public async Task Bridge_runs_DemandSupportedDeliveryMethods_when_it_applies()
    {
        ShouldBeRefused(await CrossingsOf(() => CoreSdkBridges.DemandSupportedDeliveryMethods(
            new Record(MailSchema.Mailbox) { ["incomingemaildeliverymethod"] = new ChoiceValue(MailSchema.DeliveryOutlook) }, MailSchema.Mailbox)));
    }

    [Fact]
    public async Task Bridge_skips_DemandSupportedOrganizationColumns_when_no_id_base()
    {
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.DemandSupportedOrganizationColumns(new Record("organization") { ["trackingtokenidbase"] = 0 }, "organization")));
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.DemandSupportedOrganizationColumns(new Record("organization") { ["name"] = "crb" }, "organization")));
    }

    [Fact]
    public async Task Bridge_runs_DemandSupportedOrganizationColumns_when_it_applies()
    {
        ShouldBeRefused(await CrossingsOf(() => CoreSdkBridges.DemandSupportedOrganizationColumns(new Record("organization") { ["trackingtokenidbase"] = 5 }, "organization")));
    }

    [Fact]
    public async Task Bridge_skips_DemandMailboxWritable_when_the_server_writes()
    {
        var row = new Record(MailSchema.Mailbox) { ["emailaddress"] = "crb-a@example.test" };
        ShouldBeSkipped(await CrossingsOf(() =>
            CoreSdkBridges.DemandMailboxWritable(row, new Record(MailSchema.Mailbox) { ["emailaddress"] = "crb-b@example.test" }, Caller.Internal)));
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.DemandMailboxWritable(new Record(MailSchema.Mailbox) { ["name"] = "crb" },
            new Record(MailSchema.Mailbox), new Caller(Unknown, Unknown))));
    }

    [Fact]
    public async Task Bridge_runs_DemandMailboxWritable_when_it_applies()
    {
        ShouldBeRefused(await CrossingsOf(() => CoreSdkBridges.DemandMailboxWritable(new Record(MailSchema.Mailbox) { ["emailaddress"] = "crb-a@example.test" },
            new Record(MailSchema.Mailbox) { ["emailaddress"] = "crb-b@example.test" }, new Caller(Unknown, Unknown))));
    }

    [Fact]
    public async Task Bridge_skips_ApplyIncomingChange_when_status_sent_or_nothing_incoming()
    {
        var secrets = new Dictionary<string, string?> { ["password"] = "x" };
        var row = new Record(MailSchema.Mailbox) { ["incomingemailstatus"] = new ChoiceValue(1) };
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.ApplyIncomingChangeAsync(null!, row, Unknown, secrets, default)));
        row["incomingemailstatus"].Should().Be(new ChoiceValue(1));
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.ApplyIncomingChangeAsync(null!, new Record(MailSchema.Mailbox) { ["name"] = "crb" },
            Unknown, new Dictionary<string, string?>(), default)));
    }

    [Fact]
    public async Task Bridge_runs_ApplyIncomingChange_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var row = new Record(MailSchema.Mailbox) { ["name"] = "crb" };
        ShouldHaveRun(await CrossingsOf(() =>
            CoreSdkBridges.ApplyIncomingChangeAsync(session.S, row, Unknown, new Dictionary<string, string?> { ["password"] = "x" }, default)));
        row["incomingemailstatus"].Should().Be(new ChoiceValue(MailSchema.StatusNotRun));
    }

    [Fact]
    public async Task Bridge_skips_ApplyAddressChange_when_no_address()
    {
        bool changed = true;
        ShouldBeSkipped(await CrossingsOf(async () =>
            changed = await CoreSdkBridges.ApplyAddressChangeAsync(null!, "systemuser", Unknown, new Record("systemuser") { ["firstname"] = "crb" }, default)));
        changed.Should().BeFalse();
    }

    [Fact]
    public async Task Bridge_runs_ApplyAddressChange_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var row = new Record("systemuser") { ["internalemailaddress"] = "crb-" + Guid.NewGuid().ToString("N") + "@example.test" };
        var changed = false;
        ShouldHaveRun(await CrossingsOf(async () =>
            changed = await CoreSdkBridges.ApplyAddressChangeAsync(session.S, "systemuser", kit.Caller.UserId, row, default)));
        changed.Should().BeTrue();
        row[MailSchema.ApprovalColumn].Should().Be(new ChoiceValue(MailSchema.ApprovalPending));
    }

    [Fact]
    public async Task Bridge_skips_ChangedDeliveryColumns_when_no_delivery_column()
    {
        IReadOnlySet<string> changed = null!;
        ShouldBeSkipped(await CrossingsOf(async () =>
            changed = await CoreSdkBridges.ChangedDeliveryColumnsAsync(null!, "systemuser", Unknown, new Record("systemuser") { ["firstname"] = "crb" }, default)));
        changed.Should().BeEmpty();
    }

    [Fact]
    public async Task Bridge_runs_ChangedDeliveryColumns_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var row = new Record("systemuser") { ["incomingemaildeliverymethod"] = new ChoiceValue(MailSchema.DeliveryNone) };
        IReadOnlySet<string> changed = null!;
        ShouldHaveRun(await CrossingsOf(async () =>
            changed = await CoreSdkBridges.ChangedDeliveryColumnsAsync(session.S, "systemuser", Unknown, row, default)));
        changed.Should().BeEmpty(); // no stored row: None, as sent
        row["incomingemaildeliverymethod"].Should().Be(new ChoiceValue(MailSchema.DeliveryNone));
    }

    // ---- Elastic, queues ----

    /// <summary>A table (never stored) with a lookup that has a partition column, and one without.</summary>
    private static TableDefinition LookupTable() => new()
    {
        LogicalName = "new_crblookup",
        Attributes =
        [
            new UniqueIdentifierColumn { LogicalName = "new_crblookupid" },
            new LookupColumn { LogicalName = "new_parentid", Targets = ["new_crgelastic"] },
            new TextColumn { LogicalName = "new_parentidpid", MaxLength = 100 },
            new LookupColumn { LogicalName = "new_accountid", Targets = ["account"] },
        ],
    };

    [Fact]
    public async Task Bridge_skips_ApplyLookupPartitions_when_no_partitioned_lookup()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var table = LookupTable();
        var columns = table.Attributes!.ToDictionary(a => a.LogicalName!, StringComparer.Ordinal);
        var row = new Record(table.LogicalName!) { ["new_accountid"] = null };
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.ApplyLookupPartitionsAsync(null!, kit.Store, columns, table, row, default)));
        row.Attributes.Keys.Should().Equal("new_accountid");
    }

    [Fact]
    public async Task Bridge_runs_ApplyLookupPartitions_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var table = LookupTable();
        var columns = table.Attributes!.ToDictionary(a => a.LogicalName!, StringComparer.Ordinal);
        var row = new Record(table.LogicalName!) { ["new_parentid"] = null };
        ShouldHaveRun(await CrossingsOf(() => CoreSdkBridges.ApplyLookupPartitionsAsync(null!, kit.Store, columns, table, row, default)));
        row.Attributes.Keys.Should().Equal("new_parentid", "new_parentidpid"); // a cleared lookup clears its partition
        row["new_parentidpid"].Should().BeNull();
    }

    // ---- Files, duplicates, rollups ----

    [Fact]
    public async Task Bridge_skips_ApplyImages_when_no_image_column()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await new RowKit(kit).EnsureTablesAsync();
        var table = kit.Store.GetTable(RowKit.ImageTable)!;
        var columns = table.Attributes!.ToDictionary(a => a.LogicalName!, StringComparer.Ordinal);
        var row = new Record(RowKit.ImageTable) { ["new_name"] = "crb" };
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.ApplyImagesAsync(kit.Services.GetRequiredService<FileColumnService>(), null!, table,
            columns, Unknown, row, true, default)));
        row.Attributes.Keys.Should().Equal("new_name");
    }

    [Fact]
    public async Task Bridge_runs_ApplyImages_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await new RowKit(kit).EnsureTablesAsync();
        var table = kit.Store.GetTable(RowKit.ImageTable)!;
        var columns = table.Attributes!.ToDictionary(a => a.LogicalName!, StringComparer.Ordinal);
        var row = new Record(RowKit.ImageTable) { ["new_pic"] = null };
        ShouldHaveRun(await CrossingsOf(() => CoreSdkBridges.ApplyImagesAsync(kit.Services.GetRequiredService<FileColumnService>(), null!, table,
            columns, Unknown, row, true, default)));
        row.Contains("new_pic" + FileColumns.ImageIdSuffix).Should().BeTrue(); // a cleared image clears its id
        row["new_pic" + FileColumns.ImageIdSuffix].Should().BeNull();
    }

    private async Task<TableDefinition> FileTableAsync(CoreScenarioKit kit)
    {
        if (kit.Store.GetTable(FileTable) is null)
        {
            await kit.Admin.ExecuteAsync(new CreateEntityRequest
            {
                Entity = TestHelpers.Entity(FileTable, FileTable, "Bridge files"),
                PrimaryAttribute = new StringAttributeMetadata
                {
                    LogicalName = "new_name", SchemaName = "new_name", MaxLength = 100, DisplayName = TestHelpers.Label("Name"),
                    RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
                },
            });
            await kit.Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = FileTable, Attribute = new FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_doc", DisplayName = TestHelpers.Label("Doc") },
            });
        }
        return kit.Store.GetTable(FileTable)!;
    }

    [Fact]
    public async Task Bridge_skips_ApplyFilesOnUpdate_when_no_commit_and_no_cleared_file()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var table = await FileTableAsync(kit);
        var columns = table.Attributes!.ToDictionary(a => a.LogicalName!, StringComparer.Ordinal);
        var row = new Record(FileTable) { ["new_name"] = "crb" };
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.ApplyFilesOnUpdateAsync(kit.Services.GetRequiredService<FileColumnService>(), null!,
            table, columns, Unknown, row, null, default)));
        row.Attributes.Keys.Should().Equal("new_name");
    }

    [Fact]
    public async Task Bridge_runs_ApplyFilesOnUpdate_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var table = await FileTableAsync(kit);
        var columns = table.Attributes!.ToDictionary(a => a.LogicalName!, StringComparer.Ordinal);
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var fileId = Guid.NewGuid();
        var row = new Record(FileTable) { ["new_name"] = "crb" };
        ShouldHaveRun(await CrossingsOf(() => CoreSdkBridges.ApplyFilesOnUpdateAsync(kit.Services.GetRequiredService<FileColumnService>(), session.S,
            table, columns, Unknown, row, new FileCommitGrant(FileTable, Unknown, "new_doc", fileId, "crb.txt"), default)));
        row["new_doc"].Should().Be(fileId); // the commit's column gets its file id
    }

    [Fact]
    public async Task Bridge_skips_DemandNoDuplicate_when_detection_disabled()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var table = new TableDefinition { LogicalName = "new_crbnone", IsDuplicateDetectionEnabled = new ManagedFlag { Value = false } };
        ShouldBeSkipped(await CrossingsOf(() =>
            CoreSdkBridges.DemandNoDuplicateAsync(core.DuplicateFinder, null!, table, new Record("new_crbnone"), Unknown, null, default)));
    }

    [Fact]
    public async Task Bridge_runs_DemandNoDuplicate_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var session = await Session.OpenAsync(kit.ConnectionString);
        var table = new TableDefinition { LogicalName = "new_crbnone", IsDuplicateDetectionEnabled = new ManagedFlag { Value = true } };
        var row = new Record("new_crbnone") { ["new_name"] = "crb" };
        ShouldHaveRun(await CrossingsOf(() => CoreSdkBridges.DemandNoDuplicateAsync(core.DuplicateFinder, session.S, table, row, Unknown, null, default)));
        row["new_name"].Should().Be("crb"); // no published rule on the table: nothing found
    }

    /// <summary>A table (never stored) with a rollup column whose definition is missing, so that it is not calculated.</summary>
    private static TableDefinition RollupTable(bool withRollup) => new()
    {
        LogicalName = "new_crbrollup",
        Attributes =
        [
            new UniqueIdentifierColumn { LogicalName = "new_crbrollupid" },
            new IntegerColumn { LogicalName = "new_total", SourceType = withRollup ? RollupColumns.Rollup : 0 },
        ],
    };

    [Fact]
    public async Task Bridge_skips_ApplyRollupInitialValues_when_no_rollup_column()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var row = new Record("new_crbrollup") { ["new_total"] = 1 };
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.ApplyRollupInitialValues(
            kit.Services.GetRequiredService<RollupCalculator>(), RollupTable(withRollup: false), row, DateTime.UtcNow)));
        ShouldBeSkipped(await CrossingsOf(() => CoreSdkBridges.ApplyRollupInitialValues(null, RollupTable(withRollup: true), row, DateTime.UtcNow)));
        row.Attributes.Keys.Should().Equal("new_total");
    }

    [Fact]
    public async Task Bridge_runs_ApplyRollupInitialValues_when_it_applies()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var now = new DateTime(2015, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var row = new Record("new_crbrollup") { ["new_total"] = 1 };
        ShouldHaveRun(await CrossingsOf(() => CoreSdkBridges.ApplyRollupInitialValues(
            kit.Services.GetRequiredService<RollupCalculator>(), RollupTable(withRollup: true), row, now)));
        row["new_total" + RollupColumns.StateSuffix].Should().Be(0); // not calculated
        row["new_total" + RollupColumns.DateSuffix].Should().Be(now);
    }
}
