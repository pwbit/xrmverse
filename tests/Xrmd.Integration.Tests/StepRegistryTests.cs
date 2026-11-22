using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Query;
using Xrmd.Pipeline.Registration;
using Xrmd.Query.Sql;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public sealed class StepRegistryTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Project = TestEntities.Project;
    private IDbConnectionFactory _factory = null!;
    private OrganizationService _svc = null!;
    private StepRegistry _registry = null!;
    private Guid _typeId;
    private Guid _assemblyId;

    public async Task InitializeAsync()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        _factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        await TestEntities.EnsureProjectAsync(store);
        await fx.App.Services.GetRequiredService<Xrmd.Metadata.SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        _svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        _registry = new StepRegistry(_factory, fx.App.Services.GetRequiredService<Xrmd.Pipeline.Webhooks.WebhookSecrets>());
        _assemblyId = await PluginTestHelper.RegisterAssemblyAsync(_svc, PluginTestHelper.TestPluginBytes);
        _typeId = await PluginTestHelper.RegisterTypeAsync(_svc, _assemblyId, "Xrmd.TestPlugins.SetAttributePlugin");
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Returns_enabled_steps_by_message_entity_stage_mode_ordered_by_rank()
    {
        var r3 = await PluginTestHelper.RegisterStepAsync(_svc, _typeId, "Create", Project, 20, rank: 3);
        var r1 = await PluginTestHelper.RegisterStepAsync(_svc, _typeId, "Create", Project, 20, rank: 1);
        var r2 = await PluginTestHelper.RegisterStepAsync(_svc, _typeId, "Create", Project, 20, rank: 2);
        await PluginTestHelper.RegisterStepAsync(_svc, _typeId, "Update", Project, 20);
        await PluginTestHelper.RegisterStepAsync(_svc, _typeId, "Create", Project, 40);

        var steps = await _registry.GetStepsAsync("CREATE", Project, 20, 0, default);

        steps.Select(s => s.Id).Should().Equal(r1, r2, r3);
        steps[0].Message.Should().Be("Create");
        steps[0].PrimaryEntity.Should().Be(Project);
        steps[0].TypeName.Should().Be("Xrmd.TestPlugins.SetAttributePlugin");
        steps[0].AssemblyId.Should().Be(_assemblyId);
        steps[0].AssemblyVersion.Should().BeGreaterThan(0);
        (await _registry.GetAssemblyContentAsync(_assemblyId, default)).Should().Equal(PluginTestHelper.TestPluginBytes);
    }

    [Fact]
    public async Task Disabled_step_is_excluded()
    {
        var id = await PluginTestHelper.RegisterStepAsync(_svc, _typeId, "Create", Project, 20);
        await ExecAsync($"UPDATE data.sdkmessageprocessingstep SET statecode = 1, statuscode = 2 WHERE sdkmessageprocessingstepid = '{id}'");

        (await _registry.GetStepsAsync("Create", Project, 20, 0, default)).Should().BeEmpty();
        (await _registry.GetStepAsync(id, default)).Should().NotBeNull();
        (await _registry.GetStepAsync(Guid.NewGuid(), default)).Should().BeNull();
    }

    [Fact]
    public async Task Step_without_filter_applies_to_every_entity()
    {
        var id = await PluginTestHelper.RegisterStepAsync(_svc, _typeId, "Create", null, 20);

        (await _registry.GetStepsAsync("Create", Project, 20, 0, default)).Select(s => s.Id).Should().Equal(id);
        (await _registry.GetStepsAsync("Create", "account", 20, 0, default)).Select(s => s.Id).Should().Equal(id);
    }

    [Fact]
    public async Task Images_and_filtering_attributes_are_parsed()
    {
        var id = await PluginTestHelper.RegisterStepAsync(_svc, _typeId, "Update", Project, 40, filteringAttributes: " pb_name , PB_Count",
            config: "cfg");
        await PluginTestHelper.RegisterImageAsync(_svc, id, 0, "pre", "pb_name,pb_count");
        await PluginTestHelper.RegisterImageAsync(_svc, id, 1, "post");

        var step = (await _registry.GetStepsAsync("Update", Project, 40, 0, default)).Single();

        step.FilteringAttributes.Should().Equal("pb_name", "pb_count");
        step.UnsecureConfiguration.Should().Be("cfg");
        step.SecureConfiguration.Should().BeNull();
        step.Images.Should().HaveCount(2);
        var pre = step.Images.Single(i => i.Alias == "pre");
        pre.ImageType.Should().Be(0);
        pre.Attributes.Should().Equal("pb_name", "pb_count");
        pre.MessagePropertyName.Should().Be("Target");
        step.Images.Single(i => i.Alias == "post").Attributes.Should().BeNull();
    }

    [Fact]
    public async Task Invalidate_reloads()
    {
        await PluginTestHelper.RegisterStepAsync(_svc, _typeId, "Create", Project, 20, rank: 1);
        (await _registry.GetStepsAsync("Create", Project, 20, 0, default)).Should().HaveCount(1);

        await PluginTestHelper.RegisterStepAsync(_svc, _typeId, "Create", Project, 20, rank: 2);
        (await _registry.GetStepsAsync("Create", Project, 20, 0, default)).Should().HaveCount(1);

        _registry.Invalidate();
        (await _registry.GetStepsAsync("Create", Project, 20, 0, default)).Should().HaveCount(2);
    }

    private sealed class InvalidatingFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public Xrmd.Abstractions.Storage.ISqlDialect Dialect => inner.Dialect;

        public Type ConnectionType => inner.ConnectionType;

        public StepRegistry? Registry;
        public int Opens;
        public Task<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref Opens) == 1) Registry!.Invalidate(); // Invalidate while the first load is running
            return inner.OpenAsync(ct);
        }
    }

    [Fact]
    public async Task Invalidate_during_a_load_is_not_lost()
    {
        var factory = new InvalidatingFactory(_factory);
        var registry = factory.Registry = new StepRegistry(factory, fx.App.Services.GetRequiredService<Xrmd.Pipeline.Webhooks.WebhookSecrets>());
        await PluginTestHelper.RegisterStepAsync(_svc, _typeId, "Create", Project, 20, rank: 1);

        (await registry.GetStepsAsync("Create", Project, 20, 0, default)).Should().HaveCount(1);
        var opensAfterFirst = factory.Opens;
        await PluginTestHelper.RegisterStepAsync(_svc, _typeId, "Create", Project, 20, rank: 2);

        // The snapshot loaded before the Invalidate is stale: the next read must reload and see the new step.
        (await registry.GetStepsAsync("Create", Project, 20, 0, default)).Should().HaveCount(2);
        factory.Opens.Should().BeGreaterThan(opensAfterFirst);
    }
}
