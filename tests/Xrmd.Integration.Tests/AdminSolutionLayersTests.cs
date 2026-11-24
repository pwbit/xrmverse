using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.Registration;
using Xrmd.Security;
using Xrmd.Solution;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// <c>GET /admin/solutions/{uniquename}/layers</c> and <c>GET /admin/layers/{componenttype}/{objectid}</c> (plan 2.6, Task 8,
/// ruling M16): the layers of a managed solution and of one component, for a System Administrator only.
/// </summary>
[Collection("solution-import")]
public sealed class AdminSolutionLayersTests(XrmdFixture fx) : IAsyncLifetime
{
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await CleanAsync();
    }

    public Task DisposeAsync() => CleanAsync();

    private async Task CleanAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx);
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command("""
                IF OBJECT_ID(N'data.xm_item') IS NOT NULL DELETE FROM data.xm_item;
                DELETE FROM meta.component_layer; DELETE FROM meta.solution_rank;
                DELETE FROM data.role WHERE name IN (N'xm_reader', N'xm_writer');
                DELETE FROM data.solutioncomponent WHERE solutionid IN (SELECT solutionid FROM data.solution WHERE uniquename LIKE N'xm[_]%');
                DELETE FROM data.solution WHERE uniquename LIKE N'xm[_]%';
                DELETE FROM data.publisher WHERE uniquename = N'xrmversemanaged';
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            if (Store.GetEntity(ManagedTestSolution.Table) is not null) await Store.DeleteEntityAsync(s, ManagedTestSolution.Table, default);
            if (Store.GetOptionSet("xm_status") is not null) await Store.DeleteOptionSetAsync(s, "xm_status", default);
        });
        await PluginTestHelper.ClearAsync(fx.ConnectionString);
        fx.App.Services.GetRequiredService<StepRegistry>().Invalidate();
    }

    private Task<HttpResponseMessage> GetAsync(string path, Guid? oid = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid ?? fx.AdminObjectId, fx.BaseUrl));
        return fx.Client.SendAsync(req);
    }

    private static async Task<JsonArray> RowsAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsArray();
    }

    private Task ImportAsync(ManagedSpec spec) =>
        ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = ManagedTestSolution.Build(spec), ImportJobId = Guid.NewGuid(),
        });

    [Fact]
    public async Task Solution_layers_list_the_components_it_owns()
    {
        await ImportAsync(ManagedTestSolution.V1);

        var rows = await RowsAsync(await GetAsync("/admin/solutions/xm_base/layers"));
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r!["solution"]!.GetValue<string>() == "xm_base" && r["kind"]!.GetValue<string>() == "managed"
            && r["version"]!.GetValue<string>() == "1.0.0.0");
        rows.Select(r => r!["componentType"]!.GetValue<int>()).Should().Contain(1);
        rows.Select(r => r!["order"]!.GetValue<long>()).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Component_layers_list_the_stack_top_first()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var table = Store.GetEntity(ManagedTestSolution.Table)!.MetadataId!.Value;

        var rows = await RowsAsync(await GetAsync($"/admin/layers/1/{table}"));
        rows.Should().HaveCount(1);
        rows[0]!["solution"]!.GetValue<string>().Should().Be("xm_base");
        rows[0]!["kind"]!.GetValue<string>().Should().Be("managed");
        rows[0]!["version"]!.GetValue<string>().Should().Be("1.0.0.0");
    }

    [Fact]
    public async Task Unknown_solution_or_component_is_404()
    {
        (await GetAsync("/admin/solutions/xm_nothere/layers")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync($"/admin/layers/1/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync("/admin/layers/x/" + Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync("/admin/layers/1/not-a-guid")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Layer_routes_require_a_system_administrator()
    {
        await ImportAsync(ManagedTestSolution.V1);
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await AssignRoleAsync(fx, user, await CreateRoleAsync(fx, "layers-" + Guid.NewGuid().ToString("N")[..8],
            ("prvReadSolution", DepthMask.Global)));
        (await GetAsync("/admin/solutions/xm_base/layers", oid)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
