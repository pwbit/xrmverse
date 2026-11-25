using System.Globalization;
using System.Runtime.Serialization;
using System.Xml;
using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using static Xrmd.Model.Serialization.ContractNames;

namespace Xrmd.Model.Serialization;

/// <summary>
/// Writes the contract XML of the remote context and of queries. The output is byte for byte what the SDK's data-contract
/// serializer writes: the same <see cref="XmlWriter"/> (escaping, newline handling, <c>&lt;x /&gt;</c> for an empty element), the
/// member order of the contracts, and the namespace prefixes the serializer picks: a prefix needed for a type hint, an array or
/// a dictionary entry is looked up in scope first and otherwise declared on the current element as <c>d{depth}p{n}</c>, where
/// depth counts the element nesting (the root is 1) and n counts the declarations on that element.
/// </summary>
internal sealed class ContractXmlWriter(XmlWriter xml, bool queryRoot)
{
    // True while writing under a query root and outside of any record: the query serializer does not know every collection type.
    private bool _queryStyle = queryRoot;

    private sealed class Frame
    {
        public readonly List<(string Namespace, string Prefix)> Declared = new();
        public int Count;
    }

    private readonly List<Frame> _frames = new();

    // ---- element and namespace mechanics ------------------------------------------------------------------------------

    /// <summary>Starts the root element in the default namespace, declares <c>i</c> and optionally writes the type hint.</summary>
    public void StartRoot(string name, string? typeHint)
    {
        xml.WriteStartElement(null, name, Contracts);
        var frame = new Frame();
        frame.Declared.Add((Contracts, ""));
        frame.Declared.Add((XmlSchemaInstance, "i"));
        _frames.Add(frame);
        xml.WriteAttributeString("xmlns", "i", null, XmlSchemaInstance);
        if (typeHint is not null) xml.WriteAttributeString("i", "type", XmlSchemaInstance, typeHint);
    }

    /// <summary>Starts an element in the default namespace, or in <paramref name="ns"/>, whose prefix must be in scope.</summary>
    public void Start(string name, string? ns = null)
    {
        if (ns is null || ns == Contracts) xml.WriteStartElement(null, name, Contracts);
        else xml.WriteStartElement(Lookup(ns) ?? throw new InvalidOperationException($"Namespace {ns} is not declared."), name, ns);
        _frames.Add(new Frame());
    }

    /// <summary>Starts an element that makes <paramref name="ns"/> the default namespace (an <c>xmlns</c> attribute).</summary>
    private void StartDefault(string name, string ns)
    {
        xml.WriteStartElement(null, name, ns);
        _frames.Add(new Frame());
    }

    public void End()
    {
        xml.WriteEndElement();
        _frames.RemoveAt(_frames.Count - 1);
    }

    private string? Lookup(string ns)
    {
        for (var i = _frames.Count - 1; i >= 0; i--)
        {
            var declared = _frames[i].Declared;
            for (var j = declared.Count - 1; j >= 0; j--)
                if (declared[j].Namespace == ns) return declared[j].Prefix;
        }
        return null;
    }

    /// <summary>The prefix of <paramref name="ns"/> in scope; declared on the current element when not yet.</summary>
    private string Declare(string ns)
    {
        if (Lookup(ns) is { } prefix) return prefix;
        var frame = _frames[^1];
        prefix = $"d{_frames.Count}p{++frame.Count}";
        frame.Declared.Add((ns, prefix));
        xml.WriteAttributeString("xmlns", prefix, null, ns);
        return prefix;
    }

    private void Nil() => xml.WriteAttributeString("i", "nil", XmlSchemaInstance, "true");

    private void TypeHint(string? ns, string name)
    {
        if (ns is null or Contracts) xml.WriteAttributeString("i", "type", XmlSchemaInstance, name);
        else xml.WriteAttributeString("i", "type", XmlSchemaInstance, Declare(ns) + ":" + name);
    }

    // ---- members ------------------------------------------------------------------------------------------------------

    private void Text(string name, string? text, string? ns = null)
    {
        Start(name, ns);
        if (text is null) Nil();
        else xml.WriteString(text);
        End();
    }

    private void Raw(string name, string text, string? ns = null)
    {
        Start(name, ns);
        xml.WriteString(text);
        End();
    }

    private void Bool(string name, bool value) => Raw(name, value ? "true" : "false");

    private void Int(string name, int value) => Raw(name, XmlConvert.ToString(value));

    private void NullableInt(string name, int? value)
    {
        if (value is { } v) Int(name, v);
        else { Start(name); Nil(); End(); }
    }

    private void GuidMember(string name, Guid value) => Raw(name, XmlConvert.ToString(value));

    private void NullableGuid(string name, Guid? value)
    {
        if (value is { } v) GuidMember(name, v);
        else { Start(name); Nil(); End(); }
    }

    private void EnumMember<T>(string name, T value) where T : struct, Enum
    {
        Raw(name, EnumName(value));
    }

    // ---- context ------------------------------------------------------------------------------------------------------

    public void Context(RemoteContext c, bool isRoot)
    {
        if (isRoot) StartRoot("RemoteExecutionContext", null);
        else Start("ParentContext");
        ContextBody(c);
        End();
    }

    private void ContextBody(RemoteContext c)
    {
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
        Raw("OperationCreatedOn", DateTimeText(c.OperationCreatedOn));
        GuidMember("OperationId", c.OperationId);
        GuidMember("OrganizationId", c.OrganizationId);
        Text("OrganizationName", c.OrganizationName);
        Parameters("OutputParameters", c.OutputParameters);
        if (c.OwningExtension is { } owner) Reference("OwningExtension", owner);
        else { Start("OwningExtension"); Nil(); End(); }
        if (c.ParentContext is { } parent) Context(parent, false);
        else { Start("ParentContext"); Nil(); End(); }
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
    }

    private void Parameters(string name, IEnumerable<KeyValuePair<string, object?>> bag)
    {
        Start(name);
        Declare(Generic);
        foreach (var (key, value) in bag) Entry(ParameterEntry, key, value);
        End();
    }

    private void Images(string name, IEnumerable<KeyValuePair<string, Record>> images)
    {
        Start(name);
        Declare(Generic);
        foreach (var (key, image) in images)
        {
            Start(ImageEntry);
            Text("key", key, Generic);
            Start("value", Generic);
            RecordBody(image);
            End();
            End();
        }
        End();
    }

    /// <summary>One dictionary entry whose value is an arbitrary model value (the value element carries the type hint).</summary>
    private void Entry(string entryName, string key, object? value, string? entryNs = null)
    {
        Start(entryName, entryNs);
        Text("key", key, Generic);
        Value("value", Generic, value);
        End();
    }

    // ---- values -------------------------------------------------------------------------------------------------------

    /// <summary>An element holding a polymorphic value: a type hint and a body, or <c>i:nil</c> for null.</summary>
    private void Value(string name, string? ns, object? value)
    {
        Start(name, ns);
        switch (value)
        {
            case null:
                Nil();
                break;
            case string s:
                TypeHint(XmlSchema, "string");
                xml.WriteString(s);
                break;
            case bool b:
                TypeHint(XmlSchema, "boolean");
                xml.WriteString(b ? "true" : "false");
                break;
            case int i:
                TypeHint(XmlSchema, "int");
                xml.WriteString(XmlConvert.ToString(i));
                break;
            case long l:
                TypeHint(XmlSchema, "long");
                xml.WriteString(XmlConvert.ToString(l));
                break;
            case double d:
                TypeHint(XmlSchema, "double");
                xml.WriteString(XmlConvert.ToString(d));
                break;
            case decimal m:
                TypeHint(XmlSchema, "decimal");
                xml.WriteString(XmlConvert.ToString(m));
                break;
            case Guid g:
                TypeHint(DataContractSerialization, "guid");
                xml.WriteString(XmlConvert.ToString(g));
                break;
            case DateTime t:
                TypeHint(XmlSchema, "dateTime");
                xml.WriteString(DateTimeText(t));
                break;
            case byte[] bytes:
                TypeHint(XmlSchema, "base64Binary");
                if (bytes.Length > 0) xml.WriteString(Convert.ToBase64String(bytes));
                break;
            case string[] strings:
                TypeHint(Arrays, "ArrayOfstring");
                foreach (var s in strings) Text("string", s, Arrays);
                break;
            case ChoiceValue c:
                TypeHint(null, OptionSetValueType);
                Int("Value", c.Value);
                break;
            case MoneyValue m:
                TypeHint(null, MoneyType);
                Raw("Value", XmlConvert.ToString(m.Value));
                break;
            case ChoiceValues c when _queryStyle:
                // The query serializer does not know the collection types: the hint carries the CLR namespace and the items
                // switch the default namespace (a context payload knows them, see below).
                TypeHint(ClrNamespace, "OptionSetValueCollection");
                foreach (var item in c)
                {
                    StartDefault("OptionSetValue", Choices);
                    StartDefault("Value", Contracts);
                    xml.WriteString(XmlConvert.ToString(item.Value));
                    End();
                    End();
                }
                break;
            case ChoiceValues c:
                TypeHint(Choices, "OptionSetValueCollection");
                foreach (var item in c) ChoiceItem(item);
                break;
            case RecordReference r:
                TypeHint(null, EntityReferenceType);
                ReferenceBody(r);
                break;
            case Record e:
                TypeHint(null, EntityType);
                RecordBody(e);
                break;
            case RecordCollection c:
                TypeHint(null, EntityCollectionType);
                CollectionBody(c);
                break;
            case RecordReferenceCollection c:
                TypeHint(_queryStyle ? ClrNamespace : null, EntityReferenceCollectionType);
                foreach (var r in c) Reference("EntityReference", r);
                break;
            case AliasedColumnValue a:
                TypeHint(null, AliasedValueType);
                Text("AttributeLogicalName", a.AttributeLogicalName);
                Text("EntityLogicalName", a.EntityLogicalName);
                Bool("NeedFormatting", false);
                Int("ReturnType", 0);
                Value("Value", null, a.Value);
                break;
            case ColumnSelection c:
                TypeHint(null, ColumnSetType);
                ColumnsBody(c);
                break;
            case RelationshipName r:
                TypeHint(null, RelationshipType);
                RelationshipBody(r);
                break;
            case RecordQuery q:
                TypeHint(null, QueryExpressionType);
                RecordQueryBody(q);
                break;
            case AttributeQuery q:
                TypeHint(null, QueryByAttributeType);
                AttributeQueryBody(q);
                break;
            case FetchQuery q:
                TypeHint(null, FetchExpressionType);
                Text("Query", q.Text);
                break;
            case Record[] a when a.GetType() == typeof(Record[]):
                TypeHint(null, ArrayOfEntityType);
                foreach (var e in a) { Start("Entity"); RecordBody(e); End(); }
                break;
            case RecordReference[] a when a.GetType() == typeof(RecordReference[]):
                TypeHint(null, ArrayOfEntityReferenceType);
                foreach (var r in a) Reference("EntityReference", r);
                break;
            case ChoiceValue[] a when a.GetType() == typeof(ChoiceValue[]):
                TypeHint(null, ArrayOfOptionSetValueType);
                foreach (var c in a) { Start("OptionSetValue"); Int("Value", c.Value); End(); }
                break;
            case MoneyValue[] a when a.GetType() == typeof(MoneyValue[]):
                TypeHint(null, ArrayOfMoneyType);
                foreach (var m in a) { Start("Money"); Raw("Value", XmlConvert.ToString(m.Value)); End(); }
                break;
            default:
                throw new NotSupportedException($"A value of type '{value.GetType().FullName}' is outside the model set of the contract XML.");
        }
        End();
    }

    private void ChoiceItem(ChoiceValue value)
    {
        Start("OptionSetValue", Choices);
        Int("Value", value.Value);
        End();
    }

    private void Reference(string name, RecordReference r)
    {
        Start(name);
        ReferenceBody(r);
        End();
    }

    private void ReferenceBody(RecordReference r)
    {
        GuidMember("Id", r.Id);
        KeyAttributes(r.KeyAttributes);
        Text("LogicalName", r.LogicalName);
        Text("Name", r.Name);
        Text("RowVersion", r.RowVersion);
    }

    private void KeyAttributes(IEnumerable<KeyValuePair<string, object?>> keys)
    {
        Start("KeyAttributes");
        Declare(ContractNames.KeyAttributes);
        Declare(Generic);
        foreach (var (key, value) in keys) Entry(ParameterEntry, key, value, ContractNames.KeyAttributes);
        End();
    }

    private void RecordBody(Record e)
    {
        Start("Attributes");
        Declare(Generic);
        // The attribute values of a record are written with the known types of the context, also under a query root (its key
        // attributes are not).
        var saved = _queryStyle;
        _queryStyle = false;
        foreach (var (key, value) in e.Attributes) Entry(ParameterEntry, key, value);
        _queryStyle = saved;
        End();
        Start("EntityState");
        if (e.State is { } state)
        {
            xml.WriteString(EnumName(state));
        }
        else Nil();
        End();
        Start("FormattedValues");
        Declare(Generic);
        foreach (var (key, value) in e.FormattedValues)
        {
            Start(FormattedValueEntry);
            Text("key", key, Generic);
            Text("value", value, Generic);
            End();
        }
        End();
        GuidMember("Id", e.Id);
        KeyAttributes(e.KeyAttributes);
        Text("LogicalName", e.LogicalName);
        Start("RelatedEntities");
        Declare(Generic);
        foreach (var (relationship, collection) in e.RelatedRecords)
        {
            Start(RelatedEntitiesEntry);
            Start("key", Generic);
            RelationshipBody(relationship);
            End();
            Start("value", Generic);
            CollectionBody(collection);
            End();
            End();
        }
        End();
        Text("RowVersion", e.RowVersion);
    }

    private void CollectionBody(RecordCollection c)
    {
        Start("Entities");
        foreach (var e in c.Records) { Start("Entity"); RecordBody(e); End(); }
        End();
        Text("EntityName", c.EntityName);
        Text("MinActiveRowVersion", c.MinActiveRowVersion);
        Bool("MoreRecords", c.MoreRecords);
        Text("PagingCookie", c.PagingCookie);
        Int("RecordCountLessThanMarv", c.RecordCountLessThanMarv);
        Int("TotalRecordCount", c.TotalRecordCount);
        Bool("TotalRecordCountLimitExceeded", c.TotalRecordCountLimitExceeded);
    }

    private void RelationshipBody(RelationshipName r)
    {
        Start("PrimaryEntityRole");
        if (r.PrimaryEntityRole is { } role)
        {
            xml.WriteString(EnumName(role));
        }
        else Nil();
        End();
        Text("SchemaName", r.SchemaName);
    }

    // ---- queries ------------------------------------------------------------------------------------------------------

    public void Query(QueryDefinition query)
    {
        switch (query)
        {
            case RecordQuery q:
                StartRoot("QueryBase", QueryExpressionType);
                RecordQueryBody(q);
                break;
            case AttributeQuery q:
                StartRoot("QueryBase", QueryByAttributeType);
                AttributeQueryBody(q);
                break;
            case FetchQuery q:
                StartRoot("QueryBase", FetchExpressionType);
                Text("Query", q.Text);
                break;
            default:
                throw new NotSupportedException($"A query of type '{query.GetType().FullName}' is outside the model set of the contract XML.");
        }
        End();
    }

    private void RecordQueryBody(RecordQuery q)
    {
        Columns("ColumnSet", q.Columns);
        Filter("Criteria", q.Criteria);
        Bool("Distinct", q.Distinct);
        Text("EntityName", q.EntityName);
        if (q.ForceSeek is not null) Text("ForceSeek", q.ForceSeek);
        Start("LinkEntities");
        foreach (var l in q.Links) Link("LinkEntity", l);
        End();
        Orders(q.Orders);
        Paging(q.Paging);
        if (q.SubQuery is { } sub) { Start("SubQueryExpression"); RecordQueryBody(sub); End(); }
        Bool("NoLock", q.NoLock);
        if (q.TopCount is { } top) Int("TopCount", top);
        if (q.QueryHints is not null) Text("QueryHints", q.QueryHints);
        if (q.DataSource is not null) Text("DataSource", q.DataSource);
    }

    private void AttributeQueryBody(AttributeQuery q)
    {
        Start("Attributes");
        Declare(Arrays);
        foreach (var a in q.Attributes) Text("string", a, Arrays);
        End();
        Columns("ColumnSet", q.Columns);
        Text("EntityName", q.EntityName);
        Orders(q.Orders);
        Paging(q.Paging);
        Start("Values");
        Declare(Arrays);
        foreach (var v in q.Values) Value("anyType", Arrays, v);
        End();
        NullableInt("TopCount", q.TopCount);
    }

    private void Columns(string name, ColumnSelection? c)
    {
        Start(name);
        if (c is null) Nil();
        else ColumnsBody(c);
        End();
    }

    private void ColumnsBody(ColumnSelection c)
    {
        Bool("AllColumns", c.AllColumns);
        Start("AttributeExpressions");
        foreach (var e in c.Expressions)
        {
            Start("AttributeExpression");
            EnumMember("AggregateType", e.Aggregate);
            Text("Alias", e.Alias);
            Text("AttributeName", e.AttributeName);
            EnumMember("DateTimeGrouping", e.DateGrouping);
            Bool("HasGroupBy", e.GroupBy);
            End();
        }
        End();
        Start("Columns");
        Declare(Arrays);
        foreach (var column in c.Columns) Text("string", column, Arrays);
        End();
    }

    private void Filter(string name, QueryFilter? f)
    {
        Start(name);
        if (f is null) Nil();
        else FilterBody(f);
        End();
    }

    private void FilterBody(QueryFilter f)
    {
        if (f.AnyAllLink is { } link) Link("AnyAllFilterLinkEntity", link);
        else { Start("AnyAllFilterLinkEntity"); Nil(); End(); }
        Start("Conditions");
        foreach (var c in f.Conditions)
        {
            Start("ConditionExpression");
            Text("AttributeName", c.AttributeName);
            Bool("CompareColumns", c.CompareColumns);
            EnumMember("Operator", c.Operator);
            Start("Values");
            Declare(Arrays);
            foreach (var v in c.Values) Value("anyType", Arrays, v);
            End();
            Text("EntityName", c.EntityName);
            End();
        }
        End();
        EnumMember("FilterOperator", f.Logic);
        Start("Filters");
        foreach (var child in f.Filters) Filter("FilterExpression", child);
        End();
        if (f.IsQuickFindFilter) Bool("IsQuickFindFilter", true);
        if (f.FilterHint is not null) Text("FilterHint", f.FilterHint);
    }

    private void Link(string name, QueryLink l)
    {
        Start(name);
        Columns("Columns", l.Columns);
        Text("EntityAlias", l.Alias);
        if (l.ForceSeek is not null) Text("ForceSeek", l.ForceSeek);
        EnumMember("JoinOperator", l.Join);
        Filter("LinkCriteria", l.Criteria);
        Start("LinkEntities");
        foreach (var child in l.Links) Link("LinkEntity", child);
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
        Start("Orders");
        foreach (var o in orders)
        {
            Start("OrderExpression");
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
        Start("PageInfo");
        if (p is null) Nil();
        else
        {
            Int("Count", p.Count);
            Int("PageNumber", p.PageNumber);
            Text("PagingCookie", p.PagingCookie);
            Bool("ReturnRecordCountLessThanMarv", p.ReturnRecordCountLessThanMarv);
            Bool("ReturnTotalRecordCount", p.ReturnTotalRecordCount);
        }
        End();
    }

    // ---- primitives ---------------------------------------------------------------------------------------------------

    /// <summary>The member name of an enum value; a value without a name is refused like the SDK serializer refuses it.</summary>
    /// <exception cref="SerializationException">The value is not a member of the enum.</exception>
    private static string EnumName<T>(T value) where T : struct, Enum =>
        Enum.IsDefined(value)
            ? value.ToString()
            // Text kept word for word from the .NET runtime's data-contract serializer (MIT): the tests compare it with the SDK path's message.
            : throw new SerializationException($"Enum value '{Convert.ToInt64(value, CultureInfo.InvariantCulture)}' is invalid for type '{typeof(T).FullName}' and cannot be serialized.");

    private static string DateTimeText(DateTime value) => XmlConvert.ToString(value, XmlDateTimeSerializationMode.RoundtripKind);
}
