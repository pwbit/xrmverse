using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;

namespace Xrmd.Integration.Tests;

/// <summary>The entity set names of the seeded system tables are the ones Dataverse uses (drop-in compatibility).</summary>
[Collection("xrmd")]
public sealed class SeedEntitySetNameTests(XrmdFixture fx)
{
    private async Task<HttpStatusCode> GetAsync(string set)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/data/v9.2/{set}?$top=1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        return (await fx.Client.SendAsync(req)).StatusCode;
    }

    [Theory]
    [InlineData("accounts", "account")]
    [InlineData("contacts", "contact")]
    public async Task Business_table_sets_use_the_dataverse_names(string current, string singular)
    {
        (await GetAsync(current)).Should().Be(HttpStatusCode.OK);
        (await GetAsync(singular)).Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("pluginassemblies", "pluginassemblys")]
    [InlineData("principalobjectaccessset", "principalobjectaccesss")]
    [InlineData("privilegeobjecttypecodesset", "privilegeobjecttypecodess")]
    [InlineData("roleprivilegescollection", "roleprivilegess")]
    [InlineData("systemuserrolescollection", "systemuserroless")]
    [InlineData("teamrolescollection", "teamroless")]
    public async Task Seed_sets_use_the_dataverse_names(string current, string old)
    {
        (await GetAsync(current)).Should().Be(HttpStatusCode.OK);
        (await GetAsync(old)).Should().Be(HttpStatusCode.NotFound);
    }
}
