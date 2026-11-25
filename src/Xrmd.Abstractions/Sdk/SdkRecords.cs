using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Model.Query;
using Xrmd.Model.Records;

namespace Xrmd.Abstractions.Sdk;

/// <summary>
/// Converters between the Microsoft SDK's record types and our own model (<c>Xrmd.Model.Records</c>). Every conversion
/// copies into new instances; the model and the SDK object never share a mutable collection.
/// <para>
/// Known value types, converted recursively both ways: OptionSetValue, OptionSetValueCollection, Money, EntityReference,
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
public static class SdkRecords
{
    public static Record ToModel(this Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var record = new Record(entity.LogicalName, entity.Id)
        {
            RowVersion = entity.RowVersion,
            State = entity.EntityState is { } state ? (RecordState)(int)state : null,
        };
        foreach (var a in entity.Attributes) record.Attributes[a.Key] = ValueToModel(a.Value);
        foreach (var f in entity.FormattedValues) record.FormattedValues[f.Key] = f.Value;
        foreach (var k in entity.KeyAttributes) record.KeyAttributes[k.Key] = ValueToModel(k.Value);
        foreach (var r in entity.RelatedEntities) record.RelatedRecords[r.Key.ToModel()] = r.Value.ToModel();
        return record;
    }

    public static Entity ToSdk(this Record record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var entity = new Entity(record.LogicalName, record.Id) { RowVersion = record.RowVersion };
        foreach (var a in record.Attributes) entity.Attributes[a.Key] = ValueToSdk(a.Value);
        foreach (var f in record.FormattedValues) entity.FormattedValues[f.Key] = f.Value;
        foreach (var k in record.KeyAttributes) entity.KeyAttributes[k.Key] = ValueToSdk(k.Value);
        foreach (var r in record.RelatedRecords) entity.RelatedEntities[r.Key.ToSdk()] = r.Value.ToSdk();
        if (record.State is { } state) entity.EntityState = (EntityState)(int)state;
        return entity;
    }

    public static RecordReference ToModel(this EntityReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var result = new RecordReference
        {
            LogicalName = reference.LogicalName,
            Id = reference.Id,
            Name = reference.Name,
            RowVersion = reference.RowVersion,
        };
        foreach (var k in reference.KeyAttributes) result.KeyAttributes[k.Key] = ValueToModel(k.Value);
        return result;
    }

    public static EntityReference ToSdk(this RecordReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var result = new EntityReference(reference.LogicalName, reference.Id)
        {
            Name = reference.Name,
            RowVersion = reference.RowVersion,
        };
        foreach (var k in reference.KeyAttributes) result.KeyAttributes[k.Key] = ValueToSdk(k.Value);
        return result;
    }

    public static RecordCollection ToModel(this EntityCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var result = new RecordCollection
        {
            EntityName = collection.EntityName,
            MoreRecords = collection.MoreRecords,
            PagingCookie = collection.PagingCookie,
            TotalRecordCount = collection.TotalRecordCount,
            TotalRecordCountLimitExceeded = collection.TotalRecordCountLimitExceeded,
            MinActiveRowVersion = collection.MinActiveRowVersion,
            RecordCountLessThanMarv = collection.RecordCountLessThanMarv,
        };
        foreach (var e in collection.Entities) result.Records.Add(e.ToModel());
        return result;
    }

    public static EntityCollection ToSdk(this RecordCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var result = new EntityCollection
        {
            EntityName = collection.EntityName,
            MoreRecords = collection.MoreRecords,
            PagingCookie = collection.PagingCookie,
            TotalRecordCount = collection.TotalRecordCount,
            TotalRecordCountLimitExceeded = collection.TotalRecordCountLimitExceeded,
            MinActiveRowVersion = collection.MinActiveRowVersion,
            RecordCountLessThanMarv = collection.RecordCountLessThanMarv,
        };
        foreach (var r in collection.Records) result.Entities.Add(r.ToSdk());
        return result;
    }

    public static RecordReferenceCollection ToModel(this EntityReferenceCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var result = new RecordReferenceCollection();
        foreach (var r in collection) result.Add(r.ToModel());
        return result;
    }

    public static EntityReferenceCollection ToSdk(this RecordReferenceCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var result = new EntityReferenceCollection();
        foreach (var r in collection) result.Add(r.ToSdk());
        return result;
    }

    public static ColumnSelection ToModel(this ColumnSet columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var result = new ColumnSelection { AllColumns = columns.AllColumns, Columns = columns.Columns.ToList() };
        foreach (var e in columns.AttributeExpressions) result.Expressions.Add(e?.ToModel()!);
        return result;
    }

    public static ColumnSet ToSdk(this ColumnSelection columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var result = new ColumnSet(columns.AllColumns);
        foreach (var c in columns.Columns ?? []) result.Columns.Add(c);
        foreach (var e in columns.Expressions ?? []) result.AttributeExpressions.Add(e?.ToSdk()!);
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
        return new Relationship(relationship.SchemaName)
        {
            PrimaryEntityRole = relationship.PrimaryEntityRole is { } role ? (EntityRole)(int)role : null,
        };
    }

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
        Record[] a when a.GetType() == typeof(Record[]) => Array.ConvertAll(a, e => e.ToSdk()),
        RecordReference[] a when a.GetType() == typeof(RecordReference[]) => Array.ConvertAll(a, r => r.ToSdk()),
        ChoiceValue[] a when a.GetType() == typeof(ChoiceValue[]) => Array.ConvertAll(a, o => new OptionSetValue(o.Value)),
        MoneyValue[] a when a.GetType() == typeof(MoneyValue[]) => Array.ConvertAll(a, m => new Money(m.Value)),
        object[] a when a.GetType() == typeof(object[]) => Array.ConvertAll(a, ValueToSdk),
        _ => value,
    };

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
