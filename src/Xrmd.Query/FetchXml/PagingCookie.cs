using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Xrmd.Query.FetchXml;

/// <summary>Keyset paging cookie: &lt;cookie page="N"&gt;&lt;{pk} last="{GUID}" first="{GUID}" /&gt;&lt;/cookie&gt;.</summary>
public sealed record PagingCookie(int Page, string PrimaryKey, Guid First, Guid Last)
{
    /// <summary>Null when absent or malformed.</summary>
    public static PagingCookie? TryParse(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var sr = new StringReader(cookie);
            using var xr = XmlReader.Create(sr, settings);
            var root = XDocument.Load(xr).Root;
            if (root is null || root.Name.LocalName != "cookie") return null;
            if (!int.TryParse((string?)root.Attribute("page"), NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page < 1)
                return null;
            var pk = root.Elements().FirstOrDefault();
            if (pk is null) return null;
            if (!Guid.TryParse((string?)pk.Attribute("first"), out var first)) return null;
            if (!Guid.TryParse((string?)pk.Attribute("last"), out var last)) return null;
            return new PagingCookie(page, pk.Name.LocalName, first, last);
        }
        catch (XmlException) { return null; }
    }

    public string Format() => string.Create(CultureInfo.InvariantCulture,
        $"<cookie page=\"{Page}\"><{PrimaryKey} last=\"{Last.ToString("B").ToUpperInvariant()}\" first=\"{First.ToString("B").ToUpperInvariant()}\" /></cookie>");
}
