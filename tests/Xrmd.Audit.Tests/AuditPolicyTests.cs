using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Audit.Tests;

/// <summary>
/// What is audited (plan 13, ruling A1): the organization, table and column switches, and the attribute change of a create, an
/// update and a delete.
/// </summary>
public sealed class AuditPolicyTests
{
    private static readonly Guid Id = Guid.NewGuid();

    private static EntityMetadata Table(bool audited, bool nameAudited = true)
    {
        var entity = new EntityMetadata { LogicalName = "new_thing", SchemaName = "new_Thing", IsAuditEnabled = new BooleanManagedProperty(audited) };
        entity.SetAttributes(
        [
            new UniqueIdentifierAttributeMetadata { LogicalName = "new_thingid", SchemaName = "new_ThingId", IsAuditEnabled = new BooleanManagedProperty(false) },
            new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", IsAuditEnabled = new BooleanManagedProperty(nameAudited) },
            new IntegerAttributeMetadata { LogicalName = "new_count", SchemaName = "new_Count", IsAuditEnabled = new BooleanManagedProperty(true) },
            new MoneyAttributeMetadata { LogicalName = "new_amount", SchemaName = "new_Amount", IsAuditEnabled = new BooleanManagedProperty(true) },
            new LookupAttributeMetadata { LogicalName = "new_userid", SchemaName = "new_UserId", Targets = ["systemuser"], IsAuditEnabled = new BooleanManagedProperty(true) },
            new MultiSelectPicklistAttributeMetadata { LogicalName = "new_colors", SchemaName = "new_Colors", IsAuditEnabled = new BooleanManagedProperty(true) },
            new DateTimeAttributeMetadata { LogicalName = "modifiedon", SchemaName = "ModifiedOn", IsAuditEnabled = new BooleanManagedProperty(false) },
            Companion(new StringAttributeMetadata { LogicalName = "new_useridname", SchemaName = "new_UserIdName", IsAuditEnabled = new BooleanManagedProperty(true) }, "new_userid"),
            new FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_Doc", IsAuditEnabled = new BooleanManagedProperty(true) },
        ]);
        return entity;
    }

    /// <summary><paramref name="a"/> as a companion of <paramref name="of"/> (<c>AttributeOf</c> has no public setter).</summary>
    private static AttributeMetadata Companion(AttributeMetadata a, string of)
    {
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeOf))!.GetSetMethod(nonPublic: true)!.Invoke(a, [of]);
        return a;
    }

    private static Entity Row(params (string Name, object? Value)[] values)
    {
        var row = new Entity("new_thing", Id);
        foreach (var (name, value) in values) row[name] = value;
        return row;
    }

    [Fact]
    public void Audit_policy_follows_the_three_switches()
    {
        var audited = Table(audited: true);
        AuditPolicy.IsAudited(new OrganizationAuditSettings(false, 30), audited).Should().BeFalse("the organization switch is off");
        AuditPolicy.IsAudited(new OrganizationAuditSettings(true, 30), audited).Should().BeTrue();

        var off = Table(audited: false);
        AuditPolicy.IsAudited(off).Should().BeFalse();
        AuditPolicy.IsAudited(new OrganizationAuditSettings(true, 30), off).Should().BeFalse("the table switch is off");
        AuditPolicy.AuditedAttributes(off).Should().BeEmpty();
        new EntityMetadata { LogicalName = "x" }.Invoking(AuditPolicy.IsAudited).Should().NotThrow();
        AuditPolicy.IsAudited(new EntityMetadata { LogicalName = "x" }).Should().BeFalse("a table without the setting is not audited");

        AuditPolicy.AuditedAttributes(audited).Select(a => a.LogicalName)
            .Should().BeEquivalentTo(["new_name", "new_count", "new_amount", "new_userid", "new_colors"],
                "the key, switched-off columns, companions and file columns are never audited");
        AuditPolicy.AuditedAttributes(Table(audited: true, nameAudited: false)).Select(a => a.LogicalName)
            .Should().NotContain("new_name", "the column switch is off");
    }

    [Fact]
    public void Update_change_holds_only_changed_audited_columns()
    {
        var entity = Table(audited: true);
        var before = Row(("new_name", "same"), ("new_count", 1), ("new_amount", new Money(5m)),
            ("new_userid", new EntityReference("systemuser", Id) { Name = "Old name" }),
            ("new_colors", new OptionSetValueCollection([new OptionSetValue(2), new OptionSetValue(1)])),
            ("modifiedon", DateTime.UtcNow.AddDays(-1)));
        var after = Row(("new_name", "same"), ("new_count", 2), ("new_amount", new Money(5m)),
            ("new_userid", new EntityReference("systemuser", Id)),
            ("new_colors", new OptionSetValueCollection([new OptionSetValue(1), new OptionSetValue(2)])),
            ("modifiedon", DateTime.UtcNow));

        var change = AuditWriter.AttributeChange(entity, before, after);

        change.Should().NotBeNull();
        change!.Old.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, object?>("new_count", 1));
        change.New.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, object?>("new_count", 2));

        AuditWriter.AttributeChange(entity, Row(("new_name", "same"), ("modifiedon", DateTime.UtcNow)), Row(("new_name", "same")))
            .Should().BeNull("a column absent from both images is null in both, and modifiedon is not audited");
        AuditWriter.AttributeChange(entity, Row(("new_name", "same")), Row(("new_name", "same"))).Should().BeNull("nothing differs");

        var cleared = AuditWriter.AttributeChange(entity, Row(("new_name", "x")), Row());
        cleared!.Old.Should().Equal(new Dictionary<string, object?> { ["new_name"] = "x" });
        cleared.New.Should().Equal(new Dictionary<string, object?> { ["new_name"] = null });
    }

    [Fact]
    public void Create_and_delete_changes_hold_the_non_null_audited_columns()
    {
        var entity = Table(audited: true);
        var row = Row(("new_name", "n"), ("new_count", 3), ("new_doc", Guid.NewGuid()), ("modifiedon", DateTime.UtcNow), ("new_thingid", Id));

        var created = AuditWriter.AttributeChange(entity, null, row)!;
        created.Old.Should().BeEmpty();
        created.New.Keys.Should().BeEquivalentTo(["new_name", "new_count"]);

        var deleted = AuditWriter.AttributeChange(entity, row, null)!;
        deleted.New.Should().BeEmpty();
        deleted.Old.Keys.Should().BeEquivalentTo(["new_name", "new_count"]);

        AuditWriter.AttributeChange(entity, null, Row()).Should().NotBeNull("a create is recorded even without an audited value");
        AuditWriter.AttributeChange(Table(audited: false), null, row).Should().BeNull("a table that is not audited has nothing to record");
    }
}
