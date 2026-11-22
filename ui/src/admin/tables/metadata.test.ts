import { describe, expect, it } from 'vitest';
import { isUserLocal, parseAttribute, parseOptions } from './metadata';

const dateTime = (extra: Record<string, unknown>) =>
  parseAttribute({ '@odata.type': '#Microsoft.Dynamics.CRM.DateTimeAttributeMetadata', LogicalName: 'xv_when', ...extra } as never);

describe('parseAttribute: date and time behaviour (#131)', () => {
  it('reads DateTimeBehavior, UserLocal when absent', () => {
    expect(dateTime({}).behavior).toBe('UserLocal');
    expect(dateTime({ DateTimeBehavior: { Value: 'TimeZoneIndependent' } }).behavior).toBe('TimeZoneIndependent');
    expect(dateTime({ DateTimeBehavior: 'TimeZoneIndependent' }).behavior).toBe('TimeZoneIndependent');
    const birthday = dateTime({ DateTimeBehavior: { Value: 'DateOnly' }, Format: 'DateOnly' });
    expect([birthday.behavior, birthday.dateOnly]).toEqual(['DateOnly', true]);
    expect(isUserLocal(dateTime({}))).toBe(true);
    expect(isUserLocal(dateTime({ DateTimeBehavior: { Value: 'DateOnly' } }))).toBe(false);
  });

  it('gives other columns no behaviour', () => {
    const text = parseAttribute({ '@odata.type': '#Microsoft.Dynamics.CRM.StringAttributeMetadata', LogicalName: 'xv_name' } as never);
    expect(text.behavior).toBeUndefined();
    expect(isUserLocal(text)).toBe(false);
  });
});

describe('parseOptions: colours', () => {
  it('keeps Color as color and leaves it out when null or absent', () => {
    const options = parseOptions({
      Options: [
        { Value: 1, Color: '#107C10' },
        { Value: 2, Color: null },
        { Value: 3 },
      ],
    });
    expect(options[0]).toMatchObject({ value: 1, color: '#107C10' });
    expect('color' in options[1]).toBe(false);
    expect('color' in options[2]).toBe(false);
  });
});
