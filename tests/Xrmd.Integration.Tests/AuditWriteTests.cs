using System.Data;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Audit;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The audit rows of Create, Update, Delete, Assign and SetState (plan 13, task 3; rulings A1, A3, A6), written in stage 30 in the
/// transaction of the operation: the spec's acceptance (an Update of an audited column records its old and new value; switching
/// auditing off at the column, table or organization records nothing), only changed audited columns, the rollback with a failed
/// operation, the users and the transaction id, and the organization switch taking effect at its commit.
/// </summary>
/// <remarks>
/// Auditing is switched on for the organization at the start of each test and off at its end: the other tests of the collection
/// run with the default (off).
/// </remarks>
[Collection("xrmd")]
public sealed class AuditWriteTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Account = "account";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private sealed record AuditRow(Guid AuditId, int Action, int Operation, Guid ObjectId, int? ObjectIdType, string ObjectTypeCode,
        Guid UserId, Guid? CallingUserId, DateTime CreatedOn, string ChangeData, Guid TransactionId, int TimeToLive)
    {
        public AttributeChange Change => (AttributeChange)Audit.ChangeData.Deserialize(ChangeData);
    }

    public async Task InitializeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await SetOrganizationAuditAsync(true);
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await SetOrganizationAuditAsync(false);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<Guid> OrganizationIdAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT TOP 1 organizationid FROM data.organization");
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Sets <c>organization.isauditenabled</c> through the organization service (so through the pipeline's commit).</summary>
    private async Task SetOrganizationAuditAsync(bool enabled) =>
        await Admin.UpdateAsync(new Entity("organization", await OrganizationIdAsync()) { ["isauditenabled"] = enabled });

    private async Task<Guid> AdminUserIdAsync() => (await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId)).UserId;

    private async Task<List<AuditRow>> RowsAsync(Guid objectId)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        return await RowsAsync(conn, null, objectId);
    }

    private static async Task<List<AuditRow>> RowsAsync(DbConnection conn, DbTransaction? tx, Guid objectId)
    {
        await using var cmd = TestSql.Command(conn, tx, """
            SELECT auditid, action, operation, objectid, objectidtype, objecttypecode, userid, callinguserid, createdon, changedata,
                transactionid, timetoliveinseconds
            FROM data.audit WHERE objectid = @id ORDER BY createdon, auditid
            """);
        cmd.AddParameter("@id", DbType.Guid, objectId);
        var rows = new List<AuditRow>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            rows.Add(new AuditRow(rd.GetGuid(0), rd.GetInt32(1), rd.GetInt32(2), rd.GetGuid(3), rd.IsDBNull(4) ? null : rd.GetInt32(4),
                rd.GetString(5), rd.GetGuid(6), rd.IsDBNull(7) ? null : rd.GetGuid(7), rd.GetDateTime(8), rd.GetString(9), rd.GetGuid(10),
                rd.GetInt32(11)));
        return rows;
    }

    private Task<Guid> NewAccountAsync(string name, OrganizationService? service = null, params (string Name, object Value)[] values)
    {
        var account = new Entity(Account) { ["name"] = name };
        foreach (var (column, value) in values) account[column] = value;
        return (service ?? Admin).CreateAsync(account);
    }

    private static List<AuditRow> OfAction(IEnumerable<AuditRow> rows, int action) => rows.Where(r => r.Action == action).ToList();

    private Task SetAttributeAuditAsync(string entity, string attribute, bool enabled) =>
        Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = entity,
            Attribute = new StringAttributeMetadata { LogicalName = attribute, IsAuditEnabled = new BooleanManagedProperty(enabled) },
        });

    private Task SetEntityAuditAsync(string entity, bool enabled) =>
        Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = entity, IsAuditEnabled = new BooleanManagedProperty(enabled) } });

    private async Task<string> NewTableAsync(string label)
    {
        var name = $"new_{label}{_run}";
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = name, SchemaName = name, DisplayName = Label("Table " + name), DisplayCollectionName = Label("Tables " + name),
                OwnershipType = OwnershipTypes.UserOwned, IsAuditEnabled = new BooleanManagedProperty(true),
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name"),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
        });
        return name;
    }

    // ---- spec acceptance ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_on_an_audited_column_writes_old_and_new_value()
    {
        var id = await NewAccountAsync("Before " + _run);
        var start = DateTime.UtcNow.AddSeconds(-5);

        await Admin.UpdateAsync(new Entity(Account, id) { ["name"] = "After " + _run });

        var update = OfAction(await RowsAsync(id), AuditActions.Update).Should().ContainSingle().Subject;
        update.Operation.Should().Be(AuditOperations.Update);
        update.ObjectId.Should().Be(id);
        update.ObjectTypeCode.Should().Be(Account);
        update.ObjectIdType.Should().Be(Store.GetEntity(Account)!.ObjectTypeCode);
        update.UserId.Should().Be(await AdminUserIdAsync());
        update.CallingUserId.Should().BeNull();
        update.CreatedOn.Should().BeOnOrAfter(start).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(5));
        update.TimeToLive.Should().Be(30 * 86_400, "the default retention is 30 days");
        update.Change.Old.Should().Equal(new Dictionary<string, object?> { ["name"] = "Before " + _run });
        update.Change.New.Should().Equal(new Dictionary<string, object?> { ["name"] = "After " + _run });

        // The row reads back through the SDK like any other: action and operation as choices, the record as a lookup.
        var row = await Admin.RetrieveAsync(AuditTable.Name, update.AuditId, new ColumnSet(true));
        row.GetAttributeValue<OptionSetValue>("action").Value.Should().Be(2);
        row.GetAttributeValue<OptionSetValue>("operation").Value.Should().Be(2);
        row.GetAttributeValue<EntityReference>("objectid").Should().Match<EntityReference>(r => r.Id == id && r.LogicalName == Account);
        row.GetAttributeValue<string>("objecttypecode").Should().Be(Account);
        row.GetAttributeValue<EntityReference>("userid").Id.Should().Be(await AdminUserIdAsync());
    }

    [Fact]
    public async Task Disabling_audit_on_the_column_suppresses_the_row()
    {
        var id = await NewAccountAsync("Column " + _run);
        await SetAttributeAuditAsync(Account, "name", false);
        try
        {
            await Admin.UpdateAsync(new Entity(Account, id) { ["name"] = "Column off " + _run });
            OfAction(await RowsAsync(id), AuditActions.Update).Should().BeEmpty("the column is not audited");
        }
        finally
        {
            await SetAttributeAuditAsync(Account, "name", true);
        }

        await SetEntityAuditAsync(Account, false);
        try
        {
            await Admin.UpdateAsync(new Entity(Account, id) { ["name"] = "Table off " + _run });
            OfAction(await RowsAsync(id), AuditActions.Update).Should().BeEmpty("the table is not audited");
        }
        finally
        {
            await SetEntityAuditAsync(Account, true);
        }

        await SetOrganizationAuditAsync(false);
        await Admin.UpdateAsync(new Entity(Account, id) { ["name"] = "Organization off " + _run });
        OfAction(await RowsAsync(id), AuditActions.Update).Should().BeEmpty("the organization does not audit");

        await SetOrganizationAuditAsync(true);
        await Admin.UpdateAsync(new Entity(Account, id) { ["name"] = "All on " + _run });
        OfAction(await RowsAsync(id), AuditActions.Update).Should().ContainSingle()
            .Which.Change.Old.Should().Equal(new Dictionary<string, object?> { ["name"] = "Organization off " + _run });
    }

    // ---- what is recorded -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_audits_only_changed_audited_columns()
    {
        var id = await NewAccountAsync("Same " + _run, null, ("telephone1", "111"));

        await Admin.UpdateAsync(new Entity(Account, id) { ["name"] = "Same " + _run, ["telephone1"] = "222" });
        var row = OfAction(await RowsAsync(id), AuditActions.Update).Should().ContainSingle().Subject;
        row.Change.Old.Should().Equal(new Dictionary<string, object?> { ["telephone1"] = "111" });
        row.Change.New.Should().Equal(new Dictionary<string, object?> { ["telephone1"] = "222" });

        // The stored values sent again: no row at all.
        await Admin.UpdateAsync(new Entity(Account, id) { ["name"] = "Same " + _run, ["telephone1"] = "222" });
        OfAction(await RowsAsync(id), AuditActions.Update).Should().HaveCount(1);

        // A cleared value: old value, new null.
        await Admin.UpdateAsync(new Entity(Account, id) { ["telephone1"] = null });
        var cleared = OfAction(await RowsAsync(id), AuditActions.Update)[1];
        cleared.Change.Old.Should().Equal(new Dictionary<string, object?> { ["telephone1"] = "222" });
        cleared.Change.New.Should().Equal(new Dictionary<string, object?> { ["telephone1"] = null });
    }

    [Fact]
    public async Task Create_and_delete_write_their_rows()
    {
        var adminId = await AdminUserIdAsync();
        var id = await NewAccountAsync("Created " + _run, null, ("telephone1", "333"));

        var create = (await RowsAsync(id)).Should().ContainSingle().Subject;
        (create.Action, create.Operation).Should().Be((AuditActions.Create, AuditOperations.Create));
        create.Change.Old.Should().BeEmpty();
        create.Change.New["name"].Should().Be("Created " + _run);
        create.Change.New["telephone1"].Should().Be("333");
        create.Change.New["statecode"].Should().BeOfType<OptionSetValue>().Which.Value.Should().Be(0);
        var owner = create.Change.New["ownerid"].Should().BeOfType<EntityReference>().Subject;
        (owner.LogicalName, owner.Id, owner.Name).Should().Be(("systemuser", adminId, XrmdFixture.AdminName), "a lookup keeps its name");
        create.Change.New.Keys.Should().NotContain(["accountid", "createdon", "modifiedon", "createdby", "modifiedby", "owningbusinessunit", "emailaddress1"],
            "only audited columns with a value");

        await Admin.DeleteAsync(Account, id);
        var delete = OfAction(await RowsAsync(id), AuditActions.Delete).Should().ContainSingle().Subject;
        delete.Operation.Should().Be(AuditOperations.Delete);
        delete.Change.New.Should().BeEmpty();
        delete.Change.Old["name"].Should().Be("Created " + _run);
        delete.Change.Old["telephone1"].Should().Be("333");
        delete.Change.Old["ownerid"].Should().BeOfType<EntityReference>().Which.Id.Should().Be(adminId);
        delete.Change.Old.Keys.Should().NotContain("emailaddress1", "a null column is not listed");

        // Each record a Cascade relationship deletes is a Delete of its own.
        var parent = await NewTableAsync("ap");
        var child = await NewTableAsync("ac");
        await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = $"new_{child}_new_parentid", ReferencedEntity = parent, ReferencingEntity = child,
                CascadeConfiguration = new CascadeConfiguration { Delete = CascadeType.Cascade },
            },
            Lookup = new LookupAttributeMetadata { LogicalName = "new_parentid", SchemaName = "new_ParentId", DisplayName = Label("Parent") },
        });
        var p = await Admin.CreateAsync(new Entity(parent) { ["new_name"] = "p" });
        var c = await Admin.CreateAsync(new Entity(child) { ["new_name"] = "c", ["new_parentid"] = new EntityReference(parent, p) });

        await Admin.DeleteAsync(parent, p);

        var childDelete = OfAction(await RowsAsync(c), AuditActions.Delete).Should().ContainSingle().Subject;
        childDelete.ObjectTypeCode.Should().Be(child);
        childDelete.Change.Old["new_name"].Should().Be("c");
        childDelete.Change.Old["new_parentid"].Should().BeOfType<EntityReference>().Which.Id.Should().Be(p);
        var parentDelete = OfAction(await RowsAsync(p), AuditActions.Delete).Should().ContainSingle().Subject;
        parentDelete.TransactionId.Should().Be(childDelete.TransactionId, "the cascade runs in the transaction of the Delete");
    }

    [Fact]
    public async Task Assign_and_set_state_have_their_own_action()
    {
        var adminId = await AdminUserIdAsync();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        await AssignRoleAsync(fx, user, StandardRoles.SystemAdministrator);
        var id = await NewAccountAsync("State " + _run);

        await Admin.ExecuteAsync(new AssignRequest { Target = new EntityReference(Account, id), Assignee = new EntityReference("systemuser", user) });
        var assign = OfAction(await RowsAsync(id), AuditActions.Assign).Should().ContainSingle().Subject;
        assign.Operation.Should().Be(AuditOperations.Update);
        assign.Change.Old["ownerid"].Should().BeOfType<EntityReference>().Which.Id.Should().Be(adminId);
        assign.Change.New["ownerid"].Should().BeOfType<EntityReference>().Which.Id.Should().Be(user);
        assign.Change.New.Keys.Should().BeEquivalentTo(["ownerid"], "the owning columns that follow are not audited");

        await Admin.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(Account, id), State = new OptionSetValue(1), Status = new OptionSetValue(2),
        });
        var setState = OfAction(await RowsAsync(id), AuditActions.SetState).Should().ContainSingle().Subject;
        setState.Operation.Should().Be(AuditOperations.Update);
        setState.Change.Old["statecode"].Should().BeOfType<OptionSetValue>().Which.Value.Should().Be(0);
        setState.Change.New["statecode"].Should().BeOfType<OptionSetValue>().Which.Value.Should().Be(1);
        setState.Change.New["statuscode"].Should().BeOfType<OptionSetValue>().Which.Value.Should().Be(2);

        // A plain Update of the state is an Update.
        await Admin.UpdateAsync(new Entity(Account, id) { ["statecode"] = new OptionSetValue(0), ["statuscode"] = new OptionSetValue(1) });
        OfAction(await RowsAsync(id), AuditActions.Update).Should().ContainSingle()
            .Which.Change.New["statecode"].Should().BeOfType<OptionSetValue>().Which.Value.Should().Be(0);
        OfAction(await RowsAsync(id), AuditActions.SetState).Should().HaveCount(1);
    }

    // ---- transactions ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Audit_row_rolls_back_with_the_operation()
    {
        // A post-operation plug-in that throws: the Update and its audit row are rolled back.
        var id = await NewAccountAsync("Rollback " + _run);
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", Account, 40, config: "invalid:refused after the update");

        (await FaultAsync(() => Admin.UpdateAsync(new Entity(Account, id) { ["name"] = "Never " + _run }))).Message.Should().Contain("refused");
        (await Admin.RetrieveAsync(Account, id, new ColumnSet("name"))).GetAttributeValue<string>("name").Should().Be("Rollback " + _run);
        OfAction(await RowsAsync(id), AuditActions.Update).Should().BeEmpty("the audit row went with the rolled-back Update");
        await PluginTestHelper.ClearAsync(fx);

        // A Create that a duplicate key refuses leaves no row.
        var duplicate = await NewAccountAsync("Duplicate " + _run);
        (await FaultAsync(() => Admin.CreateAsync(new Entity(Account, duplicate) { ["name"] = "Twice " + _run }))).HttpStatus.Should().Be(412);
        (await RowsAsync(duplicate)).Should().ContainSingle("only the first Create is recorded");

        // In one transaction, an audited Create followed by a Create that a duplicate key refuses: the rollback takes the first
        // Create's row with it.
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        var first = Guid.NewGuid();
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            var session = new DataSession(conn, await conn.BeginTransactionAsync());
            await core.CreateAsync(session, caller, new Entity(Account, first) { ["name"] = "In transaction " + _run }, default);
            (await RowsAsync(conn, session.Transaction, first)).Should().ContainSingle("the row is written in the transaction");
            (await FaultAsync(() => core.CreateAsync(session, caller, new Entity(Account, first) { ["name"] = "Again" }, default)))
                .HttpStatus.Should().Be(412);
            await session.Transaction!.RollbackAsync();
        }
        (await RowsAsync(first)).Should().BeEmpty("no audit row survives a rolled-back operation");
    }

    /// <summary>
    /// Plan 2.7a, Task 8 fix round 2: the <c>transactionid</c> is one per transaction, also when the provider hands out the same
    /// transaction object again (Npgsql reuses its <c>NpgsqlTransaction</c> for the next transaction of a connection). Two
    /// transactions after one another on one connection, through one session (as the pipeline does) and through a new session
    /// each, never share an id; the rows of one transaction always do.
    /// </summary>
    [Fact]
    public async Task Transactions_after_one_another_on_one_connection_have_their_own_transaction_ids()
    {
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            // One session carrying three transactions, as the pipeline sets them; the first holds two records.
            var session = new DataSession(conn, null);
            foreach (var records in new[] { ids[..2], ids[2..3], ids[3..4] })
            {
                session.Transaction = await conn.BeginTransactionAsync();
                foreach (var id in records)
                    await core.CreateAsync(session, caller, new Entity(Account, id) { ["name"] = "Tx " + _run }, default);
                await session.Transaction.CommitAsync();
                session.Committed();
                session.Transaction = null;
            }

            // A new session on the next transaction of the same connection.
            await using var tx = await conn.BeginTransactionAsync();
            var other = new DataSession(conn, tx);
            await core.CreateAsync(other, caller, new Entity(Account, ids[4]) { ["name"] = "Tx " + _run }, default);
            await tx.CommitAsync();
            other.Committed();
        }

        var transactions = new List<Guid>();
        foreach (var id in ids) transactions.Add((await RowsAsync(id)).Single().TransactionId);
        transactions[0].Should().Be(transactions[1], "both records are written in the first transaction");
        transactions.Skip(1).Should().OnlyHaveUniqueItems("every other transaction has its own id");
    }

    [Fact]
    public async Task Rows_of_one_transaction_share_a_transaction_id()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await SetEntityAuditAsync(TestEntities.Pipe, true);
        try
        {
            var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
            var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.UpdateSelfPlugin");
            await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", TestEntities.Pipe, 40);

            var id = await Admin.CreateAsync(new Entity(TestEntities.Pipe) { ["pb_name"] = "tx " + _run });
            var other = await Admin.CreateAsync(new Entity(TestEntities.Pipe) { ["pb_name"] = "tx2 " + _run });

            var rows = await RowsAsync(id);
            rows.Select(r => r.Action).Should().Equal(AuditActions.Create, AuditActions.Update);
            rows[1].Change.New.Should().Equal(new Dictionary<string, object?> { ["pb_count"] = 1 }, "the plug-in's nested Update (its own depth)");
            rows[0].TransactionId.Should().Be(rows[1].TransactionId);
            (await RowsAsync(other))[0].TransactionId.Should().NotBe(rows[0].TransactionId, "another Create is another transaction");
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
            await SetEntityAuditAsync(TestEntities.Pipe, false);
        }
    }

    // ---- users ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Impersonated_write_sets_calling_user()
    {
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        await AssignRoleAsync(fx, user, StandardRoles.SystemAdministrator);
        var asUser = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId, callerId: user);

        var id = await NewAccountAsync("Impersonated " + _run, asUser);

        var row = (await RowsAsync(id)).Should().ContainSingle().Subject;
        row.UserId.Should().Be(user, "the user the write is made for");
        row.CallingUserId.Should().Be(await AdminUserIdAsync(), "the user who made the call on the other's behalf");
    }

    [Fact]
    public async Task Internal_write_is_attributed_to_system()
    {
        var id = await NewAccountAsync("Internal " + _run);
        var executor = TestServices.NewPipelineExecutor(fx.App.Services);

        await executor.ExecuteAsync(new UpdateRequest { Target = new Entity(Account, id) { ["name"] = "By the server " + _run } },
            new InvocationContext(Caller.Internal, Guid.Empty, 1, null, null, Guid.NewGuid()), default);

        var row = OfAction(await RowsAsync(id), AuditActions.Update).Should().ContainSingle().Subject;
        row.UserId.Should().Be(SeedOptions.DeriveSystemUserId(XrmdFixture.OrgName));
        row.CallingUserId.Should().BeNull();
    }

    // ---- organization switch -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Organization_flag_change_takes_effect_at_once()
    {
        var id = await NewAccountAsync("Flag " + _run);
        await SetOrganizationAuditAsync(false);
        await Admin.UpdateAsync(new Entity(Account, id) { ["name"] = "Off " + _run }); // the setting is read and cached: off
        OfAction(await RowsAsync(id), AuditActions.Update).Should().BeEmpty();

        await SetOrganizationAuditAsync(true);
        await Admin.UpdateAsync(new Entity(Account, id) { ["name"] = "On " + _run });
        OfAction(await RowsAsync(id), AuditActions.Update).Should().ContainSingle("the commit of the organization's Update refreshed the setting");

        await SetOrganizationAuditAsync(false);
        await Admin.UpdateAsync(new Entity(Account, id) { ["name"] = "Off again " + _run });
        OfAction(await RowsAsync(id), AuditActions.Update).Should().ContainSingle();
    }
}
