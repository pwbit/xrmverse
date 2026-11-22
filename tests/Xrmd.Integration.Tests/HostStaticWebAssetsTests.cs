using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Xrmd.Host;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The host in the Development environment (the acceptance fixture and the E2E run) reads the static web assets manifest of its
/// build. The web admin build (<c>ui/dist</c>) is linked into <c>wwwroot</c> for the output folder only: it must not make the
/// manifest name a <c>wwwroot</c> content root that does not exist in the project folder, which .NET 10 refuses at startup.
/// </summary>
public class HostStaticWebAssetsTests
{
    [Fact]
    public void Development_host_builder_starts_when_the_web_admin_build_is_linked_into_wwwroot()
    {
        var act = () => WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });

        act.Should().NotThrow();
    }
}
