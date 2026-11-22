using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The setup shared by the duplicate detection tests (plan 2.15): the organization switches, the table flag, rules created and
/// published as the administrator, their publish jobs run on a worker of the test's own (the fixture host's worker is disabled),
/// and direct SQL reads. <see cref="InitializeAsync"/> turns detection on for the tables given; <see cref="DisposeAsync"/>
/// deletes the rules created and turns it off again.
/// </summary>
internal sealed class DuplicateDetectionTestKit(XrmdFixture fx)
{
    public const string Contact = "contact";
    public const string Email = "emailaddress1";

    private string[] _tables = [];

    /// <summary>A short id of the test run, for unique names and email addresses.</summary>
    public string Run { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The rules created through <see cref="RuleAsync"/>, deleted by <see cref="DisposeAsync"/>.</summary>
    public List<Guid> Rules { get; } = [];

    public Guid OrganizationId { get; private set; }

    public IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    public OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    /// <summary>
    /// Drops ready jobs left by other tests (this test's worker would take them first), turns <c>IsDuplicateDetectionEnabled</c>
    /// on for <paramref name="tables"/> and the organization switch on (also the Create/Update one with <paramref name="onlineCreateUpdate"/>).
    /// </summary>
    public async Task InitializeAsync(bool onlineCreateUpdate, params string[] tables)
    {
        _tables = tables;
        OrganizationId = (Guid)(await ScalarAsync("SELECT TOP 1 organizationid FROM data.organization"))!;
        await ExecAsync("DELETE FROM data.asyncoperation WHERE statecode = 0");
        foreach (var table in tables) await SetFlagAsync(table, true);
        await SetOrganizationAsync(true, onlineCreateUpdate ? true : null);
    }

    /// <summary>Deletes the rules left, turns both organization switches and the tables' flags off and drops the publish jobs.</summary>
    public async Task DisposeAsync()
    {
        foreach (var rule in Rules)
            if (await ScalarAsync("SELECT COUNT(*) FROM data.duplicaterule WHERE duplicateruleid = @id", ("@id", rule)) is 1 or 1L)
                await Admin.DeleteAsync(DuplicateDetectionSchema.Rule, rule);
        await SetOrganizationAsync(false, false);
        foreach (var table in _tables) await SetFlagAsync(table, false);
        await ExecAsync("DELETE FROM data.asyncoperation WHERE operationtype = 7");
    }

    /// <summary>An email address unique to this run.</summary>
    public string Mail(string label) => $"{label}.{Run}@example.com";

    public Task SetFlagAsync(string table, bool value) =>
        Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = table, IsDuplicateDetectionEnabled = new BooleanManagedProperty(value) } });

    /// <summary>Sets <c>isduplicatedetectionenabled</c> and, when given, <c>isduplicatedetectionenabledforonlinecreateupdate</c>.</summary>
    public Task SetOrganizationAsync(bool enabled, bool? onlineCreateUpdate = null)
    {
        var organization = new Entity("organization", OrganizationId) { [DuplicateDetectionSchema.OrgEnabled] = enabled };
        if (onlineCreateUpdate is { } online) organization[DuplicateDetectionSchema.OrgEnabledForOnlineCreateUpdate] = online;
        return Admin.UpdateAsync(organization);
    }

    /// <summary>
    /// A rule (Unpublished) of <paramref name="table"/> against <paramref name="matchingTable"/> (the same table when null) with one
    /// condition on <paramref name="attribute"/> of both (unless <paramref name="withCondition"/> is false).
    /// </summary>
    public async Task<Guid> RuleAsync(string attribute = Email, string table = Contact, bool excludeInactive = false, bool withCondition = true,
        DuplicateDetectionSchema.DuplicateOperator op = DuplicateDetectionSchema.DuplicateOperator.ExactMatch, string? matchingTable = null,
        bool caseSensitive = false)
    {
        var rule = await Admin.CreateAsync(new Entity(DuplicateDetectionSchema.Rule)
        {
            ["name"] = $"Rule {Run} {Rules.Count}", ["baseentityname"] = table, ["matchingentityname"] = matchingTable ?? table,
            ["excludeinactiverecords"] = excludeInactive, ["iscasesensitive"] = caseSensitive,
        });
        Rules.Add(rule);
        if (withCondition)
            await Admin.CreateAsync(new Entity(DuplicateDetectionSchema.Condition)
            {
                ["regardingobjectid"] = new EntityReference(DuplicateDetectionSchema.Rule, rule),
                ["baseattributename"] = attribute, ["matchingattributename"] = attribute, ["operatorcode"] = new OptionSetValue((int)op),
            });
        return rule;
    }

    public async Task<Guid> PublishAsync(Guid rule) =>
        (Guid)(await Admin.ExecuteAsync(new PublishDuplicateRuleRequest { DuplicateRuleId = rule })).Results["JobId"];

    /// <summary>Publishes <paramref name="rule"/> and runs its job to the end.</summary>
    public async Task PublishAndRunAsync(Guid rule)
    {
        var job = await PublishAsync(rule);
        await RunJobAsync(job);
        (await JobAsync(job)).Should().Be((3, 30, (string?)null));
    }

    public AsyncOperationWorker NewWorker() =>
        new(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Options.Create(new PipelineOptions { AsyncPollInterval = TimeSpan.FromMilliseconds(100) }));

    /// <summary>Runs ready jobs until <paramref name="job"/> is completed (statecode 3); fails after 30 s.</summary>
    public async Task RunJobAsync(Guid job)
    {
        var worker = NewWorker();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await JobAsync(job)).State != 3)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the publish job should complete");
            if (!await worker.RunOnceAsync(default)) await Task.Delay(20);
        }
    }

    public async Task<(int State, int Status, string? Message)> JobAsync(Guid job)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT statecode, statuscode, message FROM data.asyncoperation WHERE asyncoperationid = @id");
        cmd.Parameters.AddWithValue("@id", job);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue("the job exists");
        return (rd.GetInt32(0), rd.GetInt32(1), rd.IsDBNull(2) ? null : rd.GetString(2));
    }

    /// <summary>The code of <paramref name="record"/> in the match code table of <paramref name="rule"/> (null when it has none).</summary>
    public async Task<byte[]?> CodeAsync(Guid rule, Guid record) =>
        await ScalarAsync($"SELECT matchcode FROM meta.{DuplicateDetectionSchema.MatchCodeTableName(rule)} WHERE objectid = @id", ("@id", record)) as byte[];

    public async Task<int> CodeCountAsync(Guid rule) =>
        Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM meta.{DuplicateDetectionSchema.MatchCodeTableName(rule)}"));

    public async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    public async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }
}
