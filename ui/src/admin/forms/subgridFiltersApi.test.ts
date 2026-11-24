import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { XrmApi } from '../../shell/api/client';
import { en } from '../../shell/i18n/en';
import { decodeContent, encodeContent } from '../views/overlayApi';
import { formRow } from './formDesignTestSupport';
import { readForm, writeForm } from './formDesign';
import {
  filtersOf,
  FilterSettingMissing,
  FilterStoreUnreadable,
  formFiltersOf,
  loadFilterStore,
  saveFilterStore,
  withFilters,
  withFormFilters,
} from './subgridFiltersApi';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const ROOT = '/api/data/v9.2';
const DEF = '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e31';
const ROW = 'cccccccc-0000-0000-0000-000000000001';
const WR = 'eeeeeeee-0000-0000-0000-000000000001';
const NEW_WR = 'eeeeeeee-0000-0000-0000-000000000002';
const PUBLISHER = 'bbbbbbbb-0000-0000-0000-000000000001';
const FORM = '33333333-aaaa-3333-3333-333333333333';

interface Seen { method: string; path: string; body: unknown }

/** Records every request; `answer` answers it, else 204 (a POST gets `NEW_WR`'s id). */
function capture(answer: (s: Seen, search: URLSearchParams) => Response | undefined = () => undefined): Seen[] {
  const seen: Seen[] = [];
  server.use(
    http.all(`*${ROOT}/*`, async ({ request }) => {
      const url = new URL(request.url);
      const text = await request.text();
      const s: Seen = { method: request.method, path: decodeURIComponent(url.pathname).slice(ROOT.length + 1), body: text ? JSON.parse(text) : undefined };
      seen.push(s);
      const answered = answer(s, url.searchParams);
      if (answered) return answered;
      if (s.method === 'POST' && s.path === 'webresourceset') {
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x${ROOT}/webresourceset(${NEW_WR})` } });
      }
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return seen;
}

/** Answers the reads of {@link loadFilterStore}: the definition, the environment row (`value`, none when undefined) and `new_f.json`. */
function environment(value: string | undefined, content?: string, managed = false) {
  return capture((s) => {
    if (s.method !== 'GET') return undefined;
    if (s.path === 'settingdefinitions') return HttpResponse.json({ value: [{ settingdefinitionid: DEF, uniquename: 'xrmverse_SubgridFilters', defaultvalue: '' }] });
    if (s.path === 'organizationsettings') return HttpResponse.json({ value: value === undefined ? [] : [{ organizationsettingid: ROW, value }] });
    if (s.path === 'webresourceset') {
      return HttpResponse.json({ value: content === undefined ? [] : [{ webresourceid: WR, name: 'new_f.json', content: encodeContent(content), ismanaged: managed }] });
    }
    if (s.path === 'solutions') return HttpResponse.json({ value: [{ _publisherid_value: PUBLISHER }] });
    if (s.path === `publishers(${PUBLISHER})`) return HttpResponse.json({ customizationprefix: 'new' });
    return undefined;
  });
}

const writes = (seen: Seen[]) => seen.filter((s) => s.method !== 'GET').map((s) => `${s.method} ${s.path}`);
const ENTRY = { sub_cases: { source: 'parentaccountid', target: 'customerid' } };

describe('loadFilterStore', () => {
  it('reads the environment value and the first resource it names', async () => {
    environment('["new_f.json","new_g.json"]', `{"forms":{"{${FORM.toUpperCase()}}":{"Sub_Cases":{"source":"A","target":"b"}}}}`);
    const store = await loadFilterStore(api());
    expect(store).toMatchObject({ definitionId: DEF, rowId: ROW, names: ['new_f.json', 'new_g.json'], doc: { webResourceId: WR, name: 'new_f.json', managed: false } });
    expect(formFiltersOf(store.doc?.json, FORM)).toEqual({ sub_cases: { source: 'a', target: 'b' } });
  });

  it('without an environment row there is no resource', async () => {
    const seen = environment(undefined);
    expect(await loadFilterStore(api())).toEqual({ definitionId: DEF, rowId: undefined, names: [] });
    expect(seen.some((s) => s.path === 'webresourceset')).toBe(false);
  });

  it.each([
    ['not JSON', '{oops', en['presentation.reason.notJson']],
    ['not an object', '[1]', en['presentation.reason.notObject']],
    ['a "forms" that is not an object', '{"forms":[]}', en['forms.look.reason.forms']],
  ])('a resource that is %s is unreadable', async (_, content, reason) => {
    environment('["new_f.json"]', content);
    const error = await loadFilterStore(api()).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(FilterStoreUnreadable);
    expect((error as FilterStoreUnreadable).reason).toBe(reason);
    expect((error as Error).message).toBe(en['forms.filters.unreadable'].replace('{reason}', reason));
  });

  it('a missing setting definition fails with a localised message', async () => {
    capture((s) => (s.path === 'settingdefinitions' ? HttpResponse.json({ value: [] }) : undefined));
    const error = await loadFilterStore(api()).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(FilterSettingMissing);
    expect((error as Error).message).toBe(en['forms.filters.notDefined'].replace('{setting}', 'xrmverse_SubgridFilters'));
  });

  it('a value that is not a list of names is unreadable, so it is never overwritten', async () => {
    environment('new_f.json');
    await expect(loadFilterStore(api())).rejects.toBeInstanceOf(FilterStoreUnreadable);
  });
});

describe('saveFilterStore', () => {
  it('without an environment row creates the resource, the row, then writes and publishes', async () => {
    const seen = environment(undefined);
    const store = await loadFilterStore(api());
    const json = withFormFilters({}, FORM, ENTRY);
    await saveFilterStore(api(), store, json);
    expect(writes(seen)).toEqual(['POST webresourceset', 'POST organizationsettings', `PATCH webresourceset(${NEW_WR})`, 'POST PublishXml']);
    const posted = seen.find((s) => s.method === 'POST' && s.path === 'webresourceset')!.body as Record<string, unknown>;
    expect(posted).toMatchObject({ name: 'new_/xrmverse/subgridfilters.json', webresourcetype: 3 });
    expect(seen.find((s) => s.path === 'organizationsettings' && s.method === 'POST')!.body).toEqual({
      displayname: 'xrmverse_SubgridFilters',
      uniquename: 'xrmverse_SubgridFilters',
      value: '["new_/xrmverse/subgridfilters.json"]',
      'settingdefinitionid@odata.bind': `/settingdefinitions(${DEF})`,
    });
    const patched = seen.find((s) => s.method === 'PATCH')!.body as { content: string };
    expect(JSON.parse(decodeContent(patched.content))).toEqual({ forms: { [FORM]: ENTRY } });
    expect((seen.at(-1)!.body as { ParameterXml: string }).ParameterXml).toBe(
      `<importexportxml><webresources><webresource>${NEW_WR}</webresource></webresources></importexportxml>`,
    );
  });

  it('an environment row naming nothing is updated with the new resource', async () => {
    const seen = environment('');
    await saveFilterStore(api(), await loadFilterStore(api()), { forms: {} });
    expect(writes(seen)).toEqual(['POST webresourceset', `PATCH organizationsettings(${ROW})`, `PATCH webresourceset(${NEW_WR})`, 'POST PublishXml']);
    expect(seen.find((s) => s.path === `organizationsettings(${ROW})`)!.body).toEqual({ value: '["new_/xrmverse/subgridfilters.json"]' });
  });

  it('an existing row and resource: only the content is written, then published', async () => {
    const seen = environment('["new_f.json"]', '{"forms":{}}');
    await saveFilterStore(api(), await loadFilterStore(api()), { forms: { [FORM]: ENTRY } });
    expect(writes(seen)).toEqual([`PATCH webresourceset(${WR})`, 'POST PublishXml']);
  });

  it('a managed resource is not written', async () => {
    const seen = environment('["new_f.json"]', '{"forms":{}}', true);
    const store = await loadFilterStore(api());
    expect(store.doc?.managed).toBe(true);
    const error = await saveFilterStore(api(), store, { forms: { [FORM]: ENTRY } }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(FilterStoreUnreadable);
    expect((error as FilterStoreUnreadable).reason).toBe(en['forms.filters.reason.managed']);
    expect(writes(seen)).toEqual([]);
  });
});

describe('withFormFilters', () => {
  it('adds the form entry under the normalised id and keeps everything else', () => {
    const json = { other: 1, forms: { x: { a: { source: 's', target: 't' } } } };
    expect(withFormFilters(json, `{${FORM.toUpperCase()}}`, ENTRY)).toEqual({ other: 1, forms: { x: json.forms.x, [FORM]: ENTRY } });
  });

  it('removes the form entry when it has no entries, and duplicate keys of the same id', () => {
    const json = { forms: { [`{${FORM.toUpperCase()}}`]: ENTRY, x: {}, [FORM]: { old: { source: 'a', target: 'b' } } } };
    expect(withFormFilters(json, FORM, {})).toEqual({ forms: { x: {} } });
    const replaced = withFormFilters(json, FORM, ENTRY);
    expect(Object.keys(replaced.forms as object)).toEqual(['x', FORM]);
    expect(replaced).toEqual({ forms: { x: {}, [FORM]: ENTRY } });
  });

  it('updates an existing key of the form in place', () => {
    const json = { forms: { [`{${FORM.toUpperCase()}}`]: { old: { source: 'a', target: 'b' } }, x: {} } };
    expect(JSON.stringify(withFormFilters(json, FORM, ENTRY))).toBe(JSON.stringify({ forms: { [`{${FORM.toUpperCase()}}`]: ENTRY, x: {} } }));
  });

  it('returns the JSON itself when the form has no entry and gets none', () => {
    const json = { forms: { x: {} } };
    expect(withFormFilters(json, FORM, {})).toBe(json);
  });
});

describe('filtersOf / withFilters', () => {
  const subgrid = (id: string, relationship = '') => `<cell id="{${crypto.randomUUID()}}"><labels><label description="${id}" languagecode="1033" /></labels>
    <control id="${id}" classid="{E7A81278-8635-4D9E-8D4D-59480B391C5B}" indicationOfSubgrid="true"><parameters>
      <RelationshipName>${relationship}</RelationshipName><TargetEntityType>incident</TargetEntityType><ViewId>{00000000-0000-0000-0000-000000000001}</ViewId>
    </parameters></control></cell>`;
  const xml = `<form><tabs><tab name="t" id="{a0000000-0000-0000-0000-000000000001}"><labels /><columns><column width="100%"><sections>
    <section name="s" id="{a0000000-0000-0000-0000-000000000002}"><labels /><rows>
      <row>${subgrid(' Sub_Cases ')}</row><row>${subgrid('related', 'incident_customer_accounts')}</row><row>${subgrid('plain')}</row>
    </rows></section></sections></column></columns></tab></tabs></form>`;

  it('merges stored entries into subgrids without a relationship, by control id, and reads them back', () => {
    const design = readForm(formRow(xml), 1033);
    expect(filtersOf(design)).toEqual({});
    const merged = withFilters(design, { ...ENTRY, related: { source: 'a', target: 'b' }, gone: { source: 'a', target: 'b' } });
    expect(filtersOf(merged)).toEqual(ENTRY);
    // The FormXML of an otherwise unchanged design is given back as it was read.
    expect(writeForm(merged)).toBe(design.text);
  });

  it('returns the design itself when no entry applies', () => {
    const design = readForm(formRow(xml), 1033);
    expect(withFilters(design, { gone: { source: 'a', target: 'b' } })).toBe(design);
  });
});
