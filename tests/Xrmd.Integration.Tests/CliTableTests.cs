using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Cli;
using Xrmd.Schema;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>The <c>table</c> group of <c>xrmd</c> against the in-memory host (Web API metadata endpoints).</summary>
[Collection("xrmd")]
public sealed class CliTableTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "xrmd-cli-int", Guid.NewGuid().ToString("N"));
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        (await RunAsync("auth", "login", "--url", fx.BaseUrl, "--mode", "dev", "--object-id", fx.AdminObjectId.ToString()))
            .Should().Be(0, _err.ToString());
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch (DirectoryNotFoundException) { }
        return Task.CompletedTask;
    }

    private Task<int> RunAsync(params string[] args) => RunWithInputAsync(null, args);

    /// <summary>Runs with a terminal (<paramref name="input"/> is what the user types) when <paramref name="input"/> is not null.</summary>
    private Task<int> RunWithInputAsync(string? input, params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        var env = new CliEnvironment(_out, _err, new StringReader(input ?? ""), input is not null, fx.App.Server.CreateHandler(), _configDir, new NoProcess(), TimeProvider.System)
        {
            GetEnvironmentVariable = name => name == "XRMD_DEV_SIGNING_KEY" ? DevTokenFactory.SigningKey : null,
        };
        return CliApp.RunAsync(args, env);
    }

    private JsonElement Out() => JsonDocument.Parse(_out.ToString()).RootElement.Clone();

    private string Name(string stem) => $"new_{stem}{_run}";

    private async Task CreateTableAsync(string name)
    {
        (await RunAsync("table", "create", name, "--label", "Thing", "--plural-label", "Things")).Should().Be(0, _err.ToString());
    }

    [Fact]
    public async Task Table_create_column_add_describe_roundtrip()
    {
        var table = Name("rt");
        var other = Name("rtother");
        var choice = Name("rtchoice");
        await CreateTableAsync(table);
        await CreateTableAsync(other);

        (await RunAsync("table", "choice", "add", choice, "--label", "Priority", "--options", "Low=100000000,High=100000001")).Should().Be(0, _err.ToString());
        (await RunAsync("table", "column", "add", table, "new_notes", "--type", "string", "--label", "Notes", "--max-length", "250")).Should().Be(0, _err.ToString());
        (await RunAsync("table", "column", "add", table, "new_count", "--type", "int")).Should().Be(0, _err.ToString());
        (await RunAsync("table", "column", "add", table, "new_when", "--type", "datetime")).Should().Be(0, _err.ToString());
        (await RunAsync("table", "column", "add", table, "new_prio", "--type", "choice", "--choice", choice)).Should().Be(0, _err.ToString());
        (await RunAsync("table", "column", "add", table, "new_otherid", "--type", "lookup", "--target", other)).Should().Be(0, _err.ToString());
        (await RunAsync("table", "relationship", "add", table, "--to", other, "--many-to-many")).Should().Be(0, _err.ToString());

        (await RunAsync("table", "describe", table, "-o", "json")).Should().Be(0, _err.ToString());
        var described = Out();
        described.GetProperty("name").GetString().Should().Be(table);
        described.GetProperty("ownership").GetString().Should().Be("UserOwned");
        described.GetProperty("primaryNameAttribute").GetString().Should().Be("new_name");
        var columns = described.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        columns["new_notes"].GetProperty("type").GetString().Should().Be("string");
        columns["new_notes"].GetProperty("maxLength").GetInt32().Should().Be(250);
        columns["new_notes"].GetProperty("label").GetString().Should().Be("Notes");
        columns["new_count"].GetProperty("type").GetString().Should().Be("int");
        columns["new_when"].GetProperty("type").GetString().Should().Be("datetime");
        columns["new_prio"].GetProperty("type").GetString().Should().Be("choice");
        columns["new_otherid"].GetProperty("type").GetString().Should().Be("lookup");
        columns["new_otherid"].GetProperty("target").GetString().Should().Be(other);
        columns["new_name"].GetProperty("primaryName").GetBoolean().Should().BeTrue();
        var relationships = described.GetProperty("relationships").EnumerateArray().Select(r => r.GetProperty("kind").GetString()).ToList();
        relationships.Should().Contain(["one-to-many", "many-to-many"]);

        // Table output works too, and list --custom shows the table.
        (await RunAsync("table", "describe", table)).Should().Be(0, _err.ToString());
        _out.ToString().Should().Contain("Columns").And.Contain("new_notes");
        (await RunAsync("table", "list", "--custom", "-o", "json")).Should().Be(0, _err.ToString());
        Out().EnumerateArray().Select(t => t.GetProperty("name").GetString()).Should().Contain([table, other]).And.NotContain("systemuser");

        // Removing a column needs --yes without a terminal, then it is gone.
        (await RunAsync("table", "column", "remove", table, "new_count")).Should().Be(2);
        (await RunAsync("table", "column", "remove", table, "new_count", "--yes")).Should().Be(0, _err.ToString());
        (await RunAsync("table", "describe", table, "-o", "json")).Should().Be(0);
        Out().GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).Should().NotContain("new_count");
        (await RunAsync("table", "column", "remove", table, "new_count", "--yes")).Should().Be(4);
        (await RunAsync("table", "describe", Name("missing"))).Should().Be(4);
    }

    [Fact]
    public async Task One_to_many_relationship_add_creates_a_lookup()
    {
        var parent = Name("otmp");
        var child = Name("otmc");
        await CreateTableAsync(parent);
        await CreateTableAsync(child);

        (await RunAsync("table", "relationship", "add", child, "--to", parent, "-o", "json")).Should().Be(0, _err.ToString());
        var column = Out().GetProperty("column").GetString()!;
        (await RunAsync("table", "describe", child, "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == column)
            .GetProperty("target").GetString().Should().Be(parent);
    }

    [Fact]
    public async Task Table_column_add_customer_creates_both_relationships()
    {
        var table = Name("cust");
        await CreateTableAsync(table);

        (await RunAsync("table", "column", "add", table, "new_customerid", "--type", "customer", "--label", "Customer", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("relationships").EnumerateArray().Select(r => r.GetString()).Should().Equal($"{table}_new_customerid_account", $"{table}_new_customerid_contact");

        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var column = store.GetEntity(table)!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "new_customerid");
        column.AttributeType.Should().Be(AttributeTypeCode.Customer);
        column.Targets.Should().BeEquivalentTo(["account", "contact"]);
        var relationships = store.GetOneToManyRelationships().Where(r => r.ReferencingEntity == table && r.ReferencingAttribute == "new_customerid").ToList();
        relationships.Select(r => (r.SchemaName, r.ReferencedEntity, r.ReferencingEntityNavigationPropertyName)).Should().BeEquivalentTo(
        [
            ($"{table}_new_customerid_account", "account", "new_customerid_account"),
            ($"{table}_new_customerid_contact", "contact", "new_customerid_contact"),
        ]);

        (await RunAsync("table", "describe", table, "-o", "json")).Should().Be(0, _err.ToString());
        var described = Out().GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "new_customerid");
        described.GetProperty("type").GetString().Should().Be("customer");
        described.GetProperty("target").GetString().Should().Be("account, contact");
        Out().GetProperty("relationships").EnumerateArray().Count(r => r.GetProperty("column").ValueKind == JsonValueKind.String
            && r.GetProperty("column").GetString() == "new_customerid").Should().Be(2);

        // A customer column has no --target; a name that is already taken fails (exit 5) without a half-created column.
        (await RunAsync("table", "column", "add", table, "new_other", "--type", "customer", "--target", "account")).Should().Be(2);
        _err.ToString().Should().Contain("no target");
        (await RunAsync("table", "column", "add", table, "new_customerid", "--type", "customer")).Should().Be(5);
        // Removing the column removes both relationships.
        (await RunAsync("table", "column", "remove", table, "new_customerid", "--yes")).Should().Be(0, _err.ToString());
        store.GetOneToManyRelationships().Should().NotContain(r => r.ReferencingEntity == table && r.ReferencingAttribute == "new_customerid");
    }

    [Fact]
    public async Task Unknown_type_is_exit_2_listing_allowed_types()
    {
        var table = Name("ut");
        await CreateTableAsync(table);

        (await RunAsync("table", "column", "add", table, "new_x", "--type", "guid")).Should().Be(2);
        var error = _err.ToString();
        error.Should().Contain("guid");
        foreach (var type in SchemaColumnTypes.Names) error.Should().Contain(type);

        // Parameters the type does not take, and missing ones, are usage errors too.
        (await RunAsync("table", "column", "add", table, "new_x", "--type", "int", "--max-length", "5")).Should().Be(2);
        (await RunAsync("table", "column", "add", table, "new_x", "--type", "lookup")).Should().Be(2);
        (await RunAsync("table", "column", "add", table, "new_x", "--type", "choice")).Should().Be(2);
        (await RunAsync("table", "choice", "add", Name("badopt"), "--label", "X", "--options", "Low")).Should().Be(2);
        (await RunAsync("table", "create", Name("own"), "--label", "X", "--plural-label", "Xs", "--ownership", "team")).Should().Be(2);
    }

    [Fact]
    public async Task Delete_referenced_table_is_exit_5_with_server_message()
    {
        var parent = Name("dp");
        var child = Name("dc");
        await CreateTableAsync(parent);
        await CreateTableAsync(child);
        (await RunAsync("table", "column", "add", child, "new_parentid", "--type", "lookup", "--target", parent)).Should().Be(0, _err.ToString());

        (await RunAsync("table", "delete", parent, "--yes")).Should().Be(5);
        _err.ToString().Should().Contain($"referenced by attribute '{child}.new_parentid'");

        // Without --yes and without a terminal nothing is attempted.
        (await RunAsync("table", "delete", child)).Should().Be(2);
        (await RunAsync("table", "delete", child, "--yes")).Should().Be(0, _err.ToString());
        (await RunAsync("table", "delete", parent, "--yes")).Should().Be(0, _err.ToString());
        (await RunAsync("table", "delete", parent, "--yes")).Should().Be(4);
    }

    [Fact]
    public async Task Duplicate_table_and_unprefixed_default_primary_name()
    {
        var table = Name("dup");
        await CreateTableAsync(table);
        (await RunAsync("table", "create", table, "--label", "Thing", "--plural-label", "Things")).Should().Be(5);
        (await RunAsync("table", "create", "noprefix", "--label", "X", "--plural-label", "Xs")).Should().Be(2);
        _err.ToString().Should().Contain("--primary-name");
    }

    [Fact]
    public async Task Solution_option_is_sent_as_the_solution_header()
    {
        var table = Name("sol");
        (await RunAsync("table", "create", table, "--label", "Thing", "--plural-label", "Things", "--solution", "NoSuchSolution")).Should().Be(4, _err.ToString());
        (await RunAsync("table", "create", table, "--label", "Thing", "--plural-label", "Things", "--solution", "Default")).Should().Be(0, _err.ToString());
    }

    [Fact]
    public async Task Relationship_add_honours_name_and_reports_referencing_and_referenced()
    {
        var parent = Name("rnp");
        var child = Name("rnc");
        await CreateTableAsync(parent);
        await CreateTableAsync(child);
        var oneToMany = Name("custom_otm");
        var manyToMany = Name("custom_mtm");

        (await RunAsync("table", "relationship", "add", child, "--to", parent, "--name", oneToMany, "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("relationship").GetString().Should().Be(oneToMany);
        Out().GetProperty("referencing").GetString().Should().Be(child);
        Out().GetProperty("referenced").GetString().Should().Be(parent);
        (await RunAsync("table", "relationship", "add", child, "--to", parent, "--many-to-many", "--name", manyToMany, "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("relationship").GetString().Should().Be(manyToMany);

        (await RunAsync("table", "describe", child, "-o", "json")).Should().Be(0, _err.ToString());
        var relationships = Out().GetProperty("relationships").EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!);
        relationships[oneToMany].GetProperty("referencing").GetString().Should().Be(child);
        relationships[oneToMany].GetProperty("referenced").GetString().Should().Be(parent);
        relationships.Should().ContainKey(manyToMany);
    }

    [Fact]
    public async Task Solution_option_puts_relationship_and_choice_in_the_solution()
    {
        var solutionName = Name("sol");
        var solutionId = await ServiceFor(fx, fx.AdminObjectId).CreateAsync(new Entity("solution")
        {
            ["uniquename"] = solutionName, ["friendlyname"] = solutionName, ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", SystemSeed.DefaultPublisherId),
        });
        var parent = Name("sp");
        var child = Name("sc");
        await CreateTableAsync(parent);
        await CreateTableAsync(child);

        (await RunAsync("table", "relationship", "add", child, "--to", parent, "--solution", solutionName)).Should().Be(0, _err.ToString());
        (await RunAsync("table", "relationship", "add", child, "--to", parent, "--many-to-many", "--solution", solutionName)).Should().Be(0, _err.ToString());
        (await RunAsync("table", "choice", "add", Name("solchoice"), "--label", "C", "--options", "A=1", "--solution", solutionName)).Should().Be(0, _err.ToString());

        const int OptionSet = 9, Relationship = 10;
        (await ComponentRowsAsync(solutionId, Relationship)).Should().Be(2);
        (await ComponentRowsAsync(solutionId, OptionSet)).Should().Be(1);

        // An unknown solution is the server's 404 (exit 4) for each of them.
        (await RunAsync("table", "relationship", "add", child, "--to", parent, "--many-to-many", "--name", Name("nosol"), "--solution", "NoSuchSolution")).Should().Be(4);
        (await RunAsync("table", "choice", "add", Name("nosolchoice"), "--label", "C", "--options", "A=1", "--solution", "NoSuchSolution")).Should().Be(4);
    }

    private async Task<int> ComponentRowsAsync(Guid solutionId, int type)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @s AND componenttype = @t");
        cmd.Parameters.AddWithValue("@s", solutionId);
        cmd.Parameters.AddWithValue("@t", type);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static readonly string[] FileTypes = [SchemaColumnTypes.File, SchemaColumnTypes.Image];

    public static TheoryData<string> ColumnTypes() => new(SchemaColumnTypes.Names.Except(FileTypes));

    public static TheoryData<string> FileColumnTypes() => new(FileTypes);

    [Theory]
    [MemberData(nameof(ColumnTypes))]
    public Task Every_schema_column_type_can_be_added_and_is_described_as_the_same_type(string type) => AddAndDescribeAsync(type);

    [Theory]
    [MemberData(nameof(FileColumnTypes))]
    public Task File_and_image_column_types_can_be_added_and_are_described_as_the_same_type(string type) => AddAndDescribeAsync(type);

    private async Task AddAndDescribeAsync(string type)
    {
        var table = Name("ty" + type);
        var other = Name("tyo" + type);
        var choice = Name("tych" + type);
        await CreateTableAsync(table);
        await CreateTableAsync(other);
        (await RunAsync("table", "choice", "add", choice, "--label", "C", "--options", "A=100000000,B=100000001")).Should().Be(0, _err.ToString());

        var extra = type switch
        {
            SchemaColumnTypes.Lookup => new[] { "--target", other },
            SchemaColumnTypes.Choice or SchemaColumnTypes.Choices => ["--choice", choice],
            SchemaColumnTypes.String or SchemaColumnTypes.Memo => ["--max-length", "123"],
            _ => [],
        };
        (await RunAsync(["table", "column", "add", table, "new_col", "--type", type, .. extra])).Should().Be(0, _err.ToString());

        (await RunAsync("table", "describe", table, "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "new_col")
            .GetProperty("type").GetString().Should().Be(type);
    }

    [Fact]
    public async Task File_and_image_columns_take_their_options()
    {
        var table = Name("media");
        await CreateTableAsync(table);

        (await RunAsync("table", "column", "add", table, "new_doc", "--type", "file", "--max-size-in-kb", "2048")).Should().Be(0, _err.ToString());
        (await RunAsync("table", "column", "add", table, "new_photo", "--type", "image")).Should().Be(0, _err.ToString());
        (await RunAsync("table", "column", "add", table, "new_logo", "--type", "image", "--max-size-in-kb", "512", "--can-store-full-image", "--primary"))
            .Should().Be(0, _err.ToString());

        var attributes = fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(table)!.Attributes;
        attributes.OfType<FileAttributeMetadata>().Single().MaxSizeInKB.Should().Be(2048);
        var logo = attributes.OfType<ImageAttributeMetadata>().Single(a => a.LogicalName == "new_logo");
        (logo.MaxSizeInKB, logo.CanStoreFullImage, logo.IsPrimaryImage).Should().Be((512, true, true));
        attributes.OfType<ImageAttributeMetadata>().Single(a => a.LogicalName == "new_photo").IsPrimaryImage.Should().BeFalse();

        (await RunAsync("table", "column", "add", table, "new_x", "--type", "string", "--max-size-in-kb", "5")).Should().Be(2);
        (await RunAsync("table", "column", "add", table, "new_x", "--type", "file", "--primary")).Should().Be(2);
        (await RunAsync("table", "column", "add", table, "new_x", "--type", "image", "--max-size-in-kb", "30721")).Should().Be(2);
    }

    // ---- plan 2.11: custom activities, notes and activities ------------------------------------------------------------

    private async Task<JsonElement> DescribeAsync(string table)
    {
        (await RunAsync("table", "describe", table, "-o", "json")).Should().Be(0, _err.ToString());
        return Out();
    }

    private static (bool IsActivity, bool HasNotes, bool HasActivities) Flags(JsonElement described) =>
        (described.GetProperty("isActivity").GetBoolean(), described.GetProperty("hasNotes").GetBoolean(), described.GetProperty("hasActivities").GetBoolean());

    [Fact]
    public async Task Create_activity_table()
    {
        var table = Name("sms");
        (await RunAsync("table", "create", table, "--label", "SMS", "--plural-label", "SMS messages", "--activity", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("primaryName").GetString().Should().Be("subject");

        var described = await DescribeAsync(table);
        Flags(described).Should().Be((true, true, false));
        described.GetProperty("primaryIdAttribute").GetString().Should().Be("activityid");
        described.GetProperty("primaryNameAttribute").GetString().Should().Be("subject");
        var subject = described.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "subject");
        subject.GetProperty("label").GetString().Should().Be("Subject");
        fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(table)!.Attributes
            .Single(a => a.LogicalName == "subject").SchemaName.Should().Be("Subject");

        // --notes and --activities on an ordinary table; the text output names the three settings too.
        var site = Name("site");
        (await RunAsync("table", "create", site, "--label", "Site", "--plural-label", "Sites", "--notes", "--activities")).Should().Be(0, _err.ToString());
        Flags(await DescribeAsync(site)).Should().Be((false, true, true));
        (await RunAsync("table", "describe", site)).Should().Be(0, _err.ToString());
        _out.ToString().Should().Contain("isActivity").And.Contain("hasNotes").And.Contain("hasActivities");

        // An activity cannot have activities: the server's refusal (exit 5), nothing created.
        (await RunAsync("table", "create", Name("smsact"), "--label", "S", "--plural-label", "Ss", "--activity", "--activities")).Should().Be(5);
        (await RunAsync("table", "describe", Name("smsact"))).Should().Be(4);
        // An activity is user owned: an organization-owned one is the server's refusal too.
        (await RunAsync("table", "create", Name("smsorg"), "--label", "S", "--plural-label", "Ss", "--activity", "--ownership", "organization")).Should().Be(5);
        _err.ToString().Should().Contain(Name("smsorg"));
        (await RunAsync("table", "describe", Name("smsorg"))).Should().Be(4);
    }

    [Fact]
    public async Task Activity_with_primary_name_exits_2()
    {
        var table = Name("smspn");
        (await RunAsync("table", "create", table, "--label", "SMS", "--plural-label", "SMSs", "--activity", "--primary-name", "new_title")).Should().Be(2);
        _err.ToString().Should().Contain("--primary-name").And.Contain("subject");
        (await RunAsync("table", "describe", table)).Should().Be(4);
    }

    [Fact]
    public async Task Set_notes_on_then_describe()
    {
        var table = Name("setn");
        await CreateTableAsync(table);
        Flags(await DescribeAsync(table)).Should().Be((false, false, false));

        (await RunAsync("table", "set", table, "--notes", "--yes", "-o", "json")).Should().Be(0, _err.ToString());
        Flags(await DescribeAsync(table)).Should().Be((false, true, false));
        (await RunAsync("table", "set", table, "--activities", "--yes")).Should().Be(0, _err.ToString());
        Flags(await DescribeAsync(table)).Should().Be((false, true, true));
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        store.GetEntity(table)!.HasNotes.Should().BeTrue();
        store.GetEntity("annotation")!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "objectid").Targets.Should().Contain(table);

        // Already on: nothing to confirm, nothing sent.
        (await RunAsync("table", "set", table, "--notes", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("alreadyOn").EnumerateArray().Select(e => e.GetString()).Should().Equal("hasNotes");

        // An activity table cannot get activities: the server's refusal (exit 5).
        var sms = Name("setsms");
        (await RunAsync("table", "create", sms, "--label", "SMS", "--plural-label", "SMSs", "--activity")).Should().Be(0, _err.ToString());
        (await RunAsync("table", "set", sms, "--activities", "--yes")).Should().Be(5);
        (await RunAsync("table", "set", Name("nosuch"), "--notes", "--yes")).Should().Be(4);
    }

    // ---- plan 4.1a: queues ----------------------------------------------------------------------------------------------

    private static bool Queues(JsonElement described) => described.GetProperty("isValidForQueue").GetBoolean();

    [Fact]
    public async Task Queues_on_create_and_set_then_describe()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        string[] Targets() => store.GetEntity("queueitem")!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "objectid").Targets;

        var ticket = Name("qticket");
        (await RunAsync("table", "create", ticket, "--label", "Ticket", "--plural-label", "Tickets", "--queues")).Should().Be(0, _err.ToString());
        Queues(await DescribeAsync(ticket)).Should().BeTrue();
        Targets().Should().Contain(ticket);
        // An activity is queue-enabled without the flag.
        var sms = Name("qsms");
        (await RunAsync("table", "create", sms, "--label", "SMS", "--plural-label", "SMSs", "--activity")).Should().Be(0, _err.ToString());
        Queues(await DescribeAsync(sms)).Should().BeTrue();

        var plain = Name("qplain");
        await CreateTableAsync(plain);
        Queues(await DescribeAsync(plain)).Should().BeFalse();
        (await RunAsync("table", "set", plain, "--queues", "--yes", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("switchedOn").EnumerateArray().Select(e => e.GetString()).Should().Equal("isValidForQueue");
        Queues(await DescribeAsync(plain)).Should().BeTrue();
        store.GetEntity(plain)!.IsValidForQueue!.Value.Should().BeTrue();
        Targets().Should().Contain(plain);
        (await RunAsync("table", "set", plain, "--queues", "-o", "json")).Should().Be(0, _err.ToString());
        Out().GetProperty("alreadyOn").EnumerateArray().Select(e => e.GetString()).Should().Equal("isValidForQueue");

        // A table that cannot be put in a queue: the server's refusal (exit 5).
        (await RunAsync("table", "set", "annotation", "--queues", "--yes")).Should().Be(5);
        _err.ToString().Should().Contain("IsValidForQueue");
    }

    /// <summary>Decision 4 (plan 4.1a): the contact center adds its columns to the platform's queue table.</summary>
    [Fact]
    public async Task Queue_takes_a_custom_column()
    {
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        var column = Name("maxwait");
        try
        {
            (await RunAsync("table", "column", "add", "queue", column, "--type", "int")).Should().Be(0, _err.ToString());
            store.GetEntity("queue")!.Attributes.Should().Contain(a => a.LogicalName == column && a is IntegerAttributeMetadata);
            var described = await DescribeAsync("queue");
            described.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).Should().Contain(column);
        }
        finally
        {
            await TestEntities.InSessionAsync(fx.ConnectionString, s => store.DeleteAttributeAsync(s, "queue", column, default));
        }
    }

    [Fact]
    public async Task Set_asks_at_a_terminal_and_sends_nothing_when_refused()
    {
        var table = Name("setask");
        await CreateTableAsync(table);

        (await RunWithInputAsync("n\n", "table", "set", table, "--notes")).Should().Be(1);
        _err.ToString().Should().Contain("cannot be switched off").And.Contain("Aborted");
        Flags(await DescribeAsync(table)).Should().Be((false, false, false));

        (await RunWithInputAsync("y\n", "table", "set", table, "--notes")).Should().Be(0, _err.ToString());
        Flags(await DescribeAsync(table)).Should().Be((false, true, false));
    }

    [Fact]
    public async Task Set_needs_yes_without_tty()
    {
        var table = Name("sety");
        await CreateTableAsync(table);

        (await RunAsync("table", "set", table, "--notes")).Should().Be(2);
        _err.ToString().Should().Contain("--yes");
        Flags(await DescribeAsync(table)).Should().Be((false, false, false));
    }

    [Fact]
    public async Task Switching_off_is_not_offered()
    {
        var table = Name("setoff");
        await CreateTableAsync(table);
        (await RunAsync("table", "set", table, "--notes", "--yes")).Should().Be(0, _err.ToString());

        (await RunAsync("table", "set", table, "--no-notes", "--yes")).Should().Be(2);
        (await RunAsync("table", "set", table, "--notes", "false", "--yes")).Should().Be(2);
        (await RunAsync("table", "set", table, "--activities", "false", "--yes")).Should().Be(2);
        // Nothing to switch on is a usage error too.
        (await RunAsync("table", "set", table, "--yes")).Should().Be(2);
        Flags(await DescribeAsync(table)).Should().Be((false, true, false));
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No process may run.");
    }
}
