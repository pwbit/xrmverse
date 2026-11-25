using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Records;

namespace Xrmd.Model.Serialization;

/// <summary>
/// The model set of the own context serializers (<see cref="ContextXml"/>, <see cref="ContextJson"/>), decided on model values: the
/// rule of the SDK-side check of S.2e2b (<c>SdkRemoteContext.ModelCanCarry</c>) for a context whose values are already model
/// objects. A value is inside when it is null (as a parameter), a string, bool, int, long, double, decimal, Guid, DateTime,
/// <c>byte[]</c> or <c>string[]</c>, or one of the record and query model types (arrays of <see cref="Record"/>,
/// <see cref="RecordReference"/>, <see cref="ChoiceValue"/> and <see cref="MoneyValue"/> of exactly that array type), nested no
/// deeper than the own reader reads (<see cref="ContextXml.MaxNesting"/>). Any other runtime type (a value the converters pass
/// through as itself, such as an SDK-only type or an <c>int[]</c>) is outside, and so is a null element of a container or image. A
/// typed array of a model type directly as a query condition or <see cref="AttributeQuery"/> value is outside too (today's query
/// writer refuses it).
/// </summary>
public static class ModelSet
{
    // The nesting bound of the SDK-side check, counted the same way: Step per nested object (record, collection, query, filter,
    // link, parent context), Limit keeping 8 levels of headroom for the root element and the leaf value.
    private const int Step = 4;
    private const int Limit = ContextXml.MaxNesting - 8;

    private enum Style { Context, Query, QueryAliased }

    /// <summary>
    /// True when every value of <paramref name="context"/> (and of its parent chain) is inside the model set and nested no deeper
    /// than the own reader reads; the own serializers then write it byte for byte as the SDK's would.
    /// </summary>
    public static bool CanCarry(RemoteContext context) => CanCarry(context, null);

    /// <summary>
    /// <see cref="CanCarry(RemoteContext)"/>, with <paramref name="admit"/> asked about every model object of the context (records,
    /// references, collections, the immutable values, arrays, queries and their parts, the owning extensions); a false answer makes
    /// the context outside. A carrier whose model objects stand for other objects (an SDK mirror's twins) refuses through it the
    /// objects whose counterpart the own serializers would not write the same way.
    /// </summary>
    public static bool CanCarry(RemoteContext context, Func<object, bool>? admit)
    {
        ArgumentNullException.ThrowIfNull(context);
        var walk = new Walk(admit);
        var depth = 0;
        for (var c = context; c is not null; c = c.ParentContext, depth += Step)
        {
            if (depth > Limit) return false;
            var inner = depth + Step;
            if (c.OwningExtension is { } owner && !walk.CanCarry(owner, Style.Context, inner)) return false;
            foreach (var p in c.InputParameters) if (!walk.CanCarry(p.Value, Style.Context, inner)) return false;
            foreach (var p in c.OutputParameters) if (!walk.CanCarry(p.Value, Style.Context, inner)) return false;
            foreach (var p in c.SharedVariables) if (!walk.CanCarry(p.Value, Style.Context, inner)) return false;
            foreach (var i in c.PreEntityImages) if (!walk.CanCarryElement(i.Value, Style.Context, inner)) return false;
            foreach (var i in c.PostEntityImages) if (!walk.CanCarryElement(i.Value, Style.Context, inner)) return false;
        }
        return true;
    }

    private sealed class Walk(Func<object, bool>? admit)
    {
        private bool Admit(object value) => admit is null || admit(value);

        // style mirrors the writer: Query while under a query root and outside the attributes of a record (see the class remarks).
        public bool CanCarry(object? value, Style style, int depth)
        {
            if (depth > Limit) return false;
            var next = depth + Step;
            switch (value)
            {
                case null:
                    return true;
                case string or bool or int or long or double or decimal or Guid or DateTime:
                    return true;
                case byte[] or string[]:
                    return IsExactArray(value) && Admit(value);
                case ChoiceValue or MoneyValue or RelationshipName:
                    return Admit(value);
                case ChoiceValues c:
                    return Admit(c) && c.All(o => CanCarryElement(o, style, next));
                case RecordReference r:
                    return Admit(r) && r.KeyAttributes.All(k => CanCarryDirect(k.Value, style, next));
                case Record e:
                    return Admit(e) && CanCarryRecord(e, style, next);
                case RecordCollection c:
                    return Admit(c) && c.Records.All(x => CanCarryElement(x, style, next));
                case RecordReferenceCollection c:
                    return Admit(c) && c.All(x => CanCarryElement(x, style, next));
                case AliasedColumnValue a:
                    return Admit(a) && CanCarryDirect(a.Value, style == Style.Query ? Style.QueryAliased : style, next);
                case ColumnSelection c:
                    return CanCarryColumns(c);
                case Record[] a:
                    return a.GetType() == typeof(Record[]) && Admit(a) && a.All(x => CanCarryElement(x, style, next));
                case RecordReference[] a:
                    return a.GetType() == typeof(RecordReference[]) && Admit(a) && a.All(x => CanCarryElement(x, style, next));
                case ChoiceValue[] a:
                    return a.GetType() == typeof(ChoiceValue[]) && Admit(a) && a.All(x => CanCarryElement(x, style, next));
                case MoneyValue[] a:
                    return a.GetType() == typeof(MoneyValue[]) && Admit(a) && a.All(x => CanCarryElement(x, style, next));
                case RecordQuery q:
                    return Admit(q) && CanCarryQuery(q, style, next);
                case AttributeQuery q:
                    return Admit(q) && CanCarryAttributeQuery(q, style, next);
                case FetchQuery q:
                    return Admit(q);
                default:
                    return false;
            }
        }

        // A null element of a container or image is outside (the converters refuse it); null stays carriable only as a top-level value.
        public bool CanCarryElement(object? value, Style style, int depth) => value is not null && CanCarry(value, style, depth);

        // A value that stands directly in a condition, an attribute query or a key attribute list.
        private bool CanCarryDirect(object? value, Style style, int depth) => !Refused(value, style) && CanCarry(value, style, depth);

        private static bool Refused(object? value, Style style) => style switch
        {
            Style.Query => value is Record[] or RecordReference[] or ChoiceValue[] or MoneyValue[],
            Style.QueryAliased => value is Record[],
            _ => false,
        };

        private bool CanCarryRecord(Record e, Style style, int depth)
        {
            foreach (var a in e.Attributes) if (!CanCarry(a.Value, Style.Context, depth)) return false;
            foreach (var k in e.KeyAttributes) if (!CanCarryDirect(k.Value, style, depth)) return false;
            foreach (var r in e.RelatedRecords) if (!Admit(r.Key) || !CanCarryElement(r.Value, style, depth)) return false;
            return true;
        }

        private bool CanCarryQuery(RecordQuery q, Style style, int depth) =>
            depth <= Limit
            && (q.Columns is null || CanCarryColumns(q.Columns))
            && (q.Paging is null || Admit(q.Paging))
            && q.Orders.All(Admit)
            && CanCarryFilter(q.Criteria, style, depth + Step)
            && q.Links.All(l => CanCarryLink(l, style, depth + Step))
            && (q.SubQuery is null || (Admit(q.SubQuery) && CanCarryQuery(q.SubQuery, style, depth + Step)));

        private bool CanCarryAttributeQuery(AttributeQuery q, Style style, int depth) =>
            (q.Columns is null || CanCarryColumns(q.Columns))
            && (q.Paging is null || Admit(q.Paging))
            && q.Orders.All(Admit)
            && q.Values.All(v => CanCarryDirect(v, style, depth + Step));

        private bool CanCarryColumns(ColumnSelection c) => Admit(c) && c.Expressions.All(Admit);

        private bool CanCarryLink(QueryLink l, Style style, int depth) =>
            depth <= Limit
            && Admit(l)
            && (l.Columns is null || CanCarryColumns(l.Columns))
            && l.Orders.All(Admit)
            && CanCarryFilter(l.Criteria, style, depth + Step)
            && l.Links.All(x => CanCarryLink(x, style, depth + Step));

        private bool CanCarryFilter(QueryFilter? f, Style style, int depth) =>
            f is null
            || (depth <= Limit
                && Admit(f)
                && f.Conditions.All(c => Admit(c) && c.Values.All(v => CanCarryDirect(v, style, depth + Step)))
                && f.Filters.All(x => CanCarryFilter(x, style, depth + Step))
                && (f.AnyAllLink is null || CanCarryLink(f.AnyAllLink, style, depth + Step)));

        private static bool IsExactArray(object value) => value.GetType() == typeof(byte[]) || value.GetType() == typeof(string[]);
    }
}
