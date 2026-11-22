import { describe, expect, it } from 'vitest';
import { createFormatting } from './formatting';

describe('createFormatting with the user patterns (plan "personal date format", F6)', () => {
  const dateFormats = { dateFormat: 'dd/MM/yyyy', timeFormat: 'HH:mm', dateSeparator: '.', timeSeparator: ':', am: 'AM', pm: 'PM' };

  it('formats_short_dates_and_times_in_the_users_patterns_and_zone', () => {
    const f = createFormatting('en-US', { timeZone: 'Europe/Rome', dateFormats });
    const instant = new Date('2026-11-20T23:30:00Z');
    expect(f.formatDateShort(instant)).toBe('21.11.2026');
    expect(f.formatDateShort(instant, true)).toBe('21.11.2026 00:30');
    expect(f.formatTime(instant)).toBe('00:30');
  });

  it('keeps_the_locale_formats_without_user_patterns', () => {
    expect(createFormatting('en-US', { timeZone: 'UTC' }).formatDateShort(new Date('2026-11-20T23:30:00Z'))).toBe('11/20/26');
  });
});
