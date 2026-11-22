using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Pipeline;
using Xrmd.Workflows.Engine;
using Xrmd.Workflows.Xaml;
using static Xrmd.Workflows.Xaml.ValueExpr;

namespace Xrmd.Workflows.Tests;

/// <summary>
/// Plan 2.14, Task 4 (W16): the values and conditions of the step model, evaluated over a fake record reader. Only the shapes of
/// the step model are evaluated; a value the run cannot treat faithfully fails explicitly.
/// </summary>
public class ExpressionEvaluatorTests
{
    private static readonly RecordRef.Primary Primary = new();
    private static readonly Guid AccountId = Guid.Parse("6f0c7a3e-2b1d-4c5e-9f8a-0d1e2f3a4b5c");

    private static WorkflowRun Run() => new()
    {
        Workflow = new WorkflowRow { Name = "Evaluator test" },
        Primary = new EntityReference("account", AccountId),
        Caller = Caller.Internal,
        Invocation = new InvocationContext(Caller.Internal, Guid.Empty, 1, null, null, Guid.NewGuid()),
    };

    /// <summary>A reader over the columns of the primary record (and of the records its lookups point to, by lookup name).</summary>
    private static Func<RecordRef, string, CancellationToken, Task<object?>> Reader(
        Dictionary<string, object?> primary, Dictionary<string, Dictionary<string, object?>>? related = null) =>
        (record, attribute, _) => Task.FromResult(record switch
        {
            RecordRef.Primary => primary.GetValueOrDefault(attribute),
            RecordRef.Related r => related?.GetValueOrDefault(r.LookupAttribute)?.GetValueOrDefault(attribute),
            _ => null,
        });

    private static Task<object?> EvaluateAsync(ValueExpr e, Dictionary<string, object?>? primary = null,
        Dictionary<string, Dictionary<string, object?>>? related = null, WorkflowRun? run = null) =>
        ExpressionEvaluator.Evaluate(e, Reader(primary ?? [], related), run ?? Run(), CancellationToken.None);

    private static Task<bool> ConditionAsync(ConditionExpr c, Dictionary<string, object?> primary) =>
        ConditionEvaluator.EvaluateAsync(c, Reader(primary), Run(), CancellationToken.None);

    // ---- arithmetic ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Add_days_to_date()
    {
        var due = new DateTime(2026, 11, 13, 9, 30, 0, DateTimeKind.Utc);
        var columns = new Dictionary<string, object?> { ["duedate"] = due };

        (await EvaluateAsync(new Arithmetic("Add", [new AttributeOf(Primary, "duedate"), new Literal("Decimal", 3m)]), columns))
            .Should().Be(due.AddDays(3));
        (await EvaluateAsync(new Arithmetic("Subtract", [new AttributeOf(Primary, "duedate"), new Literal("Integer", 2)]), columns))
            .Should().Be(due.AddDays(-2));
    }

    [Fact]
    public async Task Increment_integer_and_decimal()
    {
        var columns = new Dictionary<string, object?>
        {
            ["numberofemployees"] = 5, ["xrmv_amount"] = 1.5m, ["creditlimit"] = new Money(100m),
        };

        // The result keeps the column's own type: an Integer stays an int, a Money stays a Money.
        (await EvaluateAsync(new Arithmetic("Increment", [new AttributeOf(Primary, "numberofemployees"), new Literal("Decimal", 2m)]), columns))
            .Should().Be(7);
        (await EvaluateAsync(new Arithmetic("Increment", [new AttributeOf(Primary, "xrmv_amount"), new Literal("Decimal", 2.25m)]), columns))
            .Should().Be(3.75m);
        (await EvaluateAsync(new Arithmetic("Decrement", [new AttributeOf(Primary, "numberofemployees"), new Literal("Integer", 1)]), columns))
            .Should().Be(4);
        (await EvaluateAsync(new Arithmetic("Multiply", [new AttributeOf(Primary, "creditlimit"), new Literal("Decimal", 1.5m)]), columns))
            .Should().BeOfType<Money>().Which.Value.Should().Be(150m);
        (await EvaluateAsync(new Arithmetic("Set", [new Literal("Integer", 42)]), columns)).Should().Be(42);
    }

    [Fact]
    public async Task Integer_result_that_is_not_whole_fails()
    {
        var columns = new Dictionary<string, object?> { ["numberofemployees"] = 5 };

        var act = () => EvaluateAsync(new Arithmetic("Increment", [new AttributeOf(Primary, "numberofemployees"), new Literal("Decimal", 0.5m)]), columns);

        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Message.Should().Contain("5.5").And.Contain("Int32");
    }

    [Fact]
    public async Task Null_attribute_in_arithmetic_is_null()
    {
        var columns = new Dictionary<string, object?> { ["numberofemployees"] = null };

        (await EvaluateAsync(new Arithmetic("Add", [new AttributeOf(Primary, "numberofemployees"), new Literal("Integer", 1)]), columns))
            .Should().BeNull();
        (await EvaluateAsync(new Arithmetic("Add", [new Literal("Integer", 1), new AttributeOf(Primary, "numberofemployees")]), columns))
            .Should().BeNull();
    }

    [Fact]
    public async Task Arithmetic_on_text_fails()
    {
        var act = () => EvaluateAsync(new Arithmetic("Add", [new Literal("String", "a"), new Literal("Integer", 1)]));

        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Message.Should().Contain("Add").And.Contain("System.String");
    }

    [Fact]
    public async Task Overflow_in_arithmetic_fails_explicitly()
    {
        var columns = new Dictionary<string, object?>
        {
            ["big"] = decimal.MaxValue, ["huge"] = double.MaxValue, ["date"] = new DateTime(9999, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        foreach (var e in new ValueExpr[]
                 {
                     new Arithmetic("Add", [new AttributeOf(Primary, "big"), new Literal("Decimal", 1m)]),
                     new Arithmetic("Multiply", [new AttributeOf(Primary, "big"), new Literal("Decimal", 2m)]),
                     new Arithmetic("Add", [new Literal("Decimal", 1m), new AttributeOf(Primary, "huge")]),
                     new Arithmetic("Add", [new AttributeOf(Primary, "date"), new Literal("Integer", 365)]),
                 })
        {
            var act = () => EvaluateAsync(e, columns);
            var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
            (fault.HttpStatus, fault.Message.Contains("out of range", StringComparison.Ordinal)).Should().Be((400, true), e.ToString());
        }
    }

    [Fact]
    public async Task Long_first_operand_keeps_long()
    {
        var columns = new Dictionary<string, object?> { ["count"] = 5L };

        (await EvaluateAsync(new Arithmetic("Increment", [new AttributeOf(Primary, "count"), new Literal("Decimal", 2m)]), columns))
            .Should().BeOfType<long>().Which.Should().Be(7L);
    }

    // ---- text -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Concat_text_and_lookup_name()
    {
        var related = new Dictionary<string, Dictionary<string, object?>>
        {
            ["parentaccountid"] = new() { ["primarycontactid"] = new EntityReference("contact", Guid.NewGuid()) { Name = "Ada Example" } },
        };
        var columns = new Dictionary<string, object?> { ["name"] = "Contoso", ["description"] = null };

        var value = await EvaluateAsync(new Concat(
        [
            new Literal("String", "Call "), new AttributeOf(new RecordRef.Related("parentaccountid"), "primarycontactid"),
            new Literal("String", " of "), new AttributeOf(Primary, "name"), new AttributeOf(Primary, "description"),
        ]), columns, related);

        value.Should().Be("Call Ada Example of Contoso");
    }

    [Fact]
    public async Task Concat_of_a_number_fails_explicitly()
    {
        // Dataverse formats numbers with the user's settings: not replicated, so refused rather than guessed.
        var act = () => EvaluateAsync(new Concat([new Literal("String", "n="), new Literal("Integer", 3)]));

        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Message.Should().Contain("System.Int32");
    }

    [Fact]
    public async Task Concat_of_only_null_parts_is_null()
    {
        var columns = new Dictionary<string, object?> { ["description"] = null };

        (await EvaluateAsync(new Concat([new AttributeOf(Primary, "description"), new Literal("Null", null)]), columns)).Should().BeNull();
        (await EvaluateAsync(new Concat([new AttributeOf(Primary, "description"), new Literal("String", "")]), columns)).Should().Be("");
    }

    // ---- reads, TargetType and the two conversions ------------------------------------------------------------------------

    [Fact]
    public async Task Money_read_as_decimal_fails_naming_column_and_types()
    {
        var columns = new Dictionary<string, object?> { ["creditlimit"] = new Money(10m) };

        var act = () => EvaluateAsync(new AttributeOf(Primary, "creditlimit") { TargetType = "System.Decimal" }, columns);

        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Message
            .Should().Contain("creditlimit").And.Contain("Microsoft.Xrm.Sdk.Money").And.Contain("System.Decimal");
    }

    [Fact]
    public async Task Read_with_its_own_type_as_target_type_is_the_value()
    {
        var columns = new Dictionary<string, object?> { ["xrmv_amount"] = 12.5m, ["description"] = null };

        (await EvaluateAsync(new AttributeOf(Primary, "xrmv_amount") { TargetType = "System.Decimal" }, columns)).Should().Be(12.5m);
        (await EvaluateAsync(new AttributeOf(Primary, "description") { TargetType = "System.Decimal" }, columns)).Should().BeNull();
    }

    [Fact]
    public async Task Primary_key_converts_to_a_reference_of_its_entity()
    {
        var columns = new Dictionary<string, object?> { ["xrmv_orderid"] = AccountId };

        var value = await EvaluateAsync(new Conversion("System.Guid", "Microsoft.Xrm.Sdk.EntityReference",
            new AttributeOf(Primary, "xrmv_orderid"), "xrmv_order"), columns);

        value.Should().BeOfType<EntityReference>().Which.Should().BeEquivalentTo(new { LogicalName = "xrmv_order", Id = AccountId });
    }

    [Fact]
    public async Task Reference_converts_to_one_activity_party()
    {
        var contact = new EntityReference("contact", Guid.NewGuid());
        var columns = new Dictionary<string, object?> { ["primarycontactid"] = contact, ["empty"] = null };
        const string reference = "Microsoft.Xrm.Sdk.EntityReference", collection = "Microsoft.Xrm.Sdk.EntityCollection";

        var value = await EvaluateAsync(new Conversion(reference, collection,
            new AttributeOf(Primary, "primarycontactid") { TargetType = reference }), columns);

        var party = value.Should().BeOfType<EntityCollection>().Which.Entities.Should().ContainSingle().Subject;
        party.LogicalName.Should().Be("activityparty");
        party.GetAttributeValue<EntityReference>("partyid").Should().Be(contact);
        (await EvaluateAsync(new Conversion(reference, collection, new AttributeOf(Primary, "empty") { TargetType = reference }), columns))
            .Should().BeNull();
    }

    [Fact]
    public async Task Conversion_of_another_type_fails_naming_the_pair()
    {
        var columns = new Dictionary<string, object?> { ["name"] = "Contoso" };

        var act = () => EvaluateAsync(new Conversion("System.Guid", "Microsoft.Xrm.Sdk.EntityReference", new AttributeOf(Primary, "name"), "account"), columns);

        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Message.Should().Contain("System.String").And.Contain("System.Guid");
    }

    [Fact]
    public async Task Literals_are_sdk_values()
    {
        var id = Guid.NewGuid();
        (await EvaluateAsync(new Literal("OptionSetValue", 3))).Should().Be(new OptionSetValue(3));
        (await EvaluateAsync(new Literal("Money", 2.5m))).Should().Be(new Money(2.5m));
        (await EvaluateAsync(new Literal("EntityReference", new EntityRefValue("systemuser", id)))).Should().Be(new EntityReference("systemuser", id));
        (await EvaluateAsync(new Literal("Null", null))).Should().BeNull();
        (await EvaluateAsync(new FirstNonNull([new Literal("Null", null), new Literal("String", "x")]))).Should().Be("x");
    }

    [Fact]
    public async Task DirectCast_keeps_the_value_and_a_null_value_type_is_its_default()
    {
        var columns = new Dictionary<string, object?> { ["xrmv_amount"] = 4m, ["empty"] = null };

        (await EvaluateAsync(new Cast("System.Decimal", new AttributeOf(Primary, "xrmv_amount")), columns)).Should().Be(4m);
        (await EvaluateAsync(new Cast("System.Decimal", new AttributeOf(Primary, "empty")), columns)).Should().Be(0m);
        (await EvaluateAsync(new Cast("System.String", new AttributeOf(Primary, "empty")), columns)).Should().BeNull();
        var act = () => EvaluateAsync(new Cast("System.String", new AttributeOf(Primary, "xrmv_amount")), columns);
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Message.Should().Contain("System.Decimal").And.Contain("System.String");
    }

    [Fact]
    public async Task DirectCast_of_null_to_an_unlisted_value_type_fails()
    {
        var act = () => EvaluateAsync(new Cast("System.Int64", new Literal("Null", null)));

        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Message.Should().Contain("System.Int64");
        (await EvaluateAsync(new Cast("Microsoft.Xrm.Sdk.Money", new Literal("Null", null)))).Should().BeNull();
    }

    [Fact]
    public async Task Variables_are_read_from_the_run()
    {
        var run = Run();
        run.Variables["Increment"] = 2m;

        (await EvaluateAsync(new VariableRef("Increment"), run: run)).Should().Be(2m);
        (await EvaluateAsync(new VariableRef("Missing"), run: run)).Should().BeNull();
    }

    [Fact]
    public async Task Execution_time_is_the_run_time()
    {
        var run = Run();

        (await EvaluateAsync(new ExecutionTime(), run: run)).Should().Be(run.Now);
    }

    // ---- conditions -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Equal_on_optionset_value()
    {
        var columns = new Dictionary<string, object?> { ["industrycode"] = new OptionSetValue(2), ["empty"] = null };

        (await ConditionAsync(new ConditionExpr.Compare("Equal", new AttributeOf(Primary, "industrycode"), new Literal("OptionSetValue", 2)), columns))
            .Should().BeTrue();
        (await ConditionAsync(new ConditionExpr.Compare("Equal", new AttributeOf(Primary, "industrycode"), new Literal("OptionSetValue", 3)), columns))
            .Should().BeFalse();
        (await ConditionAsync(new ConditionExpr.Compare("NotEqual", new AttributeOf(Primary, "industrycode"), new Literal("OptionSetValue", 3)), columns))
            .Should().BeTrue();
        (await ConditionAsync(new ConditionExpr.Compare("Equal", new AttributeOf(Primary, "empty"), new Literal("OptionSetValue", 3)), columns))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Contains_is_case_insensitive()
    {
        var columns = new Dictionary<string, object?> { ["name"] = "Contoso Pharmaceuticals", ["empty"] = null };

        ConditionExpr.Compare Text(string op, string column, string value) => new(op, new AttributeOf(Primary, column), new Literal("String", value));
        (await ConditionAsync(Text("Contains", "name", "PHARMA"), columns)).Should().BeTrue();
        (await ConditionAsync(Text("DoesNotContain", "name", "pharma"), columns)).Should().BeFalse();
        (await ConditionAsync(Text("BeginsWith", "name", "contoso"), columns)).Should().BeTrue();
        (await ConditionAsync(Text("EndsWith", "name", "CALS"), columns)).Should().BeTrue();
        (await ConditionAsync(Text("Equal", "name", "contoso pharmaceuticals"), columns)).Should().BeTrue();
        (await ConditionAsync(Text("Contains", "empty", "x"), columns)).Should().BeFalse();
        (await ConditionAsync(Text("DoesNotContain", "empty", "x"), columns)).Should().BeTrue();
    }

    [Fact]
    public async Task Between_dates()
    {
        var columns = new Dictionary<string, object?> { ["duedate"] = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc) };
        ConditionExpr Between(DateTime from, DateTime to) => new ConditionExpr.And(
            new ConditionExpr.Compare("GreaterEqual", new AttributeOf(Primary, "duedate"), new Literal("DateTime", from)),
            new ConditionExpr.Compare("LessEqual", new AttributeOf(Primary, "duedate"), new Literal("DateTime", to)));

        (await ConditionAsync(Between(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc)), columns))
            .Should().BeTrue();
        (await ConditionAsync(Between(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc)), columns))
            .Should().BeTrue();
        (await ConditionAsync(Between(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc)), columns))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Null_and_number_comparisons()
    {
        var columns = new Dictionary<string, object?> { ["xrmv_amount"] = 150m, ["creditlimit"] = new Money(5m), ["empty"] = null, ["blank"] = "" };

        (await ConditionAsync(new ConditionExpr.Compare("Null", new AttributeOf(Primary, "empty"), null), columns)).Should().BeTrue();
        (await ConditionAsync(new ConditionExpr.Compare("Null", new AttributeOf(Primary, "blank"), null), columns)).Should().BeTrue();
        (await ConditionAsync(new ConditionExpr.Compare("NotNull", new AttributeOf(Primary, "xrmv_amount"), null), columns)).Should().BeTrue();
        (await ConditionAsync(new ConditionExpr.Compare("GreaterThan", new AttributeOf(Primary, "xrmv_amount"), new Literal("Decimal", 100m)), columns))
            .Should().BeTrue();
        (await ConditionAsync(new ConditionExpr.Compare("LessThan", new AttributeOf(Primary, "creditlimit"), new Literal("Integer", 10)), columns))
            .Should().BeTrue();
        (await ConditionAsync(new ConditionExpr.Compare("GreaterThan", new AttributeOf(Primary, "empty"), new Literal("Integer", 10)), columns))
            .Should().BeFalse();
        (await ConditionAsync(new ConditionExpr.Or(
            new ConditionExpr.Compare("Null", new AttributeOf(Primary, "xrmv_amount"), null),
            new ConditionExpr.Not(new ConditionExpr.Compare("Null", new AttributeOf(Primary, "empty"), null))), columns)).Should().BeFalse();
    }

    [Fact]
    public async Task Comparing_text_with_a_number_fails()
    {
        var columns = new Dictionary<string, object?> { ["name"] = "Contoso" };

        var act = () => ConditionAsync(new ConditionExpr.Compare("GreaterThan", new AttributeOf(Primary, "name"), new Literal("Integer", 1)), columns);

        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Message.Should().Contain("GreaterThan");
    }
}
