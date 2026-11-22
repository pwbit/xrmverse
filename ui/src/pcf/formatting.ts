import { formatWithPattern } from '../apps/dateFormat';
import type { DateFormats } from '../apps/userSettings';
import { wallClockOf } from '../apps/timeZones';

/**
 * `context.formatting` of a PCF control (Learn, `ComponentFramework.Formatting`): numbers and dates in the shell language,
 * with `Intl`. A currency without its own symbol takes the record's.
 */
export interface PcfFormatting {
  formatCurrency(value: number, precision?: number, symbol?: string): string;
  formatDecimal(value: number, precision?: number): string;
  formatInteger(value: number): string;
  formatDateShort(value: Date, includeTime?: boolean): string;
  formatDateLong(value: Date): string;
  formatDateLongAbbreviated(value: Date): string;
  formatTime(value: Date, behavior?: number): string;
  formatDateYearMonth(value: Date): string;
  getWeekOfYear(value: Date): number;
}

const DEFAULT_PRECISION = 2;

const digits = (precision: number | undefined): number => {
  const p = typeof precision === 'number' && Number.isFinite(precision) ? Math.trunc(precision) : DEFAULT_PRECISION;
  return Math.min(Math.max(p, 0), 10);
};

/**
 * The formatting members for `locale`; dates are shown in `timeZone` when one is given, else in the browser's. With the user's
 * `dateFormats` (in the apps), `formatDateShort` and `formatTime` use the user's patterns (plan "personal date format", F6).
 */
export function createFormatting(locale: string, options: { currencySymbol?: string; timeZone?: string; dateFormats?: DateFormats } = {}): PcfFormatting {
  const zone = options.timeZone ? { timeZone: options.timeZone } : {};
  const number = (value: number, precision: number) =>
    new Intl.NumberFormat(locale, { minimumFractionDigits: precision, maximumFractionDigits: precision }).format(value);
  const date = (value: Date, opts: Intl.DateTimeFormatOptions) => (Number.isNaN(value?.getTime?.()) ? '' : new Intl.DateTimeFormat(locale, { ...opts, ...zone }).format(value));
  const user = options.dateFormats;
  const userPattern = (value: Date, pattern: string): string => {
    if (Number.isNaN(value?.getTime?.())) return '';
    const w = options.timeZone
      ? wallClockOf(value, options.timeZone)
      : { year: value.getFullYear(), month: value.getMonth() + 1, day: value.getDate(), hour: value.getHours(), minute: value.getMinutes() };
    return formatWithPattern({ year: w.year, month: w.month, day: w.day, hours: w.hour, minutes: w.minute }, pattern, user!, locale);
  };
  return {
    formatCurrency: (value, precision, symbol) => {
      const text = number(Math.abs(value), digits(precision));
      const sign = value < 0 ? '-' : '';
      return `${sign}${symbol ?? options.currencySymbol ?? ''}${text}`;
    },
    formatDecimal: (value, precision) => number(value, digits(precision)),
    formatInteger: (value) => number(Math.round(value), 0),
    formatDateShort: (value, includeTime) =>
      user
        ? userPattern(value, includeTime ? `${user.dateFormat} ${user.timeFormat}` : user.dateFormat)
        : date(value, includeTime ? { dateStyle: 'short', timeStyle: 'short' } : { dateStyle: 'short' }),
    formatDateLong: (value) => date(value, { dateStyle: 'full' }),
    formatDateLongAbbreviated: (value) => date(value, { dateStyle: 'medium' }),
    formatTime: (value) => (user ? userPattern(value, user.timeFormat) : date(value, { timeStyle: 'short' })),
    formatDateYearMonth: (value) => date(value, { year: 'numeric', month: 'long' }),
    getWeekOfYear: (value) => isoWeek(value),
  };
}

/** The ISO 8601 week number of the date's calendar day. */
function isoWeek(value: Date): number {
  const d = new Date(Date.UTC(value.getFullYear(), value.getMonth(), value.getDate()));
  const day = d.getUTCDay() || 7;
  d.setUTCDate(d.getUTCDate() + 4 - day);
  const yearStart = new Date(Date.UTC(d.getUTCFullYear(), 0, 1));
  return Math.ceil(((d.getTime() - yearStart.getTime()) / 86_400_000 + 1) / 7);
}
