using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Workflows.Background;

namespace Xrmd.Workflows.Tests;

/// <summary>
/// Plan 2.14 (W10): the data of a suspended background workflow job keeps every variable type the engine stores, exactly, through
/// <c>asyncoperation.data</c> (a host restart in between); anything else is refused rather than resumed with another value.
/// </summary>
public class WorkflowJobStateTests
{
    private static readonly Guid Activation = Guid.Parse("6d1bd6c2-0b1f-4a3e-9d43-2f9c0a5c1e01");
    private static readonly EntityReference Primary = new("account", Guid.Parse("a1f0c7e4-6a83-4f7b-8b21-49b1c9e2d702"));

    /// <summary>The values after <see cref="WorkflowJobState.ToJson"/>, the stored JSON, <see cref="WorkflowJobState.Parse"/> and restore.</summary>
    private static Dictionary<string, object?> RoundTrip(params (string Name, object? Value)[] variables)
    {
        var state = WorkflowJobState.Start(Activation, Primary) with
        {
            ResumePath = "1/2/0",
            Variables = WorkflowJobState.ToJson(variables.Select(v => KeyValuePair.Create(v.Name, v.Value)), "Test"),
        };
        var parsed = WorkflowJobState.Parse(state.Serialize());
        (parsed.ActivationId, parsed.Primary.LogicalName, parsed.Primary.Id, parsed.ResumePath)
            .Should().Be((Activation, Primary.LogicalName, Primary.Id, "1/2/0"));
        var restored = new Dictionary<string, object?>();
        parsed.RestoreInto(restored);
        return restored;
    }

    public static TheoryData<string, object?> Values() => new()
    {
        { "Null", null },
        { "String", "Ünïcode \"quoted\" text" },
        { "Empty string", "" },
        { "Boolean", true },
        { "Int32", int.MinValue },
        { "Int64", long.MaxValue },
        { "Decimal", 79228162514264337593543950335m },
        { "Decimal scale", 123.4500m },
        { "Double", 0.1 + 0.2 },
        { "Double small", double.Epsilon },
        { "Guid", Guid.Parse("0fe1b782-e5de-f111-84e8-002248872b66") },
        { "DateTime UTC", new DateTime(2026, 11, 13, 4, 5, 6, DateTimeKind.Utc).AddTicks(1234567) },
        { "DateTime unspecified", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified) },
        { "OptionSetValue", new OptionSetValue(100000001) },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void Value_round_trips_with_its_type(string name, object? value)
    {
        var restored = RoundTrip((name, value))[name];

        if (value is null)
        {
            restored.Should().BeNull();
            return;
        }
        restored.Should().BeOfType(value.GetType());
        switch (value)
        {
            case OptionSetValue o:
                ((OptionSetValue)restored!).Value.Should().Be(o.Value);
                break;
            case DateTime d:
                ((DateTime)restored!).Should().Be(d);
                ((DateTime)restored!).Kind.Should().Be(d.Kind);
                break;
            case decimal m:
                ((decimal)restored!).Should().Be(m);
                ((decimal)restored!).Scale.Should().Be(m.Scale);
                break;
            default:
                restored.Should().Be(value);
                break;
        }
    }

    [Fact]
    public void Money_round_trips_with_its_value()
    {
        RoundTrip(("Amount", new Money(1234.5678m)))["Amount"].Should().BeOfType<Money>().Which.Value.Should().Be(1234.5678m);
    }

    [Fact]
    public void Local_DateTime_is_kept_as_the_same_instant_in_UTC()
    {
        var local = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Local);

        var restored = (DateTime)RoundTrip(("When", local))["When"]!;

        restored.Kind.Should().Be(DateTimeKind.Utc);
        restored.Should().Be(local.ToUniversalTime());
    }

    [Fact]
    public void EntityReference_round_trips_without_its_name()
    {
        var reference = new EntityReference("contact", Guid.Parse("3e1f0307-d8de-f111-84e8-002248872b66")) { Name = "Ada" };

        var restored = RoundTrip(("Contact", reference))["Contact"].Should().BeOfType<EntityReference>().Subject;

        (restored.LogicalName, restored.Id).Should().Be(("contact", reference.Id));
        restored.Name.Should().BeNull("the name is display only: a later step reads the record by its id");
    }

    [Fact]
    public void Several_variables_round_trip_together()
    {
        var restored = RoundTrip(("a", "x"), ("b", 2), ("c", null), ("d", new OptionSetValue(1)));

        restored.Keys.Should().BeEquivalentTo("a", "b", "c", "d");
        (restored["a"], restored["b"], restored["c"]).Should().Be(("x", 2, (object?)null));
    }

    [Fact]
    public void Value_of_unkept_type_is_refused_naming_the_variable()
    {
        var act = () => WorkflowJobState.ToJson([KeyValuePair.Create<string, object?>("Record", new Entity("account"))], "Order follow-up");

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400
            && e.Message.Contains("Order follow-up") && e.Message.Contains("'Record'") && e.Message.Contains(nameof(Entity)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"ActivationId":"6d1bd6c2-0b1f-4a3e-9d43-2f9c0a5c1e01","Entity":"","Id":"a1f0c7e4-6a83-4f7b-8b21-49b1c9e2d702"}""")]
    public void Unreadable_data_is_refused(string data)
    {
        var act = () => WorkflowJobState.Parse(data);

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400 && e.Message.StartsWith("The data of the workflow job cannot be read"));
    }

    [Fact]
    public void Unknown_variable_type_is_refused_on_restore()
    {
        var data = """
            {"ActivationId":"6d1bd6c2-0b1f-4a3e-9d43-2f9c0a5c1e01","Entity":"account","Id":"a1f0c7e4-6a83-4f7b-8b21-49b1c9e2d702",
             "ResumePath":null,"Variables":{"v":{"Type":"Image","Value":"x"}}}
            """;

        var act = () => WorkflowJobState.Parse(data).RestoreInto(new Dictionary<string, object?>());

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400 && e.Message.Contains("'v'"));
    }
}
