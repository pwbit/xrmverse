import { describe, expect, it } from 'vitest';
import { addParties, isEmailAddress, partyItems, partyValuesOf, partyText, sameParties, splitAddresses } from './partyLists';

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

describe('party values (plan 4.1b, task 9)', () => {
  const rows = [
    { participationtypemask: 1, _partyid_value: 'q1', [`_partyid_value${LOGICAL}`]: 'queue', [`_partyid_value${FORMATTED}`]: 'Support', addressused: 'support@example.com' },
    { participationtypemask: 2, _partyid_value: 'c1', [`_partyid_value${LOGICAL}`]: 'contact', [`_partyid_value${FORMATTED}`]: 'Ada', addressused: 'ada@example.com' },
    { participationtypemask: 2, _partyid_value: null, addressused: 'orders@example.com' },
  ];

  it('reads the parties of one column from the party rows', () => {
    expect(partyValuesOf('to', rows)).toEqual([
      { id: 'c1', logicalName: 'contact', name: 'Ada', address: 'ada@example.com' },
      { address: 'orders@example.com' },
    ]);
    expect(partyValuesOf('from', rows)).toEqual([{ id: 'q1', logicalName: 'queue', name: 'Support', address: 'support@example.com' }]);
    expect(partyValuesOf('cc', rows)).toEqual([]);
  });

  it('compares parties by record or by address, in order', () => {
    expect(sameParties([{ id: 'C1', logicalName: 'contact', name: 'x' }], [{ id: 'c1', logicalName: 'contact' }])).toBe(true);
    expect(sameParties([{ address: 'A@example.com' }], [{ address: 'a@example.com' }])).toBe(true);
    expect(sameParties([{ address: 'a@example.com' }], [])).toBe(false);
    expect(sameParties([{ id: 'c1', logicalName: 'contact' }], [{ id: 'c1', logicalName: 'account' }])).toBe(false);
  });

  it('adds parties once each', () => {
    const list = [{ id: 'c1', logicalName: 'contact', name: 'Ada' }];
    expect(addParties(list, [{ id: '{C1}', logicalName: 'contact' }, { address: 'b@example.com' }, { address: 'B@example.com' }])).toEqual([
      ...list,
      { address: 'b@example.com' },
    ]);
  });

  it('splits pasted addresses on semicolons and commas', () => {
    expect(splitAddresses(' a@example.com; b@example.com ,c@example.com;; ')).toEqual(['a@example.com', 'b@example.com', 'c@example.com']);
    expect(isEmailAddress('a@example.com')).toBe(true);
    expect(isEmailAddress('Ada')).toBe(false);
  });

  it('writes the items of the party navigation: a bind for a record, addressused for an address alone', () => {
    const sets = { contact: 'contacts', queue: 'queues' };
    expect(
      partyItems('to', [{ id: 'c1', logicalName: 'contact', name: 'Ada', address: 'ada@example.com' }, { address: 'orders@example.com' }], (t) => sets[t as keyof typeof sets]),
    ).toEqual([
      { participationtypemask: 2, 'partyid_contact@odata.bind': '/contacts(c1)' },
      { participationtypemask: 2, addressused: 'orders@example.com' },
    ]);
  });
});
