using FluentAssertions;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Records;

namespace Xrmd.Migration.Tests.Mapping;

/// <summary>The rows of an archive entry (spec §8.7): every column to its <c>xrmva_</c> column, values as they are, lookups kept or as text.</summary>
public sealed class ArchiveTranslationTests
{
    private static readonly Guid RowId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Account = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Deal = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static SourceCatalogColumn Col(string name, string type = "String", params string[] targets) => new(name, type, targets, false);

    private static readonly SourceCatalog Catalog = new(
        [
            new SourceCatalogTable("quote", "quoteid", 1084, "user", false,
            [
                Col("quoteid", "Uniqueidentifier"), Col("name"), Col("ownerid", "Owner", "systemuser", "team"), Col("createdon", "DateTime"),
                Col("totalamount", "Money"), Col("totalamount_base", "Money"), Col("statecode", "State"), Col("statuscode", "Status"),
                Col("customerid", "Customer", "account", "contact"), Col("opportunityid", "Lookup", "opportunity"),
                Col("pricelevelid", "Lookup", "pricelevel"), Col("processid", "Uniqueidentifier"), Col("tags", "Virtual"), Col("notes", "Memo"),
            ]),
            new SourceCatalogTable("letter", "activityid", 4207, "user", true,
                [Col("activityid", "Uniqueidentifier"), Col("subject"), Col("address"), Col("to", "PartyList"), Col("activitytypecode", "EntityName"),
                 Col("statecode", "State"), Col("statuscode", "Status")]),
        ],
        [new SourceCatalogOption("quote", "statecode", 0, null, "Draft"), new SourceCatalogOption("quote", "statuscode", 1, 0, "In progress")]);

    private sealed class Target : IMapTarget
    {
        public HashSet<string> Tables { get; } = ["account", "contact", "systemuser"];
        public HashSet<(string, string)> Columns { get; } = [];

        public bool HasTable(string table) => Tables.Contains(table);
        public bool HasColumn(string table, string column) => Columns.Contains((table, column));
        public bool IsActivity(string table) => table == "xrmva_letter";
        public bool CanBeParty(string table) => table == "account";
        public IReadOnlyList<(int Value, string Label)> Options(string table, string column) => [];
        public IReadOnlyList<(int State, int Status, string Label)> Statuses(string table) => [];
        public (int State, int Status) DefaultStatus(string table) => (0, 1);
        public bool IsWholeNumber(string table, string column) => false;
    }

    private sealed class NoDerived : IDerivedValues
    {
        public MigrationValue? Get(string sourceTable, Guid id, string column) => null;
        public bool IsShared(string sourceTable, Guid id) => false;
    }

    private sealed class Notes : ITranslationNotes
    {
        public List<(string Table, string? Column, string Code)> All { get; } = [];
        public void Note(string targetTable, string? column, string code, Guid rowId) => All.Add((targetTable, column, code));
    }

    private static MapSet Maps(string yaml) => MapSet.Combine([MapReader.Parse("version: 1\ntables:\n" + yaml, "archive.yaml")]);

    private static readonly MapSet Archive = Maps("  - { source: quote, target: xrmva_quote, archive: true }\n  - { source: letter, archive: true }");

    private static MigrationRecord Row(params (string Column, MigrationValue Value)[] values) => new(RowId, values.ToDictionary(v => v.Column, v => v.Value));

    [Fact]
    public void Archive_row_takes_every_column_under_its_archive_name_and_keeps_its_values()
    {
        var notes = new Notes();
        var translator = new RecordTranslator(Archive, Catalog, new Target(), new NoDerived());
        var created = new DateTimeValue(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var result = translator.Translate("quote", Row(
            ("quoteid", new GuidValue(RowId)), ("name", new StringValue("Offer 1")), ("ownerid", new LookupValue("systemuser", Account)),
            ("createdon", created), ("totalamount", new MoneyValue(12.5m)), ("totalamount_base", new MoneyValue(12.5m)),
            ("statecode", new OptionValue(0)), ("statuscode", new OptionValue(1)), ("customerid", new LookupValue("account", Account)),
            ("opportunityid", new LookupValue("opportunity", Deal)), ("opportunityidname", new StringValue("Big deal")),
            ("pricelevelid", new LookupValue("pricelevel", Deal)), ("processid", new GuidValue(Deal)), ("notes", NullValue.Instance)), notes);

        translator.TableOf("quote").Should().Be("xrmva_quote");
        result.Values.Should().BeEquivalentTo(new Dictionary<string, MigrationValue>
        {
            ["xrmva_name"] = new StringValue("Offer 1"), ["ownerid"] = new LookupValue("systemuser", Account), ["createdon"] = created,
            ["xrmva_totalamount"] = new MoneyValue(12.5m), ["xrmva_statecode"] = new OptionValue(0), ["xrmva_statuscode"] = new OptionValue(1),
            ["xrmva_customerid"] = new LookupValue("account", Account),
            ["xrmva_opportunityidname"] = new StringValue("Big deal"), ["xrmva_pricelevelidname"] = new StringValue(Deal.ToString("D")),
            ["xrmva_notes"] = NullValue.Instance,
        });
        notes.All.Should().BeEquivalentTo(new[]
        {
            ("xrmva_quote", (string?)"xrmva_opportunityidname", "archive-lookup-as-text"),
            ("xrmva_quote", "xrmva_pricelevelidname", "archive-lookup-as-text"),
            ("xrmva_quote", "processid", "column-not-mapped"),
        });
    }

    [Fact]
    public void Lookup_to_an_archived_or_mapped_table_is_kept_and_retargeted()
    {
        var maps = Maps("  - { source: quote, archive: true }\n  - { source: opportunity, target: xrmv_deal }\n  - { source: pricelevel, archive: true }");
        var result = new RecordTranslator(maps, Catalog, new Target(), new NoDerived()).Translate("quote", Row(
            ("opportunityid", new LookupValue("opportunity", Deal)), ("pricelevelid", new LookupValue("pricelevel", Deal))), null);

        result.Values.Should().BeEquivalentTo(new Dictionary<string, MigrationValue>
        {
            ["xrmva_opportunityid"] = new LookupValue("xrmv_deal", Deal), ["xrmva_pricelevelid"] = new LookupValue("xrmva_pricelevel", Deal),
        });
    }

    [Fact]
    public void Once_the_archive_table_exists_its_columns_decide_lookup_or_text()
    {
        // The schema phase made opportunityid a lookup (its table appeared meanwhile) and pricelevelid text.
        var target = new Target();
        target.Tables.Add("xrmva_quote");
        target.Columns.Add(("xrmva_quote", "xrmva_opportunityid"));
        target.Columns.Add(("xrmva_quote", "xrmva_pricelevelidname"));
        var result = new RecordTranslator(Archive, Catalog, target, new NoDerived()).Translate("quote", Row(
            ("opportunityid", new LookupValue("opportunity", Deal)), ("pricelevelid", new LookupValue("pricelevel", Deal))), null);

        result.Values.Should().BeEquivalentTo(new Dictionary<string, MigrationValue>
        {
            ["xrmva_opportunityid"] = new LookupValue("opportunity", Deal), ["xrmva_pricelevelidname"] = NullValue.Instance,
        });
    }

    [Fact]
    public void Listed_columns_rename_or_drop()
    {
        var notes = new Notes();
        var maps = Maps("  - source: quote\n    archive: true\n    columns: { name: xrmva_title, notes: null }");
        var result = new RecordTranslator(maps, Catalog, new Target(), new NoDerived()).Translate("quote", Row(
            ("name", new StringValue("Offer 1")), ("notes", new StringValue("secret"))), notes);

        result.Values.Should().BeEquivalentTo(new Dictionary<string, MigrationValue> { ["xrmva_title"] = new StringValue("Offer 1") });
        notes.All.Should().Equal(("xrmva_quote", "notes", "column-dropped"));
    }

    [Fact]
    public void Activity_archive_keeps_activity_columns_and_parties_by_name()
    {
        var parties = new PartyListValue([new Party(2, "account", Account, null)]);
        var result = new RecordTranslator(Archive, Catalog, new Target(), new NoDerived()).Translate("letter", Row(
            ("activityid", new GuidValue(RowId)), ("subject", new StringValue("Hello")), ("address", new StringValue("1 Example Road")),
            ("to", parties), ("activitytypecode", new StringValue("letter"))), null);

        result.Values.Should().BeEquivalentTo(new Dictionary<string, MigrationValue>
        {
            ["subject"] = new StringValue("Hello"), ["xrmva_address"] = new StringValue("1 Example Road"), ["to"] = parties,
        });
    }

    [Fact]
    public void Lookup_to_a_table_the_package_creates_is_kept()
    {
        // The census and the dry run see the tables the package's solutions and delta will create, as the schema phase does.
        var result = new RecordTranslator(Archive, Catalog, new Target(), new NoDerived(), t => t == "opportunity").Translate("quote", Row(
            ("opportunityid", new LookupValue("opportunity", Deal)), ("opportunityidname", new StringValue("Big deal"))), null);

        result.Values.Should().BeEquivalentTo(new Dictionary<string, MigrationValue> { ["xrmva_opportunityid"] = new LookupValue("opportunity", Deal) });
    }

    [Fact]
    public void Custom_columns_drop_leaves_out_unlisted_custom_columns()
    {
        var catalog = new SourceCatalog(
            [new SourceCatalogTable("quote", "quoteid", 1084, "user", false,
                [Col("quoteid", "Uniqueidentifier"), Col("name"), new SourceCatalogColumn("new_rating", "Integer", [], true), new SourceCatalogColumn("new_kept", "String", [], true)])],
            []);
        var maps = Maps("  - source: quote\n    archive: true\n    customColumns: drop\n    columns: { new_kept: xrmva_kept }");
        var notes = new Notes();

        var result = new RecordTranslator(maps, catalog, new Target(), new NoDerived()).Translate("quote", Row(
            ("name", new StringValue("Offer 1")), ("new_rating", new IntValue(5)), ("new_kept", new StringValue("yes"))), notes);

        result.Values.Should().BeEquivalentTo(new Dictionary<string, MigrationValue>
        {
            ["xrmva_name"] = new StringValue("Offer 1"), ["xrmva_kept"] = new StringValue("yes"),
        });
        notes.All.Should().Equal(("xrmva_quote", (string?)"new_rating", "column-dropped"));
        ArchiveColumns.Plan(maps.BySource("quote")!, catalog.Tables[0], catalog, _ => true).Single(c => c.Source.LogicalName == "new_rating").Kind
            .Should().Be(ArchiveColumnKind.Dropped, "the schema phase does not create it either");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    public void Activity_archive_takes_the_source_state_with_its_default_status(int state, int status)
    {
        var result = new RecordTranslator(Archive, Catalog, new Target(), new NoDerived()).Translate("letter", Row(
            ("subject", new StringValue("Hello")), ("statecode", new OptionValue(state)), ("statuscode", new OptionValue(4))), null);

        result.Values.Should().BeEquivalentTo(new Dictionary<string, MigrationValue>
        {
            ["subject"] = new StringValue("Hello"), ["xrmva_statecode"] = new OptionValue(state), ["xrmva_statuscode"] = new OptionValue(4),
            ["statecode"] = new OptionValue(state), ["statuscode"] = new OptionValue(status),
        });
    }

    [Fact]
    public void Lookup_names_of_the_export_are_not_columns_of_other_entries()
    {
        var maps = Maps("  - { source: quote, target: xrmv_offer }");
        var notes = new Notes();
        var result = new RecordTranslator(maps, Catalog, new Target(), new NoDerived()).Translate("letter", Row(
            ("subject", new StringValue("Hello")), ("regardingobjectidname", new StringValue("x"))), notes);
        result.Values.Keys.Should().Equal("subject", "regardingobjectidname");

        var offer = new RecordTranslator(maps, Catalog, new Target(), new NoDerived()).Translate("quote", Row(
            ("opportunityid", new LookupValue("opportunity", Deal)), ("opportunityidname", new StringValue("Big deal"))), notes);
        offer.Values.Should().NotContainKey("opportunityidname");
        notes.All.Should().NotContain(n => n.Column == "opportunityidname");
    }

    [Fact]
    public void Plan_names_every_column_once()
    {
        var plan = ArchiveColumns.Plan(Archive.BySource("quote")!, Catalog.Tables[0], Catalog, t => t is "account" or "contact");
        plan.Select(c => (c.Source.LogicalName, c.Kind, c.Target)).Should().Equal(
            ("quoteid", ArchiveColumnKind.Key, null), ("name", ArchiveColumnKind.Copied, "xrmva_name"), ("ownerid", ArchiveColumnKind.ByName, "ownerid"),
            ("createdon", ArchiveColumnKind.ByName, "createdon"), ("totalamount", ArchiveColumnKind.Copied, "xrmva_totalamount"),
            ("totalamount_base", ArchiveColumnKind.Key, null), ("statecode", ArchiveColumnKind.Copied, "xrmva_statecode"),
            ("statuscode", ArchiveColumnKind.Copied, "xrmva_statuscode"), ("customerid", ArchiveColumnKind.Lookup, "xrmva_customerid"),
            ("opportunityid", ArchiveColumnKind.LookupAsText, "xrmva_opportunityid"), ("pricelevelid", ArchiveColumnKind.LookupAsText, "xrmva_pricelevelid"),
            ("processid", ArchiveColumnKind.Unsupported, null), ("tags", ArchiveColumnKind.Unsupported, null), ("notes", ArchiveColumnKind.Copied, "xrmva_notes"));
    }
}
