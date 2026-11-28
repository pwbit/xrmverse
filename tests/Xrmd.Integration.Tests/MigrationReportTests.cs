using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Xrmd.Admin.Migration;
using Xrmd.Migration.Records;
using static Xrmd.Integration.Tests.MigrationTestPackage;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The report of an import (plan M4a task 6; spec §7.8), after a whole run through the Admin API: tables with exported, imported and
/// failed counts, issues, nulled lookups per column, users by action and phase timings.
/// </summary>
[Collection("xrmd")]
public sealed class MigrationReportTests(XrmdFixture fx) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("xrmd-m4a-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Report_after_end_to_end_run()
    {
        Guid root = Guid.NewGuid(), ada = Guid.NewGuid(), bruno = Guid.NewGuid(), account = Guid.NewGuid(), bad = Guid.NewGuid();
        var contacts = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var package = Build(_root, p => p
            .Security("businessunit", "businessunitid", Record(root, ("name", new StringValue("Contoso"))))
            .Security("systemuser", "systemuserid",
                Record(ada, ("fullname", new StringValue("Ada Example")), ("domainname", new StringValue("ada@example.com")),
                    ("businessunitid", new LookupValue("businessunit", root))),
                Record(bruno, ("fullname", new StringValue("Bruno Example")), ("isdisabled", new BoolValue(true)),
                    ("businessunitid", new LookupValue("businessunit", root))))
            .Table("account", "accountid", [Record(account, ("name", new StringValue("Fabrikam")))])
            .Table("contact", "contactid",
            [
                .. contacts.Select(id => Record(id, ("lastname", new StringValue("Example")), ("parentcustomerid", new LookupValue("lead", Guid.NewGuid())))),
                Record(bad, ("lastname", new StringValue(new string('n', 5000)))),
            ])
            .Table("task", "activityid", [Record(Guid.NewGuid(), ("subject", new StringValue("Call")), ("regardingobjectid", new LookupValue("account", account)))])
            .Relationship("new_invented_nn", []));

        await using var app = fx.WithOwnDatabase(b => b.UseSetting("Pipeline:AsyncWorkerEnabled", "true"));
        var client = app.CreateClient();
        var token = DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl);
        async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, HttpStatusCode expected)
        {
            var req = new HttpRequestMessage(method, path) { Content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res = await client.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(expected, text);
            return JsonNode.Parse(text)!.AsObject();
        }

        var packageId = await UploadAsync(client, token, package);
        var started = await SendAsync(HttpMethod.Post, "admin/migrate/runs", new JsonObject
        {
            ["passphrase"] = Passphrase, ["packageId"] = packageId.ToString(),
            ["users"] = new JsonObject { ["users"] = new JsonArray(new JsonObject { ["id"] = ada.ToString(), ["action"] = "local", ["username"] = "ada" }) },
        }, HttpStatusCode.Accepted);
        var runId = started["runId"]!.GetValue<string>();
        var status = await MigrationRunPolling.WaitSettledAsync(() => SendAsync(HttpMethod.Get, $"admin/migrate/runs/{runId}", null, HttpStatusCode.OK),
            TimeSpan.FromSeconds(60));
        status["state"]!.GetValue<string>().Should().Be(MigrationRunStates.CompletedWithErrors, status.ToJsonString());

        var report = await SendAsync(HttpMethod.Get, $"admin/migrate/runs/{runId}/report", null, HttpStatusCode.OK);
        report["runId"]!.GetValue<string>().Should().Be(runId);
        report["state"]!.GetValue<string>().Should().Be(MigrationRunStates.CompletedWithErrors);
        JsonObject Table(string name) => report["tables"]!.AsArray().Single(t => t!["table"]!.GetValue<string>() == name)!.AsObject();
        Table("contact")["exported"]!.GetValue<long>().Should().Be(4);
        Table("contact")["imported"]!.GetValue<long>().Should().Be(3);
        Table("contact")["failed"]!.GetValue<long>().Should().Be(1);
        Table("account")["imported"]!.GetValue<long>().Should().Be(1);
        Table("task")["imported"]!.GetValue<long>().Should().Be(1);
        Table("systemuser")["exported"]!.GetValue<long>().Should().Be(2);

        var issues = report["issues"]!.AsArray().Select(i => i!.AsObject()).ToList();
        issues.Should().ContainSingle(i => i["severity"]!.GetValue<string>() == "error" && i["recordId"]!.GetValue<string>() == bad.ToString());
        issues.Should().Contain(i => i["code"]!.GetValue<string>() == "relationship-missing");
        report["issuesTruncated"]!.GetValue<bool>().Should().BeFalse();

        report["lookupsNulled"]!.AsArray().Should().ContainSingle().Which!.AsObject().Should().Match<JsonObject>(o =>
            o["table"]!.GetValue<string>() == "contact" && o["column"]!.GetValue<string>() == "parentcustomerid" && o["count"]!.GetValue<int>() == 3);
        report["users"]!["local"]!.GetValue<int>().Should().Be(1);
        report["users"]!["stub"]!.GetValue<int>().Should().Be(1);
        report["users"]!["entra"]!.GetValue<int>().Should().Be(0);

        var phases = report["phases"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToList();
        phases.Should().Equal("verify", "schema", "security", "records", "nn", "deferred-lookups", "sharing", "files", "post", "verification");
        report["phases"]![0]!["seconds"]!.GetValue<double>().Should().BeGreaterThanOrEqualTo(0);

        await SendAsync(HttpMethod.Get, $"admin/migrate/runs/{Guid.NewGuid()}/report", null, HttpStatusCode.NotFound);
    }
}
