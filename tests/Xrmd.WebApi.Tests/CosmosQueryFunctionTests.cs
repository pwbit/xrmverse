using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Core.Errors;

namespace Xrmd.WebApi.Tests;

/// <summary>
/// The <c>ExecuteCosmosSqlQuery</c> function of the Web API (plan #78, task 5): the URL parameters translated into the SDK
/// request. Semantic checks (length cap, page size bounds, duplicate keys) belong to the message and are not repeated here.
/// </summary>
public class CosmosQueryFunctionTests
{
    private const string Reference = "QueryText=@p1,EntityLogicalName=@p2,QueryParameters=@p3,PageSize=@p4,PartitionId=@p5";

    private const string Parameters = """
        {"Keys":["@sensortype","@power"],"Values":[{"Type":"System.String","Value":"Humidity"},{"Type":"System.Int32","Value":"5"}]}
        """;

    private static OrganizationRequest Request(string parameters, params (string Alias, string Value)[] aliases) =>
        CosmosQueryFunction.ToRequest(parameters, a => aliases.FirstOrDefault(x => x.Alias == a).Value);

    private static XrmdFaultException Refused(Action act) =>
        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400).Which;

    [Fact]
    public void Reference_example_maps_every_parameter()
    {
        var request = Request(Reference,
            ("@p1", "'SELECT c.props.new_name FROM c WHERE c.props.new_a = @sensortype'"),
            ("@p2", "'new_sensor'"), ("@p3", Parameters), ("@p4", "50"), ("@p5", "'p1'"));

        request.RequestName.Should().Be("ExecuteCosmosSqlQuery");
        request["QueryText"].Should().Be("SELECT c.props.new_name FROM c WHERE c.props.new_a = @sensortype");
        request["EntityLogicalName"].Should().Be("new_sensor");
        request["PageSize"].Should().Be(50L);
        request["PartitionId"].Should().Be("p1");
        var collection = request["QueryParameters"].Should().BeOfType<ParameterCollection>().Subject;
        collection["@sensortype"].Should().Be("Humidity");
        collection["@power"].Should().Be(5);
    }

    [Fact]
    public void Inline_values_and_doubled_quotes_unescape()
    {
        var request = Request("QueryText='SELECT c.props.a FROM c WHERE c.props.b = ''x, y''',EntityLogicalName='t'");

        request["QueryText"].Should().Be("SELECT c.props.a FROM c WHERE c.props.b = 'x, y'");
        request.Parameters.ContainsKey("PageSize").Should().BeFalse();
    }

    [Fact]
    public void Alias_quoted_value_unescapes_doubled_quotes()
    {
        var request = Request("QueryText=@p1,EntityLogicalName=@p2", ("@p1", "'a ''b'' c'"), ("@p2", "t"));

        request["QueryText"].Should().Be("a 'b' c");
        request["EntityLogicalName"].Should().Be("t");
    }

    [Fact]
    public void Null_and_paging_cookie_are_mapped_or_skipped()
    {
        var request = Request("QueryText='q',EntityLogicalName='t',PagingCookie='abc',PartitionId=null,PageSize=null");

        request["PagingCookie"].Should().Be("abc");
        request.Parameters.ContainsKey("PartitionId").Should().BeFalse();
        request.Parameters.ContainsKey("PageSize").Should().BeFalse();
    }

    [Theory]
    [InlineData("System.Int64", "9000000000", 9000000000L)]
    [InlineData("System.Double", "1.5", 1.5)]
    [InlineData("System.Boolean", "true", true)]
    public void Every_j6_type_is_mapped(string type, string value, object expected)
    {
        var json = $$"""{"Keys":["@v"],"Values":[{"Type":"{{type}}","Value":"{{value}}"}]}""";
        var request = Request("QueryText='q',EntityLogicalName='t',QueryParameters=@p", ("@p", json));

        ((ParameterCollection)request["QueryParameters"])["@v"].Should().Be(expected);
    }

    [Fact]
    public void Decimal_and_json_number_values_are_mapped()
    {
        var json = """{"Keys":["@d","@n"],"Values":[{"Type":"System.Decimal","Value":"2.50"},{"Type":"System.Int32","Value":7}]}""";
        var collection = (ParameterCollection)Request("QueryText='q',EntityLogicalName='t',QueryParameters=@p", ("@p", json))["QueryParameters"];

        collection["@d"].Should().Be(2.5m);
        collection["@n"].Should().Be(7);
    }

    [Fact]
    public void Unknown_parameter_is_400()
    {
        Refused(() => Request("QueryText='q',EntityLogicalName='t',Bogus='x'")).Message.Should().Contain("Bogus");
    }

    [Fact]
    public void Repeated_parameter_is_400()
    {
        Refused(() => Request("QueryText='q',EntityLogicalName='t',QueryText='r'")).Message.Should().Contain("QueryText");
    }

    [Fact]
    public void Alias_without_value_is_400()
    {
        Refused(() => Request("QueryText=@missing,EntityLogicalName='t'")).Message.Should().Contain("@missing");
    }

    [Fact]
    public void Unsupported_value_type_is_400_naming_key_and_type()
    {
        var json = """{"Keys":["@id"],"Values":[{"Type":"System.Guid","Value":"d249d106-38b5-ec11-983f-002248296cd0"}]}""";

        var message = Refused(() => Request("QueryText='q',EntityLogicalName='t',QueryParameters=@p", ("@p", json))).Message;

        message.Should().Contain("@id").And.Contain("System.Guid");
    }

    [Fact]
    public void Keys_and_values_of_different_lengths_are_400()
    {
        var json = """{"Keys":["@a","@b"],"Values":[{"Type":"System.String","Value":"x"}]}""";

        Refused(() => Request("QueryText='q',EntityLogicalName='t',QueryParameters=@p", ("@p", json))).Message
            .Should().Contain("QueryParameters");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{"Keys":["@a"]}""")]
    [InlineData("""{"Keys":[1],"Values":[{"Type":"System.String","Value":"x"}]}""")]
    [InlineData("""{"Keys":["@a"],"Values":[{"Value":"x"}]}""")]
    [InlineData("""{"Keys":["@a"],"Values":[{"Type":"System.Int32","Value":"abc"}]}""")]
    public void Malformed_query_parameters_are_400(string json)
    {
        Refused(() => Request("QueryText='q',EntityLogicalName='t',QueryParameters=@p", ("@p", json))).Message
            .Should().Contain("QueryParameters");
    }

    [Fact]
    public void Page_size_must_be_an_integer()
    {
        Refused(() => Request("QueryText='q',EntityLogicalName='t',PageSize='x'")).Message.Should().Contain("PageSize");
    }

    [Fact]
    public void Quoted_text_where_a_string_is_expected_must_be_quoted_inline()
    {
        Refused(() => Request("QueryText=abc,EntityLogicalName='t'")).Message.Should().Contain("QueryText");
    }

    [Fact]
    public void Alias_resolving_to_null_means_absent()
    {
        var request = Request("QueryText='q',EntityLogicalName='t',PartitionId=@p5,PagingCookie=@p6,PageSize=@p7",
            ("@p5", "null"), ("@p6", "null"), ("@p7", "null"));

        request.Parameters.ContainsKey("PartitionId").Should().BeFalse();
        request.Parameters.ContainsKey("PagingCookie").Should().BeFalse();
        request.Parameters.ContainsKey("PageSize").Should().BeFalse();
    }

    [Fact]
    public void Unquoted_alias_value_with_quotes_is_taken_verbatim()
    {
        var request = Request("QueryText=@p1,EntityLogicalName='t'", ("@p1", "'a' OR 'b'"));

        request["QueryText"].Should().Be("'a' OR 'b'");
    }

    [Fact]
    public void Inline_value_with_unescaped_interior_quote_is_400()
    {
        Refused(() => Request("QueryText='a'b',EntityLogicalName='t'")).Message.Should().Contain("QueryText");
    }

    [Fact]
    public void Unterminated_inline_quote_is_400()
    {
        Refused(() => Request("QueryText='abc,EntityLogicalName='t'")).Message.Should().Contain("QueryText");
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Non_finite_double_is_400_naming_the_item(string value)
    {
        var json = $$"""{"Keys":["@v"],"Values":[{"Type":"System.Double","Value":"{{value}}"}]}""";

        Refused(() => Request("QueryText='q',EntityLogicalName='t',QueryParameters=@p", ("@p", json))).Message.Should().Contain("@v");
    }
}
