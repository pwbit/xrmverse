import type { ViewColumn } from '../model/view';
import { parseXml } from '../parsers/xml';
import type { TableInfo } from './loadViews';

interface Link {
  /** The related table. */
  name: string;
  from: string;
  to: string;
  /** The table the link hangs from (the root entity or the enclosing link-entity). */
  parent: string;
}

/** The aliased `<link-entity>` elements of a FetchXML, nested ones included, by alias. */
function links(fetchXml: string): Record<string, Link> {
  const result: Record<string, Link> = {};
  for (const l of Array.from(parseXml(fetchXml).getElementsByTagName('link-entity'))) {
    const alias = l.getAttribute('alias');
    if (!alias) continue;
    result[alias] = {
      name: l.getAttribute('name') ?? '',
      from: l.getAttribute('from') ?? '',
      to: l.getAttribute('to') ?? '',
      parent: (l.parentElement?.getAttribute('name') as string | null) ?? '',
    };
  }
  return result;
}

/**
 * The header label of each view column, by column name (#104). A root column shows its display label. A related table's
 * column (`alias.attr`) shows its label followed by a qualifier, as the view designer names the related row type
 * ("Owning User (User)", Learn: "Choose and configure columns in model-driven app views"): for an N:1 link (its `from` is
 * the related table's primary key) the display label of the lookup it follows (`to` on the parent table), else the
 * related table's display name. A missing label falls back to the logical name.
 */
export function columnHeaders(columns: ViewColumn[], fetchXml: string, tables: Record<string, TableInfo>): Record<string, string> {
  const byAlias = links(fetchXml);
  const root = parseXml(fetchXml).documentElement.getElementsByTagName('entity')[0]?.getAttribute('name') ?? '';
  return Object.fromEntries(
    columns.map((c) => {
      if (!c.alias) return [c.name, tables[root]?.labels[c.attribute] ?? c.name];
      const link = byAlias[c.alias];
      if (!link) return [c.name, c.name];
      const related = tables[link.name];
      const label = related?.labels[c.attribute] ?? c.attribute;
      const qualifier =
        related && link.from !== related.primaryId
          ? (related.displayName ?? link.name)
          : (tables[link.parent]?.labels[link.to] ?? link.to);
      return [c.name, `${label} (${qualifier})`];
    }),
  );
}

/** How a view cell is shown: numbers and dates at the end of the cell (with their header), money with a space after its symbol. */
export type CellKind = 'number' | 'money' | 'date';

const KIND_OF_TYPE: Record<string, CellKind> = {
  Integer: 'number',
  BigInt: 'number',
  Decimal: 'number',
  Double: 'number',
  Money: 'money',
  DateTime: 'date',
};

/** The kind of each column that is not plain text, by column name, from the attribute types of `tables`. */
export function columnKinds(columns: ViewColumn[], fetchXml: string, tables: Record<string, TableInfo>): Record<string, CellKind> {
  const byAlias = links(fetchXml);
  const root = parseXml(fetchXml).documentElement.getElementsByTagName('entity')[0]?.getAttribute('name') ?? '';
  const kinds: Record<string, CellKind> = {};
  for (const c of columns) {
    const table = c.alias ? byAlias[c.alias]?.name : root;
    const type = table ? tables[table]?.types?.[c.attribute] : undefined;
    const kind = type ? KIND_OF_TYPE[type] : undefined;
    if (kind) kinds[c.name] = kind;
  }
  return kinds;
}

/**
 * A formatted money value with a space between its symbol and its digits (`€420,000.00` → `€ 420,000.00`, `-€5.00` →
 * `-€ 5.00`); a value that already has one, or has no leading symbol, is left as it is.
 */
export function spacedMoney(text: string): string {
  return text.replace(/^(-?)([^\d\s.,-]+)(?=[\d.,])/u, '$1$2 ');
}
