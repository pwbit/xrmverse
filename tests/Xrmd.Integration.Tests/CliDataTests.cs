using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Cli;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>The <c>data</c> and <c>dev</c> groups of <c>xrmd</c> against the in-memory host.</summary>
[Collection("xrmd")]
public sealed class CliDataTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xrmd-cli-data", Guid.NewGuid().ToString("N"));
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        (await RunAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", fx.AdminObjectId.ToString()))
            .Should().Be(0, _err.ToString());
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
        return Task.CompletedTask;
    }

    private Task<int> RunAsync(params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var env = new CliEnvironment(_out, _err, new StringReader(""), false, fx.App.Server.CreateHandler(), Path.Combine(_dir, "config"), new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
        };
        return CliApp.RunAsync(args, env);
    }

    private JsonElement Out() => JsonDocument.Parse(_out.ToString()).RootElement.Clone();

    private OrganizationService Service() => ServiceFor(fx, fx.AdminObjectId);

    // ---- query ----------------------------------------------------------------------------------------------------

    /// <summary>A table <c>new_bulk…</c> with 12,000 rows (one through the Core, the rest cloned in SQL: the test stays fast).</summary>
    private async Task<(string Table, string Set)> BulkTableAsync(int rows)
    {
        var table = $"new_bulk{_run}";
        (await RunAsync("table", "create", table, "--label", "Bulk", "--plural-label", "Bulks")).Should().Be(0, _err.ToString());
        await Service().CreateAsync(new Entity(table) { ["new_name"] = "Bulk 0" });

        // Every column but the row version (the engine sets it), the key a new id, the name numbered (plan 2.7a, Task 6: the
        // catalog through information_schema, the numbers from a cross join of digits, both engines).
        var d = TestSql.Dialect;
        var columns = (await TestSql.ColumnsAsync(fx.ConnectionString, "data", table)).Where(c => c != "versionnumber").ToList();
        var names = string.Join(", ", columns.Select(d.Quote));
        var values = string.Join(", ", columns.Select(c => c == table + "id" ? d.NewGuid : c == "new_name" ? "CONCAT('Bulk ', n.n)" : "t." + d.Quote(c)));
        const string digits = "(VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9))";
        await using var connection = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var insert = TestSql.Command(connection,
            $"WITH n AS (SELECT a.i + 10 * b.i + 100 * c.i + 1000 * e.i + 10000 * f.i + 1 AS n FROM {digits} AS a(i) " +
            $"CROSS JOIN {digits} AS b(i) CROSS JOIN {digits} AS c(i) CROSS JOIN {digits} AS e(i) CROSS JOIN {digits} AS f(i)) " +
            $"INSERT INTO data.{d.Quote(table)} ({names}) SELECT {values} FROM n " +
            $"CROSS JOIN ({d.Limit($"SELECT * FROM data.{d.Quote(table)}", "1")}) t WHERE n.n <= @rows",
            ("@rows", rows - 1));
        insert.CommandTimeout = 120;
        (await insert.ExecuteNonQueryAsync()).Should().Be(rows - 1);
        return (table, fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(table)!.EntitySetName!);
    }

    [Fact]
    public async Task Query_all_returns_every_page_as_one_json_array()
    {
        var (table, set) = await BulkTableAsync(12_000);

        (await RunAsync("data", "query", "--odata", $"{set}?$select=new_name&$orderby=new_name", "--all", "-o", "json")).Should().Be(0, _err.ToString());
        var all = Out();
        all.ValueKind.Should().Be(JsonValueKind.Array);
        all.GetArrayLength().Should().Be(12_000);
        all.EnumerateArray().Select(r => r.GetProperty($"{table}id").GetString()).Distinct().Should().HaveCount(12_000);
        _err.ToString().Should().BeEmpty();

        // Without --all: the first page only, and a hint on stderr.
        (await RunAsync("data", "query", "--odata", $"{set}?$select=new_name", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetArrayLength().Should().Be(5000);
        _err.ToString().Should().Contain("More records available; use --all.");

        // --top n returns n rows and no hint.
        (await RunAsync("data", "query", "--odata", $"{set}?$select=new_name", "--top", "7", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetArrayLength().Should().Be(7);
        _err.ToString().Should().BeEmpty();

        // FetchXML pages with the paging cookie.
        var fetch = $"<fetch><entity name='{table}'><attribute name='new_name'/><order attribute='new_name'/></entity></fetch>";
        (await RunAsync("data", "query", "--fetch", fetch, "--all", "-o", "json")).Should().Be(0, _err.ToString());
        var fetched = Out();
        fetched.GetArrayLength().Should().Be(12_000);
        fetched.EnumerateArray().Select(r => r.GetProperty($"{table}id").GetString()).Distinct().Should().HaveCount(12_000);

        (await RunAsync("data", "query", "--fetch", fetch, "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetArrayLength().Should().Be(5000);
        _err.ToString().Should().Contain("More records available; use --all.");

        // The fetch can come from a file.
        var file = Path.Combine(_dir, "q.xml");
        await File.WriteAllTextAsync(file, fetch);
        (await RunAsync("data", "query", "--fetch", "@" + file, "--top", "3", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Query_needs_exactly_one_of_fetch_and_odata()
    {
        (await RunAsync("data", "query")).Should().Be(2);
        (await RunAsync("data", "query", "--fetch", "<fetch/>", "--odata", "accounts")).Should().Be(2);
        (await RunAsync("data", "query", "--odata", "nosuchsets")).Should().Be(4);
    }

    // ---- export / import --------------------------------------------------------------------------------------------

    private async Task<List<Guid>> CreateProjectsAsync(string stem)
    {
        var service = Service();
        var ids = new List<Guid>();
        var values = new (string Name, decimal? Budget, DateTime? Start, int? Count, double? Ratio, int? Priority, bool Customer)[]
        {
            ($"{stem} plain", 10.5m, new DateTime(2024, 3, 1, 8, 30, 0, DateTimeKind.Utc), 3, 0.25, 2, true),
            ($"{stem} \"quoted\", comma", 0m, null, null, null, null, false),
            ($"{stem} line1\r\nline2", 1234567.89m, new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Utc), -4, 1.5e-3, 3, true),
            ($"{stem} ünïcode ✓", null, null, 0, null, 1, false),
        };
        foreach (var v in values)
        {
            var e = new Entity(TestEntities.Project) { ["pb_name"] = v.Name };
            if (v.Budget is { } b) e["pb_budget"] = new Money(b);
            if (v.Start is { } s) e["pb_startdate"] = s;
            if (v.Count is { } c) e["pb_count"] = c;
            if (v.Ratio is { } r) e["pb_ratio"] = r;
            if (v.Priority is { } p) e["pb_priority"] = new OptionSetValue(p);
            if (v.Customer) e["pb_customerid"] = new EntityReference("systemuser", await AdminUserIdAsync());
            ids.Add(await service.CreateAsync(e));
        }
        return ids;
    }

    private async Task<Guid> AdminUserIdAsync()
    {
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);
        return caller.UserId;
    }

    private async Task<List<JsonElement>> ProjectRowsAsync(string stem)
    {
        (await RunAsync("data", "query", "--odata",
            $"pb_projects?$select=pb_name,pb_budget,pb_startdate,pb_count,pb_ratio,pb_priority,_pb_customerid_value&$filter=startswith(pb_name,'{stem}')&$orderby=pb_name",
            "--all", "-o", "json")).Should().Be(0, _err.ToString());
        return Out().EnumerateArray().ToList();
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    public async Task Export_import_roundtrip(string format)
    {
        var stem = $"rt{format}{_run}";
        var ids = await CreateProjectsAsync(stem);
        var before = await ProjectRowsAsync(stem);
        before.Should().HaveCount(4);

        var file = Path.Combine(_dir, $"projects.{format}");
        (await RunAsync("data", "export", TestEntities.Project, "--out", file, "--format", format, "--filter", $"startswith(pb_name,'{stem}')"))
            .Should().Be(0, _err.ToString());
        var text = await File.ReadAllTextAsync(file);
        text.Should().Contain("ünïcode ✓").And.Contain("systemuser:" + await AdminUserIdAsync());
        if (format == "csv")
        {
            text.Split("\r\n")[0].Split(',').Should().Contain(["pb_projectid", "pb_name", "pb_budget", "pb_customerid", "pb_priority"]);
            text.Should().Contain("\"\"quoted\"\", comma"); // RFC 4180 quoting
            text.Should().Contain("2024-03-01T08:30:00Z");
        }

        foreach (var id in ids) await Service().DeleteAsync(TestEntities.Project, id);
        (await ProjectRowsAsync(stem)).Should().BeEmpty();

        (await RunAsync("data", "import", TestEntities.Project, "--file", file, "--format", format, "--batch", "2", "-o", "json"))
            .Should().Be(0, _err.ToString());
        Out().GetProperty("imported").GetInt32().Should().Be(4);
        Out().GetProperty("failed").GetInt32().Should().Be(0);

        var after = await ProjectRowsAsync(stem);
        after.Should().HaveCount(4);
        for (var i = 0; i < 4; i++)
        {
            foreach (var column in new[] { "pb_name", "pb_budget", "pb_startdate", "pb_count", "pb_ratio", "pb_priority", "_pb_customerid_value" })
                Normalize(after[i], column).Should().Be(Normalize(before[i], column), $"row {i} column {column}");
        }
        after.Select(r => r.GetProperty("pb_projectid").GetString()).Should().BeEquivalentTo(ids.Select(i => i.ToString()));
    }

    /// <summary>
    /// Party lists (plan 2.11, A8) are not properties of the Web API entity type: data export names and skips them instead of
    /// selecting them (which the server refuses), and an import file that has one is a usage error naming it.
    /// </summary>
    [Fact]
    public async Task Export_of_an_activity_names_and_skips_party_lists()
    {
        var contact = await Service().CreateAsync(new Entity("contact") { ["lastname"] = "Party" + _run, ["emailaddress1"] = $"p{_run}@example.com" });
        var to = new EntityCollection([new Entity("activityparty") { ["partyid"] = new EntityReference("contact", contact) }]);
        var subject = "Export " + _run;
        var email = await Service().CreateAsync(new Entity("email") { ["subject"] = subject, ["to"] = to });

        var file = Path.Combine(_dir, "emails.json");
        (await RunAsync("data", "export", "email", "--out", file, "--format", "json", "--filter", $"activityid eq {email}")).Should().Be(0, _err.ToString());
        _err.ToString().Should().Contain("Skipped column 'to' (type PartyList)").And.Contain("Skipped column 'from' (type PartyList)")
            .And.Contain("email_activity_parties").And.Contain("not exported");
        var rows = JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement;
        var row = rows.EnumerateArray().Should().ContainSingle().Subject;
        row.GetProperty("subject").GetString().Should().Be(subject);
        row.TryGetProperty("to", out _).Should().BeFalse();

        var withParty = Path.Combine(_dir, "party.json");
        await File.WriteAllTextAsync(withParty, $$"""[{ "subject": "x", "to": "contact:{{contact}}" }]""");
        (await RunAsync("data", "import", "email", "--file", withParty, "--format", "json")).Should().Be(2);
        _err.ToString().Should().Contain("'to'").And.Contain("PartyList");

        await Service().DeleteAsync("email", email);
        await Service().DeleteAsync("contact", contact);
    }

    /// <summary><c>dev seed</c> on an activity table: rows are created, the party lists are named as not seeded.</summary>
    [Fact]
    public async Task Dev_seed_of_an_activity_names_and_skips_party_lists()
    {
        (await RunAsync("dev", "seed", "task", "--count", "2", "--seed", "7", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("created").GetInt32().Should().Be(2);
        (await RunAsync("dev", "seed", "email", "--count", "2", "--seed", "7", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("created").GetInt32().Should().Be(2);
        _err.ToString().Should().Contain("Skipped column 'to' (type PartyList)").And.Contain("email_activity_parties").And.Contain("not seeded");
    }

    private static string? Normalize(JsonElement row, string column) =>
        row.TryGetProperty(column, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetRawText() : null;

    [Fact]
    public async Task Import_reports_failed_rows_and_exits_5()
    {
        var stem = $"fail{_run}";
        var file = Path.Combine(_dir, "bad.csv");
        var csv = new StringBuilder("pb_name,pb_count,pb_customerid\r\n");
        csv.Append($"{stem} ok1,1,\r\n");
        csv.Append($"{stem} {new string('x', 300)},2,\r\n");        // too long: rejected by the server
        csv.Append($"{stem} ok3,3,systemuser:{await AdminUserIdAsync()}\r\n");
        csv.Append($"{stem} badlookup,4,systemuser:{Guid.NewGuid()}\r\n"); // dangling reference
        csv.Append($"{stem} ok5,5,\r\n");
        await File.WriteAllTextAsync(file, csv.ToString());

        (await RunAsync("data", "import", TestEntities.Project, "--file", file, "--format", "csv")).Should().Be(5);
        var error = _err.ToString();
        error.Should().Contain("Row 3").And.Contain("Row 5").And.Contain("2 of 5 rows failed");
        error.Should().NotContain("Row 2").And.NotContain("Row 4");
        (await ProjectRowsAsync(stem)).Select(r => r.GetProperty("pb_name").GetString()).Should().BeEquivalentTo(
            [$"{stem} ok1", $"{stem} ok3", $"{stem} ok5"]);

        // Same through json output: the failures are part of the result.
        (await RunAsync("data", "import", TestEntities.Project, "--file", file, "--format", "csv", "-o", "json")).Should().Be(5);
        var result = Out();
        result.GetProperty("failed").GetInt32().Should().Be(2);
        result.GetProperty("failures").EnumerateArray().Select(f => f.GetProperty("row").GetInt32()).Should().BeEquivalentTo([3, 5]);
    }

    [Fact]
    public async Task Import_validates_arguments_columns_and_lookup_syntax()
    {
        var file = Path.Combine(_dir, "unknown.csv");
        await File.WriteAllTextAsync(file, "pb_name,pb_nosuch\r\nx,1\r\n");
        (await RunAsync("data", "import", TestEntities.Project, "--file", file, "--format", "csv")).Should().Be(2);
        _err.ToString().Should().Contain("pb_nosuch");

        (await RunAsync("data", "import", TestEntities.Project, "--file", file, "--format", "xml")).Should().Be(2);
        (await RunAsync("data", "import", TestEntities.Project, "--file", Path.Combine(_dir, "missing.csv"), "--format", "csv")).Should().Be(2);
        (await RunAsync("data", "import", "pb_nosuchtable", "--file", file, "--format", "csv")).Should().Be(4);

        var lookup = Path.Combine(_dir, "lookup.csv");
        await File.WriteAllTextAsync(lookup, $"pb_name,pb_customerid\r\nx,{Guid.NewGuid()}\r\n");
        (await RunAsync("data", "import", TestEntities.Project, "--file", lookup, "--format", "csv")).Should().Be(5);
        _err.ToString().Should().Contain("Row 2").And.Contain("table:guid");
    }

    // ---- failures, polymorphic lookups, single records -------------------------------------------------------------------

    [Fact]
    public async Task Import_treats_a_403_on_one_row_as_a_failed_row_not_an_abort()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var child = $"new_c403{_run}";
        var entity = new EntityMetadata { LogicalName = child, SchemaName = child, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([new LookupAttributeMetadata { LogicalName = "new_projectid", SchemaName = "new_ProjectId", Targets = [TestEntities.Project] }]);
        await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });

        // The user may create the child rows but has no append-to on pb_project: only the rows with a lookup are refused (403).
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ($"prvCreate{child}", DepthMask.Global), ($"prvRead{child}", DepthMask.Global), ($"prvAppend{child}", DepthMask.Global));
        var project = await Service().CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = "p403" });
        (await RunAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", objectId.ToString())).Should().Be(0, _err.ToString());

        var file = Path.Combine(_dir, "denied.csv");
        await File.WriteAllTextAsync(file, $"new_name,new_projectid\r\nok1,\r\ndenied2,{TestEntities.Project}:{project}\r\nok3,\r\ndenied4,{TestEntities.Project}:{project}\r\nok5,\r\n");
        (await RunAsync("data", "import", child, "--file", file, "--format", "csv", "--batch", "1")).Should().Be(5, _err.ToString());
        _err.ToString().Should().Contain("Row 3").And.Contain("Row 5").And.Contain("2 of 5 rows failed").And.Contain("prvAppendTo");
        var created = await Service().RetrieveMultipleAsync(new Microsoft.Xrm.Sdk.Query.QueryExpression(child) { ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("new_name") });
        created.Entities.Select(e => e.GetAttributeValue<string>("new_name")).Should().BeEquivalentTo(["ok1", "ok3", "ok5"]);
    }

    [Fact]
    public async Task Import_polymorphic_lookup_uses_the_table_of_the_value()
    {
        await TestEntities.EnsureProjectTagAsync(fx.App.Services.GetRequiredService<IMetadataStore>(), fx.ConnectionString);
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var table = $"new_poly{_run}";
        var entity = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await store.AddAttributeAsync(table, new LookupAttributeMetadata { LogicalName = "new_regardingid", SchemaName = "new_RegardingId", Targets = [TestEntities.Project, TestEntities.Tag] });

        var project = await Service().CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = "poly" });
        var tag = await Service().CreateAsync(new Entity(TestEntities.Tag) { ["new_name"] = "poly" });
        var file = Path.Combine(_dir, "poly.csv");
        await File.WriteAllTextAsync(file,
            $"new_name,new_regardingid\r\non project,{TestEntities.Project}:{project}\r\non tag,{TestEntities.Tag}:{tag}\r\nwrong,systemuser:{await AdminUserIdAsync()}\r\n");

        (await RunAsync("data", "import", table, "--file", file, "--format", "csv")).Should().Be(5);
        _err.ToString().Should().Contain("Row 4").And.Contain("not a target").And.Contain("1 of 3 rows failed");
        var rows = (await Service().RetrieveMultipleAsync(new Microsoft.Xrm.Sdk.Query.QueryExpression(table) { ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("new_name", "new_regardingid") })).Entities;
        rows.Single(e => e.GetAttributeValue<string>("new_name") == "on project").GetAttributeValue<EntityReference>("new_regardingid").Should().Match<EntityReference>(r => r.LogicalName == TestEntities.Project && r.Id == project);
        rows.Single(e => e.GetAttributeValue<string>("new_name") == "on tag").GetAttributeValue<EntityReference>("new_regardingid").Should().Match<EntityReference>(r => r.LogicalName == TestEntities.Tag && r.Id == tag);

        // Export gives the same table:guid values back.
        var export = Path.Combine(_dir, "poly-out.json");
        (await RunAsync("data", "export", table, "--out", export, "--format", "json")).Should().Be(0, _err.ToString());
        var text = await File.ReadAllTextAsync(export);
        text.Should().Contain($"{TestEntities.Project}:{project}").And.Contain($"{TestEntities.Tag}:{tag}");
    }

    [Fact]
    public async Task Query_odata_single_record_and_top_conflicts()
    {
        var id = await Service().CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = $"single{_run}" });
        (await RunAsync("data", "query", "--odata", $"pb_projects({id})?$select=pb_name", "-o", "json")).Should().Be(0, _err.ToString());
        var one = Out();
        one.GetArrayLength().Should().Be(1);
        one[0].GetProperty("pb_name").GetString().Should().Be($"single{_run}");
        (await RunAsync("data", "query", "--odata", $"pb_projects({id})", "--all", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetArrayLength().Should().Be(1);

        (await RunAsync("data", "query", "--odata", "pb_projects?$top=5", "--top", "2")).Should().Be(2);
        (await RunAsync("data", "query", "--odata", "pb_projects?%24top=5", "--top", "2")).Should().Be(2);
        (await RunAsync("data", "query", "--odata", "pb_projects?$select=pb_name&$topx=1", "--top", "2", "-o", "json")).Should().NotBe(2);
        (await RunAsync("data", "query", "--odata", $"pb_projects({id})", "--top", "2")).Should().Be(2);
    }

    // ---- dev seed ---------------------------------------------------------------------------------------------------

    private async Task<string> CreateSeedTableAsync(string name)
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var entity = new EntityMetadata { LogicalName = name, SchemaName = name, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes(
        [
            new StringAttributeMetadata { LogicalName = "new_code", SchemaName = "new_Code", MaxLength = 5, DisplayName = SystemAttributes.CreateLabel("Code", 1033) },
            new IntegerAttributeMetadata { LogicalName = "new_qty", SchemaName = "new_Qty", MinValue = 10, MaxValue = 20 },
            new DecimalAttributeMetadata { LogicalName = "new_rate", SchemaName = "new_Rate", Precision = 2, MinValue = 1m, MaxValue = 2m },
            new DoubleAttributeMetadata { LogicalName = "new_score", SchemaName = "new_Score", Precision = 2, MinValue = -1, MaxValue = 1 },
            new BooleanAttributeMetadata { LogicalName = "new_flag", SchemaName = "new_Flag" },
            new DateTimeAttributeMetadata { LogicalName = "new_when", SchemaName = "new_When" },
            new LookupAttributeMetadata { LogicalName = "new_ownerref", SchemaName = "new_OwnerRef", Targets = ["systemuser"] },
            new PicklistAttributeMetadata
            {
                LogicalName = "new_kind", SchemaName = "new_Kind",
                OptionSet = new OptionSetMetadata(new OptionMetadataCollection
                {
                    new OptionMetadata(SystemAttributes.CreateLabel("A", 1033), 100000000),
                    new OptionMetadata(SystemAttributes.CreateLabel("B", 1033), 100000007),
                }) { IsGlobal = false, OptionSetType = OptionSetType.Picklist },
            },
        ]);
        await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 8 });
        return store.GetEntity(name)!.EntitySetName!;
    }

    private async Task<List<JsonElement>> SeedRowsAsync(string set)
    {
        (await RunAsync("data", "query", "--odata", $"{set}?$select=new_name,new_code,new_qty,new_rate,new_score,new_flag,new_when,_new_ownerref_value,new_kind&$orderby=new_name",
            "--all", "-o", "json")).Should().Be(0, _err.ToString());
        return Out().EnumerateArray().ToList();
    }

    [Fact]
    public async Task Seed_respects_maxlength_and_option_values()
    {
        var one = $"new_seeda{_run}";
        var two = $"new_seedb{_run}";
        var setOne = await CreateSeedTableAsync(one);
        var setTwo = await CreateSeedTableAsync(two);

        (await RunAsync("dev", "seed", one, "--count", "40", "--seed", "7", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("created").GetInt32().Should().Be(40);
        (await RunAsync("dev", "seed", two, "--count", "40", "--seed", "7")).Should().Be(0, _err.ToString());

        var rows = await SeedRowsAsync(setOne);
        rows.Should().HaveCount(40);
        foreach (var row in rows)
        {
            row.GetProperty("new_name").GetString()!.Length.Should().BeLessOrEqualTo(8);
            row.GetProperty("new_code").GetString()!.Length.Should().BeLessOrEqualTo(5);
            row.GetProperty("new_qty").GetInt32().Should().BeInRange(10, 20);
            row.GetProperty("new_rate").GetDecimal().Should().BeInRange(1m, 2m);
            (Math.Round(row.GetProperty("new_rate").GetDecimal(), 2) == row.GetProperty("new_rate").GetDecimal()).Should().BeTrue();
            row.GetProperty("new_score").GetDouble().Should().BeInRange(-1, 1);
            row.GetProperty("new_kind").GetInt32().Should().BeOneOf(100000000, 100000007);
            row.GetProperty("new_when").GetString().Should().NotBeNull();
            // A null lookup is absent from the record.
            if (row.TryGetProperty("_new_ownerref_value", out var lookup) && lookup.ValueKind != JsonValueKind.Null)
                Guid.TryParse(lookup.GetString(), out _).Should().BeTrue();
        }
        var users = (await Service().RetrieveMultipleAsync(new Microsoft.Xrm.Sdk.Query.QueryExpression("systemuser"))).Entities.Select(u => u.Id.ToString()).ToHashSet();
        var refs = rows.Select(r => r.TryGetProperty("_new_ownerref_value", out var v) ? v.GetString() : null).ToList();
        refs.Should().Contain(r => r != null).And.Contain(r => r == null); // a lookup is set to an existing row, or null
        refs.Where(r => r != null).Should().OnlyContain(r => users.Contains(r!));
        rows.Select(r => r.GetProperty("new_kind").GetInt32()).Distinct().Should().HaveCount(2); // random choice reaches both options

        // The same --seed gives the same data (ids and timestamps aside).
        var other = await SeedRowsAsync(setTwo);
        string Shape(JsonElement r) => string.Join("|", new[] { "new_name", "new_code", "new_qty", "new_rate", "new_score", "new_flag", "new_kind", "_new_ownerref_value" }
            .Select(c => r.TryGetProperty(c, out var v) ? v.ToString() : "null"));
        other.Select(Shape).Should().Equal(rows.Select(Shape));

        // Default count is 10; a table that does not exist is a 404.
        (await RunAsync("dev", "seed", one, "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("created").GetInt32().Should().Be(10);
        (await RunAsync("dev", "seed", "new_nosuchtable")).Should().Be(4);
        (await RunAsync("dev", "seed", one, "--count", "0")).Should().Be(2);
    }

    [Fact]
    public async Task Dev_seed_fills_customer_columns()
    {
        var table = $"new_custseed{_run}";
        (await RunAsync("table", "create", table, "--label", "Deal", "--plural-label", "Deals")).Should().Be(0, _err.ToString());
        (await RunAsync("table", "column", "add", table, "new_customerid", "--type", "customer", "--label", "Customer")).Should().Be(0, _err.ToString());
        var account = await Service().CreateAsync(new Entity("account") { ["name"] = $"Seed account {_run}" });
        var contact = await Service().CreateAsync(new Entity("contact") { ["lastname"] = $"Seed contact {_run}" });
        (await RunAsync("dev", "seed", table, "--count", "60", "--seed", "11")).Should().Be(0, _err.ToString());

        var rows = (await Service().RetrieveMultipleAsync(new Microsoft.Xrm.Sdk.Query.QueryExpression(table) { ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("new_customerid") })).Entities;
        rows.Should().HaveCount(60);
        var references = rows.Select(r => r.GetAttributeValue<EntityReference>("new_customerid")).Where(r => r is not null).ToList();
        references.Should().NotBeEmpty();
        references.Should().OnlyContain(r => r.LogicalName == "account" || r.LogicalName == "contact");
        references.Select(r => r.LogicalName).Distinct().Should().BeEquivalentTo(["account", "contact"], "both navigation properties are bound");
        references.Where(r => r.LogicalName == "account").Select(r => r.Id).Should().OnlyContain(id => id != contact);
        references.Where(r => r.LogicalName == "contact").Select(r => r.Id).Should().OnlyContain(id => id != account);
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process may run.");
    }
}
