using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xrmd.Abstractions.Storage;
using Xrmd.Admin;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>A <see cref="XrmdFixture"/> on PostgreSQL whatever <c>XRMD_TEST_PROVIDER</c> says (plan 2.7a, Task 5).</summary>
public sealed class PostgresFixture() : XrmdFixture(StorageProvider.PostgreSql);

/// <summary>
/// Plan 2.7a, Task 5: organizations on PostgreSQL. The Admin API creates a database as ruling P5 says (UTF-8, ICU root locale, the
/// <c>citext</c> extension), migrates and seeds it, lists it and drops it; backup and restore run since plan 2.7b, Task 9 (ruling Q7). Runs on a PostgreSQL host of its
/// own, on both providers' runs.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class PostgresOrgTests(PostgresFixture fx) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly List<string> _created = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var db in _created) await fx.DropDatabaseAsync(db);
    }

    private Task<HttpResponseMessage> AsAdminAsync(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (body is not null) req.Content = JsonContent.Create(body);
        return fx.Client.SendAsync(req);
    }

    private static async Task<JsonNode?> JsonAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, text);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private async Task<T?> ScalarAsync<T>(string database, string sql)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionStringOf(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private async Task<bool> DatabaseExistsAsync(string database) =>
        await ScalarAsync<bool>(PostgresDatabasesName, $"SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = '{database}')");

    private const string PostgresDatabasesName = "postgres";

    [Fact]
    public async Task The_fixture_host_serves_a_postgresql_organization()
    {
        fx.Provider.Should().Be(StorageProvider.PostgreSql);
        fx.App.Services.GetRequiredService<IOrgProvisioner>().Should().BeOfType<PostgresOrgProvisioner>();
        var database = TestProvider.DatabaseOf(fx.ConnectionString);
        (await ScalarAsync<string>(database, "SELECT extname::text FROM pg_extension WHERE extname = 'citext'")).Should().Be("citext");
        (await ScalarAsync<long>(database, "SELECT COUNT(*) FROM meta.schema_version")).Should().Be(SchemaMigrator.PostgresBaselineVersion + 3, "the baseline (versions 1-18) and migrations 0019, 0020 and 0021");
    }

    [Fact]
    public async Task Create_list_and_delete_an_organization_on_postgresql()
    {
        var name = "zenith";
        _created.Add(name);
        var adminOid = Guid.NewGuid();

        var created = (await JsonAsync(await AsAdminAsync(HttpMethod.Post, "/admin/orgs", new { name, adminObjectId = adminOid, adminName = "Zenith Admin" }),
            HttpStatusCode.Created))!;
        created["database"]!.GetValue<string>().Should().Be(name);
        var adminUserId = created["adminUserId"]!.GetValue<Guid>();

        // Ruling P5: UTF-8, ICU, root locale; the citext extension; migrated to the baseline, 0019, 0020 and 0021 and seeded with its administrator.
        (await ScalarAsync<string>(PostgresDatabasesName,
            $"SELECT pg_encoding_to_char(encoding)::text || '/' || datlocprovider::text || '/' || daticulocale FROM pg_database WHERE datname = '{name}'"))
            .Should().Be("UTF8/i/und");
        (await ScalarAsync<string>(name, "SELECT extname::text FROM pg_extension WHERE extname = 'citext'")).Should().Be("citext");
        (await ScalarAsync<int>(name, "SELECT MAX(version) FROM meta.schema_version")).Should().Be(21);
        (await ScalarAsync<Guid>(name, $"SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = '{adminOid}'")).Should().Be(adminUserId);
        (await ScalarAsync<long>(name, $"""
            SELECT COUNT(*) FROM data.systemuserroles ur JOIN data.role r ON r.roleid = ur.roleid
            WHERE ur.systemuserid = '{adminUserId}' AND r.name = 'System Administrator'
            """)).Should().Be(1);

        var list = (await JsonAsync(await AsAdminAsync(HttpMethod.Get, "/admin/orgs"), HttpStatusCode.OK))!.AsArray();
        var zenith = list.Single(o => o!["database"]!.GetValue<string>() == name)!;
        zenith["schemaVersion"]!.GetValue<int>().Should().Be(21);
        zenith["served"]!.GetValue<bool>().Should().BeFalse();
        zenith["name"]!.GetValue<string>().Should().Be(name);
        list.Single(o => o!["database"]!.GetValue<string>() == TestProvider.DatabaseOf(fx.ConnectionString))!["served"]!.GetValue<bool>().Should().BeTrue();

        // A second create of the same name is a 409.
        await JsonAsync(await AsAdminAsync(HttpMethod.Post, "/admin/orgs", new { name, adminObjectId = Guid.NewGuid() }), HttpStatusCode.Conflict);
        // Task 5 fix round 1: PostgreSQL stores the quoted name as written and compares names with their case, so ZENITH is
        // another database name (SQL Server's names ignore case).
        var provisioner = fx.App.Services.GetRequiredService<IOrgProvisioner>();
        (await provisioner.ExistsAsync(name, CancellationToken.None)).Should().BeTrue();
        (await provisioner.ExistsAsync("ZENITH", CancellationToken.None)).Should().BeFalse();
        (await provisioner.ExistsAsync("Zenith", CancellationToken.None)).Should().BeFalse();

        await JsonAsync(await AsAdminAsync(HttpMethod.Delete, $"/admin/orgs/{name}"), HttpStatusCode.NoContent);
        (await DatabaseExistsAsync(name)).Should().BeFalse();
        (await JsonAsync(await AsAdminAsync(HttpMethod.Get, "/admin/orgs"), HttpStatusCode.OK))!.AsArray()
            .Should().NotContain(o => o!["database"]!.GetValue<string>() == name);
    }

    [Fact]
    public async Task The_served_database_is_not_deleted()
    {
        var served = TestProvider.DatabaseOf(fx.ConnectionString);
        await JsonAsync(await AsAdminAsync(HttpMethod.Delete, $"/admin/orgs/{served}"), HttpStatusCode.Conflict);
    }

    // This class always serves a PostgreSQL organization, whatever XRMD_TEST_PROVIDER says, so only the tools decide.
    [PgToolsInstalledFact]
    public async Task The_served_database_is_backed_up_with_pg_dump()
    {
        var served = TestProvider.DatabaseOf(fx.ConnectionString);

        // Plan 2.7b, ruling Q7: no 501 any more; the served database is backed up while it serves (a .dump of pg_dump).
        var backup = await JsonAsync(await AsAdminAsync(HttpMethod.Post, $"/admin/orgs/{served}/backup"), HttpStatusCode.OK);
        backup!["file"]!.GetValue<string>().Should().EndWith(".dump");
        backup["sizeBytes"]!.GetValue<long>().Should().BeGreaterThan(0);
    }

}
