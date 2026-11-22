using System.Text.Json.Nodes;
using FluentAssertions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

/// <summary>#131, rulings Z1 and Z5: the Dataverse time zone codes and the conversions the server and the UI share.</summary>
public class TimeZoneCatalogTests
{
    private static readonly DateTime SpringUtc = new(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Every_code_resolves_on_this_host()
    {
        TimeZoneCatalog.All.Should().HaveCount(141);
        TimeZoneCatalog.All.Select(z => z.Code).Should().OnlyHaveUniqueItems();
        foreach (var zone in TimeZoneCatalog.All)
            TimeZoneCatalog.Resolve(zone.Code).Should().NotBeNull(zone.StandardName);
    }

    [Fact]
    public void Observed_codes()
    {
        TimeZoneCatalog.Utc.Should().Be(92);
        TimeZoneCatalog.All.Single(z => z.Code == 92).Should().Be(new TimeZoneEntry(92, "UTC", 0, 0, "Etc/UTC"));
        var rome = TimeZoneCatalog.All.Single(z => z.Code == 110);
        (rome.StandardName, rome.Bias, rome.RetiredOrder).Should().Be(("W. Europe Standard Time", -60, 0));
        TimeZoneCatalog.Contains(110).Should().BeTrue();
        TimeZoneCatalog.Contains(9999).Should().BeFalse();
    }

    [Fact]
    public void Local_from_utc_follows_dst()
    {
        // Observed on a trial environment (export 3d): 01:30Z on the spring day is 03:30 in Rome.
        TimeZoneCatalog.LocalFromUtc(110, SpringUtc).Should().Be(new DateTime(2026, 3, 29, 3, 30, 0));
        TimeZoneCatalog.LocalFromUtc(110, SpringUtc).Kind.Should().Be(DateTimeKind.Unspecified);
        TimeZoneCatalog.LocalFromUtc(110, new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)).Should().Be(new DateTime(2026, 1, 15, 13, 0, 0));
        TimeZoneCatalog.LocalFromUtc(92, SpringUtc).Should().Be(new DateTime(2026, 3, 29, 1, 30, 0));
    }

    [Fact]
    public void Utc_from_local_gap_moves_forward()
    {
        // 02:30 does not exist in Rome on 2026-03-29: read as 03:30 CEST.
        var utc = TimeZoneCatalog.UtcFromLocal(110, new DateTime(2026, 3, 29, 2, 30, 0));
        utc.Should().Be(SpringUtc);
        utc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Utc_from_local_ambiguous_is_standard()
    {
        // 02:30 happens twice in Rome on 2026-10-25: the standard time (CET, UTC+1) is taken.
        TimeZoneCatalog.UtcFromLocal(110, new DateTime(2026, 10, 25, 2, 30, 0)).Should().Be(new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Utc));
        TimeZoneCatalog.UtcFromLocal(110, new DateTime(2026, 7, 1, 12, 0, 0)).Should().Be(new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Unknown_code_throws()
    {
        var act = () => TimeZoneCatalog.Resolve(9999);
        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400).WithMessage("*9999*");
    }

    [Fact]
    public void Fixture_matches_catalog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "ui", "time-zones.json");
        var zones = JsonNode.Parse(File.ReadAllText(path))!["zones"]!.AsObject();
        zones.ToDictionary(p => int.Parse(p.Key), p => p.Value!.GetValue<string>())
            .Should().BeEquivalentTo(TimeZoneCatalog.All.ToDictionary(z => z.Code, z => z.IanaId),
                "fixtures/ui/time-zones.json and ui/src/apps/timeZones.ts must hold TimeZoneCatalog's IANA ids");
    }
}
