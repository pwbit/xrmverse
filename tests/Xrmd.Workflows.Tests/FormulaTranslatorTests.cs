using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Workflows.Calculated;
using Xrmd.Workflows.Formula;
using static Xrmd.Metadata.Computed.ComputedExpr;

namespace Xrmd.Workflows.Tests;

/// <summary>
/// The translation of Power Fx formula columns (<c>SourceType</c> 3) into <see cref="ComputedExpr"/> (plan 2.12c, Task 1): the three
/// formulas of the 1.0.0.4 parity export, the refusals of rulings F4 to F7 and the chain limit of F10. The metadata is built here;
/// the metadata rules (ComputedColumnRules) are Task 2.
/// </summary>
public class FormulaTranslatorTests
{
    private const string Order = "xrmv_calcorder";

    private static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property)!.GetSetMethod(nonPublic: true)!.Invoke(target, [value]);

    private static T Formula<T>(T attribute, string formula) where T : AttributeMetadata
    {
        attribute.SourceType = 3;
        ComputedColumnRules.SetFormula(attribute, formula);
        return attribute;
    }

    private static EntityMetadata Table(string name, params AttributeMetadata[] attributes)
    {
        var entity = new EntityMetadata { LogicalName = name };
        entity.SetAttributes(attributes);
        return entity;
    }

    /// <summary>The simple columns of <c>xrmv_calcorder</c> the formulas read (the 1.0.0.4 export, plus a currency and a lookup).</summary>
    private static List<AttributeMetadata> OrderColumns()
    {
        var priceBase = new MoneyAttributeMetadata { LogicalName = "xrmv_price_base" };
        Set(priceBase, "IsBaseCurrency", true);
        return
        [
            new StringAttributeMetadata { LogicalName = "xrmv_code", MaxLength = 100, DisplayName = new Label("Order Code", 1033) },
            new IntegerAttributeMetadata { LogicalName = "xrmv_qty" },
            new DateTimeAttributeMetadata { LogicalName = "xrmv_duedate", DateTimeBehavior = DateTimeBehavior.DateOnly },
            new DateTimeAttributeMetadata { LogicalName = "xrmv_startedon", DateTimeBehavior = DateTimeBehavior.UserLocal },
            new MoneyAttributeMetadata { LogicalName = "xrmv_price" },
            priceBase,
            new LookupAttributeMetadata { LogicalName = "xrmv_accountid", Targets = ["account"] },
            new BooleanAttributeMetadata { LogicalName = "xrmv_flag" },
        ];
    }

    private sealed class Model
    {
        public Dictionary<string, EntityMetadata> Tables { get; } = new(StringComparer.Ordinal);

        public Model(params AttributeMetadata[] extra)
        {
            Tables[Order] = Table(Order, [.. OrderColumns(), .. extra]);
            Tables["account"] = Table("account", new StringAttributeMetadata { LogicalName = "name", MaxLength = 160 },
                new LookupAttributeMetadata { LogicalName = "parentaccountid", Targets = ["account"] });
        }

        public EntityMetadata? Find(string name) => Tables.GetValueOrDefault(name);

        public ComputedDefinition Translate(string column) =>
            new CalculatedColumns(Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance).Translate(Tables[Order], Tables[Order].Attributes.Single(a => a.LogicalName == column), Find);
    }

    /// <summary>A formula column <c>xrmv_fx</c> of type <paramref name="column"/> with <paramref name="formula"/>, translated.</summary>
    private static ComputedDefinition Fx(AttributeMetadata column, string formula)
    {
        column.LogicalName = "xrmv_fx";
        return new Model(Formula(column, formula)).Translate("xrmv_fx");
    }

    private static StringAttributeMetadata Text() => new() { MaxLength = 200 };

    private static DecimalAttributeMetadata Number() => new() { Precision = 2 };

    private static Column Own(string attribute) => new(null, attribute);

    private static Function F(string name, params ComputedExpr[] args) => new(name, args);

    /// <summary>
    /// <paramref name="actual"/> is <paramref name="expected"/>: compared as text, since the records hold lists (compared by reference),
    /// with the type of each literal.
    /// </summary>
    private static void Same(object? actual, object? expected) => Show(actual).Should().Be(Show(expected));

    private static string Show(object? node) => node switch
    {
        null => "null",
        Literal l => l.Value is null ? "Literal(null)" : $"Literal({l.Value}:{l.Value.GetType().Name})",
        Column c => $"Column({c.Lookup},{c.Attribute})",
        Now => "Now",
        Binary b => $"{b.Operator}({Show(b.Left)}, {Show(b.Right)})",
        Function f => $"{f.Name}({string.Join(", ", f.Args.Select(Show))})",
        Conditional c => $"If({string.Join(", ", c.Branches.Select(b => $"{Show(b.When)} -> {Show(b.Then)}"))}; else {Show(c.Otherwise)})",
        ComputedCondition.Compare c => $"{c.Operator}({Show(c.Left)}, {Show(c.Right)})",
        ComputedCondition.And a => $"And({Show(a.Left)}, {Show(a.Right)})",
        ComputedCondition.Or o => $"Or({Show(o.Left)}, {Show(o.Right)})",
        ComputedCondition.Not n => $"Not({Show(n.Operand)})",
        _ => node.ToString()!,
    };

    private static Action Refused(AttributeMetadata column, string formula) => () => Fx(column, formula);

    // ---- parity --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Parity_formulas_translate()
    {
        var label = Formula(new StringAttributeMetadata { LogicalName = "xrmv_fxlabel", MaxLength = 100 }, "=Upper(xrmv_code) & \"-\" & Text(xrmv_qty, \"0\")");
        var nodue = Formula(new BooleanAttributeMetadata { LogicalName = "xrmv_fxnodue" }, "=IsBlank(xrmv_duedate)");
        var plus3 = Formula(new DateTimeAttributeMetadata { LogicalName = "xrmv_fxplus3", DateTimeBehavior = DateTimeBehavior.UserLocal },
            "=DateAdd(xrmv_startedon,3,TimeUnit.Days)");
        var model = new Model(label, nodue, plus3);

        var l = model.Translate("xrmv_fxlabel");
        Same(l.Body, F("fx.Concat",
            F("fx.Concat", F("fx.Upper", F("fx.Empty", Own("xrmv_code"))), new Literal("-")),
            F("fx.Text", F("fx.Zero", Own("xrmv_qty")), new Literal("0"))));
        l.Sources.Should().Equal(Own("xrmv_code"), Own("xrmv_qty"));
        l.SourceTypeMask.Should().Be(1);

        var n = model.Translate("xrmv_fxnodue");
        Same(n.Body, new Conditional([new ComputedBranch(new ComputedCondition.Compare("Null", Own("xrmv_duedate"), null), new Literal(true))],
            new Literal(false)));
        n.Sources.Should().Equal(Own("xrmv_duedate"));

        var p = model.Translate("xrmv_fxplus3");
        Same(p.Body, F("fx.DateAdd", Own("xrmv_startedon"), new Literal(3m), new Literal("Days")));
        p.Sources.Should().Equal(Own("xrmv_startedon"));
    }

    [Fact]
    public void Leading_equals_is_optional()
    {
        FormulaTranslator.Normalize("  =Upper(xrmv_code) ").Should().Be("Upper(xrmv_code)");
        FormulaTranslator.Normalize("Upper(xrmv_code)").Should().Be("Upper(xrmv_code)");
        Same(Fx(Text(), "Upper(xrmv_code)").Body, Fx(Text(), "=Upper(xrmv_code)").Body);
    }

    [Fact]
    public void Translator_can_be_called_directly()
    {
        var model = new Model(Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_fx", Precision = 2 }, "unused"));
        var entity = model.Tables[Order];
        var d = FormulaTranslator.Translate(entity, entity.Attributes.Single(a => a.LogicalName == "xrmv_fx"), "=xrmv_qty * 2 - 10%",
            model.Find, (_, _) => throw new InvalidOperationException("no chain"));

        Same(d.Body, new Binary("Subtract", new Binary("Multiply", F("fx.Zero", Own("xrmv_qty")), new Literal(2m)),
            new Binary("Divide", new Literal(10m), new Literal(100m))));
    }

    [Fact]
    public void Errors_stay_errors_in_further_operations()
    {
        // Q1 (Dataverse, 2026-11-19, finding 1): a blank operand is 0, but the null of a failed operation is an error, which stays an
        // error (null) through a further operation: amount / 0 + 1 is null. Only real blanks are wrapped in fx.Zero / fx.Empty.
        Same(Fx(Number(), "Mod(xrmv_qty, 0) + 1").Body,
            new Binary("Add", F("fx.Mod", F("fx.Zero", Own("xrmv_qty")), new Literal(0m)), new Literal(1m)));
        Same(Fx(new DoubleAttributeMetadata(), "Sqrt(-1) + 1").Body, new Binary("Add", F("fx.Sqrt", new Literal(-1m)), new Literal(1m)));
        Same(Fx(Number(), "xrmv_qty / 0 + 1").Body,
            new Binary("Add", new Binary("Divide", F("fx.Zero", Own("xrmv_qty")), new Literal(0m)), new Literal(1m)));
        Same(Fx(Number(), "(xrmv_qty * 1000000) * 1000000 + 1").Body,
            new Binary("Add", new Binary("Multiply", new Binary("Multiply", F("fx.Zero", Own("xrmv_qty")), new Literal(1000000m)),
                new Literal(1000000m)), new Literal(1m)));
        // A blank of Blank() or a column is still 0 / "".
        Same(Fx(Number(), "Blank() + xrmv_qty").Body, new Binary("Add", new Literal(0m), F("fx.Zero", Own("xrmv_qty"))));

        // A function whose SQL would not give null for an error argument is guarded: null when the argument is an error.
        var left = F("fx.Left", F("fx.Empty", Own("xrmv_code")), new Literal(-1m));
        Same(Fx(Text(), "Left(xrmv_code, -1) & \"x\"").Body, new Conditional(
            [new ComputedBranch(new ComputedCondition.Not(Cmp("Null", left)), F("fx.Concat", left, new Literal("x")))], null));
        var division = new Binary("Divide", F("fx.Zero", Own("xrmv_qty")), new Literal(0m));
        Same(Fx(Number(), "Max(xrmv_qty / 0, 1)").Body, new Conditional(
            [new ComputedBranch(new ComputedCondition.Not(Cmp("Null", division)), F("fx.Max", division, new Literal(1m)))], null));
        // Functions that never fail keep their result as it is.
        Same(Fx(Number(), "Abs(xrmv_qty) + Len(xrmv_code)").Body,
            new Binary("Add", F("fx.Abs", F("fx.Zero", Own("xrmv_qty"))), F("fx.Len", F("fx.Empty", Own("xrmv_code")))));
    }

    [Fact]
    public void An_error_condition_makes_the_result_an_error()
    {
        var division = new Binary("Divide", F("fx.Zero", Own("xrmv_qty")), new Literal(0m));
        // IsBlank of an error is the error (Power Fx: error in, error out; only IsError / IsBlankOrError / IfError take an error in).
        var isBlank = Fx(new BooleanAttributeMetadata(), "IsBlank(xrmv_qty / 0)").Body.Should().BeOfType<Conditional>().Subject;
        Same(isBlank.Branches[0].When, Cmp("Null", division));
        Same(isBlank.Branches[0].Then, new Literal(null));
        // If: an error condition gives null before the branches.
        var c = Fx(Number(), "If(xrmv_qty / 0 > 1, 1, 0)").Body.Should().BeOfType<Conditional>().Subject;
        c.Branches.Should().HaveCount(2);
        Same(c.Branches[0].When, Cmp("Null", division));
        Same(c.Branches[0].Then, new Literal(null));
        // And stops at false: the error of its second operand counts only when the first is true.
        var and = Fx(new BooleanAttributeMetadata(), "And(xrmv_qty > 1, xrmv_qty / 0 > 1)").Body.Should().BeOfType<Conditional>().Subject;
        Same(and.Branches[0].When, new ComputedCondition.And(Cmp("GreaterThan", F("fx.Zero", Own("xrmv_qty")), new Literal(1m)), Cmp("Null", division)));
    }

    [Fact]
    public void A_value_both_blank_and_error_is_zero_only_when_blank()
    {
        // If without else is blank when its condition is false, an error when its branch fails: + 1 reads only the blank as 0.
        var sum = Fx(Number(), "If(xrmv_qty > 0, 10 / xrmv_qty) + 1").Body.Should().BeOfType<Binary>().Subject;
        var operand = sum.Left.Should().BeOfType<Conditional>().Subject;
        operand.Branches.Should().HaveCount(1);
        Same(operand.Branches[0].Then, new Literal(0m));
        operand.Otherwise.Should().BeOfType<Conditional>();
        // A DateAdd of a blank date is blank, past year 9999 an error: Day of it is blank or an error accordingly.
        Fx(Number(), "Day(DateAdd(xrmv_duedate, 1, TimeUnit.Years)) + 1").Body.Should().BeOfType<Binary>()
            .Which.Left.Should().BeOfType<Conditional>();
    }

    // ---- functions and blank semantics ------------------------------------------------------------------------------

    [Fact]
    public void If_switch_and_conditions()
    {
        var d = Fx(Text(), "If(xrmv_qty > 10 && !IsBlank(xrmv_code), \"big\", xrmv_qty = Blank(), \"none\", \"small\")");
        var c = d.Body.Should().BeOfType<Conditional>().Subject;
        c.Branches.Should().HaveCount(2);
        Same(c.Branches[0].When, new ComputedCondition.And(
            new ComputedCondition.Compare("GreaterThan", F("fx.Zero", Own("xrmv_qty")), new Literal(10m)),
            new ComputedCondition.Not(new ComputedCondition.Or(
                new ComputedCondition.Compare("Null", Own("xrmv_code"), null),
                new ComputedCondition.Compare("Equal", Own("xrmv_code"), new Literal(""))))));
        Same(c.Branches[1].When, new ComputedCondition.Compare("Null", Own("xrmv_qty"), null));
        Same(c.Otherwise, new Literal("small"));

        var s = Fx(Text(), "Switch(xrmv_qty, 1, \"one\", 2, \"two\")").Body.Should().BeOfType<Conditional>().Subject;
        s.Branches.Select(b => b.When).Should().Equal(
            new ComputedCondition.Compare("Equal", Own("xrmv_qty"), new Literal(1m)),
            new ComputedCondition.Compare("Equal", Own("xrmv_qty"), new Literal(2m)));
        s.Otherwise.Should().BeNull();
    }

    [Fact]
    public void Operators_in_power_and_mod()
    {
        Same(Fx(new BooleanAttributeMetadata(), "\"AB\" in xrmv_code").Body, new Conditional(
            [new ComputedBranch(new ComputedCondition.Compare("Equal", F("fx.In", new Literal("AB"), F("fx.Empty", Own("xrmv_code"))), new Literal(true)),
                new Literal(true))], new Literal(false)));
        Same(Fx(new DoubleAttributeMetadata(), "xrmv_qty ^ 2").Body, F("fx.Power", F("fx.Zero", Own("xrmv_qty")), new Literal(2m)));
        Same(Fx(Number(), "Mod(xrmv_qty, 3)").Body, F("fx.Mod", F("fx.Zero", Own("xrmv_qty")), new Literal(3m)));
        Same(Fx(Number(), "DateDiff(xrmv_duedate, UTCToday(), TimeUnit.Months)").Body,
            F("fx.DateDiff", Own("xrmv_duedate"), F("fx.UTCToday"), new Literal("Months")));
    }

    /// <summary>The condition of a yes/no formula: the <c>When</c> of its single branch.</summary>
    private static ComputedCondition When(string formula) =>
        Fx(new BooleanAttributeMetadata(), formula).Body.Should().BeOfType<Conditional>().Subject.Branches.Single().When;

    private static ComputedCondition.Compare Cmp(string op, ComputedExpr left, ComputedExpr? right = null) => new(op, left, right);

    [Fact]
    public void Equality_with_a_blank_operand_is_two_valued()
    {
        // Power Fx: with a blank xrmv_qty, xrmv_qty <> 5 is true; SQL "NULL <> 5" is unknown, so the tree tests for null itself.
        Same(When("xrmv_qty <> 5"),
            new ComputedCondition.Not(new ComputedCondition.And(Cmp("NotNull", Own("xrmv_qty")), Cmp("Equal", Own("xrmv_qty"), new Literal(5m)))));
        Same(When("xrmv_qty = 5"), new ComputedCondition.And(Cmp("NotNull", Own("xrmv_qty")), Cmp("Equal", Own("xrmv_qty"), new Literal(5m))));
        Same(When("Not(xrmv_code = \"A\")"),
            new ComputedCondition.Not(new ComputedCondition.And(Cmp("NotNull", Own("xrmv_code")), Cmp("Equal", Own("xrmv_code"), new Literal("A")))));
        Same(When("xrmv_flag <> false"),
            new ComputedCondition.Not(new ComputedCondition.And(Cmp("NotNull", Own("xrmv_flag")), Cmp("Equal", Own("xrmv_flag"), new Literal(false)))));

        // Both sides can be blank: equal when both are blank, or both have a value and the values are equal.
        var name = new Column("xrmv_accountid", "name");
        Same(When("xrmv_code = xrmv_accountid.name"), new ComputedCondition.Or(
            new ComputedCondition.And(Cmp("Null", Own("xrmv_code")), Cmp("Null", name)),
            new ComputedCondition.And(Cmp("NotNull", Own("xrmv_code")),
                new ComputedCondition.And(Cmp("NotNull", name), Cmp("Equal", Own("xrmv_code"), name)))));

        // A relational operator on a blank date is false (also under Not).
        Same(When("xrmv_duedate > UTCToday()"),
            new ComputedCondition.And(Cmp("NotNull", Own("xrmv_duedate")), Cmp("GreaterThan", Own("xrmv_duedate"), F("fx.UTCToday"))));
    }

    [Fact]
    public void IsUTCToday_takes_any_date()
    {
        Same(When("IsUTCToday(xrmv_startedon)"), Cmp("Equal", F("fx.IsUTCToday", Own("xrmv_startedon")), new Literal(true)));
        Same(When("IsUTCToday(xrmv_duedate)"), Cmp("Equal", F("fx.IsUTCToday", Own("xrmv_duedate")), new Literal(true)));
        Refused(Number(), "Day(xrmv_startedon)").Should().Throw<XrmdValidationException>().WithMessage("*Day on a User local date*");
    }

    [Fact]
    public void Second_translate_does_not_parse_or_check_again()
    {
        static AttributeMetadata Column() => Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_fx", Precision = 2 }, "xrmv_qty + 1");
        var model = new Model(Column());
        var columns = new CalculatedColumns(Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance);
        var entity = model.Tables[Order];
        var fx = entity.Attributes.Single(a => a.LogicalName == "xrmv_fx");

        var first = columns.Translate(entity, fx, model.Find);
        var second = columns.Translate(entity, fx, model.Find);
        Same(second.Body, first.Body);
        columns.Formulas.ParseCount.Should().Be(1);
        columns.Formulas.CheckCount.Should().Be(1);

        // The same text reading a column whose type changed (Whole number to Float) is checked again; the parse is kept. The
        // translation runs on the new metadata: the formula now gives Float.
        var changed = new Model(Column());
        var table = changed.Tables[Order];
        table.SetAttributes([.. table.Attributes.Where(a => a.LogicalName != "xrmv_qty"), new DoubleAttributeMetadata { LogicalName = "xrmv_qty" }]);
        var act = () => columns.Translate(table, table.Attributes.Single(a => a.LogicalName == "xrmv_fx"), changed.Find);
        act.Should().Throw<XrmdValidationException>().WithMessage("*gives Float, but the column is Decimal*");
        columns.Formulas.ParseCount.Should().Be(1);
        columns.Formulas.CheckCount.Should().Be(2);
    }

    // ---- refusals ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Unknown_function_is_refused()
    {
        Refused(Text(), "Proper(xrmv_code)").Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_fx' uses the function 'Proper', which formula columns do not support.");
        Refused(Text(), "Foo(xrmv_code)").Should().Throw<XrmdValidationException>().WithMessage("*uses the function 'Foo'*");
    }

    [Fact]
    public void IfError_is_refused()
    {
        Refused(Number(), "IfError(xrmv_qty / 0, 1)").Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_fx' uses the function 'IfError', which formula columns do not support.");
        Refused(new BooleanAttributeMetadata(), "IsError(xrmv_qty)").Should().Throw<XrmdValidationException>().WithMessage("*'IsError'*");
        Refused(Number(), "Weekday(xrmv_duedate, StartOfWeek.Monday)").Should().Throw<XrmdValidationException>().WithMessage("*StartOfWeek*");
    }

    [Fact]
    public void Substitute_instance_is_a_whole_constant_from_1_to_100()
    {
        // ComputedSql finds the instance-th match by one CHARINDEX per match: a constant, at most 100 (plan 2.12c, Task 3 fix).
        Fx(Text(), "Substitute(xrmv_code, \"a\", \"b\", 2)").Body.Should().BeOfType<Function>()
            .Which.Args[3].Should().Be(new Literal(2m));
        Fx(Text(), "Substitute(xrmv_code, \"a\", \"b\", 100)");
        foreach (var instance in new[] { "xrmv_qty", "1 + 1", "101", "0", "1.5", "-1" })
            Refused(Text(), $"Substitute(xrmv_code, \"a\", \"b\", {instance})").Should().Throw<XrmdValidationException>().WithMessage(
                "The formula column 'xrmv_calcorder.xrmv_fx' uses Substitute with an instance that is not a whole number from 1 to 100 " +
                "written as a constant, which formula columns do not support.", instance);
    }

    [Fact]
    public void Number_in_string_function_is_refused()
    {
        Refused(Text(), "Upper(xrmv_qty)").Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_fx' passes a number to Upper*Text(number, format)*");
        Refused(Text(), "Upper(xrmv_startedon)").Should().Throw<XrmdValidationException>().WithMessage("*passes a User local date*to Upper*");
    }

    [Fact]
    public void Implicit_number_to_text_is_refused()
    {
        Refused(Text(), "12 & \"a\"").Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_fx' uses a number with '&'*Text(number, format)*");
        Refused(Text(), "Text(xrmv_qty)").Should().Throw<XrmdValidationException>().WithMessage("*Text*format*");
    }

    [Fact]
    public void Text_with_locale_token_is_refused()
    {
        Refused(Text(), "Text(xrmv_qty, \"0.00\")").Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_fx' uses the format \"0.00\" in Text*");
        Refused(Text(), "Text(xrmv_qty, \"#,##0\")").Should().Throw<XrmdValidationException>().WithMessage("*format*");
        Same(Fx(Text(), "Text(xrmv_qty, \"#-000\")").Body, F("fx.Text", F("fx.Zero", Own("xrmv_qty")), new Literal("#-000")));
    }

    [Fact]
    public void Return_type_must_match_column()
    {
        Refused(Number(), "Upper(xrmv_code)").Should().Throw<XrmdValidationException>().WithMessage(
            "The formula of 'xrmv_calcorder.xrmv_fx' gives Text, but the column is Decimal.");
        Refused(Text(), "xrmv_qty + 1").Should().Throw<XrmdValidationException>().WithMessage(
            "The formula of 'xrmv_calcorder.xrmv_fx' gives Decimal, but the column is String.");
        Same(Fx(new IntegerAttributeMetadata(), "xrmv_qty + 1").Body, new Binary("Add", F("fx.Zero", Own("xrmv_qty")), new Literal(1m)));
        Refused(new MoneyAttributeMetadata(), "1").Should().Throw<XrmdValidationException>().WithMessage(
            "A formula column cannot be of type Money: 'xrmv_calcorder.xrmv_fx'.");
        Refused(new PicklistAttributeMetadata(), "1").Should().Throw<XrmdValidationException>().WithMessage(
            "Choice formula columns are not supported yet: 'xrmv_calcorder.xrmv_fx'.");
    }

    [Fact]
    public void Currency_column_needs_Decimal()
    {
        Refused(Number(), "xrmv_price * 2").Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_fx' reads the currency column 'xrmv_calcorder.xrmv_price' directly*Decimal(xrmv_price)*");
        Same(Fx(Number(), "Decimal(xrmv_price) * 2").Body,
            new Binary("Multiply", F("fx.Zero", F("fx.Decimal", Own("xrmv_price"))), new Literal(2m)));
    }

    [Fact]
    public void Base_column_is_refused()
    {
        Refused(Number(), "Decimal(xrmv_price_base)").Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_fx' reads the base currency column 'xrmv_calcorder.xrmv_price_base'*");
    }

    [Fact]
    public void Related_column_one_level()
    {
        var d = Fx(Text(), "Upper(xrmv_accountid.name)");
        Same(d.Body, F("fx.Upper", F("fx.Empty", new Column("xrmv_accountid", "name"))));
        d.Sources.Should().Equal(new Column("xrmv_accountid", "name"));
        d.SourceTypeMask.Should().Be(2);
        Same(Fx(Text(), "ThisRecord.xrmv_accountid.name").Body, new Column("xrmv_accountid", "name"));

        Refused(Text(), "xrmv_accountid.parentaccountid.name").Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_fx' reads 'xrmv_accountid.parentaccountid.name'*one level*");
    }

    /// <summary>
    /// <paramref name="formula"/> on <c>xrmv_fx</c> (text) of a model whose lookup <c>xrmv_accountid</c> has the SchemaName
    /// <c>xrmv_AccountId</c> (its default navigation property), and <paramref name="relationship"/> when given.
    /// </summary>
    private static ComputedDefinition WithNavigation(string formula, OneToManyRelationshipMetadata? relationship = null)
    {
        var model = new Model(Formula(new StringAttributeMetadata { LogicalName = "xrmv_fx", MaxLength = 200 }, formula));
        var order = model.Tables[Order];
        order.Attributes.OfType<LookupAttributeMetadata>().Single().SchemaName = "xrmv_AccountId";
        if (relationship is not null) order.SetRelationships([relationship], []);
        return model.Translate("xrmv_fx");
    }

    [Fact]
    public void Related_column_reads_the_navigation_property()
    {
        // Q1 (Dataverse, 2026-11-19, finding 5): xrmv_AccountId.name (the lookup's navigation property, case as in the metadata)
        // works, mask 2; the logical name xrmv_accountid.name is refused.
        var d = WithNavigation("xrmv_AccountId.name");
        Same(d.Body, new Column("xrmv_accountid", "name"));
        d.SourceTypeMask.Should().Be(2);
        FluentActions.Invoking(() => WithNavigation("xrmv_accountid.name")).Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_fx' reads 'xrmv_accountid.name' through the lookup's logical name or display name*" +
            "use 'xrmv_AccountId.name'.");

        // A relationship's own navigation property wins over the default.
        var renamed = new OneToManyRelationshipMetadata
        {
            SchemaName = "xrmv_account_order", ReferencingEntity = Order, ReferencingAttribute = "xrmv_accountid", ReferencedEntity = "account",
            ReferencingEntityNavigationPropertyName = "xrmv_Customer",
        };
        Same(WithNavigation("xrmv_Customer.name", renamed).Body, new Column("xrmv_accountid", "name"));
        FluentActions.Invoking(() => WithNavigation("xrmv_AccountId.name", renamed)).Should().Throw<XrmdValidationException>()
            .WithMessage("*use 'xrmv_Customer.name'.");
    }

    [Fact]
    public void Only_the_invariant_separators_are_read()
    {
        // Q1 (finding 7): Dataverse's Web API reads a formula in the caller's language (an Italian user writes ';' between
        // arguments) and stores it with ','. xrmverse reads the stored, invariant form only: ';' is refused, never read otherwise.
        Refused(Number(), "If(xrmv_qty > 0; 1; 0)").Should().Throw<XrmdValidationException>()
            .WithMessage("The formula column 'xrmv_calcorder.xrmv_fx' *");
        Refused(Number(), "Round(xrmv_qty; 2)").Should().Throw<XrmdValidationException>();
        Refused(Number(), "xrmv_qty * 1,5").Should().Throw<XrmdValidationException>();
    }

    [Fact]
    public void Max_and_Min_ignore_blank_arguments()
    {
        // Q1 follow-up (finding 11): Max(Blank(), -5) is -5, and Max(amount, -5) is -5 when amount is blank (not 0): a blank argument
        // is left out (GREATEST / LEAST skip a null), not read as 0. With every argument blank the result is blank.
        Same(Fx(Number(), "Max(Blank(), -5)").Body, F("fx.Max", new Literal(null), new Literal(-5m)));
        Same(Fx(Number(), "Min(xrmv_qty, -5)").Body, F("fx.Min", Own("xrmv_qty"), new Literal(-5m)));
        // An error argument still makes the result an error.
        var division = new Binary("Divide", F("fx.Zero", Own("xrmv_qty")), new Literal(0m));
        Same(Fx(Number(), "Max(xrmv_qty / 0, xrmv_qty)").Body, new Conditional(
            [new ComputedBranch(new ComputedCondition.Not(Cmp("Null", division)), F("fx.Max", division, Own("xrmv_qty")))], null));
        // All blank: blank, and 0 in a further operation.
        Same(Fx(Number(), "Max(xrmv_qty, Blank()) + 1").Body,
            new Binary("Add", F("fx.Zero", F("fx.Max", Own("xrmv_qty"), new Literal(null))), new Literal(1m)));
        // Not blank when one argument never is.
        Same(Fx(Number(), "Max(xrmv_qty, 2) + 1").Body, new Binary("Add", F("fx.Max", Own("xrmv_qty"), new Literal(2m)), new Literal(1m)));
    }

    [Fact]
    public void A_semicolon_is_refused_with_a_hint()
    {
        // Review 6b: a formula written in a non-English user's notation (';' between arguments) gets a hint to the invariant form.
        Refused(Number(), "Round(xrmv_qty; 2)").Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_fx' uses ';', which formula columns do not read: use ',' between arguments and '.' " +
            "as decimal mark (the invariant form Dataverse stores).");
        // A ';' inside text is text.
        Fx(Text(), "xrmv_code & \";\"").Body.Should().NotBeNull();
    }

    [Fact]
    public void Blank_is_not_zero_in_a_comparison()
    {
        // Q1 (finding 2): If(Blank() = 0, 1, 0) is 0 on Dataverse.
        Same(Fx(Number(), "If(Blank() = 0, 1, 0)").Body.Should().BeOfType<Conditional>().Subject.Branches.Single().When,
            Cmp("Null", new Literal(0m)));
    }

    [Fact]
    public void A_formula_reading_no_column_has_mask_1()
    {
        // Q1 (finding 9): Dataverse gives Mod(-7, 3) the SourceTypeMask 1.
        Fx(Number(), "Mod(-7, 3)").SourceTypeMask.Should().Be(1);
        Fx(Number(), "xrmv_qty + 1").SourceTypeMask.Should().Be(1);
    }

    [Fact]
    public void Display_name_resolves()
    {
        var d = Fx(Text(), "Lower('Order Code')");
        Same(d.Body, F("fx.Lower", F("fx.Empty", Own("xrmv_code"))));
        Same(Fx(Text(), "ThisRecord.'Order Code'").Body, Own("xrmv_code"));
    }

    [Fact]
    public void Cycle_and_depth()
    {
        var cycle = new Model(
            Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_f1", Precision = 2 }, "xrmv_f2 + 1"),
            Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_f2", Precision = 2 }, "xrmv_f1"));
        var act = () => cycle.Translate("xrmv_f1");
        act.Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_f1' is part of a cycle*xrmv_calcorder.xrmv_f1 -> xrmv_calcorder.xrmv_f2 -> xrmv_calcorder.xrmv_f1*");

        ChainOf(10).Translate("xrmv_f1").SourceTypeMask.Should().Be(8);
        var deep = () => ChainOf(11).Translate("xrmv_f1");
        deep.Should().Throw<XrmdValidationException>().WithMessage("The formula column 'xrmv_calcorder.xrmv_f1' chains more than 10 *");
    }

    /// <summary>xrmv_f1 = xrmv_f2 + 1, ..., xrmv_f{n} = xrmv_qty: a chain of <paramref name="n"/> formula columns.</summary>
    private static Model ChainOf(int n) => new(Enumerable.Range(1, n).Select(i => (AttributeMetadata)Formula(
        new DecimalAttributeMetadata { LogicalName = $"xrmv_f{i}", Precision = 2 }, i == n ? "xrmv_qty" : $"xrmv_f{i + 1} + 1")).ToArray());

    [Fact]
    public void Longer_than_1000_characters_is_refused()
    {
        var formula = "\"" + new string('a', 999) + "\"";
        Refused(Text(), formula).Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_fx' has a formula of 1001 characters: a formula column takes 1,000 at most.");
        Fx(Text(), "=" + formula[..^2] + "\"").Should().NotBeNull(); // 1,000 characters once the "=" is stripped
    }

    [Fact]
    public void Mixed_user_local_and_date_only_is_refused()
    {
        Refused(Number(), "DateDiff(xrmv_startedon, xrmv_duedate)").Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_fx' uses DateDiff on a User local date ('xrmv_calcorder.xrmv_startedon') and a Date only date ('xrmv_calcorder.xrmv_duedate')*");
        Same(Fx(Number(), "DateDiff(xrmv_startedon, Now(), TimeUnit.Hours)").Body,
            F("fx.DateDiff", Own("xrmv_startedon"), new Now(), new Literal("Hours")));
    }

    [Fact]
    public void Syntax_and_binding_errors_are_refused()
    {
        Refused(Number(), "xrmv_qty +").Should().Throw<XrmdValidationException>().WithMessage(
            "The formula column 'xrmv_calcorder.xrmv_fx' has a formula that is not valid: *");
        Refused(Number(), "xrmv_nope + 1").Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_nope'*");
        Refused(Number(), "Round(xrmv_qty)").Should().Throw<XrmdValidationException>().WithMessage("*Round*");
    }
}
