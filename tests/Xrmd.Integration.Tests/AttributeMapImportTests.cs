using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Metadata;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>#143, task 3: the solution import writes the entity and attribute maps of <c>EntityMaps</c>, or refuses a map that does not fit.</summary>
[Collection("xrmd")]
public sealed class AttributeMapImportTests(XrmdFixture fx)
{
    private readonly AttributeMapTestKit _kit = new(fx);

    private async Task<List<(string Source, string Target, bool IsSystem)>> AttributeMapsAsync()
    {
        var query = new QueryExpression(AttributeMapSchema.AttributeMap) { ColumnSet = new ColumnSet(true) };
        var link = query.AddLink(AttributeMapSchema.EntityMap, AttributeMapSchema.EntityMapLookup, "entitymapid");
        link.LinkCriteria.AddCondition(AttributeMapSchema.SourceEntity, ConditionOperator.Equal, _kit.Parent);
        link.LinkCriteria.AddCondition(AttributeMapSchema.TargetEntity, ConditionOperator.Equal, _kit.Child);
        return (await _kit.Admin.RetrieveMultipleAsync(query)).Entities
            .Select(e => (e.GetAttributeValue<string>(AttributeMapSchema.SourceAttribute), e.GetAttributeValue<string>(AttributeMapSchema.TargetAttribute),
                e.GetAttributeValue<bool>(AttributeMapSchema.IsSystem)))
            .OrderBy(m => m.Item2).ToList();
    }

    private async Task<int> EntityMapCountAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("SELECT COUNT(*) FROM data.entitymap WHERE sourceentityname = @s AND targetentityname = @t", conn);
        cmd.Parameters.AddWithValue("@s", _kit.Parent);
        cmd.Parameters.AddWithValue("@t", _kit.Child);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<string> JobLogAsync(Guid jobId) =>
        (await _kit.Admin.RetrieveAsync("importjob", jobId, new ColumnSet("data"))).GetAttributeValue<string>("data");

    [Fact]
    public async Task Import_writes_entity_and_attribute_maps()
    {
        await _kit.CreateTablesAsync();

        var job = await _kit.ImportAsync(AttributeMapTestKit.Map(_kit.Parent, _kit.Child, ("new_code", "new_code"), ("new_amount", "new_qty")));

        (await EntityMapCountAsync()).Should().Be(1);
        (await AttributeMapsAsync()).Should().Equal(("new_code", "new_code", false), ("new_amount", "new_qty", false));
        (await JobLogAsync(job)).Should().NotContain("EntityMaps", "the maps are applied, not kept as unsupported");
    }

    [Fact]
    public async Task Reimport_does_not_duplicate_and_replaces_the_source_of_a_target()
    {
        await _kit.CreateTablesAsync();
        await _kit.ImportAsync(AttributeMapTestKit.Map(_kit.Parent, _kit.Child, ("new_code", "new_code"), ("new_amount", "new_qty")));

        await _kit.ImportAsync(AttributeMapTestKit.Map(_kit.Parent, _kit.Child, ("new_name", "new_code")));

        (await EntityMapCountAsync()).Should().Be(1);
        (await AttributeMapsAsync()).Should().Equal(("new_name", "new_code", false), ("new_amount", "new_qty", false));
    }

    [Fact]
    public async Task Export_writes_the_imported_entity_maps_back()
    {
        await _kit.CreateTablesAsync();
        await _kit.ImportAsync(AttributeMapTestKit.Map(_kit.Parent, _kit.Child, ("new_code", "new_code")));

        var zip = ((ExportSolutionResponse)await _kit.Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = _kit.Solution })).ExportSolutionFile;

        using var archive = new ZipArchive(new MemoryStream(zip));
        using var stream = archive.GetEntry("customizations.xml")!.Open();
        var maps = XDocument.Load(stream).Root!.Element("EntityMaps")!;
        maps.Descendants("EntitySource").Single().Value.Should().Be(_kit.Parent);
        maps.Descendants("AttributeTarget").Single().Value.Should().Be("new_code");
    }

    [Theory]
    [InlineData("new_nothere", "new_code", "does not exist")]
    [InlineData("new_code", "new_qty", "different types")]
    [InlineData("new_code", "new_short", "shorter")]
    [InlineData("createdon", "createdon", "cannot be written")]
    public async Task Invalid_map_fails_the_import(string source, string target, string reason)
    {
        await _kit.CreateTablesAsync();

        var message = await BadRequestAsync(() => _kit.ImportAsync(AttributeMapTestKit.Map(_kit.Parent, _kit.Child, (source, target))));

        message.Should().Contain($"Entity map from '{_kit.Parent}' to '{_kit.Child}'").And.Contain(reason);
        (await EntityMapCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Map_onto_the_relationship_lookup_or_twice_onto_a_column_fails_the_import()
    {
        await _kit.CreateTablesAsync();

        // new_ownparentid (parent to parent) and new_parentid (child to parent) are both lookups to the parent: the relationship fills it.
        var lookup = await BadRequestAsync(() => _kit.ImportAsync(
            AttributeMapTestKit.Map(_kit.Parent, _kit.Child, ("new_ownparentid", "new_parentid"))));
        lookup.Should().Contain("the lookup to '" + _kit.Parent + "'");

        var twice = await BadRequestAsync(() => _kit.ImportAsync(
            AttributeMapTestKit.Map(_kit.Parent, _kit.Child, ("new_code", "new_code"), ("new_name", "new_code"))));
        twice.Should().Contain("mapped more than once");
        (await EntityMapCountAsync()).Should().Be(0);
    }
}
