using FluentAssertions;
using Xrmd.Cli.Server;
using Xrmd.WebApi;

namespace Xrmd.Integration.Tests;

/// <summary>The generated proxy never refuses a body the server accepts (this project references both the CLI and the server).</summary>
public class CaddyBodyLimitTests
{
    [Fact]
    public void Caddy_request_body_limit_is_the_solution_import_limit() =>
        CaddyfileRenderer.RequestBodyMaxBytes.Should().Be(SolutionFileSizeLimit.MaxBytes);
}
