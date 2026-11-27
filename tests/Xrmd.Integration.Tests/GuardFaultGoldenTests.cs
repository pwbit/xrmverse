using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Security;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3c2a, Task 1: golden dumps of the faults of the <see cref="SecurityGuard"/> and of the Security row rules it runs, captured on
/// the code before they move onto the model types (plan S.2e3c2a, Review Focus 1 to 3). Each scenario arranges its rows as the
/// administrator, then runs one request through <see cref="PipelineExecutor.ExecuteAsync"/> as a user with only the Basic User role, or
/// the caller it names, and writes the request, the fault (or <c>none</c>), the request as the caller holds it afterwards and the number
/// of rows the request would have written. The conventions are in <c>tests/fixtures/guard-fault-golden/README.md</c>; a missing file is
/// written once and the test fails, an existing file is never rewritten.
/// </summary>
[Collection("xrmd")]
public sealed class GuardFaultGoldenTests(XrmdFixture fx)
{
    public static TheoryData<string> Names => [.. GuardScenarios.All.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Guard_faults_are_what_the_golden_file_says(string name)
    {
        var scenario = GuardScenarios.All.Single(s => s.Name == name);
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var guard = new GuardKit(kit);
        var call = await scenario.Arrange(guard);

        var file = new StringBuilder();
        file.Append("=== PipelineExecutor.ExecuteAsync as ").Append(call.CallerText).Append(" ===\n");
        file.Append("--- request ---\n").Append(CoreDump.RequestText(call.Request, fixedDates: true));
        var fault = await guard.RunAsync(call.Request, call.Caller);
        file.Append("--- fault ---\n").Append(fault is null ? "none\n" : CoreDump.Response(null, fault));
        file.Append("--- request after ---\n").Append(CoreDump.RequestText(call.Request, fixedDates: true));
        file.Append("--- rows ---\n").Append(call.RowsLabel).Append(": ")
            .Append(Convert.ToInt64(await kit.ScalarAsync(call.RowsSql), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)).Append('\n');

        CoreDump.AssertGolden(name + ".txt", GuardKit.Normalize(kit, file.ToString()), GuardKit.Directory);
    }
}

/// <summary>One scenario: its arrangement, which returns the call to run.</summary>
internal sealed record GuardScenario(string Name, Func<GuardKit, Task<GuardCall>> Arrange);

/// <summary>
/// The call of a scenario: the request, the caller (and how the golden names it), and the count of the rows the request would have
/// written (<paramref name="RowsSql"/>, a query of one number, written as <paramref name="RowsLabel"/>: number).
/// </summary>
internal sealed record GuardCall(OrganizationRequest Request, Caller Caller, string CallerText, string RowsLabel, string RowsSql);

/// <summary>The scenarios of the guard fault goldens (plan S.2e3c2a, Task 1): one per guard branch and rule family.</summary>
internal static class GuardScenarios
{
    private const string BasicUser = "Basic User";
    private static readonly Guid Unknown = CoreScenarioKit.UnknownId;

    public static IReadOnlyList<GuardScenario> All { get; } =
    [
        // ---- record messages ----
        new("Create", async g =>
        {
            var (caller, text) = await g.BasicUserAsync();
            return new(new CreateRequest { Target = new Entity("account") { ["name"] = "gfg create" } }, caller, text,
                "account named 'gfg create'", "SELECT COUNT(*) FROM data.account WHERE name = 'gfg create'");
        }),
        new("Update", async g =>
        {
            var account = await g.AccountAsync("gfg update");
            var (caller, text) = await g.BasicUserAsync();
            return new(new UpdateRequest { Target = new Entity("account", account) { ["name"] = "gfg update changed" } }, caller, text,
                "account named 'gfg update changed'", "SELECT COUNT(*) FROM data.account WHERE name = 'gfg update changed'");
        }),
        new("Delete", async g =>
        {
            var account = await g.AccountAsync("gfg delete");
            var (caller, text) = await g.BasicUserAsync();
            return new(new DeleteRequest { Target = new EntityReference("account", account) }, caller, text,
                "account deleted", $"SELECT 1 - COUNT(*) FROM data.account WHERE accountid = '{account}'");
        }),
        // The new owner holds no prvReadAccount: the caller may assign, the assignee may not own.
        new("AssignAssigneeWithoutRead", async g =>
        {
            var account = await g.AccountAsync("gfg assign");
            var assignee = await g.UserAsync("assignee");
            var (caller, text) = await g.BasicUserAsync(("prvReadAccount", DepthMask.Global), ("prvAssignAccount", DepthMask.Global));
            return new(new AssignRequest { Target = new EntityReference("account", account), Assignee = new EntityReference("systemuser", assignee) },
                caller, text, "account owned by the assignee", $"SELECT COUNT(*) FROM data.account WHERE accountid = '{account}' AND ownerid = '{assignee}'");
        }),
        new("AssociateLookup", async g =>
        {
            var account = await g.AccountAsync("gfg associate");
            var contact = await g.ContactAsync("gfg-associate");
            var (caller, text) = await g.BasicUserAsync();
            return new(new AssociateRequest
            {
                Target = new EntityReference("contact", contact), Relationship = new Relationship("account_primary_contact"),
                RelatedEntities = [new EntityReference("account", account)],
            }, caller, text, "account with the contact as primary contact",
                $"SELECT COUNT(*) FROM data.account WHERE accountid = '{account}' AND primarycontactid = '{contact}'");
        }),
        new("AssociateSecurityRelationship", async g =>
        {
            var user = await g.UserAsync("member");
            var role = await g.RoleAsync("role");
            var (caller, text) = await g.BasicUserAsync();
            return new(new AssociateRequest
            {
                Target = new EntityReference("systemuser", user), Relationship = new Relationship("systemuserroles_association"),
                RelatedEntities = [new EntityReference("role", role)],
            }, caller, text, "systemuserroles of the user", $"SELECT COUNT(*) FROM data.systemuserroles WHERE systemuserid = '{user}'");
        }),
        // The caller may write the user, but not grant a role with a privilege it lacks at that depth.
        new("AssociateRoleWiderThanCaller", async g =>
        {
            var user = await g.UserAsync("member");
            var role = await g.RoleAsync("role", ("prvReadAccount", DepthMask.Global));
            var (caller, text) = await g.BasicUserAsync(("prvReadSystemUser", DepthMask.Global), ("prvWriteSystemUser", DepthMask.Global));
            return new(new AssociateRequest
            {
                Target = new EntityReference("systemuser", user), Relationship = new Relationship("systemuserroles_association"),
                RelatedEntities = [new EntityReference("role", role)],
            }, caller, text, "systemuserroles of the user", $"SELECT COUNT(*) FROM data.systemuserroles WHERE systemuserid = '{user}'");
        }),

        // ---- sharing ----
        new("GrantAccessRightCallerLacks", async g =>
        {
            var account = await g.AccountAsync("gfg grant");
            var principal = await g.UserAsync("principal", ("prvReadAccount", DepthMask.Global));
            var (caller, text) = await g.BasicUserAsync(("prvReadAccount", DepthMask.Global), ("prvShareAccount", DepthMask.Global));
            return new(new GrantAccessRequest
            {
                Target = new EntityReference("account", account),
                PrincipalAccess = new PrincipalAccess
                {
                    Principal = new EntityReference("systemuser", principal), AccessMask = AccessRights.ReadAccess | AccessRights.WriteAccess,
                },
            }, caller, text, "shares of the account", $"SELECT COUNT(*) FROM data.principalobjectaccess WHERE objectid = '{account}'");
        }),
        new("RevokeAccess", async g =>
        {
            var account = await g.AccountAsync("gfg revoke");
            var principal = await g.UserAsync("principal", ("prvReadAccount", DepthMask.Global));
            await g.Kit.Admin.ExecuteAsync(new GrantAccessRequest
            {
                Target = new EntityReference("account", account),
                PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", principal), AccessMask = AccessRights.ReadAccess },
            });
            var (caller, text) = await g.BasicUserAsync();
            return new(new RevokeAccessRequest { Target = new EntityReference("account", account), Revokee = new EntityReference("systemuser", principal) },
                caller, text, "shares of the account revoked", $"SELECT 1 - COUNT(*) FROM data.principalobjectaccess WHERE objectid = '{account}'");
        }),

        // ---- role privileges ----
        // Depth 7 is no SDK depth (Basic 0 to Global 3); the caller may write the role and holds the privilege at Basic depth.
        new("AddPrivilegesRoleDepth7", async g =>
        {
            var role = await g.RoleAsync("role");
            var (caller, text) = await g.BasicUserAsync(("prvReadRole", DepthMask.Global), ("prvWriteRole", DepthMask.Global), ("prvReadAccount", DepthMask.Basic));
            return new(new AddPrivilegesRoleRequest
            {
                RoleId = role, Privileges = [new RolePrivilege { PrivilegeId = PrivilegeNames.IdOf("prvReadAccount"), Depth = (PrivilegeDepth)7 }],
            }, caller, text, "privileges of the role", $"SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = '{role}'");
        }),
        new("RemovePrivilegeRole", async g =>
        {
            var role = await g.RoleAsync("role", ("prvReadAccount", DepthMask.Global));
            var (caller, text) = await g.BasicUserAsync();
            return new(new RemovePrivilegeRoleRequest { RoleId = role, PrivilegeId = PrivilegeNames.IdOf("prvReadAccount") }, caller, text,
                "privileges of the role removed", $"SELECT 1 - COUNT(*) FROM data.roleprivileges WHERE roleid = '{role}'");
        }),

        // ---- AddUserToRecordTeam: ruling A6, one scenario per first failing step ----
        new("RecordTeamStep1TemplateMissing", async g =>
        {
            var (record, user, _) = await g.RecordTeamAsync();
            var (caller, text) = await g.BasicUserAsync();
            return g.RecordTeamCall(record, user, Unknown, caller, text);
        }),
        new("RecordTeamStep2RecordOfAnotherTable", async g =>
        {
            var (_, user, template) = await g.RecordTeamAsync();
            var contact = await g.ContactAsync("gfg-team");
            var (caller, text) = await g.BasicUserAsync();
            return g.RecordTeamCall(new EntityReference("contact", contact), user, template, caller, text);
        }),
        // A template on contact, which has no AutoCreateAccessTeams (written with SQL: the rules refuse such a template).
        new("RecordTeamStep3TableWithoutAccessTeams", async g =>
        {
            var template = await g.ContactTemplateAsync();
            var contact = await g.ContactAsync("gfg-team");
            var user = await g.UserAsync("member", ("prvReadContact", DepthMask.Global));
            var (caller, text) = await g.BasicUserAsync();
            return g.RecordTeamCall(new EntityReference("contact", contact), user, template, caller, text);
        }),
        new("RecordTeamStep4RecordMissing", async g =>
        {
            var (_, user, template) = await g.RecordTeamAsync();
            var (caller, text) = await g.BasicUserAsync();
            return g.RecordTeamCall(new EntityReference("account", Unknown), user, template, caller, text);
        }),
        new("RecordTeamStep5UserMissing", async g =>
        {
            var (record, _, template) = await g.RecordTeamAsync();
            var (caller, text) = await g.BasicUserAsync();
            return g.RecordTeamCall(record, Unknown, template, caller, text);
        }),
        new("RecordTeamStep5UserDisabled", async g =>
        {
            var (record, _, template) = await g.RecordTeamAsync();
            var disabled = await g.UserAsync("disabled", disabled: true);
            var (caller, text) = await g.BasicUserAsync();
            return g.RecordTeamCall(record, disabled, template, caller, text);
        }),
        new("RecordTeamStep6CallerWithoutShare", async g =>
        {
            var (record, user, template) = await g.RecordTeamAsync();
            var (caller, text) = await g.BasicUserAsync(("prvReadAccount", DepthMask.Global));
            return g.RecordTeamCall(record, user, template, caller, text);
        }),
        new("RecordTeamStep7AddedUserWithoutRead", async g =>
        {
            var (record, _, template) = await g.RecordTeamAsync();
            var user = await g.UserAsync("added");
            var (caller, text) = await g.BasicUserAsync(("prvReadAccount", DepthMask.Global), ("prvShareAccount", DepthMask.Global));
            return g.RecordTeamCall(record, user, template, caller, text);
        }),

        // ---- reads and jobs ----
        new("RetrieveEntityChanges", async g =>
        {
            var (caller, text) = await g.BasicUserAsync();
            return new(new RetrieveEntityChangesRequest
            {
                EntityName = "account", Columns = new ColumnSet("name"), PageInfo = new PagingInfo { Count = 10, PageNumber = 1 },
            }, caller, text, "rows written", "SELECT 0");
        }),
        new("RetrieveExchangeRateMissingCurrency", async g =>
        {
            var (caller, text) = await g.BasicUserAsync();
            return new(new RetrieveExchangeRateRequest { TransactionCurrencyId = Unknown }, caller, text, "rows written", "SELECT 0");
        }),
        new("PublishDuplicateRule", async g =>
        {
            var rule = await g.DuplicateRuleAsync();
            var (caller, text) = await g.BasicUserAsync();
            return new(new PublishDuplicateRuleRequest { DuplicateRuleId = rule }, caller, text,
                "publish jobs of the rule", $"SELECT COUNT(*) FROM data.asyncoperation WHERE regardingobjectid = '{rule}'");
        }),
        new("RetrieveDuplicates", async g =>
        {
            var (caller, text) = await g.BasicUserAsync();
            return new(new RetrieveDuplicatesRequest
            {
                BusinessEntity = new Entity("contact") { ["emailaddress1"] = "gfg@example.com" }, MatchingEntityName = "contact",
                PagingInfo = new PagingInfo { Count = 10, PageNumber = 1 },
            }, caller, text, "rows written", "SELECT 0");
        }),
        new("BulkDetectDuplicatesFetchXml", async g =>
        {
            var (caller, text) = await g.BasicUserAsync();
            return new(new BulkDetectDuplicatesRequest
            {
                Query = new FetchExpression("<fetch><entity name='contact'><attribute name='fullname' /></entity></fetch>"), JobName = "gfg bulk detect",
                SendEmailNotification = false, ToRecipients = [], CCRecipients = [], RecurrencePattern = "",
                RecurrenceStartTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            }, caller, text, "jobs named 'gfg bulk detect'", "SELECT COUNT(*) FROM data.asyncoperation WHERE name = 'gfg bulk detect'");
        }),
        new("CalculateRollupField", async g =>
        {
            var account = await g.AccountAsync("gfg rollup");
            var (caller, text) = await g.BasicUserAsync();
            return new(new CalculateRollupFieldRequest { Target = new EntityReference("account", account), FieldName = "xrmv_ordertotal" }, caller, text,
                "rows written", "SELECT 0");
        }),

        // ---- email and queues ----
        new("AddToQueue", async g =>
        {
            var email = await g.Kit.EmailAsync();
            g.Kit.Name(email, "email");
            var queue = await g.QueueAsync("gfg public queue", privateQueue: false);
            var (caller, text) = await g.BasicUserAsync();
            return new(new AddToQueueRequest { Target = new EntityReference("email", email), DestinationQueueId = queue }, caller, text,
                "items of the queue", $"SELECT COUNT(*) FROM data.queueitem WHERE queueid = '{queue}'");
        }),
        new("RouteTo", async g =>
        {
            var (item, _) = await g.QueueItemAsync(privateQueue: false);
            var destination = await g.QueueAsync("gfg destination", privateQueue: false, label: "destination");
            var (caller, text) = await g.BasicUserAsync();
            return new(new RouteToRequest { Target = new EntityReference("queue", destination), QueueItemId = item }, caller, text,
                "items of the destination", $"SELECT COUNT(*) FROM data.queueitem WHERE queueid = '{destination}'");
        }),
        new("PickFromQueuePrivateQueue", async g =>
        {
            var (item, _) = await g.QueueItemAsync(privateQueue: true);
            var (caller, text) = await g.BasicUserAsync();
            return new(new PickFromQueueRequest { QueueItemId = item, WorkerId = caller.UserId }, caller, text,
                "items worked by the caller", $"SELECT COUNT(*) FROM data.queueitem WHERE queueitemid = '{item}' AND workerid IS NOT NULL");
        }),
        // Another user's queues need Read Queue at Global depth: the caller holds it at Basic depth only (no Basic User role).
        new("RetrieveUserQueuesOfAnotherUser", async g =>
        {
            var (caller, text) = await g.UserCallerAsync(("prvReadQueue", DepthMask.Basic));
            return new(new RetrieveUserQueuesRequest { UserId = g.Kit.Caller.UserId, IncludePublic = true }, caller, text, "rows written", "SELECT 0");
        }),
        // The caller owns the email (Write passes); its sender is a private queue the caller cannot read.
        new("SendEmailFromQueue", async g =>
        {
            var (caller, text) = await g.BasicUserAsync();
            var queue = await g.QueueAsync("gfg sender queue", privateQueue: true);
            var email = await g.Kit.Admin.CreateAsync(new Entity("email")
            {
                ["subject"] = "gfg send", ["ownerid"] = new EntityReference("systemuser", caller.UserId),
                ["from"] = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("queue", queue) }]),
            });
            g.Kit.Name(email, "email");
            return new(new SendEmailRequest { EmailId = email, IssueSend = false, TrackingToken = "" }, caller, text,
                "email sent", $"SELECT COUNT(*) FROM data.email WHERE activityid = '{email}' AND statecode = 1");
        }),

        // ---- bulk and elastic ----
        new("CreateMultiple", async g =>
        {
            var (caller, text) = await g.BasicUserAsync();
            return new(new CreateMultipleRequest
            {
                Targets = new EntityCollection([new Entity("account") { ["name"] = "gfg multiple" }]) { EntityName = "account" },
            }, caller, text, "accounts named 'gfg multiple'", "SELECT COUNT(*) FROM data.account WHERE name = 'gfg multiple'");
        }),
        new("UpsertElastic", async g =>
        {
            await g.Kit.EnsureElasticTableAsync();
            var id = new Guid("6f0e9a52-3c1d-4b7a-9e2f-0a1b2c3d4e5f");
            var (caller, text) = await g.BasicUserAsync();
            return new(new UpsertRequest { Target = new Entity("new_crgelastic", id) { ["new_name"] = "gfg upsert" } }, caller, text,
                "rows named 'gfg upsert'", "SELECT COUNT(*) FROM data.new_crgelastic WHERE new_name = 'gfg upsert'");
        }),

        // ---- field security ----
        // Two secured columns the caller may not set, the second of the table first in the Target: the first in attribute order is named.
        new("CreateSecuredColumns", async g =>
        {
            await g.EnsureSecuredTableAsync();
            var (caller, text) = await g.BasicUserAsync((GuardKit.SecuredPrivilege("Create"), DepthMask.Global), (GuardKit.SecuredPrivilege("Read"), DepthMask.Global));
            return new(new CreateRequest
            {
                Target = new Entity(GuardKit.SecuredTable) { ["new_name"] = "gfg secured create", [GuardKit.Secured2] = "two", [GuardKit.Secured1] = "one" },
            }, caller, text, "rows named 'gfg secured create'", $"SELECT COUNT(*) FROM data.{GuardKit.SecuredTable} WHERE new_name = 'gfg secured create'");
        }),
        new("UpdateSecuredColumns", async g =>
        {
            var record = await g.SecuredRecordAsync();
            var (caller, text) = await g.BasicUserAsync((GuardKit.SecuredPrivilege("Read"), DepthMask.Global), (GuardKit.SecuredPrivilege("Write"), DepthMask.Global));
            return new(new UpdateRequest
            {
                Target = new Entity(GuardKit.SecuredTable, record) { [GuardKit.Secured2] = "two changed", [GuardKit.Secured1] = "one changed" },
            }, caller, text, "rows with a changed secured column",
                $"SELECT COUNT(*) FROM data.{GuardKit.SecuredTable} WHERE {GuardKit.SecuredTable}id = '{record}' AND ({GuardKit.Secured1} = 'one changed' OR {GuardKit.Secured2} = 'two changed')");
        }),
        // Allowed: the caller reads the record and may read and update the column. The rules fill objectid (a new instance),
        // objecttypecode and the two access flags in the caller's Target.
        new("FieldShareCreate", async g =>
        {
            var record = await g.SecuredRecordAsync();
            var principal = await g.UserAsync("principal");
            var (caller, text) = await g.BasicUserAsync((GuardKit.SecuredPrivilege("Read"), DepthMask.Global));
            await g.FieldProfileAsync(caller.UserId, GuardKit.Secured1, read: true, update: true);
            return new(new CreateRequest
            {
                Target = new Entity(FieldSecurityTables.Share)
                {
                    [FieldSecurityTables.ShareObjectId] = new EntityReference(GuardKit.SecuredTable, record),
                    [FieldSecurityTables.ShareAttributeId] = g.ColumnId(GuardKit.Secured1),
                    [FieldSecurityTables.SharePrincipalId] = new EntityReference("systemuser", principal),
                    [FieldSecurityTables.ShareReadAccess] = true,
                },
            }, caller, text, "field shares of the record", $"SELECT COUNT(*) FROM data.{FieldSecurityTables.Share} WHERE objectid = '{record}'");
        }),
        new("FieldShareUpdateFixedColumn", async g =>
        {
            var record = await g.SecuredRecordAsync();
            var principal = await g.UserAsync("principal");
            var share = await g.Kit.Admin.CreateAsync(new Entity(FieldSecurityTables.Share)
            {
                [FieldSecurityTables.ShareObjectId] = new EntityReference(GuardKit.SecuredTable, record),
                [FieldSecurityTables.ShareAttributeId] = g.ColumnId(GuardKit.Secured1),
                [FieldSecurityTables.SharePrincipalId] = new EntityReference("systemuser", principal),
                [FieldSecurityTables.ShareReadAccess] = true,
            });
            g.Kit.Name(share, "share");
            var (caller, text) = await g.BasicUserAsync();
            return new(new UpdateRequest
            {
                Target = new Entity(FieldSecurityTables.Share, share) { [FieldSecurityTables.ShareAttributeId] = g.ColumnId(GuardKit.Secured2) },
            }, caller, text, "field shares moved to the second column",
                $"SELECT COUNT(*) FROM data.{FieldSecurityTables.Share} WHERE objectid = '{record}' AND attributeid = '{g.ColumnId(GuardKit.Secured2)}'");
        }),

        // ---- duplicate detection ----
        // The caller may write the condition, not its rule (ruling D6).
        new("UpdateDuplicateRuleCondition", async g =>
        {
            var rule = await g.DuplicateRuleAsync();
            var condition = (Guid)(await g.Kit.ScalarAsync($"SELECT duplicateruleconditionid FROM data.duplicaterulecondition WHERE regardingobjectid = '{rule}'"))!;
            g.Kit.Name(condition, "condition");
            var (caller, text) = await g.BasicUserAsync(("prvReadDuplicateRuleCondition", DepthMask.Global), ("prvWriteDuplicateRuleCondition", DepthMask.Global),
                ("prvReadDuplicateRule", DepthMask.Global));
            return new(new UpdateRequest
            {
                Target = new Entity("duplicaterulecondition", condition) { ["baseattributename"] = "lastname", ["matchingattributename"] = "lastname" },
            }, caller, text, "conditions on lastname",
                $"SELECT COUNT(*) FROM data.duplicaterulecondition WHERE duplicateruleconditionid = '{condition}' AND baseattributename = 'lastname'");
        }),

        // ---- system tables: rules that hold for every caller ----
        new("SystemCreatesPrivilege", async g =>
        {
            var (caller, text) = await g.SystemAsync();
            return new(new CreateRequest { Target = new Entity("privilege") { ["name"] = "prvGfgGolden" } }, caller, text,
                "privileges named 'prvGfgGolden'", "SELECT COUNT(*) FROM data.privilege WHERE name = 'prvGfgGolden'");
        }),
        new("CreateCurrencyStringRate", async g =>
        {
            var (caller, text) = await g.BasicUserAsync();
            return new(new CreateRequest
            {
                Target = new Entity("transactioncurrency")
                {
                    ["currencyname"] = "gfg currency", ["currencysymbol"] = "G", ["isocurrencycode"] = "GFG", ["exchangerate"] = "1.5",
                    ["currencyprecision"] = 2,
                },
            }, caller, text, "currencies named 'gfg currency'", "SELECT COUNT(*) FROM data.transactioncurrency WHERE currencyname = 'gfg currency'");
        }),
        new("CreateEnvironmentVariableValueWithoutDefinition", async g =>
        {
            var (caller, text) = await g.BasicUserAsync();
            return new(new CreateRequest
            {
                Target = new Entity("environmentvariablevalue") { ["schemaname"] = "gfg_value", ["value"] = "gfg" },
            }, caller, text, "values named 'gfg_value'", "SELECT COUNT(*) FROM data.environmentvariablevalue WHERE schemaname = 'gfg_value'");
        }),
        // A completed rollup calculation job (written with SQL): a completed job may be deleted, a rollup job never (ruling R9).
        new("DeleteRollupJob", async g =>
        {
            var job = await g.RollupJobAsync();
            var (caller, text) = await g.BasicUserAsync();
            return new(new DeleteRequest { Target = new EntityReference("asyncoperation", job) }, caller, text,
                "job deleted", $"SELECT 1 - COUNT(*) FROM data.asyncoperation WHERE asyncoperationid = '{job}'");
        }),
        new("CreateWorkflowType2", async g =>
        {
            var (caller, text) = await g.BasicUserAsync();
            return new(new CreateRequest
            {
                Target = new Entity("workflow")
                {
                    ["name"] = "gfg activation", ["type"] = new OptionSetValue(2), ["category"] = new OptionSetValue(0), ["primaryentity"] = "account",
                },
            }, caller, text, "processes named 'gfg activation'", "SELECT COUNT(*) FROM data.workflow WHERE name = 'gfg activation'");
        }),

        // ---- malformed requests from a denied caller ----
        new("CreateTargetIsReference", async g =>
        {
            var account = await g.AccountAsync("gfg reference target");
            var (caller, text) = await g.BasicUserAsync();
            var request = new CreateRequest();
            request.Parameters["Target"] = new EntityReference("account", account);
            return new(request, caller, text, "accounts named 'gfg reference target'", "SELECT COUNT(*) - 1 FROM data.account WHERE name = 'gfg reference target'");
        }),
        new("GrantAccessNullPrincipal", async g =>
        {
            var account = await g.AccountAsync("gfg null principal");
            var (caller, text) = await g.BasicUserAsync();
            return new(new GrantAccessRequest
            {
                Target = new EntityReference("account", account), PrincipalAccess = new PrincipalAccess { AccessMask = AccessRights.ReadAccess },
            }, caller, text, "shares of the account", $"SELECT COUNT(*) FROM data.principalobjectaccess WHERE objectid = '{account}'");
        }),
        // The caller may delete its own accounts only: the record named by its primary key is checked as by its id.
        new("DeleteByKeyOnly", async g =>
        {
            var account = await g.AccountAsync("gfg delete by key");
            var (caller, text) = await g.BasicUserAsync(("prvDeleteAccount", DepthMask.Basic));
            return new(new DeleteRequest { Target = new EntityReference("account", "accountid", account) }, caller, text,
                "account deleted", $"SELECT 1 - COUNT(*) FROM data.account WHERE accountid = '{account}'");
        }),
    ];
}

/// <summary>
/// What the guard scenarios need beyond <see cref="CoreScenarioKit"/>: the callers (a user with Basic User and the privileges a scenario
/// adds, SYSTEM), the secured table <c>new_gfgsecure</c>, record teams, queues, duplicate rules and a rollup job. Fixed names only.
/// </summary>
internal sealed class GuardKit(CoreScenarioKit kit)
{
    public const string Directory = "guard-fault-golden";
    public const string SecuredTable = "new_gfgsecure";
    public const string Secured1 = "new_gfgs1", Secured2 = "new_gfgs2";

    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static readonly Regex CustomObjectTypeCode = new(@"OTC=\d{5,}");

    public CoreScenarioKit Kit { get; } = kit;

    public static string SecuredPrivilege(string verb) => "prv" + verb + "new_GfgSecure";

    /// <summary>The kit's normalisation, and a custom table's object type code in a fault text as <c>OTC={otc}</c>.</summary>
    public static string Normalize(CoreScenarioKit kit, string text) => kit.Normalize(CustomObjectTypeCode.Replace(text, "OTC={otc}"), keepFixedDates: true);

    /// <summary>Runs <paramref name="request"/> through the pipeline as <paramref name="caller"/> at depth 1 with no session; returns its fault, or null.</summary>
    public async Task<Exception?> RunAsync(OrganizationRequest request, Caller caller)
    {
        await using var scope = Kit.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<PipelineExecutor>();
        try
        {
            await executor.ExecuteAsync(request, new InvocationContext(caller, caller.UserId, 1, null, null, Guid.NewGuid()), default);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            Kit.Cache.Invalidate();
        }
    }

    // ---- callers ----

    /// <summary>A user in the root business unit with the Basic User role and, when given, a role with <paramref name="privileges"/>; named <c>{a:caller}</c>.</summary>
    public async Task<(Caller Caller, string Text)> BasicUserAsync(params (string Privilege, DepthMask Depth)[] privileges)
    {
        var caller = await NewCallerAsync(basicUser: true, privileges);
        return (caller, privileges.Length == 0 ? "a user with Basic User" : "a user with Basic User and " + Describe(privileges));
    }

    /// <summary>A user in the root business unit with only a role holding <paramref name="privileges"/>; named <c>{a:caller}</c>.</summary>
    public async Task<(Caller Caller, string Text)> UserCallerAsync(params (string Privilege, DepthMask Depth)[] privileges)
    {
        var caller = await NewCallerAsync(basicUser: false, privileges);
        return (caller, "a user with only " + Describe(privileges));
    }

    /// <summary>The SYSTEM user.</summary>
    public async Task<(Caller Caller, string Text)> SystemAsync()
    {
        var system = await Kit.Cache.SystemUserIdAsync(default);
        Kit.Name(system, "system");
        return (new Caller(system, await SecurityTestHelper.RootBusinessUnitAsync(Kit.ConnectionString)), "SYSTEM");
    }

    private async Task<Caller> NewCallerAsync(bool basicUser, (string Privilege, DepthMask Depth)[] privileges)
    {
        var oid = Guid.NewGuid();
        var root = await SecurityTestHelper.RootBusinessUnitAsync(Kit.ConnectionString);
        var id = await SecurityTestHelper.CreateUserAsync(Kit.ConnectionString, Kit.Cache, root, oid, userName: "gfg caller");
        Kit.Name(id, "caller");
        if (basicUser) await SecurityTestHelper.AssignRoleAsync(Kit.ConnectionString, Kit.Cache, id, StandardRoles.BasicUser);
        if (privileges.Length > 0)
            await SecurityTestHelper.AssignRoleAsync(Kit.ConnectionString, Kit.Cache, id,
                await SecurityTestHelper.CreateRoleAsync(Kit.ConnectionString, Kit.Cache, "gfg caller grant", privileges));
        return await TestServices.CallerAsync(Kit.Services, oid);
    }

    private static string Describe((string Privilege, DepthMask Depth)[] privileges) =>
        string.Join(", ", privileges.Select(p => p.Privilege + " " + p.Depth));

    /// <summary>Another user in the root business unit (no role unless <paramref name="privileges"/>), named <c>{a:label}</c>.</summary>
    public async Task<Guid> UserAsync(string label, params (string Privilege, DepthMask Depth)[] privileges) =>
        await UserAsync(label, disabled: false, privileges);

    public async Task<Guid> UserAsync(string label, bool disabled, params (string Privilege, DepthMask Depth)[] privileges)
    {
        var root = await SecurityTestHelper.RootBusinessUnitAsync(Kit.ConnectionString);
        var id = await SecurityTestHelper.CreateUserAsync(Kit.ConnectionString, Kit.Cache, root, disabled: disabled, userName: "gfg " + label);
        Kit.Name(id, label);
        if (privileges.Length > 0)
            await SecurityTestHelper.AssignRoleAsync(Kit.ConnectionString, Kit.Cache, id,
                await SecurityTestHelper.CreateRoleAsync(Kit.ConnectionString, Kit.Cache, "gfg " + label + " grant", privileges));
        return id;
    }

    /// <summary>A role named <c>gfg {label}</c> in the root business unit with <paramref name="privileges"/>, named <c>{a:label}</c>.</summary>
    public async Task<Guid> RoleAsync(string label, params (string Privilege, DepthMask Depth)[] privileges)
    {
        var id = await SecurityTestHelper.CreateRoleAsync(Kit.ConnectionString, Kit.Cache, "gfg " + label, privileges);
        Kit.Name(id, label);
        return id;
    }

    // ---- records ----

    /// <summary>An account of the administrator named <paramref name="name"/>, named <c>{a:account}</c>.</summary>
    public async Task<Guid> AccountAsync(string name)
    {
        var id = await Kit.Admin.CreateAsync(new Entity("account") { ["name"] = name });
        Kit.Name(id, "account");
        return id;
    }

    /// <summary>A contact of the administrator with the last name <paramref name="lastName"/>, named <c>{a:contact}</c>.</summary>
    public async Task<Guid> ContactAsync(string lastName)
    {
        var id = await Kit.Admin.CreateAsync(new Entity("contact") { ["lastname"] = lastName });
        Kit.Name(id, "contact");
        return id;
    }

    // ---- record teams ----

    /// <summary><see cref="CoreScenarioKit.RecordTeamAsync"/>, its account, user and template named.</summary>
    public async Task<(EntityReference Record, Guid User, Guid Template)> RecordTeamAsync()
    {
        var (record, user, template) = await Kit.RecordTeamAsync();
        Kit.Name(record.Id, "account");
        Kit.Name(user, "member");
        Kit.Name(template, "template");
        return (record, user, template);
    }

    /// <summary>A template (Read) on <c>contact</c>, which has no <c>AutoCreateAccessTeams</c>: written with SQL, deleted on dispose.</summary>
    public async Task<Guid> ContactTemplateAsync()
    {
        var id = Guid.NewGuid();
        await Kit.ExecAsync($"""
            INSERT INTO data.teamtemplate (teamtemplateid, teamtemplatename, objecttypecode, defaultaccessrightsmask, issystem)
            VALUES ('{id}', 'gfg contact template', {Kit.Store.GetEntity("contact")!.ObjectTypeCode!.Value}, 1, 0)
            """);
        Kit.OnCleanup(() => Kit.ExecAsync($"DELETE FROM data.teamtemplate WHERE teamtemplateid = '{id}'"));
        Kit.Name(id, "template");
        return id;
    }

    /// <summary>AddUserToRecordTeam of <paramref name="user"/> on <paramref name="record"/> with <paramref name="template"/>.</summary>
    public GuardCall RecordTeamCall(EntityReference record, Guid user, Guid template, Caller caller, string text) =>
        new(new AddUserToRecordTeamRequest { Record = record, SystemUserId = user, TeamTemplateId = template }, caller, text,
            "access teams of the record", $"SELECT COUNT(*) FROM data.team WHERE regardingobjectid = '{record.Id}'");

    // ---- queues ----

    /// <summary>A queue of the administrator, public or private, named <c>{a:label}</c>.</summary>
    public async Task<Guid> QueueAsync(string name, bool privateQueue, string label = "queue")
    {
        var id = await Kit.Admin.CreateAsync(new Entity("queue")
        {
            ["name"] = name, ["queueviewtype"] = new OptionSetValue(privateQueue ? 1 : 0), ["queuetypecode"] = new OptionSetValue(1),
        });
        Kit.Name(id, label);
        return id;
    }

    /// <summary>An email of the administrator added to a new queue; the item is named <c>{a:item}</c>, the email <c>{a:email}</c>.</summary>
    public async Task<(Guid Item, Guid Queue)> QueueItemAsync(bool privateQueue)
    {
        var email = await Kit.EmailAsync();
        Kit.Name(email, "email");
        var queue = await QueueAsync(privateQueue ? "gfg private queue" : "gfg public queue", privateQueue);
        var item = (Guid)(await Kit.Admin.ExecuteAsync(new AddToQueueRequest { Target = new EntityReference("email", email), DestinationQueueId = queue }))
            .Results["QueueItemId"];
        Kit.Name(item, "item");
        return (item, queue);
    }

    // ---- duplicate rules, jobs ----

    /// <summary>
    /// An unpublished duplicate rule on contact with one condition on <c>emailaddress1</c>, named <c>{a:rule}</c>; deleted on dispose (with
    /// duplicate detection on for contact while the kit lives, as a rule needs it).
    /// </summary>
    public async Task<Guid> DuplicateRuleAsync()
    {
        await Kit.DuplicatesAsync();
        var rule = await Kit.Admin.CreateAsync(new Entity("duplicaterule")
        {
            ["name"] = "gfg rule", ["baseentityname"] = "contact", ["matchingentityname"] = "contact",
        });
        Kit.Name(rule, "rule");
        Kit.OnCleanup(() => Kit.Admin.DeleteAsync("duplicaterule", rule));
        await Kit.Admin.CreateAsync(new Entity("duplicaterulecondition")
        {
            ["regardingobjectid"] = new EntityReference("duplicaterule", rule), ["baseattributename"] = "emailaddress1",
            ["matchingattributename"] = "emailaddress1", ["operatorcode"] = new OptionSetValue(0),
        });
        return rule;
    }

    /// <summary>A completed rollup calculation job (<c>operationtype</c> 57) written with SQL, named <c>{a:job}</c>; deleted on dispose.</summary>
    public async Task<Guid> RollupJobAsync()
    {
        var id = Guid.NewGuid();
        await Kit.ExecAsync($"""
            INSERT INTO data.asyncoperation (asyncoperationid, name, operationtype, statecode, statuscode, createdon, modifiedon)
            VALUES ('{id}', 'gfg rollup job', 57, 3, 30, SYSUTCDATETIME(), SYSUTCDATETIME())
            """);
        Kit.OnCleanup(() => Kit.ExecAsync($"DELETE FROM data.asyncoperation WHERE asyncoperationid = '{id}'"));
        Kit.Name(id, "job");
        return id;
    }

    // ---- field security ----

    /// <summary><c>new_gfgsecure</c>: a user-owned table with the secured columns <c>new_gfgs1</c> and <c>new_gfgs2</c>, created once.</summary>
    public async Task EnsureSecuredTableAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (Kit.Store.GetEntity(SecuredTable) is not null) return;
            var entity = new EntityMetadata { LogicalName = SecuredTable, SchemaName = "new_GfgSecure", OwnershipType = OwnershipTypes.UserOwned };
            entity.SetAttributes(
            [
                new StringAttributeMetadata { LogicalName = Secured1, SchemaName = "new_GfgS1", MaxLength = 100, IsSecured = true },
                new StringAttributeMetadata { LogicalName = Secured2, SchemaName = "new_GfgS2", MaxLength = 100, IsSecured = true },
            ]);
            await Kit.Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>A row of <c>new_gfgsecure</c> of the administrator with both secured columns set, named <c>{a:record}</c>.</summary>
    public async Task<Guid> SecuredRecordAsync()
    {
        await EnsureSecuredTableAsync();
        var id = await Kit.Admin.CreateAsync(new Entity(SecuredTable) { ["new_name"] = "gfg secured", [Secured1] = "one", [Secured2] = "two" });
        Kit.Name(id, "record");
        return id;
    }

    /// <summary>The <c>MetadataId</c> of a column of <c>new_gfgsecure</c>.</summary>
    public Guid ColumnId(string column) => Kit.Store.GetEntity(SecuredTable)!.Attributes.Single(a => a.LogicalName == column).MetadataId!.Value;

    /// <summary>A field security profile with read and/or update on <paramref name="column"/> of <c>new_gfgsecure</c>, given to <paramref name="user"/>.</summary>
    public async Task FieldProfileAsync(Guid user, string column, bool read, bool update)
    {
        var profile = await Kit.Admin.CreateAsync(new Entity(FieldSecurityTables.Profile) { ["name"] = "gfg-" + Guid.NewGuid().ToString("N")[..8] });
        Kit.Name(profile, "profile");
        await Kit.Admin.CreateAsync(new Entity(FieldSecurityTables.Permission)
        {
            [FieldSecurityTables.ProfileId] = new EntityReference(FieldSecurityTables.Profile, profile),
            [FieldSecurityTables.EntityName] = SecuredTable, [FieldSecurityTables.AttributeLogicalName] = column,
            [FieldSecurityTables.CanRead] = new OptionSetValue(read ? 4 : 0), [FieldSecurityTables.CanCreate] = new OptionSetValue(0),
            [FieldSecurityTables.CanUpdate] = new OptionSetValue(update ? 4 : 0),
        });
        await Kit.Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("systemuser", user), Relationship = new Relationship(FieldSecurityTables.UserProfilesRelationship),
            RelatedEntities = [new EntityReference(FieldSecurityTables.Profile, profile)],
        });
        Kit.Cache.Invalidate();
    }
}
