import { childElements, parseXml } from '../../parsers/xml';
import { indexFilter, readFilter, writeFilter, writeNode, type FilterGroup, type FilterNode } from './filterModel';

export interface DesignColumn { name: string; attribute: string; alias?: string; width: number }
export interface DesignLink { alias: string; entity: string; from: string; to: string }
export interface DesignOrder { attribute: string; descending: boolean }
export interface ViewDesign {
  table: string; primaryId: string; primaryName: string;
  name: string; description: string; queryType: number; isDefault: boolean; isManaged: boolean;
  columns: DesignColumn[]; links: DesignLink[];
  orders: DesignOrder[]; ordersEditable: boolean;
  /** Root, type 'and'; children = the entity's top-level filters (then the read-only filters of its link-entities). */
  filter: FilterGroup;
  /** Quick find views (querytype 4) only (E6). */
  findColumns: string[];
  /** The originals: a save edits a copy of them, so what the designer does not model survives. */
  source: { fetchXml: string; layoutXml: string };
}
export interface ViewRow {
  name: string; description?: string | null; querytype: number; isdefault: boolean; ismanaged?: boolean;
  fetchxml: string; layoutxml: string; returnedtypecode: string;
}
interface EntityKeys { primaryId: string; primaryName: string }

const ROOT_KEY = 'root';
const MIN_WIDTH = 25;
const MAX_WIDTH = 1000;
const DEFAULT_WIDTH = 100;
const PRIMARY_NAME_WIDTH = 150;

const serialize = (doc: Node) => new XMLSerializer().serializeToString(doc);

function entityOf(doc: Document): Element {
  const fetch = doc.documentElement;
  const entity = fetch.tagName === 'fetch' ? childElements(fetch, 'entity')[0] : undefined;
  if (!entity) throw new Error('Not a FetchXML document.');
  return entity;
}

function rowOf(layout: Document): Element {
  const row = layout.getElementsByTagName('row')[0];
  if (!row) throw new Error('Not a LayoutXML document.');
  return row;
}

function insideLinkEntity(node: Element, entity: Element): boolean {
  for (let p = node.parentElement; p && p !== entity; p = p.parentElement) if (p.tagName === 'link-entity') return true;
  return false;
}

const isQuickFindFilter = (f: Element) => f.getAttribute('isquickfindfields') === '1';
const topFilters = (entity: Element) => childElements(entity, 'filter').filter((f) => !isQuickFindFilter(f));
const quickFindFilter = (entity: Element) => childElements(entity, 'filter').find(isQuickFindFilter);
/** The `<filter>` children of every link-entity under the entity, in document order. */
const linkFilters = (entity: Element) =>
  Array.from(entity.getElementsByTagName('filter')).filter((f) => f.parentElement?.tagName === 'link-entity');
const directLinks = (entity: Element) => childElements(entity, 'link-entity');
const linkByAlias = (entity: Element, alias: string) => directLinks(entity).find((l) => l.getAttribute('alias') === alias);

function readRoot(entity: Element): FilterGroup {
  const children: FilterNode[] = [
    ...topFilters(entity).map((f, i) => readFilter(f, entity, `f${i}`)),
    ...linkFilters(entity).map((f, i) => readFilter(f, entity, `l${i}`)),
  ];
  return { kind: 'group', type: 'and', children, key: ROOT_KEY };
}

function readColumns(entity: Element, layout: Document): DesignColumn[] {
  const rootAttributes = childElements(entity, 'attribute');
  return childElements(rowOf(layout), 'cell').map((cell) => {
    const name = cell.getAttribute('name') ?? '';
    const parsed = Number.parseInt(cell.getAttribute('width') ?? '', 10);
    const width = Number.isFinite(parsed) ? parsed : DEFAULT_WIDTH;
    const dot = name.indexOf('.');
    if (dot > 0) return { name, attribute: name.slice(dot + 1), alias: name.slice(0, dot), width };
    // A cell may show an aliased `<attribute name="x" alias="y"/>` by its alias.
    const aliased = rootAttributes.find((a) => a.getAttribute('alias') === name);
    return { name, attribute: aliased?.getAttribute('name') ?? name, width };
  });
}

function readOrders(entity: Element): { orders: DesignOrder[]; ordersEditable: boolean } {
  const elements = childElements(entity, 'order');
  const orders = elements
    .filter((o) => o.hasAttribute('attribute'))
    .map((o) => ({ attribute: o.getAttribute('attribute') ?? '', descending: o.getAttribute('descending') === 'true' }));
  const linkOrders = Array.from(entity.getElementsByTagName('order')).some((o) => o.parentElement !== entity);
  const plain = elements.every((o) =>
    o.hasAttribute('attribute') && Array.from(o.attributes).every((a) => a.name === 'attribute' || a.name === 'descending'));
  return { orders, ordersEditable: elements.length <= 2 && !linkOrders && plain };
}

export function readView(row: ViewRow, entity: EntityKeys): ViewDesign {
  const fetchDoc = parseXml(row.fetchxml);
  const ent = entityOf(fetchDoc);
  const links = directLinks(ent)
    .filter((l) => l.getAttribute('alias'))
    .map((l) => ({
      alias: l.getAttribute('alias') ?? '',
      entity: l.getAttribute('name') ?? '',
      from: l.getAttribute('from') ?? '',
      to: l.getAttribute('to') ?? '',
    }));
  const qf = quickFindFilter(ent);
  const findColumns = row.querytype === 4 && qf
    ? [...new Set(childElements(qf, 'condition').map((c) => c.getAttribute('attribute') ?? ''))]
    : [];
  return {
    table: row.returnedtypecode,
    primaryId: entity.primaryId,
    primaryName: entity.primaryName,
    name: row.name,
    description: row.description ?? '',
    queryType: row.querytype,
    isDefault: row.isdefault === true,
    isManaged: row.ismanaged ?? false,
    columns: readColumns(ent, parseXml(row.layoutxml)),
    links,
    ...readOrders(ent),
    filter: readRoot(ent),
    findColumns,
    source: { fetchXml: row.fetchxml, layoutXml: row.layoutxml },
  };
}

/** Inserts `node` before `anchor`, or appends it to `parent` when there is none. */
function insert(parent: Element, node: Node, anchor: Node | null | undefined) {
  parent.insertBefore(node, anchor && anchor.parentNode === parent ? anchor : null);
}

/** Inserts after the last `tag` child of `parent`, or as its first child. */
function insertAfterLast(parent: Element, node: Element, tag: string) {
  const last = childElements(parent, tag).at(-1);
  parent.insertBefore(node, last ? last.nextSibling : parent.firstChild);
}

function writeFilters(d: ViewDesign, doc: Document, entity: Element) {
  if (JSON.stringify(readRoot(entity)) === JSON.stringify(d.filter)) return;
  const tops = topFilters(entity);
  const links = linkFilters(entity);
  const sources = new Map<string, Element>();
  tops.forEach((f, i) => indexFilter(f, `f${i}`, sources));
  const linkKeys = new Set(links.map((_, i) => `l${i}`));

  // A read-only filter of a link-entity stays where it is unless the user deleted it.
  const kept = new Set(d.filter.children.filter((c) => linkKeys.has(c.key)).map((c) => c.key));
  links.forEach((f, i) => { if (!kept.has(`l${i}`)) f.parentNode?.removeChild(f); });

  const written: Element[] = [];
  let loose: Element | undefined;
  for (const child of d.filter.children) {
    if (linkKeys.has(child.key)) continue;
    if (child.kind === 'condition') {
      // Conditions added at the root go into one `<filter type="and">`.
      if (!loose) {
        loose = doc.createElement('filter');
        loose.setAttribute('type', 'and');
        written.push(loose);
      }
      loose.appendChild(writeNode(child, doc, sources));
    } else if (child.kind === 'group') written.push(writeFilter(child, doc, sources));
    else written.push(writeNode(child, doc, sources));
  }
  const anchor = tops[0] ?? quickFindFilter(entity);
  for (const f of written) insert(entity, f, anchor);
  for (const f of tops) entity.removeChild(f);
}

function writeQuickFind(d: ViewDesign, doc: Document, entity: Element) {
  if (d.queryType !== 4) return;
  let qf = quickFindFilter(entity);
  const conditions = qf ? childElements(qf, 'condition') : [];
  const current = [...new Set(conditions.map((c) => c.getAttribute('attribute') ?? ''))];
  if (current.length === d.findColumns.length && current.every((a, i) => a === d.findColumns[i])) return;
  const wanted = new Set(d.findColumns);
  for (const c of conditions) if (!wanted.has(c.getAttribute('attribute') ?? '')) qf?.removeChild(c);
  const have = new Set(current);
  for (const attribute of d.findColumns) {
    if (have.has(attribute)) continue;
    if (!qf) {
      qf = doc.createElement('filter');
      qf.setAttribute('type', 'or');
      qf.setAttribute('isquickfindfields', '1');
      entity.appendChild(qf);
    }
    const c = doc.createElement('condition');
    c.setAttribute('attribute', attribute);
    c.setAttribute('operator', 'like');
    c.setAttribute('value', '{0}');
    qf.appendChild(c);
  }
  if (qf && qf.children.length === 0) entity.removeChild(qf);
}

function writeOrders(d: ViewDesign, doc: Document, entity: Element) {
  if (!d.ordersEditable) return;
  const old = childElements(entity, 'order');
  const current = readOrders(entity).orders;
  if (current.length === d.orders.length
    && current.every((o, i) => o.attribute === d.orders[i].attribute && o.descending === d.orders[i].descending)) return;
  const anchor = old[0] ?? childElements(entity, 'filter')[0] ?? directLinks(entity)[0];
  for (const o of d.orders) {
    const order = doc.createElement('order');
    order.setAttribute('attribute', o.attribute);
    order.setAttribute('descending', String(o.descending));
    insert(entity, order, anchor);
  }
  for (const o of old) entity.removeChild(o);
}

/** Does a condition or order still need the root column `attribute`? */
function usedAtRoot(entity: Element, attribute: string): boolean {
  const conditions = Array.from(entity.getElementsByTagName('condition'))
    .filter((c) => !insideLinkEntity(c, entity) && !c.hasAttribute('entityname'));
  return conditions.some((c) => c.getAttribute('attribute') === attribute)
    || childElements(entity, 'order').some((o) => o.getAttribute('attribute') === attribute);
}

/** Does a condition or order still need the column `attribute` of the link-entity `link`? */
function usedInLink(entity: Element, link: Element, attribute: string): boolean {
  const alias = link.getAttribute('alias');
  return Array.from(link.getElementsByTagName('condition')).some((c) => c.getAttribute('attribute') === attribute)
    || Array.from(link.getElementsByTagName('order')).some((o) => o.getAttribute('attribute') === attribute)
    || Array.from(entity.getElementsByTagName('condition'))
      .some((c) => c.getAttribute('entityname') === alias && c.getAttribute('attribute') === attribute);
}

function writeFetchColumns(d: ViewDesign, doc: Document, entity: Element, sourceColumns: DesignColumn[]) {
  const rootAttribute = (name: string) =>
    childElements(entity, 'attribute').find((a) => (a.getAttribute('alias') ?? a.getAttribute('name')) === name);
  const linkAttribute = (link: Element, attribute: string) =>
    childElements(link, 'attribute').find((a) => a.getAttribute('name') === attribute);

  for (const column of d.columns) {
    if (!column.alias) {
      if (rootAttribute(column.name)) continue;
      const a = doc.createElement('attribute');
      a.setAttribute('name', column.attribute);
      insertAfterLast(entity, a, 'attribute');
      continue;
    }
    let link = linkByAlias(entity, column.alias);
    if (!link) {
      const def = d.links.find((l) => l.alias === column.alias);
      if (!def) continue;
      link = doc.createElement('link-entity');
      link.setAttribute('name', def.entity);
      link.setAttribute('from', def.from);
      link.setAttribute('to', def.to);
      link.setAttribute('link-type', 'outer');
      link.setAttribute('alias', def.alias);
      entity.appendChild(link);
    }
    if (linkAttribute(link, column.attribute)) continue;
    const a = doc.createElement('attribute');
    a.setAttribute('name', column.attribute);
    insertAfterLast(link, a, 'attribute');
  }

  const shown = new Set(d.columns.map((c) => c.name));
  const stillShown = (alias: string | undefined, attribute: string) =>
    d.columns.some((c) => c.alias === alias && c.attribute === attribute);
  for (const column of sourceColumns) {
    if (shown.has(column.name) || stillShown(column.alias, column.attribute)) continue;
    if (!column.alias) {
      const a = rootAttribute(column.name);
      if (a && column.attribute !== d.primaryId && !usedAtRoot(entity, column.attribute)) entity.removeChild(a);
      continue;
    }
    const link = linkByAlias(entity, column.alias);
    const a = link && linkAttribute(link, column.attribute);
    if (!link || !a || usedInLink(entity, link, column.attribute)) continue;
    link.removeChild(a);
    // An outer link-entity the designer emptied goes; an inner one still restricts the rows, so it stays.
    if (link.children.length === 0 && link.getAttribute('link-type') === 'outer') entity.removeChild(link);
  }
}

function writeLayout(d: ViewDesign): string {
  const layout = parseXml(d.source.layoutXml);
  const row = rowOf(layout);
  const cells = childElements(row, 'cell');
  const widthOf = (c: Element) => Number.parseInt(c.getAttribute('width') ?? '', 10);
  const unchanged = cells.length === d.columns.length
    && cells.every((c, i) => c.getAttribute('name') === d.columns[i].name && widthOf(c) === d.columns[i].width);
  if (unchanged) return serialize(layout);
  const byName = new Map(cells.map((c) => [c.getAttribute('name') ?? '', c]));
  const anchor = cells[0];
  for (const column of d.columns) {
    const source = byName.get(column.name);
    const cell = source ? (source.cloneNode(true) as Element) : layout.createElement('cell');
    if (!source) cell.setAttribute('name', column.name);
    if (!source || widthOf(source) !== column.width) cell.setAttribute('width', String(column.width));
    insert(row, cell, anchor);
  }
  for (const c of cells) row.removeChild(c);
  return serialize(layout);
}

/** FetchXML and LayoutXML of the design: a copy of the source documents with the user's changes applied. */
export function writeView(design: ViewDesign): { fetchxml: string; layoutxml: string } {
  const doc = parseXml(design.source.fetchXml);
  const entity = entityOf(doc);
  const sourceColumns = readColumns(entity, parseXml(design.source.layoutXml));
  writeFilters(design, doc, entity);
  writeQuickFind(design, doc, entity);
  writeOrders(design, doc, entity);
  writeFetchColumns(design, doc, entity, sourceColumns);
  return { fetchxml: serialize(doc), layoutxml: writeLayout(design) };
}

/** A new Public view: the primary name column, sorted by it, active rows only. */
export function newView(table: string, entity: EntityKeys, name: string): ViewDesign {
  const fetch = parseXml('<fetch version="1.0" mapping="logical"><entity/></fetch>');
  const ent = entityOf(fetch);
  const add = (parent: Element, tag: string, attributes: Record<string, string>) => {
    const el = parent.ownerDocument.createElement(tag);
    for (const [k, v] of Object.entries(attributes)) el.setAttribute(k, v);
    parent.appendChild(el);
    return el;
  };
  ent.setAttribute('name', table);
  if (entity.primaryName) add(ent, 'attribute', { name: entity.primaryName });
  add(ent, 'attribute', { name: entity.primaryId });
  if (entity.primaryName) add(ent, 'order', { attribute: entity.primaryName, descending: 'false' });
  add(add(ent, 'filter', { type: 'and' }), 'condition', { attribute: 'statecode', operator: 'eq', value: '0' });

  const layout = parseXml('<grid name="resultset" jump="" select="1" icon="1" preview="1"><row name="result"/></grid>');
  layout.documentElement.setAttribute('jump', entity.primaryName);
  const row = rowOf(layout);
  row.setAttribute('id', entity.primaryId);
  if (entity.primaryName) add(row, 'cell', { name: entity.primaryName, width: String(PRIMARY_NAME_WIDTH) });

  return readView({
    name, description: '', querytype: 0, isdefault: false, ismanaged: false,
    fetchxml: serialize(fetch), layoutxml: serialize(layout), returnedtypecode: table,
  }, entity);
}

export function addColumn(d: ViewDesign, attribute: string, via?: { lookup: string; entity: string; from: string }): ViewDesign {
  if (!via) {
    if (attribute === d.primaryId || d.columns.some((c) => !c.alias && c.name === attribute)) return d;
    const width = attribute === d.primaryName ? PRIMARY_NAME_WIDTH : DEFAULT_WIDTH;
    return { ...d, columns: [...d.columns, { name: attribute, attribute, width }] };
  }
  let links = d.links;
  let link = links.find((l) => l.entity === via.entity && l.from === via.from && l.to === via.lookup);
  if (!link) {
    let alias = `a_${via.lookup}`;
    for (let n = 2; links.some((l) => l.alias === alias); n += 1) alias = `a_${via.lookup}_${n}`;
    link = { alias, entity: via.entity, from: via.from, to: via.lookup };
    links = [...links, link];
  }
  const name = `${link.alias}.${attribute}`;
  if (d.columns.some((c) => c.name === name)) return d;
  return { ...d, links, columns: [...d.columns, { name, attribute, alias: link.alias, width: DEFAULT_WIDTH }] };
}

export function removeColumn(d: ViewDesign, name: string): ViewDesign {
  return { ...d, columns: d.columns.filter((c) => c.name !== name) };
}

export function moveColumn(d: ViewDesign, name: string, delta: -1 | 1): ViewDesign {
  const i = d.columns.findIndex((c) => c.name === name);
  const j = i + delta;
  if (i < 0 || j < 0 || j >= d.columns.length) return d;
  const columns = [...d.columns];
  [columns[i], columns[j]] = [columns[j], columns[i]];
  return { ...d, columns };
}

export function setWidth(d: ViewDesign, name: string, width: number): ViewDesign {
  if (!Number.isFinite(width)) return d;
  const clamped = Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, Math.round(width)));
  return { ...d, columns: d.columns.map((c) => (c.name === name ? { ...c, width: clamped } : c)) };
}
