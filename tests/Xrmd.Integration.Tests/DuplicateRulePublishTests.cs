using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Duplicates;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.15, task 2: PublishDuplicateRule (a system job of operationtype 7 that fills <c>meta.matchcode_{rule}</c>),
/// UnpublishDuplicateRule, the match codes every core write keeps (RF2, RF4) and the switch-offs that unpublish (D8 to D11).
/// The fixture host's worker is disabled: the jobs run on a worker of the test's own (<see cref="AsyncOperationWorker.RunOnceAsync"/>).
/// </summary>
[Collection("xrmd")]
public sealed class DuplicateRulePublishTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Contact = DuplicateDetectionTestKit.Contact;
    private const string Email = DuplicateDetectionTestKit.Email;
    private readonly DuplicateDetectionTestKit _kit = new(fx);

    private string Run => _kit.Run;
    private List<Guid> Rules => _kit.Rules;
    private IMetadataStore Store => _kit.Store;
    private OrganizationService Admin => _kit.Admin;

    public Task InitializeAsync() => _kit.InitializeAsync(onlineCreateUpdate: false, Contact);

    public Task DisposeAsync() => _kit.DisposeAsync();

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private string Mail(string label) => _kit.Mail(label);

    private Task SetFlagAsync(string table, bool value) => _kit.SetFlagAsync(table, value);

    private Task SetOrganizationAsync(bool enabled) => _kit.SetOrganizationAsync(enabled);

    private Task<Guid> RuleAsync(string attribute = Email, string table = Contact, bool excludeInactive = false, bool withCondition = true,
        DuplicateDetectionSchema.DuplicateOperator op = DuplicateDetectionSchema.DuplicateOperator.ExactMatch) =>
        _kit.RuleAsync(attribute, table, excludeInactive, withCondition, op);

    private Task<Guid> PublishAsync(Guid rule) => _kit.PublishAsync(rule);

    private Task PublishAndRunAsync(Guid rule) => _kit.PublishAndRunAsync(rule);

    private AsyncOperationWorker NewWorker() => _kit.NewWorker();

    private Task RunJobAsync(Guid job) => _kit.RunJobAsync(job);

    private Task<(int State, int Status, string? Message)> JobAsync(Guid job) => _kit.JobAsync(job);

    private async Task<(int State, int Status)> RuleStateAsync(Guid rule)
    {
        var entity = await Admin.RetrieveAsync(DuplicateDetectionSchema.Rule, rule, new Microsoft.Xrm.Sdk.Query.ColumnSet("statecode", "statuscode"));
        return (entity.GetAttributeValue<OptionSetValue>("statecode").Value, entity.GetAttributeValue<OptionSetValue>("statuscode").Value);
    }

    private static string Table(Guid rule) => DuplicateDetectionSchema.MatchCodeTableName(rule);

    private async Task<bool> TableExistsAsync(Guid rule) =>
        await TestSql.TableExistsAsync(fx.ConnectionString, "meta", Table(rule));

    private async Task<int> RegistryRowsAsync(Guid rule) =>
        Convert.ToInt32(await ScalarAsync("SELECT COUNT(*) FROM meta.matchcode_table WHERE duplicateruleid = @id", ("@id", rule)));

    private Task<byte[]?> CodeAsync(Guid rule, Guid record) => _kit.CodeAsync(rule, record);

    private Task<int> CodeCountAsync(Guid rule) => _kit.CodeCountAsync(rule);

    /// <summary>The code of a contact whose <paramref name="attribute"/> is <paramref name="value"/>, for a one-condition exact rule.</summary>
    private byte[]? Expected(object? value, string attribute = Email, string table = Contact) =>
        MatchCode.Compute(
            new PublishedRule(Guid.Empty, table, table, false, false, 2,
                [new DuplicateRuleCondition(Guid.Empty, attribute, attribute, DuplicateDetectionSchema.DuplicateOperator.ExactMatch, null, false)]),
            MatchSide.Matching, _ => value, name => Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == name));

    private Task<Guid> ContactAsync(string label, string? email) =>
        Admin.CreateAsync(new Entity(Contact) { ["lastname"] = label + " " + Run, [Email] = email });

    private Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters) => _kit.ScalarAsync(sql, parameters);

    private Task ExecAsync(string sql) => _kit.ExecAsync(sql);

    /// <summary>A custom table with duplicate detection on and a text column <c>new_code</c>.</summary>
    private async Task<string> CustomTableAsync(string label)
    {
        var name = $"new_{label}{Run}";
        var entity = TestHelpers.Entity(name, name, "Table " + name);
        entity.IsDuplicateDetectionEnabled = new BooleanManagedProperty(true);
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new StringAttributeMetadata { LogicalName = "new_code", SchemaName = "new_Code", MaxLength = 50, DisplayName = Label("Code") },
        });
        return name;
    }

    private async Task DeleteCustomTableAsync(string name)
    {
        foreach (var rule in Rules.ToList())
            if (await ScalarAsync("SELECT COUNT(*) FROM data.duplicaterule WHERE duplicateruleid = @id AND baseentityname = @name",
                    ("@id", rule), ("@name", name)) is 1 or 1L)
                await Admin.DeleteAsync(DuplicateDetectionSchema.Rule, rule);
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name });
    }

    // ---- publishing ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Publish_returns_job_and_rule_ends_published()
    {
        var ada = await ContactAsync("Ada", Mail("ada"));
        var grace = await ContactAsync("Grace", "  " + Mail("Grace").ToUpperInvariant() + " ");
        await ContactAsync("Nobody", null);
        var rule = await RuleAsync();

        var job = await PublishAsync(rule);

        (await ScalarAsync("SELECT operationtype FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", job))).Should().Be(7);
        (await ScalarAsync("SELECT regardingobjectid FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", job))).Should().Be(rule);
        (await JobAsync(job)).State.Should().Be(0);
        (await RuleStateAsync(rule)).Should().Be((0, 1), "the rule is Publishing until its job ends");
        (await TableExistsAsync(rule)).Should().BeTrue();
        (await RegistryRowsAsync(rule)).Should().Be(1);

        await RunJobAsync(job);

        (await JobAsync(job)).Should().Be((3, 30, (string?)null));
        (await RuleStateAsync(rule)).Should().Be((1, 2));
        var withEmail = Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM data.contact WHERE {Email} IS NOT NULL AND {TestSql.Dialect.TextLength($"TRIM({Email})")} > 0"));
        (await CodeCountAsync(rule)).Should().Be(withEmail, "one code per contact with an email");
        (await CodeAsync(rule, ada)).Should().Equal(Expected(Mail("ada")));
        (await CodeAsync(rule, grace)).Should().Equal(Expected(Mail("grace")), "trimmed and case-folded");
        var stored = await Admin.RetrieveAsync(DuplicateDetectionSchema.Rule, rule, new Microsoft.Xrm.Sdk.Query.ColumnSet(
            DuplicateDetectionSchema.BaseEntityMatchCodeTable, DuplicateDetectionSchema.MatchingEntityMatchCodeTable));
        stored.GetAttributeValue<string>(DuplicateDetectionSchema.MatchingEntityMatchCodeTable).Should().Be(Table(rule));
        stored.GetAttributeValue<string>(DuplicateDetectionSchema.BaseEntityMatchCodeTable).Should().Be(Table(rule));
    }

    [Fact]
    public async Task Publish_refusals()
    {
        async Task RefusedAsync(Guid rule, string because)
        {
            await BadRequestAsync(() => PublishAsync(rule));
            (await RuleStateAsync(rule)).Should().Be((0, 0), because);
            (await TableExistsAsync(rule)).Should().BeFalse(because);
            (await ScalarAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE regardingobjectid = @id", ("@id", rule))).Should().Be(0, because);
        }

        var missing = await FaultAsync(() => PublishAsync(Guid.NewGuid()));
        missing.HttpStatus.Should().Be(404);

        var rule = await RuleAsync();
        await SetOrganizationAsync(false);
        await RefusedAsync(rule, "duplicate detection is off for the organization");
        await SetOrganizationAsync(true);

        await SetFlagAsync(Contact, false);
        await RefusedAsync(rule, "the table does not have IsDuplicateDetectionEnabled");
        await SetFlagAsync(Contact, true);

        await RefusedAsync(await RuleAsync(withCondition: false), "a rule needs a condition");

        for (var i = 0; i < DuplicateDetectionSchema.MaxPublishedPerBaseTable; i++) await PublishAsync(await RuleAsync());
        await RefusedAsync(rule, "five rules are already published on contact");

        var table = await CustomTableAsync("states");
        var state = Store.GetEntity(table)!.Attributes.OfType<StateAttributeMetadata>().Single();
        // The store hands out its shared definitions: a third state is added for this table only, then removed.
        InMemoryMetadata.ChangeColumn(Store, table, state.LogicalName, c => ((Xrmd.Model.Metadata.StateColumn)c).OptionSet!.Options!.Add(new Xrmd.Model.Metadata.StateOption { Value = 2, DefaultStatus = 3, Label = new Xrmd.Model.Metadata.LabelSet { LocalizedLabels = [new Xrmd.Model.Metadata.LocalizedText { Label = "Pending", LanguageCode = 1033 }] } }));
        try
        {
            await RefusedAsync(await RuleAsync("new_code", table, excludeInactive: true), "only 0 is active, so other states cannot be excluded");
        }
        finally
        {
            InMemoryMetadata.ChangeColumn(Store, table, state.LogicalName, c => ((Xrmd.Model.Metadata.StateColumn)c).OptionSet!.Options!.RemoveAll(o => o.Value == 2));
            await DeleteCustomTableAsync(table);
        }
    }

    [Fact]
    public async Task Publish_and_unpublish_need_write_on_the_rule()
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ("prvReadDuplicateRule", DepthMask.Global));
        var rule = await RuleAsync();
        var reader = ServiceFor(fx, objectId);

        (await FaultAsync(() => reader.ExecuteAsync(new PublishDuplicateRuleRequest { DuplicateRuleId = rule }))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => reader.ExecuteAsync(new UnpublishDuplicateRuleRequest { DuplicateRuleId = rule }))).HttpStatus.Should().Be(403);
        (await RuleStateAsync(rule)).Should().Be((0, 0));
    }

    [Fact]
    public async Task Writes_during_publishing_keep_fresh_codes()
    {
        var untouched = await ContactAsync("Untouched", Mail("untouched"));
        var changed = await ContactAsync("Changed", Mail("old"));
        var rule = await RuleAsync();

        var job = await PublishAsync(rule); // the worker has not run yet: the rule is Publishing
        await Admin.UpdateAsync(new Entity(Contact, changed) { [Email] = Mail("new") });
        (await CodeAsync(rule, changed)).Should().Equal(Expected(Mail("new")), "a write while publishing maintains the code");
        var created = await ContactAsync("Created", Mail("created"));
        (await CodeAsync(rule, created)).Should().Equal(Expected(Mail("created")));

        await RunJobAsync(job);

        (await RuleStateAsync(rule)).Should().Be((1, 2));
        (await CodeAsync(rule, changed)).Should().Equal(Expected(Mail("new")), "the backfill never overwrites a fresher code");
        (await CodeAsync(rule, untouched)).Should().Equal(Expected(Mail("untouched")));
        (await CodeAsync(rule, created)).Should().Equal(Expected(Mail("created")));
    }

    [Fact]
    public async Task Publish_of_a_rule_being_published_is_refused_and_published_rule_is_rebuilt()
    {
        var id = await ContactAsync("Twice", Mail("twice"));
        var rule = await RuleAsync();
        var first = await PublishAsync(rule);

        (await BadRequestAsync(() => PublishAsync(rule))).Should().Contain("being published");
        (await ScalarAsync("SELECT COUNT(*) FROM data.asyncoperation WHERE regardingobjectid = @id", ("@id", rule))).Should().Be(1);

        await RunJobAsync(first);
        (await RuleStateAsync(rule)).Should().Be((1, 2));

        // D10: a Published rule is republished (rebuilt).
        var second = await PublishAsync(rule);
        (await RuleStateAsync(rule)).Should().Be((0, 1));
        await RunJobAsync(second);
        (await JobAsync(second)).Should().Be((3, 30, (string?)null));
        (await RuleStateAsync(rule)).Should().Be((1, 2));
        (await CodeAsync(rule, id)).Should().Equal(Expected(Mail("twice")));
    }

    [Fact]
    public async Task Concurrent_publishes_on_one_table_keep_the_limit_without_deadlock()
    {
        var rules = new List<Guid>();
        for (var i = 0; i < DuplicateDetectionSchema.MaxPublishedPerBaseTable + 1; i++) rules.Add(await RuleAsync());

        var outcomes = await Task.WhenAll(rules.Select(async rule =>
        {
            try
            {
                await ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(new PublishDuplicateRuleRequest { DuplicateRuleId = rule });
                return 200;
            }
            catch (Xrmd.Core.Errors.XrmdFaultException ex)
            {
                return ex.HttpStatus;
            }
        }));

        outcomes.Count(o => o == 200).Should().Be(DuplicateDetectionSchema.MaxPublishedPerBaseTable);
        outcomes.Count(o => o == 400).Should().Be(1, "the sixth is refused, and no publish fails otherwise (e.g. a deadlock)");
    }

    /// <summary>
    /// A writer whose transaction is open across the publish (its rule cache predates it, so it writes no code) and across the
    /// start of the job: the backfill waits for its row and codes the committed value (RCSI would read the old one).
    /// </summary>
    private async Task WriterOpenAcrossPublishAsync(Func<DbConnection, DbTransaction, Task> write, Func<Guid, Task> verify)
    {
        var rule = await RuleAsync();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var tx = await conn.BeginTransactionAsync();
        await write(conn, tx);

        var job = await PublishAsync(rule);
        var running = RunJobAsync(job);
        await Task.Delay(1500);
        running.IsCompleted.Should().BeFalse("the backfill waits for the open write");
        await tx.CommitAsync();
        await running;

        (await JobAsync(job)).Should().Be((3, 30, (string?)null));
        await verify(rule);
    }

    /// <summary>
    /// Plan 2.7b, Task 4 fix round 1: a writer whose transaction coded one row keeps the rule's registry row locked to its commit,
    /// so an unpublish (registry row, then table) waits for it instead of holding the registry row while it waits for the table
    /// the writer holds. The writer codes a second row, commits, and the unpublish ends after it: no deadlock on either engine.
    /// </summary>
    [Fact]
    public async Task Unpublish_waits_for_an_open_writer_of_two_rows_without_deadlock()
    {
        var rule = await RuleAsync();
        await PublishAndRunAsync(rule);
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        Guid? a = null, b = null;
        try
        {
            await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
            {
                var session = new DataSession(conn, await conn.BeginTransactionAsync());
                a = await core.CreateAsync(session, caller, new Entity(Contact) { ["lastname"] = "Writer A " + Run, [Email] = Mail("writer-a") }, default);

                var unpublish = Admin.ExecuteAsync(new UnpublishDuplicateRuleRequest { DuplicateRuleId = rule });
                await Task.Delay(1500);
                unpublish.IsCompleted.Should().BeFalse("the unpublish waits for the writer's lock on the registry row");

                b = await core.CreateAsync(session, caller, new Entity(Contact) { ["lastname"] = "Writer B " + Run, [Email] = Mail("writer-b") }, default);
                await session.Transaction!.CommitAsync();
                session.Committed();
                await unpublish;
            }

            (await RuleStateAsync(rule)).Should().Be((0, 0));
            (await TableExistsAsync(rule)).Should().BeFalse();
        }
        finally
        {
            foreach (var id in new[] { a, b })
                if (id is { } contact) await Admin.DeleteAsync(Contact, contact);
        }
    }

    /// <summary>
    /// Plan 2.7b, Task 4 fix round 2: a single-rule unpublish holds the rule's row in an open transaction while the publish job
    /// completes. The completion locks the rule's row before the registry row (the order of an unpublish), so it waits, then
    /// finds the rule no longer Publishing: no deadlock (40P01, 1205) on either engine, and the rule ends Unpublished.
    /// </summary>
    [Fact]
    public async Task Publish_completion_waits_for_an_open_unpublish_without_deadlock()
    {
        var rule = await RuleAsync();
        var job = await PublishAsync(rule);
        var publisher = fx.App.Services.GetRequiredService<DuplicateRulePublisher>();
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        var d = TestSql.Dialect;

        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var tx = await conn.BeginTransactionAsync();
            var session = new DataSession(conn, tx);
            // The first step of UnpublishAsync: the rule's row, locked.
            await using (var lockRule = TestSql.Command(conn, tx,
                             $"SELECT duplicateruleid FROM data.duplicaterule{d.LockHint(RowLock.UpdateRow)} WHERE duplicateruleid = @id{d.LockClause(RowLock.UpdateRow)}",
                             ("@id", rule)))
                await lockRule.ExecuteScalarAsync();

            var completion = Task.Run(async () =>
            {
                await using var other = await TestProvider.OpenAsync(fx.ConnectionString);
                await using var otherTx = await other.BeginTransactionAsync();
                var done = await publisher.CompletePublishAsync(new DataSession(other, otherTx), rule, job, default);
                await otherTx.CommitAsync();
                return done;
            });
            await Task.Delay(1500);
            completion.IsCompleted.Should().BeFalse("the completion waits for the unpublish's lock on the rule's row");

            await publisher.UnpublishAsync(session, caller, rule, default);
            await tx.CommitAsync();
            session.Committed();
            (await completion).Should().BeFalse("the rule is no longer Publishing for the job");
        }

        (await RuleStateAsync(rule)).Should().Be((0, 0));
        (await TableExistsAsync(rule)).Should().BeFalse();
        (await RegistryRowsAsync(rule)).Should().Be(0);
    }

    /// <summary>
    /// Plan 2.7b, Task 4 fix round 3: an unpublish of every rule of a table holds the table write lock of the rules (PostgreSQL)
    /// while the publish job completes, then runs its locking read and commits. The completion takes the table lock its UPDATE
    /// needs before it locks the rule's row (the order table, then row), so neither waits for the other in a cycle: no deadlock
    /// (40P01, 1205) on either engine, and the rule ends Unpublished.
    /// </summary>
    [Fact]
    public async Task Publish_completion_waits_for_an_open_unpublish_of_a_table_without_deadlock()
    {
        var rule = await RuleAsync();
        var job = await PublishAsync(rule);
        var publisher = fx.App.Services.GetRequiredService<DuplicateRulePublisher>();

        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var tx = await conn.BeginTransactionAsync();
            var session = new DataSession(conn, tx);
            await session.Dialect.LockTableWritesAsync(session, "data", DuplicateDetectionSchema.Rule, default);

            var completion = Task.Run(async () =>
            {
                await using var other = await TestProvider.OpenAsync(fx.ConnectionString);
                await using var otherTx = await other.BeginTransactionAsync();
                var done = await publisher.CompletePublishAsync(new DataSession(other, otherTx), rule, job, default);
                await otherTx.CommitAsync();
                return done;
            });
            await Task.Delay(1500);

            (await DuplicateDetectionSchema.UnpublishRulesAsync(session, Contact, default)).Should().NotBeNull();
            await tx.CommitAsync();
            session.Committed();
            await completion;
        }

        (await RuleStateAsync(rule)).Should().Be((0, 0), "the unpublish ran before or after the completion, never neither");
        (await TableExistsAsync(rule)).Should().BeFalse();
        (await RegistryRowsAsync(rule)).Should().Be(0);
    }

    [Fact]
    public async Task Backfill_waits_for_an_open_update_and_codes_its_new_value()
    {
        var id = await ContactAsync("Open", Mail("before"));
        await WriterOpenAcrossPublishAsync(async (conn, tx) =>
        {
            await using var cmd = TestSql.Command(conn, tx, $"UPDATE data.contact SET {Email} = @e WHERE contactid = @id", ("@e", Mail("after")), ("@id", id));
            await cmd.ExecuteNonQueryAsync();
        }, async rule => (await CodeAsync(rule, id)).Should().Equal(Expected(Mail("after"))));
    }

    [Fact]
    public async Task Backfill_waits_for_an_open_create_and_codes_it()
    {
        var id = Guid.NewGuid();
        await WriterOpenAcrossPublishAsync(async (conn, tx) =>
        {
            await using var cmd = TestSql.Command(conn, tx,
                $"INSERT INTO data.contact (contactid, lastname, {Email}, statecode, statuscode) VALUES (@id, @name, @e, 0, 1)",
                ("@id", id), ("@name", "Open create " + Run), ("@e", Mail("created-open")));
            await cmd.ExecuteNonQueryAsync();
        }, async rule => (await CodeAsync(rule, id)).Should().Equal(Expected(Mail("created-open"))));
        await Admin.DeleteAsync(Contact, id);
    }

    [Fact]
    public async Task Backfill_keeps_its_read_rows_locked_until_the_batch_commits()
    {
        var blanked = await ContactAsync("Blanked", Mail("blanked"));
        var deleted = await ContactAsync("Deleted", Mail("deleted"));
        var rule = await RuleAsync();
        var job = await PublishAsync(rule);
        var store = fx.App.Services.GetRequiredService<MatchCodeStore>();
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.AfterBackfillReadForTest = async id =>
        {
            if (id != rule) return;
            read.TrySetResult();
            await resume.Task;
        };
        try
        {
            var running = RunJobAsync(job);
            await read.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Writers with a fresh cache (the job invalidated it): their upkeep finds no code yet to remove.
            var blank = Admin.UpdateAsync(new Entity(Contact, blanked) { [Email] = null });
            var delete = Admin.DeleteAsync(Contact, deleted);
            await Task.Delay(1500);
            blank.IsCompleted.Should().BeFalse("the backfill holds its read rows until its batch commits");
            delete.IsCompleted.Should().BeFalse("the backfill holds its read rows until its batch commits");

            resume.TrySetResult();
            await running;
            await blank;
            await delete;
        }
        finally
        {
            resume.TrySetResult();
            store.AfterBackfillReadForTest = null;
        }

        (await JobAsync(job)).Should().Be((3, 30, (string?)null));
        (await CodeAsync(rule, blanked)).Should().BeNull("the email was blanked after the backfill read it");
        (await CodeAsync(rule, deleted)).Should().BeNull("the row was deleted after the backfill read it");
    }

    [Fact]
    public async Task Stale_publish_job_does_not_touch_a_later_publish()
    {
        // Final review I1: unpublish, edit and republish while the first job is between its read and its inserts.
        var phone = "telephone1";
        var a = await Admin.CreateAsync(new Entity(Contact) { ["lastname"] = "StaleA " + Run, [Email] = Mail("stale-a"), [phone] = "555-0101" });
        var b = await Admin.CreateAsync(new Entity(Contact) { ["lastname"] = "StaleB " + Run, [Email] = Mail("stale-b"), [phone] = "555-0102" });
        var c = await Admin.CreateAsync(new Entity(Contact) { ["lastname"] = "StaleC " + Run, [Email] = Mail("stale-c") });
        var rule = await RuleAsync();
        var first = await PublishAsync(rule);
        var store = fx.App.Services.GetRequiredService<MatchCodeStore>();
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        store.AfterBackfillReadForTest = async id =>
        {
            if (id != rule || Interlocked.Exchange(ref paused, 1) == 1) return;
            read.TrySetResult();
            await resume.Task;
        };
        Guid second;
        try
        {
            var running = RunJobAsync(first);
            await read.Task.WaitAsync(TimeSpan.FromSeconds(30));

            await Admin.ExecuteAsync(new UnpublishDuplicateRuleRequest { DuplicateRuleId = rule });
            var condition = (Guid)(await ScalarAsync(
                "SELECT duplicateruleconditionid FROM data.duplicaterulecondition WHERE regardingobjectid = @id", ("@id", rule)))!;
            await Admin.UpdateAsync(new Entity(DuplicateDetectionSchema.Condition, condition)
            {
                ["baseattributename"] = phone, ["matchingattributename"] = phone,
            });
            second = await PublishAsync(rule);

            resume.TrySetResult();
            await running;
        }
        finally
        {
            resume.TrySetResult();
            store.AfterBackfillReadForTest = null;
        }

        (await JobAsync(first)).Should().Be((3, 30, DuplicateRulePublishJob.SupersededMessage));
        (await RuleStateAsync(rule)).Should().Be((0, 1), "the stale job leaves the later publish alone");
        (await CodeCountAsync(rule)).Should().Be(0, "the stale job wrote no code of the old condition into the new table");

        await RunJobAsync(second);
        (await JobAsync(second)).Should().Be((3, 30, (string?)null));
        (await RuleStateAsync(rule)).Should().Be((1, 2));
        (await CodeAsync(rule, a)).Should().Equal(Expected("555-0101", phone));
        (await CodeAsync(rule, b)).Should().Equal(Expected("555-0102", phone));
        (await CodeAsync(rule, c)).Should().BeNull("the row has no phone");
    }

    [Fact]
    public async Task Stale_publish_job_failing_does_not_reset_a_later_publish()
    {
        var rule = await RuleAsync();
        var first = await PublishAsync(rule);
        await Admin.ExecuteAsync(new UnpublishDuplicateRuleRequest { DuplicateRuleId = rule });
        var second = await PublishAsync(rule);
        var publisher = fx.App.Services.GetRequiredService<DuplicateRulePublisher>();

        // The failure path of the first job (its reset) and its owner check leave the second publish as it is.
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var tx = await conn.BeginTransactionAsync();
            (await publisher.ResetFailedPublishAsync(new DataSession(conn, tx), rule, first, default)).Should().BeFalse();
            (await publisher.CompletePublishAsync(new DataSession(conn, tx), rule, first, default)).Should().BeFalse();
            await tx.CommitAsync();
        }
        (await RuleStateAsync(rule)).Should().Be((0, 1));
        (await TableExistsAsync(rule)).Should().BeTrue();

        await RunJobAsync(second);
        (await JobAsync(first)).Should().Be((3, 30, DuplicateRulePublishJob.SupersededMessage));
        (await JobAsync(second)).Should().Be((3, 30, (string?)null));
        (await RuleStateAsync(rule)).Should().Be((1, 2));
    }

    [Fact]
    public async Task Cancelled_publish_job_resets_the_rule()
    {
        var rule = await RuleAsync();
        var job = await PublishAsync(rule);

        await Admin.UpdateAsync(new Entity("asyncoperation", job) { ["statecode"] = new OptionSetValue(3), ["statuscode"] = new OptionSetValue(32) });

        (await RuleStateAsync(rule)).Should().Be((0, 0), "a cancelled publish job never runs");
        (await TableExistsAsync(rule)).Should().BeFalse();
        (await RegistryRowsAsync(rule)).Should().Be(0);

        // Deleting the cancelled job changes nothing more, and the rule can be published again at once.
        await Admin.DeleteAsync("asyncoperation", job);
        await PublishAndRunAsync(rule);
        (await RuleStateAsync(rule)).Should().Be((1, 2));
    }

    [Fact]
    public async Task Publish_job_of_a_disabled_owner_resets_the_rule()
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await AssignRoleAsync(fx, user, StandardRoles.SystemAdministrator);
        var rule = await RuleAsync();
        var job = (Guid)(await ServiceFor(fx, objectId).ExecuteAsync(new PublishDuplicateRuleRequest { DuplicateRuleId = rule })).Results["JobId"];
        await ExecAsync($"UPDATE data.systemuser SET isdisabled = 1 WHERE systemuserid = '{user}'");
        Cache(fx).Invalidate();

        await RunJobAsync(job);

        var outcome = await JobAsync(job);
        (outcome.State, outcome.Status).Should().Be((3, 31));
        (await RuleStateAsync(rule)).Should().Be((0, 0));
        (await TableExistsAsync(rule)).Should().BeFalse();
        (await RegistryRowsAsync(rule)).Should().Be(0);
    }

    [Fact]
    public async Task Startup_resets_publishing_rules_without_a_live_job()
    {
        var stranded = await RuleAsync();
        var strandedJob = await PublishAsync(stranded);
        await ExecAsync($"UPDATE data.asyncoperation SET statecode = 3, statuscode = 31 WHERE asyncoperationid = '{strandedJob}'");
        var waiting = await RuleAsync();
        await PublishAsync(waiting);

        await DuplicateRulePublisher.ResetStrandedPublishesAsync(fx.App.Services.GetRequiredService<IDbConnectionFactory>(), default);

        (await RuleStateAsync(stranded)).Should().Be((0, 0));
        (await TableExistsAsync(stranded)).Should().BeFalse();
        (await RegistryRowsAsync(stranded)).Should().Be(0);
        (await RuleStateAsync(waiting)).Should().Be((0, 1), "its job is still queued");
        (await TableExistsAsync(waiting)).Should().BeTrue();
    }

    [Fact]
    public async Task Failed_publish_job_resets_rule()
    {
        var table = await CustomTableAsync("fail");
        try
        {
            await Admin.CreateAsync(new Entity(table) { ["new_name"] = "one", ["new_code"] = "A1" });
            var rule = await RuleAsync("new_code", table);
            var job = await PublishAsync(rule);
            // The column goes under the job: reading the rows fails.
            await ExecAsync($"ALTER TABLE data.{table} DROP COLUMN new_code");
            try
            {
                await RunJobAsync(job);
            }
            finally
            {
                await ExecAsync(TestSql.Dialect.AddColumnIfMissing("data", table, "new_code", TestSql.Dialect.ColumnType(SqlColumnKind.String, 50)));
            }

            var outcome = await JobAsync(job);
            (outcome.State, outcome.Status).Should().Be((3, 31));
            outcome.Message.Should().NotBeNullOrEmpty();
            (await RuleStateAsync(rule)).Should().Be((0, 0));
            (await TableExistsAsync(rule)).Should().BeFalse();
            (await RegistryRowsAsync(rule)).Should().Be(0);
        }
        finally
        {
            await DeleteCustomTableAsync(table);
        }
    }

    // ---- upkeep -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_update_delete_maintain_codes()
    {
        var rule = await RuleAsync();
        await PublishAndRunAsync(rule);

        var id = await ContactAsync("Kept", Mail("first"));
        (await CodeAsync(rule, id)).Should().Equal(Expected(Mail("first")));

        await Admin.UpdateAsync(new Entity(Contact, id) { [Email] = Mail("second") });
        (await CodeAsync(rule, id)).Should().Equal(Expected(Mail("second")));

        await Admin.UpdateAsync(new Entity(Contact, id) { ["lastname"] = "Renamed " + Run });
        (await CodeAsync(rule, id)).Should().Equal(Expected(Mail("second")), "an unrelated column leaves the code");

        await Admin.UpdateAsync(new Entity(Contact, id) { [Email] = "   " });
        (await CodeAsync(rule, id)).Should().BeNull("a blank value has no code");

        await Admin.UpdateAsync(new Entity(Contact, id) { [Email] = Mail("third") });
        (await CodeAsync(rule, id)).Should().Equal(Expected(Mail("third")));

        await Admin.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(Contact, id), State = new OptionSetValue(1), Status = new OptionSetValue(2),
        });
        (await CodeAsync(rule, id)).Should().Equal(Expected(Mail("third")), "the state does not change the code");

        await Admin.DeleteAsync(Contact, id);
        (await CodeAsync(rule, id)).Should().BeNull();
    }

    [Fact]
    public async Task Cascade_delete_removes_codes()
    {
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "Parent " + Run });
        var child = await Admin.CreateAsync(new Entity(Contact)
        {
            ["lastname"] = "Child " + Run, [Email] = Mail("child"), ["parentcustomerid"] = new EntityReference("account", account),
        });
        var rule = await RuleAsync();
        await PublishAndRunAsync(rule);
        (await CodeAsync(rule, child)).Should().NotBeNull();

        await Admin.DeleteAsync("account", account); // contact_customer_accounts: Delete Cascade

        (await ScalarAsync("SELECT COUNT(*) FROM data.contact WHERE contactid = @id", ("@id", child))).Should().Be(0);
        (await CodeAsync(rule, child)).Should().BeNull();
    }

    [Fact]
    public async Task Remove_link_refreshes_codes()
    {
        var parent = await ContactAsync("Parent", null);
        var child = await Admin.CreateAsync(new Entity(Contact)
        {
            ["lastname"] = "Child " + Run, ["parentcustomerid"] = new EntityReference(Contact, parent),
        });
        var rule = await RuleAsync("parentcustomerid");
        await PublishAndRunAsync(rule);
        (await CodeAsync(rule, child)).Should().Equal(Expected(new EntityReference(Contact, parent), "parentcustomerid"));

        await Admin.DeleteAsync(Contact, parent); // contact_customer_contacts: Delete RemoveLink

        (await CodeAsync(rule, child)).Should().BeNull("the cleared lookup is blank");
    }

    [Fact]
    public async Task Assign_refreshes_codes_on_owner_condition()
    {
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        await AssignRoleAsync(fx, user, StandardRoles.SystemAdministrator);
        var id = await ContactAsync("Owned", null);
        var rule = await RuleAsync("ownerid");
        await PublishAndRunAsync(rule);
        var before = await CodeAsync(rule, id);
        before.Should().NotBeNull();

        await Admin.ExecuteAsync(new AssignRequest { Target = new EntityReference(Contact, id), Assignee = new EntityReference("systemuser", user) });

        (await CodeAsync(rule, id)).Should().Equal(Expected(new EntityReference("systemuser", user), "ownerid")).And.NotEqual(before);
    }

    [Fact]
    public async Task Codes_follow_nested_plugin_update()
    {
        var rule = await RuleAsync();
        await PublishAndRunAsync(rule);
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.UpdateAttributePlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", Contact, 40, config: $"{Email}={Mail("nested")}");
        try
        {
            var id = await ContactAsync("Nested", Mail("outer"));

            (await CodeAsync(rule, id)).Should().Equal(Expected(Mail("nested")), "the plug-in's Update at stage 40 wrote the final value");
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
    }

    // ---- unpublishing -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unpublish_drops_codes_and_sets_unpublished()
    {
        await ContactAsync("Coded", Mail("coded"));
        var rule = await RuleAsync();
        await PublishAndRunAsync(rule);
        (await CodeCountAsync(rule)).Should().BePositive();

        var response = await Admin.ExecuteAsync(new UnpublishDuplicateRuleRequest { DuplicateRuleId = rule });

        response.Should().BeOfType<UnpublishDuplicateRuleResponse>();
        (await RuleStateAsync(rule)).Should().Be((0, 0));
        (await TableExistsAsync(rule)).Should().BeFalse();
        (await RegistryRowsAsync(rule)).Should().Be(0);
        var stored = await Admin.RetrieveAsync(DuplicateDetectionSchema.Rule, rule,
            new Microsoft.Xrm.Sdk.Query.ColumnSet(DuplicateDetectionSchema.MatchingEntityMatchCodeTable));
        stored.Contains(DuplicateDetectionSchema.MatchingEntityMatchCodeTable).Should().BeFalse();

        // Writes no longer keep codes, and the rule can be edited again.
        await ContactAsync("After", Mail("after"));
        await Admin.UpdateAsync(new Entity(DuplicateDetectionSchema.Rule, rule) { ["description"] = "Edited" });
        (await FaultAsync(() => Admin.ExecuteAsync(new UnpublishDuplicateRuleRequest { DuplicateRuleId = Guid.NewGuid() }))).HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task Disabling_org_switch_unpublishes_all()
    {
        var published = await RuleAsync();
        await PublishAndRunAsync(published);
        var publishing = await RuleAsync();
        await PublishAsync(publishing);

        await SetOrganizationAsync(false);

        foreach (var rule in new[] { published, publishing })
        {
            (await RuleStateAsync(rule)).Should().Be((0, 0));
            (await TableExistsAsync(rule)).Should().BeFalse();
            (await RegistryRowsAsync(rule)).Should().Be(0);
        }

        await SetOrganizationAsync(true);
        (await RuleStateAsync(published)).Should().Be((0, 0), "turning the switch back on republishes nothing");
    }

    [Fact]
    public async Task Disabling_table_flag_unpublishes_its_rules()
    {
        await SetFlagAsync("account", true);
        try
        {
            var onContact = await RuleAsync();
            await PublishAndRunAsync(onContact);
            var onAccount = await RuleAsync("name", "account");
            await PublishAndRunAsync(onAccount);

            await SetFlagAsync(Contact, false);

            (await RuleStateAsync(onContact)).Should().Be((0, 0));
            (await TableExistsAsync(onContact)).Should().BeFalse();
            (await RegistryRowsAsync(onContact)).Should().Be(0);
            (await RuleStateAsync(onAccount)).Should().Be((1, 2), "a rule of another table stays published");
            (await TableExistsAsync(onAccount)).Should().BeTrue();

            // Writes to the table no longer keep codes for the unpublished rule (its table is gone).
            await ContactAsync("Disabled", Mail("disabled"));
        }
        finally
        {
            foreach (var rule in Rules.ToList())
                if (await ScalarAsync("SELECT COUNT(*) FROM data.duplicaterule WHERE duplicateruleid = @id AND baseentityname = 'account'", ("@id", rule)) is 1 or 1L)
                    await Admin.DeleteAsync(DuplicateDetectionSchema.Rule, rule);
            await SetFlagAsync("account", false);
        }
    }

    [Fact]
    public async Task Delete_published_rule_drops_table()
    {
        await ContactAsync("Coded", Mail("deleted"));
        var rule = await RuleAsync();
        await PublishAndRunAsync(rule);
        (await TableExistsAsync(rule)).Should().BeTrue();

        await Admin.DeleteAsync(DuplicateDetectionSchema.Rule, rule);

        (await TableExistsAsync(rule)).Should().BeFalse();
        (await RegistryRowsAsync(rule)).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.duplicaterulecondition WHERE regardingobjectid = @id", ("@id", rule))).Should().Be(0);
        await ContactAsync("After", Mail("after-delete")); // no code is written for the deleted rule
    }

    [Fact]
    public async Task Published_rule_cannot_be_edited()
    {
        var rule = await RuleAsync();
        await PublishAndRunAsync(rule);
        var condition = (Guid)(await ScalarAsync("SELECT duplicateruleconditionid FROM data.duplicaterulecondition WHERE regardingobjectid = @id", ("@id", rule)))!;

        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(DuplicateDetectionSchema.Rule, rule) { ["name"] = "Renamed" })))
            .Should().Contain("unpublish");
        await BadRequestAsync(() => Admin.UpdateAsync(new Entity(DuplicateDetectionSchema.Condition, condition) { ["baseattributename"] = "lastname", ["matchingattributename"] = "lastname" }));
        await BadRequestAsync(() => Admin.CreateAsync(new Entity(DuplicateDetectionSchema.Condition)
        {
            ["regardingobjectid"] = new EntityReference(DuplicateDetectionSchema.Rule, rule),
            ["baseattributename"] = "lastname", ["matchingattributename"] = "lastname", ["operatorcode"] = new OptionSetValue(0),
        }));
        await BadRequestAsync(() => Admin.DeleteAsync(DuplicateDetectionSchema.Condition, condition));
        (await RuleStateAsync(rule)).Should().Be((1, 2));
    }

    // ---- startup ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Startup_drops_orphan_matchcode_tables()
    {
        var kept = await RuleAsync();
        await PublishAndRunAsync(kept);
        var orphan = Guid.NewGuid();
        var guidType = TestSql.Dialect.ColumnType(SqlColumnKind.Guid);
        var codeType = TestSql.Dialect.ColumnType(SqlColumnKind.Binary, MatchCode.Length);
        await ExecAsync($"""
            CREATE TABLE meta.{Table(orphan)} (objectid {guidType} NOT NULL PRIMARY KEY, matchcode {codeType} NOT NULL);
            INSERT INTO meta.matchcode_table (duplicateruleid, table_name, matching_entity, created_on) VALUES ('{orphan}', N'{Table(orphan)}', N'contact', SYSUTCDATETIME());
            """);
        var unpublished = await RuleAsync();
        await ExecAsync($"CREATE TABLE meta.{Table(unpublished)} (objectid {guidType} NOT NULL PRIMARY KEY, matchcode {codeType} NOT NULL)");

        await MatchCodeStore.DropOrphanTablesAsync(fx.App.Services.GetRequiredService<IDbConnectionFactory>(), default);

        (await TableExistsAsync(orphan)).Should().BeFalse();
        (await RegistryRowsAsync(orphan)).Should().Be(0);
        (await TableExistsAsync(unpublished)).Should().BeFalse("its rule is not published");
        (await TableExistsAsync(kept)).Should().BeTrue();
        (await RegistryRowsAsync(kept)).Should().Be(1);
        (await TestSql.TableExistsAsync(fx.ConnectionString, "meta", "matchcode_table")).Should().BeTrue("the registry itself is not a match code table");
    }
}
