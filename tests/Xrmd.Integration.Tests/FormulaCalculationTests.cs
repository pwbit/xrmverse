using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata.Computed;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Formula columns computed in SQL Server (plan 2.12c, Task 3): the values of the parity export's three formula columns on the
/// recorded rows, the blank rules of Power Fx (F5), the range of decimals and the Power Fx functions whose T-SQL differs from
/// their SQL Server namesakes. Each test builds its own table (<c>new_fc*</c>; the publisher prefix of the shared server is
/// <c>new</c>) through the metadata messages, so the formulas go through the server's Power Fx translator.
/// </summary>
[Collection("xrmd")]
public sealed class FormulaCalculationTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => TestEntities.EnsurePipeAsync(Store);

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static Label Label(string text) => new(text, 1033);

    private static T Formula<T>(T attribute, string formula) where T : AttributeMetadata
    {
        attribute.SourceType = ComputedColumnRules.Formula;
        attribute.SchemaName ??= attribute.LogicalName;
        attribute.DisplayName ??= Label(attribute.LogicalName);
        ComputedColumnRules.SetFormula(attribute, formula);
        return attribute;
    }

    private static BooleanAttributeMetadata YesNo(string name) => new()
    {
        LogicalName = name,
        OptionSet = new BooleanOptionSetMetadata(new OptionMetadata(Label("Yes"), 1), new OptionMetadata(Label("No"), 0)),
    };

    /// <summary>
    /// A table <c>new_fc{label}{run}</c> with the input columns of the parity export's <c>xrmv_calcorder</c> (new_ names):
    /// <c>new_code</c> (text), <c>new_qty</c> (whole number), <c>new_amount</c> (decimal, 2), <c>new_duedate</c> (Time zone
    /// independent, date only format) and <c>new_startedon</c> (User local).
    /// </summary>
    private async Task<string> TableAsync(string label)
    {
        var name = $"new_fc{label}{_run}";
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = name, SchemaName = name, DisplayName = Label("Table " + name), DisplayCollectionName = Label("Tables " + name),
                OwnershipType = OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name"),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
        });
        await CreateAttributeAsync(name, new StringAttributeMetadata { LogicalName = "new_code", SchemaName = "new_Code", DisplayName = Label("Code"), MaxLength = 100 });
        await CreateAttributeAsync(name, new IntegerAttributeMetadata { LogicalName = "new_qty", SchemaName = "new_Qty", DisplayName = Label("Quantity") });
        await CreateAttributeAsync(name, new DecimalAttributeMetadata { LogicalName = "new_amount", SchemaName = "new_Amount", DisplayName = Label("Amount"), Precision = 2 });
        await CreateAttributeAsync(name, new DateTimeAttributeMetadata
        {
            LogicalName = "new_duedate", SchemaName = "new_DueDate", DisplayName = Label("Due date"),
            DateTimeBehavior = DateTimeBehavior.TimeZoneIndependent, Format = DateTimeFormat.DateOnly,
        });
        await CreateAttributeAsync(name, new DateTimeAttributeMetadata
        {
            LogicalName = "new_startedon", SchemaName = "new_StartedOn", DisplayName = Label("Started on"),
            DateTimeBehavior = DateTimeBehavior.UserLocal, Format = DateTimeFormat.DateAndTime,
        });
        return name;
    }

    private Task CreateAttributeAsync(string table, AttributeMetadata attribute) =>
        Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = table, Attribute = attribute });

    private async Task<Entity> CreateAndReadAsync(string table, Entity row)
    {
        row.LogicalName = table;
        var id = await Admin.CreateAsync(row);
        return await Admin.RetrieveAsync(table, id, new ColumnSet(true));
    }

    private static object? ValueOf(Entity e, string column) => e.Attributes.TryGetValue(column, out var v) ? v : null;

    // ---- parity (Review Focus 2, 3) -----------------------------------------------------------------------------------

    [Fact]
    public async Task Parity_values_match_dataverse()
    {
        // The three formula columns of xrmCalcTest_1_0_0_4.zip (Formulas/xrmv_calcorder-FormulaDefinitions.yaml), on the input
        // values of the four rows Dataverse recorded (tests/fixtures/calculated/real/xrmv_calcorders.csv).
        var table = await TableAsync("par");
        await CreateAttributeAsync(table, Formula(new StringAttributeMetadata { LogicalName = "new_fxlabel", MaxLength = 4000 },
            "Upper(new_code) & \"-\" & Text(new_qty, \"0\")"));
        await CreateAttributeAsync(table, Formula(YesNo("new_fxnodue"), "IsBlank(new_duedate)"));
        await CreateAttributeAsync(table, Formula(new DateTimeAttributeMetadata
        {
            LogicalName = "new_fxplus3", DateTimeBehavior = DateTimeBehavior.UserLocal, Format = DateTimeFormat.DateOnly,
        }, "DateAdd(new_startedon,3,TimeUnit.Days)"));

        var recorded = CalculatedParityExport.Recorded("xrmv_calcorders.csv");
        recorded.Select(r => r["xrmv_fxlabel"]).Should().Equal("AB-2", "ORD-003-1", "ORD-002-0", "ORD-001-3");
        foreach (var r in recorded)
        {
            var row = new Entity { ["new_name"] = r["xrmv_code"] };
            if (r["xrmv_code"].Length > 0) row["new_code"] = r["xrmv_code"];
            if (r["xrmv_qty"].Length > 0) row["new_qty"] = int.Parse(r["xrmv_qty"], CultureInfo.InvariantCulture);
            if (r["xrmv_amount"].Length > 0) row["new_amount"] = decimal.Parse(r["xrmv_amount"], CultureInfo.InvariantCulture);
            if (r["xrmv_duedate"].Length > 0) row["new_duedate"] = Date(r["xrmv_duedate"], DateTimeKind.Unspecified);
            if (r["xrmv_startedon"].Length > 0) row["new_startedon"] = Date(r["xrmv_startedon"], DateTimeKind.Utc);

            var e = await CreateAndReadAsync(table, row);

            e.GetAttributeValue<string>("new_fxlabel").Should().Be(r["xrmv_fxlabel"], r["xrmv_code"]);
            e.GetAttributeValue<bool>("new_fxnodue").Should().Be(bool.Parse(r["xrmv_fxnodue"]), r["xrmv_code"]);
            // A User local date keeps UTC: 2026-11-15 23:00 + 3 days is 2026-11-18 23:00, not shifted by the user's time zone.
            e.GetAttributeValue<DateTime>("new_fxplus3").Should().Be(Date(r["xrmv_fxplus3"], DateTimeKind.Utc), r["xrmv_code"]);
        }
    }

    private static DateTime Date(string text, DateTimeKind kind) =>
        DateTime.SpecifyKind(DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture), kind);

    // ---- blank values (F5), division, range ---------------------------------------------------------------------------

    [Fact]
    public async Task Blank_number_is_zero()
    {
        // Review Focus 1: a calculated column gives null for amount × qty with an empty amount; a formula column gives 0.
        var table = await TableAsync("bn");
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxtotal", Precision = 2 }, "new_amount * new_qty"));

        var e = await CreateAndReadAsync(table, new Entity { ["new_name"] = "AB", ["new_qty"] = 2 });

        ValueOf(e, "new_fxtotal").Should().Be(0m);
    }

    [Fact]
    public async Task Blank_text_is_empty()
    {
        var table = await TableAsync("bt");
        await CreateAttributeAsync(table, Formula(new StringAttributeMetadata { LogicalName = "new_fxtext", MaxLength = 100 }, "new_code & \"x\""));

        var e = await CreateAndReadAsync(table, new Entity { ["new_name"] = "no code" });

        ValueOf(e, "new_fxtext").Should().Be("x");
    }

    [Fact]
    public async Task Division_by_zero_is_null()
    {
        var table = await TableAsync("dz");
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxunit", Precision = 2 }, "new_amount / new_qty"));

        (await CreateAndReadAsync(table, new Entity { ["new_amount"] = 10m, ["new_qty"] = 0 })).Attributes.Should().NotContainKey("new_fxunit");
        ValueOf(await CreateAndReadAsync(table, new Entity { ["new_amount"] = 10m, ["new_qty"] = 4 }), "new_fxunit").Should().Be(2.5m);
    }

    [Fact]
    public async Task Errors_stay_errors_and_blanks_are_zero_in_a_further_operation()
    {
        // Q1 (Dataverse, 2026-11-19, finding 1): the null of a failed operation (division or Mod by zero, Sqrt of a negative number,
        // a decimal out of range) is an error, which stays null through a further operation; a blank operand is 0.
        var table = await TableAsync("er");
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxmodz", Precision = 2 }, "Mod(new_amount, new_qty) + 1"));
        await CreateAttributeAsync(table, Formula(new DoubleAttributeMetadata { LogicalName = "new_fxsqrtn" }, "Sqrt(-new_amount) + 1"));
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxover", Precision = 2 }, "new_amount * 100000000000 + 1"));
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxdivz", Precision = 2 }, "new_amount / new_qty + 1"));
        await CreateAttributeAsync(table, Formula(new StringAttributeMetadata { LogicalName = "new_fxtextz", MaxLength = 100 }, "Text(new_amount / new_qty, \"0\") & \"x\""));
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxmaxz", Precision = 2 }, "Max(new_amount / new_qty, 1)"));
        await CreateAttributeAsync(table, Formula(new IntegerAttributeMetadata { LogicalName = "new_fxlenz" }, "Len(Left(new_code, new_qty - 1))"));
        await CreateAttributeAsync(table, Formula(YesNo("new_fxblankz"), "IsBlank(new_amount / new_qty)"));
        await CreateAttributeAsync(table, Formula(new IntegerAttributeMetadata { LogicalName = "new_fxifz" }, "If(new_amount / new_qty > 1, 1, 0)"));
        // And and Or stop at their first decisive operand, as Power Fx evaluates them: no error is reached.
        await CreateAttributeAsync(table, Formula(YesNo("new_fxandz"), "And(new_qty > 0, new_amount / new_qty > 1)"));
        await CreateAttributeAsync(table, Formula(YesNo("new_fxorz"), "Or(new_qty = 0, new_amount / new_qty > 1)"));
        // If without else: blank when no branch matches (0 in the sum), an error when its branch fails.
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxifelse", Precision = 2 },
            "If(new_amount > 5, new_amount / new_qty) + 1"));

        var zero = await CreateAndReadAsync(table, new Entity { ["new_amount"] = 10m, ["new_qty"] = 0, ["new_code"] = "abc" });
        foreach (var column in new[] { "new_fxmodz", "new_fxsqrtn", "new_fxover", "new_fxdivz", "new_fxtextz", "new_fxmaxz", "new_fxlenz",
                     "new_fxblankz", "new_fxifz", "new_fxifelse" })
            zero.Attributes.Should().NotContainKey(column, "an error stays an error (null)");
        ValueOf(zero, "new_fxandz").Should().Be(false);
        ValueOf(zero, "new_fxorz").Should().Be(true);

        // Blank operands are 0 (and "" for text): amount / qty with a blank amount and qty 1 is 0.
        var blank = await CreateAndReadAsync(table, new Entity { ["new_qty"] = 1 });
        ValueOf(blank, "new_fxmodz").Should().Be(1m);
        ValueOf(blank, "new_fxsqrtn").Should().Be(1d);
        ValueOf(blank, "new_fxover").Should().Be(1m);
        ValueOf(blank, "new_fxdivz").Should().Be(1m);
        ValueOf(blank, "new_fxtextz").Should().Be("0x");
        ValueOf(blank, "new_fxmaxz").Should().Be(1m);
        ValueOf(blank, "new_fxlenz").Should().Be(0);
        ValueOf(blank, "new_fxblankz").Should().Be(false);
        ValueOf(blank, "new_fxifz").Should().Be(0);
        ValueOf(blank, "new_fxandz").Should().Be(false);
        ValueOf(blank, "new_fxorz").Should().Be(false);
        ValueOf(blank, "new_fxifelse").Should().Be(1m, "no branch matches: a blank, 0 in the sum");

        var value = await CreateAndReadAsync(table, new Entity { ["new_amount"] = 10m, ["new_qty"] = 4, ["new_code"] = "abcdef" });
        ValueOf(value, "new_fxdivz").Should().Be(3.5m);
        ValueOf(value, "new_fxlenz").Should().Be(3);
        ValueOf(value, "new_fxifelse").Should().Be(3.5m);
    }

    [Fact]
    public async Task Decimal_out_of_range_is_null()
    {
        // Global Constraints: a decimal outside ±100,000,000,000 gives null; the bound itself is a value.
        var table = await TableAsync("rg");
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxbig", Precision = 2 }, "new_amount * 1000000"));
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxback", Precision = 2 }, "new_amount * 1000000 / 1000000"));

        var over = await CreateAndReadAsync(table, new Entity { ["new_amount"] = 100000.01m });
        over.Attributes.Should().NotContainKey("new_fxbig");
        over.Attributes.Should().NotContainKey("new_fxback", "the intermediate out of range is an error, which the division keeps (Q1)");
        var bound = await CreateAndReadAsync(table, new Entity { ["new_amount"] = 100000m });
        ValueOf(bound, "new_fxbig").Should().Be(100000000000m);
        ValueOf(bound, "new_fxback").Should().Be(100000m);
    }

    // ---- the Q1 parity run (licensed environment, 2026-11-19) ------------------------------------------------------------

    /// <summary>The Q1 values (<c>tests/fixtures/formula/values.json</c>, see its README).</summary>
    private static JsonNode Q1 { get; } = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "formula", "values.json")))!;

    [Fact]
    public async Task Q1_values_match_the_licensed_environment_run()
    {
        // The table of the Q1 run on xrmverse: new_ names for xrmv_, a lookup new_accountid (SchemaName new_AccountId, its
        // navigation property) for xrmv_accountid. The formulas are the ones Dataverse stored, with the names mapped.
        var table = await TableAsync("q1");
        await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata { SchemaName = $"new_{table}_account", ReferencedEntity = "account", ReferencingEntity = table },
            Lookup = new LookupAttributeMetadata { LogicalName = "new_accountid", SchemaName = "new_AccountId", DisplayName = Label("Account") },
        });
        var definitions = Q1["definitions_as_stored"]!.AsObject().ToDictionary(p => "new_" + p.Key["xrmv_".Length..],
            p => p.Value!.GetValue<string>().Replace("xrmv_AccountId", "new_AccountId", StringComparison.Ordinal)
                .Replace("xrmv_", "new_", StringComparison.Ordinal));
        definitions["new_fxacc"].Should().Be("new_AccountId.name");
        AttributeMetadata ColumnOf(string name) => name switch
        {
            "new_fxtext" or "new_fxacc" or "new_fxpad" or "new_fxlabel" => new StringAttributeMetadata { LogicalName = name, MaxLength = 200 },
            "new_fxlen" or "new_fxzero" => new IntegerAttributeMetadata { LogicalName = name },
            _ => new DecimalAttributeMetadata { LogicalName = name, Precision = 2 },
        };
        foreach (var (name, formula) in definitions)
            await CreateAttributeAsync(table, Formula(ColumnOf(name), formula));

        // SourceTypeMask: 2 for the related column, 1 for every other one (Mod(-7, 3) reads no column).
        foreach (var name in definitions.Keys)
        {
            var read = (RetrieveAttributeResponse)await Admin.ExecuteAsync(new RetrieveAttributeRequest { EntityLogicalName = table, LogicalName = name });
            ComputedColumnRules.MaskOf(read.AttributeMetadata).Should().Be(name == "new_fxacc" ? 2 : 1, name);
            ComputedColumnRules.FormulaOf(read.AttributeMetadata).Should().Be(definitions[name]);
        }

        // The follow-up columns (findings 10 and 11): a formula reading a failed formula column, Max with blank arguments.
        var followup = Q1["followup"]!;
        var later = followup["definitions"]!.AsObject().ToDictionary(p => "new_" + p.Key["xrmv_".Length..],
            p => p.Value!.GetValue<string>().Replace("xrmv_", "new_", StringComparison.Ordinal));
        foreach (var (name, formula) in later)
            await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = name, Precision = 2 }, formula));
        var laterColumns = followup["columns"]!.AsArray().Select(c => c!.GetValue<string>()).ToArray();
        var laterRows = followup["rows"]!.AsArray().ToDictionary(r => r![0]!.GetValue<string>(), r => r!.AsArray());

        var accounts = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var rows = Q1["rows"]!.AsArray();
        rows.Should().HaveCount(8);
        foreach (var r in rows)
        {
            var row = new Entity { ["new_name"] = (string?)r!["name"] ?? (string?)r["code"] };
            if ((string?)r["code"] is { } code) row["new_code"] = code;
            if ((decimal?)r["amount"] is { } amount) row["new_amount"] = amount;
            if ((int?)r["qty"] is { } qty) row["new_qty"] = qty;
            if ((string?)r["account"] is { } account)
            {
                if (!accounts.TryGetValue(account, out var id))
                    accounts[account] = id = await Admin.CreateAsync(new Entity("account") { ["name"] = account });
                row["new_accountid"] = new EntityReference("account", id);
            }
            var e = await CreateAndReadAsync(table, row);

            foreach (var name in definitions.Keys)
            {
                var expected = r["fx" + name["new_fx".Length..]];
                var because = $"{name} = {definitions[name]} on {row["new_name"]}";
                switch (expected)
                {
                    case null:
                        e.Attributes.Should().NotContainKey(name, because);
                        break;
                    case JsonValue v when v.TryGetValue<string>(out var text):
                        ValueOf(e, name).Should().Be(text, because);
                        break;
                    default:
                        NumberOf(e, name, because).Should().Be(expected.GetValue<decimal>(), because);
                        break;
                }
            }

            var key = (string?)r["code"] ?? (string)r["name"]!;
            laterRows.Should().ContainKey(key);
            for (var i = 1; i < laterColumns.Length; i++)
            {
                var name = "new_" + laterColumns[i];
                var because = $"{name} = {later[name]} on {row["new_name"]}";
                NumberOf(e, name, because).Should().Be(laterRows[key][i]!.GetValue<decimal>(), because);
            }
        }
        laterRows.Should().HaveCount(rows.Count);

        // A null value must not pass for 0: the column is present and non-null before it is converted.
        static decimal NumberOf(Entity e, string name, string because)
        {
            e.Attributes.Should().ContainKey(name, because);
            var value = ValueOf(e, name);
            value.Should().NotBeNull(because);
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        // The definitions Dataverse refused: Weekday of a User local date, and the lookup's logical name.
        (await FluentActions.Awaiting(() => CreateAttributeAsync(table, Formula(new IntegerAttributeMetadata { LogicalName = "new_fxweek" },
                "Weekday(new_startedon)")))
            .Should().ThrowAsync<Exception>()).WithMessage($"The formula column '{table}.new_fxweek' uses Weekday on a User local date*");
        (await FluentActions.Awaiting(() => CreateAttributeAsync(table, Formula(new StringAttributeMetadata { LogicalName = "new_fxacc2", MaxLength = 200 },
                "new_accountid.name")))
            .Should().ThrowAsync<Exception>()).WithMessage($"The formula column '{table}.new_fxacc2' reads 'new_accountid.name' through the " +
                "lookup's logical name or display name*use 'new_AccountId.name'.");
    }

    // ---- functions ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Functions_compute_power_fx_values()
    {
        var table = await TableAsync("fn");
        var due = new DateTime(2026, 11, 15); // a Sunday
        // Date parts read a Date only column: the Power Fx check refuses a Time zone independent one in Weekday.
        await CreateAttributeAsync(table, new DateTimeAttributeMetadata
        {
            LogicalName = "new_day", SchemaName = "new_Day", DisplayName = Label("Day"),
            DateTimeBehavior = DateTimeBehavior.DateOnly, Format = DateTimeFormat.DateOnly,
        });
        var columns = new (AttributeMetadata Column, string Formula, object? Expected)[]
        {
            (new DecimalAttributeMetadata { LogicalName = "new_fmod" }, "Mod(new_qty, 3)", 2m),
            (new DecimalAttributeMetadata { LogicalName = "new_fmodneg" }, "Mod(-new_qty, -3)", -2m),
            (new DecimalAttributeMetadata { LogicalName = "new_fround" }, "Round(new_amount, 0)", 3m),
            (new DecimalAttributeMetadata { LogicalName = "new_froundneg" }, "Round(-new_amount, 0)", -3m),
            (new DecimalAttributeMetadata { LogicalName = "new_fup" }, "RoundUp(new_amount * 1.01, 1)", 2.6m),
            (new DecimalAttributeMetadata { LogicalName = "new_fdown" }, "RoundDown(new_amount - 5.09, 1)", -2.5m),
            (new DecimalAttributeMetadata { LogicalName = "new_fint" }, "Int(-new_amount)", -3m),
            (new DecimalAttributeMetadata { LogicalName = "new_ftrunc" }, "Trunc(-new_amount)", -2m),
            (new DecimalAttributeMetadata { LogicalName = "new_fmax" }, "Max(new_qty, new_amount, 1)", 2.5m),
            (new DecimalAttributeMetadata { LogicalName = "new_favg" }, "Average(new_qty, new_amount)", -2.25m),
            (new DecimalAttributeMetadata { LogicalName = "new_fval" }, "Value(new_code)", null),
            (new DecimalAttributeMetadata { LogicalName = "new_fval2" }, "Value(Left(new_code, 0) & \" 12.5\")", 12.5m),
            (new IntegerAttributeMetadata { LogicalName = "new_flen" }, "Len(new_code)", 8),
            (new IntegerAttributeMetadata { LogicalName = "new_fwd" }, "Weekday(new_day)", 1),
            (new IntegerAttributeMetadata { LogicalName = "new_fwk" }, "WeekNum(new_day)",
                CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(due, CalendarWeekRule.FirstDay, DayOfWeek.Sunday)),
            (new IntegerAttributeMetadata { LogicalName = "new_fiso" }, "ISOWeekNum(new_day)", ISOWeek.GetWeekOfYear(due)),
            (new IntegerAttributeMetadata { LogicalName = "new_fdiff" },
                "DateDiff(new_day, DateAdd(new_day, 40, TimeUnit.Days), TimeUnit.Months)", 1),
            (new StringAttributeMetadata { LogicalName = "new_ftrim", MaxLength = 100 }, "Trim(new_code)", "a b"),
            (new StringAttributeMetadata { LogicalName = "new_fmid", MaxLength = 100 }, "Mid(new_name, 2, 3)", "bcd"),
            (new StringAttributeMetadata { LogicalName = "new_fsub", MaxLength = 100 }, "Substitute(new_name & \"-abc\", \"b\", \"X\", 2)", "abcdef-aXc"),
            (new StringAttributeMetadata { LogicalName = "new_fsuball", MaxLength = 100 }, "Substitute(Upper(new_name), \"b\", \"x\")", "ABCDEF"),
            (new StringAttributeMetadata { LogicalName = "new_frep", MaxLength = 100 }, "Replace(new_name, 2, 3, \"XY\")", "aXYef"),
            (new StringAttributeMetadata { LogicalName = "new_ftext", MaxLength = 100 }, "Text(new_qty, \"000\")", "-007"),
            (new StringAttributeMetadata { LogicalName = "new_ftext2", MaxLength = 100 }, "Text(new_qty + 130, \"0-0\")", "12-3"),
            (new StringAttributeMetadata { LogicalName = "new_ftext3", MaxLength = 100 }, "Text(new_qty + 7, \"#\")", ""),
            (new DoubleAttributeMetadata { LogicalName = "new_fpow" }, "Power(new_amount, 2)", 6.25d),
            (new DoubleAttributeMetadata { LogicalName = "new_fsqrt" }, "Sqrt(new_qty)", null),
            (new DoubleAttributeMetadata { LogicalName = "new_fln" }, "Ln(new_qty)", null),
            (YesNo("new_fstarts"), "StartsWith(new_name, \"ABC\")", true),
            (YesNo("new_fin"), "\"CD\" in new_name", true),
            (YesNo("new_fexactin"), "\"CD\" exactin new_name", false),
            (YesNo("new_fblank"), "IsBlank(new_code)", false),
        };
        foreach (var (column, formula, _) in columns)
        {
            if (column is DecimalAttributeMetadata d) d.Precision = 2;
            await CreateAttributeAsync(table, Formula(column, formula));
        }

        var e = await CreateAndReadAsync(table, new Entity
        {
            ["new_name"] = "abcdef", ["new_code"] = "  a   b ", ["new_qty"] = -7, ["new_amount"] = 2.5m, ["new_day"] = due,
            ["new_startedon"] = new DateTime(2026, 11, 15, 23, 0, 0, DateTimeKind.Utc),
        });

        foreach (var (column, formula, expected) in columns)
            ValueOf(e, column.LogicalName).Should().Be(expected, "{0} = {1}", column.LogicalName, formula);
    }

    [Fact]
    public async Task Out_of_range_values_are_null_not_sql_errors()
    {
        // A DateAdd past 9999-12-31 (SQL error 517), a constant outside int (8115) and Hour of UTCToday() (9810 on a date) would
        // fail the whole query: they give null, or the Power Fx value.
        var table = await TableAsync("ov");
        await CreateAttributeAsync(table, Formula(new DateTimeAttributeMetadata
        {
            LogicalName = "new_fxlater", DateTimeBehavior = DateTimeBehavior.UserLocal, Format = DateTimeFormat.DateAndTime,
        }, "DateAdd(new_startedon, new_qty, TimeUnit.Years)"));
        await CreateAttributeAsync(table, Formula(new StringAttributeMetadata { LogicalName = "new_fxchar", MaxLength = 10 }, "Char(3000000000)"));
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxround", Precision = 2 }, "Round(new_amount, 3000000000)"));
        await CreateAttributeAsync(table, Formula(new IntegerAttributeMetadata { LogicalName = "new_fxhour" }, "Hour(UTCToday())"));
        var started = new DateTime(2026, 11, 15, 23, 0, 0, DateTimeKind.Utc);

        var far = await CreateAndReadAsync(table, new Entity { ["new_startedon"] = started, ["new_qty"] = 10000, ["new_amount"] = 2.5m });
        var near = await CreateAndReadAsync(table, new Entity { ["new_startedon"] = started, ["new_qty"] = -2025 });

        far.Attributes.Should().NotContainKey("new_fxlater");
        far.Attributes.Should().NotContainKey("new_fxchar");
        far.Attributes.Should().NotContainKey("new_fxround");
        ValueOf(far, "new_fxhour").Should().Be(0);
        ValueOf(near, "new_fxlater").Should().Be(new DateTime(1, 11, 15, 23, 0, 0));

        // A Substitute instance ComputedSql cannot write is refused when the column is defined, not when a row is read.
        (await FluentActions.Awaiting(() => CreateAttributeAsync(table,
                Formula(new StringAttributeMetadata { LogicalName = "new_fxsub", MaxLength = 100 }, "Substitute(new_code, \"a\", \"b\", new_qty)")))
            .Should().ThrowAsync<Exception>()).WithMessage(
                $"The formula column '{table}.new_fxsub' uses Substitute with an instance that is not a whole number from 1 to 100*");
    }

    [Fact]
    public async Task DateAdd_past_the_last_day_and_a_float_past_the_largest_are_null()
    {
        // Plan 2.7a, Task 7 fix round 1 (review Minor 3, 4), on either engine: DateAdd one day from the last day of 9999 is null (on
        // PostgreSQL the edge has six fractional digits: seven round up to year 10000), and Float of a text past the largest double
        // is null, as TRY_CAST gives (PostgreSQL's CAST errors).
        var table = await TableAsync("ed");
        await CreateAttributeAsync(table, Formula(new DateTimeAttributeMetadata
        {
            LogicalName = "new_fxnext", DateTimeBehavior = DateTimeBehavior.UserLocal, Format = DateTimeFormat.DateAndTime,
        }, "DateAdd(new_startedon, 1, TimeUnit.Days)"));
        await CreateAttributeAsync(table, Formula(new DoubleAttributeMetadata { LogicalName = "new_fxfloat" }, "Float(new_code)"));

        var last = await CreateAndReadAsync(table, new Entity
        {
            ["new_startedon"] = new DateTime(9999, 12, 31, 10, 0, 0, DateTimeKind.Utc), ["new_code"] = "1e400",
        });
        var negative = await CreateAndReadAsync(table, new Entity { ["new_code"] = "-1e400" });
        var day = await CreateAndReadAsync(table, new Entity
        {
            ["new_startedon"] = new DateTime(9999, 12, 30, 10, 0, 0, DateTimeKind.Utc), ["new_code"] = " 1.5e3 ",
        });

        last.Attributes.Should().NotContainKey("new_fxnext");
        last.Attributes.Should().NotContainKey("new_fxfloat");
        negative.Attributes.Should().NotContainKey("new_fxfloat");
        ValueOf(day, "new_fxnext").Should().Be(new DateTime(9999, 12, 31, 10, 0, 0, DateTimeKind.Utc));
        ValueOf(day, "new_fxfloat").Should().Be(1500d);
    }

    [Fact]
    public async Task IsBlank_of_a_space_is_false()
    {
        // T-SQL: N' ' = N'' is true (trailing spaces); Power Fx: IsBlank(" ") is false, IsBlank("") is true.
        var table = await TableAsync("sp");
        await CreateAttributeAsync(table, Formula(YesNo("new_fxblank"), "IsBlank(new_code)"));
        await CreateAttributeAsync(table, Formula(new IntegerAttributeMetadata { LogicalName = "new_fxlen" }, "Len(new_code)"));

        var space = await CreateAndReadAsync(table, new Entity { ["new_code"] = " " });
        var none = await CreateAndReadAsync(table, new Entity { ["new_name"] = "none" });

        ValueOf(space, "new_fxlen").Should().Be(1, "the space is stored");
        ValueOf(space, "new_fxblank").Should().Be(false);
        ValueOf(none, "new_fxblank").Should().Be(true);
    }

    // ---- size and nesting (final review C1, I1) -------------------------------------------------------------------------

    [Fact]
    public async Task Deeply_nested_formulas_compute_on_sql_server()
    {
        // I1: If nested more than 10 deep wrote CASE nested as deep, SQL Server error 125 on every read; C1: a blank-safe '=' of
        // nested values, a nested Switch and a chain whose links read the previous one 4 times gave SQL growing exponentially.
        var table = await TableAsync("dp");
        var depths = Enumerable.Range(11, 5).ToArray();
        foreach (var depth in depths)
        {
            // If(qty > 0, If(qty > 1, … If(qty > depth - 1, 100, depth - 1) …, 1), 0): qty when it is below depth, 100 above.
            var nested = "100";
            for (var i = depth - 1; i >= 0; i--) nested = $"If(new_qty>{i},{nested},{i})";
            await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = $"new_fxif{depth}", Precision = 2 }, nested));
        }
        // Switch nested 10 deep mapping 1 → 2 → 3 → 4 → 1: ten steps from 1 end on 3; any other value is blank.
        var cycle = "new_qty";
        for (var i = 0; i < 10; i++) cycle = $"Switch({cycle},1,2,2,3,3,4,4,1)";
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxswitch", Precision = 2 }, cycle));
        // A yes/no value used as a condition, nested 12 deep.
        var yesNo = "new_qty>0";
        for (var i = 0; i < 12; i++) yesNo = $"Not(If({yesNo},new_qty>{i},false))";
        await CreateAttributeAsync(table, Formula(YesNo("new_fxyesno"), yesNo));
        // The blank-safe '=' of nullable values, 20 deep (571 characters: an OutOfMemoryException before the fix).
        var equal = "If(new_amount=new_amount,new_amount)";
        for (var i = 1; i < 20; i++) equal = $"If({equal}=new_amount,new_amount)";
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxequal", Precision = 2 }, equal));
        // 40 sums, each kept in range by an apply that reads it twice: SQL Server found no plan within minutes when it wrote each
        // apply's value into its readers (2^40), before the applies became aggregates.
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxsum", Precision = 2 },
            string.Join("+", Enumerable.Repeat("new_qty", 40))));
        // A chain of 10 formula columns, each reading the previous one 4 times: amount × 2^10 (1.5 × 2, then doubled 9 times).
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxc1", Precision = 2 }, "new_amount*2"));
        for (var i = 2; i <= 10; i++)
            await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = $"new_fxc{i}", Precision = 2 },
                $"If(new_fxc{i - 1}=new_fxc{i - 1}, new_fxc{i - 1}+new_fxc{i - 1})"));

        foreach (var qty in new[] { 1, 7, 20 })
        {
            var e = await CreateAndReadAsync(table, new Entity { ["new_name"] = $"q{qty}", ["new_qty"] = qty, ["new_amount"] = 1.5m });

            foreach (var depth in depths)
                ValueOf(e, $"new_fxif{depth}").Should().Be(qty < depth ? qty : 100m, "If nested {0} deep, qty {1}", depth, qty);
            ValueOf(e, "new_fxswitch").Should().Be(qty == 1 ? 3m : null, "qty {0}", qty);
            var expected = qty > 0;
            for (var i = 0; i < 12; i++) expected = !(expected && qty > i);
            ValueOf(e, "new_fxyesno").Should().Be(expected, "qty {0}", qty);
            ValueOf(e, "new_fxequal").Should().Be(1.5m);
            ValueOf(e, "new_fxc10").Should().Be(1536m);
            ValueOf(e, "new_fxsum").Should().Be(40m * qty);
        }

        // A query filtering and sorting on the deepest columns runs too.
        var query = new QueryExpression(table) { ColumnSet = new ColumnSet("new_fxif15", "new_fxc10") };
        query.Criteria.AddCondition("new_fxif15", ConditionOperator.LessThan, 50m);
        query.Criteria.AddCondition("new_fxc10", ConditionOperator.Equal, 1536m);
        query.AddOrder("new_fxif15", OrderType.Descending);
        (await Admin.RetrieveMultipleAsync(query)).Entities.Select(r => r.GetAttributeValue<decimal>("new_fxif15")).Should().Equal(7m, 1m);
    }

    [Fact]
    public async Task Intermediate_values_are_bounded_on_sql_server()
    {
        // Final review N1: SQL Server's compile time grows faster than the applies of a query, and nothing bounded them: 29
        // Substitute(…, 100) (985 characters, 2,901 applies) ran SQL Server out of stack space after 3 minutes on every read.
        var table = await TableAsync("ap");
        var substitutes = string.Join("&", Enumerable.Repeat("Substitute(new_code,\"a\",\"b\",100)", 29));
        await CreateAttributeAsync(table, Formula(new StringAttributeMetadata { LogicalName = "new_fxsubs", MaxLength = 1000 }, substitutes));
        // The instance of Substitute, now one value however large: the second "a", the second "an", the last, one past it, and
        // a match that does not overlap the one before ("aa" twice in "aaaa").
        foreach (var (column, formula) in new[]
                 {
                     ("new_fxsub2", "Substitute(new_code,\"a\",\"X\",2)"), ("new_fxsuban", "Substitute(new_code,\"an\",\"_\",2)"),
                     ("new_fxsub3", "Substitute(new_code,\"a\",\"X\",3)"), ("new_fxsub4", "Substitute(new_code,\"a\",\"X\",4)"),
                     ("new_fxsubaa", "Substitute(new_code & \"-aaaa\",\"aa\",\"_\",3)"), ("new_fxsubnone", "Substitute(new_code,\"\",\"X\",1)"),
                 })
            await CreateAttributeAsync(table, Formula(new StringAttributeMetadata { LogicalName = column, MaxLength = 100 }, formula));

        // A chain of sums: each '+' of a decimal sum is computed once (the range of its intermediates), and a chain adds the
        // values of its links. new_fxs3 holds just under 250; new_fxs4 would pass it and is refused when it is defined.
        string Terms(int n) => string.Join("+", Enumerable.Repeat("new_qty", n));
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxs1", Precision = 2 }, Terms(86)));
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxs2", Precision = 2 }, "new_fxs1+" + Terms(85)));
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxs3", Precision = 2 }, "new_fxs2+" + Terms(UnderTheBound)));
        (await FluentActions.Awaiting(() => CreateAttributeAsync(table,
                Formula(new DecimalAttributeMetadata { LogicalName = "new_fxs4", Precision = 2 }, "new_fxs3+" + Terms(20))))
            .Should().ThrowAsync<Exception>()).WithMessage(
                $"The formula column '{table}.new_fxs4' is too complex to compute: its SQL would need more than 250 intermediate values; simplify the formula.");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var e = await CreateAndReadAsync(table, new Entity { ["new_code"] = "banana", ["new_qty"] = 2 });
        watch.Stop();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the columns compile within seconds");
        ValueOf(e, "new_fxsubs").Should().Be(string.Concat(Enumerable.Repeat("banana", 29)));
        ValueOf(e, "new_fxsub2").Should().Be("banXna");
        ValueOf(e, "new_fxsuban").Should().Be("ban_a");
        ValueOf(e, "new_fxsub3").Should().Be("bananX");
        ValueOf(e, "new_fxsub4").Should().Be("banana");
        ValueOf(e, "new_fxsubaa").Should().Be("banana-aaaa", "two matches of \"aa\" in \"aaaa\", not three");
        ValueOf(e, "new_fxsubnone").Should().Be("banana");
        ValueOf(e, "new_fxs3").Should().Be(2m * (86 + 85 + UnderTheBound));

        // Final review M2: a change of a column re-checks the formula columns that read it, so that new_fxs3 cannot pass the bound
        // through new_fxs1.
        (await FluentActions.Awaiting(() => Admin.ExecuteAsync(new UpdateAttributeRequest
            {
                EntityName = table, Attribute = Formula(new DecimalAttributeMetadata { LogicalName = "new_fxs1", Precision = 2 }, Terms(100)),
            }))
            .Should().ThrowAsync<Exception>()).WithMessage(
                $"The change of '{table}.new_fxs1' would make the formula column '{table}.new_fxs3' invalid: " +
                $"The formula column '{table}.new_fxs3' is too complex to compute: its SQL would need more than 250 intermediate values*");
        ValueOf(await Admin.RetrieveAsync(table, e.Id, new ColumnSet("new_fxs3")), "new_fxs3").Should().Be(2m * (86 + 85 + UnderTheBound));
    }

    /// <summary>The terms of <c>new_fxs3</c> in <see cref="Intermediate_values_are_bounded_on_sql_server"/>: its applies just under 250.</summary>
    private const int UnderTheBound = 73;
}
