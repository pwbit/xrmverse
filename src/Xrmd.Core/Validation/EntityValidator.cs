using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;

namespace Xrmd.Core.Validation;

/// <summary>
/// Checks the values of a <see cref="Record"/> against its table's metadata before it is written. On the model since S.2e3b: a fault
/// that names a value type keeps the SDK name it showed before (<c>OptionSetValue</c>, <c>EntityReference</c>, …, see
/// <see cref="TypeName"/>). The SDK twins are in <c>Sdk/EntityValidator.Sdk.cs</c>.
/// </summary>
public static partial class EntityValidator
{
    /// <summary>
    /// Every attribute must exist on the entity; writable ones (see <see cref="IsWritable"/>) must carry a value of the
    /// value type of the column, strings within <c>MaxLength</c>, option values defined in the option set and lookups
    /// pointing to one of the attribute's targets. Attributes not writable for the operation are ignored, as Dataverse does; a
    /// file column with a value is refused (ruling F4). Party lists and EntityName columns follow plan 2.11 (A7, A8, A9).
    /// </summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) naming the offending attribute.</exception>
    public static void Validate(Record e, TableDefinition m, IReadOnlyDictionary<string, ColumnDefinition> attrs, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(attrs);
        foreach (var (name, value) in e.Attributes)
        {
            if (!attrs.TryGetValue(name, out var a))
                throw XrmdFaultException.InvalidArgument($"Attribute '{name}' not found on entity '{m.LogicalName}'.");
            // Ruling F4: a file column (IsValidForCreate/IsValidForUpdate false, as in Dataverse) gets its value from the upload
            // messages only; a value is refused rather than ignored. Null on Update deletes the file (see the Core's Update).
            // Plan 2.11 A16: the note body (base64 text, checked as any String here) has no SQL column: the Core stores it in the
            // file store (AnnotationBodies).
            // Plan 2.11 A8/A9: a party list is written as activityparty rows by the Core (ActivityParties).
            if (ActivitySchema.IsPartyList(a))
            {
                ValidatePartyList(m, a, value);
                continue;
            }
            // Plan 2.11 A7: an EntityName column is server-maintained; a value sent must name what the server stores.
            if (a is EntityNameColumn entityName)
            {
                ValidateEntityName(e, m, entityName, value, isCreate);
                continue;
            }
            if (a is FileColumn && value is not null)
                throw XrmdFaultException.InvalidArgument($"File column {a.LogicalName} can only be set with the file upload messages.");
            if (!IsWritable(a, isCreate) || value is null) continue;
            ValidateValue(m, a, value);
        }
    }

    /// <summary>
    /// False for attributes the caller cannot set in this operation (<c>IsValidForCreate/IsValidForUpdate == false</c>)
    /// and for attributes stored by another one (money <c>_base</c>): the server computes or ignores them.
    /// </summary>
    public static bool IsWritable(ColumnDefinition a, bool isCreate) =>
        !TypeMapping.IsStoredByOtherAttribute(a) && (isCreate ? a.IsValidForCreate : a.IsValidForUpdate) != false;

    private static void ValidateValue(TableDefinition m, ColumnDefinition a, object value)
    {
        switch (a)
        {
            case TextColumn s:
                CheckLength(m, a, Expect<string>(m, a, value), s.MaxLength);
                break;
            case MemoColumn memo:
                CheckLength(m, a, Expect<string>(m, a, value), memo.MaxLength);
                break;
            case IntegerColumn i:
                CheckRange(m, a, Expect<int>(m, a, value), i.MinValue, i.MaxValue);
                break;
            case BigIntColumn b:
                CheckRange(m, a, Expect<long>(m, a, value), b.MinValue, b.MaxValue);
                break;
            case DecimalColumn d:
                var precision = d.Precision ?? TypeMapping.DefaultDecimalPrecision;
                var dec = Expect<decimal>(m, a, value);
                CheckColumnLimit(m, a, Math.Round(dec, precision), Pow10(TypeMapping.DecimalDigits(precision) - precision));
                CheckRange(m, a, dec, d.MinValue, d.MaxValue);
                break;
            case DoubleColumn f:
                var dbl = Expect<double>(m, a, value);
                if (!double.IsFinite(dbl))
                    throw XrmdFaultException.InvalidArgument(
                        $"Attribute '{a.LogicalName}' of entity '{m.LogicalName}' must be a finite number, got {dbl}.");
                CheckRange(m, a, dbl, f.MinValue, f.MaxValue);
                break;
            case MoneyColumn money:
                var amount = Expect<MoneyValue>(m, a, value).Value;
                if (Math.Abs(Math.Round(amount, TypeMapping.MoneyScale)) > MoneyLimit)
                    throw OutOfRange(m, a, amount, -MoneyLimit, MoneyLimit);
                CheckRange(m, a, amount, AsDecimal(money.MinValue), AsDecimal(money.MaxValue));
                break;
            case BooleanColumn:
                Expect<bool>(m, a, value);
                break;
            case DateTimeColumn:
                Expect<DateTime>(m, a, value);
                break;
            case UniqueIdentifierColumn:
                Expect<Guid>(m, a, value);
                break;
            case ImageColumn:
                // The image itself: the Core makes the thumbnail and keeps the full image (ruling F4).
                Expect<byte[]>(m, a, value);
                break;
            case MultiChoiceColumn multi:
                foreach (var option in Expect<ChoiceValues>(m, a, value)) CheckOption(m, a, multi.OptionSet, option);
                break;
            case ChoiceColumnBase enumAttribute: // Picklist, State, Status
                CheckOption(m, a, enumAttribute.OptionSet, Expect<ChoiceValue>(m, a, value));
                break;
            case LookupColumn lookup:
                var reference = Expect<RecordReference>(m, a, value);
                if (lookup.Targets is { Count: > 0 } targets && !targets.Contains(reference.LogicalName, StringComparer.Ordinal))
                    throw XrmdFaultException.InvalidArgument(
                        $"Attribute '{a.LogicalName}' of entity '{m.LogicalName}' cannot reference entity '{reference.LogicalName}' " +
                        $"(allowed: {string.Join(", ", targets)}).");
                break;
            default:
                throw XrmdFaultException.InvalidArgument(
                    $"Attribute '{a.LogicalName}' of entity '{m.LogicalName}' has type {a.AttributeType} which cannot be written.");
        }
    }

    /// <summary>The columns of an <c>activityparty</c> item that a party list value may carry besides <c>partyid</c>,
    /// <c>addressused</c> and <c>participationtypemask</c>: those a read returns, ignored on write (a retrieved list can be sent back).</summary>
    private static readonly HashSet<string> PartyReadOnlyColumns = new(StringComparer.Ordinal)
    {
        ActivitySchema.ActivityPartyId, ActivitySchema.ActivityId, ActivitySchema.IsPartyDeleted, ActivitySchema.PartyTypeCode,
        ActivitySchema.PartyIdName, "ownerid",
    };

    /// <summary>
    /// A party list value (plan 2.11 A8, A9): null, or a <see cref="RecordCollection"/> (an SDK <c>EntityCollection</c>) of <c>activityparty</c> entities. Each item
    /// names a <c>partyid</c> of the column's targets (party tables of <see cref="ActivitySchema.PartyRecordTables"/>) and/or an <c>addressused</c> (at most
    /// <see cref="ActivitySchema.AddressUsedMaxLength"/> characters; an address alone, unresolved, on <c>email</c> only); a
    /// <c>participationtypemask</c>, when given, is the column's own type (Regarding and Owner are the server's). <c>from</c> and
    /// <c>organizer</c> take one item at most (from memory: checklist 3b, 3c). The existence of the party records is the Core's
    /// check (404).
    /// </summary>
    private static void ValidatePartyList(TableDefinition m, ColumnDefinition a, object? value)
    {
        if (value is null) return;
        if (value is not RecordCollection collection)
            throw XrmdFaultException.InvalidArgument(
                $"Party list '{a.LogicalName}' of entity '{m.LogicalName}' expects an EntityCollection of {ActivitySchema.Party}, got {TypeName(value)}.");
        var columns = ActivitySchema.PartyLists(m).Where(p => p.Attribute == a.LogicalName).ToList();
        if (columns.Count == 0) throw XrmdFaultException.InvalidArgument($"Party list '{a.LogicalName}' of entity '{m.LogicalName}' cannot be written.");
        var (_, type, single) = columns[0];
        // The column's own targets (plan 4.1b: the email party lists also name a queue), the default party tables without them.
        IReadOnlyList<string> targets = a is LookupColumn { Targets.Count: > 0 } lookup
            ? [.. lookup.Targets.Where(ActivitySchema.PartyRecordTables.Contains)]
            : ActivitySchema.PartyTargets;
        if (single && collection.Records.Count > 1)
            throw XrmdFaultException.InvalidArgument(
                $"Party list '{a.LogicalName}' of entity '{m.LogicalName}' takes at most one party; {collection.Records.Count} were given.");
        foreach (var item in collection.Records)
        {
            if (item is null || item.LogicalName != ActivitySchema.Party)
                throw XrmdFaultException.InvalidArgument(
                    $"Party list '{a.LogicalName}' of entity '{m.LogicalName}' holds {ActivitySchema.Party} entities, not '{item?.LogicalName}'.");
            foreach (var (name, itemValue) in item.Attributes)
            {
                switch (name)
                {
                    case ActivitySchema.PartyId:
                        if (itemValue is null) break;
                        if (itemValue is not RecordReference party)
                            throw XrmdFaultException.InvalidArgument(
                                $"'{ActivitySchema.PartyId}' of a party in '{a.LogicalName}' expects an EntityReference, got {TypeName(itemValue)}.");
                        if (!targets.Contains(party.LogicalName ?? ""))
                            throw XrmdFaultException.InvalidArgument(
                                $"A party in '{a.LogicalName}' of entity '{m.LogicalName}' cannot be a '{party.LogicalName}' record " +
                                $"(allowed: {string.Join(", ", targets)}).");
                        break;
                    case ActivitySchema.AddressUsed:
                        if (itemValue is null) break;
                        if (itemValue is not string address)
                            throw XrmdFaultException.InvalidArgument(
                                $"'{ActivitySchema.AddressUsed}' of a party in '{a.LogicalName}' expects a String, got {TypeName(itemValue)}.");
                        if (address.Length > ActivitySchema.AddressUsedMaxLength)
                            throw XrmdFaultException.InvalidArgument(
                                $"'{ActivitySchema.AddressUsed}' of a party in '{a.LogicalName}' is {address.Length} characters long; MaxLength is {ActivitySchema.AddressUsedMaxLength}.");
                        break;
                    case ActivitySchema.ParticipationTypeMask:
                        if (itemValue is null) break;
                        var mask = itemValue as ChoiceValue
                                   ?? throw XrmdFaultException.InvalidArgument(
                                       $"'{ActivitySchema.ParticipationTypeMask}' of a party in '{a.LogicalName}' expects an OptionSetValue, got {TypeName(itemValue)}.");
                        if (mask.Value is (int)ParticipationType.Regarding or (int)ParticipationType.Owner)
                            throw XrmdFaultException.InvalidArgument(
                                $"'{ActivitySchema.ParticipationTypeMask}' {mask.Value} (Regarding, Owner) is maintained by the server and cannot be sent.");
                        if (mask.Value != (int)type)
                            throw XrmdFaultException.InvalidArgument(
                                $"'{ActivitySchema.ParticipationTypeMask}' {mask.Value} does not belong to party list '{a.LogicalName}' of entity " +
                                $"'{m.LogicalName}', whose parties have type {(int)type}.");
                        break;
                    default:
                        if (!PartyReadOnlyColumns.Contains(name))
                            throw XrmdFaultException.InvalidArgument(
                                $"Attribute '{name}' cannot be sent in a party of '{a.LogicalName}': a party names '{ActivitySchema.PartyId}' and/or '{ActivitySchema.AddressUsed}'.");
                        break;
                }
            }
            var hasParty = item.GetValue<RecordReference>(ActivitySchema.PartyId) is not null;
            var hasAddress = !string.IsNullOrEmpty(item.GetValue<string>(ActivitySchema.AddressUsed));
            if (!hasParty && !hasAddress)
                throw XrmdFaultException.InvalidArgument(
                    $"A party in '{a.LogicalName}' of entity '{m.LogicalName}' needs '{ActivitySchema.PartyId}' or '{ActivitySchema.AddressUsed}'.");
            // A7 inside a party: a partyobjecttypecode sent names the party's table (null without a party).
            if (item.Attributes.TryGetValue(ActivitySchema.PartyTypeCode, out var partyType) && partyType is not null
                && (partyType is not string named || named != item.GetValue<RecordReference>(ActivitySchema.PartyId)?.LogicalName))
                throw XrmdFaultException.InvalidArgument(
                    $"'{ActivitySchema.PartyTypeCode}' of a party in '{a.LogicalName}' must name the table of its '{ActivitySchema.PartyId}' " +
                    $"({item.GetValue<RecordReference>(ActivitySchema.PartyId)?.LogicalName ?? "none"}); '{partyType}' was sent.");
            if (!hasParty && m.LogicalName != ActivitySchema.Email)
                throw XrmdFaultException.InvalidArgument(
                    $"A party in '{a.LogicalName}' of entity '{m.LogicalName}' needs '{ActivitySchema.PartyId}': an unresolved " +
                    $"'{ActivitySchema.AddressUsed}' alone is accepted on {ActivitySchema.Email} only.");
        }
    }

    /// <summary>
    /// An EntityName column (plan 2.11 A7): the server writes it, so a value sent is accepted only when it is what the server
    /// stores. <c>activitytypecode</c> (no <c>AttributeOf</c>) names the record's own table; a companion of a lookup
    /// (<c>regardingobjecttypecode</c>, <c>objecttypecode</c>, <c>partyobjecttypecode</c>) names the lookup's target, or is null
    /// when the lookup is. On Update without the lookup in the request, the Core compares it with the stored target.
    /// </summary>
    private static void ValidateEntityName(Record e, TableDefinition m, EntityNameColumn a, object? value, bool isCreate)
    {
        if (value is not null and not string)
            throw XrmdFaultException.InvalidArgument(
                $"Attribute '{a.LogicalName}' of entity '{m.LogicalName}' expects a value of type String (a table's logical name), got {TypeName(value)}.");
        var name = value as string;
        if (string.IsNullOrEmpty(a.AttributeOf))
        {
            if (name is not null && name != m.LogicalName)
                throw XrmdFaultException.InvalidArgument(
                    $"Attribute '{a.LogicalName}' of entity '{m.LogicalName}' is set by the server to '{m.LogicalName}'; '{name}' cannot be written.");
            return;
        }
        string? expected;
        if (e.Attributes.TryGetValue(a.AttributeOf, out var lookup)) expected = (lookup as RecordReference)?.LogicalName;
        else if (isCreate) expected = null;
        else return; // the stored target decides (CoreOperations)
        if (!string.Equals(name, expected, StringComparison.Ordinal))
            throw MismatchedCompanion(m, a, name, expected);
    }

    /// <summary>The 400 of an EntityName companion that does not name its lookup's target (plan 2.11 A7).</summary>
    public static XrmdFaultException MismatchedCompanion(TableDefinition m, ColumnDefinition a, string? sent, string? target) =>
        XrmdFaultException.InvalidArgument(
            $"Attribute '{a.LogicalName}' of entity '{m.LogicalName}' follows '{a.AttributeOf}' and must be " +
            $"{(target is null ? "null" : $"'{target}'")}; '{sent ?? "null"}' was sent.");

    /// <summary>Largest money amount Dataverse accepts (and that fits the <c>decimal(19,4)</c> column).</summary>
    private const decimal MoneyLimit = 922337203685477.5807m;

    /// <summary>Attribute MinValue/MaxValue, when set.</summary>
    private static void CheckRange<T>(TableDefinition m, ColumnDefinition a, T value, T? min, T? max) where T : struct, IComparable<T>
    {
        if (min is { } lo && value.CompareTo(lo) < 0 || max is { } hi && value.CompareTo(hi) > 0)
            throw OutOfRange(m, a, value, min, max);
    }

    /// <summary>The <c>decimal(23,p)</c> column holds |v| &lt; 10^(23-p) once rounded to p digits.</summary>
    private static void CheckColumnLimit(TableDefinition m, ColumnDefinition a, decimal rounded, decimal exclusiveLimit)
    {
        if (Math.Abs(rounded) >= exclusiveLimit)
            throw XrmdFaultException.InvalidArgument(
                $"Attribute '{a.LogicalName}' of entity '{m.LogicalName}' value {rounded} is out of range: |value| must be less than {exclusiveLimit}.");
    }

    private static XrmdFaultException OutOfRange<T>(TableDefinition m, ColumnDefinition a, T value, T? min, T? max) where T : struct =>
        XrmdFaultException.InvalidArgument(
            $"Attribute '{a.LogicalName}' of entity '{m.LogicalName}' value {value} is out of range [{min?.ToString() ?? "-"}, {max?.ToString() ?? "-"}].");

    /// <summary>Money bounds are doubles (as in the SDK); one beyond the decimal range bounds nothing the column can hold.</summary>
    private static decimal? AsDecimal(double? bound) =>
        bound is { } b && double.IsFinite(b) && Math.Abs(b) < 7.9e28 ? (decimal)b : null;

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++) result *= 10;
        return result;
    }

    private static T Expect<T>(TableDefinition m, ColumnDefinition a, object value) =>
        value is T typed
            ? typed
            : throw XrmdFaultException.InvalidArgument(
                $"Attribute '{a.LogicalName}' of entity '{m.LogicalName}' expects a value of type {TypeName(typeof(T))}, " +
                $"got {TypeName(value)}.");

    private static void CheckLength(TableDefinition m, ColumnDefinition a, string value, int? maxLength)
    {
        if (maxLength is { } max && value.Length > max)
            throw XrmdFaultException.InvalidArgument(
                $"Attribute '{a.LogicalName}' of entity '{m.LogicalName}' is {value.Length} characters long; MaxLength is {max}.");
    }

    /// <summary>
    /// The value must be one of the options. The cache puts the options of a global option set in the attributes bound to
    /// it, so these are checked the same way; a global set without options accepts no value (a local one without options
    /// is not checked).
    /// </summary>
    private static void CheckOption(TableDefinition m, ColumnDefinition a, ChoiceSet? optionSet, ChoiceValue option)
    {
        var options = optionSet?.Options;
        var check = optionSet?.IsGlobal == true || options is { Count: > 0 };
        if (check && !(options ?? []).Any(o => o.Value == option.Value))
            throw XrmdFaultException.InvalidArgument(
                $"Value {option.Value} is not a valid option for attribute '{a.LogicalName}' of entity '{m.LogicalName}'.");
    }

    /// <summary>The type name a fault shows for <paramref name="value"/>: the SDK name of a model value type (S.2e3b, plan Decision 3).</summary>
    internal static string TypeName(object value) => TypeName(value.GetType());

    /// <summary>
    /// The type name a fault shows for <paramref name="type"/>: the name of the SDK type a model value type stands for
    /// (<see cref="ChoiceValue"/> → <c>OptionSetValue</c>, <see cref="RecordReference"/> → <c>EntityReference</c>, …, arrays included),
    /// so the texts are those of the SDK values a caller sends; any other type, its own name.
    /// </summary>
    internal static string TypeName(Type type) =>
        SdkTypeNames.TryGetValue(type, out var name) ? name
        : type.IsArray && type.GetElementType() is { } element && SdkTypeNames.TryGetValue(element, out var elementName) ? elementName + "[]"
        : type.Name;

    private static readonly Dictionary<Type, string> SdkTypeNames = new()
    {
        [typeof(ChoiceValue)] = "OptionSetValue",
        [typeof(ChoiceValues)] = "OptionSetValueCollection",
        [typeof(MoneyValue)] = "Money",
        [typeof(RecordReference)] = "EntityReference",
        [typeof(Record)] = "Entity",
        [typeof(RecordCollection)] = "EntityCollection",
        [typeof(RecordReferenceCollection)] = "EntityReferenceCollection",
        [typeof(AliasedColumnValue)] = "AliasedValue",
        [typeof(RelationshipName)] = "Relationship",
        [typeof(ColumnSelection)] = "ColumnSet",
        [typeof(Model.Query.RecordQuery)] = "QueryExpression",
        [typeof(Model.Query.AttributeQuery)] = "QueryByAttribute",
        [typeof(Model.Query.FetchQuery)] = "FetchExpression",
    };
}
