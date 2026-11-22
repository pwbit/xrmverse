import { describe, expect, it } from 'vitest';
import { CHOICE_TEXT_DARK, CHOICE_TEXT_LIGHT } from '../../shell/theme';
import { badgeColors, choiceKey, readableText } from './choiceColors';

describe('readableText', () => {
  it('picks the text with the higher contrast', () => {
    expect(readableText('#FFB900')).toBe(CHOICE_TEXT_DARK);
    // #0078D4: 4.52 against white, 4.64 against black, so the dark text wins by the standard formula.
    expect(readableText('#0078D4')).toBe(CHOICE_TEXT_DARK);
    expect(readableText('#107C10')).toBe(CHOICE_TEXT_LIGHT);
    // #767676: 4.54 against white, 4.62 against black.
    expect(readableText('#767676')).toBe(CHOICE_TEXT_DARK);
    expect(readableText('#FFFFFF')).toBe(CHOICE_TEXT_DARK);
    expect(readableText('#000000')).toBe(CHOICE_TEXT_LIGHT);
  });
});

describe('choiceKey', () => {
  it('uses the global name, else table.column, lowercase', () => {
    expect(choiceKey('Account', 'IndustryCode')).toBe('account.industrycode');
    expect(choiceKey('account', 'x', 'XV_Industry')).toBe('xv_industry');
  });
});

describe('badgeColors', () => {
  it('uses the readable text by default', () => {
    expect(badgeColors({ value: 1, color: '#107C10' }, 'account.x', {})).toEqual({ background: '#107C10', text: CHOICE_TEXT_LIGHT });
  });
  it('lets the overlay override the text', () => {
    expect(badgeColors({ value: 1, color: '#107C10' }, 'account.x', { 'account.x': { 1: { text: '#FFFF00' } } })).toEqual({ background: '#107C10', text: '#FFFF00' });
  });
  it('is undefined without a valid colour', () => {
    expect(badgeColors({ value: 1 }, 'account.x', {})).toBeUndefined();
    expect(badgeColors({ value: 1, color: 'red' }, 'account.x', {})).toBeUndefined();
  });
});
