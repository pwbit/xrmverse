using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Host;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.2, task 1: the seeded <c>serviceendpoint</c> table (object type code 4618), the polymorphic
/// <c>sdkmessageprocessingstep.eventhandler</c> (plug-in type or service endpoint) with its two relationships, the upgrade of an
/// organization made before it (<see cref="MetadataStore.SetSeedLookupTargetsAsync"/>), the standard roles' privileges, and the
/// step registry, which skips a step on a service endpoint until the delivery exists.
/// </summary>
[Collection("xrmd")]
public sealed class ServiceEndpointTableTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public async Task DisposeAsync()
    {
        await ExecAsync(fx.ConnectionString, "DELETE FROM data.serviceendpoint; DELETE FROM meta.serviceendpoint_secret;");
        await PluginTestHelper.ClearAsync(fx);
    }

    private static IEnumerable<int> OptionValues(AttributeMetadata a) =>
        ((EnumAttributeMetadata)a).OptionSet!.Options.Select(o => o.Value!.Value);

    [Fact]
    public async Task Seed_creates_serviceendpoint()
    {
        var endpoint = Store.GetEntity(ServiceEndpointSchema.Table)!;
        (endpoint.EntitySetName, endpoint.ObjectTypeCode, endpoint.OwnershipType).Should().Be(("serviceendpoints", 4618, OwnershipTypes.OrganizationOwned));
        Store.GetEntityBySetName("serviceendpoints")!.LogicalName.Should().Be("serviceendpoint");
        Store.GetEntityByObjectTypeCode(4618)!.LogicalName.Should().Be("serviceendpoint");
        var columns = endpoint.Attributes.ToDictionary(a => a.LogicalName);
        OptionValues(columns["contract"]).Should().Equal(Enumerable.Range(1, 11));
        OptionValues(columns["authtype"]).Should().Equal(Enumerable.Range(0, 10));
        ((EnumAttributeMetadata)columns["contract"]).OptionSet.Name.Should().Be("serviceendpoint_contract");
        ((EnumAttributeMetadata)columns["authtype"]).OptionSet.Name.Should().Be("serviceendpoint_authtype");
        columns[ServiceEndpointSchema.AuthValue].IsValidForRead.Should().BeFalse();
        (columns[ServiceEndpointSchema.IsAuthValueSet].IsValidForCreate, columns[ServiceEndpointSchema.IsAuthValueSet].IsValidForUpdate)
            .Should().Be((false, false));
        // The write-only value has no data column (plan 3.2 task 2: it lives in meta.serviceendpoint_secret).
        TypeMapping.IsReadable(columns[ServiceEndpointSchema.AuthValue]).Should().BeFalse();
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'data' AND TABLE_NAME = 'serviceendpoint' AND COLUMN_NAME = 'authvalue'")).Should().Be(0);
    }

    [Fact]
    public async Task Eventhandler_is_polymorphic()
    {
        var eventHandler = (LookupAttributeMetadata)Store.GetEntity("sdkmessageprocessingstep")!.Attributes.Single(a => a.LogicalName == "eventhandler");
        eventHandler.Targets.Should().Equal("plugintype", "serviceendpoint");
        TypeMapping.IsPolymorphicLookup(eventHandler).Should().BeTrue();
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'data' AND TABLE_NAME = 'sdkmessageprocessingstep' AND COLUMN_NAME = 'eventhandlertype'")).Should().Be(1);

        var type = (OneToManyRelationshipMetadata)Store.GetRelationship("plugintype_sdkmessageprocessingstep")!;
        (type.ReferencedEntity, type.ReferencingEntity, type.ReferencingAttribute, type.ReferencingEntityNavigationPropertyName)
            .Should().Be(("plugintype", "sdkmessageprocessingstep", "eventhandler", "eventhandler_plugintype"));
        var endpoint = (OneToManyRelationshipMetadata)Store.GetRelationship("serviceendpoint_sdkmessageprocessingstep")!;
        (endpoint.ReferencedEntity, endpoint.ReferencingEntity, endpoint.ReferencingAttribute, endpoint.ReferencingEntityNavigationPropertyName)
            .Should().Be(("serviceendpoint", "sdkmessageprocessingstep", "eventhandler", "eventhandler_serviceendpoint"));
        endpoint.CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);

        // The Web API has one navigation property per target, each named by its relationship.
        var metadata = await GetAsync("$metadata", fx.AdminObjectId);
        metadata.Status.Should().Be(HttpStatusCode.OK);
        metadata.Body.Should().Contain("<NavigationProperty Name=\"eventhandler_plugintype\" Type=\"Microsoft.Dynamics.CRM.plugintype\"")
            .And.Contain("<NavigationProperty Name=\"eventhandler_serviceendpoint\" Type=\"Microsoft.Dynamics.CRM.serviceendpoint\"");

        // A step on a plug-in type keeps working and is read back with its type; deleting an endpoint deletes its steps.
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var typeId = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
        var step = await PluginTestHelper.RegisterStepAsync(Admin, typeId, "Create", "account", 20, config: "accountnumber=from plug-in");
        (await Admin.RetrieveAsync("sdkmessageprocessingstep", step, new ColumnSet("eventhandler"))).GetAttributeValue<EntityReference>("eventhandler")
            .Should().Match<EntityReference>(r => r.LogicalName == "plugintype" && r.Id == typeId);
        (await ScalarAsync(fx.ConnectionString, "SELECT eventhandlertype FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id",
            ("@id", step))).Should().Be(4602);
    }

    /// <summary>
    /// A step on a service endpoint (written with SQL here) is loaded by the step registry as a webhook step, with its endpoint
    /// and stored auth value and no plug-in type; the plug-in steps of the same message are still there. Deleting the endpoint
    /// deletes the step (Delete Cascade).
    /// </summary>
    [Fact]
    public async Task Step_on_an_endpoint_is_loaded_and_goes_with_it()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var typeId = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, typeId, "Create", "account", 20, config: "accountnumber=from plug-in");
        var endpoint = await Admin.CreateAsync(new Entity(ServiceEndpointSchema.Table)
        {
            ["name"] = "hook", ["contract"] = new OptionSetValue(8), ["authtype"] = new OptionSetValue(4), ["url"] = "https://example.invalid/hook",
            ["authvalue"] = "hook-key",
        });
        var webhookStep = Guid.NewGuid();
        await ExecAsync(fx.ConnectionString, """
            INSERT INTO data.sdkmessageprocessingstep (sdkmessageprocessingstepid, name, sdkmessageid, sdkmessagefilterid, eventhandler,
                eventhandlertype, stage, mode, rank, createdon, modifiedon, statecode, statuscode)
            VALUES (@id, N'webhook', @message, @filter, @endpoint, 4618, 40, 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 0, 1)
            """, ("@id", webhookStep), ("@message", SdkMessageCatalog.MessageId("Create")), ("@filter", SdkMessageCatalog.FilterId("Create", "account")),
            ("@endpoint", endpoint));
        var registry = fx.App.Services.GetRequiredService<Xrmd.Pipeline.Registration.StepRegistry>();
        registry.Invalidate();

        var loaded = (await registry.GetStepAsync(webhookStep, default))!;
        loaded.Endpoint.Should().NotBeNull();
        loaded.Endpoint!.Id.Should().Be(endpoint);
        loaded.Endpoint.Url.Should().Be(new Uri("https://example.invalid/hook"));
        loaded.Endpoint.AuthValue.Should().Be("hook-key");
        (loaded.PluginTypeId, loaded.AssemblyId, loaded.TypeName, loaded.AssemblyVersion).Should().Be((Guid.Empty, Guid.Empty, "", 0L));
        loaded.ToString().Should().NotContain("hook-key");
        (await registry.GetStepsAsync("Create", "account", 20, 0, default)).Should().ContainSingle().Which.PluginTypeId.Should().Be(typeId);

        await Admin.DeleteAsync(ServiceEndpointSchema.Table, endpoint);
        (await ScalarAsync(fx.ConnectionString, "SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id",
            ("@id", webhookStep))).Should().Be(0);
        registry.Invalidate();
        (await registry.GetStepAsync(webhookStep, default)).Should().BeNull();
    }

    /// <summary>
    /// An organization made before plan 3.2 (its <c>eventhandler</c> a lookup to <c>plugintype</c> only, without the type column or
    /// the saved relationships) with a registered plug-in step: the next start's seed makes the lookup polymorphic, gives the
    /// existing row the plug-in type's object type code, saves the two relationships, and the step still runs. A second seed
    /// changes nothing.
    /// </summary>
    [SqlServerFact("an organization made before plan 3.2 exists on SQL Server only: a PostgreSQL organization starts at the 0018 baseline")]
    public async Task Upgrade_adds_serviceendpoint_target_to_old_eventhandler()
    {
        var database = "upgrade32_" + Guid.NewGuid().ToString("N")[..10];
        var connectionString = new SqlConnectionStringBuilder(fx.ConnectionString) { InitialCatalog = database }.ConnectionString;
        WebApplicationFactory<Program> Host() => fx.App.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Org", connectionString));
        try
        {
            Guid step;
            await using (var first = Host())
            {
                first.CreateClient();
                var admin = TestServices.NewOrganizationService(first.Services, fx.AdminObjectId);
                var assembly = await PluginTestHelper.RegisterAssemblyAsync(admin, PluginTestHelper.TestPluginBytes);
                var typeId = await PluginTestHelper.RegisterTypeAsync(admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
                step = await PluginTestHelper.RegisterStepAsync(admin, typeId, "Create", "account", 20, config: "accountnumber=upgraded");
            }

            // The pre-3.2 definition: a lookup to plugintype only, no type column, no saved relationship.
            await ExecAsync(connectionString, """
                UPDATE meta.attribute SET definition = JSON_MODIFY(definition, '$.Targets', JSON_QUERY(N'["plugintype"]'))
                    WHERE entity_logical_name = N'sdkmessageprocessingstep' AND logical_name = N'eventhandler';
                ALTER TABLE data.sdkmessageprocessingstep DROP COLUMN eventhandlertype;
                DELETE FROM meta.relationship WHERE schema_name IN (N'plugintype_sdkmessageprocessingstep', N'serviceendpoint_sdkmessageprocessingstep');
                """);
            var factory = new SqlConnectionFactory(connectionString);
            var old = new MetadataStore(factory);
            await old.LoadAsync();
            ((LookupAttributeMetadata)old.GetEntity("sdkmessageprocessingstep")!.Attributes.Single(a => a.LogicalName == "eventhandler"))
                .Targets.Should().Equal("plugintype");

            var options = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), fx.AdminObjectId, XrmdFixture.AdminName);
            await new SystemSeed(old, factory).EnsureAsync(options);

            var reloaded = new MetadataStore(factory);
            await reloaded.LoadAsync();
            foreach (var store in new[] { old, reloaded })
            {
                ((LookupAttributeMetadata)store.GetEntity("sdkmessageprocessingstep")!.Attributes.Single(a => a.LogicalName == "eventhandler"))
                    .Targets.Should().Equal("plugintype", "serviceendpoint");
                ((OneToManyRelationshipMetadata)store.GetRelationship("serviceendpoint_sdkmessageprocessingstep")!)
                    .ReferencingEntityNavigationPropertyName.Should().Be("eventhandler_serviceendpoint");
                ((OneToManyRelationshipMetadata)store.GetRelationship("plugintype_sdkmessageprocessingstep")!)
                    .ReferencingEntityNavigationPropertyName.Should().Be("eventhandler_plugintype");
            }
            (await ScalarAsync(connectionString, "SELECT eventhandlertype FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id",
                ("@id", step))).Should().Be(4602);

            // A second seed is a no-op.
            var stamp = reloaded.VersionStamp;
            await new SystemSeed(reloaded, factory).EnsureAsync(options);
            reloaded.VersionStamp.Should().Be(stamp);

            await using var second = Host();
            second.CreateClient();
            var service = TestServices.NewOrganizationService(second.Services, fx.AdminObjectId);
            var account = await service.CreateAsync(new Entity("account") { ["name"] = "after the upgrade" });
            (await service.RetrieveAsync("account", account, new ColumnSet("accountnumber"))).GetAttributeValue<string>("accountnumber")
                .Should().Be("upgraded");
            (await service.RetrieveAsync("sdkmessageprocessingstep", step, new ColumnSet("eventhandler"))).GetAttributeValue<EntityReference>("eventhandler")
                .LogicalName.Should().Be("plugintype");
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecAsync(new SqlConnectionStringBuilder(fx.ConnectionString) { InitialCatalog = "master" }.ConnectionString,
                $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END");
        }
    }

    [Fact]
    public async Task Customizer_can_create_endpoint()
    {
        var customizer = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), customizer);
        await AssignRoleAsync(fx, userId, StandardRoles.SystemCustomizer);
        var service = ServiceFor(fx, customizer);

        var id = await service.CreateAsync(new Entity(ServiceEndpointSchema.Table)
        {
            ["name"] = "customizer hook", ["contract"] = new OptionSetValue(ServiceEndpointSchema.WebhookContract),
            ["authtype"] = new OptionSetValue(ServiceEndpointSchema.WebhookKey), ["url"] = "https://example.invalid/hook",
            [ServiceEndpointSchema.AuthValue] = "customizer-key",
        });
        var row = await service.RetrieveAsync(ServiceEndpointSchema.Table, id, new ColumnSet("name", "contract", "url"));
        (row.GetAttributeValue<string>("name"), row.GetAttributeValue<OptionSetValue>("contract").Value).Should().Be(("customizer hook", 8));
        await service.UpdateAsync(new Entity(ServiceEndpointSchema.Table, id) { ["description"] = "changed" });
        await service.DeleteAsync(ServiceEndpointSchema.Table, id);

        // The customizer's privileges on the table, and AppendTo, as for plugintype; Basic User has none.
        var granted = await RolePrivilegesAsync(StandardRoles.SystemCustomizer);
        foreach (var verb in new[] { "Create", "Read", "Write", "Delete", "Append", "AppendTo" })
            granted.Should().ContainKey($"prv{verb}ServiceEndpoint").WhoseValue.Should().Be((int)DepthMask.Global, verb);
        (await RolePrivilegesAsync(StandardRoles.BasicUser)).Keys.Should().NotContain(k => k.EndsWith("ServiceEndpoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Basic_user_cannot_read_endpoint()
    {
        var endpoint = await Admin.CreateAsync(new Entity(ServiceEndpointSchema.Table)
        {
            ["name"] = "secret hook", ["contract"] = new OptionSetValue(8), ["authtype"] = new OptionSetValue(4), ["url"] = "https://example.invalid/hook",
            ["authvalue"] = "secret-key",
        });
        var basic = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), basic);
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);

        var fault = await FaultAsync(() => ServiceFor(fx, basic).RetrieveAsync(ServiceEndpointSchema.Table, endpoint, new ColumnSet("name")));
        (fault.HttpStatus, fault.ErrorCode).Should().Be((403, ErrorCodes.PrivilegeDenied));

        var (status, body) = await GetAsync($"serviceendpoints({endpoint})?$select=name", basic);
        status.Should().Be(HttpStatusCode.Forbidden, body);
        JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>().Should().Be("0x80040220");
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path, Guid objectId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task ExecAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(connectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(connectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<Dictionary<string, int>> RolePrivilegesAsync(Guid roleId)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("""
            SELECT p.name, rp.privilegedepthmask FROM data.roleprivileges AS rp JOIN data.privilege AS p ON p.privilegeid = rp.privilegeid
            WHERE rp.roleid = @role
            """, conn);
        cmd.Parameters.AddWithValue("@role", roleId);
        var rows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) rows.Add(rd.GetString(0), rd.GetInt32(1));
        return rows;
    }
}
