using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Migration.Mapping;
using Xrmd.Model.Metadata;

namespace Xrmd.Admin.Migration.Mapping;

/// <summary>
/// The target organization as the record translator sees it, read from the metadata store at each call (the schema phase creates
/// tables after the translator is attached). A table or column the organization lacks reads as "no column, no options": the map
/// check (R9) reports it, the translator does not fail on it.
/// </summary>
public sealed class MetadataMapTarget(IMetadataStore metadata) : IMapTarget
{
    /// <summary>The language of the labels compared with the source's: the organization's base language (xrmverse labels in 1033).</summary>
    public const int BaseLanguage = 1033;

    public bool HasTable(string table) => metadata.GetTable(table) is not null;

    public bool HasColumn(string table, string column) => Column(table, column) is not null;

    public bool IsActivity(string table) => metadata.GetTable(table)?.IsActivity == true;

    /// <summary>The tables a party of an activity may be (the rule the entity validator applies): account, contact, queue, systemuser.</summary>
    public bool CanBeParty(string table) => ActivitySchema.PartyRecordTables.Contains(table);

    public IReadOnlyList<(int Value, string Label)> Options(string table, string column) =>
        [.. OptionsOf(table, column).Where(o => o.Value is not null).Select(o => (o.Value!.Value, LabelOf(o.Label)))];

    public IReadOnlyList<(int State, int Status, string Label)> Statuses(string table) =>
        [.. OptionsOf(table, "statuscode").OfType<StatusOption>().Where(o => o.Value is not null && o.State is not null)
            .Select(o => (o.State!.Value, o.Value!.Value, LabelOf(o.Label)))];

    /// <summary>State 0 (else the lowest) with its default status reason (else its first one); (0, 1) for a table without a state.</summary>
    public (int State, int Status) DefaultStatus(string table)
    {
        var states = OptionsOf(table, "statecode").OfType<StateOption>().Where(o => o.Value is not null).OrderBy(o => o.Value).ToList();
        if (states.Count == 0) return (0, 1);
        var state = states.FirstOrDefault(s => s.Value == 0) ?? states[0];
        var status = state.DefaultStatus
                     ?? Statuses(table).Where(s => s.State == state.Value).Select(s => (int?)s.Status).FirstOrDefault()
                     ?? 1;
        return (state.Value!.Value, status);
    }

    public int? DefaultStatusOf(string table, int state) =>
        OptionsOf(table, "statecode").OfType<StateOption>().FirstOrDefault(o => o.Value == state)?.DefaultStatus
        ?? Statuses(table).Where(s => s.State == state).Select(s => (int?)s.Status).FirstOrDefault();

    public bool IsWholeNumber(string table, string column) => Column(table, column) is IntegerColumn;

    private ColumnDefinition? Column(string table, string column) =>
        metadata.GetTable(table)?.Attributes?.FirstOrDefault(a => a.LogicalName == column);

    private IEnumerable<ChoiceOption> OptionsOf(string table, string column) =>
        Column(table, column) is ChoiceColumnBase { OptionSet.Options: { } options } ? options : [];

    private static string LabelOf(LabelSet? label) =>
        label?.LocalizedLabels?.FirstOrDefault(l => l.LanguageCode == BaseLanguage)?.Label
        ?? label?.UserLocalizedLabel?.Label
        ?? label?.LocalizedLabels?.FirstOrDefault()?.Label
        ?? "";
}
