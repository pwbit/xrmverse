using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Admin;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>
/// Plan 2.7b, Task 2 (ruling Q3): the system seed step <c>pg-text-keys-299</c> rebuilds a 2.7a organization's plain unique index on a
/// text column as an index on its text key (<see cref="ISqlDialect.TextKey"/>); when the rows already collide under the new rule
/// it keeps the old index, the health report and the doctor name the key, and the organization keeps serving. Runs on PostgreSQL
/// only (SQL Server's keys compare like this already, and the step does nothing there), on a database of its own.
/// </summary>
[Collection("xrmd")]
public sealed class TextKeyUpgradeTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Index = "UX_account_name";
    private static readonly SeedOptions Options = new(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
    private IDbConnectionFactory _factory = null!;
    private string _database = null!;

    public async Task InitializeAsync()
    {
        if (!TestProvider.IsPostgres) return;
        _database = "xrmd_tk_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, _database);
        _factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, _database));
        await new SchemaMigrator(_factory).MigrateAsync();
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        if (!TestProvider.IsPostgres) return;
        await TestProvider.DropDatabaseAsync(fx.ConnectionString, _database);
    }

    private async Task SeedAsync()
    {
        var store = new MetadataStore(_factory);
        await store.LoadAsync();
        await new SystemSeed(store, _factory).EnsureAsync(Options);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.Command(sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    /// <summary>
    /// A 2.7a organization: a plain unique index on <c>account.name</c> (the alternate key of the test), the rows given, and no record
    /// of the step.
    /// </summary>
    private async Task As27aOrganizationAsync(params string[] names)
    {
        var rows = string.Join(", ", names.Select(n => $"(gen_random_uuid(), '{n}')"));
        await ScalarAsync($"""
            DELETE FROM meta.upgrade_step WHERE name = '{SystemSeed.TextKeysStep}';
            DROP INDEX IF EXISTS data."{Index}";
            CREATE UNIQUE INDEX "{Index}" ON data.account (name);
            INSERT INTO data.account (accountid, name) VALUES {rows};
            """);
    }

    private async Task<string?> IndexDefinitionAsync() =>
        (string?)await ScalarAsync($"SELECT indexdef FROM pg_catalog.pg_indexes WHERE schemaname = 'data' AND indexname = '{Index}'");

    private async Task<bool> StepRecordedAsync() =>
        Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM meta.upgrade_step WHERE name = '{SystemSeed.TextKeysStep}'")) == 1;

    private HealthReport Health()
    {
        var services = new ServiceCollection()
            .AddSingleton(_factory)
            .AddSingleton(new SchemaMigrator(_factory))
            .BuildServiceProvider();
        return new HealthReport(services, new AsyncQueueStats(services, TimeProvider.System), new ConfigurationBuilder().Build());
    }

    [Fact]
    public async Task A_new_organization_has_its_text_keys_as_expression_indexes()
    {
        if (!TestProvider.IsPostgres) return;
        ((string?)await ScalarAsync(
                "SELECT indexdef FROM pg_catalog.pg_indexes WHERE schemaname = 'data' AND indexname = 'UX_transactioncurrency_isocurrencycode'"))
            .Should().Contain("replace(replace(rtrim((isocurrencycode)::text)").And.Contain("xrmv_ci_as");
        (await StepRecordedAsync()).Should().BeTrue();
        (await Health().GetAsync(CancellationToken.None)).TextKeyConflicts.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_collision_the_upgrade_rebuilds_the_key_on_the_text_key()
    {
        if (!TestProvider.IsPostgres) return;
        await As27aOrganizationAsync("Acme", "Beta");
        (await IndexDefinitionAsync()).Should().NotContain("xrmv_ci_as");

        await SeedAsync();

        (await IndexDefinitionAsync()).Should().Be(
            $"CREATE UNIQUE INDEX \"{Index}\" ON data.account USING btree (replace(replace(rtrim((name)::text), '\u00DF'::text, 'ss'::text), '\u1E9E'::text, 'ss'::text) COLLATE data.xrmv_ci_as)");
        (await StepRecordedAsync()).Should().BeTrue();
        (await Health().GetAsync(CancellationToken.None)).TextKeyConflicts.Should().BeEmpty();
    }

    [Fact]
    public async Task Colliding_rows_keep_the_old_index_and_the_health_report_names_the_key()
    {
        if (!TestProvider.IsPostgres) return;
        await As27aOrganizationAsync("Acme", "Acme ");

        await SeedAsync(); // the organization keeps serving: no failure

        (await IndexDefinitionAsync()).Should().Be($"CREATE UNIQUE INDEX \"{Index}\" ON data.account USING btree (name)");
        (await StepRecordedAsync()).Should().BeFalse();
        var conflict = (await Health().GetAsync(CancellationToken.None)).TextKeyConflicts.Should().ContainSingle().Subject;
        conflict.Should().Be(new TextKeyConflict("account", Index));
        conflict.Message.Should().Be(
            $"Alternate key '{Index}' on 'account' has values that differ only by trailing spaces, width, kana or ß; fix the rows, then run 'xrmd server upgrade'.");

        // Fixed rows: the next start (xrmd server upgrade restarts the server) rebuilds the key.
        await ScalarAsync("DELETE FROM data.account WHERE name = 'Acme '");
        await SeedAsync();

        (await IndexDefinitionAsync()).Should().Contain("xrmv_ci_as");
        (await StepRecordedAsync()).Should().BeTrue();
        (await Health().GetAsync(CancellationToken.None)).TextKeyConflicts.Should().BeEmpty();
    }
}
