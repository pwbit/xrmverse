using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Xrmd.Storage.PostgreSql;

/// <summary>
/// Locates and runs the PostgreSQL client tools <c>pg_dump</c> and <c>pg_restore</c> of <paramref name="toolsPath"/> (plan 2.7b,
/// rulings Q7 and Q9): the xrmd image ships <c>pg_dump</c> and <c>pg_restore</c> 18, built from PostgreSQL's source, in <see cref="DefaultPath"/>
/// (<c>Storage:PgToolsPath</c>). The connection goes to the tool through the <c>PG*</c> variables of the child's environment
/// (<c>PGHOST</c>, <c>PGPORT</c>, <c>PGUSER</c>, <c>PGPASSWORD</c>, <c>PGSSLMODE</c>, …), never on its command line, which
/// other users of the machine can read; the inherited <c>PG*</c> variables are removed first, so only the connection string counts.
/// </summary>
public sealed partial class PgTools(string toolsPath)
{
    /// <summary>The configuration key of the tools' directory.</summary>
    public const string ConfigurationKey = "Storage:PgToolsPath";

    /// <summary>Where the xrmd image puts the client tools (built from source in the Dockerfile's <c>pgtools</c> stage, ruling Q9).</summary>
    public const string DefaultPath = "/usr/lib/postgresql/18/bin";

    /// <summary>The oldest major version of the tools xrmverse runs (the PostgreSQL 16 floor).</summary>
    public const int MinimumMajorVersion = 16;

    public const string PgDump = "pg_dump";
    public const string PgRestore = "pg_restore";

    /// <summary>The directory of the tools.</summary>
    public string ToolsPath { get; } = string.IsNullOrWhiteSpace(toolsPath) ? DefaultPath : toolsPath;

    /// <summary>The executable of <paramref name="tool"/> in <see cref="ToolsPath"/> (with <c>.exe</c> on Windows).</summary>
    public string PathOf(string tool) => Path.Combine(ToolsPath, ExecutableName(tool));

    private static string ExecutableName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    /// <summary>
    /// The directory of the first <paramref name="tool"/> on <paramref name="pathVariable"/> (a <c>PATH</c> value), or null when
    /// none of its directories has it.
    /// </summary>
    public static string? FindOnPath(string tool, string? pathVariable)
    {
        if (string.IsNullOrWhiteSpace(pathVariable)) return null;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, ExecutableName(tool)))) return directory;
            }
            catch (ArgumentException)
            {
                // A PATH entry with characters no path may have: not a directory of the tool.
            }
        }

        return null;
    }

    /// <summary>
    /// <c>pg_dump --format=custom --no-owner --no-privileges</c> of the database of <paramref name="source"/> into
    /// <paramref name="file"/> (ruling Q7): a consistent snapshot, without the roles and grants of the source server.
    /// </summary>
    /// <exception cref="InvalidOperationException">The tool is missing, or exited with another code than 0 (its stderr).</exception>
    public Task DumpAsync(NpgsqlConnectionStringBuilder source, string file, CancellationToken ct) => RunAsync(DumpStartInfo(source, file), ct);

    /// <inheritdoc cref="DumpAsync(NpgsqlConnectionStringBuilder, string, CancellationToken)"/>
    public Task DumpAsync(string source, string file, CancellationToken ct) => DumpAsync(new NpgsqlConnectionStringBuilder(source), file, ct);

    /// <summary>
    /// <c>pg_restore --no-owner --no-privileges --exit-on-error</c> of <paramref name="file"/> into the database of
    /// <paramref name="target"/>, which exists and is empty (ruling Q7): the objects belong to the restoring login.
    /// </summary>
    /// <remarks>
    /// A <c>pg_restore</c> of a newer major than the target server starts its session with settings the older server does not
    /// know (<c>pg_restore</c> 17 and later: <c>SET transaction_timeout = 0;</c>, which PostgreSQL 16 refuses), and
    /// <c>--exit-on-error</c> would stop there. Against such a server the restore runs without <c>--exit-on-error</c>, and it
    /// succeeds only when every error it reports is an <c>unrecognized configuration parameter</c> of those <c>SET</c>
    /// statements; any other error fails it, as <c>--exit-on-error</c> would (the caller restores into a database of its own and
    /// drops it on failure).
    /// </remarks>
    /// <exception cref="InvalidOperationException">The tool is missing, or exited with another code than 0 (its stderr).</exception>
    public Task RestoreAsync(string target, string file, CancellationToken ct) => RestoreAsync(new NpgsqlConnectionStringBuilder(target), file, ct);

    /// <inheritdoc cref="RestoreAsync(string, string, CancellationToken)"/>
    public async Task RestoreAsync(NpgsqlConnectionStringBuilder target, string file, CancellationToken ct)
    {
        var info = RestoreStartInfo(target, file);
        EnsureInstalled(info);
        var tool = await MajorVersionAsync(PgRestore, ct);
        var server = await ServerMajorVersionAsync(target, ct);
        if (tool is { } toolMajor && server < toolMajor)
        {
            await RunAsync(RestoreStartInfo(target, file, exitOnError: false), ct, OnlyNewerSettingsRefused);
            return;
        }

        await RunAsync(info, ct);
    }

    /// <summary>The major version of the server of <paramref name="connection"/> (<c>server_version_num</c> / 10000).</summary>
    public static async Task<int> ServerMajorVersionAsync(NpgsqlConnectionStringBuilder connection, CancellationToken ct)
    {
        await using var conn = await PostgresDatabases.OpenUnpooledAsync(connection.ConnectionString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT current_setting('server_version_num')::integer";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) / 10000;
    }

    /// <summary>
    /// True when a <c>pg_restore</c> run without <c>--exit-on-error</c> exited 1 and each error it reports is the refusal of a
    /// session setting the server does not know (<c>unrecognized configuration parameter</c>, from a <c>SET</c> statement).
    /// </summary>
    public static bool OnlyNewerSettingsRefused(int exitCode, string error)
    {
        if (exitCode != 1) return false;
        var lines = error.Split('\n', StringSplitOptions.TrimEntries);
        var errors = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("pg_restore: error:", StringComparison.Ordinal)) continue;
            errors++;
            if (!UnknownSettingPattern().IsMatch(lines[i])) return false;
            if (i + 1 >= lines.Length || !lines[i + 1].StartsWith("Command was: SET ", StringComparison.Ordinal)) return false;
        }

        return errors > 0 && IgnoredPattern().Match(error) is { Success: true } m
               && int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == errors;
    }

    [GeneratedRegex(@"ERROR:\s+unrecognized configuration parameter ""[a-z_]+""$")]
    private static partial Regex UnknownSettingPattern();

    [GeneratedRegex(@"errors ignored on restore: (\d+)")]
    private static partial Regex IgnoredPattern();

    /// <summary>The process <see cref="DumpAsync"/> starts: the password is in its environment only.</summary>
    public ProcessStartInfo DumpStartInfo(NpgsqlConnectionStringBuilder source, string file)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(file);
        return StartInfo(PgDump, source, ["--format=custom", "--no-owner", "--no-privileges", "--file=" + file, DatabaseArgument(source)]);
    }

    /// <summary>The process <see cref="RestoreAsync"/> starts: the password is in its environment only.</summary>
    /// <param name="exitOnError">False only against an older server (see <see cref="RestoreAsync"/>).</param>
    public ProcessStartInfo RestoreStartInfo(NpgsqlConnectionStringBuilder target, string file, bool exitOnError = true)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(file);
        string[] options = exitOnError ? ["--no-owner", "--no-privileges", "--exit-on-error"] : ["--no-owner", "--no-privileges"];
        // An absolute path: a file name starting with '-' is never read as an option.
        return StartInfo(PgRestore, target, [.. options, DatabaseArgument(target), Path.GetFullPath(file)]);
    }

    /// <summary>
    /// <c>pg_restore --list</c> of <paramref name="file"/>, with no connection: null when this <c>pg_restore</c> reads the archive,
    /// else its error output (shortened, see <see cref="Shorten"/>). A dump of a newer <c>pg_dump</c> than the tool fails here with
    /// <c>unsupported version (1.16) in file header</c> (<see cref="IsNewerArchiveError"/>).
    /// </summary>
    public async Task<string?> ArchiveErrorAsync(string file, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        var info = new ProcessStartInfo(PathOf(PgRestore)) { ArgumentList = { "--list", Path.GetFullPath(file) } };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToList())
            info.Environment.Remove(key);
        info.Environment.Remove("LC_ALL");
        info.Environment["LC_MESSAGES"] = "C";
        EnsureInstalled(info);
        var (exit, _, error) = await ExecuteAsync(info, ct);
        return exit == 0 ? null : Shorten(error.Trim().Length == 0 ? $"exit code {exit}, no error output" : error.Trim());
    }

    /// <summary>True when <paramref name="error"/> of <see cref="ArchiveErrorAsync"/> says the archive is of a newer format.</summary>
    public static bool IsNewerArchiveError(string error) => error.Contains("unsupported version", StringComparison.Ordinal);

    /// <summary>The longest tool error output an exception (and so an Admin API answer) carries.</summary>
    public const int MaxErrorLength = 4096;

    /// <summary><paramref name="text"/> cut to <see cref="MaxErrorLength"/> characters, saying how much was left out.</summary>
    public static string Shorten(string text) =>
        text.Length <= MaxErrorLength ? text : text[..MaxErrorLength] + $" … ({text.Length - MaxErrorLength} more characters)";

    /// <summary>
    /// The major version of <paramref name="tool"/> (<c>&lt;tool&gt; --version</c>: <c>pg_restore (PostgreSQL) 18.7 …</c>), or
    /// null when it is missing or does not say.
    /// </summary>
    public async Task<int?> MajorVersionAsync(string tool, CancellationToken ct)
    {
        if (!File.Exists(PathOf(tool))) return null;
        var info = new ProcessStartInfo(PathOf(tool)) { ArgumentList = { "--version" } };
        try
        {
            var (exit, output, _) = await ExecuteAsync(info, ct);
            return exit == 0 && VersionPattern().Match(output) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // not executable
        }
    }

    [GeneratedRegex(@"\(PostgreSQL\)\s+(\d+)")]
    private static partial Regex VersionPattern();

    /// <summary>
    /// The database as the tool's <c>--dbname</c>: a plain name (the organization names are letters, digits and underscores), so
    /// the tool never reads it as a connection string.
    /// </summary>
    private static string DatabaseArgument(NpgsqlConnectionStringBuilder connection)
    {
        var database = connection.Database;
        if (string.IsNullOrEmpty(database) || database.Contains('=') || database.StartsWith("postgres://", StringComparison.Ordinal)
            || database.StartsWith("postgresql://", StringComparison.Ordinal))
            throw new ArgumentException($"'{database}' is not a database name the PostgreSQL tools can take.", nameof(connection));
        return "--dbname=" + database;
    }

    private ProcessStartInfo StartInfo(string tool, NpgsqlConnectionStringBuilder connection, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(PathOf(tool))
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Insert(0, "--no-password"); // never a prompt: a missing password is an error
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToList())
            info.Environment.Remove(key);
        foreach (var (name, value) in EnvironmentOf(connection)) info.Environment[name] = value;
        // The tools' own messages in English, whatever the machine's language: RestoreAsync reads them.
        info.Environment.Remove("LC_ALL");
        info.Environment["LC_MESSAGES"] = "C";
        return info;
    }

    /// <summary>The libpq variables of <paramref name="connection"/> (only the keys it sets).</summary>
    public static IReadOnlyList<(string Name, string Value)> EnvironmentOf(NpgsqlConnectionStringBuilder connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var env = new List<(string, string)>();
        if (!string.IsNullOrEmpty(connection.Host)) env.Add(("PGHOST", connection.Host));
        if (connection.Port != 0) env.Add(("PGPORT", connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (!string.IsNullOrEmpty(connection.Username)) env.Add(("PGUSER", connection.Username));
        if (!string.IsNullOrEmpty(connection.Password)) env.Add(("PGPASSWORD", connection.Password));
        if (!string.IsNullOrEmpty(connection.ApplicationName)) env.Add(("PGAPPNAME", connection.ApplicationName));
        if (connection.Timeout > 0) env.Add(("PGCONNECT_TIMEOUT", connection.Timeout.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        env.Add(("PGSSLMODE", connection.SslMode switch
        {
            SslMode.Disable => "disable",
            SslMode.Allow => "allow",
            SslMode.Require => "require",
            SslMode.VerifyCA => "verify-ca",
            SslMode.VerifyFull => "verify-full",
            _ => "prefer",
        }));
        if (!string.IsNullOrEmpty(connection.RootCertificate)) env.Add(("PGSSLROOTCERT", connection.RootCertificate));
        if (!string.IsNullOrEmpty(connection.SslCertificate)) env.Add(("PGSSLCERT", connection.SslCertificate));
        if (!string.IsNullOrEmpty(connection.SslKey)) env.Add(("PGSSLKEY", connection.SslKey));
        return env;
    }

    private static void EnsureInstalled(ProcessStartInfo info)
    {
        if (!File.Exists(info.FileName))
            throw new InvalidOperationException(
                $"{Path.GetFileNameWithoutExtension(info.FileName)} is not installed at '{info.FileName}': install the PostgreSQL client " +
                $"tools ({MinimumMajorVersion} or later) or set {ConfigurationKey} to their directory.");
    }

    /// <param name="accept">Accepts an exit code other than 0 with its stderr; null: only 0 succeeds.</param>
    private static async Task RunAsync(ProcessStartInfo info, CancellationToken ct, Func<int, string, bool>? accept = null)
    {
        var tool = Path.GetFileNameWithoutExtension(info.FileName);
        EnsureInstalled(info);
        int exit;
        string error;
        try
        {
            (exit, _, error) = await ExecuteAsync(info, ct);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"{tool} at '{info.FileName}' could not be started: {ex.Message}", ex);
        }

        if (exit != 0 && accept?.Invoke(exit, error) != true)
        {
            var password = info.Environment.TryGetValue("PGPASSWORD", out var p) ? p : null;
            var text = error.Trim();
            if (!string.IsNullOrEmpty(password)) text = text.Replace(password, "***", StringComparison.Ordinal);
            throw new InvalidOperationException($"{tool} failed (exit code {exit}): {(text.Length == 0 ? "no error output" : Shorten(text))}");
        }
    }

    /// <summary>Runs the process to its end; standard output and error are read while it runs (no pipe fills up).</summary>
    private static async Task<(int Exit, string Output, string Error)> ExecuteAsync(ProcessStartInfo info, CancellationToken ct)
    {
        info.RedirectStandardError = true;
        info.RedirectStandardOutput = true;
        info.UseShellExecute = false;
        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (error) error.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            throw;
        }

        process.WaitForExit(); // the redirected streams are drained
        lock (output) lock (error) return (process.ExitCode, output.ToString(), error.ToString());
    }
}
