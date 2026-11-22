using Xrmd.Migration.Users;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xrmd.Admin.Migration;
using Xrmd.Admin.Migration.Import;
using Xrmd.Migration.Records;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The security phase of an import (plan M4a task 2; spec §7.3, §7.4): singletons mapped onto the organization's rows, business
/// units, users by mapping action, teams, members and role assignments written with their exported ids. Synthetic package, invented
/// names, each test on a database of its own.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationSecurityPhaseTests(XrmdFixture fx) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-m4a-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly Guid SourceRoot = Guid.NewGuid(), Child = Guid.NewGuid();
    private static readonly Guid SystemUser = Guid.NewGuid(), Integration = Guid.NewGuid();
    private static readonly Guid Entra = Guid.NewGuid(), EntraOid = Guid.NewGuid(), Remap = Guid.NewGuid(), RemapOid = Guid.NewGuid();
    private static readonly Guid Local = Guid.NewGuid(), Stub = Guid.NewGuid(), Existing = Guid.NewGuid(), Unlisted = Guid.NewGuid(), Unlicensed = Guid.NewGuid();
    private static readonly Guid OwnerTeam = Guid.NewGuid(), GroupTeam = Guid.NewGuid(), AccessTeam = Guid.NewGuid();
    private static readonly Guid AdminRole = Guid.NewGuid(), BasicRole = Guid.NewGuid(), InventedRole = Guid.NewGuid();
    private static readonly Guid Euro = Guid.NewGuid(), Dollar = Guid.NewGuid();

    private static LookupValue Ref(string table, Guid id) => new(table, id);

    private static MigrationRecord User(Guid id, string name, string? upn, Guid? oid, bool disabled = false, Guid? bu = null) =>
        Record(id, [("fullname", new StringValue(name)), .. upn is null ? [] : new[] { ("domainname", (MigrationValue)new StringValue(upn)) },
            .. oid is { } o ? new[] { ("azureactivedirectoryobjectid", (MigrationValue)new GuidValue(o)) } : [],
            ("isdisabled", new BoolValue(disabled)), ("businessunitid", Ref("businessunit", bu ?? SourceRoot))]);

    private string Package(Guid adminOid, string currencyCode = "EUR") => Build(_root, p => p
        .Security("businessunit", "businessunitid",
            Record(SourceRoot, ("name", new StringValue("Contoso"))),
            Record(Child, ("name", new StringValue("Contoso Sales")), ("parentbusinessunitid", Ref("businessunit", SourceRoot))))
        .Security("systemuser", "systemuserid",
            Record(SystemUser, ("fullname", new StringValue("SYSTEM")), ("businessunitid", Ref("businessunit", SourceRoot))),
            Record(Integration, ("fullname", new StringValue("INTEGRATION")), ("businessunitid", Ref("businessunit", SourceRoot))),
            User(Entra, "Ada Example", "ada@example.com", EntraOid, bu: Child),
            User(Remap, "Bruno Example", "bruno@example.com", Guid.NewGuid()),
            User(Local, "Carla Example", "carla@example.com", null),
            User(Stub, "Dario Example", "dario@example.com", Guid.NewGuid()),
            User(Existing, "Elena Example", "elena@example.com", Guid.NewGuid()),
            User(Unlisted, "Franco Example", "franco@example.com", Guid.NewGuid(), disabled: true),
            Record(Unlicensed, ("fullname", new StringValue("Gina Example")), ("domainname", new StringValue("gina@example.com")),
                ("azureactivedirectoryobjectid", new GuidValue(adminOid)), ("businessunitid", Ref("businessunit", SourceRoot))))
        .Security("team", "teamid",
            Record(OwnerTeam, ("name", new StringValue("Sales team")), ("teamtype", new OptionValue(0)), ("businessunitid", Ref("businessunit", Child)),
                ("administratorid", Ref("systemuser", Entra)), ("createdby", Ref("systemuser", Unlisted))),
            Record(GroupTeam, ("name", new StringValue("Group team")), ("teamtype", new OptionValue(2)), ("businessunitid", Ref("businessunit", SourceRoot)),
                ("administratorid", Ref("systemuser", Entra))),
            Record(AccessTeam, ("name", new StringValue("Access team")), ("teamtype", new OptionValue(1)), ("businessunitid", Ref("businessunit", SourceRoot)),
                ("administratorid", Ref("systemuser", Entra))))
        .Security("teammembership", "teammembershipid",
            Record(Guid.NewGuid(), ("teamid", new GuidValue(OwnerTeam)), ("systemuserid", new GuidValue(Entra))),
            Record(Guid.NewGuid(), ("teamid", new GuidValue(GroupTeam)), ("systemuserid", new GuidValue(Local))),
            Record(Guid.NewGuid(), ("teamid", new GuidValue(AccessTeam)), ("systemuserid", new GuidValue(Stub))))
        .Security("role", "roleid",
            Record(AdminRole, ("name", new StringValue("Administrator (renamed)")), ("roletemplateid", Ref("role", new Guid("627090ff-40a3-4053-8790-584edc5be201")))),
            Record(BasicRole, ("name", new StringValue("basic user"))),
            Record(InventedRole, ("name", new StringValue("Invented role"))))
        .Security("systemuserroles", "systemuserroleid",
            Record(Guid.NewGuid(), ("systemuserid", new GuidValue(Entra)), ("roleid", new GuidValue(AdminRole))),
            Record(Guid.NewGuid(), ("systemuserid", new GuidValue(Local)), ("roleid", new GuidValue(BasicRole))),
            Record(Guid.NewGuid(), ("systemuserid", new GuidValue(Stub)), ("roleid", new GuidValue(InventedRole))),
            Record(Guid.NewGuid(), ("systemuserid", new GuidValue(Unlicensed)), ("roleid", new GuidValue(BasicRole))))
        .Security("teamroles", "teamroleid",
            Record(Guid.NewGuid(), ("teamid", new GuidValue(GroupTeam)), ("roleid", new GuidValue(BasicRole))))
        .Table("transactioncurrency", "transactioncurrencyid",
        [
            Record(currencyCode == "EUR" ? Euro : Dollar, ("isocurrencycode", new StringValue(currencyCode)), ("exchangerate", new DecimalValue(1m))),
        ]));

    private static Dictionary<string, string> Options(Guid admin, bool currencyMap = false)
    {
        var users = new JsonObject
        {
            ["users"] = new JsonArray(
                new JsonObject { ["id"] = Entra.ToString(), ["action"] = "entra" },
                new JsonObject { ["id"] = Remap.ToString(), ["action"] = "entra-remap", ["objectid"] = RemapOid.ToString() },
                new JsonObject { ["id"] = Local.ToString(), ["action"] = "local", ["username"] = "carla" },
                new JsonObject { ["id"] = Stub.ToString(), ["action"] = "stub" },
                new JsonObject { ["id"] = Existing.ToString(), ["action"] = "existing", ["target"] = admin.ToString() }),
        };
        var options = new Dictionary<string, string> { ["users"] = UserMapping.Parse(users).ToJson().ToJsonString() };
        if (currencyMap) options["baseCurrencyMap"] = "true";
        return options;
    }

    /// <summary>The package the last <see cref="ImportAsync"/> imported (a re-run imports the same file, its link rows' ids included).</summary>
    private string _package = "";

    private async Task<MigrationPhaseHarness> ImportAsync(string currencyCode = "EUR", bool currencyMap = false)
    {
        var h = MigrationPhaseHarness.Start(fx);
        var admin = await h.AdminIdAsync();
        _package = Package(fx.AdminObjectId, currencyCode);
        await h.RunAsync(_package, Options(admin, currencyMap), new SecurityPhase());
        return h;
    }

    [Fact]
    public async Task Root_bu_and_default_teams_map_onto_target()
    {
        await using var h = await ImportAsync();
        var targetRoot = (Guid)(await h.ScalarAsync("SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL"))!;
        var map = await h.Ledger.LoadMapAsync(default);
        map[SourceRoot].Should().Be(targetRoot);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.businessunit WHERE businessunitid = @id", ("@id", SourceRoot))).Should().Be(0);
        (await h.ScalarAsync("SELECT parentbusinessunitid FROM data.businessunit WHERE businessunitid = @id", ("@id", Child))).Should().Be(targetRoot);
        (await h.ScalarAsync("SELECT name FROM data.businessunit WHERE businessunitid = @id", ("@id", Child))).Should().Be("Contoso Sales");
    }

    [Fact]
    public async Task Users_by_action()
    {
        await using var h = await ImportAsync();
        async Task<object?> Column(Guid id, string column) => await h.ScalarAsync($"SELECT {column} FROM data.systemuser WHERE systemuserid = @id", ("@id", id));
        (await Column(Entra, "azureactivedirectoryobjectid")).Should().Be(EntraOid);
        (await Column(Entra, "businessunitid")).Should().Be(Child);
        (await Column(Remap, "azureactivedirectoryobjectid")).Should().Be(RemapOid);
        (await Column(Local, "domainname")).Should().Be("carla");
        (await Column(Local, "azureactivedirectoryobjectid")).Should().BeNull();
        (await Column(Stub, "isdisabled")).Should().Be(true);
        (await Column(Stub, "azureactivedirectoryobjectid")).Should().BeNull();
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.systemuser WHERE systemuserid = @id", ("@id", Existing))).Should().Be(0);
        (await h.Ledger.LoadMapAsync(default))[Existing].Should().Be(await h.AdminIdAsync());
        var issues = await h.IssuesAsync();
        issues.Should().Contain(i => i.Code == "user-entra-remap" && i.RecordId == Remap);
        issues.Should().Contain(i => i.Code == "user-local" && i.RecordId == Local);
    }

    [Fact]
    public async Task Entra_user_already_in_target_is_mapped()
    {
        await using var h = await ImportAsync();
        (await h.Ledger.LoadMapAsync(default))[Unlicensed].Should().Be(await h.AdminIdAsync());
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.systemuser WHERE systemuserid = @id", ("@id", Unlicensed))).Should().Be(0);
        (await h.IssuesAsync()).Should().Contain(i => i.Code == "user-existing" && i.RecordId == Unlicensed);
    }

    [Fact]
    public async Task Unlisted_user_gets_default_action_and_createdby_resolves()
    {
        await using var h = await ImportAsync();
        (await h.ScalarAsync("SELECT isdisabled FROM data.systemuser WHERE systemuserid = @id", ("@id", Unlisted))).Should().Be(true);
        (await h.ScalarAsync("SELECT createdby FROM data.team WHERE teamid = @id", ("@id", OwnerTeam))).Should().Be(Unlisted);
        (await h.IssuesAsync()).Should().Contain(i => i.Code == "user-stub" && i.RecordId == Unlisted);
    }

    [Fact]
    public async Task System_and_integration_map_to_target_system_user()
    {
        await using var h = await ImportAsync();
        var system = (Guid)(await h.ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE fullname = N'SYSTEM'"))!;
        var map = await h.Ledger.LoadMapAsync(default);
        map[SystemUser].Should().Be(system);
        map[Integration].Should().Be(system);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.systemuser WHERE systemuserid IN (@a, @b)", ("@a", SystemUser), ("@b", Integration))).Should().Be(0);
    }

    [Fact]
    public async Task Base_currency_with_other_iso_code_fails_unless_mapped()
    {
        await using (var h = await ImportAsync())
            (await h.Ledger.LoadMapAsync(default))[Euro].Should().Be(
                (Guid)(await h.ScalarAsync("SELECT basecurrencyid FROM data.organization"))!);

        var failing = MigrationPhaseHarness.Start(fx);
        await using (failing)
        {
            var admin = await failing.AdminIdAsync();
            var run = () => failing.RunAsync(Package(fx.AdminObjectId, "USD"), Options(admin), new SecurityPhase());
            (await run.Should().ThrowAsync<MigrationPhaseException>()).Which.Message.Should().Contain("USD").And.Contain("baseCurrencyMap");
        }

        await using var mapped = await ImportAsync("USD", currencyMap: true);
        (await mapped.Ledger.LoadMapAsync(default))[Dollar].Should().Be((Guid)(await mapped.ScalarAsync("SELECT basecurrencyid FROM data.organization"))!);
        (await mapped.IssuesAsync()).Should().Contain(i => i.Code == "currency-mapped");
    }

    [Fact]
    public async Task Role_assignment_by_template_id_and_name()
    {
        await using var h = await ImportAsync();
        var adminRole = new Guid("627090ff-40a3-4053-8790-584edc5be201");
        var basic = (Guid)(await h.ScalarAsync("SELECT roleid FROM data.role WHERE name = N'Basic User'"))!;
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.systemuserroles WHERE systemuserid = @u AND roleid = @r", ("@u", Entra), ("@r", adminRole))).Should().Be(1);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.systemuserroles WHERE systemuserid = @u AND roleid = @r", ("@u", Local), ("@r", basic))).Should().Be(1);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.systemuserroles WHERE systemuserid = @u", ("@u", Stub))).Should().Be(0);
        (await h.IssuesAsync()).Should().Contain(i => i.Code == "role-unknown" && i.Severity == MigrationIssue.Warning && i.Table == "systemuserroles");
    }

    [Fact]
    public async Task Team_members_and_entra_group_team_become_owner_team()
    {
        await using var h = await ImportAsync();
        (await h.ScalarAsync("SELECT teamtype FROM data.team WHERE teamid = @id", ("@id", GroupTeam))).Should().Be(0);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.teammembership WHERE teamid = @t AND systemuserid = @u", ("@t", OwnerTeam), ("@u", Entra))).Should().Be(1);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.teammembership WHERE teamid = @t AND systemuserid = @u", ("@t", GroupTeam), ("@u", Local))).Should().Be(1);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.teamroles WHERE teamid = @t", ("@t", GroupTeam))).Should().Be(1);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.team WHERE teamid = @id", ("@id", AccessTeam))).Should().Be(0);
        (await h.IssuesAsync()).Should().Contain(i => i.Code == "team-group-to-owner" && i.RecordId == GroupTeam);
        (await h.IssuesAsync()).Should().NotContain(i => i.Severity == MigrationIssue.Error);
    }

    [Fact]
    public async Task Rerun_writes_nothing_twice()
    {
        await using var h = await ImportAsync();
        var admin = await h.AdminIdAsync();
        var before = (await h.IssuesAsync()).Count;
        await h.ScalarAsync("DELETE FROM meta.migration_chunk WHERE runid = @run", ("@run", h.RunId));
        await h.RunAsync(_package, Options(admin), new SecurityPhase());
        var issues = await h.IssuesAsync();
        issues.Should().NotContain(i => i.Severity == MigrationIssue.Error);
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.teammembership WHERE teamid = @t", ("@t", OwnerTeam))).Should().Be(1);
        // A user created by the first run is not taken, on the second, for a user of the organization with the same object id.
        (await h.Ledger.LoadMapAsync(default)).Should().NotContainKey(Entra);
        issues.Should().HaveCount(before, "the same issue is recorded once");
    }

    [Fact]
    public async Task User_mapped_onto_existing_user_gets_no_grants()
    {
        await using var h = await ImportAsync();
        var admin = await h.AdminIdAsync();
        var basic = (Guid)(await h.ScalarAsync("SELECT roleid FROM data.role WHERE name = N'Basic User'"))!;
        (await h.ScalarAsync("SELECT COUNT(*) FROM data.systemuserroles WHERE systemuserid = @u AND roleid = @r", ("@u", admin), ("@r", basic))).Should().Be(0);
        (await h.IssuesAsync()).Should().Contain(i => i.Code == "grant-skipped-existing" && i.Table == "systemuserroles");
    }

    [Fact]
    public async Task Bad_users_json_is_400_before_queue()
    {
        await using var app = fx.WithOwnDatabase(b => b.UseSetting("Pipeline:AsyncWorkerEnabled", "true"));
        var client = app.CreateClient();
        var token = DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl);
        var packageId = await UploadAsync(client, token, Build(_root));
        async Task<(HttpStatusCode, string)> StartAsync(JsonNode users)
        {
            var body = new JsonObject { ["passphrase"] = Passphrase, ["packageId"] = packageId.ToString(), ["users"] = users };
            var req = new HttpRequestMessage(HttpMethod.Post, "admin/migrate/runs") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res = await client.SendAsync(req);
            return (res.StatusCode, await res.Content.ReadAsStringAsync());
        }
        var unknown = await StartAsync(new JsonObject { ["users"] = new JsonArray(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["action"] = "clone" }) });
        unknown.Item1.Should().Be(HttpStatusCode.BadRequest, unknown.Item2);
        unknown.Item2.Should().Contain("clone");
        var oid = Guid.NewGuid().ToString();
        var twice = await StartAsync(new JsonObject
        {
            ["users"] = new JsonArray(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["action"] = "entra-remap", ["objectid"] = oid },
                new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["action"] = "entra-remap", ["objectid"] = oid }),
        });
        twice.Item1.Should().Be(HttpStatusCode.BadRequest, twice.Item2);
        var missing = await StartAsync(new JsonObject
        {
            ["users"] = new JsonArray(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["action"] = "existing", ["target"] = Guid.NewGuid().ToString() }),
        });
        missing.Item1.Should().Be(HttpStatusCode.BadRequest, missing.Item2);
        var runs = new HttpRequestMessage(HttpMethod.Get, "admin/migrate/runs");
        runs.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        JsonNode.Parse(await (await client.SendAsync(runs)).Content.ReadAsStringAsync())!["value"]!.AsArray().Should().BeEmpty();
    }
}
