using System.Text;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Query.FetchXml;

namespace Xrmd.Query.Cosmos;

/// <summary>
/// The <c>PagingCookie</c> of <c>ExecuteCosmosSqlQuery</c> (plan #78, ruling J7): the executor's FetchXML-style keyset cookie as
/// URL-safe base64 without padding. It is opaque to callers and not bound to the query text.
/// </summary>
public static partial class CosmosPagingCookie
{
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Format();

    private const string Invalid = "ExecuteCosmosSqlQuery: the PagingCookie is not valid.";

    public static string Encode(string fetchCookie)
    {
        ArgumentNullException.ThrowIfNull(fetchCookie);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(fetchCookie)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <exception cref="XrmdFaultException">400: not base64, not a cookie of the executor, or a page below 1.</exception>
    public static PagingCookie Decode(string cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie)) throw XrmdFaultException.InvalidArgument(Invalid);
        if (!Format().IsMatch(cookie)) throw XrmdFaultException.InvalidArgument(Invalid);
        var b64 = cookie.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(b64));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw XrmdFaultException.InvalidArgument(Invalid);
        }
        var parsed = PagingCookie.TryParse(text);
        // Page + 1 becomes the page number, so int.MaxValue would overflow.
        if (parsed is null or { Page: < 1 } or { Page: int.MaxValue }) throw XrmdFaultException.InvalidArgument(Invalid);
        return parsed;
    }
}
