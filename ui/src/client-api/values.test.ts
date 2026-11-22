import { describe, expect, it } from 'vitest';
import { attr } from '../admin/tables/testAttrs';
import { fromClientValue, toClientValue } from './values';

// #131, ruling Z6 (Learn, "Get values with Client API"). The results do not depend on the browser's time zone: the suite runs
// under TZ=UTC and TZ=America/New_York.
const ROME = 'Europe/Rome';
const nav = () => undefined;
const userLocal = attr('xv_due', { kind: 'datetime', behavior: 'UserLocal' });
const userLocalDay = attr('xv_day', { kind: 'datetime', behavior: 'UserLocal', dateOnly: true });
const independent = attr('xv_checkin', { kind: 'datetime', behavior: 'TimeZoneIndependent' });
const dateOnly = attr('xv_birthday', { kind: 'datetime', behavior: 'DateOnly', dateOnly: true });

describe('Client API date values (#131)', () => {
  it('a_user_local_value_is_the_instant', () => {
    expect((toClientValue(userLocal, '2026-03-29T03:30', ROME) as Date).toISOString()).toBe('2026-03-29T01:30:00.000Z');
    expect((toClientValue(userLocalDay, '2026-07-02', ROME) as Date).toISOString()).toBe('2026-07-01T22:00:00.000Z');
    expect((toClientValue(userLocal, '2026-03-29T03:30', 'Etc/UTC') as Date).toISOString()).toBe('2026-03-29T03:30:00.000Z');
    expect(fromClientValue(userLocal, new Date('2026-07-01T10:00:00Z'), nav, ROME)).toBe('2026-07-01T12:00');
    expect(fromClientValue(userLocalDay, new Date('2026-07-01T22:00:00Z'), nav, ROME)).toBe('2026-07-02');
  });

  it('a_time_zone_independent_value_has_the_stored_clock_in_the_browser_fields', () => {
    const d = toClientValue(independent, '2026-03-29T01:30', ROME) as Date;
    expect([d.getFullYear(), d.getMonth(), d.getDate(), d.getHours(), d.getMinutes()]).toEqual([2026, 2, 29, 1, 30]);
    expect(fromClientValue(independent, new Date(2026, 6, 1, 9, 15), nav, ROME)).toBe('2026-07-01T09:15');
  });

  it('a_date_only_value_is_the_browser_midnight_of_the_day', () => {
    const d = toClientValue(dateOnly, '2026-07-02', ROME) as Date;
    expect([d.getFullYear(), d.getMonth(), d.getDate(), d.getHours(), d.getMinutes()]).toEqual([2026, 6, 2, 0, 0]);
    expect(fromClientValue(dateOnly, new Date(2026, 6, 2, 23, 59), nav, ROME)).toBe('2026-07-02');
  });

  it('text_that_is_not_a_date_is_null_and_a_bad_date_is_refused', () => {
    expect(toClientValue(userLocal, 'soon', ROME)).toBeNull();
    expect(() => fromClientValue(userLocal, new Date(Number.NaN), nav, ROME)).toThrow('setValue on xv_due');
    expect(() => fromClientValue(userLocal, '2026-01-01', nav, ROME)).toThrow('setValue on xv_due');
  });
});
