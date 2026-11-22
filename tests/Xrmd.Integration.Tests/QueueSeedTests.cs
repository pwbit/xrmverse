using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Security;

namespace Xrmd.Integration.Tests;

/// <summary>The seeded queue tables (plan 4.1a, task 1): type codes, ownership, lookups, privileges and Basic User's rights.</summary>
[Collection("xrmd")]
public sealed class QueueSeedTests(XrmdFixture fx)
{
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static string Ids(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{PrivilegeNames.IdOf(n)}'"));

    private async Task<long> CountAsync(string table, IEnumerable<string> names, string extra = "") =>
        Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM data.{table} WHERE privilegeid IN ({Ids(names)}) {extra}"));

    [Fact]
    public void Seed_CreatesQueueTables_WithTypeCodesAndOwnership()
    {
        var queue = Store.GetEntity("queue")!;
        queue.ObjectTypeCode.Should().Be(2020);
        queue.OwnershipType.Should().Be(OwnershipTypes.UserOwned);
        queue.EntitySetName.Should().Be("queues");
        queue.PrimaryNameAttribute.Should().Be("name");
        var item = Store.GetEntity("queueitem")!;
        item.ObjectTypeCode.Should().Be(2029);
        item.OwnershipType.Should().Be(OwnershipTypes.None);
        item.EntitySetName.Should().Be("queueitems");
        item.PrimaryNameAttribute.Should().Be("title");
        var membership = Store.GetEntity("queuemembership")!;
        membership.ObjectTypeCode.Should().Be(2027);
        membership.IsIntersect.Should().BeTrue();
        membership.Attributes.Select(a => a.LogicalName).Should().Contain(["queueid", "systemuserid"]);
        foreach (var t in new[] { "systemuser", "team" })
            ((LookupAttributeMetadata)Store.GetEntity(t)!.Attributes.Single(a => a.LogicalName == "queueid")).Targets.Should().Equal("queue");
        var entries = Store.GetRelationship("queue_entries").Should().BeOfType<OneToManyRelationshipMetadata>().Subject;
        entries.ReferencedEntity.Should().Be("queue");
        entries.ReferencingEntity.Should().Be("queueitem");
        entries.CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);
        Store.GetRelationship("queue_system_user")!.Should().BeOfType<OneToManyRelationshipMetadata>();
        Store.GetRelationship("queue_team")!.Should().BeOfType<OneToManyRelationshipMetadata>();
    }

    [Fact]
    public void QueueMembership_Association_Exists()
    {
        var r = Store.GetRelationship("queuemembership_association").Should().BeOfType<ManyToManyRelationshipMetadata>().Subject;
        r.IntersectEntityName.Should().Be("queuemembership");
        r.Entity1LogicalName.Should().Be("queue");
        r.Entity2LogicalName.Should().Be("systemuser");
        r.Entity1IntersectAttribute.Should().Be("queueid");
        r.Entity2IntersectAttribute.Should().Be("systemuserid");
    }

    [Fact]
    public void QueueItem_ObjectId_IsMultiTableLookup_WithTypeColumn()
    {
        var item = Store.GetEntity("queueitem")!;
        var objectId = item.Attributes.Single(a => a.LogicalName == QueueSchema.ObjectId).Should().BeOfType<LookupAttributeMetadata>().Subject;
        // Task 2: the store keeps its targets, the pointer first, then the queue-enabled tables.
        objectId.Targets.Should().StartWith("activitypointer").And.Contain(["email", "task", "phonecall", "appointment"]);
        var type = item.Attributes.Single(a => a.LogicalName == QueueSchema.ObjectIdTypeCode).Should().BeOfType<EntityNameAttributeMetadata>().Subject;
        type.AttributeOf.Should().Be("objectid");
        var worker = item.Attributes.Single(a => a.LogicalName == QueueSchema.WorkerId).Should().BeOfType<LookupAttributeMetadata>().Subject;
        worker.Targets.Should().BeEquivalentTo("systemuser", "team");
    }

    [Fact]
    public void ItemRelationship_FollowsLearnNaming()
    {
        var email = QueueSchema.ItemRelationship(Store.GetEntity("email")!);
        email.SchemaName.Should().Be("Email_QueueItem");
        email.ReferencingEntityNavigationPropertyName.Should().Be("objectid_email");
        email.CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);
        email.CascadeConfiguration.Assign.Should().Be(CascadeType.NoCascade);
        QueueSchema.ItemRelationship(Store.GetEntity("phonecall")!).SchemaName.Should().Be("PhoneCall_QueueItem");
        QueueSchema.ItemRelationship(Store.GetEntity("activitypointer")!).SchemaName.Should().Be("ActivityPointer_QueueItem");
    }

    [Fact]
    public void ItemRelationship_OfCustomTable_UsesLogicalName()
    {
        // A custom table (no system type code): {logicalname}_QueueItems, whatever its SchemaName (from memory, checklist 2).
        var custom = QueueSchema.ItemRelationship(new EntityMetadata { LogicalName = "new_ticket", SchemaName = "new_Ticket" });
        custom.SchemaName.Should().Be("new_ticket_QueueItems");
        (custom.ReferencedEntity, custom.ReferencingEntity, custom.ReferencingAttribute).Should().Be(("new_ticket", "queueitem", "objectid"));
        custom.ReferencingEntityNavigationPropertyName.Should().Be("objectid_new_ticket");
        custom.CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);
    }

    [Fact]
    public async Task Deleting_systemuser_referenced_by_queuemembership_association_is_403()
    {
        // systemuser is a side of the seeded N:N: the system table rule answers first, and nothing is deleted.
        var fault = await TestHelpers.FaultAsync(() =>
            SecurityTestHelper.ServiceFor(fx, fx.AdminObjectId).ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.DeleteEntityRequest { LogicalName = "systemuser" }));
        fault.HttpStatus.Should().Be(403);
        Store.GetEntity("systemuser").Should().NotBeNull();
        Store.GetRelationship(QueueSchema.MembershipRelationshipName).Should().NotBeNull();
    }

    [Fact]
    public async Task Queue_Privileges_AreSeeded()
    {
        var names = new[] { "prvCreateQueue", "prvReadQueue", "prvWriteQueue", "prvDeleteQueue", "prvAppendQueue", "prvAppendToQueue",
            "prvAssignQueue", "prvShareQueue" };
        var itemNames = new[] { "prvReadQueueItem", "prvWriteQueueItem", "prvCreateQueueItem", "prvDeleteQueueItem", "prvAppendToQueueItem" };
        (await CountAsync("privilege", names)).Should().Be(8);
        (await CountAsync("privilege", itemNames)).Should().Be(5);
        // Organization depth only: Global is the one depth bit of an organization-owned table.
        (await CountAsync("privilege", itemNames, "AND canbebasic = 0 AND canbelocal = 0 AND canbedeep = 0 AND canbeglobal = 1")).Should().Be(5);
        (await CountAsync("privilege", names, "AND canbebasic = 1 AND canbelocal = 1 AND canbedeep = 1 AND canbeglobal = 1")).Should().Be(8);
        (await CountAsync("privilege", ["prvAssignQueueItem", "prvShareQueueItem"])).Should().Be(0);
        (await CountAsync("privilege", ["prvCreateQueueMembership", "prvReadQueueMembership"])).Should().Be(0);
    }
    [Fact]
    public async Task BasicUser_HasQueueAndQueueItemRights()
    {
        // Ruling R23: every queue item privilege at Global (the table has no owner); on the queue record, Read only.
        var items = new[] { "prvReadQueueItem", "prvWriteQueueItem", "prvCreateQueueItem", "prvDeleteQueueItem", "prvAppendQueueItem", "prvAppendToQueueItem" };
        var basic = $"AND roleid = '{StandardRoles.BasicUser}' AND privilegedepthmask = 8";
        (await CountAsync("roleprivileges", ["prvReadQueue"], basic)).Should().Be(1);
        (await CountAsync("roleprivileges", items, basic)).Should().Be(6);
        (await CountAsync("roleprivileges", ["prvCreateQueue", "prvWriteQueue"], $"AND roleid = '{StandardRoles.BasicUser}'")).Should().Be(0);
    }

    /// <summary>An organization that ran the first queue grants without Append and Append To (ruling R23): startup adds them once.</summary>
    [Fact]
    public async Task QueueItemAppend_TopUp_ReachesAnExistingOrganizationOnce()
    {
        const string step = "queue-item-append-privileges";
        var names = new[] { "prvAppendQueueItem", "prvAppendToQueueItem" };
        var ids = string.Join(", ", names.Select(n => $"'{PrivilegeNames.IdOf(n)}'"));
        var basic = $"roleid = '{StandardRoles.BasicUser}' AND privilegeid IN ({ids})";
        var bootstrap = fx.App.Services.GetRequiredService<SecurityBootstrap>();
        await ScalarAsync($"DELETE FROM data.roleprivileges WHERE {basic}");
        await ScalarAsync($"DELETE FROM meta.upgrade_step WHERE name = '{step}'");
        try
        {
            await bootstrap.EnsureAsync(default);
            Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE {basic} AND privilegedepthmask = 8")).Should().Be(2);
            Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM meta.upgrade_step WHERE name = '{step}'")).Should().Be(1);

            // Once: a grant removed afterwards stays removed.
            await ScalarAsync($"DELETE FROM data.roleprivileges WHERE {basic}");
            await bootstrap.EnsureAsync(default);
            Convert.ToInt64(await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE {basic}")).Should().Be(0);
        }
        finally
        {
            await ScalarAsync($"DELETE FROM meta.upgrade_step WHERE name = '{step}'");
            await bootstrap.EnsureAsync(default);
        }
    }

    /// <summary>Final review I4: the queue lookups have their indexes, and a startup puts a missing one back.</summary>
    [Fact]
    public async Task Queue_lookup_indexes_exist_and_are_recreated_at_startup()
    {
        var indexes = new[]
        {
            ("queueitem", "IX_queueitem_objectid_state"), ("queueitem", "IX_queueitem_queueid_state"),
            ("queuemembership", "IX_queuemembership_systemuserid"),
        };
        foreach (var (table, index) in indexes)
            (await TestSql.IndexExistsAsync(fx.ConnectionString, "data", table, index)).Should().BeTrue(index);

        await ScalarAsync(TestSql.Dialect.DropIndexIfExists("data", "queueitem", "IX_queueitem_objectid_state"));
        (await TestSql.IndexExistsAsync(fx.ConnectionString, "data", "queueitem", "IX_queueitem_objectid_state")).Should().BeFalse();
        await fx.App.Services.GetRequiredService<SecurityBootstrap>().EnsureAsync(default);
        foreach (var (table, index) in indexes)
            (await TestSql.IndexExistsAsync(fx.ConnectionString, "data", table, index)).Should().BeTrue(index);
    }
}
