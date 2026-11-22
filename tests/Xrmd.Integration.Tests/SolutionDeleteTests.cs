using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The Delete of a <c>solution</c> row (SDK Delete, Web API <c>DELETE solutions(id)</c>), as Dataverse deletes an unmanaged
/// solution: only the container (the row and its <c>solutioncomponent</c> rows) goes; every component stays in the system
/// and in <c>Default</c>. The system solutions are refused; a managed solution is uninstalled (plan 2.6).
/// </summary>
[Collection("solution-import")]
public sealed class SolutionDeleteTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Fixture = SolutionFixtureZip.FixtureName;
    private const string FailingTrigger = "xv_test_solution_delete_fails";

    /// <summary>Drops the failing trigger if it exists (on PostgreSQL, with its function).</summary>
    private static string DropFailingTrigger => TestProvider.IsPostgres
        ? $"DROP TRIGGER IF EXISTS {FailingTrigger} ON data.solution; DROP FUNCTION IF EXISTS data.{FailingTrigger}();"
        : $"DROP TRIGGER IF EXISTS data.{FailingTrigger}";
    private static readonly Guid RoleId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e01");
    private static readonly Guid AssemblyId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e10");
    private static readonly Guid CreateStepId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e21");
    private static readonly Guid WebResourceId = new("6b1c3f4e-1a2b-4c3d-8e9f-0a1b2c3d4e41");

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await ExecAsync(DropFailingTrigger);
        await SolutionImportTests.ClearImportAsync(fx);
    }

    public async Task DisposeAsync()
    {
        await ExecAsync(DropFailingTrigger);
        await SolutionImportTests.ClearImportAsync(fx);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private Task ImportAsync() =>
        Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = Guid.NewGuid() });

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt32(await ScalarAsync(sql, parameters));

    private async Task<Guid?> FixtureSolutionIdAsync() =>
        (Guid?)await ScalarAsync("SELECT solutionid FROM data.solution WHERE uniquename = @n", ("@n", Fixture));

    private async Task<List<(int Type, Guid ObjectId)>> ComponentsAsync(Guid solutionId)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        return [.. await fx.App.Services.GetRequiredService<SolutionComponentTracker>().ListAsync(new DataSession(conn, null), solutionId, default)];
    }

    /// <summary>The <c>xv_*</c> tables of the database.</summary>
    private Task<int> FixtureTablesAsync() =>
        CountAsync("SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = N'data' AND TABLE_TYPE = N'BASE TABLE' AND TABLE_NAME LIKE N'xv[_]%'");

    private async Task<HttpResponseMessage> WebApiDeleteAsync(Guid solutionId)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"{Api}solutions({solutionId:D})");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return await fx.Client.SendAsync(req);
    }

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_the_solution_keeps_its_components_in_Default()
    {
        await ImportAsync();
        var solutionId = (await FixtureSolutionIdAsync())!.Value;
        var components = await ComponentsAsync(solutionId);
        components.Should().NotBeEmpty();
        var recordId = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "Before" });
        var tables = await FixtureTablesAsync();
        var publisherId = (Guid)(await ScalarAsync("SELECT publisherid FROM data.solution WHERE solutionid = @s", ("@s", solutionId)))!;
        // A component the solution holds but Default does not track (as an older database could have it).
        await ExecAsync("DELETE FROM data.solutioncomponent WHERE solutionid = @d AND objectid = @o",
            ("@d", SystemSeed.DefaultSolutionId), ("@o", WebResourceId));

        await Admin.DeleteAsync("solution", solutionId);

        // The container is gone.
        (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE solutionid = @s", ("@s", solutionId))).Should().Be(0);
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @s", ("@s", solutionId))).Should().Be(0);
        // The publisher stays.
        (await CountAsync("SELECT COUNT(*) FROM data.publisher WHERE publisherid = @p", ("@p", publisherId))).Should().Be(1);
        // Every component stays, with its data.
        (await FixtureTablesAsync()).Should().Be(tables);
        Store.GetEntity("xv_project").Should().NotBeNull();
        Store.GetEntity("xv_task").Should().NotBeNull();
        Store.GetOptionSet("xv_priority").Should().NotBeNull();
        Store.GetRelationship("xv_project_xv_tag").Should().NotBeNull();
        (await Admin.RetrieveAsync("xv_project", recordId, new ColumnSet("xv_name"))).GetAttributeValue<string>("xv_name").Should().Be("Before");
        (await CountAsync("SELECT COUNT(*) FROM data.role WHERE roleid = @id", ("@id", RoleId))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = @id", ("@id", RoleId))).Should().BePositive();
        (await CountAsync("SELECT COUNT(*) FROM data.pluginassembly WHERE pluginassemblyid = @id", ("@id", AssemblyId))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", CreateStepId)))
            .Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.webresource WHERE webresourceid = @id", ("@id", WebResourceId))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.savedquery")).Should().BePositive();
        (await CountAsync("SELECT COUNT(*) FROM data.systemform")).Should().BePositive();
        // Each component is listed in Default, the untracked one included.
        var inDefault = (await ComponentsAsync(SystemSeed.DefaultSolutionId)).ToHashSet();
        inDefault.Should().Contain(components);
        inDefault.Should().Contain((ComponentType.WebResource, WebResourceId));
        // The plug-ins still fire.
        var id = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "After" });
        (await Admin.RetrieveAsync("xv_project", id, new ColumnSet("xv_description"))).GetAttributeValue<string>("xv_description")
            .Should().Be("set by the fixture pre-operation create step");
    }

    [Fact]
    public async Task Default_and_Active_cannot_be_deleted()
    {
        foreach (var system in new[] { SystemSeed.DefaultSolutionId, SystemSeed.ActiveSolutionId })
        {
            var fault = await FaultAsync(() => Admin.DeleteAsync("solution", system));
            fault.HttpStatus.Should().Be(403);
            var res = await WebApiDeleteAsync(system);
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync());
            (await CountAsync("SELECT COUNT(*) FROM data.solution WHERE solutionid = @s", ("@s", system))).Should().Be(1);
        }
    }

    [Fact]
    public async Task Managed_solution_delete_is_its_uninstall()
    {
        // Plan 2.6, ruling M9: the Delete of a managed solution uninstalls it (SolutionUninstallTests covers the layers). A
        // solution marked managed by hand has no layer: its uninstall deletes no component, only the solution row.
        await ImportAsync();
        var solutionId = (await FixtureSolutionIdAsync())!.Value;
        await ExecAsync("UPDATE data.solution SET ismanaged = 1 WHERE solutionid = @s", ("@s", solutionId));

        await Admin.DeleteAsync("solution", solutionId);

        (await FixtureSolutionIdAsync()).Should().BeNull();
        (await ComponentsAsync(solutionId)).Should().BeEmpty();
        Store.GetEntity("xv_project").Should().NotBeNull("a component without a layer of the solution stays");
    }

    [Fact]
    public async Task Delete_is_atomic()
    {
        await ImportAsync();
        var solutionId = (await FixtureSolutionIdAsync())!.Value;
        await ExecAsync("DELETE FROM data.solutioncomponent WHERE solutionid = @d AND objectid = @o",
            ("@d", SystemSeed.DefaultSolutionId), ("@o", WebResourceId));
        var components = await ComponentsAsync(solutionId);
        var defaultComponents = await ComponentsAsync(SystemSeed.DefaultSolutionId);
        // The row's DELETE fails after the solutioncomponent rows were written.
        await ExecAsync(TestProvider.IsPostgres
            ? $"""
              CREATE FUNCTION data.{FailingTrigger}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected failure'; END $$;
              CREATE TRIGGER {FailingTrigger} AFTER DELETE ON data.solution FOR EACH ROW EXECUTE FUNCTION data.{FailingTrigger}();
              """
            : $"""
              CREATE TRIGGER data.{FailingTrigger} ON data.solution AFTER DELETE AS
              THROW 50001, N'injected failure', 1;
              """);

        await ((Func<Task>)(() => Admin.DeleteAsync("solution", solutionId))).Should().ThrowAsync<Exception>();

        await ExecAsync(DropFailingTrigger);
        (await FixtureSolutionIdAsync()).Should().Be(solutionId);
        (await ComponentsAsync(solutionId)).Should().Equal(components);
        (await ComponentsAsync(SystemSeed.DefaultSolutionId)).Should().Equal(defaultComponents,
            "the Default tracking added by the failed delete is rolled back too");
    }

    [Fact]
    public async Task Delete_requires_prvDeleteSolution()
    {
        await ImportAsync();
        var solutionId = (await FixtureSolutionIdAsync())!.Value;
        var role = await CreateRoleAsync(fx, "Solution reader " + Guid.NewGuid().ToString("N")[..8],
            ("prvReadSolution", DepthMask.Global), ("prvWriteSolution", DepthMask.Global), ("prvCreateSolution", DepthMask.Global));
        var objectId = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId), role);
        var user = ServiceFor(fx, objectId);

        var denied = await FaultAsync(() => user.DeleteAsync("solution", solutionId));
        denied.HttpStatus.Should().Be(403);
        denied.Message.Should().Contain("prvDeleteSolution");
        (await FixtureSolutionIdAsync()).Should().Be(solutionId);

        await AddPrivilegeAsync(fx, role, "prvDeleteSolution", DepthMask.Global);
        Cache(fx).Invalidate();
        await user.DeleteAsync("solution", solutionId);
        (await FixtureSolutionIdAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Reimport_after_delete_recreates_the_solution_and_reuses_components()
    {
        await ImportAsync();
        var solutionId = (await FixtureSolutionIdAsync())!.Value;
        var components = await ComponentsAsync(solutionId);
        var projectId = Store.GetEntity("xv_project")!.MetadataId;
        var recordId = await Admin.CreateAsync(new Entity("xv_project") { ["xv_name"] = "Kept" });

        var res = await WebApiDeleteAsync(solutionId);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        (await FixtureSolutionIdAsync()).Should().BeNull();

        await ImportAsync();

        var recreated = (await FixtureSolutionIdAsync())!.Value;
        (await ComponentsAsync(recreated)).Should().BeEquivalentTo(components, "the same components are tracked again");
        Store.GetEntity("xv_project")!.MetadataId.Should().Be(projectId, "the existing table is reused, not recreated");
        (await CountAsync("SELECT COUNT(*) FROM data.role WHERE roleid = @id", ("@id", RoleId))).Should().Be(1);
        (await CountAsync("SELECT COUNT(*) FROM data.pluginassembly WHERE pluginassemblyid = @id", ("@id", AssemblyId))).Should().Be(1);
        (await Admin.RetrieveAsync("xv_project", recordId, new ColumnSet("xv_name"))).GetAttributeValue<string>("xv_name").Should().Be("Kept");
        (await CountAsync("""
            SELECT COUNT(*) FROM (SELECT componenttype, objectid FROM data.solutioncomponent WHERE solutionid = @d
                GROUP BY componenttype, objectid HAVING COUNT(*) > 1) AS duplicates
            """, ("@d", SystemSeed.DefaultSolutionId)))
            .Should().Be(0, "Default holds each component once");
    }
}
