using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xrmd.Model.Metadata;
using Xrmd.Abstractions;

namespace Xrmd.Metadata;

/// <summary>
/// The <c>AutoNumberFormat</c> of a string column (autonumber plan, rulings N1, N2, N5): literal text and the placeholders
/// <c>{SEQNUM:n}</c> (the sequence value, zero padded to at least n digits), <c>{RANDSTRING:n}</c> (n random upper-case
/// letters and digits, n from 1 to 6) and <c>{DATETIMEUTC:format}</c> (the UTC time in a .NET date and time format).
/// The column accepts any format; <see cref="Parse"/> checks it when a record is created, as Dataverse does.
/// </summary>
public sealed partial class AutoNumberFormat
{
    /// <summary>The first value of a new sequence (Learn: sequences start at 1000).</summary>
    public const long DefaultSeed = 1000;

    /// <summary>The longest random string a placeholder can ask for.</summary>
    public const int MaxRandomLength = 6;

    private const string RandomAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private abstract record Segment;
    private sealed record Literal(string Text) : Segment;
    private sealed record Sequence(int MinLength) : Segment;
    private sealed record Random(int Length) : Segment;
    private sealed record DateTimeUtc(string Format) : Segment;

    private readonly IReadOnlyList<Segment> _segments;

    private AutoNumberFormat(string text, IReadOnlyList<Segment> segments)
    {
        Text = text;
        _segments = segments;
    }

    /// <summary>The format as written.</summary>
    public string Text { get; }

    /// <summary>True when the format has a <c>{SEQNUM:n}</c> placeholder (a value of the column's sequence is needed).</summary>
    public bool UsesSequence => _segments.Any(s => s is Sequence);

    /// <summary>The format of <paramref name="text"/>.</summary>
    /// <exception cref="XrmdValidationException">An unknown or malformed placeholder, a <c>RANDSTRING</c> length outside
    /// 1..6, a <c>SEQNUM</c> length below 1, a <c>DATETIMEUTC</c> format .NET refuses, or more than one <c>SEQNUM</c>.</exception>
    public static AutoNumberFormat Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var segments = new List<Segment>();
        var literal = new StringBuilder();
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf('{', position);
            if (open < 0)
            {
                literal.Append(text, position, text.Length - position);
                break;
            }
            literal.Append(text, position, open - position);
            var close = text.IndexOf('}', open + 1);
            if (close < 0) throw Invalid(text, $"the brace at position {open} is not closed");
            if (literal.Length > 0)
            {
                segments.Add(new Literal(literal.ToString()));
                literal.Clear();
            }
            segments.Add(Placeholder(text, text.Substring(open + 1, close - open - 1)));
            position = close + 1;
        }
        if (literal.Length > 0) segments.Add(new Literal(literal.ToString()));
        if (segments.Count(s => s is Sequence) > 1) throw Invalid(text, "it has more than one {SEQNUM} placeholder");
        return new AutoNumberFormat(text, segments);
    }

    /// <summary>The value for the sequence value <paramref name="sequence"/> at <paramref name="utcNow"/>.</summary>
    public string Render(long sequence, DateTime utcNow)
    {
        var value = new StringBuilder();
        foreach (var segment in _segments)
        {
            switch (segment)
            {
                case Literal l: value.Append(l.Text); break;
                case Sequence s: value.Append(sequence.ToString(CultureInfo.InvariantCulture).PadLeft(s.MinLength, '0')); break;
                case Random r:
                    for (var i = 0; i < r.Length; i++) value.Append(RandomAlphabet[RandomNumberGenerator.GetInt32(RandomAlphabet.Length)]);
                    break;
                case DateTimeUtc d: value.Append(utcNow.ToString(d.Format, CultureInfo.InvariantCulture)); break;
            }
        }
        return value.ToString();
    }

    /// <summary>
    /// The format of <paramref name="attribute"/> (null: none), after the column checks of ruling N1: only a string column
    /// whose format is Text takes one; an empty format is none.
    /// </summary>
    /// <exception cref="XrmdValidationException">A format on another column.</exception>
    public static string? OfColumn(string entityLogicalName, ColumnDefinition attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        var format = attribute.AutoNumberFormat;
        if (string.IsNullOrEmpty(format)) return null;
        var where = $"Attribute '{entityLogicalName}.{attribute.LogicalName}'";
        if (attribute is not TextColumn s)
            throw new XrmdValidationException($"{where}: an AutoNumberFormat is allowed only on a string column.");
        if (Computed.ComputedColumnRules.IsComputed(attribute))
            throw new XrmdValidationException(
                $"{where}: an AutoNumberFormat cannot be set on a {(attribute.SourceType == Computed.ComputedColumnRules.Formula ? "formula" : "calculated")} column.");
        if (s.Format is { } f && f != TextDisplayFormat.Text || s.FormatName is { Value: { } name } && name != "Text")
            throw new XrmdValidationException($"{where}: an AutoNumberFormat is allowed only on a string column whose format is Text.");
        return format;
    }

    private static Segment Placeholder(string text, string body)
    {
        var colon = body.IndexOf(':');
        if (colon < 0) throw Invalid(text, $"'{{{body}}}' is not a placeholder (SEQNUM, RANDSTRING or DATETIMEUTC with a value)");
        var name = body[..colon];
        var argument = body[(colon + 1)..];
        switch (name)
        {
            case "SEQNUM":
                if (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var minLength) || minLength < 1)
                    throw Invalid(text, $"the length of {{SEQNUM:{argument}}} must be a whole number of at least 1");
                return new Sequence(minLength);
            case "RANDSTRING":
                if (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length is < 1 or > MaxRandomLength)
                    throw Invalid(text, $"the length of {{RANDSTRING:{argument}}} must be from 1 to {MaxRandomLength}");
                return new Random(length);
            case "DATETIMEUTC":
                if (argument.Length == 0) throw Invalid(text, "{DATETIMEUTC} needs a date and time format");
                try
                {
                    _ = DateTime.UtcNow.ToString(argument, CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    throw Invalid(text, $"'{argument}' is not a date and time format");
                }
                return new DateTimeUtc(argument);
            default:
                throw Invalid(text, $"'{{{body}}}' is not a placeholder (SEQNUM, RANDSTRING or DATETIMEUTC)");
        }
    }

    private static XrmdValidationException Invalid(string text, string why) =>
        new($"The AutoNumberFormat '{text}' is not valid: {why}.");
}
