using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Record = Xrmd.Model.Records.Record;
using Xrmd.Pipeline;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Pipeline.Mail;
using Xrmd.Pipeline.Merge;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Webhooks;
using Xrmd.Pipeline.Workflows;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.Workflows;
using S = Xrmd.Metadata.WorkflowSchema;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3c2b: probes of stage 20 on the model request. Task 2: the messages moved onto the model in S.2e3c1 hold no window at the core
/// and are left as sent; the inner-execution and create-only marks are keyed on the model request. Task 3: the custom API authoring
/// and the registration validation run on model records, and the solution import keeps its validation through the SDK twin. Task 4: the
/// service endpoint rules run on model records, and the mail secrets are stripped from the model Target and keyed on it.
/// </summary>
[Collection("xrmd")]
public sealed class Stage20ModelTests(XrmdFixture fx)
{
    private const string DumpLog = TestEntities.PipeLog2;
    private const string Plugins = "Xrmd.TestPlugins.";

    // ---- Task 2: the S.2e3c1 messages without a window (Decision 8, Review Focus 5) ----------------------------------

    /// <summary>
    /// For every message of <c>mutation.txt</c> that runs through the pipeline and is not bridged (<see cref="CoreSdkMessages.Bridged"/>),
    /// a PreOperation and a PostOperation step (<c>ContextDumpPlugin</c>) dump their context: the <c>InputParameters</c> of the two dumps
    /// are equal, identity tokens included (the core leaves its model request as sent, and the view of stage 40 holds the instances the
    /// view of stage 20 held). The role privilege messages have no <c>sdkmessage</c> row (<see cref="Xrmd.Metadata.SdkMessageCatalog"/>),
    /// so no step can be registered on them; for every message, the caller's request is also unchanged after the call (the probe of
    /// <see cref="CoreRequestMutationProbeTests"/>, through the pipeline). ExecuteWorkflow keeps its window (pre-flight ruling P3) and its
    /// scenario fails at stage 30 (no stage 40).
    /// </summary>
    [Fact]
    public async Task Moved_messages_leave_their_model_request_as_sent()
    {
        var scenarios = CoreScenarios.All
            .Where(s => !s.NonPipeline && !CoreSdkMessages.Bridged.Contains(s.Message) && s.Message != PipelineExecutor.ExecuteWorkflowMessage)
            .ToList();
        scenarios.Select(s => s.Message).Should().Contain(
            ["GrantAccess", "AddUserToRecordTeam", "AddPrivilegesRole", "RetrieveExchangeRate", "AddToQueue", "RetrieveEntityChanges", "Create"]);

        foreach (var scenario in scenarios)
        {
            await using var kit = await CoreScenarioKit.StartAsync(fx, scenario.OwnDatabase);
            await using var clean = new DumpCleanup(kit);
            var dump = await DumpTypeAsync(kit);
            var request = await scenario.Arrange(kit);
            var stepped = Xrmd.Metadata.SdkMessageCatalog.Messages.Contains(scenario.Message);
            if (stepped)
            {
                // A Create step without a filter would also dump the dump's own Create.
                var filter = scenario.Message == "Create" ? ((Entity)request["Target"]).LogicalName : null;
                await PluginTestHelper.RegisterStepAsync(kit.Admin, dump, scenario.Message, filter, 20, config: DumpLog);
                await PluginTestHelper.RegisterStepAsync(kit.Admin, dump, scenario.Message, filter, 40, config: DumpLog);
            }
            await kit.ExecAsync($"DELETE FROM data.{DumpLog}");
            var tokens = new CoreDump.Tokens();
            var before = CoreDump.Request(request, tokens);

            var (_, fault) = await CoreScenarios.ViaPipelineAsync(kit, request);

            fault.Should().BeNull(scenario.Message);
            CoreRequestMutationProbeTests.Changes(before, CoreDump.Request(request, tokens)).Should().Be("unchanged", scenario.Message);
            if (!stepped) continue;
            var pre = InputParameters(await DumpAsync(kit, $"{scenario.Message}|20|0|1"), indent: 1);
            var post = InputParameters(await DumpAsync(kit, $"{scenario.Message}|40|0|1"), indent: 1);
            post.Should().Be(pre, $"{scenario.Message} leaves its request as sent");
        }
    }

    /// <summary>
    /// A step on the <c>queueitem</c> Create of AddToQueue (and on the Update that moves an item out of its <c>SourceQueueId</c>) reads
    /// its <c>ParentContext.InputParameters</c>: they are the AddToQueue's own, the same instances its PreOperation step saw (identity
    /// tokens included), with the <c>Target</c>, <c>DestinationQueueId</c> and <c>SourceQueueId</c> the caller sent.
    /// </summary>
    [Fact]
    public async Task Nested_queue_write_sees_the_parent_as_today()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await using var clean = new DumpCleanup(kit);
        var dump = await DumpTypeAsync(kit);
        var email = await kit.EmailAsync();
        var first = await kit.QueueAsync();
        var second = await kit.QueueAsync();
        await PluginTestHelper.RegisterStepAsync(kit.Admin, dump, "AddToQueue", null, 20, config: DumpLog);
        await PluginTestHelper.RegisterStepAsync(kit.Admin, dump, "Create", "queueitem", 20, config: DumpLog);
        await PluginTestHelper.RegisterStepAsync(kit.Admin, dump, "Update", "queueitem", 20, config: DumpLog);

        foreach (var (request, nested) in new[]
                 {
                     (new AddToQueueRequest { Target = new EntityReference("email", email), DestinationQueueId = first }, "Create|20|0|2"),
                     (new AddToQueueRequest { Target = new EntityReference("email", email), SourceQueueId = first, DestinationQueueId = second },
                         "Update|20|0|2"),
                 })
        {
            await kit.ExecAsync($"DELETE FROM data.{DumpLog}");

            var (_, fault) = await CoreScenarios.ViaPipelineAsync(kit, request);

            fault.Should().BeNull();
            var own = InputParameters(await DumpAsync(kit, "AddToQueue|20|0|1"), indent: 1);
            var parent = InputParameters(await DumpAsync(kit, nested), indent: 2);
            parent.Should().Be(own, "the parent level holds the AddToQueue's own parameters");
            parent.Should().Contain($"Id: Guid:{email:D}").And.Contain($"DestinationQueueId: Guid:{request.DestinationQueueId:D}");
            if (request.SourceQueueId != Guid.Empty) parent.Should().Contain($"SourceQueueId: Guid:{first:D}");
        }
    }

    /// <summary>
    /// Decision 8: GrantAccess, AddUserToRecordTeam, AddPrivilegesRole, RetrieveExchangeRate and AddToQueue run on the model at the core
    /// (<see cref="IPipelineCore.AnswersOnModel"/>), so the pipeline holds no window for them (<see cref="CoreHandOff.Held"/> never fires);
    /// SendEmail is bridged and keeps its window.
    /// </summary>
    [Fact]
    public async Task Moved_messages_hold_no_window()
    {
        string[] moved = ["GrantAccess", "AddUserToRecordTeam", "AddPrivilegesRole", "RetrieveExchangeRate", "AddToQueue"];
        var held = new List<string?>();
        foreach (var message in moved.Append("SendEmail"))
        {
            var scenario = CoreScenarios.All.Single(s => s.Message == message);
            await using var kit = await CoreScenarioKit.StartAsync(fx, scenario.OwnDatabase);
            var request = await scenario.Arrange(kit);
            held.Clear();
            CoreHandOff.Held.Value = r => held.Add(r.Name);
            try
            {
                var (_, fault) = await CoreScenarios.ViaPipelineAsync(kit, request);
                fault.Should().BeNull(message);
            }
            finally
            {
                CoreHandOff.Held.Value = null;
            }

            if (message == "SendEmail") held.Should().Contain("SendEmail", "a bridged message keeps its window");
            else held.Should().BeEmpty($"{message} runs on the model request");
        }
    }

    /// <summary>
    /// Decision 7: the marks of an inner execution and of a create-only Upsert are keyed on the model request. The model request the
    /// core gets (<see cref="CoreOperations.CrudRequest"/>) of an UpdateMultiple's inner Update is <see cref="InnerKind.Bulk"/>, of an
    /// Upsert's inner Create <see cref="InnerKind.Upsert"/>, of a plain Create none. A create-only Upsert through the organization
    /// service of an existing row is the 412, and of a caller who may not create the row the 403 (the guard of the Create first).
    /// </summary>
    [Fact]
    public async Task Inner_and_create_only_marks_are_on_the_model_request()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await kit.EnsurePipeAsync();
        const string pipe = TestEntities.Pipe;
        await kit.ExecAsync($"DELETE FROM data.{pipe} WHERE pb_name LIKE 's20m-%'");
        var first = await kit.Admin.CreateAsync(new Entity(pipe) { ["pb_name"] = "s20m-a" });
        var second = await kit.Admin.CreateAsync(new Entity(pipe) { ["pb_name"] = "s20m-b" });
        var seen = new List<(string? Name, InnerKind? Kind)>();
        CoreOperations.CrudRequest.Value = r =>
        {
            if (r.Name is "Create" or "Update") seen.Add((r.Name, InnerExecution.KindOf(r)));
        };
        try
        {
            (await CoreScenarios.ViaPipelineAsync(kit, new UpdateMultipleRequest
            {
                Targets = new EntityCollection([
                    new Entity(pipe, first) { ["pb_count"] = 1 }, new Entity(pipe, second) { ["pb_count"] = 2 },
                ]) { EntityName = pipe },
            })).Fault.Should().BeNull();
            seen.Should().Equal(("Update", InnerKind.Bulk), ("Update", InnerKind.Bulk));

            seen.Clear();
            (await CoreScenarios.ViaPipelineAsync(kit, new UpsertRequest { Target = new Entity(pipe, Guid.NewGuid()) { ["pb_name"] = "s20m-c" } }))
                .Fault.Should().BeNull();
            seen.Should().Equal(("Create", InnerKind.Upsert));

            seen.Clear();
            (await CoreScenarios.ViaPipelineAsync(kit, new CreateRequest { Target = new Entity(pipe) { ["pb_name"] = "s20m-d" } })).Fault.Should().BeNull();
            seen.Should().Equal(("Create", (InnerKind?)null));
        }
        finally
        {
            CoreOperations.CrudRequest.Value = null;
        }

        var duplicate = await FluentActions.Awaiting(() => kit.Admin.UpsertAsync(new Entity(pipe, first) { ["pb_name"] = "s20m-again" }, createOnly: true))
            .Should().ThrowAsync<XrmdFaultException>();
        duplicate.Which.HttpStatus.Should().Be(412);
        duplicate.Which.ErrorCode.Should().Be(ErrorCodes.DuplicateRecord);

        var denied = Guid.NewGuid();
        await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), denied);
        var refused = await FluentActions.Awaiting(() => ServiceFor(fx, denied).UpsertAsync(new Entity(pipe, first) { ["pb_name"] = "s20m-denied" }, createOnly: true))
            .Should().ThrowAsync<XrmdFaultException>();
        refused.Which.HttpStatus.Should().Be(403);
        (await kit.ScalarAsync($"SELECT pb_name FROM data.{pipe} WHERE {pipe}id = '{first}'")).Should().Be("s20m-a");
    }

    // ---- Task 3: custom API authoring and registration validation on the model (Decisions 1-3) --------------------------

    /// <summary>
    /// <see cref="CustomApiAuthoring.BeforeCreateAsync(Request, Record, Xrmd.Abstractions.DataSession, CancellationToken)"/> on a model
    /// <c>customapi</c> Create answers true and fills the model Target: <c>boundentitylogicalname</c> lower-cased, the three flags
    /// false, the two choices left out as <see cref="ChoiceValue"/> 0 (a bound API keeps its <c>bindingtype</c> 1, so a global one
    /// shows the filled <c>bindingtype</c>), and <c>sdkmessageid</c> as a <see cref="RecordReference"/> to the API's message.
    /// <see cref="CustomApiAuthoring.BeforeUpdateAsync(Record, Record, Xrmd.Abstractions.DataSession, CancellationToken)"/> of a row
    /// that repeats its stored values answers false and leaves the Target as sent. The Creates run in a transaction rolled back.
    /// </summary>
    [Fact]
    public async Task Custom_api_authoring_fills_the_model_target()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        using var scope = fx.App.Services.CreateScope();
        var authoring = scope.ServiceProvider.GetRequiredService<CustomApiAuthoring>();
        var core = scope.ServiceProvider.GetRequiredService<IPipelineCore>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (bound, global) = ($"new_S20mBound{suffix}", $"new_S20mGlobal{suffix}");

        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var tx = await conn.BeginTransactionAsync();
            var s = new DataSession(conn, tx);
            var target = new Record(CustomApiTables.Api)
            {
                ["uniquename"] = bound, ["name"] = bound, ["bindingtype"] = new ChoiceValue(1), ["boundentitylogicalname"] = "Account",
            };
            (await authoring.BeforeCreateAsync(new Request { Name = "Create", Parameters = { ["Target"] = target } }, target, s, default))
                .Should().BeTrue();
            target["boundentitylogicalname"].Should().Be("account");
            target["isprivate"].Should().Be(false);
            target["workflowsdkstepenabled"].Should().Be(false);
            target["isfunction"].Should().Be(false);
            target["allowedcustomprocessingsteptype"].Should().Be(new ChoiceValue(0));
            target["sdkmessageid"].Should().BeOfType<RecordReference>().Which.Should()
                .BeEquivalentTo(new { LogicalName = "sdkmessage", Id = SdkMessageCatalog.MessageId(bound) });

            var globalTarget = new Record(CustomApiTables.Api) { ["uniquename"] = global, ["name"] = global };
            (await authoring.BeforeCreateAsync(new Request { Name = "Create", Parameters = { ["Target"] = globalTarget } }, globalTarget, s, default))
                .Should().BeTrue();
            globalTarget["bindingtype"].Should().Be(new ChoiceValue(0));
            globalTarget["allowedcustomprocessingsteptype"].Should().Be(new ChoiceValue(0));
            globalTarget["sdkmessageid"].Should().BeOfType<RecordReference>().Which.Id.Should().Be(SdkMessageCatalog.MessageId(global));
            await tx.RollbackAsync();
        }

        var admin = ServiceFor(fx, fx.AdminObjectId);
        var id = await admin.CreateAsync(new Entity(CustomApiTables.Api)
        {
            ["uniquename"] = global, ["name"] = global, ["isfunction"] = false, ["allowedcustomprocessingsteptype"] = new OptionSetValue(0),
        });
        try
        {
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            var s = new DataSession(conn, tx);
            var existing = await core.RetrieveImageAsync(new RecordReference { LogicalName = CustomApiTables.Api, Id = id }, null, s, default);
            existing.Should().NotBeNull();
            var update = new Record(CustomApiTables.Api, id) { ["uniquename"] = global, ["bindingtype"] = new ChoiceValue(0), ["description"] = "s20m" };
            (await authoring.BeforeUpdateAsync(update, existing!, s, default)).Should().BeFalse();
            update.Attributes.Keys.Should().Equal("uniquename", "bindingtype", "description");
            update["uniquename"].Should().Be(global);
            await tx.RollbackAsync();
        }
        finally
        {
            await admin.DeleteAsync(CustomApiTables.Api, id);
        }
    }

    /// <summary>
    /// <see cref="RegistrationValidator.ValidateAsync(string, Record, Record?, Xrmd.Abstractions.DataSession, CancellationToken)"/> of a
    /// <c>pluginassembly</c> Create answers true with <c>name</c>, <c>version</c>, <c>culture</c> and <c>publickeytoken</c> filled from
    /// the assembly; of a <c>plugintype</c> Update with a string <c>isworkflowactivity</c> it throws today's
    /// <see cref="InvalidCastException"/> text, which is the SDK's own text for <c>Entity.GetAttributeValue&lt;bool?&gt;</c> of that
    /// value (pre-flight ruling T3b: <see cref="SdkTypeNames.CastText"/> gives the expected text and the SDK pins it).
    /// </summary>
    [Fact]
    public async Task Registration_fills_the_model_target()
    {
        await PluginTestHelper.ClearAsync(fx);
        var validator = fx.App.Services.GetRequiredService<RegistrationValidator>();
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var tx = await conn.BeginTransactionAsync();
            var s = new DataSession(conn, tx);
            var assembly = new Record("pluginassembly") { ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes) };
            (await validator.ValidateAsync("pluginassembly", assembly, null, s, default)).Should().BeTrue();
            assembly["name"].Should().Be("Xrmd.TestPlugins");
            assembly["version"].Should().Be("1.0.0.0");
            assembly["culture"].Should().Be("neutral");
            assembly["publickeytoken"].Should().Be("a15f04ad73430aaf");
            await tx.RollbackAsync();
        }

        var admin = ServiceFor(fx, fx.AdminObjectId);
        var assemblyId = await PluginTestHelper.RegisterAssemblyAsync(admin, PluginTestHelper.TestPluginBytes);
        var typeId = await PluginTestHelper.RegisterTypeAsync(admin, assemblyId, Plugins + "ContextDumpPlugin");
        try
        {
            var expected = SdkTypeNames.CastText(typeof(string), typeof(bool?));
            FluentActions.Invoking(() => new Entity("plugintype") { ["isworkflowactivity"] = "yes" }.GetAttributeValue<bool?>("isworkflowactivity"))
                .Should().Throw<InvalidCastException>().Which.Message.Should().Be(expected, "the SDK's own text");

            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            var s = new DataSession(conn, tx);
            using var scope = fx.App.Services.CreateScope();
            var existing = await scope.ServiceProvider.GetRequiredService<IPipelineCore>()
                .RetrieveImageAsync(new RecordReference { LogicalName = "plugintype", Id = typeId }, null, s, default);
            var update = new Record("plugintype", typeId) { ["isworkflowactivity"] = "yes" };
            (await FluentActions.Awaiting(() => validator.ValidateAsync("plugintype", update, existing, s, default))
                .Should().ThrowAsync<InvalidCastException>()).Which.Message.Should().Be(expected);
            await tx.RollbackAsync();
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
    }

    /// <summary>
    /// The solution import keeps the registration validation through the SDK twin of
    /// <see cref="RegistrationValidator.ValidateAsync(string, Record, Record?, Xrmd.Abstractions.DataSession, CancellationToken)"/>: the
    /// fixture's plug-in assembly, which the file names only, is stored with the columns the model member fills for the same content.
    /// </summary>
    [Fact]
    public async Task Solution_import_keeps_registration_validation()
    {
        var assemblyId = new Guid("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e10"); // the fixture's PluginAssemblyId
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
        try
        {
            var model = new Record("pluginassembly") { ["name"] = "Xrmd.TestPlugins", ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes) };
            await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
            {
                await using var tx = await conn.BeginTransactionAsync();
                (await fx.App.Services.GetRequiredService<RegistrationValidator>()
                    .ValidateAsync("pluginassembly", model, null, new DataSession(conn, tx), default)).Should().BeTrue();
                await tx.RollbackAsync();
            }

            await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(new ImportSolutionRequest
            {
                CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = Guid.NewGuid(),
            });

            await using var read = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var cmd = TestSql.Command(read,
                "SELECT name, version, culture, publickeytoken FROM data.pluginassembly WHERE pluginassemblyid = @id");
            cmd.Parameters.AddWithValue("@id", assemblyId);
            await using var rd = await cmd.ExecuteReaderAsync();
            (await rd.ReadAsync()).Should().BeTrue("the import wrote the assembly");
            new[] { rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3) }.Should().Equal(
                (string)model["name"]!, (string)model["version"]!, (string)model["culture"]!, (string)model["publickeytoken"]!);
            rd.GetString(3).Should().Be("a15f04ad73430aaf");
        }
        finally
        {
            await SolutionImportTests.ClearImportAsync(fx);
        }
    }

    // ---- Task 4: service endpoints and mail secrets on the model (Decisions 2 and 6) -------------------------------------

    /// <summary>
    /// Decision 6: <see cref="PendingMailSecrets.StripRequest"/> on a model <c>mailbox</c> Create answers true and leaves the model Target
    /// without its <c>password</c>; <see cref="PendingMailSecrets.Claim"/> of that <see cref="Record"/> gives the value once. A request
    /// without a secret, and a message that writes nothing, answer false.
    /// </summary>
    [Fact]
    public void Mail_secrets_are_keyed_on_the_model_target()
    {
        var table = fx.App.Services.GetRequiredService<IMetadataStore>().GetTable(MailSchema.Mailbox)!;
        var target = new Record(MailSchema.Mailbox) { ["name"] = "s20m", ["password"] = "s20m-pa55" };

        PendingMailSecrets.StripRequest(new Request { Name = "Create", Parameters = { ["Target"] = target } }, table).Should().BeTrue();

        target.Attributes.Keys.Should().Equal("name");
        PendingMailSecrets.Claim(target).Should().Equal(new Dictionary<string, string?> { ["password"] = "s20m-pa55" });
        PendingMailSecrets.Claim(target).Should().BeEmpty("the values are claimed once");

        var plain = new Record(MailSchema.Mailbox) { ["name"] = "s20m" };
        PendingMailSecrets.StripRequest(new Request { Name = "Create", Parameters = { ["Target"] = plain } }, table).Should().BeFalse();
        var read = new Record(MailSchema.Mailbox) { ["password"] = "s20m-r3ad" };
        PendingMailSecrets.StripRequest(new Request { Name = "Retrieve", Parameters = { ["Target"] = read } }, table).Should().BeFalse();
        read.Contains("password").Should().BeTrue("a message that writes nothing is left as it is");

        var rows = new RecordCollection { Records = { new Record(MailSchema.Mailbox) { ["password"] = "s20m-0ne" }, new Record(MailSchema.Mailbox) { ["name"] = "s20m" } } };
        PendingMailSecrets.StripRequest(new Request { Name = "CreateMultiple", Parameters = { ["Targets"] = rows } }, table).Should().BeTrue();
        rows.Records[0].Attributes.Should().BeEmpty();
        PendingMailSecrets.Claim(rows.Records[0]).Should().Equal(new Dictionary<string, string?> { ["password"] = "s20m-0ne" });
        PendingMailSecrets.Claim(rows.Records[1]).Should().BeEmpty();
    }

    /// <summary>
    /// Moved from <see cref="CoreSdkBridgeTests"/> (S.2e3b Task 5, the claim bridges are gone): the claim of the secrets the pipeline
    /// stripped from a model Target is keyed by that <see cref="Record"/> instance (another record with the same values claims nothing),
    /// once, and nothing is converted.
    /// </summary>
    [Fact]
    public void Claim_gives_the_stripped_secrets_once_by_the_model_target()
    {
        var table = fx.App.Services.GetRequiredService<IMetadataStore>().GetTable(MailSchema.Mailbox)!;
        var stripped = new Record(MailSchema.Mailbox) { ["name"] = "crb", ["password"] = "crb-secret" };
        PendingMailSecrets.Strip(stripped, table).Should().BeTrue();
        var twin = new Record(MailSchema.Mailbox) { ["name"] = "crb" };
        IReadOnlyDictionary<string, string?> none, claimed, again;
        using (var count = CoreSdkBridges.CountCrossings())
        {
            none = PendingMailSecrets.Claim(twin);
            claimed = PendingMailSecrets.Claim(stripped);
            again = PendingMailSecrets.Claim(stripped);
            count.Value.Should().Be(0);
        }
        none.Should().BeEmpty("another instance is not the stripped Target");
        claimed.Should().Equal(new Dictionary<string, string?> { ["password"] = "crb-secret" });
        again.Should().BeEmpty("the values are claimed once");

        var direct = new Record(MailSchema.Mailbox) { ["password"] = "crb-direct" };
        PendingMailSecrets.Strip(direct, table).Should().BeTrue();
        PendingMailSecrets.Claim(direct).Should().Equal(new Dictionary<string, string?> { ["password"] = "crb-direct" });
        PendingMailSecrets.Claim(new Record("contact")).Should().BeEmpty();
    }

    /// <summary>
    /// Decision 6 and Review Focus 2: a <c>mailbox</c> Upsert that creates, then one that updates, and a CreateMultiple of two mailboxes
    /// store each password; the caller's Targets hold none after the call, and neither does a PostOperation step of the inner messages
    /// (Create, Update) or of the outer one (CreateMultiple), the <c>ParentContext</c> of an inner execution, nor any level of the
    /// asynchronous payloads.
    /// </summary>
    [Fact]
    public async Task Upsert_and_bulk_carry_mail_secrets()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var s20 = new Stage20Kit(kit);
        var queue = await s20.QueueAsync();
        await s20.RecordStage40Async(MailSchema.Mailbox, "Create", "Update", "CreateMultiple");
        var id = Guid.NewGuid();

        async Task<string> RunAsync(OrganizationRequest request, params Entity[] targets)
        {
            var jobs = await s20.AsyncOperationIdsAsync();
            await kit.ExecAsync($"DELETE FROM data.{TestEntities.PipeLog}");
            var (_, fault) = await CoreScenarios.ViaPipelineAsync(kit, request);
            fault.Should().BeNull(request.RequestName);
            foreach (var target in targets) target.Contains("password").Should().BeFalse("the caller's Target holds no secret after the call");
            var seen = await s20.Stage40TextAsync(jobs);
            seen.Should().NotContain("password", "no later context holds a secret");
            return seen;
        }

        async Task<string?> SecretAsync(string name) => (string?)await kit.ScalarAsync(
            $"SELECT s.value FROM meta.mail_secret s JOIN data.mailbox m ON m.mailboxid = s.recordid WHERE m.name = '{name}' AND s.name = 'password'");

        var created = Stage20Kit.Mailbox("s20 m4 upserted", queue, "s20m4-cr3ate");
        created.Id = id;
        var seen = await RunAsync(new UpsertRequest { Target = created }, created);
        seen.Should().Contain("step Create|40|0|1: target=").And.Contain("parent=Upsert; parenttarget=emailaddress,name,regardingobjectid,username");
        (await SecretAsync("s20 m4 upserted")).Should().Be("s20m4-cr3ate");

        var updated = Stage20Kit.Mailbox("s20 m4 upserted", queue, "s20m4-upd4te");
        updated.Id = id;
        seen = await RunAsync(new UpsertRequest { Target = updated }, updated);
        seen.Should().Contain("step Update|40|0|1: target=").And.Contain("parent=Upsert; parenttarget=emailaddress,name,regardingobjectid,username");
        (await SecretAsync("s20 m4 upserted")).Should().Be("s20m4-upd4te");

        var first = Stage20Kit.Mailbox("s20 m4 one", queue, "s20m4-0ne");
        var second = Stage20Kit.Mailbox("s20 m4 two", queue, "s20m4-tw0");
        seen = await RunAsync(new CreateMultipleRequest { Targets = new EntityCollection([first, second]) { EntityName = MailSchema.Mailbox } }, first, second);
        seen.Should().Contain("parent=CreateMultiple").And.Contain("CreateMultiple|30 target= targets=");
        (await SecretAsync("s20 m4 one")).Should().Be("s20m4-0ne");
        (await SecretAsync("s20 m4 two")).Should().Be("s20m4-tw0");
    }

    /// <summary>
    /// Decision 2: for every case of <c>ServiceEndpointRulesTests</c> (Xrmd.Pipeline.Tests), <see cref="ServiceEndpointRules.EndpointProblem(Record, bool)"/>
    /// on a model record built independently of the mirror equals the answer of the SDK twin
    /// <see cref="ServiceEndpointRules.EndpointProblem(Entity, bool)"/>; an <c>OptionSetValue</c> url (Review Focus 4) throws the same
    /// <see cref="InvalidCastException"/> text, the SDK's own.
    /// </summary>
    [Fact]
    public void Endpoint_problem_is_the_same_on_the_model()
    {
        var cases = EndpointCases().ToList();
        cases.Should().HaveCountGreaterThan(30);
        foreach (var (index, (spec, anyPort)) in cases.Index())
        {
            var twin = ServiceEndpointRules.EndpointProblem(EndpointEntity(spec), anyPort);
            ServiceEndpointRules.EndpointProblem(EndpointRecord(spec), anyPort).Should().Be(twin, $"case {index}");
        }

        var url = new Dictionary<string, object?>(Endpoint()) { ["url"] = new OptionSetValue(1) };
        var sdkText = FluentActions.Invoking(() => EndpointEntity(url).GetAttributeValue<string>("url")).Should().Throw<InvalidCastException>().Which.Message;
        FluentActions.Invoking(() => ServiceEndpointRules.EndpointProblem(EndpointEntity(url), false)).Should().Throw<InvalidCastException>()
            .Which.Message.Should().Be(sdkText);
        FluentActions.Invoking(() => ServiceEndpointRules.EndpointProblem(EndpointRecord(url), false)).Should().Throw<InvalidCastException>()
            .Which.Message.Should().Be(sdkText);
    }

    /// <summary>The cases of <c>ServiceEndpointRulesTests</c>, as SDK values: the endpoint's columns and the port flag.</summary>
    private static IEnumerable<(Dictionary<string, object?> Spec, bool AnyPort)> EndpointCases()
    {
        const string url = "https://hooks.example.invalid/receive";
        static string Settings(params (string Name, string Value)[] settings) =>
            "<settings>" + string.Concat(settings.Select(s => $"<setting name=\"{s.Name}\" value=\"{s.Value}\" />")) + "</settings>";
        static Dictionary<string, object?> With(Dictionary<string, object?> spec, string column, object? value)
        {
            spec[column] = value;
            return spec;
        }
        static Dictionary<string, object?> Without(Dictionary<string, object?> spec, string column)
        {
            spec.Remove(column);
            return spec;
        }

        yield return (Endpoint(), false);
        yield return (With(Endpoint(), "contract", new OptionSetValue(2)), false);
        yield return (Without(Endpoint(), "contract"), false);
        yield return (Endpoint(authType: 2), false);
        yield return (Endpoint(url: "https://hooks.example.invalid:8080/receive"), false);
        yield return (Endpoint(url: "https://hooks.example.invalid:8080/receive"), true);
        yield return (Endpoint(url: "http://hooks.example.invalid/receive"), false);
        yield return (Endpoint(url: "ftp://hooks.example.invalid/receive"), false);
        yield return (Endpoint(url: "https://alice:pa55@hooks.example.invalid/receive"), false);
        yield return (Endpoint(url: "/receive"), false);
        yield return (Endpoint(url: url + "#part"), false);
        yield return (Without(Endpoint(), "url"), false);
        yield return (Endpoint(authType: 5, authValue: Settings(("X-Api-Key", "one"))), false);
        yield return (Endpoint(authType: 6, authValue: Settings(("token", "one"))), false);
        yield return (Endpoint(authType: 5, authValue: Settings(("Host", "elsewhere.invalid"))), false);
        yield return (Endpoint(authType: 5, authValue: Settings(("x-ms-dynamics-organization", "other"))), false);
        yield return (Endpoint(authType: 5, authValue: Settings(("Api Key", "one"))), false);
        yield return (Endpoint(authType: 6, authValue: "<settings></settings>"), false);
        yield return (Endpoint(authType: 6, authValue: Settings(("  ", "one"))), false);
        yield return (Endpoint(authType: 5, authValue: "not xml"), false);
        yield return (Endpoint(authType: 6, authValue: "<settings><setting name=\"t0ken-name\" /></settings>"), false);
        yield return (Endpoint(authValue: new string('k', 2049)), false);
        yield return (Endpoint(authValue: new string('k', 2048)), false);
        yield return (Endpoint(authValue: null), false);
        yield return (Endpoint(authValue: "   "), false);
        yield return (With(Endpoint(), "path", "queue-1"), false);
        yield return (With(Endpoint(), "messageformat", new OptionSetValue(1)), false);
        yield return (With(Endpoint(), "connectionmode", new OptionSetValue(2)), false);
        yield return (With(With(With(With(Endpoint(), "messageformat", new OptionSetValue(2)), "messagecharset", new OptionSetValue(1)),
            "connectionmode", new OptionSetValue(1)), "userclaim", new OptionSetValue(1)), false);
        foreach (var value in new[] { "a&#13;b", "a&#10;b", "café", "a&#127;b" })
        {
            yield return (Endpoint(authType: 5, authValue: Settings(("X-Api-Key", value))), false);
            yield return (Endpoint(authType: 6, authValue: Settings(("key", value))), false);
        }
        yield return (Endpoint(authType: 5, authValue: Settings(("X-Api-Key", "Bearer a.b-c_d~e/f+g=&#9;h"))), false);
        yield return (Endpoint(authType: 5, authValue: Settings(("X-Api-Key", "s3cr3t&#13;&#10;Injected: yes"))), false);
    }

    /// <summary>The webhook of <c>ServiceEndpointRulesTests</c>, as SDK values.</summary>
    private static Dictionary<string, object?> Endpoint(int authType = 4, string? authValue = "key-1", string url = "https://hooks.example.invalid/receive")
    {
        var spec = new Dictionary<string, object?>
        {
            ["name"] = "orders hook", ["contract"] = new OptionSetValue(8), ["authtype"] = new OptionSetValue(authType), ["url"] = url,
        };
        if (authValue is not null) spec["authvalue"] = authValue;
        return spec;
    }

    private static Entity EndpointEntity(Dictionary<string, object?> spec)
    {
        var entity = new Entity(ServiceEndpointSchema.Table);
        foreach (var (column, value) in spec) entity[column] = value;
        return entity;
    }

    /// <summary>The model record of <paramref name="spec"/>, built without the mirror: an <c>OptionSetValue</c> is a <see cref="ChoiceValue"/>.</summary>
    private static Record EndpointRecord(Dictionary<string, object?> spec)
    {
        var record = new Record(ServiceEndpointSchema.Table);
        foreach (var (column, value) in spec) record[column] = value is OptionSetValue o ? new ChoiceValue(o.Value) : value;
        return record;
    }

    // ---- Task 5: process writes, the Workflows hook and stage 20 on the model request (Decisions 2, 4, 5) ----------------

    /// <summary>
    /// Decisions 2 and 4: the internal checks of stage 20 run on the model request (<see cref="PipelineExecutor.StageChecked"/> sees it),
    /// which is the instance the core gets (<see cref="CoreOperations.CrudRequest"/>): for a <c>customapi</c> Create, whose model Target
    /// then holds the filled <c>sdkmessageid</c>, and for a <c>serviceendpoint</c> Update, whose model Target holds no <c>authvalue</c>.
    /// </summary>
    [Fact]
    public async Task Stage_20_checks_run_on_the_model_request()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        kit.OnCleanup(() => PluginTestHelper.ClearAsync(fx));
        var s20 = new Stage20Kit(kit);
        await s20.EndpointsAsync();
        var endpoint = await s20.CreateAsync(Stage20Kit.Webhook("s20 m5 hook", "s20m5-f1rst"), "endpoint");
        var api = "new_S20m5" + Guid.NewGuid().ToString("N")[..8];
        var checkedRequests = new List<Request>();
        var coreRequests = new List<Request>();
        PipelineExecutor.StageChecked.Value = checkedRequests.Add;
        CoreOperations.CrudRequest.Value = coreRequests.Add;
        try
        {
            (await CoreScenarios.ViaPipelineAsync(kit, new CreateRequest { Target = Stage20Kit.Api(api) })).Fault.Should().BeNull();
            (await CoreScenarios.ViaPipelineAsync(kit, new UpdateRequest
            {
                Target = new Entity(ServiceEndpointSchema.Table, endpoint) { [ServiceEndpointSchema.AuthValue] = "s20m5-s3cond" },
            })).Fault.Should().BeNull();
        }
        finally
        {
            PipelineExecutor.StageChecked.Value = null;
            CoreOperations.CrudRequest.Value = null;
        }

        checkedRequests.Select(r => r.Name).Should().Equal("Create", "Update");
        foreach (var request in checkedRequests)
            coreRequests.Should().Contain(r => ReferenceEquals(r, request), $"the core gets the {request.Name} the checks ran on");
        var created = (Record)checkedRequests[0].Parameters["Target"]!;
        created.LogicalName.Should().Be(CustomApiTables.Api);
        created["sdkmessageid"].Should().BeOfType<RecordReference>().Which.Id.Should().Be(SdkMessageCatalog.MessageId(api));
        var updated = (Record)checkedRequests[1].Parameters["Target"]!;
        updated.Contains(ServiceEndpointSchema.AuthValue).Should().BeFalse();
        updated[ServiceEndpointSchema.IsAuthValueSet].Should().Be(true);
    }

    /// <summary>
    /// Decisions 4 and 9: a check that changes nothing crosses nothing. An Update of a step that changes only its <c>description</c> (the
    /// registration validation reads the stored step, which the core reads with no mirror) and a Delete of a <c>serviceendpoint</c> add
    /// nothing to the chain mirror's <see cref="SdkMirror.SyncCount"/> between the start of the checks and the core's dispatch.
    /// </summary>
    [Fact]
    public async Task Checks_that_write_nothing_cross_nothing()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        kit.OnCleanup(() => PluginTestHelper.ClearAsync(fx));
        var s20 = new Stage20Kit(kit);
        var step = await s20.PluginStepAsync();
        await s20.EndpointsAsync();
        var endpoint = await s20.CreateAsync(Stage20Kit.Webhook("s20 m5 hook", "s20m5-k3y"), "endpoint");

        async Task<long> CrossedAtStage20Async(OrganizationRequest request)
        {
            Request? checkedRequest = null;
            long? atChecks = null, atCore = null;
            PipelineExecutor.StageChecked.Value = r =>
            {
                checkedRequest = r;
                atChecks = SdkMirror.For(r)!.SyncCount;
            };
            CoreOperations.CrudRequest.Value = r =>
            {
                if (ReferenceEquals(r, checkedRequest)) atCore = SdkMirror.For(r)!.SyncCount;
            };
            try
            {
                (await CoreScenarios.ViaPipelineAsync(kit, request)).Fault.Should().BeNull(request.RequestName);
            }
            finally
            {
                PipelineExecutor.StageChecked.Value = null;
                CoreOperations.CrudRequest.Value = null;
            }
            atChecks.Should().NotBeNull(request.RequestName);
            atCore.Should().NotBeNull(request.RequestName);
            return atCore!.Value - atChecks!.Value;
        }

        (await CrossedAtStage20Async(new UpdateRequest { Target = new Entity("sdkmessageprocessingstep", step) { ["description"] = "s20m5" } }))
            .Should().Be(0, "a step Update whose validation changes nothing crosses nothing");
        (await kit.ScalarAsync($"SELECT description FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = '{step}'")).Should().Be("s20m5");
        (await CrossedAtStage20Async(new DeleteRequest { Target = new EntityReference(ServiceEndpointSchema.Table, endpoint) }))
            .Should().Be(0, "an endpoint Delete crosses nothing");
        (await kit.ScalarAsync("SELECT COUNT(*) FROM meta.serviceendpoint_secret")).Should().Be(0);
    }

    /// <summary>
    /// Decision 5: the Workflows hook is called behind its lent view. Its <c>Target</c> is the caller's own <see cref="Entity"/>, whose
    /// model twin is the model request's Target, written from the model before the call: a workflow Create without <c>type</c> shows
    /// <c>type</c> 1 to the hook on the model and on the view, before the pre-operation steps and at the checks of stage 20, and leaves
    /// <c>type</c> 1 in the caller's <see cref="Entity"/>, on success and on the hook's 400.
    /// </summary>
    [Fact]
    public async Task Workflow_hook_sees_the_target_through_its_view()
    {
        var calls = new List<HookCall>();
        await using var app = fx.WithOwnDatabase(b => b.ConfigureTestServices(s =>
            s.AddScoped<IWorkflowPipelineHook>(sp => new SpyHook(ActivatorUtilities.CreateInstance<WorkflowPipelineHook>(sp), calls))));
        _ = app.Server;
        await TestEntities.EnsureOrderAsync(app.Services.GetRequiredService<IMetadataStore>());
        await app.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var caller = await TestServices.CallerAsync(app.Services, fx.AdminObjectId);

        async Task<(Exception? Fault, Request Model)> RunAsync(Entity target)
        {
            var mirror = new SdkMirror();
            var request = new CreateRequest { Target = target };
            calls.Clear();
            SpyHook.Mirror.Value = mirror;
            await using var scope = app.Services.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<PipelineExecutor>().ExecuteAsync(request,
                    new InvocationContext(caller, caller.UserId, 1, null, null, Guid.NewGuid()) { Mirror = mirror }, default);
                return (null, ModelOf(mirror, request));
            }
            catch (Exception ex)
            {
                return (ex, ModelOf(mirror, request));
            }
            finally
            {
                SpyHook.Mirror.Value = null;
            }
        }

        static Request ModelOf(SdkMirror mirror, OrganizationRequest request) =>
            mirror.TryGetModel(request, out var model) ? (Request)model! : throw new InvalidOperationException("The request did not cross.");

        void HookSawTheModel(Entity target, Request model)
        {
            calls.Should().HaveCount(2, "the hook runs before the pre-operation steps and at the checks of stage 20");
            foreach (var call in calls)
            {
                call.Target.Should().BeSameAs(target, "the view's Target is the caller's Entity");
                call.Model.Should().BeSameAs(model.Parameters["Target"], "its model twin is the model request's Target");
                call.ModelType.Should().Be(new ChoiceValue((int)S.Type.Definition), "the model holds type 1 before the hook runs");
                call.ViewType.Should().Be(new OptionSetValue((int)S.Type.Definition), "the view was written from the model");
            }
            target.GetAttributeValue<OptionSetValue>(S.TypeColumn)?.Value.Should().Be((int)S.Type.Definition);
        }

        var accepted = Stage20Kit.Realtime("s20 m5 hooked");
        accepted.Attributes.Remove(S.TypeColumn);
        var (fault, model) = await RunAsync(accepted);
        fault.Should().BeNull();
        HookSawTheModel(accepted, model);

        var refused = Stage20Kit.Realtime("s20 m5 refused");
        refused.Attributes.Remove(S.TypeColumn);
        refused[S.Xaml] = "<Activity>\n  <Sequence>\n</Activity>";
        (fault, model) = await RunAsync(refused);
        fault.Should().BeOfType<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
        calls.Should().ContainSingle("the hook refused before the pre-operation steps");
        calls[0].Target.Should().BeSameAs(refused);
        calls[0].ModelType.Should().Be(new ChoiceValue((int)S.Type.Definition));
        refused.GetAttributeValue<OptionSetValue>(S.TypeColumn)?.Value.Should().Be((int)S.Type.Definition, "the caller's Entity keeps type 1 on the 400");
    }

    /// <summary>What <see cref="SpyHook"/> saw on a call of <see cref="IWorkflowPipelineHook.ValidateWriteAsync"/>.</summary>
    private sealed record HookCall(Entity Target, object? Model, object? ModelType, object? ViewType);

    /// <summary>The host's Workflows hook, recording what <see cref="IWorkflowPipelineHook.ValidateWriteAsync"/> gets.</summary>
    private sealed class SpyHook(IWorkflowPipelineHook inner, List<HookCall> calls) : IWorkflowPipelineHook
    {
        /// <summary>The chain mirror of the call under test, to find the model twin of the Target the hook gets.</summary>
        public static readonly AsyncLocal<SdkMirror?> Mirror = new();

        public Task ValidateWriteAsync(DataSession s, Caller caller, string message, Entity target, CancellationToken ct)
        {
            if (Mirror.Value is { } mirror && target.LogicalName == S.Table)
            {
                var model = mirror.TryGetModel(target, out var twin) ? twin : null;
                calls.Add(new HookCall(target, model, (model as Record)?.Attributes.GetValueOrDefault(S.TypeColumn),
                    target.Attributes.TryGetValue(S.TypeColumn, out var type) ? type : null));
            }
            return inner.ValidateWriteAsync(s, caller, message, target, ct);
        }

        public void Invalidate() => inner.Invalidate();
        public Task OnSetStateAsync(DataSession s, Caller caller, Guid workflowId, int state, CancellationToken ct) => inner.OnSetStateAsync(s, caller, workflowId, state, ct);
        public Task OnAssignedAsync(DataSession s, Guid workflowId, CancellationToken ct) => inner.OnAssignedAsync(s, workflowId, ct);
        public Task<bool> HasRealtimeAsync(string entity, CancellationToken ct) => inner.HasRealtimeAsync(entity, ct);
        public Task<IReadOnlyList<RealtimeWorkflow>> GetRealtimeAsync(string message, string? entity, int stage, IReadOnlyCollection<string> sentColumns,
            RecordOwner? recordOwner, CancellationToken ct) => inner.GetRealtimeAsync(message, entity, stage, sentColumns, recordOwner, ct);
        public Task RunRealtimeAsync(PipelineExecutor executor, RealtimeWorkflow w, XrmdPluginExecutionContext context, InvocationContext invocation,
            DataSession s, CancellationToken ct) => inner.RunRealtimeAsync(executor, w, context, invocation, s, ct);
        public Task<bool> HasBackgroundAsync(string entity, CancellationToken ct) => inner.HasBackgroundAsync(entity, ct);
        public Task QueueBackgroundAsync(string message, string? entity, IReadOnlyCollection<string> sentColumns, RecordOwner? recordOwner,
            XrmdPluginExecutionContext context, InvocationContext invocation, DataSession s, CancellationToken ct) =>
            inner.QueueBackgroundAsync(message, entity, sentColumns, recordOwner, context, invocation, s, ct);
        public Task<ActionDefinition?> FindActionAsync(string message, CancellationToken ct) => inner.FindActionAsync(message, ct);
        public Task RunActionAsync(PipelineExecutor executor, ActionDefinition action, XrmdPluginExecutionContext context, InvocationContext invocation,
            DataSession s, CancellationToken ct) => inner.RunActionAsync(executor, action, context, invocation, s, ct);
        public Task<Guid> ExecuteWorkflowAsync(PipelineExecutor executor, OrganizationRequest request, XrmdPluginExecutionContext context,
            InvocationContext invocation, DataSession s, CancellationToken ct) => inner.ExecuteWorkflowAsync(executor, request, context, invocation, s, ct);
    }

    /// <summary>
    /// Review Focus 3: a PreOperation step widens the Target after the guard and the first process-write check ran, so the internal
    /// checks of stage 20 see the step's values. A mailbox password the step adds is stored and leaves the Target (the caller's, the
    /// stage-40 step's and the asynchronous payload's); a XAML the step puts on a workflow Create gets the hook's 400, today's text, and
    /// the caller's Target holds the step's XAML and <c>type</c> 1; a server-written column the step adds reaches the pipeline's own
    /// <see cref="SystemTableRules.DemandValidProcessWrite(string, Record, Caller)"/> (the guard ran before the step: controller ruling
    /// on the Task 1 goldens, which never reach it). A service endpoint cannot be widened this way: no step runs on <c>serviceendpoint</c>
    /// (plan 3.2, <c>StepRegistry</c>), so its <c>authvalue</c> is only ever the caller's.
    /// </summary>
    [Fact]
    public async Task Pre_operation_step_widens_the_target()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        kit.OnCleanup(() => PluginTestHelper.ClearAsync(fx));
        var s20 = new Stage20Kit(kit);
        var setter = await s20.PluginTypeAsync();

        // A mailbox password added at stage 20.
        var queue = await s20.QueueAsync();
        await s20.RecordStage40Async(MailSchema.Mailbox, "Create");
        await PluginTestHelper.RegisterStepAsync(kit.Admin, setter, "Create", MailSchema.Mailbox, 20, config: "password=s20m5-st3p");
        var jobs = await s20.AsyncOperationIdsAsync();
        await kit.ExecAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        var mailbox = Stage20Kit.Mailbox("s20 m5 widened", queue, "unused");
        mailbox.Attributes.Remove("password");

        (await CoreScenarios.ViaPipelineAsync(kit, new CreateRequest { Target = mailbox })).Fault.Should().BeNull();

        mailbox.Contains("password").Should().BeFalse("the step's secret left the caller's Target");
        (await kit.ScalarAsync(
                "SELECT s.value FROM meta.mail_secret s JOIN data.mailbox m ON m.mailboxid = s.recordid WHERE m.name = 's20 m5 widened' AND s.name = 'password'"))
            .Should().Be("s20m5-st3p");
        (await kit.ScalarAsync("SELECT ispasswordset FROM data.mailbox WHERE name = 's20 m5 widened'")).Should().Be(true);
        var seen = await s20.Stage40TextAsync(jobs);
        seen.Should().Contain("step Create|40|0|1: target=").And.NotContain("password");

        // A XAML the hook refuses, put on a workflow Create at stage 20.
        await s20.WorkflowsAsync();
        const string malformed = "<Activity>\n  <Sequence>\n</Activity>";
        var xamlStep = await PluginTestHelper.RegisterStepAsync(kit.Admin, setter, "Create", S.Table, 20, config: S.Xaml + "=" + malformed);
        var definition = Stage20Kit.Realtime("s20 m5 xaml");
        definition.Attributes.Remove(S.TypeColumn);

        var fault = (await CoreScenarios.ViaPipelineAsync(kit, new CreateRequest { Target = definition })).Fault;

        var refused = fault.Should().BeOfType<XrmdFaultException>().Subject;
        refused.HttpStatus.Should().Be(400);
        refused.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        refused.Message.Should().Be(
            "The Create of the process 's20 m5 xaml' is refused: The workflow XAML is not well-formed (line 3, position 3): The 'Sequence' start tag on line 2 position 4 does not match the end tag of 'Activity'. Line 3, position 3.");
        definition[S.Xaml].Should().Be(malformed, "the caller's Target holds what the step wrote");
        definition.GetAttributeValue<OptionSetValue>(S.TypeColumn)?.Value.Should().Be((int)S.Type.Definition);
        (await kit.ScalarAsync("SELECT COUNT(*) FROM data.workflow WHERE name = 's20 m5 xaml'")).Should().Be(0);
        await kit.Admin.DeleteAsync("sdkmessageprocessingstep", xamlStep);

        // A server-written column added at stage 20: the pipeline's own process-write check of stage 20 refuses it.
        await PluginTestHelper.RegisterStepAsync(kit.Admin, setter, "Create", S.Table, 20, config: "ismanaged=true");
        var managed = Stage20Kit.Realtime("s20 m5 managed");
        managed.Attributes.Remove(S.TypeColumn);

        fault = (await CoreScenarios.ViaPipelineAsync(kit, new CreateRequest { Target = managed })).Fault;

        refused = fault.Should().BeOfType<XrmdFaultException>().Subject;
        refused.HttpStatus.Should().Be(400);
        refused.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        refused.Message.Should().Be("Create of 'workflow' is refused: The column ismanaged is written by the server and cannot be set.");
        managed["ismanaged"].Should().Be("true");
        managed.GetAttributeValue<OptionSetValue>(S.TypeColumn)?.Value.Should().Be((int)S.Type.Definition);
        (await kit.ScalarAsync("SELECT COUNT(*) FROM data.workflow WHERE name = 's20 m5 managed'")).Should().Be(0);
    }

    /// <summary>
    /// Review Focus 4 (pre-flight ruling P1): a Create with a string <c>ownerid</c> on a table with a real-time workflow, as
    /// <see cref="Caller.Internal"/> (the guard, which casts <c>ownerid</c> first for any other caller, is skipped), reaches the owner read
    /// of the real-time workflow match on the model Target: today's <see cref="InvalidCastException"/> text, the SDK's own for
    /// <c>Entity.GetAttributeValue&lt;EntityReference&gt;</c>; nothing is written.
    /// </summary>
    [Fact]
    public async Task Real_time_owner_reads_the_model()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var s20 = new Stage20Kit(kit);
        await s20.RealtimeOnOrderAsync();
        var expected = SdkTypeNames.CastText(typeof(string), typeof(RecordReference));
        FluentActions.Invoking(() => new Entity(TestEntities.Order) { ["ownerid"] = "s20 owner" }.GetAttributeValue<EntityReference>("ownerid"))
            .Should().Throw<InvalidCastException>().Which.Message.Should().Be(expected, "the SDK's own text");

        var target = new Entity(TestEntities.Order) { ["xrmv_name"] = "s20 m5 owner", ["ownerid"] = "s20 owner" };
        var fault = await new GuardKit(kit).RunAsync(new CreateRequest { Target = target }, Caller.Internal);

        fault.Should().BeOfType<InvalidCastException>().Which.Message.Should().Be(expected);
        target.Attributes.Keys.Should().Equal("xrmv_name", "ownerid");
        (await kit.ScalarAsync("SELECT COUNT(*) FROM data.xrmv_order WHERE xrmv_name = 's20 m5 owner'")).Should().Be(0);
    }

    /// <summary>
    /// Pre-flight ruling T4b (controller ruling on the Task 4 review): an UpdateMultiple and an UpsertMultiple of mailboxes carrying
    /// passwords keep the held window of stage 30; the secrets leave the model Targets at stage 20 and the model is written out once, so the
    /// held SDK request, the caller's Targets, the inner steps of stage 40 and the asynchronous payloads hold none, and each password is
    /// stored. A refusal after a partial strip (a valid password, then a non-string one) writes the model out too: the caller's Targets
    /// hold neither value, and nothing is stored.
    /// </summary>
    [Fact]
    public async Task Bulk_updates_strip_mail_secrets_on_the_held_path()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var s20 = new Stage20Kit(kit);
        var queue = await s20.QueueAsync();
        var first = await s20.CreateAsync(Stage20Kit.Mailbox("s20 m5 one", queue, "s20m5-0ne"), "one");
        var second = await s20.CreateAsync(Stage20Kit.Mailbox("s20 m5 two", queue, "s20m5-tw0"), "two");
        await s20.RecordStage40Async(MailSchema.Mailbox, "Create", "Update");

        async Task<string?> SecretAsync(string name) => (string?)await kit.ScalarAsync(
            $"SELECT s.value FROM meta.mail_secret s JOIN data.mailbox m ON m.mailboxid = s.recordid WHERE m.name = '{name}' AND s.name = 'password'");

        async Task<string> RunAsync(OrganizationRequest request, Entity[] targets)
        {
            var jobs = await s20.AsyncOperationIdsAsync();
            await kit.ExecAsync($"DELETE FROM data.{TestEntities.PipeLog}");
            (await CoreScenarios.ViaPipelineAsync(kit, request)).Fault.Should().BeNull(request.RequestName);
            foreach (var target in targets) target.Contains("password").Should().BeFalse("the caller's Target holds no secret after the call");
            var seen = await s20.Stage40TextAsync(jobs);
            seen.Should().NotContain("password", "no later context holds a secret");
            return seen;
        }

        Entity[] updates =
        [
            new(MailSchema.Mailbox, first) { ["password"] = "s20m5-0ne-upd" }, new(MailSchema.Mailbox, second) { ["password"] = "s20m5-tw0-upd" },
        ];
        var seen = await RunAsync(new UpdateMultipleRequest { Targets = new EntityCollection(updates) { EntityName = MailSchema.Mailbox } }, updates);
        seen.Should().Contain("step Update|40|0|1: target=").And.Contain("parent=UpdateMultiple");
        (await SecretAsync("s20 m5 one")).Should().Be("s20m5-0ne-upd");
        (await SecretAsync("s20 m5 two")).Should().Be("s20m5-tw0-upd");

        var created = Stage20Kit.Mailbox("s20 m5 three", queue, "s20m5-thr33");
        created.Id = Guid.NewGuid();
        Entity[] upserts = [new(MailSchema.Mailbox, first) { ["password"] = "s20m5-0ne-ups" }, created];
        seen = await RunAsync(new UpsertMultipleRequest { Targets = new EntityCollection(upserts) { EntityName = MailSchema.Mailbox } }, upserts);
        seen.Should().Contain("step Update|40|0|1: target=").And.Contain("step Create|40|0|1: target=");
        (await SecretAsync("s20 m5 one")).Should().Be("s20m5-0ne-ups");
        (await SecretAsync("s20 m5 three")).Should().Be("s20m5-thr33");

        Entity[] refusedRows =
        [
            new(MailSchema.Mailbox, first) { ["password"] = "s20m5-0ne-l0st", ["name"] = "s20 m5 one" },
            new(MailSchema.Mailbox, second) { ["password"] = 42 },
        ];
        var fault = (await CoreScenarios.ViaPipelineAsync(kit,
            new UpdateMultipleRequest { Targets = new EntityCollection(refusedRows) { EntityName = MailSchema.Mailbox } })).Fault;
        var refused = fault.Should().BeOfType<XrmdFaultException>().Subject;
        refused.HttpStatus.Should().Be(400);
        refused.Message.Should().Be("Attribute 'password' of entity 'mailbox' must be a string.");
        refusedRows[0].Attributes.Keys.Should().Equal("name");
        refusedRows[1].Attributes.Should().BeEmpty();
        (await SecretAsync("s20 m5 one")).Should().Be("s20m5-0ne-ups", "the refused request stored nothing");
        (await SecretAsync("s20 m5 two")).Should().Be("s20m5-tw0-upd");
    }

    /// <summary>
    /// Fix round 1 of Task 5: a check that fills a column of the caller's Target keeps the caller's column order as the SDK request
    /// filled in place did (plan base <c>dbbc60f2</c>; the orders below are the ones a probe read there, at <c>2e4224c1</c>, whose src is
    /// identical): the model's write-out removes and sets by key (<c>SdkRecords.Refill</c>), so a
    /// new column takes the slot of one the caller removed before sending, and the columns it kept stay where they are. Pinned for a
    /// <c>serviceendpoint</c> Create (<c>isauthvalueset</c> replaces <c>authvalue</c>), a <c>customapi</c> Create (its defaults and
    /// <c>sdkmessageid</c>) and a <c>pluginassembly</c> Create (the assembly's identity columns).
    /// </summary>
    [Fact]
    public async Task Checks_keep_the_caller_column_order()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await PluginTestHelper.ClearAsync(fx);
        kit.OnCleanup(() => PluginTestHelper.ClearAsync(fx));
        var s20 = new Stage20Kit(kit);
        await s20.EndpointsAsync();

        var endpoint = new Entity(ServiceEndpointSchema.Table)
        {
            ["name"] = "s20 m5 order", ["description"] = "dropped", [ServiceEndpointSchema.AuthValue] = "s20m5-0rder",
            ["url"] = "https://hooks.example.invalid/receive", ["contract"] = new OptionSetValue(ServiceEndpointSchema.WebhookContract),
            ["authtype"] = new OptionSetValue(ServiceEndpointSchema.WebhookKey),
        };
        endpoint.Attributes.Remove("description");
        (await CoreScenarios.ViaPipelineAsync(kit, new CreateRequest { Target = endpoint })).Fault.Should().BeNull();
        endpoint.Attributes.Keys.Should().Equal(EndpointOrder);

        var unique = "new_S20m5Order" + Guid.NewGuid().ToString("N")[..8];
        var api = new Entity(CustomApiTables.Api)
        {
            ["uniquename"] = unique, ["description"] = "dropped", ["name"] = unique, ["displayname"] = "dropped too",
            ["isfunction"] = false,
        };
        api.Attributes.Remove("description");
        api.Attributes.Remove("displayname");
        (await CoreScenarios.ViaPipelineAsync(kit, new CreateRequest { Target = api })).Fault.Should().BeNull();
        api.Attributes.Keys.Should().Equal(ApiOrder);

        var assembly = new Entity("pluginassembly")
        {
            ["description"] = "dropped", ["content"] = Convert.ToBase64String(PluginTestHelper.TestPluginBytes),
            ["isolationmode"] = new OptionSetValue(2),
        };
        assembly.Attributes.Remove("description");
        (await CoreScenarios.ViaPipelineAsync(kit, new CreateRequest { Target = assembly })).Fault.Should().BeNull();
        assembly.Attributes.Keys.Should().Equal(AssemblyOrder);
    }

    private static readonly string[] EndpointOrder = ["name", ServiceEndpointSchema.IsAuthValueSet, "url", "contract", "authtype"];
    private static readonly string[] ApiOrder =
        ["uniquename", "workflowsdkstepenabled", "name", "isprivate", "isfunction", "bindingtype", "allowedcustomprocessingsteptype", "sdkmessageid"];
    private static readonly string[] AssemblyOrder = ["name", "content", "isolationmode", "version", "culture", "publickeytoken"];

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>Registers the test plug-in assembly and <c>ContextDumpPlugin</c> on the kit's database; returns the type's id.</summary>
    private static async Task<Guid> DumpTypeAsync(CoreScenarioKit kit)
    {
        await kit.EnsurePipeAsync();
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(kit.Admin, PluginTestHelper.TestPluginBytes);
        return await PluginTestHelper.RegisterTypeAsync(kit.Admin, assembly, Plugins + "ContextDumpPlugin");
    }

    /// <summary>Deletes the dumps of a test when it ends, so that no other test that reads the dump table finds them.</summary>
    private sealed class DumpCleanup(CoreScenarioKit kit) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await kit.ExecAsync($"DELETE FROM data.{DumpLog}");
    }

    /// <summary>The one dump named <paramref name="name"/> (<c>Message|Stage|Mode|Depth</c>).</summary>
    private static async Task<string> DumpAsync(CoreScenarioKit kit, string name)
    {
        await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_data FROM data.{DumpLog} WHERE pb_name = @name");
        cmd.Parameters.AddWithValue("@name", name);
        var dumps = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) dumps.Add(rd.GetString(0));
        return dumps.Should().ContainSingle($"one dump {name}").Subject;
    }

    /// <summary>
    /// The <c>InputParameters</c> block of a context dump at <paramref name="indent"/> (1: the context's own, 2: its parent's), with
    /// its lines brought to the first level.
    /// </summary>
    private static string InputParameters(string dump, int indent)
    {
        var head = new string(' ', indent * 2) + "InputParameters:";
        var lines = dump.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith(head, StringComparison.Ordinal));
        start.Should().BeGreaterThanOrEqualTo(0, "the dump holds " + head.Trim());
        var block = new List<string> { lines[start][(indent * 2)..] };
        for (var i = start + 1; i < lines.Length && lines[i].Length - lines[i].TrimStart(' ').Length > indent * 2; i++)
            block.Add(lines[i][(indent * 2)..]);
        return string.Join('\n', block);
    }
}
