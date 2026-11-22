using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Computed;
using Xrmd.Tests.Shared;
using Xrmd.Workflows.Calculated;

namespace Xrmd.Metadata.Tests;

/// <summary>
/// The metadata rules of formula columns (plan 2.12c, Task 2), run against the real Power Fx translator (<see cref="CalculatedColumns"/>),
/// which owns the type and formula checks the rules rely on.
/// </summary>
public class FormulaColumnRulesTests
{
    private const string T = "xrmv_t", P = "xrmv_p";

    private static T1 Formula<T1>(T1 attribute, string formula) where T1 : AttributeMetadata
    {
        attribute.SourceType = ComputedColumnRules.Formula;
        ComputedColumnRules.SetFormula(attribute, formula);
        return attribute;
    }

    private static T1 Calculated<T1>(T1 attribute) where T1 : AttributeMetadata
    {
        attribute.SourceType = ComputedColumnRules.Calculated;
        ComputedColumnRules.SetFormula(attribute, "<xaml/>");
        attribute.GetType().GetProperty("SourceTypeMask")!.GetSetMethod(nonPublic: true)!.Invoke(attribute, [1]);
        return attribute;
    }

    private static EntityMetadata Table(string name, params AttributeMetadata[] attributes)
    {
        var entity = new EntityMetadata { LogicalName = name, OwnershipType = OwnershipTypes.UserOwned };
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryNameAttribute))!.GetSetMethod(nonPublic: true)!.Invoke(entity, ["xrmv_name"]);
        entity.SetAttributes(attributes);
        return entity;
    }

    /// <summary>The parent table <c>xrmv_p</c>: a whole number.</summary>
    private static EntityMetadata Parent() => Table(P, new IntegerAttributeMetadata { LogicalName = "xrmv_pn" });

    /// <summary>The table <c>xrmv_t</c>: the simple columns the formulas read, a lookup to <c>xrmv_p</c>, then <paramref name="extra"/>.</summary>
    private static EntityMetadata Orders(params AttributeMetadata[] extra) => Table(T,
    [
        new StringAttributeMetadata { LogicalName = "xrmv_name", MaxLength = 100 },
        new StringAttributeMetadata { LogicalName = "xrmv_code", MaxLength = 100 },
        new IntegerAttributeMetadata { LogicalName = "xrmv_qty" },
        new DecimalAttributeMetadata { LogicalName = "xrmv_amount", Precision = 2 },
        new DateTimeAttributeMetadata { LogicalName = "xrmv_duedate", DateTimeBehavior = DateTimeBehavior.DateOnly },
        new DateTimeAttributeMetadata { LogicalName = "xrmv_startedon", DateTimeBehavior = DateTimeBehavior.UserLocal },
        new DateTimeAttributeMetadata { LogicalName = "xrmv_shippedon", DateTimeBehavior = DateTimeBehavior.TimeZoneIndependent },
        new LookupAttributeMetadata { LogicalName = "xrmv_parentid", Targets = [P] },
        .. extra,
    ]);

    /// <summary>Adds <paramref name="a"/> to <paramref name="table"/> and validates it, as the store does for a new column.</summary>
    private static void Validate(EntityMetadata table, AttributeMetadata a, AttributeMetadata? current = null, EntityMetadata? parent = null,
        IRollupColumns? rollups = null)
    {
        parent ??= Parent();
        if (current is null && table.Attributes.All(x => x.LogicalName != a.LogicalName))
            table.SetAttributes([.. table.Attributes, a]);
        ComputedColumnRules.ValidateDefinition(table, a, current, Find(table, parent), new CalculatedColumns(Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance), rollups ?? new FakeRollupColumns(),
            _ => null);
    }

    private static Func<string, EntityMetadata?> Find(EntityMetadata table, EntityMetadata parent) =>
        n => n == table.LogicalName ? table : n == parent.LogicalName ? parent : null;

    private static void Refused(Action act, string message) =>
        act.Should().Throw<XrmdValidationException>().WithMessage(message);

    [Theory]
    [InlineData("string", "xrmv_code & \"-x\"")]
    [InlineData("integer", "xrmv_qty * 2")]
    [InlineData("decimal", "xrmv_amount * xrmv_qty")]
    [InlineData("double", "Float(xrmv_qty)")]
    [InlineData("boolean", "IsBlank(xrmv_duedate)")]
    [InlineData("datetime", "DateAdd(xrmv_startedon, 3, TimeUnit.Days)")]
    public void Allowed_types(string kind, string formula)
    {
        AttributeMetadata a = kind switch
        {
            "string" => new StringAttributeMetadata { LogicalName = "xrmv_c", MaxLength = 100 },
            "integer" => new IntegerAttributeMetadata { LogicalName = "xrmv_c" },
            "decimal" => new DecimalAttributeMetadata { LogicalName = "xrmv_c" },
            "double" => new DoubleAttributeMetadata { LogicalName = "xrmv_c" },
            "boolean" => new BooleanAttributeMetadata { LogicalName = "xrmv_c" },
            _ => new DateTimeAttributeMetadata { LogicalName = "xrmv_c", DateTimeBehavior = DateTimeBehavior.UserLocal },
        };
        Validate(Orders(), Formula(a, formula));
        a.SourceType.Should().Be(3);
    }

    /// <summary>A translator whose definition-time check refuses every formula (final review C1).</summary>
    private sealed class RefusingCheck : IComputedColumns
    {
        private readonly CalculatedColumns _inner = new(Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance);

        public ComputedDefinition Translate(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities) =>
            _inner.Translate(entity, attribute, entities);

        public ComputedDefinition Check(EntityMetadata entity, AttributeMetadata attribute, Func<string, EntityMetadata?> entities) =>
            throw new XrmdValidationException("too complex");
    }

    [Fact]
    public void The_definition_check_runs_when_a_formula_column_is_defined()
    {
        // Final review C1: the SQL of a formula column is compiled when it is defined (CalculatedColumns.Check), so that a formula
        // whose SQL is too large is refused then, not when a row is read.
        var table = Orders();
        var a = Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_c" }, "xrmv_amount * 2");
        table.SetAttributes([.. table.Attributes, a]);
        Refused(() => ComputedColumnRules.ValidateDefinition(table, a, null, Find(table, Parent()), new RefusingCheck(), new FakeRollupColumns(),
            _ => null), "too complex");

        // The real check accepts an ordinary formula.
        Validate(Orders(), Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_c" }, "xrmv_amount * 2"));
    }

    [Fact]
    public void Money_and_choice_are_refused()
    {
        Refused(() => Validate(Orders(), Formula(new MoneyAttributeMetadata { LogicalName = "xrmv_c" }, "Decimal(xrmv_amount)")),
            "A formula column cannot be of type Money: 'xrmv_t.xrmv_c'.");
        Refused(() => Validate(Orders(), Formula(new PicklistAttributeMetadata { LogicalName = "xrmv_c" }, "1")),
            "Choice formula columns are not supported yet: 'xrmv_t.xrmv_c'.");
        Refused(() => Validate(Orders(), Formula(new MultiSelectPicklistAttributeMetadata { LogicalName = "xrmv_c" }, "\"a\"")),
            "A formula column cannot be of type Virtual: 'xrmv_t.xrmv_c'.");
    }

    [Fact]
    public void Prompt_columns_keep_their_refusal()
    {
        var a = new StringAttributeMetadata { LogicalName = "xrmv_c", SourceType = ComputedColumnRules.Prompt };
        Refused(() => Validate(Orders(), a), "Prompt columns are not supported: 'xrmv_t.xrmv_c'.");
    }

    [Fact]
    public void Formula_is_forced_read_only()
    {
        var a = Formula(new IntegerAttributeMetadata
        {
            LogicalName = "xrmv_c", IsValidForCreate = true, IsValidForUpdate = true,
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired),
        }, " xrmv_qty * 2 ");
        Validate(Orders(), a);

        a.IsValidForCreate.Should().BeFalse();
        a.IsValidForUpdate.Should().BeFalse();
        a.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.None);
        ComputedColumnRules.FormulaOf(a).Should().Be("xrmv_qty * 2", "the formula is stored trimmed (F2)");
    }

    [Theory]
    [InlineData("=xrmv_qty * 2")]
    [InlineData("  =xrmv_qty")]
    [InlineData("=")]
    public void Leading_equals_is_refused(string formula)
    {
        // Q1 (Dataverse, 2026-11-19, finding 8): the Web API refuses a FormulaDefinition that starts with '='; only the formula file
        // of a solution writes it (FormulaFiles strips it before the column is defined).
        var a = Formula(new IntegerAttributeMetadata { LogicalName = "xrmv_c" }, formula);
        Refused(() => Validate(Orders(), a),
            "The formula column 'xrmv_t.xrmv_c' has a formula that starts with '=': give the formula without it " +
            "(only a solution's formula file writes it with '=').");
    }

    [Fact]
    public void Primary_name_column_cannot_be_a_formula()
    {
        var table = Orders();
        var name = table.Attributes.Single(x => x.LogicalName == "xrmv_name");
        Formula(name, "xrmv_code & \"-\"");
        Refused(() => Validate(table, name), "The primary name column of a table cannot be a formula column: 'xrmv_t.xrmv_name'.");
    }

    [Theory]
    [InlineData("integer")]
    [InlineData("decimal")]
    [InlineData("double")]
    public void MinMax_is_refused(string kind)
    {
        AttributeMetadata a = kind switch
        {
            "integer" => new IntegerAttributeMetadata { LogicalName = "xrmv_c", MinValue = 0 },
            "decimal" => new DecimalAttributeMetadata { LogicalName = "xrmv_c", MaxValue = 10 },
            _ => new DoubleAttributeMetadata { LogicalName = "xrmv_c", MinValue = 0 },
        };
        Refused(() => Validate(Orders(), Formula(a, kind == "double" ? "Float(xrmv_qty)" : "xrmv_qty * 2")),
            "MinValue and MaxValue cannot be set on a formula column: 'xrmv_t.xrmv_c'.");

        // The widest range of the type is no limit (an export may write it).
        AttributeMetadata widest = kind switch
        {
            "integer" => new IntegerAttributeMetadata { LogicalName = "xrmv_w", MinValue = int.MinValue, MaxValue = int.MaxValue },
            "decimal" => new DecimalAttributeMetadata { LogicalName = "xrmv_w", MinValue = -100000000000m, MaxValue = 100000000000m },
            _ => new DoubleAttributeMetadata { LogicalName = "xrmv_w", MinValue = -100000000000d, MaxValue = 100000000000d },
        };
        Validate(Orders(), Formula(widest, kind == "double" ? "Float(xrmv_qty)" : "xrmv_qty * 2"));
    }

    [Fact]
    public void Decimal_precision_0_to_10_default_2()
    {
        var unset = Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_c" }, "xrmv_amount * xrmv_qty");
        Validate(Orders(), unset);
        unset.Precision.Should().Be(2);

        foreach (var precision in new[] { 0, 10 })
        {
            var a = Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_c", Precision = precision }, "xrmv_amount * xrmv_qty");
            Validate(Orders(), a);
            a.Precision.Should().Be(precision);
        }
        foreach (var precision in new[] { -1, 11 })
            Refused(() => Validate(Orders(), Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_c", Precision = precision }, "xrmv_amount")),
                $"The precision of the formula column 'xrmv_t.xrmv_c' is {precision}: a formula column takes 0 to 10.");
    }

    [Fact]
    public void Formats_are_restricted()
    {
        Refused(() => Validate(Orders(), Formula(new StringAttributeMetadata { LogicalName = "xrmv_c", Format = StringFormat.Email }, "xrmv_code")),
            "A formula column of type String takes the format Text only: 'xrmv_t.xrmv_c'.");
        Refused(() => Validate(Orders(), Formula(new IntegerAttributeMetadata { LogicalName = "xrmv_c", Format = IntegerFormat.Duration }, "xrmv_qty")),
            "A formula column of type Integer takes the format None only: 'xrmv_t.xrmv_c'.");
        Validate(Orders(), Formula(new StringAttributeMetadata { LogicalName = "xrmv_s", Format = StringFormat.Text }, "xrmv_code"));
        Validate(Orders(), Formula(new IntegerAttributeMetadata { LogicalName = "xrmv_i", Format = IntegerFormat.None }, "xrmv_qty"));
    }

    [Fact]
    public void Type_change_on_update_is_refused()
    {
        var table = Orders();
        var current = Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_c", Precision = 2 }, "xrmv_amount * xrmv_qty");
        Validate(table, current);

        // Review Focus 4 (Learn): a new formula must keep the column's type.
        var update = Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_c", Precision = 2 }, "xrmv_code & \"x\"");
        Refused(() => Validate(table, update, current), "The formula of 'xrmv_t.xrmv_c' gives Text, but the column is Decimal.");

        // A formula of the same type is accepted, and the source type cannot change.
        Validate(table, Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_c", Precision = 2 }, "xrmv_amount + 1"), current);
        Refused(() => Validate(table, Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_c" }), current),
            "The SourceType of a column cannot change: 'xrmv_t.xrmv_c'.");
    }

    [Theory]
    [InlineData("DateAdd(xrmv_startedon, 1, TimeUnit.Days)", "UserLocal", null)]
    [InlineData("Now()", "UserLocal", null)]
    [InlineData("DateAdd(xrmv_startedon, 1, TimeUnit.Days)", "DateOnly", "UserLocal")]
    [InlineData("DateAdd(xrmv_startedon, 1, TimeUnit.Days)", "TimeZoneIndependent", "UserLocal")]
    [InlineData("xrmv_duedate", "DateOnly", null)]
    [InlineData("UTCToday()", "DateOnly", null)]
    [InlineData("UTCToday()", "TimeZoneIndependent", null)]
    [InlineData("UTCToday()", "UserLocal", "DateOnly")]
    [InlineData("xrmv_shippedon", "TimeZoneIndependent", null)]
    [InlineData("UTCNow()", "TimeZoneIndependent", null)]
    [InlineData("UTCNow()", "DateOnly", "TimeZoneIndependent")]
    [InlineData("UTCNow()", "UserLocal", "TimeZoneIndependent")]
    public void Date_result_behaviour_must_fit_the_column(string formula, string column, string? refusedResult)
    {
        var a = Formula(new DateTimeAttributeMetadata
        {
            LogicalName = "xrmv_c", DateTimeBehavior = new DateTimeBehavior { Value = column },
            Format = column == "DateOnly" ? DateTimeFormat.DateOnly : DateTimeFormat.DateAndTime,
        }, formula);
        var act = () => Validate(Orders(), a);
        if (refusedResult is null) act();
        else Refused(act, $"The formula of 'xrmv_t.xrmv_c' gives a {refusedResult} date, but the column is {column}.");
    }

    [Fact]
    public void SourceTypeMask()
    {
        var own = Formula(new IntegerAttributeMetadata { LogicalName = "xrmv_own" }, "xrmv_qty * 2");
        var related = Formula(new IntegerAttributeMetadata { LogicalName = "xrmv_rel" }, "xrmv_parentid.xrmv_pn + 1");
        var table = Orders();
        Validate(table, own);
        Validate(table, related);
        ComputedColumnRules.MaskOf(own).Should().Be(1);
        ComputedColumnRules.MaskOf(related).Should().Be(2);

        // Reading a formula column, or a calculated one, sets bit 8 (K12).
        var onFormula = Formula(new IntegerAttributeMetadata { LogicalName = "xrmv_onfx" }, "xrmv_own + xrmv_qty");
        Validate(table, onFormula);
        ComputedColumnRules.MaskOf(onFormula).Should().Be(9);

        // A formula reading a calculated column gets the same bit (ComputedColumnRules.SourceTypeMask, shared): the integration test
        // FormulaMetadataTests.RetrieveAttribute_returns_formula_and_mask reads one, with a real XAML definition.
    }

    [Theory]
    [InlineData("xrmv_qty * 2", null)]
    [InlineData("xrmv_parentid.xrmv_pn + 1", "related")]
    [InlineData("DateDiff(xrmv_startedon, Now(), TimeUnit.Days)", "Now")]
    [InlineData("Year(UTCNow())", "UTCNow")]
    [InlineData("DateDiff(xrmv_duedate, UTCToday(), TimeUnit.Days)", "UTCToday")]
    [InlineData("If(IsUTCToday(xrmv_duedate), 1, 0)", "IsUTCToday")]
    public void Rollup_reads_simple_formula_only(string formula, string? refusal)
    {
        var orders = Orders();
        var fx = Formula(new IntegerAttributeMetadata { LogicalName = "xrmv_fx" }, formula);
        var parent = Parent();
        Validate(orders, fx, parent: parent);

        var rollup = new IntegerAttributeMetadata { LogicalName = "xrmv_sum", SourceType = RollupColumns.Rollup };
        ComputedColumnRules.SetFormula(rollup, "<xaml/>");
        parent.SetAttributes([.. parent.Attributes, rollup]);
        var rollups = new FakeRollupColumns().Related(P, "xrmv_sum", RollupAggregate.Sum, T, "xrmv_parentid", "xrmv_t_xrmv_parentid", "xrmv_fx");
        var act = () => ComputedColumnRules.ValidateDefinition(parent, rollup, null, Find(orders, parent), new CalculatedColumns(Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance), rollups,
            _ => null);
        if (refusal is null)
        {
            act();
            return;
        }
        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.ErrorCode.Should().Be(ErrorCodes.RollupFieldsAggregateOnRollupFieldOrComplexCalcFieldNotAllowed);
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("'xrmv_t.xrmv_fx'");
    }
}
