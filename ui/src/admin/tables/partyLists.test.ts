import { describe, expect, it } from 'vitest';
import { partyText } from './partyLists';

const FORMATTED = '@OData.Community.Display.V1.FormattedValue';
const LOGICAL = '@Microsoft.Dynamics.CRM.lookuplogicalname';

describe('partyText', () => {
  it('shows the name and the table of a party before its address', () => {
    expect(partyText({ _partyid_value: 'c1', [`_partyid_value${LOGICAL}`]: 'contact', [`_partyid_value${FORMATTED}`]: 'Ada', addressused: 'ada@example.com' }, '?')).toBe(
      'Ada (contact)',
    );
  });

  it('shows the table and the id of a party without a name, never its address alone', () => {
    expect(partyText({ _partyid_value: 'c1', [`_partyid_value${LOGICAL}`]: 'contact', addressused: 'ada@example.com' }, '?')).toBe('contact c1');
  });

  it('shows the address of a row without a party, and never an empty text', () => {
    expect(partyText({ _partyid_value: null, addressused: 'bob@example.com' }, '?')).toBe('bob@example.com');
    expect(partyText({ _partyid_value: null, addressused: null }, 'Unknown participant')).toBe('Unknown participant');
  });
});
