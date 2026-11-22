using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Runtime.Loader;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Issue #51: an early-bound plug-in assembly (<c>[assembly: ProxyTypesAssembly]</c>) is loaded once per process, so a
/// <see cref="ServiceClient"/> created afterwards in the same process does not see two assemblies defining the same proxy
/// types. Registration, execution and the later client all go through one real xrmd host.
/// </summary>
[Collection("serviceclient")]
[Trait("Category", "Acceptance")]
public sealed class EarlyBoundAssemblyTests(ServiceClientFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private const string EarlyBoundName = "Xrmd.TestPlugins.EarlyBound";
    private const string PluginTypeName = "Xrmd.TestPlugins.EarlyBound.EarlyBoundLogPlugin";

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await ClearAsync();
    }

    public Task DisposeAsync() => ClearAsync();

    private async Task ClearAsync()
    {
        await PluginTestHelper.ClearAsync(fx.ConnectionString);
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, $"DELETE FROM data.{TestEntities.Pipe}; DELETE FROM data.{TestEntities.PipeLog};");
            await cmd.ExecuteNonQueryAsync();
        }

        fx.App.Services.GetRequiredService<Xrmd.Pipeline.Registration.StepRegistry>().Invalidate();
    }

    private ServiceClient Connect() =>
        new(new Uri(ServiceClientFixture.BaseUrl), _ => Task.FromResult(DevTokenFactory.Create(fx.AdminObjectId, ServiceClientFixture.BaseUrl)),
            useUniqueInstance: true, new TestOutputLogger(output));

    [Fact]
    public async Task ServiceClient_works_after_an_early_bound_plugin_ran()
    {
        var svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        var before = LoadedCopies().Count; // an earlier test of the process may have left copies of other contents
        await RegisterAsync(svc, PluginTestHelper.EarlyBoundPluginBytes);
        LogCopies("registered");
        var registered = LoadedCopies().Count;
        registered.Should().BeLessThanOrEqualTo(before + 1, "the registration check loads at most the one copy of this content");

        (await RunAndReadLogAsync(svc)).Should().Be("early|p1|1");
        LogCopies("ran");
        LoadedCopies().Should().HaveCount(registered, "the execution runs the copy the registration check loaded");

        AssertNewServiceClientWorks();
    }

    /// <summary>
    /// An update of an early-bound assembly: the SDK keeps the old copy alive, so the process lists both copies afterwards. The
    /// server runs the new content in synchronous and asynchronous steps (the async queue serializes the context with the SDK's
    /// known types). A ServiceClient of this process that had already scanned the proxy types before the update keeps working;
    /// one whose first scan came after the update would fail ("A proxy type with the name … has been defined by another
    /// assembly"), which does not concern xrmd (it hosts no ServiceClient), so this test scans first.
    /// </summary>
    [Fact]
    public async Task ServiceClient_works_after_early_bound_assembly_is_updated()
    {
        using (var before = Connect())
            ((WhoAmIResponse)before.Execute(new WhoAmIRequest())).UserId.Should().NotBeEmpty();

        var svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        var assemblyId = await RegisterAsync(svc, PluginTestHelper.EarlyBoundPluginBytes, withAsyncStep: true);
        (await RunAndReadLogsAsync(svc, expected: 2)).Should().AllBe("early|p1|1");
        LogCopies("ran v1");

        await svc.UpdateAsync(new Entity("pluginassembly", assemblyId) { ["content"] = Convert.ToBase64String(SecondVersion()) });
        LogCopies("updated");

        (await RunAndReadLogsAsync(svc, expected: 2)).Should().AllBe("later|p2|1");
        LogCopies("ran v2");

        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        ((WhoAmIResponse)client.Execute(new WhoAmIRequest())).UserId.Should().NotBeEmpty();
        client.RetrieveMultiple(new QueryExpression(TestEntities.PipeLog) { ColumnSet = new ColumnSet("pb_name") }).Entities
            .Should().HaveCount(4);
    }

    /// <summary>
    /// A new client in this process, after the plug-in ran: its handshake, a WhoAmI and a read of pb_pipelog (which the
    /// client materializes with the early-bound proxy type it finds loaded) succeed.
    /// </summary>
    private void AssertNewServiceClientWorks()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        ((WhoAmIResponse)client.Execute(new WhoAmIRequest())).UserId.Should().NotBeEmpty();
        client.RetrieveMultiple(new QueryExpression(TestEntities.PipeLog) { ColumnSet = new ColumnSet("pb_name") }).Entities
            .Should().NotBeEmpty();
        LogCopies("new client");
    }

    private int _runs;

    /// <summary>Creates a pb_pipe named p{n} and returns the name of the pb_pipelog the plug-in wrote for it.</summary>
    private async Task<string> RunAndReadLogAsync(OrganizationService svc) => (await RunAndReadLogsAsync(svc, expected: 1)).Single();

    /// <summary>
    /// Creates a pb_pipe named p{n} and returns the names of the <paramref name="expected"/> pb_pipelog rows written for it
    /// (waiting up to 60 s for the asynchronous ones; a failed asynchronous job fails with its message).
    /// </summary>
    private async Task<List<string>> RunAndReadLogsAsync(OrganizationService svc, int expected)
    {
        var name = "p" + ++_runs;
        var pipeId = await svc.CreateAsync(new Entity(TestEntities.Pipe) { ["pb_name"] = name });
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var names = new List<string>();
            await using (var cmd = TestSql.Command(conn, $"SELECT pb_name FROM data.{TestEntities.PipeLog} WHERE pb_name LIKE @n"))
            {
                cmd.Parameters.AddWithValue("@n", $"%|{name}|%");
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync()) names.Add(rd.GetString(0));
            }

            if (names.Count >= expected) return names;
            // Only this run's job: the asynchronous step's job is regarding the pb_pipe just created.
            await using (var cmd = TestSql.Command(conn, "SELECT TOP 1 message FROM data.asyncoperation WHERE regardingobjectid = @id AND statuscode IN (10, 31) AND message IS NOT NULL"))
            {
                cmd.Parameters.AddWithValue("@id", pipeId);
                if (await cmd.ExecuteScalarAsync() is string failure) throw new Xunit.Sdk.XunitException("Async job failed: " + failure);
            }

            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"Only {names.Count} of {expected} pb_pipelog rows for {name}.");
            await Task.Delay(200);
        }
    }

    private static async Task<Guid> RegisterAsync(OrganizationService svc, byte[] content, bool withAsyncStep = false)
    {
        var assemblyId = await PluginTestHelper.RegisterAssemblyAsync(svc, content, EarlyBoundName);
        var typeId = await PluginTestHelper.RegisterTypeAsync(svc, assemblyId, PluginTypeName);
        await PluginTestHelper.RegisterStepAsync(svc, typeId, "Create", TestEntities.Pipe, 40);
        if (withAsyncStep) await PluginTestHelper.RegisterStepAsync(svc, typeId, "Create", TestEntities.Pipe, 40, mode: 1, rank: 2);
        return assemblyId;
    }

    /// <summary>
    /// A new build of the early-bound assembly (same identity, other code): the user string <c>early|</c> of the name format
    /// becomes <c>later|</c> (same length, so the metadata stays valid; .NET does not check the strong-name signature).
    /// </summary>
    private static byte[] SecondVersion()
    {
        var bytes = (byte[])PluginTestHelper.EarlyBoundPluginBytes.Clone();
        var at = bytes.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes("early|"));
        at.Should().BeGreaterThan(0, "the early-bound plug-in has the user string 'early|'");
        System.Text.Encoding.Unicode.GetBytes("later|").CopyTo(bytes, at);
        return bytes;
    }

    private static List<System.Reflection.Assembly> LoadedCopies() =>
        AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == EarlyBoundName).ToList();

    private void LogCopies(string step)
    {
        var copies = LoadedCopies();
        output.WriteLine($"[{step}] {copies.Count} cop{(copies.Count == 1 ? "y" : "ies")} of {EarlyBoundName}:");
        foreach (var a in copies)
            output.WriteLine($"  {AssemblyLoadContext.GetLoadContext(a)?.Name} (collectible={a.IsCollectible}, mvid={a.ManifestModule.ModuleVersionId})");
    }
}
