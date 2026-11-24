using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Solutions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.Registration;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>
/// Plan 2.7b, Task 3: a solution's life on the run's provider leaves the same rows as on SQL Server. A managed solution is
/// installed, upgraded and uninstalled; after each step the solutions, their components, the layer tables and the custom tables
/// and columns (names, no ids) equal a snapshot written once on SQL Server (<c>Snapshots/solution-*.json</c>,
/// <c>XRMD_UPDATE_SNAPSHOTS=1</c> rewrites them). The export of an unmanaged solution gives the same <c>customizations.xml</c>,
/// and <c>xrmd apply</c> (the Admin API's schema apply) creates the same tables.
/// </summary>
[Collection("solution-import")]
public sealed partial class SolutionParityTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Table = ManagedTestSolution.Table;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

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
        if (Store.GetEntity(Table) is not null) await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.{Table}");
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.role WHERE name = 'xm_reader'");
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.publisher WHERE uniquename = 'xrmversemanaged'");
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.asyncoperation WHERE operationtype IN (203, 207, 208, 211)");
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            if (Store.GetEntity(Table) is not null) await Store.DeleteEntityAsync(s, Table, default);
            if (Store.GetOptionSet("xm_status") is not null) await Store.DeleteOptionSetAsync(s, "xm_status", default);
        });
        await SchemaCleanAsync();
        await PluginTestHelper.ClearAsync(fx.ConnectionString);
        fx.App.Services.GetRequiredService<StepRegistry>().Invalidate();
    }

    // ---- managed install, upgrade, uninstall --------------------------------------------------------------------------

    [Fact]
    public async Task A_managed_solution_installed_upgraded_and_uninstalled_leaves_the_rows_of_sql_server()
    {
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = ManagedTestSolution.Build(ManagedTestSolution.V1), ImportJobId = Guid.NewGuid() });
        Matches(await SnapshotAsync(), "solution-v1");

        await Admin.ExecuteAsync(new StageAndUpgradeRequest { CustomizationFile = ManagedTestSolution.Build(ManagedTestSolution.V2), ImportJobId = Guid.NewGuid() });
        Matches(await SnapshotAsync(), "solution-v2");

        // A row in the table: the uninstall counts it under the table share lock (ruling Q4) and drops it only when asked.
        await Admin.CreateAsync(new Entity(Table) { ["xm_name"] = "kept until asked" });
        var refused = await AdminAsync(HttpMethod.Post, "/admin/solutions/xm_base/uninstall", """{ "dropData": false, "dryRun": false }""");
        var refusal = await refused.Content.ReadAsStringAsync();
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, refusal);
        refusal.Should().Contain("table xm_item (1 row)").And.Contain("dropData");
        Matches(await SnapshotAsync(), "solution-v2");
        var uninstalled = await AdminAsync(HttpMethod.Post, "/admin/solutions/xm_base/uninstall", """{ "dropData": true, "dryRun": false }""");
        uninstalled.StatusCode.Should().Be(HttpStatusCode.OK, await uninstalled.Content.ReadAsStringAsync());
        Matches(await SnapshotAsync(), "solution-uninstalled");
    }

    // ---- export ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_export_of_an_unmanaged_solution_writes_the_customizations_of_sql_server()
    {
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = Guid.NewGuid() });

        var zip = ((ExportSolutionResponse)await Admin.ExecuteAsync(
            new ExportSolutionRequest { SolutionName = SolutionFixtureZip.FixtureName, Managed = false })).ExportSolutionFile;

        using var archive = new ZipArchive(new MemoryStream(zip));
        using var reader = new StreamReader(archive.GetEntry("customizations.xml")!.Open(), Encoding.UTF8);
        Matches(await reader.ReadToEndAsync(), "solution-export-customizations", "xml");
    }

    // ---- schema apply ------------------------------------------------------------------------------------------------

    private const string Schema = """
        publisher: zq
        choices:
          - name: zq_priority
            label: Priority
            options: [{ value: 100000000, label: Low }, { value: 100000001, label: High }]
        tables:
          - name: zq_project
            label: Project
            pluralLabel: Projects
            ownership: user
            primaryName: { name: zq_name, label: Name, maxLength: 200 }
            columns:
              - { name: zq_budget, type: money, label: Budget }
              - { name: zq_priority, type: choice, choice: zq_priority, label: Priority }
              - { name: zq_parent, type: lookup, target: zq_project, label: Parent }
              - { name: zq_notes, type: memo, maxLength: 2000, label: Notes }
            manyToMany:
              - { name: zq_project_zq_tag, with: zq_tag }
          - name: zq_tag
            label: Tag
            pluralLabel: Tags
            ownership: organization
            primaryName: { name: zq_name, label: Name }
            columns:
              - { name: zq_code, type: string, maxLength: 20, label: Code }
        """;

    [Fact]
    public async Task Schema_apply_creates_the_tables_of_sql_server()
    {
        var res = await AdminAsync(HttpMethod.Post, "/admin/schema/apply?dryRun=false", Schema, "text/yaml");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var tables = new JsonObject();
        foreach (var table in new[] { "zq_project", "zq_tag", "zq_project_zq_tag" })
        {
            var columns = await TestSql.RowsAsync(fx.ConnectionString,
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'data' AND table_name = @t", ("@t", table));
            tables[table] = new JsonArray(columns.Select(c => ((string)c[0]!).ToLowerInvariant()).Order(StringComparer.Ordinal)
                .Select(c => (JsonNode)c).ToArray());
        }
        Matches(new JsonObject { ["tables"] = tables, ["metadata"] = await MetadataNamesAsync("zq_") }.ToJsonString(Indented), "solution-schema-apply");
    }

    private async Task SchemaCleanAsync()
    {
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            if (Store.GetRelationship("zq_project_zq_tag") is not null) await Store.DeleteRelationshipAsync(s, "zq_project_zq_tag", default);
        });
        foreach (var e in Store.GetAllEntities().Where(e => e.LogicalName.StartsWith("zq_", StringComparison.Ordinal)).ToList())
            await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteEntityAsync(s, e.LogicalName, default));
        foreach (var o in Store.GetAllOptionSets().Where(o => o.Name.StartsWith("zq_", StringComparison.Ordinal)).ToList())
            await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteOptionSetAsync(s, o.Name, default));
    }

    // ---- snapshot ----------------------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The rows of the <c>xm_</c> solutions, their <c>solutioncomponent</c> rows, the layer tables and the <c>xm_</c> tables and
    /// columns of <c>meta</c>, each component named (not by id), sorted; ids (and any GUID in a payload) are written <c>&lt;id&gt;</c>.
    /// </summary>
    private async Task<string> SnapshotAsync()
    {
        var cs = fx.ConnectionString;
        var solutions = (await TestSql.RowsAsync(cs, "SELECT solutionid, uniquename, version, ismanaged FROM data.solution"))
            .Select(r => (Id: (Guid)r[0]!, Name: (string)r[1]!, Version: (string?)r[2], Managed: r[3] is true))
            .ToList();
        var names = solutions.ToDictionary(s => s.Id, s => s.Name);
        names[Xrmd.Solution.Layers.ComponentLayerStore.SystemLayerSolutionId] = "System";
        var ours = solutions.Where(s => s.Name.StartsWith("xm_", StringComparison.Ordinal)).ToList();

        await using var conn = await TestProvider.OpenAsync(cs);
        var session = new DataSession(conn, null);
        using var scope = fx.App.Services.CreateScope();
        var remover = scope.ServiceProvider.GetRequiredService<ComponentRemover>();
        var index = new ComponentIndex(Store);
        async Task<string> NameAsync(int type, Guid id) => Ids(await remover.NameAsync(session, index, type, id, default));

        var components = new List<string>();
        foreach (var row in await TestSql.RowsAsync(cs, "SELECT solutionid, componenttype, objectid, rootcomponentbehavior FROM data.solutioncomponent"))
        {
            if (!ours.Any(s => s.Id == (Guid)row[0]!)) continue;
            var type = Convert.ToInt32(row[1]);
            components.Add($"{names[(Guid)row[0]!]} | {type} | {await NameAsync(type, (Guid)row[2]!)} | behavior {Convert.ToInt32(row[3] ?? 0)}");
        }

        var layers = new List<string>();
        foreach (var row in await TestSql.RowsAsync(cs, "SELECT component_type, object_id, solution_id, layer_order, payload FROM meta.component_layer"))
        {
            var type = Convert.ToInt32(row[0]);
            var owner = names.GetValueOrDefault((Guid)row[2]!) ?? "<unknown solution>";
            // The payload (a fragment of the solution file, ids written <id>) by its length and hash: it can be large. The files it
            // carries (an assembly's bytes, which change with each build of the test plug-ins) are written <file>.
            var payload = row[4] is string p ? Digest(Ids(WithoutFileContents(p))) : "(null)";
            layers.Add($"{type} | {await NameAsync(type, (Guid)row[1]!)} | {owner} | order {Convert.ToInt64(row[3])} | {payload}");
        }

        var ranks = (await TestSql.RowsAsync(cs, "SELECT solution_id, segment, sub FROM meta.solution_rank"))
            .Select(r => $"{names.GetValueOrDefault((Guid)r[0]!) ?? "<unknown solution>"} | {Convert.ToInt32(r[1])} | {Convert.ToInt32(r[2])}");

        var snapshot = new JsonObject
        {
            ["solutions"] = Array(ours.Select(s => $"{s.Name} | {s.Version} | managed {s.Managed}")),
            ["solutioncomponent"] = Array(components),
            ["component_layer"] = Array(layers),
            ["solution_rank"] = Array(ranks),
            ["metadata"] = await MetadataNamesAsync("xm_"),
        };
        return snapshot.ToJsonString(Indented);
    }

    /// <summary>The tables of <c>meta.entity</c> with <paramref name="prefix"/>, each with its columns of <c>meta.attribute</c>.</summary>
    private async Task<JsonObject> MetadataNamesAsync(string prefix)
    {
        var result = new JsonObject();
        var entities = (await TestSql.RowsAsync(fx.ConnectionString, "SELECT logical_name FROM meta.entity"))
            .Select(r => ((string)r[0]!).ToLowerInvariant()).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal);
        var attributes = (await TestSql.RowsAsync(fx.ConnectionString, "SELECT entity_logical_name, logical_name FROM meta.attribute"))
            .Select(r => (Entity: ((string)r[0]!).ToLowerInvariant(), Name: ((string)r[1]!).ToLowerInvariant())).ToList();
        foreach (var entity in entities)
            result[entity] = Array(attributes.Where(a => a.Entity == entity).Select(a => a.Name));
        return result;
    }

    private static JsonArray Array(IEnumerable<string> items) =>
        new(items.Order(StringComparer.Ordinal).Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Guids();

    /// <summary><paramref name="payload"/> with the content of each entry of its <c>files</c> object written <c>&lt;file&gt;</c>.</summary>
    private static string WithoutFileContents(string payload)
    {
        if (JsonNode.Parse(payload) is not JsonObject json || json["files"] is not JsonObject files) return payload;
        foreach (var name in files.Select(f => f.Key).ToList()) files[name] = "<file>";
        return json.ToJsonString();
    }

    private static string Ids(string text) => Guids().Replace(text, "<id>");

    private static string Digest(string text) =>
        $"{text.Length} chars, sha256 {Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16]}";

    /// <summary>
    /// <paramref name="actual"/> equals the checked-in snapshot <c>Snapshots/{name}.{extension}</c>; <c>XRMD_UPDATE_SNAPSHOTS=1</c>
    /// writes it (on SQL Server: the snapshots are SQL Server's, which every engine must match).
    /// </summary>
    private static void Matches(string actual, string name, string extension = "json", [CallerFilePath] string file = "")
    {
        actual = actual.Replace("\r\n", "\n", StringComparison.Ordinal);
        var path = Path.Combine(Path.GetDirectoryName(file)!, "Snapshots", $"{name}.{extension}");
        if (Environment.GetEnvironmentVariable("XRMD_UPDATE_SNAPSHOTS") == "1" && !TestProvider.IsPostgres)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }
        File.Exists(path).Should().BeTrue($"the snapshot {path} is checked in (XRMD_UPDATE_SNAPSHOTS=1 on SQL Server writes it)");
        actual.Should().Be(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), $"the snapshot {name}.{extension}");
    }

    private Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, string body, string mediaType = "application/json")
    {
        var req = new HttpRequestMessage(method, path) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return fx.Client.SendAsync(req);
    }
}
