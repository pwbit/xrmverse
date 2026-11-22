using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;

namespace Xrmd.Solution.Tests;

/// <summary>
/// The custom API import rules that need no database (plan 15, C1, C2 and C11): one case per refusal, each a 400 naming the
/// API and what is at fault.
/// </summary>
public class CustomApiImportRulesTests
{
    private static readonly HashSet<string> Tables = new(StringComparer.OrdinalIgnoreCase) { "account", "contact" };

    private static bool TableExists(string table) => Tables.Contains(table);

    private static PackageCustomApiField Field(string name, int type = 10, string? entity = null, bool? optional = null, XElement? kept = null) =>
        new(name, name, type, entity, optional, new Label(name, 1033), new Label(name, 1033), kept);

    private static PackageCustomApi Api(string name = "xt_Api", int binding = 0, string? bound = null, int stepType = 2,
        bool function = false, bool workflow = false, PackageCustomApiField[]? requests = null, PackageCustomApiField[]? responses = null,
        XElement? kept = null) =>
        new(name, name, binding, bound, stepType, function, false, workflow, null, null, new Label(name, 1033), new Label(name, 1033),
            requests ?? [], responses ?? [], kept);

    private static void Refused(Action act, params string[] contains)
    {
        var fault = act.Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        foreach (var text in contains) fault.Message.Should().Contain(text);
    }

    public static TheoryData<string, string> UnsupportedCases() => new()
    {
        { "powerfx", "Power Fx" },
        { "powerfx-parameter", "request parameter 'In' has 'fxexpressionid'" },
        { "function-workflow", "enabled for workflows" },
        { "response-isoptional", "response property 'Out' has isoptional" },
        { "binding-3", "bindingtype 3" },
        { "binding-negative", "bindingtype -1" },
        { "steptype-3", "allowedcustomprocessingsteptype 3" },
        { "global-bound", "it is global" },
        { "bound-missing", "needs a boundentitylogicalname" },
        { "bound-unknown", "'nosuch'" },
        { "type-13", "type 13" },
        { "response-type-negative", "response property 'Out' has the type -1" },
        { "entity-on-string", "names no table" },
        { "entity-collection-on-response", "names no table" },
        { "entity-unknown", "'nosuch', a table neither" },
        { "duplicate-request", "request parameter 'In' more than once" },
        { "duplicate-response", "response property 'Out' more than once" },
        { "target-on-bound", "the server adds Target" },
    };

    [Fact]
    public void Import_refuses_parameter_name_with_dash() =>
        Refused(() => CustomApiImportRules.DemandSupported(Api(requests: [Field("a-b")]), TableExists),
            "xt_Api", "request parameter 'a-b' may hold letters, digits and '_' only");

    [Theory]
    [MemberData(nameof(UnsupportedCases))]
    public void Unsupported_definitions_are_refused(string kind, string expected)
    {
        var api = kind switch
        {
            "powerfx" => Api(kept: new XElement("customapi", new XElement("powerfxruleid", Guid.NewGuid()))),
            "powerfx-parameter" => Api(requests: [Field("In", kept: new XElement("customapirequestparameter", new XElement("fxexpressionid", "x")))]),
            "function-workflow" => Api(function: true, workflow: true),
            "response-isoptional" => Api(responses: [Field("Out", kept: new XElement("customapiresponseproperty", new XElement("isoptional", 1)))]),
            "binding-3" => Api(binding: 3, bound: "account"),
            "binding-negative" => Api(binding: -1),
            "steptype-3" => Api(stepType: 3),
            "global-bound" => Api(bound: "account"),
            "bound-missing" => Api(binding: 2),
            "bound-unknown" => Api(binding: 1, bound: "nosuch"),
            "type-13" => Api(requests: [Field("In", 13)]),
            "response-type-negative" => Api(responses: [Field("Out", -1)]),
            "entity-on-string" => Api(requests: [Field("In", 10, "account")]),
            "entity-collection-on-response" => Api(responses: [Field("Out", 4, "account")]),
            "entity-unknown" => Api(requests: [Field("In", 5, "nosuch")]),
            "duplicate-request" => Api(requests: [Field("In"), Field("in")]),
            "duplicate-response" => Api(responses: [Field("Out"), Field("OUT")]),
            "target-on-bound" => Api(binding: 1, bound: "account", requests: [Field("target", 5, "account")]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Refused(() => CustomApiImportRules.DemandSupported(api, TableExists), "Custom API 'xt_Api' cannot be imported", expected);
    }

    [Fact]
    public void Supported_definitions_pass()
    {
        // A captured (marker) isoptional or Power Fx element is not the element itself.
        static XElement Marker(string name) => new(name, new XAttribute(UnmodelledXml.Namespace + "m", "c"));
        foreach (var api in new[]
                 {
                     Api(),
                     Api(binding: 1, bound: "Account", requests: [Field("Record", 5, "contact", true), Field("Records", 4, "account")]),
                     Api(binding: 2, bound: "contact", responses: [Field("Out", 3, "account")]),
                     Api(function: true, stepType: 0, requests: [Field("Target", 5)]),
                     Api(requests: [Field("Same")], responses: [Field("Same")]),
                     Api(responses: [Field("Out", kept: new XElement("customapiresponseproperty", Marker("isoptional")))],
                         kept: new XElement("customapi", Marker("powerfxruleid"))),
                 })
            CustomApiImportRules.DemandSupported(api, TableExists);
    }

    [Theory]
    [InlineData("xt_Good_1", true)]
    [InlineData("xt_Bad-Name", false)]
    [InlineData("xt_Bad.Name", false)]
    [InlineData("xt_Bad Name", false)]
    public void Name_characters_are_letters_digits_and_underscore(string name, bool valid)
    {
        var act = () => CustomApiImportRules.DemandNameCharacters(Api(name));
        if (valid) act.Should().NotThrow();
        else Refused(act, name, "letters, digits and '_'");
    }

    private static CustomApiImportRules.Existing Stored(int binding = 0, string? bound = null, bool function = false, bool workflow = false,
        int stepType = 2) =>
        new(Guid.NewGuid(), "xt_Api", Guid.NewGuid(), binding, bound, function, workflow, stepType,
            new Dictionary<string, CustomApiImportRules.ExistingField>(StringComparer.OrdinalIgnoreCase)
            {
                ["In"] = new(Guid.NewGuid(), 5, false, "account"),
            },
            new Dictionary<string, CustomApiImportRules.ExistingField>(StringComparer.OrdinalIgnoreCase)
            {
                ["Out"] = new(Guid.NewGuid(), 5, false, "account"),
            });

    private static PackageCustomApi Same(Func<PackageCustomApi, PackageCustomApi>? change = null)
    {
        var api = Api(requests: [Field("In", 5, "account", false)], responses: [Field("Out", 5, "account")]);
        return change is null ? api : change(api);
    }

    public static TheoryData<string, string> ImmutableCases() => new()
    {
        { "steptype", "allowedcustomprocessingsteptype is 2 in the organization and 1 in the file" },
        { "binding", "bindingtype is 0 in the organization and 1 in the file" },
        { "bound", "boundentitylogicalname is (none) in the organization and 'contact' in the file" },
        { "function", "isfunction is 0 in the organization and 1 in the file" },
        { "workflow", "workflowsdkstepenabled is 0 in the organization and 1 in the file" },
        { "request-optional", "request parameter 'In' cannot be changed by a solution import: isoptional is 0 in the organization and 1" },
        { "request-entity", "logicalentityname is 'account' in the organization and 'contact' in the file" },
        { "request-type", "type is 5 in the organization and 3 in the file" },
        { "property-entity", "response property 'Out' cannot be changed by a solution import: logicalentityname is 'account'" },
        { "property-type", "response property 'Out' cannot be changed by a solution import: type is 5 in the organization and 3" },
    };

    [Theory]
    [MemberData(nameof(ImmutableCases))]
    public void Immutable_changes_are_refused(string kind, string expected)
    {
        var api = kind switch
        {
            "steptype" => Same(a => a with { AllowedStepType = 1 }),
            "binding" => Same(a => a with { BindingType = 1 }),
            "bound" => Same(a => a with { BoundEntity = "contact" }),
            "function" => Same(a => a with { IsFunction = true }),
            "workflow" => Same(a => a with { WorkflowEnabled = true }),
            "request-optional" => Same(a => a with { RequestParameters = [Field("In", 5, "account", true)] }),
            "request-entity" => Same(a => a with { RequestParameters = [Field("In", 5, "contact", false)] }),
            "request-type" => Same(a => a with { RequestParameters = [Field("In", 3, "account", false)] }),
            "property-entity" => Same(a => a with { ResponseProperties = [Field("Out", 5, "contact")] }),
            "property-type" => Same(a => a with { ResponseProperties = [Field("Out", 3, "account")] }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Refused(() => CustomApiImportRules.DemandUnchangedImmutables(api, Stored()), "xt_Api", expected);
    }

    [Fact]
    public void Mutable_changes_and_new_fields_pass()
    {
        // Case of the bound table and of field names is ignored; new parameters and properties are additions.
        var api = Same(a => a with
        {
            IsPrivate = true, ExecutePrivilegeName = "prvSomething", PluginTypeKey = Guid.NewGuid(), Name = "renamed",
            RequestParameters = [Field("IN", 5, "ACCOUNT", false), Field("New", 10, null, true)],
            ResponseProperties = [Field("out", 5, "Account"), Field("Extra", 7)],
        });
        CustomApiImportRules.DemandUnchangedImmutables(api, Stored());
        CustomApiImportRules.DemandUnchangedImmutables(Api(binding: 1, bound: "Account"), Stored(binding: 1, bound: "account"));
    }
}
