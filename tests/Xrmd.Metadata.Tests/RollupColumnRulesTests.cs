using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Computed;
using Xrmd.Tests.Shared;

namespace Xrmd.Metadata.Tests;

/// <summary>The metadata rules of rollup columns (plan 2.12b, Task 1), with a fake translator.</summary>
public class RollupColumnRulesTests
{
    private const string Relationship = "xrmv_o_xrmv_tid_xrmv_t";

    private static EntityMetadata Table(string name, params AttributeMetadata[] attributes)
    {
        var entity = new EntityMetadata { LogicalName = name, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes(attributes);
        return entity;
    }

    private static T Rollup<T>(T attribute) where T : AttributeMetadata
    {
        attribute.SourceType = RollupColumns.Rollup;
        ComputedColumnRules.SetFormula(attribute, "<xaml/>");
        return attribute;
    }

    private static T WithMask<T>(T attribute, int sourceType, int? mask) where T : AttributeMetadata
    {
        attribute.SourceType = sourceType;
        attribute.GetType().GetProperty("SourceTypeMask")!.GetSetMethod(nonPublic: true)!.Invoke(attribute, [mask]);
        return attribute;
    }

    /// <summary>The related table <c>xrmv_o</c>: a decimal, a money, a date, a status, the lookup, a rollup and two calculated columns.</summary>
    private static EntityMetadata Orders(DateTimeBehavior? behavior = null) => Table("xrmv_o",
        new DecimalAttributeMetadata { LogicalName = "xrmv_amount" },
        new IntegerAttributeMetadata { LogicalName = "xrmv_qty" },
        new MoneyAttributeMetadata { LogicalName = "xrmv_price" },
        new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime) { LogicalName = "xrmv_startedon", DateTimeBehavior = behavior ?? DateTimeBehavior.UserLocal },
        new PicklistAttributeMetadata { LogicalName = "xrmv_status" },
        new LookupAttributeMetadata { LogicalName = "xrmv_tid", Targets = ["xrmv_t"] },
        Rollup(new IntegerAttributeMetadata { LogicalName = "xrmv_lines" }),
        WithMask(new DecimalAttributeMetadata { LogicalName = "xrmv_basic" }, 1, 1),
        WithMask(new DecimalAttributeMetadata { LogicalName = "xrmv_fromparent" }, 1, 3),
        WithMask(new DecimalAttributeMetadata { LogicalName = "xrmv_fromrollup" }, 1, 17));

    private static void Validate(EntityMetadata table, AttributeMetadata a, FakeRollupColumns rollups, EntityMetadata? orders = null,
        AttributeMetadata? current = null, int onTable = 0, int inOrg = 0)
    {
        orders ??= Orders();
        ComputedColumnRules.ValidateDefinition(table, a, current, n => n == table.LogicalName ? table : n == orders.LogicalName ? orders : null,
            new FakeComputedColumns(), rollups, _ => null, onTable, inOrg);
    }

    private static FakeRollupColumns Sum(string column, string aggregated, RollupAggregate aggregate = RollupAggregate.Sum,
        ComputedCondition? filter = null, ComputedCondition? sourceFilter = null) =>
        new FakeRollupColumns().Related("xrmv_t", column, aggregate, "xrmv_o", "xrmv_tid", Relationship, aggregated, filter, sourceFilter);

    private static ComputedCondition NotNull(string column) => new ComputedCondition.Compare("NotNull", new ComputedExpr.Column(null, column), null);

    private static void ShouldFault(Action act, int code, string? message = null)
    {
        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(code);
        fault.HttpStatus.Should().Be(400);
        if (message is not null) fault.Message.Should().Be(message);
    }

    [Fact]
    public void Rollup_on_or_over_elastic_table_is_refused()
    {
        // Plan 2.12b review: an elastic table neither holds a rollup nor is aggregated by one (Learn: elastic tables have no rollup columns).
        var target = Table("xrmv_t");
        target.TableType = ElasticTables.TableTypeElastic;
        var a = Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" });
        ShouldFault(() => Validate(target, a, Sum("xrmv_r", "xrmv_amount")), ErrorCodes.InvalidArgument,
            "The rollup column 'xrmv_t.xrmv_r' cannot be defined on the elastic table 'xrmv_t': elastic tables have no rollup columns.");

        var orders = Orders();
        orders.TableType = ElasticTables.TableTypeElastic;
        ShouldFault(() => Validate(Table("xrmv_t"), a, Sum("xrmv_r", "xrmv_amount"), orders), ErrorCodes.InvalidArgument,
            "The rollup column 'xrmv_t.xrmv_r' cannot aggregate rows of the elastic table 'xrmv_o': a rollup reads standard tables only.");
    }

    [Fact]
    public void String_rollup_is_refused()
    {
        var a = Rollup(new StringAttributeMetadata { LogicalName = "xrmv_r" });
        ShouldFault(() => Validate(Table("xrmv_t"), a, new FakeRollupColumns()), unchecked((int)0x8006053e),
            "A rollup column cannot be of type String: 'xrmv_t.xrmv_r'.");
        ErrorCodes.RollupFieldsDataTypeNotValid.Should().Be(unchecked((int)0x8006053e));
    }

    [Fact]
    public void Count_into_decimal_is_refused()
    {
        var a = Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" });
        ShouldFault(() => Validate(Table("xrmv_t"), a, Sum("xrmv_r", "xrmv_amount", RollupAggregate.Count)), unchecked((int)0x80060545));
        // A date rollup takes only MIN and MAX.
        var date = Rollup(new DateTimeAttributeMetadata { LogicalName = "xrmv_d" });
        ShouldFault(() => Validate(Table("xrmv_t"), date, Sum("xrmv_d", "xrmv_startedon", RollupAggregate.Avg)), unchecked((int)0x80060545));
        // COUNT of any column into a whole number is fine.
        Validate(Table("xrmv_t"), Rollup(new IntegerAttributeMetadata { LogicalName = "xrmv_n" }), Sum("xrmv_n", "xrmv_status", RollupAggregate.Count));
    }

    [Fact]
    public void Sum_of_datetime_is_refused()
    {
        var a = Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" });
        ShouldFault(() => Validate(Table("xrmv_t"), a, Sum("xrmv_r", "xrmv_startedon")), unchecked((int)0x8006053a));
        ShouldFault(() => Validate(Table("xrmv_t"), a, Sum("xrmv_r", "xrmv_status", RollupAggregate.Max)), unchecked((int)0x8006053a));
    }

    [Fact]
    public void Money_rollup_of_decimal_is_refused()
    {
        var a = Rollup(new MoneyAttributeMetadata { LogicalName = "xrmv_r" });
        ShouldFault(() => Validate(Table("xrmv_t"), a, Sum("xrmv_r", "xrmv_amount")), unchecked((int)0x8006053d));
        var whole = Rollup(new IntegerAttributeMetadata { LogicalName = "xrmv_w" });
        ShouldFault(() => Validate(Table("xrmv_t"), whole, Sum("xrmv_w", "xrmv_amount")), unchecked((int)0x8006053d));
        // A decimal rollup takes decimals and whole numbers, a currency one currencies.
        Validate(Table("xrmv_t"), Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_d" }), Sum("xrmv_d", "xrmv_qty", RollupAggregate.Avg));
        Validate(Table("xrmv_t"), Rollup(new MoneyAttributeMetadata { LogicalName = "xrmv_m" }), Sum("xrmv_m", "xrmv_price"));
    }

    [Fact]
    public void Max_of_dates_with_other_behavior_is_refused()
    {
        var a = Rollup(new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime) { LogicalName = "xrmv_last", DateTimeBehavior = DateTimeBehavior.UserLocal });
        ShouldFault(() => Validate(Table("xrmv_t"), a, Sum("xrmv_last", "xrmv_startedon", RollupAggregate.Max), Orders(DateTimeBehavior.TimeZoneIndependent)),
            unchecked((int)0x80060544));
        var dateOnly = Rollup(new DateTimeAttributeMetadata(DateTimeFormat.DateOnly) { LogicalName = "xrmv_first" });
        ShouldFault(() => Validate(Table("xrmv_t"), dateOnly, Sum("xrmv_first", "xrmv_startedon", RollupAggregate.Min)), unchecked((int)0x80060544));
        // Same behavior and format (a column without a behavior is UserLocal).
        Validate(Table("xrmv_t"), Rollup(new DateTimeAttributeMetadata { LogicalName = "xrmv_ok" }), Sum("xrmv_ok", "xrmv_startedon", RollupAggregate.Max));
    }

    [Fact]
    public void Aggregating_a_rollup_or_complex_calculated_is_refused()
    {
        var code = unchecked((int)0x8006053c);
        ShouldFault(() => Validate(Table("xrmv_t"), Rollup(new IntegerAttributeMetadata { LogicalName = "xrmv_r" }), Sum("xrmv_r", "xrmv_lines")), code);
        ShouldFault(() => Validate(Table("xrmv_t"), Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" }), Sum("xrmv_r", "xrmv_fromparent")), code);
        ShouldFault(() => Validate(Table("xrmv_t"), Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" }), Sum("xrmv_r", "xrmv_fromrollup")), code);
        // The base-currency value of a money column is the server's: a currency rollup aggregates the money column instead.
        var orders = Orders();
        var priceBase = new MoneyAttributeMetadata { LogicalName = "xrmv_price_base" };
        priceBase.GetType().GetProperty(nameof(MoneyAttributeMetadata.IsBaseCurrency))!.GetSetMethod(nonPublic: true)!.Invoke(priceBase, [true]);
        orders.SetAttributes([.. orders.Attributes, priceBase]);
        ShouldFault(() => Validate(Table("xrmv_t"), Rollup(new MoneyAttributeMetadata { LogicalName = "xrmv_m" }), Sum("xrmv_m", "xrmv_price_base"), orders),
            code, "The rollup column 'xrmv_t.xrmv_m' cannot aggregate 'xrmv_o.xrmv_price_base', the base-currency value of 'xrmv_price': aggregate the " +
                  "money column itself.");
        // A basic calculated column (mask 1) can be aggregated.
        Validate(Table("xrmv_t"), Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" }), Sum("xrmv_r", "xrmv_basic"));
    }

    [Fact]
    public void Filter_on_rollup_is_refused()
    {
        var table = Table("xrmv_t", Rollup(new IntegerAttributeMetadata { LogicalName = "xrmv_other" }), new IntegerAttributeMetadata { LogicalName = "xrmv_plain" });
        var a = Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" });
        ShouldFault(() => Validate(table, a, Sum("xrmv_r", "xrmv_amount", sourceFilter: NotNull("xrmv_other"))), unchecked((int)0x80060548));
        ShouldFault(() => Validate(table, a, Sum("xrmv_r", "xrmv_amount", filter: NotNull("xrmv_fromrollup"))), unchecked((int)0x80060549));
        ShouldFault(() => Validate(table, a, Sum("xrmv_r", "xrmv_amount", filter: NotNull("xrmv_lines"))), unchecked((int)0x80060549));
        // Simple and basic calculated columns filter.
        Validate(table, a, Sum("xrmv_r", "xrmv_amount", filter: new ComputedCondition.And(NotNull("xrmv_basic"), NotNull("xrmv_status")),
            sourceFilter: NotNull("xrmv_plain")));
    }

    [Fact]
    public void Fifty_first_rollup_on_table_is_refused()
    {
        var a = Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" });
        ShouldFault(() => Validate(Table("xrmv_t"), a, Sum("xrmv_r", "xrmv_amount"), onTable: 50), unchecked((int)0x80060543));
        ShouldFault(() => Validate(Table("xrmv_t"), a, Sum("xrmv_r", "xrmv_amount"), onTable: 49, inOrg: 200), unchecked((int)0x80060542));
        Validate(Table("xrmv_t"), a, Sum("xrmv_r", "xrmv_amount"), onTable: 49, inOrg: 199);
        // An update of an existing rollup is not a new one.
        var current = Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" });
        Validate(Table("xrmv_t", current), Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" }), Sum("xrmv_r", "xrmv_amount"),
            current: current, onTable: 50, inOrg: 200);
    }

    [Fact]
    public void Rollup_is_forced_read_only()
    {
        var a = Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r", IsValidForCreate = true, IsValidForUpdate = true, MinValue = 0, MaxValue = 1000 });
        a.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired);
        Validate(Table("xrmv_t"), a, Sum("xrmv_r", "xrmv_amount"));
        a.IsValidForCreate.Should().BeFalse();
        a.IsValidForUpdate.Should().BeFalse();
        a.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.None);
        // MinValue and MaxValue are kept: they bound the value (ruling R7).
        a.MinValue.Should().Be(0);
        a.MaxValue.Should().Be(1000);
    }

    [Fact]
    public void Without_a_translator_rollup_is_refused()
    {
        var a = Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" });
        var act = () => ComputedColumnRules.ValidateDefinition(Table("xrmv_t"), a, null, _ => null, new FakeComputedColumns());
        act.Should().Throw<Xrmd.Abstractions.XrmdValidationException>().WithMessage("Rollup columns are not available on this server.");
    }

    [Fact]
    public void Companions_are_date_and_state_with_AttributeOf()
    {
        var rollup = Rollup(new DecimalAttributeMetadata("xrmv_OrderTotal")
        {
            LogicalName = "xrmv_ordertotal", MetadataId = Guid.NewGuid(), DisplayName = SystemAttributes.CreateLabel("Order total", 1033),
        });
        var companions = RollupColumns.Companions(rollup);

        companions.Select(c => c.LogicalName).Should().Equal("xrmv_ordertotal_date", "xrmv_ordertotal_state");
        companions.Select(c => c.SchemaName).Should().Equal("xrmv_OrderTotal_Date", "xrmv_OrderTotal_State");
        var date = companions[0].Should().BeOfType<DateTimeAttributeMetadata>().Which;
        date.DateTimeBehavior.Should().Be(DateTimeBehavior.UserLocal);
        date.Format.Should().Be(DateTimeFormat.DateAndTime);
        companions[1].Should().BeOfType<IntegerAttributeMetadata>();
        foreach (var c in companions)
        {
            c.AttributeOf.Should().Be("xrmv_ordertotal");
            c.LinkedAttributeId.Should().Be(rollup.MetadataId);
            c.IsValidForCreate.Should().BeFalse();
            c.IsValidForUpdate.Should().BeFalse();
            RollupColumns.IsCompanion(c).Should().BeTrue();
            RollupColumns.IsRollup(c).Should().BeFalse();
            // Each companion has its own column (unlike other AttributeOf attributes).
            TypeMapping.ToColumns(c).Should().ContainSingle().Which.Name.Should().Be(c.LogicalName);
        }
        TypeMapping.ToColumns(rollup).Should().ContainSingle().Which.Name.Should().Be("xrmv_ordertotal");
        RollupColumns.IsRollup(rollup).Should().BeTrue();
    }

    [Fact]
    public void Calculated_reading_rollup_gets_mask_16()
    {
        var table = Table("xrmv_t", Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" }), new DecimalAttributeMetadata { LogicalName = "xrmv_plain" });
        var definition = new ComputedDefinition(new ComputedExpr.Column(null, "xrmv_r"),
            [new ComputedExpr.Column(null, "xrmv_r"), new ComputedExpr.Column(null, "xrmv_plain")], 0);
        ComputedColumnRules.SourceTypeMask(table, definition, _ => null).Should().Be(17);
    }

    [Fact]
    public void Rollup_mask_is_0()
    {
        var a = Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" });
        Validate(Table("xrmv_t"), a, Sum("xrmv_r", "xrmv_amount"));
        ComputedColumnRules.MaskOf(a).Should().Be(0);
        var read = Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" });
        ComputedColumnRules.ApplyMask(Table("xrmv_t"), read, _ => null, new FakeComputedColumns());
        ComputedColumnRules.MaskOf(read).Should().Be(0);
    }

    [Fact]
    public void Dependents_name_the_rollups_reading_a_column_a_lookup_or_a_relationship()
    {
        var table = Table("xrmv_t", Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_r" }));
        EntityMetadata[] all = [table, Table("xrmv_o", Orders().Attributes.Where(x => x.LogicalName != "xrmv_lines").ToArray())];
        var rollups = Sum("xrmv_r", "xrmv_amount", filter: NotNull("xrmv_status"));

        foreach (var column in new[] { "xrmv_amount", "xrmv_tid", "xrmv_status" })
            RollupColumnRules.Dependents("xrmv_o", column, all, rollups, _ => null).Should().Equal("xrmv_t.xrmv_r");
        RollupColumnRules.Dependents("xrmv_o", "xrmv_qty", all, rollups, _ => null).Should().BeEmpty();
        RollupColumnRules.DependentsOfRelationship(Relationship.ToUpperInvariant(), all, rollups, _ => null).Should().Equal("xrmv_t.xrmv_r");
        RollupColumnRules.DependentsOfTable("xrmv_o", all, rollups, _ => null).Should().Equal("xrmv_t.xrmv_r");
        RollupColumnRules.DependentsOfTable("xrmv_t", all, rollups, _ => null).Should().BeEmpty("a table's own rollups go with it");
    }
}
