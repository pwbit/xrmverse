using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Pipeline.Serialization;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>
/// The SDK reference handle of the differential tests (S.2e2b): a copy of the <see cref="DataContractSerializer"/> setup of
/// <c>AsyncContextSerializer</c> as it was before the own writer. It stays fixed when the production serializer switches to the
/// own code, so the own output is always compared with what the SDK writes. The wire format is learned from this output only.
/// </summary>
internal static class SdkReferenceSerializers
{
    private static readonly DataContractSerializer ContextSerializer = new(typeof(RemoteExecutionContext),
        new DataContractSerializerSettings { DataContractResolver = new SdkTypeAllowlistResolver() });

    private static readonly DataContractSerializer QuerySerializer = new(typeof(QueryBase),
        new DataContractSerializerSettings { DataContractResolver = new SdkTypeAllowlistResolver() });

    // The webhook body: the DataContractJsonSerializer of today's WebhookPayload (known types = the allowlisted data contracts of
    // the SDK assemblies plus the arrays of primitives), copied here so the reference stays fixed when the production code switches.
    private static readonly DataContractJsonSerializer JsonSerializer = new(typeof(RemoteExecutionContext),
        new DataContractJsonSerializerSettings { KnownTypes = JsonKnownTypes() });

    /// <summary>The JSON bytes the SDK path writes for the webhook (UTF-8, no byte order mark).</summary>
    public static byte[] SerializeContextJson(RemoteExecutionContext context)
    {
        using var stream = new MemoryStream();
        JsonSerializer.WriteObject(stream, context);
        return stream.ToArray();
    }

    private static List<Type> JsonKnownTypes()
    {
        var types = new List<Type>();
        foreach (var assembly in new[] { typeof(Entity).Assembly, typeof(Microsoft.Crm.Sdk.Messages.WhoAmIRequest).Assembly })
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters || !SdkTypeAllowlist.IsAllowed(type)) continue;
                if (type.IsDefined(typeof(DataContractAttribute), false) || type.IsDefined(typeof(CollectionDataContractAttribute), false))
                    types.Add(type);
            }
        }
        types.AddRange(new[]
        {
            typeof(string[]), typeof(bool[]), typeof(byte[]), typeof(short[]), typeof(int[]), typeof(long[]), typeof(float[]),
            typeof(double[]), typeof(decimal[]), typeof(Guid[]), typeof(DateTime[]),
        });
        return types;
    }

    public static string SerializeContext(RemoteExecutionContext context) => Write(ContextSerializer, context);

    public static string SerializeQuery(QueryBase query) => Write(QuerySerializer, query);

    public static RemoteExecutionContext DeserializeContext(string xml) => (RemoteExecutionContext)Read(ContextSerializer, xml);

    public static QueryBase DeserializeQuery(string xml) => (QueryBase)Read(QuerySerializer, xml);

    private static object Read(DataContractSerializer serializer, string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return serializer.ReadObject(reader)!;
    }

    private static string Write(DataContractSerializer serializer, object value)
    {
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
            serializer.WriteObject(writer, value);
        return text.ToString();
    }
}
