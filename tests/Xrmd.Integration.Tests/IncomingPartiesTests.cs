using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Mail;
using Xrmd.Metadata;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1c, task 5: the addresses of an incoming email resolve to accounts, contacts, enabled users and queues (an ambiguous sender
/// stays unresolved, every recipient match is linked), and the mailbox owner's filtering method admits or drops the message.
/// </summary>
[Collection("xrmd")]
public sealed class IncomingPartiesTests(XrmdFixture fx)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private string Address(string local) => $"{local}.{_tag}@example.com";

    private Task<Guid> ContactAsync(string address)
    {
        var c = new Entity("contact") { ["lastname"] = "Example " + _tag, ["emailaddress1"] = address };
        return Admin.CreateAsync(c);
    }

    private Task<Guid> AccountAsync(string address) =>
        Admin.CreateAsync(new Entity("account") { ["name"] = "Acme " + _tag, ["emailaddress1"] = address });

    private async Task<Guid> UserAsync(string address)
    {
        var user = new Entity("systemuser")
        {
            ["fullname"] = "User " + _tag, ["domainname"] = $"{_tag}.{Guid.NewGuid():N}@example.com", ["azureactivedirectoryobjectid"] = Guid.NewGuid(),
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)), ["internalemailaddress"] = address,
        };
        return await Admin.CreateAsync(user);
    }

    private Task<Guid> QueueAsync(string address) => Admin.CreateAsync(new Entity("queue")
    {
        ["name"] = "Queue " + _tag, ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1), ["emailaddress"] = address,
    });

    private async Task<IReadOnlyDictionary<string, AddressMatch>> ResolveAsync(params string[] addresses)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        return await IncomingParties.ResolveAsync(new DataSession(conn, null), fx.App.Services.GetRequiredService<IMetadataStore>(), addresses, default);
    }

    private static AddressMatch Match(string address, params (string Table, Guid Id)[] records) =>
        new(address, [.. records.Select(r => new EntityReference(r.Table, r.Id))]);

    [Fact]
    public async Task Sender_OneMatch_Linked()
    {
        var address = Address("one");
        var contact = await ContactAsync(address);

        var matches = await ResolveAsync(address);
        var parties = IncomingParties.Build(matches[address], [], [], []);

        parties.Sender.Should().NotBeNull();
        parties.Sender!.LogicalName.Should().Be("contact");
        parties.Sender.Id.Should().Be(contact);
        parties.SenderAmbiguous.Should().BeFalse();
        var from = parties.From.Entities.Should().ContainSingle().Subject;
        from.LogicalName.Should().Be("activityparty");
        from.GetAttributeValue<EntityReference>("partyid").Id.Should().Be(contact);
        from.GetAttributeValue<string>("addressused").Should().Be(address);
        from.GetAttributeValue<OptionSetValue>("participationtypemask").Value.Should().Be((int)ParticipationType.Sender);
    }

    [Fact]
    public async Task Sender_TwoContacts_Unresolved_FlagsAmbiguous()
    {
        var address = Address("twin");
        await ContactAsync(address);
        await ContactAsync(address);

        var matches = await ResolveAsync(address);
        matches[address].Records.Should().HaveCount(2);
        var parties = IncomingParties.Build(matches[address], [], [], []);

        parties.Sender.Should().BeNull();
        parties.SenderAmbiguous.Should().BeTrue();
        var from = parties.From.Entities.Should().ContainSingle().Subject;
        from.GetAttributeValue<EntityReference>("partyid").Should().BeNull();
        from.GetAttributeValue<string>("addressused").Should().Be(address);
    }

    [Fact]
    public async Task Sender_NoMatch_AddressOnly()
    {
        var address = Address("nobody");

        var matches = await ResolveAsync(address);
        matches.Should().ContainKey(address);
        matches[address].Records.Should().BeEmpty();
        var parties = IncomingParties.Build(matches[address], [], [], []);

        parties.Sender.Should().BeNull();
        parties.SenderAmbiguous.Should().BeFalse();
        var from = parties.From.Entities.Should().ContainSingle().Subject;
        from.GetAttributeValue<EntityReference>("partyid").Should().BeNull();
        from.GetAttributeValue<string>("addressused").Should().Be(address);
    }

    [Fact]
    public async Task Recipient_TwoMatches_LinksBoth()
    {
        var address = Address("shared");
        var contact = await ContactAsync(address);
        var account = await AccountAsync(address);
        var plain = Address("plain");
        var sender = Address("sender");

        var matches = await ResolveAsync(sender, address, plain);
        var parties = IncomingParties.Build(matches[sender], [matches[address]], [matches[plain]], []);

        parties.To.Entities.Select(e => e.GetAttributeValue<EntityReference>("partyid").Id).Should().BeEquivalentTo([contact, account]);
        parties.To.Entities.Should().OnlyContain(e => e.GetAttributeValue<string>("addressused") == address
            && e.GetAttributeValue<OptionSetValue>("participationtypemask").Value == (int)ParticipationType.ToRecipient);
        var cc = parties.Cc.Entities.Should().ContainSingle().Subject;
        cc.GetAttributeValue<EntityReference>("partyid").Should().BeNull();
        cc.GetAttributeValue<string>("addressused").Should().Be(plain);
        cc.GetAttributeValue<OptionSetValue>("participationtypemask").Value.Should().Be((int)ParticipationType.CcRecipient);
        parties.Bcc.Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Recipient_CaseInsensitive()
    {
        var address = Address("mixed");
        var contact = await ContactAsync(address.ToUpperInvariant());

        var matches = await ResolveAsync(address);
        matches[address].Records.Should().ContainSingle().Which.Id.Should().Be(contact);
        // The keys are case-insensitive too.
        matches.ContainsKey(address.ToUpperInvariant()).Should().BeTrue();
    }

    [Fact]
    public async Task InactiveContact_And_DisabledUser_NotMatched()
    {
        var contactAddress = Address("inactive");
        var userAddress = Address("disabled");
        var contact = await ContactAsync(contactAddress);
        var user = await UserAsync(userAddress);
        var enabledAddress = Address("enabled");
        var enabled = await UserAsync(enabledAddress);
        await Admin.UpdateAsync(new Entity("contact", contact) { ["statecode"] = new OptionSetValue(1), ["statuscode"] = new OptionSetValue(2) });
        await Admin.UpdateAsync(new Entity("systemuser", user) { ["isdisabled"] = true });

        var matches = await ResolveAsync(contactAddress, userAddress, enabledAddress);

        matches[contactAddress].Records.Should().BeEmpty();
        matches[userAddress].Records.Should().BeEmpty();
        matches[enabledAddress].Records.Should().ContainSingle().Which.Should().Match<EntityReference>(r => r.LogicalName == "systemuser" && r.Id == enabled);
    }

    [Fact]
    public async Task Queue_AddressMatchesQueue()
    {
        var address = Address("support");
        var queue = await QueueAsync(address);

        var matches = await ResolveAsync(address);

        matches[address].Records.Should().ContainSingle().Which.Should().Match<EntityReference>(r => r.LogicalName == "queue" && r.Id == queue);
    }

    [Fact]
    public async Task Resolve_MoreThanOneBatch_ResolvesAll()
    {
        var address = Address("late");
        var contact = await ContactAsync(address);
        var addresses = Enumerable.Range(0, 1100).Select(i => $"filler{i}.{_tag}@example.com").Append(address).ToArray();

        var matches = await ResolveAsync(addresses);

        matches.Should().HaveCount(addresses.Length);
        matches[address].Records.Should().ContainSingle().Which.Id.Should().Be(contact);
    }

    [Fact]
    public async Task Resolve_SecondAddressColumn_LinkedOnce()
    {
        // The seed has no emailaddress2; the column is added through the store (the API demands a publisher prefix).
        var store = fx.App.Services.GetRequiredService<IMetadataStore>();
        if (store.GetEntity("contact")!.Attributes!.All(a => a.LogicalName != "emailaddress2"))
            await store.AddAttributeAsync("contact", new StringAttributeMetadata { LogicalName = "emailaddress2", SchemaName = "EMailAddress2", MaxLength = 100 });
        var address = Address("both");
        var other = Address("second");
        var contact = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Example " + _tag, ["emailaddress1"] = address, ["emailaddress2"] = address });
        var viaSecond = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Example " + _tag, ["emailaddress1"] = Address("x"), ["emailaddress2"] = other });

        var matches = await ResolveAsync(address, other);

        matches[address].Records.Should().ContainSingle().Which.Id.Should().Be(contact);
        matches[other].Records.Should().ContainSingle().Which.Id.Should().Be(viaSecond);
    }

    [Fact]
    public void Build_SameAddressTwice_OneAddressOnlyParty()
    {
        var parties = IncomingParties.Build(Match("s@example.com"), [Match("r@example.com"), Match("R@example.com")], [], []);
        parties.To.Entities.Should().ContainSingle();
    }

    [Fact]
    public void Filter_UnknownValue_Fails()
    {
        var act = () => IncomingFilter.Admits(7, AContact, correlated: true);
        act.Should().Throw<MailSetupException>().WithMessage("*7*");
    }

    private static readonly AddressMatch None = Match("x@example.com");
    private static readonly AddressMatch AUser = Match("u@example.com", ("systemuser", Guid.NewGuid()));
    private static readonly AddressMatch AQueue = Match("q@example.com", ("queue", Guid.NewGuid()));
    private static readonly AddressMatch AContact = Match("c@example.com", ("contact", Guid.NewGuid()));
    private static readonly AddressMatch TwoContacts = Match("t@example.com", ("contact", Guid.NewGuid()), ("contact", Guid.NewGuid()));

    [Fact]
    public void Filter_Responses_AdmitsOnlyCorrelated()
    {
        IncomingFilter.Admits(MailSchema.FilterResponses, AContact, correlated: true).Should().BeTrue();
        IncomingFilter.Admits(MailSchema.FilterResponses, AContact, correlated: false).Should().BeFalse();
        IncomingFilter.Admits(MailSchema.FilterResponses, None, correlated: true).Should().BeTrue();
        IncomingFilter.Admits(MailSchema.FilterAll, None, correlated: false).Should().BeTrue();
    }

    [Fact]
    public void Filter_ContactsAccounts_RejectsUserSender()
    {
        IncomingFilter.Admits(MailSchema.FilterContactsAccounts, AUser, correlated: false).Should().BeFalse();
        IncomingFilter.Admits(MailSchema.FilterContactsAccounts, AQueue, correlated: false).Should().BeFalse();
        IncomingFilter.Admits(MailSchema.FilterContactsAccounts, None, correlated: false).Should().BeFalse();
        IncomingFilter.Admits(MailSchema.FilterContactsAccounts, AContact, correlated: false).Should().BeTrue();
        IncomingFilter.Admits(MailSchema.FilterContactsAccounts, Match("a@example.com", ("account", Guid.NewGuid())), correlated: false).Should().BeTrue();
    }

    [Fact]
    public void Filter_EmailEnabled_AdmitsAmbiguousSender()
    {
        IncomingFilter.Admits(MailSchema.FilterEmailEnabled, TwoContacts, correlated: false).Should().BeTrue();
        IncomingFilter.Admits(MailSchema.FilterEmailEnabled, AUser, correlated: false).Should().BeTrue();
        IncomingFilter.Admits(MailSchema.FilterEmailEnabled, AQueue, correlated: false).Should().BeTrue();
        IncomingFilter.Admits(MailSchema.FilterEmailEnabled, None, correlated: false).Should().BeFalse();
    }

    [Fact]
    public void Filter_None_AdmitsNothing()
    {
        IncomingFilter.Admits(MailSchema.FilterNone, AContact, correlated: true).Should().BeFalse();
        IncomingFilter.Admits(MailSchema.FilterNone, None, correlated: false).Should().BeFalse();
    }
}
