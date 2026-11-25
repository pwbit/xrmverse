using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Serialization;
using Xrmd.Pipeline.Serialization;

namespace Xrmd.Pipeline.Async;

/// <summary>
/// The <c>asyncoperation.data</c> payload: the execution context as a <see cref="RemoteExecutionContext"/> written with a
/// <see cref="DataContractSerializer"/> and the SDK's <see cref="KnownTypesResolver"/> (as Dataverse does), restricted to
/// the <see cref="SdkTypeAllowlist"/> on write and on read. A value the own model set carries (see
/// <see cref="SdkRemoteContext.ModelCanCarry(RemoteExecutionContext)"/>) is written and read by the own serializers
/// (<see cref="ContextXml"/>, <see cref="QueryXml"/>, byte-identical to the SDK output); anything else, and a payload whose
/// content the own reader does not know, goes through the SDK serializers below.
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
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public static string Serialize(RemoteExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SdkRemoteContext.ModelCanCarry(context) ? ContextXml.Write(context.ToModel()) : SerializeWithSdk(context);
    }

    [ThreadStatic]
    private static long _sdkFallbacks;

    /// <summary>
    /// How many times a serializer of the SDK (the fallback of this class; the webhook JSON fallback is not counted) has run on the calling thread; lets tests prove a warm-up reaches it.
    /// Per thread, so tests running in parallel cannot disturb each other's reading.
    /// </summary>
    internal static long SdkFallbackCount => _sdkFallbacks;

    /// <summary>
    /// A round trip through the SDK serializers (the fallback path) of a value outside the own model set. It completes the SDK's
    /// lazy known-types initialization, which the own serializers never trigger.
    /// </summary>
    internal static void WarmUpSdk()
    {
        var context = new RemoteExecutionContext { MessageName = "Create" };
        context.SharedVariables["warmup"] = new BooleanManagedProperty(true);
        context.InputParameters["Target"] = new Entity("none") { ["value"] = new OptionSetValue(0) };
        DeserializeWithSdk(SerializeWithSdk(context));
    }

    private static string SerializeWithSdk(RemoteExecutionContext context)
    {
        _sdkFallbacks++;
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
            Serializer.WriteObject(writer, context);
        return text.ToString();
    }

    /// <summary>Reads a payload written by <see cref="Serialize"/>.</summary>
    /// <exception cref="SerializationException">The payload is not a valid context, or names a type outside the
    /// <see cref="SdkTypeAllowlist"/>, or is not well-formed XML (a <see cref="SerializationException"/> with the
    /// <see cref="XmlException"/> as inner exception).</exception>
    public static RemoteExecutionContext Deserialize(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return ContextXml.TryRead(xml, out var model) ? model!.ToSdk() : DeserializeWithSdk(xml);
    }

    private static RemoteExecutionContext DeserializeWithSdk(string xml)
    {
        _sdkFallbacks++;
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
        return SdkRemoteContext.ModelCanCarry(query) ? QueryXml.Write(query.ToModel()) : SerializeQueryWithSdk(query);
    }

    private static string SerializeQueryWithSdk(QueryBase query)
    {
        _sdkFallbacks++;
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
            QuerySerializer.WriteObject(writer, query);
        return text.ToString();
    }

    /// <summary>Reads a query written by <see cref="SerializeQuery"/>.</summary>
    /// <exception cref="SerializationException">The payload is not a query, or names a type outside the <see cref="SdkTypeAllowlist"/>,
    /// or is not well-formed XML (the <see cref="XmlException"/> is the inner exception).</exception>
    public static QueryBase DeserializeQuery(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return QueryXml.TryRead(xml, out var model) ? model!.ToSdk() : DeserializeQueryWithSdk(xml);
    }

    private static QueryBase DeserializeQueryWithSdk(string xml)
    {
        _sdkFallbacks++;
        using var reader = XmlReader.Create(new StringReader(xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return QuerySerializer.ReadObject(reader) as QueryBase
               ?? throw new SerializationException("The asyncoperation data is not a query.");
    }
}
