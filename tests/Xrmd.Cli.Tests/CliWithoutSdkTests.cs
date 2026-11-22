using System.Diagnostics;
using FluentAssertions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Cli.Plugins;
using Xrmd.TestSupport;

namespace Xrmd.Cli.Tests;

/// <summary>
/// Plan 12, B5: the published single-file <c>xrmd</c> (no Microsoft SDK in it) runs the commands that do not use the SDK
/// without it, answers the others with the missing-SDK message, and loads the SDK from the SDK folder once it is installed.
/// </summary>
public sealed class CliWithoutSdkTests : IDisposable
{
    private const string SigningKey = "test-signing-key-0123456789-abcdefghijklmnop";

    private readonly string _root = PublishedApps.NewFolder("cli-run");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task Cli_commands_without_sdk()
    {
        var sdk = Path.Combine(_root, "no-sdk");
        var missing = SdkPackage.MissingMessage(sdk);

        // server init: no SDK needed.
        var init = await RunAsync(sdk, "server", "init", "--accept-sql-server-eula", "--dir", Path.Combine(_root, "install"), "--org", "acme");
        init.Code.Should().Be(0, init.Output);
        init.Output.Should().NotContain(missing);

        // auth login and status: no SDK needed (the profile's server, on the discard port, is unreachable: exit 6).
        var login = await RunAsync(sdk, "auth", "login", "--url", "http://127.0.0.1:9/", "--mode", "dev", "--object-id", Guid.NewGuid().ToString());
        login.Code.Should().Be(0, login.Output);
        var status = await RunAsync(sdk, "auth", "status");
        status.Output.Should().NotContain(missing).And.NotContain("Unexpected error").And.NotContain("   at ");
        status.Code.Should().Be(ExitCodes.Unreachable, status.Output);

        // sdk status: reports the missing SDK by itself.
        var sdkStatus = await RunAsync(sdk, "sdk", "status");
        sdkStatus.Output.Should().Contain(missing).And.NotContain("   at ");

        // data import uses the SDK's metadata types: the missing-SDK message and exit 1, not a crash.
        var import = await RunAsync(sdk, "data", "import", "pb_pipe", "--file", Rows(), "--format", "csv");
        import.Code.Should().Be(ExitCodes.Unexpected, import.Output);
        import.Output.Trim().Should().Be(missing);

        // Once installed in the SDK folder, the same command gets past the SDK (to the unreachable server).
        var installed = await PublishedApps.InstalledSdkAsync();
        var withSdk = await RunAsync(installed, "data", "import", "pb_pipe", "--file", Rows(), "--format", "csv");
        withSdk.Output.Should().NotContain("The Microsoft SDK is not installed");
        withSdk.Code.Should().Be(ExitCodes.Unreachable, withSdk.Output);
    }

    [Fact]
    public async Task Table_describe_and_data_export_without_sdk_print_the_missing_message()
    {
        // The column types are read with the SDK's metadata deserializer inside a catch-all: a missing SDK must not be
        // swallowed there (silent fallback to the raw AttributeType), but reach the user as the missing-SDK message.
        var sdk = Path.Combine(_root, "no-sdk");
        var missing = SdkPackage.MissingMessage(sdk);
        using var server = new MetadataServer();

        var login = await RunAsync(sdk, "auth", "login", "--url", server.Url, "--mode", "dev", "--object-id", Guid.NewGuid().ToString());
        login.Code.Should().Be(0, login.Output);

        var describe = await RunAsync(sdk, "table", "describe", "pb_pipe");
        describe.Output.Trim().Should().Be(missing);
        describe.Code.Should().Be(ExitCodes.Unexpected, describe.Output);
        server.Paths.Should().Contain(p => p.EndsWith("/Attributes", StringComparison.Ordinal), "the columns were read before the SDK was needed");

        var export = await RunAsync(sdk, "data", "export", "pb_pipe", "--format", "csv", "--out", Path.Combine(_root, "out.csv"));
        export.Output.Trim().Should().Be(missing);
        export.Code.Should().Be(ExitCodes.Unexpected, export.Output);
    }

    [Fact]
    public async Task Plugin_register_finds_the_sdk_in_the_sdk_folder()
    {
        var sdk = await PublishedApps.InstalledSdkAsync();
        var app = PublishedApps.NewFolder("cli-app"); // a CLI folder without the SDK, as published
        var plugin = Path.Combine(AppContext.BaseDirectory, "Xrmd.TestPlugins.dll");

        AssemblyInspector.SdkCandidates(app, sdk).Should().Equal(Path.Combine(sdk, "Microsoft.Xrm.Sdk.dll"));
        AssemblyInspector.SdkCandidates(app, Path.Combine(_root, "no-sdk")).Should().BeEmpty();

        var info = AssemblyInspector.Inspect(plugin, withSdk: true, app, sdk);
        info.PluginTypes.Should().Contain(["Xrmd.TestPlugins.DerivedPlugin", "Xrmd.TestPlugins.SetAttributePlugin"]);
        info.PluginTypes.Should().NotContain(["Xrmd.TestPlugins.PluginBase", "Xrmd.TestPlugins.NotAPlugin"]);
    }

    [Fact]
    public void A_missing_sdk_assembly_is_recognised_through_wrappers()
    {
        var notFound = new FileNotFoundException("Could not load file or assembly.",
            "Microsoft.Xrm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");

        SdkAssemblyResolver.MissingAssembly(notFound).Should().Be("Microsoft.Xrm.Sdk");
        SdkAssemblyResolver.MissingAssembly(new TypeInitializationException("X", notFound)).Should().Be("Microsoft.Xrm.Sdk");
        SdkAssemblyResolver.MissingAssembly(new System.Reflection.TargetInvocationException(new AggregateException(notFound)))
            .Should().Be("Microsoft.Xrm.Sdk");
        SdkAssemblyResolver.MissingAssembly(new FileLoadException("x", "Microsoft.Crm.Sdk.Proxy, Version=9.0.0.0")).Should().Be("Microsoft.Crm.Sdk.Proxy");
        SdkAssemblyResolver.MissingAssembly(new FileNotFoundException("x", "/opt/sdk/Microsoft.PowerPlatform.Dataverse.Client.dll"))
            .Should().Be("Microsoft.PowerPlatform.Dataverse.Client");
        SdkAssemblyResolver.MissingAssembly(new FileNotFoundException("x", "Newtonsoft.Json, Version=13.0.0.0")).Should().BeNull();
        SdkAssemblyResolver.MissingAssembly(new FileNotFoundException("x", "/data/rows.csv")).Should().BeNull();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>A Web API that knows one table, <c>pb_pipe</c>, with one text column; any other GET is an empty collection.</summary>
    private sealed class MetadataServer : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly Task _loop;

        public MetadataServer()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string Url { get; }

        public System.Collections.Concurrent.ConcurrentQueue<string> Paths { get; } = new();

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception ex) when (ex is System.Net.HttpListenerException or ObjectDisposedException) { return; }
                var path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath);
                Paths.Enqueue(path);
                var body = path.EndsWith("/Attributes", StringComparison.Ordinal)
                    ? """{"value":[{"@odata.type":"#Microsoft.Dynamics.CRM.StringAttributeMetadata","LogicalName":"pb_name","AttributeType":"String","MaxLength":100,"IsValidForCreate":true}]}"""
                    : path.Contains("EntityDefinitions(", StringComparison.Ordinal)
                        ? """{"LogicalName":"pb_pipe","EntitySetName":"pb_pipes","PrimaryIdAttribute":"pb_pipeid","PrimaryNameAttribute":"pb_name","OwnershipType":"UserOwned"}"""
                        : """{"value":[]}""";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        }
    }

    private string Rows()
    {
        var path = Path.Combine(_root, "rows.csv");
        File.WriteAllText(path, "pb_name\nx\n");
        return path;
    }

    /// <summary>Runs the published single-file <c>xrmd</c> with its own config dir and home, no terminal, and the given SDK folder.</summary>
    private async Task<(int Code, string Output)> RunAsync(string sdkFolder, params string[] args)
    {
        var psi = new ProcessStartInfo(await PublishedApps.SingleFileCliAsync())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["HOME"] = _root;
        psi.Environment["USERPROFILE"] = _root;
        psi.Environment["XRMD_CONFIG_DIR"] = Path.Combine(_root, "config");
        psi.Environment[SdkFolder.DirectoryVariable] = sdkFolder;
        psi.Environment["XRMD_DEV_SIGNING_KEY"] = SigningKey;

        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(cts.Token);
        return (process.ExitCode, await stdout + await stderr);
    }
}
