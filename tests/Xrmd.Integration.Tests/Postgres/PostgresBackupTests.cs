using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Npgsql;
using Xrmd.Abstractions.Storage;
using Xrmd.Admin;
using Xrmd.Core.Errors;
using Xrmd.Storage.PostgreSql;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>
/// A fact of backup and restore on PostgreSQL (plan 2.7b, rulings Q7 to Q9): skipped on SQL Server, and skipped with the reason
/// when this machine has no <c>pg_dump</c> (<see cref="TestProvider.PgToolsPath"/>). CI's postgres job installs the PostgreSQL 18
/// client from PGDG and sets <c>XRMD_TEST_PG_TOOLS</c>, so it runs these facts with tools newer than its PostgreSQL 16 server.
/// </summary>
public sealed class PgToolsFactAttribute : FactAttribute
{
    public PgToolsFactAttribute() =>
        Skip = !TestProvider.IsPostgres ? "PostgreSQL only: pg_dump and pg_restore (SQL Server's backups are AdminOrgTests')"
            : TestProvider.PgToolsPath is null ? "pg_dump is not installed on this machine (install the PostgreSQL 18 client tools, or set XRMD_TEST_PG_TOOLS)"
            : null;
}

/// <summary>
/// A backup or restore fact that runs on both engines: on PostgreSQL it needs <c>pg_dump</c> and <c>pg_restore</c>, so it is
/// skipped with the reason when this machine has none (SQL Server always runs it).
/// </summary>
public sealed class BackupFactAttribute : FactAttribute
{
    public BackupFactAttribute() =>
        Skip = TestProvider.IsPostgres && TestProvider.PgToolsPath is null
            ? "pg_dump is not installed on this machine (install the PostgreSQL 18 client tools, or set XRMD_TEST_PG_TOOLS)"
            : null;
}

/// <summary>
/// A fact that needs <c>pg_dump</c> on a class that always serves a PostgreSQL organization (its own fixture), whatever
/// <c>XRMD_TEST_PROVIDER</c> says: skipped only when this machine has no PostgreSQL client tools.
/// </summary>
public sealed class PgToolsInstalledFactAttribute : FactAttribute
{
    public PgToolsInstalledFactAttribute() =>
        Skip = TestProvider.PgToolsPath is null
            ? "pg_dump is not installed on this machine (install the PostgreSQL 18 client tools, or set XRMD_TEST_PG_TOOLS)"
            : null;
}

/// <summary>
/// Plan 2.7b, Task 9: backup and restore of PostgreSQL organizations with <c>pg_dump</c>/<c>pg_restore</c> (ruling Q7). A backup
/// restored under a new name serves the same records and files, and its alternate keys still refuse duplicates: the collation
/// <c>data.xrmv_ci_as</c> and the expression indexes on text keys (#299) survive <c>pg_restore</c>. A restore loads a staging
/// database and swaps it in, so the target is never seen half restored and a failed restore leaves it as it was. The tools never
/// get the password on their command line.
/// </summary>
[Collection("xrmd")]
public sealed class PostgresBackupTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly List<string> _created = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var db in _created) await TestProvider.DropDatabaseAsync(fx.ConnectionString, db);
    }

    private string ServedDatabase => TestProvider.DatabaseOf(fx.ConnectionString);

    private string NewName()
    {
        var name = "pgbk_" + Guid.NewGuid().ToString("N")[..10];
        _created.Add(name);
        return name;
    }

    private string ConnectionStringOf(string database) => TestProvider.WithDatabase(fx.ConnectionString, database);

    private PostgresOrgProvisioner NewProvisioner() => (PostgresOrgProvisioner)BackupFileFixtures.NewProvisioner(fx);

    private Task<object?> ScalarAsync(string database, string sql) => TestSql.ScalarAsync(ConnectionStringOf(database), sql);

    private async Task<long> CountLeftoversAsync() => Convert.ToInt64(await TestSql.ScalarAsync(TestProvider.Maintenance(fx.ConnectionString),
        "SELECT COUNT(*) FROM pg_database WHERE datname LIKE 'xrmd_restore_%' OR datname LIKE 'xrmd_replaced_%'"));

    /// <summary>A host serving <paramref name="database"/> with the fixture's settings (another organization of the same server).</summary>
    private WebApplicationFactory<Program> HostOf(string database) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting(StorageSettings.ProviderKey, TestProvider.Setting(fx.Provider));
        b.UseSetting("ConnectionStrings:Org", ConnectionStringOf(database));
        b.UseSetting("Auth:Mode", "Development");
        b.UseSetting("Auth:Audience", "http://localhost/");
        b.UseSetting("Auth:DevSigningKey", DevTokenFactory.SigningKey);
        b.UseSetting("Seed:OrgName", XrmdFixture.OrgName);
        b.UseSetting("Seed:AdminObjectId", fx.AdminObjectId.ToString());
        b.UseSetting("Pipeline:AsyncWorkerEnabled", "false");
        b.UseSetting("Files:FileSystem:Root", fx.FilesRoot);
    });

    [PgToolsFact]
    public async Task A_backup_restored_under_a_new_name_serves_the_same_records_files_and_alternate_keys()
    {
        await ParityTestKit.EnsureKeyTableAsync(fx);
        var set = ParityTestKit.SetOf(fx);
        var key = "Acme" + Guid.NewGuid().ToString("N")[..8];
        var keyed = await ParityTestKit.SendAsync(fx, HttpMethod.Post, set, $$"""{ "pb_name": "{{key}}" }""");
        keyed.StatusCode.Should().Be(HttpStatusCode.NoContent, await keyed.Content.ReadAsStringAsync());
        var accountName = "Backup " + Guid.NewGuid().ToString("N")[..8];
        var accountId = await ServiceFor(fx, fx.AdminObjectId).CreateAsync(new Entity("account") { ["name"] = accountName });
        var content = RandomNumberGenerator.GetBytes(70_000);
        var (table, recordId, fileId) = await BackupFileFixtures.CreateFileAsync(fx, content);
        try
        {
            var backup = await NewProvisioner().BackupAsync(ServedDatabase, CancellationToken.None);
            backup.File.Should().EndWith(".dump").And.StartWith(fx.BackupDirectory);
            backup.SizeBytes.Should().Be(new FileInfo(backup.File).Length).And.BeGreaterThan(0);
            backup.FilesCount.Should().BeGreaterThanOrEqualTo(1);

            var target = NewName();
            var restored = await NewProvisioner().RestoreAsync(target, backup.File, overwrite: false, files: true, CancellationToken.None);
            restored.MissingCount.Should().Be(0);
            (await CountLeftoversAsync()).Should().Be(0, "the staging database took the target's name");

            // The schema-level objects of #299 and the versionnumber sequence came with the dump.
            (await ScalarAsync(target, "SELECT COUNT(*) FROM pg_collation c JOIN pg_namespace n ON n.oid = c.collnamespace " +
                                       "WHERE n.nspname = 'data' AND c.collname = 'xrmv_ci_as' AND NOT c.collisdeterministic")).Should().Be(1L);

            await using var host = HostOf(target);
            var service = TestServices.NewOrganizationService(host.Services, fx.AdminObjectId);
            (await service.RetrieveAsync("account", accountId, new ColumnSet("name"))).GetAttributeValue<string>("name").Should().Be(accountName);

            // A new row of the restored organization draws a version after every restored one (the sequence and its trigger).
            var maxRestored = Convert.ToInt64(await ScalarAsync(target, "SELECT MAX(versionnumber) FROM data.account"));
            var created = await service.CreateAsync(new Entity("account") { ["name"] = "After restore" });
            Convert.ToInt64(await ScalarAsync(target, $"SELECT versionnumber FROM data.account WHERE accountid = '{created}'"))
                .Should().BeGreaterThan(maxRestored);

            // The file, served by the restored organization from the restored blobs.
            var init = (InitializeFileBlocksDownloadResponse)await service.ExecuteAsync(new InitializeFileBlocksDownloadRequest
                { Target = new EntityReference(table, recordId), FileAttributeName = BackupFileFixtures.Attribute });
            init.FileSizeInBytes.Should().Be(content.Length);
            var block = (DownloadBlockResponse)await service.ExecuteAsync(new DownloadBlockRequest
                { FileContinuationToken = init.FileContinuationToken, Offset = 0, BlockLength = content.Length });
            block.Data.Should().Equal(content);
            (await fx.App.Services.GetRequiredService<Xrmd.Files.IFileStore>().GetLengthAsync($"{target}/{fileId}", CancellationToken.None))
                .Should().Be(content.Length);

            // The alternate key (an expression index on the text key, collation data.xrmv_ci_as) still refuses a duplicate.
            var client = host.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/" + set)
            {
                Content = new StringContent($$"""{ "pb_name": "{{key.ToUpperInvariant()}} " }""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, client.BaseAddress!.ToString()));
            var duplicate = await client.SendAsync(request);
            var body = await duplicate.Content.ReadAsStringAsync();
            duplicate.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed, body);
            JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>().Should().Be("0x80040237");
        }
        finally
        {
            await ServiceFor(fx, fx.AdminObjectId).DeleteAsync(table, recordId);
        }
    }

    [PgToolsFact]
    public async Task A_restore_swaps_the_database_in_whole_and_holds_the_target_until_then()
    {
        // Two organizations of the run's server, made by the provisioner.
        var provisioner = NewProvisioner();
        var existing = NewName();
        await provisioner.CreateAsync(existing, Guid.NewGuid(), "Before", CancellationToken.None);
        var backup = await provisioner.BackupAsync(existing, CancellationToken.None);
        await TestSql.ExecAsync(ConnectionStringOf(existing), "UPDATE data.systemuser SET fullname = 'After' WHERE fullname = 'Before'");

        // Over the existing database: until the swap it keeps its own data and stays open, and nobody else gets its instance lock.
        var seen = new List<string>();
        provisioner.BeforeSwap = async ct =>
        {
            seen.Add((string)(await ScalarAsync(existing, "SELECT fullname FROM data.systemuser WHERE fullname IN ('Before', 'After')"))!);
            var other = await OrganizationDatabase.TryTakeInstanceLockAsync(ConnectionStringOf(existing), StorageProvider.PostgreSql, ct);
            seen.Add(other is null ? "locked" : "free");
            if (other is not null) await other.DisposeAsync();
        };
        await provisioner.RestoreAsync(existing, backup.File, overwrite: true, files: true, CancellationToken.None);

        seen.Should().Equal("After", "locked");
        (await ScalarAsync(existing, "SELECT COUNT(*) FROM data.systemuser WHERE fullname = 'Before'")).Should().Be(1L);
        (await CountLeftoversAsync()).Should().Be(0, "the replaced database is dropped");
        (await OrganizationDatabase.IsInstanceLockHeldByAnotherAsync(ConnectionStringOf(existing), StorageProvider.PostgreSql, CancellationToken.None))
            .Should().BeFalse("the restore released the instance lock");

        // Under a new name: the name appears only with the whole database.
        var target = NewName();
        var exists = new List<bool>();
        provisioner.BeforeSwap = async _ => exists.Add(await TestProvider.DatabaseExistsAsync(fx.ConnectionString, target));
        await provisioner.RestoreAsync(target, backup.File, overwrite: false, files: true, CancellationToken.None);
        exists.Should().Equal(false);
        (await ScalarAsync(target, "SELECT COUNT(*) FROM data.systemuser WHERE fullname = 'Before'")).Should().Be(1L);
    }

    [PgToolsFact]
    public async Task A_failed_restore_leaves_the_target_as_it_was_and_drops_its_staging_database()
    {
        var provisioner = NewProvisioner();
        var existing = NewName();
        await provisioner.CreateAsync(existing, Guid.NewGuid(), "Kept", CancellationToken.None);
        var notADump = Path.Combine(fx.BackupDirectory, $"{existing}-garbage.dump");
        Directory.CreateDirectory(fx.BackupDirectory);
        await File.WriteAllTextAsync(notADump, "this is not a pg_dump archive");

        var act = () => provisioner.RestoreAsync(existing, notADump, overwrite: true, files: false, CancellationToken.None);

        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("not a usable PostgreSQL backup");
        (await ScalarAsync(existing, "SELECT COUNT(*) FROM data.systemuser WHERE fullname = 'Kept'")).Should().Be(1L, "the target is untouched and open");
        (await CountLeftoversAsync()).Should().Be(0);
        (await OrganizationDatabase.IsInstanceLockHeldByAnotherAsync(ConnectionStringOf(existing), StorageProvider.PostgreSql, CancellationToken.None))
            .Should().BeFalse();
    }

    /// <summary>
    /// Task 9 fix round 1: two backups of one organization in the same second (a pinned clock) whose dumps are both written before
    /// either is renamed (a barrier in <see cref="PostgresOrgProvisioner.AfterDump"/>) get two whole files: each writes its own
    /// temporary file, and the second rename takes the next free name instead of replacing the first.
    /// </summary>
    [PgToolsFact]
    public async Task Two_backups_of_one_organization_in_the_same_second_get_two_whole_files()
    {
        var provisioner = NewProvisioner();
        var database = NewName();
        await provisioner.CreateAsync(database, Guid.NewGuid(), "Twice", CancellationToken.None);
        var instant = new DateTime(2031, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        provisioner.UtcNow = () => instant;
        var arrived = 0;
        var bothDumped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provisioner.AfterDump = async ct =>
        {
            if (Interlocked.Increment(ref arrived) == 2) bothDumped.SetResult();
            await bothDumped.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        };

        var results = await Task.WhenAll(
            provisioner.BackupAsync(database, CancellationToken.None),
            provisioner.BackupAsync(database, CancellationToken.None));

        var stem = Path.Combine(fx.BackupDirectory, $"{database}-20310102030405");
        results.Select(r => r.File).Should().BeEquivalentTo([stem + ".dump", stem + "-1.dump"]);
        results.Select(r => r.FilesFile).Should().OnlyHaveUniqueItems();
        Directory.GetFiles(fx.BackupDirectory, $"{database}-*.tmp").Should().BeEmpty("each temporary file was renamed");
        foreach (var result in results)
        {
            new FileInfo(result.File).Length.Should().Be(result.SizeBytes);
            var copy = NewName();
            await NewProvisioner().RestoreAsync(copy, result.File, overwrite: false, files: true, CancellationToken.None);
            (await ScalarAsync(copy, "SELECT COUNT(*) FROM data.systemuser WHERE fullname = 'Twice'")).Should().Be(1L, "each dump is whole");
        }
    }

    /// <summary>
    /// Task 9 fix round 1: the staging and replaced databases a stopped restore left behind (here made by hand, each looking like
    /// an organization database) are not organizations: <c>GET /admin/orgs</c> leaves them out, and the next restore drops those
    /// no session is on and no running restore claims. A sweep that runs while a restore is between its <c>pg_restore</c> and its
    /// swap keeps that restore's staging database.
    /// </summary>
    [PgToolsFact]
    public async Task Leftovers_of_restores_are_not_listed_and_the_next_restore_drops_those_nobody_uses()
    {
        var provisioner = NewProvisioner();
        var source = NewName();
        await provisioner.CreateAsync(source, Guid.NewGuid(), "Swept", CancellationToken.None);
        var backup = await provisioner.BackupAsync(source, CancellationToken.None);

        var stale = await LeftoverAsync(PostgresDatabases.StagingPrefix);
        var busy = await LeftoverAsync(PostgresDatabases.ReplacedPrefix);
        var claimed = await LeftoverAsync(PostgresDatabases.StagingPrefix);
        var names = (await provisioner.ListAsync(CancellationToken.None)).Select(o => o.Database).ToList();
        names.Should().Contain(source).And.NotContain([stale, busy, claimed]);
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/orgs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var listed = await fx.Client.SendAsync(request);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await listed.Content.ReadAsStringAsync()).Should().Contain(source).And.NotContain(PostgresDatabases.StagingPrefix)
            .And.NotContain(PostgresDatabases.ReplacedPrefix);

        // A session on one, a claim (as a running restore holds it) on another.
        await using var session = new NpgsqlConnection(Unpooled(busy));
        await session.OpenAsync();
        await using var claim = await PostgresDatabases.OpenUnpooledAsync(TestProvider.Maintenance(fx.ConnectionString), CancellationToken.None);
        await PostgresDatabases.ClaimAsync(claim, claimed, CancellationToken.None);

        // The restore's own staging database survives a sweep run between its pg_restore and its swap (it holds its claim).
        var swept = new List<string>();
        var stagingSeen = new List<string>();
        provisioner.BeforeSwap = async ct =>
        {
            await using var other = await PostgresDatabases.OpenUnpooledAsync(TestProvider.Maintenance(fx.ConnectionString), ct);
            stagingSeen.AddRange(await LeftoverNamesAsync());
            swept.AddRange(await PostgresDatabases.SweepLeftoversAsync(other, fx.ConnectionString, ct));
        };
        var first = NewName();
        await provisioner.RestoreAsync(first, backup.File, overwrite: false, files: true, CancellationToken.None);
        (await ScalarAsync(first, "SELECT COUNT(*) FROM data.systemuser WHERE fullname = 'Swept'")).Should().Be(1L, "the restore went through");
        swept.Should().BeEmpty("the stale one went at the start of the restore, and the restore's own staging is claimed");
        stagingSeen.Should().Contain(n => n.StartsWith(PostgresDatabases.StagingPrefix) && n != claimed);

        var left = await LeftoverNamesAsync();
        left.Should().BeEquivalentTo([busy, claimed], "the restore dropped the stale one only");

        await session.CloseAsync();
        await PostgresDatabases.ReleaseClaimAsync(claim, claimed);
        provisioner.BeforeSwap = null;
        await provisioner.RestoreAsync(NewName(), backup.File, overwrite: false, files: false, CancellationToken.None);
        (await LeftoverNamesAsync()).Should().BeEmpty();
    }

    /// <summary>A database named <paramref name="prefix"/> + GUID with a <c>meta.schema_version</c> table, made with no pooled session left on it.</summary>
    private async Task<string> LeftoverAsync(string prefix)
    {
        var name = prefix + Guid.NewGuid().ToString("N");
        _created.Add(name);
        (await PostgresDatabases.CreateDatabaseAsync(TestProvider.Maintenance(fx.ConnectionString), name, CancellationToken.None)).Should().BeTrue();
        await TestSql.ExecAsync(Unpooled(name), "CREATE SCHEMA meta; CREATE TABLE meta.schema_version (version int); INSERT INTO meta.schema_version VALUES (1)");
        return name;
    }

    private string Unpooled(string database) => new NpgsqlConnectionStringBuilder(ConnectionStringOf(database)) { Pooling = false }.ConnectionString;

    private async Task<List<string>> LeftoverNamesAsync()
    {
        var names = new List<string>();
        await using var conn = new NpgsqlConnection(Unpooled(TestProvider.MaintenanceDatabase(fx.ConnectionString)));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT datname FROM pg_database WHERE datname LIKE 'xrmd\\_restore\\_%' OR datname LIKE 'xrmd\\_replaced\\_%'", conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) names.Add(rd.GetString(0));
        return names;
    }

    /// <summary>A name the restore reserves is refused as an organization's (400), so no organization is ever swept.</summary>
    [PgToolsFact]
    public async Task Names_of_the_restore_are_refused_for_organizations()
    {
        var act = () => NewProvisioner().CreateAsync(PostgresDatabases.StagingPrefix + "acme", Guid.NewGuid(), null, CancellationToken.None);
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Message.Should().Contain("is reserved");
    }

    [Fact]
    public void The_tools_get_the_password_in_their_environment_never_in_their_arguments()
    {
        var tools = new PgTools("/opt/pg/bin");
        var connection = new NpgsqlConnectionStringBuilder("Host=db.example.com;Port=6543;Database=acme;Username=xrmd;Password=s3cr3t-Pa55;SSL Mode=Require");

        foreach (var info in new[] { tools.DumpStartInfo(connection, "/backups/acme.dump"), tools.RestoreStartInfo(connection, "/backups/acme.dump"),
                     tools.RestoreStartInfo(connection, "/backups/acme.dump", exitOnError: false) })
        {
            info.Arguments.Should().BeEmpty("every argument goes through ArgumentList");
            info.ArgumentList.Should().NotContain(a => a.Contains("s3cr3t", StringComparison.Ordinal));
            info.ArgumentList.Should().Contain(["--no-password", "--no-owner", "--no-privileges", "--dbname=acme"]);
            info.UseShellExecute.Should().BeFalse();
            info.Environment["PGPASSWORD"].Should().Be("s3cr3t-Pa55");
            info.Environment["PGHOST"].Should().Be("db.example.com");
            info.Environment["PGPORT"].Should().Be("6543");
            info.Environment["PGUSER"].Should().Be("xrmd");
            info.Environment["PGSSLMODE"].Should().Be("require");
            info.Environment.Keys.Should().NotContain("PGDATABASE");
        }

        var dump = tools.DumpStartInfo(connection, "/backups/acme.dump");
        dump.FileName.Should().Be(Path.Combine("/opt/pg/bin", "pg_dump"));
        dump.ArgumentList.Should().Contain(["--format=custom", "--file=/backups/acme.dump"]);
        tools.RestoreStartInfo(connection, "/backups/acme.dump").ArgumentList.Should().Contain("--exit-on-error").And.EndWith("/backups/acme.dump");
        tools.RestoreStartInfo(connection, "/backups/acme.dump", exitOnError: false).ArgumentList.Should().NotContain("--exit-on-error");
    }

    /// <summary>Task 9 fix round 1: a tool's error output reaches an exception (and an Admin API answer) cut to a few KB.</summary>
    [Fact]
    public void Tool_errors_are_cut_to_a_few_kilobytes()
    {
        PgTools.Shorten("short").Should().Be("short");
        var shortened = PgTools.Shorten(new string('e', PgTools.MaxErrorLength + 10_000));
        shortened.Should().StartWith(new string('e', PgTools.MaxErrorLength)).And.EndWith(" … (10000 more characters)");
        shortened.Length.Should().BeLessThan(PgTools.MaxErrorLength + 100);
    }

    [Fact]
    public void An_older_server_accepts_only_the_refusal_of_the_newer_session_settings()
    {
        const string refused = """
            pg_restore: error: could not execute query: ERROR:  unrecognized configuration parameter "transaction_timeout"
            Command was: SET transaction_timeout = 0;
            pg_restore: warning: errors ignored on restore: 1
            """;
        PgTools.OnlyNewerSettingsRefused(1, refused).Should().BeTrue();
        PgTools.OnlyNewerSettingsRefused(0, refused).Should().BeFalse();
        PgTools.OnlyNewerSettingsRefused(1, refused + """

            pg_restore: error: could not execute query: ERROR:  relation "data.account" already exists
            Command was: CREATE TABLE data.account (id uuid);
            """).Should().BeFalse("any other error fails the restore");
        PgTools.OnlyNewerSettingsRefused(1, "pg_restore: error: input file does not appear to be a valid archive").Should().BeFalse();
    }
}
