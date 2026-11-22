using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
using static Xrmd.Integration.Tests.AuthoringTestHelper;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1d, task 1 (needs a database): the seeded <c>customcontrol</c> table, its write refusal (ruling R1) and the file
/// store of the controls (<c>meta.customcontrol_file</c>, ruling R2).
/// </summary>
[Collection("xrmd")]
public sealed class CustomControlTablesTests(XrmdFixture fx)
{
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private ICustomControlFiles Files => fx.App.Services.GetRequiredService<ICustomControlFiles>();

    private static string NewName() => "xv_Test.Control" + Guid.NewGuid().ToString("N")[..10];

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>A <c>customcontrol</c> row written as the solution import writes it (internally).</summary>
    private async Task<Guid> CreateInternallyAsync(string name)
    {
        var id = Guid.NewGuid();
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await TestEntities.InSessionAsync(fx.ConnectionString, s => core.ExecuteCoreAsync(new CreateRequest
        {
            Target = new Entity(CustomControlSchema.Table, id)
            {
                [CustomControlSchema.Name] = name,
                [CustomControlSchema.Version] = "1.0.0",
                [CustomControlSchema.Manifest] = "<manifest />",
                [CustomControlSchema.CompatibleDataTypes] = "Decimal",
            },
        }, Caller.Internal, s, default));
        return id;
    }

    private async Task<T> InSessionAsync<T>(Func<DataSession, Task<T>> work)
    {
        T result = default!;
        await TestEntities.InSessionAsync(fx.ConnectionString, async s => result = await work(s));
        return result;
    }

    [Fact]
    public void Table_exists_with_dataverse_names()
    {
        var e = Store.GetEntity(CustomControlSchema.Table);
        e.Should().NotBeNull();
        (e!.EntitySetName, e.PrimaryIdAttribute, e.PrimaryNameAttribute, e.ObjectTypeCode, e.OwnershipType)
            .Should().Be(("customcontrols", "customcontrolid", "name", 66, OwnershipTypes.OrganizationOwned));
        Store.GetEntityByObjectTypeCode(66)!.LogicalName.Should().Be(CustomControlSchema.Table);
    }

    [Fact]
    public async Task Create_by_admin_is_refused()
    {
        var act = () => ServiceFor(fx, fx.AdminObjectId).CreateAsync(new Entity(CustomControlSchema.Table)
        {
            [CustomControlSchema.Name] = NewName(),
        });
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Be(CustomControlSchema.ImportOnly);
    }

    [Fact]
    public async Task Update_and_delete_by_admin_are_refused()
    {
        var id = await CreateInternallyAsync(NewName());
        var service = ServiceFor(fx, fx.AdminObjectId);
        var update = () => service.UpdateAsync(new Entity(CustomControlSchema.Table, id) { [CustomControlSchema.Version] = "2.0.0" });
        (await update.Should().ThrowAsync<XrmdFaultException>()).Which.Message.Should().Be(CustomControlSchema.ImportOnly);
        var delete = () => service.DeleteAsync(CustomControlSchema.Table, id);
        (await delete.Should().ThrowAsync<XrmdFaultException>()).Which.Message.Should().Be(CustomControlSchema.ImportOnly);
    }

    [Fact]
    public async Task Basic_user_reads_customcontrols()
    {
        var name = NewName();
        await CreateInternallyAsync(name);
        var alice = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), alice);
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/data/v9.2/customcontrols?$filter=name eq '{name}'&$select=name,manifest,version");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(alice, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain(name).And.Contain("1.0.0");
    }

    [Fact]
    public async Task File_store_replaces_reads_and_deletes_with_the_row()
    {
        var id = await CreateInternallyAsync(NewName());
        var bundle = Encoding.UTF8.GetBytes("/* bundle v1 */");
        var strings = Encoding.UTF8.GetBytes("<root />");
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Files.ReplaceAsync(s, id, new Dictionary<string, byte[]>
        {
            ["bundle.js"] = bundle,
            ["strings/Test.1033.resx"] = strings,
        }, default));

        var got = await Files.GetAsync(id, "bundle.js", default);
        got.Should().NotBeNull();
        got!.Value.Content.Should().Equal(bundle);
        got.Value.Sha256.Should().Be(Sha(bundle)).And.HaveLength(64);
        (await Files.GetAsync(id, "strings/Test.1033.resx", default))!.Value.Content.Should().Equal(strings);
        (await Files.GetAsync(id, "missing.js", default)).Should().BeNull();
        (await Files.GetAsync(Guid.NewGuid(), "bundle.js", default)).Should().BeNull();

        // A replace drops the paths it does not name and gives the new content a new hash.
        var bundle2 = Encoding.UTF8.GetBytes("/* bundle v2 */");
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Files.ReplaceAsync(s, id, new Dictionary<string, byte[]>
        {
            ["bundle.js"] = bundle2,
            ["css/x.css"] = [],
        }, default));
        var all = await InSessionAsync(s => Files.AllAsync(s, id, default));
        all.Keys.Should().BeEquivalentTo("bundle.js", "css/x.css");
        all["bundle.js"].Should().Equal(bundle2);
        all["css/x.css"].Should().BeEmpty();
        (await Files.GetAsync(id, "bundle.js", default))!.Value.Sha256.Should().Be(Sha(bundle2));
        (await Files.GetAsync(id, "strings/Test.1033.resx", default)).Should().BeNull();

        // DeleteAsync clears them; the internal Delete of the row (an uninstall) takes the files with it.
        await TestEntities.InSessionAsync(fx.ConnectionString, s => Files.DeleteAsync(s, id, default));
        (await InSessionAsync(s => Files.AllAsync(s, id, default))).Should().BeEmpty();

        await TestEntities.InSessionAsync(fx.ConnectionString, s => Files.ReplaceAsync(s, id,
            new Dictionary<string, byte[]> { ["bundle.js"] = bundle }, default));
        using (var scope = fx.App.Services.CreateScope())
        {
            var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
            await TestEntities.InSessionAsync(fx.ConnectionString, s =>
                core.ExecuteCoreAsync(new DeleteRequest { Target = new EntityReference(CustomControlSchema.Table, id) }, Caller.Internal, s, default));
        }
        (await ScalarAsync(fx, "SELECT COUNT(*) FROM meta.customcontrol_file WHERE customcontrolid = @id", ("@id", id))).Should().Be(0);
    }
}
