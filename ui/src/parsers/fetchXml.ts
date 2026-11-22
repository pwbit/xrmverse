import type { ViewModel } from '../model/view';
import { childElements, parseXml } from './xml';

export interface ParsedFetch {
  entity: string;
  order: ViewModel['order'];
  count?: number;
  linkAliases: string[];
}

export function parseFetch(xml: string): ParsedFetch {
  const fetch = parseXml(xml).documentElement;
  const entity = childElements(fetch, 'entity')[0];
  if (fetch.tagName !== 'fetch' || !entity) throw new Error('Not a FetchXML document.');
  const orderOf = (o: Element, linkAlias?: string) => {
    const item: ViewModel['order'][number] = {
      attribute: o.getAttribute('attribute') ?? '',
      descending: o.getAttribute('descending') === 'true',
    };
    const alias = o.getAttribute('alias');
    if (alias) item.alias = alias;
    if (linkAlias) item.linkAlias = linkAlias;
    return item;
  };
  // Root orders, then the orders of each aliased link-entity in declaration order, as the server applies them.
  const links = Array.from(entity.getElementsByTagName('link-entity'));
  const order = [
    ...childElements(entity, 'order').map((o) => orderOf(o)),
    ...links.flatMap((l) => {
      const alias = l.getAttribute('alias');
      return alias ? childElements(l, 'order').map((o) => orderOf(o, alias)) : [];
    }),
  ];
  const count = Number.parseInt(fetch.getAttribute('count') ?? '', 10);
  return {
    entity: entity.getAttribute('name') ?? '',
    order,
    count: Number.isFinite(count) ? count : undefined,
    linkAliases: links
      .map((l) => l.getAttribute('alias'))
      .filter((a): a is string => !!a),
  };
}

function serialize(doc: Document): string {
  return new XMLSerializer().serializeToString(doc);
}

/** `*` is the quick find wildcard (`%`); a trailing one replaces the implicit trailing `%`. */
function quickFindPattern(text: string): string {
  const t = text.trim();
  return t.replace(/\*/g, '%') + (t.endsWith('*') ? '' : '%');
}

const PLACEHOLDER = /\{(\d+)\}/;
const NUMBER = /^[+-]?(\d+(\.\d*)?|\.\d+)$/;
/** Operators that take the text form of the search text. */
const WILDCARD_OPERATORS = new Set(['like', 'not-like']);
const PREFIX_OPERATORS = new Set(['begins-with', 'not-begin-with']);
/** Operators that take the numeric form. */
const NUMERIC_OPERATORS = new Set(['eq', 'ne', 'gt', 'ge', 'lt', 'le']);

/**
 * Fills the `{n}` placeholders of the conditions in `<filter isquickfindfields="1">`. The rule is
 * chosen per condition by its operator, whatever the placeholder index:
 * - `like` / `not-like`: the wildcard text (`*` -> `%`, trailing `%` unless the text ends with `*`);
 * - `begins-with` / `not-begin-with`: the trimmed text, no wildcard;
 * - `eq`, `ne`, `gt`, `ge`, `lt`, `le`: the number, only when the text is a number (invariant,
 *   `.` decimal), with no `%`; otherwise the condition is removed (an emptied filter goes too);
 * - any other operator holding a placeholder: an explicit error naming it.
 * Values are set as DOM attributes, so `' < & "` are escaped by the serializer. Blank text removes
 * the quick find filters.
 */
export function applyQuickFind(fetchXml: string, text: string): string {
  const doc = parseXml(fetchXml);
  const filters = Array.from(doc.getElementsByTagName('filter')).filter(
    (f) => f.getAttribute('isquickfindfields') === '1',
  );
  if (filters.length === 0) return fetchXml;
  const trimmed = text.trim();
  for (const filter of filters) {
    if (trimmed === '') {
      filter.parentNode?.removeChild(filter);
      continue;
    }
    for (const cond of childElements(filter, 'condition')) {
      const value = cond.getAttribute('value');
      const match = value === null ? null : PLACEHOLDER.exec(value);
      if (value === null || !match) continue;
      const operator = cond.getAttribute('operator') ?? '';
      let fill: string | undefined;
      if (WILDCARD_OPERATORS.has(operator)) fill = quickFindPattern(trimmed);
      else if (PREFIX_OPERATORS.has(operator)) fill = trimmed;
      else if (NUMERIC_OPERATORS.has(operator)) {
        if (!NUMBER.test(trimmed)) {
          filter.removeChild(cond);
          continue;
        }
        fill = String(Number(trimmed));
      } else {
        throw new Error(`Quick find placeholder ${match[0]} has no rule for operator "${operator}".`);
      }
      cond.setAttribute('value', value.replace(new RegExp(PLACEHOLDER.source, 'g'), () => fill));
    }
    if (childElements(filter, 'condition').length === 0) filter.parentNode?.removeChild(filter);
  }
  return serialize(doc);
}

export function withPaging(fetchXml: string, page: number, count: number, cookie?: string): string {
  const doc = parseXml(fetchXml);
  const fetch = doc.documentElement;
  fetch.setAttribute('page', String(page));
  fetch.setAttribute('count', String(count));
  if (cookie) fetch.setAttribute('paging-cookie', cookie);
  else fetch.removeAttribute('paging-cookie');
  return serialize(doc);
}

/**
 * Replaces every `<order>` (of the root entity and of each link-entity) with one order: on a root column, or inside the
 * `<link-entity>` whose alias is `linkAlias` (#100).
 */
export function withOrder(fetchXml: string, attribute: string, descending: boolean, linkAlias?: string): string {
  const doc = parseXml(fetchXml);
  for (const order of Array.from(doc.getElementsByTagName('order'))) order.parentNode?.removeChild(order);
  const root = childElements(doc.documentElement, 'entity')[0];
  if (!root) throw new Error('Not a FetchXML document.');
  const entity = linkAlias
    ? Array.from(root.getElementsByTagName('link-entity')).find((l) => l.getAttribute('alias') === linkAlias)
    : root;
  if (!entity) throw new Error(`The view has no link-entity with alias '${linkAlias}'.`);
  const order = doc.createElement('order');
  order.setAttribute('attribute', attribute);
  order.setAttribute('descending', String(descending));
  entity.appendChild(order);
  return serialize(doc);
}

/** The query without its paging and count (`page`, `count`, `paging-cookie`, `returntotalrecordcount`): what a view stores. */
export function withoutPaging(fetchXml: string): string {
  const doc = parseXml(fetchXml);
  for (const name of ['page', 'count', 'paging-cookie', 'returntotalrecordcount']) doc.documentElement.removeAttribute(name);
  return serialize(doc);
}

/** Asks for the total record count (`returntotalrecordcount="true"`). */
export function withTotalCount(fetchXml: string): string {
  const doc = parseXml(fetchXml);
  doc.documentElement.setAttribute('returntotalrecordcount', 'true');
  return serialize(doc);
}

/** The table of each aliased `<link-entity>`, nested ones included, by alias. */
export function linkEntities(fetchXml: string): Record<string, string> {
  const links: Record<string, string> = {};
  for (const link of Array.from(parseXml(fetchXml).getElementsByTagName('link-entity'))) {
    const alias = link.getAttribute('alias');
    const name = link.getAttribute('name');
    if (alias && name) links[alias] = name;
  }
  return links;
}

/** Adds a root `<attribute name>` for each name the root entity does not already select (no-op with `<all-attributes/>`). */
export function withAttributes(fetchXml: string, names: string[]): string {
  const doc = parseXml(fetchXml);
  const root = childElements(doc.documentElement, 'entity')[0];
  if (!root) throw new Error('Not a FetchXML document.');
  if (childElements(root, 'all-attributes').length > 0) return fetchXml;
  const present = new Set(childElements(root, 'attribute').map((a) => a.getAttribute('name')));
  for (const name of names) {
    if (present.has(name)) continue;
    present.add(name);
    const attribute = doc.createElement('attribute');
    attribute.setAttribute('name', name);
    root.appendChild(attribute);
  }
  return serialize(doc);
}

export interface DateRangeOptions {
  /** The column is a DateOnly one: `on-or-after`/`on-or-before` with `yyyy-mm-dd` values. Else `ge`/`le` with ISO UTC values. */
  dateOnly?: boolean;
  /** An end column: `attribute le to` AND (`end ge from` OR (`end null` AND `attribute ge from`)), so a record that starts before the range still matches. */
  end?: string;
}

/** Adds a root `<filter type="and">` restricting `attribute` to `from`..`to`. */
export function withDateRange(fetchXml: string, attribute: string, from: string, to: string, opts: DateRangeOptions = {}): string {
  const doc = parseXml(fetchXml);
  const root = childElements(doc.documentElement, 'entity')[0];
  if (!root) throw new Error('Not a FetchXML document.');
  const [after, before] = opts.dateOnly ? ['on-or-after', 'on-or-before'] : ['ge', 'le'];
  const condition = (name: string, operator: string, value?: string) => {
    const c = doc.createElement('condition');
    c.setAttribute('attribute', name);
    c.setAttribute('operator', operator);
    if (value !== undefined) c.setAttribute('value', value);
    return c;
  };
  const filter = (type: string, ...children: Element[]) => {
    const f = doc.createElement('filter');
    f.setAttribute('type', type);
    for (const child of children) f.appendChild(child);
    return f;
  };
  const range = filter('and', condition(attribute, before, to));
  if (opts.end) {
    range.appendChild(
      filter(
        'or',
        condition(opts.end, after, from),
        filter('and', condition(opts.end, 'null'), condition(attribute, after, from)),
      ),
    );
  } else {
    range.insertBefore(condition(attribute, after, from), range.firstChild);
  }
  root.appendChild(range);
  return serialize(doc);
}

/** Adds a root `<filter type="and">` holding one `null` condition on `attribute`: the records without a value. */
export function withNullCondition(fetchXml: string, attribute: string): string {
  const doc = parseXml(fetchXml);
  const root = childElements(doc.documentElement, 'entity')[0];
  if (!root) throw new Error('Not a FetchXML document.');
  const filter = doc.createElement('filter');
  filter.setAttribute('type', 'and');
  const condition = doc.createElement('condition');
  condition.setAttribute('attribute', attribute);
  condition.setAttribute('operator', 'null');
  filter.appendChild(condition);
  root.appendChild(filter);
  return serialize(doc);
}
