using Microsoft.Xrm.Sdk;

namespace Xrmd.Schema;

/// <summary>
/// What applying a <see cref="SchemaDocument"/> to an org does (<see cref="SchemaDiff.Compute"/>): the metadata requests
/// to execute in order, the custom components of the org the document does not mention (never deleted) and the changes
/// that cannot be applied (each names its component; a plan with rejections is not applied).
/// </summary>
public sealed class SchemaPlan
{
    /// <summary>The requests, in dependency order: choices, tables (with their primary name), columns, lookups, many-to-many.</summary>
    public IReadOnlyList<OrganizationRequest> Changes { get; init; } = [];

    /// <summary>What the org has and the document does not (<c>table xv_x</c>, <c>column xv_t.xv_c</c>, ...).</summary>
    public IReadOnlyList<string> Extras { get; init; } = [];

    /// <summary>The differences an additive apply cannot make (a type change, a smaller MaxLength, another lookup target, ...).</summary>
    public IReadOnlyList<string> Rejections { get; init; } = [];
}
