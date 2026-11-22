using System.Text.RegularExpressions;
using FluentAssertions;

namespace Xrmd.Core.Tests.Storage;

/// <summary>
/// Plan 2.7a, ruling P3 (plan 2.7b, final review I2): code that builds SQL asks the dialect for what differs between the engines
/// and never checks which engine it runs on. A comparison with <c>StorageProvider.PostgreSql</c> or <c>StorageProvider.SqlServer</c>
/// (or a type test of a dialect) is allowed only in the storage dialects, the PostgreSQL storage project, the host, the CLI and
/// the composition of the Admin provisioning, each file listed here with its reason.
/// </summary>
public sealed partial class EngineCheckConfinementTests
{
    /// <summary>
    /// The files that may name an engine. A file that no longer does leaves the list too (the set must be equal), so a check
    /// cannot slip back in unnoticed.
    /// </summary>
    private static readonly Dictionary<string, string> EngineFiles = new()
    {
        // Storage dialects and their settings.
        ["src/Xrmd.Abstractions/Storage/SqlServer/SqlServerDialect.cs"] = "the SQL Server dialect names its engine",
        ["src/Xrmd.Abstractions/Storage/StorageSettings.cs"] = "parses Storage:Provider",
        ["src/Xrmd.Abstractions/Storage/StorageConnectionStrings.cs"] = "tells the engine of a connection string",
        // The PostgreSQL storage project.
        ["src/Xrmd.Storage.PostgreSql/PostgresDialect.cs"] = "the PostgreSQL dialect names its engine",
        ["src/Xrmd.Storage.PostgreSql/PostgresStorage.cs"] = "registers the PostgreSQL storage",
        // The host: picks the database bootstrap, the instance lock and the storage registration of the configured engine.
        ["src/Xrmd.Host/DatabaseBootstrapper.cs"] = "database bootstrap per engine",
        ["src/Xrmd.Host/InstanceLock.cs"] = "instance lock per engine",
        ["src/Xrmd.Host/XrmdHost.cs"] = "storage registration per engine",
        // The CLI: installation settings, server commands and backup restore per engine.
        ["src/Xrmd.Cli/Commands/OrgCommands.cs"] = "organization restore per engine",
        ["src/Xrmd.Cli/Commands/ServerCommands.cs"] = "server commands per engine",
        ["src/Xrmd.Cli/Server/Installation.cs"] = "installation settings per engine",
        // The Admin provisioning composition: one IOrgProvisioner per engine and what they share.
        ["src/Xrmd.Admin/AdminEndpoints.cs"] = "registers the provisioner of the configured engine",
        ["src/Xrmd.Admin/OrganizationDatabase.cs"] = "connections and services of an organization database per engine",
        ["src/Xrmd.Admin/PostgresOrgProvisioner.cs"] = "the PostgreSQL provisioner",
        ["src/Xrmd.Admin/SqlServerOrgProvisioner.cs"] = "the SQL Server provisioner",
    };

    [Fact]
    public void Only_the_listed_files_check_the_engine()
    {
        var root = FindRepoRoot();
        var users = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
            .Where(f => ChecksEngine(File.ReadAllText(Path.Combine(root, f))))
            .Order(StringComparer.Ordinal)
            .ToList();

        users.Should().BeEquivalentTo(EngineFiles.Keys,
            "code outside the dialects, the host, the CLI and the Admin provisioning asks the dialect (ISqlDialect) for what differs " +
            "between the engines instead of checking StorageProvider (plan 2.7a, ruling P3)");
    }

    [Theory]
    [InlineData("var own = s.Transaction is null && s.Dialect.Provider == StorageProvider.PostgreSql", true)]
    [InlineData("if (provider != StorageProvider.SqlServer) return;", true)]
    [InlineData("return d.Provider is StorageProvider.PostgreSql ? a : b;", true)]
    [InlineData("case StorageProvider.SqlServer:", true)]
    [InlineData("if (s.Dialect is PostgresDialect) return;", true)]
    [InlineData("var pg = dialect as SqlServerDialect;", true)]
    [InlineData("var own = s.Transaction is null && s.Dialect.KeyLocksNeedTransaction", false)]
    [InlineData("// on StorageProvider.PostgreSql the key lock holds", false)]
    [InlineData("/// <see cref=\"StorageProvider.SqlServer\"/>", false)]
    [InlineData("StorageProvider Provider { get; }", false)]
    public void Scanner_finds_engine_checks_and_ignores_comments(string line, bool expected) => ChecksEngine(line).Should().Be(expected);

    private static bool ChecksEngine(string text) =>
        text.Split('\n').Any(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && EngineCheck().IsMatch(line));

    [GeneratedRegex(@"\bStorageProvider\.(PostgreSql|SqlServer)\b|\b(is|as)\s+(PostgresDialect|SqlServerDialect)\b")]
    private static partial Regex EngineCheck();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
