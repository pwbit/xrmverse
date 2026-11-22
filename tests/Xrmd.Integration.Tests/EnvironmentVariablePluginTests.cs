using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// #167, task 4 (needs a database): <c>IEnvironmentVariableService</c> handed to plug-ins (ruling E5). The test plug-in
/// <c>EnvironmentVariablePlugin</c> writes what the service returns into <c>pb_data</c> of the <c>pb_pipelog</c> being created.
/// </summary>
[Collection("xrmd")]
public sealed class EnvironmentVariablePluginTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string PipeLog = TestEntities.PipeLog;

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    private static string NewName() => "xv_Plug" + Guid.NewGuid().ToString("N")[..10];

    private Task<Guid> CreateDefinitionAsync(string schemaName, string? defaultValue = null, VariableType type = VariableType.String)
    {
        var definition = new Entity(EnvironmentVariableSchema.Definition)
        {
            [EnvironmentVariableSchema.SchemaName] = schemaName,
            [EnvironmentVariableSchema.DisplayName] = schemaName,
            [EnvironmentVariableSchema.Type] = new OptionSetValue((int)type),
        };
        if (defaultValue is not null) definition[EnvironmentVariableSchema.DefaultValue] = defaultValue;
        return Admin.CreateAsync(definition);
    }

    private Task<Guid> CreateValueAsync(Guid definitionId, string value) =>
        Admin.CreateAsync(new Entity(EnvironmentVariableSchema.Value)
        {
            [EnvironmentVariableSchema.ValueColumn] = value,
            [EnvironmentVariableSchema.DefinitionLookup] = new EntityReference(EnvironmentVariableSchema.Definition, definitionId),
        });

    /// <summary>Registers the test plug-in on PreOperation Create of <c>pb_pipelog</c>, reading <paramref name="schemaName"/>.</summary>
    private async Task RegisterAsync(string schemaName)
    {
        await PluginTestHelper.ClearAsync(fx);
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.EnvironmentVariablePlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", PipeLog, 20, config: schemaName);
    }

    /// <summary>Creates a <c>pb_pipelog</c> as <paramref name="service"/> and returns its <c>pb_data</c>.</summary>
    private async Task<string?> RunAsync(OrganizationService service)
    {
        var id = await service.CreateAsync(new Entity(PipeLog) { ["pb_name"] = "envvar" });
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_data FROM data.{PipeLog} WHERE {PipeLog}id = @id");
        cmd.Parameters.AddWithValue("@id", id);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : (string?)value;
    }

    [Fact]
    public async Task Service_returns_value_then_default_then_null()
    {
        var name = NewName();
        var definition = await CreateDefinitionAsync(name);
        await RegisterAsync(name);
        (await RunAsync(Admin)).Should().Be("(null)", "no value and no default");

        await Admin.UpdateAsync(new Entity(EnvironmentVariableSchema.Definition, definition) { [EnvironmentVariableSchema.DefaultValue] = "fallback" });
        (await RunAsync(Admin)).Should().Be("fallback");

        await CreateValueAsync(definition, "current");
        (await RunAsync(Admin)).Should().Be("current");
    }

    [Fact]
    public async Task Unknown_name_fails_the_plugin()
    {
        var name = NewName();
        await RegisterAsync(name);
        var act = () => RunAsync(Admin);
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Be($"Environment variable '{name}' was not found.");
    }

    [Fact]
    public async Task Secret_value_is_refused()
    {
        var name = NewName();
        await CreateDefinitionAsync(name, type: VariableType.Secret);
        await RegisterAsync(name);
        var act = () => RunAsync(Admin);
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Be("Secret environment variables need Azure Key Vault, which xrmverse does not support.");
    }

    [Fact]
    public async Task Basic_user_plugin_reads_value()
    {
        var name = NewName();
        await CreateValueAsync(await CreateDefinitionAsync(name, "fallback"), "current");
        await RegisterAsync(name);

        // A caller who may create pb_pipelog but has no privilege at all on the environment variable tables.
        var log = fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(PipeLog)!;
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, (PrivilegeNames.For(AccessRights.CreateAccess, log), DepthMask.Basic),
            (PrivilegeNames.For(AccessRights.ReadAccess, log), DepthMask.Basic), (PrivilegeNames.For(AccessRights.WriteAccess, log), DepthMask.Basic));

        (await RunAsync(ServiceFor(fx, oid))).Should().Be("current", "the service reads as the system user");
    }
}
