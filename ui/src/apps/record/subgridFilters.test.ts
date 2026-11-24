import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  mergeSubgridFilterNames,
  mergeSubgridFilters,
  parseSubgridFilterNames,
  parseSubgridFilters,
} from './subgridFilters';

const FORM = 'abc-123';

afterEach(() => vi.restoreAllMocks());

describe('parseSubgridFilterNames', () => {
  it('trims names of a JSON array', () => {
    expect(parseSubgridFilterNames('["a.json"," b.json "]')).toEqual(['a.json', 'b.json']);
  });

  it('treats an empty or missing value as no names, silently', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    expect(parseSubgridFilterNames('')).toEqual([]);
    expect(parseSubgridFilterNames(undefined)).toEqual([]);
    expect(warn).not.toHaveBeenCalled();
  });

  it.each(['x', '[1]', '{"a":1}', '["a",""]'])('warns and returns nothing for %s', (value) => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    expect(parseSubgridFilterNames(value)).toEqual([]);
    expect(warn).toHaveBeenCalledTimes(1);
    expect(warn.mock.calls[0]![0]).toBe('[xrmverse subgrid filter]');
  });
});

describe('mergeSubgridFilterNames', () => {
  it('puts the app names first and dedupes case-insensitively', () => {
    expect(mergeSubgridFilterNames(['A.json'], ['a.json', 'b.json'])).toEqual(['A.json', 'b.json']);
  });
});

describe('parseSubgridFilters', () => {
  it('normalises form and control ids', () => {
    const { map, warnings } = parseSubgridFilters(
      { forms: { '{ABC-123}': { ' Sub_Cases ': { source: ' Xrmv_Customer ', target: 'XRMV_CustomerId' } } } },
      'r.json',
    );
    expect(map).toEqual({ [FORM]: { sub_cases: { source: 'xrmv_customer', target: 'xrmv_customerid' } } });
    expect(warnings).toEqual([]);
  });

  it('drops incomplete entries with a warning naming the origin', () => {
    const { map, warnings } = parseSubgridFilters(
      { forms: { [FORM]: { a: { source: 's' }, b: { source: 's', target: 3 }, c: 'x', ok: { source: 's', target: 't' } } } },
      'r.json',
    );
    expect(Object.keys(map[FORM]!)).toEqual(['ok']);
    expect(warnings).toHaveLength(3);
    for (const w of warnings) expect(w).toContain('r.json');
  });

  it('ignores unknown keys with a warning', () => {
    const { map, warnings } = parseSubgridFilters({ forms: {}, extra: 1 }, 'r.json');
    expect(map).toEqual({});
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain('r.json');
  });

  it.each([null, [], 'x', { forms: [] }, { forms: 3 }, {}])('gives an empty map and a warning for %j', (json) => {
    const { map, warnings } = parseSubgridFilters(json, 'r.json');
    expect(map).toEqual({});
    expect(warnings.length).toBeGreaterThan(0);
  });

  it('treats prototype-named keys as plain keys', () => {
    const { map, warnings } = parseSubgridFilters(
      JSON.parse('{"forms":{"__proto__":{"a":{"source":"x","target":"y"}}}}'),
      'r.json',
    );
    expect(map['__proto__']!['a']).toEqual({ source: 'x', target: 'y' });
    expect(warnings).toEqual([]);
    expect(({} as Record<string, unknown>)['a']).toBeUndefined();
    expect(Object.keys(Object.prototype)).toEqual([]);
  });

  it('keeps the last of two entries that normalise to the same control, with a warning', () => {
    const { map, warnings } = parseSubgridFilters(
      { forms: { '{ABC-123}': { A: { source: 's1', target: 't1' }, ' a ': { source: 's2', target: 't2' } }, 'abc-123': {} } },
      'r.json',
    );
    expect(map[FORM]!['a']).toEqual({ source: 's2', target: 't2' });
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain('r.json');
    expect(warnings[0]).toContain('is given more than once; the last entry is used.');
  });

  it('does not log', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    parseSubgridFilters(null, 'r.json');
    expect(warn).not.toHaveBeenCalled();
  });
});

describe('mergeSubgridFilters', () => {
  it('first resource wins on the same control', () => {
    const merged = mergeSubgridFilters([
      { [FORM]: { a: { source: 's1', target: 't1' } } },
      { [FORM]: { a: { source: 's2', target: 't2' }, b: { source: 's3', target: 't3' } }, other: { c: { source: 's4', target: 't4' } } },
    ]);
    expect(merged).toEqual({
      [FORM]: { a: { source: 's1', target: 't1' }, b: { source: 's3', target: 't3' } },
      other: { c: { source: 's4', target: 't4' } },
    });
  });

  it('first wins for a control named like an Object.prototype member', () => {
    const merged = mergeSubgridFilters([
      { [FORM]: { constructor: { source: 's1', target: 't1' } } },
      { [FORM]: { constructor: { source: 's2', target: 't2' }, toString: { source: 's3', target: 't3' } } },
    ]);
    expect(merged[FORM]!['constructor']).toEqual({ source: 's1', target: 't1' });
    expect(merged[FORM]!['toString']).toEqual({ source: 's3', target: 't3' });
  });
});
