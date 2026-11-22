using System.Security.Cryptography;
using System.Text;
using Xrmd.Schema;

namespace Xrmd.Workflows.Authoring;

/// <summary>
/// The stable ids of authored processes (plan 2.21, A13): a document without <c>id</c> gets a UUID v5 (RFC 9562, SHA-1 name-based)
/// of <c>"{kind}/{entity}/{name}"</c> in <see cref="Namespace"/>, so the same file gives the same id in every organization; the step
/// label ids of the XAML (A9) are UUID v5 of the workflow id and the step's generated name, so a recompile is byte-identical.
/// </summary>
public static class WorkflowIdentity
{
    /// <summary>The xrmverse namespace of the name-based ids of authored processes.</summary>
    public static Guid Namespace { get; } = new("6c1f2f8e-5b52-4c3e-9a63-2d7f0b8e2a11");

    /// <summary>The id of a document without <c>id</c>: UUID v5 of <c>"{kind}/{entity}/{name}"</c>, lowercase.</summary>
    public static Guid IdOf(WorkflowKind kind, string entity, string name)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(name);
        return Uuid5(Namespace, $"{KindName(kind)}/{entity}/{name}".ToLowerInvariant());
    }

    /// <summary>The id of a step label of the workflow: UUID v5 of <c>"{workflowId}/{stepName}"</c>.</summary>
    public static Guid LabelId(Guid workflowId, string stepName)
    {
        ArgumentNullException.ThrowIfNull(stepName);
        return Uuid5(Namespace, $"{workflowId:D}/{stepName}");
    }

    /// <summary>The <c>kind</c> of the YAML (A3).</summary>
    public static string KindName(WorkflowKind kind) => kind switch
    {
        WorkflowKind.Workflow => "workflow",
        WorkflowKind.BusinessRule => "businessRule",
        WorkflowKind.Action => "action",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>RFC 9562 §5.5: SHA-1 of the namespace (network order) and the UTF-8 name, version 5, variant 10.</summary>
    private static Guid Uuid5(Guid ns, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        ns.TryWriteBytes(input, bigEndian: true, out _);
        nameBytes.CopyTo(input, 16);
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
