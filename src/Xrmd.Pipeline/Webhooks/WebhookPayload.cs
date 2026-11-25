using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Serialization;
using Xrmd.Pipeline.Serialization;

namespace Xrmd.Pipeline.Webhooks;

/// <summary>
/// The body of a webhook request: the <see cref="RemoteExecutionContext"/> written with a <see cref="DataContractJsonSerializer"/>
/// whose known types are the data contracts of the <see cref="SdkTypeAllowlist"/> (so a value carries its
/// <c>"__type": "Entity:http://schemas.microsoft.com/xrm/2011/Contracts"</c> and a date is <c>"/Date(ms)/"</c>, as Dataverse sends
/// them). A body over the limit (256 KB in Dataverse) is written again without the parent context, the input parameters and the
/// images.
/// </summary>
public static class WebhookPayload
{
    /// <summary>The largest body sent whole (Dataverse: 256 KB); a larger one is trimmed.</summary>
    public const int SizeLimit = 262144;

    /// <summary>
    /// The known types of the serializer: the non-generic, concrete data contracts and enums of the SDK assemblies that the
    /// <see cref="SdkTypeAllowlist"/> admits, and the arrays of the primitive types it admits. A value of any other type fails
    /// the serialization (<see cref="SerializationException"/>).
    /// </summary>
    internal static IReadOnlyList<Type> KnownTypes { get; } = BuildKnownTypes();

    private static readonly DataContractJsonSerializer Serializer = new(typeof(RemoteExecutionContext),
        new DataContractJsonSerializerSettings { KnownTypes = KnownTypes });

    /// <summary>The JSON of <paramref name="context"/> (UTF-8, no byte order mark).</summary>
    /// <exception cref="SerializationException">A value is not a known data contract.</exception>
    public static byte[] Serialize(RemoteExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SdkRemoteContext.ModelCanCarry(context) ? ContextJson.Write(context.ToModel()) : SerializeWithSdk(context);
    }

    private static byte[] SerializeWithSdk(RemoteExecutionContext context)
    {
        using var stream = new MemoryStream();
        Serializer.WriteObject(stream, context);
        return stream.ToArray();
    }

    /// <summary>
    /// The body to send: the JSON of <paramref name="context"/>, or, when it exceeds <paramref name="limit"/> bytes, the JSON of a
    /// copy without <c>ParentContext</c>, <c>InputParameters</c>, <c>PreEntityImages</c> and <c>PostEntityImages</c>
    /// (trimmed on the model when the own serializer carries the context, else on the SDK object; <c>Trimmed</c> true, sent with <c>x-ms-dynamics-msg-size-exceeded: true</c>). <paramref name="context"/> is not changed.
    /// </summary>
    /// <exception cref="SerializationException">A value is not a known data contract.</exception>
    public static (byte[] Body, bool Trimmed) Build(RemoteExecutionContext context, int limit = SizeLimit)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!SdkRemoteContext.ModelCanCarry(context))
        {
            var sdkBody = SerializeWithSdk(context);
            return sdkBody.Length <= limit ? (sdkBody, false) : (SerializeWithSdk(Trim(context)), true);
        }
        var model = context.ToModel();
        var body = ContextJson.Write(model);
        return body.Length <= limit ? (body, false) : (ContextJson.Write(ContextJson.Trim(model)), true);
    }

    /// <summary>The JSON of a model context: <see cref="ContextJson"/> when <see cref="ModelSet.CanCarry(RemoteContext)"/>, else the SDK
    /// serializer on <c>ToSdk()</c>.</summary>
    /// <exception cref="SerializationException">A value is not a known data contract.</exception>
    internal static byte[] Serialize(RemoteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ModelSet.CanCarry(context) ? ContextJson.Write(context) : SerializeWithSdk(context.ToSdk());
    }

    /// <summary>
    /// <see cref="Build(RemoteExecutionContext, int)"/> of a model context, with the same size rule: the model path trims with
    /// <see cref="ContextJson.Trim"/>; a context the model set does not carry is built from <c>ToSdk()</c>. <paramref name="context"/>
    /// is not changed.
    /// </summary>
    /// <exception cref="SerializationException">A value is not a known data contract.</exception>
    public static (byte[] Body, bool Trimmed) Build(RemoteContext context, int limit = SizeLimit)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ModelSet.CanCarry(context)) return Build(context.ToSdk(), limit);
        var body = ContextJson.Write(context);
        return body.Length <= limit ? (body, false) : (ContextJson.Write(ContextJson.Trim(context)), true);
    }

    /// <summary>The members of <see cref="RemoteExecutionContext"/> a trimmed body leaves out (or leaves empty).</summary>
    private static readonly HashSet<string> TrimmedMembers =
    [
        nameof(RemoteExecutionContext.ParentContext), nameof(RemoteExecutionContext.InputParameters),
        nameof(RemoteExecutionContext.PreEntityImages), nameof(RemoteExecutionContext.PostEntityImages),
    ];

    /// <summary>The context's settable members, in a copy that <see cref="Trim"/> assigns one by one.</summary>
    private static readonly PropertyInfo[] SettableMembers = typeof(RemoteExecutionContext)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 && !TrimmedMembers.Contains(p.Name))
        .ToArray();

    /// <summary>
    /// A shallow copy of <paramref name="context"/> without <c>ParentContext</c>, <c>InputParameters</c>,
    /// <c>PreEntityImages</c> and <c>PostEntityImages</c>: every other member keeps its value (the collections, which have no
    /// public setter, are copied entry by entry).
    /// </summary>
    private static RemoteExecutionContext Trim(RemoteExecutionContext context)
    {
        var copy = new RemoteExecutionContext();
        foreach (var member in SettableMembers) member.SetValue(copy, member.GetValue(context));
        foreach (var (key, value) in context.OutputParameters) copy.OutputParameters[key] = value;
        foreach (var (key, value) in context.SharedVariables) copy.SharedVariables[key] = value;
        return copy;
    }

    private static List<Type> BuildKnownTypes()
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
        // The arrays of primitives a parameter may hold (byte[] is written as base64 by default, string[] and Guid[] as arrays).
        types.AddRange(new[]
        {
            typeof(string[]), typeof(bool[]), typeof(byte[]), typeof(short[]), typeof(int[]), typeof(long[]), typeof(float[]),
            typeof(double[]), typeof(decimal[]), typeof(Guid[]), typeof(DateTime[]),
        });
        return types;
    }
}
