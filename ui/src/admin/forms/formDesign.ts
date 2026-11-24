import { CLASS_ID, controlKind } from '../../parsers/controlClass';
import { childElements, parseXml } from '../../parsers/xml';
import { newGuid } from './formIds';
import { allCells, bodyCells, bodySections, clampSpan, lower } from './formCells';
import { uiLocale } from '../../shell/i18n/t';
import type { AttributeMeta, AttrKind } from '../tables/metadata';
import { readControl, writeControl, type ControlSpec } from './formControls';
import { detach, indentStep, indentTree, ownIndent, reindentRows } from './xmlIndent';
import {
  dropEmptied, eventsJson, isEdited, readEvents, writeEvents, type DesignEvent, type DesignLibrary,
} from './formEvents';
import { pcfBindings, writePcfs, type PcfBinding, type WrittenCell } from './formPcf';

/** The columns of a `systemform` row that the designer reads. */
export interface FormRow {
  systemformid?: string;
  name: string;
  description?: string | null;
  objecttypecode: string;
  type: number;
  isdefault?: boolean | null;
  formactivationstate?: number | null;
  formxml: string | null;
  ismanaged?: boolean | null;
}

export type CellKind = 'field' | 'subgrid' | 'quickview' | 'webresource' | 'timeline' | 'custom' | 'unknown' | 'empty';

export interface DesignCell {
  key: string;
  id: string;
  kind: CellKind;
  /** The column of a field cell. */
  attribute?: string;
  labels: Record<number, string>;
  showLabel: boolean;
  visible: boolean;
  disabled: boolean;
  colSpan: number;
  rowSpan: number;
  /** Only on a field cell the designer created: the class id its control is written with. */
  classId?: string;
  /** The control of a subgrid, quick view or web resource cell (not an IFRAME); see `formControls.ts`. */
  control?: ControlSpec;
  /**
   * The control's `uniqueid`, as read (trimmed, when not empty), or given by the designer to a new quick view or a new
   * binding: kept in the design so that every save, and Save as, writes the same one.
   */
  uniqueId?: string;
  /** The custom (PCF) control that replaces the control of a field or subgrid cell (G5); see `formPcf.ts`. */
  pcf?: PcfBinding;
}

export interface DesignSection {
  key: string;
  name: string;
  id: string;
  labels: Record<number, string>;
  showLabel: boolean;
  visible: boolean;
  columns: 1 | 2 | 3 | 4;
  /** Where the section's field labels sit (`celllabelposition`): beside the field (Dataverse's default) or above it. */
  labelPosition?: 'left' | 'top';
  /** In reading order. A write keeps the source's row breaks and re-flows only the rows an edit touches ({@link keepRows}). */
  cells: DesignCell[];
}

export interface DesignColumn {
  key: string;
  width: string;
  sections: DesignSection[];
}

export interface DesignTab {
  key: string;
  name: string;
  id: string;
  labels: Record<number, string>;
  showLabel: boolean;
  visible: boolean;
  columns: DesignColumn[];
}

export interface FormDesign {
  table: string;
  name: string;
  description: string;
  type: number;
  isDefault: boolean;
  active: boolean;
  tabs: DesignTab[];
  header: DesignCell[];
  /** `<formLibraries>`, in load order (G6); see `formEvents.ts`. */
  libraries: DesignLibrary[];
  /** The OnLoad, OnSave and OnChange events the designer edits (G6); other events stay in `source` only. */
  events: DesignEvent[];
  /** The parsed `formxml`; never mutated: a write edits a copy, so what the designer does not model survives. */
  source: Document;
  /** The original `formxml`, given back as is when the design is unchanged. */
  text: string;
  /** The language whose labels the designer edits. */
  lcid: number;
}

export type TabLayout = '1' | '2-50' | '2-67' | '3';

/** The `systemform.type` values of the forms the designer edits (ruling F2). */
export const FORM_TYPE = { main: 2, quickView: 6, quickCreate: 7 } as const;

/** The most header field cells (ruling F8); other header cells don't count. */
export const HEADER_LIMIT = 4;

const FIELD_CLASS_IDS: Partial<Record<AttrKind, string>> = {
  string: CLASS_ID.text,
  memo: CLASS_ID.memo,
  int: CLASS_ID.integer,
  decimal: CLASS_ID.decimal,
  double: CLASS_ID.float,
  money: CLASS_ID.money,
  bool: CLASS_ID.twoOptions,
  datetime: CLASS_ID.dateTime,
  choice: CLASS_ID.choice,
  choices: CLASS_ID.multiSelect,
  lookup: CLASS_ID.lookup,
  partylist: CLASS_ID.partyList,
};

/**
 * Whether the designer can place the column as a field: its kind has a field class id. Task 5's pickers filter with it;
 * `bigint`, `file`, `image` and `unsupported` columns are left out.
 */
export function designable(attr: Pick<AttributeMeta, 'kind'>): boolean {
  return FIELD_CLASS_IDS[attr.kind] !== undefined;
}

/** The class id of a new field control for the column's kind (ruling F6); the text one for a kind without its own. */
export function classIdFor(attr: Pick<AttributeMeta, 'kind'>): string {
  return FIELD_CLASS_IDS[attr.kind] ?? CLASS_ID.text;
}

const LAYOUT_WIDTHS: Record<TabLayout, string[]> = {
  '1': ['100%'],
  '2-50': ['50%', '50%'],
  '2-67': ['67%', '33%'],
  '3': ['33%', '33%', '34%'],
};

/** The UI language LCID: 1040 for Italian, else 1033 (ruling F9). */
export function uiLcid(): 1033 | 1040 {
  return uiLocale() === 'it-IT' ? 1040 : 1033;
}

export { newGuid } from './formIds';

// ---------------------------------------------------------------------------------------------------------------------
// Reading

const serialize = (node: Node) => new XMLSerializer().serializeToString(node);

const flag = (el: Element, name: string, fallback: boolean): boolean => {
  const v = el.getAttribute(name)?.trim().toLowerCase();
  return v === undefined ? fallback : v === 'true' || v === '1';
};

/** The most rows a cell spans as read: a malformed huge `rowspan` must not make a write build that many padding rows. */
const MAX_SPAN = 100;

const count = (el: Element, name: string): number => {
  const n = Number(el.getAttribute(name));
  return Number.isInteger(n) && n > 0 ? Math.min(n, MAX_SPAN) : 1;
};

function labelsOf(el: Element): Record<number, string> {
  const out: Record<number, string> = {};
  for (const holder of childElements(el, 'labels')) {
    for (const l of childElements(holder, 'label')) {
      const raw = l.getAttribute('languagecode')?.trim() ?? '';
      const text = l.getAttribute('description');
      if (/^\d+$/.test(raw) && text !== null) out[Number(raw)] = text;
    }
  }
  return out;
}

/** `"1"`..`"4"` is a count; several digits (`"11"`, `"12"`) are one column each. Clamped to 1–4. */
function columnCount(value: string | null): number {
  const text = value?.trim() ?? '';
  const n = /^[1-9]{2,}$/.test(text) ? text.length : Number(text);
  return Number.isInteger(n) && n > 0 ? Math.min(n, 4) : 1;
}

const columnEls = (tab: Element) => childElements(tab, 'columns').flatMap((c) => childElements(c, 'column'));
const sectionEls = (column: Element) => childElements(column, 'sections').flatMap((s) => childElements(s, 'section'));
const rowEls = (parent: Element) => childElements(parent, 'rows').flatMap((r) => childElements(r, 'row'));
const cellEls = (parent: Element) => rowEls(parent).flatMap((row) => childElements(row, 'cell'));

interface Keyed { key: string; el: Element }
interface ScannedSection extends Keyed { cells: Keyed[] }
interface ScannedColumn extends Keyed { sections: ScannedSection[] }
interface ScannedTab extends Keyed { columns: ScannedColumn[] }
interface Scan {
  form: Element;
  tabsEl: Element;
  tabs: ScannedTab[];
  headerEl?: Element;
  header: Keyed[];
}

/**
 * The modeled elements of a `<form>` with their keys. Keys follow document order, so the same FormXML (or a copy of
 * its document) always gets the same keys: a write finds the elements of a design's nodes by key in its copy.
 */
function scan(form: Element): Scan {
  const tabsEl = childElements(form, 'tabs')[0];
  if (!tabsEl) throw new Error('The FormXML has no <tabs> element.');
  let s = 0;
  let c = 0;
  const tabs = childElements(tabsEl, 'tab').map((tab, i) => ({
    key: `t${i}`,
    el: tab,
    columns: columnEls(tab).map((col, j) => ({
      key: `t${i}.c${j}`,
      el: col,
      sections: sectionEls(col).map((sec) => ({
        key: `s${s++}`,
        el: sec,
        cells: cellEls(sec).map((cell) => ({ key: `c${c++}`, el: cell })),
      })),
    })),
  }));
  const headerEl = childElements(form, 'header')[0];
  const header = headerEl ? cellEls(headerEl).map((el, i) => ({ key: `h${i}`, el })) : [];
  return { form, tabsEl, tabs, headerEl, header };
}

/** The `uniqueid`s (lower case) of the controls a `<controlDescription>` replaces with a named custom control. */
function customControls(form: Element): Set<string> {
  const out = new Set<string>();
  for (const descs of childElements(form, 'controlDescriptions')) {
    for (const d of childElements(descs, 'controlDescription')) {
      const forControl = d.getAttribute('forControl')?.trim().toLowerCase();
      const named = childElements(d, 'customControl').some((cc) => (cc.getAttribute('name') ?? '').trim() !== '');
      if (forControl && named) out.add(forControl);
    }
  }
  return out;
}

function kindOf(control: Element | undefined, custom: Set<string>): CellKind {
  if (!control) return 'empty';
  const kind = controlKind(control.getAttribute('classid') ?? '');
  if (kind !== 'field' && kind !== 'unknown') return kind;
  if (control.getAttribute('datafieldname')) return 'field';
  const uniqueId = control.getAttribute('uniqueid')?.trim().toLowerCase();
  return uniqueId && custom.has(uniqueId) ? 'custom' : 'unknown';
}

function readCell({ key, el }: Keyed, custom: Set<string>, bindings: Map<string, PcfBinding>): DesignCell {
  const control = childElements(el, 'control')[0];
  const kind = kindOf(control, custom);
  const cell: DesignCell = {
    key,
    id: el.getAttribute('id') ?? '',
    kind,
    labels: labelsOf(el),
    showLabel: flag(el, 'showlabel', true),
    visible: flag(el, 'visible', true),
    disabled: control ? flag(control, 'disabled', false) : false,
    colSpan: count(el, 'colspan'),
    rowSpan: count(el, 'rowspan'),
  };
  if (kind === 'field') cell.attribute = control!.getAttribute('datafieldname') ?? '';
  if (kind === 'subgrid' || kind === 'quickview' || kind === 'webresource') {
    const spec = readControl(control!);
    if (spec) cell.control = spec;
  }
  const uniqueId = control?.getAttribute('uniqueid')?.trim();
  if (uniqueId) cell.uniqueId = uniqueId;
  if (kind === 'field' || kind === 'subgrid') {
    const binding = bindings.get(uniqueId?.toLowerCase() ?? '');
    if (binding) cell.pcf = binding;
  }
  return cell;
}

function readBody(sc: Scan): Pick<FormDesign, 'tabs' | 'header'> {
  const custom = customControls(sc.form);
  const bindings = pcfBindings(sc.form);
  return {
    tabs: sc.tabs.map((t) => ({
      key: t.key,
      name: t.el.getAttribute('name') ?? '',
      id: t.el.getAttribute('id') ?? '',
      labels: labelsOf(t.el),
      showLabel: flag(t.el, 'showlabel', true),
      visible: flag(t.el, 'visible', true),
      columns: t.columns.map((c) => ({
        key: c.key,
        width: c.el.getAttribute('width') ?? '',
        sections: c.sections.map((s) => ({
          key: s.key,
          name: s.el.getAttribute('name') ?? '',
          id: s.el.getAttribute('id') ?? '',
          labels: labelsOf(s.el),
          showLabel: flag(s.el, 'showlabel', true),
          visible: flag(s.el, 'visible', true),
          columns: columnCount(s.el.getAttribute('columns')) as DesignSection['columns'],
          labelPosition: s.el.getAttribute('celllabelposition')?.toLowerCase() === 'top' ? 'top' : 'left',
          cells: s.cells.map((cell) => readCell(cell, custom, bindings)),
        })),
      })),
    })),
    header: sc.header.map((cell) => readCell(cell, custom, bindings)),
  };
}

function parseForm(text: string | null): Document {
  if (!text?.trim()) throw new Error('The form has no FormXML.');
  let doc: Document;
  try {
    doc = parseXml(text);
  } catch (e) {
    throw new Error(`The FormXML can't be read: ${(e as Error).message}`);
  }
  if (doc.documentElement.tagName !== 'form') throw new Error('The FormXML has no <form> root element.');
  return doc;
}

/** The design of a `systemform` row. Throws an `Error` with a readable message when its FormXML can't be read. */
export function readForm(row: FormRow, lcid: number): FormDesign {
  const source = parseForm(row.formxml);
  return {
    table: row.objecttypecode,
    name: row.name,
    description: row.description ?? '',
    type: row.type,
    isDefault: row.isdefault === true,
    active: row.formactivationstate !== 0,
    ...readBody(scan(source.documentElement)),
    ...readEvents(source.documentElement),
    source,
    text: row.formxml!,
    lcid,
  };
}

// ---------------------------------------------------------------------------------------------------------------------
// Writing

/** The body as compared for changes; a subgrid's `filter` (X.11) is never written to the FormXML, so it is left out. */
const bodyJson = (d: Pick<FormDesign, 'tabs' | 'header'>) =>
  JSON.stringify([d.tabs, d.header], function (this: { kind?: unknown }, key, value: unknown) {
    return key === 'filter' && this.kind === 'subgrid' ? undefined : value;
  });

/** Sets `name` only when the value it reads as differs, so an untouched element serializes as it was. */
function setFlag(el: Element, name: string, value: boolean, fallback: boolean) {
  if (flag(el, name, fallback) !== value) el.setAttribute(name, String(value));
}

function setCount(el: Element, name: string, value: number) {
  if (count(el, name) !== value) el.setAttribute(name, String(value));
}

/**
 * Writes or replaces the `<label>` of each language of `labels`; removes the labels of languages it does not hold. Every
 * `<label>` of a language is updated, so a duplicate can't win over the edit when read back (`labelsOf` keeps the last).
 */
function setLabels(el: Element, labels: Record<number, string>) {
  if (JSON.stringify(labelsOf(el)) === JSON.stringify(labels)) return;
  const doc = el.ownerDocument;
  const holders = childElements(el, 'labels');
  let holder = holders[0];
  if (!holder) {
    holder = doc.createElement('labels');
    el.insertBefore(holder, el.firstChild);
  }
  const all = holders.flatMap((h) => childElements(h, 'label'));
  const lcidOf = (l: Element) => {
    const raw = l.getAttribute('languagecode')?.trim() ?? '';
    return /^\d+$/.test(raw) ? Number(raw) : undefined;
  };
  for (const l of all) {
    const lcid = lcidOf(l);
    // Only a label `labelsOf` reads (it has a description) can have been taken out of the design.
    if (lcid !== undefined && labels[lcid] === undefined && l.hasAttribute('description')) l.parentNode?.removeChild(l);
  }
  for (const [lcid, text] of Object.entries(labels)) {
    const found = all.filter((l) => lcidOf(l) === Number(lcid) && l.parentNode);
    if (found.length > 0) {
      for (const l of found) if (l.getAttribute('description') !== text) l.setAttribute('description', text);
      continue;
    }
    const label = doc.createElement('label');
    label.setAttribute('description', text);
    label.setAttribute('languagecode', lcid);
    holder.appendChild(label);
  }
}

/** The first `<tag>` child of `parent`, created (appended) when there is none. */
function container(parent: Element, tag: string): Element {
  const found = childElements(parent, tag)[0];
  if (found) return found;
  const el = parent.ownerDocument.createElement(tag);
  parent.appendChild(el);
  return el;
}

/**
 * Makes `wanted` the elements `current` held, in order, in `target`. Nothing moves when they are already the same. An
 * element of `wanted` may come from elsewhere in the document; an element of `current` not in `wanted` is dropped.
 */
function place(target: Element, current: Element[], wanted: Element[]) {
  if (current.length === wanted.length && current.every((e, i) => e === wanted[i])) return;
  const marker = target.ownerDocument.createComment('');
  target.insertBefore(marker, current.find((e) => e.parentNode === target) ?? null);
  for (const e of current) e.parentNode?.removeChild(e);
  for (const e of wanted) target.insertBefore(e, marker);
  target.removeChild(marker);
}

function element(doc: Document, tag: string, attributes: Record<string, string>): Element {
  const el = doc.createElement(tag);
  for (const [name, value] of Object.entries(attributes)) el.setAttribute(name, value);
  return el;
}

/**
 * Splits `cells` into rows of `width` slots, in reading order, as a form grid lays them out: a cell takes the first run of
 * `colSpan` free slots after the previous cell of its row, else it starts the next row. A cell with `rowSpan` n keeps its
 * slots busy in the n - 1 rows below it; a row whose slots are all busy, and the rows still covered after the last cell,
 * come back empty (FormXML writes them as `<row/>` padding). A `colSpan` wider than a row counts as the row. With no
 * `rowSpan` above 1 a row closes only when the next cell does not fit after the cells already in it.
 *
 * Adding a cell at the end of `cells` therefore puts it in the first free slot after the last cell of the last row, or
 * in a new row.
 *
 * A `fresh` cell (one with no source row, such as a new subgrid) gets no padding rows after the last cell.
 */
export function flowCells<T extends { colSpan: number; rowSpan: number }>(
  cells: T[], width: number, fresh: (cell: T) => boolean = () => false,
): T[][] {
  const rows: T[][] = [];
  /** Per slot: the rows, from the current one, that a tall cell still covers. */
  const busy: number[] = Array.from({ length: width }, () => 0);
  /** As `busy`, but only for the cells that are not fresh: only they get padding rows after the last cell. */
  const padded: number[] = Array.from({ length: width }, () => 0);
  let row: T[] = [];
  let cursor = 0;
  const close = () => {
    rows.push(row);
    row = [];
    cursor = 0;
    for (let i = 0; i < width; i += 1) {
      busy[i] = Math.max(busy[i] - 1, 0);
      padded[i] = Math.max(padded[i] - 1, 0);
    }
  };
  const freeRun = (span: number): number => {
    for (let start = cursor; start + span <= width; start += 1) {
      if (busy.slice(start, start + span).every((b) => b === 0)) return start;
    }
    return -1;
  };
  for (const cell of cells) {
    const span = Math.min(Math.max(cell.colSpan, 1), width);
    let start = freeRun(span);
    while (start < 0) {
      close();
      start = freeRun(span);
    }
    row.push(cell);
    for (let i = start; i < start + span; i += 1) {
      busy[i] = Math.max(cell.rowSpan, 1);
      padded[i] = fresh(cell) ? 0 : busy[i];
    }
    cursor = start + span;
  }
  if (row.length > 0) close();
  while (padded.some((b) => b > 0)) close();
  return rows;
}

/** The longest run of `values` that increases, as indexes into `values`. */
function increasing(values: number[]): Set<number> {
  const ends: number[] = [];
  const prev: number[] = [];
  values.forEach((v, i) => {
    let lo = 0;
    let hi = ends.length;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (values[ends[mid]] < v) lo = mid + 1;
      else hi = mid;
    }
    prev[i] = lo > 0 ? ends[lo - 1] : -1;
    ends[lo] = i;
  });
  const out = new Set<number>();
  for (let i = ends.length > 0 ? ends[ends.length - 1] : -1; i >= 0; i = prev[i]) out.add(i);
  return out;
}

/** A row as written: its cells, and the index of the source row it comes from (its `<row>` element is re-used). */
export interface KeptRow<T> { cells: T[]; from?: number }

/**
 * Lays `cells` (reading order) out in rows of `width` slots keeping the row breaks of `before` (the source rows, with
 * their empty padding rows), so an edit re-flows only the rows it touches (ruling I1):
 *
 * - when the cells are the source's, reordered with each position keeping its spans (e.g. a swap), each cell takes the
 *   row of the cell that was at its position;
 * - else the cells still in the source order keep their rows; a cell gone leaves its row (the later cells shift left, a
 *   row left empty goes); any other cell (added, moved in or moved) goes right after the cell before it in `cells`, or
 *   at the start of the first row;
 * - then a row whose cells no longer fit, beside the slots the tall cells above keep busy, breaks before the first cell
 *   that does not fit, into a new row right after it. Padding rows are added while a tall cell still covers slots.
 */
export function keepRows<T extends { key: string; colSpan: number; rowSpan: number }>(
  before: T[][], cells: T[], width: number,
): KeptRow<T>[] {
  const flat = before.flat();
  const rowOf = new Map<string, number>();
  before.forEach((r, i) => r.forEach((c) => rowOf.set(c.key, i)));
  let rows: KeptRow<T>[];
  const sameShape = flat.length === cells.length && cells.every((c) => rowOf.has(c.key))
    && flat.every((c, i) => c.colSpan === cells[i].colSpan && c.rowSpan === cells[i].rowSpan);
  if (sameShape) {
    rows = before.map((_, i) => ({ cells: [], from: i }));
    flat.forEach((c, i) => rows[rowOf.get(c.key)!].cells.push(cells[i]));
  } else {
    const order = new Map(flat.map((c, i) => [c.key, i]));
    const known = cells.map((c, i) => [i, order.get(c.key)] as const)
      .filter((x): x is readonly [number, number] => x[1] !== undefined);
    const anchored = new Set([...increasing(known.map(([, o]) => o))].map((k) => known[k][0]));
    rows = before.map((_, i) => ({ cells: [], from: i }));
    // The cells still in source order keep their rows; the others go right after the cell before them.
    const placed = new Map<string, KeptRow<T>>();
    cells.forEach((c, i) => {
      if (!anchored.has(i)) return;
      const row = rows[rowOf.get(c.key)!];
      row.cells.push(c);
      placed.set(c.key, row);
    });
    cells.forEach((c, i) => {
      if (anchored.has(i)) return;
      const previous = i > 0 ? placed.get(cells[i - 1].key) : undefined;
      let row = previous;
      // A new tall cell after every source cell (a new subgrid) starts a new row after all the source rows, padding included.
      if (!rowOf.has(c.key) && c.rowSpan > 1 && cells.slice(i).every((x) => !rowOf.has(x.key))) {
        row = { cells: [c] };
        rows.push(row);
      } else if (!row) {
        row = rows[0] ?? { cells: [] };
        if (rows.length === 0) rows.push(row);
        row.cells.unshift(c);
      } else {
        row.cells.splice(row.cells.indexOf(cells[i - 1]) + 1, 0, c);
      }
      placed.set(c.key, row);
    });
    rows = rows.filter((r) => r.cells.length > 0 || r.from === undefined || before[r.from].length === 0);
  }
  // Breaks the rows that no longer fit, and pads under the tall cells that have a source row: a cell new to the section
  // (a new subgrid) gets no padding rows after it, as Dataverse writes it.
  const out: KeptRow<T>[] = [];
  const busy: number[] = Array.from({ length: width }, () => 0);
  const padded: number[] = Array.from({ length: width }, () => 0);
  const close = (row: KeptRow<T>) => {
    out.push(row);
    for (let i = 0; i < width; i += 1) {
      busy[i] = Math.max(busy[i] - 1, 0);
      padded[i] = Math.max(padded[i] - 1, 0);
    }
  };
  for (const source of rows) {
    let row: KeptRow<T> = { cells: [], from: source.from };
    let cursor = 0;
    for (const c of source.cells) {
      const span = Math.min(Math.max(c.colSpan, 1), width);
      const free = (): number => {
        for (let start = cursor; start + span <= width; start += 1) {
          if (busy.slice(start, start + span).every((b) => b === 0)) return start;
        }
        return -1;
      };
      let start = free();
      while (start < 0) {
        close(row);
        row = { cells: [] };
        cursor = 0;
        start = free();
      }
      row.cells.push(c);
      for (let i = start; i < start + span; i += 1) {
        busy[i] = Math.max(c.rowSpan, 1);
        padded[i] = rowOf.has(c.key) ? busy[i] : 0;
      }
      cursor = start + span;
    }
    close(row);
  }
  while (padded.some((b) => b > 0)) close({ cells: [] });
  return out;
}

/** The cells of `cells` (the design of `parent`'s cells, in document order) grouped by the `<row>` that holds them. */
function rowsIn(parent: Element, cells: DesignCell[]): DesignCell[][] {
  let i = 0;
  return rowEls(parent).map((row) => childElements(row, 'cell').map(() => cells[i++]));
}

/** Per section key: the source's column count and rows. */
type SourceRows = Map<string, { columns: number; rows: DesignCell[][] }>;

function sourceRows(sc: Scan, body: Pick<FormDesign, 'tabs'>): SourceRows {
  const scanned = sc.tabs.flatMap((t) => t.columns.flatMap((c) => c.sections));
  return new Map(bodySections(body.tabs).map((s, i) => [s.key, { columns: s.columns, rows: rowsIn(scanned[i].el, s.cells) }]));
}

/**
 * The rows `s` is written with: the source's rows with only the edited ones re-flowed ({@link keepRows}), or a full
 * re-flow ({@link flowCells}) for a new section or a new column count. `from` is the source row a row re-uses.
 */
function layout(source: SourceRows, s: DesignSection): KeptRow<DesignCell>[] {
  const before = source.get(s.key);
  if (before && before.columns === s.columns) return keepRows(before.rows, s.cells, s.columns);
  const known = new Set(before?.rows.flat().map((c) => c.key));
  return flowCells(s.cells, s.columns, (c) => !known.has(c.key)).map((cells, i) => ({ cells, from: i }));
}

/** The rows each section of `d` is written with (by section key), as {@link writeForm} lays them out; the canvas shows them. */
export function sectionRows(d: FormDesign): Map<string, DesignCell[][]> {
  const sc = scan(d.source.documentElement);
  const source = sourceRows(sc, readBody(sc));
  return new Map(bodySections(d.tabs).map((s) => [s.key, layout(source, s).map((r) => r.cells)]));
}

class Writer {
  readonly doc: Document;
  readonly sc: Scan;
  readonly index = new Map<string, Element>();
  readonly original = new Map<string, DesignSection>();
  readonly originalHeader: DesignCell[];
  readonly originalRows: SourceRows;
  readonly originalHeaderRows?: DesignCell[][];
  /** The source's cells as read, by key. */
  readonly sourceCells = new Map<string, DesignCell>();
  /** The `uniqueid`s (lower case) of the source's controls. */
  readonly sourceIds: Set<string>;
  /** Every cell written, for the bindings ({@link writePcfs}). */
  readonly written: WrittenCell[] = [];
  /** The `<cell>` elements the designer created, indented where {@link rows} places them (C3). */
  readonly created = new Set<Element>();

  constructor(source: Document) {
    this.doc = source.cloneNode(true) as Document;
    this.sc = scan(this.doc.documentElement);
    for (const t of this.sc.tabs) {
      this.index.set(t.key, t.el);
      for (const c of t.columns) {
        this.index.set(c.key, c.el);
        for (const s of c.sections) {
          this.index.set(s.key, s.el);
          for (const cell of s.cells) this.index.set(cell.key, cell.el);
        }
      }
    }
    for (const cell of this.sc.header) this.index.set(cell.key, cell.el);
    const body = readBody(this.sc);
    for (const s of bodySections(body.tabs)) this.original.set(s.key, s);
    this.originalHeader = body.header;
    for (const c of allCells(body)) this.sourceCells.set(c.key, c);
    this.sourceIds = new Set(Array.from(this.doc.getElementsByTagName('control'))
      .map((c) => c.getAttribute('uniqueid')?.trim().toLowerCase() ?? '').filter((id) => id !== ''));
    // The source rows, read before any write moves a cell elsewhere.
    this.originalRows = sourceRows(this.sc, body);
    this.originalHeaderRows = this.sc.headerEl ? rowsIn(this.sc.headerEl, body.header) : undefined;
  }

  tab(t: DesignTab): Element {
    let el = this.index.get(t.key);
    if (!el) {
      el = element(this.doc, 'tab', { name: t.name, id: t.id, expanded: 'true', showlabel: String(t.showLabel), verticallayout: 'true' });
      el.appendChild(this.doc.createElement('labels'));
    }
    setFlag(el, 'showlabel', t.showLabel, true);
    setFlag(el, 'visible', t.visible, true);
    setLabels(el, t.labels);
    const wanted = t.columns.map((c) => this.column(c));
    place(container(el, 'columns'), columnEls(el), wanted);
    return el;
  }

  column(c: DesignColumn): Element {
    const el = this.index.get(c.key) ?? element(this.doc, 'column', { width: c.width });
    if ((el.getAttribute('width') ?? '') !== c.width) el.setAttribute('width', c.width);
    const wanted = c.sections.map((s) => this.section(s));
    place(container(el, 'sections'), sectionEls(el), wanted);
    return el;
  }

  section(s: DesignSection): Element {
    let el = this.index.get(s.key);
    if (!el) {
      el = element(this.doc, 'section', {
        name: s.name, id: s.id, showlabel: String(s.showLabel), showbar: 'false', columns: '1'.repeat(s.columns),
      });
      el.appendChild(this.doc.createElement('labels'));
      el.appendChild(this.doc.createElement('rows'));
    }
    setFlag(el, 'showlabel', s.showLabel, true);
    setFlag(el, 'visible', s.visible, true);
    // Left is the default: an absent attribute stays absent, one the source had is kept in step.
    if (s.labelPosition === 'top') el.setAttribute('celllabelposition', 'Top');
    else if (el.hasAttribute('celllabelposition')) el.setAttribute('celllabelposition', 'Left');
    setLabels(el, s.labels);
    const cells = s.cells.map((c) => this.cell(c, false));
    const before = this.original.get(s.key);
    const same = before !== undefined && before.columns === s.columns && before.cells.length === s.cells.length
      && before.cells.every((c, i) => c.key === s.cells[i].key && c.colSpan === s.cells[i].colSpan && c.rowSpan === s.cells[i].rowSpan);
    // Re-flowed rows are written with the column count as the designer writes it ("1", "11", "111", "1111", ruling F4).
    const columns = '1'.repeat(s.columns);
    const current = el.getAttribute('columns');
    if (columnCount(current) !== s.columns || (!same && current !== columns)) el.setAttribute('columns', columns);
    if (same) return el;
    this.rows(el, layout(this.originalRows, s), s.cells, cells);
    return el;
  }

  cell(c: DesignCell, header: boolean): Element {
    let el = this.index.get(c.key);
    if (!el) {
      el = element(this.doc, 'cell', { id: c.id, showlabel: String(c.showLabel) });
      // A subgrid cell is written as Dataverse writes it (G2).
      if (c.control?.kind === 'subgrid') {
        el.setAttribute('rowspan', String(c.rowSpan));
        el.setAttribute('colspan', String(c.colSpan));
        el.setAttribute('auto', 'false');
      }
      el.appendChild(this.doc.createElement('labels'));
      if (c.attribute) {
        el.appendChild(element(this.doc, 'control', {
          id: header ? `header_${c.attribute}` : c.attribute, classid: c.classId ?? CLASS_ID.text, datafieldname: c.attribute,
          ...(c.uniqueId ? { uniqueid: c.uniqueId } : {}), disabled: String(c.disabled),
        }));
      } else if (c.control) {
        const control = writeControl(this.doc, undefined, c.control);
        if (c.uniqueId) control.setAttribute('uniqueid', c.uniqueId);
        el.appendChild(control);
      }
      this.created.add(el);
    } else {
      const control = childElements(el, 'control')[0];
      if (control && c.control) writeControl(this.doc, control, c.control, readControl(control));
      const current = control?.getAttribute('uniqueid')?.trim().toLowerCase() ?? '';
      if (control && c.uniqueId && current !== c.uniqueId.toLowerCase()) control.setAttribute('uniqueid', c.uniqueId);
    }
    setFlag(el, 'showlabel', c.showLabel, true);
    setFlag(el, 'visible', c.visible, true);
    setCount(el, 'colspan', c.colSpan);
    setCount(el, 'rowspan', c.rowSpan);
    setLabels(el, c.labels);
    const control = childElements(el, 'control')[0];
    if (control) setFlag(control, 'disabled', c.disabled, false);
    this.written.push({ cell: c, el, source: this.sourceCells.get(c.key) });
    return el;
  }

  /**
   * Writes `rows` into `parent`'s `<rows>`: a row that comes `from` a source row re-uses that `<row>` element (and its
   * attributes), any other row is a new `<row>`. Source rows not written are removed. In an indented FormXML the rows
   * and cells are then put on their own lines (C3), and a new cell's content is indented under it.
   */
  rows(parent: Element, rows: KeptRow<DesignCell>[], design: DesignCell[], cells: Element[]) {
    for (const cell of cellEls(parent)) cell.parentNode?.removeChild(cell);
    const holder = container(parent, 'rows');
    const old = childElements(holder, 'row');
    const byKey = new Map(design.map((c, i) => [c.key, cells[i]]));
    const used = new Set<number>();
    const wanted = rows.map((row) => {
      const reuse = row.from !== undefined && old[row.from] && !used.has(row.from);
      if (reuse) used.add(row.from!);
      const rowEl = reuse ? old[row.from!] : this.doc.createElement('row');
      for (const c of row.cells) rowEl.appendChild(byKey.get(c.key)!);
      return rowEl;
    });
    place(holder, old, wanted);
    reindentRows(holder);
    for (const cell of cells) {
      const indent = this.created.has(cell) ? ownIndent(cell) : undefined;
      if (indent !== undefined) indentTree(cell, indent, indentStep(holder));
    }
  }

  header(cells: DesignCell[]) {
    const unchanged = this.originalHeader.length === cells.length
      && this.originalHeader.every((c, i) => c.key === cells[i].key && c.colSpan === cells[i].colSpan && c.rowSpan === cells[i].rowSpan);
    const els = cells.map((c) => this.cell(c, true));
    if (unchanged) return;
    let headerEl = this.sc.headerEl;
    if (!headerEl) {
      headerEl = element(this.doc, 'header', { id: newGuid(), columns: '1111', celllabelposition: 'Top' });
      this.sc.tabsEl.parentNode!.insertBefore(headerEl, this.sc.tabsEl.nextSibling);
    }
    // Up to HEADER_LIMIT fields stay in one row: a narrower header is widened to fit them (at most 4 columns).
    const fields = cells.filter((c) => c.kind === 'field').reduce((n, c) => n + c.colSpan, 0);
    const sourceWidth = columnCount(headerEl.getAttribute('columns'));
    let width = sourceWidth;
    if (fields > width && width < HEADER_LIMIT) {
      width = Math.min(fields, HEADER_LIMIT);
      headerEl.setAttribute('columns', '1'.repeat(width));
    }
    const before = this.originalHeaderRows;
    const kept = before && sourceWidth === width
      ? keepRows(before, cells, width)
      : flowCells(cells, width).map((r, i) => ({ cells: r, from: i }));
    this.rows(headerEl, kept, cells, els);
  }
}

/** The `<event name="onchange" attribute="{attribute}">` elements of the form's `<events>`. */
function changeEvents(form: Element, attribute: string): Element[] {
  const wanted = lower(attribute);
  return childElements(form, 'events').flatMap((e) => childElements(e, 'event'))
    .filter((ev) => lower(ev.getAttribute('name')) === 'onchange' && lower(ev.getAttribute('attribute')) === wanted);
}

/** The columns (lower case) bound by the field controls of the form's `<footer>`, which the designer does not model. */
function footerColumns(form: Element): string[] {
  return childElements(form, 'footer').flatMap((f) => Array.from(f.getElementsByTagName('control')))
    .map((c) => (c.getAttribute('datafieldname') ?? '').trim().toLowerCase()).filter((a) => a !== '');
}

/** The columns (lower case) of the field cells of body and header, with repeats. */
const fieldColumns = (b: Pick<FormDesign, 'tabs' | 'header'>): string[] =>
  allCells(b).filter((c) => c.kind === 'field' && c.attribute).map((c) => c.attribute!.toLowerCase());

/**
 * The OnChange events of `d.events` whose column has no field control on the form (body, header or footer), in
 * `d.events` order: events the runtime never fires, which still block their library's removal.
 */
export function orphanChangeEvents(d: FormDesign): DesignEvent[] {
  const columns = new Set([...fieldColumns(d), ...footerColumns(d.source.documentElement)]);
  return d.events.filter((e) => e.name === 'onchange' && !columns.has(lower(e.attribute)));
}

/**
 * The FormXML of the design: `d.text` when nothing changed, else a copy of `d.source` with the changes applied. Bindings
 * are written by {@link writePcfs}: a removed cell's `<controlDescription>` goes with it. Libraries and events are written
 * by {@link writeEvents} from `d.libraries` and `d.events`, where the operations already applied ruling F7 (see
 * {@link changed}). What the model does not hold is dropped here for a source column that has no field control left
 * (body, header or footer): its `application="true"` OnChange events, and the `<InternalHandlers>` that
 * {@link writeEvents} keeps of an edited OnChange event it removed. F7 drops the whole event and never a library.
 */
export function writeForm(d: FormDesign): string {
  const w = new Writer(d.source);
  const before = readBody(w.sc);
  if (bodyJson(d) === bodyJson(before) && eventsJson(d) === eventsJson(readEvents(w.sc.form))) return d.text;
  const remaining = new Set([...fieldColumns(d), ...footerColumns(w.sc.form)]);
  const gone = [...new Set(fieldColumns(before))].filter((a) => !remaining.has(a));
  const eventBlocks = childElements(w.sc.form, 'events').filter((e) => e.children.length > 0);
  place(w.sc.tabsEl, childElements(w.sc.tabsEl, 'tab'), d.tabs.map((t) => w.tab(t)));
  w.header(d.header);
  writePcfs(w.sc.form, w.written, w.sourceIds);
  writeEvents(w.sc.form, d);
  // Read after the events are written, so an event the model already dropped is not dropped twice.
  for (const ev of gone.flatMap((a) => changeEvents(w.sc.form, a))) detach(ev);
  // An F7 drop is a designer edit: an `<events>` it empties goes (G6).
  dropEmptied(eventBlocks);
  return serialize(w.doc);
}

export type NewFormType = (typeof FORM_TYPE)[keyof typeof FORM_TYPE];

/** A new form (Main by default): one tab and one section "General" (one column) holding the primary name column. */
export function newForm(
  table: string, primaryName: AttributeMeta, lcid: number, name: string, type: NewFormType = FORM_TYPE.main,
): FormDesign {
  const doc = parseXml('<form/>');
  const add = (parent: Element, tag: string, attributes: Record<string, string> = {}) =>
    parent.appendChild(element(doc, tag, attributes));
  const labels = (parent: Element, text: string) =>
    add(add(parent, 'labels'), 'label', { description: text, languagecode: String(lcid) });
  const tab = add(add(doc.documentElement, 'tabs'), 'tab', {
    name: 'tab_1', id: newGuid(), expanded: 'true', showlabel: 'true', verticallayout: 'true',
  });
  labels(tab, 'General');
  const section = add(add(add(add(tab, 'columns'), 'column', { width: '100%' }), 'sections'), 'section', {
    name: 'section_1', id: newGuid(), showlabel: 'true', showbar: 'false', columns: '1',
  });
  labels(section, 'General');
  const cell = add(add(add(section, 'rows'), 'row'), 'cell', { id: newGuid(), showlabel: 'true' });
  labels(cell, primaryName.displayName || primaryName.logicalName);
  add(cell, 'control', {
    id: primaryName.logicalName, classid: classIdFor(primaryName), datafieldname: primaryName.logicalName, disabled: 'false',
  });
  return readForm({
    name, description: '', objecttypecode: table, type, isdefault: false, formactivationstate: 1,
    formxml: serialize(doc),
  }, lcid);
}

// ---------------------------------------------------------------------------------------------------------------------
// Operations: each returns a new design, or `d` itself when nothing changes.

/**
 * The design with new body and header; `d` itself when they are unchanged. Every operation that removes a control goes
 * through here, so here ruling F7 is applied to the model: a column whose last field control (body, header or footer)
 * left the form loses its OnChange events from `d.events`, those of the source and those added in the session alike.
 */
function changed(d: FormDesign, tabs: DesignTab[], header = d.header): FormDesign {
  if (bodyJson({ tabs, header }) === bodyJson(d)) return d;
  const next = { ...d, tabs, header };
  const staying = new Set([...fieldColumns(next), ...footerColumns(d.source.documentElement)]);
  const left = new Set(fieldColumns(d).filter((a) => !staying.has(a)));
  if (left.size === 0) return next;
  const events = d.events.filter((e) => e.name !== 'onchange' || !left.has(lower(e.attribute)));
  return events.length === d.events.length ? next : { ...next, events };
}

/** `{prefix}{n}` with the smallest `n` used neither by the design nor by the original FormXML (ruling F5). */
function freshName(prefix: string, used: string[]): string {
  const taken = new Set(used.map((n) => n.toLowerCase()));
  let n = 1;
  while (taken.has(`${prefix}${n}`.toLowerCase())) n += 1;
  return `${prefix}${n}`;
}

function sourceNames(d: FormDesign, tag: 'tab' | 'section'): string[] {
  return Array.from(d.source.getElementsByTagName(tag)).map((el) => el.getAttribute('name') ?? '');
}

function newSection(d: FormDesign, tabs: DesignTab[]): DesignSection {
  const name = freshName('section_', [...bodySections(tabs).map((s) => s.name), ...sourceNames(d, 'section')]);
  const id = newGuid();
  return { key: id, name, id, labels: { [d.lcid]: name }, showLabel: true, visible: true, columns: 1, cells: [] };
}

const newColumn = (width: string, sections: DesignSection[] = []): DesignColumn =>
  ({ key: `col${newGuid()}`, width, sections });

export function addTab(d: FormDesign): FormDesign {
  const name = freshName('tab_', [...d.tabs.map((t) => t.name), ...sourceNames(d, 'tab')]);
  const id = newGuid();
  const tab: DesignTab = {
    key: id, name, id, labels: { [d.lcid]: name }, showLabel: true, visible: true,
    columns: [newColumn('100%', [newSection(d, d.tabs)])],
  };
  return { ...d, tabs: [...d.tabs, tab] };
}

export function removeTab(d: FormDesign, key: string): FormDesign {
  if (d.tabs.length <= 1) return d;
  return changed(d, d.tabs.filter((t) => t.key !== key));
}

export function moveTab(d: FormDesign, key: string, by: -1 | 1): FormDesign {
  const i = d.tabs.findIndex((t) => t.key === key);
  const j = i + by;
  if (i < 0 || j < 0 || j >= d.tabs.length) return d;
  const tabs = [...d.tabs];
  [tabs[i], tabs[j]] = [tabs[j], tabs[i]];
  return { ...d, tabs };
}

const withLabel = (labels: Record<number, string>, lcid: number, label: string | undefined) =>
  label === undefined || labels[lcid] === label ? labels : { ...labels, [lcid]: label };

export function setTab(
  d: FormDesign,
  key: string,
  patch: { label?: string; showLabel?: boolean; visible?: boolean },
): FormDesign {
  return changed(d, d.tabs.map((t) => (t.key !== key ? t : {
    ...t,
    labels: withLabel(t.labels, d.lcid, patch.label),
    showLabel: patch.showLabel ?? t.showLabel,
    visible: patch.visible ?? t.visible,
  })));
}

/** Sets the tab's column widths; the sections of dropped columns go, in order, to the end of the last column kept. */
export function setTabLayout(d: FormDesign, key: string, layout: TabLayout): FormDesign {
  const widths = LAYOUT_WIDTHS[layout];
  return changed(d, d.tabs.map((t) => {
    if (t.key !== key) return t;
    const columns = widths.map((width, i) => {
      const c = t.columns[i];
      return c ? (c.width === width ? c : { ...c, width }) : newColumn(width);
    });
    const dropped = t.columns.slice(widths.length).flatMap((c) => c.sections);
    if (dropped.length > 0) {
      const last = columns[columns.length - 1];
      columns[columns.length - 1] = { ...last, sections: [...last.sections, ...dropped] };
    }
    return { ...t, columns };
  }));
}

/** Adds a section at the end of column `columnIndex` of the tab. */
export function addSection(d: FormDesign, tabKey: string, columnIndex: number): FormDesign {
  const tab = d.tabs.find((t) => t.key === tabKey);
  if (!tab?.columns[columnIndex]) return d;
  const section = newSection(d, d.tabs);
  return changed(d, d.tabs.map((t) => (t !== tab ? t : {
    ...t,
    columns: t.columns.map((c, i) => (i !== columnIndex ? c : { ...c, sections: [...c.sections, section] })),
  })));
}

/** Where a section is: its tab, column and position. */
function locate(d: FormDesign, key: string) {
  for (const tab of d.tabs) {
    for (let ci = 0; ci < tab.columns.length; ci += 1) {
      const si = tab.columns[ci].sections.findIndex((s) => s.key === key);
      if (si >= 0) return { tab, ci, si };
    }
  }
  return undefined;
}

const withColumns = (d: FormDesign, tab: DesignTab, columns: DesignColumn[]) =>
  changed(d, d.tabs.map((t) => (t === tab ? { ...t, columns } : t)));

/** Removes a section, but not the only section of its tab. */
export function removeSection(d: FormDesign, key: string): FormDesign {
  const at = locate(d, key);
  if (!at || bodySections([at.tab]).length <= 1) return d;
  return withColumns(d, at.tab, at.tab.columns.map((c, i) =>
    (i !== at.ci ? c : { ...c, sections: c.sections.filter((s) => s.key !== key) })));
}

/** Moves a section within its column, or to the end of the previous or next column of its tab. */
export function moveSection(d: FormDesign, key: string, to: 'up' | 'down' | 'prev-column' | 'next-column'): FormDesign {
  const at = locate(d, key);
  if (!at) return d;
  const columns = [...at.tab.columns];
  const from = columns[at.ci];
  if (to === 'up' || to === 'down') {
    const j = at.si + (to === 'up' ? -1 : 1);
    if (j < 0 || j >= from.sections.length) return d;
    const sections = [...from.sections];
    [sections[at.si], sections[j]] = [sections[j], sections[at.si]];
    columns[at.ci] = { ...from, sections };
  } else {
    const target = at.ci + (to === 'prev-column' ? -1 : 1);
    if (target < 0 || target >= columns.length) return d;
    const section = from.sections[at.si];
    columns[at.ci] = { ...from, sections: from.sections.filter((s) => s.key !== key) };
    columns[target] = { ...columns[target], sections: [...columns[target].sections, section] };
  }
  return withColumns(d, at.tab, columns);
}

/** Edits a section; a new column count clamps the cells' `colSpan` to it (the rows are re-flowed on write). */
export function setSection(
  d: FormDesign,
  key: string,
  patch: { label?: string; showLabel?: boolean; visible?: boolean; columns?: 1 | 2 | 3 | 4; labelPosition?: 'left' | 'top' },
): FormDesign {
  const at = locate(d, key);
  if (!at) return d;
  return withColumns(d, at.tab, at.tab.columns.map((c, i) => (i !== at.ci ? c : {
    ...c,
    sections: c.sections.map((s) => {
      if (s.key !== key) return s;
      const columns = patch.columns ?? s.columns;
      return {
        ...s,
        labels: withLabel(s.labels, d.lcid, patch.label),
        showLabel: patch.showLabel ?? s.showLabel,
        visible: patch.visible ?? s.visible,
        columns,
        labelPosition: patch.labelPosition ?? s.labelPosition,
        cells: columns === s.columns ? s.cells
          : s.cells.map((cell) => clampSpan(cell, columns)),
      };
    }),
  })));
}

// ---------------------------------------------------------------------------------------------------------------------
// Fields and header

/** The columns (lower case) of the body's field cells. */
export function bodyAttributes(d: FormDesign): Set<string> {
  return new Set(bodyCells(d).filter((c) => c.attribute).map((c) => c.attribute!.toLowerCase()));
}

/** Applies `f` to every section; `d` itself when nothing changes. */
const mapSections = (d: FormDesign, f: (s: DesignSection) => DesignSection): FormDesign =>
  changed(d, d.tabs.map((t) => ({ ...t, columns: t.columns.map((c) => ({ ...c, sections: c.sections.map(f) })) })));

function findCell(d: FormDesign, key: string) {
  for (const section of bodySections(d.tabs)) {
    const cell = section.cells.find((c) => c.key === key);
    if (cell) return { section, cell };
  }
  return undefined;
}

function newFieldCell(d: FormDesign, attr: AttributeMeta): DesignCell {
  const id = newGuid();
  return {
    key: id, id, kind: 'field', attribute: attr.logicalName, labels: { [d.lcid]: attr.displayName || attr.logicalName },
    showLabel: true, visible: true, disabled: false, colSpan: 1, rowSpan: 1, classId: classIdFor(attr),
  };
}

/**
 * Adds a field at the end of the section: the first free slot after its last cell, or a new row (ruling F6). A no-op when
 * the column is already in the body (the control id must stay unique).
 */
export function addField(d: FormDesign, sectionKey: string, attr: AttributeMeta): FormDesign {
  if (bodyAttributes(d).has(attr.logicalName.toLowerCase()) || !bodySections(d.tabs).some((s) => s.key === sectionKey)) return d;
  const cell = newFieldCell(d, attr);
  return mapSections(d, (s) => (s.key === sectionKey ? { ...s, cells: [...s.cells, cell] } : s));
}

/**
 * Removes a body cell of any kind. When it was the column's last control on the form, the column's OnChange events go
 * too (ruling F7, see {@link changed}).
 */
export function removeField(d: FormDesign, cellKey: string): FormDesign {
  const at = findCell(d, cellKey);
  if (!at) return d;
  return mapSections(d, (s) => (s !== at.section ? s : { ...s, cells: s.cells.filter((c) => c.key !== cellKey) }));
}

/**
 * What removing a body or header field loses: the `onchange` handlers the removal would delete (none while another
 * control of the column stays in the body, header or footer), and whether the column is required.
 */
export function fieldImpact(d: FormDesign, cellKey: string, attrs: AttributeMeta[]): { handlers: number; required: boolean } {
  const cell = findCell(d, cellKey)?.cell ?? d.header.find((c) => c.key === cellKey);
  const logical = cell?.kind === 'field' ? cell.attribute?.toLowerCase() : undefined;
  if (!logical) return { handlers: 0, required: false };
  const handlers = lostHandlers(d, [logical], new Set([cellKey]));
  const level = attrs.find((a) => a.logicalName.toLowerCase() === logical)?.requiredLevel;
  return { handlers, required: level === 'ApplicationRequired' || level === 'SystemRequired' };
}

/**
 * The `onchange` handlers of `columns` (lower case) that removing the cells `gone` deletes: a column's handlers count
 * only when no field control of it stays in the body, the header or the footer. They are the handlers of the column's
 * OnChange events in `d.events` (as edited in the session) and the `<Handlers>` of its `application="true"` OnChange
 * events, which the write drops too.
 */
function lostHandlers(d: FormDesign, columns: string[], gone: Set<string>): number {
  const form = d.source.documentElement;
  const staying = new Set([
    ...allCells(d)
      .filter((c) => !gone.has(c.key) && c.kind === 'field' && c.attribute).map((c) => c.attribute!.toLowerCase()),
    ...footerColumns(form),
  ]);
  const lost = new Set(columns.filter((a) => !staying.has(a)));
  const edited = d.events.filter((e) => e.name === 'onchange' && lost.has(lower(e.attribute)))
    .reduce((n, e) => n + e.handlers.length, 0);
  const other = [...lost].flatMap((a) => changeEvents(form, a)).filter((ev) => !isEdited(form, ev))
    .reduce((n, ev) => n + childElements(ev, 'Handlers').flatMap((h) => childElements(h, 'Handler')).length, 0);
  return edited + other;
}

/**
 * What removing a tab or a section (by key) loses: its cells other than empty ones, and the `onchange` handlers of the
 * columns whose last control on the form is inside it (the write drops them, see {@link writeForm}).
 */
export function containerImpact(d: FormDesign, key: string): { cells: number; handlers: number } {
  const tab = d.tabs.find((t) => t.key === key);
  const section = tab ? undefined : bodySections(d.tabs).find((s) => s.key === key);
  const cells = (tab ? bodySections([tab]) : section ? [section] : []).flatMap((s) => s.cells).filter((c) => c.kind !== 'empty');
  const columns = cells.filter((c) => c.kind === 'field' && c.attribute).map((c) => c.attribute!.toLowerCase());
  return { cells: cells.length, handlers: lostHandlers(d, columns, new Set(cells.map((c) => c.key))) };
}

/**
 * Moves a body cell up or down within its section (the rows are re-flowed on write, so it crosses rows), or to the
 * previous or next section in reading order (tabs, columns, sections): to the end of the previous one, or the start of
 * the next one. Its `colSpan` is clamped to the target section.
 */
export function moveCell(d: FormDesign, cellKey: string, to: 'up' | 'down' | 'prev-section' | 'next-section'): FormDesign {
  const at = findCell(d, cellKey);
  if (!at) return d;
  const i = at.section.cells.indexOf(at.cell);
  if (to === 'up' || to === 'down') {
    const j = i + (to === 'up' ? -1 : 1);
    if (j < 0 || j >= at.section.cells.length) return d;
    const cells = [...at.section.cells];
    [cells[i], cells[j]] = [cells[j], cells[i]];
    return mapSections(d, (s) => (s === at.section ? { ...s, cells } : s));
  }
  const all = bodySections(d.tabs);
  const target = all[all.indexOf(at.section) + (to === 'prev-section' ? -1 : 1)];
  if (!target) return d;
  const cell = clampSpan(at.cell, target.columns);
  return mapSections(d, (s) => {
    if (s === at.section) return { ...s, cells: s.cells.filter((c) => c.key !== cellKey) };
    if (s === target) return { ...s, cells: to === 'prev-section' ? [...s.cells, cell] : [cell, ...s.cells] };
    return s;
  });
}

type CellPatch = { label?: string; showLabel?: boolean; visible?: boolean; disabled?: boolean };

const patchCell = (c: DesignCell, lcid: number, patch: CellPatch): DesignCell => ({
  ...c,
  labels: withLabel(c.labels, lcid, patch.label),
  showLabel: patch.showLabel ?? c.showLabel,
  visible: patch.visible ?? c.visible,
  disabled: patch.disabled ?? c.disabled,
});

/** Edits a body or header cell: label (UI language), show label, visible and read-only (`disabled` on the control). */
export function setCell(d: FormDesign, cellKey: string, patch: CellPatch): FormDesign {
  if (d.header.some((c) => c.key === cellKey)) {
    return changed(d, d.tabs, d.header.map((c) => (c.key === cellKey ? patchCell(c, d.lcid, patch) : c)));
  }
  return mapSections(d, (s) => (s.cells.some((c) => c.key === cellKey)
    ? { ...s, cells: s.cells.map((c) => (c.key === cellKey ? patchCell(c, d.lcid, patch) : c)) } : s));
}

/**
 * Adds a field at the end of the header, with the control id `header_{logical}` (ruling F8). A no-op when the header
 * already holds HEADER_LIMIT field cells or the column. The same column may also be in the body.
 */
export function addHeaderField(d: FormDesign, attr: AttributeMeta): FormDesign {
  const fields = d.header.filter((c) => c.kind === 'field');
  const logical = attr.logicalName.toLowerCase();
  if (fields.length >= HEADER_LIMIT || fields.some((c) => c.attribute?.toLowerCase() === logical)) return d;
  return { ...d, header: [...d.header, newFieldCell(d, attr)] };
}

/** Removes a header field cell; other header cells are kept (a no-op for them). */
export function removeHeaderField(d: FormDesign, cellKey: string): FormDesign {
  if (!d.header.some((c) => c.key === cellKey && c.kind === 'field')) return d;
  return changed(d, d.tabs, d.header.filter((c) => c.key !== cellKey));
}

/** Swaps a header field with the nearest header field to its left (-1) or right (1); other header cells stay put. */
export function moveHeaderField(d: FormDesign, cellKey: string, by: -1 | 1): FormDesign {
  const i = d.header.findIndex((c) => c.key === cellKey && c.kind === 'field');
  if (i < 0) return d;
  let j = i + by;
  while (j >= 0 && j < d.header.length && d.header[j].kind !== 'field') j += by;
  if (j < 0 || j >= d.header.length) return d;
  const header = [...d.header];
  [header[i], header[j]] = [header[j], header[i]];
  return changed(d, d.tabs, header);
}

// Positional moves, for drag and drop. `index` counts after the element has been taken out of its old place, and is
// clamped to the target's length. Each returns `d` itself when nothing moves.

const inserted = <T>(list: T[], index: number, item: T): T[] => {
  const at = Math.min(Math.max(index, 0), list.length);
  return [...list.slice(0, at), item, ...list.slice(at)];
};

/** Moves a body cell to `index` of the section `sectionKey` (any section); its `colSpan` is clamped to the target. */
export function placeCell(d: FormDesign, cellKey: string, sectionKey: string, index: number): FormDesign {
  const at = findCell(d, cellKey);
  const target = bodySections(d.tabs).find((s) => s.key === sectionKey);
  if (!at || !target) return d;
  const cell = at.section !== target ? clampSpan(at.cell, target.columns) : at.cell;
  return mapSections(d, (s) => {
    const rest = s === at.section ? s.cells.filter((c) => c.key !== cellKey) : s.cells;
    return s === target ? { ...s, cells: inserted(rest, index, cell) } : s === at.section ? { ...s, cells: rest } : s;
  });
}

/** Moves a section to `index` of the column `columnKey`, which must be a column of the section's own tab. */
export function placeSection(d: FormDesign, key: string, columnKey: string, index: number): FormDesign {
  const at = locate(d, key);
  if (!at || !at.tab.columns.some((c) => c.key === columnKey)) return d;
  const section = at.tab.columns[at.ci].sections[at.si];
  return withColumns(d, at.tab, at.tab.columns.map((c, i) => {
    const rest = i === at.ci ? c.sections.filter((s) => s.key !== key) : c.sections;
    return c.key === columnKey ? { ...c, sections: inserted(rest, index, section) } : i === at.ci ? { ...c, sections: rest } : c;
  }));
}

/** Moves a tab to `index` of the tab list. */
export function placeTab(d: FormDesign, key: string, index: number): FormDesign {
  const tab = d.tabs.find((t) => t.key === key);
  if (!tab) return d;
  const tabs = inserted(d.tabs.filter((t) => t !== tab), index, tab);
  return tabs.every((t, i) => t === d.tabs[i]) ? d : { ...d, tabs };
}

/** Moves a header field to `index` of the header cells. */
export function placeHeaderField(d: FormDesign, cellKey: string, index: number): FormDesign {
  const cell = d.header.find((c) => c.key === cellKey && c.kind === 'field');
  if (!cell) return d;
  const header = inserted(d.header.filter((c) => c !== cell), index, cell);
  return header.every((c, i) => c === d.header[i]) ? d : changed(d, d.tabs, header);
}
