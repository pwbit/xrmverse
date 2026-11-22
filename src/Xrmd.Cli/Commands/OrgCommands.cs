using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Storage.PostgreSql;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd org create|list|delete|backup|restore</c> over the Admin API. <c>create</c>, <c>list</c> and <c>delete</c> work on the server's
/// engine (SQL Server or PostgreSQL, <c>Storage:Provider</c>; plan 2.7a, Task 5), and so do <c>backup</c> and <c>restore</c>
/// (PostgreSQL since plan 2.7b, ruling Q7). Backup files live on the SQL Server host (a <c>.bak</c>) or in the xrmd container's
/// backup directory (a <c>.dump</c> of <c>pg_dump</c>), with a <c>.files.tar</c> of the file and image columns' blobs beside each:
/// <c>backup</c> prints the server-side path and <c>restore --file</c> takes such a path (a file in the server's backup directory).
/// </summary>
public static class OrgCommands
{
    public static Command Build(CliContext ctx) => new("org", "Create, list, back up and restore organization databases (Admin API).")
    {
        Create(ctx),
        List(ctx),
        Delete(ctx),
        Backup(ctx),
        Restore(ctx),
    };

    private static Argument<string> DatabaseArgument() => new("database") { Description = "Name of the organization database." };

    private static Command Create(CliContext ctx)
    {
        var name = new Argument<string>("name") { Description = "Name of the new organization (also its database name)." };
        var admin = new Option<string>("--admin-object-id") { Description = "Entra object id (GUID) of the organization's first System Administrator.", Required = true };
        var adminName = new Option<string?>("--admin-name") { Description = "Full name of that administrator." };
        var command = new Command("create", "Create an organization database with its first System Administrator.") { name, admin, adminName };
        command.SetAction(async (parse, ct) =>
        {
            var oid = parse.GetValue(admin)!;
            if (!Guid.TryParse(oid, out var oidValue))
                throw new CliException(ExitCodes.Usage, $"--admin-object-id must be a GUID: '{oid}'.");
            var body = new Dictionary<string, object?> { ["name"] = parse.GetValue(name), ["adminObjectId"] = oidValue };
            if (parse.GetValue(adminName) is { } n) body["adminName"] = n;
            var response = await ctx.CreateClient().AdminAsync(HttpMethod.Post, "orgs", Http.XrmdClient.JsonContent(body), ct);
            var json = response.Json ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for POST /admin/orgs.");

            // A new database is not served until an xrmd process runs on it (one process per organization database).
            var db = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("database", out var d) && d.GetString() is { } created ? created : parse.GetValue(name)!;
            // M11: never suggest a `server org add` that its own name rule would refuse.
            var hint = Server.Installation.IsValidOrgName(db, out _)
                ? $"The database '{db}' has no xrmd process serving it. To serve it, run: xrmd server org add {db} --admin-object-id {oidValue:D}"
                : $"The database '{db}' has no xrmd process serving it. `xrmd server org add` needs a name matching {Server.Installation.OrgNameRule}, " +
                  "so this database cannot be served under that name.";
            if (ctx.Format == Output.OutputFormat.Json && json.ValueKind == JsonValueKind.Object)
            {
                var withHint = JsonNode.Parse(json.GetRawText())!.AsObject();
                withHint["hint"] = hint;
                ctx.Write(JsonSerializer.SerializeToElement(withHint));
            }
            else
            {
                ctx.Write(json);
                ctx.Env.Out.WriteLine(hint);
            }
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command List(CliContext ctx)
    {
        var command = new Command("list", "List the organization databases on the server's database server (SQL Server or PostgreSQL).");
        command.SetAction(async (_, ct) =>
        {
            var response = await ctx.CreateClient().AdminAsync(HttpMethod.Get, "orgs", cancellationToken: ct);
            ctx.Write(response.Json ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for GET /admin/orgs."));
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Delete(CliContext ctx)
    {
        var database = DatabaseArgument();
        var command = new Command("delete", "Drop an organization database (not the one the server serves).") { database, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            var db = parse.GetValue(database)!;
            await ctx.ConfirmDestructiveAsync($"This permanently drops the database '{db}'. Continue?");
            await ctx.CreateClient().AdminAsync(HttpMethod.Delete, $"orgs/{Uri.EscapeDataString(db)}", cancellationToken: ct);
            ctx.WriteResult(new { deleted = db }, $"Deleted '{db}'.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Backup(CliContext ctx)
    {
        var database = DatabaseArgument();
        var command = new Command("backup", "Back up an organization database (prints the server-side path: a .bak on the SQL Server host, or a .dump of pg_dump in the server's backup directory on PostgreSQL).") { database };
        command.SetAction(async (parse, ct) =>
        {
            var db = parse.GetValue(database)!;
            var response = await ctx.CreateClient().AdminAsync(HttpMethod.Post, $"orgs/{Uri.EscapeDataString(db)}/backup", cancellationToken: ct);
            var json = response.Json ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for the backup.");
            ctx.Write(json);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "?";

    private static Command Restore(CliContext ctx)
    {
        var database = DatabaseArgument();
        var file = new Option<string>("--file") { Description = "Backup file as a path on the server (a file in the server's backup directory; see `org backup`). With --direct on PostgreSQL: a .dump on this machine.", Required = true };
        var overwrite = new Option<bool>("--overwrite") { Description = "Restore over an existing database." };
        var direct = new Option<bool>("--direct") { Description = "Run the restore from this machine against the database server (the served organization, with the server stopped); needs --connection-string. On PostgreSQL it runs pg_restore from PATH: 18 or later for the backups of the xrmd server image (it reads the dump first)." };
        var connectionString = new Option<string?>("--connection-string") { Description = "SQL Server or PostgreSQL connection string for --direct (any database it names is replaced by master, or postgres)." };
        var noFiles = new Option<bool>("--no-files") { Description = "Restore the database only, without the .files.tar archive that `org backup` writes beside the backup (required with --direct)." };
        var command = new Command("restore", "Restore an organization database and its files from a backup file. Exit code 7: restored, but files the database references are missing from the file store (listed on stderr).") { database, file, overwrite, direct, connectionString, noFiles, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            var db = parse.GetValue(database)!;
            var path = parse.GetValue(file)!;
            var over = parse.GetValue(overwrite);
            var isDirect = parse.GetValue(direct);
            var cs = parse.GetValue(connectionString);
            if (isDirect && string.IsNullOrWhiteSpace(cs)) throw new CliException(ExitCodes.Usage, "--direct requires --connection-string.");
            if (!isDirect && cs is not null) throw new CliException(ExitCodes.Usage, "--connection-string applies only with --direct.");
            var skipFiles = parse.GetValue(noFiles);
            if (isDirect && !skipFiles)
                throw new CliException(ExitCodes.Usage,
                    "--direct restores the database only (the server is stopped and the file store is not reachable from here): pass --no-files to confirm the files are left as they are.");

            // Validate everything that needs no server before the confirmation question.
            string? masterConnection = null;
            PostgresDirect? postgres = null;
            if (isDirect)
            {
                if (!OrganizationName.IsValid(db))
                    throw new CliException(ExitCodes.Usage, $"'{db}' is not a valid database name (a letter, then letters, digits or underscores; at most 100 characters).");
                if (IsPostgres(cs!)) postgres = await PreparePostgresAsync(ctx.Env, cs!, db, path, ct);
                else masterConnection = MasterConnectionString(cs!);
            }

            if (over)
                await ctx.ConfirmDestructiveAsync($"This restores '{path}' over the existing database '{db}', replacing its data. Continue?");

            if (postgres is not null)
            {
                await RestoreDirectPostgresAsync(postgres, db, path, over, ct);
            }
            else if (isDirect)
            {
                await RestoreDirectAsync(ctx.Env, masterConnection!, db, path, over, ct);
            }
            else
            {
                var body = new Dictionary<string, object?> { ["file"] = path, ["overwrite"] = over, ["files"] = !skipFiles };
                var response = await ctx.CreateClient().AdminAsync(HttpMethod.Post, $"orgs/{Uri.EscapeDataString(db)}/restore", Http.XrmdClient.JsonContent(body), ct);
                if (response.Json is { ValueKind: JsonValueKind.Object } answer && answer.TryGetProperty("missingCount", out var count) && count.GetInt32() > 0)
                {
                    ctx.WriteResult(new { restored = db, file = path, overwrite = over, direct = false, files = !skipFiles, missingCount = count.GetInt32(), missingFiles = answer.GetProperty("missingFiles") },
                        $"Restored '{db}' from '{path}', but {count.GetInt32()} file(s) are missing from the file store.");
                    await ctx.Env.Err.WriteLineAsync("warning: " + answer.GetProperty("warning").GetString());
                    foreach (var missing in answer.GetProperty("missingFiles").EnumerateArray())
                        await ctx.Env.Err.WriteLineAsync($"  missing file {missing.GetProperty("fileAttachmentId").GetGuid()} " +
                            $"({Text(missing, "entity")}.{Text(missing, "attribute")}, record {Text(missing, "recordId")})");
                    if (count.GetInt32() > answer.GetProperty("missingFiles").GetArrayLength())
                        await ctx.Env.Err.WriteLineAsync($"  ... and {count.GetInt32() - answer.GetProperty("missingFiles").GetArrayLength()} more");
                    return ExitCodes.FilesIncomplete;
                }
            }
            ctx.WriteResult(new { restored = db, file = path, overwrite = over, direct = isDirect, files = !skipFiles }, $"Restored '{db}' from '{path}'.");
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>True when <paramref name="connectionString"/> is an Npgsql one (a usage error when it is no connection string at all).</summary>
    private static bool IsPostgres(string connectionString)
    {
        try
        {
            return StorageConnectionStrings.ProviderOf(connectionString) == StorageProvider.PostgreSql;
        }
        catch (ArgumentException ex)
        {
            throw new CliException(ExitCodes.Usage, $"--connection-string is not a valid connection string: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// <c>RESTORE DATABASE</c> straight on SQL Server. The name is validated by the caller and goes through QUOTENAME; the file is a
    /// parameter. <c>WITH REPLACE</c> only for <paramref name="overwrite"/>; an existing database without it is the server's own error.
    /// </summary>
    private static string MasterConnectionString(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
        }
        catch (ArgumentException ex)
        {
            throw new CliException(ExitCodes.Usage, $"--connection-string is not a valid SQL Server connection string: {ex.Message}", ex);
        }
    }

    private static async Task RestoreDirectAsync(CliEnvironment env, string masterConnection, string database, string file, bool overwrite, CancellationToken ct)
    {
        var singleUser = false;
        SqlConnection? conn = null;
        try
        {
            conn = new SqlConnection(masterConnection);
            await conn.OpenAsync(ct);
            if (overwrite)
            {
                await using var single = new SqlCommand(
                    "IF DB_ID(@db) IS NOT NULL BEGIN DECLARE @sql nvarchar(400) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE'; EXEC(@sql); END", conn);
                single.Parameters.AddWithValue("@db", database);
                await single.ExecuteNonQueryAsync(ct);
                singleUser = true;
            }
            if (env.BeforeDirectRestore is { } hook) await hook(conn, ct);
            await using var restore = new SqlCommand("""
                DECLARE @sql nvarchar(max) = N'RESTORE DATABASE ' + QUOTENAME(@db) + N' FROM DISK = @file' + CASE WHEN @replace = 1 THEN N' WITH REPLACE' ELSE N'' END;
                EXEC sp_executesql @sql, N'@file nvarchar(512)', @file = @file;
                """, conn) { CommandTimeout = 0 };
            restore.Parameters.AddWithValue("@db", database);
            restore.Parameters.AddWithValue("@file", file);
            restore.Parameters.AddWithValue("@replace", overwrite ? 1 : 0);
            await restore.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex)
        {
            if (singleUser) await RevertMultiUserAsync(env, conn!, database);
            // Login failure: 3 (the credentials passed are rejected); unreachable server: 6; a rejected restore
            // (missing file, database exists, bad backup): 5.
            var code = ex.Number switch
            {
                18456 => ExitCodes.Auth,
                -1 or 2 or 53 or 40 or 4060 => ExitCodes.Unreachable,
                _ => ExitCodes.Rejected,
            };
            throw new CliException(code, $"SQL Server: {ex.Message}", ex);
        }
        catch when (singleUser)
        {
            await RevertMultiUserAsync(env, conn!, database); // e.g. cancellation: never leave the database SINGLE_USER silently
            throw;
        }
        finally
        {
            if (conn is not null) await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Sets the database back to MULTI_USER. When that fails the restore's own error still decides the exit code, but stderr
    /// says the database may be left SINGLE_USER and how to fix it.
    /// </summary>
    private static async Task RevertMultiUserAsync(CliEnvironment env, SqlConnection conn, string database)
    {
        try
        {
            await using var multi = new SqlCommand(
                "IF DB_ID(@db) IS NOT NULL BEGIN DECLARE @sql nvarchar(400) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET MULTI_USER WITH ROLLBACK IMMEDIATE'; EXEC(@sql); END", conn);
            multi.Parameters.AddWithValue("@db", database);
            await multi.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            await env.Err.WriteLineAsync(
                $"Warning: setting the database back to MULTI_USER failed ({ex.Message}): the database '{database}' may still be in SINGLE_USER mode; " +
                $"run ALTER DATABASE [{database}] SET MULTI_USER.");
        }
    }

    /// <summary>What <c>org restore --direct</c> on PostgreSQL needs, checked before the confirmation question.</summary>
    private sealed record PostgresDirect(CliEnvironment Env, string ConnectionString, PgTools Tools);

    /// <summary>
    /// Plan 2.7b, ruling Q7: the connection string (an Npgsql one), the name (PostgreSQL's 63 bytes) and a <c>pg_restore</c> of
    /// major 16 or later on <c>PATH</c> that reads the dump, else <see cref="ProviderGuard.DirectRestoreNeedsPgRestore"/> (exit 1, as the server's
    /// refusals).
    /// </summary>
    private static async Task<PostgresDirect> PreparePostgresAsync(CliEnvironment env, string connectionString, string database, string file,
        CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = PostgresFailures.Normalize(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new CliException(ExitCodes.Usage, $"--connection-string is not a valid PostgreSQL connection string: {ex.Message}", ex);
        }

        if ((PostgresDatabases.NameRefusal(database) ?? PostgresDatabases.ReservedNameRefusal(database)) is { } refusal)
            throw new CliException(ExitCodes.Usage, refusal);
        var directory = PgTools.FindOnPath(PgTools.PgRestore, env.GetEnvironmentVariable("PATH"));
        var tools = directory is null ? null : new PgTools(directory);
        if (tools is null || await tools.MajorVersionAsync(PgTools.PgRestore, ct) is not { } major || major < PgTools.MinimumMajorVersion)
            throw new CliException(ExitCodes.Unexpected, ProviderGuard.DirectRestoreNeedsPgRestore);

        // Task 9 fix round 1: the dump is read (pg_restore --list, no connection) before the question, so a pg_restore too old for
        // it (pg_dump 18 of the server image writes archive format 1.16) or a file that is no dump stops here, not mid-restore.
        if (!File.Exists(file)) throw new CliException(ExitCodes.NotFound, $"The backup file '{file}' does not exist on this machine.");
        if (await tools.ArchiveErrorAsync(file, ct) is { } error)
        {
            if (PgTools.IsNewerArchiveError(error))
                throw new CliException(ExitCodes.Unexpected,
                    $"pg_restore {major} on PATH cannot read '{file}', which a newer pg_dump made ({error}). {ProviderGuard.DirectRestoreNeedsPgRestore}");
            throw new CliException(ExitCodes.Rejected, $"'{file}' is not a pg_dump archive pg_restore {major} can read: {error}");
        }

        return new PostgresDirect(env, normalized, tools);
    }

    /// <summary>
    /// <c>pg_restore</c> straight on PostgreSQL, as the server restores (ruling Q7): into a new staging database created as the
    /// server creates an organization's (template0, ICU <c>und</c>), which then takes the name <paramref name="database"/>
    /// (<see cref="PostgresDatabases.SwapInAsync"/>, over the existing one only with <paramref name="overwrite"/>). A failure leaves
    /// the existing database as it was and drops the staging one. The server migrates the restored database when it starts.
    /// </summary>
    private static async Task RestoreDirectPostgresAsync(PostgresDirect direct, string database, string file, bool overwrite, CancellationToken ct)
    {
        try
        {
            await using var maintenance = await PostgresDatabases.OpenMaintenanceAsync(direct.ConnectionString, ct);
            var exists = await PostgresDatabases.ExistsAsync(maintenance, database, ct);
            if (exists && !overwrite)
                throw new CliException(ExitCodes.Rejected, $"PostgreSQL: the database '{database}' already exists; pass --overwrite to restore over it.");
            var staging = await PostgresDatabases.CreateStagingAsync(maintenance, ct);
            try
            {
                await direct.Tools.RestoreAsync(PostgresDatabases.ConnectionStringOf(direct.ConnectionString, staging), file, ct);
                if (direct.Env.BeforeDirectRestore is { } hook) await hook(maintenance, ct);
                if (!await PostgresDatabases.SwapInAsync(maintenance, direct.ConnectionString, staging, database, overwrite, null, ct))
                    throw new CliException(ExitCodes.Rejected, overwrite
                        ? $"PostgreSQL: the database '{database}' changed while it was being restored over; retry the restore."
                        : $"PostgreSQL: the database '{database}' already exists; pass --overwrite to restore over it.");
            }
            catch
            {
                try { await PostgresDatabases.DropDatabaseAsync(maintenance, direct.ConnectionString, staging, CancellationToken.None); }
                catch (Exception dropFailure) when (PostgresFailures.Classify(dropFailure) is not null || dropFailure is InvalidOperationException)
                {
                    await direct.Env.Err.WriteLineAsync($"Warning: the staging database '{staging}' could not be dropped ({dropFailure.Message}); the server's next restore drops it, or drop it by hand.");
                }

                throw;
            }
            finally
            {
                await PostgresDatabases.ReleaseClaimAsync(maintenance, staging);
            }
        }
        catch (Exception ex) when (PostgresFailures.Classify(ex) is { } failure)
        {
            var code = failure.Kind switch
            {
                PostgresFailureKind.Authentication => ExitCodes.Auth,
                PostgresFailureKind.Refused => ExitCodes.Rejected,
                _ => ExitCodes.Unreachable,
            };
            throw new CliException(code, $"PostgreSQL: {failure.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new CliException(ExitCodes.Rejected, $"PostgreSQL: {ex.Message}", ex); // pg_restore's own failure (its stderr)
        }
    }
}
