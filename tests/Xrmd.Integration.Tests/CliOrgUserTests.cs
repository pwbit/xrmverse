using Xrmd.Integration.Tests.Postgres;
using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Cli;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>The <c>org</c>, <c>user</c>, <c>role</c> and <c>logs</c> groups of <c>xrmd</c> against the in-memory host.</summary>
[Collection("xrmd")]
public sealed class CliOrgUserTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "xrmd-cli-int", Guid.NewGuid().ToString("N"));
    private readonly List<string> _databases = [];
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        var login = await RunAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", fx.AdminObjectId.ToString());
        login.Should().Be(0, _err.ToString());
    }

    public async Task DisposeAsync()
    {
        foreach (var db in _databases)
        {
            await TestProvider.DropDatabaseAsync(fx.ConnectionString, db);
        }
        try { Directory.Delete(_configDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private Func<System.Data.Common.DbConnection, CancellationToken, Task>? _beforeDirectRestore;

    /// <summary>A <c>pg_dump</c> of <paramref name="database"/> into a temporary file, with the fixture's tools.</summary>
    private async Task<string> DumpAsync(string database)
    {
        var file = Path.Combine(Path.GetTempPath(), "xrmd-cli-" + Guid.NewGuid().ToString("N") + ".dump");
        await new Xrmd.Storage.PostgreSql.PgTools(TestProvider.PgToolsPath!).DumpAsync(TestProvider.WithDatabase(fx.ConnectionString, database), file, CancellationToken.None);
        return file;
    }

    private Task<int> RunAsync(params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var env = new CliEnvironment(_out, _err, new StringReader(""), false, fx.App.Server.CreateHandler(), _configDir, new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => name switch
            {
                "XRMD_DEV_SIGNING_KEY" => DevTokenFactory.SigningKey,
                // Plan 2.7b, Task 9: the direct restore on PostgreSQL runs pg_restore from PATH; the fixture's tools come first.
                "PATH" => string.Join(Path.PathSeparator, new[] { TestProvider.PgToolsPath, Environment.GetEnvironmentVariable("PATH") }.Where(p => !string.IsNullOrEmpty(p))),
                _ => null,
            },
            BeforeDirectRestore = _beforeDirectRestore,
        };
        return CliApp.RunAsync(args, env);
    }

    private JsonElement Out() => JsonDocument.Parse(_out.ToString()).RootElement.Clone();

    private async Task<HttpStatusCode> ReadProjectsAsStatusAsync(Guid oid)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/data/v9.2/{TestEntities.ProjectSet}?$top=1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        return (await fx.Client.SendAsync(req)).StatusCode;
    }

    [Fact]
    public async Task Org_create_list_delete_through_cli()
    {
        var name = "cli_" + Guid.NewGuid().ToString("N")[..10];
        _databases.Add(name);

        (await RunAsync("org", "create", name, "--admin-object-id", Guid.NewGuid().ToString(), "--admin-name", "Org Admin", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("database").GetString().Should().Be(name);

        (await RunAsync("org", "list", "-o", "json")).Should().Be(0, _err.ToString());
        Out().EnumerateArray().Select(o => o.GetProperty("database").GetString()).Should().Contain(name);

        // Destructive without --yes and without a terminal: refused, the database stays.
        (await RunAsync("org", "delete", name)).Should().Be(2);
        _err.ToString().Should().Contain("Refusing to run a destructive command without --yes");

        (await RunAsync("org", "delete", name, "--yes")).Should().Be(0, _err.ToString());
        (await RunAsync("org", "list", "-o", "json")).Should().Be(0);
        Out().EnumerateArray().Select(o => o.GetProperty("database").GetString()).Should().NotContain(name);

        (await RunAsync("org", "delete", name, "--yes")).Should().Be(4);
        (await RunAsync("org", "create", "bad name!", "--admin-object-id", Guid.NewGuid().ToString())).Should().Be(5);
        (await RunAsync("org", "create", "x", "--admin-object-id", "not-a-guid")).Should().Be(2);
    }

    [BackupFact]
    public async Task Org_backup_then_restore_through_cli()
    {
        var name = "cli_" + Guid.NewGuid().ToString("N")[..10];
        var restored = "cli_" + Guid.NewGuid().ToString("N")[..10];
        _databases.AddRange([name, restored]);
        (await RunAsync("org", "create", name, "--admin-object-id", Guid.NewGuid().ToString())).Should().Be(0, _err.ToString());

        (await RunAsync("org", "backup", name, "-o", "json")).Should().Be(0, _err.ToString());
        var file = Out().GetProperty("file").GetString()!;

        (await RunAsync("org", "restore", restored, "--file", file)).Should().Be(0, _err.ToString());
        // Over an existing database: needs --overwrite (409 -> 5), and with it the confirmation.
        (await RunAsync("org", "restore", restored, "--file", file)).Should().Be(5);
        (await RunAsync("org", "restore", restored, "--file", file, "--overwrite")).Should().Be(2);
        (await RunAsync("org", "restore", restored, "--file", file, "--overwrite", "--yes")).Should().Be(0, _err.ToString());
    }

    [BackupFact]
    public async Task Org_restore_exits_7_and_lists_the_files_missing_from_the_store()
    {
        var (table, recordId, fileId) = await BackupFileFixtures.CreateFileAsync(fx, new byte[100]);
        var restored = "cli_" + Guid.NewGuid().ToString("N")[..10];
        _databases.Add(restored);
        try
        {
            var provisioner = BackupFileFixtures.NewProvisioner(fx);
            var store = fx.App.Services.GetRequiredService<Xrmd.Files.IFileStore>();
            var served = TestProvider.DatabaseOf(fx.ConnectionString);
            provisioner.Seams().BeforeArchive = ct => store.DeleteAsync($"{served}/{fileId}", ct);
            var backup = await provisioner.BackupAsync(served, CancellationToken.None);

            (await RunAsync("org", "restore", restored, "--file", backup.File, "-o", "json")).Should().Be(ExitCodes.FilesIncomplete, _err.ToString());
            ExitCodes.FilesIncomplete.Should().Be(7);
            _err.ToString().Should().Contain("warning:").And.Contain(fileId.ToString()).And.Contain(table);
            Out().GetProperty("missingCount").GetInt32().Should().BeGreaterOrEqualTo(1);
        }
        finally
        {
            await ServiceFor(fx, fx.AdminObjectId).DeleteAsync(table, recordId);
        }
    }

    [BackupFact]
    public async Task Org_restore_direct_without_no_files_is_a_usage_error()
    {
        (await RunAsync("org", "restore", "x", "--file", "/b.bak", "--direct", "--connection-string", fx.ConnectionString)).Should().Be(2);
        _err.ToString().Should().Contain("--no-files");
    }

    [BackupFact]
    public async Task Org_restore_no_files_restores_without_the_archive_and_its_absence_is_an_error_otherwise()
    {
        var name = "cli_" + Guid.NewGuid().ToString("N")[..10];
        var restored = "cli_" + Guid.NewGuid().ToString("N")[..10];
        _databases.AddRange([name, restored]);
        (await RunAsync("org", "create", name, "--admin-object-id", Guid.NewGuid().ToString())).Should().Be(0, _err.ToString());
        (await RunAsync("org", "backup", name, "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("filesFile").GetString().Should().EndWith(".files.tar");
        var file = Out().GetProperty("file").GetString()!;
        File.Delete(Path.Combine(fx.FilesBackupDirectory, Path.GetFileNameWithoutExtension(file) + ".files.tar"));

        (await RunAsync("org", "restore", restored, "--file", file)).Should().NotBe(0);
        _err.ToString().Should().Contain(".files.tar");
        (await RunAsync("org", "restore", restored, "--file", file, "--no-files")).Should().Be(0, _err.ToString());
    }

    [BackupFact]
    public async Task Org_restore_direct_needs_both_options()
    {
        (await RunAsync("org", "restore", "x", "--file", "/b.bak", "--direct")).Should().Be(2);
        _err.ToString().Should().Contain("--connection-string");
        (await RunAsync("org", "restore", "x", "--file", "/b.bak", "--connection-string", "Server=.")).Should().Be(2);
        (await RunAsync("org", "restore", "bad name", "--file", "/b.bak", "--direct", "--connection-string", "Server=.", "--no-files")).Should().Be(2);
        // Validation precedes the destructive confirmation: the error is about the name, not about --yes.
        (await RunAsync("org", "restore", "bad name", "--file", "/b.bak", "--direct", "--connection-string", "Server=.", "--overwrite", "--no-files")).Should().Be(2);
        _err.ToString().Should().Contain("not a valid database name").And.NotContain("Refusing");
        (await RunAsync("org", "restore", "x", "--file", "/b.bak", "--direct", "--connection-string", "not a connection string", "--no-files")).Should().Be(2);
    }

    [BackupFact]
    public async Task Org_restore_direct_restores_a_backup()
    {
        var name = "cli_" + Guid.NewGuid().ToString("N")[..10];
        var target = "cli_" + Guid.NewGuid().ToString("N")[..10];
        _databases.AddRange([name, target]);
        (await RunAsync("org", "create", name, "--admin-object-id", Guid.NewGuid().ToString())).Should().Be(0, _err.ToString());
        (await RunAsync("org", "backup", name, "-o", "json")).Should().Be(0, _err.ToString());
        var file = Out().GetProperty("file").GetString()!;

        if (TestProvider.IsPostgres)
        {
            // Plan 2.7b, ruling Q7: pg_restore of this machine into a new database, which then takes the name; the .dump is local here.
            (await RunAsync("org", "restore", target, "--file", file, "--direct", "--connection-string", fx.ConnectionString, "--no-files")).Should().Be(0, _err.ToString());
            (await TestSql.ScalarAsync(TestProvider.WithDatabase(fx.ConnectionString, target), "SELECT COUNT(*) FROM data.solution WHERE uniquename = 'Default'"))
                .Should().Be(1L);
            // An existing database without --overwrite: refused, exit 5.
            (await RunAsync("org", "restore", target, "--file", file, "--direct", "--connection-string", fx.ConnectionString, "--no-files")).Should().Be(5);
            _err.ToString().Should().Contain("PostgreSQL:").And.Contain("already exists");
        }
        else
        {
            // Restoring under another name without MOVE would collide with the source's files: the direct restore reports that as exit 5.
            (await RunAsync("org", "restore", target, "--file", file, "--direct", "--connection-string", fx.ConnectionString, "--no-files")).Should().Be(5);
            _err.ToString().Should().Contain("SQL Server:");
        }

        // Over the same database with --overwrite --yes it works (its own files are reused).
        (await RunAsync("org", "restore", name, "--file", file, "--direct", "--connection-string", fx.ConnectionString, "--overwrite", "--no-files")).Should().Be(2);
        (await RunAsync("org", "restore", name, "--file", file, "--direct", "--connection-string", fx.ConnectionString, "--overwrite", "--yes", "--no-files")).Should().Be(0, _err.ToString());
    }

    [BackupFact]
    public async Task Org_restore_direct_failure_leaves_the_database_multi_user()
    {
        var name = "cli_" + Guid.NewGuid().ToString("N")[..10];
        _databases.Add(name);
        (await RunAsync("org", "create", name, "--admin-object-id", Guid.NewGuid().ToString())).Should().Be(0, _err.ToString());

        if (TestProvider.IsPostgres)
        {
            // A file that is not a backup: refused when the dump is read (pg_restore --list), before anything is created.
            var notADump = Path.Combine(Path.GetTempPath(), "xrmd-not-a-dump-" + Guid.NewGuid().ToString("N") + ".dump");
            await File.WriteAllTextAsync(notADump, "not a dump");
            // A dump cut short: its table of contents reads, its data does not, so pg_restore fails mid-restore. The staging
            // database goes, the existing database is untouched and open.
            var truncated = await DumpAsync(name);
            await using (var stream = new FileStream(truncated, FileMode.Open, FileAccess.Write))
                stream.SetLength(stream.Length * 3 / 4);
            try
            {
                (await RunAsync("org", "restore", name, "--file", notADump, "--direct",
                    "--connection-string", fx.ConnectionString, "--overwrite", "--yes", "--no-files")).Should().Be(5, _err.ToString());
                _err.ToString().Should().Contain("is not a pg_dump archive pg_restore");
                (await RunAsync("org", "restore", name, "--file", truncated, "--direct",
                    "--connection-string", fx.ConnectionString, "--overwrite", "--yes", "--no-files")).Should().Be(5, _err.ToString());
                _err.ToString().Should().Contain("PostgreSQL:").And.Contain("pg_restore");
            }
            finally
            {
                File.Delete(notADump);
                File.Delete(truncated);
            }

            (await TestSql.ScalarAsync(TestProvider.WithDatabase(fx.ConnectionString, name), "SELECT COUNT(*) FROM data.solution WHERE uniquename = 'Default'"))
                .Should().Be(1L);
            (await TestSql.ScalarAsync(TestProvider.Maintenance(fx.ConnectionString), "SELECT COUNT(*) FROM pg_database WHERE datname LIKE 'xrmd_restore_%'"))
                .Should().Be(0L, "the staging database is dropped");
            return;
        }

        (await RunAsync("org", "restore", name, "--file", "/var/opt/mssql/data/no-such-file.bak", "--direct",
            "--connection-string", fx.ConnectionString, "--overwrite", "--yes", "--no-files")).Should().NotBe(0);
        _err.ToString().Should().Contain("SQL Server:");

        await using var conn = await TestProvider.OpenAsync(TestProvider.WithDatabase(fx.ConnectionString, "master"));
        await using var cmd = TestSql.Command(conn, "SELECT user_access_desc FROM sys.databases WHERE name = @db");
        cmd.Parameters.AddWithValue("@db", name);
        (await cmd.ExecuteScalarAsync()).Should().Be("MULTI_USER");
    }

    [SqlServerFact("SINGLE_USER and its MULTI_USER revert are SQL Server's (the PostgreSQL direct restore swaps a staging database in)")]
    public async Task Org_restore_direct_warns_when_the_multi_user_revert_fails()
    {
        var name = "cli_" + Guid.NewGuid().ToString("N")[..10];
        _databases.Add(name);
        (await RunAsync("org", "create", name, "--admin-object-id", Guid.NewGuid().ToString())).Should().Be(0, _err.ToString());
        (await RunAsync("org", "backup", name, "-o", "json")).Should().Be(0, _err.ToString());
        var file = Out().GetProperty("file").GetString()!;

        // The CLI's connection is killed after SINGLE_USER: the RESTORE fails and so does the MULTI_USER revert on it.
        _beforeDirectRestore = async (conn, ct) =>
        {
            await using var killer = await TestProvider.OpenAsync(TestProvider.WithDatabase(fx.ConnectionString, "master"));
            await TestSql.Command(killer, $"KILL {((SqlConnection)conn).ServerProcessId}").ExecuteNonQueryAsync(ct);
        };
        var exit = await RunAsync("org", "restore", name, "--file", file, "--direct", "--connection-string", fx.ConnectionString, "--overwrite", "--yes", "--no-files");
        _beforeDirectRestore = null;

        // The restore's own failure keeps its exit code (5, or 6 when the killed connection is reported as a transport error).
        exit.Should().BeOneOf([5, 6], _err.ToString());
        _err.ToString().Should().Contain($"'{name}' may still be in SINGLE_USER mode").And.Contain($"ALTER DATABASE [{name}] SET MULTI_USER");
    }

    [BackupFact]
    public async Task Org_restore_direct_login_failure_is_an_auth_error()
    {
        if (TestProvider.IsPostgres)
        {
            var file = await DumpAsync(TestProvider.DatabaseOf(fx.ConnectionString)); // read before the login (pg_restore --list)
            try
            {
                var wrong = new Npgsql.NpgsqlConnectionStringBuilder(fx.ConnectionString) { Password = "not-the-password" }.ConnectionString;
                (await RunAsync("org", "restore", "anything", "--file", file, "--direct", "--connection-string", wrong, "--no-files"))
                    .Should().Be(3, _err.ToString());
                _err.ToString().Should().Contain("password authentication failed").And.NotContain("not-the-password");
            }
            finally
            {
                File.Delete(file);
            }

            return;
        }

        var bad = new SqlConnectionStringBuilder(fx.ConnectionString) { Password = "not-the-password" }.ConnectionString;
        (await RunAsync("org", "restore", "anything", "--file", "/var/opt/mssql/data/x.bak", "--direct", "--connection-string", bad, "--no-files"))
            .Should().Be(3, _err.ToString());
        _err.ToString().Should().Contain("Login failed");
    }

    [Fact]
    public async Task User_add_then_role_assign_gives_access_on_next_request()
    {
        var oid = Guid.NewGuid();
        var roleName = "cli-reader-" + Guid.NewGuid().ToString("N")[..8];
        await CreateRoleAsync(fx, roleName, ("prvReadpb_Project", DepthMask.Global));

        (await RunAsync("user", "add", "--object-id", oid.ToString(), "--name", "Cli Added", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("azureactivedirectoryobjectid").GetString().Should().Be(oid.ToString());
        (await ReadProjectsAsStatusAsync(oid)).Should().Be(HttpStatusCode.Forbidden);

        (await RunAsync("role", "assign", roleName, "--user", oid.ToString())).Should().Be(0, _err.ToString());
        (await ReadProjectsAsStatusAsync(oid)).Should().Be(HttpStatusCode.OK);

        (await RunAsync("user", "list", "-o", "json")).Should().Be(0, _err.ToString());
        Out().EnumerateArray().Select(u => u.GetProperty("azureactivedirectoryobjectid").GetString()).Should().Contain(oid.ToString());
        (await RunAsync("role", "list", "-o", "json")).Should().Be(0, _err.ToString());
        Out().EnumerateArray().Select(r => r.GetProperty("name").GetString()).Should().Contain(roleName);
    }

    [Fact]
    public async Task Role_assign_by_domain_name_and_in_a_child_business_unit()
    {
        var root = await RootBusinessUnitAsync(fx);
        var buName = "cli-bu-" + Guid.NewGuid().ToString("N")[..8];
        await CreateBusinessUnitAsync(fx, root, buName);
        var domain = $"{Guid.NewGuid():N}@example.test";
        var oid = Guid.NewGuid();
        (await RunAsync("user", "add", "--object-id", oid.ToString(), "--name", "Child BU", "--domain-name", domain, "--business-unit", buName)).Should().Be(0, _err.ToString());

        // The role exists only in the root business unit, the user is in the child one: unknown role -> 4.
        var roleName = "cli-rbu-" + Guid.NewGuid().ToString("N")[..8];
        await CreateRoleAsync(fx, roleName);
        (await RunAsync("role", "assign", roleName, "--user", domain)).Should().Be(4);
        _err.ToString().Should().Contain(roleName);

        (await RunAsync("user", "add", "--object-id", Guid.NewGuid().ToString(), "--name", "x", "--business-unit", "no such bu")).Should().Be(4);
    }

    [Fact]
    public async Task Role_assign_unknown_role_is_exit_4()
    {
        var oid = Guid.NewGuid();
        (await RunAsync("user", "add", "--object-id", oid.ToString(), "--name", "No Role")).Should().Be(0, _err.ToString());

        (await RunAsync("role", "assign", "no-such-role-" + Guid.NewGuid().ToString("N"), "--user", oid.ToString())).Should().Be(4);
        _err.ToString().Should().Contain("Role").And.Contain("not found");
        (await RunAsync("role", "assign", "x", "--user", Guid.NewGuid().ToString())).Should().Be(4);
        _err.ToString().Should().Contain("User");
        (await RunAsync("role", "assign", "x", "--user", "nobody@example.test")).Should().Be(4);
    }

    [Fact]
    public async Task Logs_json_is_an_array()
    {
        (await RunAsync("logs", "-o", "json", "--take", "5")).Should().Be(0, _err.ToString());
        var logs = Out();
        logs.ValueKind.Should().Be(JsonValueKind.Array);
        logs.GetArrayLength().Should().BeLessOrEqualTo(5);

        (await RunAsync("logs", "-o", "json", "--level", "Critical", "--category", "no.such.category")).Should().Be(0);
        Out().GetArrayLength().Should().Be(0);
        (await RunAsync("logs", "--level", "loud")).Should().Be(5);
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process may run.");
    }
}
