import { bodySections } from './formCells';
import { placeCell, placeHeaderField, placeSection, placeTab, type DesignTab, type FormDesign } from './formDesign';
import type { DragKind, DropAt } from './useDragMove';

/**
 * The design after a drop on the canvas (ruling G1), only in the tab shown, so a section never reaches a column of
 * another tab. For a cell, `before` names a cell and `end` a section; for a section, `before` names a section and `end` a
 * column; for a tab, `before` names a tab and `end` (key `tabs`) the end of the tab list. `d` itself when nothing moves:
 * a drop on the item itself or on its own place, a target that is not in the tab shown, or a header cell (see
 * {@link dropOnHeader}).
 */
export function dropOnCanvas(d: FormDesign, tab: DesignTab, kind: DragKind, key: string, at: DropAt): FormDesign {
  if (at.key === key) return d;
  if (kind === 'cell') {
    const sections = bodySections([tab]);
    const dest = at.where === 'end'
      ? sections.find((s) => s.key === at.key)
      : sections.find((s) => s.cells.some((c) => c.key === at.key));
    if (!dest) return d;
    const rest = dest.cells.filter((c) => c.key !== key);
    return placeCell(d, key, dest.key, at.where === 'end' ? rest.length : rest.findIndex((c) => c.key === at.key));
  }
  if (kind === 'section') {
    const column = at.where === 'end'
      ? tab.columns.find((c) => c.key === at.key)
      : tab.columns.find((c) => c.sections.some((s) => s.key === at.key));
    if (!column) return d;
    const rest = column.sections.filter((s) => s.key !== key);
    return placeSection(d, key, column.key, at.where === 'end' ? rest.length : rest.findIndex((s) => s.key === at.key));
  }
  if (kind === 'tab') {
    if (at.where === 'end') return at.key === 'tabs' ? placeTab(d, key, d.tabs.length - 1) : d;
    const index = d.tabs.filter((x) => x.key !== key).findIndex((x) => x.key === at.key);
    return index < 0 ? d : placeTab(d, key, index);
  }
  return d;
}

/**
 * The design after a header field is dropped before the header cell `at.key`, or at the `end` (key `header`: after every
 * header cell); the index counts all of `d.header`, not only the fields. `d` itself when nothing moves.
 */
export function dropOnHeader(d: FormDesign, key: string, at: DropAt): FormDesign {
  if (at.key === key) return d;
  const rest = d.header.filter((c) => c.key !== key);
  const index = at.where === 'end' ? rest.length : rest.findIndex((c) => c.key === at.key);
  return index < 0 ? d : placeHeaderField(d, key, index);
}
