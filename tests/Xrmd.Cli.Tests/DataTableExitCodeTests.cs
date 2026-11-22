using System.Net;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Cli.Commands;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Tests;

/// <summary>Exit codes of <c>data</c> and <c>dev seed</c> decided by the CLI from the table's metadata (a scripted server).</summary>
public sealed class DataTableExitCodeTests
{
    private const string Url = "http://xrmd.test:5000";

    /// <summary>A server describing table <c>t</c> with the given entity-definition JSON and attribute JSON objects.</summary>
    private static TestCli Cli(string entity, params string[] attributes) => new(req =>
    {
        var path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath);
        if (req.Method == HttpMethod.Get && path.EndsWith("/Attributes", StringComparison.Ordinal))
            return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{{string.Join(",", attributes)}}]}""");
        if (req.Method == HttpMethod.Get && path.Contains("EntityDefinitions(", StringComparison.Ordinal))
            return FakeHandler.Json(HttpStatusCode.OK, entity);
        if (req.Method == HttpMethod.Get && path.EndsWith("/RelationshipDefinitions", StringComparison.Ordinal))
            return FakeHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
        if (req.Method == HttpMethod.Post && path.EndsWith("/ts", StringComparison.Ordinal))
        {
            var created = new HttpResponseMessage(HttpStatusCode.NoContent);
            created.Headers.Add("OData-EntityId", $"{Url}/api/data/v9.2/ts({Guid.NewGuid()})");
            return created;
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    private const string Name = """{"@odata.type":"#Microsoft.Dynamics.CRM.StringAttributeMetadata","LogicalName":"name","AttributeType":"String","MaxLength":10,"IsValidForCreate":true}""";
    private const string Id = """{"@odata.type":"#Microsoft.Dynamics.CRM.UniqueIdentifierAttributeMetadata","LogicalName":"tid","AttributeType":"Uniqueidentifier","IsValidForCreate":true}""";

    [Fact]
    public async Task A_table_without_an_entity_set_is_a_usage_error()
    {
        using var cli = Cli("""{"LogicalName":"t","EntitySetName":null,"PrimaryIdAttribute":"tid","PrimaryNameAttribute":"name"}""", Id, Name);
        (await cli.LoginDevAsync(Url)).Should().Be(0);

        (await cli.RunAsync("dev", "seed", "t", "--count", "1")).Should().Be(2, cli.Err.ToString());
        cli.Err.ToString().Should().Contain("no entity set");
        (await cli.RunAsync("data", "export", "t", "--out", Path.Combine(cli.ConfigDir, "t.json"), "--format", "json")).Should().Be(2, cli.Err.ToString());
    }

    [Fact]
    public async Task Seed_skips_a_writable_column_of_a_type_it_cannot_generate_with_a_note()
    {
        // An image column: typed metadata, writable on create, but no plain data value.
        const string image = """{"@odata.type":"#Microsoft.Dynamics.CRM.ImageAttributeMetadata","LogicalName":"pic","AttributeType":"Virtual","IsValidForCreate":true}""";
        using var cli = Cli("""{"LogicalName":"t","EntitySetName":"ts","PrimaryIdAttribute":"tid","PrimaryNameAttribute":"name"}""", Id, Name, image);
        (await cli.LoginDevAsync(Url)).Should().Be(0);

        (await cli.RunAsync("dev", "seed", "t", "--count", "2")).Should().Be(0, cli.Err.ToString());
        cli.Err.ToString().Should().Contain("Skipped column 'pic'");
        cli.Handler.Bodies.Where(b => b is not null && b.Contains("\"name\"")).Should().HaveCount(2).And.OnlyContain(b => !b!.Contains("pic"));
    }

    [Fact]
    public async Task The_single_navigation_of_a_customer_column_is_refused_and_each_target_has_its_own()
    {
        const string customer = """{"@odata.type":"#Microsoft.Dynamics.CRM.LookupAttributeMetadata","LogicalName":"pb_customerid","AttributeType":"Customer","Targets":["account","contact"],"IsValidForCreate":true}""";
        var handler = new FakeHandler(req =>
        {
            var path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath);
            if (path.EndsWith("/Attributes", StringComparison.Ordinal))
                return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{{Id}},{{Name}},{{customer}}]}""");
            if (path.EndsWith("/RelationshipDefinitions", StringComparison.Ordinal))
                return FakeHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
            return FakeHandler.Json(HttpStatusCode.OK, """{"LogicalName":"t","EntitySetName":"ts","PrimaryIdAttribute":"tid","PrimaryNameAttribute":"name"}""");
        });
        var client = new XrmdClient(new HttpClient(handler), new Uri(Url), _ => Task.FromResult("tok"));
        var table = await DataTable.LoadAsync(client, "t", CancellationToken.None);
        var lookup = table.Find("pb_customerid").Should().BeOfType<LookupAttributeMetadata>().Subject;

        table.Invoking(t => t.Navigation(lookup)).Should().Throw<InvalidOperationException>().WithMessage("*pb_customerid*target*");
        table.Navigation(lookup, "account").Should().Be("pb_customerid_account");
        table.Navigation(lookup, "contact").Should().Be("pb_customerid_contact");
    }

    [Fact]
    public async Task A_lookup_with_one_relationship_per_target_binds_through_the_targets_navigation()
    {
        // An activity's regardingobjectid has a navigation per target table: a contact must be bound through the contact's.
        const string regarding = """{"@odata.type":"#Microsoft.Dynamics.CRM.LookupAttributeMetadata","LogicalName":"regardingobjectid","AttributeType":"Lookup","Targets":["account","contact"],"IsValidForCreate":true}""";
        var handler = new FakeHandler(req =>
        {
            var path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath);
            if (path.EndsWith("/Attributes", StringComparison.Ordinal))
                return FakeHandler.Json(HttpStatusCode.OK, $$"""{"value":[{{Id}},{{Name}},{{regarding}}]}""");
            if (path.EndsWith("/RelationshipDefinitions", StringComparison.Ordinal))
                return FakeHandler.Json(HttpStatusCode.OK, """
                    {"value":[
                      {"ReferencingEntity":"t","ReferencingAttribute":"regardingobjectid","ReferencedEntity":"account","ReferencingEntityNavigationPropertyName":"regardingobjectid_account_t"},
                      {"ReferencingEntity":"t","ReferencingAttribute":"regardingobjectid","ReferencedEntity":"contact","ReferencingEntityNavigationPropertyName":"regardingobjectid_contact_t"}]}
                    """);
            return FakeHandler.Json(HttpStatusCode.OK, """{"LogicalName":"t","EntitySetName":"ts","PrimaryIdAttribute":"tid","PrimaryNameAttribute":"name"}""");
        });
        var client = new XrmdClient(new HttpClient(handler), new Uri(Url), _ => Task.FromResult("tok"));
        var table = await DataTable.LoadAsync(client, "t", CancellationToken.None);
        var lookup = table.Find("regardingobjectid").Should().BeOfType<LookupAttributeMetadata>().Subject;

        table.Navigation(lookup, "account").Should().Be("regardingobjectid_account_t");
        table.Navigation(lookup, "contact").Should().Be("regardingobjectid_contact_t");
    }
}
