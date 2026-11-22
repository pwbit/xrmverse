import type {
  Cell,
  Control,
  CustomControlParameter,
  CustomControlDataSet,
  DataSetControlRef,
  FormEvent,
  FormModel,
  Handler,
  Labels,
  NavItem,
  QuickViewControl,
  Section,
  SubgridControl,
  Tab,
  WebResourceControl,
} from '../model/form';
import { localized } from '../model/sitemap';
import { CLASS_ID, controlKind } from './controlClass';
import { childElements, parseXml } from './xml';

/** A role or form id as compared: no braces, trimmed, lower case. */
export const normaliseId = (id: string): string => id.replace(/[{}]/g, '').trim().toLowerCase();

/** The columns of a `systemform` row that the parser reads. */
export interface SystemFormRow {
  systemformid: string;
  name: string;
  objecttypecode: string;
  type: number;
  isdefault?: boolean | null;
  formxml: string;
}

/**
 * The label for `lcid`, else 1033, else undefined (the caller falls back to the attribute's display
 * label). UI-1a's `localized` applied to a `<labels>` map, without its first-label rule.
 */
export function labelFor(labels: Labels, lcid: number): string | undefined {
  if (labels[lcid] === undefined && labels[1033] === undefined) return undefined;
  return localized(labels, lcid, '');
}

const flag = (el: Element, name: string, fallback: boolean): boolean => {
  const v = el.getAttribute(name)?.trim().toLowerCase();
  return v === undefined ? fallback : v === 'true' || v === '1';
};

const count = (el: Element, name: string): number => {
  const n = Number(el.getAttribute(name));
  return Number.isInteger(n) && n > 0 ? n : 1;
};

function labelsOf(el: Element): Labels {
  const out: Labels = {};
  for (const holder of childElements(el, 'labels')) {
    for (const l of childElements(holder, 'label')) {
      const raw = l.getAttribute('languagecode')?.trim() ?? '';
      const text = l.getAttribute('description');
      if (/^\d+$/.test(raw) && text !== null) out[Number(raw)] = text;
    }
  }
  return out;
}

/** The text of a control's `<parameters><name>`, trimmed; undefined when there is none. */
export function controlParam(control: Element, name: string): string | undefined {
  for (const params of childElements(control, 'parameters')) {
    const p = childElements(params, name)[0];
    if (p) return p.textContent?.trim() ?? '';
  }
  return undefined;
}

/**
 * A web resource's `<Scrolling>` value: Learn names the three behaviours (as necessary, always, never); the values are as
 * FormXml writes them (ruling R-T6-1). Anything else is `auto`.
 */
export function scrollingOf(raw: string | undefined): 'auto' | 'yes' | 'no' {
  const v = (raw ?? '').toLowerCase();
  return v === 'yes' || v === 'always' ? 'yes' : v === 'no' || v === 'never' ? 'no' : 'auto';
}

/**
 * `QuickForms` holds XML as escaped text; an unescaped nested element is accepted too.
 * Returns target table to quick view form id.
 */
export function quickForms(control: Element): Record<string, string> {
  let root: Element | undefined;
  for (const params of childElements(control, 'parameters')) {
    const p = childElements(params, 'QuickForms')[0];
    if (!p) continue;
    root = childElements(p, 'QuickForms')[0];
    if (!root) {
      const text = p.textContent?.trim();
      if (text) root = parseXml(text).documentElement;
    }
  }
  const forms: Record<string, string> = {};
  if (!root) return forms;
  for (const ids of childElements(root, 'QuickFormIds')) {
    for (const q of childElements(ids, 'QuickFormId')) {
      const entity = q.getAttribute('entityname');
      const id = q.textContent?.trim();
      if (entity && id) forms[entity] = id;
    }
  }
  return forms;
}

/** The PCF control that replaces the control `el`, by its `uniqueid`. */
function pcfOf(el: Element, pcf: PcfControls): DataSetControlRef | undefined {
  const uniqueId = (el.getAttribute('uniqueid') ?? '').toLowerCase();
  return uniqueId === '' ? undefined : pcf.get(uniqueId);
}

function controlOf(el: Element, pcf: PcfControls): Control {
  const id = el.getAttribute('id') ?? '';
  const classId = el.getAttribute('classid') ?? '';
  const attribute = el.getAttribute('datafieldname') ?? '';
  switch (controlKind(classId)) {
    case 'subgrid': {
      const perPage = Number(controlParam(el, 'RecordsPerPage'));
      const sub: SubgridControl = {
        kind: 'subgrid',
        id,
        relationship: controlParam(el, 'RelationshipName') ?? '',
        targetEntity: controlParam(el, 'TargetEntityType') ?? '',
        viewId: controlParam(el, 'ViewId') ?? '',
        recordsPerPage: Number.isInteger(perPage) && perPage > 0 ? perPage : 4,
        enableViewPicker: controlParam(el, 'EnableViewPicker')?.toLowerCase() === 'true',
      };
      const mode = controlParam(el, 'ChartGridMode')?.toLowerCase();
      if (mode === 'chart') sub.chartGridMode = 'Chart';
      else if (mode === 'grid') sub.chartGridMode = 'Grid';
      const visualization = controlParam(el, 'VisualizationId');
      if (visualization) sub.visualizationId = visualization;
      if (controlParam(el, 'EnableChartPicker')?.toLowerCase() === 'true' || sub.enableViewPicker) sub.pickers = true;
      const custom = pcfOf(el, pcf);
      if (custom) sub.customControl = custom;
      return sub;
    }
    case 'quickview': {
      const q: QuickViewControl = {
        kind: 'quickview',
        id,
        lookupAttribute: attribute,
        forms: quickForms(el),
      };
      return q;
    }
    case 'timeline': {
      // Not read yet (spec §6.6): only their names, so that the timeline can say it ignores them.
      const parameterNames = childElements(el, 'parameters').flatMap((p) => Array.from(p.children).map((c) => c.tagName));
      return { kind: 'timeline', id, classId, parameterNames };
    }
    case 'webresource': {
      const web: WebResourceControl = {
        kind: 'webresource',
        id,
        classId,
        iframe: classId.trim().toUpperCase() === CLASS_ID.iframe,
        url: controlParam(el, 'Url') ?? '',
        data: controlParam(el, 'Data') ?? '',
        passParameters: controlParam(el, 'PassParameters')?.toLowerCase() === 'true',
        restrictScripting: controlParam(el, 'Security')?.toLowerCase() === 'true',
        scrolling: scrollingOf(controlParam(el, 'Scrolling')),
        border: controlParam(el, 'Border')?.toLowerCase() === 'true',
      };
      return web;
    }
    default: {
      if (!attribute) return { kind: 'unknown', id, classId };
      const custom = pcfOf(el, pcf);
      return {
        kind: 'field',
        id,
        attribute,
        disabled: flag(el, 'disabled', false),
        ...(custom ? { customControl: custom } : {}),
      };
    }
  }
}

function cellOf(el: Element, pcf: PcfControls): Cell {
  const cell: Cell = {
    id: el.getAttribute('id') ?? '',
    labels: labelsOf(el),
    showLabel: flag(el, 'showlabel', true),
    visible: flag(el, 'visible', true),
    colSpan: count(el, 'colspan'),
    rowSpan: count(el, 'rowspan'),
  };
  const control = childElements(el, 'control')[0];
  if (control) cell.control = controlOf(control, pcf);
  return cell;
}

function rowsOf(parent: Element, pcf: PcfControls): Cell[][] {
  return childElements(parent, 'rows').flatMap((rows) =>
    childElements(rows, 'row').map((row) => childElements(row, 'cell').map((c) => cellOf(c, pcf))),
  );
}

/**
 * A section's `columns`: a single digit is the count of equal columns (`"2"`); several digits are one column each, the digit
 * its relative width (`"111"`, `"1111"`, `"12"`), as dashboards and many main forms write it.
 */
function sectionColumns(value: string | null): Pick<Section, 'columns' | 'widths'> {
  const text = value?.trim() ?? '';
  if (/^[1-9]{2,}$/.test(text)) return { columns: text.length, widths: [...text].map(Number) };
  // Several digits with a 0 ("10") are neither a count FormXml writes nor a set of widths: one column.
  if (/^\d{2,}$/.test(text)) return { columns: 1 };
  const columns = Number(text);
  return { columns: Number.isInteger(columns) && columns > 0 ? columns : 1 };
}

function sectionOf(el: Element, pcf: PcfControls): Section {
  return {
    name: el.getAttribute('name') ?? '',
    labels: labelsOf(el),
    showLabel: flag(el, 'showlabel', true),
    visible: flag(el, 'visible', true),
    ...sectionColumns(el.getAttribute('columns')),
    labelPosition: el.getAttribute('celllabelposition')?.toLowerCase() === 'top' ? 'top' : 'left',
    rows: rowsOf(el, pcf),
  };
}

function tabOf(el: Element, pcf: PcfControls): Tab {
  return {
    name: el.getAttribute('name') ?? '',
    labels: labelsOf(el),
    visible: flag(el, 'visible', true),
    columns: childElements(el, 'columns').flatMap((cols) =>
      childElements(cols, 'column').map((col) => ({
        width: col.getAttribute('width') ?? '',
        sections: childElements(col, 'sections').flatMap((s) =>
          childElements(s, 'section').map((sec) => sectionOf(sec, pcf)),
        ),
      })),
    ),
  };
}

/** By control `uniqueid` (lower case): the PCF control that replaces it. */
type PcfControls = Map<string, DataSetControlRef>;

const DATA_SET = 'data-set';

/** The `<parameters>` children of a `customControl`, but its `<data-set>` (see {@link dataSetOf}). */
export function parametersOf(custom: Element): Record<string, CustomControlParameter> {
  const out: Record<string, CustomControlParameter> = {};
  for (const params of childElements(custom, 'parameters')) {
    for (const p of Array.from(params.children)) {
      if (p.tagName === DATA_SET) continue;
      const parameter: CustomControlParameter = { value: p.textContent?.trim() ?? '', static: flag(p, 'static', false) };
      const type = p.getAttribute('type');
      if (type) parameter.type = type;
      out[p.tagName] = parameter;
    }
  }
  return out;
}

/** The first `<data-set name>` of a `customControl`'s `<parameters>`, its children as trimmed text. */
export function dataSetOf(custom: Element): CustomControlDataSet | undefined {
  for (const params of childElements(custom, 'parameters')) {
    const ds = childElements(params, DATA_SET)[0];
    if (!ds) continue;
    const parameters: Record<string, string> = {};
    for (const p of Array.from(ds.children)) parameters[p.tagName] = p.textContent?.trim() ?? '';
    return { name: ds.getAttribute('name')?.trim() ?? '', parameters };
  }
  return undefined;
}

/**
 * The `customControl` of a `<controlDescription>` the runtime uses: of those with a `name`, the one for the web form factor
 * (`formFactor="0"`), else one without `formFactor`; undefined when there is neither.
 */
export function chosenCustomControl(description: Element): Element | undefined {
  const named = childElements(description, 'customControl').filter((c) => (c.getAttribute('name') ?? '').trim() !== '');
  return named.find((c) => c.getAttribute('formFactor')?.trim() === '0') ?? named.find((c) => !c.hasAttribute('formFactor'));
}

/**
 * The PCF controls of `<controlDescriptions>`: of the `customControl`s with a `name`, the one for the web form factor
 * (`formFactor="0"`), else one without `formFactor`. A description with neither keeps the standard control (its
 * `customControl` without `name` is the classic control).
 */
function pcfControls(form: Element): PcfControls {
  const out: PcfControls = new Map();
  for (const descs of childElements(form, 'controlDescriptions')) {
    for (const d of childElements(descs, 'controlDescription')) {
      const forControl = d.getAttribute('forControl')?.trim().toLowerCase();
      if (!forControl) continue;
      const chosen = chosenCustomControl(d);
      if (!chosen) continue;
      const ref: DataSetControlRef = { name: chosen.getAttribute('name')!.trim(), parameters: parametersOf(chosen) };
      const dataSet = dataSetOf(chosen);
      if (dataSet) ref.dataSet = dataSet;
      out.set(forControl, ref);
    }
  }
  return out;
}

const EVENT_NAMES = ['onload', 'onsave', 'onchange'] as const;

function handlerOf(el: Element): Handler {
  return {
    functionName: el.getAttribute('functionName') ?? '',
    libraryName: el.getAttribute('libraryName') ?? '',
    enabled: flag(el, 'enabled', true),
    parameters: el.getAttribute('parameters') ?? '',
    passExecutionContext: flag(el, 'passExecutionContext', false),
  };
}

/** The OnLoad, OnSave and OnChange events of `<events>`; other events are not raised by this shell. */
function eventsOf(form: Element): FormEvent[] {
  return childElements(form, 'events').flatMap((events) =>
    childElements(events, 'event').flatMap((e) => {
      const name = e.getAttribute('name')?.trim().toLowerCase();
      const known = EVENT_NAMES.find((n) => n === name);
      if (!known) return [];
      const attribute = e.getAttribute('attribute')?.trim();
      return [
        {
          name: known,
          ...(attribute ? { attribute } : {}),
          handlers: childElements(e, 'Handlers').flatMap((hs) => childElements(hs, 'Handler').map(handlerOf)),
        },
      ];
    }),
  );
}

export function parseFormXml(row: SystemFormRow): FormModel {
  const form = parseXml(row.formxml).documentElement;
  const pcf = pcfControls(form);
  const display = childElements(form, 'DisplayConditions')[0];
  const order = Number(display?.getAttribute('Order'));
  const strip = (name: string): Cell[] =>
    childElements(form, name).flatMap((el) => rowsOf(el, pcf).flat());
  return {
    id: row.systemformid,
    name: row.name,
    entity: row.objecttypecode,
    type: row.type,
    isDefault: row.isdefault === true,
    order: display?.hasAttribute('Order') && Number.isFinite(order) ? order : Number.MAX_SAFE_INTEGER,
    roles: display
      ? childElements(display, 'Role')
          .map((r) => normaliseId(r.getAttribute('Id') ?? ''))
          .filter((id) => id !== '')
      : [],
    fallback: display?.getAttribute('FallbackForm')?.toLowerCase() === 'true',
    tabs: childElements(form, 'tabs').flatMap((tabs) =>
      childElements(tabs, 'tab').map((t) => tabOf(t, pcf)),
    ),
    header: strip('header'),
    footer: strip('footer'),
    hasScripts:
      childElements(form, 'formLibraries').some((l) => l.children.length > 0) ||
      childElements(form, 'events').some((e) => e.children.length > 0),
    libraries: childElements(form, 'formLibraries').flatMap((l) =>
      childElements(l, 'Library').flatMap((lib) => lib.getAttribute('name') ?? []),
    ),
    events: eventsOf(form),
    navigation: navigationOf(form),
  };
}

/** `<Title LCID Text>` children of `<Titles>`: the labels of a navigation item. */
function titlesOf(el: Element): Labels {
  const out: Labels = {};
  for (const holder of childElements(el, 'Titles')) {
    for (const title of childElements(holder, 'Title')) {
      const lcid = title.getAttribute('LCID')?.trim() ?? '';
      const text = title.getAttribute('Text');
      if (/^\d+$/.test(lcid) && text !== null) out[Number(lcid)] = text;
    }
  }
  return out;
}

/** The items of `<Navigation><NavBar>`, ordered by `Sequence` (document order when absent), without `Show="false"`. */
function navigationOf(form: Element): NavItem[] {
  const items = childElements(form, 'Navigation')
    .flatMap((nav) => childElements(nav, 'NavBar'))
    .flatMap((bar) => [...bar.children])
    .filter((el) => el.getAttribute('Show')?.toLowerCase() !== 'false')
    .flatMap((el, index): { item: NavItem; sequence: number; index: number }[] => {
      const id = el.getAttribute('Id') ?? `nav_${index}`;
      const sequence = Number(el.getAttribute('Sequence'));
      const at = { sequence: Number.isFinite(sequence) && el.hasAttribute('Sequence') ? sequence : Number.MAX_SAFE_INTEGER, index };
      if (el.tagName === 'NavBarByRelationshipItem') {
        const relationship = el.getAttribute('RelationshipName') ?? '';
        return [{ item: { kind: 'relationship', id, relationship, labels: titlesOf(el) }, ...at }];
      }
      if (el.tagName === 'NavBarItem') return [{ item: { kind: 'unsupported', id, labels: titlesOf(el) }, ...at }];
      return [];
    });
  return items.sort((a, b) => a.sequence - b.sequence || a.index - b.index).map((i) => i.item);
}
