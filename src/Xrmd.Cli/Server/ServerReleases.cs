using System.Net.Http.Headers;
using System.Text.Json;

namespace Xrmd.Cli.Server;

/// <summary>A published server release: its version (the tag without the leading <c>v</c>) and GitHub metadata.</summary>
public sealed record ServerRelease(string Version, string Tag, DateTimeOffset? PublishedAt, bool Prerelease);

/// <summary>
/// The published releases of the server (<see cref="ReleasesRepository"/> on GitHub), read only when the user asks for them
/// (<c>server upgrade --to latest</c>, <c>server versions</c>): nothing is sent but an anonymous GET with a generic
/// user agent. <c>latest</c> is resolved here to an exact version; no moving image tag exists.
/// </summary>
public static class ServerReleases
{
    /// <summary>The public repository whose GitHub releases are the server releases.</summary>
    public const string ReleasesRepository = "pwbit/xrmverse-releases";

    /// <summary>The page listing the releases, printed when they cannot be read.</summary>
    public const string ReleasesPage = $"https://github.com/{ReleasesRepository}/releases";

    /// <summary>The GitHub REST endpoint listing the releases (newest first, drafts only for writers).</summary>
    public const string ApiUrl = $"https://api.github.com/repos/{ReleasesRepository}/releases?per_page=100";

    /// <summary>How long the request may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The published releases (not drafts) whose tag is <c>v&lt;SemVer&gt;</c>, pre-releases included, newest version first.
    /// GitHub unreachable (exit 6) or answering with an error (exit 1): the message asks for an exact <c>--to &lt;version&gt;</c>.
    /// </summary>
    public static async Task<IReadOnlyList<ServerRelease>> ListAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("xrmd-cli", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        string body;
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new CliException(ExitCodes.Unexpected,
                    $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for the releases of {ReleasesRepository}. {AskExact}");
        }
        catch (HttpRequestException ex)
        {
            throw new CliException(ExitCodes.Unreachable, $"Cannot reach GitHub to read the releases of {ReleasesRepository} ({ex.Message}). {AskExact}", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CliException(ExitCodes.Unreachable,
                $"GitHub did not answer within {Timeout.TotalSeconds:0} seconds for the releases of {ReleasesRepository}. {AskExact}", ex);
        }
        return Parse(body);
    }

    /// <summary>The newest release (<see cref="ListAsync"/>'s first); exit 4 when there is none.</summary>
    public static async Task<ServerRelease> LatestAsync(HttpClient http, CancellationToken cancellationToken) =>
        (await ListAsync(http, cancellationToken)).FirstOrDefault()
        ?? throw new CliException(ExitCodes.NotFound, $"{ReleasesRepository} has no published release. {AskExact}");

    private const string AskExact = "Pass an exact version instead: `xrmd server upgrade --to <version>` (versions: " + ReleasesPage + ").";

    internal static IReadOnlyList<ServerRelease> Parse(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new CliException(ExitCodes.Unexpected, $"GitHub returned an unreadable list of releases for {ReleasesRepository}. {AskExact}", ex);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new CliException(ExitCodes.Unexpected, $"GitHub returned an unreadable list of releases for {ReleasesRepository}. {AskExact}");
            var releases = new List<ServerRelease>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                if (!item.TryGetProperty("tag_name", out var tagElement) || tagElement.GetString() is not { } tag) continue;
                if (!tag.StartsWith('v') || !ServerImage.IsSemVer(tag[1..])) continue; // not a server release tag
                DateTimeOffset? published = item.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                    && p.TryGetDateTimeOffset(out var at) ? at : null;
                var prerelease = item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
                releases.Add(new ServerRelease(tag[1..], tag, published, prerelease));
            }
            releases.Sort((a, b) => ServerImage.CompareSemVer(b.Version, a.Version));
            return releases;
        }
    }
}
