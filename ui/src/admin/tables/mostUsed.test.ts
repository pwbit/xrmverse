import { describe, expect, it } from 'vitest';
import { cachedMostUsed, forgetMostUsed, isListCandidate, mostUsed } from './mostUsed';
import { attr } from './testAttrs';

describe('mostUsed', () => {
  it('never lists a party list or a note body (not properties of the record, or the whole file)', () => {
    expect(isListCandidate(attr('to', { kind: 'partylist', typeName: 'PartyList', targets: ['contact'] }))).toBe(false);
    expect(isListCandidate(attr('documentbody', { kind: 'string', typeName: 'String', hidden: true }))).toBe(false);
  });

  it('mostUsed_picks_top5_by_non_null_count_ties_by_label', () => {
    const attributes = [
      attr('xv_id', { isPrimaryId: true }),
      attr('xv_name', { isPrimaryName: true }),
      attr('xv_zeta', { displayName: 'Zeta' }),
      attr('xv_alpha', { displayName: 'Alpha' }),
      attr('xv_mid', { displayName: 'Mid' }),
      attr('xv_often', { displayName: 'Often' }),
      attr('xv_rare', { displayName: 'Rare' }),
      attr('xv_beta', { displayName: 'Beta' }),
      attr('xv_parent', { displayName: 'Parent', kind: 'lookup', typeName: 'Lookup', targets: ['contact'] }),
      attr('createdon', { displayName: 'Created On', kind: 'datetime', typeName: 'DateTime' }),
      attr('xv_hidden', { displayName: 'Hidden', validForRead: false }),
      attr('xv_image', { displayName: 'Image', kind: 'unsupported', typeName: 'Image' }),
    ];
    const records = [
      { xv_name: 'a', xv_often: 'x', xv_rare: null, xv_zeta: '1', xv_alpha: '1', xv_mid: '1', xv_beta: '1', _xv_parent_value: 'p1', createdon: 't', xv_hidden: 'h', xv_image: 'i' },
      { xv_name: 'b', xv_often: 'x', xv_rare: null, xv_zeta: '1', xv_alpha: '1', xv_mid: '1', xv_beta: '1', _xv_parent_value: 'p2', createdon: 't', xv_hidden: 'h', xv_image: 'i' },
      { xv_name: 'c', xv_often: 'x', xv_rare: 'r', xv_zeta: '1', xv_alpha: null, xv_mid: null, xv_beta: null, _xv_parent_value: null, createdon: 't', xv_hidden: 'h', xv_image: 'i' },
    ];
    // Counts: often 3, zeta 3, alpha 2, beta 2, mid 2, parent 2 (read from _xv_parent_value), rare 1.
    // Ties break by label: Often, Zeta (3), then Alpha, Beta, Mid (2); Parent (2) and Rare (1) fall outside the top 5.
    expect(mostUsed(records, attributes)).toEqual(['xv_often', 'xv_zeta', 'xv_alpha', 'xv_beta', 'xv_mid']);
  });

  it('excludes system, unreadable, key, primary name and unsupported columns, and is stable on an empty page', () => {
    const attributes = [
      attr('xv_id', { isPrimaryId: true }),
      attr('xv_name', { isPrimaryName: true }),
      attr('modifiedby', { kind: 'lookup', targets: ['systemuser'] }),
      attr('xv_b', { displayName: 'B' }),
      attr('xv_a', { displayName: 'A' }),
      attr('xv_no_read', { displayName: '0', validForRead: false }),
      // A thumbnail or a file id is no list cell.
      attr('xv_photo', { displayName: '1', kind: 'image', typeName: 'Image' }),
      attr('xv_doc', { displayName: '2', kind: 'file', typeName: 'File' }),
    ];
    expect(mostUsed([], attributes)).toEqual(['xv_a', 'xv_b']);
  });
});

describe('cachedMostUsed', () => {
  const session = {};
  it('shares one computation between concurrent callers and forgets a failed one', async () => {
    forgetMostUsed('t');
    let calls = 0;
    const [a, b] = await Promise.all([
      cachedMostUsed(session, 't', async () => (calls++, ['x'])),
      cachedMostUsed(session, 't', async () => (calls++, ['y'])),
    ]);
    expect([a, b, calls]).toEqual([['x'], ['x'], 1]);
    forgetMostUsed('t');
    await expect(cachedMostUsed(session, 't', () => Promise.reject(new Error('boom')))).rejects.toThrow('boom');
    await expect(cachedMostUsed(session, 't', async () => ['z'])).resolves.toEqual(['z']);
    forgetMostUsed('t');
  });

  it('starts over for a new sign-in', async () => {
    const first = {};
    const second = {};
    await expect(cachedMostUsed(first, 't', async () => ['x'])).resolves.toEqual(['x']);
    await expect(cachedMostUsed(first, 't', async () => ['other'])).resolves.toEqual(['x']);
    // Another user may not read the first user's columns: the choice is computed again.
    await expect(cachedMostUsed(second, 't', async () => ['y'])).resolves.toEqual(['y']);
    forgetMostUsed('t');
  });
});
