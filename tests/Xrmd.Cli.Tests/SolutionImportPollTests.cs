using System.Net;
using FluentAssertions;

namespace Xrmd.Cli.Tests;

/// <summary><c>solution import --async</c>: the poll loop against a scripted server, on a clock that never sleeps.</summary>
public sealed class SolutionImportPollTests : IDisposable
{
    private const string Url = "http://xrmd.test:5000";
    private static readonly Guid OperationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid JobKey = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _zip = Path.Combine(Path.GetTempPath(), "xrmd-poll-" + Guid.NewGuid().ToString("N") + ".zip");

    public SolutionImportPollTests() => File.WriteAllBytes(_zip, [0x50, 0x4b, 3, 4]);

    public void Dispose() => File.Delete(_zip);

    /// <summary>Every timer fires at once and moves the clock forward by its due time.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) Interlocked.Add(ref _ticks, dueTime.Ticks);
            return System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    private static HttpResponseMessage Operation(int state, int status) =>
        FakeHandler.Json(HttpStatusCode.OK, $$"""{"statecode":{{state}},"statuscode":{{status}},"message":null}""");

    /// <summary>A CLI whose server queues the import and answers the polls of the system job with <paramref name="poll"/>.</summary>
    private static async Task<(TestCli Cli, List<string> Polls)> CliAsync(Func<int, HttpResponseMessage> poll,
        Func<HttpResponseMessage>? progress = null)
    {
        var polls = new List<string>();
        var cli = new TestCli(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.Contains("ImportSolutionAsync"))
                return FakeHandler.Json(HttpStatusCode.OK, $$"""{"AsyncOperationId":"{{OperationId}}","ImportJobKey":"{{JobKey}}"}""");
            if (path.Contains("asyncoperations("))
            {
                lock (polls)
                {
                    polls.Add(path);
                    return poll(polls.Count);
                }
            }
            if (path.Contains("importjobs(")) return progress?.Invoke() ?? FakeHandler.Json(HttpStatusCode.OK, """{"progress":50}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }) { Clock = new SteppingClock() };
        (await cli.LoginDevAsync(Url)).Should().Be(0, cli.Err.ToString());
        return (cli, polls);
    }

    [Fact]
    public async Task Transient_poll_failures_are_retried()
    {
        var (cli, polls) = await CliAsync(n => n switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            2 => throw new HttpRequestException("connection refused"),
            3 => new HttpResponseMessage(HttpStatusCode.NotFound),
            4 => Operation(2, 20),
            _ => Operation(3, 30),
        }, progress: () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using (cli)
        {
            (await cli.RunAsync("solution", "import", _zip, "--async")).Should().Be(0, cli.Err.ToString());
            polls.Should().HaveCount(5);
            cli.Err.ToString().Should().Contain("retrying").And.Contain("Succeeded");
        }
    }

    [Fact]
    public async Task Too_many_consecutive_poll_failures_give_up_naming_the_system_job()
    {
        var (cli, polls) = await CliAsync(n => n == 1 ? Operation(2, 20) : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using (cli)
        {
            (await cli.RunAsync("solution", "import", _zip, "--async")).Should().Be(ExitCodes.Unexpected, cli.Err.ToString());
            polls.Should().HaveCount(1 + 5); // SolutionCommands.MaxConsecutivePollFailures
            cli.Err.ToString().Should().Contain($"check asyncoperation {OperationId}");
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Auth_failures_while_polling_fail_at_once(HttpStatusCode code)
    {
        var (cli, polls) = await CliAsync(_ => new HttpResponseMessage(code));
        using (cli)
        {
            (await cli.RunAsync("solution", "import", _zip, "--async")).Should().Be(ExitCodes.Auth, cli.Err.ToString());
            polls.Should().HaveCount(1);
        }

        var (cli2, _) = await CliAsync(_ => Operation(2, 20), progress: () => new HttpResponseMessage(code));
        using (cli2)
            (await cli2.RunAsync("solution", "import", _zip, "--async")).Should().Be(ExitCodes.Auth, cli2.Err.ToString());
    }

    [Fact]
    public async Task A_job_that_never_completes_stops_waiting_at_the_timeout()
    {
        var (cli, polls) = await CliAsync(_ => Operation(2, 20));
        using (cli)
        {
            (await cli.RunAsync("solution", "import", _zip, "--async", "--timeout", "1")).Should().Be(ExitCodes.Unreachable, cli.Err.ToString());
            // 1 minute at one poll every 2 s.
            polls.Count.Should().BeInRange(30, 32);
            cli.Err.ToString().Should().Contain("still running").And.Contain($"asyncoperation {OperationId}").And.NotContain("failed");
        }
    }

    [Fact]
    public async Task Timeout_is_validated()
    {
        var (cli, _) = await CliAsync(_ => Operation(3, 30));
        using (cli)
        {
            (await cli.RunAsync("solution", "import", _zip, "--async", "--timeout", "0")).Should().Be(ExitCodes.Usage);
            (await cli.RunAsync("solution", "import", _zip, "--timeout", "5")).Should().Be(ExitCodes.Usage);
            cli.Err.ToString().Should().Contain("--async");
        }
    }
}
