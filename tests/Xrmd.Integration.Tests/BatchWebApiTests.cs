using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace Xrmd.Integration.Tests;

/// <summary>
/// #130: <c>POST /api/data/v9.2/$batch</c> as Learn's "Execute batch operations using the Web API" describes it: requests and
/// atomic change sets, <c>Content-ID</c> references, the first failure stopping the batch unless <c>odata.continue-on-error</c>.
/// </summary>
[Collection("xrmd")]
public sealed class BatchWebApiTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private const string Batch = "batch_test";
    private const string ChangeSet = "changeset_test";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private sealed record Part(int Status, string Head, string Body, string? ContentId);

    private static string Request(string method, string url, string? json = null, string? contentId = null, string? headers = null) =>
        "Content-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n" +
        (contentId is null ? "" : $"Content-ID: {contentId}\r\n") + "\r\n" +
        $"{method} {url} HTTP/1.1\r\n" + (json is null ? "" : "Content-Type: application/json; type=entry\r\n") + (headers ?? "") + "\r\n" +
        (json ?? "") + "\r\n";

    private static string ChangeSetOf(params string[] requests) =>
        $"Content-Type: multipart/mixed; boundary={ChangeSet}\r\n\r\n" +
        string.Concat(requests.Select(r => $"--{ChangeSet}\r\n{r}")) + $"--{ChangeSet}--\r\n";

    private static string BatchOf(params string[] items) =>
        string.Concat(items.Select(i => $"--{Batch}\r\n{i}")) + $"--{Batch}--\r\n";

    private async Task<HttpResponseMessage> SendAsync(string body, string? prefer = null, bool authenticated = true, string? contentType = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Api + "$batch");
        if (authenticated) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (prefer is not null) req.Headers.Add("Prefer", prefer);
        req.Content = new StringContent(body, Encoding.UTF8);
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? $"multipart/mixed; boundary=\"{Batch}\"");
        return await fx.Client.SendAsync(req);
    }

    /// <summary>The application/http parts of a batch response, change sets flattened.</summary>
    private static async Task<List<Part>> PartsAsync(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        var boundary = res.Content.Headers.ContentType!.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        return Split(text, boundary);
    }

    private static List<Part> Split(string text, string boundary)
    {
        var parts = new List<Part>();
        foreach (var chunk in text.Split("--" + boundary).Skip(1))
        {
            if (chunk.StartsWith("--", StringComparison.Ordinal)) break;
            var nested = Regex.Match(chunk, @"Content-Type: multipart/mixed; boundary=(\S+)");
            if (nested.Success)
            {
                parts.AddRange(Split(chunk, nested.Groups[1].Value));
                continue;
            }
            var contentId = Regex.Match(chunk, @"Content-ID: (\S+)") is { Success: true } id ? id.Groups[1].Value : null;
            var http = chunk.IndexOf("HTTP/1.1 ", StringComparison.Ordinal);
            var message = chunk[http..];
            var split = message.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var head = message[..split];
            parts.Add(new Part(int.Parse(head.Split(' ')[1]), head, message[(split + 4)..].Trim(), contentId));
        }
        return parts;
    }

    private static Guid IdOf(Part part) =>
        Guid.Parse(Regex.Match(part.Head, @"OData-EntityId: \S+\(([0-9a-f-]{36})\)").Groups[1].Value);

    private async Task<int> CountAccountsAsync(string name)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT COUNT(*) FROM data.account WHERE name = @n");
        cmd.Parameters.AddWithValue("@n", name);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<Guid?> PrimaryContactAsync(Guid account)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT primarycontactid FROM data.account WHERE accountid = @id");
        cmd.Parameters.AddWithValue("@id", account);
        return await cmd.ExecuteScalarAsync() as Guid?;
    }

    [Fact]
    public async Task Requests_run_in_order_and_answer_in_order()
    {
        var name = $"Batch {_run}";
        var res = await SendAsync(BatchOf(
            Request("POST", Api + "accounts", $$"""{"name":"{{name}}"}"""),
            Request("POST", "accounts", $$"""{"name":"{{name}}"}"""),
            Request("GET", $"accounts?$select=name&$filter=name eq '{name}'", headers: "Prefer: odata.include-annotations=\"*\"\r\n")));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var parts = await PartsAsync(res);
        parts.Select(p => p.Status).Should().Equal(204, 204, 200);
        JsonNode.Parse(parts[2].Body)!["value"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_change_set_commits_and_resolves_content_id_references()
    {
        var name = $"Batch set {_run}";
        var res = await SendAsync(BatchOf(ChangeSetOf(
            Request("POST", "contacts", $$"""{"firstname":"Batch","lastname":"{{_run}}"}""", contentId: "1"),
            Request("POST", "accounts", $$"""{"name":"{{name}}","primarycontactid@odata.bind":"$1"}""", contentId: "2"),
            Request("PATCH", "$2", """{"telephone1":"555-0130"}""", contentId: "3"))));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var parts = await PartsAsync(res);
        parts.Select(p => (p.Status, p.ContentId)).Should().Equal((204, "1"), (204, "2"), (204, "3"));
        (await PrimaryContactAsync(IdOf(parts[1]))).Should().Be(IdOf(parts[0]));
    }

    [Fact]
    public async Task A_failed_change_set_rolls_back_and_stops_the_batch()
    {
        var name = $"Batch rollback {_run}";
        var res = await SendAsync(BatchOf(
            ChangeSetOf(
                Request("POST", "accounts", $$"""{"name":"{{name}}"}""", contentId: "1"),
                Request("POST", "accounts", """{"nosuchcolumn":"x"}""", contentId: "2")),
            Request("POST", "accounts", $$"""{"name":"{{name}}"}""")));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        var parts = await PartsAsync(res);
        parts.Should().ContainSingle().Which.Should().Match<Part>(p => p.Status == 400 && p.ContentId == "2");
        JsonNode.Parse(parts[0].Body)!["error"]!["message"]!.GetValue<string>().Should().Contain("nosuchcolumn");
        (await CountAccountsAsync(name)).Should().Be(0);
    }

    [Fact]
    public async Task Continue_on_error_runs_the_rest_and_answers_200()
    {
        var name = $"Batch continue {_run}";
        var res = await SendAsync(BatchOf(
            Request("POST", "accounts", """{"nosuchcolumn":"x"}"""),
            Request("POST", "accounts", $$"""{"name":"{{name}}"}""")), prefer: "odata.continue-on-error");

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await PartsAsync(res)).Select(p => p.Status).Should().Equal(400, 204);
        (await CountAccountsAsync(name)).Should().Be(1);
    }

    [Theory]
    [InlineData("GET", "accounts", null, "A GET request is not allowed in a change set.")]
    [InlineData("POST", "accounts", """{"primarycontactid@odata.bind":"$9"}""", "Content-ID Reference: '$9' does not exist in the batch context.")]
    [InlineData("PATCH", "$9", """{"name":"x"}""", "Content-ID Reference: '$9' does not exist in the batch context.")]
    [InlineData("POST", "EntityDefinitions", """{"SchemaName":"new_x"}""", "cannot run in a change set")]
    public async Task A_change_set_refuses_what_cannot_join_it(string method, string url, string? json, string message)
    {
        var res = await SendAsync(BatchOf(ChangeSetOf(Request(method, url, json, contentId: "1"))));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        var part = (await PartsAsync(res)).Should().ContainSingle().Subject;
        JsonNode.Parse(part.Body)!["error"]!["message"]!.GetValue<string>().Should().Contain(message);
    }

    [Fact]
    public async Task Malformed_batches_are_400_and_an_anonymous_one_is_401()
    {
        (await SendAsync("{}", contentType: "application/json")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(BatchOf(Request("POST", "$batch", "{}")))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var tooMany = BatchOf(Enumerable.Range(0, 1001).Select(_ => Request("GET", "WhoAmI")).ToArray());
        var res = await SendAsync(tooMany);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("up to 1000 requests");
        (await SendAsync(BatchOf(Request("GET", "WhoAmI")), authenticated: false)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
