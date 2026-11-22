import type { DesignCell, DesignSection, DesignTab, FormDesign } from './formDesign';

// Walks of the design's cells shared by the form model files. Only types come from `formDesign.ts`, so there is no
// runtime import cycle.

/** `s` trimmed and in lower case; empty for null and undefined. */
export const lower = (s: string | null | undefined): string => (s ?? '').trim().toLowerCase();

/** Every section of the body, in reading order (tabs, columns, sections). */
export const bodySections = (tabs: DesignTab[]): DesignSection[] => tabs.flatMap((t) => t.columns.flatMap((c) => c.sections));

/** Every body cell, in reading order. */
export const bodyCells = (d: Pick<FormDesign, 'tabs'>): DesignCell[] => bodySections(d.tabs).flatMap((s) => s.cells);

/** Every cell of the form in form order: the header first, then the body. */
export const allCells = (d: Pick<FormDesign, 'tabs' | 'header'>): DesignCell[] => [...d.header, ...bodyCells(d)];

/**
 * The design with `f` applied to the cell `cellKey`, body or header; `d` itself for an unknown key. A plain patch: it
 * does not apply ruling F7 (no control is removed).
 */
export function mapCell(d: FormDesign, cellKey: string, f: (c: DesignCell) => DesignCell): FormDesign {
  const inHeader = d.header.some((c) => c.key === cellKey);
  if (!inHeader && !bodyCells(d).some((c) => c.key === cellKey)) return d;
  const patch = (c: DesignCell): DesignCell => (c.key === cellKey ? f(c) : c);
  return {
    ...d,
    tabs: d.tabs.map((t) => ({
      ...t,
      columns: t.columns.map((c) => ({
        ...c, sections: c.sections.map((s) => (s.cells.some((x) => x.key === cellKey) ? { ...s, cells: s.cells.map(patch) } : s)),
      })),
    })),
    header: inHeader ? d.header.map(patch) : d.header,
  };
}

/** The cell with its `colSpan` clamped to a section of `columns` columns; the same object when it fits. */
export const clampSpan = (cell: DesignCell, columns: number): DesignCell =>
  cell.colSpan > columns ? { ...cell, colSpan: columns } : cell;
