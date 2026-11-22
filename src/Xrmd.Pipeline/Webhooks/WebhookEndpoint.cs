namespace Xrmd.Pipeline.Webhooks;

/// <summary>
/// A webhook <c>serviceendpoint</c> as the sender needs it: its name (for faults and logs), its url, its <c>authtype</c> (4 Webhook
/// Key, 5 Http Header, 6 Http Query String) and its write-only <c>authvalue</c>. The url and the auth value may carry secrets: only
/// the name and <c>scheme://host/path</c> of the url may appear in a log line or a fault.
/// </summary>
public sealed record WebhookEndpoint(Guid Id, string Name, Uri Url, int AuthType, string? AuthValue)
{
    /// <summary>The record's text: the name and id only (the default record text would print the url and the auth value).</summary>
    public override string ToString() => $"WebhookEndpoint {{ Id = {Id}, Name = {Name} }}";
}
