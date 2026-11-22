import { describe, expect, it } from 'vitest';
import type { Section } from '../../model/form';
import type { GridPlacement } from '../../parsers/presentationJson';
import { section, tab } from '../classic/form/formTestSupport';
import { GRID_COLUMNS, placeSections } from './gridLayout';

const s = (name: string): Section => section([], { name });
const at = (col: number, span: number, row: number, rowSpan = 1): GridPlacement => ({ col, span, row, rowSpan });
const all = () => true;

/** A tab whose FormXml splits its sections over two columns (ignored by the grid). */
const twoColumns = (...names: string[][]) => tab('t', names.map((n) => ({ width: '50%', sections: n.map(s) })));
const summary = (placed: ReturnType<typeof placeSections>) => placed.map((p) => `${p.section.name}@${p.col}+${p.span}/${p.row}+${p.rowSpan}`);

describe('placeSections (ruling C3)', () => {
  it('has 12 columns', () => {
    expect(GRID_COLUMNS).toBe(12);
  });

  it('places sections side by side on the same row, ignoring the FormXml column split', () => {
    const placed = placeSections(twoColumns(['a'], ['b']), { a: at(1, 6, 1), b: at(7, 6, 1) }, all, () => {});
    expect(summary(placed)).toEqual(['a@1+6/1+1', 'b@7+6/1+1']);
  });

  it('orders the placed sections by row then column', () => {
    const placed = placeSections(twoColumns(['a', 'b'], ['c']), { a: at(7, 6, 2), b: at(1, 6, 2), c: at(1, 12, 1) }, all, () => {});
    expect(summary(placed)).toEqual(['c@1+12/1+1', 'b@1+6/2+1', 'a@7+6/2+1']);
  });

  it('puts the visible unplaced sections after the last used row, full width, in FormXml order', () => {
    const placed = placeSections(twoColumns(['a', 'x'], ['b', 'y']), { a: at(1, 6, 1, 2), b: at(7, 6, 1) }, all, () => {});
    expect(summary(placed)).toEqual(['a@1+6/1+2', 'b@7+6/1+1', 'x@1+12/3+1', 'y@1+12/4+1']);
  });

  it('warns once per unknown section name and ignores it', () => {
    const warnings: string[] = [];
    const placed = placeSections(twoColumns(['a']), { a: at(1, 6, 1), ghost: at(7, 6, 1) }, all, (m) => warnings.push(m));
    expect(summary(placed)).toEqual(['a@1+6/1+1']);
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain('ghost');
  });

  it('a hidden section is not placed and leaves no row of its own', () => {
    const tabModel = twoColumns(['a', 'b', 'c']);
    const placements = { a: at(1, 12, 1), b: at(1, 12, 2), c: at(1, 12, 3) };
    const placed = placeSections(tabModel, placements, (x) => x.name !== 'b', () => {});
    expect(summary(placed)).toEqual(['a@1+12/1+1', 'c@1+12/2+1']);
  });

  it('a huge row number only orders: the rows are compacted', () => {
    const placed = placeSections(twoColumns(['a', 'b', 'c']), { a: at(1, 6, 1_000_000), b: at(7, 6, 1_000_000, 500) }, all, () => {});
    expect(summary(placed)).toEqual(['a@1+6/1+1', 'b@7+6/1+2', 'c@1+12/3+1']);
  });

  it('keeps overlapping row spans consistent after compaction', () => {
    const placed = placeSections(twoColumns(['a', 'b', 'c']), { a: at(1, 6, 10, 20), b: at(7, 6, 10), c: at(7, 6, 25) }, all, () => {});
    // Boundaries 10, 11, 25, 26, 30 → rows 1..4.
    expect(summary(placed)).toEqual(['a@1+6/1+4', 'b@7+6/1+1', 'c@7+6/3+1']);
  });
});
