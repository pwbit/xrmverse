using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xrmd.Model.Security;

namespace Xrmd.Abstractions.Sdk;

/// <summary>
/// Converters between the Microsoft SDK's record types and our own model (<c>Xrmd.Model.Records</c>). Every conversion
/// copies into new instances; the model and the SDK object never share a mutable collection.
/// <para>
/// Known value types, converted recursively both ways: AccessRights (to AccessMask), PrincipalAccess, RolePrivilege and their exact arrays (see <c>Xrmd.Model.Security</c>), OptionSetValue, OptionSetValueCollection, Money, EntityReference,
/// Entity, EntityCollection, EntityReferenceCollection, AliasedValue, ColumnSet (with its AttributeExpressions), QueryExpression, QueryByAttribute and FetchExpression (see <see cref="SdkQueries"/>), Relationship and exact arrays of Entity, EntityReference, OptionSetValue,
/// Money or object (exact array types only; string[], arrays of other types and subtype arrays pass through). Primitives and anything else (for example BooleanManagedProperty, byte[])
/// pass through unchanged, as the same instance: byte[], string[] and the other pass-through values are shared by reference between the
/// SDK object and the model object, not copied.
/// </para>
/// <para>
/// Not carried by the ColumnSet conversion: <c>ExtensionData</c> and the lazy-file members (<c>HasLazyFileAttribute</c>,
/// <c>LazyFileAttributeEntityName</c>, <c>LazyFileAttributeKey</c>, <c>LazyFileAttributeValue</c>,
/// <c>LazyFileAttributeSizeLimit</c>); they are not part of the data contract.
/// </para>
/// </summary>
/// <summary>
/// How a Fill maps the members of what it writes: <see cref="Child"/> for the parts of a record, query or message (always
/// converted), <see cref="Value"/> for attribute, parameter and condition values (only the known value types convert, the rest
/// passes through as itself; <c>current</c> is the SDK value the slot holds before it is refilled). The converters build fresh
/// objects; <see cref="SdkMirror"/> looks up twins.
/// </summary>
internal interface IFillMap
{
    object? Child(object? value);

    object? Value(object? value, object? current);

    /// <summary>False for the converters, which reject null elements of collections; the mirror carries them.</summary>
    bool AllowNullElements { get; }
}

public static class SdkRecords
{
    public static Record ToModel(this Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var result = new Record();
        Fill(entity, result, ToModelMap);
        return result;
    }

    public static Entity ToSdk(this Record record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var result = new Entity();
        Fill(record, result, ToSdkMap);
        return result;
    }

    public static RecordReference ToModel(this EntityReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var result = new RecordReference();
        Fill(reference, result, ToModelMap);
        return result;
    }

    public static EntityReference ToSdk(this RecordReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var result = new EntityReference();
        Fill(reference, result, ToSdkMap);
        return result;
    }

    public static RecordCollection ToModel(this EntityCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var result = new RecordCollection();
        Fill(collection, result, ToModelMap);
        return result;
    }

    public static EntityCollection ToSdk(this RecordCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var result = new EntityCollection();
        Fill(collection, result, ToSdkMap);
        return result;
    }

    public static RecordReferenceCollection ToModel(this EntityReferenceCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var result = new RecordReferenceCollection();
        Fill(collection, result, ToModelMap);
        return result;
    }

    public static EntityReferenceCollection ToSdk(this RecordReferenceCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var result = new EntityReferenceCollection();
        Fill(collection, result, ToSdkMap);
        return result;
    }

    public static ColumnSelection ToModel(this ColumnSet columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var result = new ColumnSelection();
        Fill(columns, result, ToModelMap);
        return result;
    }

    public static ColumnSet ToSdk(this ColumnSelection columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var result = new ColumnSet();
        Fill(columns, result, ToSdkMap);
        return result;
    }

    public static RelationshipName ToModel(this Relationship relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        return new RelationshipName(relationship.SchemaName, relationship.PrimaryEntityRole is { } role ? (RecordRole)(int)role : null);
    }

    public static Relationship ToSdk(this RelationshipName relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        var result = new Relationship();
        Fill(relationship, result);
        return result;
    }

    // Fill: write the whole state of the source into an existing target. Containers are cleared and refilled in source
    // order (SDK containers through Refill, which leaves an unchanged one as it is); members that are objects of a known type go through map (a fresh conversion for ToModel/ToSdk, the mirror's
    // twin lookup for SdkMirror). The same code builds a new object, so the mirror and the converters cannot drift apart.

    internal static void Fill(Entity from, Record into, IFillMap map)
    {
        into.LogicalName = from.LogicalName;
        into.Id = from.Id;
        into.RowVersion = from.RowVersion;
        into.State = from.EntityState is { } state ? (RecordState)(int)state : null;
        into.Attributes.Clear();
        foreach (var a in from.Attributes) into.Attributes[a.Key] = map.Value(a.Value, null);
        into.FormattedValues.Clear();
        foreach (var f in from.FormattedValues) into.FormattedValues[f.Key] = f.Value;
        into.KeyAttributes.Clear();
        foreach (var k in from.KeyAttributes) into.KeyAttributes[k.Key] = map.Value(k.Value, null);
        var related = from.RelatedEntities.Select(r => (Key: (RelationshipName)map.Child(NotNull(r.Key, map, "relationship"))!, Value: (RecordCollection)map.Child(NotNull(r.Value, map, "collection"))!)).ToList();
        into.RelatedRecords.Clear();
        foreach (var r in related) into.RelatedRecords[r.Key] = r.Value;
    }

    internal static void Fill(Record from, Entity into, IFillMap map)
    {
        into.LogicalName = from.LogicalName;
        if (into.Id != from.Id) into.Id = from.Id;
        into.RowVersion = from.RowVersion;
        var oldAttributes = Snapshot(into.Attributes);
        var oldKeys = Snapshot(into.KeyAttributes);
        Refill(into.Attributes, [.. from.Attributes.Select(a => Pair(a.Key, map.Value(a.Value, Slot(oldAttributes, a.Key))))]);
        Refill(into.FormattedValues, [.. from.FormattedValues]);
        Refill(into.KeyAttributes, [.. from.KeyAttributes.Select(k => Pair(k.Key, map.Value(k.Value, Slot(oldKeys, k.Key))))]);
        Refill(into.RelatedEntities, [.. from.RelatedRecords.Select(r => KeyValuePair.Create((Relationship)map.Child(NotNull(r.Key, map, "relationship"))!, (EntityCollection)map.Child(NotNull(r.Value, map, "collection"))!))]);
        into.EntityState = from.State is { } state ? (EntityState)(int)state : null;
    }

    internal static void Fill(EntityReference from, RecordReference into, IFillMap map)
    {
        into.LogicalName = from.LogicalName;
        into.Id = from.Id;
        into.Name = from.Name;
        into.RowVersion = from.RowVersion;
        into.KeyAttributes.Clear();
        foreach (var k in from.KeyAttributes) into.KeyAttributes[k.Key] = map.Value(k.Value, null);
    }

    internal static void Fill(RecordReference from, EntityReference into, IFillMap map)
    {
        into.LogicalName = from.LogicalName;
        into.Id = from.Id;
        into.Name = from.Name;
        into.RowVersion = from.RowVersion;
        var oldKeys = Snapshot(into.KeyAttributes);
        Refill(into.KeyAttributes, [.. from.KeyAttributes.Select(k => Pair(k.Key, map.Value(k.Value, Slot(oldKeys, k.Key))))]);
    }

    internal static void Fill(EntityCollection from, RecordCollection into, IFillMap map)
    {
        into.EntityName = from.EntityName;
        into.MoreRecords = from.MoreRecords;
        into.PagingCookie = from.PagingCookie;
        into.TotalRecordCount = from.TotalRecordCount;
        into.TotalRecordCountLimitExceeded = from.TotalRecordCountLimitExceeded;
        into.MinActiveRowVersion = from.MinActiveRowVersion;
        into.RecordCountLessThanMarv = from.RecordCountLessThanMarv;
        into.Records.Clear();
        foreach (var e in from.Entities) into.Records.Add((Record)map.Child(NotNull(e, map, "entity"))!);
    }

    internal static void Fill(RecordCollection from, EntityCollection into, IFillMap map)
    {
        into.EntityName = from.EntityName;
        into.MoreRecords = from.MoreRecords;
        into.PagingCookie = from.PagingCookie;
        into.TotalRecordCount = from.TotalRecordCount;
        into.TotalRecordCountLimitExceeded = from.TotalRecordCountLimitExceeded;
        into.MinActiveRowVersion = from.MinActiveRowVersion;
        into.RecordCountLessThanMarv = from.RecordCountLessThanMarv;
        Refill(into.Entities, [.. from.Records.Select(r => (Entity)map.Child(NotNull(r, map, "record"))!)]);
    }

    internal static void Fill(EntityReferenceCollection from, RecordReferenceCollection into, IFillMap map)
    {
        into.Clear();
        foreach (var r in from) into.Add((RecordReference)map.Child(NotNull(r, map, "reference"))!);
    }

    internal static void Fill(RecordReferenceCollection from, EntityReferenceCollection into, IFillMap map) =>
        Refill(into, [.. from.Select(r => (EntityReference)map.Child(NotNull(r, map, "reference"))!)]);

    internal static void Fill(OptionSetValueCollection from, ChoiceValues into, IFillMap map)
    {
        into.Clear();
        foreach (var o in from) into.Add((ChoiceValue)map.Value(o, null)!);
    }

    internal static void Fill(ChoiceValues from, OptionSetValueCollection into, IFillMap map)
    {
        var old = into.ToArray();
        Refill(into, [.. from.Select((o, i) => (OptionSetValue)map.Value(o, i < old.Length ? old[i] : null)!)]);
    }

    internal static void Fill(ColumnSet from, ColumnSelection into, IFillMap map)
    {
        into.AllColumns = from.AllColumns;
        into.Columns ??= new List<string>();
        into.Columns.Clear();
        foreach (var c in from.Columns) into.Columns.Add(c);
        into.Expressions.Clear();
        foreach (var e in from.AttributeExpressions) into.Expressions.Add((ColumnExpression)map.Child(e)!);
    }

    internal static void Fill(ColumnSelection from, ColumnSet into, IFillMap map)
    {
        into.AllColumns = from.AllColumns;
        Refill(into.Columns, [.. from.Columns ?? []]);
        Refill(into.AttributeExpressions, [.. (from.Expressions ?? []).Select(e => (XrmAttributeExpression)map.Child(e)!)]);
    }

    internal static void Fill(RelationshipName from, Relationship into)
    {
        into.SchemaName = from.SchemaName;
        into.PrimaryEntityRole = from.PrimaryEntityRole is { } role ? (EntityRole)(int)role : null;
    }

    internal static readonly IFillMap ToModelMap = new ConverterMap(true);

    internal static readonly IFillMap ToSdkMap = new ConverterMap(false);

    private sealed class ConverterMap(bool toModel) : IFillMap
    {
        public object? Child(object? value) => toModel ? ToModelAny(value) : ToSdkAny(value);

        public object? Value(object? value, object? current) => toModel ? ValueToModel(value) : ValueToSdk(value);

        public bool AllowNullElements => false;
    }

    private static T NotNull<T>(T? element, IFillMap map, string parameterName) where T : class
    {
        if (element is null && !map.AllowNullElements) throw new ArgumentNullException(parameterName);
        return element!;
    }

    private static KeyValuePair<string, object?> Pair(string key, object? value) => KeyValuePair.Create(key, value);

    /// <summary>
    /// Writes <paramref name="values"/> into a keyed SDK container: when it holds exactly these keys in this order only the values are
    /// set, which keeps valid an enumeration a plug-in has open on it (across a call of its organization service, around which its
    /// view is read back and written again); otherwise the keys missing from <paramref name="values"/> are removed and the rest set in
    /// source order, so a key the container still holds keeps its position and a new key takes the slot a removed one freed, as a direct
    /// write on the SDK object (remove a column, set another) placed it. When none of its keys is kept it is cleared, and the values
    /// go in in source order.
    /// </summary>
    internal static void Refill<TKey, TValue>(DataCollection<TKey, TValue> into, IReadOnlyList<KeyValuePair<TKey, TValue>> values)
    {
        if (!SameKeys(into, values))
        {
            var keep = new HashSet<TKey>(values.Count);
            foreach (var p in values) keep.Add(p.Key);
            List<TKey>? gone = null;
            foreach (var p in into)
                if (!keep.Contains(p.Key)) (gone ??= []).Add(p.Key);
            if (gone is not null && gone.Count == into.Count) into.Clear();
            else if (gone is not null)
                foreach (var key in gone) into.Remove(key);
        }
        foreach (var p in values) into[p.Key] = p.Value;
    }

    /// <summary>
    /// Writes <paramref name="values"/> into an SDK list: left untouched when it already holds the same items (the same instances; equal
    /// values for strings and value types) in the same order, so an open enumeration stays valid; otherwise cleared and refilled.
    /// </summary>
    internal static void Refill<T>(ICollection<T> into, IReadOnlyList<T> values)
    {
        if (SameItems(into, values)) return;
        into.Clear();
        foreach (var v in values) into.Add(v);
    }

    private static bool SameKeys<TKey, TValue>(DataCollection<TKey, TValue> into, IReadOnlyList<KeyValuePair<TKey, TValue>> values)
    {
        if (into.Count != values.Count) return false;
        var i = 0;
        foreach (var p in into)
            if (!EqualityComparer<TKey>.Default.Equals(p.Key, values[i++].Key)) return false;
        return true;
    }

    private static bool SameItems<T>(ICollection<T> into, IReadOnlyList<T> values)
    {
        if (into.Count != values.Count) return false;
        var i = 0;
        foreach (var item in into)
        {
            object? current = item, next = values[i++];
            if (!ReferenceEquals(current, next) && !(current is string or ValueType && Equals(current, next))) return false;
        }
        return true;
    }

    /// <summary>A copy of a container's content taken before it is cleared and refilled; null while it is empty.</summary>
    internal static Dictionary<string, object?>? Snapshot(IEnumerable<KeyValuePair<string, object?>> source)
    {
        Dictionary<string, object?>? result = null;
        foreach (var p in source) (result ??= new Dictionary<string, object?>(StringComparer.Ordinal))[p.Key] = p.Value;
        return result;
    }

    internal static object? Slot(Dictionary<string, object?>? snapshot, string key) => snapshot is not null && snapshot.TryGetValue(key, out var v) ? v : null;

    /// <summary>A fresh conversion of any known SDK object to its model form (value types and query parts included).</summary>
    internal static object? ToModelAny(object? value) => value switch
    {
        FilterExpression f => f.ToModel(),
        ConditionExpression c => c.ToModel(),
        LinkEntity l => l.ToModel(),
        OrderExpression o => o.ToModel(),
        PagingInfo p => p.ToModel(),
        XrmAttributeExpression e => e.ToModel(),
        _ => ValueToModel(value),
    };

    /// <summary>A fresh conversion of any known model object to its SDK form (value types and query parts included).</summary>
    internal static object? ToSdkAny(object? value) => value switch
    {
        QueryFilter f => f.ToSdk(),
        QueryCondition c => c.ToSdk(),
        QueryLink l => l.ToSdk(),
        QueryOrder o => o.ToSdk(),
        QueryPaging p => p.ToSdk(),
        ColumnExpression e => e.ToSdk(),
        _ => ValueToSdk(value),
    };

    /// <summary>Converts an SDK attribute or parameter value to its model form; see the class remarks for the known types.</summary>
    public static object? ValueToModel(object? value) => value switch
    {
        null => null,
        OptionSetValue o => new ChoiceValue(o.Value),
        OptionSetValueCollection c => ToChoiceValues(c),
        Money m => new MoneyValue(m.Value),
        EntityReference r => r.ToModel(),
        Entity e => e.ToModel(),
        EntityCollection c => c.ToModel(),
        EntityReferenceCollection c => c.ToModel(),
        ColumnSet c => c.ToModel(),
        QueryExpression q => q.ToModel(),
        QueryByAttribute q => q.ToModel(),
        FetchExpression q => q.ToModel(),
        Relationship r => r.ToModel(),
        AliasedValue a => new AliasedColumnValue(a.EntityLogicalName, a.AttributeLogicalName, ValueToModel(a.Value)),
        AccessRights r => (AccessMask)(int)r,
        PrincipalAccess p => PrincipalToModel(p),
        RolePrivilege p => PrivilegeToModel(p),
        PrincipalAccess[] a when a.GetType() == typeof(PrincipalAccess[]) => Array.ConvertAll(a, PrincipalToModel),
        RolePrivilege[] a when a.GetType() == typeof(RolePrivilege[]) => Array.ConvertAll(a, PrivilegeToModel),
        Entity[] a when a.GetType() == typeof(Entity[]) => Array.ConvertAll(a, e => e.ToModel()),
        EntityReference[] a when a.GetType() == typeof(EntityReference[]) => Array.ConvertAll(a, r => r.ToModel()),
        OptionSetValue[] a when a.GetType() == typeof(OptionSetValue[]) => Array.ConvertAll(a, o => new ChoiceValue(o.Value)),
        Money[] a when a.GetType() == typeof(Money[]) => Array.ConvertAll(a, m => new MoneyValue(m.Value)),
        object[] a when a.GetType() == typeof(object[]) => Array.ConvertAll(a, ValueToModel),
        _ => value,
    };

    /// <summary>Converts a model attribute or parameter value to its SDK form; the inverse of <see cref="ValueToModel"/>.</summary>
    public static object? ValueToSdk(object? value) => value switch
    {
        null => null,
        ChoiceValue o => new OptionSetValue(o.Value),
        ChoiceValues c => ToOptionSetValues(c),
        MoneyValue m => new Money(m.Value),
        RecordReference r => r.ToSdk(),
        Record e => e.ToSdk(),
        RecordCollection c => c.ToSdk(),
        RecordReferenceCollection c => c.ToSdk(),
        ColumnSelection c => c.ToSdk(),
        RecordQuery q => q.ToSdk(),
        AttributeQuery q => q.ToSdk(),
        FetchQuery q => q.ToSdk(),
        RelationshipName r => r.ToSdk(),
        AliasedColumnValue a => new AliasedValue(a.EntityLogicalName, a.AttributeLogicalName, ValueToSdk(a.Value)),
        AccessMask m => (AccessRights)(int)m,
        PrincipalAccessValue p => PrincipalToSdk(p),
        PrivilegeGrant g => PrivilegeToSdk(g),
        PrincipalAccessValue[] a when a.GetType() == typeof(PrincipalAccessValue[]) => Array.ConvertAll(a, PrincipalToSdk),
        PrivilegeGrant[] a when a.GetType() == typeof(PrivilegeGrant[]) => Array.ConvertAll(a, PrivilegeToSdk),
        Record[] a when a.GetType() == typeof(Record[]) => Array.ConvertAll(a, e => e.ToSdk()),
        RecordReference[] a when a.GetType() == typeof(RecordReference[]) => Array.ConvertAll(a, r => r.ToSdk()),
        ChoiceValue[] a when a.GetType() == typeof(ChoiceValue[]) => Array.ConvertAll(a, o => new OptionSetValue(o.Value)),
        MoneyValue[] a when a.GetType() == typeof(MoneyValue[]) => Array.ConvertAll(a, m => new Money(m.Value)),
        object[] a when a.GetType() == typeof(object[]) => Array.ConvertAll(a, ValueToSdk),
        _ => value,
    };

    // Access and privilege values: the rules of Xrmd.Security.Sdk.SdkSecurityTypes (Abstractions cannot reference Xrmd.Security). The
    // access flags are cast by int value; the privilege depth is range-checked.
    private static PrincipalAccessValue PrincipalToModel(PrincipalAccess p) =>
        new(p.Principal?.ToModel()!, (AccessMask)(int)p.AccessMask);

    private static PrincipalAccess PrincipalToSdk(PrincipalAccessValue p) =>
        new() { Principal = p.Principal?.ToSdk(), AccessMask = (AccessRights)(int)p.AccessMask };

    private static PrivilegeGrant PrivilegeToModel(RolePrivilege p)
    {
        var depth = (PrivilegeScope)(int)p.Depth;
        if (!Enum.IsDefined(depth)) throw new ArgumentOutOfRangeException(nameof(p), p.Depth, "Not a defined PrivilegeDepth.");
        return new PrivilegeGrant(p.PrivilegeId, p.PrivilegeName, p.BusinessUnitId, depth);
    }

    private static RolePrivilege PrivilegeToSdk(PrivilegeGrant g)
    {
        if (!Enum.IsDefined(g.Depth)) throw new ArgumentOutOfRangeException(nameof(g), g.Depth, "Not a defined PrivilegeScope.");
        return new RolePrivilege { PrivilegeId = g.PrivilegeId, PrivilegeName = g.PrivilegeName, BusinessUnitId = g.BusinessUnitId, Depth = (PrivilegeDepth)(int)g.Depth };
    }

    private static ChoiceValues ToChoiceValues(OptionSetValueCollection source)
    {
        var result = new ChoiceValues();
        foreach (var o in source) result.Add(new ChoiceValue(o.Value));
        return result;
    }

    private static OptionSetValueCollection ToOptionSetValues(ChoiceValues source)
    {
        var result = new OptionSetValueCollection();
        foreach (var o in source) result.Add(new OptionSetValue(o.Value));
        return result;
    }
}
