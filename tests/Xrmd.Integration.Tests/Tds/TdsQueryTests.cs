using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests.Tds;

[CollectionDefinition("tds-query")]
public class TdsQueryCollection : ICollectionFixture<TdsQueryFixture>;

/// <summary>
/// A SQL Server container with the organization <c>acme</c> served by a <see cref="TdsTestHost"/>: the SQL endpoint runs the
/// production query handler, and the test login is the System Administrator.
/// </summary>
public sealed class TdsQueryFixture : IAsyncLifetime
{
    private readonly TestDatabase _db = new();

    public Guid AdminObjectId { get; } = Guid.NewGuid();

    public string ConnectionString => _db.ConnectionString;

    public TdsTestHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        Host = await TdsTestHost.StartAsync(org: new TdsTestOrg(ConnectionString, AdminObjectId));
        await TestPublishers.EnsureAsync(ConnectionString);
    }

    /// <summary>An empty database of its own on the same server, for a host with other settings (one host per database).</summary>
    public async Task<string> NewDatabaseAsync()
    {
        var name = "tds_" + Guid.NewGuid().ToString("N")[..10];
        await TestProvider.CreateDatabaseAsync(ConnectionString, name);
        return TestProvider.WithDatabase(ConnectionString, name);
    }

    public async Task DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
        await _db.DisposeAsync();
    }
}

/// <summary>
/// Queries over the SQL endpoint (plan 3.3, Task 3): translated queries run with the caller's security through the query
/// executor, shaped with the T7 types; the row cap (T8); the system queries (T9); no plug-in runs (T10).
/// </summary>
[Collection("tds-query")]
public sealed class TdsQueryTests(TdsQueryFixture fx)
{
    /// <summary>A name prefix of this test's records, so that the tests sharing the database do not see each other's rows.</summary>
    private readonly string _prefix = "acme-" + Guid.NewGuid().ToString("N")[..8] + "-";

    private OrganizationService Service(TdsTestHost? host = null) =>
        TestServices.NewOrganizationService((host ?? fx.Host).App.Services, fx.AdminObjectId);

    private static async Task<SqlConnection> OpenAsync(TdsTestHost host)
    {
        var connection = new SqlConnection(host.SqlLoginConnectionString().ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<List<object?[]>> RowsAsync(TdsTestHost host, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync(host);
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private Task<List<object?[]>> RowsAsync(string sql, params (string Name, object Value)[] parameters) => RowsAsync(fx.Host, sql, parameters);

    private async Task<Guid> AccountAsync(string name, OrganizationService? service = null, Guid? contactId = null)
    {
        var account = new Entity("account") { ["name"] = _prefix + name };
        if (contactId is { } id) account["primarycontactid"] = new EntityReference("contact", id);
        return await (service ?? Service()).CreateAsync(account);
    }

    [Fact]
    public async Task Select_returns_seeded_accounts()
    {
        foreach (var name in new[] { "charlie", "alpha", "bravo" }) await AccountAsync(name);

        var rows = await RowsAsync($"SELECT name FROM account WHERE name LIKE '{_prefix}%' ORDER BY name");

        rows.Select(r => r.Single()).Should().Equal(_prefix + "alpha", _prefix + "bravo", _prefix + "charlie");
    }

    [Fact]
    public async Task Join_returns_contact_names()
    {
        var contactId = await Service().CreateAsync(new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = _prefix + "lovelace" });
        await AccountAsync("joined", contactId: contactId);
        await AccountAsync("alone");

        var rows = await RowsAsync($"""
            SELECT a.name, c.lastname, a.primarycontactid, a.primarycontactidname
            FROM account a JOIN contact c ON a.primarycontactid = c.contactid
            WHERE a.name LIKE '{_prefix}%'
            """);

        rows.Should().ContainSingle().Which.Should().Equal(_prefix + "joined", _prefix + "lovelace", contactId, _prefix + "lovelace, Ada");
    }

    [Fact]
    public async Task Group_by_counts()
    {
        foreach (var name in new[] { "x", "x", "y", "x" }) await AccountAsync(name);

        var rows = await RowsAsync($"SELECT name, COUNT(*) AS n FROM account WHERE name LIKE '{_prefix}%' GROUP BY name ORDER BY name");

        rows.Select(r => (r[0], r[1])).Should().Equal((_prefix + "x", 3), (_prefix + "y", 1));
    }

    /// <summary>
    /// Plan 2.7b, Task 8: text equality over the SQL endpoint compares like SQL Server's <c>_CI_AS</c> on both engines (ruling Q1,
    /// through the query model): case and trailing spaces do not count, and the stored value comes back as written.
    /// </summary>
    [Fact]
    public async Task Text_equality_ignores_case_and_trailing_spaces()
    {
        await AccountAsync("Acme");
        await AccountAsync("Acme Two");

        var rows = await RowsAsync($"SELECT TOP 5 name FROM account WHERE name = '{_prefix}acme '");

        rows.Select(r => r.Single()).Should().Equal(_prefix + "Acme");
    }

    [Fact]
    public async Task Parameterized_query_via_sp_executesql()
    {
        await AccountAsync("wanted");
        await AccountAsync("other");

        var rows = await RowsAsync("SELECT name, statecode, statecodename FROM account WHERE name = @n", ("@n", _prefix + "wanted"));

        rows.Should().ContainSingle().Which.Should().Equal(_prefix + "wanted", 0, "Active");
    }

    [Fact]
    public async Task Dates_are_utc()
    {
        var id = await AccountAsync("dated");
        var stored = (await Service().RetrieveAsync("account", id, new ColumnSet("createdon"))).GetAttributeValue<DateTime>("createdon");

        var rows = await RowsAsync("SELECT createdon FROM account WHERE accountid = @id", ("@id", id));

        stored.Kind.Should().Be(DateTimeKind.Utc);
        var value = (DateTime)rows.Single().Single()!;
        value.Should().Be(DateTime.SpecifyKind(stored, DateTimeKind.Unspecified), "datetime2 carries the UTC value, without offset");
    }

    [Fact]
    public async Task Top_queries_are_capped_by_top()
    {
        foreach (var name in new[] { "a", "b", "c" }) await AccountAsync(name);

        (await RowsAsync($"SELECT TOP 2 name FROM account WHERE name LIKE '{_prefix}%' ORDER BY name")).Should().HaveCount(2);
        // Above the query engine's page limit (5000): paged, never refused.
        (await RowsAsync($"SELECT TOP 6000 name FROM account WHERE name LIKE '{_prefix}%'")).Should().HaveCount(3);
    }

    [Fact]
    public async Task Top_0_returns_the_columns_and_no_row()
    {
        await AccountAsync("a");
        await using var connection = await OpenAsync(fx.Host);
        await using var command = new SqlCommand($"SELECT TOP 0 name, createdon FROM account WHERE name LIKE '{_prefix}%'", connection);
        await using var reader = await command.ExecuteReaderAsync();

        reader.FieldCount.Should().Be(2);
        reader.GetName(0).Should().Be("name");
        reader.GetFieldType(1).Should().Be(typeof(DateTime));
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Errors_have_sql_numbers()
    {
        await using var connection = await OpenAsync(fx.Host);

        async Task<SqlException> Error(string sql)
        {
            await using var command = new SqlCommand(sql, connection);
            return (await FluentActions.Awaiting(() => command.ExecuteScalarAsync()).Should().ThrowAsync<SqlException>()).Which;
        }

        (await Error("SELECT nosuchcolumn FROM account")).Number.Should().Be(207);
        (await Error("SELECT name FROM nosuchtable")).Number.Should().Be(208);
        (await Error("DELETE FROM account")).Number.Should().Be(40000);
    }

    [Fact]
    public async Task Row_cap_is_enforced()
    {
        var database = await fx.NewDatabaseAsync();
        await using var host = await TdsTestHost.StartAsync(org: new TdsTestOrg(database, fx.AdminObjectId,
            new Dictionary<string, string?> { ["Tds:MaxRows"] = "3" }));
        var service = Service(host);
        foreach (var name in new[] { "a", "b", "c", "d" }) await AccountAsync(name, service);

        var error = (await FluentActions.Awaiting(() => RowsAsync(host, "SELECT name FROM account")).Should().ThrowAsync<SqlException>()).Which;
        error.Number.Should().Be(40000);
        error.Message.Should().Be("The query returns more than 3 rows. Add TOP or a filter.");

        (await RowsAsync(host, "SELECT TOP 3 name FROM account")).Should().HaveCount(3);
        (await FluentActions.Awaiting(() => RowsAsync(host, "SELECT name, COUNT(*) AS n FROM account GROUP BY name"))
            .Should().ThrowAsync<SqlException>()).Which.Message.Should().Be("The query returns more than 3 rows. Add TOP or a filter.");
    }

    /// <summary>
    /// A 1:N join read over several pages (<c>Tds:PageSize = 2</c>): the root primary key repeats on every child row, so only the
    /// link primary keys the handler adds to the order give each row a fixed position; every pair comes back exactly once.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ORDER BY a.name")]
    public async Task Paged_one_to_many_join_returns_every_pair_once(string orderBy)
    {
        var database = await fx.NewDatabaseAsync();
        await using var host = await TdsTestHost.StartAsync(org: new TdsTestOrg(database, fx.AdminObjectId,
            new Dictionary<string, string?> { ["Tds:PageSize"] = "2" }));
        var service = Service(host);
        var expected = new List<(string, string)>();
        foreach (var account in new[] { "a", "b", "c" })
        {
            var accountId = await AccountAsync(account, service);
            foreach (var contact in new[] { "x", "y", "z" })
            {
                await service.CreateAsync(new Entity("contact")
                {
                    ["lastname"] = account + contact,
                    ["parentcustomerid"] = new EntityReference("account", accountId),
                });
                expected.Add((_prefix + account, account + contact));
            }
        }

        var rows = await RowsAsync(host, "SELECT a.name, c.lastname FROM account a JOIN contact c ON c.parentcustomerid = a.accountid" + orderBy);

        rows.Select(r => ((string)r[0]!, (string)r[1]!)).Should().BeEquivalentTo(expected);
        if (orderBy.Length > 0) rows.Select(r => (string)r[0]!).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Time_limit_is_enforced()
    {
        var database = await fx.NewDatabaseAsync();
        await using var host = await TdsTestHost.StartAsync(org: new TdsTestOrg(database, fx.AdminObjectId,
            new Dictionary<string, string?> { ["Tds:Timeout"] = "00:00:00.001" }));

        var error = (await FluentActions.Awaiting(() => RowsAsync(host, "SELECT name FROM account")).Should().ThrowAsync<SqlException>()).Which;
        error.Number.Should().Be(40000);
        error.Message.Should().StartWith("The query exceeded the ").And.EndWith(" minute limit.");
    }

    [Fact]
    public async Task Version_and_information_schema()
    {
        ((string)(await RowsAsync("SELECT @@VERSION")).Single().Single()!).Should().Contain("12.0.2000.8");
        (await RowsAsync("SELECT SERVERPROPERTY('ProductVersion') AS v")).Single().Single().Should().Be("12.0.2000.8");
        (await RowsAsync("SET NOCOUNT ON")).Should().BeEmpty();

        var tables = await RowsAsync("SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES");
        tables.Should().ContainEquivalentOf(new object?[] { "dbo", "account", "BASE TABLE" });

        var columns = (await RowsAsync("SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'account'"))
            .ToDictionary(r => (string)r[0]!, r => (string)r[1]!);
        columns.Should().Contain("name", "nvarchar").And.Contain("primarycontactid", "uniqueidentifier")
            .And.Contain("primarycontactidname", "nvarchar").And.Contain("statecode", "int").And.Contain("statecodename", "nvarchar")
            .And.Contain("createdon", "datetime2");
        columns.Should().NotContainKey("fullname", "the filter keeps the columns of account only");
    }

    [Fact]
    public async Task No_plugin_runs()
    {
        var sp = fx.Host.App.Services;
        await TestEntities.EnsurePipeAsync(sp.GetRequiredService<IMetadataStore>());
        await sp.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx.ConnectionString);
        sp.GetRequiredService<Xrmd.Pipeline.Registration.StepRegistry>().Invalidate();
        var service = Service();
        var assemblyId = await PluginTestHelper.RegisterAssemblyAsync(service, PluginTestHelper.TestPluginBytes);
        var typeId = await PluginTestHelper.RegisterTypeAsync(service, assemblyId, "Xrmd.TestPlugins.ContextRecorderPlugin");
        await PluginTestHelper.RegisterStepAsync(service, typeId, "RetrieveMultiple", "account", 40, config: TestEntities.PipeLog);
        await AccountAsync("watched");
        try
        {
            (await RowsAsync($"SELECT name FROM account WHERE name LIKE '{_prefix}%'")).Should().ContainSingle();
            (await MarkersAsync()).Should().Be(0, "a SQL query does not trigger plug-ins");

            // The step is live: the same read through the organization service runs it.
            await service.RetrieveMultipleAsync(new QueryExpression("account") { ColumnSet = new ColumnSet("name") });
            (await MarkersAsync()).Should().Be(1);
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx.ConnectionString);
            sp.GetRequiredService<Xrmd.Pipeline.Registration.StepRegistry>().Invalidate();
        }

        async Task<int> MarkersAsync()
        {
            return Convert.ToInt32(await TestSql.ScalarAsync(fx.ConnectionString, $"SELECT COUNT(*) FROM data.{TestEntities.PipeLog}"));
        }
    }
}
