using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Core;

/// <summary>
/// The parts of the InitializeFrom message (#143) that need no database: its parameters, the lookup maps that the one-to-many
/// relationships from the source table to the target table imply, and the <c>TargetFieldType</c> filter. Behaviour from Learn's
/// InitializeFrom function and "Create a record from another record", and from an observed environment (lookups only for the
/// relationship, null source values left out).
/// </summary>
public static class InitializeFrom
{
    /// <summary>The message name.</summary>
    public const string MessageName = "InitializeFrom";

    /// <summary>The parameters of an InitializeFrom request.</summary>
    /// <param name="Source">The record the values come from (<c>EntityMoniker</c>).</param>
    /// <param name="Target">The logical name of the table to initialize (<c>TargetEntityName</c>).</param>
    /// <param name="FieldType">Which columns of the target are kept (<c>TargetFieldType</c>).</param>
    /// <param name="SkipParentalRelationshipMapping">True leaves out the lookups of the relationships.</param>
    public sealed record Request(EntityReference Source, string Target, TargetFieldType FieldType, bool SkipParentalRelationshipMapping);

    /// <summary>Reads the parameters of <paramref name="request"/>.</summary>
    /// <exception cref="XrmdFaultException">400 for a missing or invalid parameter.</exception>
    public static Request Parameters(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.Parameters.TryGetValue("EntityMoniker", out var moniker) && moniker is EntityReference { Id: var id } reference
                     && id != Guid.Empty && !string.IsNullOrEmpty(reference.LogicalName)
            ? reference
            : throw Missing(request, "EntityMoniker");
        var target = request.Parameters.TryGetValue("TargetEntityName", out var name) && name is string { Length: > 0 } text
            ? text.ToLowerInvariant()
            : throw Missing(request, "TargetEntityName");
        if (!request.Parameters.TryGetValue("TargetFieldType", out var type) || type is null) throw Missing(request, "TargetFieldType");
        var number = type switch
        {
            TargetFieldType member => (int)member,
            int value => value,
            OptionSetValue option => option.Value,
            _ => -1,
        };
        if (number is < 0 or > 3)
            throw XrmdFaultException.InvalidArgument(
                $"Invalid TargetFieldType '{type}': expected All (0), ValidForCreate (1), ValidForUpdate (2) or ValidForRead (3).");
        var skip = request.Parameters.TryGetValue("SkipParentalRelationshipMapping", out var flag) && flag is true;
        return new Request(source, target, (TargetFieldType)number, skip);
    }

    private static XrmdFaultException Missing(OrganizationRequest request, string parameter) =>
        XrmdFaultException.InvalidArgument($"Required parameter '{parameter}' of request '{request.RequestName}' is missing.");

    /// <summary>
    /// The one-to-many relationships from <paramref name="source"/> to <paramref name="target"/> whose lookup the relationship's own map
    /// fills (Dataverse creates that map with the relationship): every one, a Customer column once per target, except the generated
    /// owner and audit lookups (<see cref="SystemAttributes.IsReservedName"/>).
    /// </summary>
    public static IReadOnlyList<OneToManyRelationshipMetadata> ParentalRelationships(IMetadataStore store, string source, string target)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.GetOneToManyRelationships()
            .Where(r => r.ReferencedEntity == source && r.ReferencingEntity == target
                        && !SystemAttributes.IsReservedName(target, r.ReferencingAttribute))
            .ToList();
    }

    /// <summary>True when <paramref name="attribute"/> of the target is kept for <paramref name="type"/>.</summary>
    public static bool Keeps(AttributeMetadata attribute, TargetFieldType type)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return type switch
        {
            TargetFieldType.ValidForCreate => attribute.IsValidForCreate != false,
            TargetFieldType.ValidForUpdate => attribute.IsValidForUpdate != false,
            TargetFieldType.ValidForRead => attribute.IsValidForRead != false,
            _ => true,
        };
    }
}
