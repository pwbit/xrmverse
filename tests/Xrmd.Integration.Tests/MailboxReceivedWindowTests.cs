using FluentAssertions;
using Xrmd.Core.Mail;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The first pass after a mailbox test sets <c>processemailreceivedafter</c> to the time it ran: a message that arrived in that
/// same second (IMAP internal dates and Graph filters carry whole seconds) is still delivered.
/// </summary>
public class MailboxReceivedWindowTests
{
    private static readonly DateTime Start = new(2026, 11, 23, 12, 0, 0, 400, DateTimeKind.Utc);

    [Fact]
    public void A_message_of_the_same_second_is_delivered() =>
        MailboxSyncJob.ReceivedInWindow(new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc), Start).Should().BeTrue();

    [Fact]
    public void A_message_of_the_second_before_is_not() =>
        MailboxSyncJob.ReceivedInWindow(new DateTime(2026, 11, 23, 11, 59, 59, DateTimeKind.Utc), Start).Should().BeFalse();

    [Fact]
    public void An_unspecified_kind_is_read_as_UTC() =>
        MailboxSyncJob.ReceivedInWindow(new DateTime(2026, 11, 23, 12, 0, 0), DateTime.SpecifyKind(Start, DateTimeKind.Unspecified))
            .Should().BeTrue();
}
