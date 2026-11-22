import { describe, expect, it } from 'vitest';
import { parseText } from './read';
import { valueAt } from './values';

describe('valueAt', () => {
  it('tells the empty value from the quoted text', () => {
    const { doc } = parseText(
      ['kind: workflow', 'name: X', 'entity: a', 'mode: realtime', 'steps:', "  - update: { record: primary, set: { a: ~, b: '~', c: null, d: 'null', e: 1, f: [x, ~] } }", ''].join('\n'),
    );
    expect(valueAt(doc, ['steps', 0, 'update', 'set'])).toEqual({ a: null, b: '~', c: null, d: 'null', e: '1', f: ['x', null] });
    expect(valueAt(doc, ['steps', 0, 'update', 'description'])).toBeUndefined();
    expect(valueAt(doc, ['steps', 0, 'update', 'record'])).toBe('primary');
  });
});
