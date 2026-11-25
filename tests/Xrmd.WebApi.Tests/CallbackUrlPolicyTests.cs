using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xrmd.WebApi.Connector;

namespace Xrmd.WebApi.Tests;

public class CallbackUrlPolicyTests
{
    [Fact]
    public void Accepts_a_flow_callback_url() =>
        CallbackUrlPolicy.Problem("https://prod-12.westeurope.logic.azure.com/workflows/x/triggers/manual/paths/invoke?sig=s", new ConnectorOptions())
            .Should().BeNull();

    [Fact]
    public void Accepts_a_power_platform_environment_callback_url() =>
        CallbackUrlPolicy.Problem("https://default0123.ab.environment.api.powerplatform.com/powerautomate/automations/direct/workflows/x/triggers/manual/paths/invoke?sig=s",
            new ConnectorOptions()).Should().BeNull();

    [Fact]
    public void Accepts_explicit_443() =>
        CallbackUrlPolicy.Problem("https://prod.logic.azure.com:443/x", new ConnectorOptions()).Should().BeNull();

    [Fact]
    public void Matches_host_case_insensitively() =>
        CallbackUrlPolicy.Problem("https://PROD.Logic.Azure.COM/x", new ConnectorOptions()).Should().BeNull();

    [Theory]
    [InlineData("https://logic.azure.com/x", "host is not allowed")]
    [InlineData("http://prod.logic.azure.com/x", "must use https")]
    [InlineData("https://evil.example.com/x", "host is not allowed")]
    [InlineData("https://10.0.0.5/x", "address is not allowed")]
    [InlineData("https://[::1]/x", "address is not allowed")]
    [InlineData("https://prod.logic.azure.com:8443/x", "port")]
    [InlineData("/relative/path", "absolute")]
    [InlineData("", "absolute")]
    [InlineData(null, "absolute")]
    public void Refuses(string? url, string fragment)
    {
        var problem = CallbackUrlPolicy.Problem(url, new ConnectorOptions());
        problem.Should().NotBeNull().And.StartWith("The callback URL ").And.Contain(fragment);
    }

    [Fact]
    public void Refuses_a_url_over_2048_characters()
    {
        var url = "https://prod.logic.azure.com/" + new string('a', 2048);
        CallbackUrlPolicy.Problem(url, new ConnectorOptions()).Should().NotBeNull().And.StartWith("The callback URL ");
    }

    [Fact]
    public void Never_quotes_the_url()
    {
        var problem = CallbackUrlPolicy.Problem("https://evil.example.com/x?sig=secret", new ConnectorOptions());
        problem.Should().NotContain("secret").And.NotContain("evil.example.com");
    }

    [Fact]
    public void Private_callbacks_allow_http_and_any_port_for_listed_hosts()
    {
        var options = new ConnectorOptions { AllowPrivateCallbacks = true, CallbackHosts = ["localhost"] };
        CallbackUrlPolicy.Problem("http://localhost:5123/x", options).Should().BeNull();
        CallbackUrlPolicy.Problem("http://other:5123/x", options).Should().Contain("host is not allowed");
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("2606:4700::1111", false)]
    [InlineData("0.1.2.3", true)]
    [InlineData("0.255.255.255", true)]
    [InlineData("1.0.0.0", false)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.0", false)]
    [InlineData("127.255.0.1", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("239.255.255.255", true)]
    [InlineData("240.0.0.1", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("223.255.255.255", false)]
    [InlineData("fec0::1", true)]
    [InlineData("feff::1", true)]
    [InlineData("ff02::1", true)]
    [InlineData("ff05::2", true)]
    [InlineData("64:ff9b::a00:1", true)] // NAT64 of 10.0.0.1
    [InlineData("64:ff9b::7f00:1", true)] // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe", true)] // NAT64 of 169.254.169.254
    [InlineData("64:ff9b::808:808", false)] // NAT64 of 8.8.8.8
    [InlineData("2002:a00:1::1", true)] // 6to4 of 10.0.0.1
    [InlineData("2002:c0a8:101::1", true)] // 6to4 of 192.168.1.1
    [InlineData("2002:6440:1::1", true)] // 6to4 of 100.64.0.1
    [InlineData("2002:808:808::1", false)] // 6to4 of 8.8.8.8
    [InlineData("::ffff:100.64.0.1", true)]
    [InlineData("::ffff:224.0.0.1", true)]
    public void IsPrivate(string address, bool expected) =>
        CallbackUrlPolicy.IsPrivate(IPAddress.Parse(address)).Should().Be(expected);

    [Fact]
    public void Defaults()
    {
        var o = new ConnectorOptions();
        o.EffectiveCallbackHosts.Should().Equal("*.logic.azure.com", "*.environment.api.powerplatform.com");
        o.MaxSubscriptionsPerUser.Should().Be(50);
        o.AllowPrivateCallbacks.Should().BeFalse();
    }

    [Fact]
    public void Configured_hosts_replace_the_default()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Connector:CallbackHosts:0"] = "localhost" }).Build();
        var o = new ConnectorOptions();
        config.GetSection("Connector").Bind(o);
        o.EffectiveCallbackHosts.Should().Equal("localhost");
    }

    [Fact]
    public void Nothing_configured_gives_the_default()
    {
        var o = new ConnectorOptions();
        new ConfigurationBuilder().Build().GetSection("Connector").Bind(o);
        o.EffectiveCallbackHosts.Should().Equal("*.logic.azure.com", "*.environment.api.powerplatform.com");
    }

    [Fact]
    public void Runtime_assignment_is_exact() =>
        new ConnectorOptions { CallbackHosts = ["localhost"] }.EffectiveCallbackHosts.Should().Equal("localhost");

    [Fact]
    public void Private_ip_literal_follows_the_list()
    {
        CallbackUrlPolicy.Problem("http://127.0.0.1:5000/x", new ConnectorOptions { AllowPrivateCallbacks = true, CallbackHosts = ["127.0.0.1"] }).Should().BeNull();
        CallbackUrlPolicy.Problem("http://127.0.0.1:5000/x", new ConnectorOptions { AllowPrivateCallbacks = true }).Should().Contain("host is not allowed");
        CallbackUrlPolicy.Problem("http://[::1]:5000/x", new ConnectorOptions { AllowPrivateCallbacks = true, CallbackHosts = ["::1"] }).Should().BeNull();
    }

    [Fact]
    public void Refuses_user_info() =>
        CallbackUrlPolicy.Problem("https://u:p@prod.logic.azure.com/x", new ConnectorOptions()).Should().Contain("user information");

    [Fact]
    public void Private_callbacks_allow_https_on_another_port() =>
        CallbackUrlPolicy.Problem("https://prod.logic.azure.com:8443/x", new ConnectorOptions { AllowPrivateCallbacks = true }).Should().BeNull();
}
