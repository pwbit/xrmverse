import { describe, expect, it } from 'vitest';
import { formatUserDate, formatUserTime, formatWithPattern, parseUserDate, parseUserTime, usesTwelveHours } from './dateFormat';
import { DEFAULT_DATE_FORMATS, type DateFormats } from './userSettings';

const it_: DateFormats = { dateFormat: 'dd/MM/yyyy', timeFormat: 'HH:mm', dateSeparator: '.', timeSeparator: ':', am: 'AM', pm: 'PM' };
const day = { year: 2026, month: 11, day: 5, hours: 14, minutes: 7 };

describe('dateFormat', () => {
  it('formats_with_the_separators_and_designators_of_the_settings', () => {
    expect(formatWithPattern(day, 'M/d/yyyy h:mm tt', DEFAULT_DATE_FORMATS)).toBe('11/5/2026 2:07 PM');
    expect(formatWithPattern(day, 'dd/MM/yyyy HH:mm', it_)).toBe('05.11.2026 14:07');
    expect(formatWithPattern(day, "yyyy-MM-dd 'at' hh:mm tt", { ...it_, am: 'am', pm: 'pm' })).toBe('2026-11-05 at 02:07 pm');
    expect(formatWithPattern({ ...day, hours: 0 }, 'h tt', DEFAULT_DATE_FORMATS)).toBe('12 AM');
    expect(formatWithPattern(day, 'd MMM yy', DEFAULT_DATE_FORMATS)).toBe('5 Nov 26');
  });

  it('formats_a_date_and_a_time_with_the_users_patterns', () => {
    expect(formatUserDate(new Date(2026, 10, 5), it_)).toBe('05.11.2026');
    expect(formatUserTime(9, 30, DEFAULT_DATE_FORMATS)).toBe('9:30 AM');
    expect(formatUserTime(21, 30, it_)).toBe('21:30');
  });

  it('reads_a_date_in_the_patterns_order_with_any_separator', () => {
    const read = (text: string, f: DateFormats) => {
      const d = parseUserDate(text, f);
      return d && [d.getFullYear(), d.getMonth() + 1, d.getDate()];
    };
    expect(read('05.11.2026', it_)).toEqual([2026, 11, 5]);
    expect(read('5/11/2026', it_)).toEqual([2026, 11, 5]);
    expect(read('11/5/2026', DEFAULT_DATE_FORMATS)).toEqual([2026, 11, 5]);
    expect(read('11/5/26', DEFAULT_DATE_FORMATS)).toEqual([2026, 11, 5]);
    expect(read('2026/11/05', { ...it_, dateFormat: 'yyyy/MM/dd' })).toEqual([2026, 11, 5]);
    expect(read('31.02.2026', it_)).toBeNull();
    expect(read('2026-11-05', it_)).toBeNull();
    expect(read('5.11', it_)).toBeNull();
  });

  it('reads_a_time_in_12_or_24_hours', () => {
    expect(parseUserTime('2:07 PM', DEFAULT_DATE_FORMATS)).toEqual({ hours: 14, minutes: 7 });
    expect(parseUserTime('12:00 am', DEFAULT_DATE_FORMATS)).toEqual({ hours: 0, minutes: 0 });
    expect(parseUserTime('14:07', DEFAULT_DATE_FORMATS)).toEqual({ hours: 14, minutes: 7 });
    expect(parseUserTime('14.07', { ...it_, timeSeparator: '.' })).toEqual({ hours: 14, minutes: 7 });
    expect(parseUserTime('13:00 PM', DEFAULT_DATE_FORMATS)).toBeNull();
    expect(parseUserTime('24:00', it_)).toBeNull();
    expect(parseUserTime('9', it_)).toBeNull();
  });

  it('tells_a_12_hour_pattern', () => {
    expect(usesTwelveHours(DEFAULT_DATE_FORMATS)).toBe(true);
    expect(usesTwelveHours(it_)).toBe(false);
  });
});
