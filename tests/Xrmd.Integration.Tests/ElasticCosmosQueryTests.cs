using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan #78, task 4: <c>ExecuteCosmosSqlQuery</c> on an elastic table through the organization service: the projection and filter
/// of the sample shape, the partition scope (J8), paging over a non-unique order column (J7), read and field security
/// (Review Focus 4), the refusals naming their construct and the plug-in view of the message (J9).
/// </summary>
[Collection("xrmd")]
public sealed class ElasticCosmosQueryTests(XrmdFixture fx)
{
    private const string Secret = "new_secret";
    private static readonly SemaphoreSlim TableLock = new(1, 1);
    private static string? _table;
    private static long _sequence;

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    /// <summary>The shared elastic table: <c>new_type</c> String, <c>new_value</c> Integer, <c>new_energy</c> JSON, <c>new_secret</c> secured JSON.</summary>
    private async Task<string> TableAsync()
    {
        await TableLock.WaitAsync();
        try
        {
            if (_table is not null) return _table;
            var name = "new_sensor" + Guid.NewGuid().ToString("N")[..6];
            var entity = TestHelpers.Entity(name, name, "Sensor");
            entity.TableType = ElasticTables.TableTypeElastic;
            await Admin.ExecuteAsync(new CreateEntityRequest
            {
                Entity = entity,
                PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
            });
            await AddAsync(name, new StringAttributeMetadata { LogicalName = "new_type", SchemaName = "new_Type", MaxLength = 100, DisplayName = Label("Type") });
            await AddAsync(name, new IntegerAttributeMetadata { LogicalName = "new_value", SchemaName = "new_Value", DisplayName = Label("Value") });
            await AddAsync(name, new StringAttributeMetadata
            {
                LogicalName = "new_energy", SchemaName = "new_Energy", MaxLength = 4000, DisplayName = Label("Energy"), FormatName = StringFormatName.Json,
            });
            await AddAsync(name, new StringAttributeMetadata
            {
                LogicalName = Secret, SchemaName = "new_Secret", MaxLength = 4000, DisplayName = Label("Secret"), FormatName = StringFormatName.Json, IsSecured = true,
            });
            await AddAsync(name, new StringAttributeMetadata
            {
                LogicalName = "new_hidden", SchemaName = "new_Hidden", MaxLength = 100, DisplayName = Label("Hidden"), IsSecured = true,
            });
            return _table = name;
        }
        finally
        {
            TableLock.Release();
        }
    }

    private Task AddAsync(string table, AttributeMetadata attribute) =>
        Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = table, Attribute = attribute });

    /// <summary>Ids that sort the same way as Guids and as SQL Server uniqueidentifiers: only the last six bytes differ.</summary>
    private static Guid NextId() => new($"00000000-0000-0000-0000-{Interlocked.Increment(ref _sequence):D12}");

    private static string Tag() => "t" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<Guid> RowAsync(OrganizationService svc, string table, string tag, string name, int? value = null, string? energy = null,
        string? secret = null, string? partition = null)
    {
        var e = new Entity(table, NextId()) { ["new_name"] = name, ["new_type"] = tag };
        if (value is not null) e["new_value"] = value;
        if (energy is not null) e["new_energy"] = energy;
        if (secret is not null) e[Secret] = secret;
        if (partition is not null) e[ElasticTables.PartitionId] = partition;
        return await svc.CreateAsync(e);
    }

    private sealed record Page(bool HasMore, string? Cookie, string Json)
    {
        public JsonArray Rows => JsonNode.Parse(Json)!.AsArray();

        public string[] Names(string property = "n") => [.. Rows.Select(r => (string)r![property]!)];
    }

    private static async Task<Page> QueryAsync(OrganizationService svc, string table, string text, ParameterCollection? parameters = null,
        object? pageSize = null, string? cookie = null, string? partition = null)
    {
        var request = new OrganizationRequest("ExecuteCosmosSqlQuery") { ["QueryText"] = text, ["EntityLogicalName"] = table };
        if (parameters is not null) request["QueryParameters"] = parameters;
        if (pageSize is not null) request["PageSize"] = pageSize;
        if (cookie is not null) request["PagingCookie"] = cookie;
        if (partition is not null) request["PartitionId"] = partition;
        var response = await svc.ExecuteAsync(request);
        response.ResponseName.Should().Be("ExecuteCosmosSqlQuery");
        var result = response.Results["Result"].Should().BeOfType<Entity>().Subject;
        result.LogicalName.Should().Be("expando");
        var hasMore = result.GetAttributeValue<bool>("HasMore");
        var pagingCookie = result.Contains("PagingCookie") ? result.GetAttributeValue<string>("PagingCookie") : null;
        if (!hasMore) pagingCookie.Should().BeNull("the cookie is returned only when more rows follow");
        return new Page(hasMore, pagingCookie, result.GetAttributeValue<string>("Result"));
    }

    private static ParameterCollection Tagged(string tag) => new() { ["@t"] = tag };

    private const string ByTag = "c.props.new_type = @t";

    // ---- the sample shape -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Learn_sample_shape_returns_filtered_projection()
    {
        var table = await TableAsync();
        var tag = Tag();
        await RowAsync(Admin, table, tag, "d1", energy: """{"power":7,"unit":"W"}""");
        await RowAsync(Admin, table, tag, "d2", energy: """{"power":3}""");
        await RowAsync(Admin, table, tag, "d3", energy: """{"power":9.5}""", partition: "p2");
        await RowAsync(Admin, table, tag, "d4");
        await RowAsync(Admin, table, "other" + tag, "d5", energy: """{"power":8}""");

        var page = await QueryAsync(Admin, table,
            "SELECT c.props.new_name AS deviceId, c.props.new_energy.power FROM c WHERE c.props.new_type = @type AND c.props.new_energy.power > @power",
            new ParameterCollection { ["@type"] = tag, ["@power"] = 5 });

        page.HasMore.Should().BeFalse();
        page.Cookie.Should().BeNull();
        page.Json.Should().Be("""[{"deviceId":"d1","power":7},{"deviceId":"d3","power":9.5}]""");

        // A row whose column is null has no such property.
        var all = await QueryAsync(Admin, table, "SELECT c.props.new_name AS deviceId, c.props.new_energy.power FROM c WHERE c.props.new_type = @type",
            new ParameterCollection { ["@type"] = tag });
        all.Json.Should().Be("""[{"deviceId":"d1","power":7},{"deviceId":"d2","power":3},{"deviceId":"d3","power":9.5},{"deviceId":"d4"}]""");
    }

    [Fact]
    public async Task Whole_json_column_and_missing_paths_follow_undefined_rules()
    {
        var table = await TableAsync();
        var tag = Tag();
        await RowAsync(Admin, table, tag, "a", energy: """{"list":[10,{"x":1}],"s":"text"}""");

        var page = await QueryAsync(Admin, table,
            "SELECT c.props.new_energy AS whole, c.props.new_energy.list[1].x AS x, c.props.new_energy.list[5] AS gone, " +
            "c.props.new_energy.s.deeper AS scalar, c.props.new_energy.s[0] AS idx, c.props.new_energy.list.k AS prop FROM c WHERE " + ByTag,
            Tagged(tag));

        page.Json.Should().Be("""[{"whole":{"list":[10,{"x":1}],"s":"text"},"x":1}]""");
    }

    [Fact]
    public async Task Number_literal_against_integer_column()
    {
        var table = await TableAsync();
        var tag = Tag();
        foreach (var v in new[] { 1, 2, 3 }) await RowAsync(Admin, table, tag, "v" + v, value: v);

        (await QueryAsync(Admin, table, $"SELECT c.props.new_name AS n FROM c WHERE {ByTag} AND c.props.new_value > 1", Tagged(tag)))
            .Names().Should().Equal("v2", "v3");
        (await QueryAsync(Admin, table, $"SELECT c.props.new_name AS n FROM c WHERE {ByTag} AND c.props.new_value = 2", Tagged(tag)))
            .Names().Should().Equal("v2");

        var fault = await FaultAsync(() => QueryAsync(Admin, table, $"SELECT c.props.new_name AS n FROM c WHERE {ByTag} AND c.props.new_value = 1.5", Tagged(tag)));
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task Parameter_keys_and_page_size_forms()
    {
        var table = await TableAsync();
        var tag = Tag();
        await RowAsync(Admin, table, tag, "a");
        await RowAsync(Admin, table, tag, "b");
        var text = $"SELECT c.props.new_name AS n FROM c WHERE {ByTag}";

        // A key without '@' names the same parameter; an int PageSize is as good as a long.
        var page = await QueryAsync(Admin, table, text, new ParameterCollection { ["t"] = tag }, pageSize: 1);
        page.Names().Should().Equal("a");
        page.HasMore.Should().BeTrue();

        var twice = await FaultAsync(() => QueryAsync(Admin, table, text, new ParameterCollection { ["@t"] = tag, ["t"] = tag }));
        twice.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        twice.Message.Should().Be("ExecuteCosmosSqlQuery: the parameter '@t' is given twice.");

        var notText = await FaultAsync(() => Admin.ExecuteAsync(new OrganizationRequest("ExecuteCosmosSqlQuery") { ["QueryText"] = 5, ["EntityLogicalName"] = table }));
        notText.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        notText.HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task Non_ascii_text_is_not_escaped()
    {
        var table = await TableAsync();
        var tag = Tag();
        await RowAsync(Admin, table, tag, "caf\u00e9", energy: """{"s":"\u00e9 <b>"}""");

        var page = await QueryAsync(Admin, table, $"SELECT c.props.new_name AS n, c.props.new_energy.s FROM c WHERE {ByTag}", Tagged(tag));

        page.Json.Should().Be("[{\"n\":\"caf\u00e9\",\"s\":\"\u00e9 <b>\"}]");
    }

    // ---- partition, standard tables, refusals -----------------------------------------------------------------------------

    [Fact]
    public async Task PartitionId_scopes_the_query()
    {
        var table = await TableAsync();
        var tag = Tag();
        await RowAsync(Admin, table, tag, "a1", partition: "pa");
        await RowAsync(Admin, table, tag, "a2", partition: "pa");
        await RowAsync(Admin, table, tag, "b1", partition: "pb");
        var text = $"SELECT c.props.new_name AS n FROM c WHERE {ByTag}";

        (await QueryAsync(Admin, table, text, Tagged(tag), partition: "pa")).Names().Should().Equal("a1", "a2");
        (await QueryAsync(Admin, table, text, Tagged(tag), partition: "pb")).Names().Should().Equal("b1");
        (await QueryAsync(Admin, table, text, Tagged(tag))).Names().Should().Equal("a1", "a2", "b1");
        (await QueryAsync(Admin, table, $"SELECT c.props.new_name AS n FROM c WHERE {ByTag} OR c.props.new_name = 'zz'", Tagged(tag), partition: "pb"))
            .Names().Should().Equal(["b1"], "an OR cannot escape the partition");
    }

    [Fact]
    public async Task Standard_table_is_refused()
    {
        var fault = await FaultAsync(() => QueryAsync(Admin, "account", "SELECT c.props.name FROM c"));

        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Be("ExecuteCosmosSqlQuery applies to elastic tables only; 'account' is not an elastic table.");
    }

    [Fact]
    public async Task Unknown_table_and_bad_parameters_are_400()
    {
        var table = await TableAsync();
        var unknown = await FaultAsync(() => QueryAsync(Admin, "new_nosuchtable", "SELECT c.props.x FROM c"));
        unknown.Message.Should().Contain("was not found in the MetadataCache");

        var longText = "SELECT c.props.new_name FROM c WHERE c.props.new_type = '" + new string('a', 32769) + "'";
        (await FaultAsync(() => QueryAsync(Admin, table, longText))).Message
            .Should().Be("ExecuteCosmosSqlQuery: QueryText must be at most 32768 characters.");

        foreach (var size in new long[] { 0, 5001, -1 })
            (await FaultAsync(() => QueryAsync(Admin, table, "SELECT c.props.new_name FROM c", pageSize: size))).ErrorCode
                .Should().Be(ErrorCodes.InvalidArgument);
        (await FaultAsync(() => QueryAsync(Admin, table, "SELECT c.props.new_name FROM c WHERE c.props.new_type = @missing"))).Message
            .Should().Be("ExecuteCosmosSqlQuery: the parameter '@missing' has no value.");
        (await FaultAsync(() => QueryAsync(Admin, table, "SELECT c.props.new_name FROM c WHERE c.props.new_type = @d",
            new ParameterCollection { ["@d"] = DateTime.UtcNow }))).Message.Should().Contain("'@d'").And.Contain("System.DateTime");
        (await FaultAsync(() => QueryAsync(Admin, table, "SELECT c.props.new_name FROM c", cookie: "not a cookie!"))).Message
            .Should().Be("ExecuteCosmosSqlQuery: the PagingCookie is not valid.");
        (await FaultAsync(() => QueryAsync(Admin, table, "SELECT c.props.new_name FROM c", partition: new string('p', 2000)))).ErrorCode
            .Should().Be(ErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task Refusals_are_400_naming_the_construct()
    {
        var table = await TableAsync();
        foreach (var (text, construct) in new[]
                 {
                     ("SELECT * FROM c", "SELECT *"),
                     ("SELECT TOP 1 c.props.new_name FROM c", "TOP"),
                     ("SELECT c.props.new_name FROM c JOIN t IN c.props.new_energy.list", "JOIN"),
                 })
        {
            var fault = await FaultAsync(() => QueryAsync(Admin, table, text));
            fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument, text);
            fault.HttpStatus.Should().Be(400, text);
            fault.Message.Should().Be($"ExecuteCosmosSqlQuery: {construct} is not supported by xrmverse.", text);
        }
    }

    // ---- paging -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Paging_with_order_by_returns_each_row_once()
    {
        var table = await TableAsync();
        var tag = Tag();
        // Repeated values: ties fall back to the row id, compared as SQL Server orders a uniqueidentifier.
        var values = new[] { 1, 1, 2, 2, 2, 3, 3 };
        var rows = new List<(int Value, Guid Id, string Name)>();
        for (var i = 0; i < values.Length; i++)
        {
            var name = "n" + (i + 1);
            rows.Add((values[i], await RowAsync(Admin, table, tag, name, values[i], partition: "pg"), name));
        }
        var expected = rows.OrderBy(r => r.Value).ThenBy(r => new System.Data.SqlTypes.SqlGuid(r.Id)).Select(r => r.Name).ToArray();
        await RowAsync(Admin, table, tag, "elsewhere", 2, partition: "other");
        var text = $"SELECT c.props.new_name AS n, c.props.new_value AS v FROM c WHERE {ByTag} ORDER BY c.props.new_value";

        var seen = new List<string>();
        var pages = 0;
        string? cookie = null;
        string? firstCookie = null;
        Page page;
        do
        {
            page = await QueryAsync(Admin, table, text, Tagged(tag), pageSize: 3, cookie: cookie, partition: "pg");
            pages++;
            seen.AddRange(page.Names());
            if (page.HasMore) page.Cookie.Should().NotBeNullOrEmpty();
            firstCookie ??= page.Cookie;
            cookie = page.Cookie;
            pages.Should().BeLessThan(10);
        }
        while (page.HasMore);

        pages.Should().Be(3);
        seen.Should().Equal(expected);
        page.Cookie.Should().BeNull();
        page.Rows.Should().HaveCount(1);

        foreach (var altered in new[] { firstCookie + "!", "AAAA", firstCookie![..^3] + "=" })
        {
            var fault = await FaultAsync(() => QueryAsync(Admin, table, text, Tagged(tag), pageSize: 3, cookie: altered, partition: "pg"));
            fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument, altered);
        }
    }

    // ---- security ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Read_security_applies()
    {
        var table = await TableAsync();
        var root = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, root, oid);
        await GrantAsync(fx, user, false, ($"prvRead{table}", DepthMask.Basic), ($"prvCreate{table}", DepthMask.Basic));
        var tag = Tag();
        var mine = ServiceFor(fx, oid);
        await RowAsync(mine, table, tag, "mine1");
        await RowAsync(mine, table, tag, "mine2");
        await RowAsync(Admin, table, tag, "theirs");
        var text = $"SELECT c.props.new_name AS n FROM c WHERE {ByTag}";

        (await QueryAsync(mine, table, text, Tagged(tag))).Names().Should().Equal("mine1", "mine2");
        (await QueryAsync(Admin, table, text, Tagged(tag))).Names().Should().Equal("mine1", "mine2", "theirs");

        var noRead = Guid.NewGuid();
        var noReadUser = await CreateUserAsync(fx, root, noRead);
        await GrantAsync(fx, noReadUser, false, ("prvReadAccount", DepthMask.Global));
        var fault = await FaultAsync(() => QueryAsync(ServiceFor(fx, noRead), table, text, Tagged(tag)));
        fault.ErrorCode.Should().Be(ErrorCodes.PrivilegeDenied);
        fault.HttpStatus.Should().Be(403);
    }

    [Fact]
    public async Task Secured_json_column_filter_403_and_projection_omitted()
    {
        var table = await TableAsync();
        var tag = Tag();
        var r1 = await RowAsync(Admin, table, tag, "r1", secret: """{"k":"v1"}""");
        await RowAsync(Admin, table, tag, "r2", secret: """{"k":"v2"}""");
        var root = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, root, oid);
        await GrantAsync(fx, user, false, ($"prvRead{table}", DepthMask.Global));
        var reader = ServiceFor(fx, oid);

        var denied = await FaultAsync(() => QueryAsync(reader, table, $"SELECT c.props.new_name AS n FROM c WHERE {ByTag} AND c.props.new_secret.k = 'v1'", Tagged(tag)));
        denied.ErrorCode.Should().Be(ErrorCodes.FieldReadDenied);
        denied.HttpStatus.Should().Be(403);

        var ordered = await FaultAsync(() => QueryAsync(reader, table, $"SELECT c.props.new_name AS n FROM c WHERE {ByTag} ORDER BY c.props.new_hidden", Tagged(tag)));
        ordered.ErrorCode.Should().Be(ErrorCodes.FieldReadDenied);
        ordered.HttpStatus.Should().Be(403);
        (await QueryAsync(reader, table, $"SELECT c.props.new_name AS n, c.props.new_secret AS s FROM c WHERE {ByTag}", Tagged(tag)))
            .Json.Should().Be("""[{"n":"r1"},{"n":"r2"}]""");

        var select = $"SELECT c.props.new_name AS n, c.props.new_secret.k AS k FROM c WHERE {ByTag}";
        (await QueryAsync(reader, table, select, Tagged(tag))).Json.Should().Be("""[{"n":"r1"},{"n":"r2"}]""");
        (await QueryAsync(Admin, table, select, Tagged(tag))).Json.Should().Be("""[{"n":"r1","k":"v1"},{"n":"r2","k":"v2"}]""");

        // A field share on one row reveals the column on that row only.
        var attributeId = Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == Secret).MetadataId!.Value;
        await TestSql.ExecAsync(fx.ConnectionString, $"""
            INSERT INTO data.{FieldSecurityTables.Share} (principalobjectattributeaccessid, objectid, objectidtype, objecttypecode,
                attributeid, principalid, principalidtype, readaccess, updateaccess)
            VALUES (NEWID(), @record, @otc, @table, @attribute, @principal, 8, {TestSql.True}, {TestSql.False})
            """, ("@record", r1), ("@otc", Store.GetEntity(table)!.ObjectTypeCode!.Value), ("@table", table), ("@attribute", attributeId),
            ("@principal", user));
        Cache(fx).Invalidate();

        (await QueryAsync(reader, table, select, Tagged(tag))).Json.Should().Be("""[{"n":"r1","k":"v1"},{"n":"r2"}]""");
    }

    // ---- plug-ins ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Plugin_step_sees_the_message()
    {
        var table = await TableAsync();
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
        try
        {
            var assembly = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
            var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
            await PluginTestHelper.RegisterStepAsync(Admin, type, "ExecuteCosmosSqlQuery", null, 20, config: TestEntities.PipeLog);
            await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.{TestEntities.PipeLog}");

            var tag = Tag();
            await RowAsync(Admin, table, tag, "seen");
            (await QueryAsync(Admin, table, $"SELECT c.props.new_name AS n FROM c WHERE {ByTag}", Tagged(tag))).Names().Should().Equal("seen");

            var rows = await TestSql.RowsAsync(fx.ConnectionString, $"SELECT pb_name, pb_data FROM data.{TestEntities.PipeLog}");
            rows.Should().NotBeEmpty("the pre-operation step fired");
            ((string)rows[0][0]!).Should().StartWith("ExecuteCosmosSqlQuery|20|");
            ((string)rows[0][1]!).Split('\n').Should().Contain("primary=" + table);
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
        }
    }
}
