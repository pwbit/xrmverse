using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The recalculation jobs of rollup columns (plan 2.12b, Task 4; rulings R7, R8, R9; Review Focus 1 and 4): the Mass Calculate
/// Rollup Field job (operationtype 58) of each column and the Calculate Rollup Field job (57) of each table, queued by the metadata
/// store's notifications and run by a worker of the test's own on a clock the test moves. Each test has a host and an organization
/// database of its own (the jobs and the rollups on <c>account</c> would otherwise reach the other tests), with the table
/// <c>xrmv_calcorder</c> and the rollups of the parity export (tests/fixtures/rollup/real).
/// </summary>
[Collection("xrmd")]
public sealed class RollupJobTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Account = "account", Order = "xrmv_calcorder";
    private const int Calculate = 57, Mass = 58;
    private const string NotCancelled = "Rollup calculation jobs cannot be cancelled or deleted: postpone them instead.";

    private static readonly Guid SystemUserId = SeedOptions.DeriveSystemUserId(XrmdFixture.OrgName);

    private readonly ShiftedClock _clock = new();
    private WebApplicationFactory<Program> _app = null!;
    private string _connectionString = "";

    private IServiceProvider Services => _app.Services;
    private MetadataStore Store => (MetadataStore)Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => TestServices.NewOrganizationService(Services, fx.AdminObjectId);
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The system clock moved forward by <see cref="Shift"/>.</summary>
    private sealed class ShiftedClock : TimeProvider
    {
        public TimeSpan Shift { get; set; }

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Shift;
    }

    /// <summary>The limits of the host's rollup calculator, read when it is first resolved (a test may set them before).</summary>
    private Xrmd.Core.Rollups.RollupLimits _limits = new();

    public async Task InitializeAsync()
    {
        _app = fx.WithOwnDatabase(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(_clock);
            s.AddSingleton(_ => _limits);
        }));
        _connectionString = Services.GetRequiredService<IConfiguration>().GetConnectionString("Org")!;
        await Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await Store.AddAttributeAsync(Account, Named(new MoneyAttributeMetadata { PrecisionSource = 2 }, "xrmv_budget"));
        await Store.CreateEntityAsync(new EntityMetadata
        {
            LogicalName = Order, SchemaName = Order, OwnershipType = OwnershipTypes.UserOwned, DisplayName = SystemAttributes.CreateLabel(Order, 1033),
        }, new StringAttributeMetadata { LogicalName = "xrmv_name", SchemaName = "xrmv_name", MaxLength = 100 });
        await Store.AddAttributeAsync(Order, Named(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_amount"));
        await Store.AddAttributeAsync(Order, Named(new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime) { DateTimeBehavior = DateTimeBehavior.UserLocal }, "xrmv_startedon"));
        await Store.AddAttributeAsync(Order, Named(new PicklistAttributeMetadata { OptionSet = Options(1, 2, 3) }, "xrmv_status"));
        await TestEntities.InSessionAsync(_connectionString, s => Store.CreateOneToManyAsync(s, new OneToManyRelationshipMetadata
        {
            SchemaName = "xrmv_calcorder_AccountId_account", ReferencedEntity = Account, ReferencingEntity = Order, ReferencedAttribute = "accountid",
        }, Named(new LookupAttributeMetadata(), "xrmv_accountid"), CancellationToken.None));
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    // ---- definitions ------------------------------------------------------------------------------------------------------

    private static string Real(string column) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "rollup", "real", $"account-{column}.xaml"));

    private static T Named<T>(T a, string name) where T : AttributeMetadata
    {
        a.LogicalName = name;
        a.SchemaName = name;
        a.DisplayName = SystemAttributes.CreateLabel(name, 1033);
        return a;
    }

    private static T Rollup<T>(T a, string name) where T : AttributeMetadata
    {
        Named(a, name);
        a.SourceType = RollupColumns.Rollup;
        ComputedColumnRules.SetFormula(a, Real(name));
        return a;
    }

    private static OptionSetMetadata Options(params int[] values) =>
        new(new OptionMetadataCollection(values.Select(v => new OptionMetadata(SystemAttributes.CreateLabel("Option " + v, 1033), v)).ToList()))
        {
            IsGlobal = false, OptionSetType = OptionSetType.Picklist,
        };

    private static AttributeMetadata OrderTotal() => Rollup(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_ordertotal");
    private static AttributeMetadata OrderCount() => Rollup(new IntegerAttributeMetadata(), "xrmv_ordercount");
    private static AttributeMetadata LastOrder() =>
        Rollup(new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime) { DateTimeBehavior = DateTimeBehavior.UserLocal }, "xrmv_lastorder");
    private static AttributeMetadata TreeBudget() => Rollup(new MoneyAttributeMetadata { PrecisionSource = 2 }, "xrmv_treebudget");

    /// <summary>Adds <paramref name="rollups"/> to <c>account</c> and waits for the jobs to be told.</summary>
    private async Task AddAsync(params AttributeMetadata[] rollups)
    {
        foreach (var a in rollups) await Store.AddAttributeAsync(Account, a);
        await Store.RollupJobNotifications;
    }

    private async Task DeleteAsync(string attribute)
    {
        await TestEntities.InSessionAsync(_connectionString, s => Store.DeleteAttributeAsync(s, Account, attribute, CancellationToken.None));
        await Store.RollupJobNotifications;
    }

    // ---- rows -------------------------------------------------------------------------------------------------------------

    private Task<Guid> AccountAsync(string name, params (string Column, object Value)[] values)
    {
        var account = new Entity(Account) { ["name"] = name };
        foreach (var (column, value) in values) account[column] = value;
        return Admin.CreateAsync(account);
    }

    private Task<Guid> OrderAsync(Guid account, decimal amount, int status) => Admin.CreateAsync(new Entity(Order)
    {
        ["xrmv_name"] = "order", ["xrmv_accountid"] = new EntityReference(Account, account), ["xrmv_amount"] = amount,
        ["xrmv_status"] = new OptionSetValue(status),
    });

    private async Task<Dictionary<string, object?>> RowAsync(Guid account, params string[] columns)
    {
        await using var conn = await TestProvider.OpenAsync(_connectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT {string.Join(", ", columns.Select(c => $"[{c}]"))} FROM [data].[account] WHERE accountid = @id");
        cmd.Parameters.AddWithValue("@id", account);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue();
        return columns.Select((c, i) => (c, rd.IsDBNull(i) ? null : rd.GetValue(i))).ToDictionary(x => x.c, x => x.Item2);
    }

    private async Task<(object? Value, object? State)> RollupAsync(Guid account, string rollup)
    {
        var row = await RowAsync(account, rollup, rollup + "_state");
        return (row[rollup], row[rollup + "_state"]);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(_connectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- jobs -------------------------------------------------------------------------------------------------------------

    private sealed record Job(Guid Id, int Type, string Name, string? Entity, string? Attribute, DateTime? CompletedOn, DateTime? LastRunStarted,
        int State, int Status, DateTime? PostponeUntil, string? Pattern, DateTime? StartTime, Guid? Owner, Guid? RegardingObjectId);

    private async Task<List<Job>> JobsAsync()
    {
        await using var conn = await TestProvider.OpenAsync(_connectionString);
        await using var cmd = TestSql.Command(conn, """
            SELECT asyncoperationid, operationtype, name, primaryentitytype, data, statecode, statuscode, postponeuntil, recurrencepattern,
                recurrencestarttime, ownerid, regardingobjectid
            FROM data.asyncoperation WHERE operationtype IN (57, 58) ORDER BY operationtype, createdon
            """);
        await using var rd = await cmd.ExecuteReaderAsync();
        var jobs = new List<Job>();
        while (await rd.ReadAsync())
        {
            string? attribute = null;
            DateTime? completed = null, lastRun = null;
            if (!rd.IsDBNull(4))
            {
                using var data = JsonDocument.Parse(rd.GetString(4));
                rd.GetString(4).Should().Contain("\"entity\"");
                if (data.RootElement.TryGetProperty("attribute", out var a) && a.ValueKind == JsonValueKind.String) attribute = a.GetString();
                if (data.RootElement.TryGetProperty("completedOn", out var c) && c.ValueKind == JsonValueKind.String) completed = c.GetDateTime();
                if (data.RootElement.TryGetProperty("lastRunStarted", out var l) && l.ValueKind == JsonValueKind.String) lastRun = l.GetDateTime();
            }
            jobs.Add(new Job(rd.GetGuid(0), rd.GetInt32(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3), attribute, completed, lastRun,
                rd.GetInt32(5), rd.GetInt32(6), rd.IsDBNull(7) ? null : rd.GetDateTime(7), rd.IsDBNull(8) ? null : rd.GetString(8),
                rd.IsDBNull(9) ? null : rd.GetDateTime(9), rd.IsDBNull(10) ? null : rd.GetGuid(10), rd.IsDBNull(11) ? null : rd.GetGuid(11)));
        }
        return jobs;
    }

    private async Task<Job> MassJobAsync(string attribute) => (await JobsAsync()).Single(j => j.Type == Mass && j.Attribute == attribute);

    private async Task<Job> CalculateJobAsync() => (await JobsAsync()).Single(j => j.Type == Calculate);

    private AsyncOperationWorker NewWorker() =>
        new(Services, Services.GetRequiredService<AsyncOperationQueue>(),
            Microsoft.Extensions.Options.Options.Create(new PipelineOptions { AsyncPollInterval = TimeSpan.FromMilliseconds(100) }), null, _clock);

    /// <summary>Runs ready jobs until none is (at most 20); returns how many ran.</summary>
    private async Task<int> RunJobsAsync()
    {
        var worker = NewWorker();
        var ran = 0;
        while (ran < 20 && await worker.RunOnceAsync(default)) ran++;
        return ran;
    }

    // ---- tests ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_rollup_queues_mass_job_after_delay_and_incremental_job()
    {
        var before = Now;
        await AddAsync(OrderTotal(), OrderCount());

        var jobs = await JobsAsync();
        var mass = jobs.Where(j => j.Type == Mass).ToList();
        mass.Select(j => j.Attribute).Should().BeEquivalentTo(["xrmv_ordertotal", "xrmv_ordercount"], "one Mass Calculate Rollup Field job per column");
        foreach (var job in mass)
        {
            job.Entity.Should().Be(Account);
            (job.State, job.Status).Should().Be((1, 10), "the job waits (Suspended / Waiting) until its postponeuntil");
            job.PostponeUntil.Should().BeCloseTo(before.AddHours(12), TimeSpan.FromMinutes(1), "Rollups:MassCalculationDelay defaults to 12 hours");
            job.Owner.Should().Be(SystemUserId, "the jobs are owned by SYSTEM");
            job.Pattern.Should().BeNull();
            job.CompletedOn.Should().BeNull();
            job.RegardingObjectId.Should().BeNull("the rollupfield table is not modelled (Q2)");
            job.Name.Should().Contain(job.Attribute!);
        }

        var calculate = jobs.Single(j => j.Type == Calculate);
        calculate.Entity.Should().Be(Account);
        calculate.Attribute.Should().BeNull();
        calculate.Pattern.Should().Be("FREQ=HOURLY;INTERVAL=1");
        calculate.StartTime.Should().BeCloseTo(before, TimeSpan.FromMinutes(1));
        calculate.PostponeUntil.Should().BeCloseTo(before.AddHours(1), TimeSpan.FromMinutes(1));
        (calculate.State, calculate.Status).Should().Be((1, 10));
        calculate.Owner.Should().Be(SystemUserId);

        // A third rollup on the same table adds its own Mass job only.
        await AddAsync(LastOrder());
        jobs = await JobsAsync();
        jobs.Count(j => j.Type == Calculate).Should().Be(1, "one Calculate Rollup Field job per table");
        jobs.Count(j => j.Type == Mass).Should().Be(3);
        (await RunJobsAsync()).Should().Be(0, "nothing is due before the delay");
    }

    [Fact]
    public async Task New_rollup_column_and_new_row_get_initial_values()
    {
        // Ruling R7: state 0 (NotCalculated), _date = now, value 0 for COUNT and a SUM that is neither over the hierarchy nor Currency,
        // else null, on the rows of the table when the column is added; a row created afterwards gets the empty-set values.
        var existing = await AccountAsync("Existing");
        var before = DateTime.UtcNow;
        await AddAsync(OrderTotal(), OrderCount(), LastOrder(), TreeBudget());
        var created = await AccountAsync("Created");

        foreach (var account in new[] { existing, created })
        {
            var row = await RowAsync(account, "xrmv_ordertotal", "xrmv_ordertotal_state", "xrmv_ordertotal_date", "xrmv_ordercount",
                "xrmv_ordercount_state", "xrmv_ordercount_date", "xrmv_lastorder", "xrmv_lastorder_state", "xrmv_lastorder_date",
                "xrmv_treebudget", "xrmv_treebudget_base", "xrmv_treebudget_state", "xrmv_treebudget_date");
            row["xrmv_ordertotal"].Should().Be(0m);
            row["xrmv_ordercount"].Should().Be(0);
            row["xrmv_lastorder"].Should().BeNull();
            row["xrmv_treebudget"].Should().BeNull();
            row["xrmv_treebudget_base"].Should().BeNull();
            foreach (var rollup in new[] { "xrmv_ordertotal", "xrmv_ordercount", "xrmv_lastorder", "xrmv_treebudget" })
            {
                // Dataverse calculates a rollup over related rows at once on Create (parity run, plan 2.12b Task 7): a new row has no
                // related rows, so the empty-set value with state 1; the hierarchy-only rollup and the existing rows wait (state 0).
                var calculatedOnCreate = account == created && rollup != "xrmv_treebudget";
                row[rollup + "_state"].Should().Be(calculatedOnCreate ? 1 : 0, rollup);
                ((DateTime)row[rollup + "_date"]!).Should().BeCloseTo(before, TimeSpan.FromMinutes(1), rollup);
            }
        }
    }

    [Fact]
    public async Task Update_resets_mass_delay_even_without_change()
    {
        await AddAsync(OrderTotal());
        _clock.Shift = TimeSpan.FromHours(3);
        var unchanged = Store.GetEntity(Account)!.Attributes.Single(a => a.LogicalName == "xrmv_ordertotal");
        await TestEntities.InSessionAsync(_connectionString, s => Store.UpdateAttributeAsync(s, Account, unchanged, CancellationToken.None));
        await Store.RollupJobNotifications;

        var mass = await MassJobAsync("xrmv_ordertotal");
        mass.PostponeUntil.Should().BeCloseTo(Now.AddHours(12), TimeSpan.FromMinutes(1), "an update, even with no change, resets the delay");
        (await JobsAsync()).Count(j => j.Type == Mass).Should().Be(1);

        // After a run the job sleeps ten years; an update brings it back to the delay and forgets the run (ruling candidate).
        _clock.Shift = TimeSpan.FromHours(16);
        await RunJobsAsync();
        (await MassJobAsync("xrmv_ordertotal")).CompletedOn.Should().NotBeNull();
        await TestEntities.InSessionAsync(_connectionString, s => Store.UpdateAttributeAsync(s, Account, unchanged, CancellationToken.None));
        await Store.RollupJobNotifications;
        mass = await MassJobAsync("xrmv_ordertotal");
        mass.PostponeUntil.Should().BeCloseTo(Now.AddHours(12), TimeSpan.FromMinutes(1));
        mass.CompletedOn.Should().BeNull("the incremental job waits for the Mass job of the new definition");
        (mass.State, mass.Status).Should().Be((1, 10));
    }

    [Fact]
    public async Task Mass_run_calculates_all_rows_and_sleeps_ten_years()
    {
        await AddAsync(OrderTotal(), OrderCount());
        var a = await AccountAsync("A");
        var b = await AccountAsync("B");
        await OrderAsync(a, 100m, 2);
        await OrderAsync(a, 50m, 2);
        await OrderAsync(a, 20m, 1);

        _clock.Shift = TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1);
        var start = Now;
        (await RunJobsAsync()).Should().BeGreaterThanOrEqualTo(2);

        (await RollupAsync(a, "xrmv_ordertotal")).Should().Be((150m, 1));
        (await RollupAsync(a, "xrmv_ordercount")).Should().Be((3, 1));
        (await RollupAsync(b, "xrmv_ordertotal")).Should().Be((0m, 1));
        (await RollupAsync(b, "xrmv_ordercount")).Should().Be((0, 1));
        ((DateTime)(await RowAsync(a, "xrmv_ordertotal_date"))["xrmv_ordertotal_date"]!).Should().BeCloseTo(start, TimeSpan.FromMinutes(1));

        foreach (var attribute in new[] { "xrmv_ordertotal", "xrmv_ordercount" })
        {
            var mass = await MassJobAsync(attribute);
            (mass.State, mass.Status).Should().Be((1, 10), "a Mass job is recurring: it waits again after its run");
            mass.PostponeUntil.Should().BeCloseTo(start.AddYears(10), TimeSpan.FromMinutes(1), "it sleeps about ten years (Learn)");
            mass.CompletedOn.Should().BeCloseTo(start, TimeSpan.FromMinutes(1));
        }
        (await RunJobsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Incremental_skips_rollups_whose_mass_job_has_not_run()
    {
        await AddAsync(OrderTotal());
        var a = await AccountAsync("A");
        await OrderAsync(a, 100m, 2);
        var incremental = await CalculateJobAsync();

        _clock.Shift = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1);
        var start = Now;
        (await RunJobsAsync()).Should().Be(1, "only the incremental job is due");
        (await RollupAsync(a, "xrmv_ordertotal")).Should().Be((0m, 1), "its column's Mass job has not run yet: the value of the Create stays");
        var after = await CalculateJobAsync();
        (after.State, after.Status).Should().Be((1, 10));
        after.PostponeUntil.Should().BeCloseTo(incremental.StartTime!.Value.AddHours(2), TimeSpan.FromSeconds(5), "the next occurrence of the hourly pattern");
        after.LastRunStarted.Should().BeCloseTo(start, TimeSpan.FromMinutes(1));

        _clock.Shift = TimeSpan.FromHours(12) + TimeSpan.FromMinutes(2);
        await RunJobsAsync();
        (await RollupAsync(a, "xrmv_ordertotal")).Should().Be((100m, 1));
    }

    [Fact]
    public async Task Incremental_run_updates_old_and_new_parent_after_reparent_and_delete()
    {
        // Review Focus 1: a related row moved from A to B, and another deleted, are caught by the next incremental run on both parents.
        await AddAsync(OrderTotal(), OrderCount());
        var a = await AccountAsync("A");
        var b = await AccountAsync("B");
        var moved = await OrderAsync(a, 100m, 2);
        var deleted = await OrderAsync(a, 50m, 2);
        await OrderAsync(b, 10m, 2);
        _clock.Shift = TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1);
        await RunJobsAsync();
        (await RollupAsync(a, "xrmv_ordertotal")).Should().Be((150m, 1));
        (await RollupAsync(b, "xrmv_ordertotal")).Should().Be((10m, 1));

        await Admin.UpdateAsync(new Entity(Order, moved) { ["xrmv_accountid"] = new EntityReference(Account, b) });
        await Admin.DeleteAsync(Order, deleted);
        _clock.Shift = TimeSpan.FromHours(13) + TimeSpan.FromMinutes(2);
        (await RunJobsAsync()).Should().Be(1, "the incremental job of account");

        (await RollupAsync(a, "xrmv_ordertotal")).Should().Be((0m, 1), "the old parent lost both orders");
        (await RollupAsync(a, "xrmv_ordercount")).Should().Be((0, 1));
        (await RollupAsync(b, "xrmv_ordertotal")).Should().Be((110m, 1), "the new parent gained the moved order");
        (await RollupAsync(b, "xrmv_ordercount")).Should().Be((2, 1));
    }

    [Fact]
    public async Task Job_write_touches_only_rollup_columns()
    {
        // Review Focus 4 and ruling R8: the job writes the rollup's columns as the system, in SQL: no plug-in, no audit, modifiedon and
        // modifiedby and the user's columns unchanged.
        await AddAsync(OrderTotal());
        var admin = Admin;
        await admin.UpdateAsync(new Entity("organization", await OrganizationIdAsync()) { ["isauditenabled"] = true });
        await admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = Account, IsAuditEnabled = new BooleanManagedProperty(true) } });
        var a = await AccountAsync("A", ("telephone1", "555-0100"));
        await OrderAsync(a, 100m, 2);
        var before = await RowAsync(a, "modifiedon", "modifiedby", "name", "telephone1", "versionnumber");
        var audits = await AuditCountAsync(a);
        audits.Should().BeGreaterThan(0, "the account's Create is audited");

        var assembly = await PluginTestHelper.EnsureAssemblyAsync(admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(admin, type, "Update", Account, 20, config: "invalid:The rollup job must not run Update plug-ins.");
        Services.GetRequiredService<Xrmd.Pipeline.Registration.StepRegistry>().Invalidate();
        var update = () => admin.UpdateAsync(new Entity(Account, a) { ["telephone1"] = "555-0199" });
        await update.Should().ThrowAsync<Exception>("the step is registered: an Update through the pipeline runs it");

        _clock.Shift = TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1);
        await RunJobsAsync();

        (await RollupAsync(a, "xrmv_ordertotal")).Should().Be((100m, 1));
        var mass = await MassJobAsync("xrmv_ordertotal");
        (mass.State, mass.Status).Should().Be((1, 10), "the job did not fail on the plug-in: none ran");
        var after = await RowAsync(a, "modifiedon", "modifiedby", "name", "telephone1", "versionnumber");
        after["modifiedon"].Should().Be(before["modifiedon"]);
        after["modifiedby"].Should().Be(before["modifiedby"]);
        after["name"].Should().Be("A");
        after["telephone1"].Should().Be("555-0100");
        after["versionnumber"].Should().NotBeEquivalentTo(before["versionnumber"], "the row changed: change tracking sees it (R8)");
        (await AuditCountAsync(a)).Should().Be(audits, "the job writes no audit row");
    }

    [Fact]
    public async Task Last_rollup_deleted_removes_incremental_job()
    {
        await AddAsync(OrderTotal(), OrderCount());
        await DeleteAsync("xrmv_ordertotal");
        var jobs = await JobsAsync();
        jobs.Where(j => j.Type == Mass).Select(j => j.Attribute).Should().Equal("xrmv_ordercount");
        jobs.Count(j => j.Type == Calculate).Should().Be(1, "the table still has a rollup");

        await DeleteAsync("xrmv_ordercount");
        (await JobsAsync()).Should().BeEmpty("the last rollup of the table takes the incremental job with it");
    }

    [Fact]
    public async Task Reconcile_creates_missing_and_deletes_orphan_jobs()
    {
        await AddAsync(OrderTotal(), OrderCount());
        var kept = await MassJobAsync("xrmv_ordertotal");
        var lost = await MassJobAsync("xrmv_ordercount");
        await ExecAsync($"DELETE FROM data.asyncoperation WHERE asyncoperationid = '{lost.Id}' OR operationtype = 57");
        // Orphans: the Mass job of a column that is gone, the incremental job of a table without rollups, a second Mass job of a column.
        var queue = Services.GetRequiredService<AsyncOperationQueue>();
        var system = new Caller(SystemUserId, Guid.Empty);
        await TestEntities.InSessionAsync(_connectionString, async s =>
        {
            await queue.EnqueueSystemJobAsync(s, system, Mass, "orphan", "MassCalculateRollupField", Account, null,
                """{"entity":"account","attribute":"xrmv_gone"}""", default);
            await queue.EnqueueSystemJobAsync(s, system, Calculate, "orphan", "CalculateRollupField", Order, null, """{"entity":"xrmv_calcorder"}""", default);
            await queue.EnqueueSystemJobAsync(s, system, Mass, "duplicate", "MassCalculateRollupField", Account, null,
                """{"entity":"account","attribute":"xrmv_ordertotal"}""", default);
        });

        await Services.GetRequiredService<RollupJobScheduler>().ReconcileAsync(default);

        var jobs = await JobsAsync();
        jobs.Where(j => j.Type == Mass).Select(j => j.Attribute).Should().BeEquivalentTo(["xrmv_ordertotal", "xrmv_ordercount"]);
        jobs.Should().Contain(j => j.Id == kept.Id, "an existing job is kept");
        jobs.Should().NotContain(j => j.Name == "orphan" || j.Name == "duplicate");
        var created = await MassJobAsync("xrmv_ordercount");
        created.Id.Should().NotBe(lost.Id);
        created.PostponeUntil.Should().BeCloseTo(Now.AddHours(12), TimeSpan.FromMinutes(1));
        var calculate = jobs.Single(j => j.Type == Calculate);
        (calculate.Entity, calculate.Pattern).Should().Be((Account, "FREQ=HOURLY;INTERVAL=1"));

        // Nothing to do the second time.
        await Services.GetRequiredService<RollupJobScheduler>().ReconcileAsync(default);
        (await JobsAsync()).Select(j => j.Id).Should().BeEquivalentTo(jobs.Select(j => j.Id));

        // A rollup job that ended (failed here) is replaced: its column would otherwise never be calculated again.
        await ExecAsync($"UPDATE data.asyncoperation SET statecode = 3, statuscode = 31 WHERE asyncoperationid = '{kept.Id}'");
        await Services.GetRequiredService<RollupJobScheduler>().ReconcileAsync(default);
        var replaced = await MassJobAsync("xrmv_ordertotal");
        replaced.Id.Should().NotBe(kept.Id);
        (replaced.State, replaced.Status).Should().Be((1, 10));
        replaced.PostponeUntil.Should().BeCloseTo(Now.AddHours(12), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Reconcile_resets_mass_job_of_a_changed_definition()
    {
        // Final review M5: a definition change whose notification was lost (the host stopped before it was handled) is caught at the
        // next start: the Mass job stamped with another definition is due again after the delay and forgets its last run.
        await AddAsync(OrderTotal());
        _clock.Shift = TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1);
        await RunJobsAsync();
        (await MassJobAsync("xrmv_ordertotal")).CompletedOn.Should().NotBeNull();
        var scheduler = Services.GetRequiredService<RollupJobScheduler>();

        await scheduler.ReconcileAsync(default);
        var same = await MassJobAsync("xrmv_ordertotal");
        same.CompletedOn.Should().NotBeNull("the same definition: the job is kept as it is");
        same.PostponeUntil.Should().BeAfter(Now.AddYears(9));

        await ExecAsync("UPDATE data.asyncoperation SET data = JSON_MODIFY(data, '$.definition', 'OLDDEFINITION') WHERE operationtype = 58");
        await scheduler.ReconcileAsync(default);
        var reset = await MassJobAsync("xrmv_ordertotal");
        reset.Id.Should().Be(same.Id);
        (reset.State, reset.Status).Should().Be((1, 10));
        reset.CompletedOn.Should().BeNull("the incremental job waits for the Mass job of the current definition");
        reset.PostponeUntil.Should().BeCloseTo(Now.AddHours(12), TimeSpan.FromMinutes(1));

        // Stamped again: a second reconcile leaves it alone.
        _clock.Shift += TimeSpan.FromHours(1);
        await scheduler.ReconcileAsync(default);
        (await MassJobAsync("xrmv_ordertotal")).PostponeUntil.Should().Be(reset.PostponeUntil);
    }

    [Fact]
    public async Task Rollup_jobs_can_be_postponed_and_rescheduled()
    {
        await AddAsync(OrderTotal());
        var admin = Admin;
        var mass = await MassJobAsync("xrmv_ordertotal");
        var incremental = await CalculateJobAsync();

        // Postpone: postponeuntil alone.
        var later = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await admin.UpdateAsync(new Entity("asyncoperation", mass.Id) { ["postponeuntil"] = later });
        (await MassJobAsync("xrmv_ordertotal")).PostponeUntil.Should().Be(later);

        // Modify Recurrence: recurrencepattern alone, on the Calculate Rollup Field job.
        await admin.UpdateAsync(new Entity("asyncoperation", incremental.Id) { ["recurrencepattern"] = "FREQ=DAILY;INTERVAL=1" });
        (await CalculateJobAsync()).Pattern.Should().Be("FREQ=DAILY;INTERVAL=1");

        var tooOften = () => admin.UpdateAsync(new Entity("asyncoperation", incremental.Id) { ["recurrencepattern"] = "FREQ=MINUTELY;INTERVAL=30" });
        (await tooOften.Should().ThrowAsync<XrmdFaultException>()).Which.ErrorCode.Should().Be(ErrorCodes.InvalidRecurrenceIntervalForRollupJobs);
        var otherShape = () => admin.UpdateAsync(new Entity("asyncoperation", incremental.Id) { ["recurrencepattern"] = "FREQ=MONTHLY;INTERVAL=1" });
        (await otherShape.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
        var massPattern = () => admin.UpdateAsync(new Entity("asyncoperation", mass.Id) { ["recurrencepattern"] = "FREQ=DAILY;INTERVAL=1" });
        (await massPattern.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
        var withName = () => admin.UpdateAsync(new Entity("asyncoperation", mass.Id) { ["postponeuntil"] = later, ["name"] = "renamed" });
        (await withName.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
        (await CalculateJobAsync()).Pattern.Should().Be("FREQ=DAILY;INTERVAL=1");

        // The new pattern gives the occurrence after the next run: one day from the start time.
        _clock.Shift = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1);
        (await RunJobsAsync()).Should().Be(1);
        (await CalculateJobAsync()).PostponeUntil.Should().BeCloseTo(incremental.StartTime!.Value.AddDays(1), TimeSpan.FromSeconds(5));
        (await MassJobAsync("xrmv_ordertotal")).PostponeUntil.Should().Be(later, "the postponed Mass job did not run");
    }

    [Fact]
    public async Task Rollup_jobs_cannot_be_cancelled_deleted_or_paused()
    {
        await AddAsync(OrderTotal());
        var admin = Admin;
        foreach (var job in new[] { await MassJobAsync("xrmv_ordertotal"), await CalculateJobAsync() })
        {
            var cancel = () => admin.UpdateAsync(new Entity("asyncoperation", job.Id) { ["statecode"] = new OptionSetValue(3), ["statuscode"] = new OptionSetValue(32) });
            var fault = (await cancel.Should().ThrowAsync<XrmdFaultException>()).Which;
            (fault.HttpStatus, fault.Message).Should().Be((400, NotCancelled));

            var delete = () => admin.DeleteAsync("asyncoperation", job.Id);
            fault = (await delete.Should().ThrowAsync<XrmdFaultException>()).Which;
            (fault.HttpStatus, fault.Message).Should().Be((400, NotCancelled));

            var pause = () => admin.UpdateAsync(new Entity("asyncoperation", job.Id) { ["statecode"] = new OptionSetValue(1), ["statuscode"] = new OptionSetValue(21) });
            fault = (await pause.Should().ThrowAsync<XrmdFaultException>()).Which;
            fault.HttpStatus.Should().Be(400);
            fault.Message.Should().Contain("paused or resumed");

            var resume = () => admin.UpdateAsync(new Entity("asyncoperation", job.Id) { ["statecode"] = new OptionSetValue(0), ["statuscode"] = new OptionSetValue(0) });
            fault = (await resume.Should().ThrowAsync<XrmdFaultException>()).Which;
            fault.HttpStatus.Should().Be(400);
            fault.Message.Should().Contain("paused or resumed");
        }
        (await JobsAsync()).Should().HaveCount(2).And.OnlyContain(j => j.State == 1 && j.Status == 10);
    }

    [Fact]
    public async Task Imported_rollup_makes_its_mass_job_due_at_once()
    {
        await AddAsync(OrderTotal());
        await Services.GetRequiredService<RollupJobScheduler>().DefinitionChangedAsync(Account, "xrmv_ordertotal", RollupChange.Imported, default);
        (await MassJobAsync("xrmv_ordertotal")).PostponeUntil.Should().BeCloseTo(Now, TimeSpan.FromMinutes(1), "R8: at once on solution import");
        var a = await AccountAsync("A");
        await OrderAsync(a, 40m, 2);
        (await RunJobsAsync()).Should().Be(1, "the Mass job only: the incremental job waits for its hour");
        (await RollupAsync(a, "xrmv_ordertotal")).Should().Be((40m, 1));
    }

    [Fact]
    public async Task Definition_update_during_mass_run_restarts_the_delay()
    {
        // Fix round 1 (finding 3): the scheduler leaves a running Mass job alone; the job sees the definition changed under it.
        await AddAsync(OrderTotal());
        var a = await AccountAsync("A");
        await OrderAsync(a, 100m, 2);
        await OrderAsync(a, 7m, 1);
        _clock.Shift = TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1);
        var changed = Rollup(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_ordertotal");
        ComputedColumnRules.SetFormula(changed, Real("xrmv_ordertotal").Replace("OptionSetValue, \"2\", \"Picklist\"", "OptionSetValue, \"1\", \"Picklist\""));
        Xrmd.Core.Rollups.MassCalculateRollupJob.AfterRecalculate.Value = async () =>
        {
            await TestEntities.InSessionAsync(_connectionString, s => Store.UpdateAttributeAsync(s, Account, changed, CancellationToken.None));
            await Store.RollupJobNotifications;
        };
        try
        {
            await NewWorker().RunOnceAsync(default);
        }
        finally
        {
            Xrmd.Core.Rollups.MassCalculateRollupJob.AfterRecalculate.Value = null;
        }

        var mass = await MassJobAsync("xrmv_ordertotal");
        (mass.State, mass.Status).Should().Be((1, 10));
        mass.CompletedOn.Should().BeNull("the run computed the old definition: it does not count");
        mass.PostponeUntil.Should().BeCloseTo(Now.AddHours(12), TimeSpan.FromMinutes(1), "the delay starts again, as after any update");

        _clock.Shift = TimeSpan.FromHours(24) + TimeSpan.FromMinutes(2);
        await RunJobsAsync();
        (await RollupAsync(a, "xrmv_ordertotal")).Should().Be((7m, 1), "the next run computes the new definition (status 1)");
        (await MassJobAsync("xrmv_ordertotal")).CompletedOn.Should().NotBeNull();
    }

    [Fact]
    public async Task Incremental_since_is_the_mass_run_then_the_previous_run()
    {
        // Fix round 1 (finding 5): the rows touched since the Mass run (first incremental run after it), then since the previous run, get
        // a new _date; the others keep theirs (their value does not change).
        await AddAsync(OrderTotal());
        var a = await AccountAsync("A");
        var b = await AccountAsync("B");
        var orderA = await OrderAsync(a, 10m, 2);
        var orderB = await OrderAsync(b, 20m, 2);
        var start = (await CalculateJobAsync()).StartTime!.Value;

        _clock.Shift = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1);
        (await RunJobsAsync()).Should().Be(1, "the incremental job, which skips the column");
        // Keep the incremental job off the Mass run's hour, so the two do not run together.
        await ExecAsync($"UPDATE data.asyncoperation SET postponeuntil = '{start.AddHours(12.5):yyyy-MM-ddTHH:mm:ss.fffffff}' WHERE operationtype = 57");
        _clock.Shift = TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1);
        (await RunJobsAsync()).Should().Be(1, "the Mass job");
        var massRun = (await MassJobAsync("xrmv_ordertotal")).CompletedOn!.Value;
        var dateA = await DateAsync(a);
        var dateB = await DateAsync(b);

        // A's order touched after the Mass run; B's between the previous incremental run and the Mass run.
        await TouchAsync(orderA, massRun.AddMinutes(1));
        await TouchAsync(orderB, start.AddHours(6));
        _clock.Shift = TimeSpan.FromHours(12.5) + TimeSpan.FromMinutes(1);
        var run1 = Now;
        (await RunJobsAsync()).Should().Be(1);
        (await DateAsync(a)).Should().BeCloseTo(run1, TimeSpan.FromMinutes(1), "touched since the Mass run");
        (await DateAsync(b)).Should().Be(dateB, "touched before the Mass run only");
        var lastRun = (await CalculateJobAsync()).LastRunStarted!.Value;

        // Now since the previous incremental run: B's order touched after it, A's between the Mass run and it.
        dateA = await DateAsync(a);
        await TouchAsync(orderB, lastRun.AddMinutes(5));
        await TouchAsync(orderA, massRun.AddMinutes(10));
        _clock.Shift = TimeSpan.FromHours(13) + TimeSpan.FromMinutes(2);
        var run2 = Now;
        (await RunJobsAsync()).Should().Be(1);
        (await DateAsync(b)).Should().BeCloseTo(run2, TimeSpan.FromMinutes(1), "touched since the previous run");
        (await DateAsync(a)).Should().Be(dateA, "touched before the previous run only");
        (await RollupAsync(a, "xrmv_ordertotal")).Should().Be((10m, 1));
        (await RollupAsync(b, "xrmv_ordertotal")).Should().Be((20m, 1));
    }

    [Fact]
    public async Task Jobs_of_a_gone_column_or_table_end_and_are_deleted()
    {
        // Fix round 1 (finding 6): the handlers themselves, for a job the scheduler has not removed (yet).
        await AddAsync(OrderTotal());
        await using var scope = Services.CreateAsyncScope();
        var handlers = scope.ServiceProvider.GetServices<IAsyncOperationHandler>().ToList();
        var mass = handlers.Single(h => h.OperationType == Mass);
        var calculate = handlers.Single(h => h.OperationType == Calculate);
        mass.RestartsWhenInterrupted.Should().BeTrue();
        calculate.RestartsWhenInterrupted.Should().BeTrue();
        var system = new Caller(SystemUserId, Guid.Empty);

        var gone = new AsyncOperationRun(Guid.NewGuid(), system, SystemUserId, """{"entity":"account","attribute":"xrmv_gone"}""") { Now = _clock.GetUtcNow() };
        await mass.RunAsync(gone, default);
        gone.DeleteOnSuccess.Should().BeTrue();
        gone.Suspension.Should().BeNull();
        gone.CompletionMessage.Should().Contain("account.xrmv_gone");

        var empty = new AsyncOperationRun(Guid.NewGuid(), system, SystemUserId, """{"entity":"xrmv_calcorder"}""") { Now = _clock.GetUtcNow() };
        await calculate.RunAsync(empty, default);
        empty.DeleteOnSuccess.Should().BeTrue();
        empty.Suspension.Should().BeNull();
        empty.CompletionMessage.Should().Contain("xrmv_calcorder");

        // Through the worker: the row is deleted.
        await DeleteAsync("xrmv_ordertotal");
        var queue = Services.GetRequiredService<AsyncOperationQueue>();
        await TestEntities.InSessionAsync(_connectionString, s => queue.EnqueueSystemJobAsync(s, system, Mass, "late", "MassCalculateRollupField", Account,
            null, """{"entity":"account","attribute":"xrmv_ordertotal"}""", default));
        (await RunJobsAsync()).Should().Be(1);
        (await JobsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Unusable_stored_recurrence_falls_back_to_hourly()
    {
        // Fix round 1 (finding 1): a pattern written around the rules (here in SQL) does not stop the job.
        await AddAsync(OrderTotal());
        await ExecAsync("UPDATE data.asyncoperation SET recurrencepattern = 'FREQ=DAILY;INTERVAL=3000000' WHERE operationtype = 57");
        _clock.Shift = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1);
        var run = Now;
        (await RunJobsAsync()).Should().Be(1);
        var job = await CalculateJobAsync();
        (job.State, job.Status).Should().Be((1, 10), "the job keeps recurring");
        job.PostponeUntil.Should().BeCloseTo(run.AddHours(1), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Transient_failure_of_mass_run_retries_after_delay()
    {
        // Final review I2: a deadlock or a timeout does not end the Mass job (which would leave the column without calculation until
        // the host restarts): the job waits Rollups:TransientRetryDelay and runs again, still without completedOn.
        _limits = new Xrmd.Core.Rollups.RollupLimits(JobCommandTimeoutSeconds: 1);
        Services.GetRequiredService<RollupOptions>().TransientRetryDelay.Should().Be(TimeSpan.FromMinutes(5), "the default retry delay");
        await AddAsync(OrderTotal());
        var a = await AccountAsync("A");
        await OrderAsync(a, 100m, 2);
        // Keep the incremental job out of the way.
        await ExecAsync("UPDATE data.asyncoperation SET postponeuntil = '2100-01-01' WHERE operationtype = 57");
        _clock.Shift = TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1);

        await using (var blocker = await TestProvider.OpenAsync(_connectionString))
        {
            await using var tx = blocker.BeginTransaction();
            await using (var hold = TestSql.Command(blocker, tx, TestSql.ExclusiveTableLock("data.account")))
                await hold.ExecuteScalarAsync();
            var failedAt = Now;
            (await RunJobsAsync()).Should().Be(1);
            var waiting = await MassJobAsync("xrmv_ordertotal");
            (waiting.State, waiting.Status).Should().Be((1, 10), "the timeout does not end the job");
            waiting.CompletedOn.Should().BeNull();
            waiting.PostponeUntil.Should().BeCloseTo(failedAt.AddMinutes(5), TimeSpan.FromMinutes(1));
            await tx.RollbackAsync();
        }
        (await RollupAsync(a, "xrmv_ordertotal")).Should().Be((0m, 1), "nothing was written: the value of the Create stays");

        _clock.Shift += TimeSpan.FromMinutes(6);
        var start = Now;
        (await RunJobsAsync()).Should().Be(1);
        (await RollupAsync(a, "xrmv_ordertotal")).Should().Be((100m, 1), "the column heals without a host restart");
        var mass = await MassJobAsync("xrmv_ordertotal");
        mass.CompletedOn.Should().BeCloseTo(start, TimeSpan.FromMinutes(1));
        mass.PostponeUntil.Should().BeCloseTo(start.AddYears(10), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Transient_failure_of_incremental_run_retries_after_delay()
    {
        // Final review I2: the incremental job too waits the retry delay when the database cannot be reached, its data unchanged.
        await AddAsync(OrderTotal());
        var job = await CalculateJobAsync();
        var unreachable = TestProvider.WithDatabase(_connectionString, "xrmv_no_such_database");
        var handler = new Xrmd.Core.Rollups.CalculateRollupJob(Services.GetRequiredService<Xrmd.Core.Rollups.RollupCalculator>(), Store,
            TestProvider.Factory(unreachable), null, Services.GetRequiredService<RollupOptions>());
        var data = new RollupJobData(Account, null, null, null).ToJson();
        var run = new AsyncOperationRun(job.Id, new Caller(SystemUserId, Guid.Empty), SystemUserId, data) { Now = _clock.GetUtcNow() };
        await handler.RunAsync(run, default);
        run.Suspension.Should().NotBeNull("the job waits instead of failing");
        run.Suspension!.Value.Until.Should().Be(run.Now.AddMinutes(5));
        run.Suspension.Value.Data.Should().Be(data, "the last run is not moved");
    }

    [Fact]
    public async Task Negative_retry_delay_is_refused_at_startup()
    {
        await using var app = fx.WithOwnDatabase(b => b.UseSetting("Rollups:TransientRetryDelay", "-00:05:00"));
        var act = () => app.Services;
        var thrown = act.Should().Throw<Exception>().Which;
        for (Exception? e = thrown; ; e = e.InnerException)
        {
            e.Should().NotBeNull("the refusal names the setting");
            if (e!.Message.Contains(Xrmd.Host.XrmdHost.RollupRetryDelayNegative, StringComparison.Ordinal)) break;
        }
    }

    [Fact]
    public async Task Negative_mass_delay_is_refused_at_startup()
    {
        await using var app = fx.WithOwnDatabase(b => b.UseSetting("Rollups:MassCalculationDelay", "-01:00:00"));
        var act = () => app.Services;
        var thrown = act.Should().Throw<Exception>().Which;
        Messages(thrown).Should().Contain(m => m.Contains(Xrmd.Host.XrmdHost.RollupDelayNegative, StringComparison.Ordinal));

        static IEnumerable<string> Messages(Exception? e)
        {
            for (; e is not null; e = e.InnerException) yield return e.Message;
        }
    }

    private async Task<DateTime> DateAsync(Guid account) => (DateTime)(await RowAsync(account, "xrmv_ordertotal_date"))["xrmv_ordertotal_date"]!;

    private async Task TouchAsync(Guid order, DateTime modifiedOn)
    {
        await using var conn = await TestProvider.OpenAsync(_connectionString);
        await using var cmd = TestSql.Command(conn, "UPDATE data.xrmv_calcorder SET modifiedon = @on WHERE xrmv_calcorderid = @id");
        cmd.Parameters.Add("@on", System.Data.SqlDbType.DateTime2).Value = modifiedOn;
        cmd.Parameters.AddWithValue("@id", order);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private async Task<Guid> OrganizationIdAsync()
    {
        await using var conn = await TestProvider.OpenAsync(_connectionString);
        await using var cmd = TestSql.Command(conn, "SELECT TOP 1 organizationid FROM data.organization");
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<int> AuditCountAsync(Guid objectId)
    {
        await using var conn = await TestProvider.OpenAsync(_connectionString);
        await using var cmd = TestSql.Command(conn, "SELECT COUNT(*) FROM data.audit WHERE objectid = @id");
        cmd.Parameters.AddWithValue("@id", objectId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
