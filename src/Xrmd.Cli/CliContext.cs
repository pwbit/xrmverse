using System.CommandLine;
using Xrmd.Cli.Auth;
using Xrmd.Cli.Config;
using Xrmd.Cli.Http;
using Xrmd.Cli.Output;

namespace Xrmd.Cli;

/// <summary>
/// What a command group needs: the environment, the global options (<c>--output</c>, <c>--url</c>,
/// <c>--profile</c>, recursive on the root), profile resolution, HTTP clients, output and confirmation.
/// One context per invocation; the global option values are those of the current parse.
/// </summary>
public sealed class CliContext : IDisposable
{
    public const string NoProfileMessage = "No profile: run `xrmd auth login`.";
    public const string RefuseDestructiveMessage =
        "Refusing to run a destructive command without --yes when stdin is not a terminal.";

    private HttpClient? _http;

    public CliContext(CliEnvironment env)
    {
        Env = env;
        Profiles = new ProfileStore(env.ConfigDir);
    }

    public CliEnvironment Env { get; }

    public ProfileStore Profiles { get; }

    public Option<OutputFormat> OutputOption { get; } = new("--output", "-o")
    {
        Description = "Output format: table (default) or json (only JSON on stdout).",
        HelpName = "json|table",
        Recursive = true,
        DefaultValueFactory = _ => OutputFormat.Table,
    };

    public Option<string?> UrlOption { get; } = new("--url")
    {
        Description = "Server URL; overrides the profile's URL.",
        Recursive = true,
    };

    public Option<string?> ProfileOption { get; } = new("--profile")
    {
        Description = "Profile name in config.json; default: the current profile.",
        Recursive = true,
    };

    internal ParseResult? ParseResult { get; set; }

    private ParseResult Parse => ParseResult ?? throw new InvalidOperationException("The command line has not been parsed.");

    public OutputFormat Format => Parse.GetValue(OutputOption);

    /// <summary>The <c>--url</c> value, normalized; null when absent.</summary>
    public string? Url => Parse.GetValue(UrlOption) is { } url ? NormalizeUrl(url) : null;

    /// <summary>The <c>--profile</c> value; null when absent.</summary>
    public string? ProfileName => Parse.GetValue(ProfileOption);

    /// <summary>Write a result to stdout in the selected <see cref="Format"/>.</summary>
    public void Write(object rows) => new Renderer(Env.Out).Write(rows, Format);

    /// <summary>
    /// Write a result: <paramref name="rows"/> in json, <paramref name="tableMessage"/> as one line in table
    /// (for commands whose natural table output is a sentence).
    /// </summary>
    public void WriteResult(object rows, string tableMessage)
    {
        if (Format == OutputFormat.Json) Write(rows);
        else Env.Out.WriteLine(tableMessage);
    }

    /// <summary>The profile named by <c>--profile</c>, else the current one; null when there is none.</summary>
    public (string Name, Profile Profile)? TryResolveProfile()
    {
        var config = Profiles.Load();
        var name = ProfileName ?? config.CurrentProfile;
        if (name is null) return null;
        if (!config.Profiles.TryGetValue(name, out var profile))
        {
            if (ProfileName is not null)
                throw new CliException(ExitCodes.Auth, $"Profile '{name}' not found: run `xrmd auth login --profile {name}`.");
            return null;
        }
        if (Url is not { } url) return (name, profile);
        // H7: the profile's CA file is trusted for the profile's own host only; --url to another host does without it.
        var sameHost = profile.CaFile is not null && Uri.TryCreate(url, UriKind.Absolute, out var given)
            && Uri.TryCreate(profile.Url, UriKind.Absolute, out var own) && string.Equals(given.IdnHost, own.IdnHost, StringComparison.OrdinalIgnoreCase);
        return (name, profile with { Url = url, CaFile = sameHost ? profile.CaFile : null });
    }

    /// <summary>The resolved profile (with <c>--url</c> applied), or exit 3 "No profile".</summary>
    public (string Name, Profile Profile) RequireProfile() =>
        TryResolveProfile() ?? throw new CliException(ExitCodes.Auth, NoProfileMessage);

    /// <summary>
    /// A client authenticated with the profile's token. <c>--url</c> alone is not enough: without a profile
    /// this is exit 3 "No profile".
    /// </summary>
    public XrmdClient CreateClient()
    {
        var (_, profile) = RequireProfile();
        return CreateClient(profile);
    }

    /// <summary>A client authenticated with a given profile (e.g. another organization's, for <c>server org remove --drop-database</c>).</summary>
    public XrmdClient CreateClient(Profile profile)
    {
        var tokens = TokenProviders.Create(profile, Env);
        return new XrmdClient(HttpFor(profile), new Uri(profile.Url), tokens.GetTokenAsync);
    }

    /// <summary>
    /// A client without a token, for commands that need none (health probes, <c>server init</c>): the URL is
    /// <c>--url</c>, else the profile's; exit 2 when there is neither.
    /// </summary>
    public XrmdClient CreateAnonymousClient()
    {
        var profile = TryResolveProfile()?.Profile;
        var url = Url ?? profile?.Url
            ?? throw new CliException(ExitCodes.Usage, "--url is required when there is no profile.");
        return new XrmdClient(profile is not null ? HttpFor(profile) : Http, new Uri(url), null);
    }

    /// <summary>
    /// Confirmation of a destructive command. Returns at once with <paramref name="yes"/>; without a terminal on
    /// stdin it is exit 2 with the refusal of the Global Constraints; otherwise asks on stderr and reads stdin,
    /// continuing on <c>y</c>/<c>yes</c> and exiting 1 ("Aborted.") on anything else.
    /// </summary>
    /// <summary>
    /// The one <c>--yes</c>/<c>-y</c> option of destructive commands: add it to the command
    /// (<c>new Command("delete") { ctx.YesOption }</c>) and call <see cref="ConfirmDestructiveAsync(string)"/>.
    /// </summary>
    public Option<bool> YesOption { get; } = new("--yes", "-y")
    {
        Description = "Skip the confirmation of a destructive command (required when stdin is not a terminal).",
    };

    /// <summary><see cref="ConfirmDestructiveAsync(string, bool)"/> with the parsed <see cref="YesOption"/>.</summary>
    public Task ConfirmDestructiveAsync(string prompt) => ConfirmDestructiveAsync(prompt, Parse.GetValue(YesOption));

    public async Task ConfirmDestructiveAsync(string prompt, bool yes)
    {
        if (yes) return;
        if (!Env.IsInputTty) throw new CliException(ExitCodes.Usage, RefuseDestructiveMessage);
        await Env.Err.WriteAsync($"{prompt} [y/N] ");
        await Env.Err.FlushAsync();
        var answer = (await Env.In.ReadLineAsync())?.Trim();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            throw new CliException(ExitCodes.Unexpected, "Aborted.");
    }

    /// <summary>An absolute http(s) URL with a trailing slash; exit 2 otherwise.</summary>
    public static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new CliException(ExitCodes.Usage, $"--url must be an absolute http or https URL: '{url}'.");
        var text = uri.AbsoluteUri;
        return text.EndsWith('/') ? text : text + "/";
    }

    /// <summary>
    /// No client-side timeout (long Admin API operations such as backup/restore would be misreported as
    /// unreachable); requests end with the server or with the command's cancellation token (Ctrl+C).
    /// </summary>
    internal HttpClient Http => _http ??= new HttpClient(Env.Handler ?? new SocketsHttpHandler(), disposeHandler: Env.Handler is null)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly Dictionary<(string Host, string CaFile), HttpClient> _scoped = new();

    /// <summary>Disposes <see cref="Http"/> and the per-profile clients of <see cref="HttpFor"/> (a test handler is not disposed).</summary>
    public void Dispose()
    {
        _http?.Dispose();
        foreach (var client in _scoped.Values) client.Dispose();
        _scoped.Clear();
    }

    /// <summary>
    /// The client for a profile's server: <see cref="Http"/>, or for an https profile with a <see cref="Profile.CaFile"/> one
    /// that trusts that root for the profile's host only (H7, <see cref="ScopedTrust"/>). Same timeout rule as <see cref="Http"/>.
    /// </summary>
    internal HttpClient HttpFor(Profile profile)
    {
        if (Env.Handler is not null || ScopedHost(profile) is not { } host) return Http;
        var key = (host, profile.CaFile!);
        if (!_scoped.TryGetValue(key, out var client))
            _scoped[key] = client = new HttpClient(ScopedTrust.CreateHandler(host, LoadCaFile(profile))) { Timeout = Timeout.InfiniteTimeSpan };
        return client;
    }

    /// <summary>
    /// A new handler for a profile's server (the MCP bridge owns its own): the test handler of <see cref="CliEnvironment.Handler"/>
    /// when there is one, else a <see cref="SocketsHttpHandler"/> with the profile's scoped trust (H7).
    /// </summary>
    internal HttpMessageHandler CreateHandler(Profile profile) =>
        Env.Handler ?? (ScopedHost(profile) is { } host ? ScopedTrust.CreateHandler(host, LoadCaFile(profile)) : new SocketsHttpHandler());

    /// <summary>The host the profile's CA file is trusted for: its URL's host when it is https and has a CA file; else null.</summary>
    private static string? ScopedHost(Profile profile) =>
        profile.CaFile is not null && Uri.TryCreate(profile.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.IdnHost : null;

    private System.Security.Cryptography.X509Certificates.X509Certificate2 LoadCaFile(Profile profile)
    {
        try
        {
            return ScopedTrust.LoadRoot(profile.CaFile!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            throw new CliException(ExitCodes.Unexpected,
                $"Cannot read the CA file {profile.CaFile} of the profile for {profile.Url}: {ex.Message} " +
                $"`xrmd server up --dir <installation>` exports Caddy's root certificate there again; or remove \"caFile\" from the profile in {Profiles.FilePath}.", ex);
        }
    }
}
