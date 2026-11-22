namespace Xrmd.Core;

/// <summary>
/// The server-computed <c>contact.fullname</c>: the name parts put together in the order of the organization's
/// <c>fullnameconventioncode</c>. A missing part (null, empty or blank) takes its separator with it.
/// </summary>
public static class ContactFullName
{
    /// <summary>The convention of an organization that has none stored: "Last Name, First Name".</summary>
    public const int DefaultConvention = 0;

    public const string Contact = "contact", FullName = "fullname";

    /// <summary>The columns the full name is computed from.</summary>
    public static readonly IReadOnlyList<string> Parts = ["firstname", "middlename", "lastname"];

    /// <summary>
    /// <list type="bullet">
    /// <item>0 Last, First</item><item>1 First Last</item><item>2 Last, First M.</item><item>3 First M. Last</item>
    /// <item>4 Last, First Middle</item><item>5 First Middle Last</item><item>6 Last First</item><item>7 LastFirst</item>
    /// </list>
    /// where M. is the first letter of the middle name and a period.
    /// </summary>
    /// <returns>The full name; null when it comes out empty (no part, or only parts the convention leaves out).</returns>
    /// <exception cref="ArgumentOutOfRangeException">A convention outside 0..7.</exception>
    public static string? Compose(string? first, string? middle, string? last, int convention)
    {
        first = Clean(first);
        middle = Clean(middle);
        last = Clean(last);
        var initial = middle is null ? null : char.ToUpperInvariant(middle[0]) + ".";
        var name = convention switch
        {
            0 => Join(", ", last, first),
            1 => Join(" ", first, last),
            2 => Join(", ", last, Join(" ", first, initial)),
            3 => Join(" ", first, initial, last),
            4 => Join(", ", last, Join(" ", first, middle)),
            5 => Join(" ", first, middle, last),
            6 => Join(" ", last, first),
            7 => Join("", last, first),
            _ => throw new ArgumentOutOfRangeException(nameof(convention), convention, "The full name convention must be 0..7."),
        };
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string? Clean(string? part) => string.IsNullOrWhiteSpace(part) ? null : part.Trim();

    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        return present.Length == 0 ? null : string.Join(separator, present);
    }
}
