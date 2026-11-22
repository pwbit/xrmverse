export type FilterNode = FilterGroup | FilterCondition | FilterOpaque;
export interface FilterGroup { kind: 'group'; type: 'and' | 'or'; children: FilterNode[]; key: string }
export interface FilterCondition { kind: 'condition'; attribute: string; operator: string; values: string[]; key: string }
/** A part of the filter the designer does not edit: written back byte for byte (E5). */
export interface FilterOpaque { kind: 'opaque'; xml: string; key: string }

export type ColumnKind = 'text' | 'number' | 'date' | 'choice' | 'lookup' | 'other';

const OPERATORS: Record<ColumnKind, string[]> = {
  text: ['eq', 'ne', 'like', 'not-like', 'begins-with', 'ends-with', 'null', 'not-null'],
  number: ['eq', 'ne', 'gt', 'ge', 'lt', 'le', 'null', 'not-null'],
  date: ['on', 'on-or-after', 'on-or-before', 'today', 'yesterday', 'tomorrow', 'this-week', 'this-month', 'this-year',
    'last-x-days', 'next-x-days', 'null', 'not-null'],
  choice: ['eq', 'ne', 'in', 'not-in', 'null', 'not-null'],
  lookup: ['eq', 'ne', 'null', 'not-null'],
  other: ['null', 'not-null'],
};
const USER_OPERATORS = ['eq-userid', 'ne-userid'];
const BUSINESS_OPERATORS = ['eq-businessid', 'ne-businessid'];
/** Every operator the editor shows; a condition with any other operator is read-only. */
const EDITABLE_OPERATORS = new Set([...Object.values(OPERATORS).flat(), ...USER_OPERATORS, ...BUSINESS_OPERATORS]);
const NO_VALUE_OPERATORS = new Set(['null', 'not-null', 'today', 'yesterday', 'tomorrow', 'this-week', 'this-month',
  'this-year', 'last-week', 'last-month', 'last-year', 'next-week', 'next-month', 'next-year', ...USER_OPERATORS,
  ...BUSINESS_OPERATORS]);
const MULTI_VALUE_OPERATORS = new Set(['in', 'not-in']);
/** `like` / `not-like` are "contains" / "does not contain": the value is shown without, and written with, `%…%`. */
const CONTAINS_OPERATORS = new Set(['like', 'not-like']);

/** The operators of E5 for a column kind; `target` is a lookup's target table(s), comma separated. */
export function operatorsFor(kind: ColumnKind, target?: string, attribute?: string): string[] {
  const list = [...OPERATORS[kind]];
  if (kind !== 'lookup') return list;
  const targets = (target ?? '').split(',').map((t) => t.trim());
  if (targets.includes('systemuser')) list.push(...USER_OPERATORS);
  if (targets.includes('businessunit') || attribute === 'owningbusinessunit') list.push(...BUSINESS_OPERATORS);
  return list;
}

export function noValueOperator(op: string): boolean {
  return NO_VALUE_OPERATORS.has(op);
}

let sequence = 0;
/** A fresh key for a node the user adds; keys read from FetchXML are paths (`f0.1`) and never collide with these. */
export function newKey(): string {
  sequence += 1;
  return `n${sequence}`;
}

function serialize(node: Node): string {
  return new XMLSerializer().serializeToString(node);
}

function insideLinkEntity(filter: Element, entity: Element): boolean {
  for (let p = filter.parentElement; p && p !== entity; p = p.parentElement) {
    if (p.tagName === 'link-entity') return true;
  }
  return false;
}

/** Reads one `<condition>`; undefined when the editor cannot show it (E5). */
export function readCondition(c: Element, key: string): FilterCondition | undefined {
  const attribute = c.getAttribute('attribute');
  const operator = c.getAttribute('operator') ?? '';
  if (!attribute || c.hasAttribute('entityname') || c.hasAttribute('valueof') || !EDITABLE_OPERATORS.has(operator)) {
    return undefined;
  }
  const children = Array.from(c.children);
  const value = c.getAttribute('value');
  let values: string[];
  if (MULTI_VALUE_OPERATORS.has(operator)) {
    if (value !== null || children.some((v) => v.tagName !== 'value')) return undefined;
    values = children.map((v) => v.textContent ?? '');
  } else {
    if (children.length > 0) return undefined;
    if (noValueOperator(operator)) values = [];
    else if (CONTAINS_OPERATORS.has(operator)) {
      const m = /^%([\s\S]*)%$/.exec(value ?? '');
      if (!m) return undefined;
      values = [m[1]];
    } else values = value === null ? [] : [value];
  }
  return { kind: 'condition', attribute, operator, values, key };
}

/**
 * Reads a `<filter>` of `entity`. A filter inside a `<link-entity>`, or one directly holding something the editor does
 * not show (a condition with `entityname`, `valueof` or another operator, an unknown element, a type other than
 * and/or) is opaque; a nested filter that is opaque does not make its parent opaque. `key` is the node's path key.
 */
export function readFilter(filter: Element, entity: Element, key = 'f'): FilterNode {
  const opaque = (): FilterOpaque => ({ kind: 'opaque', xml: serialize(filter), key });
  if (insideLinkEntity(filter, entity)) return opaque();
  const type = filter.getAttribute('type') ?? 'and';
  if (type !== 'and' && type !== 'or') return opaque();
  const children: FilterNode[] = [];
  for (const [i, child] of Array.from(filter.children).entries()) {
    const childKey = `${key}.${i}`;
    if (child.tagName === 'filter') children.push(readFilter(child, entity, childKey));
    else if (child.tagName === 'condition') {
      const condition = readCondition(child, childKey);
      if (!condition) return opaque();
      children.push(condition);
    } else return opaque();
  }
  return { kind: 'group', type, children, key };
}

function sameCondition(a: FilterCondition, b: FilterCondition): boolean {
  return a.attribute === b.attribute && a.operator === b.operator
    && a.values.length === b.values.length && a.values.every((v, i) => v === b.values[i]);
}

/** Elements to start from, by node key: unknown attributes of a filter or condition the user edited survive. */
export type FilterSources = ReadonlyMap<string, Element>;

function writeCondition(node: FilterCondition, doc: Document, base: Element | undefined): Element {
  const original = base?.tagName === 'condition' ? readCondition(base, node.key) : undefined;
  if (base && original && sameCondition(original, node)) return doc.importNode(base, true);
  const c = base && original ? (doc.importNode(base, false) as Element) : doc.createElement('condition');
  if (original && !sameCondition({ ...original, operator: node.operator }, node)) {
    // The display hints of a lookup value describe the old value.
    for (const hint of ['uiname', 'uitype', 'uitypename']) c.removeAttribute(hint);
  }
  c.setAttribute('attribute', node.attribute);
  c.setAttribute('operator', node.operator);
  if (MULTI_VALUE_OPERATORS.has(node.operator) || noValueOperator(node.operator)) c.removeAttribute('value');
  if (MULTI_VALUE_OPERATORS.has(node.operator)) {
    for (const v of node.values) {
      const value = doc.createElement('value');
      value.textContent = v;
      c.appendChild(value);
    }
  } else if (!noValueOperator(node.operator)) {
    const v = node.values[0] ?? '';
    c.setAttribute('value', CONTAINS_OPERATORS.has(node.operator) ? `%${v}%` : v);
  }
  return c;
}

/** The element of a node: opaque parts byte for byte, groups and conditions from their source element when known. */
export function writeNode(node: FilterNode, doc: Document, sources?: FilterSources): Element {
  if (node.kind === 'opaque') {
    return doc.importNode(new DOMParser().parseFromString(node.xml, 'application/xml').documentElement, true);
  }
  const base = sources?.get(node.key);
  if (node.kind === 'condition') return writeCondition(node, doc, base);
  return writeFilter(node, doc, sources);
}

/** Writes a group as a `<filter type>`; with `sources`, a known group keeps its unknown attributes. */
export function writeFilter(node: FilterGroup, doc: Document, sources?: FilterSources): Element {
  const base = sources?.get(node.key);
  const f = base?.tagName === 'filter' ? (doc.importNode(base, false) as Element) : doc.createElement('filter');
  // A source `<filter>` without `type` is an `and`: it stays without one.
  if (!base || f.hasAttribute('type') || node.type !== 'and') f.setAttribute('type', node.type);
  for (const child of node.children) f.appendChild(writeNode(child, doc, sources));
  return f;
}

/** Every filter and condition element under `filter`, by the key `readFilter` gives it. */
export function indexFilter(filter: Element, key: string, into: Map<string, Element>): Map<string, Element> {
  into.set(key, filter);
  for (const [i, child] of Array.from(filter.children).entries()) {
    if (child.tagName === 'filter') indexFilter(child, `${key}.${i}`, into);
    else into.set(`${key}.${i}`, child);
  }
  return into;
}
