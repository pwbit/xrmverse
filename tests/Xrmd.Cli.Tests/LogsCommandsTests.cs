using System.Net;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

public class LogsCommandsTests
{
    /// <summary>A clock whose timers fire at once, so a polling loop runs without waiting.</summary>
    private sealed class ImmediateClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static string Entry(string time, string category, string message, string level = "Information") =>
        $$"""{"timestamp":"2026-11-08T10:00:{{time}}.0000000+00:00","level":"{{level}}","category":"{{category}}","message":"{{message}}","exception":null}""";

    private static readonly string First = Entry("00", "Xrmd.A", "one");
    private static readonly string Second = Entry("05", "Xrmd.A", "two");
    private static readonly string SameInstant = Entry("05", "Xrmd.A", "two-b"); // distinct entry, same timestamp as Second
    private static readonly string OtherCategory = Entry("07", "Xrmd.B", "other", "Warning");
    private static readonly string Third = Entry("09", "Xrmd.A", "three", "Error");

    [Fact]
    public async Task Follow_polls_from_the_last_timestamp_prints_each_entry_once_and_filters_by_category()
    {
        var polls = 0;
        using var cli = new TestCli(_ =>
        {
            polls++;
            var body = polls switch
            {
                1 => $"[{First},{Second},{SameInstant}]",
                // `since` is inclusive: the entries at the cursor come back, matching the filter, and must not print again.
                2 => $"[{Second},{SameInstant},{OtherCategory},{Third}]",
                3 => $"[{Third}]",
                _ => throw new OperationCanceledException(), // Ctrl+C
            };
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":{{body}}}""");
        });
        cli.Clock = new ImmediateClock();
        (await cli.LoginDevAsync()).Should().Be(0);

        var exit = await cli.RunAsync("logs", "--follow", "--category", "xrmd.a", "-o", "json");

        exit.Should().Be(0, cli.Err.ToString());
        var lines = cli.Out.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("message").GetString())
            .Should().Equal("one", "two", "two-b", "three");

        var logRequests = cli.Handler.Requests.Where(r => r.RequestUri!.AbsolutePath == "/admin/logs").ToList();
        logRequests[0].RequestUri!.Query.Should().NotContain("since").And.NotContain("take"); // server default, like without --follow
        logRequests[1].RequestUri!.Query.Should().Contain("since=2026-11-08T10%3A00%3A05").And.Contain("take=1000");
        logRequests[2].RequestUri!.Query.Should().Contain("since=2026-11-08T10%3A00%3A09"); // advanced over the filtered-out entry
    }

    [Fact]
    public async Task Without_follow_the_array_is_printed_once_and_the_filters_are_sent()
    {
        using var cli = new TestCli(_ => FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{{First}},{{OtherCategory}}]}"""));
        (await cli.LoginDevAsync()).Should().Be(0);

        (await cli.RunAsync("logs", "--since", "2026-11-08T09:00:00Z", "--level", "Warning", "--category", "Xrmd.B", "-o", "json")).Should().Be(0, cli.Err.ToString());

        cli.Out.ToString().Should().Contain("other").And.NotContain("\"one\"");
        var query = cli.Handler.Requests.Last().RequestUri!.Query;
        query.Should().Contain("level=Warning").And.Contain("since=2026-11-08T09%3A00%3A00Z");
    }
}
