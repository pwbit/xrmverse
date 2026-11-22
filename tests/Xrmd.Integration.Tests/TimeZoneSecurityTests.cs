using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>#131 (needs a database): every user reads the time zones, no request writes them, and a user's time zone code is checked (rulings Z3, Z4).</summary>
[Collection("xrmd")]
public sealed class TimeZoneSecurityTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = await TestProvider.OpenAsync(connectionString);
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, Guid objectId, string? json = null)
    {
        var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, fx.BaseUrl));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await fx.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A Basic User the Core creates (so it has its settings row); returns its id and Entra object id.</summary>
    private async Task<(Guid UserId, Guid ObjectId)> NewBasicUserAsync()
    {
        var objectId = Guid.NewGuid();
        var run = Guid.NewGuid().ToString("N")[..8];
        var userId = await ServiceFor(fx, fx.AdminObjectId).CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = "Tess Zone " + run, ["domainname"] = $"tess.{run}@example.invalid", ["azureactivedirectoryobjectid"] = objectId,
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);
        return (userId, objectId);
    }

    [Fact]
    public async Task Basic_user_reads_time_zones()
    {
        var (_, objectId) = await NewBasicUserAsync();
        var (status, body) = await SendAsync(HttpMethod.Get, "timezonedefinitions?$select=timezonecode,standardname&$filter=timezonecode eq 110", objectId);
        status.Should().Be(HttpStatusCode.OK, body);
        var rows = JsonNode.Parse(body)!["value"]!.AsArray();
        rows.Should().ContainSingle();
        rows[0]!["standardname"]!.GetValue<string>().Should().Be("W. Europe Standard Time");
    }

    [Fact]
    public async Task Time_zones_are_not_written_by_requests()
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "timezonedefinitions", fx.AdminObjectId,
            """{ "timezonecode": 9999, "standardname": "Nowhere", "userinterfacename": "Nowhere", "bias": 0, "retiredorder": 0 }""");
        status.Should().Be(HttpStatusCode.Forbidden, body);
        (status, body) = await SendAsync(HttpMethod.Patch, $"timezonedefinitions({TimeZoneSchema.RowId(110)})", fx.AdminObjectId, """{ "bias": 0 }""");
        status.Should().Be(HttpStatusCode.Forbidden, body);
    }

    [Fact]
    public async Task User_time_zone_code_must_be_known()
    {
        var (user, objectId) = await NewBasicUserAsync();
        var (status, body) = await SendAsync(HttpMethod.Patch, $"usersettingscollection({user})", objectId, """{ "timezonecode": 9999 }""");
        status.Should().Be(HttpStatusCode.BadRequest, body);
        JsonNode.Parse(body)!["error"]!["message"]!.GetValue<string>().Should().Contain("9999");
        (status, body) = await SendAsync(HttpMethod.Patch, $"usersettingscollection({user})", objectId, """{ "timezonecode": 110 }""");
        status.Should().Be(HttpStatusCode.NoContent, body);
        (await ScalarAsync(fx.ConnectionString, $"SELECT timezonecode FROM data.usersettings WHERE systemuserid = '{user}'")).Should().Be(110);
    }
}
