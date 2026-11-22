import type { DateFormats } from './userSettings';

/**
 * The user's date and time patterns (plan "personal date format", ruling F2): .NET custom patterns as `usersettings` stores
 * them, where `/` stands for the date separator, `:` for the time separator and `tt` for the AM/PM designators. Supported
 * tokens: `d dd M MM MMM MMMM yy yyyy h hh H HH m mm tt`, text in quotes and `\x` escapes; any other character is literal.
 */

/** A calendar day (month 1-12) and an optional clock. */
export interface WallParts {
  year: number;
  month: number;
  day: number;
  hours?: number;
  minutes?: number;
}

type Token = { kind: 'field'; value: string } | { kind: 'literal'; value: string };

const FIELD = /^(dd?|M{1,4}|yyyy|yy|hh?|HH?|mm?|tt)/;

/** The tokens of `pattern`, with `/` and `:` replaced by the separators. */
function tokenize(pattern: string, formats: DateFormats): Token[] {
  const tokens: Token[] = [];
  let i = 0;
  while (i < pattern.length) {
    const rest = pattern.slice(i);
    const field = FIELD.exec(rest);
    if (field) {
      tokens.push({ kind: 'field', value: field[0] });
      i += field[0].length;
      continue;
    }
    const c = pattern[i];
    if (c === "'" || c === '"') {
      const end = pattern.indexOf(c, i + 1);
      const text = end < 0 ? pattern.slice(i + 1) : pattern.slice(i + 1, end);
      tokens.push({ kind: 'literal', value: text });
      i = end < 0 ? pattern.length : end + 1;
    } else if (c === '\\' && i + 1 < pattern.length) {
      tokens.push({ kind: 'literal', value: pattern[i + 1] });
      i += 2;
    } else {
      tokens.push({ kind: 'literal', value: c === '/' ? formats.dateSeparator : c === ':' ? formats.timeSeparator : c });
      i++;
    }
  }
  return tokens;
}

const pad = (n: number, width = 2) => String(n).padStart(width, '0');

function monthName(month: number, style: 'short' | 'long', locale: string): string {
  return new Intl.DateTimeFormat(locale, { month: style, timeZone: 'UTC' }).format(new Date(Date.UTC(2026, month - 1, 1)));
}

/** `parts` written with `pattern` (month names in `locale`). */
export function formatWithPattern(parts: WallParts, pattern: string, formats: DateFormats, locale = 'en-US'): string {
  const hours = parts.hours ?? 0;
  const minutes = parts.minutes ?? 0;
  const h12 = hours % 12 === 0 ? 12 : hours % 12;
  return tokenize(pattern, formats)
    .map((t) => {
      if (t.kind === 'literal') return t.value;
      switch (t.value) {
        case 'd': return String(parts.day);
        case 'dd': return pad(parts.day);
        case 'M': return String(parts.month);
        case 'MM': return pad(parts.month);
        case 'MMM': return monthName(parts.month, 'short', locale);
        case 'MMMM': return monthName(parts.month, 'long', locale);
        case 'yy': return pad(parts.year % 100);
        case 'yyyy': return pad(parts.year, 4);
        case 'h': return String(h12);
        case 'hh': return pad(h12);
        case 'H': return String(hours);
        case 'HH': return pad(hours);
        case 'm': return String(minutes);
        case 'mm': return pad(minutes);
        case 'tt': return hours < 12 ? formats.am : formats.pm;
        default: return t.value;
      }
    })
    .join('');
}

/** The day of a `Date`'s local parts written with the user's date pattern. */
export function formatUserDate(date: Date, formats: DateFormats, locale?: string): string {
  return formatWithPattern({ year: date.getFullYear(), month: date.getMonth() + 1, day: date.getDate() }, formats.dateFormat, formats, locale);
}

/** A clock written with the user's time pattern. */
export function formatUserTime(hours: number, minutes: number, formats: DateFormats): string {
  return formatWithPattern({ year: 2000, month: 1, day: 1, hours, minutes }, formats.timeFormat, formats);
}

/** Two-digit years: 00-49 are 2000-2049, 50-99 are 1950-1999 (the .NET en-US cut-off). */
const fullYear = (yy: number) => (yy < 50 ? 2000 + yy : 1900 + yy);

/**
 * Reads a date typed in the user's date pattern: the day, month and year fields in the pattern's order, separated by any
 * non-digit characters (so `20-11-2026` reads as `dd/MM/yyyy` too). Null when it is not a day that exists. Month names are not read.
 */
export function parseUserDate(text: string, formats: DateFormats): Date | null {
  const order = tokenize(formats.dateFormat, formats)
    .filter((t) => t.kind === 'field')
    .map((t) => t.value[0])
    .filter((c) => c === 'd' || c === 'M' || c === 'y');
  if (order.length !== 3 || tokenize(formats.dateFormat, formats).some((t) => t.kind === 'field' && /^MMM/.test(t.value))) return null;
  const numbers = text.trim().split(/[^\d]+/).filter((s) => s !== '');
  if (numbers.length !== 3) return null;
  const value = (c: 'd' | 'M' | 'y') => numbers[order.indexOf(c)];
  const yearText = value('y');
  if (!/^\d{2}$|^\d{4}$/.test(yearText)) return null;
  const year = yearText.length === 2 ? fullYear(Number(yearText)) : Number(yearText);
  const [month, day] = [Number(value('M')) - 1, Number(value('d'))];
  const date = new Date(year, month, day);
  date.setFullYear(year);
  return date.getFullYear() === year && date.getMonth() === month && date.getDate() === day ? date : null;
}

const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/**
 * Reads a time typed in the user's style: hours and minutes separated by the time separator or `:`, with an optional AM/PM
 * designator (any case); without one the hours are 24-hour. Null when it is not a time.
 */
export function parseUserTime(text: string, formats: DateFormats): { hours: number; minutes: number } | null {
  const separators = [...new Set([formats.timeSeparator, ':'])].map(escape).join('|');
  const designators = [formats.am, formats.pm, 'AM', 'PM'].filter((d) => d !== '').map(escape).join('|');
  const m = new RegExp(`^(\\d{1,2})(?:${separators})(\\d{2})\\s*(${designators})?$`, 'i').exec(text.trim());
  if (!m) return null;
  let hours = Number(m[1]);
  const minutes = Number(m[2]);
  if (minutes > 59) return null;
  if (m[3]) {
    if (hours < 1 || hours > 12) return null;
    const d = m[3].toLowerCase();
    const pm = d === formats.pm.toLowerCase() || (d === 'pm' && d !== formats.am.toLowerCase());
    hours = (hours % 12) + (pm ? 12 : 0);
  } else if (hours > 23) return null;
  return { hours, minutes };
}

/** True when the user's time pattern is a 12-hour one (`h`/`hh`). */
export const usesTwelveHours = (formats: DateFormats): boolean => tokenize(formats.timeFormat, formats).some((t) => t.kind === 'field' && /^h/.test(t.value));

/** `pattern` as the user reads it in a placeholder or a message: `/` and `:` replaced by the separators (`dd.MM.yyyy`). */
export function patternText(pattern: string, formats: DateFormats): string {
  return pattern.replace(/\//g, formats.dateSeparator).replace(/:/g, formats.timeSeparator);
}
