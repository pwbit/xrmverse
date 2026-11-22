namespace Xrmd.Tds.Translation;

/// <summary>
/// A batch the SQL endpoint refuses, with the SQL Server error number the client sees (class 16). Thrown before anything runs.
/// </summary>
public sealed class TdsSqlException(int number, string message) : Exception(message)
{
    public int Number { get; } = number;
}

/// <summary>The SQL Server error numbers of a refused batch (generic SQL Server numbers, our own message text).</summary>
public static class TdsSqlErrors
{
    /// <summary>The batch uses a construct the endpoint does not support (ruling T6).</summary>
    public const int Unsupported = 40000;

    /// <summary>A column that does not exist or cannot be read over SQL (ruling T7).</summary>
    public const int InvalidColumn = 207;

    /// <summary>A table that does not exist.</summary>
    public const int InvalidObject = 208;

    /// <summary>A column name that more than one table of the query has.</summary>
    public const int AmbiguousColumn = 209;

    /// <summary>Two tables of the query have the same exposed name (table name or alias).</summary>
    public const int DuplicateExposedName = 1013;

    /// <summary>The SQL text cannot be parsed.</summary>
    public const int Syntax = 102;

    /// <summary>A variable that was not passed as a parameter.</summary>
    public const int UndeclaredVariable = 137;

    public static TdsSqlException NotSupported(string construct) =>
        new(Unsupported, $"The SQL endpoint does not support {construct}.");

    // Guardrails §2: SQL Server's exact text, kept on purpose. SQL tools (SSMS IntelliSense, ORMs) match these two messages to
    // tell an unknown column or table from other errors.
    public static TdsSqlException InvalidColumnName(string name) => new(InvalidColumn, $"Invalid column name '{name}'.");

    public static TdsSqlException InvalidObjectName(string name) => new(InvalidObject, $"Invalid object name '{name}'.");
}
