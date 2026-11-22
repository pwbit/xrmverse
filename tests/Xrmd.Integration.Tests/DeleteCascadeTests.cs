using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The delete behavior of saved one-to-many relationships, enforced in the transaction of the Delete: Cascade deletes the
/// records that point to the deleted one (recursively, each as a Delete of the pipeline: its privilege check, its plug-in
/// steps, its own cascades), Restrict refuses the Delete while such records exist, RemoveLink clears their lookup; a cycle or
/// a self-reference is deleted once.
/// </summary>
[Collection("xrmd")]
public sealed class DeleteCascadeTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    private async Task<string> NewTableAsync(string label)
    {
        var name = $"new_{label}{_run}";
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = name, SchemaName = name, DisplayName = Label("Table " + name), DisplayCollectionName = Label("Tables " + name),
                OwnershipType = OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name"),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
        });
        return name;
    }

    /// <summary>A lookup <paramref name="lookup"/> on <paramref name="referencing"/> to <paramref name="referenced"/>; returns the relationship's name.</summary>
    private async Task<string> RelateAsync(string referenced, string referencing, string lookup, CascadeType delete)
    {
        var schemaName = $"new_{referencing}_{lookup}";
        await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = schemaName, ReferencedEntity = referenced, ReferencingEntity = referencing,
                CascadeConfiguration = new CascadeConfiguration { Delete = delete },
            },
            Lookup = new LookupAttributeMetadata { LogicalName = lookup, SchemaName = lookup, DisplayName = Label("Parent") },
        });
        return schemaName;
    }

    private Task<Guid> CreateAsync(string table, string name, params (string Lookup, EntityReference Value)[] lookups)
    {
        var row = new Entity(table) { ["new_name"] = name };
        foreach (var (lookup, value) in lookups) row[lookup] = value;
        return Admin.CreateAsync(row);
    }

    private async Task<bool> ExistsAsync(string table, Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT COUNT(*) FROM data.[{table}] WHERE [{table}id] = @id");
        cmd.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    [Fact]
    public async Task Cascade_deletes_the_children_recursively_and_their_own_remove_links_apply()
    {
        var parent = await NewTableAsync("cp");
        var child = await NewTableAsync("cc");
        var grandchild = await NewTableAsync("cg");
        var linked = await NewTableAsync("cl");
        await RelateAsync(parent, child, "new_parentid", CascadeType.Cascade);
        await RelateAsync(child, grandchild, "new_childid", CascadeType.Cascade);
        await RelateAsync(child, linked, "new_childid", CascadeType.RemoveLink);

        var p = await CreateAsync(parent, "p");
        var other = await CreateAsync(parent, "other");
        var c1 = await CreateAsync(child, "c1", ("new_parentid", new EntityReference(parent, p)));
        var c2 = await CreateAsync(child, "c2", ("new_parentid", new EntityReference(parent, p)));
        var kept = await CreateAsync(child, "kept", ("new_parentid", new EntityReference(parent, other)));
        var g = await CreateAsync(grandchild, "g", ("new_childid", new EntityReference(child, c1)));
        var l = await CreateAsync(linked, "l", ("new_childid", new EntityReference(child, c2)));

        await Admin.DeleteAsync(parent, p);

        (await ExistsAsync(parent, p)).Should().BeFalse();
        (await ExistsAsync(child, c1)).Should().BeFalse();
        (await ExistsAsync(child, c2)).Should().BeFalse();
        (await ExistsAsync(grandchild, g)).Should().BeFalse();
        (await ExistsAsync(child, kept)).Should().BeTrue();
        var survivor = await Admin.RetrieveAsync(linked, l, new ColumnSet("new_childid", "new_name"));
        survivor.GetAttributeValue<string>("new_name").Should().Be("l");
        survivor.GetAttributeValue<EntityReference>("new_childid").Should().BeNull();
        // A missing record is a 404, and nothing is deleted on its behalf.
        (await FaultAsync(() => Admin.DeleteAsync(parent, p))).HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task Cascade_on_a_self_referential_relationship_deletes_a_cycle_and_a_self_reference_once()
    {
        var table = await NewTableAsync("self");
        await RelateAsync(table, table, "new_parentid", CascadeType.Cascade);
        var a = await CreateAsync(table, "a");
        var b = await CreateAsync(table, "b", ("new_parentid", new EntityReference(table, a)));
        var c = await CreateAsync(table, "c", ("new_parentid", new EntityReference(table, b)));
        await Admin.UpdateAsync(new Entity(table, a) { ["new_parentid"] = new EntityReference(table, c) }); // a → c → b → a
        var self = await CreateAsync(table, "self");
        await Admin.UpdateAsync(new Entity(table, self) { ["new_parentid"] = new EntityReference(table, self) });
        var alone = await CreateAsync(table, "alone");

        await Admin.DeleteAsync(table, a);
        await Admin.DeleteAsync(table, self);

        foreach (var id in new[] { a, b, c, self }) (await ExistsAsync(table, id)).Should().BeFalse();
        (await ExistsAsync(table, alone)).Should().BeTrue();
    }

    [Fact]
    public async Task Restrict_refuses_the_delete_and_a_refused_cascade_rolls_everything_back()
    {
        var parent = await NewTableAsync("rp");
        var restricted = await NewTableAsync("rr");
        var relationship = await RelateAsync(parent, restricted, "new_parentid", CascadeType.Restrict);
        var p = await CreateAsync(parent, "p");
        var r = await CreateAsync(restricted, "r", ("new_parentid", new EntityReference(parent, p)));

        (await BadRequestAsync(() => Admin.DeleteAsync(parent, p))).Should().Contain(relationship).And.Contain("Restrict");
        (await ExistsAsync(parent, p)).Should().BeTrue();
        // Without the record that points to it, the delete goes through.
        await Admin.DeleteAsync(restricted, r);
        await Admin.DeleteAsync(parent, p);
        (await ExistsAsync(parent, p)).Should().BeFalse();

        // Restrict one level down: the cascade to the child meets it, and the whole Delete is rolled back.
        var top = await NewTableAsync("rt");
        await RelateAsync(top, parent, "new_topid", CascadeType.Cascade);
        var t = await CreateAsync(top, "t");
        var p2 = await CreateAsync(parent, "p2", ("new_topid", new EntityReference(top, t)));
        var r2 = await CreateAsync(restricted, "r2", ("new_parentid", new EntityReference(parent, p2)));
        (await BadRequestAsync(() => Admin.DeleteAsync(top, t))).Should().Contain(relationship);
        (await ExistsAsync(top, t)).Should().BeTrue();
        (await ExistsAsync(parent, p2)).Should().BeTrue();
        (await ExistsAsync(restricted, r2)).Should().BeTrue();

        // A record that points to itself does not restrict its own delete.
        await RelateAsync(restricted, restricted, "new_selfid", CascadeType.Restrict);
        await Admin.UpdateAsync(new Entity(restricted, r2) { ["new_selfid"] = new EntityReference(restricted, r2) });
        await Admin.DeleteAsync(restricted, r2);
        (await ExistsAsync(restricted, r2)).Should().BeFalse();
    }

    [Fact]
    public async Task A_cascaded_delete_is_a_delete_of_the_pipeline_with_its_privilege_check_and_plug_in_steps()
    {
        var parent = await NewTableAsync("pp");
        var child = await NewTableAsync("pc");
        await RelateAsync(parent, child, "new_parentid", CascadeType.Cascade);

        // A user who may delete the parent table but not the child table cannot delete a parent that has children.
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false,
            ($"prvCreate{parent}", DepthMask.Global), ($"prvRead{parent}", DepthMask.Global), ($"prvDelete{parent}", DepthMask.Global),
            ($"prvAppendTo{parent}", DepthMask.Global),
            ($"prvCreate{child}", DepthMask.Global), ($"prvRead{child}", DepthMask.Global), ($"prvAppend{child}", DepthMask.Global));
        var asUser = ServiceFor(fx, objectId);
        var p = await asUser.CreateAsync(new Entity(parent) { ["new_name"] = "p" });
        var c = await asUser.CreateAsync(new Entity(child) { ["new_name"] = "c", ["new_parentid"] = new EntityReference(parent, p) });
        (await FaultAsync(() => asUser.DeleteAsync(parent, p))).HttpStatus.Should().Be(403);
        (await ExistsAsync(parent, p)).Should().BeTrue();
        (await ExistsAsync(child, c)).Should().BeTrue();
        // Without children, the same user deletes a parent.
        var lonely = await asUser.CreateAsync(new Entity(parent) { ["new_name"] = "lonely" });
        await asUser.DeleteAsync(parent, lonely);

        // A pre-operation step on the Delete of the child table fires for the cascaded delete, and its failure rolls back all.
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Delete", child, stage: 20, config: "invalid:child delete refused");
        (await FaultAsync(() => Admin.DeleteAsync(parent, p))).Message.Should().Be("child delete refused");
        (await ExistsAsync(parent, p)).Should().BeTrue();
        (await ExistsAsync(child, c)).Should().BeTrue();
        await PluginTestHelper.ClearAsync(fx);
        await Admin.DeleteAsync(parent, p);
        (await ExistsAsync(child, c)).Should().BeFalse();
    }
}
