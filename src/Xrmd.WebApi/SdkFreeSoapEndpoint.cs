using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xrmd.Abstractions.Sdk;
using Xrmd.Core.Errors;

namespace Xrmd.WebApi;

/// <summary>
/// The SOAP organization service route of an SDK-free server (plan S.2a): the same route and authorization as
/// <see cref="OrganizationSoapEndpoint"/>, answered with HTTP 501 and a SOAP 1.1 fault whose detail is an
/// <c>OrganizationServiceFault</c> (<see cref="ErrorCodes.FeatureNotAvailable"/>, <see cref="SdkFreeMessages.Soap"/>), so that a
/// ServiceClient raises its usual fault. The request body is not read. The envelope is written by hand with an
/// <see cref="XmlWriter"/>: this type names no SDK type, so mapping it loads no SDK assembly. The element names and namespaces
/// are wire identifiers of the organization service.
/// </summary>
public static class SdkFreeSoapEndpoint
{
    private const string SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string ContractsNs = "http://schemas.microsoft.com/xrm/2011/Contracts";

    /// <summary>Maps <see cref="OrganizationSoapEndpoint.Route"/> to the refusal.</summary>
    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost(OrganizationSoapEndpoint.Route, HandleAsync).RequireAuthorization();
        return app;
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var envelope = Envelope(ErrorCodes.FeatureNotAvailable, SdkFreeMessages.Soap);
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        context.Response.ContentType = "text/xml; charset=utf-8";
        context.Response.ContentLength = envelope.Length;
        await context.Response.Body.WriteAsync(envelope, context.RequestAborted);
    }

    /// <summary>
    /// The SOAP 1.1 envelope of a client fault carrying <paramref name="code"/> and <paramref name="message"/>; the members of the
    /// fault are a subsequence of the data contract's order (ActivityId, ErrorCode, ErrorDetails, HelpLink, Message, Timestamp, …,
    /// InnerFault, …, TraceText), so the SDK's serializer reads it.
    /// </summary>
    internal static byte[] Envelope(int code, string message)
    {
        using var buffer = new MemoryStream();
        using (var w = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true }))
        {
            w.WriteStartElement("s", "Envelope", SoapNs);
            w.WriteStartElement("s", "Body", SoapNs);
            w.WriteStartElement("s", "Fault", SoapNs);
            w.WriteElementString("faultcode", "", "s:Client");
            w.WriteStartElement("faultstring", "");
            w.WriteAttributeString("xml", "lang", null, "en-US");
            w.WriteString(message);
            w.WriteEndElement();
            w.WriteStartElement("detail", "");
            w.WriteStartElement("OrganizationServiceFault", ContractsNs);
            w.WriteAttributeString("xmlns", "i", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteElementString("ErrorCode", ContractsNs, code.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteStartElement("ErrorDetails", ContractsNs);
            w.WriteEndElement();
            w.WriteElementString("HelpLink", ContractsNs, SdkFreeMessages.DocsLink);
            w.WriteElementString("Message", ContractsNs, message);
            w.WriteElementString("Timestamp", ContractsNs, XmlConvert.ToString(DateTime.UtcNow, XmlDateTimeSerializationMode.Utc));
            w.WriteStartElement("InnerFault", ContractsNs);
            w.WriteAttributeString("i", "nil", "http://www.w3.org/2001/XMLSchema-instance", "true");
            w.WriteEndElement();
            w.WriteStartElement("TraceText", ContractsNs);
            w.WriteAttributeString("i", "nil", "http://www.w3.org/2001/XMLSchema-instance", "true");
            w.WriteEndElement();
            w.WriteEndElement(); // OrganizationServiceFault
            w.WriteEndElement(); // detail
            w.WriteEndElement(); // Fault
            w.WriteEndElement(); // Body
            w.WriteEndElement(); // Envelope
        }

        return buffer.ToArray();
    }
}
