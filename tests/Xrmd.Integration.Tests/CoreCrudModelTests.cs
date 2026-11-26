using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Admin.Migration.Import;
using Xrmd.Core;
using Xrmd.Core.Elastic;
using Xrmd.Core.Errors;
using Xrmd.Core.Query;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Migration.Records;
using Xrmd.Pipeline;
using Xrmd.Model.Records;
using Xrmd.Query.Model;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using Record = Xrmd.Model.Records.Record;
using MoneyValue = Xrmd.Model.Records.MoneyValue;
using AssignRequest = Microsoft.Crm.Sdk.Messages.AssignRequest;
using AssignResponse = Microsoft.Crm.Sdk.Messages.AssignResponse;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e3b: the core CRUD members on the model. Task 3: the read path (<see cref="CoreOperations.RetrieveRecordAsync"/>,
/// <see cref="CoreOperations.RetrieveRecordsAsync"/>, <see cref="QueryExecutor.ExecuteRecordsAsync(QueryModel, Caller, CancellationToken)"/>)
/// answers model records that, written out, are the Task 1 read goldens byte for byte; the SDK twins convert each row once. Task 5: Create
/// and Update (<see cref="CoreOperations.CreateRecordAsync(DataSession, Caller, Record, CancellationToken, bool, IReadOnlyDictionary{string, object?}?)"/>,
/// <see cref="CoreOperations.UpdateRecordAsync(DataSession, Caller, Record, CancellationToken, bool)"/>) store the Task 1 golden rows, a plain
/// table crosses no bridge, and the SDK twins and their direct callers behave as on <c>main</c>. Task 6: Delete and its cascades. Task 7:
/// Assign, Associate and Disassociate (<see cref="CoreOperations.AssignRecordAsync"/>, <see cref="CoreOperations.AssociateRecordsAsync"/>)
/// store the Task 1 golden rows.
/// </summary>
[Collection("xrmd")]
public sealed class CoreCrudModelTests(XrmdFixture fx)
{
    public static TheoryData<string> Reads => [.. RowScenarios.Reads.Select(s => s.Name)];

    /// <summary>
    /// Each Task 1 read scenario, answered by the model members and written out with <see cref="SdkRecords.ToSdk(Record)"/>: section (a)
    /// is the typed response of the core's model Retrieve or RetrieveMultiple, section (b) the model query executor's collection.
    /// </summary>
    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Model_read_equals_the_golden(string name)
    {
        var scenario = RowScenarios.Reads.Single(s => s.Name == name);
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var rows = new RowKit(kit);
        await rows.EnsureTablesAsync();
        var file = new StringBuilder();
        var (request, query) = await scenario.Arrange(rows);
        file.Append("=== (a) PipelineExecutor.ExecuteAsync ===\n--- request ---\n").Append(CoreDump.RequestText(request, fixedDates: true)).Append("--- response ---\n");
        await using (var scope = kit.Services.CreateAsyncScope())
        {
            var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            var session = new DataSession(conn, null);
            OrganizationResponse response = request switch
            {
                RetrieveRequest retrieve => new RetrieveResponse
                {
                    ResponseName = "Retrieve",
                    Results =
                    {
                        ["Entity"] = (await core.RetrieveRecordAsync(session, kit.Caller, retrieve.Target.LogicalName, retrieve.Target.Id,
                            retrieve.ColumnSet.ToModel(), default)).ToSdk(),
                    },
                },
                RetrieveMultipleRequest multiple => new RetrieveMultipleResponse
                {
                    ResponseName = "RetrieveMultiple",
                    Results =
                    {
                        ["EntityCollection"] = (await core.RetrieveRecordsAsync(session, kit.Caller, CoreOperations.ReadQuery(multiple.Query), default)).ToSdk(),
                    },
                },
                _ => throw new InvalidOperationException(request.RequestName),
            };
            file.Append(CoreDump.Response(response, null, fixedDates: true));
        }

        file.Append("=== (b) QueryExecutor.ExecuteAsync ===\n--- result ---\n");
        await using (var scope = kit.Services.CreateAsyncScope())
        {
            var records = await scope.ServiceProvider.GetRequiredService<QueryExecutor>().ExecuteRecordsAsync(query, kit.Caller);
            file.Append(CoreDump.ValueText("Result", records.ToSdk(), fixedDates: true));
        }

        CoreDump.AssertGolden(name + ".txt", kit.Normalize(file.ToString(), keepFixedDates: true), RowKit.Directory);
    }

    public static TheoryData<string> ValidationFaults => ["FaultLookupWrongType", "FaultChoiceWrongType", "FaultMoneyWrongType", "FaultPartyListWrongType"];

    /// <summary>
    /// S.2e3b Task 4 (Review Focus 2): each wrong-type input of the Task 1 fault goldens, as a model record through
    /// <see cref="CoreOperations.ValidateRecord"/>, gives the fault line of its golden (code, status and the text naming the SDK type).
    /// </summary>
    [Theory]
    [MemberData(nameof(ValidationFaults))]
    public async Task Validation_faults_equal_the_golden(string name)
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await new RowKit(kit).EnsureTablesAsync();
        var record = name switch
        {
            "FaultLookupWrongType" => new Record("contact") { ["lastname"] = "crr-fault", ["parentcustomerid"] = "not a reference" },
            "FaultChoiceWrongType" => new Record(RowKit.TypesTable) { ["new_name"] = "crr-fault", ["new_choice"] = 100000001 },
            "FaultMoneyWrongType" => new Record(RowKit.TypesTable) { ["new_name"] = "crr-fault-money", ["new_amount"] = 12.5m },
            "FaultPartyListWrongType" => new Record("email")
            {
                ["subject"] = "crr-fault", ["to"] = new RecordReference { LogicalName = "contact", Id = Guid.NewGuid() },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var fault = FluentActions.Invoking(() => core.ValidateRecord(record, isCreate: true)).Should().Throw<XrmdFaultException>().Which;
        var line = string.Create(CultureInfo.InvariantCulture, $"Fault: {nameof(XrmdFaultException)} 0x{fault.ErrorCode:X8} {fault.HttpStatus} ")
                   + CoreDump.Quote(fault.Message);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        var golden = File.ReadAllLines(Path.Combine(dir!.FullName, "tests", "fixtures", RowKit.Directory, name + ".txt"))
            .Single(l => l.StartsWith("Fault: ", StringComparison.Ordinal));
        line.Should().Be(golden);
    }

    /// <summary>
    /// Ruling 8b (Decision 4, the dual row sink): a 1,000-row read through the SDK twin shapes its entities straight from the reader (the SDK
    /// sink's hook sees each entity it returns, once) and answers what the model read of the same query writes out; the model read still
    /// shapes model records only (no SDK value in them).
    /// </summary>
    [Fact]
    public async Task Sdk_twin_read_shapes_entities_from_the_reader()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var rows = new RowKit(kit);
        await rows.EnsureTablesAsync();
        const string prefix = "crr-bulk-";
        await kit.ExecAsync($"DELETE FROM data.{RowKit.TagTable} WHERE new_name LIKE '{prefix}%'");
        try
        {
            var admin = kit.Admin;
            for (var i = 0; i < 1000; i++)
                await admin.CreateAsync(new Entity(RowKit.TagTable) { ["new_name"] = prefix + i.ToString("D4") });
            var query = CoreOperations.ReadQuery(new QueryExpression(RowKit.TagTable)
            {
                ColumnSet = new ColumnSet("new_name", "ownerid", "statecode"),
                Criteria = { Conditions = { new ConditionExpression("new_name", ConditionOperator.BeginsWith, prefix) } },
            });

            await using var scope = kit.Services.CreateAsyncScope();
            var executor = scope.ServiceProvider.GetRequiredService<QueryExecutor>();
            var shaped = new List<Entity>();
            QueryExecutorSdk.RowShaped.Value = shaped.Add;
            EntityCollection result;
            try
            {
                result = await executor.ExecuteAsync(query, kit.Caller);
            }
            finally
            {
                QueryExecutorSdk.RowShaped.Value = null;
            }

            result.Entities.Should().HaveCount(1000);
            shaped.Should().Equal(result.Entities, ReferenceEquals);
            result.Entities[0].GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);

            var records = await executor.ExecuteRecordsAsync(query, kit.Caller);
            records.Records.Should().HaveCount(1000);
            records.Records.Should().OnlyContain(r => r["ownerid"] is RecordReference && r["statecode"] is ChoiceValue);
            records.Records.SelectMany(r => r.Attributes.Values).Should().NotContain(v => v != null && v.GetType().Namespace!.StartsWith("Microsoft."));
            CoreDump.ValueText("rows", result).Should().Be(CoreDump.ValueText("rows", records.ToSdk()));
        }
        finally
        {
            await kit.ExecAsync($"DELETE FROM data.{RowKit.TagTable} WHERE new_name LIKE '{prefix}%'");
        }
    }

    /// <summary>
    /// Ruling 8b: the SDK row sink answers exactly what the model rows write out, for every value type (money with its currency format, choices,
    /// dates in the caller's format, lookups with their names), link-entity columns, all columns and an aggregate; the core's SDK twins
    /// (<c>RetrieveAsync</c>, <c>RetrieveMultipleAsync</c>) shape through the same sink.
    /// </summary>
    [Fact]
    public async Task Sdk_row_sink_answers_what_the_model_rows_write_out()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var rows = new RowKit(kit);
        await rows.EnsureTablesAsync();
        var id = await rows.AllTypesRowAsync("crr-sink");
        await using var scope = kit.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<QueryExecutor>();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var all = new QueryExpression(RowKit.TypesTable) { ColumnSet = new ColumnSet(true), Criteria = { Conditions = { new ConditionExpression("new_name", ConditionOperator.Equal, "crr-sink") } } };
        var linked = new QueryExpression(RowKit.TypesTable)
        {
            ColumnSet = new ColumnSet(RowKit.AllTypesColumns),
            Criteria = { Conditions = { new ConditionExpression("new_name", ConditionOperator.Equal, "crr-sink") } },
            LinkEntities = { new LinkEntity(RowKit.TypesTable, "systemuser", "ownerid", "systemuserid", JoinOperator.Inner) { EntityAlias = "u", Columns = new ColumnSet("fullname", "createdon") } },
        };
        var aggregate = new FetchExpression($"<fetch aggregate='true'><entity name='{RowKit.TypesTable}'><attribute name='new_amount' alias='total' aggregate='sum'/>" +
                                            "<attribute name='ownerid' alias='owner' groupby='true'/><attribute name='new_name' alias='n' aggregate='count'/></entity></fetch>");
        foreach (var source in new QueryBase[] { all, linked, aggregate })
        {
            var query = CoreOperations.ReadQuery(source);
            var shaped = new List<Entity>();
            QueryExecutorSdk.RowShaped.Value = shaped.Add;
            EntityCollection sdk;
            try
            {
                sdk = await executor.ExecuteAsync(query, kit.Caller);
            }
            finally
            {
                QueryExecutorSdk.RowShaped.Value = null;
            }
            sdk.Entities.Should().NotBeEmpty();
            shaped.Should().Equal(sdk.Entities, ReferenceEquals);
            var model = await executor.ExecuteRecordsAsync(query, kit.Caller);
            CoreDump.ValueText("rows", sdk).Should().Be(CoreDump.ValueText("rows", model.ToSdk()));
        }

        await using var conn = await kit.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        var session = new DataSession(conn, null);
        var twins = new List<Entity>();
        QueryExecutorSdk.RowShaped.Value = twins.Add;
        Entity retrieved;
        EntityCollection retrievedMany;
        try
        {
            retrieved = await core.RetrieveAsync(session, kit.Caller, RowKit.TypesTable, id, new ColumnSet(true), default);
            retrievedMany = await core.RetrieveMultipleAsync(session, kit.Caller, CoreOperations.ReadQuery(all), default);
        }
        finally
        {
            QueryExecutorSdk.RowShaped.Value = null;
        }
        twins.Should().HaveCount(2);
        twins[0].Should().BeSameAs(retrieved);
        twins[1].Should().BeSameAs(retrievedMany.Entities.Single());
        CoreDump.ValueText("row", retrieved).Should().Be(
            CoreDump.ValueText("row", (await core.RetrieveRecordAsync(session, kit.Caller, RowKit.TypesTable, id, new ColumnSet(true).ToModel(), default)).ToSdk()));
    }

    /// <summary>
    /// A restricted caller's Retrieve on the model answers as today: its own record; 403 AccessDenied with the Dataverse text for a record
    /// it may not read (after the <c>AfterRestrictedRetrieveMiss</c> seam ran); 404 for a record that does not exist.
    /// </summary>
    [Fact]
    public async Task Restricted_retrieve_answers_as_today()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        var tag = "CCM" + Guid.NewGuid().ToString("N")[..8];
        var root = await RootBusinessUnitAsync(fx);
        var unitA = await CreateBusinessUnitAsync(fx, root, "A-" + tag);
        var unitB = await CreateBusinessUnitAsync(fx, root, "B-" + tag);
        var userA = await CreateUserAsync(fx, unitA, Guid.NewGuid());
        var userB = await CreateUserAsync(fx, unitB, Guid.NewGuid());
        await AssignRoleAsync(fx, userA, await CreateRoleAsync(fx, "ccm-" + tag, ("prvReadpb_Project", DepthMask.Local)));
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var mine = await admin.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = tag + "-A", ["ownerid"] = new EntityReference("systemuser", userA) });
        var hidden = await admin.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = tag + "-B", ["ownerid"] = new EntityReference("systemuser", userB) });
        var caller = new Caller(userA, unitA);
        var columns = new ColumnSelection { Columns = { "pb_name" } };

        await using var scope = fx.App.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var misses = 0;
        core.AfterRestrictedRetrieveMiss = () =>
        {
            misses++;
            return Task.CompletedTask;
        };
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var session = new DataSession(conn, null);

        var own = await core.RetrieveRecordAsync(session, caller, TestEntities.Project, mine, columns, default);
        own.Id.Should().Be(mine);
        own["pb_name"].Should().Be(tag + "-A");
        misses.Should().Be(0);

        await FluentActions.Awaiting(() => core.RetrieveRecordAsync(session, caller, TestEntities.Project, hidden, columns, default))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.AccessDenied && e.HttpStatus == 403
                        && e.Message == $"Principal user with id {userA} does not have ReadAccess right(s) for record with id {hidden} of entity {TestEntities.Project}.");
        misses.Should().Be(1);

        var missing = Guid.NewGuid();
        await FluentActions.Awaiting(() => core.RetrieveRecordAsync(session, caller, TestEntities.Project, missing, columns, default))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist && e.HttpStatus == 404
                        && e.Message == $"{TestEntities.Project} With Id = {missing} Does Not Exist");
        misses.Should().Be(2);

        // The SDK twin answers the same, its record written out once.
        (await core.RetrieveAsync(session, caller, TestEntities.Project, mine, new ColumnSet("pb_name"), default)).GetAttributeValue<string>("pb_name")
            .Should().Be(tag + "-A");
        await FluentActions.Awaiting(() => core.RetrieveAsync(session, caller, TestEntities.Project, hidden, new ColumnSet("pb_name"), default))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.AccessDenied && e.HttpStatus == 403);
    }

    public static TheoryData<string> Writes =>
    [
        .. RowScenarios.Writes.Where(s => s.Name.StartsWith("Create", StringComparison.Ordinal) || s.Name.StartsWith("Update", StringComparison.Ordinal)
                                          || (s.Name.StartsWith("Fault", StringComparison.Ordinal))).Select(s => s.Name),
    ];

    /// <summary>
    /// S.2e3b Task 5 (Review Focus 1): each Task 1 Create, Update and fault scenario, its target read in as a model record and written by
    /// <see cref="CoreOperations.CreateRecordAsync(DataSession, Caller, Record, CancellationToken, bool, IReadOnlyDictionary{string, object?}?)"/> or
    /// <see cref="CoreOperations.UpdateRecordAsync(DataSession, Caller, Record, CancellationToken, bool)"/> directly (in a committed transaction of
    /// its own, the migration scenario inside its scope), gives its golden file byte for byte: the same answer or fault and the same rows.
    /// </summary>
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Model_create_and_update_store_the_golden_rows(string name)
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
        var target = ((Entity)request.Parameters["Target"]).ToModel();
        var detect = CoreOperations.DetectDuplicates(request.ToModel());
        OrganizationResponse? response = null;
        Exception? fault = null;
        await using (var scope = kit.Services.CreateAsyncScope())
        {
            var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            var session = new DataSession(conn, tx);
            try
            {
                if (request is CreateRequest)
                {
                    Guid id;
                    using (scenario.Migration ? MigrationScope.Enter() : null)
                        id = await core.CreateRecordAsync(session, kit.Caller, target, default, detect);
                    // The answer the dispatch gives around the body (the elastic session token included).
                    response = scenario.Migration
                        ? new CreateResponse { Results = { ["id"] = id } }
                        : await ElasticRecords.WithSessionTokenAsync(session, kit.Store.GetEntity(target.LogicalName)!, id,
                            new CreateResponse { ResponseName = "Create", Results = { ["id"] = id } }, default);
                }
                else
                {
                    await core.UpdateRecordAsync(session, kit.Caller, target, default, detect);
                    response = await ElasticRecords.WithSessionTokenAsync(session, kit.Store.GetEntity(target.LogicalName)!, target.Id,
                        new UpdateResponse { ResponseName = "Update" }, default);
                }
                await tx.CommitAsync();
            }
            catch (Exception ex)
            {
                fault = ex;
                await tx.RollbackAsync();
            }
            finally
            {
                kit.Cache.Invalidate();
            }
        }
        file.Append("--- response ---\n").Append(CoreDump.Response(response, fault, fixedDates: true));
        await scenario.Rows(rows, response);
        file.Append(rows.Text);

        CoreDump.AssertGolden(name + ".txt", kit.Normalize(file.ToString(), keepFixedDates: true), RowKit.Directory);
    }

    /// <summary>
    /// S.2e3b Task 5 (Review Focus 4): a Create and an Update of <c>pb_pipe</c> (no mail, elastic, image, rollup, audit or duplicate rule)
    /// convert nothing through <see cref="CoreSdkBridges"/>, through the model members and through their SDK twins; in the same flow an
    /// audited table converts exactly its audit images: one crossing for a Create (the after image), one for an Update (both images).
    /// </summary>
    [Fact]
    public async Task Plain_table_write_crosses_no_bridge()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await TestEntities.EnsurePipeAsync(kit.Services.GetRequiredService<IMetadataStore>());
        var audited = (await kit.AuditedRowAsync()).Row;
        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        var s = new DataSession(conn, tx);
        try
        {
            using var crossings = CoreSdkBridges.CountCrossings();
            var id = await core.CreateRecordAsync(s, kit.Caller, new Record(TestEntities.Pipe) { ["pb_name"] = "ccm plain", ["pb_count"] = 1 }, default);
            await core.UpdateRecordAsync(s, kit.Caller, new Record(TestEntities.Pipe, id) { ["pb_name"] = "ccm plain 2", ["pb_count"] = 2 }, default);
            var sdkId = await core.CreateAsync(s, kit.Caller, new Entity(TestEntities.Pipe) { ["pb_name"] = "ccm plain sdk" }, default);
            await core.UpdateAsync(s, kit.Caller, new Entity(TestEntities.Pipe, sdkId) { ["pb_count"] = 3 }, default);
            crossings.Value.Should().Be(0, "a plain table converts nothing");

            var auditedId = await core.CreateRecordAsync(s, kit.Caller, new Record("new_crgaudited") { ["new_name"] = "ccm audited" }, default);
            crossings.Value.Should().Be(1, "an audited Create converts its after image");
            await core.UpdateRecordAsync(s, kit.Caller, new Record("new_crgaudited", audited.Id) { ["new_name"] = "ccm audited 2" }, default);
            crossings.Value.Should().Be(2, "an audited Update converts its two images once");
            (await core.RetrieveRecordAsync(s, Caller.Internal, "new_crgaudited", auditedId, new ColumnSelection { Columns = { "new_name" } }, default))
                ["new_name"].Should().Be("ccm audited");
        }
        finally
        {
            await tx.RollbackAsync();
            kit.Cache.Invalidate();
        }
    }

    /// <summary>
    /// S.2e3b Task 5 (Global Constraints, Review Focus 5): the SDK twins leave the caller's <see cref="Entity"/> as <c>main</c> (3dd0ca6e)
    /// does, checked on <c>main</c> first with the same probe: an activity with parties, a note with a body and a contact (Create and an
    /// Update carrying its unchanged full name) are left unchanged, deep. The validation still changes the entity in place as on
    /// <c>main</c>: a Create by key gets its id and its <c>partitionid</c> from the key, and a value sent for a calculated column is removed.
    /// </summary>
    [Fact]
    public async Task Sdk_twin_leaves_the_callers_entity_as_today()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await kit.EnsureElasticTableAsync();
        var calculated = await CalculatedTableAsync(kit);
        var admin = kit.Admin;
        var account = await admin.CreateAsync(new Entity("account") { ["name"] = "ccm probe account" });
        var contact = await admin.CreateAsync(new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = "ccm-probe" });
        var email = await admin.CreateAsync(new Entity("email") { ["subject"] = "ccm probe" });
        static EntityCollection Parties(params EntityReference[] parties) =>
            new([.. parties.Select(p => new Entity("activityparty") { ["partyid"] = p })]);
        var key = Guid.NewGuid();
        var cases = new List<(string Name, bool Create, Entity Entity, Action<Entity, Entity>? Expected)>
        {
            ("CreateEmailWithParties", true, new Entity("email")
            {
                ["subject"] = "ccm probe", ["from"] = Parties(new EntityReference("systemuser", kit.Caller.UserId)),
                ["to"] = Parties(new EntityReference("contact", contact)),
            }, null),
            ("UpdateEmailParties", false, new Entity("email", email) { ["subject"] = "ccm probe 2", ["to"] = Parties(new EntityReference("account", account)) }, null),
            ("CreateNoteWithBody", true, new Entity("annotation")
            {
                ["subject"] = "ccm probe", ["filename"] = "p.txt", ["mimetype"] = "text/plain",
                ["documentbody"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("ccm probe")), ["objectid"] = new EntityReference("account", account),
            }, null),
            ("CreateContact", true, new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = "ccm-probe2" }, null),
            ("UpdateContactSameFullName", false, new Entity("contact", contact) { ["fullname"] = "ccm-probe, Ada", ["jobtitle"] = "x" }, null),
            // As on main: the id and the partitionid come from the key, in the caller's entity.
            ("CreateElasticByKey", true, new Entity("new_crgelastic")
            {
                ["new_name"] = "ccm probe", KeyAttributes = { ["new_crgelasticid"] = key, ["partitionid"] = "ccm-p" },
            }, (before, _) =>
            {
                before.Id = key;
                before["partitionid"] = "ccm-p";
            }),
            // As on main: the value sent for a calculated column is removed from the caller's entity (plan 2.12a, ruling K5).
            ("CreateCalculated", true, new Entity(calculated) { ["new_name"] = "ccm probe", ["new_quantity"] = 2, ["new_total"] = new Money(9m) },
                (before, _) => before.Attributes.Remove("new_total")),
        };
        foreach (var (name, create, entity, expected) in cases)
        {
            var copy = new Entity(entity.LogicalName, entity.Id) { KeyAttributes = entity.KeyAttributes };
            foreach (var (column, value) in entity.Attributes) copy[column] = value;
            expected?.Invoke(copy, entity);
            var want = CoreDump.RequestText(new CreateRequest { Target = copy });
            await using var scope = kit.Services.CreateAsyncScope();
            var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                if (create) await core.CreateAsync(new DataSession(conn, tx), kit.Caller, entity, default);
                else await core.UpdateAsync(new DataSession(conn, tx), kit.Caller, entity, default);
            }
            finally
            {
                await tx.RollbackAsync();
                kit.Cache.Invalidate();
            }
            CoreDump.RequestText(new CreateRequest { Target = entity }).Should().Be(want, name);
        }
    }

    /// <summary>
    /// S.2e3b Task 5 (Review Focus 5): the direct SDK callers of Create and Update get today's results: a migration import of one row
    /// (<see cref="ImportRecordWriter"/>) stores the row of the Task 1 golden <c>CreateInMigrationScope</c>, the MCP tool
    /// <c>create_record</c> (<c>RecordTools</c>) stores the row of <c>CreateContactFullName</c>, and a <see cref="Xrmd.Files.FileColumnService"/>
    /// commit sets the file column and its name.
    /// </summary>
    [Fact]
    public async Task Direct_callers_get_todays_results()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);

        // ImportRecordWriter, one row.
        var rows = new RowKit(kit);
        await rows.EnsureTablesAsync();
        var target = (Entity)(await RowScenarios.Writes.Single(s => s.Name == "CreateInMigrationScope").Arrange(rows)).Parameters["Target"];
        var record = new MigrationRecord(target.Id, new Dictionary<string, MigrationValue>
        {
            ["name"] = new StringValue(target.GetAttributeValue<string>("name")),
            ["createdon"] = new DateTimeValue(target.GetAttributeValue<DateTime>("createdon")),
            ["modifiedon"] = new DateTimeValue(target.GetAttributeValue<DateTime>("modifiedon")),
            ["createdby"] = Lookup(target, "createdby"), ["modifiedby"] = Lookup(target, "modifiedby"), ["ownerid"] = Lookup(target, "ownerid"),
            ["owningbusinessunit"] = Lookup(target, "owningbusinessunit"),
            ["statecode"] = new OptionValue(1), ["statuscode"] = new OptionValue(2),
        });
        await using (var scope = kit.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var store = services.GetRequiredService<IMetadataStore>();
            var writer = new ImportRecordWriter(services.GetRequiredService<CoreOperations>(), kit.Caller,
                new MigrationLedger(services.GetRequiredService<IDbConnectionFactory>(), Guid.NewGuid()),
                new ImportRecordBuilder(store, new Dictionary<Guid, Guid>(), new HashSet<string>(StringComparer.Ordinal), strictLookups: false), 4, store);
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            using (MigrationScope.Enter())
                await writer.WriteAsync(new DataSession(conn, tx), "account", [record], null, default);
            await tx.CommitAsync();
            kit.Cache.Invalidate();
        }
        await rows.RowAsync("account", target.Id);
        kit.Normalize(rows.Text, keepFixedDates: true).Should().Be(GoldenRows("CreateInMigrationScope"));

        // RecordTools create (MCP create_record).
        var mcp = new RowKit(kit);
        await using (var client = await McpTests.ConnectAsync(kit.Fx.App, kit.Fx.BaseUrl, kit.Fx.AdminObjectId))
        {
            var result = await client.CallToolAsync("create_record", new Dictionary<string, object?>
            {
                ["table"] = "contact",
                ["data"] = new Dictionary<string, object?> { ["firstname"] = "Ada", ["middlename"] = "B.", ["lastname"] = "crr-fullname" },
            });
            result.IsError.Should().NotBe(true, McpTests.Text(result));
            var id = Guid.Parse((string)JsonNode.Parse(McpTests.Text(result))!["id"]!);
            mcp.Name(id, "created");
            await mcp.RowAsync("contact", id);
        }
        kit.Normalize(mcp.Text, keepFixedDates: true).Should().Be(GoldenRows("CreateContactFullName"));

        // FileColumnService commit (CommitFileBlocksUpload): the Update of the record with the committed file.
        var (file, fileId) = await kit.UploadedFileAsync();
        await using (var conn = await TestProvider.OpenAsync(kit.ConnectionString))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT new_doc, new_doc_name FROM data.new_crgfile WHERE new_crgfileid = '{file.Id}'";
            await using var rd = await cmd.ExecuteReaderAsync();
            (await rd.ReadAsync()).Should().BeTrue();
            rd.GetGuid(0).Should().Be(fileId);
            rd.GetString(1).Should().Be("report.pdf");
        }
    }

    public static TheoryData<string> Deletes => [.. RowScenarios.Writes.Where(s => s.Name.StartsWith("Delete", StringComparison.Ordinal)).Select(s => s.Name)];

    /// <summary>
    /// S.2e3b Task 6 (Review Focus 1): each Task 1 Delete scenario run by
    /// <see cref="CoreOperations.DeleteRecordAsync(DataSession, Caller, string, Guid, NestedRequest?, CancellationToken)"/> directly (in a
    /// committed transaction of its own), its cascaded children run by the same member through a test <see cref="NestedRequest"/>, gives
    /// its golden file byte for byte: the same answer and the same rows (the audit rows, the attachments, their files and blobs).
    /// </summary>
    [Theory]
    [MemberData(nameof(Deletes))]
    public async Task Model_delete_stores_the_golden_rows(string name)
    {
        var scenario = RowScenarios.Writes.Single(s => s.Name == name);
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var rows = new RowKit(kit);
        await rows.EnsureTablesAsync();
        var file = new StringBuilder();

        var request = await scenario.Arrange(rows);
        file.Append("=== PipelineExecutor.ExecuteAsync ===\n");
        file.Append("--- request ---\n").Append(CoreDump.RequestText(request, fixedDates: true));
        var target = (EntityReference)request.Parameters["Target"];
        OrganizationResponse? response = null;
        Exception? fault = null;
        await using (var scope = kit.Services.CreateAsyncScope())
        {
            var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            var session = new DataSession(conn, tx);
            try
            {
                await core.DeleteRecordAsync(session, kit.Caller, target.LogicalName, target.Id, DeletingNested(core, session, kit.Caller, []), default);
                response = new DeleteResponse { ResponseName = "Delete" };
                await tx.CommitAsync();
                session.Committed(); // the files' blobs go after the commit
            }
            catch (Exception ex)
            {
                fault = ex;
                await tx.RollbackAsync();
                session.RolledBack();
            }
            finally
            {
                kit.Cache.Invalidate();
            }
        }
        file.Append("--- response ---\n").Append(CoreDump.Response(response, fault, fixedDates: true));
        await scenario.Rows(rows, response);
        file.Append(rows.Text);

        CoreDump.AssertGolden(name + ".txt", kit.Normalize(file.ToString(), keepFixedDates: true), RowKit.Directory);
    }

    /// <summary>
    /// S.2e3b Task 6: the Delete of an account with a child contact (<c>contact_customer_accounts</c>) and a task regarding it issues each
    /// cascaded child as a model request named Delete, targeting the child by a <see cref="Xrmd.Model.Records.RecordReference"/> and marked
    /// with <see cref="CascadeHint.Mark(Xrmd.Model.Messages.Request)"/>, in the order observed on this branch (which reads the same
    /// relationship list as <c>main</c>), pinned as a literal: the task,
    /// then the contact. The children are gone with the account.
    /// </summary>
    [Fact]
    public async Task Model_delete_cascade_marks_its_children()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var account = await kit.Admin.CreateAsync(new Entity("account") { ["name"] = "ccm cascade" });
        var contact = await kit.Admin.CreateAsync(new Entity("contact")
        {
            ["lastname"] = "ccm-cascade", ["parentcustomerid"] = new EntityReference("account", account),
        });
        var task = await kit.Admin.CreateAsync(new Entity("task") { ["subject"] = "ccm cascade", ["regardingobjectid"] = new EntityReference("account", account) });
        // The order observed on this branch, which reads the same relationship list as main, pinned: the task regarding the account
        // before the contact (contact_customer_accounts).
        var expected = new[] { ("task", task), ("contact", contact) };

        var issued = new List<Xrmd.Model.Messages.Request>();
        await using (var scope = kit.Services.CreateAsyncScope())
        {
            var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            var session = new DataSession(conn, tx);
            await core.DeleteRecordAsync(session, kit.Caller, "account", account, DeletingNested(core, session, kit.Caller, issued), default);
            await tx.CommitAsync();
            session.Committed();
            kit.Cache.Invalidate();
        }

        issued.Should().AllSatisfy(r =>
        {
            r.Name.Should().Be("Delete");
            r.Parameters.Keys.Should().Equal("Target");
            CascadeHint.IsMarked(r).Should().BeTrue("a cascaded child is marked on the model");
        });
        issued.Select(r => (Xrmd.Model.Records.RecordReference)r["Target"]!).Select(t => (t.LogicalName, t.Id)).Should().Equal(expected);
        (await TestSql.CountAsync(kit.ConnectionString, $"SELECT COUNT(*) FROM data.account WHERE accountid = '{account}'")).Should().Be(0);
        (await TestSql.CountAsync(kit.ConnectionString, $"SELECT COUNT(*) FROM data.contact WHERE contactid = '{contact}'")).Should().Be(0);
        (await TestSql.CountAsync(kit.ConnectionString, $"SELECT COUNT(*) FROM data.task WHERE activityid = '{task}'")).Should().Be(0);
    }

    /// <summary>
    /// S.2e3b Task 6 (Review Focus 4): the Delete of a <c>pb_pipe</c> row (no mail, elastic, image, file, rollup, audit or duplicate rule)
    /// converts nothing through <see cref="CoreSdkBridges"/>, through the model member and through its SDK twin; in the same flow the Delete
    /// of an audited row converts exactly its audit image (the before image), one crossing.
    /// </summary>
    [Fact]
    public async Task Plain_table_delete_crosses_no_bridge()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await TestEntities.EnsurePipeAsync(kit.Services.GetRequiredService<IMetadataStore>());
        await kit.AuditedRowAsync(); // turns the organization's auditing on
        await using var scope = kit.Services.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        var s = new DataSession(conn, tx);
        try
        {
            var plain = await core.CreateRecordAsync(s, kit.Caller, new Record(TestEntities.Pipe) { ["pb_name"] = "ccm delete" }, default);
            var plainSdk = await core.CreateRecordAsync(s, kit.Caller, new Record(TestEntities.Pipe) { ["pb_name"] = "ccm delete sdk" }, default);
            var audited = await core.CreateRecordAsync(s, kit.Caller, new Record("new_crgaudited") { ["new_name"] = "ccm audited delete" }, default);

            using var crossings = CoreSdkBridges.CountCrossings();
            await core.DeleteRecordAsync(s, kit.Caller, TestEntities.Pipe, plain, null, default);
            await core.DeleteAsync(s, kit.Caller, TestEntities.Pipe, plainSdk, default);
            crossings.Value.Should().Be(0, "a plain table converts nothing");

            await core.DeleteRecordAsync(s, kit.Caller, "new_crgaudited", audited, null, default);
            crossings.Value.Should().Be(1, "an audited Delete converts its before image");
            await using var cmd = s.Command($"SELECT COUNT(*) FROM data.audit WHERE objectid = '{audited}' AND operation = 3");
            Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture).Should().Be(1, "the Delete wrote its audit row");
        }
        finally
        {
            await tx.RollbackAsync();
            kit.Cache.Invalidate();
        }
    }

    public static TheoryData<string> AssignsAndAssociations =>
    [
        .. RowScenarios.Writes.Where(s => s.Name.StartsWith("Assign", StringComparison.Ordinal)
                                          || s.Name.StartsWith("Associate", StringComparison.Ordinal)
                                          || s.Name.StartsWith("Disassociate", StringComparison.Ordinal)).Select(s => s.Name),
    ];

    /// <summary>
    /// S.2e3b Task 7 (Review Focus 1): each Task 1 Assign, Associate and Disassociate scenario (1:N from the referenced side, N:N, a role to a
    /// user), its parameters read in as model values and run by <see cref="CoreOperations.AssignRecordAsync"/> or
    /// <see cref="CoreOperations.AssociateRecordsAsync"/> directly (in a committed transaction of its own), gives its golden file byte for
    /// byte: the same answer and the same rows.
    /// </summary>
    [Theory]
    [MemberData(nameof(AssignsAndAssociations))]
    public async Task Model_assign_and_associate_store_the_golden_rows(string name)
    {
        var scenario = RowScenarios.Writes.Single(s => s.Name == name);
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var rows = new RowKit(kit);
        await rows.EnsureTablesAsync();
        var file = new StringBuilder();

        var request = await scenario.Arrange(rows);
        file.Append("=== PipelineExecutor.ExecuteAsync ===\n");
        file.Append("--- request ---\n").Append(CoreDump.RequestText(request, fixedDates: true));
        OrganizationResponse? response = null;
        Exception? fault = null;
        await using (var scope = kit.Services.CreateAsyncScope())
        {
            var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            var session = new DataSession(conn, tx);
            try
            {
                switch (request)
                {
                    case AssignRequest assign:
                        await core.AssignRecordAsync(session, kit.Caller, assign.Target.ToModel(), assign.Assignee.ToModel(), default);
                        response = new AssignResponse { ResponseName = "Assign" };
                        break;
                    case AssociateRequest associate:
                        await core.AssociateRecordsAsync(session, kit.Caller, associate.Target.ToModel(), associate.Relationship.ToModel(),
                            associate.RelatedEntities.ToModel(), associate: true, default);
                        response = new AssociateResponse { ResponseName = "Associate" };
                        break;
                    case DisassociateRequest disassociate:
                        await core.AssociateRecordsAsync(session, kit.Caller, disassociate.Target.ToModel(), disassociate.Relationship.ToModel(),
                            disassociate.RelatedEntities.ToModel(), associate: false, default);
                        response = new DisassociateResponse { ResponseName = "Disassociate" };
                        break;
                    default:
                        throw new InvalidOperationException(request.RequestName);
                }
                await tx.CommitAsync();
                session.Committed(); // the security cache drops what the role assignment changed
            }
            catch (Exception ex)
            {
                fault = ex;
                await tx.RollbackAsync();
                session.RolledBack();
            }
            finally
            {
                kit.Cache.Invalidate();
            }
        }
        file.Append("--- response ---\n").Append(CoreDump.Response(response, fault, fixedDates: true));
        await scenario.Rows(rows, response);
        file.Append(rows.Text);

        CoreDump.AssertGolden(name + ".txt", kit.Normalize(file.ToString(), keepFixedDates: true), RowKit.Directory);
    }

    /// <summary>
    /// S.2e3b Task 7: <c>contact_customer_accounts</c> through <see cref="CoreOperations.AssociateRecordsAsync"/> from its referencing side
    /// (the contact the target, its one account related) sets and clears the lookup; a Disassociate from either side whose contact points
    /// to another account writes nothing (the row keeps its lookup and its <c>modifiedon</c>), as today.
    /// </summary>
    [Fact]
    public async Task Model_associate_one_to_many_from_either_side()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        var parent = await kit.Admin.CreateAsync(new Entity("account") { ["name"] = "ccm parent" });
        var other = await kit.Admin.CreateAsync(new Entity("account") { ["name"] = "ccm other" });
        var contact = await kit.Admin.CreateAsync(new Entity("contact")
        {
            ["lastname"] = "ccm-child", ["parentcustomerid"] = new EntityReference("account", other),
        });
        var relationship = new RelationshipName("contact_customer_accounts", null);
        var contactRef = new RecordReference { LogicalName = "contact", Id = contact };
        var parentRef = new RecordReference { LogicalName = "account", Id = parent };

        async Task<(object? Parent, object? ModifiedOn)> StoredAsync() =>
            (await TestSql.ScalarAsync(kit.ConnectionString, $"SELECT parentcustomerid FROM data.contact WHERE contactid = '{contact}'"),
             await TestSql.ScalarAsync(kit.ConnectionString, $"SELECT modifiedon FROM data.contact WHERE contactid = '{contact}'"));

        async Task RunAsync(RecordReference target, RecordReference related, bool associate)
        {
            await using var scope = kit.Services.CreateAsyncScope();
            var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            var session = new DataSession(conn, tx);
            await core.AssociateRecordsAsync(session, kit.Caller, target, relationship, [related], associate, default);
            await tx.CommitAsync();
            session.Committed();
        }

        var before = await StoredAsync();
        before.Parent.Should().Be(other);
        await Task.Delay(20); // a write would move modifiedon
        await RunAsync(parentRef, contactRef, associate: false);
        (await StoredAsync()).Should().Be(before, "the contact points to another account: nothing is written");
        await RunAsync(contactRef, parentRef, associate: false);
        (await StoredAsync()).Should().Be(before, "the same from the referencing side");

        await RunAsync(contactRef, parentRef, associate: true);
        var associated = await StoredAsync();
        associated.Parent.Should().Be(parent);
        associated.ModifiedOn.Should().NotBe(before.ModifiedOn);

        await RunAsync(contactRef, parentRef, associate: false);
        var cleared = await StoredAsync();
        cleared.Parent.Should().BeNull();

        // A Disassociate of a lookup that is already empty writes nothing (the stored value is null, not the parent).
        await Task.Delay(20);
        await RunAsync(parentRef, contactRef, associate: false);
        (await StoredAsync()).Should().Be(cleared, "the lookup is already empty: nothing is written");
    }

    // ---- S.2e3b Task 8: the adapter dispatches the CRUD messages on the model ---------------------------------------------------

    /// <summary>Runs <paramref name="request"/> through the pipeline as the kit's caller, at the first level, with <paramref name="mirror"/>.</summary>
    private static async Task<OrganizationResponse> PipelineAsync(CoreScenarioKit kit, OrganizationRequest request, SdkMirror? mirror = null)
    {
        await using var scope = kit.Services.CreateAsyncScope();
        var invocation = new InvocationContext(kit.Caller, kit.Caller.UserId, 1, null, null, Guid.NewGuid()) { Mirror = mirror };
        return await scope.ServiceProvider.GetRequiredService<PipelineExecutor>().ExecuteAsync(request, invocation, default);
    }

    /// <summary>The objects one crossing of <paramref name="sdk"/> writes.</summary>
    private static long SizeOf(object sdk)
    {
        var mirror = new SdkMirror();
        mirror.In(sdk);
        return mirror.SyncCount;
    }

    /// <summary>
    /// S.2e3b Task 8 (Decisions 7 and 8): the eight CRUD messages reach the core's model handler as the model request: the adapter's
    /// <see cref="CoreHandOff.HandlerRequest"/> seam (the SDK request a handler of <c>CoreOperations.cs</c> runs on) never fires for them,
    /// <see cref="CoreOperations.CrudRequest"/> sees each one. The request crosses once, at the pipeline's entry: an Update of 5 attributes
    /// with no step writes its request once (it wrote it 3 times before: in at the entry, out and in at the boundary) and its typed
    /// response once.
    /// </summary>
    [Fact]
    public async Task Crud_messages_do_not_cross_the_request()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await TestEntities.EnsureProjectTagAsync(kit.Store, kit.ConnectionString);
        var tag = await kit.Admin.CreateAsync(new Entity(TestEntities.Tag) { ["new_name"] = "ccm cross" });
        UpdateRequest Update(Guid id) => new()
        {
            Target = new Entity(TestEntities.Project, id)
            {
                ["pb_name"] = "ccm cross 2", ["pb_count"] = 5, ["pb_ratio"] = 0.5d, ["pb_startdate"] = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc),
                ["pb_priority"] = new OptionSetValue(2),
            },
        };
        Relationship ProjectTag() => new(TestEntities.ProjectTag);
        var handled = new List<OrganizationRequest>();
        var crud = new List<Xrmd.Model.Messages.Request>();

        CoreHandOff.HandlerRequest.Value = handled.Add;
        CoreOperations.CrudRequest.Value = crud.Add;
        Guid project;
        try
        {
            project = (Guid)(await PipelineAsync(kit, new CreateRequest { Target = new Entity(TestEntities.Project) { ["pb_name"] = "ccm cross" } })).Results["id"];
            var reference = new EntityReference(TestEntities.Project, project);
            await PipelineAsync(kit, new RetrieveRequest { Target = reference, ColumnSet = new ColumnSet("pb_name") });
            await PipelineAsync(kit, Update(project));
            await PipelineAsync(kit, new RetrieveMultipleRequest
            {
                Query = new QueryExpression(TestEntities.Project) { ColumnSet = new ColumnSet("pb_name"), TopCount = 1 },
            });
            await PipelineAsync(kit, new AssignRequest { Target = reference, Assignee = new EntityReference("systemuser", kit.Caller.UserId) });
            await PipelineAsync(kit, new AssociateRequest
            {
                Target = reference, Relationship = ProjectTag(), RelatedEntities = [new EntityReference(TestEntities.Tag, tag)],
            });
            await PipelineAsync(kit, new DisassociateRequest
            {
                Target = reference, Relationship = ProjectTag(), RelatedEntities = [new EntityReference(TestEntities.Tag, tag)],
            });
            await PipelineAsync(kit, new DeleteRequest { Target = reference });
        }
        finally
        {
            CoreHandOff.HandlerRequest.Value = null;
            CoreOperations.CrudRequest.Value = null;
        }

        handled.Should().BeEmpty("no CRUD message reaches an SDK handler");
        crud.Select(r => r.Name).Should().Equal("Create", "Retrieve", "Update", "RetrieveMultiple", "Assign", "Associate", "Disassociate", "Delete");

        project = await kit.Admin.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = "ccm cross" });
        var mirror = new SdkMirror();
        await PipelineAsync(kit, Update(project), mirror);
        mirror.SyncCount.Should().Be(SizeOf(Update(project)) + SizeOf(new UpdateResponse { ResponseName = "Update" }),
            "the request crosses once, at the entry (3 times before), and the typed response is written once");
    }

    /// <summary>
    /// S.2e3b Task 8 (Decision 7): the callers that take the SDK response get today's typed response, filled: a plug-in's service casts the
    /// Create and RetrieveMultiple answers (<c>TypedResponsesPlugin</c>), an extension caller of <see cref="PipelineCoreSdk"/> (as
    /// <c>SolutionImporter</c> calls the core) gets a <see cref="CreateResponse"/> with the stored id and a
    /// <see cref="RetrieveMultipleResponse"/> with the row, and the Web API Create answers with the row's <c>OData-EntityId</c>.
    /// </summary>
    [Fact]
    public async Task Typed_responses_for_pipeline_and_extension_callers()
    {
        await using var kit = await CoreScenarioKit.StartAsync(fx, ownDatabase: false);
        await TestEntities.EnsurePipeAsync(kit.Store);
        await kit.ExecAsync($"DELETE FROM data.{TestEntities.PipeLog} WHERE pb_name IN ('typed', 'typed-result')");

        // A plug-in's service.
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(kit.Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(kit.Admin, assembly, "Xrmd.TestPlugins.TypedResponsesPlugin");
        await PluginTestHelper.RegisterStepAsync(kit.Admin, type, "Create", TestEntities.Pipe, 40, config: TestEntities.PipeLog);
        try
        {
            await kit.Admin.CreateAsync(new Entity(TestEntities.Pipe) { ["pb_name"] = "typed-trigger" });
        }
        finally
        {
            await PluginTestHelper.ClearAsync(kit.Fx);
        }
        var typed = await TestSql.ScalarAsync(kit.ConnectionString, $"SELECT {TestEntities.PipeLog}id FROM data.{TestEntities.PipeLog} WHERE pb_name = 'typed'");
        (await TestSql.ScalarAsync(kit.ConnectionString, $"SELECT pb_data FROM data.{TestEntities.PipeLog} WHERE pb_name = 'typed-result'"))
            .Should().Be($"output=True;created={typed};rows=1;name=typed");

        // An extension caller of PipelineCoreSdk.
        await using (var scope = kit.Services.CreateAsyncScope())
        {
            var core = scope.ServiceProvider.GetRequiredService<IPipelineCore>();
            await using var conn = await TestProvider.OpenAsync(kit.ConnectionString);
            await using var tx = await conn.BeginTransactionAsync();
            var session = new DataSession(conn, tx);
            var created = await core.ExecuteCoreAsync(new CreateRequest { Target = new Entity(TestEntities.Pipe) { ["pb_name"] = "ccm typed sdk" } },
                kit.Caller, session, default);
            var id = created.Should().BeOfType<CreateResponse>().Subject.id;
            var read = await core.ExecuteCoreAsync(new RetrieveMultipleRequest
            {
                Query = new QueryExpression(TestEntities.Pipe)
                {
                    ColumnSet = new ColumnSet("pb_name"), Criteria = { Conditions = { new ConditionExpression("pb_name", ConditionOperator.Equal, "ccm typed sdk") } },
                },
            }, kit.Caller, session, default);
            read.Should().BeOfType<RetrieveMultipleResponse>().Subject.EntityCollection.Entities.Should().ContainSingle().Which.Id.Should().Be(id);
            await tx.RollbackAsync();
        }

        // The Web API Create.
        var post = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/" + TestEntities.PipeSet)
        {
            Content = new StringContent("{\"pb_name\":\"ccm typed web\"}", Encoding.UTF8, "application/json"),
        };
        post.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            DevTokenFactory.Create(kit.Fx.AdminObjectId, kit.Fx.BaseUrl));
        var res = await kit.Fx.Client.SendAsync(post);
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var url = res.Headers.GetValues("OData-EntityId").Single();
        var webId = Guid.Parse(url[(url.LastIndexOf('(') + 1)..^1]);
        (await TestSql.ScalarAsync(kit.ConnectionString, $"SELECT pb_name FROM data.{TestEntities.Pipe} WHERE {TestEntities.Pipe}id = '{webId}'"))
            .Should().Be("ccm typed web");
    }

    /// <summary>
    /// A <see cref="NestedRequest"/> that records each request the core issues in <paramref name="issued"/> and runs its Delete through
    /// <see cref="CoreOperations.DeleteRecordAsync(DataSession, Caller, string, Guid, NestedRequest?, CancellationToken)"/> with itself,
    /// as the pipeline's Delete would (without its steps).
    /// </summary>
    private static NestedRequest DeletingNested(CoreOperations core, DataSession session, Caller caller, List<Xrmd.Model.Messages.Request> issued)
    {
        NestedRequest? nested = null;
        nested = async (request, ct) =>
        {
            issued.Add(request);
            var target = (Xrmd.Model.Records.RecordReference)request["Target"]!;
            await core.DeleteRecordAsync(session, caller, target.LogicalName, target.Id, nested, ct);
            return new Xrmd.Model.Messages.Response { Name = "Delete" };
        };
        return nested;
    }

    private static LookupValue Lookup(Entity entity, string column) =>
        entity.GetAttributeValue<EntityReference>(column) is { } r ? new LookupValue(r.LogicalName, r.Id) : throw new InvalidOperationException(column);

    /// <summary>The <c>--- rows ---</c> section of a Task 1 row golden.</summary>
    private static string GoldenRows(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "tests", "fixtures", RowKit.Directory, name + ".txt"));
        return text[text.IndexOf("--- rows ---\n", StringComparison.Ordinal)..];
    }

    private static readonly SemaphoreSlim CalculatedLock = new(1, 1);

    /// <summary>
    /// <c>new_ccmcalc</c> (created once): <c>new_name</c>, <c>new_amount</c> (money), <c>new_quantity</c> and the calculated
    /// <c>new_total</c> = amount × quantity, as <c>CalculatedWriteTests</c> builds its tables.
    /// </summary>
    private static async Task<string> CalculatedTableAsync(CoreScenarioKit kit)
    {
        const string table = "new_ccmcalc";
        await CalculatedLock.WaitAsync();
        try
        {
            if (kit.Services.GetRequiredService<IMetadataStore>().GetEntity(table) is not null) return table;
            static Label Label(string text) => new(text, 1033);
            var admin = kit.Admin;
            await admin.ExecuteAsync(new CreateEntityRequest
            {
                Entity = new EntityMetadata
                {
                    LogicalName = table, SchemaName = table, DisplayName = Label(table), DisplayCollectionName = Label(table + "s"),
                    OwnershipType = OwnershipTypes.UserOwned,
                },
                PrimaryAttribute = new StringAttributeMetadata
                {
                    LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name"),
                    RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
                },
            });
            await admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = table,
                Attribute = new MoneyAttributeMetadata { LogicalName = "new_amount", SchemaName = "new_Amount", DisplayName = Label("Amount"), PrecisionSource = 2 },
            });
            await admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = table,
                Attribute = new IntegerAttributeMetadata { LogicalName = "new_quantity", SchemaName = "new_Quantity", DisplayName = Label("Quantity") },
            });
            var total = new MoneyAttributeMetadata
            {
                LogicalName = "new_total", SchemaName = "new_total", DisplayName = Label("Total"), PrecisionSource = 0, Precision = 2,
                SourceType = ComputedColumnRules.Calculated,
            };
            ComputedColumnRules.SetFormula(total, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "calculated", "arithmetic-money.xaml"))
                .Replace("xrmv_calcorder", table).Replace("xrmv_", "new_"));
            await admin.ExecuteAsync(new CreateAttributeRequest { EntityName = table, Attribute = total });
            return table;
        }
        finally
        {
            CalculatedLock.Release();
        }
    }
}
