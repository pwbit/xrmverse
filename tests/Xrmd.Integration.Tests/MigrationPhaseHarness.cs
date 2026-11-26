using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.Admin.Migration.Mapping;
using Xrmd.Core;
using Xrmd.Migration.Packaging;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Runs import phases straight on an organization (plan M4a tests), as the migration job does: a run row, the package, the
/// administrator as the caller, each phase inside the migration scope. The tests read the result with <see cref="ScalarAsync"/>.
/// </summary>
internal sealed class MigrationPhaseHarness(WebApplicationFactory<Program> app, XrmdFixture fx) : IAsyncDisposable
{
    public WebApplicationFactory<Program> App { get; } = app;
    public Guid RunId { get; } = Guid.NewGuid();
    public string ConnectionString => App.Services.GetRequiredService<IConfiguration>().GetConnectionString("Org")!;
    public MigrationLedger Ledger => new(App.Services.GetRequiredService<IDbConnectionFactory>(), RunId);

    /// <summary>A harness on a database of its own.</summary>
    public static MigrationPhaseHarness Start(XrmdFixture fx) => new(fx.WithOwnDatabase(_ => { }), fx);

    /// <summary>The administrator's user id (the run's owner).</summary>
    public async Task<Guid> AdminIdAsync() =>
        (Guid)(await ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid", ("@oid", fx.AdminObjectId)))!;

    /// <summary>Runs <paramref name="phases"/> in order on <paramref name="packageFile"/>; the first call creates the run row.</summary>
    public async Task RunAsync(string packageFile, IReadOnlyDictionary<string, string>? options, params IMigrationPhase[] phases)
    {
        var factory = App.Services.GetRequiredService<IDbConnectionFactory>();
        var store = new MigrationRunStore(factory);
        var admin = await AdminIdAsync();
        if (await store.GetRunAsync(RunId, default) is null)
            await store.CreateRunAsync(new MigrationRunRow(RunId, null, packageFile, MigrationRunStates.Running, 0, "{}", null, null, admin,
                DateTime.UtcNow, DateTime.UtcNow, null, null), default);
        using var scope = App.Services.CreateScope();
        Caller caller;
        await using (var conn = await factory.OpenAsync(default))
            caller = await scope.ServiceProvider.GetRequiredService<CoreOperations>().ResolveUserAsync(admin, new DataSession(conn, null), default);
        using var package = PackageReader.Open(packageFile, MigrationTestPackage.Passphrase);
        options ??= new Dictionary<string, string>();
        await MigrationMaps.AttachAsync(package, options, scope.ServiceProvider, default);
        foreach (var phase in phases)
            using (MigrationScope.Enter())
                await phase.RunAsync(new MigrationPhaseContext(RunId, package, caller, store, options, new Progress<string>(), Ledger, scope.ServiceProvider), default);
    }

    public Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] parameters) =>
        TestSql.ScalarAsync(ConnectionString, sql, parameters);

    /// <summary>Sends an Admin API or Web API request as the administrator.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, path)
        {
            Content = json is null ? null : new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return App.CreateClient().SendAsync(req);
    }

    public async Task<List<MigrationIssue>> IssuesAsync() => await Ledger.IssuesAsync(1000, default);

    public ValueTask DisposeAsync() => App.DisposeAsync();
}
