using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Cli;
using Xrmd.Core;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>The <c>audit</c> group of <c>xrmd</c> (plan 13, task 6) against the in-memory host, over the Web API.</summary>
[Collection("xrmd")]
public sealed class CliAuditTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "xrmd-cli-int", Guid.NewGuid().ToString("N"));
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        (await RunAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", fx.AdminObjectId.ToString()))
            .Should().Be(0, _err.ToString());
    }

    /// <summary>Leaves the organization as the other tests expect it: not auditing, retention 30 days.</summary>
    public async Task DisposeAsync()
    {
        await RunAsync("audit", "disable");
        await RunAsync("audit", "retention", "30");
        try { Directory.Delete(_configDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private Task<int> RunAsync(params string[] args) => RunAsync(null, args);

    private Task<int> RunAsync(TextReader? input, params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var env = new CliEnvironment(_out, _err, input ?? new StringReader(""), false, fx.App.Server.CreateHandler(), _configDir, new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
        };
        return CliApp.RunAsync(args, env);
    }

    private JsonElement Out() => JsonDocument.Parse(_out.ToString()).RootElement.Clone();

    private async Task<string> NewTableAsync()
    {
        var table = $"new_aud{_run}";
        (await RunAsync("table", "create", table, "--label", "Audited", "--plural-label", "Auditeds")).Should().Be(0, _err.ToString());
        (await RunAsync("table", "column", "add", table, "new_count", "--type", "int")).Should().Be(0, _err.ToString());
        return table;
    }

    [Fact]
    public async Task Audit_enable_table_and_column_then_history_shows_the_change()
    {
        var table = await NewTableAsync();
        (await RunAsync("audit", "enable")).Should().Be(0, _err.ToString());
        (await RunAsync("audit", "enable", "--table", table)).Should().Be(0, _err.ToString());
        (await RunAsync("audit", "enable", "--table", table, "--column", "new_name")).Should().Be(0, _err.ToString());
        (await RunAsync("audit", "disable", "--table", table, "--column", "new_count")).Should().Be(0, _err.ToString());

        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "First " + _run, ["new_count"] = 1 });
        await Admin.UpdateAsync(new Entity(table, id) { ["new_name"] = "Second " + _run, ["new_count"] = 2 });

        (await RunAsync("audit", "history", table, id.ToString(), "--column", "new_name", "-o", "json")).Should().Be(0, _err.ToString());
        var rows = Out().EnumerateArray().ToList();
        rows.Should().HaveCount(2, "the create and the update of new_name; new_count is not audited");
        rows[0].GetProperty("action").GetString().Should().Be("Update");
        rows[0].GetProperty("column").GetString().Should().Be("new_name");
        rows[0].GetProperty("oldValue").GetString().Should().Be("First " + _run);
        rows[0].GetProperty("newValue").GetString().Should().Be("Second " + _run);
        rows[0].GetProperty("user").GetString().Should().NotBeNullOrEmpty();
        rows[0].GetProperty("date").GetString().Should().NotBeNullOrEmpty();
        rows[1].GetProperty("action").GetString().Should().Be("Create");
        rows[1].GetProperty("newValue").GetString().Should().Be("First " + _run);

        (await RunAsync("audit", "history", table, id.ToString(), "-o", "json")).Should().Be(0, _err.ToString());
        var all = Out().EnumerateArray().ToList();
        all.Should().Contain(r => r.GetProperty("column").GetString() == "new_name");
        all.Should().NotContain(r => r.GetProperty("column").GetString() == "new_count");

        (await RunAsync("audit", "history", table, id.ToString(), "--top", "1", "-o", "json")).Should().Be(0, _err.ToString());
        Out().EnumerateArray().Select(r => r.GetProperty("action").GetString()).Distinct().Should().Equal("Update");
        (await RunAsync("audit", "history", table, id.ToString(), "--column", "new_count", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetArrayLength().Should().Be(0);
        (await RunAsync("audit", "history", table, id.ToString())).Should().Be(0, _err.ToString());
        _out.ToString().Should().Contain("First " + _run).And.Contain("Second " + _run).And.Contain("→");
    }

    [Fact]
    public async Task Audit_status_json_lists_the_settings()
    {
        var table = await NewTableAsync();
        (await RunAsync("audit", "enable")).Should().Be(0, _err.ToString());
        (await RunAsync("audit", "enable", "--table", table)).Should().Be(0, _err.ToString());

        (await RunAsync("audit", "status", "-o", "json")).Should().Be(0, _err.ToString());
        var status = Out();
        status.GetProperty("enabled").GetBoolean().Should().BeTrue();
        status.GetProperty("retentionDays").GetInt32().Should().Be(30);
        status.GetProperty("tables").EnumerateArray().Select(t => t.GetString()).Should().Contain([table, "account"]);

        (await RunAsync("audit", "disable", "--table", table)).Should().Be(0, _err.ToString());
        (await RunAsync("audit", "disable")).Should().Be(0, _err.ToString());
        (await RunAsync("audit", "status", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("enabled").GetBoolean().Should().BeFalse();
        Out().GetProperty("tables").EnumerateArray().Select(t => t.GetString()).Should().NotContain(table);

        (await RunAsync("audit", "status")).Should().Be(0, _err.ToString());
        _out.ToString().Should().Contain("30");
    }

    [Fact]
    public async Task Audit_retention_sets_days_and_forever()
    {
        async Task<int> RetentionAsync()
        {
            (await RunAsync("audit", "status", "-o", "json")).Should().Be(0, _err.ToString());
            return Out().GetProperty("retentionDays").GetInt32();
        }

        (await RunAsync("audit", "retention", "90")).Should().Be(0, _err.ToString());
        _out.ToString().Should().Contain("from now on").And.Contain("xrmd audit delete --before");
        (await RetentionAsync()).Should().Be(90);
        (await RunAsync("audit", "retention", "30")).Should().Be(0, _err.ToString());
        (await RetentionAsync()).Should().Be(30);
        (await RunAsync("audit", "retention", "--forever", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("retentionDays").GetInt32().Should().Be(-1);
        (await RetentionAsync()).Should().Be(-1);

        // The server's 400 is exit 5; nothing changed.
        (await RunAsync("audit", "retention", "0")).Should().Be(5);
        (await RunAsync("audit", "retention", "400000")).Should().Be(5);
        (await RetentionAsync()).Should().Be(-1);

        // Exactly one of <days> and --forever.
        (await RunAsync("audit", "retention")).Should().Be(2);
        (await RunAsync("audit", "retention", "5", "--forever")).Should().Be(2);
        (await RetentionAsync()).Should().Be(-1);
    }

    [Fact]
    public async Task Audit_delete_needs_confirmation()
    {
        var id = Guid.NewGuid();
        (await RunAsync("audit", "delete", "--table", "account", "--id", id.ToString())).Should().Be(2);
        _err.ToString().Should().Contain("Refusing to run a destructive command without --yes");
        (await RunAsync("audit", "delete", "--before", "2020-01-01")).Should().Be(2);
        _err.ToString().Should().Contain("Refusing to run a destructive command without --yes");
        (await RunAsync(new StringReader("n\n"), "audit", "delete", "--before", "2020-01-01")).Should().Be(2, "stdin is not a terminal");

        // One of the two forms, and a valid date or id, before any question.
        (await RunAsync("audit", "delete", "--yes")).Should().Be(2);
        (await RunAsync("audit", "delete", "--table", "account", "--yes")).Should().Be(2);
        (await RunAsync("audit", "delete", "--table", "account", "--id", id.ToString(), "--before", "2020-01-01", "--yes")).Should().Be(2);
        (await RunAsync("audit", "delete", "--before", "not a date", "--yes")).Should().Be(2);
        (await RunAsync("audit", "delete", "--table", "account", "--id", "nope", "--yes")).Should().Be(2);
    }

    [Fact]
    public async Task Audit_delete_removes_the_history_of_a_record()
    {
        var table = await NewTableAsync();
        (await RunAsync("audit", "enable")).Should().Be(0, _err.ToString());
        (await RunAsync("audit", "enable", "--table", table)).Should().Be(0, _err.ToString());
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "Gone " + _run });
        (await RunAsync("audit", "history", table, id.ToString(), "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetArrayLength().Should().BeGreaterThan(0);

        (await RunAsync("audit", "delete", "--table", table, "--id", id.ToString(), "--yes", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("deleted").GetInt32().Should().BeGreaterThan(0);
        (await RunAsync("audit", "history", table, id.ToString(), "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetArrayLength().Should().Be(0);

        (await RunAsync("audit", "delete", "--before", "2000-01-01", "--yes", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("partitionsDeleted").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Audit_history_without_privilege_exits_with_the_server_error()
    {
        var table = await NewTableAsync();
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        var role = await CreateRoleAsync(fx, "audlow-" + _run, ("prvReadAuditSummary", DepthMask.Global));
        await AssignRoleAsync(fx, user, role);
        try
        {
            (await RunAsync("auth", "login", "--profile", "low", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", oid.ToString()))
                .Should().Be(0, _err.ToString());

            // The role may read audit rows but not the record history: the server's 403 names the privilege.
            (await RunAsync("audit", "history", table, Guid.NewGuid().ToString(), "--profile", "low")).Should().Be(3);
            _err.ToString().Should().Contain("prvReadRecordAuditHistory");
            (await RunAsync("audit", "delete", "--table", table, "--id", Guid.NewGuid().ToString(), "--yes", "--profile", "low")).Should().Be(3);
            _err.ToString().Should().Contain("prvDeleteRecordChangeHistory");
            (await RunAsync("audit", "delete", "--before", "2000-01-01", "--yes", "--profile", "low")).Should().Be(3);
            _err.ToString().Should().Contain("prvDeleteAuditPartitions");
        }
        finally
        {
            await RevokeAuditPrivilegesAsync(fx);
        }
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process may run.");
    }
}
