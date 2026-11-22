using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline.CustomApis;

namespace Xrmd.Pipeline.Tests;

/// <summary>
/// <see cref="CustomApiValidator"/> (plan 15, C6 steps 1–4 and C7): the request of a custom API against its declared
/// parameters, and the response of its main operation against its declared properties.
/// </summary>
public class CustomApiValidatorTests
{
    private const int InvalidArgument = unchecked((int)0x80040203);
    private const int UnexpectedPluginException = unchecked((int)0x80040224);

    private static CustomApiDefinition Api(int binding = 0, string? bound = null, CustomApiField[]? requests = null,
        CustomApiField[]? responses = null) =>
        new(Guid.NewGuid(), "xt_Test", binding, bound, false, false, 2, null, null, requests ?? [], responses ?? []);

    private static CustomApiField Field(string name, CustomApiType type, bool optional = false, string? entity = null) =>
        new(name, type, entity, optional);

    private static OrganizationRequest Request(params (string Name, object? Value)[] parameters)
    {
        var request = new OrganizationRequest("xt_Test");
        foreach (var (name, value) in parameters) request.Parameters[name] = value;
        return request;
    }

    private static XrmdFaultException Refused(CustomApiDefinition api, OrganizationRequest request)
    {
        var act = () => CustomApiValidator.Validate(api, request);
        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(InvalidArgument);
        return fault;
    }

    /// <summary>For each type: a value of its SDK type, and a value of another type.</summary>
    public static TheoryData<CustomApiType, object, object> Types() => new()
    {
        { CustomApiType.Boolean, true, "true" },
        { CustomApiType.DateTime, new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc), "2026-11-10" },
        { CustomApiType.Decimal, 1.5m, 1.5d },
        { CustomApiType.Entity, new Entity("account"), new EntityReference("account", Guid.NewGuid()) },
        { CustomApiType.EntityCollection, new EntityCollection(), new[] { new Entity("account") } },
        { CustomApiType.EntityReference, new EntityReference("account", Guid.NewGuid()), new Entity("account") },
        { CustomApiType.Float, 1.5d, 1.5m },
        { CustomApiType.Integer, 7, 7L },
        { CustomApiType.Money, new Money(3m), 3m },
        { CustomApiType.Picklist, new OptionSetValue(1), 1 },
        { CustomApiType.String, "text", 5 },
        { CustomApiType.StringArray, new[] { "a", "b" }, "a,b" },
        { CustomApiType.Guid, Guid.NewGuid(), Guid.NewGuid().ToString() },
    };

    [Theory]
    [MemberData(nameof(Types))]
    public void Each_type_accepts_its_sdk_type_and_refuses_another(CustomApiType type, object right, object wrong)
    {
        var api = Api(requests: [Field("Value", type)]);

        CustomApiValidator.Validate(api, Request(("Value", right)));
        var fault = Refused(api, Request(("Value", wrong)));

        fault.Message.Should().Contain("Value").And.Contain("xt_Test");
    }

    [Fact]
    public void Missing_required_parameter_is_refused_and_optional_may_be_left_out()
    {
        var api = Api(requests: [Field("Required", CustomApiType.String), Field("Optional", CustomApiType.Integer, optional: true)]);

        CustomApiValidator.Validate(api, Request(("Required", "x")));
        Refused(api, Request(("Optional", 1))).Message.Should().Contain("Required");
    }

    [Fact]
    public void Null_for_a_required_parameter_is_refused_and_null_for_an_optional_one_is_accepted()
    {
        var api = Api(requests: [Field("Required", CustomApiType.String), Field("Optional", CustomApiType.Integer, optional: true)]);

        CustomApiValidator.Validate(api, Request(("Required", "x"), ("Optional", null)));
        Refused(api, Request(("Required", null))).Message.Should().Contain("Required");
    }

    [Fact]
    public void Undeclared_parameter_is_refused_but_tag_is_accepted()
    {
        var api = Api(requests: [Field("Text", CustomApiType.String)]);

        CustomApiValidator.Validate(api, Request(("Text", "x"), ("tag", "from-a-test")));
        Refused(api, Request(("Text", "x"), ("Other", "y"))).Message.Should().Contain("Other");
        // Parameter names are matched as the SDK matches them: exactly.
        Refused(api, Request(("Text", "x"), ("text", "y"))).Message.Should().Contain("'text'");
    }

    [Fact]
    public void Target_rules()
    {
        var bound = Api(binding: 1, bound: "account");
        var global = Api();
        var collection = Api(binding: 2, bound: "account");

        CustomApiValidator.Validate(bound, Request(("Target", new EntityReference("account", Guid.NewGuid()))));
        Refused(bound, Request()).Message.Should().Contain("Target").And.Contain("account");
        Refused(bound, Request(("Target", null))).Message.Should().Contain("Target");
        Refused(bound, Request(("Target", new Entity("account", Guid.NewGuid())))).Message.Should().Contain("Target");
        Refused(bound, Request(("Target", new EntityReference("contact", Guid.NewGuid())))).Message.Should().Contain("contact");
        // Target is the server's own parameter of an entity-bound API only.
        Refused(global, Request(("Target", new EntityReference("account", Guid.NewGuid())))).Message.Should().Contain("Target");
        Refused(collection, Request(("Target", new EntityReference("account", Guid.NewGuid())))).Message.Should().Contain("Target");
    }

    [Fact]
    public void Logical_entity_name_limits_the_records_a_parameter_takes()
    {
        var api = Api(requests:
        [
            Field("Ref", CustomApiType.EntityReference, optional: true, entity: "account"),
            Field("Record", CustomApiType.Entity, optional: true, entity: "account"),
            Field("Records", CustomApiType.EntityCollection, optional: true, entity: "account"),
        ]);

        CustomApiValidator.Validate(api, Request(("Ref", new EntityReference("account", Guid.NewGuid())), ("Record", new Entity("account")),
            ("Records", new EntityCollection([new Entity("account"), new Entity("account")]))));
        Refused(api, Request(("Ref", new EntityReference("contact", Guid.NewGuid())))).Message.Should().Contain("Ref").And.Contain("contact");
        Refused(api, Request(("Record", new Entity("contact")))).Message.Should().Contain("Record");
        Refused(api, Request(("Records", new EntityCollection([new Entity("account"), new Entity("contact")]))))
            .Message.Should().Contain("Records");
    }

    [Fact]
    public void Checks_run_in_order_target_required_type_undeclared()
    {
        var api = Api(binding: 1, bound: "account", requests: [Field("Count", CustomApiType.Integer), Field("Name", CustomApiType.String, true)]);

        Refused(api, Request(("Name", 5), ("Other", 1))).Message.Should().Contain("Target");
        Refused(api, Request(("Target", new EntityReference("account", Guid.NewGuid())), ("Name", 5), ("Other", 1)))
            .Message.Should().Contain("Count");
        Refused(api, Request(("Target", new EntityReference("account", Guid.NewGuid())), ("Count", 1), ("Name", 5), ("Other", 1)))
            .Message.Should().Contain("Name");
    }

    // ---- CompleteResponse ---------------------------------------------------------------------------------------------

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Complete_response_fills_the_default_of_every_unset_property()
    {
        var api = Api(responses: Enum.GetValues<CustomApiType>().Select(t => Field(t.ToString(), t)).ToArray());
        var outputs = new ParameterCollection { ["String"] = "set" };

        CustomApiValidator.CompleteResponse(api, outputs, new ListLogger());

        outputs.Should().HaveCount(13);
        outputs["String"].Should().Be("set");
        outputs["Boolean"].Should().Be(false);
        outputs["DateTime"].Should().Be(DateTime.MinValue);
        outputs["Decimal"].Should().Be(0m);
        outputs["Float"].Should().Be(0d);
        outputs["Integer"].Should().Be(0);
        outputs["Guid"].Should().Be(Guid.Empty);
        foreach (var name in new[] { "Entity", "EntityCollection", "EntityReference", "Money", "Picklist", "StringArray" })
            outputs[name].Should().BeNull(name);
    }

    [Fact]
    public void Complete_response_drops_and_logs_an_undeclared_key()
    {
        var api = Api(responses: [Field("Count", CustomApiType.Integer)]);
        var outputs = new ParameterCollection { ["Count"] = 2, ["Extra"] = "x" };
        var logger = new ListLogger();

        CustomApiValidator.CompleteResponse(api, outputs, logger);

        outputs.Keys.Should().Equal("Count");
        logger.Entries.Should().ContainSingle().Which.Should().Match<(LogLevel Level, string Text)>(e =>
            e.Level == LogLevel.Warning && e.Text.Contains("xt_Test") && e.Text.Contains("Extra"));
    }

    [Fact]
    public void Complete_response_refuses_a_value_of_the_wrong_type()
    {
        var api = Api(responses: [Field("Count", CustomApiType.Integer), Field("Name", CustomApiType.String)]);
        var outputs = new ParameterCollection { ["Count"] = "two" };

        var act = () => CustomApiValidator.CompleteResponse(api, outputs, new ListLogger());

        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(500);
        fault.ErrorCode.Should().Be(UnexpectedPluginException);
        fault.Message.Should().Contain("Count").And.Contain("xt_Test");
        // The second pass runs after stage 40, so the value may come from a registered step, not the main operation.
        fault.Message.Should().Contain("a plug-in of the call set it to").And.NotContain("main operation");
    }

    [Fact]
    public void Complete_response_refuses_null_for_a_value_type_and_accepts_it_for_a_reference_type()
    {
        var api = Api(responses: [Field("Count", CustomApiType.Integer), Field("Name", CustomApiType.String)]);

        var nullName = new ParameterCollection { ["Count"] = 1, ["Name"] = null };
        CustomApiValidator.CompleteResponse(api, nullName, new ListLogger());
        nullName["Name"].Should().BeNull();

        var act = () => CustomApiValidator.CompleteResponse(api, new ParameterCollection { ["Count"] = null }, new ListLogger());
        act.Should().Throw<XrmdFaultException>().Which.ErrorCode.Should().Be(UnexpectedPluginException);
    }
}
