using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace Xrmd.Integration.Tests;

/// <summary>#76, Task 5: the bulk actions bound to <c>accounts</c> (a standard table) through the Web API.</summary>
[Collection("xrmd")]
public sealed class StandardBulkWebApiTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private const string Type = "Microsoft.Dynamics.CRM.account";
    private readonly string _tag = "SW" + Guid.NewGuid().ToString("N")[..10];

    private async Task<HttpResponseMessage> PostAsync(string action, string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Api}accounts/Microsoft.Dynamics.CRM.{action}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private async Task<string?> NameOfAsync(Guid id)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT name FROM data.account WHERE accountid = @id");
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private async Task<int> CountTaggedAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT COUNT(*) FROM data.account WHERE name LIKE @p");
        cmd.Parameters.AddWithValue("@p", _tag + "%");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<Guid[]> CreateAsync(params string[] names)
    {
        var targets = string.Join(",", names.Select(n => $$"""{ "@odata.type": "{{Type}}", "name": "{{n}}" }"""));
        var res = await PostAsync("CreateMultiple", $$"""{ "Targets": [ {{targets}} ] }""");
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!["Ids"]!.AsArray().Select(n => Guid.Parse(n!.GetValue<string>())).ToArray();
    }

    [Fact]
    public async Task CreateMultiple_returns_the_ids_in_target_order()
    {
        var ids = await CreateAsync(_tag + "-a", _tag + "-b");

        ids.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        (await NameOfAsync(ids[0])).Should().Be(_tag + "-a");
        (await NameOfAsync(ids[1])).Should().Be(_tag + "-b");
    }

    [Fact]
    public async Task UpdateMultiple_and_UpsertMultiple_answer_204()
    {
        var ids = await CreateAsync(_tag + "-a", _tag + "-b");
        var fresh = Guid.NewGuid();

        var updated = await PostAsync("UpdateMultiple", $$"""
            { "Targets": [ { "@odata.type": "{{Type}}", "accountid": "{{ids[0]}}", "name": "{{_tag}}-a2" } ] }
            """);
        updated.StatusCode.Should().Be(HttpStatusCode.NoContent, await updated.Content.ReadAsStringAsync());
        (await NameOfAsync(ids[0])).Should().Be(_tag + "-a2");

        var upserted = await PostAsync("UpsertMultiple", $$"""
            { "Targets": [
              { "@odata.type": "{{Type}}", "accountid": "{{ids[1]}}", "name": "{{_tag}}-b2" },
              { "@odata.type": "{{Type}}", "accountid": "{{fresh}}", "name": "{{_tag}}-new" } ] }
            """);
        upserted.StatusCode.Should().Be(HttpStatusCode.NoContent, await upserted.Content.ReadAsStringAsync());
        (await NameOfAsync(ids[1])).Should().Be(_tag + "-b2");
        (await NameOfAsync(fresh)).Should().Be(_tag + "-new");
    }

    [Fact]
    public async Task A_failing_target_returns_its_error_and_writes_nothing()
    {
        var res = await PostAsync("CreateMultiple", $$"""
            { "Targets": [
              { "@odata.type": "{{Type}}", "name": "{{_tag}}-1" },
              { "@odata.type": "{{Type}}", "name": "{{_tag}}-2" },
              { "@odata.type": "{{Type}}", "name": "{{_tag}}-{{new string('x', 200)}}" } ] }
            """);

        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        var error = JsonNode.Parse(body)!["error"]!;
        error["code"]!.GetValue<string>().Should().Be("0x80040203");
        error["message"]!.GetValue<string>().Should().Contain("name");
        (await CountTaggedAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeleteMultiple_on_a_standard_table_is_refused()
    {
        var res = await PostAsync("DeleteMultiple", $$"""
            { "Targets": [ { "@odata.type": "{{Type}}", "accountid": "{{Guid.NewGuid()}}" } ] }
            """);

        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonNode.Parse(body)!["error"]!["message"]!.GetValue<string>().Should().Be("DeleteMultiple has not yet been implemented.");
    }
}
