using Xrmd.Migration.Records;

namespace Xrmd.Migration.Mapping;

/// <summary>What the record translator needs to know about the target organization (logical names of the target).</summary>
public interface IMapTarget
{
    /// <summary>True when the organization has <paramref name="table"/> (an archive table once the schema phase has created it).</summary>
    bool HasTable(string table) => false;

    bool HasColumn(string table, string column);

    bool IsActivity(string table);

    /// <summary>True when a record of <paramref name="table"/> may be an activity party.</summary>
    bool CanBeParty(string table);

    /// <summary>The options of a choice column with their labels in the organization's base language.</summary>
    IReadOnlyList<(int Value, string Label)> Options(string table, string column);

    /// <summary>The status reasons of a table, each with its state and its base-language label.</summary>
    IReadOnlyList<(int State, int Status, string Label)> Statuses(string table);

    (int State, int Status) DefaultStatus(string table);

    /// <summary>The default status reason of <paramref name="state"/> of a table; null when the organization does not tell (no such table yet).</summary>
    int? DefaultStatusOf(string table, int state) => null;

    /// <summary>True when the column holds a whole number (not a choice): translated values are written as numbers.</summary>
    bool IsWholeNumber(string table, string column);
}

/// <summary>The values of referenced rows that <c>derived</c> entries read (spec §6.5), by source names.</summary>
public interface IDerivedValues
{
    /// <summary>The value of <paramref name="column"/> of the referenced row; null when the row is not in the package.</summary>
    MigrationValue? Get(string sourceTable, Guid id, string column);

    /// <summary>True when more than one source row takes derived values from this referenced row.</summary>
    bool IsShared(string sourceTable, Guid id);
}

/// <summary>Receives what the translator did to a row; called at most once per row for each (column, code).</summary>
public interface ITranslationNotes
{
    /// <param name="targetTable">The target table.</param>
    /// <param name="column">The target column, or the source column for <c>column-not-mapped</c>.</param>
    /// <param name="code">An issue code of the migration table mapping (<c>value-by-label</c>, <c>status-defaulted</c>, …).</param>
    void Note(string targetTable, string? column, string code, Guid rowId);
}
