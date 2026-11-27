using System.Xml;
using System.Xml.Linq;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Records;
using Xrmd.Security;

namespace Xrmd.Pipeline.Webhooks;

/// <summary>
/// The rules of an accepted <c>serviceendpoint</c> (plan 3.2): xrmverse delivers webhooks only, so the endpoint is a webhook
/// (<c>contract</c> 8) with authtype 4, 5 or 6, an absolute http(s) <c>url</c> without user information or fragment on port 80 or
/// 443 (any port with <see cref="PipelineOptions.WebhookAllowAnyPort"/>), an <c>authvalue</c> of its authtype, and none of the
/// Azure Service Bus columns. Checked on Create and on Update as the row will be (400 <c>InvalidArgument</c>). No message repeats
/// the url or the authvalue: they may carry secrets.
/// </summary>
public static partial class ServiceEndpointRules
{
    private const string Name = "name", Contract = "contract", AuthType = "authtype", Url = "url";

    /// <summary>The labels of <c>serviceendpoint_contract</c>.</summary>
    private static readonly Dictionary<int, string> ContractLabels = new()
    {
        [1] = "OneWay", [2] = "Queue", [3] = "Rest", [4] = "TwoWay", [5] = "Topic", [6] = "Queue (Persistent)", [7] = "Event Hub",
        [8] = "Webhook", [9] = "Event Grid", [10] = "Managed Data Lake", [11] = "Container Storage",
    };

    /// <summary>The labels of <c>serviceendpoint_authtype</c>.</summary>
    private static readonly Dictionary<int, string> AuthTypeLabels = new()
    {
        [0] = "Not Specified", [1] = "ACS", [2] = "SAS Key", [3] = "SAS Token", [4] = "Webhook Key", [5] = "Http Header",
        [6] = "Http Query String", [7] = "Connection String", [8] = "Access Key", [9] = "Managed Identity",
    };

    /// <summary>The choice columns of Azure Service Bus endpoints, with the values a webhook accepts (null always accepted).</summary>
    private static readonly (string Column, int[] Accepted)[] ServiceBusChoices =
    [
        ("messageformat", [2]), ("messagecharset", [0, 1]), ("connectionmode", [1]), ("userclaim", [1]),
    ];

    /// <summary>The text columns of Azure Service Bus endpoints, which a webhook leaves empty.</summary>
    private static readonly string[] ServiceBusTexts = ["namespaceaddress", "path", "solutionnamespace", "saskeyname"];

    /// <summary>The headers xrmverse sets itself, which a Http Header endpoint may not set.</summary>
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Host", "Transfer-Encoding",
    };

    private const string ReservedHeaderPrefix = "x-ms-";

    /// <summary>The MaxLength of <c>authvalue</c> (and of its column in <c>meta.serviceendpoint_secret</c>).</summary>
    public const int MaxAuthValueLength = 2048;

    private const string SettingsShape = "is not a settings XML (<settings><setting name=\"...\" value=\"...\" /></settings>)";

    /// <summary>
    /// The refusal of <paramref name="record"/>, the endpoint as it will be stored (with its <c>authvalue</c>), or null when it
    /// is an accepted webhook. The message names the endpoint and never repeats the url or the authvalue. A read of a column of
    /// another type throws today's <see cref="InvalidCastException"/> text, with the SDK type names (S.2e3c2b, Decision 1).
    /// </summary>
    /// <param name="record">The endpoint: the Create target, or the stored row overlaid with the Update target and the secret.</param>
    /// <param name="allowAnyPort">Accept a url on any port (<c>Pipeline:WebhookAllowAnyPort</c>, tests and development).</param>
    public static string? EndpointProblem(Record record, bool allowAnyPort)
    {
        ArgumentNullException.ThrowIfNull(record);
        var name = record.SdkValue<string>(Name) ?? "";

        if (Choice(record, Contract) is not { } contract) return $"Service endpoint '{name}' needs a contract.";
        if (contract != ServiceEndpointSchema.WebhookContract)
            return $"Service endpoint '{name}': contract {contract} ({Label(ContractLabels, contract)}) is not supported; " +
                   $"xrmverse delivers webhooks (contract {ServiceEndpointSchema.WebhookContract}) only.";

        // A missing authtype is Not Specified (0), which a webhook does not accept.
        var authType = Choice(record, AuthType) ?? 0;
        if (authType is not (ServiceEndpointSchema.WebhookKey or ServiceEndpointSchema.HttpHeader or ServiceEndpointSchema.HttpQueryString))
            return $"Webhook '{name}': authtype {authType} ({Label(AuthTypeLabels, authType)}) is not supported; " +
                   "use 4 (Webhook Key), 5 (Http Header) or 6 (Http Query String).";

        if (UrlProblem(record.SdkValue<string>(Url), allowAnyPort) is { } url) return $"Webhook '{name}': url {url}.";

        var authValue = record.Attributes.TryGetValue(ServiceEndpointSchema.AuthValue, out var v) ? v as string : null;
        if (string.IsNullOrWhiteSpace(authValue)) return NeedsAuthValue(name);
        if (authValue.Length > MaxAuthValueLength) return $"Webhook '{name}': authvalue is longer than {MaxAuthValueLength} characters.";
        if (authType != ServiceEndpointSchema.WebhookKey && AuthValueProblem(authValue, authType == ServiceEndpointSchema.HttpHeader) is { } auth)
            return $"Webhook '{name}': authvalue {auth}.";

        foreach (var (column, accepted) in ServiceBusChoices)
            if (Choice(record, column) is { } value && !accepted.Contains(value)) return ServiceBusOnly(name, column);
        foreach (var column in ServiceBusTexts)
            if (record.Attributes.TryGetValue(column, out var text) && text is not null && !(text is string s && s.Length == 0))
                return ServiceBusOnly(name, column);
        return null;
    }

    /// <summary>The refusal of a webhook without an <c>authvalue</c> (missing, empty or blank).</summary>
    public static string NeedsAuthValue(string? name) => $"Webhook '{name}' needs an authvalue.";

    /// <summary>
    /// Reads the settings XML of an Http Header or Http Query String endpoint:
    /// <c>&lt;settings&gt;&lt;setting name="K" value="V" /&gt;…&lt;/settings&gt;</c>, at least one setting, each with a non-blank
    /// name and a value. <paramref name="problem"/> is the reason when it is not (never the XML itself).
    /// </summary>
    public static bool TryParseSettings(string xml, out IReadOnlyList<KeyValuePair<string, string>> settings, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(xml);
        settings = [];
        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            root = XElement.Load(reader);
        }
        catch (XmlException)
        {
            // The exception's message may quote the XML: it is not passed on.
            problem = SettingsShape;
            return false;
        }

        if (root.Name != "settings" || root.Elements().Any(e => e.Name != "setting"))
        {
            problem = SettingsShape;
            return false;
        }
        var parsed = new List<KeyValuePair<string, string>>();
        foreach (var setting in root.Elements())
        {
            var key = (string?)setting.Attribute("name");
            if (string.IsNullOrWhiteSpace(key))
            {
                problem = "has a setting without a name";
                return false;
            }
            if ((string?)setting.Attribute("value") is not { } value)
            {
                problem = "has a setting without a value";
                return false;
            }
            parsed.Add(new(key, value));
        }
        if (parsed.Count == 0)
        {
            problem = "has no setting";
            return false;
        }
        settings = parsed;
        problem = null;
        return true;
    }

    /// <summary>
    /// The endpoint rules on a Create (<paramref name="existing"/> null) or Update of <c>serviceendpoint</c>: the record as it will
    /// be is <paramref name="existing"/> overlaid with <paramref name="target"/>; on an Update that sends no <c>authvalue</c>, the
    /// stored one (<see cref="WebhookSecrets.ReadAsync"/>) is part of it.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 <c>InvalidArgument</c> with <see cref="EndpointProblem(Record, bool)"/>.</exception>
    public static async Task ValidateAsync(Record target, Record? existing, bool allowAnyPort, WebhookSecrets secrets, DataSession s,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(s);
        var record = new Record(ServiceEndpointSchema.Table, target.Id);
        if (existing is not null)
            foreach (var (attribute, value) in existing.Attributes) record[attribute] = value;
        foreach (var (attribute, value) in target.Attributes) record[attribute] = value;
        if (existing is not null && !target.Attributes.ContainsKey(ServiceEndpointSchema.AuthValue)
            && await secrets.ReadAsync(existing.Id != Guid.Empty ? existing.Id : target.Id, s, ct) is { } stored)
            record[ServiceEndpointSchema.AuthValue] = stored;
        if (EndpointProblem(record, allowAnyPort) is { } problem) throw XrmdFaultException.InvalidArgument(problem);
    }

    /// <summary>The reason a url is refused, or null; never the url itself.</summary>
    private static string? UrlProblem(string? url, bool allowAnyPort)
    {
        if (string.IsNullOrWhiteSpace(url)) return "is required";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || url.StartsWith('/')) return "must be an absolute URL";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "must use the http or https scheme";
        if (uri.UserInfo.Length > 0) return "must not contain user information";
        if (uri.Fragment.Length > 0 || url.Contains('#')) return "must not contain a fragment";
        if (!allowAnyPort && uri.Port is not (80 or 443)) return "must use port 80 or 443";
        return null;
    }

    /// <summary>The reason the settings XML of authtype 5 (<paramref name="headers"/>) or 6 is refused, or null.</summary>
    private static string? AuthValueProblem(string xml, bool headers)
    {
        if (!TryParseSettings(xml, out var settings, out var problem)) return problem;
        if (!headers) return null;
        foreach (var (key, value) in settings)
        {
            if (!IsToken(key)) return "has a setting whose name is not a valid HTTP header name";
            if (ReservedHeaders.Contains(key) || key.StartsWith(ReservedHeaderPrefix, StringComparison.OrdinalIgnoreCase))
                return "sets a header that xrmverse sets itself";
            if (!IsHeaderValue(value)) return "has a setting whose value is not a valid HTTP header value";
        }
        return null;
    }

    /// <summary>
    /// A header value the sender can write: visible ASCII, space and tab only (RFC 9110 field values without obs-text; no CR, LF,
    /// NUL or other control character, and no non-ASCII character, which the HTTP stack refuses at send time).
    /// </summary>
    internal static bool IsHeaderValue(string value) => value.All(c => c is '\t' or >= ' ' and <= '~');

    /// <summary>An HTTP token (RFC 9110): letters, digits and <c>!#$%&amp;'*+-.^_`|~</c>.</summary>
    internal static bool IsToken(string name) =>
        name.Length > 0 && name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            || "!#$%&'*+-.^_`|~".Contains(c));

    private static string ServiceBusOnly(string name, string column) =>
        $"Webhook '{name}': {column} applies to Azure Service Bus endpoints, which are not supported.";

    private static string Label(Dictionary<int, string> labels, int value) => labels.GetValueOrDefault(value, "unknown");

    private static int? Choice(Record record, string column) =>
        record.Attributes.TryGetValue(column, out var value) ? value switch
        {
            ChoiceValue o => o.Value,
            int i => i,
            _ => null,
        } : null;
}
