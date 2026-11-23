using Xrmd.Metadata;

namespace Xrmd.Core.Mail;

/// <summary>The incoming email filtering of the mailbox owner (plan 4.1c, Task 5): which messages are delivered.</summary>
public static class IncomingFilter
{
    /// <summary>
    /// Whether a message from <paramref name="from"/> is delivered under the filtering method <paramref name="filter"/>
    /// (<see cref="MailSchema.FilterAll"/> and the others). <paramref name="correlated"/> says the message answers an email the
    /// organization already holds. Contacts and accounts only count for <see cref="MailSchema.FilterContactsAccounts"/>; an ambiguous
    /// sender still has matches and is admitted.
    /// </summary>
    /// <exception cref="MailSetupException">The value is not one of the five.</exception>
    public static bool Admits(int filter, AddressMatch from, bool correlated)
    {
        ArgumentNullException.ThrowIfNull(from);
        return filter switch
        {
            MailSchema.FilterAll => true,
            MailSchema.FilterResponses => correlated,
            MailSchema.FilterContactsAccounts => from.Records.Any(r => r.LogicalName is "account" or "contact"),
            MailSchema.FilterEmailEnabled => from.Records.Count > 0,
            MailSchema.FilterNone => false,
            _ => throw new MailSetupException($"The incoming email filtering method {filter} is not one of 0-4."),
        };
    }
}
