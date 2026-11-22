namespace Xrmd.Workflows.Formula;

/// <summary>
/// The Power Fx functions a formula column may call (plan 2.12c, Global Constraints): their arities, the kind of each argument and
/// the kind of the result. <see cref="FormulaTranslator"/> checks a call against this list before it translates it; the functions
/// whose translation is not a plain <c>fx.&lt;Name&gt;</c> call (<c>If</c>, <c>Switch</c>, <c>And</c>, ...) are marked
/// <see cref="Returns.Special"/>.
/// </summary>
internal static class FormulaFunctions
{
    /// <summary>The kind an argument must have.</summary>
    public enum Arg
    {
        /// <summary>Text; a blank text is <c>""</c> (<c>fx.Empty</c>).</summary>
        Text,
        /// <summary>A number (Decimal or Float); a blank number is 0 (<c>fx.Zero</c>).</summary>
        Number,
        /// <summary>A date; a blank date stays blank.</summary>
        Date,
        /// <summary>A <c>TimeUnit</c> value.</summary>
        Unit,
        /// <summary>Checked by the translator itself.</summary>
        Any,
    }

    /// <summary>The kind of the result.</summary>
    public enum Returns
    {
        Text,
        Decimal,
        Float,
        /// <summary>The widest of the number arguments (Float when one is Float, Decimal otherwise).</summary>
        SameNumber,
        /// <summary>A yes/no value computed by an <c>fx.</c> function (<c>fx.In</c>, <c>fx.IsUTCToday</c>, ...).</summary>
        Boolean,
        /// <summary>The translator handles the function itself.</summary>
        Special,
    }

    /// <summary>
    /// A supported function: <paramref name="Args"/> are the kinds of the leading arguments, <paramref name="Rest"/> the kind of each
    /// further argument up to <paramref name="Max"/> (<see cref="int.MaxValue"/> for variadic functions).
    /// </summary>
    public sealed record Spec(int Min, int Max, Arg[] Args, Arg Rest, Returns Returns)
    {
        /// <summary>The kind of argument <paramref name="index"/> (0-based).</summary>
        public Arg ArgAt(int index) => index < Args.Length ? Args[index] : Rest;
    }

    private const int Many = int.MaxValue;

    private static Spec S(int min, int max, Returns returns, params Arg[] args) => new(min, max, args, args.Length > 0 ? args[^1] : Arg.Any, returns);

    /// <summary>The functions of the Global Constraints, by their Power Fx name (case-sensitive, as Power Fx).</summary>
    public static readonly IReadOnlyDictionary<string, Spec> Supported = new Dictionary<string, Spec>(StringComparer.Ordinal)
    {
        ["Abs"] = S(1, 1, Returns.SameNumber, Arg.Number),
        ["And"] = S(1, Many, Returns.Special, Arg.Any),
        ["Average"] = S(1, Many, Returns.SameNumber, Arg.Number),
        ["Blank"] = S(0, 0, Returns.Special),
        ["Char"] = S(1, 1, Returns.Text, Arg.Number),
        ["Concatenate"] = S(1, Many, Returns.Text, Arg.Text),
        ["DateAdd"] = S(2, 3, Returns.Special, Arg.Date, Arg.Number, Arg.Unit),
        ["DateDiff"] = S(2, 3, Returns.Special, Arg.Date, Arg.Date, Arg.Unit),
        ["Day"] = S(1, 1, Returns.Decimal, Arg.Date),
        ["Decimal"] = S(1, 1, Returns.Special, Arg.Any),
        ["EndsWith"] = S(2, 2, Returns.Boolean, Arg.Text, Arg.Text),
        ["Exp"] = S(1, 1, Returns.Float, Arg.Number),
        ["Float"] = S(1, 1, Returns.Special, Arg.Any),
        ["Hour"] = S(1, 1, Returns.Decimal, Arg.Date),
        ["If"] = S(2, Many, Returns.Special, Arg.Any),
        ["Int"] = S(1, 1, Returns.SameNumber, Arg.Number),
        ["IsBlank"] = S(1, 1, Returns.Special, Arg.Any),
        ["ISOWeekNum"] = S(1, 1, Returns.Decimal, Arg.Date),
        ["IsUTCToday"] = S(1, 1, Returns.Boolean, Arg.Date),
        ["Left"] = S(2, 2, Returns.Text, Arg.Text, Arg.Number),
        ["Len"] = S(1, 1, Returns.Decimal, Arg.Text),
        ["Ln"] = S(1, 1, Returns.Float, Arg.Number),
        ["Lower"] = S(1, 1, Returns.Text, Arg.Text),
        ["Max"] = S(1, Many, Returns.SameNumber, Arg.Number),
        ["Mid"] = S(2, 3, Returns.Text, Arg.Text, Arg.Number, Arg.Number),
        ["Min"] = S(1, Many, Returns.SameNumber, Arg.Number),
        ["Minute"] = S(1, 1, Returns.Decimal, Arg.Date),
        ["Mod"] = S(2, 2, Returns.SameNumber, Arg.Number, Arg.Number),
        ["Month"] = S(1, 1, Returns.Decimal, Arg.Date),
        ["Not"] = S(1, 1, Returns.Special, Arg.Any),
        ["Now"] = S(0, 0, Returns.Special),
        ["Or"] = S(1, Many, Returns.Special, Arg.Any),
        ["Power"] = S(2, 2, Returns.Float, Arg.Number, Arg.Number),
        ["Replace"] = S(4, 4, Returns.Text, Arg.Text, Arg.Number, Arg.Number, Arg.Text),
        ["Right"] = S(2, 2, Returns.Text, Arg.Text, Arg.Number),
        ["Round"] = S(2, 2, Returns.SameNumber, Arg.Number, Arg.Number),
        ["RoundDown"] = S(2, 2, Returns.SameNumber, Arg.Number, Arg.Number),
        ["RoundUp"] = S(2, 2, Returns.SameNumber, Arg.Number, Arg.Number),
        ["Second"] = S(1, 1, Returns.Decimal, Arg.Date),
        ["Sqrt"] = S(1, 1, Returns.Float, Arg.Number),
        ["StartsWith"] = S(2, 2, Returns.Boolean, Arg.Text, Arg.Text),
        ["Substitute"] = S(3, 4, Returns.Text, Arg.Text, Arg.Text, Arg.Text, Arg.Number),
        ["Sum"] = S(1, Many, Returns.SameNumber, Arg.Number),
        ["Switch"] = S(3, Many, Returns.Special, Arg.Any),
        ["Text"] = S(1, 2, Returns.Special, Arg.Any),
        ["Trim"] = S(1, 1, Returns.Text, Arg.Text),
        ["TrimEnds"] = S(1, 1, Returns.Text, Arg.Text),
        ["Trunc"] = S(1, 2, Returns.SameNumber, Arg.Number, Arg.Number),
        ["Upper"] = S(1, 1, Returns.Text, Arg.Text),
        ["UTCNow"] = S(0, 0, Returns.Special),
        ["UTCToday"] = S(0, 0, Returns.Special),
        ["Value"] = S(1, 1, Returns.Special, Arg.Any),
        ["Weekday"] = S(1, 2, Returns.Decimal, Arg.Date, Arg.Any),
        ["WeekNum"] = S(1, 2, Returns.Decimal, Arg.Date, Arg.Any),
        ["Year"] = S(1, 1, Returns.Decimal, Arg.Date),
    };

    /// <summary>
    /// The functions the Power Fx 1.8.1 core library does not know (they are Dataverse's), and the core function of the same
    /// argument and result types the translator puts in their place in the text it gives <c>Engine.Check</c> (the translation reads
    /// the original text).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CheckedAs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Decimal"] = "Value",
        ["Float"] = "Value",
        ["UTCNow"] = "Now",
        ["UTCToday"] = "Today",
        ["IsUTCToday"] = "IsToday",
    };

    /// <summary>The <c>TimeUnit</c> values <c>DateAdd</c> and <c>DateDiff</c> take (Power Fx names).</summary>
    public static readonly IReadOnlySet<string> TimeUnits = new HashSet<string>(StringComparer.Ordinal)
    {
        "Milliseconds", "Seconds", "Minutes", "Hours", "Days", "Months", "Quarters", "Years",
    };

    /// <summary>The <c>TimeUnit</c> values below a day, which a Date only value cannot take.</summary>
    public static readonly IReadOnlySet<string> TimeOfDayUnits = new HashSet<string>(StringComparer.Ordinal)
    {
        "Milliseconds", "Seconds", "Minutes", "Hours",
    };

    /// <summary>The date functions that read a part of a date (a User local date would need the user's time zone).</summary>
    public static readonly IReadOnlySet<string> DateParts = new HashSet<string>(StringComparer.Ordinal)
    {
        "Day", "Month", "Year", "Hour", "Minute", "Second", "Weekday", "WeekNum", "ISOWeekNum",
    };
}
