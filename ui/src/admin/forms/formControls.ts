import { CLASS_ID, controlKind } from '../../parsers/controlClass';
import { controlParam, quickForms, scrollingOf } from '../../parsers/formXml';
import { childElements } from '../../parsers/xml';
import type { SubgridFilter } from '../../apps/record/subgridFilters';
import { allCells, bodySections, mapCell } from './formCells';
import type { DesignCell, DesignSection, FormDesign } from './formDesign';
import { newGuid } from './formIds';
import { append } from './xmlIndent';

/** A subgrid control (G2). `relationship` is empty for "All records" and for records matching a form column. */
export interface SubgridSpec {
  kind: 'subgrid';
  controlId: string;
  relationship: string;
  targetEntity: string;
  viewId: string;
  recordsPerPage: number;
  /** `EnableViewPicker`: "Show view selector". */
  viewPicker: boolean;
  /**
   * Records matching a column of the form (X.11): an xrmverse extension held in the design only, never in the FormXML
   * (the relationship is then empty). {@link readControl} never sets it.
   */
  filter?: SubgridFilter;
}

/** A quick view control (G3). */
export interface QuickViewSpec {
  kind: 'quickview';
  controlId: string;
  /** The lookup column (`datafieldname`). */
  lookup: string;
  /** Target table logical name to quick view form id, in the order they are written. */
  forms: Record<string, string>;
}

/** A web resource control (G4); IFRAMEs are not modeled (F12). */
export interface WebResourceSpec {
  kind: 'webresource';
  controlId: string;
  /** `<Url>`: the web resource name. */
  url: string;
  /** `<WebResourceId>`, as written (`{id}`). */
  webResourceId: string;
  /** `<Data>`: the custom parameter. */
  data: string;
  passParameters: boolean;
  /** `<Security>`: restrict cross-frame scripting. */
  restrictScripting: boolean;
  scrolling: 'auto' | 'yes' | 'no';
  border: boolean;
}

export type ControlSpec = SubgridSpec | QuickViewSpec | WebResourceSpec;

const isTrue = (v: string | undefined) => v?.toLowerCase() === 'true';

/** The most rows a subgrid shows per page (G2). */
export const MAX_RECORDS_PER_PAGE = 250;

/** The most rows a new subgrid cell spans: the read cap of `formDesign.ts`. */
const MAX_SUBGRID_ROWSPAN = 100;

/** `n` as a whole number in 1–250; 4 when it is not a number. */
export function clampRecordsPerPage(n: number): number {
  return Number.isFinite(n) ? Math.min(Math.max(Math.round(n), 1), MAX_RECORDS_PER_PAGE) : 4;
}

/** Whether `text`, as typed, is a records-per-page value: a whole number in 1–250. */
export const validPerPage = (text: string): boolean =>
  /^\d+$/.test(text.trim()) && Number(text) >= 1 && Number(text) <= MAX_RECORDS_PER_PAGE;

/** The spec with its keys in one order, so that two equal specs serialize alike (the design's no-change check). */
function canonical(spec: ControlSpec): ControlSpec {
  switch (spec.kind) {
    case 'subgrid': {
      const { kind, controlId, relationship, targetEntity, viewId, recordsPerPage, viewPicker, filter } = spec;
      const base: SubgridSpec = { kind, controlId, relationship, targetEntity, viewId, recordsPerPage, viewPicker };
      return filter ? { ...base, filter: { source: filter.source, target: filter.target } } : base;
    }
    case 'quickview': {
      const { kind, controlId, lookup, forms } = spec;
      return { kind, controlId, lookup, forms: { ...forms } };
    }
    case 'webresource': {
      const { kind, controlId, url, webResourceId, data, passParameters, restrictScripting, scrolling, border } = spec;
      return { kind, controlId, url, webResourceId, data, passParameters, restrictScripting, scrolling, border };
    }
  }
}

/**
 * The spec of a subgrid, quick view or web resource `<control>`; undefined for an IFRAME, the timeline, a field and an
 * unknown control. Values are read as the runtime parser reads them (`parseFormXml`).
 */
export function readControl(el: Element): ControlSpec | undefined {
  const classId = el.getAttribute('classid') ?? '';
  const controlId = el.getAttribute('id') ?? '';
  switch (controlKind(classId)) {
    case 'subgrid': {
      const perPage = Number(controlParam(el, 'RecordsPerPage'));
      return {
        kind: 'subgrid',
        controlId,
        relationship: controlParam(el, 'RelationshipName') ?? '',
        targetEntity: controlParam(el, 'TargetEntityType') ?? '',
        viewId: controlParam(el, 'ViewId') ?? '',
        recordsPerPage: Number.isInteger(perPage) && perPage > 0 ? perPage : 4,
        viewPicker: isTrue(controlParam(el, 'EnableViewPicker')),
      };
    }
    case 'quickview': {
      let forms: Record<string, string>;
      try {
        forms = quickForms(el);
      } catch {
        // Malformed escaped QuickForms text: the cell stays a control the designer does not touch.
        return undefined;
      }
      return { kind: 'quickview', controlId, lookup: el.getAttribute('datafieldname') ?? '', forms };
    }
    case 'webresource': {
      if (classId.trim().toUpperCase() === CLASS_ID.iframe) return undefined;
      return {
        kind: 'webresource',
        controlId,
        url: controlParam(el, 'Url') ?? '',
        webResourceId: controlParam(el, 'WebResourceId') ?? '',
        data: controlParam(el, 'Data') ?? '',
        passParameters: isTrue(controlParam(el, 'PassParameters')),
        restrictScripting: isTrue(controlParam(el, 'Security')),
        scrolling: scrollingOf(controlParam(el, 'Scrolling')),
        border: isTrue(controlParam(el, 'Border')),
      };
    }
    default:
      return undefined;
  }
}

const escapeXml = (s: string) =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

/** The `QuickForms` text: XML, which the `<QuickForms>` parameter holds escaped (as Dataverse writes it). */
function quickFormsText(forms: Record<string, string>): string {
  const ids = Object.entries(forms)
    .map(([entity, id]) => `<QuickFormId entityname="${escapeXml(entity)}">${escapeXml(id)}</QuickFormId>`).join('');
  return `<QuickForms><QuickFormIds>${ids}</QuickFormIds></QuickForms>`;
}

/** `{id}`, with the braces added when missing; empty for an empty id. */
const braced = (id: string) => (id.trim() === '' ? '' : `{${id.trim().replace(/[{}]/g, '')}}`);

/** The `<parameters>` children of the spec, in the order a new control writes them (G2–G4). */
function parameters(spec: ControlSpec): [string, string][] {
  switch (spec.kind) {
    case 'subgrid':
      return [
        ['ViewId', spec.viewId],
        ['IsUserView', 'false'],
        ['RelationshipName', spec.filter ? '' : spec.relationship],
        ['TargetEntityType', spec.targetEntity],
        ['AutoExpand', 'Fixed'],
        ['EnableQuickFind', 'false'],
        ['EnableViewPicker', String(spec.viewPicker)],
        ['EnableJumpBar', 'false'],
        ['ChartGridMode', 'Grid'],
        ['VisualizationId', ''],
        ['IsUserChart', 'false'],
        ['EnableChartPicker', 'false'],
        ['RecordsPerPage', String(spec.recordsPerPage)],
      ];
    case 'quickview':
      return [['QuickForms', quickFormsText(spec.forms)]];
    case 'webresource':
      return [
        ['Url', spec.url],
        ['PassParameters', String(spec.passParameters)],
        ['Security', String(spec.restrictScripting)],
        ['Scrolling', spec.scrolling],
        ['Border', String(spec.border)],
        ['WebResourceId', braced(spec.webResourceId)],
        ['Data', spec.data],
      ];
  }
}

const CLASS: Record<ControlSpec['kind'], string> = {
  subgrid: CLASS_ID.subgrid,
  quickview: CLASS_ID.quickView,
  webresource: CLASS_ID.webResource,
};

function setAttribute(el: Element, name: string, value: string) {
  if (el.getAttribute(name) !== value) el.setAttribute(name, value);
}

/**
 * Writes `spec` as a `<control>`. Without `el` it creates the control of G2–G4. With `el` it changes only what differs
 * from `before` (the spec `el` was read as): the `id`, a quick view's `datafieldname`, and each `<parameters>` child whose
 * value differs, created at the end of `<parameters>` when missing. Other children keep their element, order and text.
 */
export function writeControl(doc: Document, el: Element | undefined, spec: ControlSpec, before?: ControlSpec): Element {
  let control = el;
  if (!control) {
    control = doc.createElement('control');
    control.setAttribute('id', spec.controlId);
    control.setAttribute('classid', CLASS[spec.kind]);
    if (spec.kind === 'subgrid') control.setAttribute('indicationOfSubgrid', 'true');
    if (spec.kind === 'quickview') control.setAttribute('datafieldname', spec.lookup);
  } else if (before) {
    if (before.controlId !== spec.controlId) setAttribute(control, 'id', spec.controlId);
    if (spec.kind === 'quickview' && (before.kind !== 'quickview' || before.lookup !== spec.lookup)) {
      setAttribute(control, 'datafieldname', spec.lookup);
    }
  }
  const old = new Map(before && before.kind === spec.kind ? parameters(before) : []);
  const changes = parameters(spec).filter(([name, value]) => !old.has(name) || old.get(name) !== value);
  if (changes.length === 0) return control;
  let holder = childElements(control, 'parameters')[0];
  if (!holder) {
    holder = doc.createElement('parameters');
    append(control, holder);
  }
  for (const [name, value] of changes) {
    const found = childElements(control, 'parameters').map((p) => childElements(p, name)[0]).find((p) => p);
    if (found) {
      if (found.textContent !== value) found.textContent = value;
      continue;
    }
    const p = doc.createElement(name);
    if (value !== '') p.textContent = value;
    append(holder, p);
  }
  return control;
}

// ---------------------------------------------------------------------------------------------------------------------
// Operations: each returns a new design, or `d` itself when nothing changes.

/** The control ids (lower case) of the form: those of the FormXML and those of the design's cells. */
function usedControlIds(d: FormDesign): Set<string> {
  const ids = Array.from(d.source.getElementsByTagName('control')).map((c) => c.getAttribute('id') ?? '');
  const header = new Set(d.header);
  for (const c of allCells(d)) {
    if (c.control) ids.push(c.control.controlId);
    if (c.kind === 'field' && c.attribute) ids.push(header.has(c) ? `header_${c.attribute}` : c.attribute);
  }
  return new Set(ids.map((id) => id.toLowerCase()));
}

/** `base`, or `base_{n}` with the smallest n from 2 that no control of the form uses (ignoring case). */
export function uniqueControlId(d: FormDesign, base: string): string {
  const used = usedControlIds(d);
  if (!used.has(base.toLowerCase())) return base;
  let n = 2;
  while (used.has(`${base}_${n}`.toLowerCase())) n += 1;
  return `${base}_${n}`;
}

/** `WebResource_{name}`: the web resource name without its path and publisher prefix, letters and digits only (G4). */
export function webResourceControlId(name: string): string {
  const last = name.split('/').filter((p) => p !== '').pop() ?? '';
  const bare = name.includes('/') ? last : last.replace(/^[^_]*_/, '');
  return `WebResource_${bare.replace(/[^A-Za-z0-9]/g, '')}`;
}

function addControl(
  d: FormDesign, sectionKey: string, spec: ControlSpec, base: string, label: string,
  span: (s: DesignSection) => Pick<DesignCell, 'colSpan' | 'rowSpan'>, extra: Pick<DesignCell, 'uniqueId'> = {},
): FormDesign {
  const target = bodySections(d.tabs).find((s) => s.key === sectionKey);
  if (!target) return d;
  const id = newGuid();
  const cell: DesignCell = {
    key: id, id, kind: spec.kind, labels: { [d.lcid]: label }, showLabel: true, visible: true, disabled: false,
    ...span(target), control: canonical({ ...spec, controlId: uniqueControlId(d, spec.controlId.trim() || base) }), ...extra,
  };
  return {
    ...d,
    tabs: d.tabs.map((t) => ({
      ...t,
      columns: t.columns.map((c) => ({
        ...c, sections: c.sections.map((s) => (s === target ? { ...s, cells: [...s.cells, cell] } : s)),
      })),
    })),
  };
}

/**
 * Adds a subgrid in a new row at the end of the section (G2): its control id is the target table's name (or
 * `spec.controlId`) made unique; the cell spans the section's columns and `recordsPerPage` rows (at most 100), with no
 * padding rows after it. `recordsPerPage` is clamped to 1–250. `d` itself when `targetEntity` is empty.
 */
export function addSubgrid(d: FormDesign, sectionKey: string, spec: SubgridSpec, label: string): FormDesign {
  if (spec.targetEntity.trim() === '') return d;
  const perPage = clampRecordsPerPage(spec.recordsPerPage);
  return addControl(d, sectionKey, { ...spec, recordsPerPage: perPage }, spec.targetEntity, label,
    (s) => ({ colSpan: s.columns, rowSpan: Math.min(perPage, MAX_SUBGRID_ROWSPAN) }));
}

/**
 * Adds a quick view at the end of the section (G3); its control id is `{lookup}_quickview` (or `spec.controlId`) made
 * unique, and its cell holds a new `uniqueid`. `d` itself when `lookup` is empty.
 */
export function addQuickView(d: FormDesign, sectionKey: string, spec: QuickViewSpec, label: string): FormDesign {
  if (spec.lookup.trim() === '') return d;
  return addControl(d, sectionKey, spec, `${spec.lookup}_quickview`, label, () => ({ colSpan: 1, rowSpan: 1 }), { uniqueId: newGuid() });
}

/** Adds a web resource at the end of the section (G4); its control id is {@link webResourceControlId} made unique. */
export function addWebResource(d: FormDesign, sectionKey: string, spec: WebResourceSpec, label: string): FormDesign {
  return addControl(d, sectionKey, spec, webResourceControlId(spec.url), label, () => ({ colSpan: 1, rowSpan: 1 }));
}

/**
 * Configures the control of a subgrid, quick view or web resource cell (body or header). What the designer does not
 * offer to change is kept from the current spec and a different value is ignored: the control id, a subgrid's
 * relationship and target table (G2: delete and add instead), and a quick view's lookup; the `filter` (X.11) of a subgrid
 * without a relationship may be set or removed, and one with a relationship never gets one. The cell's spans are kept.
 * `recordsPerPage` is clamped to 1–250 (4 when it is not a number).
 * `d` itself for another kind, a cell without a spec (IFRAME, timeline) or no change.
 */
export function setControl(d: FormDesign, cellKey: string, spec: ControlSpec): FormDesign {
  const old = allCells(d).find((c) => c.key === cellKey)?.control;
  if (!old || old.kind !== spec.kind) return d;
  const sub = spec as SubgridSpec;
  const kept: ControlSpec = old.kind === 'subgrid'
    ? {
      ...sub, controlId: old.controlId, relationship: old.relationship, targetEntity: old.targetEntity,
      // A subgrid with a relationship keeps it (X.11): a filter would empty its RelationshipName.
      ...(old.relationship ? { filter: undefined } : {}),
      // A value read from the source and left as it is stays, even out of range.
      recordsPerPage: sub.recordsPerPage === old.recordsPerPage ? old.recordsPerPage : clampRecordsPerPage(sub.recordsPerPage),
    }
    : old.kind === 'quickview'
      ? { ...(spec as QuickViewSpec), controlId: old.controlId, lookup: old.lookup }
      : { ...spec, controlId: old.controlId };
  const next = canonical(kept);
  if (JSON.stringify(next) === JSON.stringify(old)) return d;
  return mapCell(d, cellKey, (c) => withDataSet({ ...c, control: next }, old));
}

/** The values of a subgrid that a dataset PCF control's `<data-set>` holds (G5), in the order a new binding writes them. */
export function subgridDataSet(spec: SubgridSpec): Record<string, string> {
  return {
    ViewId: spec.viewId,
    IsUserView: 'false',
    RelationshipName: spec.filter ? '' : spec.relationship,
    TargetEntityType: spec.targetEntity,
    EnableViewPicker: String(spec.viewPicker),
    RecordsPerPage: String(spec.recordsPerPage),
  };
}

/** The cell with the subgrid values that changed from `old` put in its PCF binding's `<data-set>`, so the binding follows. */
function withDataSet(c: DesignCell, old: ControlSpec): DesignCell {
  const ds = c.pcf?.dataSet;
  if (!ds || c.control?.kind !== 'subgrid' || old.kind !== 'subgrid') return c;
  const before = subgridDataSet(old);
  const changes = Object.entries(subgridDataSet(c.control)).filter(([k, v]) => before[k] !== v);
  if (changes.length === 0) return c;
  return { ...c, pcf: { ...c.pcf!, dataSet: { ...ds, parameters: { ...ds.parameters, ...Object.fromEntries(changes) } } } };
}
