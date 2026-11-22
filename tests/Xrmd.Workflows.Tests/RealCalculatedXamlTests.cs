using System.Globalization;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Workflows.Calculated;
using Xrmd.Workflows.Xaml;
using static Xrmd.Metadata.Computed.ComputedExpr;

namespace Xrmd.Workflows.Tests;

/// <summary>
/// The calculated column XAML of the 2.12a parity export <c>xrmCalcTest_1_0_0_4.zip</c> (tests/fixtures/calculated/real, byte for
/// byte; plan 2.12a, Task 6): every file translates, to the tree its column's definition in the export describes (plan 2.12a, Q1).
/// The model below is the export's own metadata for <c>xrmv_calcorder</c> and <c>account</c> (types, behaviors, option values).
/// </summary>
public class RealCalculatedXamlTests
{
    private const string Order = "xrmv_calcorder";

    internal static string RealDirectory => Path.Combine(AppContext.BaseDirectory, "fixtures", "calculated", "real");

    /// <summary>The XAML of a calculated column of <c>xrmv_calcorder</c>, as the export holds it.</summary>
    internal static string Real(string column) => File.ReadAllText(Path.Combine(RealDirectory, $"{Order}-{column}.xaml"));

    private static OptionSetMetadata Options(params int[] values) =>
        new(new OptionMetadataCollection(values.Select(v => new OptionMetadata(new Label(), v)).ToList()));

    private static DateTimeAttributeMetadata Date(string name, DateTimeBehavior behavior) =>
        new() { LogicalName = name, DateTimeBehavior = behavior, Format = behavior == DateTimeBehavior.DateOnly ? DateTimeFormat.DateOnly : DateTimeFormat.DateAndTime };

    /// <summary>The calculated columns of the export: logical name and an empty column of its type.</summary>
    public static readonly (string Column, Func<AttributeMetadata> Make)[] CalculatedColumns =
    [
        ("xrmv_accountbudget", () => new MoneyAttributeMetadata { LogicalName = "xrmv_accountbudget", Precision = 2 }),
        ("xrmv_age", () => new IntegerAttributeMetadata { LogicalName = "xrmv_age" }),
        ("xrmv_dueplus", () => Date("xrmv_dueplus", DateTimeBehavior.DateOnly)),
        ("xrmv_followup", () => Date("xrmv_followup", DateTimeBehavior.UserLocal)),
        ("xrmv_fxtotal", () => new DecimalAttributeMetadata { LogicalName = "xrmv_fxtotal", Precision = 2 }),
        ("xrmv_hours", () => new IntegerAttributeMetadata { LogicalName = "xrmv_hours" }),
        ("xrmv_isbig", () => new BooleanAttributeMetadata { LogicalName = "xrmv_isbig" }),
        ("xrmv_label", () => new StringAttributeMetadata { LogicalName = "xrmv_label", MaxLength = 4000 }),
        ("xrmv_minusdays", () => Date("xrmv_minusdays", DateTimeBehavior.UserLocal)),
        ("xrmv_minushours", () => Date("xrmv_minushours", DateTimeBehavior.UserLocal)),
        ("xrmv_minusmonths", () => Date("xrmv_minusmonths", DateTimeBehavior.UserLocal)),
        ("xrmv_minusweeks", () => Date("xrmv_minusweeks", DateTimeBehavior.UserLocal)),
        ("xrmv_minusyears", () => Date("xrmv_minusyears", DateTimeBehavior.UserLocal)),
        ("xrmv_minutes", () => new IntegerAttributeMetadata { LogicalName = "xrmv_minutes" }),
        ("xrmv_months", () => new IntegerAttributeMetadata { LogicalName = "xrmv_months" }),
        ("xrmv_net", () => new DecimalAttributeMetadata { LogicalName = "xrmv_net", Precision = 2 }),
        ("xrmv_net2", () => new DecimalAttributeMetadata { LogicalName = "xrmv_net2", Precision = 2 }),
        ("xrmv_plushours", () => Date("xrmv_plushours", DateTimeBehavior.UserLocal)),
        ("xrmv_plusweeks", () => Date("xrmv_plusweeks", DateTimeBehavior.UserLocal)),
        ("xrmv_plusyears", () => Date("xrmv_plusyears", DateTimeBehavior.UserLocal)),
        ("xrmv_short", () => new StringAttributeMetadata { LogicalName = "xrmv_short", MaxLength = 4000 }),
        ("xrmv_tail", () => new StringAttributeMetadata { LogicalName = "xrmv_tail", MaxLength = 4000 }),
        ("xrmv_tier", () => new PicklistAttributeMetadata { LogicalName = "xrmv_tier", OptionSet = Options(128830000, 128830001) }),
        ("xrmv_total", () => new MoneyAttributeMetadata { LogicalName = "xrmv_total", Precision = 2 }),
        ("xrmv_unitprice", () => new DecimalAttributeMetadata { LogicalName = "xrmv_unitprice", Precision = 2 }),
        ("xrmv_weeks", () => new IntegerAttributeMetadata { LogicalName = "xrmv_weeks" }),
        ("xrmv_years", () => new IntegerAttributeMetadata { LogicalName = "xrmv_years" }),
    ];

    /// <summary>
    /// The definition of each column (plan 2.12a, Q1), written as <see cref="Show"/> renders a tree: own columns by name, parent
    /// columns as <c>lookup.column</c>, functions by their catalog name.
    /// </summary>
    public static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["xrmv_accountbudget"] = "xrmv_accountid.xrmv_budget",
        ["xrmv_age"] = "DIFFINDAYS(createdon, NOW)",
        ["xrmv_dueplus"] = "ADDDAYS(10, xrmv_duedate)",
        ["xrmv_followup"] = "IF xrmv_status Equal 1 THEN ADDDAYS(7, xrmv_startedon) ELSE ADDMONTHS(1, xrmv_startedon)",
        ["xrmv_fxtotal"] = "(xrmv_amount Multiply xrmv_qty)",
        ["xrmv_hours"] = "DIFFINHOURS(xrmv_startedon, NOW)",
        ["xrmv_isbig"] = "IF (xrmv_amount GreaterThan 1000 And xrmv_qty GreaterEqual 2) THEN True ELSE False",
        ["xrmv_label"] = "CONCAT(xrmv_code, '-', xrmv_accountid.name)",
        ["xrmv_minusdays"] = "SUBTRACTDAYS(3, xrmv_startedon)",
        ["xrmv_minushours"] = "SUBTRACTHOURS(5, xrmv_startedon)",
        ["xrmv_minusmonths"] = "SUBTRACTMONTHS(1, xrmv_startedon)",
        ["xrmv_minusweeks"] = "SUBTRACTWEEKS(2, xrmv_startedon)",
        ["xrmv_minusyears"] = "SUBTRACTYEARS(1, xrmv_startedon)",
        ["xrmv_minutes"] = "DIFFINMINUTES(xrmv_startedon, NOW)",
        ["xrmv_months"] = "DIFFINMONTHS(xrmv_startedon, NOW)",
        ["xrmv_net"] = "(xrmv_amount Subtract (xrmv_amount Divide 10))",
        ["xrmv_net2"] = "(xrmv_net Multiply 2)",
        ["xrmv_plushours"] = "ADDHOURS(5, xrmv_startedon)",
        ["xrmv_plusweeks"] = "ADDWEEKS(2, xrmv_startedon)",
        ["xrmv_plusyears"] = "ADDYEARS(1, xrmv_startedon)",
        ["xrmv_short"] = "TRIMLEFT(xrmv_code, 2)",
        ["xrmv_tail"] = "TRIMRIGHT(xrmv_code, 2)",
        ["xrmv_tier"] = "IF (xrmv_amount GreaterThan 500 Or xrmv_status Equal 2) THEN 128830001 ELSE 128830000",
        ["xrmv_total"] = "(xrmv_price Multiply xrmv_qty)",
        ["xrmv_unitprice"] = "(xrmv_amount Divide xrmv_qty)",
        ["xrmv_weeks"] = "DIFFINWEEKS(xrmv_startedon, NOW)",
        ["xrmv_years"] = "DIFFINYEARS(xrmv_startedon, NOW)",
    };

    /// <summary>The export's tables: <c>xrmv_calcorder</c> with every calculated column of the export, and <c>account</c>.</summary>
    internal static Dictionary<string, EntityMetadata> Model()
    {
        List<AttributeMetadata> order =
        [
            new StringAttributeMetadata { LogicalName = "xrmv_name", MaxLength = 850 },
            new StringAttributeMetadata { LogicalName = "xrmv_code", MaxLength = 100 },
            new DecimalAttributeMetadata { LogicalName = "xrmv_amount", Precision = 2 },
            new IntegerAttributeMetadata { LogicalName = "xrmv_qty" },
            new MoneyAttributeMetadata { LogicalName = "xrmv_price", Precision = 2 },
            new PicklistAttributeMetadata { LogicalName = "xrmv_status", OptionSet = Options(1, 2, 3) },
            Date("xrmv_startedon", DateTimeBehavior.UserLocal),
            Date("xrmv_duedate", DateTimeBehavior.DateOnly),
            Date("createdon", DateTimeBehavior.UserLocal),
            new LookupAttributeMetadata { LogicalName = "xrmv_accountid", Targets = ["account"] },
        ];
        foreach (var (column, make) in CalculatedColumns)
        {
            var attribute = make();
            attribute.SourceType = 1;
            ComputedColumnRules.SetFormula(attribute, Real(column));
            order.Add(attribute);
        }

        var orderTable = new EntityMetadata { LogicalName = Order };
        orderTable.SetAttributes(order);
        var account = new EntityMetadata { LogicalName = "account" };
        account.SetAttributes([new StringAttributeMetadata { LogicalName = "name", MaxLength = 160 },
            new MoneyAttributeMetadata { LogicalName = "xrmv_budget", Precision = 2 }]);
        return new(StringComparer.Ordinal) { [Order] = orderTable, ["account"] = account };
    }

    public static TheoryData<string> Columns() => new(CalculatedColumns.Select(c => c.Column));

    [Fact]
    public void The_export_holds_one_xaml_per_calculated_column()
    {
        Directory.GetFiles(RealDirectory, "*.xaml").Select(Path.GetFileNameWithoutExtension).Should()
            .BeEquivalentTo(CalculatedColumns.Select(c => $"{Order}-{c.Column}"));
        Expected.Keys.Should().BeEquivalentTo(CalculatedColumns.Select(c => c.Column));
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public void Real_xaml_translates(string column)
    {
        var tables = Model();
        var attribute = tables[Order].Attributes.Single(a => a.LogicalName == column);

        var definition = new CalculatedColumns(Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance).Translate(tables[Order], attribute, n => tables.GetValueOrDefault(n));

        Show(definition.Body).Should().Be(Expected[column]);
    }

    /// <summary>The columns a definition reads and its <c>SourceTypeMask</c> (ruling K12): own (1), parent (2), calculated (8).</summary>
    [Theory]
    [InlineData("xrmv_total", "xrmv_price, xrmv_qty", 1)]
    [InlineData("xrmv_label", "xrmv_code, xrmv_accountid.name", 3)]
    [InlineData("xrmv_age", "createdon", 1)]
    [InlineData("xrmv_accountbudget", "xrmv_accountid.xrmv_budget", 2)]
    [InlineData("xrmv_net2", "xrmv_net", 8)]
    [InlineData("xrmv_tier", "xrmv_amount, xrmv_status", 1)]
    public void Real_sources_and_mask(string column, string sources, int mask)
    {
        var tables = Model();
        var definition = new CalculatedColumns(Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance).Translate(tables[Order], tables[Order].Attributes.Single(a => a.LogicalName == column),
            n => tables.GetValueOrDefault(n));

        string.Join(", ", definition.Sources.Select(Show)).Should().Be(sources);
        definition.SourceTypeMask.Should().Be(mask);
    }

    [Fact]
    public void A_leading_byte_order_mark_is_read()
    {
        // The export's files have none, but a solution file's XAML is kept verbatim (ruling K13), a byte order mark included.
        var tables = Model();
        var total = tables[Order].Attributes.Single(a => a.LogicalName == "xrmv_total");
        ComputedColumnRules.SetFormula(total, "﻿" + Real("xrmv_total"));

        Show(new CalculatedColumns(Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance).Translate(tables[Order], total, n => tables.GetValueOrDefault(n)).Body)
            .Should().Be("(xrmv_price Multiply xrmv_qty)");
    }

    [Fact]
    public void Real_functions_stay_unsupported_in_workflows()
    {
        // The same file read as a business rule: the functions and the current time are calculated column operators only.
        var d = XamlWorkflowParser.Parse(Real("xrmv_age"), 2, workflowSdkAvailable: false);
        d.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("'Now'");

        // TRIMLEFT's count is an Integer with the column's TargetType String, a conversion a workflow refuses first; written as a
        // String, the count passes and TrimLeft itself is refused.
        XamlWorkflowParser.Parse(Real("xrmv_short"), 2, workflowSdkAvailable: false).Unsupported.Should().ContainSingle();
        var trim = XamlWorkflowParser.Parse(Real("xrmv_short").Replace("WorkflowPropertyType.Integer, \"2\"", "WorkflowPropertyType.String, \"2\""),
            2, workflowSdkAvailable: false);
        trim.Unsupported.Should().ContainSingle().Which.Reason.Should().Contain("'TrimLeft'");
    }

    [Fact]
    public void A_target_type_other_than_the_column_type_is_refused()
    {
        // The export writes the column's type (Whole number: Int32) as the TargetType of the steps of xrmv_age; any other type that
        // is not the value's own is refused, naming it.
        var tables = Model();
        var age = tables[Order].Attributes.Single(a => a.LogicalName == "xrmv_age");
        ComputedColumnRules.SetFormula(age, Real("xrmv_age").Replace("Value=\"x:Int32\"", "Value=\"x:String\""));

        var act = () => new CalculatedColumns(Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance).Translate(tables[Order], age, n => tables.GetValueOrDefault(n));
        act.Should().Throw<Xrmd.Abstractions.XrmdValidationException>().WithMessage("*TargetType System.String*");

        // Parsed with no column type, the export's file is refused the same way: only the column's type passes.
        var untyped = () => XamlWorkflowParser.ParseCalculated(Real("xrmv_age"), Order, "xrmv_age");
        untyped.Should().Throw<XamlWorkflowException>().WithMessage("*TargetType System.Int32*");
        XamlWorkflowParser.ParseCalculated(Real("xrmv_age"), Order, "xrmv_age", "System.Int32").Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public void Real_xaml_has_no_byte_order_mark_and_crlf_line_ends(string column)
    {
        var bytes = File.ReadAllBytes(Path.Combine(RealDirectory, $"{Order}-{column}.xaml"));
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        System.Text.Encoding.UTF8.GetString(bytes).Should().Contain("\r\n").And.NotMatchRegex("[^\r]\n");
    }

    // ---- rendering ---------------------------------------------------------------------------------------------------

    /// <summary>A readable form of a tree, for comparisons with <see cref="Expected"/>.</summary>
    internal static string Show(ComputedExpr e) => e switch
    {
        Literal { Value: string s } => $"'{s}'",
        Literal { Value: IFormattable f } => f.ToString(null, CultureInfo.InvariantCulture),
        Literal l => l.Value?.ToString() ?? "null",
        Column { Lookup: null } c => c.Attribute,
        Column c => $"{c.Lookup}.{c.Attribute}",
        Now => "NOW",
        Binary b => $"({Show(b.Left)} {b.Operator} {Show(b.Right)})",
        Function f => $"{f.Name}({string.Join(", ", f.Args.Select(Show))})",
        Conditional c => string.Join(" ELSE ", c.Branches.Select(b => $"IF {Show(b.When)} THEN {Show(b.Then)}"))
            + (c.Otherwise is null ? "" : $" ELSE {Show(c.Otherwise)}"),
        _ => e.ToString(),
    };

    private static string Show(ComputedCondition c) => c switch
    {
        ComputedCondition.Compare { Right: null } x => $"{Show(x.Left)} {x.Operator}",
        ComputedCondition.Compare x => $"{Show(x.Left)} {x.Operator} {Show(x.Right)}",
        ComputedCondition.And a => $"({Show(a.Left)} And {Show(a.Right)})",
        ComputedCondition.Or o => $"({Show(o.Left)} Or {Show(o.Right)})",
        ComputedCondition.Not n => $"Not {Show(n.Operand)}",
        _ => c.ToString(),
    };
}
