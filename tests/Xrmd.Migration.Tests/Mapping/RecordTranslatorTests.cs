using FluentAssertions;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Records;

namespace Xrmd.Migration.Tests.Mapping;

public sealed class RecordTranslatorTests
{
    private const int LeadOtc = 4;
    private static readonly Guid RowId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OppA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OppB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static readonly MapSet SalesMaps = MapSet.Combine([
        MapReader.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mapping", "sales-like.yaml")), "sales-like.yaml")]);

    private static SourceCatalogColumn Col(string name, bool custom = false, string type = "String") => new(name, type, [], custom);

    private static readonly SourceCatalog Catalog = new(
        [
            new SourceCatalogTable("lead", "leadid", LeadOtc, "user", false,
                [Col("leadid"), Col("subject"), Col("ownerid"), Col("createdon"), Col("address2_city"), Col("leadsourcecode"),
                 Col("new_score", custom: true), Col("msdyn_gdpr", custom: true), Col("statecode"), Col("statuscode")]),
            new SourceCatalogTable("opportunity", "opportunityid", 3, "user", false, [Col("opportunityid"), Col("statecode"), Col("statuscode")]),
            new SourceCatalogTable("opportunityclose", "activityid", 4208, "user", true,
                [Col("activityid"), Col("subject"), Col("opportunityid"), Col("regardingobjectid"), Col("actualdurationminutes"),
                 Col("prioritycode"), Col("activitytypecode"), Col("customers", type: "PartyList"), Col("statecode"), Col("statuscode")]),
            new SourceCatalogTable("account", "accountid", 1, "user", false, [Col("accountid"), Col("creditlimit"), Col("address2_city")]),
            new SourceCatalogTable("new_thing", "new_thingid", 10010, "user", false,
                [Col("new_thingid"), Col("new_kind"), Col("new_level"), Col("new_tags"), Col("prioritycode"), Col("statecode"), Col("statuscode")]),
            new SourceCatalogTable("email", "activityid", 4202, "user", true, [Col("activityid"), Col("to", type: "PartyList")]),
        ],
        [
            new SourceCatalogOption("lead", "leadsourcecode", 8, null, "Web"),
            new SourceCatalogOption("lead", "leadsourcecode", 100000010, null, "Webinar"),
            new SourceCatalogOption("lead", "leadsourcecode", 100000011, null, "  Trade FAIR "),
            new SourceCatalogOption("lead", "leadsourcecode", 100000012, null, ""),
            new SourceCatalogOption("lead", "leadsourcecode", 100000013, null, null),
            new SourceCatalogOption("lead", "statuscode", 1, 0, "New"),
            new SourceCatalogOption("lead", "statuscode", 4, 2, "Lost"),
            new SourceCatalogOption("lead", "statuscode", 100000003, 2, "Went elsewhere"),
            new SourceCatalogOption("lead", "statuscode", 100000009, 2, "No budget"),
            new SourceCatalogOption("opportunity", "statecode", 2, null, "Lost"),
            new SourceCatalogOption("new_thing", "statuscode", 100000005, 1, "Retired"),
        ]);

    private sealed class FakeMapTarget : IMapTarget
    {
        public HashSet<(string, string)> Columns { get; } = [];
        public HashSet<string> Activities { get; } = ["xrmv_opportunityclose", "email"];
        public HashSet<string> Parties { get; } = ["account", "contact"];
        public Dictionary<(string, string), List<(int, string)>> OptionSets { get; } = new();
        public Dictionary<string, List<(int, int, string)>> StatusSets { get; } = new();
        public HashSet<(string, string)> WholeNumbers { get; } = [];

        public bool HasColumn(string table, string column) => Columns.Contains((table, column));
        public bool IsActivity(string table) => Activities.Contains(table);
        public bool CanBeParty(string table) => Parties.Contains(table);
        public IReadOnlyList<(int Value, string Label)> Options(string table, string column) => OptionSets.GetValueOrDefault((table, column)) ?? [];
        public IReadOnlyList<(int State, int Status, string Label)> Statuses(string table) => StatusSets.GetValueOrDefault(table) ?? [];
        public (int State, int Status) DefaultStatus(string table) => (0, 1);
        public bool IsWholeNumber(string table, string column) => WholeNumbers.Contains((table, column));
    }

    private sealed class FakeDerived : IDerivedValues
    {
        public Dictionary<(string, Guid, string), MigrationValue> Values { get; } = new();
        public HashSet<(string, Guid)> Shared { get; } = [];

        public MigrationValue? Get(string sourceTable, Guid id, string column) => Values.GetValueOrDefault((sourceTable, id, column));
        public bool IsShared(string sourceTable, Guid id) => Shared.Contains((sourceTable, id));
    }

    private sealed class Notes : ITranslationNotes
    {
        public List<(string Table, string? Column, string Code, Guid RowId)> All { get; } = [];
        public void Note(string targetTable, string? column, string code, Guid rowId) => All.Add((targetTable, column, code, rowId));

        /// <summary>The notes without <c>derived-defaulted</c>: rows of tests about other rules have no lookup to derive from.</summary>
        public List<(string Table, string? Column, string Code, Guid RowId)> Plain => All.Where(n => n.Code != "derived-defaulted").ToList();
    }

    private static FakeMapTarget Target()
    {
        var t = new FakeMapTarget();
        foreach (var c in new[] { "ownerid", "createdon" }) t.Columns.Add(("xrmv_lead", c));
        foreach (var c in new[] { "actualdurationminutes", "prioritycode", "regardingobjectid", "customers", "subject" })
            t.Columns.Add(("xrmv_opportunityclose", c));
        t.OptionSets[("xrmv_lead", "xrmv_source")] = [(100000000, "Web"), (100000006, "Other"), (100000008, "webinar"), (100000009, "trade fair"), (100000010, "")];
        t.OptionSets[("xrmv_lead", "xrmv_rating")] = [(100000000, "Hot"), (100000001, "Warm"), (100000002, "Cold")];
        t.StatusSets["xrmv_lead"] =
        [
            (0, 1, "New"), (0, 100000000, "Contacted"), (0, 100000007, "No budget"),
            (1, 2, "Qualified"), (1, 100000001, "Lost"), (1, 100000004, "Cannot contact"), (1, 100000005, "No Budget"),
        ];
        return t;
    }

    private static RecordTranslator Translator(FakeMapTarget? target = null, FakeDerived? derived = null, MapSet? maps = null) =>
        new(maps ?? SalesMaps, Catalog, target ?? Target(), derived ?? new FakeDerived());

    private static MapSet Maps(string yaml) => MapSet.Combine([MapReader.Parse("version: 1\n" + yaml, "t.yaml")]);

    private static readonly string[] SalesDerivedColumns = ["xrmv_qualifiedon", "xrmv_outcome", "xrmv_reason"];

    /// <summary>The values without the derived columns of the reference map (see <see cref="Notes.Plain"/>).</summary>
    private static Dictionary<string, MigrationValue> Plain(MigrationRecord record) =>
        record.Values.Where(v => !SalesDerivedColumns.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value);

    private static MigrationRecord Row(params (string Column, MigrationValue Value)[] values) =>
        new(RowId, values.ToDictionary(v => v.Column, v => v.Value));

    // R2 / §6.1

    [Fact]
    public void Column_of_a_listed_column_is_its_target_and_any_other_keeps_its_name()
    {
        Translator().ColumnOf("lead", "subject").Should().Be("xrmv_topic");
        Translator().ColumnOf("lead", "new_score").Should().Be("new_score");
        Translator().ColumnOf("contact", "fullname").Should().Be("fullname");
    }

    [Fact]
    public void Renamed_table_keeps_system_columns_and_drops_unlisted_standard_ones()
    {
        var notes = new Notes();
        var created = new DateTimeValue(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var result = Translator().Translate("lead", Row(
            ("ownerid", new LookupValue("systemuser", OppA)), ("createdon", created), ("subject", new StringValue("Hello")),
            ("address2_city", new StringValue("Springfield"))), notes);

        Plain(result).Should().BeEquivalentTo(new Dictionary<string, MigrationValue>
        {
            ["ownerid"] = new LookupValue("systemuser", OppA), ["createdon"] = created, ["xrmv_topic"] = new StringValue("Hello"),
        });
        notes.Plain.Should().Equal(("xrmv_lead", "address2_city", "column-not-mapped", RowId));
    }

    [Fact]
    public void Unlisted_empty_column_is_not_noted()
    {
        var notes = new Notes();
        Plain(Translator().Translate("lead", Row(("address2_city", NullValue.Instance)), notes)).Should().BeEmpty();
        notes.Plain.Should().BeEmpty();
    }

    [Fact]
    public void Activity_to_activity_passes_activity_columns_but_not_activitytypecode()
    {
        var notes = new Notes();
        var customers = new PartyListValue([new Party(11, "account", OppB, null)]);
        var result = Translator().Translate("opportunityclose", Row(
            ("actualdurationminutes", new IntValue(30)), ("prioritycode", new OptionValue(1)),
            ("activitytypecode", new StringValue("opportunityclose")), ("customers", customers)), notes);

        Plain(result).Keys.Should().BeEquivalentTo("actualdurationminutes", "prioritycode", "customers");
        result.Values["customers"].Should().BeSameAs(customers);
        notes.Plain.Should().BeEmpty();
    }

    [Fact]
    public void Same_table_entry_renames_only_listed_columns()
    {
        var notes = new Notes();
        var result = Translator().Translate("account", Row(
            ("accountid", new GuidValue(RowId)), ("creditlimit", new MoneyValue(10m)), ("address2_city", new StringValue("Springfield")),
            ("statecode", new OptionValue(1))), notes);

        result.Values.Should().BeEquivalentTo(new Dictionary<string, MigrationValue>
        {
            ["accountid"] = new GuidValue(RowId), ["xrmv_creditlimit"] = new MoneyValue(10m),
            ["address2_city"] = new StringValue("Springfield"), ["statecode"] = new OptionValue(1),
        });
        notes.Plain.Should().BeEmpty();
    }

    // R7 at row level

    [Fact]
    public void Custom_columns_carry_or_drop()
    {
        var carryNotes = new Notes();
        var carried = Translator().Translate("lead", Row(("new_score", new IntValue(7)), ("msdyn_gdpr", new BoolValue(true))), carryNotes);
        Plain(carried).Should().BeEquivalentTo(new Dictionary<string, MigrationValue> { ["new_score"] = new IntValue(7) });
        carryNotes.Plain.Should().Equal(("xrmv_lead", "msdyn_gdpr", "column-not-mapped", RowId));

        var dropNotes = new Notes();
        var drop = Maps("tables: [{source: lead, target: xrmv_lead, customColumns: drop}]");
        Translator(maps: drop).Translate("lead", Row(("new_score", new IntValue(7))), dropNotes).Values.Should().BeEmpty();
        dropNotes.Plain.Should().Equal(("xrmv_lead", "new_score", "column-dropped", RowId));
    }

    // R3

    [Fact]
    public void Values_map_first()
    {
        var notes = new Notes();
        Translator().Translate("lead", Row(("leadsourcecode", new OptionValue(8))), notes)
            .Values["xrmv_source"].Should().Be(new OptionValue(100000000));
        notes.Plain.Should().BeEmpty();
    }

    [Fact]
    public void By_label_after_values()
    {
        var notes = new Notes();
        Translator().Translate("lead", Row(("leadsourcecode", new OptionValue(100000010))), notes)
            .Values["xrmv_source"].Should().Be(new OptionValue(100000008));
        notes.Plain.Should().Equal(("xrmv_lead", "xrmv_source", "value-by-label", RowId));
    }

    [Fact]
    public void By_label_ignores_case_and_spaces_and_skips_empty_labels()
    {
        var notes = new Notes();
        var translator = Translator();
        translator.Translate("lead", Row(("leadsourcecode", new OptionValue(100000011))), notes)
            .Values["xrmv_source"].Should().Be(new OptionValue(100000009));
        notes.Plain.Should().Equal(("xrmv_lead", "xrmv_source", "value-by-label", RowId));

        foreach (var unlabelled in new[] { 100000012, 100000013 })
        {
            var n = new Notes();
            translator.Translate("lead", Row(("leadsourcecode", new OptionValue(unlabelled))), n)
                .Values["xrmv_source"].Should().Be(new OptionValue(100000006));
            n.Plain.Should().Equal(("xrmv_lead", "xrmv_source", "value-defaulted", RowId));
        }
    }

    [Fact]
    public void Fallback_last_and_counted()
    {
        var notes = new Notes();
        Translator().Translate("lead", Row(("leadsourcecode", new OptionValue(100000099))), notes)
            .Values["xrmv_source"].Should().Be(new OptionValue(100000006));
        notes.Plain.Should().Equal(("xrmv_lead", "xrmv_source", "value-defaulted", RowId));
    }

    [Fact]
    public void Fallback_null_empties_and_counts()
    {
        var notes = new Notes();
        Translator().Translate("lead", Row(("leadqualitycode", new OptionValue(4))), notes)
            .Values["xrmv_rating"].Should().Be(NullValue.Instance);
        notes.Plain.Should().Equal(("xrmv_lead", "xrmv_rating", "value-defaulted", RowId));
    }

    [Fact]
    public void Empty_value_stays_empty()
    {
        var notes = new Notes();
        Translator().Translate("lead", Row(("leadsourcecode", NullValue.Instance)), notes)
            .Values["xrmv_source"].Should().Be(NullValue.Instance);
        notes.Plain.Should().BeEmpty();
    }

    private const string ThingMap = """
        tables:
          - source: new_thing
            target: xrmv_thing
            columns:
              new_kind: xrmv_kind
              new_level: { target: xrmv_level, fallback: 3 }
              new_tags: { target: xrmv_tags, values: { 1: 10, 2: 20 }, fallback: null }
              prioritycode: { target: xrmv_priority, values: { 1: 5 } }
        """;

    private static FakeMapTarget ThingTarget()
    {
        var t = Target();
        t.OptionSets[("xrmv_thing", "xrmv_kind")] = [(1, "One"), (2, "Two")];
        t.OptionSets[("xrmv_thing", "xrmv_level")] = [(1, "Low"), (3, "High")];
        t.OptionSets[("xrmv_thing", "xrmv_tags")] = [(10, "A"), (20, "B")];
        t.WholeNumbers.Add(("xrmv_thing", "xrmv_priority"));
        t.StatusSets["xrmv_thing"] = [(0, 1, "Active"), (1, 2, "Inactive")];
        return t;
    }

    [Fact]
    public void No_values_no_bylabel_copies_when_target_has_it()
    {
        var translator = Translator(ThingTarget(), maps: Maps(ThingMap));

        var notes = new Notes();
        var result = translator.Translate("new_thing", Row(("new_kind", new OptionValue(2)), ("new_level", new OptionValue(1))), notes);
        result.Values["xrmv_kind"].Should().Be(new OptionValue(2));
        result.Values["xrmv_level"].Should().Be(new OptionValue(1));
        notes.Plain.Should().BeEmpty();

        var missing = new Notes();
        var other = translator.Translate("new_thing", Row(("new_kind", new OptionValue(9)), ("new_level", new OptionValue(9))), missing);
        other.Values["xrmv_kind"].Should().Be(NullValue.Instance);
        other.Values["xrmv_level"].Should().Be(new OptionValue(3));
        missing.Plain.Should().BeEquivalentTo(new[]
        {
            ("xrmv_thing", (string?)"xrmv_kind", "value-defaulted", RowId), ("xrmv_thing", (string?)"xrmv_level", "value-defaulted", RowId),
        });
    }

    [Fact]
    public void Multi_select_translates_each_value()
    {
        var notes = new Notes();
        var result = Translator(ThingTarget(), maps: Maps(ThingMap))
            .Translate("new_thing", Row(("new_tags", new OptionsValue([1, 7, 2, 8]))), notes);
        ((OptionsValue)result.Values["xrmv_tags"]).Values.Should().Equal(10, 20);
        notes.Plain.Should().Equal(("xrmv_thing", "xrmv_tags", "value-defaulted", RowId));
    }

    [Fact]
    public void Whole_number_target_takes_mapped_number()
    {
        var notes = new Notes();
        Translator(ThingTarget(), maps: Maps(ThingMap)).Translate("new_thing", Row(("prioritycode", new OptionValue(1))), notes)
            .Values["xrmv_priority"].Should().Be(new IntValue(5));
        notes.Plain.Should().BeEmpty();
    }

    // R4

    private static (MigrationValue State, MigrationValue Status, List<(string, string?, string, Guid)> Notes) Lead(int state, int status)
    {
        var notes = new Notes();
        var result = Translator().Translate("lead", Row(("statecode", new OptionValue(state)), ("statuscode", new OptionValue(status))), notes);
        return (result.Values["statecode"], result.Values["statuscode"], notes.Plain);
    }

    [Fact]
    public void Exact_state_pair()
    {
        var (state, status, notes) = Lead(2, 5);
        state.Should().Be(new OptionValue(1));
        status.Should().Be(new OptionValue(100000002));
        notes.Should().BeEmpty();
    }

    [Fact]
    public void Custom_status_by_label_within_mapped_state()
    {
        var (state, status, notes) = Lead(2, 100000009);
        state.Should().Be(new OptionValue(1));
        status.Should().Be(new OptionValue(100000005));
        notes.Should().Equal(("xrmv_lead", "statuscode", "status-by-label", RowId));
    }

    [Fact]
    public void Unmatched_status_goes_to_state_fallback()
    {
        var (state, status, notes) = Lead(2, 100000003);
        state.Should().Be(new OptionValue(1));
        status.Should().Be(new OptionValue(100000004));
        notes.Should().Equal(("xrmv_lead", "statuscode", "status-defaulted", RowId));
    }

    [Fact]
    public void State_only_in_stateFallback_searches_by_label_in_the_fallback_state()
    {
        var maps = Maps("""
            tables:
              - source: new_thing
                target: xrmv_thing
                state:
                  - { from: [0, 1], to: [0, 1] }
                stateFallback: { 1: [1, 2] }
            """);
        var target = ThingTarget();
        target.StatusSets["xrmv_thing"] = [(0, 1, "Active"), (0, 100000004, "Retired"), (1, 2, "Inactive"), (1, 100000003, "retired ")];
        var translator = Translator(target, maps: maps);

        var byLabel = new Notes();
        var found = translator.Translate("new_thing", Row(("statecode", new OptionValue(1)), ("statuscode", new OptionValue(100000005))), byLabel);
        found.Values["statecode"].Should().Be(new OptionValue(1));
        found.Values["statuscode"].Should().Be(new OptionValue(100000003));
        byLabel.All.Should().Equal(("xrmv_thing", "statuscode", "status-by-label", RowId));

        var unmatched = new Notes();
        var defaulted = translator.Translate("new_thing", Row(("statecode", new OptionValue(1)), ("statuscode", new OptionValue(100000006))), unmatched);
        defaulted.Values["statecode"].Should().Be(new OptionValue(1));
        defaulted.Values["statuscode"].Should().Be(new OptionValue(2));
        unmatched.All.Should().Equal(("xrmv_thing", "statuscode", "status-defaulted", RowId));
    }

    [Fact]
    public void No_state_section_copies_valid_pair_else_default()
    {
        var translator = Translator(ThingTarget(), maps: Maps(ThingMap));

        var valid = new Notes();
        var copied = translator.Translate("new_thing", Row(("statecode", new OptionValue(1)), ("statuscode", new OptionValue(2))), valid);
        copied.Values["statecode"].Should().Be(new OptionValue(1));
        copied.Values["statuscode"].Should().Be(new OptionValue(2));
        valid.Plain.Should().BeEmpty();

        var invalid = new Notes();
        var defaulted = translator.Translate("new_thing", Row(("statecode", new OptionValue(1)), ("statuscode", new OptionValue(100000001))), invalid);
        defaulted.Values["statecode"].Should().Be(new OptionValue(0));
        defaulted.Values["statuscode"].Should().Be(new OptionValue(1));
        invalid.Plain.Should().Equal(("xrmv_thing", "statuscode", "status-defaulted", RowId));
    }

    // R5

    [Fact]
    public void Lookup_to_mapped_table_is_retargeted()
    {
        var notes = new Notes();
        var result = Translator().Translate("task", Row(
            ("regardingobjectid", new LookupValue("lead", OppA)), ("ownerid", new LookupValue("systemuser", OppB)),
            ("parentid", new LookupValue("account", OppB))), notes);

        result.Values["regardingobjectid"].Should().Be(new LookupValue("xrmv_lead", OppA));
        result.Values["ownerid"].Should().Be(new LookupValue("systemuser", OppB));
        result.Values["parentid"].Should().Be(new LookupValue("account", OppB));
        notes.Plain.Should().BeEmpty();
    }

    [Fact]
    public void Regarding_and_objecttypecode_name_and_code_are_retargeted()
    {
        var result = Translator().Translate("annotation", Row(
            ("objecttypecode", new StringValue("lead")), ("regardingobjecttypecode", new IntValue(LeadOtc)),
            ("othertypecode", new StringValue("lead")), ("ownertypecode", new IntValue(LeadOtc))), null);

        result.Values["objecttypecode"].Should().Be(new StringValue("xrmv_lead"));
        result.Values["regardingobjecttypecode"].Should().Be(new StringValue("xrmv_lead"));
        result.Values["othertypecode"].Should().Be(new StringValue("lead"));
        result.Values["ownertypecode"].Should().Be(new IntValue(LeadOtc));
    }

    [Fact]
    public void Party_of_mapped_table_retargeted_or_left_out()
    {
        var parties = new PartyListValue([new Party(2, "lead", OppA, null), new Party(2, "contact", OppB, null), new Party(2, null, null, "x@example.com")]);

        var notes = new Notes();
        var removed = (PartyListValue)Translator().Translate("email", Row(("to", parties)), notes).Values["to"];
        removed.Parties.Should().Equal(new Party(2, "contact", OppB, null), new Party(2, null, null, "x@example.com"));
        notes.Plain.Should().Equal(("email", "to", "party-not-allowed", RowId));

        var allowing = Target();
        allowing.Parties.Add("xrmv_lead");
        var kept = new Notes();
        var retargeted = (PartyListValue)Translator(allowing).Translate("email", Row(("to", parties)), kept).Values["to"];
        retargeted.Parties[0].Should().Be(new Party(2, "xrmv_lead", OppA, null));
        retargeted.Parties.Should().HaveCount(3);
        kept.Plain.Should().BeEmpty();
    }

    // R6

    private static FakeDerived OpportunityOutcome(int state, int status)
    {
        var d = new FakeDerived();
        d.Values[("opportunity", OppA, "statecode")] = new OptionValue(state);
        d.Values[("opportunity", OppA, "statuscode")] = new OptionValue(status);
        return d;
    }

    private static FakeMapTarget CloseTarget()
    {
        var t = Target();
        t.OptionSets[("xrmv_opportunityclose", "xrmv_outcome")] = [(100000000, "Won"), (100000001, "Lost")];
        t.WholeNumbers.Add(("xrmv_opportunityclose", "xrmv_reason"));
        return t;
    }

    [Fact]
    public void Derived_takes_the_referenced_column_through_values()
    {
        var notes = new Notes();
        var result = Translator(CloseTarget(), OpportunityOutcome(2, 4))
            .Translate("opportunityclose", Row(("opportunityid", new LookupValue("opportunity", OppA))), notes);

        result.Values["xrmv_outcome"].Should().Be(new OptionValue(100000001));
        result.Values["xrmv_reason"].Should().Be(new IntValue(100000001));
        result.Values["regardingobjectid"].Should().Be(new LookupValue("xrmv_opportunity", OppA));
        notes.All.Should().BeEmpty();
    }

    [Fact]
    public void Derived_with_empty_lookup_uses_fallback()
    {
        var notes = new Notes();
        var result = Translator(CloseTarget(), OpportunityOutcome(2, 4))
            .Translate("opportunityclose", Row(("opportunityid", NullValue.Instance)), notes);

        result.Values["xrmv_outcome"].Should().Be(NullValue.Instance);
        result.Values["xrmv_reason"].Should().Be(NullValue.Instance);
        notes.All.Should().BeEquivalentTo(new[]
        {
            ("xrmv_opportunityclose", (string?)"xrmv_outcome", "derived-defaulted", RowId),
            ("xrmv_opportunityclose", (string?)"xrmv_reason", "derived-defaulted", RowId),
        });
    }

    [Fact]
    public void Derived_referenced_row_missing_uses_fallback()
    {
        var notes = new Notes();
        var result = Translator(CloseTarget(), new FakeDerived())
            .Translate("opportunityclose", Row(("opportunityid", new LookupValue("opportunity", OppB))), notes);

        result.Values["xrmv_outcome"].Should().Be(NullValue.Instance);
        notes.All.Should().Contain(("xrmv_opportunityclose", "xrmv_outcome", "derived-defaulted", RowId));
    }

    [Fact]
    public void Derived_from_a_lookup_that_was_not_exported_is_like_an_empty_lookup()
    {
        var notes = new Notes();
        var result = Translator(CloseTarget(), OpportunityOutcome(2, 4))
            .Translate("opportunityclose", Row(("subject", new StringValue("x"))), notes);

        result.Values["xrmv_outcome"].Should().Be(NullValue.Instance);
        result.Values["xrmv_reason"].Should().Be(NullValue.Instance);
        notes.All.Should().BeEquivalentTo(new[]
        {
            ("xrmv_opportunityclose", (string?)"xrmv_outcome", "derived-defaulted", RowId),
            ("xrmv_opportunityclose", (string?)"xrmv_reason", "derived-defaulted", RowId),
        });
    }

    [Fact]
    public void Derived_numeric_fallback_is_written_as_choice_or_whole_number()
    {
        var maps = Maps("""
            tables:
              - source: new_thing
                target: xrmv_thing
                derived:
                  xrmv_choice: { from: new_parentid.new_kind, fallback: 3 }
                  xrmv_number: { from: new_parentid.new_kind, fallback: 4 }
            """);
        var target = ThingTarget();
        target.OptionSets[("xrmv_thing", "xrmv_choice")] = [(3, "Three")];
        target.WholeNumbers.Add(("xrmv_thing", "xrmv_number"));
        var notes = new Notes();
        var result = Translator(target, maps: maps).Translate("new_thing", Row(("new_parentid", NullValue.Instance)), notes);

        result.Values["xrmv_choice"].Should().Be(new OptionValue(3));
        result.Values["xrmv_number"].Should().Be(new IntValue(4));
        notes.All.Should().BeEquivalentTo(new[]
        {
            ("xrmv_thing", (string?)"xrmv_choice", "derived-defaulted", RowId),
            ("xrmv_thing", (string?)"xrmv_number", "derived-defaulted", RowId),
        });
    }

    [Fact]
    public void Derived_shared_is_noted()
    {
        var derived = OpportunityOutcome(1, 3);
        derived.Shared.Add(("opportunity", OppA));
        var notes = new Notes();
        var result = Translator(CloseTarget(), derived)
            .Translate("opportunityclose", Row(("opportunityid", new LookupValue("opportunity", OppA))), notes);

        result.Values["xrmv_outcome"].Should().Be(new OptionValue(100000000));
        result.Values["xrmv_reason"].Should().Be(new IntValue(2));
        notes.All.Should().BeEquivalentTo(new[]
        {
            ("xrmv_opportunityclose", (string?)"xrmv_outcome", "derived-shared", RowId),
            ("xrmv_opportunityclose", (string?)"xrmv_reason", "derived-shared", RowId),
        });
    }

    [Fact]
    public void Derived_without_values_copies_the_referenced_value()
    {
        var created = new DateTimeValue(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        var derived = new FakeDerived();
        derived.Values[("opportunity", OppA, "createdon")] = created;
        var notes = new Notes();
        Translator(derived: derived).Translate("lead", Row(("qualifyingopportunityid", new LookupValue("opportunity", OppA))), notes)
            .Values["xrmv_qualifiedon"].Should().Be(created);
        notes.All.Should().BeEmpty();
    }

    // Rule 9

    [Fact]
    public void Listed_column_wins_over_implicit_one()
    {
        var notes = new Notes();
        var result = Translator(CloseTarget(), OpportunityOutcome(2, 4)).Translate("opportunityclose", Row(
            ("opportunityid", new LookupValue("opportunity", OppA)), ("regardingobjectid", new LookupValue("opportunity", OppB))), notes);

        result.Values["regardingobjectid"].Should().Be(new LookupValue("xrmv_opportunity", OppA));
        notes.All.Should().Equal(("xrmv_opportunityclose", "regardingobjectid", "column-overridden", RowId));

        var same = new Notes();
        Translator(CloseTarget(), OpportunityOutcome(2, 4)).Translate("opportunityclose", Row(
            ("opportunityid", new LookupValue("opportunity", OppA)), ("regardingobjectid", new LookupValue("opportunity", OppA))), same);
        same.All.Should().BeEmpty();
    }

    // Primary key

    [Fact]
    public void Primary_key_takes_the_target_name_same_id()
    {
        var notes = new Notes();
        var result = Translator().Translate("lead", Row(("leadid", new GuidValue(RowId)), ("subject", new StringValue("x"))), notes);

        result.Id.Should().Be(RowId);
        Plain(result).Keys.Should().Equal("xrmv_topic");
        notes.Plain.Should().BeEmpty();
    }

    [Fact]
    public void Table_and_relationship_names()
    {
        var translator = Translator();
        translator.TableOf("lead").Should().Be("xrmv_lead");
        translator.TableOf("account").Should().Be("account");
        translator.TableOf("task").Should().Be("task");
        translator.RelationshipOf("leadcompetitors").Should().Be("xrmv_lead_xrmv_competitor");
        translator.RelationshipOf("other").Should().BeNull();
    }
}
