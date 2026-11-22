using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>#131, rulings Z2 and Z3: the seed definitions of <c>timezonedefinition</c> and the new <c>usersettings</c> columns (no database).</summary>
public class TimeZoneSeedTests
{
    private static EntityMetadata Seed(string logicalName)
    {
        using var stream = typeof(SystemSeed).Assembly.GetManifestResourceStream($"Xrmd.Metadata.Seed.{logicalName}.json")
                           ?? throw new InvalidOperationException($"No seed resource for {logicalName}.");
        using var reader = new StreamReader(stream);
        return MetadataSerializer.Deserialize(reader.ReadToEnd());
    }

    private static T Column<T>(EntityMetadata e, string name) where T : AttributeMetadata =>
        e.Attributes.Should().ContainSingle(a => a.LogicalName == name, name).Subject.Should().BeAssignableTo<T>(name).Subject;

    [Fact]
    public void Timezonedefinition_is_seeded()
    {
        var zones = Seed(TimeZoneSchema.Table);
        (zones.SchemaName, zones.EntitySetName, zones.ObjectTypeCode, zones.OwnershipType, zones.PrimaryNameAttribute)
            .Should().Be(("TimeZoneDefinition", "timezonedefinitions", 4810, OwnershipTypes.None, "userinterfacename"));
        Column<StringAttributeMetadata>(zones, "userinterfacename").MaxLength.Should().Be(TimeZoneSchema.MaxNameLength);
        Column<StringAttributeMetadata>(zones, "standardname").MaxLength.Should().Be(100);
        foreach (var number in new[] { TimeZoneSchema.Code, "bias", "retiredorder" }) Column<IntegerAttributeMetadata>(zones, number);
        zones.Attributes.Should().NotContain(a => a.LogicalName == "daylightname");
    }

    [Fact]
    public void Usersettings_has_time_zone_and_format_columns()
    {
        var settings = Seed("usersettings");
        Column<IntegerAttributeMetadata>(settings, TimeZoneSchema.Code);
        foreach (var (name, length) in new[]
                 {
                     ("dateformatstring", 255), ("timeformatstring", 255), ("dateseparator", 5), ("timeseparator", 5),
                     ("amdesignator", 25), ("pmdesignator", 25),
                 })
            Column<StringAttributeMetadata>(settings, name).MaxLength.Should().Be(length, name);
        TimeZoneSchema.UserSettingsDefaults.Select(d => d.Column)
            .Should().Equal("timezonecode", "dateformatstring", "timeformatstring", "dateseparator", "timeseparator", "amdesignator", "pmdesignator");
        TimeZoneSchema.UserSettingsDefaults.Select(d => d.Value)
            .Should().Equal(92, "M/d/yyyy", "h:mm tt", "/", ":", "AM", "PM");
    }

    [Fact]
    public void Row_ids_are_stable_and_distinct()
    {
        TimeZoneSchema.RowId(110).Should().Be(TimeZoneSchema.RowId(110));
        TimeZoneCatalog.All.Select(z => TimeZoneSchema.RowId(z.Code)).Should().OnlyHaveUniqueItems();
    }
}
