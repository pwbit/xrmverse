using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Records;

namespace Xrmd.Pipeline.Tests.Sdk;

public class MessageConversionTests
{
    [Fact]
    public void Typed_RetrieveMultipleRequest_converts_and_query_passes_through()
    {
        var query = new QueryExpression("account") { ColumnSet = new ColumnSet("name"), TopCount = 5 };
        var sdk = new RetrieveMultipleRequest { Query = query, RequestId = Guid.NewGuid() };

        var model = sdk.ToModel();
        Assert.Equal("RetrieveMultiple", model.Name);
        Assert.Same(query, model["Query"]);
        Assert.Equal(sdk.RequestId, model.RequestId);

        var back = model.ToSdk();
        Assert.Equal(typeof(OrganizationRequest), back.GetType());
        Assert.Equal(sdk.RequestName, back.RequestName);
        Assert.Equal(sdk.RequestId, back.RequestId);
        Assert.Same(query, back["Query"]);
        DeepEqual.Assert(sdk.Parameters, back.Parameters);
    }

    [Fact]
    public void Typed_UpsertRequest_with_keyed_target_converts()
    {
        var target = new Entity("account", new KeyAttributeCollection { { "accountnumber", "A-1" } }) { ["name"] = "x", ["choice"] = new OptionSetValue(2) };
        var sdk = new UpsertRequest { Target = target };

        var model = sdk.ToModel();
        Assert.Equal("Upsert", model.Name);
        var record = Assert.IsType<Xrmd.Model.Records.Record>(model["Target"]);
        Assert.Equal("A-1", record.KeyAttributes["accountnumber"]);
        Assert.Equal(new ChoiceValue(2), record["choice"]);

        var back = model.ToSdk();
        Assert.Equal(sdk.RequestName, back.RequestName);
        Assert.Equal(sdk.Parameters.Keys.OrderBy(k => k), back.Parameters.Keys.OrderBy(k => k));
        foreach (var key in sdk.Parameters.Keys) DeepEqual.Assert(sdk.Parameters[key], back.Parameters[key]);
    }

    [Fact]
    public void Plain_request_without_id_round_trips()
    {
        var sdk = new OrganizationRequest("new_Custom") { ["a"] = 1, ["b"] = new Money(2m), ["c"] = null };
        var model = sdk.ToModel();
        var back = model.ToSdk();
        DeepEqual.Assert(sdk, back);
    }

    [Fact]
    public void Response_with_EntityCollection_round_trips()
    {
        var results = new EntityCollection(new List<Entity> { new Entity("account", Guid.NewGuid()) { ["name"] = "x" } }) { EntityName = "account", MoreRecords = true, PagingCookie = "c" };
        var sdk = new OrganizationResponse { ResponseName = "RetrieveMultiple" };
        sdk.Results["EntityCollection"] = results;
        sdk.Results["Count"] = 3;

        var model = sdk.ToModel();
        Assert.Equal("RetrieveMultiple", model.Name);
        Assert.IsType<RecordCollection>(model["EntityCollection"]);

        DeepEqual.Assert(sdk, model.ToSdk());
    }

    [Fact]
    public void Typed_response_converts_to_base_response()
    {
        var sdk = new RetrieveMultipleResponse { ResponseName = "RetrieveMultiple" };
        sdk.Results["EntityCollection"] = new EntityCollection();
        var model = sdk.ToModel();
        Assert.Equal("RetrieveMultiple", model.Name);
        Assert.Equal(sdk.Results.Keys, model.Results.Keys);
    }

    [Fact]
    public void RetrieveRequest_ColumnSet_is_converted_and_round_trips()
    {
        var sdk = new RetrieveRequest { Target = new EntityReference("account", Guid.NewGuid()), ColumnSet = new ColumnSet("name", "revenue") };
        var model = sdk.ToModel();
        var cols = Assert.IsType<ColumnSelection>(model["ColumnSet"]);
        Assert.Equal(new[] { "name", "revenue" }, cols.Columns);
        var back = model.ToSdk();
        Assert.Equal(sdk.RequestName, back.RequestName);
        foreach (var key in sdk.Parameters.Keys) DeepEqual.Assert(sdk.Parameters[key], back.Parameters[key]);
    }

    [Fact]
    public void AssociateRequest_Relationship_and_references_round_trip()
    {
        var sdk = new AssociateRequest
        {
            Target = new EntityReference("account", Guid.NewGuid()),
            Relationship = new Relationship("a_b") { PrimaryEntityRole = EntityRole.Referenced },
            RelatedEntities = new EntityReferenceCollection { new EntityReference("contact", Guid.NewGuid()) },
        };
        var model = sdk.ToModel();
        Assert.Equal(new RelationshipName("a_b", RecordRole.Referenced), model["Relationship"]);
        Assert.IsType<RecordReferenceCollection>(model["RelatedEntities"]);
        var back = model.ToSdk();
        Assert.Equal(sdk.RequestName, back.RequestName);
        foreach (var key in sdk.Parameters.Keys) DeepEqual.Assert(sdk.Parameters[key], back.Parameters[key]);
    }
}
