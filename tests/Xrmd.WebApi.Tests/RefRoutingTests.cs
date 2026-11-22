using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;

namespace Xrmd.WebApi.Tests;

/// <summary>The <c>@odata.id</c>/<c>$id</c> URL of a <c>$ref</c> request resolved to a record.</summary>
public class RefRoutingTests
{
    private static readonly Guid Id = Guid.Parse("0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70");
    private static readonly FakeStore Store = new(new EntityMetadata { LogicalName = "new_tag", EntitySetName = "new_tags" });

    [Theory]
    [InlineData("https://org.example/api/data/v9.2/new_tags(0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70)")]
    [InlineData("http://localhost/api/data/v9.0/new_tags(0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70)/")]
    [InlineData("/api/data/v9.2/new_tags(0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70)")]
    [InlineData("new_tags(0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70)")]
    [InlineData("new_tags(0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70)?x=1")]
    public void Absolute_and_relative_urls_resolve_to_the_record(string url)
    {
        var reference = RefController.ParseReference(Store, url);

        reference.LogicalName.Should().Be("new_tag");
        reference.Id.Should().Be(Id);
    }

    [Theory]
    [InlineData("https://org.example/api/data/v9.2/unknown_sets(0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70)")]
    [InlineData("https://org.example/api/data/v9.2/new_tags")]
    [InlineData("https://org.example/api/data/v9.2/new_tags(not-a-guid)")]
    public void Unresolvable_urls_are_invalid_arguments(string url)
    {
        var act = () => RefController.ParseReference(Store, url);

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400);
    }
}
