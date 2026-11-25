using System.Runtime.Serialization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;
using Xrmd.Model.Serialization;

namespace Xrmd.Abstractions.Sdk;

/// <summary>
/// Converters between the SDK's <see cref="RemoteExecutionContext"/> and our <see cref="RemoteContext"/>, recursive over the
/// parent chain; values convert with <see cref="SdkRecords.ValueToModel"/> / <see cref="SdkRecords.ValueToSdk"/>. Every
/// conversion copies into new instances.
/// <para>
/// <see cref="ModelCanCarry(RemoteExecutionContext)"/> and <see cref="ModelCanCarry(QueryBase)"/> decide, on the SDK object and
/// before any conversion, whether the model set can carry every value (primitives string, bool, int, long, double, decimal, Guid,
/// DateTime, byte[], string[] and the record and query model types). A value of any other runtime type (compared exactly: an
/// <see cref="Entity"/> subclass is outside) makes the answer false, so the caller uses the SDK serializer. A typed array of a model type directly as a query condition or
/// <see cref="QueryByAttribute"/> value is outside too: today's query serializer refuses it.
/// </para>
/// </summary>
public static class SdkRemoteContext
{
    public static RemoteContext ToModel(this RemoteExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = Scalars(context);
        result.ParentContext = context.ParentContext?.ToModel();
        foreach (var p in context.InputParameters) result.InputParameters[p.Key] = SdkRecords.ValueToModel(p.Value);
        foreach (var p in context.OutputParameters) result.OutputParameters[p.Key] = SdkRecords.ValueToModel(p.Value);
        foreach (var p in context.SharedVariables) result.SharedVariables[p.Key] = SdkRecords.ValueToModel(p.Value);
        foreach (var i in context.PreEntityImages) result.PreEntityImages[i.Key] = i.Value.ToModel();
        foreach (var i in context.PostEntityImages) result.PostEntityImages[i.Key] = i.Value.ToModel();
        return result;
    }

    /// <summary>
    /// <see cref="ToModel(RemoteExecutionContext)"/> whose parameter values and images are the twins of the SDK ones in
    /// <paramref name="mirror"/> (paired, so lending the result gives back these same SDK instances, with their CLR types and any
    /// extension data); the scalars and owning extensions are copied as there.
    /// </summary>
    public static RemoteContext ToModel(this RemoteExecutionContext context, SdkMirror mirror)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mirror);
        var result = Scalars(context);
        result.ParentContext = context.ParentContext?.ToModel(mirror);
        // The collections cross whole, so their values convert by the value rules (an inner request or a paging held as a value is
        // its own twin, as in ValueToModel).
        foreach (var p in mirror.In(context.InputParameters)!) result.InputParameters[p.Key] = p.Value;
        foreach (var p in mirror.In(context.OutputParameters)!) result.OutputParameters[p.Key] = p.Value;
        foreach (var p in mirror.In(context.SharedVariables)!) result.SharedVariables[p.Key] = p.Value;
        foreach (var i in context.PreEntityImages) result.PreEntityImages[i.Key] = (Record)mirror.In(i.Value)!;
        foreach (var i in context.PostEntityImages) result.PostEntityImages[i.Key] = (Record)mirror.In(i.Value)!;
        return result;
    }

    private static RemoteContext Scalars(RemoteExecutionContext context) => new()
    {
        BusinessUnitId = context.BusinessUnitId,
        CorrelationId = context.CorrelationId,
        Depth = context.Depth,
        InitiatingUserAgent = context.InitiatingUserAgent,
        InitiatingUserAzureActiveDirectoryObjectId = context.InitiatingUserAzureActiveDirectoryObjectId,
        InitiatingUserId = context.InitiatingUserId,
        IsExecutingOffline = context.IsExecutingOffline,
        IsInTransaction = context.IsInTransaction,
        IsOfflinePlayback = context.IsOfflinePlayback,
        IsolationMode = context.IsolationMode,
        MessageName = context.MessageName,
        Mode = context.Mode,
        OperationCreatedOn = context.OperationCreatedOn,
        OperationId = context.OperationId,
        OrganizationId = context.OrganizationId,
        OrganizationName = context.OrganizationName,
        OwningExtension = context.OwningExtension?.ToModel(),
        PrimaryEntityId = context.PrimaryEntityId,
        PrimaryEntityName = context.PrimaryEntityName,
        RequestId = context.RequestId,
        SecondaryEntityName = context.SecondaryEntityName,
        Stage = context.Stage,
        UserAzureActiveDirectoryObjectId = context.UserAzureActiveDirectoryObjectId,
        UserId = context.UserId,
    };

    public static RemoteExecutionContext ToSdk(this RemoteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = new RemoteExecutionContext
        {
            BusinessUnitId = context.BusinessUnitId,
            CorrelationId = context.CorrelationId,
            Depth = context.Depth,
            InitiatingUserAgent = context.InitiatingUserAgent,
            InitiatingUserAzureActiveDirectoryObjectId = context.InitiatingUserAzureActiveDirectoryObjectId,
            InitiatingUserId = context.InitiatingUserId,
            IsExecutingOffline = context.IsExecutingOffline,
            IsInTransaction = context.IsInTransaction,
            IsOfflinePlayback = context.IsOfflinePlayback,
            IsolationMode = context.IsolationMode,
            MessageName = context.MessageName,
            Mode = context.Mode,
            OperationCreatedOn = context.OperationCreatedOn,
            OperationId = context.OperationId,
            OrganizationId = context.OrganizationId,
            OrganizationName = context.OrganizationName,
            OwningExtension = context.OwningExtension?.ToSdk(),
            ParentContext = context.ParentContext?.ToSdk(),
            PrimaryEntityId = context.PrimaryEntityId,
            PrimaryEntityName = context.PrimaryEntityName,
            RequestId = context.RequestId,
            SecondaryEntityName = context.SecondaryEntityName,
            Stage = context.Stage,
            UserAzureActiveDirectoryObjectId = context.UserAzureActiveDirectoryObjectId,
            UserId = context.UserId,
        };
        foreach (var p in context.InputParameters) result.InputParameters[p.Key] = SdkRecords.ValueToSdk(p.Value);
        foreach (var p in context.OutputParameters) result.OutputParameters[p.Key] = SdkRecords.ValueToSdk(p.Value);
        foreach (var p in context.SharedVariables) result.SharedVariables[p.Key] = SdkRecords.ValueToSdk(p.Value);
        foreach (var i in context.PreEntityImages) result.PreEntityImages[i.Key] = i.Value.ToSdk();
        foreach (var i in context.PostEntityImages) result.PostEntityImages[i.Key] = i.Value.ToSdk();
        return result;
    }

    // Nesting bound. The own reader refuses payloads nested deeper than ContextXml.MaxNesting XML levels (the caller then uses the
    // SDK reader), so the own writer is only used for values that stay inside it. Depth is counted in units of Step per nested
    // object (entity, collection, query, filter, link, parent context); one nested object takes at most 4 XML levels (a filter to
    // a condition value: Filters, Filter, Conditions, Condition, Values, value is 4 below the filter), so the count is an upper
    // bound of the XML depth. Limit keeps 8 levels of headroom for the root element and the leaf value.
    private const int Step = 4;
    private const int Limit = ContextXml.MaxNesting - 8;

    /// <summary>
    /// True when every value of <paramref name="context"/> (and of its parent chain) is inside the model set and nested no deeper
    /// than the own reader reads (<see cref="ContextXml.MaxNesting"/>); beyond that the SDK serializers are used.
    /// </summary>
    public static bool ModelCanCarry(RemoteExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var depth = 0;
        for (var c = context; c is not null; c = c.ParentContext, depth += Step)
        {
            if (depth > Limit) return false;
            var inner = depth + Step;
            if (HasExtensionData(c)) return false;
            if (c.OwningExtension is { } owner && !CanCarry(owner, Style.Context, inner)) return false;
            foreach (var p in c.InputParameters) if (!CanCarry(p.Value, Style.Context, inner)) return false;
            foreach (var p in c.OutputParameters) if (!CanCarry(p.Value, Style.Context, inner)) return false;
            foreach (var p in c.SharedVariables) if (!CanCarry(p.Value, Style.Context, inner)) return false;
            foreach (var i in c.PreEntityImages) if (!CanCarryElement(i.Value, Style.Context, inner)) return false;
            foreach (var i in c.PostEntityImages) if (!CanCarryElement(i.Value, Style.Context, inner)) return false;
        }
        return true;
    }

    /// <summary>True when every value of <paramref name="query"/> is inside the model set and not nested deeper than the own reader reads.</summary>
    public static bool ModelCanCarry(QueryBase query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return CanCarry(query, Style.Query, Step);
    }

    // style mirrors the writer: true while under a query root and outside the attributes of a record. In that style today's
    // query serializer does not know the typed arrays of model types (it throws "Invalid name character in 'Money[]'"), so they
    // are outside wherever a value stands directly (condition and QueryByAttribute values, key attribute values) and Entity[] is
    // outside in an aliased value; the attributes of a record and a query nested in a context are written with known types.
    private static bool CanCarry(object? value, Style style, int depth)
    {
        if (depth > Limit) return false;
        var next = depth + Step;
        if (value is IExtensibleDataObject { ExtensionData: not null }) return false;
        switch (value)
        {
            case null:
                return true;
            case string or bool or int or long or double or decimal or Guid or DateTime or byte[] or string[]:
            case OptionSetValue or Money or Relationship:
                return IsExact(value);
            case OptionSetValueCollection c:
                return IsExact(c) && c.All(o => CanCarryElement(o, style, next));
            case EntityReference r:
                return IsExact(r) && r.KeyAttributes.All(k => CanCarryDirect(k.Value, style, next));
            case Entity e:
                return IsExact(e) && CanCarryEntity(e, style, next);
            case EntityCollection c:
                return IsExact(c) && c.Entities.All(x => CanCarryElement(x, style, next));
            case EntityReferenceCollection c:
                return IsExact(c) && c.All(x => CanCarryElement(x, style, next));
            case AliasedValue a:
                return IsExact(a) && CanCarryDirect(a.Value, style == Style.Query ? Style.QueryAliased : style, next);
            case ColumnSet c:
                return IsExact(c) && CanCarryColumns(c);
            case Entity[] a:
                return a.GetType() == typeof(Entity[]) && a.All(x => CanCarryElement(x, style, next));
            case EntityReference[] a:
                return a.GetType() == typeof(EntityReference[]) && a.All(x => CanCarryElement(x, style, next));
            case OptionSetValue[] a:
                return a.GetType() == typeof(OptionSetValue[]) && a.All(x => CanCarryElement(x, style, next));
            case Money[] a:
                return a.GetType() == typeof(Money[]) && a.All(x => CanCarryElement(x, style, next));
            case QueryExpression q:
                return IsExact(q) && CanCarryQuery(q, style, next);
            case QueryByAttribute q:
                return IsExact(q) && CanCarryAttributeQuery(q, style, next);
            case FetchExpression q:
                return IsExact(q);
            default:
                return false;
        }
    }

    // A null element of a container or image is not preserved by the converters (they throw), so it is outside the model set;
    // null stays carriable only as a top-level value, which ValueToModel passes through.
    private static bool CanCarryElement(object? value, Style style, int depth) => value is not null && CanCarry(value, style, depth);

    // A value that stands directly in a condition, a QueryByAttribute or a key attribute list.
    private static bool CanCarryDirect(object? value, Style style, int depth) =>
        !Refused(value, style) && CanCarry(value, style, depth);

    private static bool Refused(object? value, Style style) => style switch
    {
        Style.Query => value is Entity[] or EntityReference[] or OptionSetValue[] or Money[],
        Style.QueryAliased => value is Entity[],
        _ => false,
    };

    private enum Style { Context, Query, QueryAliased }

    private static bool HasExtensionData(object value) => value is IExtensibleDataObject { ExtensionData: not null };

    private static bool CanCarryEntity(Entity e, Style style, int depth) =>
        e.Attributes.All(a => CanCarry(a.Value, Style.Context, depth))
        && e.KeyAttributes.All(k => CanCarryDirect(k.Value, style, depth))
        && e.RelatedEntities.All(r => !HasExtensionData(r.Key) && CanCarryElement(r.Value, style, depth));

    private static bool CanCarryQuery(QueryExpression q, Style style, int depth) =>
        depth <= Limit
        && !HasExtensionData(q)
        && (q.ColumnSet is null || CanCarryColumns(q.ColumnSet))
        && !(q.PageInfo is not null && HasExtensionData(q.PageInfo))
        && q.Orders.All(o => !HasExtensionData(o))
        && CanCarryFilter(q.Criteria, style, depth + Step)
        && q.LinkEntities.All(l => CanCarryLink(l, style, depth + Step))
        && (q.SubQueryExpression is null || CanCarryQuery(q.SubQueryExpression, style, depth + Step));

    private static bool CanCarryAttributeQuery(QueryByAttribute q, Style style, int depth) =>
        (q.ColumnSet is null || CanCarryColumns(q.ColumnSet))
        && !(q.PageInfo is not null && HasExtensionData(q.PageInfo))
        && q.Orders.All(o => !HasExtensionData(o))
        && q.Values.All(v => CanCarryDirect(v, style, depth + Step));

    private static bool CanCarryColumns(ColumnSet c) =>
        !HasExtensionData(c) && c.AttributeExpressions.All(e => !HasExtensionData(e));

    private static bool CanCarryLink(LinkEntity l, Style style, int depth) =>
        depth <= Limit
        && !HasExtensionData(l)
        && (l.Columns is null || CanCarryColumns(l.Columns))
        && l.Orders.All(o => !HasExtensionData(o))
        && CanCarryFilter(l.LinkCriteria, style, depth + Step)
        && l.LinkEntities.All(x => CanCarryLink(x, style, depth + Step));

    private static bool CanCarryFilter(FilterExpression? f, Style style, int depth) =>
        f is null
        || (depth <= Limit
            && !HasExtensionData(f)
            && f.Conditions.All(c => !HasExtensionData(c) && c.Values.All(v => CanCarryDirect(v, style, depth + Step)))
            && f.Filters.All(x => CanCarryFilter(x, style, depth + Step))
            && (f.AnyAllFilterLinkEntity is null || CanCarryLink(f.AnyAllFilterLinkEntity, style, depth + Step)));

    private static bool IsExact(object value) => value.GetType() switch
    {
        var t when t == typeof(string) || t == typeof(bool) || t == typeof(int) || t == typeof(long) || t == typeof(double)
            || t == typeof(decimal) || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(byte[]) || t == typeof(string[]) => true,
        var t when t == typeof(OptionSetValue) || t == typeof(Money) || t == typeof(Relationship) || t == typeof(OptionSetValueCollection)
            || t == typeof(EntityReference) || t == typeof(Entity) || t == typeof(EntityCollection)
            || t == typeof(EntityReferenceCollection) || t == typeof(AliasedValue) || t == typeof(ColumnSet)
            || t == typeof(QueryExpression) || t == typeof(QueryByAttribute) || t == typeof(FetchExpression) => true,
        _ => false,
    };
}
