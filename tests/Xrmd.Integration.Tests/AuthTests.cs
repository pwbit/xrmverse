using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public sealed class AuthTests(XrmdFixture fx)
{
    private const string WhoAmI = "/api/data/v9.2/WhoAmI";

    private Task<HttpResponseMessage> GetAsync(string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, WhoAmI);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return fx.Client.SendAsync(req);
    }

    [Fact]
    public async Task No_token_returns_401()
    {
        (await GetAsync(null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_audience_returns_401_without_body()
    {
        var res = await GetAsync(DevTokenFactory.Create(Guid.NewGuid(), "https://other.example/"));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await res.Content.ReadAsStringAsync()).Should().BeEmpty();
        res.Headers.WwwAuthenticate.ToString().Should().NotContain("error_description");
    }

    [Fact]
    public async Task Expired_token_returns_401()
    {
        var res = await GetAsync(DevTokenFactory.Create(Guid.NewGuid(), fx.BaseUrl, lifetime: TimeSpan.FromHours(-1)));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await res.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Valid_dev_token_reaches_whoami_endpoint(bool trailingSlash)
    {
        var audience = trailingSlash ? fx.BaseUrl.TrimEnd('/') + "/" : fx.BaseUrl.TrimEnd('/');
        var res = await GetAsync(DevTokenFactory.Create(fx.AdminObjectId, audience));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("UserId").GetGuid().Should().Be(await AdminSystemUserIdAsync());
    }

    private async Task<Guid> AdminSystemUserIdAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid");
        cmd.Parameters.Add("@oid", System.Data.SqlDbType.UniqueIdentifier).Value = fx.AdminObjectId;
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Healthz_stays_anonymous()
    {
        (await fx.Client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
