using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Solution;
using Xrmd.Solution.Layers;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.6, Task 2: the component layer store (<c>meta.component_layer</c>, <c>meta.solution_rank</c>; rulings M1, M2), the eager
/// Active marker (M4: metadata messages, row components, role privileges) and the read-only <c>ismanaged</c> column of the row
/// component tables (M14). The layers are seeded directly with <see cref="ComponentLayerStore.UpsertAsync"/>: no managed import
/// exists yet.
/// </summary>
[Collection("xrmd")]
public sealed class ComponentLayerStoreTests(XrmdFixture fx)
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private ComponentLayerStore Layers => fx.App.Services.GetRequiredService<ComponentLayerStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private string Name(string label) => $"new_{label}{_run}";

    /// <summary>The row component tables that carry the read-only <c>ismanaged</c> column.</summary>
    private static readonly string[] RowComponentTables =
    [
        "role", "savedquery", "systemform", "webresource", "sitemap", "fieldsecurityprofile", "appmodule", "plugintype", "pluginassembly",
        "sdkmessageprocessingstep", "sdkmessageprocessingstepimage", "customapi", "customcontrol", "savedqueryvisualization",
    ];

    public static TheoryData<string> ComponentTables => new(RowComponentTables);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>The Delete of a row as a layer operation (<see cref="Caller.Internal"/>), as an uninstall deletes a managed component.</summary>
    private async Task InternalDeleteAsync(string table, Guid id)
    {
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await TestEntities.InSessionAsync(fx.ConnectionString, s =>
            core.ExecuteCoreAsync(new DeleteRequest { Target = new EntityReference(table, id) }, Caller.Internal, s, default));
    }

    private async Task<T> InSessionAsync<T>(Func<DataSession, Task<T>> work)
    {
        T result = default!;
        await TestEntities.InSessionAsync(fx.ConnectionString, async s => result = await work(s));
        return result;
    }

    private Task InSessionAsync(Func<DataSession, Task> work) => TestEntities.InSessionAsync(fx.ConnectionString, work);

    private Task<IReadOnlyList<ComponentLayer>> StackAsync(int type, Guid id) => InSessionAsync(s => Layers.StackAsync(s, type, id, default));

    private Task UpsertAsync(ComponentLayer layer) => InSessionAsync(s => Layers.UpsertAsync(s, layer, default));

    private static ComponentLayer Managed(int type, Guid id, Guid solution, long order = 0) =>
        new(type, id, solution, order, LayerKind.Managed, "{\"xml\":\"<x />\",\"files\":{}}");

    private static ComponentLayer SystemLayer(int type, Guid id) =>
        new(type, id, ComponentLayerStore.SystemLayerSolutionId, 0, LayerKind.System, "{\"xml\":\"<s />\",\"files\":{}}");

    private async Task<Guid> CreateViewAsync(string name) => await Admin.CreateAsync(new Entity("savedquery")
    {
        ["name"] = name, ["returnedtypecode"] = "account", ["querytype"] = 0, ["isdefault"] = false,
        ["fetchxml"] = "<fetch><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>",
        ["layoutxml"] = "<grid name=\"resultset\" object=\"1\" jump=\"name\" select=\"1\" icon=\"1\" preview=\"1\"><row name=\"result\" id=\"accountid\"><cell name=\"name\" width=\"300\" /></row></grid>",
    });

    private async Task<string> NewEntityAsync(string label)
    {
        var name = Name(label);
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = TestHelpers.Entity(name, name, "Table " + name),
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new IntegerAttributeMetadata { LogicalName = "new_count", SchemaName = "new_Count", MinValue = 0, MaxValue = 100, DisplayName = Label("Count") },
        });
        return name;
    }

    private Guid AttributeId(string entity, string attribute) =>
        Store.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == attribute).MetadataId!.Value;

    // ---- the store --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stack_orders_by_rank_with_active_on_top()
    {
        var id = Guid.NewGuid();
        var (lower, upper) = (Guid.NewGuid(), Guid.NewGuid());
        await UpsertAsync(Managed(ComponentType.Attribute, id, upper, ComponentLayerStore.Order(2, 0)));
        await UpsertAsync(SystemLayer(ComponentType.Attribute, id));
        await UpsertAsync(Managed(ComponentType.Attribute, id, lower, ComponentLayerStore.Order(1, 0)));
        (await InSessionAsync(s => Layers.MarkActiveAsync(s, ComponentType.Attribute, id, default))).Should().BeTrue();

        var stack = await StackAsync(ComponentType.Attribute, id);
        stack.Select(l => l.Kind).Should().Equal(LayerKind.Active, LayerKind.Managed, LayerKind.Managed, LayerKind.System);
        stack.Select(l => l.SolutionId).Should().Equal(SystemSeed.ActiveSolutionId, upper, lower, ComponentLayerStore.SystemLayerSolutionId);
        stack.Select(l => l.Order).Should().Equal(long.MaxValue, 2 * 1024, 1024, 0);
        stack[0].Payload.Should().BeNull("the Active layer is a marker: its content is the effective state");
        stack[1].Payload.Should().Contain("<x />");

        // An upsert replaces the solution's layer (one row per component and solution).
        await UpsertAsync(Managed(ComponentType.Attribute, id, lower, ComponentLayerStore.Order(1, 0)) with { Payload = "{\"xml\":\"<y />\"}" });
        (await StackAsync(ComponentType.Attribute, id)).Should().HaveCount(4).And.Contain(l => l.SolutionId == lower && l.Payload!.Contains("<y />"));

        // Type 3 is recorded as 10, as the solution components are.
        var relationship = Guid.NewGuid();
        await UpsertAsync(Managed(ComponentType.Relationship, relationship, lower));
        (await StackAsync(ComponentType.EntityRelationship, relationship)).Should().ContainSingle().Which.Type.Should().Be(ComponentType.EntityRelationship);
        (await StackAsync(ComponentType.Relationship, relationship)).Should().ContainSingle();

        (await InSessionAsync(s => Layers.OfSolutionAsync(s, lower, default))).Select(l => l.ObjectId).Should().BeEquivalentTo([id, relationship]);
        await InSessionAsync(s => Layers.RemoveSolutionAsync(s, lower, default));
        (await StackAsync(ComponentType.Attribute, id)).Select(l => l.SolutionId).Should().NotContain(lower);
        await InSessionAsync(s => Layers.RemoveComponentAsync(s, [id, relationship], default));
        (await StackAsync(ComponentType.Attribute, id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Holding_rank_sits_above_its_base_and_below_later_segment()
    {
        var id = Guid.NewGuid();
        var (baseSolution, holding, later) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        try
        {
            var segment = await InSessionAsync(async s =>
            {
                var next = await Layers.NextSegmentAsync(s, default);
                next.Should().BeGreaterThan(0, "segment 0 is the System layer's");
                await Layers.SetRankAsync(s, baseSolution, (int)next, 0, default);
                (await Layers.NextSegmentAsync(s, default)).Should().Be(next + 1, "the last installed base solution goes on top");
                await Layers.SetRankAsync(s, holding, (int)next, 1023, default);
                await Layers.SetRankAsync(s, later, (int)next + 1, 0, default);
                return next;
            });

            // The rank decides the order of a managed layer, whatever order the caller passes.
            await UpsertAsync(Managed(ComponentType.Attribute, id, holding));
            await UpsertAsync(Managed(ComponentType.Attribute, id, later));
            await UpsertAsync(Managed(ComponentType.Attribute, id, baseSolution));
            var stack = await StackAsync(ComponentType.Attribute, id);
            stack.Select(l => l.SolutionId).Should().Equal(later, holding, baseSolution);
            stack.Select(l => l.Order).Should().Equal((segment + 1) * 1024, segment * 1024 + 1023, segment * 1024);

            // Promote renumbers the holding to sub 0: its existing layers follow.
            await InSessionAsync(s => Layers.SetRankAsync(s, holding, (int)segment, 0, default));
            (await StackAsync(ComponentType.Attribute, id)).Single(l => l.SolutionId == holding).Order.Should().Be(segment * 1024);
        }
        finally
        {
            await InSessionAsync(async s =>
            {
                foreach (var solution in new[] { baseSolution, holding, later }) await Layers.RemoveSolutionAsync(s, solution, default);
            });
        }
        (await StackAsync(ComponentType.Attribute, id)).Should().BeEmpty();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT COUNT(*) FROM meta.solution_rank WHERE solution_id IN ('{baseSolution}', '{holding}', '{later}')");
        (Convert.ToInt32(await cmd.ExecuteScalarAsync())).Should().Be(0, "removing a solution's layers removes its rank");
    }

    [Fact]
    public async Task Mark_active_needs_a_lower_layer()
    {
        var id = Guid.NewGuid();
        (await InSessionAsync(s => Layers.MarkActiveAsync(s, ComponentType.SavedQuery, id, default))).Should().BeFalse();
        (await StackAsync(ComponentType.SavedQuery, id)).Should().BeEmpty("an unlayered component gets no marker");

        await UpsertAsync(SystemLayer(ComponentType.SavedQuery, id));
        (await InSessionAsync(s => Layers.MarkActiveAsync(s, ComponentType.SavedQuery, id, default))).Should().BeTrue();
        (await InSessionAsync(s => Layers.MarkActiveAsync(s, ComponentType.SavedQuery, id, default))).Should().BeTrue();
        var stack = await StackAsync(ComponentType.SavedQuery, id);
        stack.Should().HaveCount(2);
        stack[0].Should().Be(new ComponentLayer(ComponentType.SavedQuery, id, SystemSeed.ActiveSolutionId, long.MaxValue, LayerKind.Active, null));

        await InSessionAsync(s => Layers.RemoveActiveAsync(s, ComponentType.SavedQuery, id, default));
        (await StackAsync(ComponentType.SavedQuery, id)).Should().ContainSingle().Which.Kind.Should().Be(LayerKind.System);
        await InSessionAsync(s => Layers.RemoveComponentAsync(s, [id], default));
    }

    // ---- the Active marker (M4) -------------------------------------------------------------------------------------

    [Fact]
    public async Task Unmanaged_update_of_layered_attribute_marks_active()
    {
        var entity = await NewEntityAsync("la");
        var layered = AttributeId(entity, "new_count");
        var plain = AttributeId(entity, "new_name");
        await UpsertAsync(Managed(ComponentType.Attribute, layered, Guid.NewGuid()));

        await Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = entity, Attribute = new IntegerAttributeMetadata { LogicalName = "new_count", DisplayName = Label("Changed here") },
        });
        await Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = entity, Attribute = new StringAttributeMetadata { LogicalName = "new_name", DisplayName = Label("Changed too") },
        });

        var stack = await StackAsync(ComponentType.Attribute, layered);
        stack.Select(l => l.Kind).Should().Equal(LayerKind.Active, LayerKind.Managed);
        (await StackAsync(ComponentType.Attribute, plain)).Should().BeEmpty("a component without a managed or system layer gets no marker");

        // The entity's own message marks the entity once it is layered.
        var entityId = Store.GetEntity(entity)!.MetadataId!.Value;
        await UpsertAsync(SystemLayer(ComponentType.Entity, entityId));
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = entity, DisplayName = Label("Renamed") } });
        (await StackAsync(ComponentType.Entity, entityId)).Select(l => l.Kind).Should().Equal(LayerKind.Active, LayerKind.System);

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = entity });
    }

    [Fact]
    public async Task Update_of_layered_view_row_marks_active()
    {
        var view = await CreateViewAsync("Layered view " + _run);
        var plain = await CreateViewAsync("Plain view " + _run);
        await UpsertAsync(Managed(ComponentType.SavedQuery, view, Guid.NewGuid()));

        await Admin.UpdateAsync(new Entity("savedquery", view) { ["name"] = "Customized " + _run });
        await Admin.UpdateAsync(new Entity("savedquery", plain) { ["name"] = "Plain changed " + _run });

        (await StackAsync(ComponentType.SavedQuery, view)).Select(l => l.Kind).Should().Equal(LayerKind.Active, LayerKind.Managed);
        (await StackAsync(ComponentType.SavedQuery, plain)).Should().BeEmpty();

        await InternalDeleteAsync("savedquery", view); // a managed component goes only by a layer operation (M14)
        await Admin.DeleteAsync("savedquery", plain);
    }

    [Fact]
    public async Task Role_privilege_change_of_layered_role_marks_active()
    {
        var role = await CreateRoleAsync(fx, "Layered role " + _run);
        await UpsertAsync(Managed(ComponentType.Role, role, Guid.NewGuid()));
        Guid privilege;
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, "SELECT privilegeid FROM data.privilege WHERE name = 'prvReadAccount'");
            privilege = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        await Admin.ExecuteAsync(new AddPrivilegesRoleRequest
        {
            RoleId = role, Privileges = [new RolePrivilege { PrivilegeId = privilege, Depth = PrivilegeDepth.Basic }],
        });

        (await StackAsync(ComponentType.Role, role)).Select(l => l.Kind).Should().Equal(LayerKind.Active, LayerKind.Managed);
        await InternalDeleteAsync("role", role); // a managed component goes only by a layer operation (M14)
        (await StackAsync(ComponentType.Role, role)).Should().BeEmpty();
    }

    /// <summary>Task 2 parked: an image added to a layered step changes the step, which gets its Active marker.</summary>
    [Fact]
    public async Task Step_image_of_layered_step_marks_the_step_active()
    {
        await PluginTestHelper.ClearAsync(fx);
        var step = Guid.Empty;
        try
        {
            var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
            var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
            step = await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", "account", 40, config: "pb_pipelog");
            var plain = await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", "account", 40, rank: 2, config: "pb_pipelog");
            await UpsertAsync(Managed(ComponentType.SdkMessageProcessingStep, step, Guid.NewGuid()));

            await PluginTestHelper.RegisterImageAsync(Admin, step, 0, "pre");
            await PluginTestHelper.RegisterImageAsync(Admin, plain, 0, "pre");

            (await StackAsync(ComponentType.SdkMessageProcessingStep, step)).Select(l => l.Kind).Should().Equal(LayerKind.Active, LayerKind.Managed);
            (await StackAsync(ComponentType.SdkMessageProcessingStep, plain)).Should().BeEmpty();
        }
        finally
        {
            if (step != Guid.Empty) await InSessionAsync(s => Layers.RemoveComponentAsync(s, [step], default));
            await PluginTestHelper.ClearAsync(fx);
        }
    }

    /// <summary>Task 2 parked: a change of a layered global option set (a new value) gives it its Active marker.</summary>
    [Fact]
    public async Task Change_of_layered_global_option_set_marks_active()
    {
        var name = Name("layeredset");
        var id = ((CreateOptionSetResponse)await Admin.ExecuteAsync(new CreateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata([new OptionMetadata(Label("Red"), 100000000)]) { Name = name, DisplayName = Label("Colors") },
        })).OptionSetId;
        await UpsertAsync(Managed(ComponentType.OptionSet, id, Guid.NewGuid()));

        await Admin.ExecuteAsync(new InsertOptionValueRequest { OptionSetName = name, Label = Label("Green") });

        (await StackAsync(ComponentType.OptionSet, id)).Select(l => l.Kind).Should().Equal(LayerKind.Active, LayerKind.Managed);
        await InSessionAsync(s => Layers.RemoveComponentAsync(s, [id], default));
        await Admin.ExecuteAsync(new DeleteOptionSetRequest { Name = name });
    }

    /// <summary>Task 2 parked: the Active marker is written in the change's transaction; a rolled-back change leaves none.</summary>
    [Fact]
    public async Task Rolled_back_change_leaves_no_active_marker()
    {
        var view = await CreateViewAsync("Rolled back view " + _run);
        await UpsertAsync(Managed(ComponentType.SavedQuery, view, Guid.NewGuid()));
        var caller = new Caller(fx.AdminObjectId, await RootBusinessUnitAsync(fx));

        using (var scope = fx.App.Services.CreateScope())
        {
            var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
            var act = () => TestEntities.InSessionAsync(fx.ConnectionString, async s =>
            {
                await core.ExecuteCoreAsync(new UpdateRequest { Target = new Entity("savedquery", view) { ["name"] = "Changed " + _run } }, caller, s, default);
                (await Layers.StackAsync(s, ComponentType.SavedQuery, view, default)).Select(l => l.Kind)
                    .Should().Equal([LayerKind.Active, LayerKind.Managed], "the marker is written with the change");
                throw new InvalidOperationException("roll back");
            });
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("roll back");
        }

        (await StackAsync(ComponentType.SavedQuery, view)).Select(l => l.Kind).Should().Equal(LayerKind.Managed);
        (await Admin.RetrieveAsync("savedquery", view, new Microsoft.Xrm.Sdk.Query.ColumnSet("name"))).GetAttributeValue<string>("name")
            .Should().Be("Rolled back view " + _run);
        await InternalDeleteAsync("savedquery", view);
    }

    [Fact]
    public async Task Component_delete_removes_its_layers()
    {
        var view = await CreateViewAsync("Deleted view " + _run);
        await UpsertAsync(Managed(ComponentType.SavedQuery, view, Guid.NewGuid()));
        await InSessionAsync(s => Layers.MarkActiveAsync(s, ComponentType.SavedQuery, view, default));
        (await StackAsync(ComponentType.SavedQuery, view)).Should().HaveCount(2);
        await InternalDeleteAsync("savedquery", view); // a managed component goes only by a layer operation (M14)
        (await StackAsync(ComponentType.SavedQuery, view)).Should().BeEmpty();

        // A deleted table takes its attributes' layers with it.
        var entity = await NewEntityAsync("ld");
        var attribute = AttributeId(entity, "new_count");
        await UpsertAsync(Managed(ComponentType.Attribute, attribute, Guid.NewGuid()));
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = entity });
        (await StackAsync(ComponentType.Attribute, attribute)).Should().BeEmpty();
    }

    // ---- ismanaged (M14) --------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ComponentTables))]
    public void Ismanaged_column_is_defined_read_only(string table)
    {
        var column = Store.GetEntity(table)!.Attributes.Should().ContainSingle(a => a.LogicalName == "ismanaged").Which;
        column.Should().BeOfType<BooleanAttributeMetadata>();
        column.IsValidForCreate.Should().BeFalse();
        column.IsValidForUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task Ismanaged_column_is_read_only()
    {
        var view = await CreateViewAsync("Read-only flag " + _run);
        (await Admin.RetrieveAsync("savedquery", view, new Microsoft.Xrm.Sdk.Query.ColumnSet("ismanaged")))
            .GetAttributeValue<bool?>("ismanaged").Should().BeFalse("a row created here is unmanaged");

        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/data/v9.2/savedqueries({view})")
        {
            Content = new StringContent("""{ "ismanaged": true }""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("ismanaged");

        var fault = await FaultAsync(() => Admin.UpdateAsync(new Entity("savedquery", view) { ["ismanaged"] = true }));
        fault.HttpStatus.Should().Be(400, fault.ToString());
        fault = await FaultAsync(() => Admin.CreateAsync(new Entity("webresource") { ["name"] = "new_/x.js", ["ismanaged"] = false }));
        fault.HttpStatus.Should().Be(400, fault.ToString());
        fault.Message.Should().Contain("ismanaged");

        (await Admin.RetrieveAsync("savedquery", view, new Microsoft.Xrm.Sdk.Query.ColumnSet("ismanaged")))
            .GetAttributeValue<bool?>("ismanaged").Should().BeFalse();
        await Admin.DeleteAsync("savedquery", view);
    }

    // ---- startup upgrade --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Existing_database_receives_migration_and_columns()
    {
        var database = "xrmd_layers_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var connectionString = TestProvider.WithDatabase(fx.ConnectionString, database);
        var factory = TestProvider.Factory(connectionString);
        var options = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
        async Task<MetadataStore> NewStoreAsync()
        {
            var store = new MetadataStore(factory);
            await store.LoadAsync();
            return store;
        }
        async Task<int> CountAsync(string sql)
        {
            await using var conn = await factory.OpenAsync();
            await using var cmd = TestSql.Command(conn, sql);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        await new SchemaMigrator(factory).MigrateAsync();
        await new SystemSeed(await NewStoreAsync(), factory).EnsureAsync(options);
        var fresh = (await NewStoreAsync()).GetEntity("asyncoperation")!.Attributes.OfType<PicklistAttributeMetadata>().Single(a => a.LogicalName == "operationtype");
        fresh.OptionSet.Options.Select(o => o.Value).Should().Contain([207, 208, 211]);

        // PostgreSQL organizations start with the schema of 2.7a, which has the layers: none of an earlier version exists to
        // upgrade, so the new database is checked as it is.
        if (TestProvider.IsPostgres)
        {
            (await TestSql.TableExistsAsync(connectionString, "meta", "component_layer")).Should().BeTrue();
            (await TestSql.TableExistsAsync(connectionString, "meta", "solution_rank")).Should().BeTrue();
            var store = await NewStoreAsync();
            foreach (var table in RowComponentTables)
            {
                store.GetEntity(table)!.Attributes.Should().Contain(a => a.LogicalName == "ismanaged", table);
                (await TestSql.ColumnExistsAsync(connectionString, "data", table, "ismanaged")).Should().BeTrue(table);
            }
            return;
        }

        // An organization of the previous version: no layer tables, schema version 6, no ismanaged column on the component rows.
        var tables = RowComponentTables;
        var inList = string.Join(", ", tables.Select(t => $"'{t}'"));
        await using (var conn = await factory.OpenAsync())
        await using (var cmd = TestSql.Command(conn, $"""
            DROP TABLE meta.component_layer;
            DROP TABLE meta.solution_rank;
            DELETE FROM meta.schema_version WHERE version = 8;
            DELETE FROM meta.attribute WHERE logical_name = 'ismanaged' AND entity_logical_name IN ({inList});
            {string.Concat(tables.Select(t => $"ALTER TABLE data.[{t}] DROP COLUMN ismanaged;\n"))}
            """))
            await cmd.ExecuteNonQueryAsync();
        await using (var conn = await factory.OpenAsync())
        await using (var cmd = TestSql.Command(conn, "INSERT INTO data.savedquery (savedqueryid, name) VALUES (NEWID(), N'Made before plan 2.6')"))
            await cmd.ExecuteNonQueryAsync();
        (await NewStoreAsync()).GetEntity("role")!.Attributes.Should().NotContain(a => a.LogicalName == "ismanaged");

        await new SchemaMigrator(factory).MigrateAsync();
        await new SystemSeed(await NewStoreAsync(), factory).EnsureAsync(options);

        (await CountAsync("SELECT COUNT(*) FROM meta.schema_version WHERE version = 8")).Should().Be(1);
        (await TestSql.TableExistsAsync(connectionString, "meta", "component_layer")).Should().BeTrue();
        (await TestSql.TableExistsAsync(connectionString, "meta", "solution_rank")).Should().BeTrue();
        var upgraded = await NewStoreAsync();
        foreach (var table in tables) upgraded.GetEntity(table)!.Attributes.Should().Contain(a => a.LogicalName == "ismanaged", table);
        foreach (var table in tables) (await TestSql.ColumnExistsAsync(connectionString, "data", table, "ismanaged")).Should().BeTrue(table);
        (await CountAsync("SELECT COUNT(*) FROM data.savedquery WHERE name = N'Made before plan 2.6' AND ismanaged = 0")).Should().Be(1, "an existing row is unmanaged");
        (await CountAsync("SELECT COUNT(*) FROM data.savedquery WHERE ismanaged IS NULL")).Should().Be(0);
    }
}
