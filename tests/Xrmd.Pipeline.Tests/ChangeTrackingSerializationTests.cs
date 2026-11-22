using System.Runtime.Serialization;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Pipeline.Serialization;

namespace Xrmd.Pipeline.Tests;

/// <summary>Plan 2.13: the RetrieveEntityChanges response goes through the restricted data contracts (SOAP, async payloads).</summary>
public class ChangeTrackingSerializationTests
{
    [Theory]
    [InlineData(typeof(BusinessEntityChanges))]
    [InlineData(typeof(BusinessEntityChangesCollection))]
    [InlineData(typeof(NewOrUpdatedItem))]
    [InlineData(typeof(RemovedOrDeletedItem))]
    [InlineData(typeof(ChangeType))]
    [InlineData(typeof(RetrieveEntityChangesRequest))]
    [InlineData(typeof(RetrieveEntityChangesResponse))]
    public void Change_tracking_types_are_allowed(Type type) => SdkTypeAllowlist.IsAllowed(type).Should().BeTrue();

    [Fact]
    public void Response_round_trips_through_the_restricted_serializer()
    {
        var updated = new Entity("new_t", Guid.NewGuid()) { ["new_name"] = "a", RowVersion = "42" };
        var deleted = new EntityReference("new_t", Guid.NewGuid());
        var changes = new BusinessEntityChanges
        {
            Changes = new BusinessEntityChangesCollection
            {
                new NewOrUpdatedItem(ChangeType.NewOrUpdated, updated),
                new RemovedOrDeletedItem(ChangeType.RemoveOrDeleted, deleted),
            },
            DataToken = "43!2026-11-11 10:00:00", MoreRecords = false, PagingCookie = "44!u!42",
        };
        var response = new OrganizationResponse { ResponseName = "RetrieveEntityChanges", Results = { ["EntityChanges"] = changes } };
        var serializer = new DataContractSerializer(typeof(OrganizationResponse),
            new DataContractSerializerSettings { DataContractResolver = new SdkTypeAllowlistResolver() });

        using var buffer = new MemoryStream();
        serializer.WriteObject(buffer, response);
        buffer.Position = 0;
        var read = (OrganizationResponse)serializer.ReadObject(buffer)!;

        var back = read.Results["EntityChanges"].Should().BeOfType<BusinessEntityChanges>().Subject;
        back.DataToken.Should().Be("43!2026-11-11 10:00:00");
        back.PagingCookie.Should().Be("44!u!42");
        back.Changes.Should().HaveCount(2);
        var item = back.Changes[0].Should().BeOfType<NewOrUpdatedItem>().Subject;
        item.NewOrUpdatedEntity.Id.Should().Be(updated.Id);
        item.NewOrUpdatedEntity.RowVersion.Should().Be("42");
        back.Changes[1].Should().BeOfType<RemovedOrDeletedItem>().Which.RemovedItem.Id.Should().Be(deleted.Id);
    }
}
