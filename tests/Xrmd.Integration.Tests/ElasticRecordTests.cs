using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.4, task 3: the identity of an elastic row (id + partition id), the single-record writes and their rules, Upsert as
/// replace and the session token, through the organization service.
/// </summary>
[Collection("xrmd")]
public sealed class ElasticRecordTests(XrmdFixture fx)
{
    private const string SessionToken = "x-ms-session-token";
    private static readonly SemaphoreSlim TableLock = new(1, 1);
    private static string? _table;

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    /// <summary>The elastic user-owned table <c>new_sensordata…</c> with <c>new_value</c> (Integer) and <c>new_payload</c> (JSON String), created once.</summary>
    private async Task<string> TableAsync()
    {
        await TableLock.WaitAsync();
        try
        {
            if (_table is not null) return _table;
            var name = "new_sensordata" + Guid.NewGuid().ToString("N")[..6];
            var entity = TestHelpers.Entity(name, name, "Sensor data");
            entity.TableType = "Elastic";
            await Admin.ExecuteAsync(new CreateEntityRequest
            {
                Entity = entity,
                PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
            });
            await Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name,
                Attribute = new IntegerAttributeMetadata { LogicalName = "new_value", SchemaName = "new_Value", DisplayName = Label("Value") },
            });
            await Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name,
                Attribute = new StringAttributeMetadata
                {
                    LogicalName = "new_payload", SchemaName = "new_Payload", DisplayName = Label("Payload"), MaxLength = 4000,
                    FormatName = StringFormatName.Json,
                },
            });
            await Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name,
                Attribute = new StringAttributeMetadata
                {
                    LogicalName = "new_secret", SchemaName = "new_Secret", DisplayName = Label("Secret"), MaxLength = 100, IsSecured = true,
                },
            });
            return _table = name;
        }
        finally
        {
            TableLock.Release();
        }
    }

    private static string Pk(string table) => table + "id";

    private static KeyAttributeCollection Keys(string table, Guid id, string? partition) =>
        new() { { Pk(table), id }, { ElasticTables.PartitionId, partition! } };

    private async Task<Guid> CreateRowAsync(string table, string? partition, int value = 1)
    {
        var e = new Entity(table) { ["new_name"] = "row", ["new_value"] = value };
        if (partition is not null) e[ElasticTables.PartitionId] = partition;
        return await Admin.CreateAsync(e);
    }

    private Task<OrganizationResponse> RetrieveAsync(string table, Guid id, string? partition, bool asParameter = false)
    {
        var request = new RetrieveRequest { ColumnSet = new ColumnSet(true) };
        if (partition is null) request.Target = new EntityReference(table, id);
        else if (asParameter)
        {
            request.Target = new EntityReference(table, id);
            request["partitionId"] = partition;
        }
        else request.Target = new EntityReference(table, Keys(table, id, partition));
        return Admin.ExecuteAsync(request);
    }

    private Task<OrganizationResponse> UpdateAsync(string table, Guid id, string? partition, int value)
    {
        var request = new UpdateRequest();
        var target = new Entity(table, id) { ["new_value"] = value };
        if (partition is not null) request["partitionId"] = partition;
        request.Target = target;
        return Admin.ExecuteAsync(request);
    }

    private Task<OrganizationResponse> DeleteAsync(string table, Guid id, string? partition) =>
        Admin.ExecuteAsync(new DeleteRequest
        {
            Target = partition is null ? new EntityReference(table, id) : new EntityReference(table, Keys(table, id, partition)),
        });

    private async Task<Entity?> ReadAsync(string table, Guid id)
    {
        var rows = await Admin.RetrieveMultipleAsync(new QueryExpression(table)
        {
            ColumnSet = new ColumnSet(true), Criteria = { Conditions = { new ConditionExpression(Pk(table), ConditionOperator.Equal, id) } },
        });
        return rows.Entities.SingleOrDefault();
    }

    [Fact]
    public async Task Create_with_partition_then_retrieve_by_key_attributes()
    {
        var table = await TableAsync();
        var id = await CreateRowAsync(table, "p1", 7);

        var entity = (Entity)(await RetrieveAsync(table, id, "p1")).Results["Entity"];

        entity.Id.Should().Be(id);
        entity.GetAttributeValue<string>(ElasticTables.PartitionId).Should().Be("p1");
        entity.GetAttributeValue<int>("new_value").Should().Be(7);
    }

    [Fact]
    public async Task Create_with_partition_then_retrieve_by_partitionId_parameter()
    {
        var table = await TableAsync();
        var id = await CreateRowAsync(table, "p1", 8);

        var entity = (Entity)(await RetrieveAsync(table, id, "p1", asParameter: true)).Results["Entity"];

        entity.GetAttributeValue<int>("new_value").Should().Be(8);
    }

    [Fact]
    public async Task Partition_mismatch_is_not_found()
    {
        var table = await TableAsync();
        foreach (var (stored, given) in new[] { ((string?)null, (string?)"p2"), ("p1", null), ("p1", "p2") })
        {
            var id = await CreateRowAsync(table, stored, 1);
            var because = $"stored {stored ?? "null"}, given {given ?? "none"}";
            (await FaultAsync(() => RetrieveAsync(table, id, given))).ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist, because);
            (await FaultAsync(() => RetrieveAsync(table, id, given, asParameter: true))).ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist, because);
            (await FaultAsync(() => UpdateAsync(table, id, given, 2))).ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist, because);
            (await FaultAsync(() => DeleteAsync(table, id, given))).ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist, because);
            var row = await ReadAsync(table, id);
            row.Should().NotBeNull(because);
            row!.GetAttributeValue<int>("new_value").Should().Be(1, because);
        }

        // The right combination works.
        foreach (var partition in new[] { (string?)null, "p1" })
        {
            var id = await CreateRowAsync(table, partition, 1);
            (await RetrieveAsync(table, id, partition)).Results["Entity"].Should().BeOfType<Entity>();
            await UpdateAsync(table, id, partition, 2);
            (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(2);
            await DeleteAsync(table, id, partition);
            (await ReadAsync(table, id)).Should().BeNull();
        }
    }

    [Fact]
    public async Task Partition_with_trailing_space_is_not_found()
    {
        var table = await TableAsync();
        var id = await CreateRowAsync(table, "p1", 1);

        (await FaultAsync(() => RetrieveAsync(table, id, "p1 "))).ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        (await FaultAsync(() => UpdateAsync(table, id, "p1 ", 2))).ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(1);
    }

    [Fact]
    public async Task Same_id_in_other_partition_is_refused()
    {
        var table = await TableAsync();
        var id = await CreateRowAsync(table, "p1");

        var fault = await FaultAsync(() => Admin.CreateAsync(new Entity(table, id) { [ElasticTables.PartitionId] = "p2" }));

        fault.ErrorCode.Should().Be(ErrorCodes.DuplicateRecord);
        (await ReadAsync(table, id))!.GetAttributeValue<string>(ElasticTables.PartitionId).Should().Be("p1");
    }

    [Fact]
    public async Task Update_finds_the_row_by_its_partitionid_value()
    {
        var table = await TableAsync();
        var id = await CreateRowAsync(table, "p1", 1);

        // As Learn's examples: the partition given only as the partitionid value of the target.
        await Admin.UpdateAsync(new Entity(table, id) { [ElasticTables.PartitionId] = "p1", ["new_value"] = 9 });
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(9);

        // Another partition (exact) does not find it.
        (await FaultAsync(() => Admin.UpdateAsync(new Entity(table, id) { [ElasticTables.PartitionId] = "P1", ["new_value"] = 10 })))
            .ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(9);
    }

    [Fact]
    public async Task Update_partition_precedence_is_key_then_parameter_then_value()
    {
        var table = await TableAsync();
        var id = await CreateRowAsync(table, "p1", 1);

        // The partitionId parameter names the row; the differing value is then a change of partition: refused, never moved.
        var request = new UpdateRequest { Target = new Entity(table, id) { [ElasticTables.PartitionId] = "p2", ["new_value"] = 5 } };
        request["partitionId"] = "p1";
        await BadRequestAsync(() => Admin.ExecuteAsync(request));
        var row = await ReadAsync(table, id);
        row!.GetAttributeValue<string>(ElasticTables.PartitionId).Should().Be("p1");
        row.GetAttributeValue<int>("new_value").Should().Be(1);

        // The key wins over the parameter.
        var keyed = new UpdateRequest { Target = new Entity(table, Keys(table, id, "p1")) { ["new_value"] = 6 } };
        keyed["partitionId"] = "p2";
        await Admin.ExecuteAsync(keyed);
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(6);
    }

    [Fact]
    public async Task Partitionid_is_immutable()
    {
        var table = await TableAsync();
        var id = await CreateRowAsync(table, "p1", 1);

        var update = new Entity(table, Keys(table, id, "p1")) { [ElasticTables.PartitionId] = "p2", ["new_value"] = 5 };
        await BadRequestAsync(() => Admin.UpdateAsync(update));
        var row = await ReadAsync(table, id);
        row!.GetAttributeValue<string>(ElasticTables.PartitionId).Should().Be("p1");
        row.GetAttributeValue<int>("new_value").Should().Be(1);

        await Admin.UpdateAsync(new Entity(table, Keys(table, id, "p1")) { [ElasticTables.PartitionId] = "p1", ["new_value"] = 6 });
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(6);
    }

    [Fact]
    public async Task Ttl_out_of_range_is_refused()
    {
        var table = await TableAsync();
        foreach (var ttl in new[] { 0, -1 })
            await BadRequestAsync(() => Admin.CreateAsync(new Entity(table) { [ElasticTables.TtlInSeconds] = ttl }));
        var id = await Admin.CreateAsync(new Entity(table) { [ElasticTables.TtlInSeconds] = 60 });
        await BadRequestAsync(() => Admin.UpdateAsync(new Entity(table, id) { [ElasticTables.TtlInSeconds] = 0 }));
    }

    [Fact]
    public async Task Invalid_partition_characters_are_refused()
    {
        var table = await TableAsync();
        foreach (var partition in new[] { "a/b", "a:b", "a+b", new string('x', 101) })
            (await BadRequestAsync(() => Admin.CreateAsync(new Entity(table) { [ElasticTables.PartitionId] = partition })))
                .Should().Contain("partitionid");
        await BadRequestAsync(() => RetrieveAsync(table, Guid.NewGuid(), "a?b"));
        await BadRequestAsync(() => RetrieveAsync(table, Guid.NewGuid(), "a%b", asParameter: true));
    }

    [Fact]
    public async Task Invalid_json_in_json_column_is_refused()
    {
        var table = await TableAsync();
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity(table) { ["new_payload"] = "{not json" }))).Should().Contain("new_payload");
        var id = await Admin.CreateAsync(new Entity(table) { ["new_payload"] = """{"a":[1,2]}""" });
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(table, id) { ["new_payload"] = "[1," }))).Should().Contain("new_payload");
        (await ReadAsync(table, id))!.GetAttributeValue<string>("new_payload").Should().Be("""{"a":[1,2]}""");
    }

    [Fact]
    public async Task Deep_insert_is_refused()
    {
        var table = await TableAsync();
        var entity = new Entity(table) { ["new_name"] = "deep" };
        entity.RelatedEntities[new Relationship("new_whatever")] = new EntityCollection([new Entity("account") { ["name"] = "x" }]);

        (await BadRequestAsync(() => Admin.CreateAsync(entity))).Should().Contain("elastic");
    }

    [Fact]
    public async Task GrantAccess_on_elastic_is_refused()
    {
        var table = await TableAsync();
        var id = await CreateRowAsync(table, null);
        var me = ((WhoAmIResponse)await Admin.ExecuteAsync(new WhoAmIRequest())).UserId;

        (await BadRequestAsync(() => Admin.ExecuteAsync(new GrantAccessRequest
        {
            Target = new EntityReference(table, id),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", me), AccessMask = AccessRights.ReadAccess },
        }))).Should().Contain("elastic");
    }

    [Fact]
    public async Task Write_returns_session_token()
    {
        var table = await TableAsync();
        var created = await Admin.ExecuteAsync(new CreateRequest { Target = new Entity(table) { [ElasticTables.PartitionId] = "p1" } });
        var id = (Guid)created.Results["id"];
        created.Results[SessionToken].Should().BeOfType<string>().Which.Should().MatchRegex("^0:[0-9]+$");

        var updated = await Admin.ExecuteAsync(new UpdateRequest { Target = new Entity(table, Keys(table, id, "p1")) { ["new_value"] = 3 } });
        var token = updated.Results[SessionToken].Should().BeOfType<string>().Which;
        token.Should().MatchRegex("^0:[0-9]+$").And.NotBe((string)created.Results[SessionToken]);

        var deleted = await Admin.ExecuteAsync(new DeleteRequest { Target = new EntityReference(table, Keys(table, id, "p1")) });
        deleted.Results.ContainsKey(SessionToken).Should().BeFalse();

        // A standard table's writes carry none.
        var account = await Admin.ExecuteAsync(new CreateRequest { Target = new Entity("account") { ["name"] = "no token" } });
        account.Results.ContainsKey(SessionToken).Should().BeFalse();
    }

    [Fact]
    public async Task SessionToken_parameter_is_ignored()
    {
        var table = await TableAsync();
        var id = await CreateRowAsync(table, "p1", 4);
        var request = new RetrieveRequest { Target = new EntityReference(table, Keys(table, id, "p1")), ColumnSet = new ColumnSet("new_value") };
        request["SessionToken"] = "0:123";

        ((Entity)(await Admin.ExecuteAsync(request)).Results["Entity"]).GetAttributeValue<int>("new_value").Should().Be(4);

        var update = new UpdateRequest { Target = new Entity(table, Keys(table, id, "p1")) { ["new_value"] = 5 } };
        update["SessionToken"] = "anything";
        await Admin.ExecuteAsync(update);
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(5);
    }

    [Fact]
    public async Task Upsert_creates_then_replaces()
    {
        var table = await TableAsync();
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        try
        {
            var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
            var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
            await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", table, 20, config: "invalid:create step fired");
            await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", table, 20, config: "invalid:update step fired");

            var id = Guid.NewGuid();
            var first = (UpsertResponse)await Admin.ExecuteAsync(new UpsertRequest
            {
                Target = new Entity(table, Keys(table, id, "p1")) { ["new_name"] = "first", ["new_value"] = 10, ["new_payload"] = "{}" },
            });
            first.RecordCreated.Should().BeTrue();
            first.Target.Id.Should().Be(id);
            first.Results[SessionToken].Should().BeOfType<string>();

            var second = (UpsertResponse)await Admin.ExecuteAsync(new UpsertRequest
            {
                Target = new Entity(table, Keys(table, id, "p1")) { ["new_name"] = "second" },
            });
            second.RecordCreated.Should().BeFalse();
            var row = await ReadAsync(table, id);
            row!.GetAttributeValue<string>("new_name").Should().Be("second");
            row.Contains("new_value").Should().BeFalse("an upsert replaces the row: a column not given becomes null");
            row.Contains("new_payload").Should().BeFalse();
            row.GetAttributeValue<string>(ElasticTables.PartitionId).Should().Be("p1");

            await PluginTestHelper.RegisterStepAsync(Admin, type, "Upsert", table, 20, config: "invalid:upsert step fired");
            (await FaultAsync(() => Admin.ExecuteAsync(new UpsertRequest { Target = new Entity(table, Keys(table, id, "p1")) { ["new_value"] = 1 } })))
                .Message.Should().Contain("upsert step fired");
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
    }

    [Fact]
    public async Task Upsert_with_the_partitionId_parameter_creates_then_replaces_in_that_partition()
    {
        // Final review I3: the parameter names the partition as it does for Update (key, then parameter, then value).
        var table = await TableAsync();
        var id = Guid.NewGuid();

        var created = (UpsertResponse)await Admin.ExecuteAsync(new UpsertRequest
        {
            Target = new Entity(table, id) { ["new_name"] = "first", ["new_value"] = 4 }, ["partitionId"] = "up-1",
        });
        created.RecordCreated.Should().BeTrue();
        (await ReadAsync(table, id))!.GetAttributeValue<string>(ElasticTables.PartitionId).Should().Be("up-1");

        var replaced = (UpsertResponse)await Admin.ExecuteAsync(new UpsertRequest
        {
            Target = new Entity(table, id) { ["new_name"] = "second" }, ["partitionId"] = "up-1",
        });
        replaced.RecordCreated.Should().BeFalse();
        var row = await ReadAsync(table, id);
        row!.GetAttributeValue<string>("new_name").Should().Be("second");
        row.Contains("new_value").Should().BeFalse();
        row.GetAttributeValue<string>(ElasticTables.PartitionId).Should().Be("up-1");

        // A partitionid value that differs from the partition the parameter names is refused, never moved or dropped.
        await BadRequestAsync(() => Admin.ExecuteAsync(new UpsertRequest
        {
            Target = new Entity(table, id) { [ElasticTables.PartitionId] = "up-2" }, ["partitionId"] = "up-1",
        }));
    }

    [Fact]
    public async Task Upsert_waits_for_a_concurrent_upsert_that_creates_the_same_row_and_updates_it()
    {
        // Plan 2.7b, Task 5: an Upsert's check of a missing row holds its key until the end of the transaction (SQL Server: the
        // key range of HOLDLOCK; PostgreSQL: the key lock), so a second Upsert of the same id waits, then finds the row and
        // updates it instead of creating it a second time.
        var table = await TableAsync();
        var id = Guid.NewGuid();
        await using var scope = fx.App.Services.CreateAsyncScope();
        await using var conn = await scope.ServiceProvider.GetRequiredService<Xrmd.Abstractions.IDbConnectionFactory>().OpenAsync();
        int spid;
        await using (var cmd = TestSql.Command(conn, TestSql.SessionIdQuery)) spid = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Task<OrganizationResponse> second;
        await using (var tx = await conn.BeginTransactionAsync())
        {
            var first = await new Xrmd.Core.Elastic.ElasticUpsert(scope.ServiceProvider.GetRequiredService<CoreOperations>()).ExecuteAsync(
                new Xrmd.Abstractions.DataSession(conn, tx), Xrmd.Abstractions.Caller.Internal,
                new Entity(table, Keys(table, id, "race")) { ["new_name"] = "first" }, new UpsertRequest(), default);
            first.RecordCreated.Should().BeTrue();

            second = Admin.ExecuteAsync(new UpsertRequest { Target = new Entity(table, Keys(table, id, "race")) { ["new_name"] = "second" } });
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Convert.ToInt32(await TestSql.ScalarAsync(fx.ConnectionString, TestSql.BlockedByQuery, ("@spid", spid))) == 0)
            {
                second.IsCompleted.Should().BeFalse("the second Upsert must wait for the first one's transaction");
                DateTime.UtcNow.Should().BeBefore(deadline);
                await Task.Delay(20);
            }
            await tx.CommitAsync();
        }

        ((UpsertResponse)await second).RecordCreated.Should().BeFalse();
        (await ReadAsync(table, id))!.GetAttributeValue<string>("new_name").Should().Be("second");
    }

    [Fact]
    public async Task Upsert_replace_by_partitionId_parameter_needs_write_only()
    {
        var table = await FileTableAsync(); // no secured column the replace would clear
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ($"prvWrite{table}", DepthMask.Global), ($"prvRead{table}", DepthMask.Global));
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "one", [ElasticTables.PartitionId] = "up-w" });

        // The guard sees the same row as the core: an update of the stored row, so Create is not demanded.
        var response = (UpsertResponse)await ServiceFor(fx, objectId).ExecuteAsync(new UpsertRequest
        {
            Target = new Entity(table, id) { ["new_name"] = "two" }, ["partitionId"] = "up-w",
        });

        response.RecordCreated.Should().BeFalse();
        (await ReadAsync(table, id))!.GetAttributeValue<string>("new_name").Should().Be("two");
    }

    [Fact]
    public async Task Upsert_of_an_id_in_another_partition_is_refused()
    {
        var table = await TableAsync();
        var id = await CreateRowAsync(table, "p1", 3);

        (await FaultAsync(() => Admin.ExecuteAsync(new UpsertRequest { Target = new Entity(table, Keys(table, id, "p2")) { ["new_value"] = 9 } })))
            .ErrorCode.Should().Be(ErrorCodes.DuplicateRecord);
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(3);
    }

    [Fact]
    public async Task Key_attributes_are_refused_on_a_standard_table_and_other_keys_on_an_elastic_one()
    {
        var table = await TableAsync();
        await BadRequestAsync(() => Admin.ExecuteAsync(new RetrieveRequest
        {
            Target = new EntityReference("account", new KeyAttributeCollection { { "accountnumber", "A1" } }), ColumnSet = new ColumnSet("name"),
        }));
        await BadRequestAsync(() => Admin.ExecuteAsync(new RetrieveRequest
        {
            Target = new EntityReference(table, new KeyAttributeCollection { { "new_name", "x" } }), ColumnSet = new ColumnSet("new_name"),
        }));
    }

    [Fact]
    public async Task Restricted_user_writes_by_key_with_privileges_only()
    {
        var table = await TableAsync();
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ($"prvCreate{table}", DepthMask.Global), ($"prvRead{table}", DepthMask.Global));
        var asUser = ServiceFor(fx, objectId);
        var id = await asUser.CreateAsync(new Entity(table) { [ElasticTables.PartitionId] = "p1", ["new_value"] = 1 });

        (await FaultAsync(() => asUser.UpdateAsync(new Entity(table, Keys(table, id, "p1")) { ["new_value"] = 2 }))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => asUser.ExecuteAsync(new UpsertRequest { Target = new Entity(table, Keys(table, id, "p1")) { ["new_value"] = 2 } })))
            .HttpStatus.Should().Be(403);
        (await FaultAsync(() => asUser.ExecuteAsync(new DeleteRequest { Target = new EntityReference(table, Keys(table, id, "p1")) })))
            .HttpStatus.Should().Be(403);
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(1);

        // Upsert of a new row needs Create only.
        var created = (UpsertResponse)await asUser.ExecuteAsync(new UpsertRequest
        {
            Target = new Entity(table, Keys(table, Guid.NewGuid(), "p1")) { ["new_value"] = 2 },
        });
        created.RecordCreated.Should().BeTrue();
    }

    [Fact]
    public async Task Lookup_to_elastic_by_key_sets_pid_column()
    {
        var table = await TableAsync();
        var lookup = "new_sensor" + Guid.NewGuid().ToString("N")[..6];
        await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = $"new_account_{lookup}", ReferencedEntity = table, ReferencingEntity = "account",
            },
            Lookup = new LookupAttributeMetadata { LogicalName = lookup, SchemaName = lookup, DisplayName = Label("Sensor") },
        });
        var id = await CreateRowAsync(table, "p1");
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "with sensor" });

        await Admin.UpdateAsync(new Entity("account", account) { [lookup] = new EntityReference(table, Keys(table, id, "p1")) });

        var read = await Admin.RetrieveAsync("account", account, new ColumnSet(lookup, ElasticTables.PartitionColumnOf(lookup)));
        read.GetAttributeValue<EntityReference>(lookup).Id.Should().Be(id);
        read.GetAttributeValue<string>(ElasticTables.PartitionColumnOf(lookup)).Should().Be("p1");

        // The row is looked up in the partition given.
        (await FaultAsync(() => Admin.UpdateAsync(new Entity("account", account) { [lookup] = new EntityReference(table, Keys(table, id, "p2")) })))
            .ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
    }

    [Fact]
    public async Task Elastic_row_has_no_statecode()
    {
        var table = await TableAsync();
        var id = await CreateRowAsync(table, null);

        var entity = (Entity)(await RetrieveAsync(table, id, null)).Results["Entity"];
        entity.Contains("statecode").Should().BeFalse();
        entity.Contains("statuscode").Should().BeFalse();

        await BadRequestAsync(() => Admin.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(table, id), State = new OptionSetValue(1), Status = new OptionSetValue(2),
        }));
    }

    [Fact]
    public async Task Partition_matching_is_case_sensitive()
    {
        var table = await TableAsync();
        var id = await CreateRowAsync(table, "p1", 1);

        (await FaultAsync(() => RetrieveAsync(table, id, "P1"))).ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        (await FaultAsync(() => RetrieveAsync(table, id, "P1", asParameter: true))).ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        (await FaultAsync(() => UpdateAsync(table, id, "P1", 2))).ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        (await FaultAsync(() => DeleteAsync(table, id, "P1"))).ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
        // Upsert in "P1" does not find the "p1" row: it creates, and the id is taken (ruling E1).
        (await FaultAsync(() => Admin.ExecuteAsync(new UpsertRequest { Target = new Entity(table, Keys(table, id, "P1")) { ["new_value"] = 3 } })))
            .ErrorCode.Should().Be(ErrorCodes.DuplicateRecord);
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(1);
    }

    [Fact]
    public async Task Upsert_replace_needs_field_update_on_the_secured_columns_it_clears()
    {
        var table = await TableAsync();
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ($"prvCreate{table}", DepthMask.Global), ($"prvRead{table}", DepthMask.Global),
            ($"prvWrite{table}", DepthMask.Global));
        var profile = await Admin.CreateAsync(new Entity(FieldSecurityTables.Profile) { ["name"] = "el-" + Guid.NewGuid().ToString("N")[..8] });
        await Admin.CreateAsync(new Entity(FieldSecurityTables.Permission)
        {
            [FieldSecurityTables.ProfileId] = new EntityReference(FieldSecurityTables.Profile, profile),
            [FieldSecurityTables.EntityName] = table, [FieldSecurityTables.AttributeLogicalName] = "new_secret",
            [FieldSecurityTables.CanRead] = new OptionSetValue(4), [FieldSecurityTables.CanCreate] = new OptionSetValue(4),
            [FieldSecurityTables.CanUpdate] = new OptionSetValue(0),
        });
        await Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("systemuser", user), Relationship = new Relationship("systemuserprofiles_association"),
            RelatedEntities = [new EntityReference(FieldSecurityTables.Profile, profile)],
        });
        var asUser = ServiceFor(fx, objectId);
        var id = await Admin.CreateAsync(new Entity(table) { [ElasticTables.PartitionId] = "p1", ["new_value"] = 1, ["new_secret"] = "kept" });

        var fault = await FaultAsync(() => asUser.ExecuteAsync(new UpsertRequest { Target = new Entity(table, Keys(table, id, "p1")) { ["new_value"] = 2 } }));

        fault.HttpStatus.Should().Be(403);
        var row = await ReadAsync(table, id);
        row!.GetAttributeValue<string>("new_secret").Should().Be("kept");
        row.GetAttributeValue<int>("new_value").Should().Be(1);
        // Without the secured column in play (a new row), the same user's Upsert passes.
        ((UpsertResponse)await asUser.ExecuteAsync(new UpsertRequest
        {
            Target = new Entity(table, Keys(table, Guid.NewGuid(), "p1")) { ["new_value"] = 2 },
        })).RecordCreated.Should().BeTrue();
    }

    [Fact]
    public async Task Upsert_of_an_id_in_another_partition_is_412_for_a_user_without_write()
    {
        var table = await TableAsync();
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ($"prvCreate{table}", DepthMask.Global), ($"prvRead{table}", DepthMask.Global));
        var id = await CreateRowAsync(table, "p1", 1);

        (await FaultAsync(() => ServiceFor(fx, objectId).ExecuteAsync(new UpsertRequest
        {
            Target = new Entity(table, Keys(table, id, "p2")) { ["new_value"] = 2 },
        }))).ErrorCode.Should().Be(ErrorCodes.DuplicateRecord);
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(1);
    }

    [Fact]
    public async Task Lookup_by_key_without_partitionid_names_a_row_without_partition()
    {
        var table = await TableAsync();
        var lookup = "new_sensor" + Guid.NewGuid().ToString("N")[..6];
        await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = $"new_account_{lookup}", ReferencedEntity = table, ReferencingEntity = "account",
            },
            Lookup = new LookupAttributeMetadata { LogicalName = lookup, SchemaName = lookup, DisplayName = Label("Sensor") },
        });
        var partitioned = await CreateRowAsync(table, "p1");
        var plain = await CreateRowAsync(table, null);
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "key without partition" });
        var pid = ElasticTables.PartitionColumnOf(lookup);

        (await FaultAsync(() => Admin.UpdateAsync(new Entity("account", account)
        {
            [lookup] = new EntityReference(table, new KeyAttributeCollection { { Pk(table), partitioned } }),
        }))).ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);

        await Admin.UpdateAsync(new Entity("account", account)
        {
            [lookup] = new EntityReference(table, new KeyAttributeCollection { { Pk(table), plain } }),
        });
        var read = await Admin.RetrieveAsync("account", account, new ColumnSet(lookup, pid));
        read.GetAttributeValue<EntityReference>(lookup).Id.Should().Be(plain);
        read.GetAttributeValue<string>(pid).Should().BeNull();
    }

    [Fact]
    public async Task Upsert_on_a_standard_table_through_the_pipeline_is_checked_as_its_inner_create()
    {
        // #76, ruling B10: a call that reaches the pipeline directly (as a plug-in's does) runs an inner Create, which the guard
        // checks; a caller with Create may upsert a new row, with or without its id (StandardUpsertTests has the rest).
        var root = await RootBusinessUnitAsync(fx);
        var user = await CreateUserAsync(fx, root, Guid.NewGuid());
        await GrantAsync(fx, user, false, ("prvCreateAccount", DepthMask.Global), ("prvReadAccount", DepthMask.Global));
        using var scope = fx.App.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<Xrmd.Pipeline.PipelineExecutor>();
        var caller = new Xrmd.Abstractions.Caller(user, root);

        foreach (var target in new[]
                 {
                     new Entity("account") { ["name"] = "x" },
                     new Entity("account", Guid.NewGuid()) { ["name"] = "x" },
                 })
        {
            var response = (UpsertResponse)await executor.ExecuteAsync(new UpsertRequest { Target = target },
                new Xrmd.Pipeline.InvocationContext(caller, user, 1, null, null, Guid.NewGuid()), default);
            response.RecordCreated.Should().BeTrue();
            if (target.Id != Guid.Empty) response.Target.Id.Should().Be(target.Id);
        }
    }

    /// <summary>A new elastic table with the file column <c>new_doc</c>.</summary>
    private async Task<string> FileTableAsync()
    {
        var name = "new_elfile" + Guid.NewGuid().ToString("N")[..6];
        var entity = TestHelpers.Entity(name, name, "Elastic files");
        entity.TableType = ElasticTables.TableTypeElastic;
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name, Attribute = new FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_doc", DisplayName = Label("Doc") },
        });
        return name;
    }

    /// <summary>Uploads one block to <c>new_doc</c> of the row <paramref name="id"/>, named by id alone; returns the file id.</summary>
    private async Task<Guid> UploadFileAsync(string table, Guid id)
    {
        var token = ((InitializeFileBlocksUploadResponse)await Admin.ExecuteAsync(new InitializeFileBlocksUploadRequest
        {
            Target = new EntityReference(table, id), FileAttributeName = "new_doc", FileName = "a.txt",
        })).FileContinuationToken;
        var block = Convert.ToBase64String("block-000000"u8.ToArray());
        await Admin.ExecuteAsync(new UploadBlockRequest { FileContinuationToken = token, BlockId = block, BlockData = [1, 2, 3] });
        return ((CommitFileBlocksUploadResponse)await Admin.ExecuteAsync(new CommitFileBlocksUploadRequest
        {
            FileContinuationToken = token, BlockList = [block], FileName = "a.txt", MimeType = "text/plain",
        })).FileId;
    }

    [Theory]
    [InlineData("fp1")]
    [InlineData(null)]
    public async Task File_column_of_an_elastic_row_is_uploaded_and_deleted_by_id(string? partition)
    {
        // Final review I2: the file messages name the row by id alone; the row's stored partition is used.
        var table = await FileTableAsync();
        var row = new Entity(table) { ["new_name"] = "f" };
        if (partition is not null) row[ElasticTables.PartitionId] = partition;
        var id = await Admin.CreateAsync(row);

        var fileId = await UploadFileAsync(table, id);

        var stored = await ReadAsync(table, id);
        stored!.GetAttributeValue<Guid>("new_doc").Should().Be(fileId);
        stored.GetAttributeValue<string>(ElasticTables.PartitionId).Should().Be(partition);

        await Admin.ExecuteAsync(new DeleteFileRequest { FileId = fileId });

        stored = await ReadAsync(table, id);
        stored!.Contains("new_doc").Should().BeFalse();
        stored.GetAttributeValue<string>(ElasticTables.PartitionId).Should().Be(partition);
    }

    [Fact]
    public async Task Target_whose_primary_key_attribute_differs_from_its_id_is_refused_through_the_pipeline()
    {
        // Final review I4: the guard and the core must address the same row. A caller with Write on their own row A only names
        // A as the primary key attribute and another user's row B as the key (or the id): 400, and B is unchanged.
        var table = await TableAsync();
        var root = await RootBusinessUnitAsync(fx);
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, root, objectId);
        await GrantAsync(fx, user, false, ($"prvCreate{table}", DepthMask.Basic), ($"prvRead{table}", DepthMask.Basic),
            ($"prvWrite{table}", DepthMask.Basic));
        var own = await ServiceFor(fx, objectId).CreateAsync(new Entity(table) { ["new_value"] = 1, [ElasticTables.PartitionId] = "pk-a" });
        var other = await CreateRowAsync(table, "pk-b", 1);
        using var scope = fx.App.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<Xrmd.Pipeline.PipelineExecutor>();
        Task<OrganizationResponse> Run(OrganizationRequest request) =>
            executor.ExecuteAsync(request, new Xrmd.Pipeline.InvocationContext(new Xrmd.Abstractions.Caller(user, root), user, 1, null, null, Guid.NewGuid()), default);

        (await BadRequestAsync(() => Run(new UpsertRequest
        {
            Target = new Entity(table, Keys(table, other, "pk-b")) { [Pk(table)] = own, ["new_name"] = "taken" },
        }))).Should().Contain(Pk(table));
        (await BadRequestAsync(() => Run(new UpsertRequest
        {
            Target = new Entity(table, other) { [Pk(table)] = own, ["new_name"] = "taken", [ElasticTables.PartitionId] = "pk-b" },
        }))).Should().Contain(Pk(table));
        (await BadRequestAsync(() => Run(new UpdateRequest
        {
            Target = new Entity(table, other) { [Pk(table)] = own, ["new_value"] = 9, [ElasticTables.PartitionId] = "pk-b" },
        }))).Should().Contain(Pk(table));

        var row = await ReadAsync(table, other);
        row!.GetAttributeValue<int>("new_value").Should().Be(1);
        row.GetAttributeValue<string>("new_name").Should().Be("row");
    }

    [Fact]
    public async Task Upsert_with_a_non_string_partition_key_is_refused_for_a_restricted_caller()
    {
        var table = await TableAsync();
        var root = await RootBusinessUnitAsync(fx);
        var user = await CreateUserAsync(fx, root, Guid.NewGuid());
        await GrantAsync(fx, user, false, ($"prvCreate{table}", DepthMask.Global), ($"prvRead{table}", DepthMask.Global),
            ($"prvWrite{table}", DepthMask.Global));
        var id = await CreateRowAsync(table, "p1", 1);
        using var scope = fx.App.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<Xrmd.Pipeline.PipelineExecutor>();

        // Through the pipeline, as a plug-in's call: the guard reads the key before the core does.
        (await BadRequestAsync(() => executor.ExecuteAsync(new UpsertRequest
        {
            Target = new Entity(table, new KeyAttributeCollection { { Pk(table), id }, { ElasticTables.PartitionId, 5 } }) { ["new_value"] = 2 },
        }, new Xrmd.Pipeline.InvocationContext(new Xrmd.Abstractions.Caller(user, root), user, 1, null, null, Guid.NewGuid()), default)))
            .Should().Contain("partitionid");
        (await ReadAsync(table, id))!.GetAttributeValue<int>("new_value").Should().Be(1);
    }
}
