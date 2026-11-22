using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// #76, ruling B10: <c>Upsert</c> on a standard table (<c>account</c>) through the SDK. It is not an event there: its stage 30
/// locks the id, then runs an inner <c>Create</c> or <c>Update</c> through the pipeline at the same depth, with the Upsert's
/// context as <c>ParentContext</c>, in the Upsert's transaction.
/// </summary>
[Collection("xrmd")]
public sealed class StandardUpsertTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Account = "account";
    private const string PipeLog = TestEntities.PipeLog;
    private const string Plugins = "Xrmd.TestPlugins.";

    private readonly string _tag = "SU" + Guid.NewGuid().ToString("N")[..10];
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private Guid _assemblyId;
    private readonly Dictionary<string, Guid> _types = new();

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync($"DELETE FROM data.{PipeLog}; DELETE FROM data.{TestEntities.PipeLog2};");
        _assemblyId = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<Guid> StepAsync(string plugin, string message, string? entity, int stage, string? config = null)
    {
        if (!_types.TryGetValue(plugin, out var typeId))
            _types[plugin] = typeId = await PluginTestHelper.RegisterTypeAsync(Admin, _assemblyId, Plugins + plugin);
        return await PluginTestHelper.RegisterStepAsync(Admin, typeId, message, entity, stage, config: config);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<Entity?> ReadAsync(Guid id)
    {
        var query = new QueryExpression(Account) { ColumnSet = new ColumnSet("name", "emailaddress1") };
        query.Criteria.AddCondition("accountid", ConditionOperator.Equal, id);
        return (await Admin.RetrieveMultipleAsync(query)).Entities.SingleOrDefault();
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

    private static async Task<UpsertResponse> UpsertAsync(OrganizationService service, Entity target) =>
        (UpsertResponse)await service.ExecuteAsync(new UpsertRequest { Target = target });

    // ---- creates and updates ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Upsert_creates_with_given_id()
    {
        var id = Guid.NewGuid();

        var response = await UpsertAsync(Admin, new Entity(Account, id) { ["name"] = _tag + "-given" });

        response.RecordCreated.Should().BeTrue();
        response.Target.Should().BeEquivalentTo(new EntityReference(Account, id), o => o.Including(r => r.LogicalName).Including(r => r.Id));
        (await ReadAsync(id))!.GetAttributeValue<string>("name").Should().Be(_tag + "-given");
    }

    [Fact]
    public async Task Upsert_with_primary_key_attribute_creates_with_that_id()
    {
        var id = Guid.NewGuid();

        var response = await UpsertAsync(Admin, new Entity(Account) { ["accountid"] = id, ["name"] = _tag + "-pk" });

        response.RecordCreated.Should().BeTrue();
        response.Target.Id.Should().Be(id);
        (await ReadAsync(id))!.GetAttributeValue<string>("name").Should().Be(_tag + "-pk");
    }

    [Fact]
    public async Task Upsert_without_id_creates_new()
    {
        var response = await UpsertAsync(Admin, new Entity(Account) { ["name"] = _tag + "-new" });

        response.RecordCreated.Should().BeTrue();
        response.Target.LogicalName.Should().Be(Account);
        response.Target.Id.Should().NotBeEmpty();
        (await ReadAsync(response.Target.Id))!.GetAttributeValue<string>("name").Should().Be(_tag + "-new");
    }

    [Fact]
    public async Task Upsert_updates_existing()
    {
        var id = await Admin.CreateAsync(new Entity(Account) { ["name"] = _tag + "-old", ["emailaddress1"] = "keep@example.com" });

        var response = await UpsertAsync(Admin, new Entity(Account, id) { ["name"] = _tag + "-upd" });

        response.RecordCreated.Should().BeFalse();
        response.Target.Id.Should().Be(id);
        var row = (await ReadAsync(id))!;
        row.GetAttributeValue<string>("name").Should().Be(_tag + "-upd");
        // Unlike an elastic Upsert (ruling E6), a column the target does not carry keeps its value.
        row.GetAttributeValue<string>("emailaddress1").Should().Be("keep@example.com");
    }

    // ---- steps --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Upsert_fires_create_or_update_steps_not_upsert()
    {
        await StepAsync("ContextRecorderPlugin", "Create", Account, 20, PipeLog);
        await StepAsync("ContextRecorderPlugin", "Update", Account, 40, PipeLog);
        // A filter-less Upsert step (still accepted) never fires on a standard table: Upsert is not an event there.
        await StepAsync("ThrowPlugin", "Upsert", null, 20, "invalid:an Upsert step fired");

        var created = await UpsertAsync(Admin, new Entity(Account) { ["name"] = _tag + "-steps" });
        var log = await LogAsync();
        log.Should().ContainSingle();
        log[0].Name.Should().Be("Create|20|0|1");
        log[0].Data.Should().Contain("parent=Upsert");
        log[0].Data.Should().Contain("intx=True");
        log[0].Data.Should().Contain("parentstage=30"); // the outer Upsert's stage, not the base context's 0

        await ExecAsync($"DELETE FROM data.{PipeLog};");
        await UpsertAsync(Admin, new Entity(Account, created.Target.Id) { ["name"] = _tag + "-steps2" });
        log = await LogAsync();
        log.Should().ContainSingle();
        log[0].Name.Should().Be("Update|40|0|1");
        log[0].Data.Should().Contain("parent=Upsert");
        // The Update target has no primary key attribute.
        log[0].Data.Should().Contain("target=name");

        // Task 1: no step can be registered on Upsert of a standard table: it has no filter row (404); a forged one is refused
        // with a 400 (RegistrationValidationTests).
        await FluentActions.Invoking(() => StepAsync("ContextRecorderPlugin", "Upsert", Account, 20, PipeLog))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.HttpStatus == 404 && e.ErrorCode == ErrorCodes.ObjectDoesNotExist && e.Message.Contains("sdkmessagefilter"));
    }

    [Fact]
    public async Task Single_steps_keep_depth_one()
    {
        // A plug-in guarding on Depth > 1 must still run its single-record logic for an Upsert (Review Focus 2, Upsert half).
        await StepAsync("ContextRecorderPlugin", "Create", Account, 40, PipeLog);
        await StepAsync("ContextRecorderPlugin", "Update", Account, 20, PipeLog);

        var created = await UpsertAsync(Admin, new Entity(Account) { ["name"] = _tag + "-depth" });
        await UpsertAsync(Admin, new Entity(Account, created.Target.Id) { ["name"] = _tag + "-depth2" });

        (await LogAsync()).Select(r => r.Name).Should().Equal("Create|40|0|1", "Update|20|0|1");
    }

    [Fact]
    public async Task Upsert_from_a_plugin_runs_the_inner_create_one_level_down()
    {
        // B12: Upsert from a plug-in is allowed. The plug-in's call is one level deeper; its inner Create keeps that depth.
        var id = Guid.NewGuid();
        await StepAsync("UpsertRecordPlugin", "Create", TestEntities.Pipe, 40, $"{TestEntities.PipeLog2}|{id}");
        await StepAsync("ContextRecorderPlugin", "Create", TestEntities.PipeLog2, 20, PipeLog);

        await Admin.CreateAsync(new Entity(TestEntities.Pipe) { ["pb_name"] = _tag + "-pipe" });

        var log = await LogAsync();
        log.Should().ContainSingle();
        log[0].Name.Should().Be("Create|20|0|2");
        log[0].Data.Should().Contain("parent=Upsert");
        (await CountAsync($"SELECT COUNT(*) FROM data.{TestEntities.PipeLog2} WHERE {TestEntities.PipeLog2}id = '{id}'")).Should().Be(1);
    }

    [Fact]
    public async Task Upsert_from_a_plugin_that_fails_rolls_back_the_caller()
    {
        var id = Guid.NewGuid();
        await StepAsync("UpsertRecordPlugin", "Create", TestEntities.Pipe, 40, $"{TestEntities.PipeLog2}|{id}");
        await StepAsync("ThrowPlugin", "Create", TestEntities.PipeLog2, 40, "invalid:inner create refused");
        var name = _tag + "-pipe-failed";

        await FluentActions.Invoking(() => Admin.CreateAsync(new Entity(TestEntities.Pipe) { ["pb_name"] = name }))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.Message.Contains("inner create refused"));

        (await CountAsync($"SELECT COUNT(*) FROM data.{TestEntities.Pipe} WHERE pb_name = N'{name}'")).Should().Be(0);
        (await CountAsync($"SELECT COUNT(*) FROM data.{TestEntities.PipeLog2} WHERE {TestEntities.PipeLog2}id = '{id}'")).Should().Be(0);
    }

    // ---- validation and parameters ------------------------------------------------------------------------------------

    [Fact]
    public async Task Upsert_validates_each_branch_with_its_own_rules()
    {
        // contact.fullname is the server's: a Create refuses any value, an Update accepts the stored one (dropped as a no-op).
        var id = await Admin.CreateAsync(new Entity("contact") { ["firstname"] = "Ada", ["lastname"] = _tag });
        var stored = (await Admin.RetrieveAsync("contact", id, new ColumnSet("fullname"))).GetAttributeValue<string>("fullname");
        stored.Should().NotBeNullOrEmpty();

        var updated = await UpsertAsync(Admin, new Entity("contact", id) { ["fullname"] = stored, ["emailaddress1"] = "ada@example.com" });

        updated.RecordCreated.Should().BeFalse();
        (await Admin.RetrieveAsync("contact", id, new ColumnSet("emailaddress1"))).GetAttributeValue<string>("emailaddress1").Should().Be("ada@example.com");

        // The same value on a missing record takes the Create branch, which refuses it.
        await FluentActions.Invoking(() => UpsertAsync(Admin, new Entity("contact", Guid.NewGuid()) { ["fullname"] = stored, ["lastname"] = _tag }))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.HttpStatus == 400 && e.Message.Contains("fullname"));
    }

    // ---- refusals and failures --------------------------------------------------------------------------------------

    [Fact]
    public async Task Upsert_with_key_attributes_on_standard_is_refused()
    {
        var target = new Entity(Account, new KeyAttributeCollection { { "accountnumber", "A-1" } }) { ["name"] = _tag + "-key" };

        await FluentActions.Invoking(() => Admin.ExecuteAsync(new UpsertRequest { Target = target }))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.HttpStatus == 400 && e.ErrorCode == ErrorCodes.InvalidArgument
                        && e.Message == "Key attributes (accountnumber) are not supported for entity 'account': xrmverse supports alternate keys only for elastic tables.");
    }

    [Fact]
    public async Task Upsert_failure_rolls_back()
    {
        var id = await Admin.CreateAsync(new Entity(Account) { ["name"] = _tag + "-keep" });
        await StepAsync("ThrowPlugin", "Update", Account, 40, "invalid:no update today");

        await FluentActions.Invoking(() => UpsertAsync(Admin, new Entity(Account, id) { ["name"] = _tag + "-lost" }))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.HttpStatus == 400 && e.Message == "no update today");

        (await ReadAsync(id))!.GetAttributeValue<string>("name").Should().Be(_tag + "-keep");
    }

    [Fact]
    public async Task Upsert_create_failure_rolls_back()
    {
        await StepAsync("ThrowPlugin", "Create", Account, 40, "invalid:no create today");
        var id = Guid.NewGuid();

        await FluentActions.Invoking(() => UpsertAsync(Admin, new Entity(Account, id) { ["name"] = _tag + "-gone" }))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.HttpStatus == 400 && e.Message == "no create today");

        (await ReadAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_upserts_of_one_id()
    {
        // Review Focus 5: one creates, the other updates; neither fails with DuplicateRecord.
        for (var round = 0; round < 3; round++)
        {
            var id = Guid.NewGuid();
            var results = await Task.WhenAll(
                Task.Run(() => UpsertAsync(Admin, new Entity(Account, id) { ["name"] = _tag + "-a" })),
                Task.Run(() => UpsertAsync(Admin, new Entity(Account, id) { ["name"] = _tag + "-b" })));

            results.Count(r => r.RecordCreated).Should().Be(1);
            results.Should().OnlyContain(r => r.Target.Id == id);
            (await ReadAsync(id)).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Upsert_respects_privileges()
    {
        var root = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, root, oid);
        await GrantAsync(fx, user, false, ("prvReadAccount", DepthMask.Global), ("prvWriteAccount", DepthMask.Global));
        var existing = await Admin.CreateAsync(new Entity(Account) { ["name"] = _tag + "-priv" });
        var service = ServiceFor(fx, oid);

        // The row exists: the inner Update needs Write only.
        (await UpsertAsync(service, new Entity(Account, existing) { ["name"] = _tag + "-priv2" })).RecordCreated.Should().BeFalse();

        // The row is missing: the inner Create demands Create.
        var missing = Guid.NewGuid();
        await FluentActions.Invoking(() => UpsertAsync(service, new Entity(Account, missing) { ["name"] = _tag + "-denied" }))
            .Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.HttpStatus == 403 && e.ErrorCode == ErrorCodes.PrivilegeDenied && e.Message.Contains("prvCreateAccount"));
        (await ReadAsync(missing)).Should().BeNull();
    }
}
