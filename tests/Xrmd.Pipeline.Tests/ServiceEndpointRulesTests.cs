using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Pipeline.Webhooks;

namespace Xrmd.Pipeline.Tests;

/// <summary>The rules of an accepted webhook endpoint (plan 3.2 task 2), one per rule of the Global Constraints.</summary>
public class ServiceEndpointRulesTests
{
    private const string Url = "https://hooks.example.invalid/receive";

    private static Entity Webhook(int authType = 4, string? authValue = "key-1", string url = Url)
    {
        var record = new Entity("serviceendpoint")
        {
            ["name"] = "orders hook",
            ["contract"] = new OptionSetValue(8),
            ["authtype"] = new OptionSetValue(authType),
            ["url"] = url,
        };
        if (authValue is not null) record["authvalue"] = authValue;
        return record;
    }

    private static string Settings(params (string Name, string Value)[] settings) =>
        "<settings>" + string.Concat(settings.Select(s => $"<setting name=\"{s.Name}\" value=\"{s.Value}\" />")) + "</settings>";

    [Fact]
    public void Valid_webhook_key_endpoint_has_no_problem() =>
        ServiceEndpointRules.EndpointProblem(Webhook(), allowAnyPort: false).Should().BeNull();

    [Fact]
    public void Contract_queue_refused() =>
        ServiceEndpointRules.EndpointProblem(Webhook().With("contract", new OptionSetValue(2)), allowAnyPort: false)
            .Should().Be("Service endpoint 'orders hook': contract 2 (Queue) is not supported; xrmverse delivers webhooks (contract 8) only.");

    [Fact]
    public void Missing_contract_refused() =>
        ServiceEndpointRules.EndpointProblem(Webhook().Without("contract"), allowAnyPort: false)
            .Should().Be("Service endpoint 'orders hook' needs a contract.");

    [Fact]
    public void Authtype_sas_refused() =>
        ServiceEndpointRules.EndpointProblem(Webhook(authType: 2), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': authtype 2 (SAS Key) is not supported; use 4 (Webhook Key), 5 (Http Header) or 6 (Http Query String).");

    [Fact]
    public void Url_with_port_8080_refused_unless_allowed()
    {
        var record = Webhook(url: "https://hooks.example.invalid:8080/receive");
        ServiceEndpointRules.EndpointProblem(record, allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': url must use port 80 or 443.");
        ServiceEndpointRules.EndpointProblem(record, allowAnyPort: true).Should().BeNull();
        ServiceEndpointRules.EndpointProblem(Webhook(url: "http://hooks.example.invalid/receive"), allowAnyPort: false).Should().BeNull();
    }

    [Fact]
    public void Url_ftp_refused() =>
        ServiceEndpointRules.EndpointProblem(Webhook(url: "ftp://hooks.example.invalid/receive"), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': url must use the http or https scheme.");

    [Fact]
    public void Url_with_userinfo_refused()
    {
        var problem = ServiceEndpointRules.EndpointProblem(Webhook(url: "https://alice:pa55@hooks.example.invalid/receive"), allowAnyPort: false);
        problem.Should().Be("Webhook 'orders hook': url must not contain user information.");
        problem.Should().NotContain("pa55");
    }

    [Fact]
    public void Url_relative_or_with_fragment_refused()
    {
        ServiceEndpointRules.EndpointProblem(Webhook(url: "/receive"), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': url must be an absolute URL.");
        ServiceEndpointRules.EndpointProblem(Webhook(url: Url + "#part"), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': url must not contain a fragment.");
        ServiceEndpointRules.EndpointProblem(Webhook().Without("url"), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': url is required.");
    }

    [Fact]
    public void Header_settings_parsed()
    {
        ServiceEndpointRules.TryParseSettings(Settings(("X-Api-Key", "one"), ("Authorization", "Bearer two")), out var settings, out var problem)
            .Should().BeTrue();
        problem.Should().BeNull();
        settings.Should().Equal(new KeyValuePair<string, string>("X-Api-Key", "one"), new KeyValuePair<string, string>("Authorization", "Bearer two"));
        ServiceEndpointRules.EndpointProblem(Webhook(authType: 5, authValue: Settings(("X-Api-Key", "one"))), allowAnyPort: false).Should().BeNull();
        ServiceEndpointRules.EndpointProblem(Webhook(authType: 6, authValue: Settings(("token", "one"))), allowAnyPort: false).Should().BeNull();
    }

    [Fact]
    public void Header_named_host_refused() =>
        ServiceEndpointRules.EndpointProblem(Webhook(authType: 5, authValue: Settings(("Host", "elsewhere.invalid"))), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': authvalue sets a header that xrmverse sets itself.");

    [Fact]
    public void Header_x_ms_refused() =>
        ServiceEndpointRules.EndpointProblem(Webhook(authType: 5, authValue: Settings(("x-ms-dynamics-organization", "other"))), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': authvalue sets a header that xrmverse sets itself.");

    [Fact]
    public void Header_name_not_a_token_refused() =>
        ServiceEndpointRules.EndpointProblem(Webhook(authType: 5, authValue: Settings(("Api Key", "one"))), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': authvalue has a setting whose name is not a valid HTTP header name.");

    [Fact]
    public void Empty_settings_refused()
    {
        ServiceEndpointRules.TryParseSettings("<settings />", out var settings, out var problem).Should().BeFalse();
        settings.Should().BeEmpty();
        problem.Should().Be("has no setting");
        ServiceEndpointRules.EndpointProblem(Webhook(authType: 6, authValue: "<settings></settings>"), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': authvalue has no setting.");
        ServiceEndpointRules.EndpointProblem(Webhook(authType: 6, authValue: Settings(("  ", "one"))), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': authvalue has a setting without a name.");
        ServiceEndpointRules.EndpointProblem(Webhook(authType: 5, authValue: "not xml"), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': authvalue is not a settings XML (<settings><setting name=\"...\" value=\"...\" /></settings>).");
    }

    [Fact]
    public void Setting_without_value_refused_without_its_name() =>
        ServiceEndpointRules.EndpointProblem(Webhook(authType: 6, authValue: "<settings><setting name=\"t0ken-name\" /></settings>"), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': authvalue has a setting without a value.");

    [Fact]
    public void Authvalue_longer_than_2048_refused()
    {
        ServiceEndpointRules.EndpointProblem(Webhook(authValue: new string('k', 2049)), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': authvalue is longer than 2048 characters.");
        ServiceEndpointRules.EndpointProblem(Webhook(authValue: new string('k', 2048)), allowAnyPort: false).Should().BeNull();
    }

    [Fact]
    public void Missing_or_blank_authvalue_refused()
    {
        ServiceEndpointRules.EndpointProblem(Webhook(authValue: null), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook' needs an authvalue.");
        ServiceEndpointRules.EndpointProblem(Webhook(authValue: "   "), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook' needs an authvalue.");
    }

    [Fact]
    public void Servicebus_column_refused()
    {
        ServiceEndpointRules.EndpointProblem(Webhook().With("path", "queue-1"), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': path applies to Azure Service Bus endpoints, which are not supported.");
        ServiceEndpointRules.EndpointProblem(Webhook().With("messageformat", new OptionSetValue(1)), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': messageformat applies to Azure Service Bus endpoints, which are not supported.");
        ServiceEndpointRules.EndpointProblem(Webhook().With("connectionmode", new OptionSetValue(2)), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': connectionmode applies to Azure Service Bus endpoints, which are not supported.");
        ServiceEndpointRules.EndpointProblem(Webhook()
                .With("messageformat", new OptionSetValue(2)).With("messagecharset", new OptionSetValue(1))
                .With("connectionmode", new OptionSetValue(1)).With("userclaim", new OptionSetValue(1)), allowAnyPort: false)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("a&#13;b")] // CR
    [InlineData("a&#10;b")] // LF
    [InlineData("caf\u00e9")] // non-ASCII
    [InlineData("a&#127;b")] // DEL
    public void Header_value_must_be_writable(string value)
    {
        ServiceEndpointRules.EndpointProblem(Webhook(authType: 5, authValue: Settings(("X-Api-Key", value))), allowAnyPort: false)
            .Should().Be("Webhook 'orders hook': authvalue has a setting whose value is not a valid HTTP header value.");
        // The same value is a valid query parameter (it is escaped).
        ServiceEndpointRules.EndpointProblem(Webhook(authType: 6, authValue: Settings(("key", value))), allowAnyPort: false)
            .Should().BeNull();
    }

    [Fact]
    public void Header_value_with_tab_and_symbols_is_accepted() =>
        ServiceEndpointRules.EndpointProblem(Webhook(authType: 5, authValue: Settings(("X-Api-Key", "Bearer a.b-c_d~e/f+g=&#9;h"))),
            allowAnyPort: false).Should().BeNull();

    [Fact]
    public void Problem_never_contains_the_value()
    {
        var problem = ServiceEndpointRules.EndpointProblem(
            Webhook(authType: 5, authValue: Settings(("X-Api-Key", "s3cr3t&#13;&#10;Injected: yes"))), allowAnyPort: false);
        problem.Should().Be("Webhook 'orders hook': authvalue has a setting whose value is not a valid HTTP header value.");
        problem.Should().NotContain("s3cr3t").And.NotContain("X-Api-Key");

        ServiceEndpointRules.TryParseSettings("<settings><setting name=\"k\" value=\"s3cr3t\"></settings>", out _, out var parse)
            .Should().BeFalse();
        parse.Should().NotContain("s3cr3t");
    }
}

/// <summary>Small helpers to vary a test record.</summary>
internal static class EntityTestExtensions
{
    public static Entity With(this Entity e, string attribute, object? value)
    {
        e[attribute] = value;
        return e;
    }

    public static Entity Without(this Entity e, string attribute)
    {
        e.Attributes.Remove(attribute);
        return e;
    }
}
