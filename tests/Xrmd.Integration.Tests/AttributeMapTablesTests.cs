using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>#143, task 1 (needs a database): the seeded <c>entitymap</c> and <c>attributemap</c> tables.</summary>
[Collection("xrmd")]
public sealed class AttributeMapTablesTests(XrmdFixture fx)
{
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    [Theory]
    [InlineData("entitymap", "entitymaps", "entitymapid", 4600)]
    [InlineData("attributemap", "attributemaps", "attributemapid", 4601)]
    public void Tables_exist_with_dataverse_names(string table, string set, string id, int otc)
    {
        var e = Store.GetEntity(table);
        e.Should().NotBeNull();
        (e!.EntitySetName, e.PrimaryIdAttribute, e.ObjectTypeCode, e.OwnershipType).Should().Be((set, id, otc, OwnershipTypes.OrganizationOwned));
        Store.GetEntityByObjectTypeCode(otc)!.LogicalName.Should().Be(table);
        Store.GetRelationship(AttributeMapSchema.Relationship).Should().NotBeNull();
    }

    [Theory]
    [InlineData("entitymap")]
    [InlineData("attributemap")]
    public async Task Create_by_admin_is_refused(string table)
    {
        var target = new Entity(table);
        if (table == AttributeMapSchema.EntityMap)
        {
            target[AttributeMapSchema.SourceEntity] = "account";
            target[AttributeMapSchema.TargetEntity] = "contact";
        }
        else
        {
            target[AttributeMapSchema.SourceAttribute] = "name";
            target[AttributeMapSchema.TargetAttribute] = "lastname";
        }
        var act = () => ServiceFor(fx, fx.AdminObjectId).CreateAsync(target);
        (await act.Should().ThrowAsync<XrmdFaultException>()).Which.Message.Should().Be(AttributeMapSchema.ImportOnly);
    }

    [Fact]
    public async Task Basic_user_reads_the_maps()
    {
        var alice = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), alice);
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);
        foreach (var set in new[] { "entitymaps", "attributemaps" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/data/v9.2/{set}?$top=1");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(alice, fx.BaseUrl));
            var response = await fx.Client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK, set + ": " + await response.Content.ReadAsStringAsync());
        }
    }
}
