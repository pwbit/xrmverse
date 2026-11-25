using System.Globalization;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using static Xrmd.Model.Serialization.ContractNames;

namespace Xrmd.Model.Serialization;

/// <summary>
/// The webhook JSON of a <see cref="RemoteContext"/>: byte for byte what the SDK's <c>DataContractJsonSerializer</c> writes for a
/// <c>RemoteExecutionContext</c> with the known types of the webhook (a complex value carries its
/// <c>"__type": "Name:Namespace"</c> as the first member, a date is <c>/Date(ms)/</c> or <c>/Date(ms±hhmm)/</c>, a dictionary is an
/// array of <c>key</c>/<c>value</c> objects, an enum is its number, <c>byte[]</c> is an array of numbers). The same
/// JSON-infoset writer (<see cref="JsonReaderWriterFactory"/>, a BCL type) does the escaping and the punctuation.
/// </summary>
public static class ContextJson
{
    /// <summary>The JSON of <paramref name="context"/>: UTF-8 without a byte order mark.</summary>
    /// <exception cref="NotSupportedException">A value is outside the model set (the twins never pass one).</exception>
    public static byte[] Write(RemoteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var stream = new MemoryStream();
        using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, new UTF8Encoding(false), ownsStream: false))
        {
            new JsonContractWriter(writer).Context(context, "root");
            writer.Flush();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// A shallow copy of <paramref name="context"/> without <c>ParentContext</c>, <c>InputParameters</c>, <c>PreEntityImages</c> and
    /// <c>PostEntityImages</c> (the four are empty or null, as today's trimmed body): every other member keeps its value, the
    /// output parameters and shared variables are copied entry by entry. <paramref name="context"/> is not changed.
    /// </summary>
    public static RemoteContext Trim(RemoteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var copy = new RemoteContext
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
            OwningExtension = context.OwningExtension,
            PrimaryEntityId = context.PrimaryEntityId,
            PrimaryEntityName = context.PrimaryEntityName,
            RequestId = context.RequestId,
            SecondaryEntityName = context.SecondaryEntityName,
            Stage = context.Stage,
            UserAzureActiveDirectoryObjectId = context.UserAzureActiveDirectoryObjectId,
            UserId = context.UserId,
        };
        foreach (var (key, value) in context.OutputParameters) copy.OutputParameters[key] = value;
        foreach (var (key, value) in context.SharedVariables) copy.SharedVariables[key] = value;
        return copy;
    }
}

/// <summary>Writes the model through the JSON-infoset writer (members are elements, <c>type</c> attributes give the JSON type).</summary>
internal sealed class JsonContractWriter(XmlDictionaryWriter json)
{
    // ---- JSON infoset mechanics -----------------------------------------------------------------------------------------

    private void Start(string name, string? type = null, string? hint = null)
    {
        json.WriteStartElement(name);
        if (type is not null) json.WriteAttributeString("type", type);
        if (hint is not null) json.WriteAttributeString("__type", hint + ":" + Contracts);
    }

    private void Object(string name, string? hint = null) => Start(name, "object", hint);

    private void Array(string name) => Start(name, "array");

    private void End() => json.WriteEndElement();

    private void Null(string name)
    {
        Start(name, "null");
        End();
    }

    private void Text(string name, string? text)
    {
        if (text is null) Null(name);
        else
        {
            Start(name);
            json.WriteString(text);
            End();
        }
    }

    private void Number(string name, string text)
    {
        Start(name, "number");
        json.WriteString(text);
        End();
    }

    private void Bool(string name, bool value)
    {
        Start(name, "boolean");
        json.WriteString(value ? "true" : "false");
        End();
    }

    private void Int(string name, int value) => Number(name, value.ToString(CultureInfo.InvariantCulture));

    private void NullableInt(string name, int? value)
    {
        if (value is { } v) Int(name, v);
        else Null(name);
    }

    private void GuidMember(string name, Guid value) => Text(name, value.ToString("D", CultureInfo.InvariantCulture));

    private void NullableGuid(string name, Guid? value)
    {
        if (value is { } v) GuidMember(name, v);
        else Null(name);
    }

    private void EnumMember<T>(string name, T value) where T : struct, Enum => Int(name, Convert.ToInt32(value, CultureInfo.InvariantCulture));

    // ---- values -----------------------------------------------------------------------------------------------------

    private static string DoubleText(double d) =>
        double.IsNaN(d) ? "NaN"
        : double.IsPositiveInfinity(d) ? "INF"
        : double.IsNegativeInfinity(d) ? "-INF"
        : d.ToString("R", CultureInfo.InvariantCulture);

    private const long UnixEpochTicks = 621355968000000000L;

    /// <summary>
    /// <c>/Date(ms)/</c> for a UTC value and <c>/Date(ms±hhmm)/</c> for a local or unspecified one. The rule is applied to the
    /// original value (not to a copy rebuilt from its ticks: a local value keeps the BCL's ambiguous-daylight flag, which a copy
    /// loses): <c>ms</c> is <c>value.ToUniversalTime()</c> counted from 1970-01-01 UTC, truncated toward zero; the suffix is the
    /// zone offset at <c>value.ToLocalTime()</c> (an unspecified value reads as UTC there), the sign following the hours part
    /// (-00:43 reads <c>+0043</c>), the minutes absolute. A value out of range once converted to UTC is refused.
    /// </summary>
    /// <exception cref="SerializationException">The value is out of range once converted to UTC.</exception>
    private static string DateText(DateTime value)
    {
        var suffix = "";
        long ticks = value.Ticks;
        if (value.Kind != DateTimeKind.Utc)
        {
            var utcTicks = value.Ticks - TimeZoneInfo.Local.GetUtcOffset(value).Ticks;
            if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
                // Text kept word for word from the .NET runtime's JSON serializer (MIT): the tests compare it with the SDK path's message.
                throw new SerializationException(
                    "DateTime values that are greater than DateTime.MaxValue or smaller than DateTime.MinValue when converted to UTC cannot be serialized to JSON.",
                    new ArgumentOutOfRangeException("value"));
            ticks = value.ToUniversalTime().Ticks;
            var shown = TimeZoneInfo.Local.GetUtcOffset(value.ToLocalTime());
            suffix = (shown.Hours < 0 ? "-" : "+") + Math.Abs(shown.Hours).ToString("00", CultureInfo.InvariantCulture) +
                     Math.Abs(shown.Minutes).ToString("00", CultureInfo.InvariantCulture);
        }
        var ms = (ticks - UnixEpochTicks) / TimeSpan.TicksPerMillisecond;
        return "/Date(" + ms.ToString(CultureInfo.InvariantCulture) + suffix + ")/";
    }

    public void Context(RemoteContext c, string name)
    {
        Object(name);
        GuidMember("BusinessUnitId", c.BusinessUnitId);
        GuidMember("CorrelationId", c.CorrelationId);
        Int("Depth", c.Depth);
        Text("InitiatingUserAgent", c.InitiatingUserAgent);
        GuidMember("InitiatingUserAzureActiveDirectoryObjectId", c.InitiatingUserAzureActiveDirectoryObjectId);
        GuidMember("InitiatingUserId", c.InitiatingUserId);
        Parameters("InputParameters", c.InputParameters);
        Bool("IsExecutingOffline", c.IsExecutingOffline);
        Bool("IsInTransaction", c.IsInTransaction);
        Bool("IsOfflinePlayback", c.IsOfflinePlayback);
        Int("IsolationMode", c.IsolationMode);
        Text("MessageName", c.MessageName);
        Int("Mode", c.Mode);
        Text("OperationCreatedOn", DateText(c.OperationCreatedOn));
        GuidMember("OperationId", c.OperationId);
        GuidMember("OrganizationId", c.OrganizationId);
        Text("OrganizationName", c.OrganizationName);
        Parameters("OutputParameters", c.OutputParameters);
        if (c.OwningExtension is { } owner) Reference("OwningExtension", owner, null);
        else Null("OwningExtension");
        if (c.ParentContext is { } parent) Context(parent, "ParentContext");
        else Null("ParentContext");
        Images("PostEntityImages", c.PostEntityImages);
        Images("PreEntityImages", c.PreEntityImages);
        GuidMember("PrimaryEntityId", c.PrimaryEntityId);
        Text("PrimaryEntityName", c.PrimaryEntityName);
        NullableGuid("RequestId", c.RequestId);
        Text("SecondaryEntityName", c.SecondaryEntityName);
        Parameters("SharedVariables", c.SharedVariables);
        Int("Stage", c.Stage);
        GuidMember("UserAzureActiveDirectoryObjectId", c.UserAzureActiveDirectoryObjectId);
        GuidMember("UserId", c.UserId);
        End();
    }

    private void Parameters(string name, IEnumerable<KeyValuePair<string, object?>> bag)
    {
        Array(name);
        foreach (var (key, value) in bag) Entry(key, value);
        End();
    }

    private void Entry(string key, object? value)
    {
        Object("item");
        Text("key", key);
        Value("value", value);
        End();
    }

    private void Images(string name, IEnumerable<KeyValuePair<string, Record>> images)
    {
        Array(name);
        foreach (var (key, image) in images)
        {
            Object("item");
            Text("key", key);
            Record("value", image, null);
            End();
        }
        End();
    }

    /// <summary>An element holding a polymorphic value: its JSON form and, for a complex type, the <c>__type</c> hint.</summary>
    private void Value(string name, object? value)
    {
        switch (value)
        {
            case null: Null(name); break;
            case string s: Text(name, s); break;
            case bool b: Bool(name, b); break;
            case int i: Int(name, i); break;
            case long l: Number(name, l.ToString(CultureInfo.InvariantCulture)); break;
            case double d: Number(name, DoubleText(d)); break;
            case decimal m: Number(name, m.ToString(CultureInfo.InvariantCulture)); break;
            case Guid g: GuidMember(name, g); break;
            case DateTime t: Text(name, DateText(t)); break;
            case byte[] bytes:
                Array(name);
                foreach (var b in bytes) Int("item", b);
                End();
                break;
            case string[] strings:
                Array(name);
                foreach (var s in strings) Text("item", s);
                End();
                break;
            case ChoiceValue c: Choice(name, c, OptionSetValueType); break;
            case MoneyValue m: Money(name, m, MoneyType); break;
            case ChoiceValues c:
                Array(name);
                foreach (var item in c) Choice("item", item, OptionSetValueType);
                End();
                break;
            case RecordReference r: Reference(name, r, EntityReferenceType); break;
            case Record e: Record(name, e, EntityType); break;
            case RecordCollection c: Collection(name, c, EntityCollectionType); break;
            case RecordReferenceCollection c:
                Array(name);
                foreach (var r in c) Reference("item", r, EntityReferenceType);
                End();
                break;
            case AliasedColumnValue a:
                Object(name, AliasedValueType);
                Text("AttributeLogicalName", a.AttributeLogicalName);
                Text("EntityLogicalName", a.EntityLogicalName);
                Bool("NeedFormatting", false);
                Int("ReturnType", 0);
                Value("Value", a.Value);
                End();
                break;
            case ColumnSelection c:
                Object(name, ColumnSetType);
                ColumnsBody(c);
                End();
                break;
            case RelationshipName r:
                Object(name, RelationshipType);
                RelationshipBody(r);
                End();
                break;
            case RecordQuery q:
                Object(name, QueryExpressionType);
                RecordQueryBody(q);
                End();
                break;
            case AttributeQuery q:
                Object(name, QueryByAttributeType);
                AttributeQueryBody(q);
                End();
                break;
            case FetchQuery q:
                Object(name, FetchExpressionType);
                Text("Query", q.Text);
                End();
                break;
            case Record[] a when a.GetType() == typeof(Record[]):
                Array(name);
                foreach (var e in a) Record("item", e, EntityType);
                End();
                break;
            case RecordReference[] a when a.GetType() == typeof(RecordReference[]):
                Array(name);
                foreach (var r in a) Reference("item", r, EntityReferenceType);
                End();
                break;
            case ChoiceValue[] a when a.GetType() == typeof(ChoiceValue[]):
                Array(name);
                foreach (var c in a) Choice("item", c, OptionSetValueType);
                End();
                break;
            case MoneyValue[] a when a.GetType() == typeof(MoneyValue[]):
                Array(name);
                foreach (var m in a) Money("item", m, MoneyType);
                End();
                break;
            default:
                throw new NotSupportedException($"A value of type '{value.GetType().FullName}' is outside the model set of the webhook JSON.");
        }
    }

    private void Choice(string name, ChoiceValue value, string? hint)
    {
        Object(name, hint);
        Int("Value", value.Value);
        End();
    }

    private void Money(string name, MoneyValue value, string? hint)
    {
        Object(name, hint);
        Number("Value", value.Value.ToString(CultureInfo.InvariantCulture));
        End();
    }

    private void Reference(string name, RecordReference r, string? hint)
    {
        Object(name, hint);
        GuidMember("Id", r.Id);
        Parameters("KeyAttributes", r.KeyAttributes);
        Text("LogicalName", r.LogicalName);
        Text("Name", r.Name);
        Text("RowVersion", r.RowVersion);
        End();
    }

    private void Record(string name, Record e, string? hint)
    {
        Object(name, hint);
        Parameters("Attributes", e.Attributes);
        if (e.State is { } state) EnumMember("EntityState", state);
        else Null("EntityState");
        Array("FormattedValues");
        foreach (var (key, value) in e.FormattedValues)
        {
            Object("item");
            Text("key", key);
            Text("value", value);
            End();
        }
        End();
        GuidMember("Id", e.Id);
        Parameters("KeyAttributes", e.KeyAttributes);
        Text("LogicalName", e.LogicalName);
        Array("RelatedEntities");
        foreach (var (relationship, collection) in e.RelatedRecords)
        {
            Object("item");
            Object("key");
            RelationshipBody(relationship);
            End();
            Collection("value", collection, null);
            End();
        }
        End();
        Text("RowVersion", e.RowVersion);
        End();
    }

    private void Collection(string name, RecordCollection c, string? hint)
    {
        Object(name, hint);
        Array("Entities");
        foreach (var e in c.Records) Record("item", e, null);
        End();
        Text("EntityName", c.EntityName);
        Text("MinActiveRowVersion", c.MinActiveRowVersion);
        Bool("MoreRecords", c.MoreRecords);
        Text("PagingCookie", c.PagingCookie);
        Int("RecordCountLessThanMarv", c.RecordCountLessThanMarv);
        Int("TotalRecordCount", c.TotalRecordCount);
        Bool("TotalRecordCountLimitExceeded", c.TotalRecordCountLimitExceeded);
        End();
    }

    private void RelationshipBody(RelationshipName r)
    {
        if (r.PrimaryEntityRole is { } role) EnumMember("PrimaryEntityRole", role);
        else Null("PrimaryEntityRole");
        Text("SchemaName", r.SchemaName);
    }

    // ---- queries (values of a parameter) ----------------------------------------------------------------------------

    private void RecordQueryBody(RecordQuery q)
    {
        Columns("ColumnSet", q.Columns);
        Filter("Criteria", q.Criteria);
        Bool("Distinct", q.Distinct);
        Text("EntityName", q.EntityName);
        if (q.ForceSeek is not null) Text("ForceSeek", q.ForceSeek);
        Array("LinkEntities");
        foreach (var l in q.Links) Link("item", l);
        End();
        Orders(q.Orders);
        Paging(q.Paging);
        if (q.SubQuery is { } sub)
        {
            Object("SubQueryExpression");
            RecordQueryBody(sub);
            End();
        }
        Bool("NoLock", q.NoLock);
        if (q.TopCount is { } top) Int("TopCount", top);
        if (q.QueryHints is not null) Text("QueryHints", q.QueryHints);
        if (q.DataSource is not null) Text("DataSource", q.DataSource);
    }

    private void AttributeQueryBody(AttributeQuery q)
    {
        Array("Attributes");
        foreach (var a in q.Attributes) Text("item", a);
        End();
        Columns("ColumnSet", q.Columns);
        Text("EntityName", q.EntityName);
        Orders(q.Orders);
        Paging(q.Paging);
        Array("Values");
        foreach (var v in q.Values) Value("item", v);
        End();
        NullableInt("TopCount", q.TopCount);
    }

    private void Columns(string name, ColumnSelection? c)
    {
        if (c is null) { Null(name); return; }
        Object(name);
        ColumnsBody(c);
        End();
    }

    private void ColumnsBody(ColumnSelection c)
    {
        Bool("AllColumns", c.AllColumns);
        Array("AttributeExpressions");
        foreach (var e in c.Expressions)
        {
            Object("item");
            EnumMember("AggregateType", e.Aggregate);
            Text("Alias", e.Alias);
            Text("AttributeName", e.AttributeName);
            EnumMember("DateTimeGrouping", e.DateGrouping);
            Bool("HasGroupBy", e.GroupBy);
            End();
        }
        End();
        Array("Columns");
        foreach (var column in c.Columns) Text("item", column);
        End();
    }

    private void Filter(string name, QueryFilter? f)
    {
        if (f is null) { Null(name); return; }
        Object(name);
        if (f.AnyAllLink is { } link) Link("AnyAllFilterLinkEntity", link);
        else Null("AnyAllFilterLinkEntity");
        Array("Conditions");
        foreach (var c in f.Conditions)
        {
            Object("item");
            Text("AttributeName", c.AttributeName);
            Bool("CompareColumns", c.CompareColumns);
            EnumMember("Operator", c.Operator);
            Array("Values");
            foreach (var v in c.Values) Value("item", v);
            End();
            Text("EntityName", c.EntityName);
            End();
        }
        End();
        EnumMember("FilterOperator", f.Logic);
        Array("Filters");
        foreach (var child in f.Filters) Filter("item", child);
        End();
        if (f.IsQuickFindFilter) Bool("IsQuickFindFilter", true);
        if (f.FilterHint is not null) Text("FilterHint", f.FilterHint);
        End();
    }

    private void Link(string name, QueryLink l)
    {
        Object(name);
        Columns("Columns", l.Columns);
        Text("EntityAlias", l.Alias);
        if (l.ForceSeek is not null) Text("ForceSeek", l.ForceSeek);
        EnumMember("JoinOperator", l.Join);
        Filter("LinkCriteria", l.Criteria);
        Array("LinkEntities");
        foreach (var child in l.Links) Link("item", child);
        End();
        Text("LinkFromAttributeName", l.FromAttributeName);
        Text("LinkFromEntityName", l.FromEntityName);
        Text("LinkToAttributeName", l.ToAttributeName);
        Text("LinkToEntityName", l.ToEntityName);
        Orders(l.Orders);
        End();
    }

    private void Orders(IEnumerable<QueryOrder> orders)
    {
        Array("Orders");
        foreach (var o in orders)
        {
            Object("item");
            Text("Alias", o.Alias);
            Text("AttributeName", o.AttributeName);
            Text("EntityName", o.EntityName);
            EnumMember("OrderType", o.Direction);
            End();
        }
        End();
    }

    private void Paging(QueryPaging? p)
    {
        if (p is null) { Null("PageInfo"); return; }
        Object("PageInfo");
        Int("Count", p.Count);
        Int("PageNumber", p.PageNumber);
        Text("PagingCookie", p.PagingCookie);
        Bool("ReturnRecordCountLessThanMarv", p.ReturnRecordCountLessThanMarv);
        Bool("ReturnTotalRecordCount", p.ReturnTotalRecordCount);
        End();
    }
}
