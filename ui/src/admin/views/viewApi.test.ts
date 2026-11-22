import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { XrmApi } from '../../shell/api/client';
import { parseXml } from '../../parsers/xml';
import { deleteView, listViews, loadView, previewView, publishTable, saveView, setDefaultView, storeView } from './viewApi';
import { newView, writeView } from './viewDesign';
import { ACCOUNT } from './viewDesignTestSupport';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const ROOT = '/api/data/v9.2';
const PUBLISH_XML = '<importexportxml><entities><entity>account</entity></entities></importexportxml>';
const error = (status: number, message: string) => HttpResponse.json({ error: { code: '0x80040265', message } }, { status });

interface Seen { method: string; path: string; search: URLSearchParams; headers: Headers; body: unknown }

/** Records every Web API request, answering with `answer` (204 by default). */
function capture(answer: (s: Seen, index: number) => Response | undefined = () => undefined): Seen[] {
  const seen: Seen[] = [];
  server.use(
    http.all(`*${ROOT}/*`, async ({ request }) => {
      const url = new URL(request.url);
      const text = await request.text();
      const s: Seen = { method: request.method, path: decodeURIComponent(url.pathname).slice(ROOT.length + 1), search: url.searchParams, headers: request.headers, body: text ? JSON.parse(text) : undefined };
      seen.push(s);
      return answer(s, seen.length - 1) ?? new HttpResponse(null, { status: 204 });
    }),
  );
  return seen;
}

const design = () => ({ ...newView('account', ACCOUNT, 'Mine'), description: 'Desc' });

describe('viewApi', () => {
  it('listViews_filters_by_table_and_selects_view_columns', async () => {
    const seen = capture(() => HttpResponse.json({ value: [{ savedqueryid: 'A', name: 'A view' }] }));
    const rows = await listViews(api(), 'account');
    expect(rows).toEqual([{ savedqueryid: 'A', name: 'A view' }]);
    expect(seen[0].path).toBe('savedqueries');
    expect(seen[0].search.get('$filter')).toBe("returnedtypecode eq 'account'");
    expect(seen[0].search.get('$orderby')).toBe('name');
    expect(seen[0].search.get('$select')).toBe('savedqueryid,name,description,querytype,isdefault,ismanaged,fetchxml,layoutxml,returnedtypecode');
  });

  it('listViews_follows_next_links', async () => {
    const origin = globalThis.location.origin;
    capture((s) => s.search.get('page') === '2'
      ? HttpResponse.json({ value: [{ savedqueryid: 'B' }] })
      : HttpResponse.json({ value: [{ savedqueryid: 'A' }], '@odata.nextLink': `${origin}${ROOT}/savedqueries?page=2` }));
    expect((await listViews(api(), 'account')).map((r) => r.savedqueryid)).toEqual(['A', 'B']);
  });

  it('loadView_reads_one_row', async () => {
    const seen = capture(() => HttpResponse.json({ savedqueryid: 'A', name: 'N' }));
    expect(await loadView(api(), 'A')).toMatchObject({ name: 'N' });
    expect(seen[0].path).toBe('savedqueries(A)');
    expect(seen[0].search.get('$select')).toContain('layoutxml');
  });

  it('saveView_create_posts_then_publishes_and_returns_id', async () => {
    const d = design();
    const seen = capture((s) => (s.method === 'POST' && s.path === 'savedqueries'
      ? new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x${ROOT}/savedqueries(11111111-2222-3333-4444-555555555555)` } })
      : undefined));
    const id = await saveView(api(), d);
    expect(id).toBe('11111111-2222-3333-4444-555555555555');
    const { fetchxml, layoutxml } = writeView(d);
    expect(seen[0]).toMatchObject({ method: 'POST', path: 'savedqueries' });
    expect(seen[0].body).toEqual({ name: 'Mine', description: 'Desc', returnedtypecode: 'account', querytype: 0, isdefault: false, fetchxml, layoutxml });
    expect(seen[1]).toMatchObject({ method: 'POST', path: 'PublishXml', body: { ParameterXml: PUBLISH_XML } });
    expect(seen).toHaveLength(2);
  });

  it('saveView_update_patches_only_the_editable_columns_then_publishes', async () => {
    const d = design();
    const seen = capture();
    expect(await saveView(api(), d, 'V1')).toBe('V1');
    const { fetchxml, layoutxml } = writeView(d);
    expect(seen[0]).toMatchObject({ method: 'PATCH', path: 'savedqueries(V1)' });
    expect(seen[0].body).toEqual({ name: 'Mine', description: 'Desc', fetchxml, layoutxml });
    expect(seen[1]).toMatchObject({ method: 'POST', path: 'PublishXml', body: { ParameterXml: PUBLISH_XML } });
  });

  it('saveView_failed_patch_rejects_with_server_message_and_does_not_publish', async () => {
    const seen = capture((s) => (s.method === 'PATCH' ? error(400, 'Component is not customizable.') : undefined));
    await expect(saveView(api(), design(), 'V1')).rejects.toThrow('Component is not customizable.');
    expect(seen.map((s) => s.method)).toEqual(['PATCH']);
  });

  it('saveView_create_without_entity_id_rejects', async () => {
    const seen = capture();
    await expect(saveView(api(), design())).rejects.toThrow();
    expect(seen).toHaveLength(1);
  });

  it('storeView_creates_without_publishing_and_returns_id', async () => {
    const seen = capture((s) => (s.method === 'POST' && s.path === 'savedqueries'
      ? new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x${ROOT}/savedqueries(11111111-2222-3333-4444-555555555555)` } })
      : undefined));
    expect(await storeView(api(), design())).toBe('11111111-2222-3333-4444-555555555555');
    expect(seen.map((s) => `${s.method} ${s.path}`)).toEqual(['POST savedqueries']);
  });

  it('storeView_updates_without_publishing', async () => {
    const seen = capture();
    expect(await storeView(api(), design(), 'V1')).toBe('V1');
    expect(seen.map((s) => `${s.method} ${s.path}`)).toEqual(['PATCH savedqueries(V1)']);
  });

  it('publishTable_posts_the_publish_xml', async () => {
    const seen = capture();
    await publishTable(api(), 'account');
    expect(seen).toHaveLength(1);
    expect(seen[0]).toMatchObject({ method: 'POST', path: 'PublishXml', body: { ParameterXml: PUBLISH_XML } });
  });

  it('deleteView_deletes_then_publishes', async () => {
    const seen = capture();
    await deleteView(api(), 'V1', 'account');
    expect(seen.map((s) => `${s.method} ${s.path}`)).toEqual(['DELETE savedqueries(V1)', 'POST PublishXml']);
  });

  it('deleteView_failure_rejects_and_does_not_publish', async () => {
    const seen = capture((s) => (s.method === 'DELETE' ? error(400, 'Managed component.') : undefined));
    await expect(deleteView(api(), 'V1', 'account')).rejects.toThrow('Managed component.');
    expect(seen).toHaveLength(1);
  });

  it('setDefaultView_patches_clicked_first_then_other_defaults_then_publishes', async () => {
    const seen = capture((s) => (s.method === 'GET' ? HttpResponse.json({ value: [{ savedqueryid: 'O1' }, { savedqueryid: 'O2' }] }) : undefined));
    await setDefaultView(api(), 'V1', 'account');
    expect(seen.map((s) => `${s.method} ${s.path}`)).toEqual([
      'PATCH savedqueries(V1)', 'GET savedqueries', 'PATCH savedqueries(O1)', 'PATCH savedqueries(O2)', 'POST PublishXml',
    ]);
    expect(seen[0].body).toEqual({ isdefault: true });
    expect(seen[2].body).toEqual({ isdefault: false });
    expect(seen[1].search.get('$filter')).toBe("returnedtypecode eq 'account' and querytype eq 0 and isdefault eq true and savedqueryid ne V1");
  });

  it('setDefaultView_other_patch_failure_still_rejects_after_the_clicked_view_is_default', async () => {
    const seen = capture((s) => {
      if (s.method === 'GET') return HttpResponse.json({ value: [{ savedqueryid: 'O1' }, { savedqueryid: 'O2' }] });
      if (s.path === 'savedqueries(O1)') return error(400, 'Cannot change O1.');
      return undefined;
    });
    await expect(setDefaultView(api(), 'V1', 'account')).rejects.toThrow('Cannot change O1.');
    expect(seen[0]).toMatchObject({ method: 'PATCH', path: 'savedqueries(V1)', body: { isdefault: true } });
    // The remaining views are still tried and the change is published.
    expect(seen.map((s) => s.path)).toContain('savedqueries(O2)');
    expect(seen.at(-1)).toMatchObject({ path: 'PublishXml' });
  });

  it('setDefaultView_failed_first_patch_rejects_without_touching_the_others', async () => {
    const seen = capture((s) => (s.method === 'PATCH' ? error(400, 'No.') : undefined));
    await expect(setDefaultView(api(), 'V1', 'account')).rejects.toThrow('No.');
    expect(seen).toHaveLength(1);
  });

  it('previewView_gets_ten_rows_with_formatted_values', async () => {
    const seen = capture(() => HttpResponse.json({ value: [{ name: 'A' }] }));
    const rows = await previewView(api(), 'accounts', design());
    expect(rows).toEqual([{ name: 'A' }]);
    expect(seen[0].path).toBe('accounts');
    const fetch = parseXml(seen[0].search.get('fetchXml')!).documentElement;
    expect(fetch.getAttribute('count')).toBe('10');
    expect(fetch.getAttribute('page')).toBe('1');
    expect(fetch.getElementsByTagName('entity')[0].getAttribute('name')).toBe('account');
    expect(seen[0].headers.get('prefer')).toContain('odata.include-annotations="OData.Community.Display.V1.FormattedValue"');
  });
});
