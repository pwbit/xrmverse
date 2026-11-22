using FluentAssertions;
using Xrmd.Migration.Packaging;
using Xrmd.Migration.Records;

namespace Xrmd.Migration.Tests;

public class RecordLineCodecTests
{
    private static readonly Guid Id = Guid.Parse("6f1c0b7a-8f65-4b0e-9d52-0f4bb1d3c0a1");
    private static readonly Guid Other = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static void ShouldRoundTrip(MigrationRecord record)
    {
        var back = RecordLineCodec.Read(RecordLineCodec.Write(record));
        back.Id.Should().Be(record.Id);
        back.Values.Keys.Should().BeEquivalentTo(record.Values.Keys);
        foreach (var (key, value) in record.Values)
            back.Values[key].Should().BeEquivalentTo(value, o => o.RespectingRuntimeTypes(), key);
    }

    [Fact]
    public void Round_trips_every_value_kind()
    {
        ShouldRoundTrip(new MigrationRecord(Id, new Dictionary<string, MigrationValue>
        {
            ["name"] = new StringValue("Contoso Example"),
            ["numberofemployees"] = new IntValue(1200),
            ["xv_ratio"] = new DecimalValue(1.25m),
            ["xv_score"] = new DoubleValue(0.5),
            ["revenue"] = new MoneyValue(1200.50m),
            ["donotemail"] = new BoolValue(true),
            ["createdon"] = new DateTimeValue(new DateTime(2024, 3, 1, 10, 0, 0, 123, DateTimeKind.Utc)),
            ["birthdate"] = new DateOnlyValue(new DateOnly(1990, 5, 17)),
            ["statuscode"] = new OptionValue(1),
            ["xv_tags"] = new OptionsValue([3, 5]),
            ["xv_externalid"] = new GuidValue(Other),
            ["parentaccountid"] = new LookupValue("account", Other),
            ["to"] = new PartyListValue([new Party(2, "contact", Other, null), new Party(2, null, null, "anna@example.com")]),
            ["xv_contract"] = new FileValue("files/account/xv_contract/" + Id, "contract.pdf", "application/pdf", 2048),
            ["entityimage"] = new ImageValue("files/account/entityimage/" + Id),
            ["fax"] = NullValue.Instance,
        }));
    }

    [Fact]
    public void Decimal_keeps_all_digits()
    {
        var record = new MigrationRecord(Id, new Dictionary<string, MigrationValue>
        {
            ["a"] = new DecimalValue(12345678901234.5678901234m),
            ["b"] = new MoneyValue(0.0001m),
        });
        var line = RecordLineCodec.Write(record);
        line.Should().Contain("\"12345678901234.5678901234\"").And.Contain("\"0.0001\"");
        ShouldRoundTrip(record);
    }

    [Fact]
    public void Date_only_and_utc_datetime_stay_distinct()
    {
        RecordLineCodec.Write(new MigrationRecord(Id, new Dictionary<string, MigrationValue> { ["d"] = new DateOnlyValue(new DateOnly(2024, 3, 1)) }))
            .Should().Contain("{\"date\":\"2024-03-01\"}");
        var act = () => new DateTimeValue(new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Local));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Null_and_absent_are_different()
    {
        var withNull = RecordLineCodec.Read(RecordLineCodec.Write(new MigrationRecord(Id, new Dictionary<string, MigrationValue> { ["parentaccountid"] = NullValue.Instance })));
        withNull.Values.Should().ContainKey("parentaccountid").WhoseValue.Should().BeSameAs(NullValue.Instance);
        var without = RecordLineCodec.Read(RecordLineCodec.Write(new MigrationRecord(Id, new Dictionary<string, MigrationValue>())));
        without.Values.Should().NotContainKey("parentaccountid");
    }

    [Fact]
    public void Non_ascii_round_trips()
    {
        ShouldRoundTrip(new MigrationRecord(Id, new Dictionary<string, MigrationValue>
        {
            ["xv_cittaid"] = new LookupValue("xv_città", Other),
            ["name"] = new StringValue("Zürich – 東京"),
        }));
    }

    [Fact]
    public void Unknown_tag_is_refused()
    {
        var act = () => RecordLineCodec.Read($"{{\"id\":\"{Id}\",\"v\":{{\"x\":{{\"blob\":1}}}}}}");
        act.Should().Throw<PackageFormatException>().Where(e => e.Message.Contains("blob") && e.Message.Contains("'x'"));
    }
}
