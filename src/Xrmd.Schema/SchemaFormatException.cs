namespace Xrmd.Schema;

/// <summary>A schema YAML file that is not valid: the message names the problem and where it is (1-based line and column).</summary>
public sealed class SchemaFormatException : Exception
{
    public SchemaFormatException(string message, long line, long column, Exception? inner = null)
        : base($"(line {line}, column {column}): {message}", inner)
    {
        Line = line;
        Column = column;
    }

    public long Line { get; }

    public long Column { get; }
}
