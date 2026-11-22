using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xrmd.Cli.Commands;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Tests;

public sealed class RowWriterTests
{
    private sealed class Handler(Func<int, HttpStatusCode> status, string errorCode = "0x1") : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var code = status(Interlocked.Increment(ref _calls));
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent("{\"error\":{\"code\":\"" + errorCode + "\",\"message\":\"server says " + (int)code + "\"}}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (XrmdClient Client, IReadOnlyList<(int, JsonObject)> Rows) Setup(Func<int, HttpStatusCode> status, int rows = 5, string code = "0x1") =>
        (new XrmdClient(new HttpClient(new Handler(status, code)), new Uri("http://localhost/"), _ => Task.FromResult("t")),
         Enumerable.Range(1, rows).Select(n => (n, new JsonObject { ["name"] = "r" + n })).ToList());

    [Fact]
    public async Task A_403_on_a_row_is_a_failed_row_and_the_run_goes_on()
    {
        var (client, rows) = Setup(call => call is 2 or 4 ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent);
        var run = await RowWriter.CreateAsync(client, "things", rows, 1, default);
        run.Aborted.Should().BeNull();
        run.Created.Should().Be(3);
        run.Failures.Select(f => f.Row).Should().Equal(2, 4);
        run.Failures[0].Error.Should().Be("server says 403");
    }

    [Fact]
    public async Task A_401_stops_the_run_and_the_summary_says_how_far_it_got()
    {
        var (client, rows) = Setup(call => call switch { 1 => HttpStatusCode.NoContent, 2 => HttpStatusCode.BadRequest, _ => HttpStatusCode.Unauthorized });
        var run = await RowWriter.CreateAsync(client, "things", rows, 1, default);
        run.Aborted.Should().NotBeNull();
        run.Aborted!.ExitCode.Should().Be(ExitCodes.Auth);
        run.Created.Should().Be(1);
        run.Failures.Select(f => f.Row).Should().Equal(2);

        var err = new StringWriter();
        var act = () => RowWriter.ReportAsync(err, 5, run.Failures, run);
        (await act.Should().ThrowAsync<CliException>()).Which.ExitCode.Should().Be(ExitCodes.Auth);
        err.ToString().Should().Contain("Row 2: server says 400").And.Contain("Stopped: 1 of 5 rows created, 1 failed");
    }

    [Fact]
    public async Task A_deadlock_victim_is_sent_again()
    {
        // Calls 1 and 2: row 1 is a deadlock victim twice, then created; later rows are created at once.
        var (client, rows) = Setup(call => call <= 2 ? HttpStatusCode.InternalServerError : HttpStatusCode.NoContent, rows: 3, code: "0x80044150");
        var run = await RowWriter.CreateAsync(client, "things", rows, 1, default);
        run.Created.Should().Be(3);
        run.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_that_keeps_deadlocking_fails_after_three_tries_and_another_500_is_not_retried()
    {
        var (client, rows) = Setup(call => call <= 3 ? HttpStatusCode.InternalServerError : HttpStatusCode.NoContent, rows: 2, code: "0x80044150");
        var run = await RowWriter.CreateAsync(client, "things", rows, 1, default);
        run.Created.Should().Be(1);
        run.Failures.Select(f => f.Row).Should().Equal(1);

        (client, rows) = Setup(call => call == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.NoContent, rows: 2);
        run = await RowWriter.CreateAsync(client, "things", rows, 1, default);
        run.Created.Should().Be(1);
        run.Failures.Select(f => f.Row).Should().Equal(1);
    }
}
