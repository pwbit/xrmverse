using System.Data;
using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>
/// Plan 2.7b, Task 2 (ruling Q1, #299): text equality, <c>in</c>, joins on text, group-by on text and unique (alternate) keys compare
/// like SQL Server's <c>_CI_AS</c> on both engines: case, trailing spaces, width and kana type ignored, accents kept; the stored
/// value keeps its spaces. Runs on the run's provider (<c>XRMD_TEST_PROVIDER</c>), so both answer the same.
/// </summary>
/// <remarks>
/// The accounts of one class instance carry a tag of their own (<c>pb_texttag</c>, added to <c>account</c>): other classes of the
/// collection create accounts too, so every query filters on the tag.
/// </remarks>
[Collection("xrmd")]
public sealed class TextEqualityParityTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Tag = "pb_texttag";
    private const string Straße = "straße";
    private const string HalfWidthAiu = "ｱｲｳ"; // ｱｲｳ
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static readonly string[] Names = ["Acme", Straße, HalfWidthAiu, "école"];
    private readonly string _tag = Guid.NewGuid().ToString("N");

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Service => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (Store.GetEntity("account")!.Attributes.All(a => a.LogicalName != Tag))
                await Store.AddAttributeAsync("account", new StringAttributeMetadata { LogicalName = Tag, SchemaName = "pb_TextTag", MaxLength = 50 });
        }
        finally
        {
            Lock.Release();
        }

        foreach (var name in Names)
            await Service.CreateAsync(new Entity("account") { ["name"] = name, [Tag] = _tag });
        await ParityTestKit.EnsureKeyTableAsync(fx);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string[]> NamesAsync(ConditionOperator op, params object[] values)
    {
        var query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
        query.Criteria.AddCondition(Tag, ConditionOperator.Equal, _tag);
        query.Criteria.AddCondition("name", op, values);
        query.AddOrder("name", OrderType.Ascending);
        return (await Service.RetrieveMultipleAsync(query)).Entities.Select(e => e.GetAttributeValue<string>("name")).ToArray();
    }

    [Fact]
    public async Task Eq_ignores_case_and_trailing_spaces()
    {
        (await NamesAsync(ConditionOperator.Equal, "ACME  ")).Should().Equal("Acme");
    }

    [Fact]
    public async Task Eq_ignores_width()
    {
        (await NamesAsync(ConditionOperator.Equal, "ＡＣＭＥ")).Should().Equal("Acme"); // ＡＣＭＥ
    }

    [Fact]
    public async Task Eq_ignores_kana_type_and_width()
    {
        (await NamesAsync(ConditionOperator.Equal, "アイウ")).Should().Equal(HalfWidthAiu); // アイウ
    }

    [Fact]
    public async Task Eq_sharp_s()
    {
        (await NamesAsync(ConditionOperator.Equal, "STRASSE")).Should().Equal(Straße);
    }

    [Fact]
    public async Task Eq_keeps_accents()
    {
        (await NamesAsync(ConditionOperator.Equal, "ecole")).Should().BeEmpty();
    }

    [Fact]
    public async Task Ne_ignores_case_and_trailing_spaces()
    {
        (await NamesAsync(ConditionOperator.NotEqual, "acme ")).Should().NotContain("Acme").And.HaveCount(Names.Length - 1);
    }

    [Fact]
    public async Task In_ignores_case_and_trailing_spaces()
    {
        (await NamesAsync(ConditionOperator.In, "acme ", "x")).Should().Equal("Acme");
        (await NamesAsync(ConditionOperator.NotIn, "acme ", "x")).Should().NotContain("Acme").And.HaveCount(Names.Length - 1);
    }

    [Fact]
    public async Task In_with_many_values_ignores_case_and_trailing_spaces()
    {
        // Above ConditionCompiler.MaxInlineInValues the values go as one JSON parameter.
        var values = Enumerable.Range(0, 150).Select(i => (object)("x" + i)).Append("ACME ").ToArray();
        (await NamesAsync(ConditionOperator.In, values)).Should().Equal("Acme");
    }

    [Fact]
    public async Task A_join_on_text_ignores_case_and_trailing_spaces()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await Service.CreateAsync(new Entity("account") { ["name"] = "Join" + suffix, [Tag] = _tag });
        var key = await ParityTestKit.SendAsync(fx, HttpMethod.Post, ParityTestKit.SetOf(fx), $$"""{ "pb_name": "JOIN{{suffix.ToUpperInvariant()}} " }""");
        key.StatusCode.Should().Be(HttpStatusCode.NoContent, await key.Content.ReadAsStringAsync());

        var query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
        query.Criteria.AddCondition(Tag, ConditionOperator.Equal, _tag);
        var link = query.AddLink(ParityTestKit.KeyTable, "name", ParityTestKit.KeyName, JoinOperator.Inner);
        link.EntityAlias = "k";
        link.Columns = new ColumnSet(ParityTestKit.KeyName);

        var rows = (await Service.RetrieveMultipleAsync(query)).Entities;
        rows.Select(e => e.GetAttributeValue<string>("name")).Should().Equal("Join" + suffix);
        rows[0].GetAttributeValue<AliasedValue>("k." + ParityTestKit.KeyName).Value.Should().Be($"JOIN{suffix.ToUpperInvariant()} ");
    }

    private async Task<string> TagRowsAsync(params string[] names)
    {
        var tag = Guid.NewGuid().ToString("N");
        foreach (var name in names) await Service.CreateAsync(new Entity("account") { ["name"] = name, [Tag] = tag });
        return tag;
    }

    private async Task<IReadOnlyList<(string Name, int Count)>> GroupsAsync(string tag)
    {
        var fetch = new FetchExpression($"""
            <fetch aggregate="true">
              <entity name="account">
                <attribute name="name" alias="n" groupby="true" />
                <attribute name="accountid" alias="c" aggregate="count" />
                <filter><condition attribute="{Tag}" operator="eq" value="{tag}" /></filter>
              </entity>
            </fetch>
            """);
        return (await Service.RetrieveMultipleAsync(fetch)).Entities
            .Select(e => ((string)e.GetAttributeValue<AliasedValue>("n").Value, (int)e.GetAttributeValue<AliasedValue>("c").Value)).ToList();
    }

    private async Task<string[]> DistinctAsync(string tag)
    {
        var query = new QueryExpression("account") { ColumnSet = new ColumnSet("name"), Distinct = true };
        query.Criteria.AddCondition(Tag, ConditionOperator.Equal, tag);
        return (await Service.RetrieveMultipleAsync(query)).Entities.Select(e => e.GetAttributeValue<string>("name")).ToArray();
    }

    // The group and the distinct row show one of the stored values exactly, spaces and characters kept (fix round 1).

    [Fact]
    public async Task Group_by_text_ignores_case_and_trailing_spaces_and_shows_a_stored_value()
    {
        var group = (await GroupsAsync(await TagRowsAsync("Acme", "ACME  "))).Should().ContainSingle().Subject;
        group.Name.Should().BeOneOf("Acme", "ACME  ");
        group.Count.Should().Be(2);
    }

    [Fact]
    public async Task Group_by_text_treats_sharp_s_as_ss_and_shows_a_stored_value()
    {
        var group = (await GroupsAsync(await TagRowsAsync(Straße, "STRASSE"))).Should().ContainSingle().Subject;
        group.Name.Should().BeOneOf(Straße, "STRASSE");
        group.Count.Should().Be(2);
    }

    [Fact]
    public async Task Distinct_text_ignores_case_and_trailing_spaces_and_shows_a_stored_value()
    {
        (await DistinctAsync(await TagRowsAsync("Acme", "ACME  ", "Beta"))).Should().HaveCount(2)
            .And.ContainSingle(n => n == "Acme" || n == "ACME  ").And.Contain("Beta");
    }

    [Fact]
    public async Task Distinct_text_treats_sharp_s_as_ss_and_shows_a_stored_value()
    {
        (await DistinctAsync(await TagRowsAsync(Straße, "STRASSE"))).Should().ContainSingle().Which.Should().BeOneOf(Straße, "STRASSE");
    }

    [Fact]
    public async Task Alternate_key_refuses_a_value_differing_only_in_case_and_trailing_spaces()
    {
        var set = ParityTestKit.SetOf(fx);
        var name = "Acme" + Guid.NewGuid().ToString("N")[..8];
        var first = await ParityTestKit.SendAsync(fx, HttpMethod.Post, set, $$"""{ "pb_name": "{{name}}" }""");
        first.StatusCode.Should().Be(HttpStatusCode.NoContent, await first.Content.ReadAsStringAsync());

        var second = await ParityTestKit.SendAsync(fx, HttpMethod.Post, set, $$"""{ "pb_name": "{{name.ToUpperInvariant()}} " }""");

        var body = await second.Content.ReadAsStringAsync();
        second.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed, body);
        JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>().Should().Be("0x80040237");
    }

    [Fact]
    public async Task Alternate_key_refuses_ss_when_sharp_s_exists()
    {
        var set = ParityTestKit.SetOf(fx);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var first = await ParityTestKit.SendAsync(fx, HttpMethod.Post, set, $$"""{ "pb_name": "stra\u00DFe{{suffix}}" }""");
        first.StatusCode.Should().Be(HttpStatusCode.NoContent, await first.Content.ReadAsStringAsync());

        var second = await ParityTestKit.SendAsync(fx, HttpMethod.Post, set, $$"""{ "pb_name": "STRASSE{{suffix.ToUpperInvariant()}}" }""");

        var body = await second.Content.ReadAsStringAsync();
        second.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed, body);
        JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>().Should().Be("0x80040237");
    }

    [Fact]
    public async Task The_stored_value_keeps_its_trailing_spaces()
    {
        var id = await Service.CreateAsync(new Entity("account") { ["name"] = "Acme  ", [Tag] = Guid.NewGuid().ToString("N") });
        (await Service.RetrieveAsync("account", id, new ColumnSet("name"))).GetAttributeValue<string>("name").Should().Be("Acme  ");
    }

    [Fact]
    public async Task A_key_lookup_on_postgresql_seeks_the_text_key_index()
    {
        if (!TestProvider.IsPostgres) return; // SQL Server: the plain column index, unchanged

        var prefix = "explain" + Guid.NewGuid().ToString("N")[..8] + "_";
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var d = SqlDialects.For(conn);
        var table = $"data.{d.Quote(ParityTestKit.KeyTable)}";
        await using (var fill = conn.Command(
                         $"INSERT INTO {table} (pb_paritykeyid, {d.Quote(ParityTestKit.KeyName)}) " +
                         $"SELECT gen_random_uuid(), '{prefix}' || g FROM generate_series(1, 10000) AS g; ANALYZE {table};"))
            await fill.ExecuteNonQueryAsync();
        try
        {
            await using var explain = conn.Command(
                $"EXPLAIN SELECT t0.pb_paritykeyid FROM {table} AS t0 WHERE {d.TextKey($"t0.{d.Quote(ParityTestKit.KeyName)}")} = {d.TextKey("@p0")}");
            explain.AddParameter("@p0", DbType.String, (prefix + "5000  ").ToUpperInvariant());
            var plan = new List<string>();
            await using (var rd = await explain.ExecuteReaderAsync())
                while (await rd.ReadAsync()) plan.Add(rd.GetString(0));

            var text = string.Join("\n", plan);
            text.Should().MatchRegex("(Bitmap )?Index Scan", text).And.Contain("UX_pb_paritykey_name", text);
        }
        finally
        {
            await using var clean = conn.Command($"DELETE FROM {table} WHERE {d.Quote(ParityTestKit.KeyName)} LIKE '{prefix}%'");
            await clean.ExecuteNonQueryAsync();
        }
    }
}
