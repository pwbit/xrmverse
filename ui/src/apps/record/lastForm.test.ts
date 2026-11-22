import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { XrmApi } from '../../shell/api/client';
import { server } from '../../test/server';
import { lastFormIdOf, mruFormXml, readLastForm, writeLastForm } from './lastForm';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const USER = '20000000-0000-0000-0000-000000000001';
const FORM = '10000000-0000-0000-0000-00000000000A';
const ROW = '30000000-0000-0000-0000-000000000001';

interface Sent {
  method: string;
  url: string;
  body?: unknown;
}

function mock(rows: { userentityuisettingsid: string; lastviewedformxml?: string }[]) {
  const sent: Sent[] = [];
  server.use(
    http.get('*/api/data/v9.2/userentityuisettingsset', ({ request }) => {
      sent.push({ method: 'GET', url: decodeURIComponent(request.url) });
      return HttpResponse.json({ value: rows });
    }),
    http.patch('*/api/data/v9.2/*', async ({ request }) => {
      sent.push({ method: 'PATCH', url: decodeURIComponent(request.url), body: await request.json() });
      return new HttpResponse(null, { status: 204 });
    }),
    http.post('*/api/data/v9.2/userentityuisettingsset', async ({ request }) => {
      sent.push({ method: 'POST', url: decodeURIComponent(request.url), body: await request.json() });
      return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x/api/data/v9.2/userentityuisettingsset(${ROW})` } });
    }),
  );
  return sent;
}

describe('lastForm', () => {
  it('reads_the_main_form_of_the_mru_xml', () => {
    expect(lastFormIdOf(`<MRUForm><Form Type="Main" Id="{${FORM}}" /></MRUForm>`)).toBe(FORM.toLowerCase());
    expect(lastFormIdOf('<MRUForm><Form Type="QuickCreate" Id="{x}" /></MRUForm>')).toBeUndefined();
    expect(lastFormIdOf('not xml <')).toBeUndefined();
    expect(lastFormIdOf(null)).toBeUndefined();
    expect(mruFormXml(FORM)).toBe(`<MRUForm><Form Type="Main" Id="{${FORM.toLowerCase()}}" /></MRUForm>`);
  });

  it('read_filters_by_owner_and_table', async () => {
    const sent = mock([{ userentityuisettingsid: ROW, lastviewedformxml: mruFormXml(FORM) }]);
    expect(await readLastForm(api(), USER, 'account')).toBe(FORM.toLowerCase());
    expect(sent[0].url).toContain(`$filter=_ownerid_value eq ${USER} and objecttypecode eq 'account'`);
  });

  it('write_creates_the_row_the_first_time', async () => {
    const sent = mock([]);
    await writeLastForm(api(), USER, 'account', FORM);
    expect(sent.at(-1)).toEqual({
      method: 'POST',
      url: expect.stringContaining('userentityuisettingsset'),
      body: { objecttypecode: 'account', lastviewedformxml: mruFormXml(FORM) },
    });
  });

  it('write_updates_the_row_and_skips_an_unchanged_form', async () => {
    const other = '10000000-0000-0000-0000-00000000000b';
    const sent = mock([{ userentityuisettingsid: ROW, lastviewedformxml: mruFormXml(other) }]);
    await writeLastForm(api(), USER, 'account', FORM);
    expect(sent.at(-1)).toEqual({ method: 'PATCH', url: expect.stringContaining(`userentityuisettingsset(${ROW})`), body: { lastviewedformxml: mruFormXml(FORM) } });

    const same = mock([{ userentityuisettingsid: ROW, lastviewedformxml: mruFormXml(FORM) }]);
    await writeLastForm(api(), USER, 'account', FORM);
    expect(same.map((s) => s.method)).toEqual(['GET']);
  });
});
