using System.Data.Common;
using Xrmd.Abstractions.Storage;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Sandbox;
using Xrmd.WebApi;

namespace Xrmd.Integration.Tests;

/// <summary>
/// A SQL deadlock victim (1205) and a SQL command timeout (-2) reach the caller as the Dataverse SQL error codes
/// <c>0x80044150</c> / <c>0x80044151</c> (HTTP 500) at every edge, with no retry by the server; the exceptions are real ones.
/// </summary>
[Collection("xrmd")]
public sealed class SqlFaultTests(XrmdFixture fx)
{
    private const int DeadlockCode = unchecked((int)0x80044150);
    private const int TimeoutCode = unchecked((int)0x80044151);
    private const string Log = TestEntities.PipeLog;
    private const string Pipe = TestEntities.Pipe;

    /// <summary>
    /// Provokes a real deadlock between two connections of the run's provider (SQL Server error 1205, PostgreSQL 40P01; plan
    /// 2.7a, Task 6) and returns the exception of the victim.
    /// </summary>
    internal static async Task<DbException> DeadlockAsync(string connectionString)
    {
        var table = (TestProvider.ProviderOf(connectionString) == StorageProvider.PostgreSql ? "public" : "dbo") + ".sqlfault_" + Guid.NewGuid().ToString("N");
        await using var a = await TestProvider.OpenAsync(connectionString);
        await using var b = await TestProvider.OpenAsync(connectionString);
        await Exec(a, $"CREATE TABLE {table} (id int PRIMARY KEY, v int)");
        await Exec(a, $"INSERT INTO {table} VALUES (1, 0), (2, 0)");
        await using var ta = await a.BeginTransactionAsync();
        await using var tb = await b.BeginTransactionAsync();
        try
        {
            await Exec(a, $"UPDATE {table} SET v = 1 WHERE id = 1", ta);
            await Exec(b, $"UPDATE {table} SET v = 1 WHERE id = 2", tb);
            var first = Try(a, $"UPDATE {table} SET v = 2 WHERE id = 2", ta);
            var second = Try(b, $"UPDATE {table} SET v = 2 WHERE id = 1", tb);
            var errors = (await Task.WhenAll(first, second)).Where(e => e is not null).ToList();
            errors.Should().ContainSingle("the engine picks exactly one deadlock victim");
            SqlDialects.Classify(errors[0]!).Should().Be(DbErrorKind.Deadlock);
            return errors[0]!;
        }
        finally
        {
            // The winner's transaction is still open and holds locks; release them so the DROP does not block.
            await RollbackAsync(ta);
            await RollbackAsync(tb);
            await Exec(a, $"DROP TABLE IF EXISTS {table}");
        }
    }

    private static async Task RollbackAsync(DbTransaction tx)
    {
        try
        {
            await tx.RollbackAsync();
        }
        catch (InvalidOperationException)
        {
            // SQL Server already rolled the victim's transaction back.
        }
    }

    private static async Task<DbException?> Try(DbConnection conn, string sql, DbTransaction tx)
    {
        try
        {
            await Exec(conn, sql, tx);
            return null;
        }
        catch (DbException ex)
        {
            return ex;
        }
    }

    private static async Task Exec(DbConnection conn, string sql, DbTransaction? tx = null)
    {
        await using var cmd = TestSql.Command(conn, tx, sql);
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A real command timeout of the run's provider (SQL Server -2; on PostgreSQL the client cancels the statement).</summary>
    private async Task<DbException> TimeoutAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, TestProvider.IsPostgres ? "SELECT pg_sleep(5)" : "WAITFOR DELAY '00:00:05'");
        cmd.CommandTimeout = 1;
        var act = () => cmd.ExecuteNonQueryAsync();
        var ex = (await act.Should().ThrowAsync<DbException>()).Which;
        SqlDialects.Classify(ex).Should().Be(DbErrorKind.Timeout);
        return ex;
    }

    [Fact]
    public async Task Deadlock_is_sql_error_0x80044150()
    {
        var fault = SqlFaults.Translate(await DeadlockAsync(fx.ConnectionString));
        fault!.ErrorCode.Should().Be(DeadlockCode);
        fault.HttpStatus.Should().Be(500);
        fault.Message.Should().Be("The database chose this request as a deadlock victim and rolled it back. Retry the request.");
    }

    [Fact]
    public async Task Timeout_is_sql_timeout_0x80044151()
    {
        var timeout = await TimeoutAsync();
        var fault = SqlFaults.Translate(timeout);
        fault!.ErrorCode.Should().Be(TimeoutCode);
        fault.HttpStatus.Should().Be(500);
        fault.Message.Should().Be("The database did not answer in time and the request was rolled back. Retry the request.");
    }

    [Fact]
    public async Task A_wrapped_sql_exception_is_translated_one_level_down()
    {
        var wrapped = new InvalidOperationException("wrapper", await DeadlockAsync(fx.ConnectionString));
        SqlFaults.Translate(wrapped)!.ErrorCode.Should().Be(DeadlockCode);
    }

    [Fact]
    public void Only_sql_exceptions_are_translated()
    {
        SqlFaults.Translate(new InvalidOperationException("deadlock")).Should().BeNull();
        SqlFaults.Translate(new TimeoutException("timeout")).Should().BeNull();
    }

    [Fact]
    public async Task Web_api_returns_the_code()
    {
        var deadlock = await DeadlockAsync(fx.ConnectionString);
        var timeout = await TimeoutAsync();
        foreach (var (ex, code, text) in new[] { (deadlock, "0x80044150", "deadlock victim"), (timeout, "0x80044151", "did not answer in time") })
        {
            await using var app = await HostAsync(ex);
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/pb_pipelogs")
            {
                Content = new StringContent("""{"pb_name":"x"}""", Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
            using var res = await app.CreateClient().SendAsync(req);
            res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var error = JsonNode.Parse(await res.Content.ReadAsStringAsync())!["error"]!;
            error["code"]!.GetValue<string>().Should().Be(code);
            error["message"]!.GetValue<string>().Should().Contain(text).And.Contain("Retry the request");
        }
    }

    [Fact]
    public async Task Soap_returns_the_code()
    {
        var deadlock = await DeadlockAsync(fx.ConnectionString);
        var timeout = await TimeoutAsync();
        foreach (var (ex, code) in new[] { (deadlock, DeadlockCode), (timeout, TimeoutCode) })
        {
            await using var app = await HostAsync(ex);
            var body = OrganizationSoapEndpointTests.Envelope(OrganizationSoapEndpointTests.Execute(
                new CreateRequest { Target = new Entity(Log) { ["pb_name"] = "x" } }));
            using var req = new HttpRequestMessage(HttpMethod.Post, "XRMServices/2011/Organization.svc/web")
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
            using var res = await app.CreateClient().SendAsync(req);
            res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            XNamespace contracts = "http://schemas.microsoft.com/xrm/2011/Contracts";
            var detail = XDocument.Parse(await res.Content.ReadAsStringAsync()).Descendants(contracts + "OrganizationServiceFault").Single();
            ((int)detail.Element(contracts + "ErrorCode")!).Should().Be(code);
            ((string)detail.Element(contracts + "Message")!).Should().Contain("Retry the request");
        }
    }

    [Fact]
    public async Task Deadlock_in_nested_call_reaches_the_plugin_as_a_fault_with_the_code()
    {
        await using var app = await HostAsync(await DeadlockAsync(fx.ConnectionString));
        var svc = TestServices.NewOrganizationService(app.Services, fx.AdminObjectId);
        // PreValidation runs outside the transaction: the plug-in's catch is visible in the stored record.
        await RegisterTryCreateAsync(svc, stage: 10);

        using var res = await PostPipeAsync(app);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var id = Guid.Parse(res.Headers.GetValues("OData-EntityId").Single().Split('(', ')')[1]);
        (await ReadAsync(app, id)).Should().Be("caught " + DeadlockCode);
    }

    [Fact]
    public async Task Deadlock_in_nested_call_dooms_the_outer_request()
    {
        await using var app = await HostAsync(await DeadlockAsync(fx.ConnectionString));
        var svc = TestServices.NewOrganizationService(app.Services, fx.AdminObjectId);
        await RegisterTryCreateAsync(svc, stage: 20); // inside the transaction: the plug-in swallows the fault, the transaction is doomed

        using var res = await PostPipeAsync(app);

        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var error = JsonNode.Parse(await res.Content.ReadAsStringAsync())!["error"]!;
        // Doomed as before: the plug-in swallowed the fault, so the outer request fails as an unexpected plug-in error.
        error["code"]!.GetValue<string>().Should().Be(ODataErrorMiddleware.FormatCode(ErrorCodes.UnexpectedPluginException));
        (await TestSql.CountAsync(ConnectionStringOf(app), $"SELECT COUNT(*) FROM data.{Pipe}")).Should().Be(0);
    }

    // ---- helpers ----

    /// <summary>Creates a pb_pipe through the host's Web API (the host's own, decorated core runs it and its plug-ins).</summary>
    private async Task<HttpResponseMessage> PostPipeAsync(WebApplicationFactory<Program> app)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/pb_pipes")
        {
            Content = new StringContent("{\"pb_name\":\"p\"}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return await app.CreateClient().SendAsync(req);
    }

    private static string ConnectionStringOf(WebApplicationFactory<Program> app) =>
        app.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["ConnectionStrings:Org"]!;

    private static async Task<string?> ReadAsync(WebApplicationFactory<Program> app, Guid id)
    {
        return (string?)await TestSql.ScalarAsync(ConnectionStringOf(app), $"SELECT pb_data FROM data.{Pipe} WHERE {Pipe}id = @id", ("@id", id));
    }

    private static async Task RegisterTryCreateAsync(OrganizationService svc, int stage)
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(svc, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(svc, assembly, "Xrmd.TestPlugins.TryCreatePlugin");
        await PluginTestHelper.RegisterStepAsync(svc, type, "Create", Pipe, stage, config: $"{Log}|{Guid.NewGuid()}");
    }

    /// <summary>A host on a database of its own whose core throws <paramref name="failure"/> for every Create of <c>pb_pipelog</c> (the real, captured SQL exception).</summary>
    private async Task<WebApplicationFactory<Program>> HostAsync(Exception failure)
    {
        var app = fx.WithOwnDatabase(b => b.ConfigureTestServices(s =>
            s.AddScoped<IPipelineCore>(sp => new FailingCore(sp.GetRequiredService<CoreOperations>(), failure))));
        _ = app.Server; // start: creates, migrates and seeds the database
        await TestPublishers.EnsureAsync(ConnectionStringOf(app));
        var store = app.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await app.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        return app;
    }

    private sealed class FailingCore(CoreOperations inner, Exception failure) : IPipelineCore
    {
        private void Check(Xrmd.Model.Messages.Request request)
        {
            if (string.Equals(request.Name, "Create", StringComparison.OrdinalIgnoreCase)
                && request.Parameters.TryGetValue("Target", out var target) && target is Xrmd.Model.Records.Record { LogicalName: Log })
                throw failure;
        }

        public Task<Xrmd.Model.Messages.Response> ExecuteCoreAsync(Xrmd.Model.Messages.Request request, CoreCall call, CancellationToken ct)
        {
            Check(request);
            return inner.ExecuteCoreAsync(request, call, ct);
        }

        public bool IsCoreMessage(string messageName) => inner.IsCoreMessage(messageName);
        public bool AnswersOnModel(string messageName) => inner.AnswersOnModel(messageName);
        public IReadOnlyCollection<string> WrittenEntities(Xrmd.Model.Messages.Request request) => inner.WrittenEntities(request);
        public bool IsNonPipelineMessage(string messageName) => inner.IsNonPipelineMessage(messageName);
        public Task<Xrmd.Model.Messages.Response> ExecuteNonPipelineAsync(Xrmd.Model.Messages.Request request, Caller caller, DataSession session,
            CancellationToken ct) => inner.ExecuteNonPipelineAsync(request, caller, session, ct);
        public Task<bool> LockExistsAsync(Xrmd.Model.Records.RecordReference target, DataSession session, CancellationToken ct) =>
            inner.LockExistsAsync(target, session, ct);
        public Task<Xrmd.Model.Records.Record?> RetrieveImageAsync(Xrmd.Model.Records.RecordReference target, string[]? attributes,
            DataSession session, CancellationToken ct) =>
            inner.RetrieveImageAsync(target, attributes, session, ct);
        public Task<Caller> ResolveUserAsync(Guid systemUserId, DataSession session, CancellationToken ct) => inner.ResolveUserAsync(systemUserId, session, ct);
        public Task<Guid> SystemUserIdAsync(DataSession session, CancellationToken ct) => inner.SystemUserIdAsync(session, ct);
        public Task<(Guid Id, string Name)> OrganizationAsync(DataSession session, CancellationToken ct) => inner.OrganizationAsync(session, ct);
        public Task<int> PluginTraceLogSettingAsync(DataSession session, CancellationToken ct) => inner.PluginTraceLogSettingAsync(session, ct);
    }
}
