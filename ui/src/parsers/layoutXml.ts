import type { ViewModel } from '../model/view';
import { parseXml } from './xml';

const DEFAULT_WIDTH = 100;

/** Reads the `<cell>` list of a LayoutXml grid, in order. `alias.attr` cells are split. */
export function parseLayout(xml: string): ViewModel['columns'] {
  const doc = parseXml(xml);
  const row = doc.getElementsByTagName('row')[0];
  if (!row) return [];
  return Array.from(row.children)
    .filter((c) => c.tagName === 'cell')
    .map((cell) => {
      const name = cell.getAttribute('name') ?? '';
      const width = Number.parseInt(cell.getAttribute('width') ?? '', 10);
      const dot = name.indexOf('.');
      const column: ViewModel['columns'][number] = {
        name,
        // A cell without a width keeps the 100px default.
        width: Number.isFinite(width) ? width : DEFAULT_WIDTH,
        attribute: dot < 0 ? name : name.slice(dot + 1),
        isHidden: cell.getAttribute('ishidden') === '1',
      };
      if (dot > 0) column.alias = name.slice(0, dot);
      return column;
    });
}
