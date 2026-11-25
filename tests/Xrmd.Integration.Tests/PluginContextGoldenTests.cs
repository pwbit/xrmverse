using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Security;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2e2c, Task 1: golden dumps of what plug-ins see. <c>ContextDumpPlugin</c> (net462, written against the public
/// <c>IPluginExecutionContext7</c> members only) writes a canonical dump of its execution context into <c>pb_pipe2log</c>; each
/// scenario registers its steps, runs the message and compares the dumps (ordered by name, then content) and the caller-visible
/// result with <c>tests/fixtures/plugin-context-golden/{scenario}.txt</c>. The files were captured from the code before the
/// pipeline moved onto the model context and are the contract of that move (plan S.2e2c, Global Constraints). A missing file is
/// written once and the test fails; an existing file is never rewritten.
/// </summary>
[Collection("solution-import")]
public sealed class PluginContextGoldenTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Pipe = TestEntities.Pipe;
    private const string PipeLog = TestEntities.PipeLog;
    private const string DumpLog = TestEntities.PipeLog2;
    private const string Account = "account";
    private const string Plugins = "Xrmd.TestPlugins.";
    private const string Echo = Plugins + "CustomApiEchoPlugin";
    private const string Api = "/api/data/v9.2/";
    private const int Sync = 0;
    private const int Async = 1;

    private static readonly Regex GuidPattern = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
    private static readonly Regex TokenPattern = new("@obj[0-9a-f]{8}-[0-9]+");

    private OrganizationService Admin => SecurityTestHelper.ServiceFor(fx, fx.AdminObjectId);
    private readonly Dictionary<string, Guid> _types = new();
    private Guid _assemblyId;
    private StringBuilder _file = new();
    private readonly List<Guid> _users = [];

    /// <summary>The ids the test knows, by the role name the golden shows. Every other id is numbered per scenario.</summary>
    private readonly List<(Guid Id, string Name)> _known = [];

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
        await PluginTestHelper.ClearAsync(fx);
        await CleanDataAsync();
        _assemblyId = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);

        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using (var cmd = TestSql.Command(conn, "SELECT systemuserid, businessunitid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid"))
        {
            cmd.Parameters.AddWithValue("@oid", fx.AdminObjectId);
            await using var rd = await cmd.ExecuteReaderAsync();
            await rd.ReadAsync();
            _known.Add((rd.GetGuid(0), "{user}"));
            _known.Add((rd.GetGuid(1), "{businessunit}"));
        }

        await using (var cmd = TestSql.Command(conn, "SELECT organizationid FROM data.organization"))
            _known.Add(((Guid)(await cmd.ExecuteScalarAsync())!, "{organization}"));
        _known.Add((fx.AdminObjectId, "{useraad}"));
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await CleanDataAsync();
        foreach (var id in _users) await ExecAsync($"DELETE FROM data.systemuser WHERE systemuserid = '{id}';");
        await SolutionImportTests.ClearImportAsync(fx);
    }

    private Task CleanDataAsync() => ExecAsync(
        $"DELETE FROM data.{Pipe}; DELETE FROM data.{PipeLog}; DELETE FROM data.{DumpLog}; DELETE FROM data.asyncoperation; " +
        "DELETE FROM data.account WHERE name LIKE 'gld-%';");

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string> ScalarAsync(string sql, Guid? id = null)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        if (id is not null) cmd.Parameters.AddWithValue("@id", id.Value);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? "<null>" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private async Task<Guid> StepAsync(string plugin, string message, string? entity, int stage, string? config = null, int mode = Sync,
        int rank = 1, string? filteringAttributes = null, Guid? runAs = null)
    {
        if (!_types.TryGetValue(plugin, out var typeId))
            _types[plugin] = typeId = await PluginTestHelper.RegisterTypeAsync(Admin, _assemblyId, Plugins + plugin);
        return await PluginTestHelper.RegisterStepAsync(Admin, typeId, message, entity, stage, mode, rank, config, filteringAttributes,
            runAs);
    }

    /// <summary>A <c>ContextDumpPlugin</c> step writing into <c>pb_pipe2log</c>, optionally setting <paramref name="mutation"/> (<c>attr=value</c>).</summary>
    private Task<Guid> DumpAsync(string message, string? entity, int stage, string? mutation = null, int mode = Sync, int rank = 1,
        string? filteringAttributes = null, Guid? runAs = null) =>
        StepAsync("ContextDumpPlugin", message, entity, stage, mutation is null ? DumpLog : $"{DumpLog}|{mutation}", mode, rank,
            filteringAttributes, runAs);

    /// <summary>A mutation-only <c>ContextDumpPlugin</c> step (nothing is dumped).</summary>
    private Task<Guid> MutateAsync(string message, string? entity, int stage, string mutation, int rank) =>
        StepAsync("ContextDumpPlugin", message, entity, stage, "-|" + mutation, Sync, rank);

    /// <summary>A user other than the admin to run steps as; it may create the dump and log records.</summary>
    private async Task<Guid> RunAsUserAsync()
    {
        var root = _known.Single(k => k.Name == "{businessunit}").Id;
        var id = await SecurityTestHelper.CreateUserAsync(fx, root, Guid.NewGuid());
        _users.Add(id);
        await SecurityTestHelper.GrantAsync(fx, id, team: false, ("prvCreatepb_Pipe2Log", DepthMask.Basic), ("prvCreatepb_PipeLog", DepthMask.Basic));
        _known.Add((id, "{runasuser}"));
        return id;
    }

    private AsyncOperationWorker NewWorker() =>
        new(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
            Options.Create(new PipelineOptions
            {
                AsyncPollInterval = TimeSpan.FromMilliseconds(100),
                AsyncRetryDelay = TimeSpan.FromMilliseconds(100),
                AsyncMaxRetries = 1,
            }));

    private async Task DrainAsync()
    {
        var worker = NewWorker();
        while (await worker.RunOnceAsync(default)) { }
        (await CountAsync("asyncoperation WHERE statecode <> 3")).Should().Be(0, "every queued job has run");
    }

    private async Task<string> StoredPipesAsync() =>
        "stored pb_pipe rows=" + await CountAsync(Pipe) + " names=" + await ScalarAsync($"SELECT MIN(pb_name) FROM data.{Pipe}");

    private async Task<string> StoredLogsAsync() =>
        "stored pb_pipelog rows=" + await CountAsync(PipeLog) + " names=" + await ScalarAsync($"SELECT MIN(pb_name) FROM data.{PipeLog}");

    private async Task<string> JobsAsync() =>
        "asyncoperation rows=" + await CountAsync("asyncoperation") + " state/status=" +
        await ScalarAsync("SELECT MIN(statecode * 100 + statuscode) FROM data.asyncoperation");

    private async Task<int> CountAsync(string fromWhere)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT COUNT(*) FROM data.{fromWhere}");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Takes the dumps written so far (and removes them) as one section of the golden file.</summary>
    private async Task SectionAsync(string title, params string[] callerLines)
    {
        var rows = new List<(string Name, string Data, int Seq, string Key)>();
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data FROM data.{DumpLog}");
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var name = rd.GetString(0);
                var data = (rd.IsDBNull(1) ? "" : rd.GetString(1)).Replace("\r\n", "\n");
                var eol = data.IndexOf('\n');
                var seq = int.Parse(data[..eol].Replace("Seq: ", ""), System.Globalization.CultureInfo.InvariantCulture);
                data = data[(eol + 1)..];
                // The order must not depend on ids or tokens (they are numbered later, in this order).
                var key = name + "\n" + TokenPattern.Replace(GuidPattern.Replace(data, "{g}"), "@o");
                rows.Add((name, data, seq, key));
            }
        }

        await ExecAsync($"DELETE FROM data.{DumpLog};");
        rows.Sort((x, y) =>
        {
            var c = StringComparer.Ordinal.Compare(x.Key, y.Key);
            return c != 0 ? c : x.Seq.CompareTo(y.Seq);
        });
        _file.Append("=== ").Append(title).Append(" ===\n");
        foreach (var (name, data, _, _) in rows) _file.Append("--- dump ").Append(name).Append(" ---\n").Append(data).Append('\n');
        _file.Append("--- caller ---\n");
        foreach (var line in callerLines) _file.Append(line).Append('\n');
    }

    /// <summary>
    /// The file with ids named (the known ones by role) or numbered {id1}... and identity tokens numbered @1... in first-seen
    /// order over the whole scenario, so drift of an id or a replaced instance between stages shows.
    /// </summary>
    private string Normalized()
    {
        var ids = new Dictionary<Guid, string>();
        foreach (var (id, name) in _known) ids.TryAdd(id, name);
        var tokens = new Dictionary<string, string>();
        var text = TokenPattern.Replace(_file.ToString(), m =>
        {
            if (!tokens.TryGetValue(m.Value, out var n)) tokens[m.Value] = n = "@" + (tokens.Count + 1);
            return n;
        });
        return GuidPattern.Replace(text, m =>
        {
            var id = Guid.Parse(m.Value);
            if (id == Guid.Empty) return "{empty}";
            if (!ids.TryGetValue(id, out var n)) ids[id] = n = "{id" + (ids.Count(i => i.Value.StartsWith("{id", StringComparison.Ordinal)) + 1) + "}";
            return n;
        });
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        dir.Should().NotBeNull("the repository root holds xrmverse.sln");
        return dir!.FullName;
    }

    private void AssertGolden(string scenario)
    {
        var path = Path.Combine(RepositoryRoot(), "tests", "fixtures", "plugin-context-golden", scenario + ".txt");
        var text = Normalized();
        var actual = Encoding.UTF8.GetBytes(text);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.Write(actual);
            Assert.Fail($"golden file {scenario}.txt written, review and commit");
        }

        // Compare as text first: a difference is readable; then the bytes.
        Encoding.UTF8.GetString(File.ReadAllBytes(path)).Should().Be(text);
        File.ReadAllBytes(path).Should().Equal(actual);
    }

    private async Task<string> WebApiAsync(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, body);
        return string.Join(",", JsonNode.Parse(body)!["value"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()));
    }

    // ---- the scenarios ----------------------------------------------------------------------------------------------

    public static TheoryData<string> Names =>
    [
        "create_pre_mutates_target", "update_images_filter", "shared_across_stages", "nested_parent_chain", "retrieve_output_edited",
        "webapi_filter_with_step", "merged_bulk_views", "upsert_inner", "custom_api_outputs", "setparent_after_nested",
        "runas_user", "in_place_mutations",
    ];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Plugins_see_what_the_golden_file_says(string scenario)
    {
        _file = new StringBuilder();
        switch (scenario)
        {
            case "create_pre_mutates_target": await CreatePreMutatesTargetAsync(); break;
            case "update_images_filter": await UpdateImagesFilterAsync(); break;
            case "shared_across_stages": await SharedAcrossStagesAsync(); break;
            case "nested_parent_chain": await NestedParentChainAsync(); break;
            case "retrieve_output_edited": await RetrieveOutputEditedAsync(); break;
            case "webapi_filter_with_step": await WebApiFilterWithStepAsync(); break;
            case "merged_bulk_views": await MergedBulkViewsAsync(); break;
            case "upsert_inner": await UpsertInnerAsync(); break;
            case "custom_api_outputs": await CustomApiOutputsAsync(); break;
            case "setparent_after_nested": await SetParentAfterNestedAsync(); break;
            case "runas_user": await RunAsUserScenarioAsync(); break;
            case "in_place_mutations": await InPlaceMutationsAsync(); break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        AssertGolden(scenario);
    }

    private async Task CreatePreMutatesTargetAsync()
    {
        await DumpAsync("Create", Pipe, 10);
        await StepAsync("SetAttributePlugin", "Create", Pipe, 20, "pb_data=set-at-20");
        await DumpAsync("Create", Pipe, 20, rank: 2);
        await DumpAsync("Create", Pipe, 40);

        var id = await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "gld-create", ["pb_count"] = 3 });

        await SectionAsync("create", "stored pb_name=" + await ScalarAsync($"SELECT pb_name FROM data.{Pipe} WHERE pb_pipeid = @id", id),
            "stored pb_data=" + await ScalarAsync($"SELECT pb_data FROM data.{Pipe} WHERE pb_pipeid = @id", id),
            "stored pb_count=" + await ScalarAsync($"SELECT pb_count FROM data.{Pipe} WHERE pb_pipeid = @id", id));
    }

    private async Task UpdateImagesFilterAsync()
    {
        var id = await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "gld-old", ["pb_count"] = 1, ["pb_data"] = "old-data" });
        var pre = await DumpAsync("Update", Pipe, 20, filteringAttributes: "pb_data");
        await PluginTestHelper.RegisterImageAsync(Admin, pre, 0, "pre", "pb_name,pb_count,pb_data");
        var post = await DumpAsync("Update", Pipe, 40);
        await PluginTestHelper.RegisterImageAsync(Admin, post, 0, "pre", "pb_name");
        await PluginTestHelper.RegisterImageAsync(Admin, post, 1, "post", "pb_name,pb_count,pb_data");

        // The stage 20 step filters on pb_data: it does not fire for a change of pb_name only.
        await Admin.UpdateAsync(new Entity(Pipe, id) { ["pb_name"] = "gld-new" });
        await SectionAsync("update pb_name only (the pb_data filter does not match)");

        await Admin.UpdateAsync(new Entity(Pipe, id) { ["pb_data"] = "new-data", ["pb_count"] = 2 });
        await SectionAsync("update pb_data and pb_count (the filter matches)",
            "stored pb_name=" + await ScalarAsync($"SELECT pb_name FROM data.{Pipe} WHERE pb_pipeid = @id", id),
            "stored pb_data=" + await ScalarAsync($"SELECT pb_data FROM data.{Pipe} WHERE pb_pipeid = @id", id),
            "stored pb_count=" + await ScalarAsync($"SELECT pb_count FROM data.{Pipe} WHERE pb_pipeid = @id", id));
    }

    private async Task SharedAcrossStagesAsync()
    {
        await StepAsync("SharedVariablePlugin", "Create", Pipe, 10, "set|first|from-10");
        await DumpAsync("Create", Pipe, 20);
        await DumpAsync("Create", Pipe, 40, rank: 1);
        await DumpAsync("Create", Pipe, 40, mode: Async);

        await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "gld-shared" });
        await SectionAsync("create, before the asynchronous job runs", await StoredPipesAsync(), await JobsAsync());

        await DrainAsync();
        await SectionAsync("the asynchronous job", await StoredPipesAsync(), await JobsAsync());
    }

    private async Task NestedParentChainAsync()
    {
        await StepAsync("SharedVariablePlugin", "Create", Pipe, 10, "set|outer|from-parent-10");
        await StepAsync("CreateRecordPlugin", "Create", Pipe, 40, PipeLog);
        await DumpAsync("Create", PipeLog, 20);
        await DumpAsync("Create", Pipe, 40, rank: 2); // the parent's own Target, to compare with the child's ParentContext

        await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "gld-parent" });

        await SectionAsync("parent create at 40 calls the service, the child create is dumped at 20",
            await StoredPipesAsync(), await StoredLogsAsync());
    }

    private async Task RetrieveOutputEditedAsync()
    {
        var id = await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "gld-retrieve", ["pb_count"] = 9 });
        await DumpAsync("Retrieve", Pipe, 20);
        await DumpAsync("Retrieve", Pipe, 40, "pb_name=x");

        var read = await Admin.RetrieveAsync(Pipe, id, new ColumnSet("pb_name", "pb_count"));

        await SectionAsync("retrieve",
            "pb_name=" + read.GetAttributeValue<string>("pb_name"),
            "pb_count=" + read.GetAttributeValue<int>("pb_count"),
            "attributes=" + string.Join(",", read.Attributes.Keys.Order(StringComparer.Ordinal)),
            "stored pb_name=" + await ScalarAsync($"SELECT pb_name FROM data.{Pipe} WHERE pb_pipeid = @id", id));
    }

    private async Task WebApiFilterWithStepAsync()
    {
        foreach (var name in new[] { "gld-wf-a", "gld-wf-b", "gld-wf-other", "gld-wf-c" })
            await Admin.CreateAsync(new Entity(Account) { ["name"] = name });
        const string query = "accounts?$select=name&$top=50&$orderby=name" +
                             "&$filter=endswith(name,'-a') or endswith(name,'-b') or endswith(name,'-c')";
        var without = await WebApiAsync(query);

        await DumpAsync("RetrieveMultiple", Account, 20);
        await DumpAsync("RetrieveMultiple", Account, 40);
        var with = await WebApiAsync(query);

        var gld = (string r) => string.Join(",", r.Split(',').Where(n => n.StartsWith("gld-wf-", StringComparison.Ordinal)));
        gld(with).Should().Be(gld(without), "a RetrieveMultiple step does not change the rows");
        await SectionAsync("web api get with $filter and RetrieveMultiple steps at 20 and 40",
            "rows without the step=" + gld(without), "rows with the step=" + gld(with));
    }

    private async Task MergedBulkViewsAsync()
    {
        // A single Create seen by a CreateMultiple step: Targets[0] is the Create's own target.
        await DumpAsync("CreateMultiple", Account, 20, "name=gld-bulk-mutated");
        await DumpAsync("Create", Account, 20, rank: 2); // its Target is the CreateMultiple view's Targets[0] (same instance)
        var created = await Admin.CreateAsync(new Entity(Account) { ["name"] = "gld-bulk-single" });
        await SectionAsync("single create seen by a CreateMultiple step that changes Targets[0]",
            "stored name=" + await ScalarAsync("SELECT name FROM data.account WHERE accountid = @id", created));
        await PluginTestHelper.ClearAsync(fx);
        _types.Clear();
        _assemblyId = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);

        var a = await Admin.CreateAsync(new Entity(Account) { ["name"] = "gld-bulk-old-a" });
        var b = await Admin.CreateAsync(new Entity(Account) { ["name"] = "gld-bulk-old-b" });
        var pre = await DumpAsync("UpdateMultiple", Account, 20, "name=gld-bulk-upd");
        await PluginTestHelper.RegisterImageAsync(Admin, pre, 0, "pre", "name");
        var post = await DumpAsync("UpdateMultiple", Account, 40);
        await PluginTestHelper.RegisterImageAsync(Admin, post, 1, "post", "name");
        await DumpAsync("Update", Account, 20, rank: 2);

        await Admin.ExecuteAsync(new UpdateMultipleRequest
        {
            Targets = new EntityCollection([new Entity(Account, a) { ["name"] = "gld-bulk-new-a" }, new Entity(Account, b) { ["name"] = "gld-bulk-new-b" }])
            {
                EntityName = Account,
            },
        });

        await SectionAsync("UpdateMultiple with image collections, a step that changes every target and the inner Update executions",
            "stored a=" + await ScalarAsync("SELECT name FROM data.account WHERE accountid = @id", a),
            "stored b=" + await ScalarAsync("SELECT name FROM data.account WHERE accountid = @id", b));
    }

    private async Task UpsertInnerAsync()
    {
        await DumpAsync("Create", Account, 20);
        await DumpAsync("Update", Account, 20);
        var id = Guid.NewGuid();

        var created = (UpsertResponse)await Admin.ExecuteAsync(new UpsertRequest { Target = new Entity(Account, id) { ["name"] = "gld-upsert-1" } });
        await SectionAsync("upsert of a new record (inner Create)", "RecordCreated=" + created.RecordCreated,
            "stored name=" + await ScalarAsync("SELECT name FROM data.account WHERE accountid = @id", id));

        var updated = (UpsertResponse)await Admin.ExecuteAsync(new UpsertRequest { Target = new Entity(Account, id) { ["name"] = "gld-upsert-2" } });
        await SectionAsync("upsert of an existing record (inner Update)", "RecordCreated=" + updated.RecordCreated,
            "stored name=" + await ScalarAsync("SELECT name FROM data.account WHERE accountid = @id", id));
    }

    private async Task CustomApiOutputsAsync()
    {
        await CustomApiTestSolution.CreateAsync(Admin, new TestApi("xt_GoldenEcho", AllowedStepType: 2, PluginTypeName: Echo,
            Requests: [("Text", 10, false, null), ("Count", 7, true, null)],
            Responses: [("TextOut", 10), ("CountOut", 7), ("Context", 10)]));
        await DumpAsync("xt_GoldenEcho", null, 20);
        await DumpAsync("xt_GoldenEcho", null, 40);

        var response = await Admin.ExecuteAsync(new OrganizationRequest("xt_GoldenEcho") { ["Text"] = "abc", ["Count"] = 5 });

        await SectionAsync("custom api with the echo plug-in as main operation",
            [$"ResponseName={response.ResponseName}", .. response.Results.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}={r.Value}")]);
    }

    private async Task SetParentAfterNestedAsync()
    {
        await StepAsync("CreateRecordPlugin", "Create", Pipe, 40, PipeLog);
        await DumpAsync("Create", Pipe, 40, rank: 2);
        await StepAsync("SharedVariablePlugin", "Create", PipeLog, 20, "setparent|fromnested|set-by-child");

        await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "gld-setparent" });

        await SectionAsync("a nested step sets a shared variable in its ParentContext, the parent's later step at 40 dumps",
            await StoredPipesAsync(), await StoredLogsAsync());
    }

    private async Task RunAsUserScenarioAsync()
    {
        var user = await RunAsUserAsync();
        // The dump steps run as another user: UserId is that user, InitiatingUserId the admin who called.
        await DumpAsync("Create", Pipe, 20, runAs: user);
        await StepAsync("CreateRecordPlugin", "Create", Pipe, 40, PipeLog, runAs: user);
        await DumpAsync("Create", PipeLog, 20);
        await DumpAsync("Create", PipeLog, 40, runAs: user);

        await Admin.CreateAsync(new Entity(Pipe) { ["pb_name"] = "gld-runas" });

        await SectionAsync("steps registered to run as another user, with a nested call",
            await StoredPipesAsync(), await StoredLogsAsync(),
            "pb_pipelog createdby=" + await ScalarAsync($"SELECT MIN(createdby) FROM data.{PipeLog}"),
            "pb_pipelog owner=" + await ScalarAsync($"SELECT MIN(ownerid) FROM data.{PipeLog}"));
    }

    private async Task InPlaceMutationsAsync()
    {
        // At stage 20, in this order: remove an attribute, change an OptionSetValue in place, replace the Target instance.
        const string task = "task";
        await MutateAsync("Create", task, 20, "description=-", 1);
        await MutateAsync("Create", task, 20, "prioritycode=@option:2", 2);
        await DumpAsync("Create", task, 20, rank: 3);
        await MutateAsync("Create", task, 20, "Target=replace:subject", 4);
        await DumpAsync("Create", task, 20, rank: 5);
        await DumpAsync("Create", task, 40);

        var target = new Entity(task) { ["subject"] = "gld-mutate", ["description"] = "to be removed", ["prioritycode"] = new OptionSetValue(0) };
        var id = await Admin.CreateAsync(target);
        var stored = await Admin.RetrieveAsync(task, id, new ColumnSet("subject", "description", "prioritycode"));
        await ExecAsync($"DELETE FROM data.task WHERE activityid = '{id}';");

        await SectionAsync("create with the attribute removed, the option changed in place and the Target replaced at 20",
            "stored subject=" + stored.GetAttributeValue<string>("subject"),
            "stored description=" + (stored.Contains("description") ? stored.GetAttributeValue<string>("description") : "<absent>"),
            "stored prioritycode=" + stored.GetAttributeValue<OptionSetValue>("prioritycode")?.Value,
            "caller's entity has description=" + target.Contains("description"),
            "caller's entity prioritycode=" + target.GetAttributeValue<OptionSetValue>("prioritycode").Value,
            "caller's entity subject=" + target.GetAttributeValue<string>("subject"));
    }
}
