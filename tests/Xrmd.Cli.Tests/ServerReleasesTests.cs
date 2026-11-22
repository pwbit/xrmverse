using System.Net;
using FluentAssertions;
using Xrmd.Cli.Server;
using Xunit;

namespace Xrmd.Cli.Tests;

/// <summary>The releases come from the public source repository and, during the move, from the former assets-only one.</summary>
public sealed class ServerReleasesTests
{
    private static FakeHandler Handler(HttpResponseMessage source, Func<HttpResponseMessage> legacy) => new(request =>
        request.RequestUri!.AbsolutePath switch
        {
            "/repos/pwbit/xrmverse/releases" => source,
            "/repos/pwbit/xrmverse-releases/releases" => legacy(),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

    [Fact]
    public async Task Both_repositories_are_merged_newest_first_and_a_tag_in_both_counts_once()
    {
        using var http = new HttpClient(Handler(
            FakeHandler.Json(HttpStatusCode.OK, """
                [{"tag_name":"v0.1.11","draft":false,"prerelease":false,"published_at":"2026-11-30T10:00:00Z"},
                 {"tag_name":"v0.1.10","draft":false,"prerelease":false,"published_at":"2026-11-22T11:31:00Z"}]
                """),
            () => FakeHandler.Json(HttpStatusCode.OK, """
                [{"tag_name":"v0.1.10","draft":false,"prerelease":false,"published_at":"2026-11-22T11:00:00Z"},
                 {"tag_name":"v0.1.0-preview.9","draft":false,"prerelease":true,"published_at":"2026-11-21T19:00:00Z"}]
                """)));

        var releases = await ServerReleases.ListAsync(http, default);

        releases.Select(r => r.Version).Should().Equal("0.1.11", "0.1.10", "0.1.0-preview.9");
        releases[1].PublishedAt.Should().Be(DateTimeOffset.Parse("2026-11-22T11:31:00Z"), "the source repository wins for a tag in both");
    }

    [Fact]
    public async Task An_error_from_the_former_repository_is_ignored()
    {
        using var http = new HttpClient(Handler(
            FakeHandler.Json(HttpStatusCode.OK, """[{"tag_name":"v0.1.11","draft":false,"prerelease":false}]"""),
            () => new HttpResponseMessage(HttpStatusCode.NotFound)));

        (await ServerReleases.LatestAsync(http, default)).Version.Should().Be("0.1.11");
    }

    [Fact]
    public async Task An_error_from_the_source_repository_fails_and_asks_for_an_exact_version()
    {
        using var http = new HttpClient(Handler(new HttpResponseMessage(HttpStatusCode.Forbidden), () => FakeHandler.Json(HttpStatusCode.OK, "[]")));

        var act = () => ServerReleases.ListAsync(http, default);

        (await act.Should().ThrowAsync<CliException>()).Which.Message.Should()
            .Contain("403").And.Contain("pwbit/xrmverse").And.Contain("--to <version>");
    }
}
