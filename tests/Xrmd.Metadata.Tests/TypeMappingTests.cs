using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions.Storage;
using Xrmd.Abstractions.Storage.SqlServer;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

public class TypeMappingTests
{
    private static readonly ISqlDialect D = SqlServerDialect.Instance;

    /// <summary>The columns of <paramref name="a"/> with their SQL Server types.</summary>
    private static IEnumerable<(string Name, string SqlType)> Columns(AttributeMetadata a) =>
        TypeMapping.ToColumns(a).Select(c => (c.Name, c.SqlType(D)));

    // Keys instead of AttributeMetadata instances keep theory data serializable.
    private static AttributeMetadata Create(string kind) => kind switch
    {
        "String100" => new StringAttributeMetadata { LogicalName = "x", MaxLength = 100 },
        "Memo" => new MemoAttributeMetadata { LogicalName = "x" },
        "Integer" => new IntegerAttributeMetadata { LogicalName = "x" },
        "BigInt" => new BigIntAttributeMetadata { LogicalName = "x" },
        "Decimal2" => new DecimalAttributeMetadata { LogicalName = "x", Precision = 2 },
        "Double" => new DoubleAttributeMetadata { LogicalName = "x" },
        "Money" => new MoneyAttributeMetadata { LogicalName = "x" },
        "Boolean" => new BooleanAttributeMetadata { LogicalName = "x" },
        "Picklist" => new PicklistAttributeMetadata { LogicalName = "x" },
        "State" => new StateAttributeMetadata { LogicalName = "x" },
        "Status" => new StatusAttributeMetadata { LogicalName = "x" },
        "MultiSelectPicklist" => new MultiSelectPicklistAttributeMetadata { LogicalName = "x" },
        "DateTime" => new DateTimeAttributeMetadata { LogicalName = "x" },
        "Lookup" => new LookupAttributeMetadata { LogicalName = "x" },
        "Owner" => SystemAttributes.Owner(),
        "Uniqueidentifier" => new UniqueIdentifierAttributeMetadata { LogicalName = "x" },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [InlineData("String100", "nvarchar(100)")]
    [InlineData("Memo", "nvarchar(max)")]
    [InlineData("Integer", "int")]
    [InlineData("BigInt", "bigint")]
    [InlineData("Decimal2", "decimal(23,2)")]
    [InlineData("Double", "float")]
    [InlineData("Money", "decimal(19,4)")]
    [InlineData("Boolean", "bit")]
    [InlineData("Picklist", "int")]
    [InlineData("State", "int")]
    [InlineData("Status", "int")]
    [InlineData("MultiSelectPicklist", "nvarchar(max)")]
    [InlineData("DateTime", "datetime2")]
    [InlineData("Lookup", "uniqueidentifier")]
    [InlineData("Owner", "uniqueidentifier")]
    [InlineData("Uniqueidentifier", "uniqueidentifier")]
    public void ToSql_maps_sdk_type_to_tsql(string kind, string expected) =>
        TypeMapping.ToSql(Create(kind), D).Should().Be(expected);

    /// <summary>
    /// Pre-flight C14: <c>transactioncurrency.exchangerate</c> is seeded with Precision 12 (above the public cap of 10, which
    /// only the seed path passes) and needs one more integer digit than <c>decimal(23,p)</c> leaves: 1E11 has twelve.
    /// </summary>
    [Theory]
    [InlineData(10, "decimal(23,10)")]
    [InlineData(11, "decimal(23,11)")]
    [InlineData(12, "decimal(24,12)")]
    public void Decimal_precision_12_maps_to_decimal_24_12(int precision, string expected) =>
        TypeMapping.ToSql(new DecimalAttributeMetadata { LogicalName = "x", Precision = precision }, D).Should().Be(expected);

    [Fact]
    public void ToColumns_money_adds_base_column() =>
        Columns(new MoneyAttributeMetadata { LogicalName = "pb_budget" }).Should().Equal(
            ("pb_budget", "decimal(19,4)"), ("pb_budget_base", "decimal(19,4)"));

    [Fact]
    public void ToColumns_owner_adds_type_column_but_plain_lookup_does_not()
    {
        Columns(SystemAttributes.Owner()).Should().Equal(
            ("ownerid", "uniqueidentifier"), ("owneridtype", "int"));
        Columns(new LookupAttributeMetadata { LogicalName = "pb_parentid" }).Should().Equal(
            ("pb_parentid", "uniqueidentifier"));
    }

    [Fact]
    public void ToColumns_versionnumber_is_rowversion() =>
        Columns(new BigIntAttributeMetadata { LogicalName = "versionnumber" }).Should().Equal(
            ("versionnumber", "rowversion"));

    [Fact]
    public void ToSql_file_is_a_guid_and_image_is_varbinary_max()
    {
        TypeMapping.ToSql(new FileAttributeMetadata { LogicalName = "x" }, D).Should().Be("uniqueidentifier");
        TypeMapping.ToSql(new ImageAttributeMetadata { LogicalName = "x" }, D).Should().Be("varbinary(max)");
    }

    [Fact]
    public void ToColumns_of_file_and_image_companions_keeps_the_ones_that_hold_a_value()
    {
        var file = new FileAttributeMetadata { LogicalName = "new_f", SchemaName = "new_f", MetadataId = Guid.NewGuid() };
        var image = new ImageAttributeMetadata { LogicalName = "new_i", SchemaName = "new_i", MetadataId = Guid.NewGuid() };

        Columns(file).Should().Equal(("new_f", "uniqueidentifier"));
        Columns(image).Should().Equal(("new_i", "varbinary(max)"));
        FileColumns.Companions(file).SelectMany(Columns).Should().Equal(("new_f_name", "nvarchar(255)"));
        FileColumns.Companions(image).SelectMany(Columns).Should().Equal(
            ("new_iid", "uniqueidentifier"), ("new_i_timestamp", "bigint"));
        FileColumns.Companions(image).Should().OnlyContain(c => c.AttributeOf == "new_i" && c.IsValidForCreate == false);
    }

    [Fact]
    public void ToSql_unsupported_type_throws() =>
        FluentActions.Invoking(() => TypeMapping.ToSql(new ManagedPropertyAttributeMetadata { LogicalName = "x" }, D))
            .Should().Throw<NotSupportedException>();

    private static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property)!.GetSetMethod(nonPublic: true)!.Invoke(target, [value]);

    private static T On<T>(T a, string entity) where T : AttributeMetadata
    {
        Set(a, nameof(AttributeMetadata.EntityLogicalName), entity);
        return a;
    }

    /// <summary>Plan 2.11 A7: an EntityName companion of a polymorphic lookup reads the lookup's type column; it has none of its own.</summary>
    [Fact]
    public void Entityname_companion_has_no_column()
    {
        var companion = new EntityNameAttributeMetadata { LogicalName = "objecttypecode" };
        Set(companion, nameof(AttributeMetadata.AttributeOf), "objectid");
        Columns(companion).Should().BeEmpty();
        TypeMapping.IsReadable(companion).Should().BeTrue();
        TypeMapping.EntityNameColumn(companion).Should().Be("objectidtype");

        // Without AttributeOf (activitypointer.activitytypecode) it is an int column holding the object type code.
        var own = new EntityNameAttributeMetadata { LogicalName = "activitytypecode" };
        Columns(own).Should().Equal(("activitytypecode", "int"));
        TypeMapping.EntityNameColumn(own).Should().Be("activitytypecode");
    }

    /// <summary>Plan 2.11 A8: a party list has no SQL column (its rows live in activityparty).</summary>
    [Fact]
    public void Party_list_has_no_column()
    {
        var to = new LookupAttributeMetadata { LogicalName = "to", Targets = ["account", "contact", "systemuser"] };
        Set(to, nameof(AttributeMetadata.AttributeType), AttributeTypeCode.PartyList);
        Columns(to).Should().BeEmpty();
        TypeMapping.IsReadable(to).Should().BeFalse();
        TypeMapping.IsPolymorphicLookup(to).Should().BeFalse();
    }

    /// <summary>Pre-flight C2: annotation.documentbody is never stored in SQL (A16).</summary>
    [Fact]
    public void Annotation_documentbody_has_no_column()
    {
        var body = On(new StringAttributeMetadata { LogicalName = "documentbody", MaxLength = 1073741823 }, "annotation");
        Columns(body).Should().BeEmpty();
        // Another table's column of that name is an ordinary column.
        Columns(On(new StringAttributeMetadata { LogicalName = "documentbody", MaxLength = 100 }, "new_thing"))
            .Should().Equal(("documentbody", "nvarchar(100)"));
    }

    /// <summary>Pre-flight C1: the managed lookups of plan 2.11 keep a type column whatever their targets.</summary>
    [Theory]
    [InlineData("email", "regardingobjectid")]
    [InlineData("activitypointer", "regardingobjectid")]
    [InlineData("annotation", "objectid")]
    [InlineData("activityparty", "partyid")]
    [InlineData("activityparty", "activityid")]
    public void Managed_activity_lookups_are_polymorphic(string entity, string lookup)
    {
        var a = On(new LookupAttributeMetadata { LogicalName = lookup, Targets = [] }, entity);
        TypeMapping.IsPolymorphicLookup(a).Should().BeTrue();
        Columns(a).Should().Equal((lookup, "uniqueidentifier"), (lookup + "type", "int"));
    }
}
