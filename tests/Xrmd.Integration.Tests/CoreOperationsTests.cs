using System.Data;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Query;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public sealed class CoreOperationsTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Project = TestEntities.Project;

    private IMetadataStore _store = null!;
    private IDbConnectionFactory _factory = null!;
    private CoreOperations _ops = null!;
    private Caller _caller = null!;

    public async Task InitializeAsync()
    {
        _store = fx.App.Services.GetRequiredService<IMetadataStore>();
        _factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        await TestEntities.EnsureProjectAsync(_store);
        _ops = new CoreOperations(_store,
            new QueryExecutor(new SqlQueryCompiler(_store, TestSql.Dialect), _factory, _store, TimeProvider.System,
                fx.App.Services.GetRequiredService<Xrmd.Security.SecurityCache>(), fx.App.Services.GetRequiredService<Xrmd.Security.FieldSecurity>(), fx.App.Services.GetRequiredService<ICurrencyCatalog>(),
                fx.App.Services.GetRequiredService<Xrmd.Files.FileColumnService>()),
            new Xrmd.Security.SecurityAdministration(_store, fx.App.Services.GetRequiredService<Xrmd.Security.SecurityCache>()), new Xrmd.Solution.SolutionComponentTracker(),
            fx.App.Services.GetRequiredService<Xrmd.Files.FileColumnService>(), fx.App.Services.GetRequiredService<Xrmd.Audit.AuditWriter>(),
            fx.App.Services.GetRequiredService<AuditMessages>(), fx.App.Services.GetRequiredService<Xrmd.Core.Duplicates.DuplicateRulePublisher>(),
            fx.App.Services.GetRequiredService<ICurrencyCatalog>(), fx.App.Services.GetRequiredService<AccessMessages>());

        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, "SELECT systemuserid, businessunitid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid");
        cmd.AddParameter("@oid", DbType.Guid, fx.AdminObjectId);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue();
        _caller = new Caller(rd.GetGuid(0), rd.GetGuid(1));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static QueryModel ByName(string name)
    {
        var root = new EntityNode { EntityName = Project };
        root.Columns.Add(new ColumnNode("pb_name"));
        root.Filter.Conditions.Add(new ConditionNode(null, "pb_name", ConditionOperator.Equal, [name]));
        return new QueryModel { Root = root };
    }

    private async Task<bool> ExistsAsync(Guid id)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, $"SELECT COUNT(*) FROM data.{Project} WHERE {Project}id = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    [Fact]
    public async Task Operations_share_the_session_transaction()
    {
        var name = "Shared-" + Guid.NewGuid();
        Guid id;
        await using (var conn = await _factory.OpenAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            var session = new DataSession(conn, tx);
            id = await _ops.CreateAsync(session, _caller, new Entity(Project) { ["pb_name"] = name }, default);

            var read = await _ops.RetrieveAsync(session, _caller, Project, id, new ColumnSet("pb_name"), default);
            read.GetAttributeValue<string>("pb_name").Should().Be(name);
            var many = await _ops.RetrieveMultipleAsync(session, _caller, ByName(name), default);
            many.Entities.Should().ContainSingle();

            await tx.RollbackAsync();
        }

        (await ExistsAsync(id)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_on_session_respects_rollback()
    {
        Guid id;
        await using (var conn = await _factory.OpenAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            id = await _ops.CreateAsync(new DataSession(conn, tx), _caller, new Entity(Project) { ["pb_name"] = "Keep" }, default);
            await tx.CommitAsync();
        }

        await using (var conn = await _factory.OpenAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await _ops.DeleteAsync(new DataSession(conn, tx), _caller, Project, id, default);
            await tx.RollbackAsync();
        }

        (await ExistsAsync(id)).Should().BeTrue();
    }
}
