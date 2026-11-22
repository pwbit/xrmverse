using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// #76, Task 4: the steps of <c>CreateMultiple</c>/<c>UpdateMultiple</c> on a standard table (<c>account</c>). They fire on a
/// plain Create or Update with a multiple view of the context, merged by rank with the single-record steps (ruling B5); their
/// images come in index-aligned collections (B6); an UpdateMultiple filter matches when any target carries it (B7).
/// </summary>
[Collection("xrmd")]
public sealed class PipelineMergeTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Account = "account";
    private const string PipeLog = TestEntities.PipeLog;
    private const string Plugins = "Xrmd.TestPlugins.";

    private readonly string _tag = "PM" + Guid.NewGuid().ToString("N")[..10];
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private Guid _assemblyId;
    private readonly Dictionary<string, Guid> _types = new();

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{PipeLog};");
        _assemblyId = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<Guid> StepAsync(string plugin, string message, int stage, string? config = null, int rank = 1,
        string? filteringAttributes = null)
    {
        if (!_types.TryGetValue(plugin, out var typeId))
            _types[plugin] = typeId = await PluginTestHelper.RegisterTypeAsync(Admin, _assemblyId, Plugins + plugin);
        return await PluginTestHelper.RegisterStepAsync(Admin, typeId, message, Account, stage, rank: rank, config: config,
            filteringAttributes: filteringAttributes);
    }

    private Task<Guid> RecorderAsync(string message, int stage, int rank = 1, string? filteringAttributes = null, int mode = 0)
    {
        return mode == 0 ? StepAsync("ContextRecorderPlugin", message, stage, PipeLog, rank, filteringAttributes) : AsyncRecorderAsync();

        async Task<Guid> AsyncRecorderAsync()
        {
            if (!_types.TryGetValue("ContextRecorderPlugin", out var typeId))
                _types["ContextRecorderPlugin"] = typeId = await PluginTestHelper.RegisterTypeAsync(Admin, _assemblyId, Plugins + "ContextRecorderPlugin");
            return await PluginTestHelper.RegisterStepAsync(Admin, typeId, message, Account, stage, mode: mode, rank: rank, config: PipeLog);
        }
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The <c>pb_name</c> and <c>pb_data</c> lines of the rows the recorder plug-in wrote, in creation order.</summary>
    private async Task<List<(string Name, string[] Data)>> LogAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data FROM data.{PipeLog} ORDER BY createdon");
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, string[])>();
        while (await rd.ReadAsync()) rows.Add((rd.GetString(0), rd.IsDBNull(1) ? [] : rd.GetString(1).Split('\n')));
        return rows;
    }

    private async Task<string?> NameAsync(Guid id)
    {
        var query = new QueryExpression(Account) { ColumnSet = new ColumnSet("name") };
        query.Criteria.AddCondition("accountid", ConditionOperator.Equal, id);
        return (await Admin.RetrieveMultipleAsync(query)).Entities.Single().GetAttributeValue<string>("name");
    }

    private Entity NewAccount(string suffix) => new(Account) { ["name"] = _tag + suffix };

    private static EntityCollection Targets(params Entity[] targets) => new(targets.ToList()) { EntityName = Account };

    // ---- single to bulk ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Multiple_step_fires_on_single_create()
    {
        await RecorderAsync("CreateMultiple", 10);
        await RecorderAsync("CreateMultiple", 20);
        await RecorderAsync("Create", 20, rank: 2);

        await Admin.CreateAsync(NewAccount("-a"));

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal("CreateMultiple|10|0|1", "CreateMultiple|20|0|1", "Create|20|0|1");
        log[1].Data.Should().Contain(["targets=1", "target=", "primary=account", "parent=", "intx=True", "precollections=1"]);
        // SharedVariables are shared: the single-record step sees what the multiple steps before it set.
        log[2].Data.Should().Contain(["shared=recorder", "targets="]);
    }

    [Fact]
    public async Task Multiple_step_fires_on_single_update()
    {
        var id = await Admin.CreateAsync(NewAccount("-old"));
        var step = await RecorderAsync("UpdateMultiple", 20);
        await PluginTestHelper.RegisterImageAsync(Admin, step, 0, "img", "name");

        await Admin.UpdateAsync(new Entity(Account, id) { ["name"] = _tag + "-new" });

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal("UpdateMultiple|20|0|1");
        // One record; its pre image only in the (one-element) collection, as for a bulk execution (ruling B6).
        log[0].Data.Should().Contain(["targets=1", "target=", "pre=", "precollections=1", $"preimagenames=img:{_tag}-old"]);
    }

    [Fact]
    public async Task Multiple_step_change_on_single_create_is_saved()
    {
        // Review Focus 3: the item of Targets is the Create's own target.
        await StepAsync("SetAttributePlugin", "CreateMultiple", 20, $"name={_tag}-changed");

        var id = await Admin.CreateAsync(NewAccount("-sent"));

        (await NameAsync(id)).Should().Be(_tag + "-changed");
    }

    [Fact]
    public async Task Steps_of_both_messages_run_by_rank()
    {
        await RecorderAsync("Create", 20, rank: 2);
        await RecorderAsync("CreateMultiple", 20, rank: 1);

        await Admin.CreateAsync(NewAccount("-r"));

        (await LogAsync()).Select(r => r.Name).Should().Equal("CreateMultiple|20|0|1", "Create|20|0|1");
    }

    [Fact]
    public async Task Inner_create_does_not_fire_multiple_steps_twice()
    {
        await RecorderAsync("CreateMultiple", 20);

        await Admin.ExecuteAsync(new CreateMultipleRequest { Targets = Targets(NewAccount("-0"), NewAccount("-1")) });

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal("CreateMultiple|20|0|1");
        log[0].Data.Should().Contain("targets=2");
    }

    [Fact]
    public async Task Multiple_view_post_has_ids()
    {
        var step = await RecorderAsync("CreateMultiple", 40);
        await PluginTestHelper.RegisterImageAsync(Admin, step, 1, "img", "name");

        await Admin.CreateAsync(NewAccount("-p"));

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal("CreateMultiple|40|0|1");
        log[0].Data.Should().Contain(["ids=1", "output=Ids", "targets=1", "post=", "postimagecollections=1",
            $"postimagenames=img:{_tag}-p"]);
    }

    // ---- bulk images and filters ------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateMultiple_images_align_with_targets()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++) ids.Add(await Admin.CreateAsync(NewAccount($"-old{i}")));
        var pre = await RecorderAsync("UpdateMultiple", 20);
        await PluginTestHelper.RegisterImageAsync(Admin, pre, 0, "img", "name");
        var post = await RecorderAsync("UpdateMultiple", 40);
        await PluginTestHelper.RegisterImageAsync(Admin, post, 1, "img", "name");

        await Admin.ExecuteAsync(new UpdateMultipleRequest
        {
            Targets = Targets([.. ids.Select((id, i) => new Entity(Account, id) { ["name"] = $"{_tag}-new{i}" })]),
        });

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal("UpdateMultiple|20|0|1", "UpdateMultiple|40|0|1");
        log[0].Data.Should().Contain(["targets=3", "pre=", "precollections=3",
            $"preimagenames=img:{_tag}-old0;img:{_tag}-old1;img:{_tag}-old2"]);
        log[1].Data.Should().Contain(["post=", "postimagecollections=3",
            $"postimagenames=img:{_tag}-new0;img:{_tag}-new1;img:{_tag}-new2"]);
    }

    [Fact]
    public async Task CreateMultiple_post_images_align_with_targets()
    {
        var post = await RecorderAsync("CreateMultiple", 40);
        await PluginTestHelper.RegisterImageAsync(Admin, post, 1, "img", "name");

        await Admin.ExecuteAsync(new CreateMultipleRequest { Targets = Targets(NewAccount("-0"), NewAccount("-1")) });

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal("CreateMultiple|40|0|1");
        log[0].Data.Should().Contain(["postimagecollections=2", $"postimagenames=img:{_tag}-0;img:{_tag}-1", "precollections=2",
            "preimagenames=;"]);
    }

    [Fact]
    public async Task UpdateMultiple_filter_matches_any_target()
    {
        // Review Focus 4: one of two targets carries the filtered column; the step fires once and sees both.
        var a = await Admin.CreateAsync(NewAccount("-a"));
        var b = await Admin.CreateAsync(NewAccount("-b"));
        await RecorderAsync("UpdateMultiple", 20, filteringAttributes: "name");

        await Admin.ExecuteAsync(new UpdateMultipleRequest
        {
            Targets = Targets(new Entity(Account, a) { ["emailaddress1"] = "a@example.com" }, new Entity(Account, b) { ["name"] = _tag + "-b2" }),
        });

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal("UpdateMultiple|20|0|1");
        log[0].Data.Should().Contain("targets=2");
    }

    [Fact]
    public async Task UpdateMultiple_filter_matches_none()
    {
        var a = await Admin.CreateAsync(NewAccount("-a"));
        var b = await Admin.CreateAsync(NewAccount("-b"));
        await RecorderAsync("UpdateMultiple", 20, filteringAttributes: "name");

        await Admin.ExecuteAsync(new UpdateMultipleRequest
        {
            Targets = Targets(new Entity(Account, a) { ["emailaddress1"] = "a@example.com" }, new Entity(Account, b) { ["emailaddress1"] = "b@example.com" }),
        });

        (await LogAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateMultiple_filter_on_single_update_needs_the_single_target()
    {
        var id = await Admin.CreateAsync(NewAccount("-a"));
        await RecorderAsync("UpdateMultiple", 20, filteringAttributes: "name");

        await Admin.UpdateAsync(new Entity(Account, id) { ["emailaddress1"] = "a@example.com" });
        (await LogAsync()).Should().BeEmpty();

        await Admin.UpdateAsync(new Entity(Account, id) { ["name"] = _tag + "-a2" });
        (await LogAsync()).Select(r => r.Name).Should().Equal("UpdateMultiple|20|0|1");
    }

    // ---- fix round 1: Upsert inners merge, coverage ------------------------------------------------------------------

    [Fact]
    public async Task Upsert_that_creates_fires_the_CreateMultiple_step_once()
    {
        await RecorderAsync("CreateMultiple", 20);

        await Admin.ExecuteAsync(new UpsertRequest { Target = new Entity(Account, Guid.NewGuid()) { ["name"] = _tag + "-u" } });

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal("CreateMultiple|20|0|1");
        log[0].Data.Should().Contain(["targets=1", "parent=Upsert"]);
    }

    [Fact]
    public async Task Upsert_that_updates_fires_the_UpdateMultiple_step_once()
    {
        var id = await Admin.CreateAsync(NewAccount("-old"));
        await RecorderAsync("UpdateMultiple", 20);

        await Admin.ExecuteAsync(new UpsertRequest { Target = new Entity(Account, id) { ["name"] = _tag + "-new" } });

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal("UpdateMultiple|20|0|1");
        log[0].Data.Should().Contain(["targets=1", "parent=Upsert"]);
    }

    [Fact]
    public async Task UpsertMultiple_fires_each_merged_step_once()
    {
        var id = await Admin.CreateAsync(NewAccount("-old"));
        await RecorderAsync("CreateMultiple", 20);
        await RecorderAsync("UpdateMultiple", 20);

        await Admin.ExecuteAsync(new UpsertMultipleRequest
        {
            Targets = Targets(new Entity(Account, Guid.NewGuid()) { ["name"] = _tag + "-c" }, new Entity(Account, id) { ["name"] = _tag + "-u" }),
        });

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal("CreateMultiple|20|0|1", "UpdateMultiple|20|0|1");
        log.Should().OnlyContain(r => r.Data.Contains("targets=1") && r.Data.Contains("parent=Upsert"));
    }

    [Fact]
    public async Task CreateMultiple_fires_its_step_once()
    {
        await RecorderAsync("CreateMultiple", 20);
        await RecorderAsync("CreateMultiple", 40);

        await Admin.ExecuteAsync(new CreateMultipleRequest { Targets = Targets(NewAccount("-0"), NewAccount("-1"), NewAccount("-2")) });

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal("CreateMultiple|20|0|1", "CreateMultiple|40|0|1");
        log.Should().OnlyContain(r => r.Data.Contains("targets=3"));
    }

    [Fact]
    public async Task Inner_update_does_not_fire_multiple_steps_twice()
    {
        var a = await Admin.CreateAsync(NewAccount("-a"));
        var b = await Admin.CreateAsync(NewAccount("-b"));
        await RecorderAsync("UpdateMultiple", 20);
        await RecorderAsync("UpdateMultiple", 40);

        await Admin.ExecuteAsync(new UpdateMultipleRequest
        {
            Targets = Targets(new Entity(Account, a) { ["name"] = _tag + "-a2" }, new Entity(Account, b) { ["name"] = _tag + "-b2" }),
        });

        var log = await LogAsync();
        log.Select(r => r.Name).Should().Equal("UpdateMultiple|20|0|1", "UpdateMultiple|40|0|1");
        log.Should().OnlyContain(r => r.Data.Contains("targets=2"));
    }

    [Fact]
    public async Task Steps_of_the_same_rank_run_by_step_id()
    {
        var single = await RecorderAsync("Create", 20, rank: 1);
        var multiple = await RecorderAsync("CreateMultiple", 20, rank: 1);

        await Admin.CreateAsync(NewAccount("-t"));

        var expected = new[] { (Id: single, Name: "Create|20|0|1"), (Id: multiple, Name: "CreateMultiple|20|0|1") }
            .OrderBy(s => s.Id).Select(s => s.Name);
        (await LogAsync()).Select(r => r.Name).Should().Equal(expected);
    }

    [Fact]
    public async Task Async_merged_step_is_queued_once_and_runs()
    {
        await ExecAsync("DELETE FROM data.asyncoperation;");
        try
        {
            var step = await RecorderAsync("CreateMultiple", 40, mode: 1);

            await Admin.CreateAsync(NewAccount("-async"));

            await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
            {
                await using var cmd = TestSql.Command(conn, "SELECT COUNT(*) FROM data.asyncoperation WHERE owningextensionid = @step AND messagename = 'CreateMultiple'");
                cmd.Parameters.AddWithValue("@step", step);
                (Convert.ToInt32(await cmd.ExecuteScalarAsync())).Should().Be(1);
            }
            var worker = new AsyncOperationWorker(fx.App.Services, fx.App.Services.GetRequiredService<AsyncOperationQueue>(),
                Options.Create(new PipelineOptions { AsyncPollInterval = TimeSpan.FromMilliseconds(100), AsyncRetryDelay = TimeSpan.FromMilliseconds(100) }));
            (await worker.RunOnceAsync(default)).Should().BeTrue();

            var log = await LogAsync();
            log.Select(r => r.Name).Should().Equal("CreateMultiple|40|1|1");
            log[0].Data.Should().Contain(["targets=1", "ids=1", "target="]);
        }
        finally
        {
            await ExecAsync("DELETE FROM data.asyncoperation;");
        }
    }
}
