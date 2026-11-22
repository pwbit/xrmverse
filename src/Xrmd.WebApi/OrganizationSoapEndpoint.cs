using System.Text.Json;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Pipeline.Serialization;

namespace Xrmd.WebApi;

/// <summary>
/// Minimal SOAP surface of the organization service (<c>POST /XRMServices/2011/Organization.svc/web</c>), as the
/// Dataverse <c>ServiceClient</c> calls it: SOAP 1.1 (BasicHttpBinding, bearer token), operation <c>Execute</c> only.
/// The caller must map to a <c>systemuser</c> (checked before the body is read); the <c>CallerId</c> header (or the HTTP
/// headers of <see cref="CurrentUser"/>) impersonates another user, resolved before the body is deserialized. The request and response are serialized
/// with the SDK's own data contracts (the SDK's resolver restricted to the <see cref="SdkTypeAllowlist"/>, bounded reader quotas); the messages supported are
/// those of <see cref="OrganizationService.ExecuteAsync"/>. Errors are SOAP faults whose detail is an
/// <see cref="OrganizationServiceFault"/> (the client raises <c>FaultException&lt;OrganizationServiceFault&gt;</c>).
/// </summary>
public static class OrganizationSoapEndpoint
{
    public const string Route = "/XRMServices/2011/Organization.svc/web";
    private const string SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string ServicesNs = "http://schemas.microsoft.com/xrm/2011/Contracts/Services";
    private const string ContractsNs = "http://schemas.microsoft.com/xrm/2011/Contracts";
    private const string GenericMessage = "An unexpected error occurred.";
    private const string MalformedMessage = "The SOAP request is malformed.";
    private const string InvalidArgumentMessage = "The request contains an invalid argument.";

    /// <summary>
    /// WCF defaults, except strings (record values) up to 8 MB and arrays up to the body limit
    /// (<see cref="SolutionFileSizeLimit"/>): the <c>CustomizationFile</c> of an import is a <c>byte[]</c>.
    /// </summary>
    private static readonly XmlDictionaryReaderQuotas Quotas = new()
    {
        MaxDepth = 32,
        MaxStringContentLength = 8 * 1024 * 1024,
        MaxArrayLength = (int)SolutionFileSizeLimit.MaxBytes,
        MaxBytesPerRead = 4096,
        MaxNameTableCharCount = 16384,
    };

    private const int MaxItemsInObjectGraph = 65536;

    private static readonly DataContractSerializer RequestSerializer = Serializer(typeof(OrganizationRequest), "request");
    private static readonly DataContractSerializer ResponseSerializer = Serializer(typeof(OrganizationResponse), "ExecuteResult");
    private static readonly DataContractSerializer FaultSerializer = new(typeof(OrganizationServiceFault));

    public static IEndpointRouteBuilder MapOrganizationSoapEndpoint(this IEndpointRouteBuilder app)
    {
        SolutionFileSizeLimit.ApplyTo(app.MapPost(Route, HandleAsync).RequireAuthorization());
        return app;
    }

    private static async Task HandleAsync(HttpContext context, OrganizationService service, CallerResolver caller, ILoggerFactory loggers)
    {
        var ct = context.RequestAborted;
        int status;
        MemoryStream envelope;
        try
        {
            // Non-members never reach the deserializer; nor does a caller the CallerId header cannot impersonate.
            await caller.ResolveAsync(ct);

            // XmlReader reads synchronously: buffer the body first (Kestrel disallows synchronous I/O).
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, ct);
            body.Position = 0;
            using var reader = XmlDictionaryReader.CreateTextReader(body, Quotas);
            if (ReadCallerId(reader) is { } callerId) await caller.ImpersonateAsync(callerId, null, ct);
            var response = await service.ExecuteAsync(ReadExecuteRequest(reader), ct);
            // Serialized inside the try so that a response the serializer cannot write becomes a SOAP fault too
            // (a server error: the serializer exceptions below would otherwise read as a malformed request).
            try
            {
                envelope = Envelope(w =>
                {
                    w.WriteStartElement("ExecuteResponse", ServicesNs);
                    ResponseSerializer.WriteObject(w, response);
                    w.WriteEndElement();
                });
            }
            catch (Exception ex) when (ex is XmlException or SerializationException or InvalidDataContractException)
            {
                throw new InvalidOperationException($"Serializing the {response.ResponseName} response failed.", ex);
            }
            status = StatusCodes.Status200OK;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var logger = loggers.CreateLogger(typeof(OrganizationSoapEndpoint).FullName!);
            var sql = SqlFaults.Translate(ex);
            var (code, message) = ex switch
            {
                _ when sql is not null => (sql.ErrorCode, sql.Message),
                XrmdFaultException fault => (fault.ErrorCode, fault.Message),
                XrmdValidationException invalid => (ErrorCodes.InvalidArgument, invalid.Message),
                NotSupportedException => (ErrorCodes.InvalidArgument, ex.Message), // our own messages
                BadHttpRequestException rejected => (ErrorCodes.InvalidArgument, rejected.Message), // e.g. a body over the limit
                XmlException or SerializationException or InvalidDataContractException => (ErrorCodes.InvalidArgument, MalformedMessage),
                ArgumentException => (ErrorCodes.InvalidArgument, InvalidArgumentMessage),
                _ => (ErrorCodes.Unexpected, GenericMessage),
            };
            if (sql is not null) logger.LogWarning(ex, "SQL failure {Code} for SOAP Execute", ODataErrorMiddleware.FormatCode(code));
            else if (code == ErrorCodes.Unexpected) logger.LogError(ex, "Unhandled exception for SOAP Execute");
            else if (ex is not XrmdFaultException and not NotSupportedException) logger.LogInformation(ex, "Rejected SOAP Execute request");
            envelope = Envelope(w => WriteFault(w, code, message, ex as XrmdFaultException));
            // A request Kestrel rejects keeps its status (a body over the limit is 413 Payload Too Large, as in Dataverse);
            // every other fault is a SOAP 500.
            status = ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
        }

        using (envelope)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/xml; charset=utf-8";
            await envelope.CopyToAsync(context.Response.Body, ct);
        }
    }

    /// <summary>
    /// Reads <c>Envelope</c> and its <c>Header</c>, if any, leaving <paramref name="reader"/> before the <c>Body</c>; returns
    /// the value of the <c>CallerId</c> header (impersonation), null without one. Other headers (SdkClientVersion, UserType,
    /// ...) are not used.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument when <c>CallerId</c> is not a GUID.</exception>
    private static Guid? ReadCallerId(XmlDictionaryReader reader)
    {
        reader.MoveToContent();
        reader.ReadStartElement("Envelope", SoapNs);
        reader.MoveToContent();
        if (!reader.IsStartElement("Header", SoapNs)) return null;
        var header = (XElement)XNode.ReadFrom(reader);
        if (header.Element(XName.Get("CallerId", ContractsNs)) is not { } callerId) return null;
        return Guid.TryParse(callerId.Value.Trim(), out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument("The CallerId header is not a valid GUID.");
    }

    /// <summary>
    /// Reads <c>Body/Execute/request</c> after <see cref="ReadCallerId"/>, then the rest of the envelope, before anything
    /// runs: nothing may follow the <c>Body</c> (a <c>Header</c> there, e.g. a <c>CallerId</c>, would otherwise be ignored).
    /// Any other operation is not supported.
    /// </summary>
    /// <exception cref="XmlException">An element after the <c>Body</c> (a malformed request).</exception>
    private static OrganizationRequest ReadExecuteRequest(XmlDictionaryReader reader)
    {
        reader.MoveToContent();
        reader.ReadStartElement("Body", SoapNs);
        reader.MoveToContent();
        if (!reader.IsStartElement("Execute", ServicesNs))
            throw new NotSupportedException($"SOAP operation '{reader.LocalName}' is not supported; use Execute.");
        reader.ReadStartElement();
        reader.MoveToContent();
        var request = RequestSerializer.ReadObject(reader) as OrganizationRequest
                      ?? throw new SerializationException("Execute has no request.");
        ReadToEndOf(reader); // Execute
        ReadToEndOf(reader); // Body
        if (reader.MoveToContent() == XmlNodeType.Element)
            throw new XmlException($"Unexpected element '{reader.LocalName}' after the SOAP Body.");
        reader.ReadEndElement(); // Envelope
        return request;
    }

    /// <summary>Skips the remaining children of the current element and reads its end tag.</summary>
    private static void ReadToEndOf(XmlDictionaryReader reader)
    {
        while (reader.MoveToContent() == XmlNodeType.Element) reader.Skip();
        reader.ReadEndElement();
    }

    private static void WriteFault(XmlWriter w, int code, string message, XrmdFaultException? source = null)
    {
        w.WriteStartElement("s", "Fault", SoapNs);
        w.WriteElementString("faultcode", "", code == ErrorCodes.Unexpected ? "s:Server" : "s:Client");
        w.WriteStartElement("faultstring", "");
        w.WriteAttributeString("xml", "lang", null, "en-US");
        w.WriteString(message);
        w.WriteEndElement();
        w.WriteStartElement("detail", "");
        var fault = new OrganizationServiceFault { ErrorCode = code, Message = message, Timestamp = DateTime.UtcNow };
        // The details of the fault (the failed targets of a bulk message): strings, as the client reads them back.
        if (source is not null)
            foreach (var (name, value) in source.ErrorDetails)
                if (value is not null) fault.ErrorDetails[name] = value as string ?? JsonSerializer.Serialize(value);
        FaultSerializer.WriteObject(w, fault);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// <summary>The whole <c>s:Envelope/s:Body</c> written to a buffer, positioned at its start.</summary>
    private static MemoryStream Envelope(Action<XmlWriter> writeBody)
    {
        var buffer = new MemoryStream();
        using (var w = XmlDictionaryWriter.CreateTextWriter(buffer, new UTF8Encoding(false), ownsStream: false))
        {
            w.WriteStartElement("s", "Envelope", SoapNs);
            w.WriteStartElement("s", "Body", SoapNs);
            writeBody(w);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        buffer.Position = 0;
        return buffer;
    }

    private static DataContractSerializer Serializer(Type type, string rootName) =>
        new(type, new DataContractSerializerSettings
        {
            RootName = new XmlDictionary().Add(rootName),
            RootNamespace = new XmlDictionary().Add(ServicesNs),
            DataContractResolver = new SdkTypeAllowlistResolver(),
            MaxItemsInObjectGraph = MaxItemsInObjectGraph,
        });
}
