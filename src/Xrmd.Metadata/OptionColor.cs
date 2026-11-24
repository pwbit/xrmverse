using System.Globalization;
using Xrmd.Model.Metadata;
using Xrmd.Abstractions;

namespace Xrmd.Metadata;

/// <summary>
/// The background colour of a choice option (<see cref="ChoiceOption.Color"/>): <c>#RRGGBB</c>, hexadecimal digits in either
/// case, or none (null or empty). Checked on every metadata write path, as Dataverse refuses a malformed colour.
/// </summary>
public static partial class OptionColor
{
    /// <summary>True for no colour (null or empty) and for <c>#RRGGBB</c>.</summary>
    public static bool IsValid(string? color)
    {
        if (string.IsNullOrEmpty(color)) return true;
        if (color.Length != 7 || color[0] != '#') return false;
        for (var i = 1; i < color.Length; i++)
            if (!char.IsAsciiHexDigit(color[i])) return false;
        return true;
    }

    /// <summary>Checks the colour of <paramref name="option"/>.</summary>
    /// <param name="owner">Names the attribute or option set in the error message.</param>
    /// <exception cref="XrmdValidationException">The colour is not <c>#RRGGBB</c>; the message names the owner, the option value and the colour.</exception>
    public static void Demand(string owner, ChoiceOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (!IsValid(option.Color))
            throw new XrmdValidationException(
                $"{owner}: option {option.Value?.ToString(CultureInfo.InvariantCulture)} has colour '{option.Color}', which is not #RRGGBB.");
    }
}
