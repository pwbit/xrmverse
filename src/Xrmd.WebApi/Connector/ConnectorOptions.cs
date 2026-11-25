namespace Xrmd.WebApi.Connector;

/// <summary>X.12b: the <c>Connector</c> configuration section (the flow trigger's webhook subscriptions).</summary>
public sealed class ConnectorOptions
{
    /// <summary>
    /// Host patterns a callback URL may point at, case-insensitive: an exact host, or <c>*.example.com</c> (one or more labels
    /// before <c>example.com</c>).
    /// </summary>
    /// <remarks>
    /// Empty by default: configuration binding appends array items to an existing array, so a non-empty default could not be
    /// replaced. Read <see cref="EffectiveCallbackHosts"/>, which falls back to <see cref="DefaultCallbackHosts"/> when empty.
    /// </remarks>
    public string[] CallbackHosts { get; set; } = [];

    /// <summary>The hosts applied when <see cref="CallbackHosts"/> is empty: the public-cloud flow hosts.</summary>
    public static readonly string[] DefaultCallbackHosts = ["*.logic.azure.com", "*.environment.api.powerplatform.com"];

    /// <summary>The configured <see cref="CallbackHosts"/>, or <see cref="DefaultCallbackHosts"/> when none are configured.</summary>
    public string[] EffectiveCallbackHosts =>
        CallbackHosts is { Length: > 0 } hosts && hosts.Any(h => !string.IsNullOrWhiteSpace(h)) ? hosts : DefaultCallbackHosts;

    /// <summary>The most live subscriptions one user may hold.</summary>
    public int MaxSubscriptionsPerUser { get; set; } = 50;

    /// <summary>Tests and development only: allows <c>http</c>, any port, and loopback and private addresses.</summary>
    public bool AllowPrivateCallbacks { get; set; }
}
