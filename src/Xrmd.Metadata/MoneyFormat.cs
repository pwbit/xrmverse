namespace Xrmd.Metadata;

/// <summary>
/// How a money value is formatted (plan 2.10, ruling X11): the currency's symbol (or ISO code, per the organization's
/// <c>currencydisplayoption</c>), the effective precision of the attribute and the organization's <c>currencyformatcode</c>
/// (0 <c>$123</c>, 1 <c>123$</c>, 2 <c>$ 123</c>, 3 <c>123 $</c>). See <see cref="FormattedValues.FormatMoney"/>.
/// </summary>
/// <param name="Symbol">The text placed beside the amount.</param>
/// <param name="Precision">The number of decimals shown.</param>
/// <param name="FormatCode">The placement of <paramref name="Symbol"/>.</param>
public sealed record MoneyFormat(string Symbol, int Precision, int FormatCode);
