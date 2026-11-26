using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Security;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3a, Task 1: golden dumps of what the core answers, captured on the code before <c>IPipelineCore</c> moved onto the model. One
/// scenario per message of <c>CoreOperations.CoreMessages</c> and <c>NonPipelineMessages</c> that a test fixture can run (the skip list
/// with reasons is in <c>tests/fixtures/core-response-golden/README.md</c>); each runs (a) through <see cref="PipelineExecutor.ExecuteAsync"/>
/// and (b) through the core's interface directly, inside a transaction: the SDK signatures
/// <see cref="PipelineCoreSdk.ExecuteCoreAsync(IPipelineCore, OrganizationRequest, Caller, DataSession, SdkNestedRequest?, CancellationToken)"/>
/// or <see cref="PipelineCoreSdk.ExecuteNonPipelineAsync"/> (since S.2e3a, extension methods over the model members). It writes the
/// response CLR type, its <c>ResponseName</c> and its results (ordinal key order, values as <c>Type:value</c>), or the fault. The files are the contract of the
/// move (plan S.2e3a, Global Constraints): a missing file is written once and the test fails; an existing file is never rewritten.
/// </summary>
[Collection("xrmd")]
public sealed class CoreResponseGoldenTests(XrmdFixture fx)
{
    public static TheoryData<string> Names => [.. CoreScenarios.All.Select(s => s.Message)];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Core_answers_what_the_golden_file_says(string message)
    {
        var scenario = CoreScenarios.All.Single(s => s.Message == message);
        await using var kit = await CoreScenarioKit.StartAsync(fx, scenario.OwnDatabase);
        var file = new StringBuilder();

        // Each section starts with the request (its ids share their tokens with the response); the rows a scenario arranges, and
        // those its call creates (read back by the anchor), are named per section ({a:created}, {b:queueitem}), so a returned id
        // that is not the stored row's fails.
        kit.Section = "a";
        file.Append("=== (a) PipelineExecutor.ExecuteAsync ===\n");
        var request = await scenario.Arrange(kit);
        file.Append("--- request ---\n").Append(CoreDump.RequestText(request)).Append("--- response ---\n");
        var (viaPipeline, pipelineFault) = await CoreScenarios.ViaPipelineAsync(kit, request);
        if (scenario.Anchor is { } anchorA && pipelineFault is null) await anchorA(kit);
        file.Append(CoreDump.Response(viaPipeline, pipelineFault, scenario.AllColumnsOrder));

        kit.Section = "b";
        file.Append(scenario.NonPipeline ? "=== (b) IPipelineCore.ExecuteNonPipelineAsync ===\n" : "=== (b) IPipelineCore.ExecuteCoreAsync ===\n");
        request = await scenario.Arrange(kit);
        file.Append("--- request ---\n").Append(CoreDump.RequestText(request)).Append("--- response ---\n");
        var (viaCore, coreFault) = await CoreScenarios.ViaCoreAsync(kit, scenario, request);
        if (scenario.Anchor is { } anchorB && coreFault is null) await anchorB(kit);
        file.Append(CoreDump.Response(viaCore, coreFault, scenario.AllColumnsOrder));

        CoreDump.AssertGolden(message + ".txt", kit.Normalize(file.ToString()));
    }
}

/// <summary>
/// One message's scenario: how to build its request (with what it needs in the database), which core entry answers it, and the
/// <paramref name="Anchor"/> that reads back and names the ids of the rows the call created (run after a call that succeeded).
/// </summary>
internal sealed record CoreScenario(string Message, bool NonPipeline, Func<CoreScenarioKit, Task<OrganizationRequest>> Arrange,
    bool OwnDatabase = false, Func<CoreScenarioKit, Task>? Anchor = null, bool AllColumnsOrder = false);

/// <summary>The scenarios of the core response goldens and the request-mutation probe, and the two ways a request reaches the core.</summary>
internal static class CoreScenarios
{
    private const string Pipe = TestEntities.Pipe;
    private const string FileColumn = "new_doc";
    private const string TrackedTable = "new_crgtracked";
    private const string ElasticTable = "new_crgelastic";
    private const string OptionSetName = "new_crgoptions";
    private const string Contact = DuplicateDetectionTestKit.Contact;
    private const string DuplicateMail = "crg.duplicate@example.com";

    /// <summary>The solution job and export messages a plug-in may not run (<c>CoreOperations.CallerOnlyMessages</c>).</summary>
    private static readonly string[] CallerOnly =
        ["ImportSolutionAsync", "ExecuteAsync", "UninstallSolutionAsync", "StageAndUpgradeAsync", "DeleteAndPromoteAsync", "ExportSolution",
            "StageAndUpgrade", "DeleteAndPromote"];

    /// <summary>The metadata write messages (<c>MetadataOperations.WriteMessages</c>): refused from a plug-in's session.</summary>
    private static readonly string[] MetadataWrites =
        ["CreateEntity", "CreateAttribute", "CreateOneToMany", "CreateManyToMany", "CreateCustomerRelationships", "CreateOptionSet",
            "UpdateEntity", "UpdateAttribute", "UpdateOptionSet", "InsertOptionValue", "UpdateOptionValue", "DeleteEntity", "DeleteAttribute",
            "DeleteRelationship", "DeleteOptionSet", "PublishXml", "PublishAllXml", "AddSolutionComponent", "RemoveSolutionComponent"];

    public static IReadOnlyList<CoreScenario> All { get; } =
    [
        // ---- CRUD (the PipelineTests tables) ----
        Core("Create", async k =>
        {
            await k.EnsurePipeAsync();
            await k.ExecAsync($"DELETE FROM data.{Pipe} WHERE pb_name = 'crg-create'");
            return new CreateRequest { Target = new Entity(Pipe) { ["pb_name"] = "crg-create", ["pb_count"] = 3 } };
        }, k => k.NameAsync("created", $"SELECT pb_pipeid FROM data.{Pipe} WHERE pb_name = 'crg-create'")),
        Core("Retrieve", async k => new RetrieveRequest { Target = new EntityReference(Pipe, await k.PipeAsync("crg-retrieve")), ColumnSet = new ColumnSet("pb_name", "pb_count") }),
        Core("Update", async k => new UpdateRequest { Target = new Entity(Pipe, await k.PipeAsync("crg-update")) { ["pb_name"] = "crg-updated", ["pb_count"] = 4 } }),
        Core("Delete", async k => new DeleteRequest { Target = new EntityReference(Pipe, await k.PipeAsync("crg-delete")) }),
        Core("RetrieveMultiple", async k =>
        {
            await k.EnsurePipeAsync();
            await k.ExecAsync($"DELETE FROM data.{Pipe} WHERE pb_name LIKE 'crg-rm-%'");
            k.Name(await k.PipeAsync("crg-rm-a"), "rm-a");
            k.Name(await k.PipeAsync("crg-rm-b"), "rm-b");
            return new RetrieveMultipleRequest
            {
                Query = new QueryExpression(Pipe)
                {
                    ColumnSet = new ColumnSet("pb_name", "pb_count"),
                    Criteria = { Conditions = { new ConditionExpression("pb_name", ConditionOperator.BeginsWith, "crg-rm-") } },
                    Orders = { new OrderExpression("pb_name", OrderType.Ascending) },
                },
            };
        }),
        Core("Assign", async k =>
        {
            var id = await k.PipeAsync("crg-assign");
            var user = await k.UserAsync(("prvReadpb_Pipe", DepthMask.Global));
            return new AssignRequest { Target = new EntityReference(Pipe, id), Assignee = new EntityReference("systemuser", user) };
        }),
        Core("Associate", async k =>
        {
            var (project, tag) = await k.ProjectAndTagAsync();
            return new AssociateRequest
            {
                Target = project, Relationship = new Relationship(TestEntities.ProjectTag), RelatedEntities = [tag],
            };
        }),
        Core("Disassociate", async k =>
        {
            var (project, tag) = await k.ProjectAndTagAsync();
            await k.Admin.ExecuteAsync(new AssociateRequest { Target = project, Relationship = new Relationship(TestEntities.ProjectTag), RelatedEntities = [tag] });
            return new DisassociateRequest
            {
                Target = project, Relationship = new Relationship(TestEntities.ProjectTag), RelatedEntities = [tag],
            };
        }),

        // ---- Access (the SharingTests table) ----
        Core("GrantAccess", async k =>
        {
            var (project, user) = await k.ProjectAndUserAsync();
            return new GrantAccessRequest
            {
                Target = project, PrincipalAccess = new PrincipalAccess { Principal = user, AccessMask = AccessRights.ReadAccess | AccessRights.WriteAccess },
            };
        }),
        Core("ModifyAccess", async k =>
        {
            var (project, user) = await k.SharedProjectAsync();
            return new ModifyAccessRequest
            {
                Target = project, PrincipalAccess = new PrincipalAccess { Principal = user, AccessMask = AccessRights.ReadAccess },
            };
        }),
        Core("RevokeAccess", async k =>
        {
            var (project, user) = await k.SharedProjectAsync();
            return new RevokeAccessRequest { Target = project, Revokee = user };
        }),
        Core("AddPrivilegesRole", async k => new AddPrivilegesRoleRequest
        {
            RoleId = await k.RoleAsync(), Privileges = [new RolePrivilege { PrivilegeId = PrivilegeNames.IdOf("prvReadpb_Pipe"), Depth = PrivilegeDepth.Basic }],
        }),
        Core("ReplacePrivilegesRole", async k => new ReplacePrivilegesRoleRequest
        {
            RoleId = await k.RoleAsync(("prvWritepb_Pipe", DepthMask.Basic)),
            Privileges = [new RolePrivilege { PrivilegeId = PrivilegeNames.IdOf("prvReadpb_Pipe"), Depth = PrivilegeDepth.Local }],
        }),
        Core("RemovePrivilegeRole", async k => new RemovePrivilegeRoleRequest
        {
            RoleId = await k.RoleAsync(("prvReadpb_Pipe", DepthMask.Basic), ("prvWritepb_Pipe", DepthMask.Basic)),
            PrivilegeId = PrivilegeNames.IdOf("prvReadpb_Pipe"),
        }),

        // ---- Files (as FileUploadTests) ----
        Core("InitializeFileBlocksUpload", async k => new InitializeFileBlocksUploadRequest
        {
            Target = await k.FileRecordAsync(), FileAttributeName = FileColumn, FileName = "report.pdf",
        }),
        Core("UploadBlock", async k => new UploadBlockRequest
        {
            FileContinuationToken = await k.InitializeUploadAsync(await k.FileRecordAsync()), BlockId = CoreScenarioKit.BlockId, BlockData = CoreScenarioKit.FileBytes,
        }),
        Core("CommitFileBlocksUpload", async k =>
        {
            var record = await k.FileRecordAsync();
            k.Last["record"] = record.Id;
            var token = await k.InitializeUploadAsync(record);
            await k.Admin.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = CoreScenarioKit.BlockId, BlockData = CoreScenarioKit.FileBytes });
            return new CommitFileBlocksUploadRequest
            {
                FileContinuationToken = token, BlockList = [CoreScenarioKit.BlockId], FileName = "report.pdf", MimeType = "application/pdf",
            };
        }, k => k.NameAsync("file", $"SELECT new_doc FROM data.new_crgfile WHERE new_crgfileid = '{k.Last["record"]}'")),
        Core("DeleteFile", async k => new DeleteFileRequest { FileId = (await k.UploadedFileAsync()).FileId }),
        Core("InitializeFileBlocksDownload", async k => new InitializeFileBlocksDownloadRequest
        {
            Target = (await k.UploadedFileAsync()).Record, FileAttributeName = FileColumn,
        }),
        Core("DownloadBlock", async k =>
        {
            var record = (await k.UploadedFileAsync()).Record;
            var init = (InitializeFileBlocksDownloadResponse)await k.Admin.ExecuteAsync(new InitializeFileBlocksDownloadRequest { Target = record, FileAttributeName = FileColumn });
            return new DownloadBlockRequest { FileContinuationToken = init.FileContinuationToken, Offset = 0, BlockLength = CoreScenarioKit.FileBytes.Length };
        }),

        // ---- Record teams (as RecordTeamTests) ----
        Core("AddUserToRecordTeam", async k =>
        {
            var (record, user, template) = await k.RecordTeamAsync();
            k.Last["record"] = record.Id;
            return new AddUserToRecordTeamRequest { Record = record, SystemUserId = user, TeamTemplateId = template };
        }, k => k.NameAsync("accessteam", $"SELECT teamid FROM data.team WHERE regardingobjectid = '{k.Last["record"]}'")),
        Core("RemoveUserFromRecordTeam", async k =>
        {
            var (record, user, template) = await k.RecordTeamAsync();
            await k.Admin.ExecuteAsync(new AddUserToRecordTeamRequest { Record = record, SystemUserId = user, TeamTemplateId = template });
            await k.NameAsync("accessteam", $"SELECT teamid FROM data.team WHERE regardingobjectid = '{record.Id}'");
            return new RemoveUserFromRecordTeamRequest { Record = record, SystemUserId = user, TeamTemplateId = template };
        }),

        // ---- Change tracking (as RetrieveEntityChangesTests) ----
        Core("RetrieveEntityChanges", async k =>
        {
            await k.EnsureTrackedTableAsync();
            await k.ExecAsync($"DELETE FROM data.{TrackedTable}");
            k.Name(await k.Admin.CreateAsync(new Entity(TrackedTable) { ["new_name"] = "crg-tracked" }), "tracked");
            return new RetrieveEntityChangesRequest
            {
                EntityName = TrackedTable, Columns = new ColumnSet("new_name"), PageInfo = new PagingInfo { Count = 50, PageNumber = 1 },
            };
        }),

        // ---- Duplicates (as RetrieveDuplicatesTests, BulkDetectDuplicatesTests) ----
        Core("PublishDuplicateRule", async k =>
        {
            var rule = await (await k.DuplicatesAsync()).RuleAsync();
            k.Last["rule"] = rule;
            return new PublishDuplicateRuleRequest { DuplicateRuleId = rule };
        }, k => k.NameAsync("job", $"SELECT asyncoperationid FROM data.asyncoperation WHERE regardingobjectid = '{k.Last["rule"]}'")),
        Core("UnpublishDuplicateRule", async k =>
        {
            var duplicates = await k.DuplicatesAsync();
            var rule = await duplicates.RuleAsync();
            await duplicates.PublishAndRunAsync(rule);
            return new UnpublishDuplicateRuleRequest { DuplicateRuleId = rule };
        }),
        // The core reads the matches with all columns: their order is the cached table metadata's, which a metadata reload (by an
        // earlier test in the run) changes from definition order to name order. The dump writes them by name (see the README).
        Core("RetrieveDuplicates", async k =>
        {
            var duplicates = await k.DuplicatesAsync();
            await k.ExecAsync($"DELETE FROM data.{Contact} WHERE lastname = 'crg-duplicate'");
            k.Name(await k.Admin.CreateAsync(new Entity(Contact) { ["lastname"] = "crg-duplicate", [DuplicateDetectionTestKit.Email] = DuplicateMail }), "duplicate");
            await duplicates.PublishAndRunAsync(await duplicates.RuleAsync());
            return new RetrieveDuplicatesRequest
            {
                BusinessEntity = new Entity(Contact) { [DuplicateDetectionTestKit.Email] = DuplicateMail }, MatchingEntityName = Contact,
                PagingInfo = new PagingInfo { Count = 50, PageNumber = 1 },
            };
        }) with { AllColumnsOrder = true },
        Core("BulkDetectDuplicates", async k =>
        {
            var duplicates = await k.DuplicatesAsync();
            await duplicates.PublishAndRunAsync(await duplicates.RuleAsync());
            await k.ExecAsync("DELETE FROM data.asyncoperation WHERE name = 'crg bulk detection'");
            return new BulkDetectDuplicatesRequest
            {
                Query = new QueryExpression(Contact) { Criteria = { Conditions = { new ConditionExpression("lastname", ConditionOperator.Equal, "crg-duplicate") } } },
                JobName = "crg bulk detection", SendEmailNotification = false, ToRecipients = [], CCRecipients = [], TemplateId = Guid.Empty,
            };
        }, k => k.NameAsync("job", "SELECT asyncoperationid FROM data.asyncoperation WHERE name = 'crg bulk detection'")),

        // ---- Currency, email, queues ----
        Core("RetrieveExchangeRate", async k => new RetrieveExchangeRateRequest
        {
            TransactionCurrencyId = (Guid)(await k.ScalarAsync("SELECT basecurrencyid FROM data.organization"))!,
        }),
        Core("SendEmail", async k => new SendEmailRequest { EmailId = await k.DraftEmailAsync(), IssueSend = false, TrackingToken = "" }),
        Core("GetTrackingTokenEmail", async k =>
        {
            await k.TrackingTokensOnAsync();
            return new GetTrackingTokenEmailRequest { Subject = "crg subject" };
        }),
        Core("AddToQueue", async k =>
        {
            var email = await k.EmailAsync();
            k.Last["email"] = email;
            return new AddToQueueRequest { Target = new EntityReference("email", email), DestinationQueueId = await k.QueueAsync() };
        }, k => k.NameAsync("queueitem", $"SELECT queueitemid FROM data.queueitem WHERE objectid = '{k.Last["email"]}'")),
        Core("RemoveFromQueue", async k => new RemoveFromQueueRequest { QueueItemId = await k.QueueItemAsync() }),
        Core("RouteTo", async k =>
        {
            var item = await k.QueueItemAsync();
            return new RouteToRequest { Target = new EntityReference("queue", await k.QueueAsync()), QueueItemId = item };
        }),
        Core("AddPrincipalToQueue", async k => new AddPrincipalToQueueRequest
        {
            QueueId = await k.QueueAsync(), Principal = new Entity("systemuser", await k.UserAsync(("prvReadActivity", DepthMask.Global))),
        }),
        Core("PickFromQueue", async k => new PickFromQueueRequest { QueueItemId = await k.QueueItemAsync(), WorkerId = k.Caller.UserId, RemoveQueueItem = false }),
        Core("ReleaseToQueue", async k =>
        {
            var item = await k.QueueItemAsync();
            await k.Admin.ExecuteAsync(new PickFromQueueRequest { QueueItemId = item, WorkerId = k.Caller.UserId, RemoveQueueItem = false });
            return new ReleaseToQueueRequest { QueueItemId = item };
        }),
        Core("RetrieveUserQueues", async k => new RetrieveUserQueuesRequest { UserId = await k.UserAsync(), IncludePublic = false }),

        // ---- Elastic tables (as ElasticBulkTests, ElasticCosmosQueryTests) ----
        Core("Upsert", async k =>
        {
            await k.EnsureElasticTableAsync();
            await k.ExecAsync($"DELETE FROM data.{ElasticTable}");
            return new UpsertRequest { Target = new Entity(ElasticTable, Guid.NewGuid()) { ["new_name"] = "crg-elastic" } };
        }),
        Core("ExecuteCosmosSqlQuery", async k =>
        {
            await k.EnsureElasticTableAsync();
            await k.ExecAsync($"DELETE FROM data.{ElasticTable}");
            await k.Admin.CreateAsync(new Entity(ElasticTable) { ["new_name"] = "crg-elastic" });
            return new OrganizationRequest("ExecuteCosmosSqlQuery") { ["QueryText"] = "SELECT c.props.new_name AS n FROM c", ["EntityLogicalName"] = ElasticTable };
        }),

        // ---- Rollup (as RollupMessageTests, on a database of its own) ----
        new("CalculateRollupField", false, k => k.RollupAsync(), OwnDatabase: true),

        // ---- ExecuteWorkflow: its stage 30 is the workflows' hook; the core refuses it ----
        Core("ExecuteWorkflow", async k => new ExecuteWorkflowRequest { EntityId = await k.PipeAsync("crg-workflow"), WorkflowId = CoreScenarioKit.UnknownId }),

        // ---- Non-pipeline messages ----
        NonPipeline("WhoAmI", _ => Task.FromResult<OrganizationRequest>(new WhoAmIRequest())),
        NonPipeline("RetrieveUserPrivileges", async k => new RetrieveUserPrivilegesRequest { UserId = await k.UserAsync(("prvReadpb_Pipe", DepthMask.Basic)) }),
        NonPipeline("RetrieveAppComponents", async k => new RetrieveAppComponentsRequest { AppModuleId = await k.AppModuleAsync() }),
        NonPipeline("RetrieveUnpublished", async k => new RetrieveUnpublishedRequest
        {
            Target = new EntityReference("appmodule", await k.AppModuleAsync()), ColumnSet = new ColumnSet("name"),
        }),
        NonPipeline("InitializeFrom", async k => new InitializeFromRequest
        {
            EntityMoniker = new EntityReference("account", await k.Admin.CreateAsync(new Entity("account") { ["name"] = "crg account", ["telephone1"] = "555 0100" })),
            TargetEntityName = "contact", TargetFieldType = TargetFieldType.ValidForCreate,
        }),
        NonPipeline("RetrieveEntity", async k =>
        {
            await k.EnsureTrackedTableAsync();
            return new RetrieveEntityRequest { LogicalName = TrackedTable, EntityFilters = EntityFilters.Entity };
        }),
        NonPipeline("RetrieveAttribute", async k =>
        {
            await k.EnsureTrackedTableAsync();
            return new RetrieveAttributeRequest { EntityLogicalName = TrackedTable, LogicalName = "new_name" };
        }),
        NonPipeline("ImportSolution", _ => Task.FromResult(new OrganizationRequest("ImportSolution"))),
        NonPipeline("RetrieveFormattedImportJobResults", async k => new OrganizationRequest("RetrieveFormattedImportJobResults") { ["ImportJobId"] = await k.ImportJobAsync() }),
        NonPipeline("RetrievePrincipalAccess", async k =>
        {
            var (project, user) = await k.SharedProjectAsync();
            return new RetrievePrincipalAccessRequest { Target = project, Principal = user };
        }),
        NonPipeline("RetrieveSharedPrincipalsAndAccess", async k => new RetrieveSharedPrincipalsAndAccessRequest { Target = (await k.SharedProjectAsync()).Project }),
        .. CallerOnly.Select(m => NonPipeline(m, _ => Task.FromResult(new OrganizationRequest(m)))),
        .. MetadataWrites.Select(m => NonPipeline(m, _ => Task.FromResult(new OrganizationRequest(m)))),
        NonPipeline("RetrieveRelationship", async k =>
        {
            await TestEntities.EnsureProjectTagAsync(k.Store, k.ConnectionString);
            return new RetrieveRelationshipRequest { Name = TestEntities.ProjectTag };
        }),
        NonPipeline("RetrieveOptionSet", async k =>
        {
            await k.EnsureOptionSetAsync();
            return new RetrieveOptionSetRequest { Name = OptionSetName };
        }),
        // On new_crgaudited (auditing on, only this kit uses it), with the organization's auditing on: a row created, then updated once.
        NonPipeline("RetrieveAuditDetails", async k => new RetrieveAuditDetailsRequest { AuditId = (await k.AuditedRowAsync()).UpdateAudit }),
        NonPipeline("RetrieveRecordChangeHistory", async k => new RetrieveRecordChangeHistoryRequest { Target = (await k.AuditedRowAsync()).Row }),
        NonPipeline("RetrieveAttributeChangeHistory", async k => new RetrieveAttributeChangeHistoryRequest
        {
            Target = (await k.AuditedRowAsync()).Row, AttributeLogicalName = "new_name",
        }),
        NonPipeline("DeleteRecordChangeHistory", async k => new DeleteRecordChangeHistoryRequest { Target = (await k.AuditedRowAsync()).Row }),
        NonPipeline("DeleteAuditData", _ => Task.FromResult<OrganizationRequest>(new DeleteAuditDataRequest { EndDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) })),
    ];

    private static CoreScenario Core(string message, Func<CoreScenarioKit, Task<OrganizationRequest>> arrange,
        Func<CoreScenarioKit, Task>? anchor = null) => new(message, false, arrange, Anchor: anchor);

    private static CoreScenario NonPipeline(string message, Func<CoreScenarioKit, Task<OrganizationRequest>> arrange) => new(message, true, arrange);

    /// <summary>(a): the request through the pipeline, as the administrator at depth 1 with no session (the pipeline opens one).</summary>
    public static async Task<(OrganizationResponse? Response, Exception? Fault)> ViaPipelineAsync(CoreScenarioKit kit, OrganizationRequest request)
    {
        await using var scope = kit.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<PipelineExecutor>();
        try
        {
            return (await executor.ExecuteAsync(request, new InvocationContext(kit.Caller, kit.Caller.UserId, 1, null, null, Guid.NewGuid()), default), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
        finally
        {
            kit.Cache.Invalidate();
        }
    }

    /// <summary>
    /// (b): the request through the core's interface directly, as the administrator, in a transaction of its own that is committed when
    /// the call succeeds (as the direct callers do). A core message gets an <see cref="SdkNestedRequest"/> that runs a request the core
    /// issues through the pipeline in that transaction, one level deeper (as the pipeline's own delegate does, without a parent context).
    /// </summary>
    public static async Task<(OrganizationResponse? Response, Exception? Fault)> ViaCoreAsync(CoreScenarioKit kit, CoreScenario scenario,
        OrganizationRequest request)
    {
        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<IPipelineCore>();
        var executor = scope.ServiceProvider.GetRequiredService<PipelineExecutor>();
        await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        var session = new DataSession(conn, tx);
        try
        {
            var response = scenario.NonPipeline
                ? await core.ExecuteNonPipelineAsync(request, kit.Caller, session, default)
                : await core.ExecuteCoreAsync(request, kit.Caller, session,
                    (nested, ct) => executor.ExecuteAsync(nested,
                        new InvocationContext(kit.Caller, kit.Caller.UserId, 2, null, session, Guid.NewGuid()) { IsCoreCascade = CascadeHint.IsMarked(nested) },
                        ct), default);
            await tx.CommitAsync();
            return (response, null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return (null, ex);
        }
        finally
        {
            kit.Cache.Invalidate();
        }
    }
}

/// <summary>
/// What a scenario needs: the services and database it runs on (the shared fixture's, or a database of its own), the administrator as
/// caller, the ids the goldens name by role, and the tables and rows the scenarios arrange. Fixed names only, so that nothing volatile
/// reaches a golden; what a scenario turns on is turned off again on dispose.
/// </summary>
internal sealed class CoreScenarioKit : IAsyncDisposable
{
    public static readonly Guid UnknownId = new("0c0e0c0e-0000-4000-8000-00000000c0de");
    public const string BlockId = "YmxvY2stMDAwMDAw"; // base64 of "block-000000"
    public static readonly byte[] FileBytes = [.. Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3))];

    private static readonly SemaphoreSlim Lock = new(1, 1);

    private readonly List<Func<Task>> _cleanups = [];
    private readonly List<(Guid Id, string Name)> _known = [];
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? _ownApp;
    private DuplicateDetectionTestKit? _duplicates;

    private CoreScenarioKit(XrmdFixture fx, IServiceProvider services, string connectionString)
    {
        Fx = fx;
        Services = services;
        ConnectionString = connectionString;
    }

    public XrmdFixture Fx { get; }

    public IServiceProvider Services { get; }

    public string ConnectionString { get; }

    public Caller Caller { get; private set; } = null!;

    /// <summary>The section being arranged and run (<c>a</c> or <c>b</c>): the prefix of the ids <see cref="Name"/> names.</summary>
    public string Section { get; set; } = "a";

    /// <summary>Ids an arranger keeps for its anchor (the record whose created row the anchor reads back).</summary>
    public Dictionary<string, Guid> Last { get; } = [];

    public IMetadataStore Store => Services.GetRequiredService<IMetadataStore>();

    public SecurityCache Cache => Services.GetRequiredService<SecurityCache>();

    public OrganizationService Admin => TestServices.NewOrganizationService(Services, Fx.AdminObjectId);

    /// <summary>A kit on the shared database, or on a database of its own (<paramref name="ownDatabase"/>).</summary>
    public static async Task<CoreScenarioKit> StartAsync(XrmdFixture fx, bool ownDatabase)
    {
        CoreScenarioKit kit;
        if (ownDatabase)
        {
            var app = fx.WithOwnDatabase(_ => { });
            kit = new CoreScenarioKit(fx, app.Services, app.Services.GetRequiredService<IConfiguration>().GetConnectionString("Org")!) { _ownApp = app };
        }
        else
        {
            kit = new CoreScenarioKit(fx, fx.App.Services, fx.ConnectionString);
            await PluginTestHelper.ClearAsync(fx);
        }

        await kit.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        kit.Caller = await TestServices.CallerAsync(kit.Services, fx.AdminObjectId);
        kit._known.Add((kit.Caller.UserId, "{user}"));
        kit._known.Add((kit.Caller.BusinessUnitId, "{businessunit}"));
        kit._known.Add(((Guid)(await kit.ScalarAsync("SELECT organizationid FROM data.organization"))!, "{organization}"));
        kit._known.Add((fx.AdminObjectId, "{useraad}"));
        kit._known.Add((PrivilegeNames.IdOf("prvReadpb_Pipe"), "{prvReadpb_Pipe}"));
        return kit;
    }

    public async ValueTask DisposeAsync()
    {
        for (var i = _cleanups.Count - 1; i >= 0; i--) await _cleanups[i]();
        if (_duplicates is not null)
        {
            await _duplicates.DisposeAsync();
            await ExecAsync("DELETE FROM data.asyncoperation WHERE statecode = 0");
        }
        Cache.Invalidate();
        if (_ownApp is not null) await _ownApp.DisposeAsync();
        else await PluginTestHelper.ClearAsync(Fx);
    }

    // ---- normalisation ----------------------------------------------------------------------------------------------

    private static readonly Regex GuidPattern = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    /// <summary>A formatted date (the <c>FormattedValues</c> of a date column, <c>11/25/2026 9:17 PM</c>).</summary>
    private static readonly Regex FormattedDatePattern = new(@"\b\d{1,2}/\d{1,2}/\d{4}( \d{1,2}:\d{2} [AP]M)?");

    /// <summary>
    /// <paramref name="text"/> with the known ids named by role and every other id numbered <c>{id1}</c>, <c>{id2}</c>... in first-seen
    /// order over the whole file (so an id that differs between (a) and (b) shows), <c>{empty}</c> for the empty id; formatted dates
    /// are <c>{date}</c>.
    /// </summary>
    /// <remarks><paramref name="keepFixedDates"/> (S.2e3b) keeps a formatted date before <see cref="CoreDump.FixedDateYear"/> (a fixed value
    /// a scenario wrote).</remarks>
    public string Normalize(string text, bool keepFixedDates = false)
    {
        text = FormattedDatePattern.Replace(text, m =>
            keepFixedDates && int.Parse(m.Value.Split(' ')[0].Split('/')[2], CultureInfo.InvariantCulture) < CoreDump.FixedDateYear ? m.Value : "{date}");
        var ids = new Dictionary<Guid, string>();
        foreach (var (id, name) in _known) ids.TryAdd(id, name);
        var numbered = 0;
        return GuidPattern.Replace(text, m =>
        {
            var id = Guid.Parse(m.Value);
            if (id == Guid.Empty) return "{empty}";
            if (id == UnknownId) return "{unknown}";
            if (!ids.TryGetValue(id, out var n)) ids[id] = n = "{id" + ++numbered + "}";
            return n;
        });
    }

    /// <summary>
    /// <paramref name="text"/> with each id named so far written by its name and every other id as <c>?</c>: a key that orders rows the
    /// same way on every run (S.2e3b), whatever ids the database gave them.
    /// </summary>
    public string Mask(string text)
    {
        var ids = new Dictionary<Guid, string>();
        foreach (var (id, name) in _known) ids.TryAdd(id, name);
        return GuidPattern.Replace(text, m => ids.TryGetValue(Guid.Parse(m.Value), out var n) ? n : "?");
    }

    // ---- data -------------------------------------------------------------------------------------------------------

    public void OnCleanup(Func<Task> cleanup) => _cleanups.Add(cleanup);

    /// <summary>Names <paramref name="id"/> <c>{section:label}</c> in the golden (an id named once keeps its first name).</summary>
    public void Name(Guid id, string label) => _known.Add((id, "{" + Section + ":" + label + "}"));

    /// <summary>Names the single id <paramref name="sql"/> reads (the row a call created); fails when it reads none or several.</summary>
    public async Task<Guid> NameAsync(string label, string sql)
    {
        var ids = new List<Guid>();
        await using (var conn = await TestProvider.OpenAsync(ConnectionString))
        await using (var cmd = TestSql.Command(conn, sql))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) ids.Add(rd.GetGuid(0));
        ids.Should().ContainSingle($"the anchor '{label}' reads one row ({sql})");
        Name(ids[0], label);
        return ids[0];
    }

    public Task ExecAsync(string sql) => TestSql.ExecAsync(ConnectionString, sql);

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    public Task EnsurePipeAsync() => TestEntities.EnsurePipeAsync(Store);

    public async Task<Guid> PipeAsync(string name)
    {
        await EnsurePipeAsync();
        return await Admin.CreateAsync(new Entity(TestEntities.Pipe) { ["pb_name"] = name, ["pb_count"] = 3 });
    }

    /// <summary>A user in the root business unit named <c>crg user</c>, with <paramref name="privileges"/> when given.</summary>
    public async Task<Guid> UserAsync(params (string Privilege, DepthMask Depth)[] privileges)
    {
        var root = await SecurityTestHelper.RootBusinessUnitAsync(ConnectionString);
        var id = await SecurityTestHelper.CreateUserAsync(ConnectionString, Cache, root, userName: "crg user");
        if (privileges.Length > 0)
            await SecurityTestHelper.AssignRoleAsync(ConnectionString, Cache, id,
                await SecurityTestHelper.CreateRoleAsync(ConnectionString, Cache, "crg grant", privileges));
        return id;
    }

    /// <summary>A role named <c>crg role</c> in the root business unit with <paramref name="privileges"/>.</summary>
    public Task<Guid> RoleAsync(params (string Privilege, DepthMask Depth)[] privileges) =>
        SecurityTestHelper.CreateRoleAsync(ConnectionString, Cache, "crg role", privileges);

    public async Task<(EntityReference Project, EntityReference Tag)> ProjectAndTagAsync()
    {
        await TestEntities.EnsureProjectTagAsync(Store, ConnectionString);
        var project = await Admin.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = "crg project" });
        var tag = await Admin.CreateAsync(new Entity(TestEntities.Tag) { ["new_name"] = "crg tag" });
        return (new EntityReference(TestEntities.Project, project), new EntityReference(TestEntities.Tag, tag));
    }

    /// <summary>A project of the administrator and a user who may read projects (a share demands it), named <c>{section:principal}</c>.</summary>
    public async Task<(EntityReference Project, EntityReference User)> ProjectAndUserAsync()
    {
        await TestEntities.EnsureProjectAsync(Store);
        var project = await Admin.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = "crg project" });
        var user = await UserAsync(("prvReadpb_Project", DepthMask.Global));
        Name(user, "principal");
        return (new EntityReference(TestEntities.Project, project), new EntityReference("systemuser", user));
    }

    /// <summary><see cref="ProjectAndUserAsync"/>, the project shared with the user for read and write.</summary>
    public async Task<(EntityReference Project, EntityReference User)> SharedProjectAsync()
    {
        var (project, user) = await ProjectAndUserAsync();
        await Admin.ExecuteAsync(new GrantAccessRequest
        {
            Target = project, PrincipalAccess = new PrincipalAccess { Principal = user, AccessMask = AccessRights.ReadAccess | AccessRights.WriteAccess },
        });
        return (project, user);
    }

    // ---- files ----

    /// <summary>A row of <c>new_crgfile</c> (a user-owned table with the file column <c>new_doc</c>, created once).</summary>
    public async Task<EntityReference> FileRecordAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (Store.GetEntity("new_crgfile") is null)
            {
                await Admin.ExecuteAsync(new CreateEntityRequest
                {
                    Entity = TestHelpers.Entity("new_crgfile", "new_crgfile", "Core golden files"),
                    PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = TestHelpers.Label("Name") },
                });
                await Admin.ExecuteAsync(new CreateAttributeRequest
                {
                    EntityName = "new_crgfile",
                    Attribute = new FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_doc", DisplayName = TestHelpers.Label("Document") },
                });
            }
        }
        finally
        {
            Lock.Release();
        }

        return new EntityReference("new_crgfile", await Admin.CreateAsync(new Entity("new_crgfile") { ["new_name"] = "crg file" }));
    }

    public async Task<string> InitializeUploadAsync(EntityReference record) =>
        ((InitializeFileBlocksUploadResponse)await Admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = record, FileAttributeName = "new_doc", FileName = "report.pdf",
        })).FileContinuationToken;

    /// <summary>A row of <c>new_crgfile</c> with <see cref="FileBytes"/> uploaded into <c>new_doc</c>.</summary>
    public async Task<(EntityReference Record, Guid FileId)> UploadedFileAsync()
    {
        var record = await FileRecordAsync();
        var token = await InitializeUploadAsync(record);
        await Admin.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = BlockId, BlockData = FileBytes });
        var commit = (CommitFileBlocksUploadResponse)await Admin.ExecuteAsync(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = [BlockId], FileName = "report.pdf", MimeType = "application/pdf",
        });
        return (record, commit.FileId);
    }

    // ---- record teams ----

    /// <summary>
    /// An account of the administrator, a user who may read accounts and a team template on account (read), with access teams turned on
    /// for account until the kit is disposed.
    /// </summary>
    public async Task<(EntityReference Record, Guid User, Guid Template)> RecordTeamAsync()
    {
        if (Store.GetEntity("account")!.AutoCreateAccessTeams != true)
        {
            await SetAccessTeamsAsync(true);
            OnCleanup(() => SetAccessTeamsAsync(false));
        }
        var template = await Admin.CreateAsync(new Entity("teamtemplate")
        {
            ["teamtemplatename"] = "crg template " + Guid.NewGuid().ToString("N")[..8],
            ["objecttypecode"] = Store.GetEntity("account")!.ObjectTypeCode!.Value,
            ["defaultaccessrightsmask"] = (int)AccessRights.ReadAccess,
        });
        OnCleanup(() => Admin.DeleteAsync("teamtemplate", template));
        var record = await Admin.CreateAsync(new Entity("account") { ["name"] = "crg team account" });
        var user = await UserAsync(("prvReadAccount", DepthMask.Global));
        return (new EntityReference("account", record), user, template);
    }

    private Task SetAccessTeamsAsync(bool value) =>
        Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = "account", AutoCreateAccessTeams = value } });

    // ---- change tracking, elastic, option set ----

    /// <summary><c>new_crgtracked</c>: a user-owned table with change tracking on, created once.</summary>
    public async Task EnsureTrackedTableAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (Store.GetEntity("new_crgtracked") is not null) return;
            await Admin.ExecuteAsync(new CreateEntityRequest
            {
                Entity = TestHelpers.Entity("new_crgtracked", "new_crgtracked", "Core golden tracked"),
                PrimaryAttribute = new StringAttributeMetadata
                {
                    LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = TestHelpers.Label("Name"),
                    RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
                },
            });
            await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = "new_crgtracked", ChangeTrackingEnabled = true } });
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// A row of <c>new_crgaudited</c> (a user-owned table with auditing on, created once; no other test uses it), created and then
    /// updated once with the organization's auditing on (turned off again on dispose): its two audit rows are named
    /// <c>{section:audit-create}</c> and <c>{section:audit-update}</c>.
    /// </summary>
    public async Task<(EntityReference Row, Guid UpdateAudit)> AuditedRowAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (Store.GetEntity("new_crgaudited") is null)
            {
                await Admin.ExecuteAsync(new CreateEntityRequest
                {
                    Entity = TestHelpers.Entity("new_crgaudited", "new_crgaudited", "Core golden audited"),
                    PrimaryAttribute = new StringAttributeMetadata
                    {
                        LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = TestHelpers.Label("Name"),
                        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
                    },
                });
                await Admin.ExecuteAsync(new UpdateEntityRequest
                {
                    Entity = new EntityMetadata { LogicalName = "new_crgaudited", IsAuditEnabled = new BooleanManagedProperty(true) },
                });
            }
        }
        finally
        {
            Lock.Release();
        }

        var organization = (Guid)(await ScalarAsync("SELECT organizationid FROM data.organization"))!;
        await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = true });
        OnCleanup(() => Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = false }));
        var id = await Admin.CreateAsync(new Entity("new_crgaudited") { ["new_name"] = "crg-audited" });
        Name(id, "audited");
        await Admin.UpdateAsync(new Entity("new_crgaudited", id) { ["new_name"] = "crg-audited-changed" });
        await NameAsync("audit-create", $"SELECT auditid FROM data.audit WHERE objectid = '{id}' AND action = 1");
        var update = await NameAsync("audit-update", $"SELECT auditid FROM data.audit WHERE objectid = '{id}' AND action = 2");
        return (new EntityReference("new_crgaudited", id), update);
    }

    /// <summary>An <c>importjob</c> with a one-result log in <c>data</c>, written through the core as the solution import writes it.</summary>
    public async Task<Guid> ImportJobAsync()
    {
        var id = Guid.NewGuid();
        const string log = "<importexportxml><entities><entity LocalizedName=\"crg table\" id=\"new_crgtable\">" +
                           "<result result=\"success\" errorcode=\"0\" errortext=\"\" datetime=\"12:00:00\" /></entity></entities></importexportxml>";
        await using var scope = Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<IPipelineCore>();
        await using var conn = await TestProvider.OpenAsync(ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        await core.ExecuteCoreAsync(new CreateRequest
        {
            Target = new Entity("importjob", id) { ["name"] = "crg import", ["solutionname"] = "crg", ["progress"] = 100d, ["data"] = log },
        }, Caller.Internal, new DataSession(conn, tx), default);
        await tx.CommitAsync();
        Name(id, "importjob");
        return id;
    }

    /// <summary><c>new_crgelastic</c>: an elastic user-owned table, created once.</summary>
    public async Task EnsureElasticTableAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (Store.GetEntity("new_crgelastic") is not null) return;
            var entity = TestHelpers.Entity("new_crgelastic", "new_crgelastic", "Core golden elastic");
            entity.TableType = ElasticTables.TableTypeElastic;
            await Admin.ExecuteAsync(new CreateEntityRequest
            {
                Entity = entity,
                PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = TestHelpers.Label("Name") },
            });
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>The global option set <c>new_crgoptions</c> (1, 2), created once.</summary>
    public async Task EnsureOptionSetAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (Store.GetOptionSet("new_crgoptions") is not null) return;
            await Admin.ExecuteAsync(new CreateOptionSetRequest
            {
                OptionSet = new OptionSetMetadata(new OptionMetadataCollection
                {
                    new OptionMetadata(TestHelpers.Label("One"), 1), new OptionMetadata(TestHelpers.Label("Two"), 2),
                })
                {
                    Name = "new_crgoptions", DisplayName = TestHelpers.Label("Core golden options"), IsGlobal = true, OptionSetType = OptionSetType.Picklist,
                },
            });
        }
        finally
        {
            Lock.Release();
        }
    }

    // ---- duplicates ----

    /// <summary>The duplicate detection kit on contact (the organization switch on), disposed with this kit.</summary>
    public async Task<DuplicateDetectionTestKit> DuplicatesAsync()
    {
        if (_duplicates is not null) return _duplicates;
        _duplicates = new DuplicateDetectionTestKit(Fx);
        await _duplicates.InitializeAsync(onlineCreateUpdate: false, DuplicateDetectionTestKit.Contact);
        return _duplicates;
    }

    // ---- email and queues ----

    /// <summary>Tracking tokens on (<c>CRM:</c>, four digits, the next number 7, as SendEmailTests) until the kit is disposed.</summary>
    public async Task TrackingTokensOnAsync()
    {
        await ExecAsync("UPDATE data.organization SET trackingprefix = 'CRM:', trackingtokeniddigits = 4, nexttrackingnumber = 7");
        OnCleanup(() => ExecAsync("UPDATE data.organization SET trackingprefix = NULL, trackingtokeniddigits = NULL, nexttrackingnumber = NULL"));
    }

    public Task<Guid> EmailAsync() => Admin.CreateAsync(new Entity("email") { ["subject"] = "crg email" });

    /// <summary>A draft email to a contact.</summary>
    public async Task<Guid> DraftEmailAsync()
    {
        var contact = await Admin.CreateAsync(new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = "crg-mail", ["emailaddress1"] = "ada.crg@example.com" });
        return await Admin.CreateAsync(new Entity("email")
        {
            ["subject"] = "crg email",
            ["to"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("contact", contact) }]),
        });
    }

    public Task<Guid> QueueAsync() => Admin.CreateAsync(new Entity("queue")
    {
        ["name"] = "crg queue", ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1),
    });

    /// <summary>An email added to a new queue; returns the queue item.</summary>
    public async Task<Guid> QueueItemAsync()
    {
        var email = await EmailAsync();
        return (Guid)(await Admin.ExecuteAsync(new AddToQueueRequest { Target = new EntityReference("email", email), DestinationQueueId = await QueueAsync() }))
            .Results["QueueItemId"];
    }

    public Task<Guid> AppModuleAsync() => Admin.CreateAsync(new Entity("appmodule")
    {
        ["name"] = "crg app", ["uniquename"] = "new_crg" + Guid.NewGuid().ToString("N")[..8],
    });

    // ---- rollup (own database) ----

    /// <summary>
    /// As RollupMessageTests, on this kit's own database: account gets the rollup <c>xrmv_ordertotal</c> (the sum of
    /// <c>xrmv_calcorder.xrmv_amount</c> of status 2) once; an account with two orders of status 2 and one of status 1.
    /// </summary>
    public async Task<OrganizationRequest> RollupAsync()
    {
        const string order = "xrmv_calcorder";
        var store = (MetadataStore)Store;
        if (store.GetEntity(order) is null)
        {
            await store.CreateEntityAsync(new EntityMetadata
            {
                LogicalName = order, SchemaName = order, OwnershipType = OwnershipTypes.UserOwned, DisplayName = SystemAttributes.CreateLabel(order, 1033),
            }, new StringAttributeMetadata { LogicalName = "xrmv_name", SchemaName = "xrmv_name", MaxLength = 100 });
            await store.AddAttributeAsync(order, Named(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_amount"));
            await store.AddAttributeAsync(order, Named(new PicklistAttributeMetadata
            {
                OptionSet = new OptionSetMetadata(new OptionMetadataCollection(
                    new[] { 1, 2, 3 }.Select(v => new OptionMetadata(SystemAttributes.CreateLabel("Option " + v, 1033), v)).ToList()))
                {
                    IsGlobal = false, OptionSetType = OptionSetType.Picklist,
                },
            }, "xrmv_status"));
            await TestEntities.InSessionAsync(ConnectionString, s => store.CreateOneToManyAsync(s, new OneToManyRelationshipMetadata
            {
                SchemaName = "xrmv_calcorder_AccountId_account", ReferencedEntity = "account", ReferencingEntity = order, ReferencedAttribute = "accountid",
            }, Named(new LookupAttributeMetadata(), "xrmv_accountid"), CancellationToken.None));
            var total = Named(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_ordertotal");
            total.SourceType = RollupColumns.Rollup;
            ComputedColumnRules.SetFormula(total, File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "rollup", "real", "account-xrmv_ordertotal.xaml")));
            await store.AddAttributeAsync("account", total);
            await store.RollupJobNotifications;
        }

        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "crg rollup" });
        foreach (var (amount, status) in new[] { (100m, 2), (25.5m, 2), (7m, 1) })
            await Admin.CreateAsync(new Entity(order)
            {
                ["xrmv_name"] = "order", ["xrmv_accountid"] = new EntityReference("account", account), ["xrmv_amount"] = amount,
                ["xrmv_status"] = new OptionSetValue(status),
            });
        return new CalculateRollupFieldRequest { Target = new EntityReference("account", account), FieldName = "xrmv_ordertotal" };
    }

    private static T Named<T>(T a, string name) where T : AttributeMetadata
    {
        a.LogicalName = name;
        a.SchemaName = name;
        a.DisplayName = SystemAttributes.CreateLabel(name, 1033);
        return a;
    }
}

/// <summary>
/// The canonical dump of the response goldens and the request-mutation probe: one entry per value with its path from the root, so a
/// dump renders as indented lines (labels in ordinal order, values as <c>Type:value</c>) and two dumps compare path by path. The known
/// SDK types are written by explicit writers that name their public data members; reflection over the public properties is the
/// fallback. In golden mode dates are <c>{date}</c>, row versions <c>{rowversion}</c>, custom object type codes <c>{otc}</c>; in probe
/// mode values are raw and each reference object carries an identity token (<c>@n</c>, the same instance the same token).
/// </summary>
internal sealed class CoreDump(CoreDump.Tokens? tokens = null, bool byName = false, bool fixedDates = false)
{
    private readonly List<(string Path, int Depth, string Line)> _entries = [];
    private readonly HashSet<object> _visiting = new(ReferenceEqualityComparer.Instance);
    private bool Probe => tokens is not null;

    public IReadOnlyList<(string Path, int Depth, string Line)> Entries => _entries;

    /// <summary>The golden text of a response (type, name and results) or of the fault that replaced it.</summary>
    /// <remarks>
    /// <paramref name="byName"/> writes an entity's <c>Attributes</c> and <c>FormattedValues</c> by name, for a message whose core reads
    /// all columns (their order then depends on the state of the metadata cache, not on the message).
    /// <para><paramref name="fixedDates"/> (S.2e3b) writes a date before 2024 (a fixed value a scenario wrote) as it is; later ones stay
    /// <c>{date:Kind}</c>.</para>
    /// </remarks>
    public static string Response(OrganizationResponse? response, Exception? fault, bool byName = false, bool fixedDates = false)
    {
        var dump = new CoreDump(byName: byName, fixedDates: fixedDates);
        if (fault is not null)
        {
            var inner = fault is AggregateException { InnerException: { } e } ? e : fault;
            dump.Add("Fault", 0, "Fault: " + inner.GetType().Name + (inner is XrmdFaultException x
                ? string.Create(CultureInfo.InvariantCulture, $" 0x{x.ErrorCode:X8} {x.HttpStatus}")
                : "") + " " + Quote(inner.Message));
        }
        else
        {
            dump.Add("Type", 0, "Type: " + response!.GetType().FullName);
            dump.Value("", "ResponseName", response.ResponseName, 0);
            dump.Value("", "Results", response.Results, 0);
        }
        return dump.Render();
    }

    /// <summary>A dump of a request for the mutation probe: its name, id and parameters, with identity tokens from <paramref name="tokens"/>.</summary>
    public static CoreDump Request(OrganizationRequest request, Tokens tokens)
    {
        var dump = new CoreDump(tokens);
        dump.Value("", "Request", request, 0);
        return dump;
    }

    /// <summary>
    /// The golden text of any value (S.2e3b: a query result read through <c>QueryExecutor</c>), with the writers of a response;
    /// <paramref name="byName"/> and <paramref name="fixedDates"/> as in <see cref="Response"/>.
    /// </summary>
    public static string ValueText(string label, object? value, bool byName = false, bool fixedDates = false)
    {
        var dump = new CoreDump(byName: byName, fixedDates: fixedDates);
        dump.Value("", label, value, 0);
        return dump.Render();
    }

    /// <summary>The golden text of a request (its name, id and parameters), written at the head of a section.</summary>
    public static string RequestText(OrganizationRequest request, bool fixedDates = false)
    {
        var dump = new CoreDump(fixedDates: fixedDates);
        dump.Value("", "Request", request, 0);
        return dump.Render();
    }

    public string Render()
    {
        var text = new StringBuilder();
        foreach (var (_, depth, line) in _entries) text.Append(' ', depth * 2).Append(line).Append('\n');
        return text.ToString();
    }

    private void Add(string path, int depth, string line) => _entries.Add((path, depth, line));

    /// <summary>The identity tokens of one probe: the same instance gets the same token in every dump made with them.</summary>
    public sealed class Tokens
    {
        private readonly ConditionalWeakTable<object, string> _table = new();
        private int _count;

        public string Of(object o)
        {
            lock (_table) return _table.GetValue(o, _ => (++_count).ToString(CultureInfo.InvariantCulture));
        }
    }

    private string Token(object o) => tokens is null ? "" : " @" + tokens.Of(o);

    /// <summary>The results whose text changes with every call (a version, a session or a continuation token): written as <c>{label}</c>.</summary>
    private static readonly HashSet<string> VolatileStrings = new(StringComparer.Ordinal) { "DataToken", "FileContinuationToken", "x-ms-session-token" };

    /// <summary>A date before this year is a fixed value a scenario wrote, not the time of a call (S.2e3b, <c>fixedDates</c>).</summary>
    public const int FixedDateYear = 2024;

    public static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

    private string? Scalar(string label, object v) => v switch
    {
        string s when Probe || !VolatileStrings.Contains(label) => Quote(s),
        string => "{" + label + "}",
        Guid g => g.ToString("D"),
        DateTime d when fixedDates && d.Year < FixedDateYear => d.ToString("O", CultureInfo.InvariantCulture),
        DateTime d => Probe ? d.ToString("O", CultureInfo.InvariantCulture) : "{date:" + d.Kind + "}",
        DateTimeOffset d => Probe ? d.ToString("O", CultureInfo.InvariantCulture) : "{date:" + d.Offset.ToString("c", CultureInfo.InvariantCulture) + "}",
        bool b => b ? "True" : "False",
        Enum e => e.ToString(),
        byte[] bytes when bytes.Length <= 64 => Convert.ToHexString(bytes),
        byte[] bytes => string.Create(CultureInfo.InvariantCulture, $"[{bytes.Length}] sha256:{Convert.ToHexString(SHA256.HashData(bytes))}"),
        int i when !Probe && label == "ObjectTypeCode" && i >= 10000 => "{otc}",
        long when !Probe && label == "versionnumber" => "{rowversion}",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        Type t => t.Name,
        _ => null,
    };

    private static string TypeName(Type t)
    {
        var i = t.Name.IndexOf('`');
        return i < 0 ? t.Name : t.Name[..i];
    }

    private static string KeyText(object? key) => key switch
    {
        string s => s,
        Relationship r => "Relationship:" + r.SchemaName + ":" + r.PrimaryEntityRole,
        _ => Convert.ToString(key, CultureInfo.InvariantCulture) ?? "null",
    };

    private void Value(string parent, string label, object? v, int depth)
    {
        var path = parent.Length == 0 ? label : parent + "/" + label;
        if (v is null) { Add(path, depth, label + ": null"); return; }
        if (depth > 24) { Add(path, depth, label + ": <depth limit>"); return; }
        if (Scalar(label, v) is { } scalar) { Add(path, depth, label + ": " + v.GetType().Name + ":" + scalar); return; }
        if (!_visiting.Add(v)) { Add(path, depth, label + ": <cycle>" + Token(v)); return; }
        try
        {
            Composite(path, label, v, depth);
        }
        finally
        {
            _visiting.Remove(v);
        }
    }

    /// <summary>A bag written in its own enumeration order (the attributes of an entity), or by key when <paramref name="sorted"/>.</summary>
    private void InOrder<TValue>(string parent, string label, IEnumerable<KeyValuePair<string, TValue>> bag, int depth, bool sorted = false)
    {
        var path = parent + "/" + label;
        var items = sorted ? bag.OrderBy(p => p.Key, StringComparer.Ordinal).ToList() : bag.ToList();
        Add(path, depth, label + ": " + TypeName(bag.GetType()) + "[" + items.Count + "]" + Token(bag));
        foreach (var (key, value) in items) Value(path, key, value, depth + 1);
    }

    private void Composite(string path, string label, object v, int depth)
    {
        var d = depth + 1;
        switch (v)
        {
            case OrganizationRequest request:
                Add(path, depth, label + ": " + TypeName(v.GetType()) + Token(v));
                Value(path, "RequestName", request.RequestName, d);
                Value(path, "RequestId", request.RequestId, d);
                Value(path, "Parameters", request.Parameters, d);
                return;
            case Entity entity:
                Add(path, depth, label + ": " + TypeName(v.GetType()) + "(" + entity.LogicalName + ")" + Token(v));
                Value(path, "Id", entity.Id, d);
                Value(path, "EntityState", entity.EntityState, d);
                Add(path + "/RowVersion", d, "RowVersion: " + (entity.RowVersion is null ? "null" : Probe ? entity.RowVersion : "{rowversion}"));
                // In collection order (the Web API writes the attributes in that order): a reordering fails.
                InOrder(path, "Attributes", entity.Attributes, d, byName);
                InOrder(path, "FormattedValues", entity.FormattedValues, d, byName);
                InOrder(path, "KeyAttributes", entity.KeyAttributes, d);
                Value(path, "RelatedEntities", entity.RelatedEntities, d);
                return;
            case EntityReference reference:
                Add(path, depth, label + ": EntityReference" + Token(v));
                Value(path, "LogicalName", reference.LogicalName, d);
                Value(path, "Id", reference.Id, d);
                Value(path, "Name", reference.Name, d);
                InOrder(path, "KeyAttributes", reference.KeyAttributes, d);
                Add(path + "/RowVersion", d, "RowVersion: " + (reference.RowVersion is null ? "null" : Probe ? reference.RowVersion : "{rowversion}"));
                return;
            case EntityCollection collection:
                Add(path, depth, label + ": EntityCollection" + Token(v));
                Value(path, "EntityName", collection.EntityName, d);
                Value(path, "Entities", collection.Entities, d);
                Value(path, "MoreRecords", collection.MoreRecords, d);
                Value(path, "PagingCookie", collection.PagingCookie, d);
                Value(path, "TotalRecordCount", collection.TotalRecordCount, d);
                Value(path, "TotalRecordCountLimitExceeded", collection.TotalRecordCountLimitExceeded, d);
                return;
            case OptionSetValue option:
                Add(path, depth, label + ": OptionSetValue" + Token(v));
                Value(path, "Value", option.Value, d);
                return;
            case Money money:
                Add(path, depth, label + ": Money" + Token(v));
                Value(path, "Value", money.Value, d);
                return;
            case Relationship relationship:
                Add(path, depth, label + ": Relationship" + Token(v));
                Value(path, "SchemaName", relationship.SchemaName, d);
                Value(path, "PrimaryEntityRole", relationship.PrimaryEntityRole, d);
                return;
            case IEnumerable items:
                var keyed = new List<KeyValuePair<string, object?>>();
                var listed = new List<object?>();
                foreach (var item in items)
                {
                    var t = item?.GetType();
                    if (t is { IsGenericType: true } && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                        keyed.Add(new(KeyText(t.GetProperty("Key")!.GetValue(item)), t.GetProperty("Value")!.GetValue(item)));
                    else listed.Add(item);
                }
                Add(path, depth, label + ": " + TypeName(v.GetType()) + "[" + (keyed.Count + listed.Count) + "]" + Token(v));
                foreach (var pair in keyed.OrderBy(p => p.Key, StringComparer.Ordinal)) Value(path, pair.Key, pair.Value, d);
                for (var k = 0; k < listed.Count; k++) Value(path, "[" + k + "]", listed[k], d);
                return;
            default:
                var type = v.GetType();
                Add(path, depth, label + ": " + TypeName(type) + Token(v));
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (property.GetIndexParameters().Length > 0 || property.Name == "ExtensionData" || !property.CanRead) continue;
                    object? value;
                    try
                    {
                        value = property.GetValue(v);
                    }
                    catch (Exception ex)
                    {
                        Add(path + "/" + property.Name, d, property.Name + ": throws " + (ex.InnerException ?? ex).GetType().Name);
                        continue;
                    }
                    Value(path, property.Name, value, d);
                }
                return;
        }
    }

    // ---- golden files -----------------------------------------------------------------------------------------------

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        dir.Should().NotBeNull("the repository root holds xrmverse.sln");
        return dir!.FullName;
    }

    /// <summary>
    /// Compares <paramref name="text"/> with <c>tests/fixtures/{directory}/{name}</c> (<c>core-response-golden</c> by default): a missing
    /// file is written once and the test fails; an existing file is never rewritten.
    /// </summary>
    public static void AssertGolden(string name, string text, string directory = "core-response-golden")
    {
        var path = Path.Combine(RepositoryRoot(), "tests", "fixtures", directory, name);
        var actual = Encoding.UTF8.GetBytes(text);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.Write(actual);
            Assert.Fail($"golden file {name} written, review and commit");
        }

        // Compare as text first: a difference is readable; then the bytes.
        Encoding.UTF8.GetString(File.ReadAllBytes(path)).Should().Be(text);
        File.ReadAllBytes(path).Should().Equal(actual);
    }
}
