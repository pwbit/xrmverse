using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Pipeline.Registration;
using Xrmd.Solution;

namespace Xrmd.Integration.Tests;

/// <summary>The MCP tools <c>list_component_layers</c> and the <c>holding</c> field of <c>list_solutions</c> (plan 2.6, Task 8, ruling M16).</summary>
[Collection("solution-import")]
public sealed class McpSolutionLayersTests(XrmdFixture fx) : IAsyncLifetime
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

    private async Task<McpClient> ConnectAsync(Guid oid)
    {
        var http = fx.App.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(fx.BaseUrl + "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static async Task<JsonObject> CallAsync(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        result.IsError.Should().NotBe(true, Text(result));
        return JsonNode.Parse(Text(result))!.AsObject();
    }

    private Task ImportAsync(ManagedSpec spec, bool holding = false) =>
        TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId).ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = ManagedTestSolution.Build(spec), ImportJobId = Guid.NewGuid(), HoldingSolution = holding,
        });

    [Fact]
    public async Task List_component_layers_by_solution_and_by_component()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await using var client = await ConnectAsync(fx.AdminObjectId);

        var bySolution = await CallAsync(client, "list_component_layers", new() { ["solutionName"] = "xm_base" });
        var rows = bySolution["layers"]!.AsArray();
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r!["solution"]!.GetValue<string>() == "xm_base" && r["kind"]!.GetValue<string>() == "managed");

        var table = Store.GetEntity(ManagedTestSolution.Table)!.MetadataId!.Value;
        var byComponent = await CallAsync(client, "list_component_layers",
            new() { ["componentType"] = 1, ["componentId"] = table.ToString() });
        var stack = byComponent["layers"]!.AsArray();
        stack.Should().HaveCount(1);
        stack[0]!["solution"]!.GetValue<string>().Should().Be("xm_base");
        stack[0]!["version"]!.GetValue<string>().Should().Be("1.0.0.0");

        var neither = await client.CallToolAsync("list_component_layers", new Dictionary<string, object?>());
        neither.IsError.Should().BeTrue();
        var unknown = await client.CallToolAsync("list_component_layers", new Dictionary<string, object?> { ["solutionName"] = "xm_nothere" });
        unknown.IsError.Should().BeTrue();
        Text(unknown).Should().Contain("does not exist");
    }

    [Fact]
    public async Task List_solutions_flags_the_holding_solution()
    {
        await ImportAsync(ManagedTestSolution.V1);
        await ImportAsync(ManagedTestSolution.V2, holding: true);
        await using var client = await ConnectAsync(fx.AdminObjectId);

        var solutions = (await CallAsync(client, "list_solutions", new()))["solutions"]!.AsArray();
        bool Holding(string name) => solutions.Single(s => s!["uniquename"]!.GetValue<string>() == name)!["holding"]!.GetValue<bool>();
        Holding("xm_base").Should().BeFalse();
        Holding("xm_base_Upgrade").Should().BeTrue();
    }
}
