using System.Text.Json.Nodes;
using FluentAssertions;
using Xrmd.Metadata;
using Xrmd.Pipeline.CustomApis;
using Xrmd.WebApi.Connector;

namespace Xrmd.WebApi.Tests;

public class CustomApiSchemaBuilderTests
{
    private static CustomApiDefinition Api(IReadOnlyList<CustomApiField> requests, IReadOnlyList<CustomApiField> responses) =>
        new(Guid.NewGuid(), "xt_Api", 0, null, false, false, 0, null, null, requests, responses);

    [Fact]
    public void Request_marks_non_optional_parameters_required()
    {
        var api = Api([new("Text", CustomApiType.String, null, false), new("Count", CustomApiType.Integer, null, true)], []);
        var schema = CustomApiSchemaBuilder.Build(api, request: true);
        schema["type"]!.GetValue<string>().Should().Be("object");
        schema["properties"]!.AsObject().Select(p => p.Key).Should().Equal("Text", "Count");
        schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("Text");
    }

    [Fact]
    public void Request_without_required_parameters_has_no_required_array()
    {
        var api = Api([new("Count", CustomApiType.Integer, null, true)], []);
        CustomApiSchemaBuilder.Build(api, request: true).ContainsKey("required").Should().BeFalse();
    }

    [Theory]
    [InlineData(0, "boolean", null)]
    [InlineData(1, "string", "date-time")]
    [InlineData(2, "number", null)]
    [InlineData(3, "object", null)]
    [InlineData(4, "object", null)]
    [InlineData(5, "object", null)]
    [InlineData(6, "number", null)]
    [InlineData(7, "integer", "int32")]
    [InlineData(8, "number", null)]
    [InlineData(9, "integer", null)]
    [InlineData(10, "string", null)]
    [InlineData(11, "array", null)]
    [InlineData(12, "string", "uuid")]
    public void Maps_each_type_code(int code, string type, string? format)
    {
        var api = Api([new("P", (CustomApiType)code, null, false)], []);
        var p = CustomApiSchemaBuilder.Build(api, request: true)["properties"]!["P"]!.AsObject();
        p["type"]!.GetValue<string>().Should().Be(type);
        (p["format"]?.GetValue<string>()).Should().Be(format);
        p["x-ms-summary"]!.GetValue<string>().Should().Be("P");
        if (code is 3 or 4 or 5)
        {
            p["additionalProperties"]!.GetValue<bool>().Should().BeTrue();
            p["description"]!.GetValue<string>().Should().Contain("Web API");
        }
        if (code == 11) p["items"]!["type"]!.GetValue<string>().Should().Be("string");
    }

    [Fact]
    public void Response_has_no_required_array()
    {
        var api = Api([new("In", CustomApiType.String, null, false)], [new("Out", CustomApiType.String, null, false)]);
        var schema = CustomApiSchemaBuilder.Build(api, request: false);
        schema.ContainsKey("required").Should().BeFalse();
        schema["properties"]!.AsObject().Select(p => p.Key).Should().Equal("Out");
    }
}
