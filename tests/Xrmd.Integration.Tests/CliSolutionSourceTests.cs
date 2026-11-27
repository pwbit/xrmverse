using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Cli;
using Xrmd.Metadata;
using Xrmd.TestSupport;

namespace Xrmd.Integration.Tests;

/// <summary>The solution source tests run on a database of their own: the XrmSharedTest solution they import is never removed.</summary>
[CollectionDefinition("solution-source")]
public class SolutionSourceCollection : ICollectionFixture<XrmdFixture>;

/// <summary>
/// <c>xrmd solution sync</c> and <c>diff</c> against the in-memory host: a source folder pulled from the organization, a
/// change made by hand in the organization, its detection and its pull, and the import of what <c>pack</c> builds from the folder.
/// </summary>
[Collection("solution-source")]
public sealed class CliSolutionSourceTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Solution = "XrmSharedTest";
    private const string Api = "/api/data/v9.2/";
    private const string Workflow = "Buttami-BF5DBD53-E0E9-F111-84EA-7CED8D2D53E4.xaml";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xrmd-cli-source-int", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    private string ConfigDir => Path.Combine(_root, "config");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        (await RunAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", fx.AdminObjectId.ToString()))
            .Should().Be(0, _err.ToString());
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
        return Task.CompletedTask;
    }

    private Task<int> RunAsync(params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var env = new CliEnvironment(_out, _err, new StringReader(""), false, fx.App.Server.CreateHandler(), ConfigDir, new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
        };
        return CliApp.RunAsync(args, env);
    }

    private string Output => _out + _err.ToString();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(request);
    }

    [SourceLayoutFact]
    public async Task Hand_change_is_detected_and_pulled()
    {
        var zip = Path.Combine(_root, Solution + ".zip");
        await File.WriteAllBytesAsync(zip, SourceLayoutFixtures.Zip(Solution));
        // The solution's own publisher has the prefix 'new'; its table carries 'op365', which a publisher must own here.
        (await RunAsync("publisher", "create", "--unique-name", "op365tables", "--prefix", "op365", "--option-value-prefix", "36500")).Should().Be(0, Output);
        (await RunAsync("solution", "import", zip)).Should().Be(0, Output);

        // First pull into a new folder, then nothing differs.
        var folder = Path.Combine(_root, "repo");
        (await RunAsync("solution", "sync", Solution, "--folder", folder)).Should().Be(0, Output);
        File.Exists(Path.Combine(folder, "Other", "Solution.xml")).Should().BeTrue();
        (await RunAsync("solution", "diff", Solution, "--folder", folder)).Should().Be(0, Output);
        // The global choice and the classic workflow of the zip come back from the organization in their own files.
        File.Exists(Path.Combine(folder, "OptionSets", "new_buttamiscelte.xml")).Should().BeTrue();
        File.Exists(Path.Combine(folder, "Workflows", Workflow)).Should().BeTrue();
        File.Exists(Path.Combine(folder, "Workflows", Workflow + ".data.xml")).Should().BeTrue();

        // A form changed by hand in the organization.
        var form = Directory.EnumerateFiles(Path.Combine(folder, "Entities"), "*.xml", SearchOption.AllDirectories)
            .First(f => f.Contains(Path.DirectorySeparatorChar + "main" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        var formPath = Path.GetRelativePath(folder, form).Replace(Path.DirectorySeparatorChar, '/');
        var formId = Guid.Parse(Path.GetFileNameWithoutExtension(form));
        using (var read = await SendAsync(HttpMethod.Get, $"systemforms({formId})?$select=formxml"))
        {
            var body = await read.Content.ReadAsStringAsync();
            read.StatusCode.Should().Be(HttpStatusCode.OK, body);
            var formXml = JsonDocument.Parse(body).RootElement.GetProperty("formxml").GetString()!;
            var label = Regex.Match(formXml, "<label description=\"([^\"]+)\"");
            label.Success.Should().BeTrue(formXml);
            var edited = formXml.Remove(label.Groups[1].Index, label.Groups[1].Length).Insert(label.Groups[1].Index, label.Groups[1].Value + " (by hand)");
            using var patch = await SendAsync(HttpMethod.Patch, $"systemforms({formId})", JsonSerializer.Serialize(new { formxml = edited }));
            patch.StatusCode.Should().Be(HttpStatusCode.NoContent, await patch.Content.ReadAsStringAsync());
        }

        (await RunAsync("solution", "diff", Solution, "--folder", folder)).Should().Be(ExitCodes.Differences, Output);
        var lines = _out.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle(l => Regex.IsMatch(l, "^changed +form +.+ +" + Regex.Escape(formPath) + "$"), _out.ToString());
        lines.Count(l => Regex.IsMatch(l, "^(changed|missing|new) ")).Should().Be(1, _out.ToString());

        (await RunAsync("solution", "sync", Solution, "--folder", folder)).Should().Be(0, Output);
        _out.ToString().Should().Contain("1 written");
        File.ReadAllText(form).Should().Contain(" (by hand)");
        (await RunAsync("solution", "diff", Solution, "--folder", folder)).Should().Be(0, Output);

        // The import accepts what pack builds from the folder.
        var packed = Path.Combine(_root, "packed.zip");
        (await RunAsync("solution", "pack", folder, "--out", packed)).Should().Be(0, Output);
        (await RunAsync("solution", "import", packed)).Should().Be(0, Output);
        (await RunAsync("solution", "diff", Solution, "--folder", folder)).Should().Be(0, Output);
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No process may run in this test.");
    }
}
