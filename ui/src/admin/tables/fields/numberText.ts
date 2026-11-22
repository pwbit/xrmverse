import type { AttributeMeta } from '../metadata';
import { isWholeNumber } from '../metadata';

/** The separators of `locale` (`it-IT`: `.` groups, `,` decimal). */
function separators(locale: string): { group: string; decimal: string } {
  const parts = new Intl.NumberFormat(locale).formatToParts(12345.6);
  return {
    group: parts.find((p) => p.type === 'group')?.value ?? ',',
    decimal: parts.find((p) => p.type === 'decimal')?.value ?? '.',
  };
}

/** Fraction digits a column shows: none for whole numbers, else its precision (2 when the server gave none). */
export function fractionDigits(attr: AttributeMeta): number {
  if (isWholeNumber(attr.kind)) return 0;
  const p = attr.precision;
  return typeof p === 'number' && Number.isFinite(p) ? Math.min(Math.max(Math.trunc(p), 0), 10) : 2;
}

/**
 * The text a number field shows when it is not being edited: digit grouping and the column's fraction digits in `locale`
 * (`1234567.5` money in it-IT: `1.234.567,50`). Text that is not a number (a half-typed value) is shown as it is.
 */
export function displayNumber(canonical: string, attr: AttributeMeta, locale: string): string {
  const n = Number(canonical);
  if (canonical.trim() === '' || !Number.isFinite(n)) return canonical;
  const digits = fractionDigits(attr);
  return new Intl.NumberFormat(locale, { minimumFractionDigits: digits, maximumFractionDigits: digits }).format(n);
}

/** The text a number field shows while it is edited: no grouping, the decimal mark of `locale`, every digit kept. */
export function editNumber(canonical: string, locale: string): string {
  const n = Number(canonical);
  if (canonical.trim() === '' || !Number.isFinite(n)) return canonical;
  return canonical.trim().replace('.', separators(locale).decimal);
}

/**
 * Reads what the user typed in `locale` into the canonical form the form saves (`1.234,5` in it-IT: `1234.5`): group
 * separators are dropped and the locale's decimal mark becomes `.`. Spaces (and the no-break spaces some locales group with)
 * are dropped too. Anything else is kept, so a wrong value still fails with the form's own number error.
 */
export function parseNumberText(text: string, locale: string): string {
  const { group, decimal } = separators(locale);
  let s = text.replace(/[\s  ]/g, '');
  if (group !== decimal) s = s.split(group).join('');
  return decimal === '.' ? s : s.replace(decimal, '.');
}
