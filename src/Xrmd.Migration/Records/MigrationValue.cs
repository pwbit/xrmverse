namespace Xrmd.Migration.Records;

/// <summary>A column value in a package, typed so that the importer never has to guess (see <see cref="RecordLineCodec"/>).</summary>
public abstract record MigrationValue;

public sealed record StringValue(string Value) : MigrationValue;

public sealed record IntValue(long Value) : MigrationValue;

public sealed record DecimalValue(decimal Value) : MigrationValue;

public sealed record DoubleValue(double Value) : MigrationValue;

public sealed record MoneyValue(decimal Value) : MigrationValue;

public sealed record BoolValue(bool Value) : MigrationValue;

/// <summary>A date and time, always UTC.</summary>
public sealed record DateTimeValue : MigrationValue
{
    public DateTimeValue(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc) throw new ArgumentException("A date-time value must be UTC.", nameof(utc));
        Utc = utc;
    }

    public DateTime Utc { get; }
}

/// <summary>A date-only value (no time, no time zone).</summary>
public sealed record DateOnlyValue(DateOnly Value) : MigrationValue;

public sealed record OptionValue(int Value) : MigrationValue;

public sealed record OptionsValue(IReadOnlyList<int> Values) : MigrationValue;

public sealed record GuidValue(Guid Value) : MigrationValue;

public sealed record LookupValue(string Table, Guid Id) : MigrationValue;

/// <summary>A party list: each party is a record (<see cref="Party.Table"/> and <see cref="Party.Id"/>) or an unresolved address.</summary>
public sealed record PartyListValue(IReadOnlyList<Party> Parties) : MigrationValue;

public sealed record Party(int ParticipationMask, string? Table, Guid? Id, string? Address);

/// <summary>A file column: the content is the package entry at <see cref="Path"/>.</summary>
public sealed record FileValue(string Path, string FileName, string MimeType, long Bytes) : MigrationValue;

/// <summary>An image column: the full image is the package entry at <see cref="Path"/>.</summary>
public sealed record ImageValue(string Path) : MigrationValue;

/// <summary>The column was exported and is empty (an absent column was not exported).</summary>
public sealed record NullValue : MigrationValue
{
    public static readonly NullValue Instance = new();

    private NullValue() { }
}

/// <summary>A record of a table: its id and the exported columns.</summary>
public sealed record MigrationRecord(Guid Id, IReadOnlyDictionary<string, MigrationValue> Values);
