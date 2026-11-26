using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using SkiaSharp;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Query;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Query;
using Xrmd.Pipeline;
using Xrmd.Query.Model;
using Xrmd.Security;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3b, Task 1: golden dumps of what the CRUD handlers store and answer, captured on the code before they move onto the model types
/// (plan S.2e3b, Review Focus 1 and 2). A write scenario runs its request through <see cref="PipelineExecutor.ExecuteAsync"/> as the
/// administrator (the migration scenario through the core inside a <see cref="MigrationScope"/>, where the pipeline refuses to run) and
/// writes the response or fault, then every column of the rows it touched, read with plain SQL (columns in name order, values as
/// <c>Type:value</c>): the data rows, the <c>activityparty</c>, <c>activitypointer</c> and <c>audit</c> rows, the match codes and the
/// mail secrets. A read scenario runs its request through the pipeline and its query through <see cref="QueryExecutor.ExecuteAsync(QueryModel, Caller, CancellationToken)"/>
/// directly, and writes both answers with the <see cref="CoreDump"/> writers. The conventions are in
/// <c>tests/fixtures/core-row-golden/README.md</c>; a missing file is written once and the test fails, an existing file is never rewritten.
/// </summary>
[Collection("xrmd")]
public sealed class CoreRowGoldenTests(XrmdFixture fx)
{
    public static TheoryData<string> Writes => [.. RowScenarios.Writes.Select(s => s.Name)];

    public static TheoryData<string> Reads => [.. RowScenarios.Reads.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Stored_rows_are_what_the_golden_file_says(string name)
    {
        var scenario = RowScenarios.Writes.Single(s => s.Name == name);
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var rows = new RowKit(kit);
        await rows.EnsureTablesAsync();
        var file = new StringBuilder();

        var request = await scenario.Arrange(rows);
        file.Append(scenario.Migration
            ? "=== CoreOperations.CreateAsync in a MigrationScope ===\n"
            : "=== PipelineExecutor.ExecuteAsync ===\n");
        file.Append("--- request ---\n").Append(CoreDump.RequestText(request, fixedDates: true));
        var (response, fault) = scenario.Migration
            ? await rows.CreateInMigrationScopeAsync((CreateRequest)request)
            : await CoreScenarios.ViaPipelineAsync(kit, request);
        file.Append("--- response ---\n").Append(CoreDump.Response(response, fault, fixedDates: true));
        await scenario.Rows(rows, response);
        file.Append(rows.Text);

        CoreDump.AssertGolden(name + ".txt", kit.Normalize(file.ToString(), keepFixedDates: true), RowKit.Directory);
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Reads_are_what_the_golden_file_says(string name)
    {
        var scenario = RowScenarios.Reads.Single(s => s.Name == name);
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var rows = new RowKit(kit);
        await rows.EnsureTablesAsync();
        var file = new StringBuilder();

        var (request, query) = await scenario.Arrange(rows);
        file.Append("=== (a) PipelineExecutor.ExecuteAsync ===\n--- request ---\n").Append(CoreDump.RequestText(request, fixedDates: true)).Append("--- response ---\n");
        var (response, fault) = await CoreScenarios.ViaPipelineAsync(kit, request);
        file.Append(CoreDump.Response(response, fault, fixedDates: true));

        file.Append("=== (b) QueryExecutor.ExecuteAsync ===\n--- result ---\n");
        await using (var scope = kit.Services.CreateAsyncScope())
        {
            try
            {
                var result = await scope.ServiceProvider.GetRequiredService<QueryExecutor>().ExecuteAsync(query, kit.Caller);
                file.Append(CoreDump.ValueText("Result", result, fixedDates: true));
            }
            catch (Exception ex)
            {
                file.Append(CoreDump.Response(null, ex));
            }
        }

        CoreDump.AssertGolden(name + ".txt", kit.Normalize(file.ToString(), keepFixedDates: true), RowKit.Directory);
    }
}

/// <summary>A write scenario: its request (with what it needs in the database) and the rows it dumps after the call.</summary>
internal sealed record RowScenario(string Name, Func<RowKit, Task<OrganizationRequest>> Arrange, Func<RowKit, OrganizationResponse?, Task> Rows,
    bool Migration = false);

/// <summary>A read scenario: its request through the pipeline and the same query in the model form, for <see cref="QueryExecutor"/>.</summary>
internal sealed record ReadScenario(string Name, Func<RowKit, Task<(OrganizationRequest Request, QueryModel Query)>> Arrange);

/// <summary>The scenarios of the row, fault and read goldens (plan S.2e3b, Task 1).</summary>
internal static class RowScenarios
{
    private const string Types = RowKit.TypesTable;
    private const string Image = RowKit.ImageTable;
    private const string Key = RowKit.KeyTable;
    private static readonly DateTime Original = new(2015, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    public static IReadOnlyList<RowScenario> Writes { get; } =
    [
        // ---- activities and their party lists (activityparty, activitypointer) ----
        new("CreateEmailWithParties", async r =>
        {
            var contact = await r.ContactAsync("crr-party");
            var account = await r.AccountAsync("crr party account");
            return new CreateRequest
            {
                Target = new Entity("email")
                {
                    ["subject"] = "crr email", ["description"] = "crr body",
                    ["from"] = Parties(new EntityReference("systemuser", r.Kit.Caller.UserId)),
                    ["to"] = Parties(new EntityReference("contact", contact), new EntityReference("account", account)),
                    ["cc"] = new EntityCollection([new Entity("activityparty") { ["addressused"] = "crr.unresolved@example.com" }]),
                },
            };
        }, async (r, response) => await r.ActivityAsync("email", r.Created(response))),
        new("UpdateEmailParties", async r =>
        {
            var contact = await r.ContactAsync("crr-party");
            var account = await r.AccountAsync("crr party account");
            var email = await r.Kit.Admin.CreateAsync(new Entity("email")
            {
                ["subject"] = "crr email", ["to"] = Parties(new EntityReference("contact", contact)),
            });
            r.Name(email, "email");
            return new UpdateRequest
            {
                Target = new Entity("email", email) { ["subject"] = "crr email changed", ["to"] = Parties(new EntityReference("account", account)) },
            };
        }, (r, _) => r.ActivityAsync("email", r.Id("email"))),
        // State and status defaults of an activity, and its activitypointer row.
        new("CreateTaskDefaults", _ => Task.FromResult<OrganizationRequest>(new CreateRequest
        {
            Target = new Entity("task") { ["subject"] = "crr task", ["scheduledend"] = Original },
        }), (r, response) => r.ActivityAsync("task", r.Created(response))),

        // ---- note body, attachment ----
        new("CreateNoteWithBody", async r =>
        {
            var account = await r.AccountAsync("crr note account");
            return new CreateRequest
            {
                Target = new Entity("annotation")
                {
                    ["subject"] = "crr note", ["notetext"] = "crr text", ["filename"] = "crr.txt", ["mimetype"] = "text/plain",
                    ["documentbody"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("crr note body")), ["objectid"] = new EntityReference("account", account),
                },
            };
        }, async (r, response) =>
        {
            var id = r.Created(response);
            await r.RowAsync("annotation", id);
            await r.FilesAsync(id);
        }),
        new("CreateAttachment", async r =>
        {
            var email = await r.Kit.Admin.CreateAsync(new Entity("email") { ["subject"] = "crr email" });
            r.Name(email, "email");
            return new CreateRequest
            {
                Target = new Entity("activitymimeattachment")
                {
                    ["objectid"] = new EntityReference("email", email), ["subject"] = "crr attachment", ["filename"] = "crr.txt",
                    ["mimetype"] = "text/plain", ["body"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("crr attachment body")),
                },
            };
        }, async (r, response) =>
        {
            var id = r.Created(response);
            await r.RowAsync("activitymimeattachment", id);
            await r.FilesAsync(id);
            await r.RowAsync("email", r.Id("email"));
        }),

        // ---- contact full name ----
        new("CreateContactFullName", _ => Task.FromResult<OrganizationRequest>(new CreateRequest
        {
            Target = new Entity("contact") { ["firstname"] = "Ada", ["middlename"] = "B.", ["lastname"] = "crr-fullname" },
        }), (r, response) => r.RowAsync("contact", r.Created(response))),
        new("UpdateContactFullName", async r =>
        {
            var contact = await r.ContactAsync("crr-fullname");
            return new UpdateRequest { Target = new Entity("contact", contact) { ["firstname"] = "Grace", ["lastname"] = "crr-fullname-changed" } };
        }, (r, _) => r.RowAsync("contact", r.Id("contact"))),

        // ---- money with the currency stamp, state and status, owner and business unit ----
        // The seeded account has no money column: the currency stamp on new_crrtypes.new_amount (new_amount_base, exchangerate,
        // transactioncurrencyid).
        new("CreateMoney", async r =>
        {
            // The golden row holds the default first code (CRR-1000): seeded, so the scenario does not depend on the ones before it.
            await r.SeedCodeAsync(AutoNumberFormat.DefaultSeed);
            return new CreateRequest { Target = new Entity(Types) { ["new_name"] = "crr money", ["new_amount"] = new Money(1234.5m) } };
        }, (r, response) => r.RowAsync(Types, r.Created(response))),
        new("UpdateMoney", async r =>
        {
            await r.SeedCodeAsync(101);
            var row = await r.Kit.Admin.CreateAsync(new Entity(Types) { ["new_name"] = "crr money", ["new_amount"] = new Money(10m) });
            r.Name(row, "types");
            return new UpdateRequest { Target = new Entity(Types, row) { ["new_amount"] = new Money(99.125m) } };
        }, (r, _) => r.RowAsync(Types, r.Id("types"))),
        new("UpdateAccountState", async r =>
        {
            var account = await r.AccountAsync("crr state");
            return new UpdateRequest { Target = new Entity("account", account) { ["statecode"] = new OptionSetValue(1) } };
        }, (r, _) => r.RowAsync("account", r.Id("account"))),
        new("AssignAccountToChildUnit", async r =>
        {
            var account = await r.AccountAsync("crr assign");
            var user = await r.ChildUnitUserAsync();
            return new AssignRequest { Target = new EntityReference("account", account), Assignee = new EntityReference("systemuser", user) };
        }, (r, _) => r.RowAsync("account", r.Id("account"))),

        // ---- every column type, auto-number, money and lookup on a custom table; image column ----
        new("CreateAllTypesAutoNumber", async r =>
        {
            await r.Kit.Admin.ExecuteAsync(new SetAutoNumberSeedRequest { EntityName = Types, AttributeName = "new_code", Value = 100 });
            return new CreateRequest { Target = await r.AllTypesAsync("crr all types") };
        }, (r, response) => r.RowAsync(Types, r.Created(response))),
        new("CreateImage", _ => Task.FromResult<OrganizationRequest>(new CreateRequest
        {
            Target = new Entity(Image) { ["new_name"] = "crr image", ["new_pic"] = RowKit.ImageBytes },
        }), async (r, response) =>
        {
            var id = r.Created(response);
            await r.RowAsync(Image, id);
            await r.FilesAsync(id);
        }),

        // ---- mail tables: a server profile and its secret; a queue and its mailbox; a queue item ----
        new("CreateMailProfile", async r =>
        {
            await r.Kit.ExecAsync("DELETE FROM data.emailserverprofile WHERE name = 'crr profile'");
            return new CreateRequest
            {
                Target = new Entity("emailserverprofile")
                {
                    ["name"] = "crr profile", ["servertype"] = new OptionSetValue(4), ["outgoingserverlocation"] = "smtp.example.com",
                    ["outgoingauthenticationprotocol"] = new OptionSetValue(3), ["outgoingcredentialretrieval"] = new OptionSetValue(1),
                    ["outgoingusername"] = "sender@example.com", ["outgoingpassword"] = "crr-secret",
                },
            };
        }, async (r, response) =>
        {
            var id = r.Created(response);
            await r.RowAsync("emailserverprofile", id);
            await r.RowsAsync("meta.mail_secret", "recordid", id, "name");
        }),
        new("CreateQueueWithMailbox", _ => Task.FromResult<OrganizationRequest>(new CreateRequest
        {
            Target = new Entity("queue")
            {
                ["name"] = "crr queue", ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1),
                ["emailaddress"] = "crr.queue@example.com",
            },
        }), async (r, response) =>
        {
            var id = r.Created(response);
            await r.RowAsync("queue", id);
            await r.RowsAsync("data.mailbox", "regardingobjectid", id);
        }),
        new("CreateQueueItem", async r =>
        {
            var email = await r.Kit.Admin.CreateAsync(new Entity("email") { ["subject"] = "crr queued email" });
            r.Name(email, "email");
            var queue = await r.Kit.QueueAsync();
            r.Name(queue, "queue");
            return new CreateRequest
            {
                Target = new Entity("queueitem") { ["objectid"] = new EntityReference("email", email), ["queueid"] = new EntityReference("queue", queue) },
            };
        }, (r, response) => r.RowAsync("queueitem", r.Created(response))),

        // ---- elastic table ----
        new("CreateElastic", async r =>
        {
            await r.Kit.EnsureElasticTableAsync();
            return new CreateRequest { Target = new Entity("new_crgelastic") { ["new_name"] = "crr elastic", ["partitionid"] = "crr-partition" } };
        }, (r, response) => r.RowAsync("new_crgelastic", r.Created(response))),

        // ---- duplicate detection: the match codes of a Create (the published rule on contact.emailaddress1) ----
        new("CreateContactMatchCodes", async r =>
        {
            await r.DuplicateRuleAsync();
            return new CreateRequest { Target = new Entity("contact") { ["lastname"] = "crr-match", ["emailaddress1"] = "Crr.Match@Example.com" } };
        }, async (r, response) =>
        {
            var id = r.Created(response);
            await r.RowAsync("contact", id);
            await r.MatchCodesAsync(id);
        }),

        // ---- overriddencreatedon, a migration scope ----
        new("CreateOverriddenCreatedOn", _ => Task.FromResult<OrganizationRequest>(new CreateRequest
        {
            Target = new Entity("account") { ["name"] = "crr imported", ["overriddencreatedon"] = Original },
        }), (r, response) => r.RowAsync("account", r.Created(response))),
        new("CreateInMigrationScope", async r =>
        {
            var root = await SecurityTestHelper.RootBusinessUnitAsync(r.Kit.ConnectionString);
            r.Name(root, "rootunit");
            var user = await SecurityTestHelper.CreateUserAsync(r.Kit.ConnectionString, r.Kit.Cache, root, userName: "crr migrated user");
            r.Name(user, "migrateduser");
            return new CreateRequest
            {
                Target = new Entity("account", r.NewId("migrated"))
                {
                    ["name"] = "crr migrated", ["createdon"] = Original, ["modifiedon"] = Original.AddDays(1),
                    ["createdby"] = new EntityReference("systemuser", user), ["modifiedby"] = new EntityReference("systemuser", user),
                    ["ownerid"] = new EntityReference("systemuser", user), ["owningbusinessunit"] = new EntityReference("businessunit", root),
                    ["statecode"] = new OptionSetValue(1), ["statuscode"] = new OptionSetValue(2),
                },
            };
        }, (r, _) => r.RowAsync("account", r.Id("migrated")), Migration: true),

        // ---- audit (new_crgaudited, auditing on; the organization's auditing on for the kit's lifetime) ----
        new("CreateAudited", async r =>
        {
            await r.Kit.AuditedRowAsync();
            return new CreateRequest { Target = new Entity("new_crgaudited") { ["new_name"] = "crr-audited" } };
        }, async (r, response) =>
        {
            var id = r.Created(response);
            await r.RowAsync("new_crgaudited", id);
            await r.AuditAsync(id);
        }),
        new("UpdateAudited", async r =>
        {
            var row = await r.AuditedRowAsync();
            return new UpdateRequest { Target = new Entity("new_crgaudited", row.Id) { ["new_name"] = "crr-audited-again" } };
        }, async (r, _) =>
        {
            await r.RowAsync("new_crgaudited", r.Id("audited"));
            await r.AuditAsync(r.Id("audited"));
        }),
        new("DeleteAudited", async r => new DeleteRequest { Target = await r.AuditedRowAsync() }, async (r, _) =>
        {
            await r.RowAsync("new_crgaudited", r.Id("audited"));
            await r.AuditAsync(r.Id("audited"));
        }),

        // ---- associate and disassociate: 1:N, N:N, a security relationship ----
        new("AssociateOneToMany", async r =>
        {
            var account = await r.AccountAsync("crr parent");
            var contact = await r.ContactAsync("crr-child");
            return new AssociateRequest
            {
                Target = new EntityReference("account", account), Relationship = new Relationship("contact_customer_accounts"),
                RelatedEntities = [new EntityReference("contact", contact)],
            };
        }, (r, _) => r.RowAsync("contact", r.Id("contact"))),
        new("DisassociateOneToMany", async r =>
        {
            var account = await r.AccountAsync("crr parent");
            var contact = await r.Kit.Admin.CreateAsync(new Entity("contact")
            {
                ["firstname"] = "Ada", ["lastname"] = "crr-child", ["parentcustomerid"] = new EntityReference("account", account),
            });
            r.Name(contact, "contact");
            return new DisassociateRequest
            {
                Target = new EntityReference("account", account), Relationship = new Relationship("contact_customer_accounts"),
                RelatedEntities = [new EntityReference("contact", contact)],
            };
        }, (r, _) => r.RowAsync("contact", r.Id("contact"))),
        new("AssociateManyToMany", async r =>
        {
            var (row, tag) = await r.RowAndTagAsync();
            return new AssociateRequest { Target = row, Relationship = new Relationship(RowKit.TagRelationship), RelatedEntities = [tag] };
        }, (r, _) => r.RowsAsync("data." + RowKit.TagRelationship, Types + "id", r.Id("types"))),
        new("DisassociateManyToMany", async r =>
        {
            var (row, tag) = await r.RowAndTagAsync();
            await r.Kit.Admin.ExecuteAsync(new AssociateRequest { Target = row, Relationship = new Relationship(RowKit.TagRelationship), RelatedEntities = [tag] });
            return new DisassociateRequest { Target = row, Relationship = new Relationship(RowKit.TagRelationship), RelatedEntities = [tag] };
        }, (r, _) => r.RowsAsync("data." + RowKit.TagRelationship, Types + "id", r.Id("types"))),
        new("AssociateRoleToUser", async r =>
        {
            var root = await SecurityTestHelper.RootBusinessUnitAsync(r.Kit.ConnectionString);
            var user = await SecurityTestHelper.CreateUserAsync(r.Kit.ConnectionString, r.Kit.Cache, root, userName: "crr role user");
            r.Name(user, "roleuser");
            var role = await SecurityTestHelper.CreateRoleAsync(r.Kit.ConnectionString, r.Kit.Cache, "crr role");
            r.Name(role, "role");
            return new AssociateRequest
            {
                Target = new EntityReference("systemuser", user), Relationship = new Relationship("systemuserroles_association"),
                RelatedEntities = [new EntityReference("role", role)],
            };
        }, (r, _) => r.RowsAsync("data.systemuserroles", "systemuserid", r.Id("roleuser"))),

        // ---- delete cascades ----
        new("DeleteAccountCascade", async r =>
        {
            var account = await r.AccountAsync("crr cascade");
            foreach (var label in new[] { "child1", "child2" })
                r.Name(await r.Kit.Admin.CreateAsync(new Entity("contact")
                {
                    ["lastname"] = "crr-" + label, ["parentcustomerid"] = new EntityReference("account", account),
                }), label);
            return new DeleteRequest { Target = new EntityReference("account", account) };
        }, async (r, _) =>
        {
            await r.RowAsync("account", r.Id("account"));
            await r.RowAsync("contact", r.Id("child1"));
            await r.RowAsync("contact", r.Id("child2"));
        }),
        new("DeleteEmailWithAttachments", async r =>
        {
            var contact = await r.ContactAsync("crr-party");
            var email = await r.Kit.Admin.CreateAsync(new Entity("email") { ["subject"] = "crr email", ["to"] = Parties(new EntityReference("contact", contact)) });
            r.Name(email, "email");
            foreach (var label in new[] { "attachment1", "attachment2" })
                r.Name(await r.Kit.Admin.CreateAsync(new Entity("activitymimeattachment")
                {
                    ["objectid"] = new EntityReference("email", email), ["subject"] = label, ["filename"] = label + ".txt",
                    ["body"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(label)),
                }), label);
            foreach (var label in new[] { "attachment1", "attachment2" })
                r.Name(await r.Kit.NameAsync(label + "-file", $"SELECT fileattachmentid FROM data.fileattachment WHERE objectid = '{r.Id(label)}'"), label + "-file");
            return new DeleteRequest { Target = new EntityReference("email", email) };
        }, async (r, _) =>
        {
            await r.ActivityAsync("email", r.Id("email"));
            await r.RowsAsync("data.activitymimeattachment", "objectid", r.Id("email"));
            // The bodies' fileattachment rows and blobs (the blobs go after the commit, through the host's deletion queue: drained first).
            await r.Kit.Fx.DrainFileDeletionsAsync();
            foreach (var label in new[] { "attachment1", "attachment2" })
            {
                await r.FilesAsync(r.Id(label));
                await r.BlobAsync(r.Id(label + "-file"));
            }
        }),

        // ---- faults (Review Focus 2): value types, a missing record, a duplicate id, a unique index, a duplicate found ----
        new("FaultLookupWrongType", _ => Task.FromResult<OrganizationRequest>(new CreateRequest
        {
            Target = new Entity("contact") { ["lastname"] = "crr-fault", ["parentcustomerid"] = "not a reference" },
        }), (r, _) => r.CountAsync("SELECT COUNT(*) FROM data.contact WHERE lastname = 'crr-fault'")),
        new("FaultChoiceWrongType", _ => Task.FromResult<OrganizationRequest>(new CreateRequest
        {
            Target = new Entity(Types) { ["new_name"] = "crr-fault", ["new_choice"] = 100000001 },
        }), (r, _) => r.CountAsync($"SELECT COUNT(*) FROM data.{Types} WHERE new_name = 'crr-fault'")),
        new("FaultMoneyWrongType", _ => Task.FromResult<OrganizationRequest>(new CreateRequest
        {
            Target = new Entity(Types) { ["new_name"] = "crr-fault-money", ["new_amount"] = 12.5m },
        }), (r, _) => r.CountAsync($"SELECT COUNT(*) FROM data.{Types} WHERE new_name = 'crr-fault-money'")),
        new("FaultPartyListWrongType", async r =>
        {
            var contact = await r.ContactAsync("crr-party");
            return new CreateRequest { Target = new Entity("email") { ["subject"] = "crr-fault", ["to"] = new EntityReference("contact", contact) } };
        }, (r, _) => r.CountAsync("SELECT COUNT(*) FROM data.email WHERE subject = 'crr-fault'")),
        new("FaultMissingReference", _ => Task.FromResult<OrganizationRequest>(new CreateRequest
        {
            Target = new Entity("contact") { ["lastname"] = "crr-fault", ["parentcustomerid"] = new EntityReference("account", CoreScenarioKit.UnknownId) },
        }), (r, _) => r.CountAsync("SELECT COUNT(*) FROM data.contact WHERE lastname = 'crr-fault'")),
        new("FaultMissingParty", _ => Task.FromResult<OrganizationRequest>(new CreateRequest
        {
            Target = new Entity("email") { ["subject"] = "crr-fault", ["to"] = Parties(new EntityReference("contact", CoreScenarioKit.UnknownId)) },
        }), (r, _) => r.CountAsync("SELECT COUNT(*) FROM data.email WHERE subject = 'crr-fault'")),
        new("FaultDuplicateId", async r =>
        {
            var account = await r.AccountAsync("crr duplicate id");
            return new CreateRequest { Target = new Entity("account", account) { ["name"] = "crr-fault" } };
        }, (r, _) => r.RowAsync("account", r.Id("account"))),
        new("FaultUniqueIndex", async r =>
        {
            await r.Kit.ExecAsync($"DELETE FROM data.{Key} WHERE new_name = 'crr-unique'");
            r.Name(await r.Kit.Admin.CreateAsync(new Entity(Key) { ["new_name"] = "crr-unique" }), "first");
            return new CreateRequest { Target = new Entity(Key) { ["new_name"] = "CRR-UNIQUE" } };
        }, (r, _) => r.CountAsync($"SELECT COUNT(*) FROM data.{Key} WHERE new_name = 'crr-unique'")),
        new("FaultDuplicateFound", async r =>
        {
            await r.DuplicateRuleAsync();
            await r.Kit.ExecAsync("DELETE FROM data.contact WHERE lastname = 'crr-duplicate'");
            r.Name(await r.Kit.Admin.CreateAsync(new Entity("contact") { ["lastname"] = "crr-duplicate", ["emailaddress1"] = "crr.duplicate@example.com" }), "first");
            return new CreateRequest
            {
                Target = new Entity("contact") { ["lastname"] = "crr-duplicate", ["emailaddress1"] = "crr.duplicate@example.com" },
                ["SuppressDuplicateDetection"] = false,
            };
        }, (r, _) => r.CountAsync("SELECT COUNT(*) FROM data.contact WHERE lastname = 'crr-duplicate'")),
    ];

    public static IReadOnlyList<ReadScenario> Reads { get; } =
    [
        new("ReadRetrieveAllTypes", async r =>
        {
            await r.SeedCodeAsync(107);
            var id = await r.AllTypesRowAsync("crr read types");
            var columns = RowKit.AllTypesColumns;
            return (new RetrieveRequest { Target = new EntityReference(Types, id), ColumnSet = new ColumnSet(columns) }, r.RetrieveModel(Types, id, columns));
        }),
        new("ReadMultipleAllTypes", async r =>
        {
            await r.SeedCodeAsync(108);
            await r.AllTypesRowAsync("crr read types");
            var query = new QueryExpression(Types)
            {
                ColumnSet = new ColumnSet(RowKit.AllTypesColumns),
                Criteria = { Conditions = { new ConditionExpression("new_name", ConditionOperator.Equal, "crr read types") } },
            };
            return (new RetrieveMultipleRequest { Query = query }, CoreOperations.ReadQuery(query));
        }),
        new("ReadLinkEntityAliased", async r =>
        {
            await r.Kit.ExecAsync("DELETE FROM data.contact WHERE lastname = 'crr-read-link'");
            var account = await r.Kit.Admin.CreateAsync(new Entity("account") { ["name"] = "crr read link", ["telephone1"] = "555 0100", ["accountnumber"] = "CRR-7" });
            r.Name(account, "account");
            r.Name(await r.Kit.Admin.CreateAsync(new Entity("contact")
            {
                ["firstname"] = "Ada", ["lastname"] = "crr-read-link", ["parentcustomerid"] = new EntityReference("account", account),
            }), "contact");
            var query = new QueryExpression("contact")
            {
                ColumnSet = new ColumnSet("fullname", "parentcustomerid"),
                Criteria = { Conditions = { new ConditionExpression("lastname", ConditionOperator.Equal, "crr-read-link") } },
                LinkEntities =
                {
                    new LinkEntity("contact", "account", "parentcustomerid", "accountid", JoinOperator.Inner)
                    {
                        EntityAlias = "acct", Columns = new ColumnSet("name", "telephone1", "accountnumber", "statecode", "ownerid"),
                    },
                },
            };
            return (new RetrieveMultipleRequest { Query = query }, CoreOperations.ReadQuery(query));
        }),
        new("ReadAggregate", async r =>
        {
            await r.Kit.ExecAsync($"DELETE FROM data.{Types} WHERE new_name LIKE 'crr-agg-%'");
            foreach (var (i, choice) in new[] { (1, 100000000), (2, 100000001), (3, 100000000) })
                await r.Kit.Admin.CreateAsync(new Entity(Types)
                {
                    ["new_name"] = "crr-agg-" + i, ["new_int"] = i * 10, ["new_amount"] = new Money(i * 1.5m), ["new_choice"] = new OptionSetValue(choice),
                });
            const string fetch = $"""
                <fetch aggregate='true'><entity name='{Types}'>
                <attribute name='new_choice' alias='choice' groupby='true' />
                <attribute name='new_int' alias='total' aggregate='sum' />
                <attribute name='new_amount' alias='amount' aggregate='sum' />
                <attribute name='new_name' alias='n' aggregate='count' />
                <order alias='choice' />
                <filter><condition attribute='new_name' operator='like' value='crr-agg-%' /></filter>
                </entity></fetch>
                """;
            return (new RetrieveMultipleRequest { Query = new FetchExpression(fetch) }, CoreOperations.ReadQuery(new FetchExpression(fetch)));
        }),
        new("ReadActivityParties", async r =>
        {
            var contact = await r.ContactAsync("crr-party");
            var email = await r.Kit.Admin.CreateAsync(new Entity("email")
            {
                ["subject"] = "crr read email",
                ["from"] = Parties(new EntityReference("systemuser", r.Kit.Caller.UserId)),
                ["to"] = Parties(new EntityReference("contact", contact)),
                ["cc"] = new EntityCollection([new Entity("activityparty") { ["addressused"] = "crr.unresolved@example.com" }]),
            });
            r.Name(email, "email");
            string[] columns = ["subject", "from", "to", "cc", "statecode", "activitytypecode"];
            return (new RetrieveRequest { Target = new EntityReference("email", email), ColumnSet = new ColumnSet(columns) }, r.RetrieveModel("email", email, columns));
        }),
        new("ReadNoteBody", async r =>
        {
            var account = await r.AccountAsync("crr note account");
            var note = await r.Kit.Admin.CreateAsync(new Entity("annotation")
            {
                ["subject"] = "crr note", ["filename"] = "crr.txt", ["mimetype"] = "text/plain",
                ["documentbody"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("crr note body")), ["objectid"] = new EntityReference("account", account),
            });
            r.Name(note, "note");
            string[] columns = ["subject", "filename", "mimetype", "filesize", "documentbody", "objectid", "isdocument"];
            return (new RetrieveRequest { Target = new EntityReference("annotation", note), ColumnSet = new ColumnSet(columns) }, r.RetrieveModel("annotation", note, columns));
        }),
        // The administrator's date format and time zone (W. Europe, yyyy-MM-dd) for the kit's lifetime: the User Local column is
        // formatted in that zone, the Date Only one as it is.
        new("ReadUserDateFormat", async r =>
        {
            var id = await r.AllTypesRowAsync("crr read dates");
            await r.UserDateFormatAsync();
            string[] columns = ["new_name", "new_when", "new_day"];
            return (new RetrieveRequest { Target = new EntityReference(Types, id), ColumnSet = new ColumnSet(columns) }, r.RetrieveModel(Types, id, columns));
        }),
    ];

    private static EntityCollection Parties(params EntityReference[] parties) =>
        new([.. parties.Select(p => new Entity("activityparty") { ["partyid"] = p })]);
}

/// <summary>
/// What the row scenarios need on top of <see cref="CoreScenarioKit"/>: their own tables (fixed names, created once), the rows they
/// arrange, and the dump of stored rows read with plain SQL.
/// </summary>
internal sealed class RowKit(CoreScenarioKit kit)
{
    public const string Directory = "core-row-golden";
    public const string TypesTable = "new_crrtypes";
    public const string TagTable = "new_crrtag";
    public const string ImageTable = "new_crrimage";
    public const string KeyTable = "new_crrkey";
    public const string TagRelationship = "new_crrtypes_new_crrtag";

    /// <summary>The columns of <see cref="TypesTable"/> a read selects.</summary>
    public static readonly string[] AllTypesColumns =
    [
        "new_name", "new_memo", "new_int", "new_big", "new_decimal", "new_double", "new_amount", "new_amount_base", "transactioncurrencyid",
        "exchangerate", "new_flag", "new_when", "new_day", "new_choice", "new_choices", "new_accountid", "new_code", "ownerid", "statecode",
        "statuscode",
    ];

    /// <summary>
    /// The columns whose value changes with every call although it is no id, date or row version: written as <c>{volatile}</c> when
    /// not null (see the README).
    /// </summary>
    private static readonly HashSet<string> VolatileColumns = new(StringComparer.Ordinal)
    {
        "new_pic_timestamp",
    };

    /// <summary>The image columns: their stored value is a thumbnail the server made (see <see cref="Thumbnail"/>).</summary>
    private static readonly HashSet<string> ThumbnailColumns = new(StringComparer.Ordinal) { "new_pic" };

    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static readonly DateTime When = new(2015, 3, 4, 23, 30, 0, DateTimeKind.Utc);

    private readonly StringBuilder _text = new("--- rows ---\n");
    private readonly Dictionary<string, Guid> _ids = [];

    public CoreScenarioKit Kit => kit;

    public string Text => _text.ToString();

    // ---- names ----

    /// <summary>Names <paramref name="id"/> <c>{a:label}</c> and keeps it under <paramref name="label"/>.</summary>
    public void Name(Guid id, string label)
    {
        _ids[label] = id;
        kit.Name(id, label);
    }

    public Guid Id(string label) => _ids[label];

    /// <summary>A new id named <paramref name="label"/>.</summary>
    public Guid NewId(string label)
    {
        var id = Guid.NewGuid();
        Name(id, label);
        return id;
    }

    /// <summary>The id a Create answered, named <c>{a:created}</c>.</summary>
    public Guid Created(OrganizationResponse? response)
    {
        response.Should().NotBeNull("the Create succeeded");
        var id = (Guid)response!.Results["id"];
        Name(id, "created");
        return id;
    }

    // ---- arranged rows ----

    /// <summary>A row of <c>new_crgaudited</c> created and updated once with auditing on (<see cref="CoreScenarioKit.AuditedRowAsync"/>), <c>{a:audited}</c>.</summary>
    public async Task<EntityReference> AuditedRowAsync()
    {
        var row = (await kit.AuditedRowAsync()).Row;
        Name(row.Id, "audited");
        return row;
    }

    public async Task<Guid> AccountAsync(string name)
    {
        var id = await kit.Admin.CreateAsync(new Entity("account") { ["name"] = name });
        Name(id, "account");
        return id;
    }

    public async Task<Guid> ContactAsync(string lastName)
    {
        var id = await kit.Admin.CreateAsync(new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = lastName });
        Name(id, "contact");
        return id;
    }

    /// <summary>A user in a new child business unit of the root (<c>{a:childunit}</c>, <c>{a:assignee}</c>) who may read accounts.</summary>
    public async Task<Guid> ChildUnitUserAsync()
    {
        var root = await SecurityTestHelper.RootBusinessUnitAsync(kit.ConnectionString);
        var unit = await SecurityTestHelper.CreateBusinessUnitAsync(kit.ConnectionString, kit.Cache, root, "crr unit");
        Name(unit, "childunit");
        var user = await SecurityTestHelper.CreateUserAsync(kit.ConnectionString, kit.Cache, unit, userName: "crr assignee");
        Name(user, "assignee");
        await SecurityTestHelper.AssignRoleAsync(kit.ConnectionString, kit.Cache, user,
            await SecurityTestHelper.CreateRoleAsync(kit.ConnectionString, kit.Cache, "crr assignee role", ("prvReadAccount", DepthMask.Global)));
        return user;
    }

    /// <summary>A value in every column of <see cref="TypesTable"/> (the lookup to a new account <c>{a:account}</c>).</summary>
    public async Task<Entity> AllTypesAsync(string name) => new(TypesTable)
    {
        ["new_name"] = name, ["new_memo"] = "line one\nline two", ["new_int"] = 42, ["new_big"] = 9_000_000_000L, ["new_decimal"] = 12.34m,
        ["new_double"] = 0.125d, ["new_amount"] = new Money(1234.5m), ["new_flag"] = true, ["new_when"] = When,
        ["new_day"] = new DateTime(2015, 3, 4, 0, 0, 0, DateTimeKind.Unspecified), ["new_choice"] = new OptionSetValue(100000001),
        ["new_choices"] = new OptionSetValueCollection([new OptionSetValue(3), new OptionSetValue(1)]),
        ["new_accountid"] = new EntityReference("account", await AccountAsync("crr types account")),
    };

    /// <summary>
    /// Sets the next <c>new_code</c> of <see cref="TypesTable"/> to <paramref name="next"/>, so that a scenario whose golden shows an
    /// auto-number does not depend on the scenarios that ran before it.
    /// </summary>
    public Task SeedCodeAsync(long next) =>
        kit.Admin.ExecuteAsync(new SetAutoNumberSeedRequest { EntityName = TypesTable, AttributeName = "new_code", Value = next });

    /// <summary>A row of <see cref="TypesTable"/> with every column (earlier rows of <paramref name="name"/> deleted), named <c>{a:types}</c>.</summary>
    public async Task<Guid> AllTypesRowAsync(string name)
    {
        await kit.ExecAsync($"DELETE FROM data.{TypesTable} WHERE new_name = '{name}'");
        var id = await kit.Admin.CreateAsync(await AllTypesAsync(name));
        Name(id, "types");
        return id;
    }

    /// <summary>A row of <see cref="TypesTable"/> (<c>{a:types}</c>) and one of <see cref="TagTable"/> (<c>{a:tag}</c>).</summary>
    public async Task<(EntityReference Row, EntityReference Tag)> RowAndTagAsync()
    {
        var row = await kit.Admin.CreateAsync(new Entity(TypesTable) { ["new_name"] = "crr nn" });
        Name(row, "types");
        var tag = await kit.Admin.CreateAsync(new Entity(TagTable) { ["new_name"] = "crr tag" });
        Name(tag, "tag");
        return (new EntityReference(TypesTable, row), new EntityReference(TagTable, tag));
    }

    /// <summary>
    /// Duplicate detection on contact for the kit's lifetime (the organization's switches on, Create/Update included) with a published
    /// rule on <c>emailaddress1</c>, named <c>{a:rule}</c>.
    /// </summary>
    public async Task DuplicateRuleAsync()
    {
        var duplicates = new DuplicateDetectionTestKit(kit.Fx);
        await duplicates.InitializeAsync(onlineCreateUpdate: true, DuplicateDetectionTestKit.Contact);
        kit.OnCleanup(async () =>
        {
            await duplicates.DisposeAsync();
            await kit.ExecAsync("DELETE FROM data.asyncoperation WHERE statecode = 0");
        });
        var rule = await duplicates.RuleAsync();
        Name(rule, "rule");
        await duplicates.PublishAndRunAsync(rule);
    }

    /// <summary>The administrator's time zone (110, W. Europe) and date format (<c>yyyy-MM-dd HH:mm</c>) until the kit is disposed.</summary>
    public async Task UserDateFormatAsync()
    {
        const string columns = "timezonecode, dateformatstring, dateseparator, timeformatstring";
        var before = new List<object?>();
        await using (var conn = await TestProvider.OpenAsync(kit.ConnectionString))
        await using (var cmd = TestSql.Command(conn, $"SELECT {columns} FROM data.usersettings WHERE systemuserid = @id"))
        {
            cmd.AddParameter("@id", DbType.Guid, kit.Caller.UserId);
            await using var rd = await cmd.ExecuteReaderAsync();
            (await rd.ReadAsync()).Should().BeTrue("the administrator has a usersettings row");
            for (var i = 0; i < rd.FieldCount; i++) before.Add(rd.IsDBNull(i) ? null : rd.GetValue(i));
        }
        kit.OnCleanup(async () =>
        {
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            await using var cmd = TestSql.Command(conn,
                "UPDATE data.usersettings SET timezonecode = @tz, dateformatstring = @df, dateseparator = @ds, timeformatstring = @tf WHERE systemuserid = @id");
            cmd.AddParameter("@tz", DbType.Int32, before[0]);
            cmd.AddParameter("@df", DbType.String, before[1]);
            cmd.AddParameter("@ds", DbType.String, before[2]);
            cmd.AddParameter("@tf", DbType.String, before[3]);
            cmd.AddParameter("@id", DbType.Guid, kit.Caller.UserId);
            await cmd.ExecuteNonQueryAsync();
        });
        await kit.Admin.UpdateAsync(new Entity("usersettings", kit.Caller.UserId)
        {
            ["timezonecode"] = 110, ["dateformatstring"] = "yyyy-MM-dd", ["dateseparator"] = "-", ["timeformatstring"] = "HH:mm",
        });
    }

    /// <summary>
    /// The image the image scenario writes: a 300 x 200 PNG, red with a blue top-right quarter, as fixed bytes (written once by SkiaSharp
    /// 4.154.0), so that the request does not depend on an encoder. The thumbnail the server makes from it is written as decoded facts
    /// (<see cref="Thumbnail"/>), not as bytes.
    /// </summary>
    public static readonly byte[] ImageBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAASwAAADICAYAAABS39xVAAAABHNCSVQICAgIfAhkiAAAArVJREFUeJzt1sEJwDAMBEE5/ffstOBHglmYqeAeYtHaM3vgY8tZ" +
        "8YPn9gCAU4IFZAgWkCFYQIZgARmCBWQIFpAhWECGYAEZggVkCBaQIVhAhmABGYIFZAgWkCFYQIZgARmCBWQIFpAhWECGYAEZggVkCBaQIVhAhmABGYIFZAgW" +
        "kCFYQIZgARmCBWQIFpAhWECGYAEZggVkCBaQIVhAhmABGYIFZAgWkCFYQIZgARmCBWQIFpAhWECGYAEZggVkCBaQIVhAhmABGYIFZAgWkCFYQIZgARmCBWQI" +
        "FpAhWECGYAEZggVkCBaQIVhAhmABGYIFZAgWkCFYQIZgARmCBWQIFpAhWECGYAEZggVkCBaQIVhAhmABGYIFZAgWkCFYQIZgARmCBWQIFpAhWECGYAEZggVk" +
        "CBaQIVhAhmABGYIFZAgWkCFYQIZgARmCBWQIFpAhWECGYAEZggVkCBaQIVhAhmABGYIFZAgWkLH2zL49AuCEDwvIECwgQ7CADMECMgQLyBAsIEOwgAzBAjIE" +
        "C8gQLCBDsIAMwQIyBAvIECwgQ7CADMECMgQLyBAsIEOwgAzBAjIEC8gQLCBDsIAMwQIyBAvIECwgQ7CADMECMgQLyBAsIEOwgAzBAjIEC8gQLCBDsIAMwQIy" +
        "BAvIECwgQ7CADMECMgQLyBAsIEOwgAzBAjIEC8gQLCBDsIAMwQIyBAvIECwgQ7CADMECMgQLyBAsIEOwgAzBAjIEC8gQLCBDsIAMwQIyBAvIECwgQ7CADMEC" +
        "MgQLyBAsIEOwgAzBAjIEC8gQLCBDsIAMwQIyBAvIECwgQ7CADMECMgQLyBAsIEOwgAzBAjIEC8gQLCBDsIAMwQIyBAvIECwgQ7CADMECMgQLyBAsIEOwgAzB" +
        "AjIEC8gQLCBDsIAMwQIyBAvIECwgQ7CAjBefzQaLtkXmZwAAAABJRU5ErkJggg==");

    /// <summary>
    /// A stored thumbnail as its format, size and the colours of four pixels well inside its quarters (as ThumbnailerTests reads them):
    /// the resampling near the colour edge differs with the CPU's raster path, so its bytes are not pinned.
    /// </summary>
    private static string Thumbnail(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        if (codec is null) return string.Create(CultureInfo.InvariantCulture, $"Thumbnail:not an image ({bytes.Length} bytes)");
        using var bitmap = SKBitmap.Decode(bytes);
        var pixels = new[] { (20, 20), (124, 20), (20, 124), (124, 124) }
            .Select(p => p.Item1 < bitmap.Width && p.Item2 < bitmap.Height ? $"({p.Item1},{p.Item2})={bitmap.GetPixel(p.Item1, p.Item2)}" : $"({p.Item1},{p.Item2})=outside");
        return string.Create(CultureInfo.InvariantCulture, $"Thumbnail:{codec.EncodedFormat} {bitmap.Width}x{bitmap.Height} {string.Join(" ", pixels)}");
    }

    /// <summary>The query <c>CoreOperations.RetrieveAsync</c> runs for a Retrieve of <paramref name="columns"/> of one record.</summary>
    public QueryModel RetrieveModel(string table, Guid id, IEnumerable<string> columns)
    {
        var root = new EntityNode { EntityName = table };
        foreach (var column in columns) root.Columns.Add(new ColumnNode(column));
        root.Filter.Conditions.Add(new ConditionNode(null, SystemAttributes.PrimaryIdName(kit.Store.GetEntity(table)!), FilterOperator.Equal, [id]));
        return new QueryModel { Root = root, Top = 1 };
    }

    // ---- the migration path ----

    /// <summary>The Create through the core (as the migration import writes), in one committed transaction inside a migration scope.</summary>
    public async Task<(OrganizationResponse? Response, Exception? Fault)> CreateInMigrationScopeAsync(CreateRequest request)
    {
        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            Guid id;
            using (MigrationScope.Enter())
                id = await core.CreateAsync(new DataSession(conn, tx), kit.Caller, request.Target, default);
            await tx.CommitAsync();
            return (new CreateResponse { Results = { ["id"] = id } }, null);
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

    // ---- tables ----

    /// <summary>
    /// The tables of these goldens, created once (only they use them): <see cref="TypesTable"/> (user-owned, a column of every type,
    /// money, an auto-number column and a lookup to account), <see cref="TagTable"/> and the N:N <see cref="TagRelationship"/> to it,
    /// <see cref="ImageTable"/> (an image column) and <see cref="KeyTable"/> (organization-owned, a unique index on its name).
    /// </summary>
    public async Task EnsureTablesAsync()
    {
        await Lock.WaitAsync();
        try
        {
            var store = kit.Store;
            var admin = kit.Admin;
            if (store.GetEntity(TypesTable) is null)
            {
                await admin.ExecuteAsync(new CreateEntityRequest { Entity = TestHelpers.Entity(TypesTable, TypesTable, "Row golden types"), PrimaryAttribute = TextColumn("new_name") });
                AttributeMetadata[] columns =
                [
                    new MemoAttributeMetadata { MaxLength = 2000 },
                    new IntegerAttributeMetadata { MinValue = -1000000, MaxValue = 1000000 },
                    new BigIntAttributeMetadata(),
                    new DecimalAttributeMetadata { Precision = 2, MinValue = -1000000, MaxValue = 1000000 },
                    new DoubleAttributeMetadata { Precision = 3, MinValue = -1000000, MaxValue = 1000000 },
                    new MoneyAttributeMetadata { Precision = 2, MinValue = 0, MaxValue = 1000000 },
                    new BooleanAttributeMetadata
                    {
                        OptionSet = new BooleanOptionSetMetadata(new OptionMetadata(TestHelpers.Label("Yes"), 1), new OptionMetadata(TestHelpers.Label("No"), 0)),
                    },
                    new DateTimeAttributeMetadata { Format = DateTimeFormat.DateAndTime, DateTimeBehavior = DateTimeBehavior.UserLocal },
                    new DateTimeAttributeMetadata { Format = DateTimeFormat.DateOnly, DateTimeBehavior = DateTimeBehavior.DateOnly },
                    new PicklistAttributeMetadata
                    {
                        OptionSet = new OptionSetMetadata([new OptionMetadata(TestHelpers.Label("Alpha"), 100000000), new OptionMetadata(TestHelpers.Label("Beta"), 100000001)])
                        {
                            IsGlobal = false, OptionSetType = OptionSetType.Picklist,
                        },
                    },
                    new MultiSelectPicklistAttributeMetadata
                    {
                        OptionSet = new OptionSetMetadata([new OptionMetadata(TestHelpers.Label("One"), 1), new OptionMetadata(TestHelpers.Label("Two"), 2), new OptionMetadata(TestHelpers.Label("Three"), 3)])
                        {
                            IsGlobal = false, OptionSetType = OptionSetType.Picklist,
                        },
                    },
                    TextColumn("new_code", "CRR-{SEQNUM:4}"),
                ];
                string[] names = ["new_memo", "new_int", "new_big", "new_decimal", "new_double", "new_amount", "new_flag", "new_when", "new_day", "new_choice", "new_choices", "new_code"];
                for (var i = 0; i < columns.Length; i++)
                {
                    columns[i].LogicalName = names[i];
                    columns[i].SchemaName = names[i];
                    columns[i].DisplayName = TestHelpers.Label(names[i]);
                    columns[i].RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None);
                    await admin.ExecuteAsync(new CreateAttributeRequest { EntityName = TypesTable, Attribute = columns[i] });
                }
                await admin.ExecuteAsync(new CreateOneToManyRequest
                {
                    OneToManyRelationship = new OneToManyRelationshipMetadata
                    {
                        SchemaName = "new_account_new_crrtypes", ReferencedEntity = "account", ReferencedAttribute = "accountid", ReferencingEntity = TypesTable,
                    },
                    Lookup = new LookupAttributeMetadata
                    {
                        LogicalName = "new_accountid", SchemaName = "new_AccountId", DisplayName = TestHelpers.Label("Account"),
                        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
                    },
                });
            }

            if (store.GetEntity(TagTable) is null)
                await admin.ExecuteAsync(new CreateEntityRequest { Entity = TestHelpers.Entity(TagTable, TagTable, "Row golden tags"), PrimaryAttribute = TextColumn("new_name") });
            if (store.GetRelationship(TagRelationship) is null)
                await TestEntities.InSessionAsync(kit.ConnectionString, s => store.CreateManyToManyAsync(s, new ManyToManyRelationshipMetadata
                {
                    SchemaName = TagRelationship, Entity1LogicalName = TypesTable, Entity2LogicalName = TagTable, IntersectEntityName = TagRelationship,
                }, default));

            if (store.GetEntity(ImageTable) is null)
            {
                await admin.ExecuteAsync(new CreateEntityRequest { Entity = TestHelpers.Entity(ImageTable, ImageTable, "Row golden images"), PrimaryAttribute = TextColumn("new_name") });
                await admin.ExecuteAsync(new CreateAttributeRequest
                {
                    EntityName = ImageTable, Attribute = new ImageAttributeMetadata { LogicalName = "new_pic", SchemaName = "new_pic", DisplayName = TestHelpers.Label("Picture") },
                });
            }

            if (store.GetEntity(KeyTable) is null)
            {
                var entity = TestHelpers.Entity(KeyTable, KeyTable, "Row golden keys");
                entity.OwnershipType = OwnershipTypes.OrganizationOwned;
                await admin.ExecuteAsync(new CreateEntityRequest { Entity = entity, PrimaryAttribute = TextColumn("new_name") });
                await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
                var d = SqlDialects.For(conn);
                await using var cmd = conn.Command(d.CreateIndexIfMissing("data", KeyTable, "UX_new_crrkey_name", DdlGenerator.KeyColumns(d, [("new_name", true)]), unique: true));
                await cmd.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            Lock.Release();
        }

        Name((Guid)(await kit.ScalarAsync("SELECT basecurrencyid FROM data.organization"))!, "basecurrency");
    }

    private static StringAttributeMetadata TextColumn(string name, string? autoNumber = null) => new()
    {
        LogicalName = name, SchemaName = name == "new_name" ? "new_Name" : name, MaxLength = 100, DisplayName = TestHelpers.Label(name),
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None), AutoNumberFormat = autoNumber,
    };

    // ---- the dump of stored rows ----

    /// <summary>The row of <paramref name="table"/> whose primary key is <paramref name="id"/>.</summary>
    public Task RowAsync(string table, Guid id) => RowsAsync("data." + table, SystemAttributes.PrimaryIdName(kit.Store.GetEntity(table)!), id);

    /// <summary>An activity's row, its <c>activitypointer</c> row and its <c>activityparty</c> rows.</summary>
    public async Task ActivityAsync(string table, Guid id)
    {
        await RowAsync(table, id);
        await RowsAsync("data.activitypointer", "activityid", id);
        await RowsAsync("data.activityparty", "activityid", id);
    }

    /// <summary>Whether the file store still holds the blob of the fileattachment <paramref name="fileId"/>, and its length.</summary>
    public async Task BlobAsync(Guid fileId)
    {
        var length = await kit.Services.GetRequiredService<Xrmd.Files.IFileStore>()
            .GetLengthAsync($"{TestProvider.DatabaseOf(kit.ConnectionString)}/{fileId:D}", default);
        _text.Append(CultureInfo.InvariantCulture, $"file store blob {fileId:D}: {(length is { } l ? "length " + l : "none")}\n");
    }

    /// <summary>
    /// The <c>fileattachment</c> rows of a record (a note body, an attachment body, an image) and the bytes of each in the file store
    /// (<c>{database}/{fileattachmentid}</c>), as a <c>byte[]</c> is written.
    /// </summary>
    public async Task FilesAsync(Guid id)
    {
        await RowsAsync("data.fileattachment", "objectid", id);
        var files = new List<Guid>();
        await using (var conn = await TestProvider.OpenAsync(kit.ConnectionString))
        await using (var cmd = TestSql.Command(conn, "SELECT fileattachmentid FROM data.fileattachment WHERE objectid = @id"))
        {
            cmd.AddParameter("@id", DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) files.Add(rd.GetGuid(0));
        }
        var store = kit.Services.GetRequiredService<Xrmd.Files.IFileStore>();
        var blobs = new List<string>();
        foreach (var file in files)
        {
            await using var stream = await store.OpenReadAsync($"{TestProvider.DatabaseOf(kit.ConnectionString)}/{file:D}", 0, null, default);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            blobs.Add(Value("blob", copy.ToArray()));
        }
        _text.Append("file store blobs of the fileattachment rows: ").Append(blobs.Count).Append('\n');
        foreach (var blob in blobs.Order(StringComparer.Ordinal)) _text.Append("  ").Append(blob).Append('\n');
    }

    /// <summary>The audit rows of a record (<c>changedata</c> verbatim).</summary>
    public Task AuditAsync(Guid id) => RowsAsync("data.audit", "objectid", id);

    /// <summary>The record's row in the match code table of the rule <c>{a:rule}</c>.</summary>
    public Task MatchCodesAsync(Guid id) =>
        RowsAsync("meta." + DuplicateDetectionSchema.MatchCodeTableName(Id("rule")), "objectid", id, label: "meta.matchcode_{rule}");

    public async Task CountAsync(string sql) =>
        _text.Append(sql).Append(": ").Append(Convert.ToInt64(await kit.ScalarAsync(sql), CultureInfo.InvariantCulture)).Append('\n');

    /// <summary>
    /// Every row of <paramref name="table"/> (schema-qualified) where <paramref name="column"/> is <paramref name="id"/>, every column in
    /// name order, values as <c>Type:value</c>. Rows are ordered by <paramref name="orderBy"/> when given, else by their text with the
    /// ids named so far and every other id as <c>?</c> (an order that does not depend on the ids the database gave them). The columns of
    /// custom attributes other tests may add to a shared table (any table but the <c>new_crr</c>/<c>new_crg</c> ones of the goldens) are
    /// left out, so that the file does not depend on which tests ran first.
    /// </summary>
    /// <param name="label">The table as the file names it (a table whose name holds an id).</param>
    public async Task RowsAsync(string table, string column, Guid id, string? orderBy = null, string? label = null)
    {
        var rows = new List<(string Key, string Text)>();
        var logicalName = table.StartsWith("data.", StringComparison.Ordinal) ? table["data.".Length..] : null;
        var skip = SkippedPrefixes(logicalName);
        await using (var conn = await TestProvider.OpenAsync(kit.ConnectionString))
        await using (var cmd = TestSql.Command(conn, $"SELECT * FROM {table} WHERE {column} = @id"))
        {
            cmd.AddParameter("@id", DbType.Guid, id);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var line = new StringBuilder();
                var columns = Enumerable.Range(0, rd.FieldCount).Select(i => (Name: rd.GetName(i).ToLowerInvariant(), Ordinal: i))
                    .Where(c => !skip.Any(p => c.Name.StartsWith(p, StringComparison.Ordinal)))
                    .OrderBy(c => c.Name, StringComparer.Ordinal);
                foreach (var (name, ordinal) in columns)
                    line.Append("    ").Append(name).Append(": ").Append(Value(name, rd.IsDBNull(ordinal) ? null : rd.GetValue(ordinal))).Append('\n');
                var key = orderBy is null ? kit.Mask(line.ToString()) : Convert.ToString(rd[orderBy], CultureInfo.InvariantCulture) ?? "";
                rows.Add((key, line.ToString()));
            }
        }

        _text.Append(CultureInfo.InvariantCulture, $"{label ?? table} where {column} = {id:D}: {rows.Count} row(s)\n");
        var n = 0;
        foreach (var (_, text) in rows.OrderBy(r => r.Key, StringComparer.Ordinal))
            _text.Append(CultureInfo.InvariantCulture, $"  [{n++}]\n").Append(text);
    }

    /// <summary>The custom attributes of a shared table: their columns (and companions, such as <c>_base</c> or <c>type</c>) are left out.</summary>
    private List<string> SkippedPrefixes(string? logicalName)
    {
        if (logicalName is null || logicalName.StartsWith("new_crr", StringComparison.Ordinal) || logicalName.StartsWith("new_crg", StringComparison.Ordinal)
            || kit.Store.GetEntity(logicalName) is not { } m)
            return [];
        return [.. (m.Attributes ?? []).Where(a => a.LogicalName is not null && MetadataStore.IsCustomAttribute(m, a)).Select(a => a.LogicalName)];
    }

    private static string Value(string column, object? v) => v switch
    {
        null => "null",
        _ when VolatileColumns.Contains(column) => "{volatile}",
        _ when column == "versionnumber" => "{rowversion}",
        byte[] bytes when ThumbnailColumns.Contains(column) => Thumbnail(bytes),
        // The object type code of a custom table depends on how many tables the run created before.
        int code when code >= 10000 && (column.EndsWith("type", StringComparison.Ordinal) || column.EndsWith("typecode", StringComparison.Ordinal)) => "Int32:{otc}",
        string s => "String:" + CoreDump.Quote(s),
        Guid g => "Guid:" + g.ToString("D"),
        DateTime d when d.Year < CoreDump.FixedDateYear => "DateTime:" + d.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTime => "DateTime:{date}",
        DateTimeOffset => "DateTimeOffset:{date}",
        bool b => "Boolean:" + (b ? "True" : "False"),
        byte[] bytes when bytes.Length <= 64 => "Byte[]:" + Convert.ToHexString(bytes),
        byte[] bytes => string.Create(CultureInfo.InvariantCulture, $"Byte[]:[{bytes.Length}] sha256:{Convert.ToHexString(SHA256.HashData(bytes))}"),
        IFormattable f => v.GetType().Name + ":" + f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.GetType().Name + ":" + v,
    };
}
