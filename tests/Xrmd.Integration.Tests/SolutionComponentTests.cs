using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using Xrmd.Solution;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The solution system tables (seeded Default/Active solutions and default publisher), the tracking of components created
/// by the metadata messages and by Create of component rows, AddSolutionComponent/RemoveSolutionComponent and the tables
/// no request writes.
/// </summary>
[Collection("xrmd")]
public sealed class SolutionComponentTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Plugins = "Xrmd.TestPlugins.";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private string Name(string label) => $"new_{label}{_run}";

    private static CreateEntityRequest CreateEntity(string logicalName, string? solution) => new()
    {
        Entity = new EntityMetadata
        {
            LogicalName = logicalName,
            SchemaName = logicalName,
            DisplayName = Label("Table " + logicalName),
            DisplayCollectionName = Label("Tables " + logicalName),
            OwnershipType = OwnershipTypes.UserOwned,
        },
        PrimaryAttribute = new StringAttributeMetadata
        {
            LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name"),
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
        },
        SolutionUniqueName = solution,
    };

    /// <summary>A new unmanaged solution of the default publisher; returns its unique name and id.</summary>
    private async Task<(string Name, Guid Id)> NewSolutionAsync(string label)
    {
        var name = $"{label}{_run}";
        var id = await Admin.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = name,
            ["friendlyname"] = name,
            ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", SystemSeed.DefaultPublisherId),
        });
        return (name, id);
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    /// <summary>The number of <c>solutioncomponent</c> rows of the solution for that component.</summary>
    private async Task<int> ComponentRowsAsync(Guid solutionId, int type, Guid objectId) =>
        Convert.ToInt32(await ScalarAsync(
            "SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @s AND componenttype = @t AND objectid = @o",
            ("@s", solutionId), ("@t", type), ("@o", objectId)));

    private async Task<IReadOnlyList<(int Type, Guid ObjectId)>> ListAsync(Guid solutionId)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        return await fx.App.Services.GetRequiredService<SolutionComponentTracker>().ListAsync(new DataSession(conn, null), solutionId, default);
    }

    // ---- seed -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Seed_has_Default_and_Active_solutions_and_default_publisher()
    {
        foreach (var name in new[] { "publisher", "solution", "solutioncomponent", "importjob", "savedquery", "systemform", "webresource", "sitemap", "appmodule" })
            Store.GetEntity(name).Should().NotBeNull(name);
        Store.GetEntity("webresource")!.EntitySetName.Should().Be("webresourceset");

        var publisher = await Admin.RetrieveAsync("publisher", SystemSeed.DefaultPublisherId, new ColumnSet(true));
        publisher.GetAttributeValue<string>("uniquename").Should().StartWith("DefaultPublisher");
        publisher.GetAttributeValue<string>("customizationprefix").Should().Be("new");
        publisher.GetAttributeValue<int>("customizationoptionvalueprefix").Should().Be(10000);

        foreach (var (id, name) in new[] { (SystemSeed.DefaultSolutionId, "Default"), (SystemSeed.ActiveSolutionId, "Active") })
        {
            var solution = await Admin.RetrieveAsync("solution", id, new ColumnSet(true));
            solution.GetAttributeValue<string>("uniquename").Should().Be(name);
            solution.GetAttributeValue<bool>("ismanaged").Should().BeFalse();
            solution.GetAttributeValue<EntityReference>("publisherid").Id.Should().Be(SystemSeed.DefaultPublisherId);
        }
        (await ScalarAsync("SELECT COUNT(*) FROM data.solution WHERE uniquename IN (N'Default', N'Active')")).Should().Be(2);
        (await ScalarAsync("SELECT COUNT(*) FROM data.publisher WHERE uniquename LIKE N'DefaultPublisher%'")).Should().Be(1);
    }

    // ---- tracking on create -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_with_SolutionUniqueName_adds_the_component_to_that_solution()
    {
        var (solution, solutionId) = await NewSolutionAsync("fixture");

        // Entity (1) and attribute (2): objectid is the MetadataId.
        var entity = Name("sctrack");
        var created = await Admin.ExecuteAsync(CreateEntity(entity, solution));
        var entityId = (Guid)created.Results["EntityId"];
        Store.GetEntity(entity)!.MetadataId.Should().Be(entityId);
        (await ComponentRowsAsync(solutionId, ComponentType.Entity, entityId)).Should().Be(1);
        (await ComponentRowsAsync(SystemSeed.DefaultSolutionId, ComponentType.Entity, entityId)).Should().Be(1);
        (await ScalarAsync("SELECT ismetadata FROM data.solutioncomponent WHERE solutionid = @s AND objectid = @o",
            ("@s", solutionId), ("@o", entityId))).Should().Be(true);

        var attribute = await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = entity,
            Attribute = new IntegerAttributeMetadata { LogicalName = "new_count", SchemaName = "new_Count", DisplayName = Label("Count") },
            SolutionUniqueName = solution,
        });
        var attributeId = (Guid)attribute.Results["AttributeId"];
        (await ComponentRowsAsync(solutionId, ComponentType.Attribute, attributeId)).Should().Be(1);
        (await ComponentRowsAsync(SystemSeed.DefaultSolutionId, ComponentType.Attribute, attributeId)).Should().Be(1);

        // Without SolutionUniqueName: Default only.
        var other = Name("scdefault");
        var otherId = (Guid)(await Admin.ExecuteAsync(CreateEntity(other, null))).Results["EntityId"];
        (await ComponentRowsAsync(SystemSeed.DefaultSolutionId, ComponentType.Entity, otherId)).Should().Be(1);
        (await ComponentRowsAsync(solutionId, ComponentType.Entity, otherId)).Should().Be(0);

        // Role (20): a row component, through Create with SolutionUniqueName.
        var roleId = (Guid)(await Admin.ExecuteAsync(new CreateRequest
        {
            Target = new Entity("role")
            {
                ["name"] = "Role " + _run,
                ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
            },
            ["SolutionUniqueName"] = solution,
        })).Results["id"];
        (await ComponentRowsAsync(solutionId, ComponentType.Role, roleId)).Should().Be(1);
        (await ComponentRowsAsync(SystemSeed.DefaultSolutionId, ComponentType.Role, roleId)).Should().Be(1);

        // Step (92); the assembly (91) and type (90) created without a solution are in Default only.
        var assemblyId = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var typeId = await PluginTestHelper.RegisterTypeAsync(Admin, assemblyId, Plugins + "SetAttributePlugin");
        var stepId = (Guid)(await Admin.ExecuteAsync(new CreateRequest
        {
            Target = new Entity("sdkmessageprocessingstep")
            {
                ["name"] = "tracked step",
                ["sdkmessageid"] = new EntityReference("sdkmessage", SdkMessageCatalog.MessageId("Create")),
                ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", SdkMessageCatalog.FilterId("Create", TestEntities.Pipe)),
                ["plugintypeid"] = new EntityReference("plugintype", typeId),
                ["eventhandler"] = new EntityReference("plugintype", typeId),
                ["stage"] = new OptionSetValue(20),
                ["mode"] = new OptionSetValue(0),
                ["rank"] = 1,
            },
            ["SolutionUniqueName"] = solution,
        })).Results["id"];
        (await ComponentRowsAsync(solutionId, ComponentType.SdkMessageProcessingStep, stepId)).Should().Be(1);
        (await ComponentRowsAsync(SystemSeed.DefaultSolutionId, ComponentType.SdkMessageProcessingStep, stepId)).Should().Be(1);
        (await ComponentRowsAsync(SystemSeed.DefaultSolutionId, ComponentType.PluginAssembly, assemblyId)).Should().Be(1);
        (await ComponentRowsAsync(SystemSeed.DefaultSolutionId, ComponentType.PluginType, typeId)).Should().Be(1);
        (await ComponentRowsAsync(solutionId, ComponentType.PluginAssembly, assemblyId)).Should().Be(0);

        (await ListAsync(solutionId)).Should().BeEquivalentTo(new[]
        {
            (ComponentType.Entity, entityId), (ComponentType.Attribute, attributeId), (ComponentType.Role, roleId),
            (ComponentType.SdkMessageProcessingStep, stepId),
        });

        // Deleting a component removes it from every solution.
        await Admin.DeleteAsync("role", roleId);
        (await ComponentRowsAsync(solutionId, ComponentType.Role, roleId)).Should().Be(0);
        (await ComponentRowsAsync(SystemSeed.DefaultSolutionId, ComponentType.Role, roleId)).Should().Be(0);
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = entity });
        (await ScalarAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE objectid IN (@e, @a)", ("@e", entityId), ("@a", attributeId)))
            .Should().Be(0);
    }

    [Fact]
    public async Task Create_in_an_unknown_solution_is_404_and_writes_nothing()
    {
        var entity = Name("scmissing");
        var fault = await FaultAsync(() => Admin.ExecuteAsync(CreateEntity(entity, "nosuchsolution" + _run)));
        fault.HttpStatus.Should().Be(404);
        Store.GetEntity(entity).Should().BeNull();

        var role = "Missing " + _run;
        var root = await RootBusinessUnitAsync(fx);
        fault = await FaultAsync(() => Admin.ExecuteAsync(new CreateRequest
        {
            Target = new Entity("role")
            {
                ["name"] = role, ["businessunitid"] = new EntityReference("businessunit", root),
            },
            ["SolutionUniqueName"] = "nosuchsolution" + _run,
        }));
        fault.HttpStatus.Should().Be(404);
        (await ScalarAsync("SELECT COUNT(*) FROM data.role WHERE name = @n", ("@n", role))).Should().Be(0);
    }

    [Fact]
    public async Task Web_api_header_adds_created_rows_and_metadata_to_the_solution()
    {
        var (solution, solutionId) = await NewSolutionAsync("webapi");
        async Task<HttpResponseMessage> PostAsync(string path, string json)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/" + path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
            req.Headers.Add("MSCRM.SolutionUniqueName", solution);
            return await fx.Client.SendAsync(req);
        }

        var res = await PostAsync("webresourceset",
            $$"""{"name":"new_/page{{_run}}.htm","displayname":"page","webresourcetype":1,"content":"PGh0bWw+PC9odG1sPg=="}""");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var id = Guid.Parse(res.Headers.GetValues("OData-EntityId").Single().Split('(', ')')[1]);
        (await ComponentRowsAsync(solutionId, ComponentType.WebResource, id)).Should().Be(1);
        (await ComponentRowsAsync(SystemSeed.DefaultSolutionId, ComponentType.WebResource, id)).Should().Be(1);

        var entity = Name("scweb");
        res = await PostAsync("EntityDefinitions", $$"""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata",
              "LogicalName": "{{entity}}", "SchemaName": "{{entity}}",
              "DisplayName": { "LocalizedLabels": [ { "Label": "Web", "LanguageCode": 1033 } ] }, "OwnershipType": "UserOwned",
              "Attributes": [ { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "new_name",
                                "SchemaName": "new_Name", "MaxLength": 100, "IsPrimaryName": true } ]
            }
            """);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        (await ComponentRowsAsync(solutionId, ComponentType.Entity, Store.GetEntity(entity)!.MetadataId!.Value)).Should().Be(1);
    }

    // ---- AddSolutionComponent / RemoveSolutionComponent -------------------------------------------------------------

    [Fact]
    public async Task AddSolutionComponent_and_RemoveSolutionComponent()
    {
        var (solution, solutionId) = await NewSolutionAsync("addremove");
        var entity = Name("scadd");
        var entityId = (Guid)(await Admin.ExecuteAsync(CreateEntity(entity, null))).Results["EntityId"];
        var attributeIds = Store.GetEntity(entity)!.Attributes!.Select(a => a.MetadataId!.Value).ToList();
        attributeIds.Should().NotBeEmpty();

        var added = (AddSolutionComponentResponse)await Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentId = entityId, ComponentType = ComponentType.Entity, SolutionUniqueName = solution, AddRequiredComponents = true,
        });
        var rowId = (Guid)added.Results["id"];
        (await ScalarAsync("SELECT objectid FROM data.solutioncomponent WHERE solutioncomponentid = @id AND solutionid = @s",
            ("@id", rowId), ("@s", solutionId))).Should().Be(entityId);
        (await ListAsync(solutionId)).Should().BeEquivalentTo(
            attributeIds.Select(id => (ComponentType.Attribute, id)).Prepend((ComponentType.Entity, entityId)));
        (await ScalarAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @s AND rootsolutioncomponentid = @r",
            ("@s", solutionId), ("@r", rowId))).Should().Be(attributeIds.Count);

        // Adding again keeps the one row.
        var again = await Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentId = entityId, ComponentType = ComponentType.Entity, SolutionUniqueName = solution,
        });
        again.Results["id"].Should().Be(rowId);
        (await ComponentRowsAsync(solutionId, ComponentType.Entity, entityId)).Should().Be(1);

        var removed = (RemoveSolutionComponentResponse)await Admin.ExecuteAsync(new RemoveSolutionComponentRequest
        {
            ComponentId = entityId, ComponentType = ComponentType.Entity, SolutionUniqueName = solution,
        });
        removed.Results["id"].Should().Be(rowId);
        (await ListAsync(solutionId)).Should().BeEmpty();
        (await ComponentRowsAsync(SystemSeed.DefaultSolutionId, ComponentType.Entity, entityId)).Should().Be(1);

        // Not in the solution any more: 404; from Default: 400.
        (await FaultAsync(() => Admin.ExecuteAsync(new RemoveSolutionComponentRequest
        {
            ComponentId = entityId, ComponentType = ComponentType.Entity, SolutionUniqueName = solution,
        }))).HttpStatus.Should().Be(404);
        var fromDefault = await FaultAsync(() => Admin.ExecuteAsync(new RemoveSolutionComponentRequest
        {
            ComponentId = entityId, ComponentType = ComponentType.Entity, SolutionUniqueName = "Default",
        }));
        fromDefault.HttpStatus.Should().Be(400);
        fromDefault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        (await ComponentRowsAsync(SystemSeed.DefaultSolutionId, ComponentType.Entity, entityId)).Should().Be(1);
    }

    [Fact]
    public async Task AddSolutionComponent_fails_explicitly_for_what_it_cannot_honour()
    {
        var (solution, solutionId) = await NewSolutionAsync("addfail");
        var entity = Name("scfail");
        await Admin.ExecuteAsync(CreateEntity(entity, null));
        var attributeId = Store.GetEntity(entity)!.Attributes!.First().MetadataId!.Value;

        // A type xrmverse does not track (300: canvas app; 29, workflow, is tracked since plan 2.14, 66, custom control, since plan 3.5 UI-1d).
        (await FaultAsync(() => Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentId = Guid.NewGuid(), ComponentType = 300, SolutionUniqueName = solution,
        }))).HttpStatus.Should().Be(400);
        // A component that does not exist.
        (await FaultAsync(() => Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentId = Guid.NewGuid(), ComponentType = ComponentType.WebResource, SolutionUniqueName = solution,
        }))).HttpStatus.Should().Be(404);
        // The required components of an attribute are not computed.
        (await FaultAsync(() => Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentId = attributeId, ComponentType = ComponentType.Attribute, SolutionUniqueName = solution, AddRequiredComponents = true,
        }))).HttpStatus.Should().Be(400);
        // An unknown solution.
        (await FaultAsync(() => Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentId = attributeId, ComponentType = ComponentType.Attribute, SolutionUniqueName = "nosuch" + _run,
        }))).HttpStatus.Should().Be(404);
        (await ListAsync(solutionId)).Should().BeEmpty();
    }

    [Fact]
    public async Task AddSolutionComponent_requires_write_on_solution()
    {
        var (solution, _) = await NewSolutionAsync("addpriv");
        var webResource = await Admin.CreateAsync(new Entity("webresource")
        {
            ["name"] = $"new_/script{_run}.js", ["displayname"] = "script", ["webresourcetype"] = new OptionSetValue(3),
            ["content"] = Convert.ToBase64String("alert(1);"u8.ToArray()),
        });
        var request = new AddSolutionComponentRequest
        {
            ComponentId = webResource, ComponentType = ComponentType.WebResource, SolutionUniqueName = solution, AddRequiredComponents = true,
        };

        async Task<OrganizationService> UserWithRoleAsync(Guid role)
        {
            var objectId = Guid.NewGuid();
            await AssignRoleAsync(fx, await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId), role);
            return ServiceFor(fx, objectId);
        }

        var basic = await UserWithRoleAsync(StandardRoles.BasicUser);
        var denied = await FaultAsync(() => basic.ExecuteAsync(request));
        denied.HttpStatus.Should().Be(403);
        denied.Message.Should().Contain("prvWriteSolution");
        var customizer = await UserWithRoleAsync(StandardRoles.SystemCustomizer);
        (await customizer.ExecuteAsync(request)).Results["id"].Should().BeOfType<Guid>();
    }

    // ---- tables no request writes -----------------------------------------------------------------------------------

    [Fact]
    public async Task Solutioncomponent_and_importjob_cannot_be_written_directly()
    {
        var component = await FaultAsync(() => Admin.CreateAsync(new Entity("solutioncomponent")
        {
            ["solutionid"] = new EntityReference("solution", SystemSeed.DefaultSolutionId),
            ["componenttype"] = new OptionSetValue(ComponentType.Entity),
            ["objectid"] = Guid.NewGuid(),
        }));
        component.HttpStatus.Should().Be(403);
        component.ErrorCode.Should().Be(ErrorCodes.PrivilegeDenied);

        (await FaultAsync(() => Admin.CreateAsync(new Entity("importjob") { ["name"] = "job", ["solutionname"] = "x" })))
            .HttpStatus.Should().Be(403);

        var existing = (Guid)(await ScalarAsync("SELECT TOP 1 solutioncomponentid FROM data.solutioncomponent"))!;
        (await FaultAsync(() => Admin.DeleteAsync("solutioncomponent", existing))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => Admin.UpdateAsync(new Entity("solutioncomponent", existing) { ["objectid"] = Guid.NewGuid() })))
            .HttpStatus.Should().Be(403);

        foreach (var system in new[] { SystemSeed.DefaultSolutionId, SystemSeed.ActiveSolutionId })
        {
            (await FaultAsync(() => Admin.DeleteAsync("solution", system))).HttpStatus.Should().Be(403);
            (await FaultAsync(() => Admin.UpdateAsync(new Entity("solution", system) { ["uniquename"] = "Renamed" + _run })))
                .HttpStatus.Should().Be(403);
        }
        (await ScalarAsync("SELECT COUNT(*) FROM data.solution WHERE solutionid IN (@d, @a)",
            ("@d", SystemSeed.DefaultSolutionId), ("@a", SystemSeed.ActiveSolutionId))).Should().Be(2);

        // Another solution can be deleted.
        var (_, id) = await NewSolutionAsync("deletable");
        await Admin.DeleteAsync("solution", id);
    }
}
