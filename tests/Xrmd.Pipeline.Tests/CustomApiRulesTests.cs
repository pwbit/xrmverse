using FluentAssertions;
using Xrmd.Pipeline.CustomApis;

namespace Xrmd.Pipeline.Tests;

/// <summary>The custom API definition rules shared by solution import and authoring (plan 3.1 task 1).</summary>
public class CustomApiRulesTests
{
    private static bool TableExists(string table) => table is "account" or "contact";

    private static CustomApiFieldShape Field(string name, int type = 10, string? table = null, bool optional = false) =>
        new(name, type, table, optional);

    private static CustomApiShape Api(int binding = 0, string? bound = null, bool function = false, bool workflow = false, int stepType = 2,
        CustomApiFieldShape[]? requests = null, CustomApiFieldShape[]? responses = null, string name = "xt_Api") =>
        new(name, binding, bound, function, workflow, stepType, requests ?? [], responses ?? []);

    [Fact]
    public void ApiProblem_valid_definition_is_null() =>
        CustomApiRules.ApiProblem(Api(binding: 1, bound: "account"), TableExists).Should().BeNull();

    [Fact]
    public void ApiProblem_global_with_bound_table() =>
        CustomApiRules.ApiProblem(Api(bound: "account"), TableExists)
            .Should().Be("it is global (bindingtype 0) but has the boundentitylogicalname 'account'");

    [Fact]
    public void ApiProblem_bound_without_table() =>
        CustomApiRules.ApiProblem(Api(binding: 2), TableExists).Should().Be("bindingtype 2 needs a boundentitylogicalname");

    [Fact]
    public void ApiProblem_unknown_bound_table() =>
        CustomApiRules.ApiProblem(Api(binding: 1, bound: "nosuch"), TableExists)
            .Should().Be("its boundentitylogicalname 'nosuch' is a table neither in the solution nor in the organization");

    [Fact]
    public void ApiProblem_binding_out_of_range() =>
        CustomApiRules.ApiProblem(Api(binding: 3, bound: "account"), TableExists)
            .Should().Be("bindingtype 3 is not 0 (Global), 1 (Entity) or 2 (Entity Collection)");

    [Fact]
    public void ApiProblem_step_type_out_of_range() =>
        CustomApiRules.ApiProblem(Api(stepType: 3), TableExists)
            .Should().Be("allowedcustomprocessingsteptype 3 is not 0 (None), 1 (Async Only) or 2 (Sync and Async)");

    [Fact]
    public void ApiProblem_function_enabled_for_workflows() =>
        CustomApiRules.ApiProblem(Api(function: true, workflow: true), TableExists)
            .Should().Be("it is a function (isfunction 1) enabled for workflows (workflowsdkstepenabled 1), a combination Dataverse does not allow");

    [Fact]
    public void FieldProblem_type_out_of_range()
    {
        var field = Field("In", 13);
        CustomApiRules.FieldProblem(Api(requests: [field]), field, true, TableExists)
            .Should().Be("request parameter 'In' has the type 13, which is not a value from 0 to 12");
    }

    [Fact]
    public void FieldProblem_table_on_a_string()
    {
        var field = Field("In", 10, "account");
        CustomApiRules.FieldProblem(Api(requests: [field]), field, true, TableExists)
            .Should().Be("request parameter 'In' has the logicalentityname 'account', but a String value names no table");
    }

    [Fact]
    public void FieldProblem_entitycollection_property_with_table()
    {
        var field = Field("Out", (int)Xrmd.Metadata.CustomApiType.EntityCollection, "account");
        CustomApiRules.FieldProblem(Api(responses: [field]), field, false, TableExists)
            .Should().Be("response property 'Out' has the logicalentityname 'account', but a EntityCollection value names no table");
    }

    [Fact]
    public void FieldProblem_entitycollection_parameter_with_table_is_fine()
    {
        var field = Field("In", (int)Xrmd.Metadata.CustomApiType.EntityCollection, "account");
        CustomApiRules.FieldProblem(Api(requests: [field]), field, true, TableExists).Should().BeNull();
    }

    [Fact]
    public void FieldProblem_unknown_table()
    {
        var field = Field("In", (int)Xrmd.Metadata.CustomApiType.EntityReference, "nosuch");
        CustomApiRules.FieldProblem(Api(requests: [field]), field, true, TableExists)
            .Should().Be("request parameter 'In' has the logicalentityname 'nosuch', a table neither in the solution nor in the organization");
    }

    [Fact]
    public void FieldProblem_bad_characters()
    {
        var field = Field("a-b");
        CustomApiRules.FieldProblem(Api(requests: [field]), field, true, TableExists)
            .Should().Be("request parameter 'a-b' may hold letters, digits and '_' only");
    }

    [Fact]
    public void FieldProblem_target_on_entity_bound()
    {
        var field = Field("target", 5);
        CustomApiRules.FieldProblem(Api(binding: 1, bound: "account", requests: [field]), field, true, TableExists)
            .Should().Be("it is bound to a table and declares the request parameter 'target': the server adds Target to an entity-bound API itself");
    }

    [Fact]
    public void FieldProblem_target_on_collection_bound_is_fine()
    {
        var field = Field("Target", 5);
        CustomApiRules.FieldProblem(Api(binding: 2, bound: "account", requests: [field]), field, true, TableExists).Should().BeNull();
    }

    [Fact]
    public void FieldProblem_isoptional_on_response_property()
    {
        var field = Field("Out", optional: true);
        CustomApiRules.FieldProblem(Api(responses: [field]), field, false, TableExists)
            .Should().Be("response property 'Out' has isoptional, which only request parameters can have");
    }

    [Fact]
    public void DuplicateFieldProblem_ignores_case() =>
        CustomApiRules.DuplicateFieldProblem(Api(requests: [Field("In"), Field("IN")]))
            .Should().Be("it declares the request parameter 'In' more than once (unique names ignore case)");

    [Fact]
    public void DuplicateFieldProblem_response_properties() =>
        CustomApiRules.DuplicateFieldProblem(Api(responses: [Field("Out"), Field("out")]))
            .Should().Be("it declares the response property 'Out' more than once (unique names ignore case)");

    [Fact]
    public void DuplicateFieldProblem_distinct_names_is_null() =>
        CustomApiRules.DuplicateFieldProblem(Api(requests: [Field("A")], responses: [Field("A")])).Should().BeNull();

    [Theory]
    [InlineData("xt_Api1", true)]
    [InlineData("a-b", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void HasValidNameCharacters_accepts_letters_digits_underscore(string name, bool expected) =>
        CustomApiRules.HasValidNameCharacters(name).Should().Be(expected);

    [Fact]
    public void EntitySetProblem_global_named_like_a_set() =>
        CustomApiRules.EntitySetProblem(Api(name: "accounts"), new Dictionary<string, string> { ["accounts"] = "account" })
            .Should().Be("it is global and 'accounts' is the entity set of table 'account', so the Web API could not call it");

    [Fact]
    public void EntitySetProblem_bound_api_is_not_checked() =>
        CustomApiRules.EntitySetProblem(Api(binding: 1, bound: "account", name: "accounts"), new Dictionary<string, string> { ["accounts"] = "account" })
            .Should().BeNull();
}
