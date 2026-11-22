import { dayKey } from './calendarGrid';

/** One dated record of the calendar, clipped to the shown range. Days are `yyyy-mm-dd` keys. */
export interface Bar {
  key: string;
  id?: string;
  title: string;
  /** The record's full first and last day (the last is the first when there is no end, or the end is earlier). */
  start: string;
  end: string;
  /** `start` and `end` clipped to the shown range. */
  from: string;
  to: string;
  /** The real start lies before the range, the real end after it. */
  startsBefore: boolean;
  endsAfter: boolean;
  /** The full duration, start and end included. */
  days: number;
  /** The position in the sorted list (picks the bar's colour). */
  index: number;
}

export interface CalendarBarsOptions {
  /** The start and end columns (raw values: ISO UTC for a date and time, `yyyy-mm-dd` for a date only). */
  start: string;
  end?: string;
  dateOnly: boolean;
  /** The zone the days are read in, already resolved by the caller (UTC for a TimeZoneIndependent column). */
  zone: string;
  primaryId: string;
  title(row: Record<string, unknown>): string;
  /** The first and last day of the shown range. */
  first: string;
  last: string;
}

const text = (v: unknown): string | undefined => (typeof v === 'string' && v !== '' ? v : undefined);

const DAY_MS = 86_400_000;
const timeOf = (key: string): number => {
  const date = new Date(Date.UTC(2000, Number(key.slice(5, 7)) - 1, Number(key.slice(8, 10))));
  date.setUTCFullYear(Number(key.slice(0, 4)));
  return date.getTime();
};

/**
 * The bars of the records that meet the range [first, last], sorted by start then longer first. Rows without a start are
 * skipped (the caller counts them as undated).
 */
export function calendarBars(rows: Record<string, unknown>[], o: CalendarBarsOptions): Bar[] {
  const bars: Omit<Bar, 'index'>[] = [];
  rows.forEach((row, index) => {
    const startValue = text(row[o.start]);
    if (!startValue) return;
    const start = dayKey(startValue, o.dateOnly, o.zone);
    const endValue = o.end ? text(row[o.end]) : undefined;
    let end = endValue ? dayKey(endValue, o.dateOnly, o.zone) : start;
    if (end < start) end = start;
    if (end < o.first || start > o.last) return;
    const id = text(row[o.primaryId]);
    bars.push({
      key: id ?? `row-${index}`,
      id,
      title: o.title(row),
      start,
      end,
      from: start < o.first ? o.first : start,
      to: end > o.last ? o.last : end,
      startsBefore: start < o.first,
      endsAfter: end > o.last,
      days: Math.round((timeOf(end) - timeOf(start)) / DAY_MS) + 1,
    });
  });
  bars.sort((a, b) => (a.start < b.start ? -1 : a.start > b.start ? 1 : b.days - a.days));
  return bars.map((bar, index) => ({ ...bar, index }));
}

/** The part of a bar inside one week row of the month grid. */
export interface Segment {
  bar: Bar;
  /** The week row (0 to 5), the first column (0 to 6) and the number of columns. */
  week: number;
  col: number;
  span: number;
  lane: number;
  /** Only the segment holding the bar's first shown day carries the text. */
  labelled: boolean;
  /** The record really starts / ends in this segment (rounded edge). */
  roundStart: boolean;
  roundEnd: boolean;
}

/** The most bar lanes in a week row; the bars beyond are counted per day. */
export const MAX_LANES = 4;

/**
 * Cuts the bars into week segments over the 42 days of the grid. Each bar, in order, takes the lowest lane free on all of
 * its shown days and keeps it in every week; a bar that would need a lane `>= MAX_LANES` gets no segment and is listed in
 * `hidden` under each of its days.
 */
export function monthSegments(bars: Bar[], days: string[]): { segments: Segment[]; hidden: Map<string, Bar[]> } {
  const segments: Segment[] = [];
  const hidden = new Map<string, Bar[]>();
  const first = days[0];
  const last = days[days.length - 1];
  if (first === undefined || last === undefined) return { segments, hidden };
  const lanes: boolean[][] = [];

  for (const bar of bars) {
    if (bar.end < first || bar.start > last) continue;
    const fromIndex = bar.start < first ? 0 : days.indexOf(bar.start);
    const toIndex = bar.end > last ? days.length - 1 : days.indexOf(bar.end);
    if (fromIndex < 0 || toIndex < 0) continue;

    let lane = 0;
    while (lanes[lane]?.some((taken, i) => taken && i >= fromIndex && i <= toIndex)) lane++;
    if (lane >= MAX_LANES) {
      for (let i = fromIndex; i <= toIndex; i++) {
        const list = hidden.get(days[i]!) ?? [];
        list.push(bar);
        hidden.set(days[i]!, list);
      }
      continue;
    }
    const taken = (lanes[lane] ??= []);
    for (let i = fromIndex; i <= toIndex; i++) taken[i] = true;

    const startsInGrid = bar.start >= first;
    const endsInGrid = bar.end <= last;
    for (let week = Math.floor(fromIndex / 7); week <= Math.floor(toIndex / 7); week++) {
      const from = Math.max(fromIndex, week * 7);
      const to = Math.min(toIndex, week * 7 + 6);
      segments.push({
        bar,
        week,
        col: from - week * 7,
        span: to - from + 1,
        lane,
        labelled: from === fromIndex,
        roundStart: from === fromIndex && startsInGrid,
        roundEnd: to === toIndex && endsInGrid,
      });
    }
  }
  return { segments, hidden };
}
