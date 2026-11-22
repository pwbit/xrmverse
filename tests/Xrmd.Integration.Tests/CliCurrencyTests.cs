using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xrmd.Abstractions;
using Xrmd.Cli;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>The <c>currency</c> group of <c>xrmd</c> (plan 2.10, task 6) against the in-memory host, over the Web API.</summary>
[Collection("xrmd")]
public sealed class CliCurrencyTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "xrmd-cli-int", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public async Task InitializeAsync() =>
        (await RunAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", fx.AdminObjectId.ToString()))
            .Should().Be(0, _err.ToString());

    public async Task DisposeAsync()
    {
        await RunAsync("currency", "pricing-precision", "2");
        try { Directory.Delete(_configDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private Task<int> RunAsync(params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var env = new CliEnvironment(_out, _err, new StringReader(""), false, fx.App.Server.CreateHandler(), _configDir, new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
        };
        return CliApp.RunAsync(args, env);
    }

    private JsonElement Out() => JsonDocument.Parse(_out.ToString()).RootElement.Clone();

    /// <summary>The codes handed out in this test run (final review M6).</summary>
    private static readonly HashSet<string> Issued = new(StringComparer.Ordinal);

    /// <summary>
    /// A custom code unique to this call (three capital letters): never an ISO 4217 code (the seeded ones, such as EUR and USD,
    /// among them), never one already in the database, never one handed out before in this run (final review M6).
    /// </summary>
    private async Task<string> NewCodeAsync()
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var code = new string([(char)('A' + Random.Shared.Next(26)), (char)('A' + Random.Shared.Next(26)), (char)('A' + Random.Shared.Next(26))]);
            if (IsoCurrencyCodes.Contains(code)) continue;
            lock (Issued)
                if (Issued.Contains(code)) continue;
            await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
            await using var cmd = TestSql.Command(conn, "SELECT COUNT(*) FROM data.transactioncurrency WHERE isocurrencycode = @code");
            cmd.Parameters.AddWithValue("@code", code);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0) continue;
            lock (Issued)
                if (Issued.Add(code)) return code;
        }
        throw new InvalidOperationException("No free currency code was found.");
    }

    private JsonElement Row(string code) =>
        Out().EnumerateArray().Single(r => r.GetProperty("code").GetString() == code);

    [Fact]
    public async Task List_shows_base_currency()
    {
        (await RunAsync("currency", "list")).Should().Be(0, _err.ToString());
        _out.ToString().Should().Contain("EUR").And.Contain("Base");
        (await RunAsync("currency", "list", "-o", "json")).Should().Be(0, _err.ToString());
        var eur = Row("EUR");
        eur.GetProperty("base").GetBoolean().Should().BeTrue();
        eur.GetProperty("rate").GetDecimal().Should().Be(1m);
    }

    [Fact]
    public async Task Create_then_rate_then_list()
    {
        var code = await NewCodeAsync();
        (await RunAsync("currency", "create", code, "--name", "Test " + code, "--symbol", "T$", "--rate", "1.25", "--custom")).Should().Be(0, _err.ToString());
        (await RunAsync("currency", "rate", code, "0.5")).Should().Be(0, _err.ToString());
        (await RunAsync("currency", "list", "-o", "json")).Should().Be(0, _err.ToString());
        var row = Row(code);
        row.GetProperty("rate").GetDecimal().Should().Be(0.5m);
        row.GetProperty("precision").GetInt32().Should().Be(2);
        row.GetProperty("symbol").GetString().Should().Be("T$");
        row.GetProperty("base").GetBoolean().Should().BeFalse();
        row.GetProperty("state").GetString().Should().Be("Active");

        (await RunAsync("currency", "deactivate", code, "--yes")).Should().Be(0, _err.ToString());
        (await RunAsync("currency", "list", "-o", "json")).Should().Be(0, _err.ToString());
        Row(code).GetProperty("state").GetString().Should().Be("Inactive");
        (await RunAsync("currency", "activate", code)).Should().Be(0, _err.ToString());
        (await RunAsync("currency", "list", "-o", "json")).Should().Be(0, _err.ToString());
        Row(code).GetProperty("state").GetString().Should().Be("Active");
    }

    [Fact]
    public async Task List_tolerates_a_currency_with_a_null_rate()
    {
        // The Web API leaves a null column out of a $select response; another test leaves such a row in the shared database.
        var code = await NewCodeAsync();
        (await RunAsync("currency", "create", code, "--name", "Test " + code, "--symbol", "T$", "--rate", "2", "--custom")).Should().Be(0, _err.ToString());
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await using var cmd = TestSql.Command(conn, "UPDATE data.transactioncurrency SET exchangerate = NULL WHERE isocurrencycode = @code");
            cmd.Parameters.AddWithValue("@code", code);
            await cmd.ExecuteNonQueryAsync();
        }
        (await RunAsync("currency", "list")).Should().Be(0, _err.ToString());
        (await RunAsync("currency", "list", "-o", "json")).Should().Be(0, _err.ToString());
        Row(code).GetProperty("rate").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Create_uppercases_the_code()
    {
        var code = await NewCodeAsync();
        (await RunAsync("currency", "create", code.ToLowerInvariant(), "--name", "Test " + code, "--symbol", "T$", "--rate", "2", "--custom", "-o", "json"))
            .Should().Be(0, _err.ToString());
        Out().GetProperty("code").GetString().Should().Be(code);
    }

    [Fact]
    public async Task Rate_of_base_exits_5()
    {
        (await RunAsync("currency", "rate", "EUR", "2")).Should().Be(5);
        _err.ToString().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Deactivate_needs_yes_without_tty()
    {
        var code = await NewCodeAsync();
        (await RunAsync("currency", "create", code, "--name", "Test " + code, "--symbol", "T$", "--rate", "2", "--custom")).Should().Be(0, _err.ToString());
        (await RunAsync("currency", "deactivate", code)).Should().Be(2);
        _err.ToString().Should().Contain("--yes");
    }

    [Fact]
    public async Task Unknown_code_exits_4()
    {
        (await RunAsync("currency", "rate", "QQQ", "2")).Should().Be(4);
        (await RunAsync("currency", "activate", "QQQ")).Should().Be(4);
        (await RunAsync("currency", "deactivate", "QQQ", "--yes")).Should().Be(4);
    }

    [Fact]
    public async Task Non_numeric_rate_exits_2()
    {
        var code = await NewCodeAsync();
        (await RunAsync("currency", "create", code, "--name", "Test " + code, "--symbol", "T$", "--rate", "abc", "--custom")).Should().Be(2);
        _err.ToString().Should().Contain("'abc' is not a number");
        (await RunAsync("currency", "list", "-o", "json")).Should().Be(0, _err.ToString());
        Out().EnumerateArray().Should().NotContain(r => r.GetProperty("code").GetString() == code, "nothing is sent for a usage error");
        (await RunAsync("currency", "rate", "EUR", "1,5")).Should().Be(2);
        _err.ToString().Should().Contain("is not a number");
    }

    [Fact]
    public async Task Non_admin_exits_3()
    {
        // A user without the System Administrator role: Basic User reads currencies, but cannot create or change one.
        var oid = Guid.NewGuid();
        await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        (await RunAsync("auth", "login", "--profile", "basic", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", oid.ToString()))
            .Should().Be(0, _err.ToString());
        var code = await NewCodeAsync();
        (await RunAsync("currency", "create", code, "--name", "Test " + code, "--symbol", "T$", "--rate", "2", "--custom", "--profile", "basic"))
            .Should().Be(3, _err.ToString());
        _err.ToString().Should().NotBeEmpty();
        (await RunAsync("currency", "pricing-precision", "3", "--profile", "basic")).Should().Be(3, _err.ToString());
    }

    [Fact]
    public async Task Pricing_precision_5_exits_5()
    {
        (await RunAsync("currency", "pricing-precision", "5")).Should().Be(5);
        (await RunAsync("currency", "pricing-precision", "3")).Should().Be(0, _err.ToString());
    }

    [Fact]
    public async Task Json_output()
    {
        var code = await NewCodeAsync();
        (await RunAsync("currency", "create", code, "--name", "Test " + code, "--symbol", "T$", "--rate", "3", "--precision", "3", "--custom", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("code").GetString().Should().Be(code);
        (await RunAsync("currency", "pricing-precision", "4", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("pricingPrecision").GetInt32().Should().Be(4);
        (await RunAsync("currency", "rate", code, "4", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("rate").GetDecimal().Should().Be(4m);
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process may run.");
    }
}
