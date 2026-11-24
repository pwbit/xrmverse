import { parseSubgridFilterNames, parseSubgridFilters, type SubgridFilter } from '../../apps/record/subgridFilters';
import { normaliseId } from '../../parsers/formXml';
import type { XrmApi } from '../../shell/api/client';
import { XrmError } from '../../shell/api/errors';
import { t } from '../../shell/i18n/t';
import { loadDefaultPrefix } from '../tables/tableApi';
import { decodeContent, encodeContent } from '../views/overlayApi';
import { allCells } from './formCells';
import type { DesignCell, FormDesign } from './formDesign';

/** The setting naming the subgrid filter resources (X.11, an xrmverse extension). */
export const SUBGRID_FILTERS = 'xrmverse_SubgridFilters';
/** The path of the resource the designer creates, after the Default publisher's prefix and `_`. */
const RESOURCE_PATH = '/xrmverse/subgridfilters.json';

/**
 * The environment's subgrid filter store: the setting's definition, its `organizationsetting` row (when there is one), the
 * resource names of its value, and the first name's resource as read for editing (none when the value names none).
 */
export interface FilterStore {
  definitionId: string;
  rowId?: string;
  names: string[];
  doc?: { webResourceId: string; name: string; json: Record<string, unknown>; managed: boolean };
}

/** The filter resource cannot be edited safely: `reason` says why; nothing is written to it. */
export class FilterStoreUnreadable extends Error {
  constructor(readonly reason: string) {
    super(t('forms.filters.unreadable', { reason }));
    this.name = 'FilterStoreUnreadable';
  }
}

/**
 * The environment has no `xrmverse_SubgridFilters` definition: no filter can be stored (the apps read none), and none can be
 * written.
 */
export class FilterSettingMissing extends Error {
  constructor() {
    super(t('forms.filters.notDefined', { setting: SUBGRID_FILTERS }));
    this.name = 'FilterSettingMissing';
  }
}

const quote = (s: string) => `'${s.replace(/'/g, "''")}'`;
const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();
const controlKey = (id: string) => id.trim().toLowerCase();

/**
 * Reads the environment's value of `xrmverse_SubgridFilters` (never an app's) and the first resource it names. Throws
 * {@link FilterStoreUnreadable} when the value is not a list of names, or that resource is missing, not a JSON object, or
 * has a `forms` that is not an object.
 */
export async function loadFilterStore(api: XrmApi): Promise<FilterStore> {
  const definitions = (
    await api.page<{ settingdefinitionid: string; uniquename: string; defaultvalue?: string | null }>(
      `settingdefinitions?$select=settingdefinitionid,uniquename,defaultvalue&$filter=${encodeURIComponent(`uniquename eq ${quote(SUBGRID_FILTERS)}`)}`,
    )
  ).value;
  const definition = definitions.find((d) => same(d.uniquename, SUBGRID_FILTERS));
  if (!definition) throw new FilterSettingMissing();
  const rows = (
    await api.page<{ organizationsettingid: string; value?: string | null }>(
      `organizationsettings?$select=organizationsettingid,value&$filter=${encodeURIComponent(`_settingdefinitionid_value eq ${definition.settingdefinitionid}`)}`,
    )
  ).value;
  const row = rows[0];
  const value = row ? row.value ?? '' : definition.defaultvalue ?? '';
  const names = parseSubgridFilterNames(value);
  // A value that is not a list of names is not overwritten.
  if (names.length === 0 && value.trim() !== '') throw new FilterStoreUnreadable(t('forms.filters.reason.names'));
  const store: FilterStore = { definitionId: definition.settingdefinitionid, rowId: row?.organizationsettingid, names };
  const name = names[0];
  if (name === undefined) return store;
  const resources = (
    await api.page<{ webresourceid: string; name: string; content?: string | null; ismanaged?: boolean | null }>(
      `webresourceset?$select=webresourceid,name,content,ismanaged&$filter=${encodeURIComponent(`name eq ${quote(name)}`)}`,
    )
  ).value;
  const resource = resources.find((r) => same(r.name, name)) ?? resources[0];
  if (!resource) throw new FilterStoreUnreadable(t('views.modes.reason.missing'));
  let json: unknown;
  try {
    json = JSON.parse(decodeContent(resource.content ?? ''));
  } catch {
    throw new FilterStoreUnreadable(t('presentation.reason.notJson'));
  }
  if (!isObject(json)) throw new FilterStoreUnreadable(t('presentation.reason.notObject'));
  if (json.forms !== undefined && !isObject(json.forms)) throw new FilterStoreUnreadable(t('forms.look.reason.forms'));
  return { ...store, doc: { webResourceId: resource.webresourceid, name: resource.name, json, managed: resource.ismanaged === true } };
}

/** The entries of the form `formId` in a store's JSON, as the apps read them (control ids lowercase, trimmed). */
export function formFiltersOf(json: Record<string, unknown> | undefined, formId: string): Record<string, SubgridFilter> {
  if (!json) return {};
  return { ...(parseSubgridFilters(json, 'subgrid filters').map[normaliseId(formId)] ?? {}) };
}

/**
 * `json` with the entry of the form `formId` set to `entries`: the last key that normalises to the id (the one whose entries
 * the apps read last) is updated in place, every other such key is removed, else the normalised id is added; no entries
 * removes the form's entry. Every other key and value stays; `json` itself is returned when there is nothing to remove and
 * nothing to add.
 */
export function withFormFilters(json: Record<string, unknown>, formId: string, entries: Record<string, SubgridFilter>): Record<string, unknown> {
  const id = normaliseId(formId);
  const forms = isObject(json.forms) ? json.forms : {};
  const keys = Object.keys(forms).filter((k) => normaliseId(k) === id);
  const controls = Object.keys(entries);
  if (controls.length === 0 && keys.length === 0) return json;
  const key = keys.at(-1) ?? id;
  const entry = Object.fromEntries(controls.map((c) => [c, { source: entries[c].source, target: entries[c].target }]));
  const next: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(forms)) {
    if (!keys.includes(k)) next[k] = v;
    else if (k === key && controls.length > 0) next[k] = entry;
  }
  if (controls.length > 0 && keys.length === 0) next[key] = entry;
  return { ...json, forms: next };
}

/**
 * Writes `json` to the store's resource, then publishes it. Without a resource, one is created first (`{prefix}_/xrmverse/
 * subgridfilters.json`, a script web resource as for the presentation overlays) and the environment's value names it. A
 * managed resource is not written ({@link FilterStoreUnreadable}).
 */
export async function saveFilterStore(api: XrmApi, store: FilterStore, json: Record<string, unknown>): Promise<void> {
  if (store.doc?.managed) throw new FilterStoreUnreadable(t('forms.filters.reason.managed'));
  const content = encodeContent(JSON.stringify(json, null, 2));
  let webResourceId = store.doc?.webResourceId;
  if (!webResourceId) {
    const name = `${await loadDefaultPrefix(api)}_${RESOURCE_PATH}`;
    const created = await api.post('webresourceset', { name, displayname: name, webresourcetype: 3, content });
    if (!created.id) throw new XrmError(0, t('api.error.invalidBody'));
    webResourceId = created.id;
    const value = JSON.stringify([name]);
    if (store.rowId) await api.patch(`organizationsettings(${store.rowId})`, { value });
    else {
      await api.post('organizationsettings', {
        displayname: SUBGRID_FILTERS,
        uniquename: SUBGRID_FILTERS,
        value,
        'settingdefinitionid@odata.bind': `/settingdefinitions(${store.definitionId})`,
      });
    }
  }
  await api.patch(`webresourceset(${webResourceId})`, { content });
  await api.action('PublishXml', {
    ParameterXml: `<importexportxml><webresources><webresource>${webResourceId}</webresource></webresources></importexportxml>`,
  });
}

/** A subgrid cell that can hold a filter: a subgrid without a relationship. */
const filterable = (c: DesignCell) => c.control?.kind === 'subgrid' && !c.control.relationship;

/** The design's subgrid filters by control id (lowercase, trimmed): the entries its form gets in the store. */
export function filtersOf(design: FormDesign): Record<string, SubgridFilter> {
  const out: Record<string, SubgridFilter> = {};
  for (const c of allCells(design)) {
    if (!filterable(c) || c.control?.kind !== 'subgrid' || !c.control.filter) continue;
    const key = controlKey(c.control.controlId);
    if (key) out[key] = { source: c.control.filter.source, target: c.control.filter.target };
  }
  return out;
}

/** The design with the stored `entries` set on its subgrids without a relationship (by control id); `design` when none applies. */
export function withFilters(design: FormDesign, entries: Record<string, SubgridFilter>): FormDesign {
  let changed = false;
  const apply = (c: DesignCell): DesignCell => {
    if (!filterable(c) || c.control?.kind !== 'subgrid') return c;
    const key = controlKey(c.control.controlId);
    if (!Object.hasOwn(entries, key)) return c;
    changed = true;
    return { ...c, control: { ...c.control, filter: { source: entries[key].source, target: entries[key].target } } };
  };
  const next: FormDesign = {
    ...design,
    tabs: design.tabs.map((tab) => ({
      ...tab,
      columns: tab.columns.map((col) => ({ ...col, sections: col.sections.map((s) => ({ ...s, cells: s.cells.map(apply) })) })),
    })),
    header: design.header.map(apply),
  };
  return changed ? next : design;
}
