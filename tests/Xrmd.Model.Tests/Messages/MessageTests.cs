using FluentAssertions;
using Xrmd.Model.Messages;

namespace Xrmd.Model.Tests.Messages;

public class MessageTests
{
    [Fact]
    public void Request_indexer_reads_and_writes_parameters()
    {
        var request = new Request { Name = "WhoAmI" };
        request["Target"] = 5;
        request.Parameters["Target"].Should().Be(5);
        request["Target"].Should().Be(5);
        request.RequestId.Should().BeNull();
    }

    [Fact]
    public void Request_indexer_throws_when_absent()
    {
        var act = () => new Request()["x"];
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Response_indexer_reads_and_writes_results()
    {
        var response = new Response { Name = "WhoAmI" };
        response["UserId"] = Guid.Empty;
        response.Results["UserId"].Should().Be(Guid.Empty);
    }

    [Fact]
    public void Parameter_keys_compare_ordinally()
    {
        var bag = new ParameterBag { ["Target"] = 1 };
        bag.ContainsKey("target").Should().BeFalse();
    }
}
