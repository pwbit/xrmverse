import type { CustomControlDataSet, CustomControlParameter } from '../../model/form';
import { chosenCustomControl, dataSetOf, parametersOf } from '../../parsers/formXml';
import { childElements } from '../../parsers/xml';
import { manifestTypeOf } from '../../pcf/context';
import type { PcfManifest } from '../../pcf/manifest';
import type { AttributeMeta } from '../tables/metadata';
import { allCells, lower, mapCell } from './formCells';
import { subgridDataSet, writeControl } from './formControls';
import type { DesignCell, FormDesign } from './formDesign';
import { newGuid } from './formIds';
import { append, detach, insertBlock } from './xmlIndent';

/**
 * A custom (PCF) control that replaces a field or subgrid control (G5): the web entry (`formFactor="0"`, else the one
 * without `formFactor`) of its `<controlDescription>`.
 */
export interface PcfBinding {
  /** The `customcontrol` row name (`{prefix}_{namespace}.{constructor}`). */
  name: string;
  /** By manifest property name: a column (bound) or a literal (`static`, input). */
  parameters: Record<string, CustomControlParameter>;
  /** A dataset control's `<data-set>`. */
  dataSet?: CustomControlDataSet;
  /**
   * The phone and tablet entries (`formFactor` 1 and 2) differ from the web entry, or one is missing: an edit changes the
   * web entry only. Otherwise they follow it.
   */
  ownFactors: boolean;
}

/** A `customcontrols` row: its manifest, or why the manifest could not be read. */
export interface PcfControlRow {
  name: string;
  manifest?: PcfManifest;
  error?: string;
}

const isTrue = (el: Element, name: string) => {
  const v = lower(el.getAttribute(name));
  return v === 'true' || v === '1';
};

/** The binding with its keys in one order, so that two equal bindings serialize alike (the design's no-change check). */
function canonical(b: PcfBinding): PcfBinding {
  const parameters: Record<string, CustomControlParameter> = {};
  for (const [name, p] of Object.entries(b.parameters)) {
    parameters[name] = p.type !== undefined ? { value: p.value, static: p.static, type: p.type } : { value: p.value, static: p.static };
  }
  const dataSet = b.dataSet ? { name: b.dataSet.name, parameters: { ...b.dataSet.parameters } } : undefined;
  const out: PcfBinding = { name: b.name, parameters, ...(dataSet ? { dataSet } : {}), ownFactors: b.ownFactors };
  return out;
}

const same = (a: PcfBinding | undefined, b: PcfBinding | undefined) =>
  JSON.stringify(a && canonical(a)) === JSON.stringify(b && canonical(b));

/** An element as text with the whitespace between and around its parts left out: for comparing entries. */
function shape(el: Element): string {
  const attrs = Array.from(el.attributes).map((a) => `${a.name}=${JSON.stringify(a.value)}`).sort().join(' ');
  const children = Array.from(el.children);
  const body = children.length > 0 ? children.map(shape).join('') : JSON.stringify((el.textContent ?? '').trim());
  return `<${el.tagName} ${attrs}>${body}</>`;
}

const entryShape = (entry: Element) =>
  `${(entry.getAttribute('name') ?? '').trim()}|${childElements(entry, 'parameters').map(shape).join('')}`;

/** The named `customControl` of the form factor `n`. */
const factor = (description: Element, n: string): Element | undefined =>
  childElements(description, 'customControl')
    .find((c) => (c.getAttribute('name') ?? '').trim() !== '' && c.getAttribute('formFactor')?.trim() === n);

function bindingOf(description: Element): PcfBinding | undefined {
  const chosen = chosenCustomControl(description);
  if (!chosen) return undefined;
  const dataSet = dataSetOf(chosen);
  const ownFactors = ['1', '2'].some((n) => {
    const other = factor(description, n);
    return !other || entryShape(other) !== entryShape(chosen);
  });
  return canonical({
    name: chosen.getAttribute('name')!.trim(), parameters: parametersOf(chosen), ...(dataSet ? { dataSet } : {}), ownFactors,
  });
}

const allDescriptions = (form: Element) =>
  childElements(form, 'controlDescriptions').flatMap((c) => childElements(c, 'controlDescription'));

/** By `forControl` (lower case): the `<controlDescription>`; a later one wins, as the runtime parser reads them. */
function descriptionsOf(form: Element): Map<string, Element> {
  const out = new Map<string, Element>();
  for (const d of allDescriptions(form)) {
    const forControl = lower(d.getAttribute('forControl'));
    if (forControl) out.set(forControl, d);
  }
  return out;
}

/** By control `uniqueid` (lower case): the bindings the designer can model. */
export function pcfBindings(form: Element): Map<string, PcfBinding> {
  const out = new Map<string, PcfBinding>();
  for (const [id, d] of descriptionsOf(form)) {
    const b = bindingOf(d);
    if (b) out.set(id, b);
  }
  return out;
}

/**
 * The binding of the control with `uniqueId` in the `<form>` element `form`; undefined when there is none, or when its
 * `<controlDescription>` has no entry the runtime would use (the designer leaves such a description untouched).
 */
export function readPcf(form: Element, uniqueId: string): PcfBinding | undefined {
  const d = descriptionsOf(form).get(lower(uniqueId));
  return d ? bindingOf(d) : undefined;
}

/** The `uniqueid`s (lower case) of the controls of the design's source. */
const sourceUniqueIds = (d: FormDesign): Set<string> =>
  new Set(Array.from(d.source.getElementsByTagName('control')).map((c) => lower(c.getAttribute('uniqueid'))).filter((id) => id !== ''));

/** Whether a custom control can replace the cell's control (G5): a field with a column, or a subgrid. */
export const canBindPcf = (c: DesignCell): boolean =>
  (c.kind === 'field' && !!c.attribute) || (c.kind === 'subgrid' && c.control?.kind === 'subgrid');

/**
 * Sets or removes (`undefined`, "Use the standard control") the binding of a field or subgrid cell, body or header.
 * `ownFactors` is kept as read (false for a new binding). A new binding on a subgrid takes its `<data-set>` values from the
 * subgrid. A binding on a cell without `uniqueId` gives it a new one; removing the binding removes a `uniqueId` that no
 * control of the source has (one given in the session). `d` itself for another kind of cell, an unknown cell or no change.
 */
export function setPcf(d: FormDesign, cellKey: string, binding: PcfBinding | undefined): FormDesign {
  const cell = allCells(d).find((c) => c.key === cellKey);
  if (!cell || !canBindPcf(cell)) return d;
  let next: PcfBinding | undefined;
  if (binding) {
    next = canonical({ ...binding, ownFactors: cell.pcf?.ownFactors ?? false });
    if (!cell.pcf && next.dataSet && cell.control?.kind === 'subgrid') {
      next.dataSet = { name: next.dataSet.name, parameters: { ...next.dataSet.parameters, ...subgridDataSet(cell.control) } };
    }
  }
  let uniqueId = cell.uniqueId;
  if (next && !uniqueId) uniqueId = newGuid();
  if (!next && uniqueId && !sourceUniqueIds(d).has(lower(uniqueId))) uniqueId = undefined;
  if (same(next, cell.pcf) && uniqueId === cell.uniqueId) return d;
  return mapCell(d, cellKey, (c) => {
    // `uniqueId` then `pcf`, the order `readForm` gives them.
    const { pcf: _, uniqueId: _u, ...rest } = c;
    return { ...rest, ...(uniqueId ? { uniqueId } : {}), ...(next ? { pcf: next } : {}) };
  });
}

/**
 * A new binding to the control `name` for the cell: the first `bound` property takes the cell's column, each `input`
 * property with a `default-value` takes it as a literal of its `of-type`. On a subgrid the first `data-set` of the manifest
 * holds the subgrid's values (the first one without `property-set`). Other properties are left for the maker to set.
 */
export function newPcfBinding(name: string, manifest: PcfManifest, cell: DesignCell): PcfBinding {
  const parameters: Record<string, CustomControlParameter> = {};
  let column = cell.kind === 'field' ? cell.attribute : undefined;
  for (const p of manifest.properties) {
    if (p.usage === 'bound' && column) {
      parameters[p.name] = { value: column, static: false };
      column = undefined;
    } else if (p.usage === 'input' && p.defaultValue !== undefined) {
      const type = p.ofType || p.types[0];
      parameters[p.name] = { value: p.defaultValue, static: true, ...(type ? { type } : {}) };
    }
  }
  const binding: PcfBinding = { name, parameters, ownFactors: false };
  const ds = manifest.dataSets.find((x) => !x.hasPropertySet);
  if (ds && cell.control?.kind === 'subgrid') binding.dataSet = { name: ds.name, parameters: subgridDataSet(cell.control) };
  return canonical(binding);
}

/**
 * The controls that can replace the cell's control (G5): on a field cell, those with a `bound` property whose type matches
 * the column (`manifestTypeOf`); on a subgrid cell, those with a `data-set`. A row whose manifest did not parse is never
 * offered, nor one the runtime cannot render: a field control that declares a data-set, a dataset control whose every
 * data-set has a `property-set`.
 */
export function pcfChoices(
  manifests: PcfControlRow[], cell: Pick<DesignCell, 'kind'>, attr: AttributeMeta | undefined,
): { name: string; manifest: PcfManifest }[] {
  const parsed = manifests.flatMap((r) => (r.manifest ? [{ name: r.name, manifest: r.manifest }] : []));
  // The runtime renders the data-set the binding names and refuses one with a `property-set` (PcfDatasetControl).
  if (cell.kind === 'subgrid') return parsed.filter((r) => r.manifest.dataSets.some((ds) => !ds.hasPropertySet));
  if (cell.kind !== 'field' || !attr) return [];
  const type = lower(manifestTypeOf(attr));
  return parsed.filter((r) => r.manifest.dataSets.length === 0 && r.manifest.properties.some((p) => p.usage === 'bound' && p.types.some((t) => lower(t) === type)));
}

/**
 * The names of the manifest's required properties that the binding leaves unset or blank, in manifest order. A property
 * the binding leaves out is set when the manifest gives it a non-blank `default-value`: the runtime uses it (a binding
 * imported from a solution often writes only the bound column). A value the binding gives wins, so a blank one is unset.
 */
export function missingRequired(binding: PcfBinding, manifest: PcfManifest): string[] {
  const set = (p: PcfManifest['properties'][number]) => (Object.hasOwn(binding.parameters, p.name)
    ? binding.parameters[p.name].value.trim() !== ''
    : (p.defaultValue ?? '').trim() !== '');
  return manifest.properties.filter((p) => p.required && p.usage !== 'output' && !set(p)).map((p) => p.name);
}

/** The row of the control `name` (case ignored), or undefined when it is not installed. */
export const pcfRow = (rows: PcfControlRow[], name: string): PcfControlRow | undefined =>
  rows.find((r) => lower(r.name) === lower(name));

/** The installed spelling of the control `name` (case ignored), or undefined when it is not installed. */
export const installedName = (rows: PcfControlRow[], name: string): string | undefined => pcfRow(rows, name)?.name;

/** Whether the cell's binding names, ignoring case, one of the controls that can replace its control ({@link pcfChoices}). */
export function pcfFits(rows: PcfControlRow[], cell: DesignCell, attr: AttributeMeta | undefined): boolean {
  const name = cell.pcf ? lower(cell.pcf.name) : undefined;
  return name !== undefined && pcfChoices(rows, cell, attr).some((c) => lower(c.name) === name);
}

/** A binding whose control is known and leaves required properties unset: what blocks Save (G5). */
export interface PcfMissing {
  cell: DesignCell;
  properties: string[];
}

/**
 * The bindings of the body and header cells that leave required properties unset, in form order. A binding that does not
 * fit its cell ({@link pcfFits}: a control that is not installed, whose manifest did not parse, or that does not take the
 * cell's column type) is left out: the designer cannot tell, and does not edit it.
 */
export function pcfMissing(d: FormDesign, rows: PcfControlRow[], attributes: AttributeMeta[]): PcfMissing[] {
  return allCells(d).flatMap((cell) => {
    const attr = cell.attribute ? attributes.find((a) => lower(a.logicalName) === lower(cell.attribute)) : undefined;
    const manifest = cell.pcf && pcfFits(rows, cell, attr) ? pcfRow(rows, cell.pcf.name)?.manifest : undefined;
    const properties = manifest ? missingRequired(cell.pcf!, manifest) : [];
    return properties.length > 0 ? [{ cell, properties }] : [];
  });
}

// ---------------------------------------------------------------------------------------------------------------------
// Writing

function create(doc: Document, tag: string, attributes: Record<string, string> = {}, text = ''): Element {
  const el = doc.createElementNS(null, tag);
  for (const [name, value] of Object.entries(attributes)) el.setAttribute(name, value);
  if (text !== '') el.textContent = text;
  return el;
}

/** Makes a parameter element hold `p`, changing only what differs. */
function setParameter(el: Element, p: CustomControlParameter) {
  if ((el.textContent ?? '').trim() !== p.value) el.textContent = p.value;
  if (p.static && !isTrue(el, 'static')) el.setAttribute('static', 'true');
  if (!p.static && isTrue(el, 'static')) el.removeAttribute('static');
  const type = el.getAttribute('type') || undefined;
  if (type !== p.type) {
    if (p.type) el.setAttribute('type', p.type);
    else el.removeAttribute('type');
  }
}

const newParameter = (doc: Document, name: string, p: CustomControlParameter) =>
  create(doc, name, p.static ? { static: 'true', ...(p.type ? { type: p.type } : {}) } : p.type ? { type: p.type } : {}, p.value);

/**
 * Makes a named `customControl` hold the binding: its `name`, its `<data-set>` and its parameters. A child that already
 * holds its value keeps its element, attributes and text; a missing one is added at the end; one the binding no longer
 * has is removed.
 */
function writeEntry(entry: Element, b: PcfBinding) {
  const doc = entry.ownerDocument;
  if ((entry.getAttribute('name') ?? '').trim() !== b.name) entry.setAttribute('name', b.name);
  let holder = childElements(entry, 'parameters')[0];
  if (!holder) {
    holder = doc.createElementNS(null, 'parameters');
    append(entry, holder);
  }
  const holders = childElements(entry, 'parameters');
  const sets = holders.flatMap((h) => childElements(h, 'data-set'));
  if (b.dataSet) {
    let ds = sets[0];
    if (!ds) {
      ds = create(doc, 'data-set', { name: b.dataSet.name });
      append(holder, ds);
    }
    if ((ds.getAttribute('name') ?? '').trim() !== b.dataSet.name) ds.setAttribute('name', b.dataSet.name);
    const children = Array.from(ds.children);
    for (const [name, value] of Object.entries(b.dataSet.parameters)) {
      const found = children.filter((c) => c.tagName === name).pop();
      if (!found) append(ds, create(doc, name, {}, value));
      else if ((found.textContent ?? '').trim() !== value) found.textContent = value;
    }
    for (const c of children) if (!Object.hasOwn(b.dataSet.parameters, c.tagName)) detach(c);
  } else {
    for (const ds of sets) detach(ds);
  }
  const children = holders.flatMap((h) => Array.from(h.children)).filter((c) => c.tagName !== 'data-set');
  for (const [name, p] of Object.entries(b.parameters)) {
    // The last one, as the runtime parser reads a repeated parameter.
    const found = children.filter((c) => c.tagName === name).pop();
    if (found) setParameter(found, p);
    else append(holder, newParameter(doc, name, p));
  }
  for (const c of children) if (!Object.hasOwn(b.parameters, c.tagName)) detach(c);
}

/** A new `<controlDescription>` for `control` (G5): the classic entry, then the entries of form factors 0, 1 and 2. */
function newDescription(doc: Document, forControl: string, control: Element, cell: DesignCell, b: PcfBinding): Element {
  const description = create(doc, 'controlDescription', { forControl });
  const classic = create(doc, 'customControl', { id: control.getAttribute('classid') ?? '' });
  const params = childElements(control, 'parameters')[0];
  if (cell.control?.kind === 'subgrid') {
    classic.appendChild(params ? params.cloneNode(true) : doc.createElementNS(null, 'parameters'));
  } else {
    const holder = doc.createElementNS(null, 'parameters');
    holder.appendChild(create(doc, 'datafieldname', {}, control.getAttribute('datafieldname') ?? cell.attribute ?? ''));
    classic.appendChild(holder);
  }
  description.appendChild(classic);
  for (const n of ['0', '1', '2']) {
    const entry = create(doc, 'customControl', { formFactor: n, name: b.name });
    writeEntry(entry, b);
    description.appendChild(entry);
  }
  return description;
}

/** A written cell: its design, its element in the written document and, for a cell of the source, its design as read. */
export interface WrittenCell {
  cell: DesignCell;
  el: Element;
  source?: DesignCell;
}

/**
 * Writes the cells' bindings into the `<form>` element `form` (a copy of the source, its cells already written):
 * - a binding unchanged keeps its `<controlDescription>` as it is, but a subgrid edit follows into its classic entry;
 * - an edited binding changes the web entry, and the phone and tablet entries too unless `ownFactors`;
 * - a new binding writes a new `<controlDescription>` for the control's `uniqueid` (replacing one the designer could not
 *   model): the design gives the cell its `uniqueId` ({@link setPcf}), which the cell's write puts on the control;
 * - a removed binding, and a control of the source (`sourceIds`, its `uniqueid`s in lower case) that is no longer in the
 *   form, lose their `<controlDescription>`. A `<controlDescriptions>` this empties is removed.
 */
export function writePcfs(form: Element, written: WrittenCell[], sourceIds: Set<string>) {
  const doc = form.ownerDocument;
  const containers = new Set<Element>();
  const remove = (description: Element) => {
    if (description.parentNode) containers.add(description.parentNode as Element);
    detach(description);
  };
  const removeAll = (id: string) => {
    for (const d of allDescriptions(form)) if (lower(d.getAttribute('forControl')) === id) remove(d);
  };
  const byId = descriptionsOf(form);
  for (const { cell, el, source } of written) {
    const control = childElements(el, 'control')[0];
    if (!control) continue;
    const before = source?.pcf;
    const id = lower(control.getAttribute('uniqueid'));
    const description = id ? byId.get(id) : undefined;
    if (!cell.pcf) {
      // Every description for the control: an earlier duplicate would bring the binding back.
      if (before && description) removeAll(id);
      continue;
    }
    if (before && description) {
      if (!same(cell.pcf, before)) {
        const chosen = chosenCustomControl(description)!;
        const entries = before.ownFactors ? [chosen] : [chosen, factor(description, '1'), factor(description, '2')];
        for (const entry of entries) if (entry) writeEntry(entry, cell.pcf);
      }
      const spec = cell.control;
      const was = source?.control;
      if (spec?.kind === 'subgrid' && was?.kind === 'subgrid' && JSON.stringify(spec) !== JSON.stringify(was)) {
        const classic = childElements(description, 'customControl').find((c) => (c.getAttribute('name') ?? '').trim() === '');
        if (classic) writeControl(doc, classic, spec, was);
      }
      continue;
    }
    const forControl = control.getAttribute('uniqueid')?.trim() ?? '';
    // Invariant guard, not reachable from the UI: setPcf always assigns a uniqueId and source bindings are keyed by their uniqueid.
    if (forControl === '') throw new Error('A bound control has no uniqueid.');
    if (description) removeAll(id);
    let holder = childElements(form, 'controlDescriptions')[0];
    if (!holder) {
      holder = doc.createElementNS(null, 'controlDescriptions');
      insertBlock(form, holder, childElements(form, 'DisplayConditions')[0] ?? null);
    }
    append(holder, newDescription(doc, forControl, control, cell, cell.pcf));
  }
  const present = new Set(Array.from(form.getElementsByTagName('control')).map((c) => lower(c.getAttribute('uniqueid'))));
  for (const description of allDescriptions(form)) {
    const id = lower(description.getAttribute('forControl'));
    if (sourceIds.has(id) && !present.has(id)) remove(description);
  }
  for (const c of containers) if (childElements(c, 'controlDescription').length === 0) detach(c);
}
