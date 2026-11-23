using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Admin;
using Xrmd.Core;
using Xrmd.Core.Mail;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 4.1b, task 3: every user and every queue has a mailbox (disabled until it is set up), kept in step with the owner's name and
/// address; the approval of the address and the mailbox's server profile are a System Administrator's; Outlook and Forward Mailbox
/// delivery are refused; and an organization from before the plan gets its mailboxes at the first start only.
/// </summary>
[Collection("xrmd")]
public sealed class MailboxTests(XrmdFixture fx)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private async Task<(Guid Id, Guid Oid)> NewUserAsync(string fullname, string? address = null)
    {
        var oid = Guid.NewGuid();
        var user = new Entity("systemuser")
        {
            ["fullname"] = fullname, ["domainname"] = $"{_tag}.{Guid.NewGuid():N}@example.com", ["azureactivedirectoryobjectid"] = oid,
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        };
        if (address is not null) user["internalemailaddress"] = address;
        return (await Admin.CreateAsync(user), oid);
    }

    private Task<Guid> NewQueueAsync(string name, string? address = null)
    {
        var queue = new Entity("queue") { ["name"] = name, ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1) };
        if (address is not null) queue["emailaddress"] = address;
        return Admin.CreateAsync(queue);
    }

    private async Task<Entity> MailboxOfAsync(string table, Guid owner)
    {
        var reference = (await Admin.RetrieveAsync(table, owner, new ColumnSet("defaultmailbox"))).GetAttributeValue<EntityReference>("defaultmailbox");
        reference.Should().NotBeNull($"the {table} has a mailbox");
        reference.LogicalName.Should().Be("mailbox");
        return await Admin.RetrieveAsync("mailbox", reference.Id, new ColumnSet(true));
    }

    /// <summary>The option value; a column never written (an approval nobody set) is Empty, 0.</summary>
    private static int Option(Entity e, string column) => e.GetAttributeValue<OptionSetValue>(column)?.Value ?? 0;

    [Fact]
    public async Task NewUser_And_NewQueue_GetADisabledMailbox()
    {
        var (user, _) = await NewUserAsync("Ada " + _tag, $"ada.{_tag}@example.com");
        var queue = await NewQueueAsync("Support " + _tag, $"support.{_tag}@example.com");

        var userMailbox = await MailboxOfAsync("systemuser", user);
        userMailbox.GetAttributeValue<string>("name").Should().Be("Ada " + _tag);
        userMailbox.GetAttributeValue<EntityReference>("regardingobjectid").Should().Match<EntityReference>(r => r.LogicalName == "systemuser" && r.Id == user);
        userMailbox.GetAttributeValue<string>("emailaddress").Should().Be($"ada.{_tag}@example.com");
        Option(userMailbox, "incomingemaildeliverymethod").Should().Be(0);
        Option(userMailbox, "outgoingemaildeliverymethod").Should().Be(0);
        Option(userMailbox, "outgoingemailstatus").Should().Be(0);
        Option(userMailbox, "incomingemailstatus").Should().Be(0);
        userMailbox.GetAttributeValue<bool>("enabledforoutgoingemail").Should().BeFalse();
        userMailbox.GetAttributeValue<bool>("enabledforincomingemail").Should().BeFalse();
        Option(userMailbox, "statecode").Should().Be(0);
        userMailbox.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(user);
        // An address makes the approval wait for a System Administrator.
        Option(await Admin.RetrieveAsync("systemuser", user, new ColumnSet("emailrouteraccessapproval")), "emailrouteraccessapproval").Should().Be(2);

        var queueMailbox = await MailboxOfAsync("queue", queue);
        queueMailbox.GetAttributeValue<string>("name").Should().Be("Support " + _tag);
        queueMailbox.GetAttributeValue<EntityReference>("regardingobjectid").Should().Match<EntityReference>(r => r.LogicalName == "queue" && r.Id == queue);
        queueMailbox.GetAttributeValue<string>("emailaddress").Should().Be($"support.{_tag}@example.com");
        Option(queueMailbox, "outgoingemaildeliverymethod").Should().Be(0);
        queueMailbox.GetAttributeValue<bool>("enabledforoutgoingemail").Should().BeFalse();

        // The default queue of the user is a queue too: it has its mailbox.
        var defaultQueue = (await Admin.RetrieveAsync("systemuser", user, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid").Id;
        (await MailboxOfAsync("queue", defaultQueue)).GetAttributeValue<string>("name").Should().Be($"<Ada {_tag}>");

        // A user without an address has no approval to wait for.
        var (plain, _) = await NewUserAsync("Plain " + _tag);
        (await MailboxOfAsync("systemuser", plain)).GetAttributeValue<string>("emailaddress").Should().BeNull();
        Option(await Admin.RetrieveAsync("systemuser", plain, new ColumnSet("emailrouteraccessapproval")), "emailrouteraccessapproval").Should().Be(0);
    }

    [Fact]
    public async Task AddressChange_UpdatesMailbox_AndResetsApproval()
    {
        var (user, _) = await NewUserAsync("Grace " + _tag, $"grace.{_tag}@example.com");
        var queue = await NewQueueAsync("Billing " + _tag, $"billing.{_tag}@example.com");
        await Admin.UpdateAsync(new Entity("systemuser", user) { ["emailrouteraccessapproval"] = new OptionSetValue(1) });
        await Admin.UpdateAsync(new Entity("queue", queue) { ["emailrouteraccessapproval"] = new OptionSetValue(1) });
        (await MailboxOfAsync("systemuser", user)).GetAttributeValue<OptionSetValue>("emailrouteraccessapproval").Value.Should().Be(1);
        // The mailbox has been tested: a new address starts again.
        var userMailbox = (await MailboxOfAsync("systemuser", user)).Id;
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.mailbox SET outgoingemailstatus = 1 WHERE mailboxid = @id", ("@id", userMailbox));

        await Admin.UpdateAsync(new Entity("systemuser", user) { ["internalemailaddress"] = $"new.{_tag}@example.com" });
        await Admin.UpdateAsync(new Entity("queue", queue) { ["emailaddress"] = $"new-billing.{_tag}@example.com", ["name"] = "Billing 2 " + _tag });

        Option(await Admin.RetrieveAsync("systemuser", user, new ColumnSet("emailrouteraccessapproval")), "emailrouteraccessapproval").Should().Be(2);
        var changed = await MailboxOfAsync("systemuser", user);
        changed.GetAttributeValue<string>("emailaddress").Should().Be($"new.{_tag}@example.com");
        Option(changed, "emailrouteraccessapproval").Should().Be(2);
        Option(changed, "outgoingemailstatus").Should().Be(0);
        Option(await Admin.RetrieveAsync("queue", queue, new ColumnSet("emailrouteraccessapproval")), "emailrouteraccessapproval").Should().Be(2);
        var queueMailbox = await MailboxOfAsync("queue", queue);
        queueMailbox.GetAttributeValue<string>("emailaddress").Should().Be($"new-billing.{_tag}@example.com");
        queueMailbox.GetAttributeValue<string>("name").Should().Be("Billing 2 " + _tag);

        // The same address, in another case, is no change; a System Administrator who approves in the same write keeps the decision.
        await Admin.UpdateAsync(new Entity("systemuser", user) { ["emailrouteraccessapproval"] = new OptionSetValue(1) });
        await Admin.UpdateAsync(new Entity("systemuser", user) { ["internalemailaddress"] = $"NEW.{_tag}@example.com" });
        Option(await Admin.RetrieveAsync("systemuser", user, new ColumnSet("emailrouteraccessapproval")), "emailrouteraccessapproval").Should().Be(1);
        await Admin.UpdateAsync(new Entity("systemuser", user) { ["emailrouteraccessapproval"] = new OptionSetValue(2) });
        await Admin.UpdateAsync(new Entity("systemuser", user)
            { ["internalemailaddress"] = $"third.{_tag}@example.com", ["emailrouteraccessapproval"] = new OptionSetValue(1) });
        Option(await Admin.RetrieveAsync("systemuser", user, new ColumnSet("emailrouteraccessapproval")), "emailrouteraccessapproval").Should().Be(1);
    }

    [Fact]
    public async Task QueueDelete_DeletesItsMailbox_AndSecrets()
    {
        var queue = await NewQueueAsync("Gone " + _tag, $"gone.{_tag}@example.com");
        var mailbox = (await MailboxOfAsync("queue", queue)).Id;
        await Admin.UpdateAsync(new Entity("mailbox", mailbox) { ["username"] = "gone", ["password"] = "s3cret-" + _tag });
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM meta.mail_secret WHERE recordid = @id", ("@id", mailbox))).Should().Be(1);

        await Admin.DeleteAsync("queue", queue);

        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM data.mailbox WHERE mailboxid = @id", ("@id", mailbox))).Should().Be(0);
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM meta.mail_secret WHERE recordid = @id", ("@id", mailbox))).Should().Be(0);
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM data.queue WHERE queueid = @id", ("@id", queue))).Should().Be(0);
    }

    [Fact]
    public async Task TeamDelete_DeletesItsDefaultQueue_AndThatQueuesMailbox()
    {
        var team = await Admin.CreateAsync(new Entity("team")
        {
            ["name"] = "Team " + _tag, ["teamtype"] = new OptionSetValue(0),
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
        });
        var queue = (await Admin.RetrieveAsync("team", team, new ColumnSet("queueid"))).GetAttributeValue<EntityReference>("queueid").Id;
        var mailbox = (await MailboxOfAsync("queue", queue)).Id;

        await Admin.DeleteAsync("team", team);

        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM data.mailbox WHERE mailboxid = @id", ("@id", mailbox))).Should().Be(0);
    }

    /// <summary>A user with the Basic User role and Write on users and queues at the widest depth, so the only refusal left is the approval's.</summary>
    private async Task<(Guid Id, OrganizationService Service)> BasicUserAsync(string fullname)
    {
        var (id, oid) = await NewUserAsync(fullname, $"{_tag}.{Guid.NewGuid():N}@example.com");
        await AssignRoleAsync(fx, id, StandardRoles.BasicUser);
        await GrantAsync(fx, id, team: false, ("prvWriteSystemUser", DepthMask.Global), ("prvWriteQueue", DepthMask.Global));
        return (id, ServiceFor(fx, oid));
    }

    [Fact]
    public async Task Approval_ByNonAdmin_Is403()
    {
        var (basic, service) = await BasicUserAsync("Basic " + _tag);
        var queue = await NewQueueAsync("Approved " + _tag, $"q.{_tag}@example.com");

        foreach (var (table, id) in new[] { ("systemuser", basic), ("queue", queue) })
        {
            var fault = await FaultAsync(() => service.UpdateAsync(new Entity(table, id) { ["emailrouteraccessapproval"] = new OptionSetValue(1) }));
            fault.HttpStatus.Should().Be(403);
            fault.Message.Should().Contain("emailrouteraccessapproval");
            Option(await Admin.RetrieveAsync(table, id, new ColumnSet("emailrouteraccessapproval")), "emailrouteraccessapproval").Should().Be(2);
        }
        // A client that sends back the stored value changes nothing.
        await service.UpdateAsync(new Entity("systemuser", basic) { ["emailrouteraccessapproval"] = new OptionSetValue(2) });
    }

    [Fact]
    public async Task Approval_ByAdmin_Succeeds()
    {
        var (user, _) = await NewUserAsync("Approved " + _tag, $"a.{_tag}@example.com");
        var queue = await NewQueueAsync("Approved " + _tag, $"b.{_tag}@example.com");

        await Admin.UpdateAsync(new Entity("systemuser", user) { ["emailrouteraccessapproval"] = new OptionSetValue(1) });
        await Admin.UpdateAsync(new Entity("queue", queue) { ["emailrouteraccessapproval"] = new OptionSetValue(3) });

        Option(await Admin.RetrieveAsync("systemuser", user, new ColumnSet("emailrouteraccessapproval")), "emailrouteraccessapproval").Should().Be(1);
        Option(await Admin.RetrieveAsync("queue", queue, new ColumnSet("emailrouteraccessapproval")), "emailrouteraccessapproval").Should().Be(3);
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var state = await Mailboxes.ResolveSenderAsync(new DataSession(conn, null), new EntityReference("systemuser", user), default);
        state.Approved.Should().BeTrue();
        state.CanSend.Should().BeFalse("it is not set up");
        (await Mailboxes.ResolveSenderAsync(new DataSession(conn, null), new EntityReference("queue", queue), default)).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task BasicUser_CannotChangeMailboxProfile_OrApproval()
    {
        var (basic, service) = await BasicUserAsync("Basic " + _tag);
        var (other, _) = await NewUserAsync("Other " + _tag);
        var profile = await Admin.CreateAsync(new Entity("emailserverprofile")
        {
            ["name"] = "smtp " + _tag, ["servertype"] = new OptionSetValue(4), ["outgoingserverlocation"] = "smtp.example.com",
            ["outgoingauthenticationprotocol"] = new OptionSetValue(3), ["outgoingcredentialretrieval"] = new OptionSetValue(0),
        });
        var own = (await MailboxOfAsync("systemuser", basic)).Id;
        var others = (await MailboxOfAsync("systemuser", other)).Id;

        await service.UpdateAsync(new Entity("mailbox", own)
        {
            ["username"] = "basic@example.com", ["password"] = "p4ss-" + _tag, ["outgoingemaildeliverymethod"] = new OptionSetValue(2),
            ["incomingemaildeliverymethod"] = new OptionSetValue(2), ["enabledforoutgoingemail"] = true, ["testemailconfigurationscheduled"] = true,
        });
        var written = await Admin.RetrieveAsync("mailbox", own, new ColumnSet(true));
        written.GetAttributeValue<string>("username").Should().Be("basic@example.com");
        written.GetAttributeValue<bool>("ispasswordset").Should().BeTrue();
        written.GetAttributeValue<bool>("enabledforoutgoingemail").Should().BeTrue();
        Option(written, "outgoingemaildeliverymethod").Should().Be(2);

        var profileFault = await FaultAsync(() => service.UpdateAsync(new Entity("mailbox", own)
            { ["emailserverprofile"] = new EntityReference("emailserverprofile", profile) }));
        profileFault.HttpStatus.Should().Be(403);
        var approvalFault = await FaultAsync(() => service.UpdateAsync(new Entity("mailbox", own) { ["emailrouteraccessapproval"] = new OptionSetValue(1) }));
        approvalFault.HttpStatus.Should().Be(403);
        (await Admin.RetrieveAsync("mailbox", own, new ColumnSet("emailserverprofile"))).GetAttributeValue<EntityReference>("emailserverprofile").Should().BeNull();

        // A client that sends back the stored (empty) profile with its other changes is no change.
        await service.UpdateAsync(new Entity("mailbox", own) { ["emailserverprofile"] = null, ["username"] = "again@example.com" });

        // Another user's mailbox is not theirs to write.
        (await FaultAsync(() => service.UpdateAsync(new Entity("mailbox", others) { ["username"] = "x@example.com" }))).HttpStatus.Should().Be(403);

        // A System Administrator sets the profile.
        await Admin.UpdateAsync(new Entity("mailbox", own) { ["emailserverprofile"] = new EntityReference("emailserverprofile", profile) });
        (await Admin.RetrieveAsync("mailbox", own, new ColumnSet("emailserverprofile"))).GetAttributeValue<EntityReference>("emailserverprofile").Id.Should().Be(profile);
        (await FaultAsync(() => service.UpdateAsync(new Entity("mailbox", own) { ["emailserverprofile"] = null }))).HttpStatus.Should().Be(403);
    }

    [Fact]
    public async Task BasicUser_CannotWriteServerColumns_OfOwnMailbox_AndNobodyWritesTheAddress()
    {
        var (basic, service) = await BasicUserAsync("Basic " + _tag);
        var own = (await MailboxOfAsync("systemuser", basic)).Id;

        foreach (var change in new Entity[]
                 {
                     new("mailbox", own) { ["emailaddress"] = $"ceo.{_tag}@example.com" },
                     new("mailbox", own) { ["outgoingemailstatus"] = new OptionSetValue(1) },
                     new("mailbox", own) { ["statecode"] = new OptionSetValue(1), ["statuscode"] = new OptionSetValue(2) },
                     new("mailbox", own) { ["isemailaddressapprovedbyo365admin"] = true },
                 })
            (await FaultAsync(() => service.UpdateAsync(change))).HttpStatus.Should().Be(403);

        // Not even a System Administrator writes the address or a status: the owner's address and the server do.
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("mailbox", own) { ["emailaddress"] = $"ceo.{_tag}@example.com" })))
            .Should().Contain("owner").And.Contain("email address");
        await BadRequestAsync(() => Admin.UpdateAsync(new Entity("mailbox", own) { ["outgoingemailstatus"] = new OptionSetValue(1) }));
        // Sending the stored values back is no write.
        var stored = await Admin.RetrieveAsync("mailbox", own, new ColumnSet("emailaddress", "outgoingemailstatus"));
        await Admin.UpdateAsync(new Entity("mailbox", own) { ["emailaddress"] = stored["emailaddress"], ["outgoingemailstatus"] = stored["outgoingemailstatus"], ["username"] = "same" });
    }

    [Fact]
    public async Task NonAdmin_ChangingAddress_WithStoredApproval_StaysPending()
    {
        var (_, service) = await BasicUserAsync("Basic " + _tag);
        var queue = await NewQueueAsync("Attack " + _tag, $"support.{_tag}@example.com");
        await Admin.UpdateAsync(new Entity("queue", queue) { ["emailrouteraccessapproval"] = new OptionSetValue(1) });

        await service.UpdateAsync(new Entity("queue", queue) { ["emailaddress"] = $"ceo.{_tag}@example.com", ["emailrouteraccessapproval"] = new OptionSetValue(1) });

        Option(await Admin.RetrieveAsync("queue", queue, new ColumnSet("emailrouteraccessapproval")), "emailrouteraccessapproval").Should().Be(2);
        Option(await MailboxOfAsync("queue", queue), "emailrouteraccessapproval").Should().Be(2);
    }

    [Fact]
    public async Task ResolveSender_UsesTheOwnersMailbox_AndAddress()
    {
        var queue = await NewQueueAsync("Two " + _tag, $"two.{_tag}@example.com");
        var first = (await MailboxOfAsync("queue", queue)).Id;
        var second = await Admin.CreateAsync(new Entity("mailbox") { ["name"] = "extra", ["regardingobjectid"] = new EntityReference("queue", queue) });
        await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.mailbox SET emailaddress = 'other@example.com' WHERE mailboxid = @id", ("@id", first));

        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var state = await Mailboxes.ResolveSenderAsync(new DataSession(conn, null), new EntityReference("queue", queue), default);

        state.Id.Should().Be(first).And.NotBe(second);
        state.Address.Should().Be($"two.{_tag}@example.com", "the owner's address, not the mailbox copy");
    }

    [Fact]
    public async Task DeliveryMethod_OutlookOrForward_Is400()
    {
        var (user, _) = await NewUserAsync("Delivery " + _tag);
        var queue = await NewQueueAsync("Delivery " + _tag);
        var mailbox = (await MailboxOfAsync("systemuser", user)).Id;

        foreach (var (table, id) in new[] { ("mailbox", mailbox), ("queue", queue), ("systemuser", user) })
        {
            (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(table, id) { ["incomingemaildeliverymethod"] = new OptionSetValue(3) })))
                .Should().Contain("Forward Mailbox");
            await BadRequestAsync(() => Admin.UpdateAsync(new Entity(table, id) { ["outgoingemaildeliverymethod"] = new OptionSetValue(1) }));
            await BadRequestAsync(() => Admin.UpdateAsync(new Entity(table, id) { ["incomingemaildeliverymethod"] = new OptionSetValue(1) }));
            // Server-Side Synchronization is the one that works.
            await Admin.UpdateAsync(new Entity(table, id) { ["outgoingemaildeliverymethod"] = new OptionSetValue(2) });
        }
        await BadRequestAsync(() => NewQueueWithAsync(3));
    }

    [Fact]
    public async Task NewMailbox_GetsTheOrganizationsDefaultProfile_AndKeepsDeliveryNone()
    {
        // Final review I3: organization.defaultemailserverprofileid is the profile a new mailbox gets.
        var profile = await Admin.CreateAsync(new Entity("emailserverprofile")
        {
            ["name"] = "Default " + _tag, ["servertype"] = new OptionSetValue(4), ["outgoingserverlocation"] = "smtp.example.com",
            ["outgoingportnumber"] = 587, ["outgoingauthenticationprotocol"] = new OptionSetValue(3), ["outgoingcredentialretrieval"] = new OptionSetValue(0),
        });
        var organization = (Guid)(await TestSql.ScalarAsync(fx.ConnectionString, "SELECT organizationid FROM data.organization"))!;
        try
        {
            var (before, _) = await NewUserAsync("Before " + _tag);
            (await MailboxOfAsync("systemuser", before)).GetAttributeValue<EntityReference>("emailserverprofile").Should().BeNull("no default is set yet");

            await Admin.UpdateAsync(new Entity("organization", organization)
                { ["defaultemailserverprofileid"] = new EntityReference("emailserverprofile", profile) });
            var (user, _) = await NewUserAsync("Profiled " + _tag, $"profiled.{_tag}@example.com");
            var queue = await NewQueueAsync("Profiled " + _tag, $"profiled-q.{_tag}@example.com");

            foreach (var mailbox in new[] { await MailboxOfAsync("systemuser", user), await MailboxOfAsync("queue", queue) })
            {
                mailbox.GetAttributeValue<EntityReference>("emailserverprofile")?.Id.Should().Be(profile);
                Option(mailbox, "outgoingemaildeliverymethod").Should().Be(0, "the profile alone does not turn delivery on");
                Option(mailbox, "incomingemaildeliverymethod").Should().Be(0);
                mailbox.GetAttributeValue<bool>("enabledforoutgoingemail").Should().BeFalse();
            }
            // A mailbox made before the default keeps what it had.
            (await MailboxOfAsync("systemuser", before)).GetAttributeValue<EntityReference>("emailserverprofile").Should().BeNull();
        }
        finally
        {
            await TestSql.ExecAsync(fx.ConnectionString, "UPDATE data.organization SET defaultemailserverprofileid = NULL");
        }
    }

    [Fact]
    public async Task OwnerDeliveryMethods_AreMirroredToTheMailbox_OnCreateAndUpdate()
    {
        // Final review I3: the incoming and outgoing delivery methods of a user or a queue are its mailbox's.
        var oid = Guid.NewGuid();
        var user = await Admin.CreateAsync(new Entity("systemuser")
        {
            ["fullname"] = "Synced " + _tag, ["domainname"] = $"{_tag}.{Guid.NewGuid():N}@example.com", ["azureactivedirectoryobjectid"] = oid,
            ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)),
            ["outgoingemaildeliverymethod"] = new OptionSetValue(2),
        });
        var queue = await Admin.CreateAsync(new Entity("queue")
        {
            ["name"] = "Synced " + _tag, ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1),
            ["incomingemaildeliverymethod"] = new OptionSetValue(2),
        });

        var userMailbox = await MailboxOfAsync("systemuser", user);
        (Option(userMailbox, "incomingemaildeliverymethod"), Option(userMailbox, "outgoingemaildeliverymethod")).Should().Be((0, 2));
        var queueMailbox = await MailboxOfAsync("queue", queue);
        (Option(queueMailbox, "incomingemaildeliverymethod"), Option(queueMailbox, "outgoingemaildeliverymethod")).Should().Be((2, 0));

        await Admin.UpdateAsync(new Entity("systemuser", user)
            { ["incomingemaildeliverymethod"] = new OptionSetValue(2), ["outgoingemaildeliverymethod"] = new OptionSetValue(0) });
        await Admin.UpdateAsync(new Entity("queue", queue) { ["outgoingemaildeliverymethod"] = new OptionSetValue(2) });
        await Admin.UpdateAsync(new Entity("queue", queue) { ["incomingemaildeliverymethod"] = null });

        userMailbox = await MailboxOfAsync("systemuser", user);
        (Option(userMailbox, "incomingemaildeliverymethod"), Option(userMailbox, "outgoingemaildeliverymethod")).Should().Be((2, 0));
        queueMailbox = await MailboxOfAsync("queue", queue);
        (Option(queueMailbox, "incomingemaildeliverymethod"), Option(queueMailbox, "outgoingemaildeliverymethod")).Should().Be((0, 2), "an empty method is None");
    }

    [Theory]
    [InlineData("systemuser")]
    [InlineData("queue")]
    public async Task OwnerWrittenBackUnchanged_KeepsTheMailboxsDeliveryMethod_ARealChangeStillPropagates(string table)
    {
        // Fix-wave N1: a mailbox linked on itself (xrmd mailbox link, the web admin, a PATCH of the mailbox) keeps its delivery
        // method when its user or queue is read with all columns and written back unchanged.
        var owner = table == "systemuser"
            ? (await NewUserAsync("WriteBack " + _tag, $"wb.{_tag}@example.com")).Id
            : await NewQueueAsync("WriteBack " + _tag, $"wbq.{_tag}@example.com");
        // The owner's stored methods are None (0), so a read-all returns them and the write-back resends them.
        await Admin.UpdateAsync(new Entity(table, owner)
            { ["incomingemaildeliverymethod"] = new OptionSetValue(0), ["outgoingemaildeliverymethod"] = new OptionSetValue(0) });
        var mailbox = (await MailboxOfAsync(table, owner)).Id;
        await Admin.UpdateAsync(new Entity("mailbox", mailbox) { ["outgoingemaildeliverymethod"] = new OptionSetValue(2), ["enabledforoutgoingemail"] = true });

        var all = await Admin.RetrieveAsync(table, owner, new ColumnSet(true));
        all.GetAttributeValue<OptionSetValue>("outgoingemaildeliverymethod")?.Value.Should().Be(0, "the write-back resends the stored None");
        await Admin.UpdateAsync(all);

        Option(await MailboxOfAsync(table, owner), "outgoingemaildeliverymethod").Should().Be(2, "an unchanged value is no change");

        // A real change on the owner still reaches the mailbox.
        await Admin.UpdateAsync(new Entity(table, owner) { ["incomingemaildeliverymethod"] = new OptionSetValue(2) });
        var after = await MailboxOfAsync(table, owner);
        (Option(after, "incomingemaildeliverymethod"), Option(after, "outgoingemaildeliverymethod")).Should().Be((2, 2));
        await Admin.UpdateAsync(new Entity(table, owner) { ["outgoingemaildeliverymethod"] = new OptionSetValue(2) });
        await Admin.UpdateAsync(new Entity(table, owner) { ["outgoingemaildeliverymethod"] = new OptionSetValue(0) });
        Option(await MailboxOfAsync(table, owner), "outgoingemaildeliverymethod").Should().Be(0);
    }

    private Task<Guid> NewQueueWithAsync(int incoming) => Admin.CreateAsync(new Entity("queue")
    {
        ["name"] = "Forward " + _tag, ["queueviewtype"] = new OptionSetValue(0), ["queuetypecode"] = new OptionSetValue(1),
        ["incomingemaildeliverymethod"] = new OptionSetValue(incoming),
    });

    // ---- startup ----

    private static async Task<object?> ScalarAsync(IDbConnectionFactory factory, string sql)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, sql);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result is long n ? (int)n : result;
    }

    private static Task StartAsync(string connectionString, SeedOptions seed) => Task.Run(async () =>
    {
        await using var services = OrganizationDatabase.BuildServices(connectionString, TestProvider.Current);
        await OrganizationDatabase.PopulateAsync(services, seed);
    });

    [Fact]
    public async Task QueueMailbox_FollowsTheQueuesOwner_OnAssignAndUpdate()
    {
        var (first, _) = await NewUserAsync("First " + _tag);
        var (second, _) = await NewUserAsync("Second " + _tag);
        await AssignRoleAsync(fx, first, StandardRoles.BasicUser);
        await AssignRoleAsync(fx, second, StandardRoles.BasicUser);
        var queue = await NewQueueAsync("Owned " + _tag);

        await Admin.ExecuteAsync(new Microsoft.Crm.Sdk.Messages.AssignRequest
            { Target = new EntityReference("queue", queue), Assignee = new EntityReference("systemuser", first) });
        var mailbox = await MailboxOfAsync("queue", queue);
        mailbox.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(first);

        await Admin.UpdateAsync(new Entity("queue", queue) { ["ownerid"] = new EntityReference("systemuser", second) });
        mailbox = await MailboxOfAsync("queue", queue);
        mailbox.GetAttributeValue<EntityReference>("ownerid").Id.Should().Be(second);
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM data.mailbox WHERE mailboxid = @id AND owninguser = @u", ("@id", mailbox.Id), ("@u", second))).Should().Be(1);
    }

    [Fact]
    public async Task MailboxApproval_IsMirrored_ADirectWriteIs400()
    {
        var (user, _) = await NewUserAsync("Mirror " + _tag, $"m.{_tag}@example.com");
        var mailbox = (await MailboxOfAsync("systemuser", user)).Id;

        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("mailbox", mailbox) { ["emailrouteraccessapproval"] = new OptionSetValue(1) })))
            .Should().Contain("approve the user or the queue");
    }

    [Fact]
    public async Task LaterStart_GivesANewAdministratorItsMailbox_AndSystemNone()
    {
        var database = "xrmd_mbx_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var connectionString = TestProvider.WithDatabase(fx.ConnectionString, database);
        var factory = TestProvider.Factory(connectionString);
        var seed = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
        try
        {
            await StartAsync(connectionString, seed);
            var newAdmin = Guid.NewGuid();
            await StartAsync(connectionString, seed with { AdminObjectId = newAdmin, AdminName = "Bob" });

            (await ScalarAsync(factory, $"SELECT COUNT(*) FROM data.systemuser u JOIN data.mailbox m ON m.mailboxid = u.defaultmailbox WHERE u.azureactivedirectoryobjectid = '{newAdmin}'"))
                .Should().Be(1, "the administrator added at this start has its mailbox in the same start");
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.systemuser WHERE fullname = 'SYSTEM' AND defaultmailbox IS NOT NULL")).Should().Be(0);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.mailbox m JOIN data.systemuser u ON u.systemuserid = m.regardingobjectid WHERE u.fullname = 'SYSTEM'")).Should().Be(0);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.mailbox m JOIN data.queue q ON q.queueid = m.regardingobjectid JOIN data.systemuser u ON u.systemuserid = q.ownerid WHERE u.fullname = 'SYSTEM'"))
                .Should().Be(0);
        }
        finally
        {
            await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
        }
    }

    [Fact]
    public async Task Startup_Twice_WritesNothingTheSecondTime()
    {
        var database = "xrmd_mbx_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var connectionString = TestProvider.WithDatabase(fx.ConnectionString, database);
        var factory = TestProvider.Factory(connectionString);
        var seed = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
        try
        {
            await StartAsync(connectionString, seed);
            // From before the plan: users, an owner team and a queue that nobody gave a mailbox (the start above gave the ones it knew).
            var bu = (Guid)(await ScalarAsync(factory, "SELECT businessunitid FROM data.businessunit WHERE parentbusinessunitid IS NULL"))!;
            var now = TestSql.Dialect.UtcNow;
            var no = TestSql.Dialect.BooleanLiteral(false);
            for (var i = 0; i < 3; i++)
                await ScalarAsync(factory, $"""
                    INSERT INTO data.systemuser (systemuserid, fullname, domainname, businessunitid, isdisabled, owningbusinessunit, createdon, modifiedon, statecode, statuscode, internalemailaddress)
                    VALUES ('{Guid.NewGuid()}', 'Old user {i}', 'old{i}@example.com', '{bu}', {no}, '{bu}', {now}, {now}, 0, 1, 'old{i}@example.com')
                    """);
            await ScalarAsync(factory, $"""
                INSERT INTO data.team (teamid, name, businessunitid, teamtype, isdefault, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
                VALUES ('{Guid.NewGuid()}', 'Old team', '{bu}', 0, {no}, '{bu}', {now}, {now}, 0, 1)
                """);

            await StartAsync(connectionString, seed);

            // The built-in SYSTEM user and the queue it owns get none.
            const string notSystem = "NOT (fullname = 'SYSTEM' AND azureactivedirectoryobjectid IS NULL)";
            const string queueNotSystem = "NOT EXISTS (SELECT 1 FROM data.systemuser u WHERE u.systemuserid = data.queue.ownerid AND u.fullname = 'SYSTEM' AND u.azureactivedirectoryobjectid IS NULL)";
            var owners = (int)(await ScalarAsync(factory, $"SELECT COUNT(*) FROM data.systemuser WHERE {notSystem}"))!
                         + (int)(await ScalarAsync(factory, $"SELECT COUNT(*) FROM data.queue WHERE {queueNotSystem}"))!;
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.mailbox")).Should().Be(owners, "one mailbox for every user and queue, team default queues included");
            (await ScalarAsync(factory, $"SELECT COUNT(*) FROM data.systemuser WHERE defaultmailbox IS NULL AND {notSystem}")).Should().Be(0);
            (await ScalarAsync(factory, $"SELECT COUNT(*) FROM data.queue WHERE defaultmailbox IS NULL AND {queueNotSystem}")).Should().Be(0);
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.mailbox m JOIN data.systemuser u ON u.defaultmailbox = m.mailboxid AND u.systemuserid = m.regardingobjectid"))
                .Should().Be(await ScalarAsync(factory, $"SELECT COUNT(*) FROM data.systemuser WHERE {notSystem}"));
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.systemuser WHERE emailrouteraccessapproval = 2")).Should().Be(3, "the three users with an address wait for approval");
            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.mailbox WHERE enabledforoutgoingemail = " + TestSql.Dialect.BooleanLiteral(true))).Should().Be(0);

            var before = await ScalarAsync(factory, "SELECT COUNT(*) + COALESCE(SUM(CASE WHEN defaultmailbox IS NULL THEN 1 ELSE 0 END), 0) FROM data.queue");
            var stamp = await ScalarAsync(factory, "SELECT MAX(modifiedon) FROM data.mailbox");
            await StartAsync(connectionString, seed);

            (await ScalarAsync(factory, "SELECT COUNT(*) FROM data.mailbox")).Should().Be(owners);
            (await ScalarAsync(factory, "SELECT MAX(modifiedon) FROM data.mailbox")).Should().Be(stamp);
            (await ScalarAsync(factory, "SELECT COUNT(*) + COALESCE(SUM(CASE WHEN defaultmailbox IS NULL THEN 1 ELSE 0 END), 0) FROM data.queue")).Should().Be(before);
        }
        finally
        {
            await TestProvider.DropDatabaseAsync(fx.ConnectionString, database);
        }
    }
}
