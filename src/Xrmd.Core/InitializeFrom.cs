using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;

namespace Xrmd.Core;

/// <summary>
/// The parts of the InitializeFrom message (#143) that need no database: its parameters, the lookup maps that the one-to-many
/// relationships from the source table to the target table imply, and the <c>TargetFieldType</c> filter. Behaviour from Learn's
/// InitializeFrom function and "Create a record from another record", and from an observed environment (lookups only for the
/// relationship, null source values left out). S.2e3c1 (Task 6): on the model request and the model metadata.
/// </summary>
public static class InitializeFrom
{
    /// <summary>The message name.</summary>
    public const string MessageName = "InitializeFrom";

    /// <summary>Which columns of the target InitializeFrom keeps (<c>TargetFieldType</c>; Learn's members and values).</summary>
    public enum TargetFieldType
    {
        /// <summary>Every column.</summary>
        All = 0,

        /// <summary>The columns valid for create.</summary>
        ValidForCreate = 1,

        /// <summary>The columns valid for update.</summary>
        ValidForUpdate = 2,

        /// <summary>The columns valid for read.</summary>
        ValidForRead = 3,
    }

    /// <summary>The parameters of an InitializeFrom request.</summary>
    /// <param name="Source">The record the values come from (<c>EntityMoniker</c>).</param>
    /// <param name="Target">The logical name of the table to initialize (<c>TargetEntityName</c>).</param>
    /// <param name="FieldType">Which columns of the target are kept (<c>TargetFieldType</c>).</param>
    /// <param name="SkipParentalRelationshipMapping">True leaves out the lookups of the relationships.</param>
    public sealed record Request(RecordReference Source, string Target, TargetFieldType FieldType, bool SkipParentalRelationshipMapping);

    /// <summary>
    /// Reads the parameters of the model <paramref name="request"/>. <c>TargetFieldType</c> is the SDK enum a caller passes (it crosses
    /// the mirror as it is: a value of an enum named <c>TargetFieldType</c>, this one or the SDK's), a number, or a choice value.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing or invalid parameter.</exception>
    public static Request Parameters(Model.Messages.Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.Parameters.TryGetValue("EntityMoniker", out var moniker) && moniker is RecordReference { Id: var id } reference
                     && id != Guid.Empty && !string.IsNullOrEmpty(reference.LogicalName)
            ? reference
            : throw Missing(request, "EntityMoniker");
        var target = request.Parameters.TryGetValue("TargetEntityName", out var name) && name is string { Length: > 0 } text
            ? text.ToLowerInvariant()
            : throw Missing(request, "TargetEntityName");
        if (!request.Parameters.TryGetValue("TargetFieldType", out var type) || type is null) throw Missing(request, "TargetFieldType");
        var number = type switch
        {
            Enum member when member.GetType().Name == nameof(TargetFieldType) => Convert.ToInt32(member, System.Globalization.CultureInfo.InvariantCulture),
            int value => value,
            ChoiceValue option => option.Value,
            _ => -1,
        };
        if (number is < 0 or > 3)
            throw XrmdFaultException.InvalidArgument(
                $"Invalid TargetFieldType '{SdkText(type)}': expected All (0), ValidForCreate (1), ValidForUpdate (2) or ValidForRead (3).");
        var skip = request.Parameters.TryGetValue("SkipParentalRelationshipMapping", out var flag) && flag is true;
        return new Request(source, target, (TargetFieldType)number, skip);
    }

    /// <summary>
    /// How today's text names a value: the SDK object a caller passed, whose <c>ToString</c> is its type name for every SDK class (wire
    /// texts, kept as they are). A model value that stands for such a class prints the SDK name; any other value prints as it did.
    /// </summary>
    private static string? SdkText(object value) => value switch
    {
        ChoiceValue => "Microsoft.Xrm.Sdk.OptionSetValue",
        ChoiceValues => "Microsoft.Xrm.Sdk.OptionSetValueCollection",
        MoneyValue => "Microsoft.Xrm.Sdk.Money",
        RecordReference => "Microsoft.Xrm.Sdk.EntityReference",
        RecordReferenceCollection => "Microsoft.Xrm.Sdk.EntityReferenceCollection",
        Record => "Microsoft.Xrm.Sdk.Entity",
        RecordCollection => "Microsoft.Xrm.Sdk.EntityCollection",
        AliasedColumnValue => "Microsoft.Xrm.Sdk.AliasedValue",
        _ => value.ToString(),
    };

    private static XrmdFaultException Missing(Model.Messages.Request request, string parameter) =>
        XrmdFaultException.InvalidArgument($"Required parameter '{parameter}' of request '{request.Name}' is missing.");

    /// <summary>
    /// The one-to-many relationships from <paramref name="source"/> to <paramref name="target"/> whose lookup the relationship's own map
    /// fills (Dataverse creates that map with the relationship): every one, a Customer column once per target, except the generated
    /// owner and audit lookups (<see cref="SystemAttributes.IsReservedName"/>).
    /// </summary>
    public static IReadOnlyList<OneToManyRelationship> ParentalRelationships(IMetadataStore store, string source, string target)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.GetOneToManyDefinitions()
            .Where(r => r.ReferencedEntity == source && r.ReferencingEntity == target
                        && !SystemAttributes.IsReservedName(target, r.ReferencingAttribute))
            .ToList();
    }

    /// <summary>True when <paramref name="column"/> of the target is kept for <paramref name="type"/>.</summary>
    public static bool Keeps(ColumnDefinition column, TargetFieldType type)
    {
        ArgumentNullException.ThrowIfNull(column);
        return type switch
        {
            TargetFieldType.ValidForCreate => column.IsValidForCreate != false,
            TargetFieldType.ValidForUpdate => column.IsValidForUpdate != false,
            TargetFieldType.ValidForRead => column.IsValidForRead != false,
            _ => true,
        };
    }
}
