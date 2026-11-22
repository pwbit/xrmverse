using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>
/// Which columns can be secured with field security (plan 14, Global Constraints, "Securable columns"): one case per
/// securable type and one per excluded type, name, companion and table.
/// </summary>
public class FieldSecurabilityTests
{
    /// <summary>Sets a property the SDK exposes read-only (the server sets it).</summary>
    private static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property)!.GetSetMethod(nonPublic: true)!.Invoke(target, [value]);

    private static EntityMetadata Table(string name = "new_thing", params AttributeMetadata[] extra)
    {
        var entity = new EntityMetadata { LogicalName = name, OwnershipType = OwnershipTypes.UserOwned };
        Set(entity, nameof(EntityMetadata.PrimaryIdAttribute), name + "id");
        Set(entity, nameof(EntityMetadata.PrimaryNameAttribute), "new_name");
        entity.SetAttributes(SystemAttributes.Complete(entity, [new StringAttributeMetadata { LogicalName = "new_name" }, .. extra]));
        return entity;
    }

    private static bool Securable(AttributeMetadata attribute, string table = "new_thing")
    {
        var entity = Table(table, attribute);
        return FieldSecurability.IsSecurable(entity, entity.Attributes.Single(a => a.LogicalName == attribute.LogicalName));
    }

    private static bool Securable(string column, string table = "new_thing")
    {
        var entity = Table(table);
        return FieldSecurability.IsSecurable(entity, entity.Attributes.Single(a => a.LogicalName == column));
    }

    public static TheoryData<AttributeMetadata> SecurableTypes => new()
    {
        new StringAttributeMetadata { LogicalName = "new_text" },
        new MemoAttributeMetadata { LogicalName = "new_memo" },
        new IntegerAttributeMetadata { LogicalName = "new_int" },
        new BigIntAttributeMetadata { LogicalName = "new_big" },
        new DecimalAttributeMetadata { LogicalName = "new_dec" },
        new DoubleAttributeMetadata { LogicalName = "new_dbl" },
        new MoneyAttributeMetadata { LogicalName = "new_money" },
        new BooleanAttributeMetadata { LogicalName = "new_flag" },
        new PicklistAttributeMetadata { LogicalName = "new_choice" },
        new MultiSelectPicklistAttributeMetadata { LogicalName = "new_choices" },
        new DateTimeAttributeMetadata { LogicalName = "new_when" },
        new FileAttributeMetadata { LogicalName = "new_doc" },
        new ImageAttributeMetadata { LogicalName = "new_pic" },
    };

    [Theory]
    [MemberData(nameof(SecurableTypes))]
    public void Columns_of_the_listed_types_are_securable(AttributeMetadata attribute) =>
        Securable(attribute).Should().BeTrue(attribute.GetType().Name);

    [Fact]
    public void A_system_column_of_a_listed_type_is_securable()
    {
        var account = new EntityMetadata { LogicalName = "account" };
        Set(account, nameof(EntityMetadata.PrimaryIdAttribute), "accountid");
        Set(account, nameof(EntityMetadata.PrimaryNameAttribute), "name");
        var phone = new StringAttributeMetadata { LogicalName = "telephone1" };
        account.SetAttributes([new StringAttributeMetadata { LogicalName = "name" }, phone]);
        FieldSecurability.IsSecurable(account, phone).Should().BeTrue();
    }

    [Fact]
    public void The_primary_id_and_the_primary_name_are_not_securable()
    {
        Securable("new_thingid").Should().BeFalse();
        Securable("new_name").Should().BeFalse();
    }

    [Fact]
    public void Lookup_customer_and_owner_columns_are_not_securable()
    {
        Securable(new LookupAttributeMetadata { LogicalName = "new_parentid", Targets = ["account"] }).Should().BeFalse();
        var customer = new LookupAttributeMetadata { LogicalName = "new_customerid", Targets = ["account", "contact"] };
        Set(customer, nameof(AttributeMetadata.AttributeType), AttributeTypeCode.Customer);
        Securable(customer).Should().BeFalse();
        Securable("ownerid").Should().BeFalse();
        Securable("owninguser").Should().BeFalse();
    }

    [Theory]
    [InlineData("statecode")]
    [InlineData("statuscode")]
    [InlineData("createdon")]
    [InlineData("modifiedon")]
    [InlineData("createdby")]
    [InlineData("modifiedby")]
    [InlineData("ownerid")]
    [InlineData("versionnumber")]
    public void The_listed_system_columns_are_not_securable(string column) => Securable(column).Should().BeFalse(column);

    [Fact]
    public void Generated_companion_columns_are_not_securable()
    {
        var money = new MoneyAttributeMetadata { LogicalName = "new_amount" };
        var entity = Table("new_thing", money);
        FieldSecurability.IsSecurable(entity, entity.Attributes.Single(a => a.LogicalName == "new_amount_base")).Should().BeFalse("*_base");

        var image = new ImageAttributeMetadata { LogicalName = "new_pic", MetadataId = Guid.NewGuid() };
        var file = new FileAttributeMetadata { LogicalName = "new_doc", MetadataId = Guid.NewGuid() };
        var withFiles = Table("new_thing", [image, file, .. FileColumns.Companions(image), .. FileColumns.Companions(file)]);
        foreach (var companion in withFiles.Attributes.Where(a => a.AttributeOf is "new_pic" or "new_doc"))
            FieldSecurability.IsSecurable(withFiles, companion).Should().BeFalse(companion.LogicalName);
        withFiles.Attributes.Should().Contain(a => a.LogicalName == "new_pic_url").And.Contain(a => a.LogicalName == "new_pic_timestamp");

        // The name companions of a lookup or a choice, as a solution file gives them: AttributeOf names the main column.
        var lookup = new LookupAttributeMetadata { LogicalName = "new_parentid", Targets = ["account"] };
        var name = new StringAttributeMetadata { LogicalName = "new_parentidname" };
        var yomi = new StringAttributeMetadata { LogicalName = "new_parentidyominame" };
        Set(name, nameof(AttributeMetadata.AttributeOf), "new_parentid");
        Set(yomi, nameof(AttributeMetadata.AttributeOf), "new_parentid");
        var choice = new PicklistAttributeMetadata { LogicalName = "new_choice" };
        var choiceName = new StringAttributeMetadata { LogicalName = "new_choicename" };
        Set(choiceName, nameof(AttributeMetadata.AttributeOf), "new_choice");
        var named = Table("new_thing", lookup, name, yomi, choice, choiceName);
        foreach (var companion in new[] { "new_parentidname", "new_parentidyominame", "new_choicename" })
            FieldSecurability.IsSecurable(named, named.Attributes.Single(a => a.LogicalName == companion)).Should().BeFalse(companion);
    }

    [Fact]
    public void A_column_named_like_a_companion_is_securable()
    {
        // Only metadata makes a companion: a name next to a sibling decides nothing, whatever the creation order.
        var siblings = Table("new_thing",
            new StringAttributeMetadata { LogicalName = "new_code" }, new StringAttributeMetadata { LogicalName = "new_codename" },
            new LookupAttributeMetadata { LogicalName = "new_user", Targets = ["systemuser"] }, new StringAttributeMetadata { LogicalName = "new_username" },
            new LookupAttributeMetadata { LogicalName = "new_ref", Targets = ["account"] }, new StringAttributeMetadata { LogicalName = "new_refyominame" },
            new IntegerAttributeMetadata { LogicalName = "new_size" }, new IntegerAttributeMetadata { LogicalName = "new_size_base" },
            new StringAttributeMetadata { LogicalName = "new_site" }, new StringAttributeMetadata { LogicalName = "new_site_url" },
            new DateTimeAttributeMetadata { LogicalName = "new_seen" }, new BigIntAttributeMetadata { LogicalName = "new_seen_timestamp" });
        foreach (var column in new[] { "new_codename", "new_username", "new_refyominame", "new_size_base", "new_site_url", "new_seen_timestamp" })
            FieldSecurability.IsSecurable(siblings, siblings.Attributes.Single(a => a.LogicalName == column)).Should().BeTrue(column);
        Securable(new StringAttributeMetadata { LogicalName = "new_firstname" }).Should().BeTrue();
    }

    [Fact]
    public void Columns_of_other_types_are_not_securable()
    {
        Securable(new UniqueIdentifierAttributeMetadata { LogicalName = "new_ref" }).Should().BeFalse();
        Securable(new EntityNameAttributeMetadata { LogicalName = "new_kind" }).Should().BeFalse();
        Securable("statecode").Should().BeFalse();
    }

    [Fact]
    public void A_formula_column_is_not_securable()
    {
        // Learn (field-level-security, "Which columns can be secured"): formula columns cannot be secured (final review I2).
        var formula = new DecimalAttributeMetadata { LogicalName = "new_fx", SourceType = 3 };
        Securable(formula).Should().BeFalse();
        var entity = Table("new_thing", formula);
        var column = entity.Attributes.Single(a => a.LogicalName == "new_fx");
        FieldSecurability.Apply(entity, column);
        (column.CanBeSecuredForCreate, column.CanBeSecuredForRead, column.CanBeSecuredForUpdate).Should().Be((false, false, false));
        // A calculated column of the same type stays securable.
        Securable(new DecimalAttributeMetadata { LogicalName = "new_calc", SourceType = 1 }).Should().BeTrue();
    }

    [Theory]
    [InlineData("fieldsecurityprofile")]
    [InlineData("fieldpermission")]
    [InlineData("systemuserprofiles")]
    [InlineData("teamprofiles")]
    [InlineData("principalobjectattributeaccess")]
    [InlineData("audit")]
    [InlineData("systemuser")]
    [InlineData("team")]
    [InlineData("businessunit")]
    [InlineData("role")]
    [InlineData("privilege")]
    [InlineData("systemuserroles")]
    [InlineData("teamroles")]
    [InlineData("teammembership")]
    [InlineData("roleprivileges")]
    [InlineData("privilegeobjecttypecodes")]
    public void No_column_of_the_security_and_audit_tables_is_securable(string table) =>
        Securable(new StringAttributeMetadata { LogicalName = "new_text" }, table).Should().BeFalse(table);

    [Fact]
    public void The_security_tables_include_the_field_security_tables()
    {
        FieldSecurability.SecurityEntities.Should().Contain(FieldSecurityTables.All);
        FieldSecurability.SecurityEntities.Should().Contain(["systemuser", "team", "businessunit", "role", "privilege", "systemuserroles",
            "teamroles", "teammembership", "roleprivileges", "privilegeobjecttypecodes"]);
    }

    [Fact]
    public void Apply_sets_the_three_flags_from_the_rule()
    {
        var entity = Table("new_thing", new StringAttributeMetadata { LogicalName = "new_text" });
        var text = entity.Attributes.Single(a => a.LogicalName == "new_text");
        var primary = entity.Attributes.Single(a => a.LogicalName == "new_name");
        Set(primary, nameof(AttributeMetadata.CanBeSecuredForRead), true); // a stored value never wins

        FieldSecurability.Apply(entity, text);
        FieldSecurability.Apply(entity, primary);

        (text.CanBeSecuredForCreate, text.CanBeSecuredForRead, text.CanBeSecuredForUpdate).Should().Be((true, true, true));
        (primary.CanBeSecuredForCreate, primary.CanBeSecuredForRead, primary.CanBeSecuredForUpdate).Should().Be((false, false, false));
    }

    [Fact]
    public void A_companion_names_its_main_column()
    {
        var money = new MoneyAttributeMetadata { LogicalName = "new_salary" };
        var doc = new FileAttributeMetadata { LogicalName = "new_doc", MetadataId = Guid.NewGuid() };
        var pic = new ImageAttributeMetadata { LogicalName = "new_pic", MetadataId = Guid.NewGuid() };
        var entity = Table("new_thing", [money, doc, .. FileColumns.Companions(doc), pic, .. FileColumns.Companions(pic),
            new StringAttributeMetadata { LogicalName = "new_codename" }, new StringAttributeMetadata { LogicalName = "new_code" }]);
        string? MainOf(string column) => FieldSecurability.MainColumnOf(entity, entity.Attributes.Single(a => a.LogicalName == column));

        MainOf("new_salary_base").Should().Be("new_salary");
        MainOf("new_doc_name").Should().Be("new_doc");
        foreach (var companion in new[] { "new_picid", "new_pic_timestamp", "new_pic_url" })
            MainOf(companion).Should().Be("new_pic", companion);
        MainOf("new_salary").Should().BeNull("a main column is no companion");
        MainOf("new_codename").Should().BeNull("the name alone never makes a companion");
    }
}
