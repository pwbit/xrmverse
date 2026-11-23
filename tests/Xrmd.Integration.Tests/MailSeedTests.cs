using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Security;

namespace Xrmd.Integration.Tests;

/// <summary>The seeded mail tables and columns (plan 4.1b, task 1): type codes, ownership, new columns, secrets and privileges.</summary>
[Collection("xrmd")]
public sealed class MailSeedTests(XrmdFixture fx)
{
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private AttributeMetadata Column(string table, string column) =>
        Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == column);

    private T Column<T>(string table, string column) where T : AttributeMetadata => Column(table, column).Should().BeOfType<T>().Subject;

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static string Ids(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{PrivilegeNames.IdOf(n)}'"));

    [Fact]
    public void Seed_CreatesMailTables_WithTypeCodesAndOwnership()
    {
        var profile = Store.GetEntity(MailSchema.EmailServerProfile)!;
        profile.ObjectTypeCode.Should().Be(9605);
        profile.OwnershipType.Should().Be(OwnershipTypes.UserOwned);
        profile.EntitySetName.Should().Be("emailserverprofiles");
        profile.PrimaryIdAttribute.Should().Be("emailserverprofileid");
        profile.PrimaryNameAttribute.Should().Be("name");
        Column<StringAttributeMetadata>("emailserverprofile", "name").MaxLength.Should().Be(100);

        var mailbox = Store.GetEntity(MailSchema.Mailbox)!;
        mailbox.ObjectTypeCode.Should().Be(9606);
        mailbox.OwnershipType.Should().Be(OwnershipTypes.UserOwned);
        mailbox.EntitySetName.Should().Be("mailboxes");
        mailbox.PrimaryIdAttribute.Should().Be("mailboxid");
        mailbox.PrimaryNameAttribute.Should().Be("name");
        Column<StringAttributeMetadata>("mailbox", "name").MaxLength.Should().Be(200);

        var attachment = Store.GetEntity(MailSchema.Attachment)!;
        attachment.LogicalName.Should().Be("activitymimeattachment");
        attachment.ObjectTypeCode.Should().Be(1001);
        attachment.OwnershipType.Should().Be(OwnershipTypes.None);
        attachment.EntitySetName.Should().Be("activitymimeattachments");
        attachment.PrimaryIdAttribute.Should().Be("activitymimeattachmentid");
        attachment.PrimaryNameAttribute.Should().Be("filename");
        Column<StringAttributeMetadata>("activitymimeattachment", "filename").MaxLength.Should().Be(255);
    }

    [Fact]
    public void Seed_HasTheLearnOptionValues()
    {
        static int[] Values(PicklistAttributeMetadata p) => p.OptionSet.Options.Select(o => o.Value!.Value).ToArray();
        Values(Column<PicklistAttributeMetadata>("emailserverprofile", "servertype")).Should().Equal(0, 1, 2, 3, 4);
        Values(Column<PicklistAttributeMetadata>("emailserverprofile", "outgoingauthenticationprotocol")).Should().Equal(0, 1, 2, 3, 4);
        Values(Column<PicklistAttributeMetadata>("emailserverprofile", "incomingauthenticationprotocol")).Should().Equal(0, 1, 2, 3, 4);
        Values(Column<PicklistAttributeMetadata>("emailserverprofile", "outgoingcredentialretrieval")).Should().Equal(0, 1, 2, 3, 4, 5, 6, 7, 8);
        Values(Column<PicklistAttributeMetadata>("emailserverprofile", "serverauthority")).Should().Equal(0, 1, 2, 3);
        Values(Column<PicklistAttributeMetadata>("mailbox", "incomingemaildeliverymethod")).Should().Equal(0, 1, 2, 3);
        Values(Column<PicklistAttributeMetadata>("mailbox", "outgoingemaildeliverymethod")).Should().Equal(0, 1, 2);
        Values(Column<PicklistAttributeMetadata>("mailbox", "outgoingemailstatus")).Should().Equal(0, 1, 2);
        Column<StateAttributeMetadata>("mailbox", "statecode").OptionSet.Options.Select(o => o.Value!.Value).Should().Equal(0, 1);
        MailSchema.ServerTypeExchange.Should().Be(0);
        MailSchema.ServerTypeImapSmtp.Should().Be(4);
        (MailSchema.ProtocolBasic, MailSchema.ProtocolOAuth).Should().Be((3, 4));
        (MailSchema.CredentialsByUserOrQueue, MailSchema.CredentialsInProfile, MailSchema.CredentialsEntraOAuth).Should().Be((0, 1, 7));
        (MailSchema.EmailDraft, MailSchema.EmailFailed).Should().Be((1, 8));
    }

    [Fact]
    public void Seed_AddsEmailColumns()
    {
        var email = "email";
        Column<StringAttributeMetadata>(email, "inreplyto");
        Column<StringAttributeMetadata>(email, "conversationindex");
        var method = Column<PicklistAttributeMetadata>(email, "correlationmethod");
        method.OptionSet.Options.Should().HaveCount(8);
        method.IsValidForCreate.Should().BeFalse();
        method.IsValidForUpdate.Should().BeFalse();
        Column<LookupAttributeMetadata>(email, "parentactivityid").Targets.Should().Equal("email");
        var count = Column<IntegerAttributeMetadata>(email, "attachmentcount");
        (count.IsValidForCreate, count.IsValidForUpdate).Should().Be((false, false));
        var sender = Column<LookupAttributeMetadata>(email, "emailsender");
        sender.Targets.Should().BeEquivalentTo("account", "contact", "queue", "systemuser");
        (sender.IsValidForCreate, sender.IsValidForUpdate).Should().Be((false, false));
        Column<StringAttributeMetadata>(email, "sender");
        Column<StringAttributeMetadata>(email, "torecipients");
        Column<IntegerAttributeMetadata>(email, "deliveryattempts");
    }

    [Fact]
    public void Seed_AddsMailColumns_OnUserQueueAndOrganization()
    {
        foreach (var table in new[] { "systemuser", "queue" })
        {
            Column<PicklistAttributeMetadata>(table, "emailrouteraccessapproval").OptionSet.Options.Should().HaveCount(4);
            var mailbox = Column<LookupAttributeMetadata>(table, "defaultmailbox");
            mailbox.Targets.Should().Equal("mailbox");
            (mailbox.IsValidForCreate, mailbox.IsValidForUpdate).Should().Be((false, false));
            Column<PicklistAttributeMetadata>(table, "incomingemaildeliverymethod");
            Column<PicklistAttributeMetadata>(table, "outgoingemaildeliverymethod");
        }
        Column<PicklistAttributeMetadata>("queue", "incomingemailfilteringmethod").OptionSet.Options.Should().HaveCount(5);
        Column<MemoAttributeMetadata>("organization", "blockedattachments");
        Column<StringAttributeMetadata>("organization", "trackingprefix");
        foreach (var name in new[] { "trackingtokenidbase", "trackingtokeniddigits", "maximumtrackingnumber", "nexttrackingnumber" })
            Column<IntegerAttributeMetadata>("organization", name);
        Column<LookupAttributeMetadata>("organization", "defaultemailserverprofileid").Targets.Should().Equal("emailserverprofile");
        Column<BooleanAttributeMetadata>("organization", "allowunresolvedpartiesonemailsend").DefaultValue.Should().BeFalse();
        Column<BooleanAttributeMetadata>("mailbox", "testemailconfigurationscheduled").IsValidForCreate.Should().NotBe(false);
    }

    [Fact]
    public void Secrets_AreNotValidForRead()
    {
        MailSchema.SecretColumns.Keys.Should().BeEquivalentTo(
            "emailserverprofile.incomingpassword", "emailserverprofile.outgoingpassword", "emailserverprofile.oauthclientsecret", "mailbox.password");
        foreach (var (key, (table, flag)) in MailSchema.SecretColumns)
        {
            var column = key[(key.IndexOf('.') + 1)..];
            Column(table, column).IsValidForRead.Should().BeFalse(key);
            var set = Column<BooleanAttributeMetadata>(table, flag);
            (set.IsValidForCreate, set.IsValidForUpdate).Should().Be((false, false), flag);
        }
    }

    [Fact]
    public async Task Mail_Privileges_AreSeeded()
    {
        foreach (var stem in new[] { "EmailServerProfile", "Mailbox" })
        {
            var names = new[] { "Create", "Read", "Write", "Delete", "Append", "AppendTo", "Assign", "Share" }.Select(v => $"prv{v}{stem}").ToArray();
            (await CountAsync($"SELECT COUNT(*) FROM data.privilege WHERE privilegeid IN ({Ids(names)})")).Should().Be(8, stem);
        }
        // Basic User: Read and Write on its mailbox at user depth (from memory, checklist 3); nothing on the profiles.
        var basic = $"roleid = '{StandardRoles.BasicUser}'";
        (await CountAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE {basic} AND privilegedepthmask = 1 " +
                          $"AND privilegeid IN ({Ids(["prvReadMailbox", "prvWriteMailbox"])})")).Should().Be(2);
        (await CountAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE {basic} AND privilegeid IN ({Ids(["prvCreateMailbox", "prvDeleteMailbox"])})"))
            .Should().Be(0);
        var profileNames = new[] { "Create", "Read", "Write", "Delete", "Append", "AppendTo", "Assign", "Share" }.Select(v => $"prv{v}EmailServerProfile");
        (await CountAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE {basic} AND privilegeid IN ({Ids(profileNames)})")).Should().Be(0);
    }
}
