using System.Text;
using System.Xml;
using Xrmd.Model.Messages;

namespace Xrmd.Model.Serialization;

/// <summary>
/// The XML of a <see cref="RemoteContext"/>: the <c>asyncoperation.data</c> payload, byte for byte as the SDK's data-contract
/// serializer writes a <c>RemoteExecutionContext</c>.
/// </summary>
public static class ContextXml
{
    /// <summary>
    /// The deepest XML element nesting the own reader accepts (its stack guard). A deeper payload makes <see cref="TryRead"/> return
    /// false, and a carrier of the model bounds what it hands to the own writer by the same number.
    /// </summary>
    public const int MaxNesting = ContractXmlReader.MaxDepth;

    /// <summary>The XML of <paramref name="context"/>.</summary>
    /// <exception cref="NotSupportedException">A value is outside the model set (the twins never pass one).</exception>
    /// <exception cref="System.Runtime.Serialization.SerializationException">An enum member holds a value without a name.</exception>
    public static string Write(RemoteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
            new ContractXmlWriter(writer, false).Context(context, true);
        return text.ToString();
    }

    /// <summary>
    /// Reads a payload written by <see cref="Write"/> or by the SDK's serializer. False (and a null result) when the payload holds
    /// something outside the model set: a type hint the model does not know (it is never instantiated), a member the contract
    /// does not list, a null item in a collection, or an element nesting deeper than <see cref="MaxNesting"/>; the caller then uses the SDK reader.
    /// </summary>
    /// <exception cref="System.Runtime.Serialization.SerializationException">The payload is not well-formed XML (the inner exception is
    /// the <see cref="XmlException"/>; the SDK path wraps it the same way), holds a DTD, is not a context, or holds a value that cannot
    /// be converted.</exception>
    public static bool TryRead(string xml, out RemoteContext? context) => ContractXmlReader.TryReadContext(xml, out context);
}
