using FluentAssertions;

namespace Xrmd.Cli.Tests;

/// <summary>The <c>org</c> commands that run without a server.</summary>
public class OrgCommandsTests
{
    /// <summary>
    /// Plan 2.7b, ruling Q7: <c>org restore --direct</c> given a PostgreSQL connection string runs <c>pg_restore</c> from <c>PATH</c>;
    /// without one it stops with the Q7 text (exit 1, as the server's refusals), before any connection and before the
    /// confirmation question, instead of SqlClient's <c>Keyword not supported: 'host'</c>.
    /// </summary>
    [Fact]
    public async Task Direct_restore_of_a_postgresql_organization_without_pg_restore_on_path_is_refused_with_the_q7_text()
    {
        var empty = Directory.CreateTempSubdirectory("xrmd-no-pg-restore-");
        try
        {
            using var cli = new TestCli();
            cli.Vars["PATH"] = empty.FullName;

            var code = await cli.RunAsync(["org", "restore", "acme", "--file", "/backups/acme.dump", "--direct", "--no-files", "--overwrite",
                "--connection-string", "Host=localhost;Username=postgres;Password=secret"]);

            code.Should().Be(ExitCodes.Unexpected, cli.Err.ToString());
            cli.Err.ToString().Should().Contain("'xrmd org restore --direct' on PostgreSQL needs pg_restore on PATH, of the PostgreSQL version that made the backup or later: 18 or later for the backups of the xrmd server image (at least 16).")
                .And.NotContain("Keyword not supported").And.NotContain("Refusing").And.NotContain("secret");
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    /// <summary>A <c>pg_restore</c> on <c>PATH</c> older than 16 is refused with the same text.</summary>
    [Fact]
    public async Task Direct_restore_with_a_pg_restore_older_than_16_is_refused_with_the_q7_text()
    {
        if (OperatingSystem.IsWindows()) return; // the fake tool is a shell script
        var dir = Directory.CreateTempSubdirectory("xrmd-old-pg-restore-");
        try
        {
            var tool = System.IO.Path.Combine(dir.FullName, "pg_restore");
            await File.WriteAllTextAsync(tool, "#!/bin/sh\necho 'pg_restore (PostgreSQL) 15.8'\n");
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var cli = new TestCli();
            cli.Vars["PATH"] = dir.FullName;

            var code = await cli.RunAsync(["org", "restore", "acme", "--file", "/backups/acme.dump", "--direct", "--no-files",
                "--connection-string", "Host=localhost;Username=postgres;Password=secret"]);

            code.Should().Be(ExitCodes.Unexpected, cli.Err.ToString());
            cli.Err.ToString().Should().Contain("'xrmd org restore --direct' on PostgreSQL needs pg_restore on PATH, of the PostgreSQL version that made the backup or later: 18 or later for the backups of the xrmd server image (at least 16).");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Task 9 fix round 1: a <c>pg_restore</c> 16 cannot read the archive format 1.16 of <c>pg_dump</c> 18 (the server image's). The
    /// dump is read with <c>pg_restore --list</c> before the confirmation question, and the refusal names the version needed; a
    /// file that is no dump is rejected (exit 5) there too. Neither connects to the server.
    /// </summary>
    [Theory]
    [InlineData("pg_restore: error: unsupported version (1.16) in file header", ExitCodes.Unexpected, "pg_restore 16 on PATH cannot read")]
    [InlineData("pg_restore: error: input file does not appear to be a valid archive", ExitCodes.Rejected, "is not a pg_dump archive pg_restore 16 can read")]
    public async Task Direct_restore_reads_the_dump_before_the_question(string listError, int expected, string message)
    {
        if (OperatingSystem.IsWindows()) return; // the fake tool is a shell script
        var dir = Directory.CreateTempSubdirectory("xrmd-pg-restore-16-");
        try
        {
            var log = System.IO.Path.Combine(dir.FullName, "calls.log");
            var tool = System.IO.Path.Combine(dir.FullName, "pg_restore");
            await File.WriteAllTextAsync(tool, $$"""
                #!/bin/sh
                echo "$@" >> '{{log}}'
                if [ "$1" = "--version" ]; then echo 'pg_restore (PostgreSQL) 16.4'; exit 0; fi
                echo '{{listError}}' >&2
                exit 1

                """);
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var dump = System.IO.Path.Combine(dir.FullName, "-acme.dump");
            await File.WriteAllTextAsync(dump, "PGDMP");
            using var cli = new TestCli();
            cli.Vars["PATH"] = dir.FullName;

            var code = await cli.RunAsync(["org", "restore", "acme", "--file", dump, "--direct", "--no-files", "--overwrite",
                "--connection-string", "Host=192.0.2.1;Username=postgres;Password=secret;Timeout=1"]);

            code.Should().Be(expected, cli.Err.ToString());
            cli.Err.ToString().Should().Contain(message).And.Contain(listError).And.NotContain("Refusing").And.NotContain("secret");
            if (expected == ExitCodes.Unexpected)
                cli.Err.ToString().Should().Contain("18 or later for the backups of the xrmd server image");
            (await File.ReadAllLinesAsync(log)).Should().Equal("--version", $"--list {dump}");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
