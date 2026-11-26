namespace Xrmd.Cli;

/// <summary>The process exit codes of <c>xrmd</c> (Global Constraints).</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    /// <summary>Unexpected failure or a server 5xx / unmapped 4xx.</summary>
    public const int Unexpected = 1;
    /// <summary>Invalid command line.</summary>
    public const int Usage = 2;
    /// <summary>401/403, no profile, no token.</summary>
    public const int Auth = 3;
    /// <summary>404.</summary>
    public const int NotFound = 4;
    /// <summary>400/409/412: the server rejected the request.</summary>
    public const int Rejected = 5;
    /// <summary>Connection refused, DNS failure, timeout.</summary>
    public const int Unreachable = 6;
    /// <summary><c>org restore</c> finished but the restored database references files the file store does not hold.</summary>
    public const int FilesIncomplete = 7;
    /// <summary>A migration package that cannot be read or trusted: not a package, unknown format, or an entry that fails verification.</summary>
    public const int PackageInvalid = 8;
    /// <summary>A comparison (<c>solution diff</c>) finished and found differences.</summary>
    public const int Differences = 9;
}

/// <summary>
/// A failure that ends the command: <see cref="CliApp"/> writes <see cref="Exception.Message"/> to stderr
/// and exits with <see cref="ExitCode"/>. Commands throw it instead of printing errors themselves.
/// </summary>
public sealed class CliException(int exitCode, string message, Exception? inner = null) : Exception(message, inner)
{
    public int ExitCode { get; } = exitCode;

    /// <summary>The HTTP status that caused the failure, when it came from the server (401 and 403 share exit code 3).</summary>
    public int? HttpStatus { get; init; }

    /// <summary>The raw response body when the failure came from the server (commands that parse structured error bodies).</summary>
    public string? ResponseBody { get; init; }
}
