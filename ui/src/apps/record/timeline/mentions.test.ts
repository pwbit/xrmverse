import { describe, expect, it } from 'vitest';
import { formatMention, isUserMention, parseMentions } from './mentions';

const ID = '3f2a9c10-1111-4222-8333-444455556666';

describe('mentions', () => {
  it('parse_user_mention_both_forms', () => {
    expect(parseMentions(`Hi @[8,{${ID}},"Mario Rossi"] and @[systemuser,${ID},"Anna"]!`)).toEqual([
      { kind: 'text', text: 'Hi ' },
      { kind: 'mention', entity: '8', id: `{${ID}}`, display: 'Mario Rossi' },
      { kind: 'text', text: ' and ' },
      { kind: 'mention', entity: 'systemuser', id: ID, display: 'Anna' },
      { kind: 'text', text: '!' },
    ]);
  });

  it('parse_keeps_malformed_text', () => {
    expect(parseMentions('@[8,abc')).toEqual([{ kind: 'text', text: '@[8,abc' }]);
    expect(parseMentions('a @[8, x,"No"] b @[8,x,"Yes"]')).toEqual([
      { kind: 'text', text: 'a @[8, x,"No"] b ' },
      { kind: 'mention', entity: '8', id: 'x', display: 'Yes' },
    ]);
    expect(parseMentions('')).toEqual([]);
  });

  it('parse_keeps_other_tables_as_mentions', () => {
    const [part] = parseMentions(`@[account,${ID},"Contoso"]`);
    expect(part).toMatchObject({ kind: 'mention', entity: 'account', display: 'Contoso' });
    expect(isUserMention(part)).toBe(false);
  });

  it('format_writes_braced_lower_case_guid', () => {
    expect(formatMention(ID.toUpperCase(), 'Mario Rossi')).toBe(`@[8,{${ID}},"Mario Rossi"]`);
    expect(formatMention(`{${ID}}`, 'Mario')).toBe(`@[8,{${ID}},"Mario"]`);
  });

  it('format_escapes_display', () => {
    expect(formatMention(ID, 'Ma"rio Ros]si')).toBe(`@[8,{${ID}},"Mario Rossi"]`);
  });

  it('format_then_parse_roundtrips', () => {
    expect(parseMentions(formatMention(ID, 'Mario Rossi'))).toEqual([{ kind: 'mention', entity: '8', id: `{${ID}}`, display: 'Mario Rossi' }]);
  });

  it('isUserMention_is_true_for_8_and_systemuser_only', () => {
    expect(isUserMention({ kind: 'mention', entity: '8', id: 'x', display: 'd' })).toBe(true);
    expect(isUserMention({ kind: 'mention', entity: 'SystemUser', id: 'x', display: 'd' })).toBe(true);
    expect(isUserMention({ kind: 'mention', entity: '1', id: 'x', display: 'd' })).toBe(false);
    expect(isUserMention({ kind: 'text', text: '8' })).toBe(false);
  });
});
