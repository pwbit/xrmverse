import { utcText, wallClockOf, wallClockToUtc } from '../timeZones';

/** A day of the grid (`month` 1 to 12); `inMonth` is false for the days of the neighbouring months. */
export interface Day {
  year: number;
  month: number;
  day: number;
  inMonth: boolean;
}

const DAYS = 42;
const pad = (n: number, width = 2) => String(n).padStart(width, '0');

/** `yyyy-mm-dd` of a day. */
export const keyOf = (d: { year: number; month: number; day: number }): string => `${pad(d.year, 4)}-${pad(d.month)}-${pad(d.day)}`;

/** The 6x7 grid of the month of (year, month), weeks starting on `weekStart` (0 Sunday, 1 Monday). */
export function monthGrid(year: number, month: number, weekStart: 0 | 1): Day[] {
  const first = new Date(Date.UTC(2000, month - 1, 1));
  first.setUTCFullYear(year);
  const lead = (first.getUTCDay() - weekStart + 7) % 7;
  const days: Day[] = [];
  for (let i = 0; i < DAYS; i++) {
    const d = new Date(first.getTime());
    d.setUTCDate(1 - lead + i);
    const m = d.getUTCMonth() + 1;
    days.push({ year: d.getUTCFullYear(), month: m, day: d.getUTCDate(), inMonth: m === month && d.getUTCFullYear() === year });
  }
  return days;
}

/**
 * The fetch range of a grid: first and last day, as `yyyy-mm-dd` when dateOnly, else the ISO UTC of the user's local midnight of
 * the first day and of 23:59:59 of the last.
 */
export function gridRange(days: Day[], dateOnly: boolean, zone: string): { from: string; to: string } {
  const first = days[0]!;
  const last = days[days.length - 1]!;
  if (dateOnly) return { from: keyOf(first), to: keyOf(last) };
  const from = wallClockToUtc({ ...first, hour: 0, minute: 0 }, zone);
  const to = new Date(wallClockToUtc({ ...last, hour: 23, minute: 59 }, zone).getTime() + 59_000);
  return { from: utcText(from), to: utcText(to) };
}

/** The key `yyyy-mm-dd` of the day a value falls on: the date text as is when dateOnly, else the wall clock in `zone`. */
export function dayKey(value: string, dateOnly: boolean, zone: string): string {
  if (dateOnly) return value.slice(0, 10);
  return keyOf(wallClockOf(new Date(value), zone));
}
