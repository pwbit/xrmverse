using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Record = Xrmd.Model.Records.Record;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>
/// S.2e2c Task 5: a plug-in may enumerate an SDK container of its context while it calls its organization service; around that
/// call the executor reads its view back and writes it again (<c>In</c>, then <c>Out</c>). Today the containers are not touched,
/// so such an enumeration goes on; an unchanged container must therefore be written without invalidating its enumerators.
/// </summary>
public class SdkMirrorEnumerationTests
{
    /// <summary>Enumerates <paramref name="items"/> and runs <paramref name="crossing"/> after the first item.</summary>
    private static void EnumerateAcross<T>(IEnumerable<T> items, Action crossing)
    {
        var first = true;
        foreach (var _ in items)
        {
            if (!first) continue;
            first = false;
            crossing();
        }
    }

    [Fact]
    public void Unchanged_containers_keep_their_enumerators_across_In_then_Out()
    {
        var mirror = new SdkMirror();
        var inner = new Entity("contact", Guid.NewGuid()) { ["name"] = "c" };
        var target = new Entity("account", Guid.NewGuid())
        {
            ["name"] = "a", ["code"] = new OptionSetValue(1), ["parent"] = new EntityReference("account", Guid.NewGuid()),
        };
        target.FormattedValues["code"] = "One";
        target.KeyAttributes["k"] = "v";
        var rows = new EntityCollection([inner, new Entity("contact", Guid.NewGuid())]);
        var query = new QueryExpression("account") { ColumnSet = new ColumnSet("name", "code") };
        query.Criteria.AddCondition("name", ConditionOperator.Equal, "a");
        query.Criteria.AddCondition("code", ConditionOperator.In, 1, 2);
        query.AddOrder("name", OrderType.Ascending);
        query.AddLink("contact", "accountid", "parentcustomerid");
        var parameters = new ParameterCollection
        {
            ["Target"] = target, ["Rows"] = rows, ["Query"] = query,
            ["Refs"] = new EntityReferenceCollection([new EntityReference("account", Guid.NewGuid())]),
            ["Codes"] = new OptionSetValueCollection([new OptionSetValue(1), new OptionSetValue(2)]),
        };
        var bag = mirror.In(parameters)!;

        void RoundTrip()
        {
            mirror.In(parameters);
            mirror.Out(bag);
        }

        EnumerateAcross(parameters, RoundTrip);
        EnumerateAcross(target.Attributes, RoundTrip);
        EnumerateAcross(target.FormattedValues, RoundTrip);
        EnumerateAcross(target.KeyAttributes, RoundTrip);
        EnumerateAcross(rows.Entities, RoundTrip);
        EnumerateAcross((EntityReferenceCollection)parameters["Refs"], RoundTrip);
        EnumerateAcross((OptionSetValueCollection)parameters["Codes"], RoundTrip);
        EnumerateAcross(query.ColumnSet.Columns, RoundTrip);
        EnumerateAcross(query.Criteria.Conditions, RoundTrip);
        EnumerateAcross(query.Criteria.Conditions[1].Values, RoundTrip);
        EnumerateAcross(query.Orders, RoundTrip);
        EnumerateAcross(query.LinkEntities, RoundTrip);
        Assert.Same(target, parameters["Target"]);
        Assert.Same(inner, rows.Entities[0]);
    }

    [Fact]
    public void A_changed_container_is_written_in_source_order()
    {
        var mirror = new SdkMirror();
        var target = new Entity("account") { ["a"] = 1, ["b"] = 2 };
        var record = (Record)mirror.In(target)!;

        record.Attributes.Remove("a");
        record["c"] = 3;
        mirror.Out(record);
        Assert.Equal(record.Attributes.Keys, target.Attributes.Keys);
        Assert.False(target.Contains("a"));

        record["b"] = 5; // same keys: the value is set in place
        mirror.Out(record);
        Assert.Equal(record.Attributes.Keys, target.Attributes.Keys);
        Assert.Equal(5, target["b"]);

        var rows = new EntityCollection([new Entity("a"), new Entity("b")]);
        var collection = (Xrmd.Model.Records.RecordCollection)mirror.In(rows)!;
        (collection.Records[0], collection.Records[1]) = (collection.Records[1], collection.Records[0]);
        mirror.Out(collection);
        Assert.Equal(["b", "a"], rows.Entities.Select(e => e.LogicalName));
    }
}
