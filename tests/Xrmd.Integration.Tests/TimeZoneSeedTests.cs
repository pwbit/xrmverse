using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>#131 (needs a database): the seeded time zones and the time zone columns of every user's settings (rulings Z2, Z3).</summary>
[Collection("xrmd")]
public sealed class TimeZoneSeedTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = await TestProvider.OpenAsync(connectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, Guid objectId, string? json = null)
    {
        var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, fx.BaseUrl));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await fx.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A Basic User the Core creates (so it has its settings row); returns its id and Entra object id.</summary>
    private async Task<(Guid UserId, Guid ObjectId)> NewBasicUserAsync()
    {
        var objectId = Guid.NewGuid();
        var run = Guid.NewGuid().ToString("N")[..8];
        var userId = await ServiceFor(fx, fx.AdminObjectId).CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = "Tess Zone " + run, ["domainname"] = $"tess.{run}@example.invalid", ["azureactivedirectoryobjectid"] = objectId,
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);
        return (userId, objectId);
    }

    [Fact]
    public async Task Seed_has_every_time_zone()
    {
        (await ScalarAsync(fx.ConnectionString, "SELECT COUNT(*) FROM data.timezonedefinition")).Should().Be(TimeZoneCatalog.All.Count);
        (await ScalarAsync(fx.ConnectionString, "SELECT standardname FROM data.timezonedefinition WHERE timezonecode = 110"))
            .Should().Be("W. Europe Standard Time");
        (await ScalarAsync(fx.ConnectionString, "SELECT bias FROM data.timezonedefinition WHERE timezonecode = 110")).Should().Be(-60);
        (await ScalarAsync(fx.ConnectionString, "SELECT COUNT(*) FROM data.timezonedefinition WHERE ISNULL(userinterfacename, '') = ''"))
            .Should().Be(0);
        (await ScalarAsync(fx.ConnectionString, $"SELECT timezonedefinitionid FROM data.timezonedefinition WHERE timezonecode = 92"))
            .Should().Be(TimeZoneSchema.RowId(92));
    }

    [Fact]
    public async Task New_user_gets_utc_and_en_us_formats()
    {
        var (user, _) = await NewBasicUserAsync();
        foreach (var (column, value) in TimeZoneSchema.UserSettingsDefaults)
            (await ScalarAsync(fx.ConnectionString, $"SELECT {column} FROM data.usersettings WHERE systemuserid = '{user}'")).Should().Be(value, column);
        // The seeded users too.
        (await ScalarAsync(fx.ConnectionString, "SELECT COUNT(*) FROM data.usersettings WHERE timezonecode IS NULL OR dateformatstring IS NULL"))
            .Should().Be(0);
    }
}

/// <summary>#131: an organization seeded before the time zone columns gets them, and their defaults once (ruling Z2).</summary>
[Collection("xrmd")]
public sealed class TimeZoneSeedUpgradeTests(XrmdFixture fx) : IAsyncLifetime
{
    private static readonly SeedOptions Options = new(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
    private static readonly string[] Columns = TimeZoneSchema.UserSettingsDefaults.Select(d => d.Column).ToArray();
    private IDbConnectionFactory _factory = null!;
    private string _database = null!;

    public async Task InitializeAsync()
    {
        _database = "xrmd_tz_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, _database);
        _factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, _database));
        await new SchemaMigrator(_factory).MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await TestProvider.DropDatabaseAsync(fx.ConnectionString, _database);
    }

    private async Task<MetadataStore> NewStoreAsync()
    {
        var store = new MetadataStore(_factory);
        await store.LoadAsync();
        return store;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        // A COUNT(*) is bigint on PostgreSQL: the counts compare as int.
        return result switch { DBNull => null, long count => checked((int)count), _ => result };
    }

    [Fact]
    public async Task Existing_org_gets_time_zones_and_defaults_once()
    {
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);

        // An organization seeded before #131: no time zone table, none of the new usersettings columns, no upgrade step.
        var columns = string.Join(", ", Columns.Select(c => $"'{c}'"));
        await ScalarAsync($"""
            DELETE FROM meta.attribute WHERE entity_logical_name = '{TimeZoneSchema.Table}';
            DELETE FROM meta.entity WHERE logical_name = '{TimeZoneSchema.Table}';
            DROP TABLE data.{TimeZoneSchema.Table};
            DELETE FROM meta.attribute WHERE entity_logical_name = 'usersettings' AND logical_name IN ({columns});
            {TestSql.Dialect.DropColumns("data.usersettings", Columns)}
            DELETE FROM meta.upgrade_step WHERE name = '{TimeZoneSchema.DefaultsStep}';
            """);
        var stale = await NewStoreAsync();
        stale.GetEntity(TimeZoneSchema.Table).Should().BeNull();

        await new SystemSeed(stale, _factory).EnsureAsync(Options);

        (await NewStoreAsync()).GetEntity(TimeZoneSchema.Table)!.ObjectTypeCode.Should().Be(4810);
        (await ScalarAsync("SELECT COUNT(*) FROM data.timezonedefinition")).Should().Be(TimeZoneCatalog.All.Count);
        (await ScalarAsync("SELECT COUNT(*) FROM data.usersettings")).Should().NotBe(0);
        foreach (var (column, value) in TimeZoneSchema.UserSettingsDefaults)
            (await ScalarAsync($"SELECT COUNT(*) FROM data.usersettings WHERE {column} IS NULL OR {column} <> {(value is string ? $"N'{value}'" : value)}"))
                .Should().Be(0, column);

        // An administrator clears a user's time zone: a restart leaves it cleared, and adds no second row per code.
        await ScalarAsync("UPDATE data.usersettings SET timezonecode = NULL");
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
        (await ScalarAsync("SELECT COUNT(*) FROM data.usersettings WHERE timezonecode IS NOT NULL")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.timezonedefinition")).Should().Be(TimeZoneCatalog.All.Count);
    }
}
