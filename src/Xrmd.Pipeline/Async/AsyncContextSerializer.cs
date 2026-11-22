using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Pipeline.Serialization;

namespace Xrmd.Pipeline.Async;

/// <summary>
/// The <c>asyncoperation.data</c> payload: the execution context as a <see cref="RemoteExecutionContext"/> written with a
/// <see cref="DataContractSerializer"/> and the SDK's <see cref="KnownTypesResolver"/> (as Dataverse does), restricted to
/// the <see cref="SdkTypeAllowlist"/> on write and on read.
/// </summary>
public static class AsyncContextSerializer
{
    private static readonly DataContractSerializer Serializer = new(typeof(RemoteExecutionContext),
        new DataContractSerializerSettings { DataContractResolver = new SdkTypeAllowlistResolver() });

    private static readonly DataContractSerializer QuerySerializer = new(typeof(QueryBase),
        new DataContractSerializerSettings { DataContractResolver = new SdkTypeAllowlistResolver() });

    /// <summary>
    /// A deep copy of <paramref name="context"/> (and its parent chain) as a <see cref="RemoteExecutionContext"/>; the
    /// parameter collections and images are copied entry by entry, so later changes to the source do not show.
    /// </summary>
    public static RemoteExecutionContext ToRemote(IPluginExecutionContext context)
    {
        var remote = new RemoteExecutionContext
        {
            Stage = context.Stage,
            Mode = context.Mode,
            MessageName = context.MessageName,
            PrimaryEntityName = context.PrimaryEntityName,
            PrimaryEntityId = context.PrimaryEntityId,
            SecondaryEntityName = context.SecondaryEntityName,
            RequestId = context.RequestId,
            UserId = context.UserId,
            InitiatingUserId = context.InitiatingUserId,
            BusinessUnitId = context.BusinessUnitId,
            OrganizationId = context.OrganizationId,
            OrganizationName = context.OrganizationName,
            CorrelationId = context.CorrelationId,
            Depth = context.Depth,
            IsExecutingOffline = context.IsExecutingOffline,
            IsOfflinePlayback = context.IsOfflinePlayback,
            IsolationMode = context.IsolationMode,
            IsInTransaction = context.IsInTransaction,
            OperationId = context.OperationId,
            OperationCreatedOn = context.OperationCreatedOn,
            OwningExtension = context.OwningExtension,
            ParentContext = context.ParentContext is null ? null : ToRemote(context.ParentContext),
        };
        if (context is IPluginExecutionContext2 c2)
        {
            remote.UserAzureActiveDirectoryObjectId = c2.UserAzureActiveDirectoryObjectId;
            remote.InitiatingUserAzureActiveDirectoryObjectId = c2.InitiatingUserAzureActiveDirectoryObjectId;
        }
        if (context is XrmdPluginExecutionContext x) remote.InitiatingUserAgent = x.InitiatingUserAgent;

        foreach (var (key, value) in context.InputParameters) remote.InputParameters[key] = value;
        foreach (var (key, value) in context.OutputParameters) remote.OutputParameters[key] = value;
        foreach (var (key, value) in context.SharedVariables) remote.SharedVariables[key] = value;
        foreach (var (key, value) in context.PreEntityImages) remote.PreEntityImages[key] = value;
        foreach (var (key, value) in context.PostEntityImages) remote.PostEntityImages[key] = value;
        return remote;
    }

    /// <summary>The XML of <paramref name="context"/>.</summary>
    /// <exception cref="SerializationException">A value (e.g. a shared variable) is not a known data contract, or its type is
    /// outside the <see cref="SdkTypeAllowlist"/>.</exception>
    public static string Serialize(RemoteExecutionContext context)
    {
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
            Serializer.WriteObject(writer, context);
        return text.ToString();
    }

    /// <summary>Reads a payload written by <see cref="Serialize"/>.</summary>
    /// <exception cref="SerializationException">The payload is not a valid context, or names a type outside the
    /// <see cref="SdkTypeAllowlist"/>.</exception>
    /// <exception cref="XmlException">The payload is not well-formed XML.</exception>
    public static RemoteExecutionContext Deserialize(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return Serializer.ReadObject(reader) as RemoteExecutionContext
               ?? throw new SerializationException("The asyncoperation data is not an execution context.");
    }

    /// <summary>
    /// The XML of the query of a system job (the <c>asyncoperation.data</c> of a bulk duplicate detection, plan 2.15 ruling D15):
    /// a <see cref="QueryExpression"/>, <see cref="QueryByAttribute"/> or <see cref="FetchExpression"/>, written like a context.
    /// </summary>
    /// <exception cref="SerializationException">A value of the query is outside the <see cref="SdkTypeAllowlist"/>.</exception>
    public static string SerializeQuery(QueryBase query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
            QuerySerializer.WriteObject(writer, query);
        return text.ToString();
    }

    /// <summary>Reads a query written by <see cref="SerializeQuery"/>.</summary>
    /// <exception cref="SerializationException">The payload is not a query, or names a type outside the <see cref="SdkTypeAllowlist"/>.</exception>
    /// <exception cref="XmlException">The payload is not well-formed XML.</exception>
    public static QueryBase DeserializeQuery(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var reader = XmlReader.Create(new StringReader(xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return QuerySerializer.ReadObject(reader) as QueryBase
               ?? throw new SerializationException("The asyncoperation data is not a query.");
    }
}
