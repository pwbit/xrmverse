using System.Net;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

public class AuditCommandsTests
{
    private const string Id = "6f1c0b7a-8f65-4b0e-9d52-0f4bb1d3c0a1";

    private static TestCli Cli(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(respond);

    /// <summary>An activity table: its primary id is activityid, not task + "id" (the server refuses any other key in the Target).</summary>
    private static HttpResponseMessage ActivityServer(HttpRequestMessage request)
    {
        var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
        if (url.Contains("EntityDefinitions(LogicalName='task')")) return FakeHandler.Json(HttpStatusCode.OK, """{"LogicalName":"task","PrimaryIdAttribute":"activityid"}""");
        if (url.Contains("DeleteRecordChangeHistory")) return FakeHandler.Json(HttpStatusCode.OK, """{"DeletedEntriesCount":3}""");
        if (url.Contains("RetrieveRecordChangeHistory")) return FakeHandler.Json(HttpStatusCode.OK, """{"AuditDetailCollection":{"AuditDetails":[]}}""");
        if (url.Contains("audits?")) return FakeHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_history_of_an_activity_uses_the_primary_id_from_the_metadata()
    {
        using var cli = Cli(ActivityServer);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("audit", "delete", "--table", "task", "--id", Id, "--yes", "-o", "json")).Should().Be(0, cli.Err.ToString());
        var body = cli.Handler.Bodies.Single(b => b is not null && b.Contains("Target"))!;
        body.Should().Contain("\"activityid\"").And.Contain(Id).And.NotContain("\"taskid\"");
    }

    [Fact]
    public async Task History_of_an_activity_asks_the_history_function_with_the_primary_id()
    {
        using var cli = Cli(ActivityServer);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("audit", "history", "task", Id, "-o", "json")).Should().Be(0, cli.Err.ToString());
        var probe = cli.Handler.Requests.Select(r => Uri.UnescapeDataString(r.RequestUri!.ToString())).Single(u => u.Contains("RetrieveRecordChangeHistory"));
        probe.Should().Contain("activityid").And.NotContain("taskid");
    }

    [Fact]
    public async Task History_without_column_bounds_the_audit_query_with_top()
    {
        using var cli = Cli(ActivityServer);
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        (await cli.RunAsync("audit", "history", "task", Id, "--top", "3", "-o", "json")).Should().Be(0, cli.Err.ToString());
        cli.Handler.Requests.Select(r => r.RequestUri!.ToString()).Single(u => u.Contains("audits?")).Should().Contain("$top=3");
    }
}
