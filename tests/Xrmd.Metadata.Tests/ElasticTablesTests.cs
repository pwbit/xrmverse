using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>Plan 3.4, task 1: the table type, the partition id rule and the features an elastic table refuses (rulings E9).</summary>
public class ElasticTablesTests
{
    private static EntityMetadata Elastic(OwnershipTypes ownership = OwnershipTypes.UserOwned) =>
        new() { LogicalName = "new_sensor", TableType = "Elastic", OwnershipType = ownership };

    [Theory]
    [InlineData(null, "Standard")]
    [InlineData("", "Standard")]
    [InlineData("standard", "Standard")]
    [InlineData("Standard", "Standard")]
    [InlineData("ELASTIC", "Elastic")]
    [InlineData("elastic", "Elastic")]
    public void NormalizeTableType_accepts_standard_and_elastic_in_any_case(string? value, string expected) =>
        ElasticTables.NormalizeTableType(value).Should().Be(expected);

    [Fact]
    public void NormalizeTableType_refuses_other_types_naming_the_value()
    {
        var act = () => ElasticTables.NormalizeTableType("Virtual");
        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400 && e.Message.Contains("Virtual"));
    }

    [Fact]
    public void IsElastic_reads_the_table_type()
    {
        ElasticTables.IsElastic(Elastic()).Should().BeTrue();
        ElasticTables.IsElastic(new EntityMetadata { LogicalName = "account" }).Should().BeFalse();
        ElasticTables.IsElastic(new EntityMetadata { LogicalName = "account", TableType = "Standard" }).Should().BeFalse();
    }

    [Fact]
    public void NoSqlKey_is_the_primary_id_and_partitionid()
    {
        var entity = Elastic();

        var key = ElasticTables.NoSqlKey(entity);

        key.LogicalName.Should().Be("keyfornosqlentitywithpkpartitionid");
        key.SchemaName.Should().Be("KeyForNoSqlEntityWithPKPartitionId");
        key.KeyAttributes.Should().Equal("new_sensorid", "partitionid");
        key.EntityLogicalName.Should().Be("new_sensor");
    }

    [Fact]
    public void PartitionColumnOf_appends_pid() => ElasticTables.PartitionColumnOf("new_deviceid").Should().Be("new_deviceidpid");

    [Fact]
    public void DemandValidPartitionId_accepts_a_plain_value_and_null()
    {
        ElasticTables.DemandValidPartitionId("device-001");
        ElasticTables.DemandValidPartitionId(null);
        ElasticTables.DemandValidPartitionId(new string('a', 100));
    }

    [Fact]
    public void DemandValidPartitionId_refuses_101_characters() =>
        ((Action)(() => ElasticTables.DemandValidPartitionId(new string('a', 101)))).Should().Throw<XrmdFaultException>()
            .Where(e => e.HttpStatus == 400 && e.Message.StartsWith("Invalid partitionid value"));

    [Theory]
    [InlineData("/")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("*")]
    [InlineData("%")]
    [InlineData("&")]
    [InlineData(":")]
    [InlineData("\\")]
    [InlineData("?")]
    [InlineData("+")]
    public void DemandValidPartitionId_refuses_each_forbidden_character(string character) =>
        ((Action)(() => ElasticTables.DemandValidPartitionId("a" + character + "b"))).Should().Throw<XrmdFaultException>()
            .Where(e => e.HttpStatus == 400 && e.Message.StartsWith("Invalid partitionid value"));

    [Fact]
    public void DemandValidEntity_accepts_user_and_organization_owned()
    {
        ElasticTables.DemandValidEntity(Elastic());
        ElasticTables.DemandValidEntity(Elastic(OwnershipTypes.OrganizationOwned));
        ElasticTables.DemandValidEntity(new EntityMetadata { LogicalName = "new_std", OwnershipType = OwnershipTypes.TeamOwned });
    }

    [Fact]
    public void DemandValidEntity_refuses_the_settings_of_rule_E9()
    {
        foreach (var bad in new Action<EntityMetadata>[]
                 {
                     e => e.OwnershipType = OwnershipTypes.TeamOwned,
                     e => e.IsActivity = true,
                     e => e.AutoCreateAccessTeams = true,
                     e => e.IsDuplicateDetectionEnabled = new BooleanManagedProperty(true),
                     e => e.TableType = "Virtual",
                 })
        {
            var entity = Elastic();
            bad(entity);
            ((Action)(() => ElasticTables.DemandValidEntity(entity))).Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
        }
    }

    private static LookupAttributeMetadata Customer()
    {
        var customer = new LookupAttributeMetadata { LogicalName = "new_c", Targets = ["account", "contact"] };
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!
            .GetSetMethod(nonPublic: true)!.Invoke(customer, [AttributeTypeCode.Customer]);
        return customer;
    }

    public static TheoryData<AttributeMetadata> RefusedAttributes() => new()
    {
        new MoneyAttributeMetadata { LogicalName = "new_m" },
        new MultiSelectPicklistAttributeMetadata { LogicalName = "new_ms" },
        new StateAttributeMetadata { LogicalName = "new_state" },
        new StatusAttributeMetadata { LogicalName = "new_status" },
        new ImageAttributeMetadata { LogicalName = "new_img" },
        new IntegerAttributeMetadata { LogicalName = "new_i", Format = IntegerFormat.Duration },
        new IntegerAttributeMetadata { LogicalName = "new_i2", Format = IntegerFormat.TimeZone },
        Customer(),
        new StringAttributeMetadata { LogicalName = "new_calc", SourceType = 1 },
        new StringAttributeMetadata { LogicalName = "partitionid" },
        new IntegerAttributeMetadata { LogicalName = "ttlinseconds" },
    };

    [Theory]
    [MemberData(nameof(RefusedAttributes))]
    public void DemandValidAttribute_refuses_each_E9_type(AttributeMetadata attribute) =>
        ((Action)(() => ElasticTables.DemandValidAttribute(Elastic(), attribute))).Should().Throw<XrmdFaultException>()
            .Which.HttpStatus.Should().Be(400);

    [Fact]
    public void DemandValidAttribute_accepts_json_strings_and_plain_columns()
    {
        var json = new StringAttributeMetadata { LogicalName = "new_json", FormatName = StringFormatName.Json };
        ElasticTables.DemandValidAttribute(Elastic(), json);
        ElasticTables.DemandValidAttribute(Elastic(), new StringAttributeMetadata { LogicalName = "new_s" });
        ElasticTables.DemandValidAttribute(Elastic(), new MemoAttributeMetadata { LogicalName = "new_memo" });
        ElasticTables.DemandValidAttribute(Elastic(), new IntegerAttributeMetadata { LogicalName = "new_n", Format = IntegerFormat.None });
        ElasticTables.DemandValidAttribute(Elastic(), new PicklistAttributeMetadata { LogicalName = "new_p" });
        ElasticTables.DemandValidAttribute(Elastic(), new LookupAttributeMetadata { LogicalName = "new_l", Targets = ["account"] });
    }

    [Fact]
    public void DemandValidAttribute_does_not_restrict_a_standard_table() =>
        ElasticTables.DemandValidAttribute(new EntityMetadata { LogicalName = "account" }, new MoneyAttributeMetadata { LogicalName = "new_m" });

    private static OneToManyRelationshipMetadata Relationship(CascadeType all) => new()
    {
        SchemaName = "new_r",
        CascadeConfiguration = new CascadeConfiguration
        {
            Assign = all, Delete = all, Merge = all, Reparent = all, Share = all, Unshare = all, RollupView = all,
        },
    };

    [Fact]
    public void DemandValidRelationship_accepts_no_cascade_and_standard_pairs()
    {
        var standard = new EntityMetadata { LogicalName = "account" };
        ElasticTables.DemandValidRelationship(Relationship(CascadeType.NoCascade), Elastic(), standard);
        ElasticTables.DemandValidRelationship(Relationship(CascadeType.NoCascade), standard, Elastic());
        ElasticTables.DemandValidRelationship(new OneToManyRelationshipMetadata { SchemaName = "new_r" }, Elastic(), standard);
        ElasticTables.DemandValidRelationship(Relationship(CascadeType.Cascade), standard, standard);
    }

    [Theory]
    [InlineData(CascadeType.Cascade)]
    [InlineData(CascadeType.Restrict)]
    [InlineData(CascadeType.RemoveLink)]
    public void DemandValidRelationship_refuses_cascade_when_a_side_is_elastic(CascadeType cascade)
    {
        var standard = new EntityMetadata { LogicalName = "account" };
        ((Action)(() => ElasticTables.DemandValidRelationship(Relationship(cascade), Elastic(), standard))).Should().Throw<XrmdFaultException>();
        ((Action)(() => ElasticTables.DemandValidRelationship(Relationship(cascade), standard, Elastic()))).Should().Throw<XrmdFaultException>();
    }

    [Fact]
    public void DemandNotElastic_names_the_feature()
    {
        ElasticTables.DemandNotElastic(new EntityMetadata { LogicalName = "account" }, "many-to-many relationships");
        ((Action)(() => ElasticTables.DemandNotElastic(Elastic(), "many-to-many relationships"))).Should().Throw<XrmdFaultException>()
            .Where(e => e.HttpStatus == 400 && e.Message.Contains("many-to-many relationships") && e.Message.Contains("elastic table"));
    }
}
