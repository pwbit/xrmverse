using System.Net;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

/// <summary><c>table column add --type image --primary</c>: a create, then an update that moves the primary image flag.</summary>
public class TableColumnPrimaryTests
{
    [Fact]
    public async Task Primary_update_carries_the_solution_header_and_a_failure_says_the_column_exists()
    {
        using var cli = new TestCli();
        (await cli.LoginDevAsync()).Should().Be(0, cli.Err.ToString());
        cli.Handler = new FakeHandler(request => request.Method == HttpMethod.Put
            ? FakeHandler.Json(HttpStatusCode.BadRequest, """{"error":{"code":"0x80040203","message":"Server refuses the flag."}}""")
            : new HttpResponseMessage(HttpStatusCode.NoContent));

        var code = await cli.RunAsync("table", "column", "add", "new_thing", "new_logo", "--type", "image", "--primary", "--solution", "new_sol");

        code.Should().Be(5);
        cli.Handler.Requests.Select(r => r.Method).Should().Equal(HttpMethod.Post, HttpMethod.Put);
        foreach (var request in cli.Handler.Requests)
            request.Headers.GetValues("MSCRM.SolutionUniqueName").Should().Equal(["new_sol"], request.Method.Method);
        cli.Err.ToString().Should().Contain("new_thing.new_logo").And.Contain("was created but was not made the primary image")
            .And.Contain("Server refuses the flag.");
    }
}
