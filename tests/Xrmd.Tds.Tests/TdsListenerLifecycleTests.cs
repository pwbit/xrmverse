using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xrmd.Tds;

namespace Xrmd.Tds.Tests;

/// <summary>
/// The listener's stop and dispose run in any order and any number of times: the host can dispose its services before a
/// second stop (e.g. a host that stopped itself, then disposed by <c>WebApplicationFactory</c>), with the endpoint on or off.
/// </summary>
public sealed class TdsListenerLifecycleTests
{
    private static TdsListener Listener(int? port) => new(
        Options.Create(new TdsOptions { Port = port }),
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        new DevelopmentEnvironment(),
        NullLogger<TdsListener>.Instance);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task Stop_after_dispose_does_not_throw(int? port)
    {
        var listener = Listener(port);
        await listener.StartAsync(CancellationToken.None);
        (listener.BoundPort is not null).Should().Be(port is not null);

        await listener.StopAsync(CancellationToken.None);
        listener.Dispose();
        await FluentActions.Awaiting(() => listener.StopAsync(CancellationToken.None)).Should().NotThrowAsync();
        listener.Dispose();
        listener.BoundPort.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task Dispose_before_any_stop_does_not_throw(int? port)
    {
        var listener = Listener(port);
        await listener.StartAsync(CancellationToken.None);

        listener.Dispose();
        await FluentActions.Awaiting(() => listener.StopAsync(CancellationToken.None)).Should().NotThrowAsync();
        await FluentActions.Awaiting(() => listener.StopAsync(CancellationToken.None)).Should().NotThrowAsync();
        listener.BoundPort.Should().BeNull();
    }

    /// <summary>A listener whose certificate files do not exist yet (plan 3.3, Task 5: tds-certs has not copied them).</summary>
    private static TdsListener WaitingListener(string dir) => new(
        Options.Create(new TdsOptions { Port = 0, CertificatePath = Path.Combine(dir, "tds.crt"), PrivateKeyPath = Path.Combine(dir, "tds.key") }),
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        new DevelopmentEnvironment { EnvironmentName = Environments.Production },
        NullLogger<TdsListener>.Instance);

    [Fact]
    public async Task A_missing_certificate_keeps_the_endpoint_closed_without_failing_the_start()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xrmd-tds-lifecycle", Guid.NewGuid().ToString("N"));
        var listener = WaitingListener(dir);

        await FluentActions.Awaiting(() => listener.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
        listener.BoundPort.Should().BeNull();

        await listener.StopAsync(CancellationToken.None);
        listener.Dispose();
        await FluentActions.Awaiting(() => listener.StopAsync(CancellationToken.None)).Should().NotThrowAsync();
        listener.Dispose();
        listener.BoundPort.Should().BeNull();
    }

    [Fact]
    public async Task Dispose_while_waiting_for_the_certificate_does_not_throw()
    {
        var listener = WaitingListener(Path.Combine(Path.GetTempPath(), "xrmd-tds-lifecycle", Guid.NewGuid().ToString("N")));
        await listener.StartAsync(CancellationToken.None);

        listener.Dispose();
        await FluentActions.Awaiting(() => listener.StopAsync(CancellationToken.None)).Should().NotThrowAsync();
        listener.Dispose();
        listener.BoundPort.Should().BeNull();
    }

    [Fact]
    public async Task A_key_path_without_a_certificate_path_still_fails_the_start()
    {
        var listener = new TdsListener(
            Options.Create(new TdsOptions { Port = 0, PrivateKeyPath = "/nonexistent/tds.key" }),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new DevelopmentEnvironment(),
            NullLogger<TdsListener>.Instance);

        await FluentActions.Awaiting(() => listener.StartAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
        listener.Dispose();
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "xrmd-tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
