import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { en } from '../../shell/i18n/en';
import { API } from '../security/testSupport';
import { decodeContent, encodeContent } from '../views/overlayApi';
import {
  canvasReady, controlById, FORM_ID, installControls, installFilterForm, openForm, PAGE_TEST_TIMEOUT, paramsOf, propsCard, saveButton,
  savedForm, section, type Call,
} from './controlsTestSupport';

type User = ReturnType<typeof userEvent.setup>;

const ROOT = '/api/data/v9.2';
const DEF = '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e31';
const ROW = 'cccccccc-0000-0000-0000-000000000001';
const WR = 'eeeeeeee-0000-0000-0000-000000000001';
const COPY_ID = '99999999-9999-9999-9999-999999999999';
const ENTRY = { sub_cases: { source: 'parentid', target: 'parentid' } };

const pathOf = (request: Request) => decodeURIComponent(new URL(request.url).pathname).slice(ROOT.length + 1);
const filterOf = (request: Request) => new URL(request.url).searchParams.get('$filter') ?? '';

interface Store {
  /** Every read of the filter store (setting definition, environment row, resource). */
  reads: string[];
  /** The resource's JSON as last written. */
  json: () => unknown;
}

/**
 * The form with the subgrid `sub_cases` (all accounts) and the lookup `parentid`, and an environment filter store: `content`
 * is the text of its resource `new_f.json` (no environment row when undefined).
 */
interface Options {
  /** Refuses the resource's PATCH. */
  failPatch?: boolean;
  /** Refuses the first read of the environment row (the read when the form opens). */
  failFirstRead?: boolean;
  /** The resource comes from a managed solution. */
  managed?: boolean;
}

function install(content: string | undefined, { failPatch = false, failFirstRead = false, managed = false }: Options = {}): { calls: Call[]; store: Store } {
  const calls = installControls();
  installFilterForm('sub_cases');
  const reads: string[] = [];
  let text = content;
  server.use(
    http.get(`${API}/settingdefinitions`, ({ request }) => {
      if (!filterOf(request).includes('xrmverse_SubgridFilters')) return undefined;
      reads.push('settingdefinitions');
      return HttpResponse.json({ value: [{ settingdefinitionid: DEF, uniquename: 'xrmverse_SubgridFilters', defaultvalue: '' }] });
    }),
    http.get(`${API}/organizationsettings`, ({ request }) => {
      if (!filterOf(request).includes(DEF)) return undefined;
      reads.push('organizationsettings');
      if (failFirstRead && reads.filter((x) => x === 'organizationsettings').length === 1) {
        return HttpResponse.json({ error: { code: '0x2', message: 'Server busy' } }, { status: 500 });
      }
      return HttpResponse.json({ value: text === undefined ? [] : [{ organizationsettingid: ROW, value: '["new_f.json"]' }] });
    }),
    http.get(`${API}/webresourceset`, ({ request }) => {
      if (!filterOf(request).includes('new_f.json')) return undefined;
      reads.push('webresourceset');
      return HttpResponse.json({ value: [{ webresourceid: WR, name: 'new_f.json', content: encodeContent(text ?? ''), ismanaged: managed }] });
    }),
    http.patch(`${API}/webresourceset*`, async ({ request }) => {
      const body = (await request.json()) as { content: string };
      calls.push({ method: 'PATCH', path: pathOf(request), body });
      if (failPatch) return HttpResponse.json({ error: { code: '0x1', message: 'Filters refused' } }, { status: 403 });
      text = decodeContent(body.content);
      return new HttpResponse(null, { status: 204 });
    }),
    http.post(`${API}/PublishXml`, async ({ request }) => {
      calls.push({ method: 'POST', path: 'PublishXml', body: (await request.json()) as Record<string, unknown> });
      return new HttpResponse(null, { status: 204 });
    }),
    http.post(`${API}/systemforms`, async ({ request }) => {
      calls.push({ method: 'POST', path: 'systemforms', body: (await request.json()) as Record<string, unknown> });
      return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x${ROOT}/systemforms(${COPY_ID})` } });
    }),
    http.post(`${API}/webresourceset`, () => {
      calls.push({ method: 'POST', path: 'webresourceset' });
      return new HttpResponse(null, { status: 204 });
    }),
    http.post(`${API}/organizationsettings`, () => {
      calls.push({ method: 'POST', path: 'organizationsettings' });
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return { calls, store: { reads, json: () => JSON.parse(text ?? 'null') } };
}

const withEntry = JSON.stringify({ forms: { [`{${FORM_ID.toUpperCase()}}`]: ENTRY } });
const radio = (panel: HTMLElement, key: 'forms.controls.records.all' | 'forms.controls.records.matchingShort') =>
  within(panel).getByRole('radio', { name: new RegExp(`^${en[key]}`) });
const matchingHint = en['forms.props.subgridMatching'].replace('{table}', 'Account').replace('{target}', 'Parent').replace('{source}', 'Parent');
const storeWrites = (calls: Call[]) => calls.filter((c) => c.path.startsWith('webresourceset') || c.path.startsWith('organizationsettings'));
const publishedResources = (calls: Call[]) => calls.filter((c) => c.path === 'PublishXml' && String(c.body?.ParameterXml).includes('<webresources>'));

async function selectSubgrid(user: User) {
  await canvasReady();
  await user.click(within(section('Controls')).getByRole('button', { name: 'All accounts (Subgrid)' }));
  return propsCard();
}

/** Under a full run the page can take more than the default second to load or save. */
const SLOW = { timeout: 5_000 };

async function save(user: User, calls: Call[]) {
  await waitFor(() => expect(saveButton()).toBeEnabled(), SLOW);
  await user.click(saveButton());
  await waitFor(() => expect(calls.some((c) => c.path === `systemforms(${FORM_ID})`)).toBe(true), SLOW);
  await waitFor(() => expect(saveButton()).toBeEnabled(), SLOW);
}

afterEach(() => sessionStorage.clear());

describe('FormDesigner subgrid filters (X.11)', { timeout: PAGE_TEST_TIMEOUT }, () => {
  it('opening a form puts its stored entry on the subgrid', async () => {
    const user = userEvent.setup();
    install(withEntry);
    openForm();
    const panel = await selectSubgrid(user);
    expect(radio(panel, 'forms.controls.records.matchingShort')).toBeChecked();
    await waitFor(() => expect(within(panel).getByText(matchingHint)).toBeInTheDocument());
  });

  it('saving writes the entries of the subgrids, and the FormXML keeps an empty relationship', async () => {
    const user = userEvent.setup();
    const { calls, store } = install('{"forms":{}}');
    openForm();
    const panel = await selectSubgrid(user);
    await user.click(radio(panel, 'forms.controls.records.matchingShort'));
    await waitFor(() => expect(within(panel).getByText(matchingHint)).toBeInTheDocument());
    await save(user, calls);
    await waitFor(() => expect(publishedResources(calls)).toHaveLength(1));
    expect(store.json()).toEqual({ forms: { [FORM_ID]: ENTRY } });
    expect(paramsOf(controlById(savedForm(calls), 'sub_cases'))).toContainEqual(['RelationshipName', '']);
  });

  it('an unchanged save reads the store again but writes nothing', async () => {
    const user = userEvent.setup();
    const { calls, store } = install(withEntry);
    openForm();
    await canvasReady();
    await save(user, calls);
    await waitFor(() => expect(store.reads.filter((r) => r === 'webresourceset')).toHaveLength(2));
    expect(storeWrites(calls)).toEqual([]);
  });

  it('a subgrid switched back to all records drops its entry', async () => {
    const user = userEvent.setup();
    const { calls, store } = install(withEntry);
    openForm();
    const panel = await selectSubgrid(user);
    await user.click(radio(panel, 'forms.controls.records.all'));
    await save(user, calls);
    await waitFor(() => expect(publishedResources(calls)).toHaveLength(1));
    expect(store.json()).toEqual({ forms: {} });
  });

  it('saving with nothing changed and no entry anywhere sends no filter request', async () => {
    const user = userEvent.setup();
    const { calls, store } = install(undefined);
    openForm();
    await canvasReady();
    await waitFor(() => expect(store.reads).toEqual(['settingdefinitions', 'organizationsettings']));
    await save(user, calls);
    await waitFor(() => expect(calls.some((c) => c.path === 'PublishXml')).toBe(true));
    expect(store.reads).toEqual(['settingdefinitions', 'organizationsettings']);
    expect(storeWrites(calls)).toEqual([]);
  });

  it('a failed write shows the error and keeps the page changed, so Back still asks', async () => {
    const user = userEvent.setup();
    const { calls } = install('{"forms":{}}', { failPatch: true });
    openForm();
    const panel = await selectSubgrid(user);
    await user.click(radio(panel, 'forms.controls.records.matchingShort'));
    await waitFor(() => expect(within(panel).getByText(matchingHint)).toBeInTheDocument());
    await user.click(saveButton());
    const prefix = en['forms.filters.saveFailed'].replace('{detail}', '');
    expect(await screen.findByText((text) => text.startsWith(prefix) && text.includes('Filters refused'))).toBeInTheDocument();
    expect(publishedResources(calls)).toEqual([]);
    await waitFor(() => expect(saveButton()).toBeEnabled());
    await user.click(screen.getByRole('button', { name: en['forms.designer.back'] }));
    expect(await screen.findByRole('dialog')).toHaveTextContent(en['forms.designer.leave.title']);
  });

  it('Save as copies the entries to the new form', async () => {
    const user = userEvent.setup();
    const { calls, store } = install(withEntry);
    openForm();
    await canvasReady();
    await waitFor(() => expect(screen.getByRole('button', { name: en['forms.designer.saveAs'] })).toBeEnabled(), SLOW);
    await user.click(screen.getByRole('button', { name: en['forms.designer.saveAs'] }));
    await waitFor(() => expect(publishedResources(calls)).toHaveLength(1));
    expect(store.json()).toEqual({ forms: { [`{${FORM_ID.toUpperCase()}}`]: ENTRY, [COPY_ID]: ENTRY } });
  });

  it('a store not read at open is announced, and a filter added then is not written: the page stays changed', async () => {
    const user = userEvent.setup();
    const { calls } = install(withEntry, { failFirstRead: true });
    openForm();
    const loadPrefix = en['forms.filters.loadFailed'].replace('{detail}', '');
    expect(await screen.findByText((text) => text.startsWith(loadPrefix) && text.includes('Server busy'))).toBeInTheDocument();
    const panel = await selectSubgrid(user);
    // The stored filter is not shown: the subgrid reads as all records.
    expect(radio(panel, 'forms.controls.records.all')).toBeChecked();
    await user.click(radio(panel, 'forms.controls.records.matchingShort'));
    await waitFor(() => expect(within(panel).getByText(matchingHint)).toBeInTheDocument());
    await user.click(saveButton());
    const prefix = en['forms.filters.saveFailed'].replace('{detail}', '');
    expect(await screen.findByText((text) => text.startsWith(prefix) && text.includes('Server busy'))).toBeInTheDocument();
    expect(storeWrites(calls)).toEqual([]);
    expect(publishedResources(calls)).toEqual([]);
    await waitFor(() => expect(saveButton()).toBeEnabled(), SLOW);
    await user.click(screen.getByRole('button', { name: en['forms.designer.back'] }));
    expect(await screen.findByRole('dialog')).toHaveTextContent(en['forms.designer.leave.title']);
  });

  it('Save as of a form whose store was not read at open does not drop the entries silently', async () => {
    const user = userEvent.setup();
    const { calls } = install(withEntry, { failFirstRead: true });
    openForm();
    await canvasReady();
    await waitFor(() => expect(screen.getByRole('button', { name: en['forms.designer.saveAs'] })).toBeEnabled(), SLOW);
    await user.click(screen.getByRole('button', { name: en['forms.designer.saveAs'] }));
    const prefix = en['forms.filters.saveFailed'].replace('{detail}', '');
    expect(await screen.findByText((text) => text.startsWith(prefix) && text.includes('Server busy'))).toBeInTheDocument();
    expect(storeWrites(calls)).toEqual([]);
  });

  it('a managed resource is announced; its filters are shown', async () => {
    const user = userEvent.setup();
    install(withEntry, { managed: true });
    openForm();
    const reason = en['forms.filters.reason.managed'];
    expect(await screen.findByText(en['forms.filters.unreadable'].replace('{reason}', reason))).toBeInTheDocument();
    const panel = await selectSubgrid(user);
    expect(radio(panel, 'forms.controls.records.matchingShort')).toBeChecked();
  });

  it('an unreadable store is announced and the form opens without filters', async () => {
    install('{"forms":[]}');
    openForm();
    await canvasReady();
    const reason = en['forms.look.reason.forms'];
    expect(await screen.findByText(en['forms.filters.unreadable'].replace('{reason}', reason))).toBeInTheDocument();
  });
});
