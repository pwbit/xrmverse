import { normaliseId, parseFormEntry, parseViewEntry, type FormPresentation, type GridPlacement, type ViewPresentation } from '../../parsers/presentationJson';
import type { XrmApi } from '../../shell/api/client';
import { collect, collectByIds } from '../../shell/auth/roles';
import { t } from '../../shell/i18n/t';

/** A presentation overlay in use: its web resource and who uses it ('Environment' or the app's name). */
export interface OverlayChoice {
  webResourceName: string;
  label: string;
}

/** An overlay as read for editing: the web resource's id and its JSON object, kept whole (unknown keys and their order). */
export interface OverlayDoc {
  webResourceId: string;
  json: Record<string, unknown>;
}

/** The overlay text cannot be edited safely: `reason` says why (E11); the card stays read-only. */
export class OverlayUnreadable extends Error {
  constructor(readonly reason: string) {
    super(t('views.modes.unreadable', { reason }));
    this.name = 'OverlayUnreadable';
  }
}

const PRESENTATION = 'xrmverse_Presentation';

const quote = (s: string) => `'${s.replace(/'/g, "''")}'`;
const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

/** Decodes the base64 `content` of a web resource as UTF-8. */
export function decodeContent(base64: string): string {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) bytes[i] = binary.charCodeAt(i);
  return new TextDecoder().decode(bytes);
}

/** Encodes `text` as the base64 of its UTF-8 bytes (a web resource's `content`). */
export function encodeContent(text: string): string {
  const bytes = new TextEncoder().encode(text);
  let binary = '';
  const CHUNK = 0x8000;
  for (let i = 0; i < bytes.length; i += CHUNK) binary += String.fromCharCode(...bytes.subarray(i, i + CHUNK));
  return btoa(binary);
}

/**
 * The presentation overlays in use (E11): the environment's (its `organizationsetting` of `xrmverse_Presentation`, else the
 * definition's default, as the apps read it) first, then each app's own (`appsetting`) by app name. One entry per web
 * resource name (names compare without case); the first user of a name gives its label.
 */
export async function listOverlays(api: XrmApi): Promise<OverlayChoice[]> {
  const definitions = await collect<{ settingdefinitionid: string; uniquename: string; defaultvalue?: string | null }>(
    api,
    `settingdefinitions?$select=settingdefinitionid,uniquename,defaultvalue&$filter=uniquename eq ${quote(PRESENTATION)}`,
  );
  const definition = definitions.find((d) => same(d.uniquename, PRESENTATION));
  if (!definition) return [];
  const id = definition.settingdefinitionid;
  const environment = await collect<{ value?: string | null }>(api, `organizationsettings?$select=value&$filter=_settingdefinitionid_value eq ${id}`);
  const appRows = await collect<{ _parentappmoduleid_value: string; value?: string | null }>(
    api,
    `appsettings?$select=_parentappmoduleid_value,value&$filter=_settingdefinitionid_value eq ${id}`,
  );
  const used = appRows.filter((r) => r.value?.trim());
  const appIds = [...new Set(used.map((r) => r._parentappmoduleid_value.toLowerCase()))];
  const apps = appIds.length ? await collectByIds<{ appmoduleid: string; name: string }>(api, 'appmodules?$select=appmoduleid,name', 'appmoduleid', appIds) : [];
  const appName = (appId: string) => apps.find((a) => same(a.appmoduleid, appId))?.name ?? appId;

  const choices: OverlayChoice[] = [];
  const add = (name: string | null | undefined, label: string) => {
    const trimmed = name?.trim();
    if (trimmed && !choices.some((c) => same(c.webResourceName, trimmed))) choices.push({ webResourceName: trimmed, label });
  };
  add([environment[0]?.value, definition.defaultvalue].find((v) => !!v?.trim()), t('views.modes.environment'));
  used
    .map((r) => ({ name: r.value, label: appName(r._parentappmoduleid_value) }))
    .sort((a, b) => a.label.localeCompare(b.label))
    .forEach((a) => add(a.name, a.label));
  return choices;
}

/** Reads the web resource `name` and parses its content; throws {@link OverlayUnreadable} when it cannot be edited safely. */
export async function loadOverlay(api: XrmApi, name: string): Promise<OverlayDoc> {
  const rows = await collect<{ webresourceid: string; name: string; content?: string | null }>(
    api,
    `webresourceset?$select=webresourceid,name,content&$filter=${encodeURIComponent(`name eq ${quote(name)}`)}`,
  );
  const row = rows.find((r) => same(r.name, name)) ?? rows[0];
  if (!row) throw new OverlayUnreadable(t('views.modes.reason.missing'));
  let json: unknown;
  try {
    json = JSON.parse(decodeContent(row.content ?? ''));
  } catch {
    throw new OverlayUnreadable(t('presentation.reason.notJson'));
  }
  if (!isObject(json)) throw new OverlayUnreadable(t('presentation.reason.notObject'));
  if (json.views !== undefined && !isObject(json.views)) throw new OverlayUnreadable(t('views.modes.reason.views'));
  if (json.forms !== undefined && !isObject(json.forms)) throw new OverlayUnreadable(t('forms.look.reason.forms'));
  return { webResourceId: row.webresourceid, json };
}

/** The keys of `views` that normalise to `viewId`, in their order. */
const keysOf = (views: Record<string, unknown>, viewId: string) => Object.keys(views).filter((k) => normaliseId(k) === normaliseId(viewId));

/** The view's entry, read as the apps read it (the last key that normalises to the id). */
export function viewModesOf(doc: OverlayDoc, viewId: string): ViewPresentation | undefined {
  const views = doc.json.views;
  if (!isObject(views)) return undefined;
  const key = keysOf(views, viewId).at(-1);
  return key === undefined ? undefined : parseViewEntry(normaliseId(viewId), views[key]);
}

/** True when `modes` switches on at least one mode besides the grid. */
export const anyMode = (modes: ViewPresentation | undefined): boolean => !!(modes?.kanban || modes?.calendar || modes?.cards);

/** True when the view's entry holds anything to keep: a mode, or a column that wraps. */
export const anySetting = (modes: ViewPresentation | undefined): boolean => anyMode(modes) || !!modes?.wrap?.length;

/** The modes of a view's entry without what only a view has (`wrap`): what a subgrid's entry may copy. */
export function modesOnly(modes: ViewPresentation | undefined): ViewPresentation | undefined {
  if (!modes) return undefined;
  const { wrap: _wrap, ...rest } = modes;
  return rest;
}

/** The entry written for `modes`, over the existing one: keys the designer does not model stay. */
function entryFor(old: Record<string, unknown>, modes: ViewPresentation): Record<string, unknown> {
  const entry = { ...old };
  const objectOf = (v: unknown) => (isObject(v) ? v : {});
  /** The stored object with every setting of the mode laid over it (a setting left undefined is removed). */
  const merged = (stored: unknown, settings: object): Record<string, unknown> => {
    const out: Record<string, unknown> = { ...objectOf(stored), ...settings };
    for (const [k, v] of Object.entries(out)) if (v === undefined) delete out[k];
    return out;
  };
  if (modes.kanban) entry.kanban = merged(old.kanban, modes.kanban);
  else delete entry.kanban;
  if (modes.calendar) {
    const calendar = merged(old.calendar, modes.calendar);
    if (!modes.calendar.end) delete calendar.end;
    // 'month' is the default layout: it is written as absent.
    if (modes.calendar.layout !== 'timeline') delete calendar.layout;
    entry.calendar = calendar;
  } else delete entry.calendar;
  if (modes.cards) entry.cards = merged(old.cards, modes.cards);
  else delete entry.cards;
  const d = modes.default;
  if (d && d !== 'grid' && modes[d]) entry.default = d;
  else delete entry.default;
  if (modes.wrap?.length) entry.wrap = [...modes.wrap];
  else delete entry.wrap;
  return entry;
}

/**
 * The overlay with the view's entry set to `modes` (E11): the existing key that normalises to the id is updated in place
 * (earlier duplicates, which the apps ignore, are dropped), else the bare lowercase id is added; the entry is deleted when
 * every mode is off and no column wraps. Every other key, value and key order stays. `doc` is not changed.
 */
export function withViewModes(doc: OverlayDoc, viewId: string, modes: ViewPresentation | undefined): OverlayDoc {
  const views = isObject(doc.json.views) ? doc.json.views : undefined;
  const keys = views ? keysOf(views, viewId) : [];
  if (!anySetting(modes)) {
    if (!views || keys.length === 0) return doc;
    const kept = Object.fromEntries(Object.entries(views).filter(([k]) => !keys.includes(k)));
    return { ...doc, json: { ...doc.json, views: kept } };
  }
  const key = keys.at(-1) ?? normaliseId(viewId);
  const entry = entryFor(views && isObject(views[key]) ? views[key] : {}, modes!);
  const next: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(views ?? {})) {
    if (k === key) next[k] = entry;
    else if (!keys.includes(k)) next[k] = v;
  }
  next[key] = entry;
  return { ...doc, json: { ...doc.json, views: next } };
}

/** The keys of `forms` that normalise to `formId`, in their order. */
const formKeysOf = (forms: Record<string, unknown>, formId: string) => keysOf(forms, formId);

/** The form's entry, read as the apps read it (the last key that normalises to the id). */
export function formLayoutOf(doc: OverlayDoc, formId: string): FormPresentation | undefined {
  const forms = doc.json.forms;
  if (!isObject(forms)) return undefined;
  const key = formKeysOf(forms, formId).at(-1);
  return key === undefined ? undefined : parseFormEntry(normaliseId(formId), forms[key]);
}

const TIMELINE_DEFAULT = { appearance: 'list', density: 'comfortable' } as const;
const objectOf = (v: unknown): Record<string, unknown> => (isObject(v) ? v : {});
const placementKeys = ['col', 'span', 'row', 'rowSpan'] as const;
const samePlacement = (a: GridPlacement, b: GridPlacement) => placementKeys.every((k) => a[k] === b[k]);

/** `value` as JSON with the keys of every object sorted and undefined members left out: equal for equal settings in any order. */
function stableJson(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(stableJson).join(',')}]`;
  if (isObject(value)) {
    const keys = Object.keys(value)
      .filter((k) => value[k] !== undefined)
      .sort();
    return `{${keys.map((k) => `${JSON.stringify(k)}:${stableJson(value[k])}`).join(',')}}`;
  }
  return JSON.stringify(value) ?? 'null';
}

/** The entry written for `layout`, over the existing one: keys the designer does not model, and values it did not change, stay. */
function formEntryFor(old: Record<string, unknown>, layout: FormPresentation): Record<string, unknown> {
  const entry = { ...old };
  const was = parseFormEntry('x', old);
  // layout
  if (layout.layout !== (was?.layout ?? 'tabs')) {
    if (layout.layout === 'wizard') entry.layout = 'wizard';
    else delete entry.layout;
  }
  // timeline
  const wasTimeline = was?.timeline;
  const wanted = layout.timeline;
  const sameTimeline = wanted ? !!wasTimeline && wanted.appearance === wasTimeline.appearance && wanted.density === wasTimeline.density : !wasTimeline;
  if (!sameTimeline) {
    if (!wanted || (wanted.appearance === TIMELINE_DEFAULT.appearance && wanted.density === TIMELINE_DEFAULT.density)) delete entry.timeline;
    else entry.timeline = { ...objectOf(old.timeline), appearance: wanted.appearance, density: wanted.density };
  }
  // grid: tab by tab, section by section
  const oldGrid = objectOf(old.grid);
  const grid: Record<string, unknown> = {};
  const wasGrid = was?.grid ?? {};
  /** The stored order first, then what is new. */
  const ordered = (stored: string[], wanted: string[]) => [...stored.filter((k) => wanted.includes(k)), ...wanted.filter((k) => !stored.includes(k))];
  for (const tab of ordered(Object.keys(oldGrid), Object.keys(layout.grid))) {
    const sections = layout.grid[tab];
    const oldTab = objectOf(oldGrid[tab]);
    if (!sections || Object.keys(sections).length === 0) continue;
    const written: Record<string, unknown> = {};
    for (const name of ordered(Object.keys(oldTab), Object.keys(sections))) {
      const placement = sections[name];
      const before = wasGrid[tab]?.[name];
      if (before && samePlacement(before, placement)) written[name] = oldTab[name];
      else written[name] = { ...objectOf(oldTab[name]), col: placement.col, span: placement.span, row: placement.row, rowSpan: placement.rowSpan };
    }
    grid[tab] = written;
  }
  if (Object.keys(grid).length > 0) {
    entry.grid = grid;
  } else delete entry.grid;
  // background: written only when it changed; an empty one is dropped
  const sameColours = (a: Record<string, string> = {}, b: Record<string, string> = {}) =>
    Object.keys(a).length === Object.keys(b).length && Object.entries(a).every(([k, v]) => b[k] === v);
  const wasBackground = was?.background;
  const background = layout.background;
  if (!sameColours(wasBackground?.sections, background?.sections) || !sameColours(wasBackground?.fields, background?.fields)) {
    const written: Record<string, unknown> = { ...objectOf(old.background) };
    for (const key of ['sections', 'fields'] as const) {
      const colours = background?.[key] ?? {};
      if (Object.keys(colours).length > 0) written[key] = { ...colours };
      else delete written[key];
    }
    if (Object.keys(written).length > 0) entry.background = written;
    else delete entry.background;
  }
  // subgrids: written only when they changed; an empty map is dropped
  // The whole parsed entry, keys sorted: a change to any of its settings (not only the ones known today) is written.
  const canonical = (m: ViewPresentation | undefined) => stableJson(m ?? null);
  const wasSubgrids = was?.subgrids ?? {};
  const wantedSubgrids = layout.subgrids ?? {};
  const sameSubgrids =
    Object.keys(wasSubgrids).length === Object.keys(wantedSubgrids).length &&
    Object.entries(wantedSubgrids).every(([k, v]) => Object.hasOwn(wasSubgrids, k) && canonical(wasSubgrids[k]) === canonical(v));
  if (!sameSubgrids) {
    const oldSubgrids = objectOf(old.subgrids);
    // Keys are control ids from the form: no prototype, so an id such as "__proto__" is an ordinary key.
    const written: Record<string, unknown> = Object.create(null);
    for (const [name, modes] of Object.entries(wantedSubgrids)) {
      // Update the stored key (the last one that normalises to the name) in place, else add the lowercase name.
      const stored = Object.keys(oldSubgrids).filter((k) => k.trim().toLowerCase() === name).at(-1) ?? name;
      if (Object.hasOwn(wasSubgrids, name) && canonical(wasSubgrids[name]) === canonical(modes)) written[stored] = oldSubgrids[stored];
      else written[stored] = entryFor(objectOf(oldSubgrids[stored]), modes);
    }
    if (Object.keys(written).length > 0) entry.subgrids = written;
    else delete entry.subgrids;
  }
  return entry;
}

/**
 * The overlay with the form's entry set to `layout` (F11): the existing key that normalises to the id is updated in place
 * (earlier duplicates, which the apps ignore, are dropped), else the bare lowercase id is added; the entry is deleted when
 * nothing is left in it (`layout` undefined, or tabs with no placement and no timeline setting, and no key the designer
 * does not model). Every other key, value and key order stays; `doc` is returned as it is when nothing changes.
 */
export function withFormLayout(doc: OverlayDoc, formId: string, layout: FormPresentation | undefined): OverlayDoc {
  const forms = isObject(doc.json.forms) ? doc.json.forms : undefined;
  const keys = forms ? formKeysOf(forms, formId) : [];
  const key = keys.at(-1) ?? normaliseId(formId);
  const old = forms && keys.length > 0 && isObject(forms[key]) ? forms[key] : {};
  const entry = layout ? formEntryFor(old, layout) : {};
  if (Object.keys(entry).length === 0) {
    if (!forms || keys.length === 0) return doc;
    const kept = Object.fromEntries(Object.entries(forms).filter(([k]) => !keys.includes(k)));
    return { ...doc, json: { ...doc.json, forms: kept } };
  }
  if (keys.length === 1 && JSON.stringify(forms![key]) === JSON.stringify(entry)) return doc;
  const next: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(forms ?? {})) {
    if (k === key) next[k] = entry;
    else if (!keys.includes(k)) next[k] = v;
  }
  next[key] = entry;
  return { ...doc, json: { ...doc.json, forms: next } };
}

const HEX6 = /^#[0-9a-fA-F]{6}$/;
/** The keys of `choices` the apps read as `key` (they trim and lowercase it), in their order. */
const choiceKeysOf = (choices: Record<string, unknown>, key: string) => Object.keys(choices).filter((k) => same(k.trim(), key.trim()));

/** The text colours of a choice's values (`choices[key]`, as the apps read it: the last matching key); invalid entries are left out. */
export function choiceTextOf(doc: OverlayDoc, key: string): Record<number, string> {
  const choices = doc.json.choices;
  if (!isObject(choices)) return {};
  const found = choiceKeysOf(choices, key).at(-1);
  const values = found === undefined ? undefined : choices[found];
  if (!isObject(values)) return {};
  const texts: Record<number, string> = {};
  for (const [value, entry] of Object.entries(values)) {
    if (/^-?\d+$/.test(value) && isObject(entry) && typeof entry.text === 'string' && HEX6.test(entry.text)) texts[Number(value)] = entry.text;
  }
  return texts;
}

/**
 * The overlay with the text colour of the value `value` of the choice `key` set to `text` (undefined removes it): the existing
 * key the apps read is updated in place (earlier duplicates, which the apps ignore, are dropped), else the lowercase key is
 * added. An empty choice is removed, then an empty `choices`. Every other key, value and key order stays; `doc` is returned
 * as it is when nothing changes.
 */
export function withChoiceText(doc: OverlayDoc, key: string, value: number, text: string | undefined): OverlayDoc {
  const choices = isObject(doc.json.choices) ? doc.json.choices : undefined;
  const keys = choices ? choiceKeysOf(choices, key) : [];
  const found = keys.at(-1);
  const old = choices && found !== undefined && isObject(choices[found]) ? choices[found] : {};
  const name = String(value);
  const values: Record<string, unknown> = { ...old };
  if (text === undefined) {
    if (!(name in old)) return doc;
    delete values[name];
  } else {
    const entry = objectOf(old[name]);
    if (entry.text === text && keys.length === 1) return doc;
    values[name] = { ...entry, text };
  }
  const target = found ?? key.trim().toLowerCase();
  const next: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(choices ?? {})) {
    if (k === target) {
      if (Object.keys(values).length > 0) next[k] = values;
    } else if (!keys.includes(k)) next[k] = v;
  }
  if (!(target in next) && Object.keys(values).length > 0) next[target] = values;
  const json = { ...doc.json };
  if (Object.keys(next).length > 0) json.choices = next;
  else delete json.choices;
  return { ...doc, json };
}

/** Writes the overlay's content (indented JSON, base64 of UTF-8), then publishes the web resource. */
export async function saveOverlay(api: XrmApi, doc: OverlayDoc): Promise<void> {
  await api.patch(`webresourceset(${doc.webResourceId})`, { content: encodeContent(JSON.stringify(doc.json, null, 2)) });
  await api.action('PublishXml', {
    ParameterXml: `<importexportxml><webresources><webresource>${doc.webResourceId}</webresource></webresources></importexportxml>`,
  });
}
