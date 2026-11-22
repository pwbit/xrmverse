using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The web shell mints its development token in the browser (<c>ui/src/shell/auth/devToken.ts</c>). This pins the exact
/// byte shape against the committed vector <c>fixtures/ui/dev-token-vector.json</c> (which the Vitest suite asserts too)
/// and proves the server accepts a token built that way.
/// </summary>
[Collection("xrmd")]
public sealed class UiDevTokenTests(XrmdFixture fx)
{
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Exactly what <c>createDevToken</c> does: compact JSON in this claim order, base64url without padding, HMAC-SHA256 over the UTF-8 key.</summary>
    private static string MintLikeTheShell(string key, string oid, string name, string audience, long nbf)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = "xrmverse-dev",
            ["aud"] = audience,
            ["oid"] = oid,
            ["name"] = name,
            ["nbf"] = nbf,
            ["exp"] = nbf + 3600,
        });
        var payload = Base64Url(Encoding.UTF8.GetBytes(payloadJson));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes($"{header}.{payload}"));
        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    private static JsonElement Vector()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "ui", "dev-token-vector.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await fx.Client.SendAsync(req);
    }

    [Fact]
    public void Shell_token_matches_the_committed_vector()
    {
        var v = Vector();
        var token = MintLikeTheShell(
            v.GetProperty("signingKey").GetString()!,
            v.GetProperty("objectId").GetString()!,
            v.GetProperty("name").GetString()!,
            v.GetProperty("audience").GetString()!,
            v.GetProperty("nbf").GetInt64());

        token.Should().Be(v.GetProperty("token").GetString());
    }

    [Fact]
    public async Task Token_minted_like_the_shell_is_accepted()
    {
        var token = MintLikeTheShell(
            DevTokenFactory.SigningKey, fx.AdminObjectId.ToString(), "Administrator", fx.BaseUrl, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var res = await GetAsync("/api/data/v9.2/WhoAmI", token);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Access_queries_of_the_shell_resolve_the_seed_administrator_as_System_Administrator()
    {
        var token = MintLikeTheShell(
            DevTokenFactory.SigningKey, fx.AdminObjectId.ToString(), "Administrator", fx.BaseUrl, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var whoAmI = JsonDocument.Parse(await (await GetAsync("/api/data/v9.2/WhoAmI", token)).Content.ReadAsStringAsync());
        var userId = whoAmI.RootElement.GetProperty("UserId").GetGuid();

        var links = await GetAsync($"/api/data/v9.2/systemuserrolescollection?$select=roleid&$filter=systemuserid eq {userId}", token);
        links.StatusCode.Should().Be(HttpStatusCode.OK);
        var roleIds = JsonDocument.Parse(await links.Content.ReadAsStringAsync()).RootElement.GetProperty("value")
            .EnumerateArray().Select(r => r.GetProperty("roleid").GetGuid()).ToList();
        roleIds.Should().NotBeEmpty();

        var filter = string.Join(" or ", roleIds.Select(id => $"roleid eq {id}"));
        var roles = await GetAsync($"/api/data/v9.2/roles?$select=roleid,name,roletemplateid&$filter={filter}", token);
        roles.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = JsonDocument.Parse(await roles.Content.ReadAsStringAsync()).RootElement.GetProperty("value").EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("roletemplateid").GetGuid())
            .Should().Contain(new Guid("627090FF-40A3-4053-8790-584EDC5BE201"));

        var teams = await GetAsync($"/api/data/v9.2/teammemberships?$select=teamid&$filter=systemuserid eq {userId}", token);
        teams.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync("/api/data/v9.2/teamrolescollection?$select=roleid&$filter=teamid eq 00000000-0000-0000-0000-000000000000", token))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
