using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Workflows.Calculated;
using Xrmd.Workflows.Xaml;
using static Xrmd.Metadata.Computed.ComputedExpr;

namespace Xrmd.Workflows.Tests;

/// <summary>
/// The translation of calculated column XAML into <see cref="ComputedExpr"/> (plan 2.12a, Task 2), on the provisional fixtures of
/// tests/fixtures/calculated (operator names aligned with the 2.12a parity export, other shapes assumed, ruling K2) and variants of
/// them. The real files of the export are translated by <see cref="RealCalculatedXamlTests"/>.
/// </summary>
public class CalculatedTranslatorTests
{
    private const string Order = "xrmv_calcorder";

    private static string Xaml(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "calculated", name + ".xaml"));

    private static string RealWorkflow(string prefix) =>
        File.ReadAllText(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "workflows", "real"), prefix + "-*.xaml").Single());

    private static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property)!.GetSetMethod(nonPublic: true)!.Invoke(target, [value]);

    private static T Calculated<T>(T attribute, string xaml) where T : AttributeMetadata
    {
        attribute.SourceType = 1;
        ComputedColumnRules.SetFormula(attribute, xaml);
        return attribute;
    }

    private static OptionSetMetadata Options(params int[] values) =>
        new(new OptionMetadataCollection(values.Select(v => new OptionMetadata(new Label(), v)).ToList()));

    private static EntityMetadata Table(string name, params AttributeMetadata[] attributes)
    {
        var entity = new EntityMetadata { LogicalName = name };
        entity.SetAttributes(attributes);
        return entity;
    }

    /// <summary>The simple columns of <c>xrmv_calcorder</c> the fixtures read.</summary>
    private static List<AttributeMetadata> OrderColumns() =>
    [
        new StringAttributeMetadata { LogicalName = "xrmv_name", MaxLength = 100 },
        new MoneyAttributeMetadata { LogicalName = "xrmv_amount" },
        new IntegerAttributeMetadata { LogicalName = "xrmv_quantity" },
        new DateTimeAttributeMetadata { LogicalName = "xrmv_orderdate", DateTimeBehavior = DateTimeBehavior.UserLocal },
        new DateTimeAttributeMetadata { LogicalName = "xrmv_shipdate", DateTimeBehavior = DateTimeBehavior.DateOnly },
        new BooleanAttributeMetadata { LogicalName = "xrmv_rush" },
        new PicklistAttributeMetadata { LogicalName = "xrmv_status", OptionSet = Options(100000000, 100000001) },
        new LookupAttributeMetadata { LogicalName = "xrmv_accountid", Targets = ["account"] },
    ];

    /// <summary>A model: <c>xrmv_calcorder</c> with <paramref name="extra"/> columns, and <c>account</c> with <paramref name="accountExtra"/>.</summary>
    private sealed class Model
    {
        public Dictionary<string, EntityMetadata> Tables { get; } = new(StringComparer.Ordinal);

        public Model(IEnumerable<AttributeMetadata> extra, IEnumerable<AttributeMetadata>? accountExtra = null, IEnumerable<AttributeMetadata>? orderColumns = null)
        {
            Tables[Order] = Table(Order, [.. orderColumns ?? OrderColumns(), .. extra]);
            Tables["account"] = Table("account", [new StringAttributeMetadata { LogicalName = "name", MaxLength = 160 },
                new LookupAttributeMetadata { LogicalName = "parentaccountid", Targets = ["account"] }, .. accountExtra ?? []]);
        }

        public EntityMetadata? Find(string name) => Tables.GetValueOrDefault(name);

        public ComputedDefinition Translate(string table, string column) =>
            new CalculatedColumns(Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance).Translate(Tables[table], Tables[table].Attributes.Single(a => a.LogicalName == column), Find);
    }

    private static ComputedDefinition TranslateOrder(AttributeMetadata column, params AttributeMetadata[] others) =>
        new Model([column, .. others]).Translate(Order, column.LogicalName);

    private static Column Own(string attribute) => new(null, attribute);

    private static Literal Int(int value) => new(value);

    // ---- the fixtures ----------------------------------------------------------------------------------------------
    // The shapes the 2.12a parity export confirms (a product, CONCAT with a parent column, DIFFINDAYS with the current time, a
    // leading byte order mark) are tested on its real files: RealCalculatedXamlTests. The provisional fixtures below cover the
    // shapes it does not show.

    [Fact]
    public void Conditional_with_two_branches_and_otherwise()
    {
        var d = TranslateOrder(Calculated(new DateTimeAttributeMetadata { LogicalName = "xrmv_followup" }, Xaml("conditional-dates")));

        var conditional = d.Body.Should().BeOfType<Conditional>().Subject;
        conditional.Branches.Should().HaveCount(2);
        conditional.Branches[0].When.Should().Be(new ComputedCondition.Compare("GreaterThan", Own("xrmv_amount"), new Literal(1000m)));
        Fn(conditional.Branches[0].Then).Should().Be(("ADDDAYS", "2,xrmv_orderdate"));
        conditional.Branches[1].When.Should().Be(new ComputedCondition.Compare("GreaterThan", Own("xrmv_amount"), new Literal(100m)));
        Fn(conditional.Branches[1].Then).Should().Be(("ADDWEEKS", "1,xrmv_orderdate"));
        Fn(conditional.Otherwise!).Should().Be(("ADDMONTHS", "1,xrmv_orderdate"));
        d.Sources.Should().Equal(Own("xrmv_amount"), Own("xrmv_orderdate"));
    }

    /// <summary>A function of a literal and a column, as (name, "literal,column").</summary>
    private static (string, string) Fn(ComputedExpr e)
    {
        var f = e.Should().BeOfType<Function>().Subject;
        f.Args.Should().HaveCount(2);
        return (f.Name, $"{f.Args[0].Should().BeOfType<Literal>().Subject.Value},{f.Args[1].Should().BeOfType<Column>().Subject.Attribute}");
    }

    [Fact]
    public void And_or_conditions_nest()
    {
        var d = TranslateOrder(Calculated(new BooleanAttributeMetadata { LogicalName = "xrmv_isbig" }, Xaml("boolean-and-or")));

        var conditional = d.Body.Should().BeOfType<Conditional>().Subject;
        conditional.Branches.Should().ContainSingle();
        conditional.Branches[0].When.Should().Be(new ComputedCondition.Or(
            new ComputedCondition.And(
                new ComputedCondition.Compare("GreaterThan", Own("xrmv_amount"), new Literal(1000m)),
                new ComputedCondition.Compare("GreaterEqual", Own("xrmv_quantity"), Int(10))),
            new ComputedCondition.Compare("Equal", Own("xrmv_rush"), new Literal(true))));
        conditional.Branches[0].Then.Should().Be(new Literal(true));
        conditional.Otherwise.Should().Be(new Literal(false));
    }

    [Fact]
    public void Picklist_branches_check_option_values()
    {
        var tier = () => Calculated(new PicklistAttributeMetadata { LogicalName = "xrmv_tier", OptionSet = Options(100000000, 100000001, 100000002) },
            Xaml("picklist-branches"));
        var d = TranslateOrder(tier());

        var conditional = d.Body.Should().BeOfType<Conditional>().Subject;
        conditional.Branches[0].When.Should().Be(new ComputedCondition.Compare("Equal", Own("xrmv_status"), Int(100000001)));
        conditional.Branches[0].Then.Should().Be(Int(100000002));
        conditional.Otherwise.Should().Be(Int(100000000));

        // A value the calculated column's choices do not have.
        var missingResult = () => TranslateOrder(Calculated(
            new PicklistAttributeMetadata { LogicalName = "xrmv_tier", OptionSet = Options(100000000, 100000001) }, Xaml("picklist-branches")));
        missingResult.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_tier'*100000002*");

        // A value the compared column's choices do not have.
        var columns = OrderColumns();
        columns[columns.FindIndex(c => c.LogicalName == "xrmv_status")] =
            new PicklistAttributeMetadata { LogicalName = "xrmv_status", OptionSet = Options(100000000) };
        var missingCondition = () => new Model([tier()], orderColumns: columns).Translate(Order, "xrmv_tier");
        missingCondition.Should().Throw<XrmdValidationException>().WithMessage("*100000001*xrmv_status*");
    }

    [Fact]
    public void Choice_compared_with_an_empty_constant_is_accepted()
    {
        // "status equals (empty)": the constant is null, not an option value, so the choices are not checked.
        var xaml = Xaml("picklist-branches").Replace("[New Object() { ConditionBranchStep2_2 }]", "[New Object() { Nothing }]");
        var d = TranslateOrder(Calculated(
            new PicklistAttributeMetadata { LogicalName = "xrmv_tier", OptionSet = Options(100000000, 100000001, 100000002) }, xaml));

        var conditional = d.Body.Should().BeOfType<Conditional>().Subject;
        conditional.Branches[0].When.Should().Be(new ComputedCondition.Compare("Null", Own("xrmv_status"), null));

        // NotEqual empty is "has a value", with the constant on either side; other operators cannot compare with it.
        var notEqual = TranslateOrder(Calculated(
            new PicklistAttributeMetadata { LogicalName = "xrmv_tier", OptionSet = Options(100000000, 100000001, 100000002) },
            xaml.Replace("x:Key=\"ConditionOperator\">Equal<", "x:Key=\"ConditionOperator\">NotEqual<")));
        notEqual.Body.Should().BeOfType<Conditional>().Which.Branches[0].When
            .Should().Be(new ComputedCondition.Compare("NotNull", Own("xrmv_status"), null));

        var quantity = Xaml("picklist-branches")
            .Replace("Attribute=\"xrmv_status\" Entity", "Attribute=\"xrmv_quantity\" Entity")
            .Replace("[New Object() { ConditionBranchStep2_2 }]", "[New Object() { Nothing }]")
            .Replace("x:Key=\"ConditionOperator\">Equal<", "x:Key=\"ConditionOperator\">GreaterThan<");
        var greater = () => TranslateOrder(Calculated(
            new PicklistAttributeMetadata { LogicalName = "xrmv_tier", OptionSet = Options(100000000, 100000001, 100000002) }, quantity));
        greater.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_tier'*GreaterThan with an empty value*");
    }

    [Fact]
    public void Trim_functions()
    {
        var d = TranslateOrder(Calculated(new StringAttributeMetadata { LogicalName = "xrmv_code", MaxLength = 100 }, Xaml("trim")));

        d.Body.Should().BeEquivalentTo(
            new Function("TRIMLEFT", [new Function("TRIMRIGHT", [Own("xrmv_name"), Int(2)]), Int(3)]), o => o.RespectingRuntimeTypes());
        d.Sources.Should().Equal(Own("xrmv_name"));
    }

    // ---- chains ------------------------------------------------------------------------------------------------------

    /// <summary>chained.xaml with its target and the calculated column it reads renamed: <paramref name="target"/> = <paramref name="source"/> / 2.</summary>
    private static string Chain(string target, string source) =>
        Xaml("chained").Replace("xrmv_double", target).Replace("\"xrmv_total\"", $"\"{source}\"");

    [Fact]
    public void Chained_calculated_column_sets_mask_8()
    {
        var total = Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_total" }, Xaml("arithmetic-money"));
        var d = TranslateOrder(Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_double", Precision = 2 }, Xaml("chained")), total);

        d.Body.Should().Be(new Binary("Divide", Own("xrmv_total"), new Literal(2m)));
        d.Sources.Should().Equal(Own("xrmv_total"));
        d.SourceTypeMask.Should().Be(8);
    }

    /// <summary>xrmv_c1 reads xrmv_c2 ... xrmv_c{n} reads xrmv_amount: a chain of <paramref name="n"/> calculated columns.</summary>
    private static Model ChainOf(int n)
    {
        var columns = Enumerable.Range(1, n).Select(i => (AttributeMetadata)Calculated(new DecimalAttributeMetadata { LogicalName = $"xrmv_c{i}", Precision = 2 },
            Chain($"xrmv_c{i}", i == n ? "xrmv_amount" : $"xrmv_c{i + 1}"))).ToList();
        return new Model(columns);
    }

    [Fact]
    public void Chained_calculated_depth_6_is_refused()
    {
        ChainOf(5).Translate(Order, "xrmv_c1").Sources.Should().Equal(Own("xrmv_c2"));

        var act = () => ChainOf(6).Translate(Order, "xrmv_c1");
        act.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_c1'*more than 5*");
    }

    [Fact]
    public void Cycle_is_refused()
    {
        var model = new Model([
            Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_a", Precision = 2 }, Chain("xrmv_a", "xrmv_b")),
            Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_b", Precision = 2 }, Chain("xrmv_b", "xrmv_a")),
        ]);

        var act = () => model.Translate(Order, "xrmv_a");
        act.Should().Throw<XrmdValidationException>().WithMessage("*cycle*xrmv_calcorder.xrmv_a -> xrmv_calcorder.xrmv_b -> xrmv_calcorder.xrmv_a*");

        var self = () => TranslateOrder(Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_a", Precision = 2 }, Chain("xrmv_a", "xrmv_a")));
        self.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_a'*itself*");
    }

    /// <summary>concat-related.xaml as a column of <c>account</c> reading its own parent account (<c>parentaccountid</c>).</summary>
    private static string AccountReadingItsParent() => Xaml("concat-related")
        .Replace("xrmv_calcorder", "account").Replace("related_xrmv_accountid#account", "related_parentaccountid#account")
        .Replace("\"xrmv_name\"", "\"name\"").Replace("xrmv_label", "xrmv_pcalc");

    [Fact]
    public void Parent_calculated_referencing_its_parent_is_refused()
    {
        var pcalc = Calculated(new StringAttributeMetadata { LogicalName = "xrmv_pcalc", MaxLength = 200 }, AccountReadingItsParent());
        // The parent's column alone is valid (two tables).
        new Model([], [pcalc]).Translate("account", "xrmv_pcalc").SourceTypeMask.Should().Be(3);

        // xrmv_label reads account.xrmv_pcalc, which reads the account's parent: three tables.
        var label = Calculated(new StringAttributeMetadata { LogicalName = "xrmv_label", MaxLength = 200 },
            Xaml("concat-related").Replace("Attribute=\"name\"", "Attribute=\"xrmv_pcalc\""));
        var act = () => new Model([label], [pcalc]).Translate(Order, "xrmv_label");
        act.Should().Throw<XrmdValidationException>().WithMessage("*'account.xrmv_pcalc'*two tables*");
    }

    // ---- calculated columns reading formula columns (plan 2.12c: Learn advises against it but allows it) ---------------------

    private static T Formula<T>(T attribute, string formula) where T : AttributeMetadata
    {
        attribute.SourceType = ComputedColumnRules.Formula;
        ComputedColumnRules.SetFormula(attribute, formula);
        return attribute;
    }

    [Fact]
    public void Calculated_column_reads_a_formula_column()
    {
        var fx = Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_fx", Precision = 2 }, "Decimal(xrmv_amount) * 2");
        var d = TranslateOrder(Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_double", Precision = 2 }, Chain("xrmv_double", "xrmv_fx")), fx);

        d.Sources.Should().Equal(Own("xrmv_fx"));
        d.SourceTypeMask.Should().Be(8);
    }

    /// <summary>
    /// xrmv_c1 ... xrmv_c{calculated} (calculated, each reading the next) then xrmv_f1 ... xrmv_f{formulas} (formula, the last reading
    /// xrmv_quantity).
    /// </summary>
    private static Model MixedChainOf(int calculated, int formulas)
    {
        var columns = Enumerable.Range(1, calculated).Select(i => (AttributeMetadata)Calculated(
            new DecimalAttributeMetadata { LogicalName = $"xrmv_c{i}", Precision = 2 },
            Chain($"xrmv_c{i}", i == calculated ? "xrmv_f1" : $"xrmv_c{i + 1}"))).ToList();
        columns.AddRange(Enumerable.Range(1, formulas).Select(i => (AttributeMetadata)Formula(
            new DecimalAttributeMetadata { LogicalName = $"xrmv_f{i}", Precision = 2 }, i == formulas ? "xrmv_quantity" : $"xrmv_f{i + 1} + 1")));
        return new Model(columns);
    }

    [Fact]
    public void Mixed_chain_counts_toward_the_formula_limit_of_10()
    {
        // Ruling F10: a chain holding a formula column may hold 10 columns, more than the 5 of a calculated-only chain.
        MixedChainOf(3, 3).Translate(Order, "xrmv_c1").SourceTypeMask.Should().Be(8);
        MixedChainOf(5, 5).Translate(Order, "xrmv_c1").Sources.Should().Equal(Own("xrmv_c2"));

        var act = () => MixedChainOf(5, 6).Translate(Order, "xrmv_c1");
        act.Should().Throw<XrmdValidationException>().WithMessage("The calculated column 'xrmv_calcorder.xrmv_c1' chains more than 10 formula or calculated columns*");
    }

    [Fact]
    public void Parent_formula_column_must_read_its_own_record()
    {
        var own = Formula(new StringAttributeMetadata { LogicalName = "xrmv_pfx", MaxLength = 200 }, "name & \"!\"");
        var label = Calculated(new StringAttributeMetadata { LogicalName = "xrmv_label", MaxLength = 200 },
            Xaml("concat-related").Replace("Attribute=\"name\"", "Attribute=\"xrmv_pfx\""));
        new Model([label], [own]).Translate(Order, "xrmv_label").SourceTypeMask.Should().Be(8 | 1);

        // A parent formula column that reads the parent's own parent makes three tables.
        var related = Formula(new StringAttributeMetadata { LogicalName = "xrmv_pfx", MaxLength = 200 }, "parentaccountid.name & \"!\"");
        var act = () => new Model([label], [related]).Translate(Order, "xrmv_label");
        act.Should().Throw<XrmdValidationException>().WithMessage("*'account.xrmv_pfx', a formula column of its parent record*two tables*");

        // The same through another formula column of the parent (OwnRecordOnly follows computed columns).
        var outer = Formula(new StringAttributeMetadata { LogicalName = "xrmv_pfx2", MaxLength = 200 }, "xrmv_pfx & \"?\"");
        var label2 = Calculated(new StringAttributeMetadata { LogicalName = "xrmv_label", MaxLength = 200 },
            Xaml("concat-related").Replace("Attribute=\"name\"", "Attribute=\"xrmv_pfx2\""));
        var indirect = () => new Model([label2], [related, outer]).Translate(Order, "xrmv_label");
        indirect.Should().Throw<XrmdValidationException>().WithMessage("*'account.xrmv_pfx2'*two tables*");
    }

    [Fact]
    public void Customer_lookup_parent_is_refused()
    {
        var columns = OrderColumns();
        var customer = new LookupAttributeMetadata { LogicalName = "xrmv_accountid", Targets = ["account", "contact"] };
        Set(customer, nameof(AttributeMetadata.AttributeType), AttributeTypeCode.Customer);
        columns[columns.FindIndex(c => c.LogicalName == "xrmv_accountid")] = customer;
        var label = Calculated(new StringAttributeMetadata { LogicalName = "xrmv_label", MaxLength = 200 }, Xaml("concat-related"));

        var act = () => new Model([label], orderColumns: columns).Translate(Order, "xrmv_label");
        act.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_label'*xrmv_accountid*");
    }

    // ---- refusals ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Diff_with_mixed_behaviors_is_refused()
    {
        // diff-now.xaml with the current time replaced by a read of xrmv_shipdate (DateOnly; xrmv_orderdate is UserLocal).
        var xaml = Regex.Replace(Xaml("diff-now"),
            "<mxswa:ActivityReference [^>]*>\\s*<mxswa:ActivityReference.Arguments>\\s*<InArgument [^>]*>Now</InArgument>.*?</mxswa:ActivityReference>",
            "<mxswa:GetEntityProperty Attribute=\"xrmv_shipdate\" Entity=\"[InputEntities(&quot;primaryEntity&quot;)]\" EntityName=\"xrmv_calcorder\" Value=\"[SetAttributeValueStep1_3]\" />",
            RegexOptions.Singleline);
        xaml.Should().NotContain(">Now<");

        var act = () => TranslateOrder(Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_age" }, xaml));
        act.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_age'*DIFFINDAYS*UserLocal*DateOnly*");

        // The same behavior on both sides is accepted.
        TranslateOrder(Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_age" }, xaml.Replace("xrmv_shipdate", "xrmv_orderdate")))
            .Body.Should().BeOfType<Function>().Which.Name.Should().Be("DIFFINDAYS");
    }

    // ---- empty constants and date-only hours (final review I2: refused at definition, K6) ------------------------------------

    private static PicklistAttributeMetadata Tier() =>
        new() { LogicalName = "xrmv_tier", OptionSet = Options(100000000, 100000001, 100000002) };

    [Fact]
    public void Empty_constant_as_divisor_is_refused()
    {
        // xrmv_double = xrmv_total / Nothing: NULLIF(NULL, 0) fails every read in SQL Server.
        var total = Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_total" }, Xaml("arithmetic-money"));
        var xaml = Xaml("chained").Replace("{ SetAttributeValueStep1_2, SetAttributeValueStep1_3 }", "{ SetAttributeValueStep1_2, Nothing }");
        var act = () => TranslateOrder(Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_double", Precision = 2 }, xaml), total);
        act.Should().Throw<XrmdValidationException>().WithMessage("The calculated column 'xrmv_calcorder.xrmv_double' uses Divide with an empty value*");
    }

    [Fact]
    public void Empty_constant_as_trim_text_is_refused()
    {
        // TRIMRIGHT(Nothing, 2): a function cannot take an empty constant.
        var xaml = Xaml("trim").Replace("{ SetAttributeValueStep1_2, SetAttributeValueStep1_3 }", "{ Nothing, SetAttributeValueStep1_3 }");
        var act = () => TranslateOrder(Calculated(new StringAttributeMetadata { LogicalName = "xrmv_code", MaxLength = 100 }, xaml));
        act.Should().Throw<XrmdValidationException>().WithMessage("The calculated column 'xrmv_calcorder.xrmv_code' uses TRIMRIGHT with an empty value as argument 1*");
    }

    [Fact]
    public void Conditional_whose_branches_are_all_empty_is_refused()
    {
        var both = Xaml("picklist-branches").Replace("Value=\"[SetAttributeValueStep1_1]\"", "Value=\"[Nothing]\"")
            .Replace("Value=\"[SetAttributeValueStep2_1]\"", "Value=\"[Nothing]\"");
        var act = () => TranslateOrder(Calculated(Tier(), both));
        act.Should().Throw<XrmdValidationException>().WithMessage("The calculated column 'xrmv_calcorder.xrmv_tier' sets an empty value in every branch*");

        // One empty branch beside a value is accepted.
        var one = Xaml("picklist-branches").Replace("Value=\"[SetAttributeValueStep1_1]\"", "Value=\"[Nothing]\"");
        var conditional = TranslateOrder(Calculated(Tier(), one)).Body.Should().BeOfType<Conditional>().Subject;
        conditional.Branches[0].Then.Should().Be(new Literal(null));
        conditional.Otherwise.Should().Be(Int(100000000));
    }

    [Fact]
    public void Hours_on_a_date_only_value_are_refused()
    {
        // conditional-dates with ADDHOURS on xrmv_shipdate (Date only).
        var hours = Xaml("conditional-dates").Replace(">AddDays<", ">AddHours<").Replace("Attribute=\"xrmv_orderdate\"", "Attribute=\"xrmv_shipdate\"");
        var act = () => TranslateOrder(Calculated(new DateTimeAttributeMetadata { LogicalName = "xrmv_followup" }, hours));
        act.Should().Throw<XrmdValidationException>()
            .WithMessage("The calculated column 'xrmv_calcorder.xrmv_followup' uses ADDHOURS on a date only value ('xrmv_calcorder.xrmv_shipdate')*");

        // The same on a Date only calculated column it reads (its value is a date, without a time).
        var shipCalc = Calculated(new DateTimeAttributeMetadata { LogicalName = "xrmv_shipcalc", DateTimeBehavior = DateTimeBehavior.DateOnly },
            Xaml("conditional-dates").Replace("xrmv_followup", "xrmv_shipcalc"));
        var subtract = Xaml("conditional-dates").Replace(">AddDays<", ">SubtractHours<").Replace("Attribute=\"xrmv_orderdate\"", "Attribute=\"xrmv_shipcalc\"");
        var chained = () => TranslateOrder(Calculated(new DateTimeAttributeMetadata { LogicalName = "xrmv_followup" }, subtract), shipCalc);
        chained.Should().Throw<XrmdValidationException>().WithMessage("*SUBTRACTHOURS on a date only value*xrmv_shipcalc*");

        // ADDHOURS on a User local date is accepted.
        TranslateOrder(Calculated(new DateTimeAttributeMetadata { LogicalName = "xrmv_followup" },
            Xaml("conditional-dates").Replace(">AddDays<", ">AddHours<"))).Body.Should().BeOfType<Conditional>();
    }

    [Fact]
    public void Target_attribute_mismatch_is_refused()
    {
        var act = () => TranslateOrder(Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_other" }, Xaml("arithmetic-money")));
        act.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_other'*xrmv_total*");
    }

    [Fact]
    public void Unknown_operator_is_refused_with_its_name()
    {
        var xaml = Xaml("arithmetic-money").Replace("ExpressionOperator\">Multiply<", "ExpressionOperator\">POWER<");
        var act = () => TranslateOrder(Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_total" }, xaml));
        act.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_total'*POWER*");
    }

    [Fact]
    public void Workflow_xaml_is_not_a_calculated_definition()
    {
        // The OnCreate workflow of the 2.14 parity export creates a task: not a calculated column definition.
        // Its table renamed to the calculated column's, so that the refusal is about its steps.
        var xaml = RealWorkflow("OnCreate").Replace("\"xrmv_order\"", $"\"{Order}\"").Replace("&quot;xrmv_order&quot;", $"&quot;{Order}&quot;");
        var act = () => TranslateOrder(Calculated(new StringAttributeMetadata { LogicalName = "xrmv_label", MaxLength = 100 }, xaml));
        act.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_label'*Create*cannot appear in a calculated column definition*");

        // As exported, it is also written for another table.
        var asIs = () => TranslateOrder(Calculated(new StringAttributeMetadata { LogicalName = "xrmv_label", MaxLength = 100 }, RealWorkflow("OnCreate")));
        asIs.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_label'*'xrmv_order'*");
    }

    [Fact]
    public void Result_type_must_fit_the_column()
    {
        // A text result in a currency column.
        var act = () => TranslateOrder(Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_label" },
            Xaml("concat-related")));
        act.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_label'*Money*");
    }

    [Fact]
    public void Column_without_a_value_of_its_own_is_refused()
    {
        // CONCAT(xrmv_accountidname, " / ", account.name): the name of a lookup is stored by the lookup, it has no value of its own.
        var lookupName = new StringAttributeMetadata { LogicalName = "xrmv_accountidname", MaxLength = 100 };
        Set(lookupName, nameof(AttributeMetadata.AttributeOf), "xrmv_accountid");
        var xaml = Xaml("concat-related").Replace("Attribute=\"xrmv_name\"", "Attribute=\"xrmv_accountidname\"");
        var act = () => TranslateOrder(Calculated(new StringAttributeMetadata { LogicalName = "xrmv_label", MaxLength = 200 }, xaml), lookupName);
        act.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_label'*'xrmv_calcorder.xrmv_accountidname'*no value of its own*");
    }

    [Fact]
    public void Money_base_column_is_a_source()
    {
        // xrmv_amount_base * xrmv_quantity: the base value of a money column is stored, it can be read.
        var amountBase = new MoneyAttributeMetadata { LogicalName = "xrmv_amount_base" };
        Set(amountBase, nameof(MoneyAttributeMetadata.IsBaseCurrency), true);
        Set(amountBase, nameof(AttributeMetadata.AttributeOf), "xrmv_amount");
        var xaml = Xaml("arithmetic-money").Replace("Attribute=\"xrmv_amount\"", "Attribute=\"xrmv_amount_base\"");

        var d = TranslateOrder(Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_total" }, xaml), amountBase);

        d.Body.Should().Be(new Binary("Multiply", Own("xrmv_amount_base"), Own("xrmv_quantity")));
    }

    [Fact]
    public void Function_argument_types_are_checked()
    {
        // ADDDAYS(2, xrmv_orderdate) with the date replaced by a text column.
        var xaml = Xaml("conditional-dates").Replace("Attribute=\"xrmv_orderdate\"", "Attribute=\"xrmv_name\"");
        var act = () => TranslateOrder(Calculated(new DateTimeAttributeMetadata { LogicalName = "xrmv_followup" }, xaml));
        act.Should().Throw<XrmdValidationException>().WithMessage("*ADDDAYS*");
    }

    [Fact]
    public void Missing_or_malformed_definition_throws_only_validation_errors()
    {
        var empty = () => TranslateOrder(Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_total" }, ""));
        empty.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_total'*");

        var malformed = () => TranslateOrder(Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_total" }, "<Activity"));
        malformed.Should().Throw<XrmdValidationException>().WithMessage("*'xrmv_calcorder.xrmv_total'*");

        var unknownColumn = () => TranslateOrder(Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_total" },
            Xaml("arithmetic-money").Replace("\"xrmv_amount\"", "\"xrmv_missing\"")));
        unknownColumn.Should().Throw<XrmdValidationException>().WithMessage("*xrmv_missing*");
    }

    [Fact]
    public void Calculated_functions_stay_unsupported_in_workflows()
    {
        // The same XAML read as a business rule: the functions are calculated column operators only.
        var d = XamlWorkflowParser.Parse(Xaml("diff-now"), 2, workflowSdkAvailable: false);
        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("'Now'");

        var divide = XamlWorkflowParser.Parse(Xaml("chained"), 2, workflowSdkAvailable: false);
        divide.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("Divide");

        var dates = XamlWorkflowParser.Parse(Xaml("conditional-dates"), 2, workflowSdkAvailable: false);
        dates.Unsupported.Should().Contain(u => u.Reason.Contains("'AddDays'"));
    }

    [Fact]
    public void Retrieve_current_time_is_refused_in_a_calculated_column()
    {
        // The parity export writes the current time as Now; the workflow operator RetrieveCurrentTime is refused, like a function
        // name the export does not write (ADDDAYS).
        var act = () => TranslateOrder(Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_age" },
            Xaml("diff-now").Replace(">Now<", ">RetrieveCurrentTime<")));
        act.Should().Throw<XrmdValidationException>().WithMessage("*the expression operator 'RetrieveCurrentTime' is not supported*");

        var upper = () => TranslateOrder(Calculated(new DateTimeAttributeMetadata { LogicalName = "xrmv_followup" },
            Xaml("conditional-dates").Replace(">AddDays<", ">ADDDAYS<")));
        upper.Should().Throw<XrmdValidationException>().WithMessage("*the expression operator 'ADDDAYS' is not supported*");
    }

    [Fact]
    public void ParseCalculated_gives_the_set_value_step_of_the_target()
    {
        var d = XamlWorkflowParser.ParseCalculated(Xaml("arithmetic-money"), Order, "xrmv_total", "Microsoft.Xrm.Sdk.Money");

        d.PrimaryEntity.Should().Be(Order);
        d.Root.DepthFirst().OfType<SetValueStep>().Should().ContainSingle().Which.Attribute.Should().Be("xrmv_total");

        var other = () => XamlWorkflowParser.ParseCalculated(Xaml("arithmetic-money"), "account", "xrmv_total", "Microsoft.Xrm.Sdk.Money");
        other.Should().Throw<XamlWorkflowException>().WithMessage("*xrmv_calcorder*account*");
    }
}
