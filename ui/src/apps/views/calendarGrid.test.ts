import { describe, expect, it } from 'vitest';
import { dayKey, gridRange, monthGrid } from './calendarGrid';

describe('monthGrid', () => {
  it('starts November 2026 on Monday 26 October with 42 days', () => {
    const days = monthGrid(2026, 11, 1);
    expect(days).toHaveLength(42);
    expect(days[0]).toEqual({ year: 2026, month: 10, day: 26, inMonth: false });
    expect(days[5]).toEqual({ year: 2026, month: 10, day: 31, inMonth: false });
    expect(days[6]).toEqual({ year: 2026, month: 11, day: 1, inMonth: true });
    expect(days[41]).toEqual({ year: 2026, month: 12, day: 6, inMonth: false });
    expect(days.filter((d) => d.inMonth)).toHaveLength(30);
  });

  it('starts on Sunday 1 November (a Sunday) with a Sunday week start, and on Sunday 31 May 2026 for June', () => {
    const days = monthGrid(2026, 11, 0);
    expect(days[0]).toEqual({ year: 2026, month: 11, day: 1, inMonth: true });
    expect(days[41]).toEqual({ year: 2026, month: 12, day: 12, inMonth: false });
    expect(monthGrid(2026, 6, 0)[0]).toEqual({ year: 2026, month: 5, day: 31, inMonth: false });
  });

  it('crosses a year', () => {
    const days = monthGrid(2026, 1, 1);
    expect(days[0]).toEqual({ year: 2025, month: 12, day: 29, inMonth: false });
  });
});

describe('dayKey', () => {
  it('reads an instant on the wall clock of the zone', () => {
    expect(dayKey('2026-11-30T23:30:00Z', false, 'Europe/Rome')).toBe('2026-12-01');
    expect(dayKey('2026-12-01T03:00:00Z', false, 'America/Los_Angeles')).toBe('2026-11-30');
  });
  it('takes a date only as its text', () => {
    expect(dayKey('2026-12-01', true, 'America/Los_Angeles')).toBe('2026-12-01');
    expect(dayKey('2026-12-01T00:00:00Z', true, 'America/Los_Angeles')).toBe('2026-12-01');
  });
});

describe('gridRange', () => {
  const days = monthGrid(2026, 11, 1);
  it('is the first and last day for a date only column', () => {
    expect(gridRange(days, true, 'Europe/Rome')).toEqual({ from: '2026-10-26', to: '2026-12-06' });
  });
  it('is the local midnight of the first day to 23:59:59 of the last, in UTC', () => {
    expect(gridRange(days, false, 'Europe/Rome')).toEqual({ from: '2026-10-25T23:00:00Z', to: '2026-12-06T22:59:59Z' });
    expect(gridRange(days, false, 'Etc/UTC')).toEqual({ from: '2026-10-26T00:00:00Z', to: '2026-12-06T23:59:59Z' });
  });
});

describe('across a DST change', () => {
  it('Rome grid containing the end of summer time (25 Oct 2026): from is +2, to is +1', () => {
    const days = monthGrid(2026, 10, 1); // 28 Sep .. 8 Nov
    expect(days.some((d) => d.month === 10 && d.day === 25)).toBe(true);
    expect(gridRange(days, false, 'Europe/Rome')).toEqual({ from: '2026-09-27T22:00:00Z', to: '2026-11-08T22:59:59Z' });
  });
  it('Rome grid containing the start of summer time (29 Mar 2026): from is +1, to is +2', () => {
    const days = monthGrid(2026, 3, 1); // 23 Feb .. 5 Apr
    expect(gridRange(days, false, 'Europe/Rome')).toEqual({ from: '2026-02-22T23:00:00Z', to: '2026-04-05T21:59:59Z' });
  });
  it('Los Angeles grid containing 1 Nov 2026', () => {
    const days = monthGrid(2026, 11, 0); // 1 Nov .. 12 Dec
    expect(gridRange(days, false, 'America/Los_Angeles')).toEqual({ from: '2026-11-01T07:00:00Z', to: '2026-12-13T07:59:59Z' });
    // 1 Nov 00:30 PDT is still before the change; 08:30 UTC on 1 Nov is 00:30 PST.
    expect(dayKey('2026-11-01T07:30:00Z', false, 'America/Los_Angeles')).toBe('2026-11-01');
    expect(dayKey('2026-11-01T06:59:00Z', false, 'America/Los_Angeles')).toBe('2026-10-31');
  });
  it('dayKey around the Rome change', () => {
    expect(dayKey('2026-10-24T22:00:00Z', false, 'Europe/Rome')).toBe('2026-10-25');
    expect(dayKey('2026-10-25T22:59:00Z', false, 'Europe/Rome')).toBe('2026-10-25');
    expect(dayKey('2026-10-25T23:00:00Z', false, 'Europe/Rome')).toBe('2026-10-26');
  });
});
