import fixture from '../../../fixtures/ui/time-zones.json';
import { IANA_BY_CODE, ianaOf, offsetMinutes, wallClockOf, wallClockToUtc, zoneLabel } from './timeZones';

// #131, rulings Z1 and Z5. The results do not depend on the machine's time zone: run under TZ=UTC and TZ=America/New_York.
const ROME = 'Europe/Rome';

describe('time zones', () => {
  it('holds_the_catalog_of_fixtures_ui_time_zones', () => {
    expect(Object.fromEntries(Object.entries(IANA_BY_CODE).map(([code, iana]) => [code, iana]))).toEqual(fixture.zones);
    for (const iana of Object.values(IANA_BY_CODE)) expect(() => new Intl.DateTimeFormat('en-US', { timeZone: iana })).not.toThrow();
    expect(ianaOf(92)).toBe('Etc/UTC');
    expect(() => ianaOf(9999)).toThrow('9999');
  });

  it('reads_the_wall_clock_of_an_instant', () => {
    // Observed on a trial environment: 01:30Z on Rome's spring day is 03:30.
    expect(wallClockOf(new Date('2026-03-29T01:30:00Z'), ROME)).toEqual({ year: 2026, month: 3, day: 29, hour: 3, minute: 30 });
    expect(wallClockOf(new Date('2026-03-29T00:30:00Z'), ROME)).toEqual({ year: 2026, month: 3, day: 29, hour: 1, minute: 30 });
    expect(wallClockOf(new Date('2026-07-01T22:00:00Z'), ROME)).toEqual({ year: 2026, month: 7, day: 2, hour: 0, minute: 0 });
    expect(wallClockOf(new Date('2026-07-01T22:00:00Z'), 'Etc/UTC')).toEqual({ year: 2026, month: 7, day: 1, hour: 22, minute: 0 });
    // Two instants an hour apart show the same 02:30 when the clocks go back.
    expect(wallClockOf(new Date('2026-10-25T00:30:00Z'), ROME).hour).toBe(2);
    expect(wallClockOf(new Date('2026-10-25T01:30:00Z'), ROME).hour).toBe(2);
  });

  it('converts_a_wall_clock_to_utc', () => {
    expect(wallClockToUtc({ year: 2026, month: 3, day: 29, hour: 3, minute: 30 }, ROME).toISOString()).toBe('2026-03-29T01:30:00.000Z');
    expect(wallClockToUtc({ year: 2026, month: 1, day: 15, hour: 13, minute: 0 }, ROME).toISOString()).toBe('2026-01-15T12:00:00.000Z');
    expect(wallClockToUtc({ year: 2026, month: 7, day: 2, hour: 0, minute: 0 }, ROME).toISOString()).toBe('2026-07-01T22:00:00.000Z');
    expect(wallClockToUtc({ year: 2026, month: 7, day: 2, hour: 0, minute: 0 }, 'Etc/UTC').toISOString()).toBe('2026-07-02T00:00:00.000Z');
  });

  it('moves_a_time_in_the_spring_gap_forward', () => {
    // 02:30 does not exist in Rome on 2026-03-29: read as 03:30 CEST, as the server does.
    expect(wallClockToUtc({ year: 2026, month: 3, day: 29, hour: 2, minute: 30 }, ROME).toISOString()).toBe('2026-03-29T01:30:00.000Z');
  });

  it('reads_an_ambiguous_time_as_standard_time', () => {
    expect(wallClockToUtc({ year: 2026, month: 10, day: 25, hour: 2, minute: 30 }, ROME).toISOString()).toBe('2026-10-25T01:30:00.000Z');
  });

  it('gives_the_offset_east_of_utc', () => {
    expect(offsetMinutes(ROME, new Date('2026-07-01T00:00:00Z'))).toBe(120);
    expect(offsetMinutes(ROME, new Date('2026-01-01T00:00:00Z'))).toBe(60);
    expect(offsetMinutes('Etc/UTC', new Date('2026-07-01T00:00:00Z'))).toBe(0);
    expect(offsetMinutes('America/St_Johns', new Date('2026-01-15T00:00:00Z'))).toBe(-210);
  });

  it('names_the_zone_in_the_shell_language', () => {
    expect(zoneLabel(ROME, 'en-US')).toMatch(/Central European/);
    expect(zoneLabel('Etc/UTC', 'en-US')).toBe('UTC');
  });
});
