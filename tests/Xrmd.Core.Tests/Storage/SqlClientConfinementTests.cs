using System.Text.RegularExpressions;
using FluentAssertions;

namespace Xrmd.Core.Tests.Storage;

/// <summary>
/// Plan 2.7a, Task 1 (ruling P3): SqlClient is confined to the SQL Server classes. Every other file of <c>src/</c> builds its
/// commands on <c>DbConnection</c> and types its parameters with <c>DbType</c>, so it runs on every storage provider.
/// </summary>
public sealed partial class SqlClientConfinementTests
{
    /// <summary>
    /// The files outside a <c>SqlServer/</c> folder that may use SqlClient, each with its SQL Server-only reason. A file that no
    /// longer uses SqlClient leaves the list too (the set must be equal), so it cannot slip back in unnoticed.
    /// </summary>
    private static readonly Dictionary<string, string> SqlServerFiles = new()
    {
        // The SQL Server IDbConnectionFactory: opens a SqlConnection (Task 3 adds the Npgsql one).
        ["src/Xrmd.Core/SqlConnectionFactory.cs"] = "SQL Server connection factory",
        // Creates, backs up, restores and drops SQL Server databases (master, BACKUP/RESTORE, SINGLE_USER, file sizes); the
        // SQL Server IOrgProvisioner (plan 2.7a, Task 5; PostgreSQL's is PostgresOrgProvisioner + PostgresDatabases).
        ["src/Xrmd.Admin/SqlServerOrgProvisioner.cs"] = "SQL Server database provisioning, backup and restore",
        // CREATE DATABASE on master, READ_COMMITTED_SNAPSHOT, the non-pooled SqlConnection of the instance lock, and the services
        // bound to a SqlConnectionFactory.
        ["src/Xrmd.Admin/OrganizationDatabase.cs"] = "SQL Server database creation and instance lock connection",
        // Validates and rewrites the SQL Server connection string of the installation (--sql-connection, bundled SQL Server).
        ["src/Xrmd.Cli/Server/Installation.cs"] = "SQL Server installation settings",
        // 'xrmd org restore': RESTORE DATABASE through master with SINGLE_USER / MULTI_USER.
        ["src/Xrmd.Cli/Commands/OrgCommands.cs"] = "SQL Server backup restore",
        // Opens master and creates the organization database there under sp_getapplock (the PostgreSQL path goes through
        // PostgresDatabases; plan 2.7a, Task 5).
        ["src/Xrmd.Host/DatabaseBootstrapper.cs"] = "SQL Server database bootstrap",
    };

    [Fact]
    public void Only_sql_server_classes_use_SqlClient()
    {
        var root = FindRepoRoot();
        var src = Path.Combine(root, "src");
        var users = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
            .Where(f => !f.Contains("/SqlServer/"))
            .Where(f => UsesSqlClient(File.ReadAllText(Path.Combine(root, f))))
            .Order(StringComparer.Ordinal)
            .ToList();

        users.Should().BeEquivalentTo(SqlServerFiles.Keys,
            "only the SQL Server classes may use SqlClient (plan 2.7a, ruling P3); convert a new user to DbConnection/DbType");
    }

    /// <summary>
    /// A global using of SqlClient (in a .cs file, a <c>SqlServer/</c> one included, or as <c>&lt;Using Include&gt;</c> in a project
    /// file) would let any file of the project use SqlClient types without a using line the scan above sees (Task 1 review M2).
    /// </summary>
    [Fact]
    public void No_global_using_brings_SqlClient_in()
    {
        var root = FindRepoRoot();
        string[] projectFiles = ["*.cs", "*.csproj", "*.props", "*.targets"];
        var offenders = projectFiles
            .SelectMany(pattern => Directory.EnumerateFiles(Path.Combine(root, "src"), pattern, SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
            .Where(f => GlobalSqlClient(File.ReadAllText(Path.Combine(root, f))))
            .ToList();

        offenders.Should().BeEmpty("a global using of Microsoft.Data.SqlClient escapes the confinement scan (plan 2.7a, ruling P3)");
    }

    /// <summary>
    /// A <c>DetachedParameter</c> only carries a parameter until <c>AddParameters</c> copies it: added to a command as it is, the
    /// provider refuses it at run time (Task 1 review M3). Code outside <c>Storage/</c> and the SQL Server classes adds parameters
    /// through <c>AddParameter</c>/<c>AddParameters</c> only, so it can never pass one.
    /// </summary>
    [Fact]
    public void Parameters_are_added_through_the_storage_helpers()
    {
        var root = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/") && !f.Contains("/Storage/") && !SqlServerFiles.ContainsKey(f))
            .Where(f => DirectAdd().IsMatch(File.ReadAllText(Path.Combine(root, f))))
            .ToList();

        offenders.Should().BeEmpty("parameters are added with AddParameter/AddParameters (plan 2.7a, ruling P3)");
    }

    [Fact]
    public void A_detached_parameter_added_as_it_is_fails_loudly()
    {
        using var cmd = new Microsoft.Data.SqlClient.SqlCommand();
        var detached = new Xrmd.Abstractions.Storage.DetachedParameter("@a", System.Data.DbType.Int32, 1);

        FluentActions.Invoking(() => cmd.Parameters.Add(detached)).Should().Throw<InvalidCastException>();
    }

    [Theory]
    [InlineData("using Microsoft.Data.SqlClient;", true)]
    [InlineData("var c = new SqlCommand(sql, conn);", true)]
    [InlineData("SqlConnection conn = null!;", true)]
    [InlineData("p.Add(\"@a\", SqlDbType.Int);", true)]
    [InlineData("var e = (Microsoft.Data.SqlClient.SqlException)ex;", true)]
    [InlineData("catch (SqlException ex)", true)]
    [InlineData("var b = new SqlConnectionStringBuilder(cs);", true)]
    [InlineData("SqlParameter p = null!;", true)]
    [InlineData("await using SqlDataReader rd = null!;", true)]
    [InlineData("SqlTransaction tx = null!;", true)]
    [InlineData("// one SqlCommand per page", false)]
    [InlineData("/// <see cref=\"SqlConnection\"/>", false)]
    [InlineData("new SqlConnectionFactory(cs)", false)]
    [InlineData("cmd.AddParameter(\"@a\", DbType.Int32, 1);", false)]
    public void Scanner_finds_SqlClient_code_and_ignores_comments(string line, bool expected) =>
        UsesSqlClient(line).Should().Be(expected);

    private static bool UsesSqlClient(string text) =>
        text.Split('\n').Any(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && SqlClient().IsMatch(line));

    [GeneratedRegex(@"Microsoft\.Data\.SqlClient|\bSql(Command|Connection|ConnectionStringBuilder|DbType|Exception|Parameter|ParameterCollection|Transaction|DataReader|BulkCopy)\b")]
    private static partial Regex SqlClient();

    /// <summary>
    /// Plan 2.7a, Task 3 (ruling P3): Npgsql is confined to <c>src/Xrmd.Storage.PostgreSql</c> (code, project files and global
    /// usings alike). The rest of the server reaches PostgreSQL through <c>DbConnection</c>, the dialect and the factory.
    /// </summary>
    [Fact]
    public void Only_the_postgresql_project_uses_Npgsql()
    {
        var root = FindRepoRoot();
        string[] projectFiles = ["*.cs", "*.csproj", "*.props", "*.targets"];
        var offenders = projectFiles
            .SelectMany(pattern => Directory.EnumerateFiles(Path.Combine(root, "src"), pattern, SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
            .Where(f => !f.StartsWith(PostgresProject, StringComparison.Ordinal))
            .Where(f => UsesNpgsql(File.ReadAllText(Path.Combine(root, f))))
            .ToList();

        offenders.Should().BeEmpty($"only {PostgresProject} may use Npgsql (plan 2.7a, ruling P3)");
    }

    private const string PostgresProject = "src/Xrmd.Storage.PostgreSql/";

    [Theory]
    [InlineData("using Npgsql;", true)]
    [InlineData("global using Npgsql;", true)]
    [InlineData("var c = new NpgsqlConnection(cs);", true)]
    [InlineData("catch (PostgresException ex)", true)]
    [InlineData("p.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;", true)]
    [InlineData("<PackageReference Include=\"Npgsql\" Version=\"10.0.3\" />", true)]
    [InlineData("var f = new NpgsqlConnectionFactory(cs);", false)]
    [InlineData("using Xrmd.Storage.PostgreSql;", false)]
    [InlineData("<ProjectReference Include=\"..\\Xrmd.Storage.PostgreSql\\Xrmd.Storage.PostgreSql.csproj\" />", false)]
    [InlineData("// an Npgsql connection", false)]
    public void Npgsql_scanner(string line, bool expected) => UsesNpgsql(line).Should().Be(expected);

    private static bool UsesNpgsql(string text) =>
        text.Split('\n').Any(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && Npgsql().IsMatch(line));

    [GeneratedRegex(@"\bNpgsql\b|\bNpgsqlTypes\b|\bNpgsql(Connection|Command|DataSource|DataSourceBuilder|Parameter|Exception|DbType|Transaction|DataReader|ConnectionStringBuilder|Batch)\b|\bPostgresException\b")]
    private static partial Regex Npgsql();

    [Theory]
    [InlineData("global using Microsoft.Data.SqlClient;", true)]
    [InlineData("global using static Microsoft.Data.SqlClient.SqlCommand;", true)]
    [InlineData("<Using Include=\"Microsoft.Data.SqlClient\" />", true)]
    [InlineData("using Microsoft.Data.SqlClient;", false)]
    [InlineData("<PackageReference Include=\"Microsoft.Data.SqlClient\" Version=\"7.1.1\" />", false)]
    public void Global_using_scanner(string text, bool expected) => GlobalSqlClient(text).Should().Be(expected);

    private static bool GlobalSqlClient(string text) => GlobalUsing().IsMatch(text);

    [GeneratedRegex(@"global\s+using\s+(static\s+)?Microsoft\.Data\.SqlClient|<Using\s+Include\s*=\s*""Microsoft\.Data\.SqlClient")]
    private static partial Regex GlobalUsing();

    [GeneratedRegex(@"\.Parameters\.(Add|AddRange|Insert)\(")]
    private static partial Regex DirectAdd();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
