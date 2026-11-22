using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1d, task 3 (ruling R5): <c>GET /Controls/{name}/{*path}</c> serves the files of an imported custom control with
/// their content type and SHA-256 ETag, after a read check on <c>customcontrol</c>; the session cookie authenticates it.
/// </summary>
[Collection("real-export")]
public sealed class CustomControlFilesEndpointTests(XrmdFixture fx)
{
    private const string Control = "op365_OP365.ProgressField";
    private const string Bundle = "/Controls/" + Control + "/bundle.js";
    private const string Resx = "/Controls/" + Control + "/strings/ProgressField.1033.resx";
    private static readonly string FixturePath = Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "OpenProject365_1_0_1_1.zip");

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    /// <summary>
    /// The control <see cref="Control"/> with the files of its folder in the export, written as the solution import writes them
    /// (the row by the internal caller, the files through <see cref="ICustomControlFiles"/>): the route is under test, not the import.
    /// </summary>
    private async Task ImportAsync()
    {
        var folder = $"Controls/{Control}/";
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var zip = ZipFile.OpenRead(FixturePath))
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(folder, StringComparison.Ordinal) && e.Name.Length > 0))
                files[entry.FullName[folder.Length..]] = ZipEntry(entry.FullName);
        var existing = await TestSql.ScalarAsync(fx.ConnectionString,
            $"SELECT {CustomControlSchema.Table}id FROM data.{CustomControlSchema.Table} WHERE {CustomControlSchema.Name} = @name", ("@name", Control));
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        var controlFiles = fx.App.Services.GetRequiredService<ICustomControlFiles>();
        await TestEntities.InSessionAsync(fx.ConnectionString, async s =>
        {
            var id = existing as Guid? ?? Guid.NewGuid();
            if (existing is null)
                await core.ExecuteCoreAsync(new Microsoft.Xrm.Sdk.Messages.CreateRequest
                {
                    Target = new Microsoft.Xrm.Sdk.Entity(CustomControlSchema.Table, id)
                    {
                        [CustomControlSchema.Name] = Control,
                        [CustomControlSchema.Manifest] = System.Text.Encoding.UTF8.GetString(files["ControlManifest.xml"]),
                        [CustomControlSchema.Version] = "1.0.0",
                    },
                }, Caller.Internal, s, default);
            await controlFiles.ReplaceAsync(s, id, files, default);
        });
    }

    private static byte[] ZipEntry(string path)
    {
        using var zip = ZipFile.OpenRead(FixturePath);
        using var stream = zip.GetEntry(path)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private async Task<HttpResponseMessage> GetAsync(string path, Guid? objectId = null, string? ifNoneMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl));
        if (ifNoneMatch is not null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return await fx.Client.SendAsync(req);
    }

    [OpenProject365Fact]
    public async Task Serves_bundle_with_type_etag_and_cache_headers()
    {
        await ImportAsync();
        var expected = ZipEntry($"Controls/{Control}/bundle.js");

        var response = await GetAsync(Bundle);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(expected);
        response.Headers.ETag!.ToString().Should().Be("\"" + Convert.ToHexStringLower(SHA256.HashData(expected)) + "\"");
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl!.MustRevalidate.Should().BeTrue();
        response.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.Zero);
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
    }

    [OpenProject365Fact]
    public async Task Serves_resx_as_xml()
    {
        await ImportAsync();
        var response = await GetAsync(Resx);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(ZipEntry($"Controls/{Control}/strings/ProgressField.1033.resx"));
    }

    [OpenProject365Fact]
    public async Task Serves_manifest_and_name_is_case_insensitive()
    {
        await ImportAsync();
        var response = await GetAsync("/Controls/" + Control.ToUpperInvariant() + "/ControlManifest.xml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
    }

    [OpenProject365Fact]
    public async Task Session_cookie_authenticates()
    {
        await ImportAsync();
        using var client = fx.App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var req = new HttpRequestMessage(HttpMethod.Get, Resx);
        req.Headers.TryAddWithoutValidation("Cookie", "xrmd.session=" + DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));

        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");
    }

    [OpenProject365Fact]
    public async Task Anonymous_is_401()
    {
        await ImportAsync();
        using var client = fx.App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        (await client.GetAsync(Bundle)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [OpenProject365Fact]
    public async Task If_none_match_is_304()
    {
        await ImportAsync();
        var etag = (await GetAsync(Bundle)).Headers.ETag!.ToString();

        var second = await GetAsync(Bundle, ifNoneMatch: etag);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        second.Headers.ETag!.ToString().Should().Be(etag);
        (await GetAsync(Bundle, ifNoneMatch: "\"other\"")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [OpenProject365Theory]
    [InlineData("/Controls/op365_OP365.NoSuchControl/bundle.js")]
    [InlineData("/Controls/" + Control + "/missing.js")]
    [InlineData("/Controls/" + Control + "/strings/ProgressField.9999.resx")]
    [InlineData("/Controls/" + Control)]
    [InlineData("/Controls/" + Control + "/")]
    public async Task Unknown_control_or_path_is_404(string path)
    {
        await ImportAsync();
        (await GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [OpenProject365Theory]
    [InlineData("/Controls/" + Control + "/strings/..%2Fbundle.js")]
    [InlineData("/Controls/" + Control + "/strings%2F..%2Fbundle.js")]
    [InlineData("/Controls/" + Control + "/strings%5CProgressField.1033.resx")]
    [InlineData("/Controls/" + Control + "/%2Fbundle.js")]
    [InlineData("/Controls/" + Control + "//bundle.js")]
    [InlineData("/Controls/" + Control + "/C:%5Cbundle.js")]
    public async Task Path_traversal_is_404(string path)
    {
        await ImportAsync();
        (await GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [OpenProject365Fact]
    public async Task Needs_read_on_customcontrol()
    {
        await ImportAsync();
        var root = await RootBusinessUnitAsync(fx);
        var other = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, root, other),
            await CreateRoleAsync(fx, "no-controls-" + Guid.NewGuid().ToString("N")[..8], ("prvReadAccount", DepthMask.Global)));
        var basic = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, root, basic), StandardRoles.BasicUser);

        (await GetAsync(Bundle, other)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(Bundle, basic)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
