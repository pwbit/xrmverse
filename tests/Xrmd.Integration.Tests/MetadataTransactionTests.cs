using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Metadata writes in a caller's <see cref="DataSession"/>: nothing is published before <see cref="DataSession.Committed"/>,
/// everything (rows, DDL, catalogs, cache) is gone after a rollback, deletes take their dependent rows with them.
/// Runs on the host's store, so the privilege and SDK message catalogs take part as in production.
/// </summary>
[Collection("xrmd")]
public sealed class MetadataTransactionTests(XrmdFixture fx)
{
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private static string NewName() => "pb_tx" + Guid.NewGuid().ToString("N")[..12];

    private static EntityMetadata Entity(string logicalName, params AttributeMetadata[] attributes) =>
        TestHelpers.Entity(logicalName, "pb_Tx" + logicalName[5..], "Tx", attributes);

    private static StringAttributeMetadata PrimaryName() => new() { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 100 };

    /// <summary>A session on a connection and transaction of the test's own; the test commits or rolls back.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private Session(DbConnection connection, DbTransaction transaction)
        {
            Connection = connection;
            Transaction = transaction;
            Data = new DataSession(connection, transaction);
        }

        public DbConnection Connection { get; }
        public DbTransaction Transaction { get; }
        public DataSession Data { get; }
        private bool _done;

        public static async Task<Session> OpenAsync(string connectionString)
        {
            var conn = await TestProvider.OpenAsync(connectionString);
            return new Session(conn, await conn.BeginTransactionAsync());
        }

        public async Task CommitAsync()
        {
            await Transaction.CommitAsync();
            _done = true;
            Data.Committed();
        }

        public async Task RollbackAsync()
        {
            await Transaction.RollbackAsync();
            _done = true;
            Data.RolledBack();
        }

        public async ValueTask DisposeAsync()
        {
            if (!_done) await RollbackAsync();
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? -1 : Convert.ToInt64(result);
    }

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> TableExistsAsync(string name) =>
        await TestSql.TableExistsAsync(fx.ConnectionString, "data", name) ? 1 : 0;

    private Task<long> ColumnLengthAsync(string table, string column) =>
        ScalarAsync("SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'data' AND TABLE_NAME = @t AND COLUMN_NAME = @c",
            ("@t", table), ("@c", column));

    [Fact]
    public async Task Entity_created_in_a_rolled_back_session_leaves_no_table_and_no_cache_entry()
    {
        var name = NewName();
        var schema = Entity(name).SchemaName;
        var stamp = Store.VersionStamp;

        await using (var session = await Session.OpenAsync(fx.ConnectionString))
        {
            await Store.CreateEntityAsync(session.Data, Entity(name), PrimaryName(), default);
            await Store.AddAttributeAsync(session.Data, name, new IntegerAttributeMetadata { LogicalName = "pb_count" }, default);
            await session.RollbackAsync();
        }

        Store.GetEntity(name).Should().BeNull();
        Store.GetAllEntities().Should().NotContain(e => e.LogicalName == name);
        (await TableExistsAsync(name)).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.entity WHERE logical_name = @n", ("@n", name))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.attribute WHERE entity_logical_name = @n", ("@n", name))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name = @p", ("@p", "prvCreate" + schema))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessagefilter WHERE primaryobjecttypecode = @n", ("@n", name))).Should().Be(0);
        Store.VersionStamp.Should().Be(stamp);

        // The write lock was released: a write of another session goes through.
        await Store.CreateEntityAsync(Entity(name), PrimaryName());
        Store.GetEntity(name).Should().NotBeNull();
    }

    [Fact]
    public async Task Entity_created_in_a_committed_session_is_published_after_commit()
    {
        var name = NewName();
        var schema = Entity(name).SchemaName;
        var stamp = Store.VersionStamp;

        await using (var session = await Session.OpenAsync(fx.ConnectionString))
        {
            await Store.CreateEntityAsync(session.Data, Entity(name), PrimaryName(), default);
            await Store.AddAttributeAsync(session.Data, name, new IntegerAttributeMetadata { LogicalName = "pb_count" }, default);

            Store.GetEntity(name).Should().BeNull("nothing is published before the commit");
            Store.VersionStamp.Should().Be(stamp);

            await session.CommitAsync();
        }

        var entity = Store.GetEntity(name);
        entity.Should().NotBeNull();
        entity!.Attributes.Should().Contain(a => a.LogicalName == "pb_count").And.Contain(a => a.LogicalName == "pb_name");
        Store.GetEntityBySetName(entity.EntitySetName).Should().BeSameAs(entity);
        Store.GetEntityByObjectTypeCode(entity.ObjectTypeCode!.Value).Should().BeSameAs(entity);
        Store.VersionStamp.Should().BeGreaterThan(stamp);
        (await TableExistsAsync(name)).Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name = @p", ("@p", "prvCreate" + schema))).Should().Be(1);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilegeobjecttypecodes WHERE objecttypecode = @n", ("@n", name))).Should().Be(8);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessagefilter WHERE primaryobjecttypecode = @n", ("@n", name)))
            .Should().Be(SdkMessageCatalog.EntityMessages.Count(m => m != "Upsert") + SdkMessageCatalog.MergePairs.Count);
    }

    [Fact]
    public async Task Attribute_and_entity_delete_drop_column_and_table()
    {
        var name = NewName();
        var schema = Entity(name).SchemaName;
        await Store.CreateEntityAsync(Entity(name, new MoneyAttributeMetadata { LogicalName = "pb_cost" }), PrimaryName());

        // A step (with an image) on the entity's Create filter and a role granted one of its privileges.
        var stepId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        await ExecAsync("""
            INSERT INTO data.sdkmessageprocessingstep (sdkmessageprocessingstepid, name, sdkmessageid, sdkmessagefilterid, stage, mode, rank, statecode, statuscode)
            VALUES (@step, N'tx step', @message, @filter, 20, 0, 1, 0, 1);
            INSERT INTO data.sdkmessageprocessingstepimage (sdkmessageprocessingstepimageid, name, sdkmessageprocessingstepid, imagetype, entityalias, messagepropertyname)
            VALUES (@image, N'tx image', @step, 0, N'pre', N'Target');
            INSERT INTO data.roleprivileges (roleprivilegesid, roleid, privilegeid, privilegedepthmask) VALUES (NEWID(), @role, @privilege, 8);
            """,
            ("@step", stepId), ("@image", imageId), ("@message", SdkMessageCatalog.MessageId("Create")),
            ("@filter", SdkMessageCatalog.FilterId("Create", name)), ("@role", roleId), ("@privilege", PrivilegeNames.IdOf("prvRead" + schema)));

        await using (var session = await Session.OpenAsync(fx.ConnectionString))
        {
            await Store.DeleteAttributeAsync(session.Data, name, "pb_cost", default);
            Store.GetEntity(name)!.Attributes.Should().Contain(a => a.LogicalName == "pb_cost", "nothing is published before the commit");
            await session.CommitAsync();
        }

        Store.GetEntity(name)!.Attributes.Should().NotContain(a => a.LogicalName == "pb_cost" || a.LogicalName == "pb_cost_base");
        (await ScalarAsync("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'data' AND TABLE_NAME = @t AND COLUMN_NAME LIKE 'pb[_]cost%'",
            ("@t", name))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.attribute WHERE entity_logical_name = @n AND logical_name LIKE 'pb[_]cost%'", ("@n", name)))
            .Should().Be(0);

        await using (var session = await Session.OpenAsync(fx.ConnectionString))
        {
            await Store.DeleteEntityAsync(session.Data, name, default);
            Store.GetEntity(name).Should().NotBeNull("nothing is published before the commit");
            await session.CommitAsync();
        }

        Store.GetEntity(name).Should().BeNull();
        Store.GetAllEntities().Should().NotContain(e => e.LogicalName == name);
        (await TableExistsAsync(name)).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.entity WHERE logical_name = @n", ("@n", name))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.attribute WHERE entity_logical_name = @n", ("@n", name))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name LIKE @p", ("@p", "prv%" + schema))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilegeobjecttypecodes WHERE objecttypecode = @n", ("@n", name))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = @r", ("@r", roleId))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessagefilter WHERE primaryobjecttypecode = @n", ("@n", name))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @s", ("@s", stepId))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstepimage WHERE sdkmessageprocessingstepimageid = @i", ("@i", imageId)))
            .Should().Be(0);
    }

    [Fact]
    public async Task Delete_of_a_system_entity_or_attribute_is_refused()
    {
        var name = NewName();
        await Store.CreateEntityAsync(Entity(name), PrimaryName());

        await using var session = await Session.OpenAsync(fx.ConnectionString);
        foreach (var delete in new Func<Task>[]
                 {
                     () => Store.DeleteEntityAsync(session.Data, "systemuser", default),
                     () => Store.DeleteAttributeAsync(session.Data, name, "createdon", default),
                     () => Store.DeleteAttributeAsync(session.Data, name, name + "id", default),
                     () => Store.DeleteAttributeAsync(session.Data, name, "pb_name", default),
                     () => Store.DeleteAttributeAsync(session.Data, "systemuser", "fullname", default),
                 })
        {
            var fault = (await delete.Should().ThrowAsync<XrmdFaultException>()).Which;
            fault.HttpStatus.Should().Be(403);
            fault.ErrorCode.Should().Be(ErrorCodes.PrivilegeDenied);
        }
        await session.RollbackAsync();

        Store.GetEntity("systemuser")!.Attributes.Should().Contain(a => a.LogicalName == "fullname");
        Store.GetEntity(name)!.Attributes.Should().Contain(a => a.LogicalName == "createdon");
        (await TableExistsAsync("systemuser")).Should().Be(1);
    }

    [Fact]
    public async Task Delete_of_an_entity_targeted_by_a_lookup_of_another_entity_is_refused()
    {
        var target = NewName();
        var referencing = NewName();
        await Store.CreateEntityAsync(Entity(target), PrimaryName());
        await Store.CreateEntityAsync(Entity(referencing, new LookupAttributeMetadata { LogicalName = "pb_targetid", Targets = [target] }),
            PrimaryName());

        await using (var session = await Session.OpenAsync(fx.ConnectionString))
        {
            var fault = (await FluentActions.Awaiting(() => Store.DeleteEntityAsync(session.Data, target, default))
                .Should().ThrowAsync<XrmdFaultException>()).Which;
            fault.HttpStatus.Should().Be(400);
            fault.Message.Should().Be($"The component cannot be deleted because it is referenced by attribute '{referencing}.pb_targetid'.");
            await session.RollbackAsync();
        }

        // Once the lookup is gone, the entity can be deleted (both in one session).
        await using (var session = await Session.OpenAsync(fx.ConnectionString))
        {
            await Store.DeleteAttributeAsync(session.Data, referencing, "pb_targetid", default);
            await Store.DeleteEntityAsync(session.Data, target, default);
            await session.CommitAsync();
        }
        Store.GetEntity(target).Should().BeNull();
        (await TableExistsAsync(target)).Should().Be(0);
    }

    [Fact]
    public async Task Entity_and_attribute_updates_change_labels_length_and_local_options()
    {
        var name = NewName();
        await Store.CreateEntityAsync(Entity(name,
            new PicklistAttributeMetadata
            {
                LogicalName = "pb_priority",
                OptionSet = new OptionSetMetadata(new OptionMetadataCollection
                {
                    new OptionMetadata(SystemAttributes.CreateLabel("Low", 1033), 1),
                    new OptionMetadata(SystemAttributes.CreateLabel("High", 1033), 2),
                }) { IsGlobal = false, OptionSetType = OptionSetType.Picklist },
            }), PrimaryName());
        var setName = Store.GetEntity(name)!.EntitySetName;

        await using (var session = await Session.OpenAsync(fx.ConnectionString))
        {
            await Store.UpdateEntityAsync(session.Data,
                new EntityMetadata { LogicalName = name, DisplayName = SystemAttributes.CreateLabel("Renamed", 1033) }, default);
            await Store.UpdateAttributeAsync(session.Data, name,
                new StringAttributeMetadata
                {
                    LogicalName = "pb_name", MaxLength = 300,
                    RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired),
                }, default);
            await Store.UpdateAttributeAsync(session.Data, name,
                new PicklistAttributeMetadata
                {
                    LogicalName = "pb_priority",
                    OptionSet = new OptionSetMetadata(new OptionMetadataCollection
                    {
                        new OptionMetadata(SystemAttributes.CreateLabel("Urgent", 1033), 2),
                        new OptionMetadata(SystemAttributes.CreateLabel("Medium", 1033), 3),
                    }),
                }, default);

            await FluentActions.Awaiting(() => Store.UpdateEntityAsync(session.Data, new EntityMetadata { LogicalName = name, EntitySetName = "pb_others" }, default))
                .Should().ThrowAsync<XrmdValidationException>();
            await FluentActions.Awaiting(() => Store.UpdateAttributeAsync(session.Data, name, new StringAttributeMetadata { LogicalName = "pb_name", MaxLength = 50 }, default))
                .Should().ThrowAsync<XrmdValidationException>();
            await FluentActions.Awaiting(() => Store.UpdateAttributeAsync(session.Data, name, new IntegerAttributeMetadata { LogicalName = "pb_name" }, default))
                .Should().ThrowAsync<XrmdValidationException>();

            Store.GetEntity(name)!.DisplayName.UserLocalizedLabel.Label.Should().Be("Tx", "nothing is published before the commit");
            await session.CommitAsync();
        }

        var entity = Store.GetEntity(name)!;
        entity.DisplayName.UserLocalizedLabel.Label.Should().Be("Renamed");
        entity.EntitySetName.Should().Be(setName);
        var primary = entity.Attributes.OfType<StringAttributeMetadata>().Single(a => a.LogicalName == "pb_name");
        primary.MaxLength.Should().Be(300);
        primary.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.ApplicationRequired);
        // PostgreSQL's citext has no length (the metadata's MaxLength is the limit there).
        (await ColumnLengthAsync(name, "pb_name")).Should().Be(TestProvider.IsPostgres ? -1 : 300);
        var options = entity.Attributes.OfType<PicklistAttributeMetadata>().Single().OptionSet.Options;
        options.Select(o => (o.Value, o.Label.UserLocalizedLabel.Label)).Should().Equal((1, "Low"), (2, "Urgent"), (3, "Medium"));

        // The store reloaded from meta.* sees the same.
        var reloaded = new MetadataStore(fx.App.Services.GetRequiredService<IDbConnectionFactory>());
        await reloaded.LoadAsync();
        reloaded.GetEntity(name)!.DisplayName.UserLocalizedLabel.Label.Should().Be("Renamed");
        reloaded.GetEntity(name)!.Attributes.OfType<PicklistAttributeMetadata>().Single().OptionSet.Options.Should().HaveCount(3);
    }

    [Fact]
    public async Task Creating_an_existing_component_throws_the_dedicated_duplicate_exception()
    {
        var name = NewName();
        await Store.CreateEntityAsync(Entity(name), PrimaryName());

        await FluentActions.Awaiting(() => Store.CreateEntityAsync(Entity(name), PrimaryName()))
            .Should().ThrowAsync<MetadataComponentExistsException>().WithMessage($"*{name}*");
        var sameSet = Entity(NewName());
        sameSet.EntitySetName = Store.GetEntity(name)!.EntitySetName;
        await FluentActions.Awaiting(() => Store.CreateEntityAsync(sameSet, PrimaryName()))
            .Should().ThrowAsync<MetadataComponentExistsException>();
        await FluentActions.Awaiting(() => Store.AddAttributeAsync(name, new IntegerAttributeMetadata { LogicalName = "pb_name" }))
            .Should().ThrowAsync<MetadataComponentExistsException>();

        await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteEntityAsync(s, name, default));
    }

    [Fact]
    public async Task Concurrent_reader_never_sees_half_published_metadata()
    {
        var names = new[] { NewName(), NewName(), NewName() }.ToHashSet();
        var seen = new ConcurrentBag<int>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                seen.Add(Store.GetAllEntities().Count(e => names.Contains(e.LogicalName)));
                started.TrySetResult();
            }
        });
        // The reader has read before the first write: it sees the state before the session.
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await using (var session = await Session.OpenAsync(fx.ConnectionString))
        {
            foreach (var name in names)
            {
                await Store.CreateEntityAsync(session.Data, Entity(name), PrimaryName(), default);
                await Task.Delay(20);
            }
            await session.CommitAsync();
        }
        // Poll (with a timeout) until the reader has seen the published state, instead of a fixed delay.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!seen.Contains(3) && DateTime.UtcNow < deadline) await Task.Delay(10);
        await stop.CancelAsync();
        await reader;

        seen.Should().OnlyContain(count => count == 0 || count == 3);
        seen.Should().Contain(0).And.Contain(3);
    }

    /// <summary>Calls back into its store from inside the session that creates an entity (while <see cref="Enabled"/>).</summary>
    private sealed class ReentrantHandler(Func<MetadataStore, Task> call) : IEntityCreatedHandler
    {
        public MetadataStore Store { get; set; } = null!;
        public bool Enabled { get; set; } = true;

        public Task OnEntityCreatedAsync(EntityMetadata entity, DataSession session, CancellationToken ct) =>
            Enabled ? call(Store) : Task.CompletedTask;
    }

    [Fact]
    public async Task A_store_call_that_would_wait_for_the_session_holding_the_write_lock_throws_instead_of_hanging()
    {
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        var calls = new Func<MetadataStore, Task>[]
        {
            store => store.LoadAsync(),
            store => store.AddAttributeAsync("systemuser", new IntegerAttributeMetadata { LogicalName = "pb_never" }),
            store => store.CreateEntityAsync(Entity(NewName()), PrimaryName()),
            async store =>
            {
                // A session overload with another session would wait for the lock as well.
                await using var other = await Session.OpenAsync(fx.ConnectionString);
                await store.CreateEntityAsync(other.Data, Entity(NewName()), PrimaryName(), default);
            },
        };
        foreach (var call in calls)
        {
            var handler = new ReentrantHandler(call);
            var store = new MetadataStore(factory, [handler]);
            handler.Store = store;
            await store.LoadAsync();
            var name = NewName();

            var create = store.CreateEntityAsync(Entity(name), PrimaryName());
            (await Task.WhenAny(create, Task.Delay(TimeSpan.FromSeconds(30))))
                .Should().BeSameAs(create, "a call that needs the lock its own call path holds must fail, not wait");
            (await FluentActions.Awaiting(() => create).Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("A metadata session is already in progress on this call path."))
                .Which.Should().NotBeOfType<MetadataComponentExistsException>("only an existing component is a duplicate (412)");
            store.GetEntity(name).Should().BeNull();
            (await TableExistsAsync(name)).Should().Be(0);

            // The lock was released and the marker does not outlive the call path: the next write goes through.
            handler.Enabled = false;
            await store.CreateEntityAsync(Entity(name), PrimaryName());
            store.GetEntity(name).Should().NotBeNull();
            await store.LoadAsync();

            // Clean up: this store has no catalog handlers, so the entity must not linger for the host's store.
            await using var cleanup = await Session.OpenAsync(fx.ConnectionString);
            await store.DeleteEntityAsync(cleanup.Data, name, default);
            await cleanup.CommitAsync();
        }
    }

    [Fact]
    public async Task Update_in_the_creating_transaction_names_the_component()
    {
        var name = NewName();
        var ops = fx.App.Services.GetRequiredService<MetadataOperations>();
        await using var session = await Session.OpenAsync(fx.ConnectionString);
        await ops.ExecuteAsync(new CreateEntityRequest { Entity = Entity(name), PrimaryAttribute = PrimaryName() }, Caller.Internal, session.Data, default);
        await ops.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new IntegerAttributeMetadata { LogicalName = "pb_count", SchemaName = "pb_Count" },
        }, Caller.Internal, session.Data, default);

        var entity = await FluentActions.Awaiting(() => ops.ExecuteAsync(
                new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = name } }, Caller.Internal, session.Data, default))
            .Should().ThrowAsync<XrmdFaultException>();
        entity.Which.HttpStatus.Should().Be(400);
        entity.Which.Message.Should().Be(
            $"Entity '{name}' was created in this transaction and cannot be updated before it commits; send the final definition in the create request.");

        var attribute = await FluentActions.Awaiting(() => ops.ExecuteAsync(
                new UpdateAttributeRequest { EntityName = name, Attribute = new IntegerAttributeMetadata { LogicalName = "pb_count" } },
                Caller.Internal, session.Data, default))
            .Should().ThrowAsync<XrmdFaultException>();
        attribute.Which.Message.Should().Be(
            $"Attribute '{name}.pb_count' was created in this transaction and cannot be updated before it commits; send the final definition in the create request.");
    }

    [Fact]
    public async Task Making_an_image_created_in_the_transaction_primary_is_the_one_update_it_takes()
    {
        // A create ignores IsPrimaryImage = true when the table has an image column (Learn), so a schema apply or a
        // solution import that creates two images and wants the second primary must update it in the same transaction.
        var name = NewName();
        var ops = fx.App.Services.GetRequiredService<MetadataOperations>();
        await using var session = await Session.OpenAsync(fx.ConnectionString);
        await ops.ExecuteAsync(new CreateEntityRequest { Entity = Entity(name), PrimaryAttribute = PrimaryName() }, Caller.Internal, session.Data, default);
        foreach (var image in new[] { "pb_pic1", "pb_pic2" })
            await ops.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name,
                Attribute = new ImageAttributeMetadata { LogicalName = image, SchemaName = "pb_" + image[3..], IsPrimaryImage = true },
            }, Caller.Internal, session.Data, default);

        var other = await FluentActions.Awaiting(() => ops.ExecuteAsync(
                new UpdateAttributeRequest { EntityName = name, Attribute = new ImageAttributeMetadata { LogicalName = "pb_pic2", IsPrimaryImage = true, MaxSizeInKB = 20 } },
                Caller.Internal, session.Data, default))
            .Should().ThrowAsync<XrmdFaultException>();
        other.Which.Message.Should().Be(
            $"Attribute '{name}.pb_pic2' was created in this transaction and cannot be updated before it commits; send the final definition in the create request.");

        await ops.ExecuteAsync(
            new UpdateAttributeRequest { EntityName = name, Attribute = new ImageAttributeMetadata { LogicalName = "pb_pic2", IsPrimaryImage = true } },
            Caller.Internal, session.Data, default);
        await session.CommitAsync();

        var images = Store.GetEntity(name)!.Attributes.OfType<ImageAttributeMetadata>().ToDictionary(i => i.LogicalName, i => i.IsPrimaryImage);
        images.Should().BeEquivalentTo(new Dictionary<string, bool?> { ["pb_pic1"] = false, ["pb_pic2"] = true });
    }
}
