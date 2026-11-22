import { describe, expect, it } from 'vitest';
import { calendarBars, MAX_LANES, monthSegments, type Bar } from './calendarBars';
import { keyOf, monthGrid } from './calendarGrid';

// November 2026, weeks from Sunday: Nov 1 to Dec 12.
const DAYS = monthGrid(2026, 11, 0).map(keyOf);
const FIRST = DAYS[0]!;
const LAST = DAYS[DAYS.length - 1]!;

const options = (over: Partial<Parameters<typeof calendarBars>[1]> = {}): Parameters<typeof calendarBars>[1] => ({
  start: 'start',
  end: 'end',
  dateOnly: true,
  zone: 'UTC',
  primaryId: 'id',
  title: (row) => String(row.name ?? ''),
  first: FIRST,
  last: LAST,
  ...over,
});

const row = (id: string, start: string | null, end?: string | null, name = id) => ({ id, name, start, end });

describe('calendarBars', () => {
  it('grid_of_november_2026_from_sunday_runs_nov_1_to_dec_12', () => {
    expect(FIRST).toBe('2026-11-01');
    expect(LAST).toBe('2026-12-12');
  });

  it('clips_a_record_that_starts_before_the_range', () => {
    const [bar] = calendarBars([row('a', '2026-10-12', '2026-11-18')], options());
    expect(bar).toMatchObject({ id: 'a', title: 'a', start: '2026-10-12', end: '2026-11-18', from: '2026-11-01', to: '2026-11-18', startsBefore: true, endsAfter: false, days: 38 });
  });

  it('clips_a_record_that_ends_after_the_range', () => {
    const [bar] = calendarBars([row('a', '2026-11-23', '2026-12-20')], options());
    expect(bar).toMatchObject({ from: '2026-11-23', to: '2026-12-12', startsBefore: false, endsAfter: true, days: 28 });
  });

  it('a_record_without_end_is_one_day', () => {
    const [bar] = calendarBars([row('a', '2026-11-05', null)], options());
    expect(bar).toMatchObject({ start: '2026-11-05', end: '2026-11-05', from: '2026-11-05', to: '2026-11-05', days: 1 });
    const [noEndColumn] = calendarBars([row('a', '2026-11-05', '2026-11-09')], options({ end: undefined }));
    expect(noEndColumn).toMatchObject({ to: '2026-11-05', days: 1 });
  });

  it('end_before_start_is_one_day_never_negative', () => {
    const [bar] = calendarBars([row('a', '2026-11-10', '2026-11-02')], options());
    expect(bar!.to).toBe(bar!.from);
    expect(bar).toMatchObject({ from: '2026-11-10', end: '2026-11-10', days: 1 });
  });

  it('date_and_time_near_midnight_follows_the_zone_day', () => {
    const [bar] = calendarBars([row('a', '2026-11-30T23:30:00Z', null)], options({ dateOnly: false, zone: 'Europe/Rome' }));
    expect(bar).toMatchObject({ start: '2026-12-01', from: '2026-12-01' });
    const [utc] = calendarBars([row('a', '2026-11-30T23:30:00Z', null)], options({ dateOnly: false, zone: 'UTC' }));
    expect(utc!.start).toBe('2026-11-30');
  });

  it('skips_rows_without_a_start_and_records_outside_the_range', () => {
    const bars = calendarBars([row('a', null), row('b', '2026-09-01', '2026-09-03'), row('c', '2026-12-13', null), row('d', '2026-11-04', null)], options());
    expect(bars.map((b) => b.id)).toEqual(['d']);
  });

  it('sorts_by_start_then_longer_first_and_numbers_the_index', () => {
    const bars = calendarBars(
      [row('late', '2026-11-20', null), row('short', '2026-11-03', '2026-11-04'), row('long', '2026-11-03', '2026-11-26'), row('early', '2026-10-30', '2026-11-02')],
      options(),
    );
    expect(bars.map((b) => [b.id, b.index])).toEqual([
      ['early', 0],
      ['long', 1],
      ['short', 2],
      ['late', 3],
    ]);
  });

  it('a_row_without_id_gets_a_row_key', () => {
    const [bar] = calendarBars([{ start: '2026-11-05', name: 'x' }], options());
    expect(bar!.id).toBeUndefined();
    expect(bar!.key).toBe('row-0');
  });
});

const bar = (id: string, start: string, end: string, index = 0): Bar => {
  const [b] = calendarBars([row(id, start, end)], options());
  return { ...b!, index };
};

describe('monthSegments', () => {
  it('max_lanes_is_4', () => {
    expect(MAX_LANES).toBe(4);
  });

  it('cuts_a_record_into_one_segment_per_week_and_labels_only_the_first', () => {
    const { segments, hidden } = monthSegments([bar('a', '2026-11-03', '2026-11-26')], DAYS);
    expect(hidden.size).toBe(0);
    expect(segments.map(({ week, col, span, lane, labelled, roundStart, roundEnd }) => ({ week, col, span, lane, labelled, roundStart, roundEnd }))).toEqual([
      { week: 0, col: 2, span: 5, lane: 0, labelled: true, roundStart: true, roundEnd: false },
      { week: 1, col: 0, span: 7, lane: 0, labelled: false, roundStart: false, roundEnd: false },
      { week: 2, col: 0, span: 7, lane: 0, labelled: false, roundStart: false, roundEnd: false },
      { week: 3, col: 0, span: 5, lane: 0, labelled: false, roundStart: false, roundEnd: true },
    ]);
  });

  it('overlapping_records_get_separate_lanes_kept_across_weeks', () => {
    const bars = calendarBars([row('a', '2026-11-03', '2026-11-10'), row('b', '2026-11-05', '2026-11-12'), row('c', '2026-11-11', '2026-11-11')], options());
    const { segments } = monthSegments(bars, DAYS);
    const lanes = (id: string) => [...new Set(segments.filter((s) => s.bar.id === id).map((s) => s.lane))];
    expect(lanes('a')).toEqual([0]);
    expect(lanes('b')).toEqual([1]);
    expect(lanes('c')).toEqual([0]);
  });

  it('a_record_from_before_the_grid_is_labelled_on_week_0_without_a_rounded_start', () => {
    const { segments } = monthSegments([bar('a', '2026-10-12', '2026-11-18')], DAYS);
    expect(segments[0]).toMatchObject({ week: 0, col: 0, labelled: true, roundStart: false, roundEnd: false });
    expect(segments.filter((s) => s.labelled)).toHaveLength(1);
    expect(segments[segments.length - 1]).toMatchObject({ week: 2, col: 0, span: 4, roundEnd: true });
  });

  it('a_record_spanning_the_whole_grid_has_no_rounded_ends', () => {
    const { segments } = monthSegments([bar('a', '2026-10-01', '2027-01-31')], DAYS);
    expect(segments).toHaveLength(6);
    expect(segments.every((s) => !s.roundStart && !s.roundEnd && s.span === 7)).toBe(true);
    expect(segments.filter((s) => s.labelled).map((s) => s.week)).toEqual([0]);
  });

  it('a_fifth_overlapping_record_is_hidden_on_each_of_its_days', () => {
    const bars = calendarBars(
      ['a', 'b', 'c', 'd', 'e'].map((id) => row(id, '2026-11-09', '2026-11-11')),
      options(),
    );
    const { segments, hidden } = monthSegments(bars, DAYS);
    expect(new Set(segments.map((s) => s.bar.id))).toEqual(new Set(['a', 'b', 'c', 'd']));
    expect(segments.every((s) => s.lane < MAX_LANES)).toBe(true);
    for (const day of ['2026-11-09', '2026-11-10', '2026-11-11']) {
      expect(hidden.get(day)?.map((b) => b.id)).toEqual(['e']);
    }
    expect(hidden.has('2026-11-12')).toBe(false);
  });
});
