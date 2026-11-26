using Xrmd.Migration.Records;
using Xrmd.Migration.Source;

namespace Xrmd.Migration.Mapping;

/// <summary>
/// Turns one source record of a package into the record the import writes (migration table mapping spec §6.1–§6.5):
/// lookups, parties and object-type columns that name a mapped table are retargeted in every table (R5); the rows of a
/// mapped table get their columns renamed (R2, R7), their values (R3) and state (R4) translated, and derived values (R6); the rows of an
/// archive entry (spec §8.7) are copied into the columns of <see cref="ArchiveColumns"/>, values as they are.
/// Pure: everything it needs comes from the maps, the source catalog, <see cref="IMapTarget"/> and <see cref="IDerivedValues"/>.
/// </summary>
public sealed class RecordTranslator
{
    /// <summary>The system columns every migration keeps by name (spec §6.1).</summary>
    public static IReadOnlySet<string> SystemColumns { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "ownerid", "owningbusinessunit", "createdon", "createdby", "modifiedon", "modifiedby", "createdonbehalfby",
        "modifiedonbehalfby", "overriddencreatedon", "transactioncurrencyid", "exchangerate", "importsequencenumber",
        "timezoneruleversionnumber", "utcconversiontimezonecode",
    };

    /// <summary>The columns an activity → activity map keeps by name, besides every party-list column (spec §6.1).</summary>
    public static IReadOnlySet<string> ActivityColumns { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "subject", "description", "scheduledstart", "scheduledend", "actualstart", "actualend", "actualdurationminutes",
        "scheduleddurationminutes", "prioritycode", "regardingobjectid", "isbilled", "isworkflowcreated",
    };

    private const string StateCode = "statecode";
    private const string StatusCode = "statuscode";

    private readonly MapSet _maps;
    private readonly IMapTarget _target;
    private readonly IDerivedValues _derived;
    private readonly Dictionary<string, SourceCatalogTable> _tables;
    private readonly Dictionary<(string Table, string Column), SourceCatalogColumn> _columns;
    private readonly Dictionary<(string Table, string Column, int Value), string?> _labels;
    private readonly Dictionary<int, string> _targetByObjectTypeCode;
    private readonly Dictionary<(string Table, string Column), IReadOnlyList<(int Value, string Label)>> _targetOptions = new();
    private readonly SourceCatalog _catalog;
    private readonly Dictionary<string, (bool Exists, Dictionary<string, ArchiveColumn> Plan)> _archivePlans = new(StringComparer.Ordinal);
    private readonly Func<string, bool> _planned;

    /// <param name="planned">For a table name: true when the package's solutions or schema delta will create it (spec §8.7: a lookup of an
    /// archive table to it is kept). The same set as the dry run's and the schema phase's; null for none.</param>
    public RecordTranslator(MapSet maps, SourceCatalog catalog, IMapTarget target, IDerivedValues derived, Func<string, bool>? planned = null)
    {
        _planned = planned ?? (_ => false);
        _maps = maps;
        _target = target;
        _derived = derived;
        _catalog = catalog;
        _tables = catalog.Tables.ToDictionary(t => t.LogicalName, StringComparer.Ordinal);
        _columns = new();
        foreach (var t in catalog.Tables)
            foreach (var c in t.Columns) _columns.TryAdd((t.LogicalName, c.LogicalName), c);
        _labels = new();
        foreach (var o in catalog.Options) _labels.TryAdd((o.Table, o.Column, o.Value), o.Label);
        _targetByObjectTypeCode = new();
        foreach (var t in catalog.Tables)
            if (t.ObjectTypeCode is int code && maps.TargetOf(t.LogicalName) is string renamed)
                _targetByObjectTypeCode.TryAdd(code, renamed);
    }

    /// <summary>The target of a mapped table (renamed or not); any other table keeps its name.</summary>
    public string TableOf(string sourceTable) => _maps.BySource(sourceTable)?.Target ?? sourceTable;

    /// <summary>
    /// The target column of <paramref name="sourceTable"/>.<paramref name="sourceColumn"/>: the one its map entry lists, else the same
    /// name (a column a renamed table leaves out has no such column in the target).
    /// </summary>
    public string ColumnOf(string sourceTable, string sourceColumn) =>
        _maps.BySource(sourceTable)?.Columns.TryGetValue(sourceColumn, out var listed) == true ? listed.Target : sourceColumn;

    /// <summary>The target of a mapped N:N relationship; null when it is not mapped.</summary>
    public string? RelationshipOf(string sourceSchemaName) => _maps.RelationshipTargetOf(sourceSchemaName);

    /// <summary>
    /// The record of <paramref name="sourceTable"/> as the import writes it into <see cref="TableOf"/>. The id is unchanged;
    /// on a renamed table the source primary-key column is left out (the import names the target key).
    /// </summary>
    public MigrationRecord Translate(string sourceTable, MigrationRecord record, ITranslationNotes? notes)
    {
        var targetTable = TableOf(sourceTable);
        var row = new RowNotes(notes, targetTable, record.Id);

        // (1) R5 in every table.
        var values = Retarget(record.Values, row);

        // (2) A table without a map entry is done; an archive entry is a plain copy (spec §8.7).
        var entry = _maps.BySource(sourceTable);
        if (entry is null) return record with { Values = WithoutLookupNames(sourceTable, values) };
        if (entry.Archive) return record with { Values = TranslateArchive(entry, targetTable, record.Values, values, row) };

        // (3) Columns (§6.1). Implicit columns are added last, so that a listed or derived one wins (rule 9).
        var catalogTable = _tables.GetValueOrDefault(sourceTable);
        var output = new Dictionary<string, MigrationValue>(StringComparer.Ordinal);
        var implicitColumns = new List<(string Column, MigrationValue Value)>();
        foreach (var (column, value) in values)
        {
            if (IsLookupName(sourceTable, column)) continue;
            if (entry.Columns.TryGetValue(column, out var listed))
            {
                // (4) Values (§6.2).
                output[listed.Target] = TranslateValue(value, Rule.Of(listed), sourceTable, column, targetTable, listed.Target, "value-defaulted", row);
                continue;
            }
            if (!entry.IsRenamed) { implicitColumns.Add((column, value)); continue; }
            if (ImplicitColumn(entry, catalogTable, targetTable, column, value, row)) implicitColumns.Add((column, value));
        }

        // (5) State (§6.3); a same-table entry keeps its own (MapSet rejects state on one).
        if (entry.IsRenamed) TranslateState(entry, sourceTable, targetTable, values, output, row);

        // (6) Derived (§6.5), from the lookups as exported (before R5); a lookup column that was not exported counts as empty.
        foreach (var (column, derived) in entry.Derived)
            output[column] = DeriveValue(derived, record.Values.GetValueOrDefault(derived.LookupColumn) ?? NullValue.Instance, targetTable, column, row);

        foreach (var (column, value) in implicitColumns)
        {
            if (!output.TryGetValue(column, out var winner)) output[column] = value;
            else if (value is not NullValue && !Equals(winner, value)) row.Note(column, "column-overridden");
        }
        return record with { Values = output };
    }

    /// <summary>
    /// True when an unlisted column of a renamed table is written under its own name; otherwise it is left out, noted
    /// when it had a value.
    /// </summary>
    private bool ImplicitColumn(MapTable entry, SourceCatalogTable? catalogTable, string targetTable, string column, MigrationValue value, RowNotes row)
    {
        var primaryKey = catalogTable?.PrimaryKey ?? entry.Source + "id";
        if (column == primaryKey || column is StateCode or StatusCode or "activitytypecode" or "activityid") return false;

        var activity = (catalogTable?.IsActivity ?? false) && _target.IsActivity(targetTable);
        if (SystemColumns.Contains(column) || (activity && (ActivityColumns.Contains(column) || IsPartyList(entry.Source, column, value))))
        {
            if (_target.HasColumn(targetTable, column)) return true;
        }
        else if (IsCustom(entry.Source, column))
        {
            if (entry.CarryCustomColumns) return true;
            if (value is not NullValue) row.Note(column, "column-dropped");
            return false;
        }
        if (value is not NullValue) row.Note(column, "column-not-mapped");
        return false;
    }

    // Archive tables (spec §8.7)

    /// <summary>
    /// The row of an archive table: each column as <see cref="ArchiveColumns.Plan"/> says, values as they are (after R5). A lookup the
    /// archive keeps as text gets the referenced record's name when the package has it (<c>&lt;column&gt;name</c>), else nothing.
    /// </summary>
    private Dictionary<string, MigrationValue> TranslateArchive(MapTable entry, string targetTable, IReadOnlyDictionary<string, MigrationValue> raw,
        Dictionary<string, MigrationValue> values, RowNotes row)
    {
        var plan = ArchivePlan(entry, targetTable);
        var nameColumns = plan.Values.Where(c => c.Kind is ArchiveColumnKind.Lookup or ArchiveColumnKind.LookupAsText)
            .Select(c => c.Source.LogicalName + "name").ToHashSet(StringComparer.Ordinal);
        var output = new Dictionary<string, MigrationValue>(StringComparer.Ordinal);
        foreach (var (column, value) in values)
        {
            if (!plan.TryGetValue(column, out var planned))
            {
                if (!nameColumns.Contains(column) && value is not NullValue) row.Note(column, "column-not-mapped");
                continue;
            }
            switch (planned.Kind)
            {
                case ArchiveColumnKind.ByName or ArchiveColumnKind.Copied or ArchiveColumnKind.Lookup:
                    output[planned.Target!] = value;
                    break;
                case ArchiveColumnKind.LookupAsText:
                    // The referenced row's name as the export wrote it; its id when the source gave none (an older package).
                    output[planned.TextTarget!] = raw.GetValueOrDefault(column + "name") is StringValue { Value.Length: > 0 } name ? name
                        : raw.GetValueOrDefault(column) is LookupValue lookup ? new StringValue(lookup.Id.ToString("D"))
                        : NullValue.Instance;
                    if (value is not NullValue) row.Note(planned.TextTarget, "archive-lookup-as-text");
                    break;
                case ArchiveColumnKind.Dropped:
                    if (value is not NullValue) row.Note(column, "column-dropped");
                    break;
                case ArchiveColumnKind.Unsupported:
                    if (value is not NullValue) row.Note(column, "column-not-mapped");
                    break;
            }
        }
        if (_tables.GetValueOrDefault(entry.Source)?.IsActivity == true) ActivityState(targetTable, values, output);
        return output;
    }

    /// <summary>
    /// An activity archive keeps the source's state in its own: open (0), completed (1) or canceled (2), with that state's default status
    /// reason, so that a completed activity is not shown open. The source's exact state and status stay in <c>xrmva_statecode</c> and
    /// <c>xrmva_statuscode</c>. A table's states are fixed in xrmverse; an activity has these three.
    /// </summary>
    private void ActivityState(string targetTable, IReadOnlyDictionary<string, MigrationValue> values, Dictionary<string, MigrationValue> output)
    {
        if (values.GetValueOrDefault(StateCode) is not OptionValue { Value: >= 0 and <= 2 } state) return;
        var status = _target.DefaultStatusOf(targetTable, state.Value) ?? ActivityDefaultStatus[state.Value];
        output[StateCode] = new OptionValue(state.Value);
        output[StatusCode] = new OptionValue(status);
    }

    /// <summary>The default status reason of each activity state (open, completed, canceled) before the archive table exists.</summary>
    private static readonly int[] ActivityDefaultStatus = [1, 2, 3];

    /// <summary>
    /// A name the export writes beside a lookup of a table an archive may be made from (<c>&lt;column&gt;name</c>, spec §8.7): not a
    /// column of the source, only what an archive's text column takes.
    /// </summary>
    private bool IsLookupName(string sourceTable, string column) =>
        column.EndsWith("name", StringComparison.Ordinal) && !_columns.ContainsKey((sourceTable, column))
        && _columns.GetValueOrDefault((sourceTable, column[..^4])) is { AttributeType: "Lookup" or "Customer" or "Owner" };

    private Dictionary<string, MigrationValue> WithoutLookupNames(string sourceTable, Dictionary<string, MigrationValue> values)
    {
        foreach (var column in values.Keys.Where(c => IsLookupName(sourceTable, c)).ToList()) values.Remove(column);
        return values;
    }

    /// <summary>
    /// The archive plan of a source table, by source column. A lookup is kept or made text as the archive table has it once it exists;
    /// before (the census of phase 1, a dry run) as the schema phase will decide: kept when its tables are in the target, mapped or archived.
    /// </summary>
    private Dictionary<string, ArchiveColumn> ArchivePlan(MapTable entry, string targetTable)
    {
        var exists = _target.HasTable(targetTable);
        if (_archivePlans.TryGetValue(entry.Source, out var cached) && cached.Exists == exists) return cached.Plan;
        var table = _tables.GetValueOrDefault(entry.Source) ?? new SourceCatalogTable(entry.Source, entry.Source + "id", null, "user", false, []);
        var plan = new Dictionary<string, ArchiveColumn>(StringComparer.Ordinal);
        foreach (var column in ArchiveColumns.Plan(entry, table, _catalog, t => _maps.BySource(t) is not null || _target.HasTable(t) || _planned(t)))
        {
            var decided = column;
            if (exists && column.Kind is ArchiveColumnKind.Lookup or ArchiveColumnKind.LookupAsText)
                decided = column with { Kind = _target.HasColumn(targetTable, column.Target!) ? ArchiveColumnKind.Lookup : ArchiveColumnKind.LookupAsText };
            plan.TryAdd(column.Source.LogicalName, decided);
        }
        _archivePlans[entry.Source] = (exists, plan);
        return plan;
    }

    private bool IsPartyList(string sourceTable, string column, MigrationValue value) =>
        value is PartyListValue || _columns.GetValueOrDefault((sourceTable, column))?.AttributeType == "PartyList";

    private bool IsCustom(string sourceTable, string column) =>
        _columns.GetValueOrDefault((sourceTable, column)) is { } c && IsCustomColumn(c);

    /// <summary>A custom column of the source that <c>customColumns</c> governs: custom, and not of a first-party prefix.</summary>
    public static bool IsCustomColumn(SourceCatalogColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.IsCustom && !ExclusionCatalog.FirstPartyPrefixes.Any(p => column.LogicalName.StartsWith(p, StringComparison.Ordinal));
    }

    // R5

    private Dictionary<string, MigrationValue> Retarget(IReadOnlyDictionary<string, MigrationValue> values, RowNotes row)
    {
        var result = new Dictionary<string, MigrationValue>(values.Count, StringComparer.Ordinal);
        foreach (var (column, value) in values) result[column] = Retarget(column, value, row);
        return result;
    }

    private MigrationValue Retarget(string column, MigrationValue value, RowNotes row)
    {
        switch (value)
        {
            case LookupValue lookup when _maps.TargetOf(lookup.Table) is string target:
                return lookup with { Table = target };
            case PartyListValue list when list.Parties.Any(p => p.Table is not null && _maps.TargetOf(p.Table) is not null):
                var parties = new List<Party>(list.Parties.Count);
                foreach (var party in list.Parties)
                {
                    if (party.Table is null || _maps.TargetOf(party.Table) is not string target) { parties.Add(party); continue; }
                    if (_target.CanBeParty(target)) parties.Add(party with { Table = target });
                    else row.Note(column, "party-not-allowed");
                }
                return new PartyListValue(parties);
            case StringValue name when IsObjectTypeColumn(column) && _maps.TargetOf(name.Value) is string target:
                return new StringValue(target);
            case IntValue code when IsObjectTypeColumn(column) && _targetByObjectTypeCode.TryGetValue((int)code.Value, out var target):
                return new StringValue(target);
            default:
                return value;
        }
    }

    private static bool IsObjectTypeColumn(string column) => column.EndsWith("objecttypecode", StringComparison.Ordinal);

    // R3

    /// <summary><c>values</c>, <c>byLabel</c> and <c>fallback</c> of a listed or derived column.</summary>
    private sealed record Rule(IReadOnlyDictionary<int, int>? Values, bool ByLabel, MapFallback Fallback)
    {
        public static Rule Of(MapColumn c) => new(c.Values, c.ByLabel, c.Fallback);
        public static Rule Of(MapDerived d) => new(d.Values, d.ByLabel, d.Fallback);
        public bool IsSet => Values is not null || ByLabel || Fallback.IsSet;
    }

    /// <summary>
    /// Translates a choice (or a whole number with a rule) of the source column <paramref name="labelTable"/>.<paramref name="labelColumn"/>
    /// into <paramref name="targetTable"/>.<paramref name="targetColumn"/>; any other value is only renamed.
    /// </summary>
    private MigrationValue TranslateValue(MigrationValue value, Rule rule, string labelTable, string labelColumn, string targetTable, string targetColumn,
        string defaultedCode, RowNotes row)
    {
        int? One(int source)
        {
            var (result, code) = TranslateNumber(source, rule, labelTable, labelColumn, targetTable, targetColumn, defaultedCode);
            if (code is not null) row.Note(targetColumn, code);
            return result;
        }

        switch (value)
        {
            case OptionValue option:
                return Write(One(option.Value), targetTable, targetColumn);
            case IntValue number when rule.IsSet:
                return Write(One((int)number.Value), targetTable, targetColumn);
            case OptionsValue options:
                var translated = options.Values.Select(One).OfType<int>().Distinct().ToList();
                return translated.Count == 0 && options.Values.Count > 0 ? NullValue.Instance : new OptionsValue(translated);
            default:
                return value;
        }
    }

    private (int? Value, string? Code) TranslateNumber(int source, Rule rule, string labelTable, string labelColumn, string targetTable, string targetColumn,
        string defaultedCode)
    {
        if (rule.Values is not null && rule.Values.TryGetValue(source, out var mapped)) return (mapped, null);

        if (rule.ByLabel)
        {
            var label = _labels.GetValueOrDefault((labelTable, labelColumn, source));
            foreach (var (value, targetLabel) in TargetOptions(targetTable, targetColumn))
                if (SameLabel(label, targetLabel)) return (value, "value-by-label");
        }

        var targetHasIt = _target.IsWholeNumber(targetTable, targetColumn) || TargetOptions(targetTable, targetColumn).Any(o => o.Value == source);
        if (rule.Values is null && !rule.ByLabel && targetHasIt) return (source, null);
        if (rule.Fallback.IsSet) return (rule.Fallback.Value, defaultedCode);
        return targetHasIt ? (source, null) : (null, defaultedCode);
    }

    private IReadOnlyList<(int Value, string Label)> TargetOptions(string table, string column)
    {
        if (!_targetOptions.TryGetValue((table, column), out var options))
            _targetOptions[(table, column)] = options = _target.Options(table, column);
        return options;
    }

    private MigrationValue Write(int? value, string targetTable, string targetColumn) => value switch
    {
        null => NullValue.Instance,
        int v when _target.IsWholeNumber(targetTable, targetColumn) => new IntValue(v),
        int v => new OptionValue(v),
    };

    private static bool SameLabel(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // R4

    private void TranslateState(MapTable entry, string sourceTable, string targetTable, IReadOnlyDictionary<string, MigrationValue> values,
        Dictionary<string, MigrationValue> output, RowNotes row)
    {
        if (AsInt(values.GetValueOrDefault(StateCode)) is not int state) return;
        var status = AsInt(values.GetValueOrDefault(StatusCode));

        var (pair, code) = entry.State.Count == 0 && entry.StateFallback.Count == 0
            ? CopyState(targetTable, state, status)
            : MapState(entry, sourceTable, targetTable, state, status);
        output[StateCode] = new OptionValue(pair.State);
        output[StatusCode] = new OptionValue(pair.Status);
        if (code is not null) row.Note(StatusCode, code);
    }

    /// <summary>No <c>state</c> section: the pair when the target has it, else the target's default pair.</summary>
    private ((int State, int Status) Pair, string? Code) CopyState(string targetTable, int state, int? status) =>
        status is int s && _target.Statuses(targetTable).Any(x => x.State == state && x.Status == s)
            ? ((state, s), null)
            : (_target.DefaultStatus(targetTable), "status-defaulted");

    private ((int State, int Status) Pair, string? Code) MapState(MapTable entry, string sourceTable, string targetTable, int state, int? status)
    {
        if (status is int s)
        {
            var exact = entry.State.FirstOrDefault(p => p.From == (state, s));
            if (exact is not null) return (exact.To, null);

            // Rule 4: every pair of a source state names the same target state; a state only in stateFallback searches
            // the state of its fallback pair.
            var targetState = entry.State.FirstOrDefault(p => p.From.State == state)?.To.State
                ?? (entry.StateFallback.TryGetValue(state, out var pair) ? pair.State : null);
            if (targetState is int searched)
            {
                var label = _labels.GetValueOrDefault((sourceTable, StatusCode, s));
                foreach (var (tState, tStatus, tLabel) in _target.Statuses(targetTable))
                    if (tState == searched && SameLabel(label, tLabel)) return ((tState, tStatus), "status-by-label");
            }
        }
        return entry.StateFallback.TryGetValue(state, out var fallback)
            ? (fallback, "status-defaulted")
            : (_target.DefaultStatus(targetTable), "status-defaulted");
    }

    private static int? AsInt(MigrationValue? value) => value switch
    {
        OptionValue o => o.Value,
        IntValue i => (int)i.Value,
        _ => null,
    };

    // R6

    private MigrationValue DeriveValue(MapDerived derived, MigrationValue lookupValue, string targetTable, string column, RowNotes row)
    {
        var rule = Rule.Of(derived);
        if (lookupValue is not LookupValue lookup
            || _derived.Get(lookup.Table, lookup.Id, derived.Column) is not { } value)
        {
            row.Note(column, "derived-defaulted");
            return Write(rule.Fallback.IsSet ? rule.Fallback.Value : null, targetTable, column);
        }
        if (_derived.IsShared(lookup.Table, lookup.Id)) row.Note(column, "derived-shared");
        return TranslateValue(value, rule, lookup.Table, derived.Column, targetTable, column, "derived-defaulted", row);
    }

    /// <summary>The notes of one row, each (column, code) once.</summary>
    private sealed class RowNotes(ITranslationNotes? notes, string targetTable, Guid rowId)
    {
        private readonly HashSet<(string? Column, string Code)> _seen = [];

        public void Note(string? column, string code)
        {
            if (notes is not null && _seen.Add((column, code))) notes.Note(targetTable, column, code, rowId);
        }
    }
}
