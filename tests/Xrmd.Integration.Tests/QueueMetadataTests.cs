using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Solution;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1a, task 2: the table setting <c>IsValidForQueue</c> (one-way, on by default for the activities) and the targets and
/// relationships of <c>queueitem.objectid</c> the metadata store keeps for it, and the startup upgrade of a kept value.
/// </summary>
[Collection("xrmd")]
public sealed class QueueMetadataTests(XrmdFixture fx)
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private string Name(string label) => $"new_{label}{_run}";

    private static readonly string[] SeededActivities = ["email", "task", "phonecall", "appointment"];

    private static CreateEntityRequest CreateTable(string logicalName, Action<EntityMetadata>? configure = null)
    {
        var entity = TestHelpers.Entity(logicalName, logicalName, "Table " + logicalName);
        configure?.Invoke(entity);
        return new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        };
    }

    private static CreateEntityRequest CreateActivity(string logicalName)
    {
        var entity = TestHelpers.Entity(logicalName, logicalName, "Activity " + logicalName);
        entity.IsActivity = true;
        return new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "subject", SchemaName = "Subject", MaxLength = 200, DisplayName = Label("Subject") },
        };
    }

    private Task UpdateEntityAsync(EntityMetadata changes) => Admin.ExecuteAsync(new UpdateEntityRequest { Entity = changes });

    private string[] ObjectIdTargets() =>
        Store.GetEntity("queueitem")!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "objectid").Targets;

    private async Task<EntityMetadata> RetrieveAsync(string name) =>
        ((RetrieveEntityResponse)await Admin.ExecuteAsync(new RetrieveEntityRequest { LogicalName = name, EntityFilters = EntityFilters.Entity }))
        .EntityMetadata;

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    [Fact]
    public async Task ActivityTables_AreQueueEnabled_ByDefault()
    {
        foreach (var table in SeededActivities)
        {
            Store.GetEntity(table)!.IsValidForQueue!.Value.Should().BeTrue(table);
            (await RetrieveAsync(table)).IsValidForQueue.Value.Should().BeTrue(table);
            Store.QueueTables.Should().Contain(table);
        }
        ObjectIdTargets().Should().Contain(["activitypointer", .. SeededActivities]);
        ObjectIdTargets()[0].Should().Be("activitypointer");
        foreach (var name in new[] { "ActivityPointer_QueueItem", "Email_QueueItem", "Task_QueueItem", "PhoneCall_QueueItem", "Appointment_QueueItem" })
        {
            var relationship = Store.GetRelationship(name).Should().BeOfType<OneToManyRelationshipMetadata>(name).Subject;
            relationship.CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade, name);
            relationship.ReferencingEntityNavigationPropertyName.Should().Be($"objectid_{relationship.ReferencedEntity}");
        }

        var sms = Name("qa");
        await Admin.ExecuteAsync(CreateActivity(sms));
        Store.GetEntity(sms)!.IsValidForQueue!.Value.Should().BeTrue();
        (await RetrieveAsync(sms)).IsValidForQueue.Value.Should().BeTrue();
        ObjectIdTargets().Should().Contain(sms);
        Store.GetRelationship($"{sms}_QueueItems").Should().NotBeNull();

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = sms });
        ObjectIdTargets().Should().NotContain(sms);
        Store.GetRelationship($"{sms}_QueueItems").Should().BeNull();
    }

    [Fact]
    public async Task IsValidForQueue_TurnOn_AddsObjectIdTargetAndRelationship()
    {
        var ticket = Name("ticket");
        await Admin.ExecuteAsync(CreateTable(ticket));
        (await RetrieveAsync(ticket)).IsValidForQueue.Value.Should().BeFalse("a new table is not queue-enabled");
        ObjectIdTargets().Should().NotContain(ticket);

        await UpdateEntityAsync(new EntityMetadata { LogicalName = ticket, IsValidForQueue = new Microsoft.Xrm.Sdk.BooleanManagedProperty(true) });

        Store.QueueTables.Should().Contain(ticket);
        (await RetrieveAsync(ticket)).IsValidForQueue.Value.Should().BeTrue();
        ObjectIdTargets().Should().Contain(ticket);
        var relationship = Store.GetRelationship($"{ticket}_QueueItems").Should().BeOfType<OneToManyRelationshipMetadata>().Subject;
        (relationship.ReferencedEntity, relationship.ReferencingEntity, relationship.ReferencingAttribute)
            .Should().Be((ticket, "queueitem", "objectid"));
        relationship.ReferencingEntityNavigationPropertyName.Should().Be($"objectid_{ticket}");
        relationship.CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);
        // Sent again, true is no change; a retrieved definition sent back passes.
        await UpdateEntityAsync(new EntityMetadata { LogicalName = ticket, IsValidForQueue = new Microsoft.Xrm.Sdk.BooleanManagedProperty(true) });

        // The table cannot be deleted while a queue item refers to one of its records (as with notes); then it can.
        var code = Store.GetEntity(ticket)!.ObjectTypeCode!.Value;
        var queue = await Admin.CreateAsync(new Microsoft.Xrm.Sdk.Entity("queue") { ["name"] = "Tickets " + _run });
        var record = await Admin.CreateAsync(new Microsoft.Xrm.Sdk.Entity(ticket) { ["new_name"] = "T1" });
        var item = Guid.NewGuid();
        await ScalarAsync($"INSERT INTO data.queueitem (queueitemid, queueid, objectid, objectidtype, title, statecode, statuscode) " +
                          $"VALUES ('{item}', '{queue}', '{record}', {code}, 'T1', 0, 1)");
        (await BadRequestAsync(() => Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = ticket }))).Should().Contain("1 queue items");
        Store.GetEntity(ticket).Should().NotBeNull();
        await ScalarAsync($"DELETE FROM data.queueitem WHERE queueitemid = '{item}'");
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = ticket });
        ObjectIdTargets().Should().NotContain(ticket);
        Store.GetRelationship($"{ticket}_QueueItems").Should().BeNull();
        await Admin.DeleteAsync("queue", queue);
    }

    [Fact]
    public async Task IsValidForQueue_AtCreation_LinksTheTable()
    {
        var ticket = Name("tc");
        await Admin.ExecuteAsync(CreateTable(ticket, e => e.IsValidForQueue = new Microsoft.Xrm.Sdk.BooleanManagedProperty(true)));
        ObjectIdTargets().Should().Contain(ticket);
        Store.GetRelationship($"{ticket}_QueueItems").Should().NotBeNull();
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = ticket });
    }

    [Fact]
    public async Task IsValidForQueue_TurnOff_Is400()
    {
        var ticket = Name("off");
        await Admin.ExecuteAsync(CreateTable(ticket, e => e.IsValidForQueue = new Microsoft.Xrm.Sdk.BooleanManagedProperty(true)));

        foreach (var table in new[] { ticket, "email" })
            (await BadRequestAsync(() => UpdateEntityAsync(
                    new EntityMetadata { LogicalName = table, IsValidForQueue = new Microsoft.Xrm.Sdk.BooleanManagedProperty(false) })))
                .Should().Contain("IsValidForQueue", table);
        Store.QueueTables.Should().Contain([ticket, "email"]);
        ObjectIdTargets().Should().Contain(ticket);

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = ticket });
    }

    [Fact]
    public async Task IsValidForQueue_OnRefusedTable_Is400()
    {
        foreach (var table in new[] { "queueitem", "queue", "activitypointer", "activityparty", "annotation" })
            (await BadRequestAsync(() => TestEntities.InSessionAsync(fx.ConnectionString, s => Store.UpdateEntityAsync(s,
                    new EntityMetadata { LogicalName = table, IsValidForQueue = new Microsoft.Xrm.Sdk.BooleanManagedProperty(true) }, default))))
                .Should().Contain("IsValidForQueue", table);
        var intersect = Store.GetAllEntities().First(e => e.IsIntersect == true).LogicalName;
        (await BadRequestAsync(() => TestEntities.InSessionAsync(fx.ConnectionString, s => Store.UpdateEntityAsync(s,
                new EntityMetadata { LogicalName = intersect, IsValidForQueue = new Microsoft.Xrm.Sdk.BooleanManagedProperty(true) }, default))))
            .Should().Contain("intersect");
        foreach (var table in new[] { "queueitem", "queue", "activitypointer", "activityparty", "annotation", intersect })
            Store.QueueTables.Should().NotContain(table);
    }

    [Fact]
    public async Task Upgrade_turns_a_kept_IsValidForQueue_into_metadata_once()
    {
        // A table imported before plan 4.1a: the setting only in its kept XML.
        var name = Name("up");
        await Admin.ExecuteAsync(CreateTable(name));
        var (type, key) = UnmodelledXml.EntityKey(name);
        var kept = new XElement("entity", new XAttribute("Name", name), new XElement("IsMapiGridEnabled", "1"), new XElement("IsValidForQueue", "1"));
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.SetUnmodelledXmlAsync(s, type, key, kept.ToString(SaveOptions.DisableFormatting), default));
        ObjectIdTargets().Should().NotContain(name);

        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        await QueueSettingsUpgrade.EnsureAsync(Store, factory);

        Store.GetEntity(name)!.IsValidForQueue!.Value.Should().BeTrue();
        ObjectIdTargets().Should().Contain(name);
        Store.GetRelationship($"{name}_QueueItems").Should().NotBeNull();
        var after = XElement.Parse(Store.GetUnmodelledXml(type, key)!);
        UnmodelledXml.IsMarker(after.Element("IsValidForQueue")!).Should().BeTrue();
        after.Element("IsMapiGridEnabled")!.Value.Should().Be("1");

        // A second start changes nothing.
        var stamp = (long)(await ScalarAsync("SELECT [current] FROM meta.version_stamp"))!;
        await QueueSettingsUpgrade.EnsureAsync(Store, factory);
        (await ScalarAsync("SELECT [current] FROM meta.version_stamp")).Should().Be(stamp);

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name });
    }

    [Fact]
    public async Task Upgrade_keeps_a_kept_IsValidForQueue_0_of_a_custom_activity_off()
    {
        // Final review I3: a custom activity imported before plan 4.1a whose solution said IsValidForQueue 0, kept in its XML, with
        // no value in the metadata. The activity default must not turn it on.
        var name = Name("off");
        var create = CreateActivity(name);
        create.Entity.IsValidForQueue = new Microsoft.Xrm.Sdk.BooleanManagedProperty(false);
        await Admin.ExecuteAsync(create);
        var publisher = await Admin.CreateAsync(new Microsoft.Xrm.Sdk.Entity("publisher")
        {
            ["uniquename"] = $"pq{_run}", ["friendlyname"] = "Queues", ["customizationprefix"] = "new", ["customizationoptionvalueprefix"] = 10400,
        });
        var solution = $"queueoff{_run}";
        await Admin.CreateAsync(new Microsoft.Xrm.Sdk.Entity("solution")
        {
            ["uniquename"] = solution, ["friendlyname"] = "Queue off", ["version"] = "1.0.0.0", ["publisherid"] = new Microsoft.Xrm.Sdk.EntityReference("publisher", publisher),
        });
        await Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentType = 1, ComponentId = Store.GetEntity(name)!.MetadataId!.Value, SolutionUniqueName = solution,
        });
        var (type, key) = UnmodelledXml.EntityKey(name);
        var kept = new XElement("entity", new XElement("IsMapiGridEnabled", "1"), new XElement("IsValidForQueue", "0"));
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.SetUnmodelledXmlAsync(s, type, key, kept.ToString(SaveOptions.DisableFormatting), default));
        await ScalarAsync($"UPDATE meta.entity SET definition = JSON_MODIFY(definition, '$.IsValidForQueue', NULL) WHERE logical_name = '{name}'");
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        var old = new MetadataStore(factory);
        await old.LoadAsync();
        old.GetEntity(name)!.IsValidForQueue.Should().BeNull();
        try
        {
            await QueueSettingsUpgrade.EnsureAsync(old, factory);

            old.GetEntity(name)!.IsValidForQueue!.Value.Should().BeFalse("the kept 0 is kept, as an explicit value");
            old.QueueTables.Should().NotContain(name);
            UnmodelledXml.IsMarker(XElement.Parse(old.GetUnmodelledXml(type, key)!).Element("IsValidForQueue")!).Should().BeTrue();
            var stamp = old.VersionStamp;
            await QueueSettingsUpgrade.EnsureAsync(old, factory);
            old.VersionStamp.Should().Be(stamp, "a second run changes nothing");

            // A restart: the server's store reloads and runs the upgrade again; the table stays off and exports 0.
            await Store.LoadAsync();
            await QueueSettingsUpgrade.EnsureAsync(Store, factory);
            Store.GetEntity(name)!.IsValidForQueue!.Value.Should().BeFalse();
            (await RetrieveAsync(name)).IsValidForQueue.Value.Should().BeFalse();
            ObjectIdTargets().Should().NotContain(name);
            var zip = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = solution })).ExportSolutionFile;
            var (_, customizations, _) = SolutionZip.Read(zip);
            var exported = customizations.Descendants("entity").Single(e => string.Equals((string?)e.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase));
            exported.Element("IsValidForQueue")!.Value.Should().Be("0");
        }
        finally
        {
            await Store.LoadAsync();
            await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = name });
        }
    }

    [Fact]
    public async Task Upgrade_reports_a_kept_IsValidForQueue_the_table_cannot_have()
    {
        var (type, key) = UnmodelledXml.EntityKey("annotation");
        var original = Store.GetUnmodelledXml(type, key);
        var kept = new XElement("entity", new XAttribute("Name", "annotation"), new XElement("IsValidForQueue", "1"));
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.SetUnmodelledXmlAsync(s, type, key, kept.ToString(SaveOptions.DisableFormatting), default));
        try
        {
            var logger = new ListLogger<QueueMetadataTests>();
            await QueueSettingsUpgrade.EnsureAsync(Store, fx.App.Services.GetRequiredService<IDbConnectionFactory>(), default, logger);
            (Store.GetEntity("annotation")!.IsValidForQueue?.Value == true).Should().BeFalse();
            logger.Entries.Select(e => e.Message).Should().Contain(m => m.Contains("IsValidForQueue of 'annotation'") && m.Contains("not applied"));
        }
        finally
        {
            await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.SetUnmodelledXmlAsync(s, type, key, original, default));
        }
    }
}
