using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.15, task 3: <c>RetrieveDuplicates</c> (ruling D14) on a published rule contact → contact on <c>emailaddress1</c>:
/// the matches of an unsaved or a stored record, read as the caller (record and field security apply), paged.
/// </summary>
[Collection("xrmd")]
public sealed class RetrieveDuplicatesTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Contact = DuplicateDetectionTestKit.Contact;
    private const string Email = DuplicateDetectionTestKit.Email;

    private readonly DuplicateDetectionTestKit _kit = new(fx);

    private OrganizationService Admin => _kit.Admin;

    public Task InitializeAsync() => _kit.InitializeAsync(onlineCreateUpdate: false, Contact);

    public Task DisposeAsync() => _kit.DisposeAsync();

    private string Mail(string label) => _kit.Mail(label);

    private Task<Guid> ContactAsync(string label, string? email) =>
        Admin.CreateAsync(new Entity(Contact) { ["lastname"] = label + " " + _kit.Run, [Email] = email });

    private async Task<Guid> PublishedRuleAsync()
    {
        var rule = await _kit.RuleAsync();
        await _kit.PublishAndRunAsync(rule);
        return rule;
    }

    private static async Task<EntityCollection> DuplicatesAsync(OrganizationService service, Entity business, PagingInfo? paging = null)
    {
        var request = new RetrieveDuplicatesRequest { BusinessEntity = business, MatchingEntityName = Contact, PagingInfo = paging };
        var response = await service.ExecuteAsync(request);
        return response.Results["DuplicateCollection"].Should().BeOfType<EntityCollection>().Subject;
    }

    [Fact]
    public async Task Finds_existing_record_for_unsaved_entity()
    {
        var anna = await ContactAsync("Anna", Mail("anna"));
        await ContactAsync("Other", Mail("other"));
        await PublishedRuleAsync();

        var found = await DuplicatesAsync(Admin, new Entity(Contact) { [Email] = " " + Mail("ANNA") }, new PagingInfo { Count = 50, PageNumber = 1 });

        found.EntityName.Should().Be(Contact);
        found.Entities.Select(e => e.Id).Should().Equal(anna);
        found.Entities[0].GetAttributeValue<string>("lastname").Should().Be("Anna " + _kit.Run, "the matches carry all columns");
        found.MoreRecords.Should().BeFalse();
    }

    [Fact]
    public async Task Existing_id_only_uses_stored_values_and_excludes_itself()
    {
        var anna = await ContactAsync("Anna", Mail("anna"));
        var twin = await ContactAsync("Twin", Mail("anna"));
        await PublishedRuleAsync();

        var found = await DuplicatesAsync(Admin, new Entity(Contact, anna));

        found.Entities.Select(e => e.Id).Should().Equal(twin);

        var overlaid = await DuplicatesAsync(Admin, new Entity(Contact, anna) { [Email] = Mail("nobody") });
        overlaid.Entities.Should().BeEmpty("the given attributes overlay the stored ones");
    }

    [Fact]
    public async Task Unreadable_match_is_left_out()
    {
        await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ("prvReadContact", DepthMask.Basic));

        var found = await DuplicatesAsync(ServiceFor(fx, objectId), new Entity(Contact) { [Email] = Mail("anna") });

        found.Entities.Should().BeEmpty("the user reads only its own contacts");
    }

    [Fact]
    public async Task Stored_record_needs_read_access()
    {
        var anna = await ContactAsync("Anna", Mail("anna"));
        await ContactAsync("Twin", Mail("anna"));
        await PublishedRuleAsync();
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ("prvReadContact", DepthMask.Basic));
        var reader = ServiceFor(fx, objectId);

        // The user cannot read Anna: naming her id must not reveal her stored values through their duplicates.
        (await FaultAsync(() => DuplicatesAsync(reader, new Entity(Contact, anna)))).HttpStatus.Should().Be(403);

        // An id that names no row is an unsaved record.
        (await DuplicatesAsync(reader, new Entity(Contact, Guid.NewGuid()) { [Email] = Mail("anna") })).Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Base_table_read_is_required()
    {
        await PublishedRuleAsync();
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ("prvReadAccount", DepthMask.Global));

        var fault = await FaultAsync(() => DuplicatesAsync(ServiceFor(fx, objectId), new Entity(Contact) { [Email] = Mail("anna") }));
        fault.HttpStatus.Should().Be(403);
        fault.Message.Should().Contain("prvReadContact");
    }

    [Fact]
    public async Task No_published_rule_returns_empty()
    {
        await ContactAsync("Anna", Mail("anna"));
        await _kit.RuleAsync();

        var found = await DuplicatesAsync(Admin, new Entity(Contact) { [Email] = Mail("anna") });

        found.Entities.Should().BeEmpty();
        found.MoreRecords.Should().BeFalse();
    }

    [Fact]
    public async Task Paging_count_and_more_records()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++) ids.Add(await ContactAsync("Copy " + i, Mail("anna")));
        await PublishedRuleAsync();
        var business = new Entity(Contact) { [Email] = Mail("anna") };

        var first = await DuplicatesAsync(Admin, business, new PagingInfo { Count = 2, PageNumber = 1 });
        var second = await DuplicatesAsync(Admin, business, new PagingInfo { Count = 2, PageNumber = 2 });

        first.Entities.Should().HaveCount(2);
        first.MoreRecords.Should().BeTrue();
        second.Entities.Should().HaveCount(1);
        second.MoreRecords.Should().BeFalse();
        first.Entities.Concat(second.Entities).Select(e => e.Id).Should().BeEquivalentTo(ids);
    }

    [Fact]
    public async Task Page_size_above_5000_is_400()
    {
        await PublishedRuleAsync();
        var business = new Entity(Contact) { [Email] = Mail("anna") };

        await BadRequestAsync(() => DuplicatesAsync(Admin, business, new PagingInfo { Count = 5001, PageNumber = 1 }));
        await BadRequestAsync(() => DuplicatesAsync(Admin, business, new PagingInfo { Count = -1, PageNumber = 1 }));
        await BadRequestAsync(() => DuplicatesAsync(Admin, business, new PagingInfo { Count = 10, PageNumber = -1 }));
    }

    [Fact]
    public async Task Secured_column_is_masked_in_duplicates()
    {
        var anna = await Admin.CreateAsync(new Entity(Contact)
        {
            ["lastname"] = "Anna " + _kit.Run, [Email] = Mail("anna"), ["telephone1"] = "555-0100",
        });
        await PublishedRuleAsync();
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, ("prvReadContact", DepthMask.Global));
        // telephone1 is not secured in the seed: the in-memory definition is, for this test only.
        var phone = _kit.Store.GetEntity(Contact)!.Attributes.Single(a => a.LogicalName == "telephone1");
        phone.IsSecured = true;
        Cache(fx).Invalidate();
        try
        {
            var found = await DuplicatesAsync(ServiceFor(fx, objectId), new Entity(Contact) { [Email] = Mail("anna") });

            var match = found.Entities.Should().ContainSingle().Subject;
            match.Id.Should().Be(anna);
            match.GetAttributeValue<string>("telephone1").Should().BeNull("the user cannot read the secured column");
            match.GetAttributeValue<string>(Email).Should().Be(Mail("anna"));
        }
        finally
        {
            phone.IsSecured = false;
            Cache(fx).Invalidate();
        }
    }

    [Fact]
    public async Task Rule_on_unreadable_secured_column_is_refused()
    {
        // Final review I2 (ruling F3): a rule condition is a filter on the matches; a caller who cannot read its column is refused.
        const string Phone = "telephone1";
        var anna = await Admin.CreateAsync(new Entity(Contact) { ["lastname"] = "Anna " + _kit.Run, [Phone] = "555-" + _kit.Run });
        var rule = await _kit.RuleAsync(Phone);
        await _kit.PublishAndRunAsync(rule);
        var root = await RootBusinessUnitAsync(fx);
        var blindId = Guid.NewGuid();
        var blind = await CreateUserAsync(fx, root, blindId);
        await GrantAsync(fx, blind, false, ("prvReadContact", DepthMask.Global));
        var readerId = Guid.NewGuid();
        var reader = await CreateUserAsync(fx, root, readerId);
        await GrantAsync(fx, reader, false, ("prvReadContact", DepthMask.Global));
        // telephone1 is not secured in the seed: the in-memory definition is, for this test only.
        var column = _kit.Store.GetEntity(Contact)!.Attributes.Single(a => a.LogicalName == Phone);
        column.IsSecured = true;
        Cache(fx).Invalidate();
        try
        {
            var profile = await Admin.CreateAsync(new Entity(FieldSecurityTables.Profile) { ["name"] = "dd-" + _kit.Run });
            await Admin.CreateAsync(new Entity(FieldSecurityTables.Permission)
            {
                [FieldSecurityTables.ProfileId] = new EntityReference(FieldSecurityTables.Profile, profile),
                [FieldSecurityTables.EntityName] = Contact, [FieldSecurityTables.AttributeLogicalName] = Phone,
                [FieldSecurityTables.CanRead] = new OptionSetValue(4),
            });
            await Admin.ExecuteAsync(new AssociateRequest
            {
                Target = new EntityReference("systemuser", reader), Relationship = new Relationship("systemuserprofiles_association"),
                RelatedEntities = [new EntityReference(FieldSecurityTables.Profile, profile)],
            });
            var guess = new Entity(Contact) { [Phone] = "555-" + _kit.Run };

            var fault = await FaultAsync(() => DuplicatesAsync(ServiceFor(fx, blindId), guess));
            fault.HttpStatus.Should().Be(403);
            fault.ErrorCode.Should().Be(Xrmd.Core.Errors.ErrorCodes.FieldReadDenied);
            (await FaultAsync(() => DuplicatesAsync(ServiceFor(fx, blindId), new Entity(Contact, anna)))).ErrorCode
                .Should().Be(Xrmd.Core.Errors.ErrorCodes.FieldReadDenied, "a stored id compares the stored secured value too");

            (await DuplicatesAsync(ServiceFor(fx, readerId), guess)).Entities.Select(e => e.Id).Should().Equal(anna);
            (await DuplicatesAsync(Admin, guess)).Entities.Select(e => e.Id).Should().Equal(anna);
        }
        finally
        {
            column.IsSecured = false;
            Cache(fx).Invalidate();
        }
    }
}
