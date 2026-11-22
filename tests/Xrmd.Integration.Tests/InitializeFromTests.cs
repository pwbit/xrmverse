using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>#143, task 4: InitializeFrom through the organization service (relationship lookups and imported maps).</summary>
[Collection("xrmd")]
public sealed class InitializeFromTests(XrmdFixture fx)
{
    private readonly AttributeMapTestKit _kit = new(fx);

    private static InitializeFromRequest Request(string table, Guid id, string target, TargetFieldType type = TargetFieldType.ValidForCreate) => new()
    {
        EntityMoniker = new EntityReference(table, id), TargetEntityName = target, TargetFieldType = type,
    };

    private async Task<Entity> InitializeAsync(OrganizationRequest request, Guid? objectId = null) =>
        ((InitializeFromResponse)await ServiceFor(fx, objectId ?? fx.AdminObjectId).ExecuteAsync(request)).Entity;

    /// <summary>The kit's tables with the maps new_name→new_name, new_code→new_code and new_amount→new_qty.</summary>
    private async Task MappedTablesAsync()
    {
        await _kit.CreateTablesAsync();
        await _kit.WriteMapsAsync(AttributeMapTestKit.Map(_kit.Parent, _kit.Child,
            ("new_name", "new_name"), ("new_code", "new_code"), ("new_amount", "new_qty")));
    }

    [Fact]
    public async Task Account_to_contact_sets_parentcustomerid()
    {
        var account = await _kit.Admin.CreateAsync(new Entity("account") { ["name"] = "Contoso Initialize", ["telephone1"] = "555 0100" });

        var contact = await InitializeAsync(Request("account", account, "contact"));

        contact.LogicalName.Should().Be("contact");
        contact.Id.Should().Be(Guid.Empty);
        contact.Attributes.Keys.Should().Equal("parentcustomerid");
        var parent = contact.GetAttributeValue<EntityReference>("parentcustomerid");
        (parent.LogicalName, parent.Id, parent.Name).Should().Be(("account", account, "Contoso Initialize"));
    }

    [Fact]
    public async Task User_map_copies_non_null_values()
    {
        await MappedTablesAsync();
        var parent = await _kit.Admin.CreateAsync(new Entity(_kit.Parent) { ["new_name"] = "P1", ["new_code"] = "C-1" });

        var child = await InitializeAsync(Request(_kit.Parent, parent, _kit.Child));

        child.Attributes.Keys.Should().BeEquivalentTo("new_parentid", "new_name", "new_code");
        child.GetAttributeValue<EntityReference>("new_parentid").Should().BeEquivalentTo(new EntityReference(_kit.Parent, parent) { Name = "P1" });
        (child["new_name"], child["new_code"]).Should().Be(("P1", "C-1"));

        var withAmount = await _kit.Admin.CreateAsync(new Entity(_kit.Parent) { ["new_name"] = "P2", ["new_amount"] = 7 });
        (await InitializeAsync(Request(_kit.Parent, withAmount, _kit.Child, TargetFieldType.All)))["new_qty"].Should().Be(7);
    }

    [Fact]
    public async Task Skip_parental_mapping_leaves_out_the_lookup()
    {
        await MappedTablesAsync();
        var parent = await _kit.Admin.CreateAsync(new Entity(_kit.Parent) { ["new_name"] = "P1" });
        var request = Request(_kit.Parent, parent, _kit.Child);
        request.SkipParentalRelationshipMapping = true;

        (await InitializeAsync(request)).Attributes.Keys.Should().Equal("new_name");
    }

    [Fact]
    public async Task Unknown_target_or_pair_without_map_is_400()
    {
        await _kit.CreateTablesAsync();
        var parent = await _kit.Admin.CreateAsync(new Entity(_kit.Parent) { ["new_name"] = "P1" });
        var child = await _kit.Admin.CreateAsync(new Entity(_kit.Child) { ["new_name"] = "C1" });

        (await BadRequestAsync(() => InitializeAsync(Request(_kit.Parent, parent, "new_nothere")))).Should().Contain("new_nothere");
        (await BadRequestAsync(() => InitializeAsync(Request(_kit.Child, child, _kit.Parent))))
            .Should().Be($"No map exists from table '{_kit.Child}' to table '{_kit.Parent}'.");
    }

    [Fact]
    public async Task Missing_record_is_404_and_unreadable_source_is_403()
    {
        await _kit.CreateTablesAsync();
        (await FaultAsync(() => InitializeAsync(Request(_kit.Parent, Guid.NewGuid(), _kit.Child)))).HttpStatus.Should().Be(404);

        var parent = await _kit.Admin.CreateAsync(new Entity(_kit.Parent) { ["new_name"] = "P1" });
        var alice = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), alice);
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);
        (await FaultAsync(() => InitializeAsync(Request(_kit.Parent, parent, _kit.Child), alice))).HttpStatus.Should().Be(403);
    }

    [Fact]
    public async Task Secured_source_column_is_not_copied_to_a_user_who_cannot_read_it()
    {
        await _kit.CreateTablesAsync();
        foreach (var table in new[] { _kit.Parent, _kit.Child })
            await _kit.Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = table,
                Attribute = new StringAttributeMetadata { LogicalName = "new_secret", SchemaName = "new_Secret", MaxLength = 100, IsSecured = true, DisplayName = Label("Secret") },
            });
        await _kit.WriteMapsAsync(AttributeMapTestKit.Map(_kit.Parent, _kit.Child, ("new_secret", "new_secret"), ("new_code", "new_code")));
        var parent = await _kit.Admin.CreateAsync(new Entity(_kit.Parent) { ["new_name"] = "P1", ["new_code"] = "C-1", ["new_secret"] = "hidden" });
        var bob = Guid.NewGuid();
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), bob);
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);
        await GrantAsync(fx, userId, false, (PrivilegeNames.For(AccessRights.ReadAccess, fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(_kit.Parent)!), DepthMask.Global));

        var child = await InitializeAsync(Request(_kit.Parent, parent, _kit.Child), bob);

        child.Attributes.Keys.Should().BeEquivalentTo("new_parentid", "new_code");
        (await InitializeAsync(Request(_kit.Parent, parent, _kit.Child)))["new_secret"].Should().Be("hidden", "the administrator reads it");
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/data/v9.2/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Web_api_returns_binds_and_values()
    {
        await MappedTablesAsync();
        var parent = await _kit.Admin.CreateAsync(new Entity(_kit.Parent) { ["new_name"] = "P1", ["new_amount"] = 3 });
        var parentSet = fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(_kit.Parent)!.EntitySetName;
        var childSet = fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(_kit.Child)!.EntitySetName;

        var (status, body) = await GetAsync(
            "InitializeFrom(EntityMoniker=@p1,TargetEntityName=@p2,TargetFieldType=@p3)" +
            $"?@p1={Uri.EscapeDataString($"{{'@odata.id':'{parentSet}({parent})'}}")}&@p2='{_kit.Child}'" +
            "&@p3=Microsoft.Dynamics.CRM.TargetFieldType'ValidForCreate'");

        status.Should().Be(HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!.AsObject();
        json["@odata.context"]!.GetValue<string>().Should().EndWith($"$metadata#{childSet}/$entity");
        json["@odata.type"]!.GetValue<string>().Should().Be($"#Microsoft.Dynamics.CRM.{_kit.Child}");
        json["new_ParentId@odata.bind"]!.GetValue<string>().Should().Be($"{parentSet}({parent})");
        (json["new_name"]!.GetValue<string>(), json["new_qty"]!.GetValue<int>()).Should().Be(("P1", 3));
        json.ContainsKey("new_code").Should().BeFalse("a null source value is left out");

        var (numberStatus, numberBody) = await GetAsync(
            $"InitializeFrom(EntityMoniker=@p1,TargetEntityName=@p2,TargetFieldType=@p3)?@p1={Uri.EscapeDataString($"{{'@odata.id':'{parentSet}({parent})'}}")}&@p2='{_kit.Child}'&@p3=1");
        numberStatus.Should().Be(HttpStatusCode.BadRequest, numberBody);
        numberBody.Should().Contain("0x80060888");

        var (metadataStatus, metadata) = await GetAsync("$metadata");
        metadataStatus.Should().Be(HttpStatusCode.OK);
        metadata.Should().Contain("<Function Name=\"InitializeFrom\"").And.Contain("<EnumType Name=\"TargetFieldType\"");
    }
}
