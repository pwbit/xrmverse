import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { XrmApi } from '../../shell/api/client';
import { en } from '../../shell/i18n/en';
import type { ViewPresentation } from '../../parsers/presentationJson';
import {
  choiceTextOf,
  decodeContent,
  encodeContent,
  formLayoutOf,
  listOverlays,
  loadOverlay,
  OverlayUnreadable,
  saveOverlay,
  viewModesOf,
  withChoiceText,
  withFormLayout,
  withViewModes,
  type OverlayDoc,
} from './overlayApi';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const ROOT = '/api/data/v9.2';
const DEF = 'dddddddd-0000-0000-0000-000000000001';
const VIEW = '11111111-aaaa-1111-1111-111111111111';
const WR = 'eeeeeeee-0000-0000-0000-000000000001';
const FORM = '33333333-aaaa-3333-3333-333333333333';

interface Seen { method: string; path: string; search: URLSearchParams; body: unknown }

function capture(answer: (s: Seen) => Response | undefined = () => undefined): Seen[] {
  const seen: Seen[] = [];
  server.use(
    http.all(`*${ROOT}/*`, async ({ request }) => {
      const url = new URL(request.url);
      const text = await request.text();
      const s: Seen = { method: request.method, path: decodeURIComponent(url.pathname).slice(ROOT.length + 1), search: url.searchParams, body: text ? JSON.parse(text) : undefined };
      seen.push(s);
      return answer(s) ?? new HttpResponse(null, { status: 204 });
    }),
  );
  return seen;
}

/** Answers the setting reads of {@link listOverlays}. */
function settings(environment: string | undefined, apps: { id: string; name: string; value: string }[], defaultvalue: string | null = null) {
  return capture((s) => {
    if (s.path === 'settingdefinitions') return HttpResponse.json({ value: [{ settingdefinitionid: DEF, uniquename: 'xrmverse_Presentation', defaultvalue }] });
    if (s.path === 'organizationsettings') return HttpResponse.json({ value: environment === undefined ? [] : [{ value: environment }] });
    if (s.path === 'appsettings') return HttpResponse.json({ value: apps.map((a) => ({ _parentappmoduleid_value: a.id, value: a.value })) });
    if (s.path === 'appmodules') return HttpResponse.json({ value: apps.map((a) => ({ appmoduleid: a.id, name: a.name })) });
    return undefined;
  });
}

const resource = (text: string) => capture((s) =>
  s.path === 'webresourceset' && s.method === 'GET'
    ? HttpResponse.json({ value: [{ webresourceid: WR, name: 'new_look.json', content: encodeContent(text) }] })
    : undefined,
);

const doc = (json: Record<string, unknown>): OverlayDoc => ({ webResourceId: WR, json });

describe('overlayApi', () => {
  it('listOverlays lists the environment overlay first, then the apps by name, one entry per web resource name', async () => {
    const seen = settings('new_env.json', [
      { id: 'a2', name: 'Service', value: 'new_service.json' },
      { id: 'a1', name: 'Sales', value: 'new_sales.json' },
      { id: 'a3', name: 'Field', value: 'NEW_ENV.json' },
      { id: 'a4', name: 'Marketing', value: 'new_sales.json' },
      { id: 'a5', name: 'Empty', value: '  ' },
    ]);
    expect(await listOverlays(api())).toEqual([
      { webResourceName: 'new_env.json', label: en['views.modes.environment'] },
      { webResourceName: 'new_sales.json', label: 'Marketing' }, // shared with Sales: the first app by name labels it
      { webResourceName: 'new_service.json', label: 'Service' },
    ]);
    expect(seen.find((s) => s.path === 'settingdefinitions')!.search.get('$filter')).toBe("uniquename eq 'xrmverse_Presentation'");
    expect(seen.find((s) => s.path === 'appsettings')!.search.get('$filter')).toBe(`_settingdefinitionid_value eq ${DEF}`);
  });

  it('listOverlays is empty when no overlay is in use, and uses the definition default as the environment one', async () => {
    settings(undefined, []);
    expect(await listOverlays(api())).toEqual([]);
    settings(undefined, [], 'new_default.json');
    expect(await listOverlays(api())).toEqual([{ webResourceName: 'new_default.json', label: en['views.modes.environment'] }]);
  });

  it('loadOverlay reads the web resource by name and decodes UTF-8 base64', async () => {
    const seen = resource('{"version":1,"whiteLabel":{"name":"Caffè"}}');
    const loaded = await loadOverlay(api(), "new_l'ook.json");
    expect(loaded).toEqual({ webResourceId: WR, json: { version: 1, whiteLabel: { name: 'Caffè' } } });
    expect(seen[0].search.get('$filter')).toBe("name eq 'new_l''ook.json'");
    expect(decodeContent(encodeContent('è ✓ 😀'))).toBe('è ✓ 😀');
  });

  it('loadOverlay throws OverlayUnreadable for invalid JSON, a non-object and a non-object views', async () => {
    for (const [text, reason] of [
      ['{ nope', en['presentation.reason.notJson']],
      ['[1]', en['presentation.reason.notObject']],
      ['{"version":1,"views":[]}', en['views.modes.reason.views']],
    ]) {
      resource(text);
      const failure = await loadOverlay(api(), 'new_look.json').catch((e: unknown) => e);
      expect(failure).toBeInstanceOf(OverlayUnreadable);
      expect((failure as OverlayUnreadable).reason).toBe(reason);
    }
  });

  it('loadOverlay throws OverlayUnreadable when the web resource does not exist', async () => {
    capture((s) => (s.path === 'webresourceset' ? HttpResponse.json({ value: [] }) : undefined));
    const failure = await loadOverlay(api(), 'new_gone.json').catch((e: unknown) => e);
    expect(failure).toBeInstanceOf(OverlayUnreadable);
  });

  it('viewModesOf finds the entry under any key that normalises to the view id', () => {
    const d = doc({ views: { [`{${VIEW.toUpperCase()}}`]: { kanban: { groupBy: 'statuscode' }, default: 'kanban' } } });
    expect(viewModesOf(d, VIEW)).toEqual({ kanban: { groupBy: 'statuscode' }, default: 'kanban' });
    expect(viewModesOf(d, '22222222-0000-0000-0000-000000000000')).toBeUndefined();
    expect(viewModesOf(doc({ version: 1 }), VIEW)).toBeUndefined();
  });

  it('withViewModes writes calendar.layout timeline and drops it for month', () => {
    const d = doc({ version: 1, views: { [VIEW]: { calendar: { start: 'a', layout: 'timeline', x_note: 'kept' } } } });
    const timeline = withViewModes(d, VIEW, { calendar: { start: 'a', end: 'b', layout: 'timeline' } });
    expect((timeline.json.views as Record<string, unknown>)[VIEW]).toEqual({ calendar: { start: 'a', end: 'b', layout: 'timeline', x_note: 'kept' } });
    const month = withViewModes(d, VIEW, { calendar: { start: 'a' } });
    expect((month.json.views as Record<string, unknown>)[VIEW]).toEqual({ calendar: { start: 'a', x_note: 'kept' } });
  });

  it('withViewModes updates an existing upper-case braced key in place, keeping the other keys and their order', () => {
    const upper = `{${VIEW.toUpperCase()}}`;
    const before = {
      version: 1,
      theme: { density: 'compact' },
      views: { first: { cards: {} }, [upper]: { kanban: { groupBy: 'statuscode' }, x_note: 'kept' }, last: { cards: {} } },
      forms: { f: { layout: 'wizard' } },
    };
    const next = withViewModes(doc(structuredClone(before)), VIEW, { calendar: { start: 'createdon' }, default: 'calendar' });
    expect(Object.keys(next.json)).toEqual(['version', 'theme', 'views', 'forms']);
    const views = next.json.views as Record<string, unknown>;
    expect(Object.keys(views)).toEqual(['first', upper, 'last']);
    expect(views[upper]).toEqual({ x_note: 'kept', calendar: { start: 'createdon' }, default: 'calendar' });
    const untouched = (j: Record<string, unknown>) => {
      const { views: v, ...rest } = j as { views: Record<string, unknown> };
      const { [upper]: _entry, ...others } = v;
      return JSON.stringify([rest, others]);
    };
    expect(untouched(next.json)).toBe(untouched(before));
  });

  it('withViewModes adds a bare lowercase id when there is no entry, and a views key when there is none', () => {
    const next = withViewModes(doc({ version: 1, views: { other: {} } }), `{${VIEW.toUpperCase()}}`, { cards: {} });
    expect(next.json.views).toEqual({ other: {}, [VIEW]: { cards: {} } });
    const fresh = withViewModes(doc({ version: 1 }), VIEW, { kanban: { groupBy: 'statuscode' } });
    expect(fresh.json).toEqual({ version: 1, views: { [VIEW]: { kanban: { groupBy: 'statuscode' } } } });
  });

  it('withViewModes deletes the entry when every mode is off and writes default only for a mode that is on', () => {
    const d = doc({ version: 1, views: { [VIEW.toUpperCase()]: { cards: {} }, other: {} } });
    expect(withViewModes(d, VIEW, undefined).json.views).toEqual({ other: {} });
    expect(withViewModes(d, VIEW, { default: 'grid' }).json.views).toEqual({ other: {} });
    expect(withViewModes(doc({ version: 1 }), VIEW, undefined).json).toEqual({ version: 1 });
    expect(withViewModes(d, VIEW, { cards: {}, default: 'grid' }).json.views).toEqual({ [VIEW.toUpperCase()]: { cards: {} }, other: {} });
    expect(withViewModes(d, VIEW, { cards: {}, default: 'kanban' }).json.views).toEqual({ [VIEW.toUpperCase()]: { cards: {} }, other: {} });
  });

  it('saveOverlay PATCHes the content as UTF-8 base64 of the indented JSON, then publishes the web resource', async () => {
    const seen = capture();
    const json = { version: 1, whiteLabel: { name: 'Caffè' } };
    await saveOverlay(api(), doc(json));
    expect(seen.map((s) => `${s.method} ${s.path}`)).toEqual([`PATCH webresourceset(${WR})`, 'POST PublishXml']);
    expect(decodeContent((seen[0].body as { content: string }).content)).toBe(JSON.stringify(json, null, 2));
    expect(seen[1].body).toEqual({
      ParameterXml: `<importexportxml><webresources><webresource>${WR}</webresource></webresources></importexportxml>`,
    });
  });

  it('saveOverlay does not publish when the PATCH fails', async () => {
    const seen = capture((s) => (s.method === 'PATCH' ? HttpResponse.json({ error: { code: '0x1', message: 'No way' } }, { status: 403 }) : undefined));
    await expect(saveOverlay(api(), doc({ version: 1 }))).rejects.toThrow('No way');
    expect(seen).toHaveLength(1);
  });

  it('an overlay with only views is readable, and one with only forms too', async () => {
    resource(JSON.stringify({ version: 1, views: { a: {} } }));
    await expect(loadOverlay(api(), 'new_look.json')).resolves.toMatchObject({ webResourceId: WR });
    resource(JSON.stringify({ version: 1, forms: { a: {} } }));
    await expect(loadOverlay(api(), 'new_look.json')).resolves.toMatchObject({ webResourceId: WR });
    resource(JSON.stringify({ version: 1, forms: 'oops' }));
    const failure = await loadOverlay(api(), 'new_look.json').catch((e: unknown) => e);
    expect(failure).toBeInstanceOf(OverlayUnreadable);
    expect((failure as OverlayUnreadable).reason).toBe(en['forms.look.reason.forms']);
  });

  it('formLayoutOf reads the entry under any key that normalises to the form id, and nothing without a forms key', () => {
    const d = doc({ forms: { [`{${FORM.toUpperCase()}}`]: { layout: 'wizard', grid: { general: { main: { col: 1, row: 1, span: 6 } } } } } });
    expect(formLayoutOf(d, FORM)).toEqual({ layout: 'wizard', grid: { general: { main: { col: 1, span: 6, row: 1, rowSpan: 1 } } } });
    expect(formLayoutOf(d, '22222222-0000-0000-0000-000000000000')).toBeUndefined();
    expect(formLayoutOf(doc({ version: 1, views: {} }), FORM)).toBeUndefined();
  });

  const place = { col: 1, span: 6, row: 1, rowSpan: 1 };

  it('withFormLayout updates the existing key in place, drops earlier duplicates, keeps the other keys and their order', () => {
    const before = {
      version: 1,
      forms: { z: {}, [FORM]: { layout: 'tabs', future: 1 }, other: { layout: 'wizard' }, [`{${FORM.toUpperCase()}}`]: { layout: 'tabs', unknown: true } },
      views: { v: {} },
    };
    const next = withFormLayout(doc(structuredClone(before)), FORM, { layout: 'wizard', grid: { general: { main: place } } });
    expect(Object.keys(next.json)).toEqual(['version', 'forms', 'views']);
    expect(Object.keys(next.json.forms as object)).toEqual(['z', 'other', `{${FORM.toUpperCase()}}`]);
    expect((next.json.forms as Record<string, unknown>)[`{${FORM.toUpperCase()}}`]).toEqual({
      layout: 'wizard',
      unknown: true,
      grid: { general: { main: place } },
    });
  });

  it('withFormLayout adds a lowercase bare id (and a forms key) when there is no entry', () => {
    const next = withFormLayout(doc({ version: 1, forms: { other: {} } }), `{${FORM.toUpperCase()}}`, { layout: 'wizard', grid: {} });
    expect(next.json.forms).toEqual({ other: {}, [FORM]: { layout: 'wizard' } });
    expect(withFormLayout(doc({ version: 1 }), FORM, { layout: 'wizard', grid: {} }).json).toEqual({ version: 1, forms: { [FORM]: { layout: 'wizard' } } });
  });

  it('withFormLayout deletes an empty entry, and keeps one that holds keys the designer does not model', () => {
    const d = doc({ version: 1, forms: { [FORM]: { layout: 'wizard' }, other: {} } });
    expect(withFormLayout(d, FORM, { layout: 'tabs', grid: {} }).json.forms).toEqual({ other: {} });
    expect(withFormLayout(d, FORM, undefined).json.forms).toEqual({ other: {} });
    expect(withFormLayout(doc({ version: 1 }), FORM, undefined).json).toEqual({ version: 1 });
    expect(withFormLayout(doc({ forms: { [FORM]: { layout: 'wizard', future: 1 } } }), FORM, { layout: 'tabs', grid: {} }).json.forms).toEqual({ [FORM]: { future: 1 } });
  });

  it('withFormLayout writes subgrids when they change, leaves unchanged ones byte-identical, and removes an empty map', () => {
    const subgrids = { contacts: { kanban: { groupBy: 'statuscode' } } };
    const set = withFormLayout(doc({ version: 1, forms: { [FORM]: { layout: 'wizard' } } }), FORM, { layout: 'wizard', grid: {}, subgrids });
    expect(((set.json.forms as Record<string, any>)[FORM]).subgrids.contacts.kanban.groupBy).toBe('statuscode');
    const stored = doc({ version: 1, forms: { [FORM]: { subgrids: { Contacts: { kanban: { groupBy: 'statuscode' }, future: 1 } } } } });
    expect(withFormLayout(stored, FORM, { layout: 'tabs', grid: {}, subgrids })).toBe(stored);
    const edited = withFormLayout(stored, FORM, { layout: 'tabs', grid: {}, subgrids: { contacts: { kanban: { groupBy: 'x' }, cards: {} } } });
    expect((edited.json.forms as Record<string, unknown>)[FORM]).toEqual({ subgrids: { Contacts: { kanban: { groupBy: 'x' }, cards: {}, future: 1 } } });
    const cleared = withFormLayout(doc({ version: 1, forms: { [FORM]: { layout: 'wizard', subgrids: { contacts: { cards: {} } } } } }), FORM, { layout: 'wizard', grid: {}, subgrids: {} });
    expect((cleared.json.forms as Record<string, unknown>)[FORM]).toEqual({ layout: 'wizard' });
  });

  it('withFormLayout compares a whole subgrid entry: a change to a setting outside default, groupBy, start, end and cards is written', () => {
    const stored = doc({ version: 1, forms: { [FORM]: { subgrids: { contacts: { calendar: { start: 'createdon' } } } } } });
    // A layout-only change (month to timeline) is saved.
    const edited = withFormLayout(stored, FORM, { layout: 'tabs', grid: {}, subgrids: { contacts: { calendar: { start: 'createdon', layout: 'timeline' } } } });
    expect(edited).not.toBe(stored);
    expect((edited.json.forms as Record<string, unknown>)[FORM]).toEqual({ subgrids: { contacts: { calendar: { start: 'createdon', layout: 'timeline' } } } });
    // And back to month: the layout is written as absent.
    const back = withFormLayout(edited, FORM, { layout: 'tabs', grid: {}, subgrids: { contacts: { calendar: { start: 'createdon' } } } });
    expect(back).not.toBe(edited);
    expect((back.json.forms as Record<string, unknown>)[FORM]).toEqual({ subgrids: { contacts: { calendar: { start: 'createdon' } } } });
    // A setting a later version adds to the calendar, in the shape the parser would give it, is written too.
    const calendar = { start: 'createdon', x_future: 1 } as unknown as ViewPresentation['calendar'];
    const future = withFormLayout(stored, FORM, { layout: 'tabs', grid: {}, subgrids: { contacts: { calendar } } });
    expect((future.json.forms as Record<string, unknown>)[FORM]).toEqual({ subgrids: { contacts: { calendar: { start: 'createdon', x_future: 1 } } } });
    // Same settings in another key order: nothing to write.
    const ordered = doc({ version: 1, forms: { [FORM]: { subgrids: { contacts: { cards: {}, default: 'cards', kanban: { groupBy: 'x' } } } } } });
    expect(withFormLayout(ordered, FORM, { layout: 'tabs', grid: {}, subgrids: { contacts: { kanban: { groupBy: 'x' }, default: 'cards', cards: {} } } })).toBe(ordered);
  });

  it('withFormLayout writes a subgrid whose control id is __proto__ as an ordinary key', () => {
    const edited = withFormLayout(doc({ version: 1 }), FORM, { layout: 'tabs', grid: {}, subgrids: JSON.parse('{"__proto__":{"cards":{}}}') });
    const subgrids = ((edited.json.forms as Record<string, any>)[FORM]).subgrids as Record<string, unknown>;
    expect(Object.hasOwn(subgrids, '__proto__')).toBe(true);
    expect(JSON.parse(JSON.stringify(edited.json)).forms[FORM].subgrids).toEqual(JSON.parse('{"__proto__":{"cards":{}}}'));
  });

  it('withFormLayout writes background colours when they change, keeps unknown keys, and drops an empty background', () => {
    const background = { sections: { main: 'yellow' as const }, fields: { name: '#a1b2c3' as const } };
    const set = withFormLayout(doc({ version: 1, forms: { [FORM]: { background: { future: 1 } } } }), FORM, { layout: 'tabs', grid: {}, background });
    expect((set.json.forms as Record<string, unknown>)[FORM]).toEqual({ background: { future: 1, sections: { main: 'yellow' }, fields: { name: '#a1b2c3' } } });
    const same = doc({ version: 1, forms: { [FORM]: { background: { sections: { main: 'Yellow' }, fields: { name: '#A1B2C3' } } } } });
    expect(withFormLayout(same, FORM, { layout: 'tabs', grid: {}, background })).toBe(same);
    const cleared = withFormLayout(doc({ version: 1, forms: { [FORM]: { background: { sections: { main: 'yellow' } } } } }), FORM, { layout: 'tabs', grid: {} });
    expect(cleared.json.forms).toEqual({});
  });

  it('withFormLayout writes the timeline only when it differs from the default, and returns the same doc when nothing changes', () => {
    const d = doc({ forms: { [FORM]: { layout: 'wizard', timeline: { appearance: 'cards', density: 'comfortable' }, grid: { general: { main: { col: 1, row: 1, span: 6 } } } } } });
    const read = formLayoutOf(d, FORM)!;
    expect(withFormLayout(d, FORM, read)).toBe(d);
    expect(withFormLayout(d, FORM, { ...read, timeline: { appearance: 'list', density: 'comfortable' } }).json.forms).toEqual({
      [FORM]: { layout: 'wizard', grid: { general: { main: { col: 1, row: 1, span: 6 } } } },
    });
    expect(withFormLayout(d, FORM, { ...read, timeline: { appearance: 'cards', density: 'compact' } }).json.forms).toEqual({
      [FORM]: { layout: 'wizard', timeline: { appearance: 'cards', density: 'compact' }, grid: { general: { main: { col: 1, row: 1, span: 6 } } } },
    });
  });

  it('withFormLayout drops a tab with no placement and a section that is no longer in the layout', () => {
    const d = doc({ forms: { [FORM]: { grid: { general: { main: place, gone: place }, other: { x: place } } } } });
    const next = withFormLayout(d, FORM, { layout: 'tabs', grid: { general: { main: place }, other: {} } });
    expect(next.json.forms).toEqual({ [FORM]: { grid: { general: { main: place } } } });
  });

  it('choiceTextOf reads the text colours under any key that is the same ignoring case and spaces, the last one winning', () => {
    const d = doc({ choices: { 'XV_Project.XV_Priority': { '1': { text: '#000000' } }, ' xv_project.xv_priority ': { '1': { text: '#111111' }, '2': { text: '#222222' }, x: { text: '#333333' }, '3': { text: 'red' } } } });
    expect(choiceTextOf(d, 'xv_project.xv_priority')).toEqual({ 1: '#111111', 2: '#222222' });
    expect(choiceTextOf(doc({}), 'xv_project.xv_priority')).toEqual({});
    expect(choiceTextOf(doc({ choices: { 'xv_project.xv_priority': 'bad' } }), 'xv_project.xv_priority')).toEqual({});
  });

  it('withChoiceText sets a value in place, keeping its other keys, the other choices and their order', () => {
    const d = doc({ version: 1, choices: { a: { '1': { text: '#000000' } }, 'XV_P.XV_C': { '1': { text: '#111111', note: 'keep' }, '2': { text: '#222222' } }, b: {} }, views: {} });
    const next = withChoiceText(d, 'xv_p.xv_c', 1, '#ABCDEF');
    expect(next.json).toEqual({ version: 1, choices: { a: { '1': { text: '#000000' } }, 'XV_P.XV_C': { '1': { text: '#ABCDEF', note: 'keep' }, '2': { text: '#222222' } }, b: {} }, views: {} });
    expect(Object.keys(next.json.choices as object)).toEqual(['a', 'XV_P.XV_C', 'b']);
    expect(d.json.choices).toEqual({ a: { '1': { text: '#000000' } }, 'XV_P.XV_C': { '1': { text: '#111111', note: 'keep' }, '2': { text: '#222222' } }, b: {} });
  });

  it('withChoiceText adds a lowercase key (and a choices key) when there is none, and drops earlier duplicates', () => {
    expect(withChoiceText(doc({ version: 1 }), 'XV_P.XV_C', 3, '#123456').json).toEqual({ version: 1, choices: { 'xv_p.xv_c': { '3': { text: '#123456' } } } });
    const dup = doc({ choices: { 'xv_p.xv_c': { '1': { text: '#000000' } }, 'XV_P.xv_c': { '2': { text: '#222222' } } } });
    expect(withChoiceText(dup, 'xv_p.xv_c', 1, '#111111').json).toEqual({ choices: { 'XV_P.xv_c': { '1': { text: '#111111' }, '2': { text: '#222222' } } } });
  });

  it('withChoiceText removes a value, then an empty choice, then an empty choices key; nothing to remove keeps the doc', () => {
    const d = doc({ version: 1, choices: { 'xv_p.xv_c': { '1': { text: '#111111' }, '2': { text: '#222222' } } } });
    const one = withChoiceText(d, 'xv_p.xv_c', 1, undefined);
    expect(one.json).toEqual({ version: 1, choices: { 'xv_p.xv_c': { '2': { text: '#222222' } } } });
    expect(withChoiceText(one, 'xv_p.xv_c', 2, undefined).json).toEqual({ version: 1 });
    const other = doc({ choices: { a: { '1': { text: '#000000' } }, 'xv_p.xv_c': { '1': { text: '#111111' } } } });
    expect(withChoiceText(other, 'xv_p.xv_c', 1, undefined).json).toEqual({ choices: { a: { '1': { text: '#000000' } } } });
    expect(withChoiceText(d, 'xv_p.xv_c', 9, undefined)).toBe(d);
    expect(withChoiceText(d, 'xv_p.xv_c', 1, '#111111')).toBe(d);
  });

  it('withViewModes writes and removes wrap, and keeps an entry that only wraps', () => {
    const d = doc({ version: 1, views: {} });
    const wrapped = withViewModes(d, VIEW, { wrap: ['title'] });
    expect((wrapped.json.views as Record<string, unknown>)[VIEW.toLowerCase().replace(/[{}]/g, '')]).toEqual({ wrap: ['title'] });
    const cleared = withViewModes(wrapped, VIEW, {});
    expect(cleared.json.views).toEqual({});
  });
});

