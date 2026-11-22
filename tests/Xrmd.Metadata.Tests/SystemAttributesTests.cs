using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

public class SystemAttributesTests
{
    [Fact]
    public void Complete_adds_primary_key_system_columns_owner_and_money_base()
    {
        var entity = new EntityMetadata { LogicalName = "pb_x", OwnershipType = OwnershipTypes.UserOwned };

        var attrs = SystemAttributes.Complete(entity, [new MoneyAttributeMetadata { LogicalName = "pb_amount" }]);

        attrs.Select(a => a.LogicalName).Should().Equal(
            "pb_xid", "pb_amount", "pb_amount_base", "transactioncurrencyid", "exchangerate",
            "createdon", "createdby", "modifiedon", "modifiedby",
            "owninguser", "owningteam", "owningbusinessunit",
            "statecode", "statuscode", "versionnumber", "ownerid");
        attrs[0].IsPrimaryId.Should().BeTrue();
        attrs[0].AttributeType.Should().Be(AttributeTypeCode.Uniqueidentifier);
        attrs.Single(a => a.LogicalName == "ownerid").AttributeType.Should().Be(AttributeTypeCode.Owner);
        attrs.Single(a => a.LogicalName == "pb_amount_base").Should().BeOfType<MoneyAttributeMetadata>()
            .Which.IsBaseCurrency.Should().BeTrue();
        attrs.Should().OnlyContain(a => a.EntityLogicalName == "pb_x" || a.LogicalName == "pb_amount");
    }

    [Fact]
    public void Complete_on_organization_owned_entity_has_no_ownerid() =>
        SystemAttributes.Complete(new EntityMetadata { LogicalName = "pb_x", OwnershipType = OwnershipTypes.OrganizationOwned }, [])
            .Select(a => a.LogicalName).Should().NotContain("ownerid");

    [Fact]
    public void Complete_is_idempotent()
    {
        var entity = new EntityMetadata { LogicalName = "pb_x", OwnershipType = OwnershipTypes.UserOwned };
        var once = SystemAttributes.Complete(entity, [new MoneyAttributeMetadata { LogicalName = "pb_amount" }]);

        SystemAttributes.Complete(entity, once).Should().Equal(once);
    }

    /// <summary>The <c>_base</c> companion has the Dataverse metadata (observed on <c>xv_budget_base</c>, plan 2.10 Global Constraints).</summary>
    [Fact]
    public void Money_base_has_dataverse_metadata()
    {
        var money = new MoneyAttributeMetadata("xv_Budget")
        {
            LogicalName = "xv_budget", Precision = 2, PrecisionSource = 0, MinValue = 0, MaxValue = 1000,
            DisplayName = SystemAttributes.CreateLabel("Budget", 1033),
        };

        var b = SystemAttributes.MoneyBase(money);

        b.LogicalName.Should().Be("xv_budget_base");
        b.SchemaName.Should().Be("xv_Budget_Base");
        b.Precision.Should().Be(4);
        b.MinValue.Should().Be(-922337203685477);
        b.MaxValue.Should().Be(922337203685477);
        b.PrecisionSource.Should().Be(2);
        b.IsBaseCurrency.Should().BeTrue();
        b.IsValidForCreate.Should().BeFalse();
        b.IsValidForUpdate.Should().BeFalse();
        b.DisplayName.UserLocalizedLabel.Label.Should().Be("Budget (Base)");
    }

    [Fact]
    public void Complete_adds_currency_pair_once_for_two_money_columns()
    {
        var entity = new EntityMetadata { LogicalName = "pb_x", OwnershipType = OwnershipTypes.UserOwned };

        var attrs = SystemAttributes.Complete(entity,
            [new MoneyAttributeMetadata { LogicalName = "pb_amount" }, new MoneyAttributeMetadata { LogicalName = "pb_cost" }]);

        attrs.Select(a => a.LogicalName).Should().ContainInOrder(
            "pb_amount", "pb_amount_base", "pb_cost", "pb_cost_base", "transactioncurrencyid", "exchangerate");
        attrs.Count(a => a.LogicalName == "transactioncurrencyid").Should().Be(1);
        attrs.Count(a => a.LogicalName == "exchangerate").Should().Be(1);
        attrs.Single(a => a.LogicalName == "transactioncurrencyid").Should().BeOfType<LookupAttributeMetadata>()
            .Which.Targets.Should().Equal("transactioncurrency");
        attrs.Single(a => a.LogicalName == "exchangerate").Should().BeOfType<DecimalAttributeMetadata>()
            .Which.Precision.Should().Be(10);
        attrs.Where(a => a.LogicalName is "transactioncurrencyid" or "exchangerate").Should().OnlyContain(a => a.EntityLogicalName == "pb_x");
        SystemAttributes.Complete(entity, attrs).Select(a => a.LogicalName).Should().Equal(attrs.Select(a => a.LogicalName));
    }

    [Fact]
    public void Complete_without_money_adds_no_currency_pair() =>
        SystemAttributes.Complete(new EntityMetadata { LogicalName = "pb_x" }, [new StringAttributeMetadata { LogicalName = "pb_name" }])
            .Select(a => a.LogicalName).Should().NotContain(["transactioncurrencyid", "exchangerate"]);

    [Fact]
    public void Currency_schema_tells_a_table_with_the_pair_and_its_money_columns()
    {
        var entity = new EntityMetadata { LogicalName = "pb_x" };
        entity.SetAttributes(SystemAttributes.Complete(entity,
            [new MoneyAttributeMetadata { LogicalName = "pb_amount" }, new StringAttributeMetadata { LogicalName = "pb_name" }]));
        var plain = new EntityMetadata { LogicalName = "pb_y" };
        plain.SetAttributes(SystemAttributes.Complete(plain, [new StringAttributeMetadata { LogicalName = "pb_name" }]));

        CurrencySchema.HasCurrencyColumns(entity).Should().BeTrue();
        CurrencySchema.HasCurrencyColumns(plain).Should().BeFalse();
        CurrencySchema.MoneyAttributes(entity).Select(a => a.LogicalName).Should().Equal("pb_amount");
        CurrencySchema.MoneyAttributes(plain).Should().BeEmpty();
    }

    [Fact]
    public void Complete_of_an_elastic_user_owned_table_has_partition_and_ttl_but_no_state()
    {
        var entity = new EntityMetadata { LogicalName = "pb_x", TableType = "Elastic", OwnershipType = OwnershipTypes.UserOwned };

        var attrs = SystemAttributes.Complete(entity, [new StringAttributeMetadata { LogicalName = "pb_name" }]);

        attrs.Select(a => a.LogicalName).Should().Contain(["partitionid", "ttlinseconds", "ownerid", "versionnumber", "createdon"]);
        attrs.Select(a => a.LogicalName).Should().NotContain(["statecode", "statuscode"]);
        var partition = attrs.Single(a => a.LogicalName == "partitionid").Should().BeOfType<StringAttributeMetadata>().Which;
        partition.MaxLength.Should().Be(100);
        partition.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.None);
        partition.IsValidForCreate.Should().BeTrue();
        partition.IsValidForUpdate.Should().BeFalse();
        var ttl = attrs.Single(a => a.LogicalName == "ttlinseconds").Should().BeOfType<IntegerAttributeMetadata>().Which;
        ttl.MinValue.Should().Be(1);
        ttl.MaxValue.Should().Be(int.MaxValue);
        ttl.Format.Should().Be(IntegerFormat.None);
        ttl.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.None);
    }

    [Fact]
    public void Complete_of_an_elastic_table_is_idempotent_and_organization_owned_has_no_ownerid()
    {
        var entity = new EntityMetadata { LogicalName = "pb_x", TableType = "Elastic", OwnershipType = OwnershipTypes.OrganizationOwned };
        var once = SystemAttributes.Complete(entity, []);

        SystemAttributes.Complete(entity, once).Should().Equal(once);
        once.Select(a => a.LogicalName).Should().NotContain("ownerid").And.Contain("partitionid");
    }

    [Fact]
    public void Complete_of_a_standard_table_has_no_partition_column() =>
        SystemAttributes.Complete(new EntityMetadata { LogicalName = "pb_x", TableType = "Standard" }, [])
            .Select(a => a.LogicalName).Should().NotContain(["partitionid", "ttlinseconds"]).And.Contain(["statecode", "statuscode"]);
}
