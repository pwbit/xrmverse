import { describe, expect, it } from 'vitest';
import { parseLayout } from './layoutXml';

describe('parseLayout', () => {
  it('keeps order and widths, and splits alias.attr', () => {
    const cols = parseLayout(
      '<grid><row><cell name="name" width="250"/><cell name="pc.fullname" width="120"/><cell name="x" ishidden="1"/></row></grid>',
    );
    expect(cols).toEqual([
      { name: 'name', width: 250, attribute: 'name', isHidden: false },
      { name: 'pc.fullname', width: 120, alias: 'pc', attribute: 'fullname', isHidden: false },
      { name: 'x', width: 100, attribute: 'x', isHidden: true },
    ]);
  });
});
