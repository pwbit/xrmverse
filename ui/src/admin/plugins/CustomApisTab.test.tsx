import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, signInAsAdministrator } from '../security/testSupport';
import { servePlugins } from './pluginTestSupport';

const ECHO = 'dddddddd-7777-0000-0000-000000000001';
const RENAME = 'dddddddd-7777-0000-0000-000000000002';

const apis = [
  {
    customapiid: ECHO, uniquename: 'xt_Echo', name: 'Echo', displayname: 'Echo API', description: 'Echoes text',
    bindingtype: 0, boundentitylogicalname: null, isfunction: false, isprivate: false,
    plugintypeid: { plugintypeid: 'eeeeeeee-7777-0000-0000-000000000001', typename: 'Acme.Plugins.EchoPlugin' },
  },
  {
    customapiid: RENAME, uniquename: 'xt_Rename', name: 'Rename', displayname: 'Rename', description: null,
    bindingtype: 1, boundentitylogicalname: 'account', isfunction: true, isprivate: true, plugintypeid: null,
  },
];
const parameters = [
  { uniquename: 'Text', type: 10, isoptional: false, logicalentityname: null, _customapiid_value: ECHO },
  { uniquename: 'Times', type: 7, isoptional: true, logicalentityname: null, _customapiid_value: ECHO },
  { uniquename: 'Target', type: 5, isoptional: false, logicalentityname: 'account', _customapiid_value: RENAME },
];
const properties = [{ uniquename: 'Echoed', type: 10, logicalentityname: null, _customapiid_value: ECHO }];

/** Serves the three reads of the tab; `seen` says which were made (the tab reads all three when it opens). */
function serveCustomApis() {
  const seen = { apis: [] as string[], parameters: 0, properties: 0, writes: 0 };
  server.use(
    http.get(`${API}/customapis`, ({ request }) => {
      seen.apis.push(new URL(request.url).search);
      return HttpResponse.json({ value: apis });
    }),
    http.get(`${API}/customapirequestparameters`, () => {
      seen.parameters++;
      return HttpResponse.json({ value: parameters });
    }),
    http.get(`${API}/customapiresponseproperties`, () => {
      seen.properties++;
      return HttpResponse.json({ value: properties });
    }),
    // Any write at all counts (MSW reads parentheses in a path pattern as groups, so the pattern is the Web API root).
    http.post(`${API}/*`, () => (seen.writes++, new HttpResponse(null, { status: 204 }))),
    http.patch(`${API}/*`, () => (seen.writes++, new HttpResponse(null, { status: 204 }))),
    http.delete(`${API}/*`, () => (seen.writes++, new HttpResponse(null, { status: 204 }))),
  );
  return seen;
}

beforeEach(() => signInAsAdministrator());
afterEach(() => sessionStorage.clear());

async function openTab(parameterPages = 1, afterServe?: (seen: ReturnType<typeof serveCustomApis>) => void) {
  servePlugins();
  const seen = serveCustomApis();
  afterServe?.(seen);
  const user = userEvent.setup();
  render(<App initialEntries={['/manage/plugins']} />);
  await user.click(await screen.findByRole('tab', { name: en['plugins.tab.customApis'] }));
  // Wait for every request the tab makes when it opens, so none is left running when the test ends.
  await waitFor(() => expect([seen.apis.length, seen.parameters, seen.properties]).toEqual([1, parameterPages, 1]));
  return { seen, user };
}

describe('CustomApisTab', () => {
  it('lists the custom APIs with binding, kind, private, plug-in type and the counts of parameters and properties', async () => {
    const { seen } = await openTab();
    const table = await screen.findByRole('table', { name: en['plugins.tab.customApis'] });
    const rows = within(table).getAllByRole('row');
    const headers = within(rows[0]).getAllByRole('columnheader').map((h) => h.textContent);
    expect(headers).toEqual([
      en['customApis.col.name'], en['customApis.col.binding'], en['customApis.col.kind'], en['customApis.col.private'],
      en['customApis.col.pluginType'], en['customApis.col.parameters'], en['customApis.col.properties'],
    ]);
    const cells = (row: HTMLElement) => within(row).getAllByRole('cell').map((c) => c.textContent);
    expect(cells(rows[1])).toEqual(['xt_Echo', 'global', en['customApis.kind.action'], en['common.no'], 'Acme.Plugins.EchoPlugin', '2', '1']);
    expect(cells(rows[2])).toEqual(['xt_Rename', 'entity:account', en['customApis.kind.function'], en['common.yes'], '', '1', '0']);

    const query = new URLSearchParams(seen.apis[0]);
    expect(query.get('$expand')).toBe('plugintypeid($select=typename)');
    expect(query.get('$select')).not.toContain('iscustomizable');
    expect(screen.getByText(en['customApis.import'])).toBeInTheDocument();
  });

  it('shows the parameters and properties of the selected API', async () => {
    const { user } = await openTab();
    await user.click(await screen.findByRole('button', { name: en['customApis.open'].replace('{name}', 'xt_Echo') }));
    const panel = await screen.findByRole('region', { name: en['customApis.detail'].replace('{name}', 'xt_Echo') });
    expect(within(panel).getByText('Echoes text')).toBeInTheDocument();
    const parameterRows = within(within(panel).getByRole('table', { name: en['customApis.parameters'] })).getAllByRole('row').slice(1);
    expect(parameterRows.map((r) => within(r).getAllByRole('cell').map((c) => c.textContent))).toEqual([
      ['Text', 'String', en['common.no'], ''],
      ['Times', 'Integer', en['common.yes'], ''],
    ]);
    const propertyRows = within(within(panel).getByRole('table', { name: en['customApis.properties'] })).getAllByRole('row').slice(1);
    expect(propertyRows.map((r) => within(r).getAllByRole('cell').map((c) => c.textContent))).toEqual([['Echoed', 'String', '']]);

    await user.click(screen.getByRole('button', { name: en['customApis.open'].replace('{name}', 'xt_Rename') }));
    const second = await screen.findByRole('region', { name: en['customApis.detail'].replace('{name}', 'xt_Rename') });
    expect(within(second).getByText('EntityReference')).toBeInTheDocument();
    expect(within(second).getByText('account')).toBeInTheDocument();
  });

  it('has no create, edit or delete control and sends no write', async () => {
    const { seen, user } = await openTab();
    await user.click(await screen.findByRole('button', { name: en['customApis.open'].replace('{name}', 'xt_Echo') }));
    await screen.findByRole('region', { name: en['customApis.detail'].replace('{name}', 'xt_Echo') });
    const names = screen.getAllByRole('button').map((b) => b.getAttribute('aria-label') ?? b.textContent ?? '');
    expect(names.filter((n) => /\b(create|new|add|edit|delete|remove|save)\b/i.test(n))).toEqual([]);
    expect(screen.queryByRole('textbox')).toBeNull();
    expect(seen.writes).toBe(0);
  });

  it('reads every page of the parameters, so the counts and the detail are complete', async () => {
    const { user } = await openTab(2, (seen) =>
      server.use(
        http.get(`${API}/customapirequestparameters`, ({ request }) => {
          seen.parameters++;
          const url = new URL(request.url);
          // Times (page 1) and Text, Target (page 2) of the same API are joined from two pages.
          if (url.searchParams.has('$skiptoken')) return HttpResponse.json({ value: [parameters[0], parameters[2]] });
          return HttpResponse.json({ value: [parameters[1]], '@odata.nextLink': `${url.origin}${url.pathname}?$skiptoken=2` });
        }),
      ),
    );
    const table = await screen.findByRole('table', { name: en['plugins.tab.customApis'] });
    const echo = within(table).getAllByRole('row')[1];
    expect(within(echo).getAllByRole('cell')[5].textContent).toBe('2');
    await user.click(screen.getByRole('button', { name: en['customApis.open'].replace('{name}', 'xt_Echo') }));
    const panel = await screen.findByRole('region', { name: en['customApis.detail'].replace('{name}', 'xt_Echo') });
    expect(within(within(panel).getByRole('table', { name: en['customApis.parameters'] })).getAllByRole('row')).toHaveLength(3);
  });
});
