using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using static Xrmd.Metadata.DuplicateDetectionSchema;

namespace Xrmd.Core.Duplicates;

/// <summary>A condition of a duplicate detection rule, as the match code and the lookups use it.</summary>
/// <param name="Id">The <c>duplicateruleconditionid</c>.</param>
/// <param name="BaseAttribute">The column of the base table (<c>baseattributename</c>).</param>
/// <param name="MatchingAttribute">The column of the matching table (<c>matchingattributename</c>).</param>
/// <param name="Operator">The <c>operatorcode</c>.</param>
/// <param name="Param">The <c>operatorparam</c> (the number of characters of operators 1 and 2).</param>
/// <param name="IgnoreBlank">The <c>ignoreblankvalues</c>.</param>
public sealed record DuplicateRuleCondition(Guid Id, string BaseAttribute, string MatchingAttribute, DuplicateOperator Operator, int? Param,
    bool IgnoreBlank);

/// <summary>A rule in Publishing or Published with its conditions, in their stable order (<c>duplicateruleconditionid</c>).</summary>
/// <param name="StatusCode">The rule's <c>statuscode</c>: <see cref="RuleStatus.Publishing"/> or <see cref="RuleStatus.Published"/>.</param>
public sealed record PublishedRule(Guid Id, string BaseEntity, string MatchingEntity, bool CaseSensitive, bool ExcludeInactive, int StatusCode,
    IReadOnlyList<DuplicateRuleCondition> Conditions);

/// <summary>Which table of a rule a row belongs to, so which attribute of each condition is read.</summary>
public enum MatchSide
{
    /// <summary>The row is of the base table: <see cref="DuplicateRuleCondition.BaseAttribute"/>.</summary>
    Base,

    /// <summary>The row is of the matching table: <see cref="DuplicateRuleCondition.MatchingAttribute"/>.</summary>
    Matching,
}

/// <summary>
/// The match code of a row for a rule (plan 2.15, ruling D8): the normalized value of each condition, joined in the rule's
/// condition order and hashed with SHA-256, so two rows are duplicates for the rule when their codes are equal.
/// </summary>
public static class MatchCode
{
    /// <summary>The length of a code: <c>binary(32)</c>.</summary>
    public const int Length = 32;

    /// <summary>The token of a blank value that joins the code (a condition with <c>ignoreblankvalues</c> false).</summary>
    private const string BlankToken = "\u0000";

    private const char Separator = '\u001F';

    /// <summary>
    /// The code of a row for <paramref name="rule"/>: <c>SHA256(UTF-8 of "{n}\u001F{v1}\u001F…{vn}")</c> over the
    /// <see cref="Normalize">normalized</see> value of each condition's attribute on <paramref name="side"/>. Null (no code)
    /// when a blank value is ignored (a rule with one condition always ignores blanks; with several, a condition with
    /// <c>ignoreblankvalues</c> true does) or when every value is blank; a blank value of another condition joins as <c>\u0000</c>.
    /// </summary>
    /// <param name="value">The row's value of an attribute (an SDK value: string, <see cref="EntityReference"/>, <see cref="OptionSetValue"/>…).</param>
    /// <param name="attribute">The definition of an attribute of the side's table.</param>
    public static byte[]? Compute(PublishedRule rule, MatchSide side, Func<string, object?> value, Func<string, AttributeMetadata> attribute)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(attribute);
        var conditions = rule.Conditions;
        if (conditions.Count == 0) return null;
        var text = new StringBuilder().Append(conditions.Count.ToString(CultureInfo.InvariantCulture));
        var anyValue = false;
        foreach (var c in conditions)
        {
            var name = side == MatchSide.Base ? c.BaseAttribute : c.MatchingAttribute;
            var normalized = Normalize(value(name), attribute(name), c, rule.CaseSensitive);
            if (normalized is null)
            {
                // Learn: a rule with one condition treats "ignore blank values" false as true.
                if (conditions.Count == 1 || c.IgnoreBlank) return null;
                normalized = BlankToken;
            }
            else
            {
                anyValue = true;
            }
            text.Append(Separator).Append(normalized);
        }
        return anyValue ? SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())) : null;
    }

    /// <summary>
    /// The normalized value of one condition (ruling D8); null when it is blank (null, or a text empty after trimming):
    /// <list type="bullet">
    /// <item>text: trimmed, upper-cased (invariant) unless <paramref name="caseSensitive"/>, then the first (1) or last (2)
    /// <c>operatorparam</c> characters (the whole value when shorter);</item>
    /// <item>date and time: the UTC date <c>yyyy-MM-dd</c> (Same Date) or the UTC value to the second (Same Date and Time);</item>
    /// <item>numbers: invariant culture, a decimal or money rounded to its column's scale (as stored) and without trailing zeros; Boolean <c>0</c>/<c>1</c>; an option its value;</item>
    /// <item>lookups: <c>{logicalname}:{id:N}</c>; a bare id only for a lookup with a single target (400 otherwise).</item>
    /// </list>
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a bare id of a lookup with several targets.</exception>
    public static string? Normalize(object? value, AttributeMetadata attribute, DuplicateRuleCondition c, bool caseSensitive)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(c);
        switch (value)
        {
            case null:
                return null;
            case string text:
                return NormalizeText(text, c, caseSensitive);
            case DateTime moment:
                var utc = moment.Kind switch
                {
                    DateTimeKind.Local => moment.ToUniversalTime(),
                    DateTimeKind.Unspecified => DateTime.SpecifyKind(moment, DateTimeKind.Utc),
                    _ => moment,
                };
                return c.Operator == DuplicateOperator.SameDate
                    ? utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            case EntityReference reference:
                return LookupToken(reference.LogicalName, reference.Id);
            case Guid id when attribute is LookupAttributeMetadata { Targets: [var only] } && !TypeMapping.IsPolymorphicLookup(attribute):
                return LookupToken(only, id);
            case Guid when attribute is LookupAttributeMetadata:
                // A stored value always carries its table; a bare id of a multi-table lookup cannot be coded the same way.
                throw XrmdFaultException.InvalidArgument(
                    $"The value of the lookup '{attribute.LogicalName}' must name its table (an entity reference), not only an id.");
            case Guid id:
                return id.ToString("N");
            case OptionSetValue option:
                return option.Value.ToString(CultureInfo.InvariantCulture);
            case Money money:
                return NormalizeDecimal(RoundToStoredScale(money.Value, attribute));
            case decimal number:
                return NormalizeDecimal(RoundToStoredScale(number, attribute));
            case double number:
                return number.ToString("R", CultureInfo.InvariantCulture);
            case bool flag:
                return flag ? "1" : "0";
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return value.ToString();
        }
    }

    private static string? NormalizeText(string text, DuplicateRuleCondition c, bool caseSensitive)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        if (!caseSensitive) trimmed = trimmed.ToUpperInvariant();
        var n = c.Param ?? trimmed.Length;
        return c.Operator switch
        {
            DuplicateOperator.SameFirstCharacters when n < trimmed.Length => trimmed[..n],
            DuplicateOperator.SameLastCharacters when n < trimmed.Length => trimmed[^n..],
            _ => trimmed,
        };
    }

    private static string LookupToken(string logicalName, Guid id) => logicalName + ":" + id.ToString("N");

    /// <summary>
    /// <paramref name="number"/> as its column stores it: a decimal column has the scale of its <c>Precision</c> (2 by default), a
    /// money column scale 4 (<see cref="TypeMapping"/>). SQL Server rounds half away from zero when it converts a value to the
    /// column's scale, so the code of a value written with more digits is the code of the stored value.
    /// </summary>
    private static decimal RoundToStoredScale(decimal number, AttributeMetadata attribute) => attribute switch
    {
        DecimalAttributeMetadata d => Math.Round(number, d.Precision ?? TypeMapping.DefaultDecimalPrecision, MidpointRounding.AwayFromZero),
        MoneyAttributeMetadata => Math.Round(number, TypeMapping.MoneyScale, MidpointRounding.AwayFromZero),
        _ => number,
    };

    /// <summary>A decimal without trailing zeros (1.50 and 1.5000 are both <c>1.5</c>), invariant culture.</summary>
    private static string NormalizeDecimal(decimal number)
    {
        var text = number.ToString(CultureInfo.InvariantCulture);
        if (!text.Contains('.', StringComparison.Ordinal)) return text;
        text = text.TrimEnd('0');
        return text.EndsWith('.') ? text[..^1] : text;
    }
}
