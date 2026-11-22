import { describe, expect, it } from 'vitest';
import type { AttributeMeta } from '../tables/metadata';
import { dropOnCanvas, dropOnHeader } from './canvasDrop';
import { addHeaderField, placeCell, placeHeaderField, placeSection, placeTab, readForm } from './formDesign';
import { formRow, RICH_FORM } from './formDesignTestSupport';

const read = () => readForm(formRow(RICH_FORM), 1033);

/** RICH_FORM: tab t0 has the columns t0.c0 (section s0, cells c0–c4) and t0.c1 (section s1, cell c5); tab t1 follows. */
describe('canvasDrop', () => {
  it('a cell dropped before a cell of another section is placed at that cell\'s index', () => {
    const d = read();
    const next = dropOnCanvas(d, d.tabs[0], 'cell', 'c0', { where: 'before', key: 'c5' });
    expect(next).not.toBe(d);
    expect(next).toEqual(placeCell(d, 'c0', 's1', 0));
  });

  it('a cell dropped at the end of a section goes last', () => {
    const d = read();
    const next = dropOnCanvas(d, d.tabs[0], 'cell', 'c0', { where: 'end', key: 's1' });
    expect(next).toEqual(placeCell(d, 'c0', 's1', 1));
    expect(next.tabs[0].columns[1].sections[0].cells.map((c) => c.key)).toEqual(['c5', 'c0']);
  });

  it('a section dropped at the end of a column goes last', () => {
    const d = read();
    const next = dropOnCanvas(d, d.tabs[0], 'section', 's1', { where: 'end', key: 't0.c0' });
    expect(next).toEqual(placeSection(d, 's1', 't0.c0', 1));
    expect(next.tabs[0].columns[0].sections.map((s) => s.key)).toEqual(['s0', 's1']);
  });

  it('a section dropped before a section takes its index', () => {
    const d = read();
    expect(dropOnCanvas(d, d.tabs[0], 'section', 's1', { where: 'before', key: 's0' })).toEqual(placeSection(d, 's1', 't0.c0', 0));
  });

  it('a tab dropped before a tab takes its index', () => {
    const d = read();
    const next = dropOnCanvas(d, d.tabs[0], 'tab', 't1', { where: 'before', key: 't0' });
    expect(next).toEqual(placeTab(d, 't1', 0));
    expect(next.tabs.map((t) => t.key)).toEqual(['t1', 't0']);
  });

  it('a drop on the item itself, or on its own place, returns d', () => {
    const d = read();
    const tab = d.tabs[0];
    expect(dropOnCanvas(d, tab, 'cell', 'c0', { where: 'before', key: 'c0' })).toBe(d);
    expect(dropOnCanvas(d, tab, 'cell', 'c0', { where: 'before', key: 'c1' })).toBe(d);
    expect(dropOnCanvas(d, tab, 'cell', 'c5', { where: 'end', key: 's1' })).toBe(d);
    expect(dropOnCanvas(d, tab, 'section', 's0', { where: 'before', key: 's0' })).toBe(d);
    expect(dropOnCanvas(d, tab, 'section', 's1', { where: 'end', key: 't0.c1' })).toBe(d);
    expect(dropOnCanvas(d, tab, 'tab', 't0', { where: 'before', key: 't0' })).toBe(d);
    expect(dropOnCanvas(d, tab, 'tab', 't0', { where: 'before', key: 't1' })).toBe(d);
  });

  it('a target outside the tab shown, or of another kind, moves nothing', () => {
    const d = read();
    // s2 is a section of the other tab: only the tab shown takes drops.
    expect(dropOnCanvas(d, d.tabs[0], 'cell', 'c0', { where: 'end', key: 's2' })).toBe(d);
    expect(dropOnCanvas(d, d.tabs[0], 'section', 's0', { where: 'end', key: 't1.c0' })).toBe(d);
    expect(dropOnCanvas(d, d.tabs[0], 'header', 'h0', { where: 'before', key: 'h1' })).toBe(d);
  });

  it('dropOnHeader places a header field before a header cell; on itself it returns d', () => {
    const d = addHeaderField(read(), { logicalName: 'fax', displayName: 'Fax', kind: 'string', requiredLevel: 'None' } as AttributeMeta);
    const field = d.header[d.header.length - 1];
    const first = d.header[0];
    const next = dropOnHeader(d, field.key, { where: 'before', key: first.key });
    expect(next).toEqual(placeHeaderField(d, field.key, 0));
    expect(next.header[0]).toBe(field);
    expect(dropOnHeader(d, field.key, { where: 'before', key: field.key })).toBe(d);
    expect(dropOnHeader(d, first.key, { where: 'before', key: d.header[1].key })).toBe(d);
  });

  it('a tab dropped at the end goes last; a header field dropped at the end goes after every header cell (H7)', () => {
    const d = read();
    const next = dropOnCanvas(d, d.tabs[0], 'tab', 't0', { where: 'end', key: 'tabs' });
    expect(next).toEqual(placeTab(d, 't0', d.tabs.length - 1));
    expect(next.tabs.map((t) => t.key)).toEqual(['t1', 't0']);
    expect(dropOnCanvas(d, d.tabs[0], 'tab', 't1', { where: 'end', key: 'tabs' })).toBe(d);

    const h = addHeaderField(read(), { logicalName: 'fax', displayName: 'Fax', kind: 'string', requiredLevel: 'None' } as AttributeMeta);
    const first = h.header[0];
    const moved = dropOnHeader(h, first.key, { where: 'end', key: 'header' });
    expect(moved).toEqual(placeHeaderField(h, first.key, h.header.length - 1));
    expect(moved.header[moved.header.length - 1]).toBe(first);
    expect(dropOnHeader(h, h.header[h.header.length - 1].key, { where: 'end', key: 'header' })).toBe(h);
  });
});
