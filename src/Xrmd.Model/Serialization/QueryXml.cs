using System.Text;
using System.Xml;
using Xrmd.Model.Query;

namespace Xrmd.Model.Serialization;

/// <summary>
/// The XML of a <see cref="QueryDefinition"/>: byte for byte as the SDK's data-contract serializer writes a
/// <c>QueryBase</c> (<c>QueryExpression</c>, <c>QueryByAttribute</c> or <c>FetchExpression</c>).
/// </summary>
public static class QueryXml
{
    /// <summary>The XML of <paramref name="query"/>.</summary>
    /// <exception cref="NotSupportedException">The query or a value of it is outside the model set (the twins never pass one).</exception>
    /// <exception cref="System.Runtime.Serialization.SerializationException">An enum member holds a value without a name.</exception>
    public static string Write(QueryDefinition query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
            new ContractXmlWriter(writer, true).Query(query);
        return text.ToString();
    }

    /// <summary>
    /// Reads a query written by <see cref="Write"/> or by the SDK's serializer. False (and a null result) when the payload holds
    /// something outside the model set (see <see cref="ContextXml.TryRead"/>); the caller then uses the SDK reader.
    /// </summary>
    /// <exception cref="System.Runtime.Serialization.SerializationException">The payload is not well-formed XML, holds a DTD, is not
    /// a query, or holds a value that cannot be converted.</exception>
    public static bool TryRead(string xml, out QueryDefinition? query) => ContractXmlReader.TryReadQuery(xml, out query);
}
