import type { Section, Tab } from '../../model/form';
import { GRID_COLUMNS, type GridPlacement } from '../../parsers/presentationJson';

export { GRID_COLUMNS };

/** A section and its cell on the free grid; `row` and `rowSpan` are compacted grid lines, not the overlay's numbers. */
export interface PlacedSection {
  section: Section;
  col: number;
  span: number;
  row: number;
  rowSpan: number;
}

/**
 * Places the tab's visible sections on the free grid (ruling C3), in reading order (row, then column): the placed ones at their
 * overlay cell, then the unplaced ones full width after the last used row, in FormXml order. The overlay's row numbers only
 * order: the rows actually used are renumbered from 1, so a huge `row` cannot create empty rows and a hidden section leaves no
 * row of its own. Warns once per section name the tab does not have.
 */
export function placeSections(tab: Tab, placements: Record<string, GridPlacement>, visible: (s: Section) => boolean, warn: (m: string) => void): PlacedSection[] {
  const sections = tab.columns.flatMap((c) => c.sections);
  const names = new Set(sections.map((s) => s.name));
  for (const name of Object.keys(placements)) {
    if (!names.has(name)) warn(`The grid of tab "${tab.name}" places a section "${name}" that the tab does not have; it is ignored.`);
  }
  const shown = sections.filter(visible);
  const placed = shown.flatMap((section) => {
    const p = Object.hasOwn(placements, section.name) ? placements[section.name] : undefined;
    return p ? [{ section, p }] : [];
  });
  // The grid lines bounding the shown placed sections, renumbered 1, 2, … in order, skipping the bands no section covers.
  const lines = [...new Set(placed.flatMap(({ p }) => [p.row, p.row + p.rowSpan]))].sort((a, b) => a - b);
  const renumbered = new Map<number, number>();
  lines.forEach((n, i) => {
    if (i === 0) return void renumbered.set(n, 1);
    const prev = lines[i - 1]!;
    const covered = placed.some(({ p }) => p.row <= prev && p.row + p.rowSpan >= n);
    renumbered.set(n, renumbered.get(prev)! + (covered ? 1 : 0));
  });
  const line = (n: number) => renumbered.get(n)!;
  const result: PlacedSection[] = placed
    .map(({ section, p }) => ({ section, col: p.col, span: p.span, row: line(p.row), rowSpan: line(p.row + p.rowSpan) - line(p.row) }))
    .sort((a, b) => a.row - b.row || a.col - b.col);
  let next = lines.length > 0 ? line(lines[lines.length - 1]!) : 1;
  for (const section of shown) {
    if (placed.some((x) => x.section === section)) continue;
    result.push({ section, col: 1, span: GRID_COLUMNS, row: next++, rowSpan: 1 });
  }
  return result;
}
