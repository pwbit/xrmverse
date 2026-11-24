using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The seeded <c>serviceendpoint</c> table (plan 3.2): names and values of Learn's ServiceEndpoint table reference.
/// <c>sdkmessageprocessingstep.eventhandler</c> targets it and <c>plugintype</c>.
/// </summary>
public static class ServiceEndpointSchema
{
    /// <summary>The logical name of the table.</summary>
    public const string Table = "serviceendpoint";

    /// <summary>The object type code of the table (also the <c>eventhandlertype</c> of a step on an endpoint).</summary>
    public const int ObjectTypeCode = 4618;

    /// <summary>The <c>contract</c> value of a webhook, the only one xrmverse delivers.</summary>
    public const int WebhookContract = 8;

    /// <summary>The <c>authtype</c> values of a webhook: Webhook Key, Http Header and Http Query String.</summary>
    public const int WebhookKey = 4, HttpHeader = 5, HttpQueryString = 6;

    /// <summary>
    /// The write-only authentication value (<c>IsValidForRead</c> false). It has no data column (<see cref="IsAuthValue"/>):
    /// the value lives in <c>meta.serviceendpoint_secret</c> (migration 0012), one row per endpoint, out of every read path.
    /// </summary>
    public const string AuthValue = "authvalue";

    /// <summary>
    /// True for <c>serviceendpoint.authvalue</c>, which gets no data column (<see cref="TypeMapping.ToColumns"/>), so that no
    /// query, image or export can read it (<see cref="TypeMapping.IsReadable"/> is false).
    /// </summary>
    public static bool IsAuthValue(ColumnDefinition a) => a.EntityLogicalName == Table && a.LogicalName == AuthValue;

    /// <summary>
    /// The refusal (400) of a webhook, a step on one or an image of such a step in a solution, and of an export that meets one.
    /// </summary>
    public const string NotInSolution = "Webhooks and their steps cannot be added to a solution yet.";

    /// <summary>The read-only flag that tells whether <see cref="AuthValue"/> holds a value.</summary>
    public const string IsAuthValueSet = "isauthvalueset";
}
