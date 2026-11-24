using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Integration.Tests;

/// <summary>
/// S.2b2: the SDK view of the metadata store (<see cref="SdkMetadataView"/>) over its model reads. Reference identity: two reads
/// of the same table give the same SDK instance. Freshness: after a metadata change the view shows the new definition, also
/// in the session that made it, and after the writes on the model (Task 3).
/// Each test gets its own freshly migrated database, as <see cref="MetadataStoreTests"/>.
/// </summary>
[Collection("xrmd")]
public sealed class SdkMetadataViewTests(XrmdFixture fx) : IAsyncLifetime
{
    private IDbConnectionFactory _factory = null!;
    private string? _database;

    public async Task InitializeAsync()
    {
        var database = _database = "xrmd_view_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        _factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
        await new SchemaMigrator(_factory).MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null) await fx.DropDatabaseAsync(_database);
    }

    private async Task<IMetadataStore> StoreWithProjectAsync()
    {
        var store = new MetadataStore(_factory);
        await store.LoadAsync();
        await store.CreateEntityAsync(
            new EntityMetadata
            {
                LogicalName = "pb_project", SchemaName = "pb_project", EntitySetName = "pb_projects",
                OwnershipType = OwnershipTypes.UserOwned, DisplayName = SystemAttributes.CreateLabel("Project", 1033),
            },
            new StringAttributeMetadata { LogicalName = "pb_name", MaxLength = 200 });
        return store;
    }

    private async Task InSessionAsync(Func<DataSession, Task> write)
    {
        await using var conn = await _factory.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var session = new DataSession(conn, tx);
        try
        {
            await write(session);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            session.RolledBack();
            throw;
        }
        session.Committed();
    }

    [Fact]
    public async Task Reads_of_the_same_table_give_the_same_instance()
    {
        var store = await StoreWithProjectAsync();

        var first = store.GetEntity("pb_project");
        first.Should().NotBeNull();
        store.GetEntity("pb_project").Should().BeSameAs(first);
        store.GetEntityBySetName("pb_projects").Should().BeSameAs(first);
        store.GetEntityByObjectTypeCode(first!.ObjectTypeCode!.Value).Should().BeSameAs(first);
        store.GetAllEntities().Single(e => e.LogicalName == "pb_project").Should().BeSameAs(first);
        store.GetAllEntities().Should().BeSameAs(store.GetAllEntities(), "the snapshot list is cached until metadata changes");
        store.GetTable("pb_project").Should().BeSameAs(store.GetTable("pb_project"));
    }

    [Fact]
    public async Task The_view_lists_are_read_only()
    {
        var store = await StoreWithProjectAsync();

        var all = store.GetAllEntities();
        all.Should().BeAssignableTo<System.Collections.ObjectModel.ReadOnlyCollection<EntityMetadata>>();
        ((ICollection<EntityMetadata>)all).IsReadOnly.Should().BeTrue();
        FluentActions.Invoking(() => ((IList<EntityMetadata>)all)[0] = new EntityMetadata()).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task After_AddAttribute_the_view_shows_the_new_column()
    {
        var store = await StoreWithProjectAsync();
        var before = store.GetEntity("pb_project")!;
        var listBefore = store.GetAllEntities();

        await store.AddAttributeAsync("pb_project", new IntegerAttributeMetadata { LogicalName = "pb_count", SchemaName = "pb_Count" });

        var after = store.GetEntity("pb_project")!;
        after.Should().NotBeSameAs(before);
        after.Attributes.Should().Contain(a => a.LogicalName == "pb_count");
        before.Attributes.Should().NotContain(a => a.LogicalName == "pb_count", "a published definition is never changed in place");
        store.GetEntityBySetName("pb_projects").Should().BeSameAs(after);
        store.GetAllEntities().Should().NotBeSameAs(listBefore);
        store.GetAllEntities().Single(e => e.LogicalName == "pb_project").Should().BeSameAs(after);
        store.GetTable("pb_project")!.Attributes.Should().Contain(c => c.LogicalName == "pb_count");
    }

    [Fact]
    public async Task After_UpdateEntity_the_view_shows_the_new_display_name()
    {
        var store = await StoreWithProjectAsync();
        var before = store.GetEntity("pb_project")!;

        await InSessionAsync(s => store.UpdateEntityAsync(s,
            new EntityMetadata { LogicalName = "pb_project", DisplayName = SystemAttributes.CreateLabel("Programme", 1033) }, default));

        var after = store.GetEntity("pb_project")!;
        after.Should().NotBeSameAs(before);
        after.DisplayName.UserLocalizedLabel.Label.Should().Be("Programme");
        before.DisplayName.UserLocalizedLabel.Label.Should().Be("Project");
        store.GetTable("pb_project")!.DisplayName!.UserLocalizedLabel!.Label.Should().Be("Programme");
    }

    private static LabelSet ModelLabel(string text) => new()
    {
        LocalizedLabels = [new LocalizedText { Label = text, LanguageCode = 1033 }],
        UserLocalizedLabel = new LocalizedText { Label = text, LanguageCode = 1033 },
    };

    [Fact]
    public async Task A_session_read_after_a_write_in_the_same_session_shows_it()
    {
        var store = await StoreWithProjectAsync();

        await InSessionAsync(async s =>
        {
            var before = store.GetEntity(s, "pb_project")!;
            await store.AddAttributeAsync(s, "pb_project", new IntegerAttributeMetadata { LogicalName = "pb_count", SchemaName = "pb_Count" }, default);

            var afterAdd = store.GetEntity(s, "pb_project")!;
            afterAdd.Should().NotBeSameAs(before);
            afterAdd.Attributes.Should().Contain(a => a.LogicalName == "pb_count");
            store.GetEntity(s, "pb_project").Should().BeSameAs(afterAdd);
            store.GetTable(s, "pb_project")!.Attributes.Should().Contain(c => c.LogicalName == "pb_count");
            store.GetEntity("pb_project")!.Attributes.Should().NotContain(a => a.LogicalName == "pb_count", "published reads wait for the commit");

            // A write on the model member, read back through the view in the same session.
            await store.UpdateEntityAsync(s, new TableDefinition { LogicalName = "pb_project", DisplayName = ModelLabel("Programme") }, default);

            store.GetEntity(s, "pb_project")!.DisplayName.UserLocalizedLabel.Label.Should().Be("Programme");
            store.GetEntity(s, "pb_project")!.Attributes.Should().Contain(a => a.LogicalName == "pb_count");
            store.GetTable(s, "pb_project")!.DisplayName!.UserLocalizedLabel!.Label.Should().Be("Programme");
        });

        store.GetEntity("pb_project")!.DisplayName.UserLocalizedLabel.Label.Should().Be("Programme");
        store.GetEntity("pb_project")!.Attributes.Should().Contain(a => a.LogicalName == "pb_count");
    }

    [Fact]
    public async Task After_UpdateOptionSet_the_view_shows_the_new_definition()
    {
        var store = await StoreWithProjectAsync();
        await InSessionAsync(s => store.CreateOptionSetAsync(s, new OptionSetMetadata(new OptionMetadataCollection(
            [new OptionMetadata(SystemAttributes.CreateLabel("Low", 1033), 100000000)]))
        {
            Name = "pb_priority", DisplayName = SystemAttributes.CreateLabel("Priority", 1033),
        }, default));
        var before = (OptionSetMetadata)store.GetOptionSet("pb_priority")!;
        var modelBefore = store.GetChoiceSet("pb_priority")!;

        // Through the SDK overload, then through the model member.
        await InSessionAsync(s => store.UpdateOptionSetAsync(s, new OptionSetMetadata(new OptionMetadataCollection(
            [new OptionMetadata(SystemAttributes.CreateLabel("High", 1033), 100000001)])) { Name = "pb_priority" }, default));

        var after = (OptionSetMetadata)store.GetOptionSet("pb_priority")!;
        after.Should().NotBeSameAs(before);
        after.Options.Select(o => o.Value).Should().Equal(100000000, 100000001);
        before.Options.Select(o => o.Value).Should().Equal([100000000], "a published definition is never changed in place");
        store.GetChoiceSet("pb_priority").Should().NotBeSameAs(modelBefore);
        ((ChoiceSet)store.GetChoiceSet("pb_priority")!).Options!.Select(o => o.Value).Should().Equal(100000000, 100000001);
        store.GetAllOptionSets().Single(o => o.Name == "pb_priority").Should().BeSameAs(after);

        await InSessionAsync(s => store.UpdateOptionSetAsync(s,
            new ChoiceSet { Name = "pb_priority", Description = ModelLabel("How urgent") }, default));

        store.GetOptionSet("pb_priority")!.Description.UserLocalizedLabel.Label.Should().Be("How urgent");
        store.GetChoiceSet("pb_priority")!.Description!.UserLocalizedLabel!.Label.Should().Be("How urgent");
        ((OptionSetMetadata)store.GetOptionSet("pb_priority")!).Options.Should().HaveCount(2);
    }

    [Fact]
    public async Task After_a_relationship_update_the_view_shows_the_new_definition()
    {
        var store = await StoreWithProjectAsync();
        await store.CreateEntityAsync(
            new TableDefinition
            {
                LogicalName = "pb_parent", SchemaName = "pb_Parent", EntitySetName = "pb_parents",
                OwnershipType = TableOwnership.UserOwned, DisplayName = ModelLabel("Parent"),
            },
            new TextColumn { LogicalName = "pb_name", MaxLength = 100 });
        await InSessionAsync(async s =>
        {
            await store.CreateOneToManyAsync(s,
                new OneToManyRelationshipMetadata { SchemaName = "pb_parent_pb_project", ReferencedEntity = "pb_parent", ReferencingEntity = "pb_project" },
                new LookupAttributeMetadata { LogicalName = "pb_parentid", SchemaName = "pb_ParentId" }, default);
            await store.CreateManyToManyAsync(s,
                new ManyToManyRelationship { SchemaName = "pb_parent_pb_project_links", Entity1LogicalName = "pb_parent", Entity2LogicalName = "pb_project" },
                default);
        });

        foreach (var name in (string[])["pb_parent_pb_project", "pb_parent_pb_project_links"])
        {
            var before = store.GetRelationship(name)!;
            var modelBefore = store.GetRelationshipDefinition(name)!;
            before.IsManaged.Should().NotBe(true, name);

            await InSessionAsync(async s => (await store.SetRelationshipIsManagedAsync(s, name, true, default)).Should().BeTrue(name));

            var after = store.GetRelationship(name)!;
            after.Should().NotBeSameAs(before, name);
            after.IsManaged.Should().BeTrue(name);
            before.IsManaged.Should().NotBe(true, "a published definition is never changed in place");
            store.GetRelationshipDefinition(name).Should().NotBeSameAs(modelBefore, name);
            store.GetRelationshipDefinition(name)!.IsManaged.Should().BeTrue(name);
        }
        store.GetOneToManyRelationships().Single(r => r.SchemaName == "pb_parent_pb_project").IsManaged.Should().BeTrue();
        store.GetManyToManyRelationships().Single(r => r.SchemaName == "pb_parent_pb_project_links").IsManaged.Should().BeTrue();
    }
}
