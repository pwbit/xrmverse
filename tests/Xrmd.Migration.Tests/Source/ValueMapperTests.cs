using System.Text.Json;
using FluentAssertions;
using Xrmd.Migration.Records;
using Xrmd.Migration.Source;

namespace Xrmd.Migration.Tests.Source;

public class ValueMapperTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static SourceColumn Col(string name, string type, string? behavior = null, params string[] targets) =>
        new(name, type, behavior, targets, false, true);

    private static SourceTable Table(params SourceColumn[] columns) =>
        new("account", "accounts", "accountid", false, false, false, [Col("accountid", "Uniqueidentifier"), .. columns]);

    private static MigrationRecord Map(SourceTable table, string json, Func<SourceColumn, Guid, MigrationValue?>? files = null)
    {
        using var doc = JsonDocument.Parse(json);
        return ValueMapper.Map(table, doc.RootElement, files ?? ((_, _) => null));
    }

    private static string Row(string body) => $$"""{"accountid":"{{Id}}",{{body}}}""";

    [Theory]
    [InlineData("String")]
    [InlineData("Memo")]
    [InlineData("EntityName")]
    public void Text_types_map_to_string(string type) =>
        Map(Table(Col("x", type)), Row("\"x\":\"hello\"")).Values["x"].Should().Be(new StringValue("hello"));

    [Theory]
    [InlineData("Integer")]
    [InlineData("BigInt")]
    public void Whole_numbers_map_to_int(string type) =>
        Map(Table(Col("x", type)), Row("\"x\":9007199254740993")).Values["x"].Should().Be(new IntValue(9007199254740993));

    [Fact]
    public void Decimal_keeps_exact_digits() =>
        Map(Table(Col("x", "Decimal")), Row("\"x\":12345678901.0000000001")).Values["x"].Should().Be(new DecimalValue(12345678901.0000000001m));

    [Fact]
    public void Double_maps_to_double() =>
        Map(Table(Col("x", "Double")), Row("\"x\":1.5")).Values["x"].Should().Be(new DoubleValue(1.5));

    [Fact]
    public void Money_maps_to_money() =>
        Map(Table(Col("x", "Money")), Row("\"x\":1000.25")).Values["x"].Should().Be(new MoneyValue(1000.25m));

    [Fact]
    public void Boolean_maps_to_bool() =>
        Map(Table(Col("x", "Boolean")), Row("\"x\":true")).Values["x"].Should().Be(new BoolValue(true));

    [Fact]
    public void Date_time_is_utc()
    {
        var value = (DateTimeValue)Map(Table(Col("x", "DateTime", "UserLocal")), Row("\"x\":\"2026-03-01T10:15:30Z\"")).Values["x"];
        value.Utc.Should().Be(new DateTime(2026, 3, 1, 10, 15, 30, DateTimeKind.Utc));
        value.Utc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Date_only_column_maps_to_date_only() =>
        Map(Table(Col("x", "DateTime", "DateOnly")), Row("\"x\":\"2026-03-01\"")).Values["x"].Should().Be(new DateOnlyValue(new DateOnly(2026, 3, 1)));

    [Theory]
    [InlineData("Picklist")]
    [InlineData("State")]
    [InlineData("Status")]
    public void Choices_map_to_option(string type) =>
        Map(Table(Col("x", type)), Row("\"x\":100000001")).Values["x"].Should().Be(new OptionValue(100000001));

    [Fact]
    public void Multi_select_maps_to_options() =>
        ((OptionsValue)Map(Table(Col("x", "MultiSelectPicklist")), Row("\"x\":\"1,3\"")).Values["x"]).Values.Should().Equal(1, 3);

    [Fact]
    public void Uniqueidentifier_maps_to_guid() =>
        Map(Table(Col("x", "Uniqueidentifier")), Row($"\"x\":\"{Other}\"")).Values["x"].Should().Be(new GuidValue(Other));

    [Theory]
    [InlineData("Lookup")]
    [InlineData("Customer")]
    [InlineData("Owner")]
    public void Lookup_reads_target_from_annotation(string type) =>
        Map(Table(Col("x", type, null, "account", "contact")),
                Row($"\"_x_value\":\"{Other}\",\"_x_value@Microsoft.Dynamics.CRM.lookuplogicalname\":\"contact\""))
            .Values["x"].Should().Be(new LookupValue("contact", Other));

    [Fact]
    public void Lookup_without_annotation_uses_the_single_target() =>
        Map(Table(Col("x", "Lookup", null, "account")), Row($"\"_x_value\":\"{Other}\"")).Values["x"].Should().Be(new LookupValue("account", Other));

    [Fact]
    public void Lookup_without_annotation_and_many_targets_fails() =>
        FluentActions.Invoking(() => Map(Table(Col("x", "Customer", null, "account", "contact")), Row($"\"_x_value\":\"{Other}\"")))
            .Should().Throw<InvalidDataException>().WithMessage("*account.x*");

    [Fact]
    public void File_and_image_come_from_the_file_callback()
    {
        var file = new FileValue("files/account/doc/" + Id, "a.pdf", "application/pdf", 3);
        var image = new ImageValue("files/account/pic/" + Id);
        var record = Map(Table(Col("doc", "File"), Col("pic", "Image")), Row($"\"doc\":\"{Other}\",\"pic\":\"AAAA\""),
            (c, id) => { id.Should().Be(Id); return c.LogicalName == "doc" ? file : image; });
        record.Values["doc"].Should().Be(file);
        record.Values["pic"].Should().Be(image);
    }

    [Fact]
    public void Empty_file_column_is_null_without_callback() =>
        Map(Table(Col("doc", "File")), Row("\"doc\":null"), (_, _) => throw new InvalidOperationException("no download"))
            .Values["doc"].Should().Be(NullValue.Instance);

    [Fact]
    public void File_skipped_by_the_callback_is_not_written() =>
        Map(Table(Col("doc", "File")), Row($"\"doc\":\"{Other}\""), (_, _) => null).Values.Should().NotContainKey("doc");

    [Fact]
    public void Null_is_kept_as_null() =>
        Map(Table(Col("x", "String")), Row("\"x\":null")).Values["x"].Should().Be(NullValue.Instance);

    [Fact]
    public void Column_missing_from_the_row_is_null()
    {
        // FetchXML through the Web API leaves out empty columns, so an absent column is an empty one.
        var record = Map(Table(Col("x", "String"), Col("y", "Lookup", null, "account")), Row("\"z\":1"));
        record.Values["x"].Should().Be(NullValue.Instance);
        record.Values["y"].Should().Be(NullValue.Instance);
    }

    [Fact]
    public void Unknown_type_is_skipped_and_reported()
    {
        var record = Map(Table(Col("p", "PartyList"), Col("v", "Virtual"), Col("x", "String")), Row("\"x\":\"a\",\"p\":null"));
        record.Values.Keys.Should().BeEquivalentTo(["x"]);
        ValueMapper.IsSupported("PartyList").Should().BeFalse();
        ValueMapper.IsSupported("ManagedProperty").Should().BeFalse();
        ValueMapper.IsSupported("CalendarRules").Should().BeFalse();
        ValueMapper.IsSupported("Money").Should().BeTrue();
    }

    [Fact]
    public void Record_id_is_the_primary_key_and_not_a_value()
    {
        var record = Map(Table(Col("x", "String")), Row("\"x\":\"a\""));
        record.Id.Should().Be(Id);
        record.Values.Should().NotContainKey("accountid");
    }

    [Fact]
    public void Lookup_without_annotation_is_reported_when_a_callback_is_given()
    {
        var unresolved = new List<string>();
        using var doc = JsonDocument.Parse(Row($"\"_x_value\":\"{Other}\""));
        var record = ValueMapper.Map(Table(Col("x", "Lookup", null, "account", "contact")), doc.RootElement, (_, _) => null, unresolved.Add);
        record.Values.Should().NotContainKey("x");
        unresolved.Should().Equal("x");
    }

    [Fact]
    public void Share_target_comes_from_objecttypecode()
    {
        var poa = new SourceTable("principalobjectaccess", "principalobjectaccessset", "principalobjectaccessid", false, false, false,
            [Col("principalobjectaccessid", "Uniqueidentifier"), Col("objectid", "Lookup", null, "account", "contact")]);
        using var doc = JsonDocument.Parse($$"""{"principalobjectaccessid":"{{Id}}","_objectid_value":"{{Other}}","objecttypecode":"contact"}""");
        ValueMapper.Map(poa, doc.RootElement, (_, _) => null).Values["objectid"].Should().Be(new LookupValue("contact", Other));
    }

    [Fact]
    public void Value_of_an_unexpected_type_names_the_column()
    {
        using var doc = JsonDocument.Parse(Row("\"x\":5"));
        FluentActions.Invoking(() => ValueMapper.Map(Table(Col("x", "String")), doc.RootElement, (_, _) => null))
            .Should().Throw<InvalidDataException>().WithMessage("*account.x*");
    }

    [Fact]
    public void Value_of_an_unexpected_type_is_left_out_when_asked()
    {
        using var doc = JsonDocument.Parse(Row("\"x\":5,\"y\":\"ok\""));
        var invalid = new List<string>();
        var record = ValueMapper.Map(Table(Col("x", "String"), Col("y", "String")), doc.RootElement, (_, _) => null, invalid: invalid.Add);
        record.Values.Should().NotContainKey("x");
        record.Values["y"].Should().Be(new StringValue("ok"));
        invalid.Should().Equal("x");
    }

    [Fact]
    public void Object_type_code_number_written_as_table_name()
    {
        // M6 run: Dataverse types userentityuisettings.objecttypecode and teamtemplate.objecttypecode as numbers, and the numbers of
        // custom tables are the source's own: the export writes the table's logical name instead.
        using var doc = JsonDocument.Parse(Row("\"objecttypecode\":10574,\"x\":7"));
        var record = ValueMapper.Map(Table(Col("objecttypecode", "Integer"), Col("x", "Integer")), doc.RootElement, (_, _) => null,
            tableOfCode: code => code == 10574 ? "op365_project" : null);
        record.Values["objecttypecode"].Should().Be(new StringValue("op365_project"));
        record.Values["x"].Should().Be(new IntValue(7));
    }

    [Fact]
    public void Unknown_object_type_code_kept_as_number()
    {
        using var doc = JsonDocument.Parse(Row("\"objecttypecode\":99999"));
        ValueMapper.Map(Table(Col("objecttypecode", "Integer")), doc.RootElement, (_, _) => null, tableOfCode: _ => null)
            .Values["objecttypecode"].Should().Be(new IntValue(99999));
    }
}
