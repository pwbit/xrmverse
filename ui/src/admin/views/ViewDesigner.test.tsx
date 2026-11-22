import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { parseXml } from '../../parsers/xml';
import { decodeContent, encodeContent } from './overlayApi';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';
import { installMetadata, rawAttr, rawEntity, rawOneToMany } from '../tables/tableTestSupport';

const ID = '11111111-1111-1111-1111-111111111111';
const NEW_ID = '99999999-9999-9999-9999-999999999999';
const ROOT = '/api/data/v9.2';
const DEF = 'dddddddd-0000-0000-0000-000000000001';
const WR = 'eeeeeeee-0000-0000-0000-000000000001';
const OVERLAY = { version: 1, theme: { density: 'compact' }, views: { [`{${ID}}`]: { kanban: { groupBy: 'statuscode' } } } };

const account = rawEntity(
  'account',
  'accounts',
  [
    rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }),
    rawAttr('name', 'String', { IsPrimaryName: true, display: 'Account Name' }),
    rawAttr('telephone1', 'String', { display: 'Main Phone' }),
    rawAttr('revenue', 'Money', { display: 'Annual Revenue' }),
    rawAttr('primarycontactid', 'Lookup', { display: 'Primary Contact', Targets: ['contact'] }),
  ],
  { PrimaryIdAttribute: 'accountid', PrimaryNameAttribute: 'name', display: 'Account' },
);
const contact = rawEntity(
  'contact',
  'contacts',
  [
    rawAttr('contactid', 'Uniqueidentifier', { IsPrimaryId: true }),
    rawAttr('fullname', 'String', { IsPrimaryName: true, display: 'Full Name' }),
    rawAttr('emailaddress1', 'String', { display: 'Email' }),
  ],
  { PrimaryIdAttribute: 'contactid', PrimaryNameAttribute: 'fullname', display: 'Contact' },
);

const FETCH =
  '<fetch version="1.0" mapping="logical"><entity name="account"><attribute name="name"/><attribute name="telephone1"/>' +
  '<attribute name="accountid"/><order attribute="name" descending="false"/>' +
  '<filter type="and"><condition attribute="statecode" operator="eq" value="0"/></filter></entity></fetch>';
const LAYOUT =
  '<grid name="resultset" jump="name" select="1" icon="1" preview="1"><row name="result" id="accountid">' +
  '<cell name="name" width="300"/><cell name="telephone1" width="125"/></row></grid>';

const record = (over: Record<string, unknown> = {}) => ({
  savedqueryid: ID,
  name: 'Active Accounts',
  description: 'All the active ones',
  querytype: 0,
  isdefault: true,
  ismanaged: false,
  fetchxml: FETCH,
  layoutxml: LAYOUT,
  returnedtypecode: 'account',
  ...over,
});

interface Call { method: string; path: string; body?: Record<string, unknown> }
let calls: Call[];
let current: ReturnType<typeof record>;
let previewFetches: string[];
let previewAnswer: () => Response;

function install() {
  server.use(
    onPath('get', `savedqueries(${ID})`, () => HttpResponse.json(current)),
    http.post(`${API}/savedqueries`, async ({ request }) => {
      calls.push({ method: 'POST', path: 'savedqueries', body: (await request.json()) as Record<string, unknown> });
      return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x${ROOT}/savedqueries(${NEW_ID})` } });
    }),
    http.patch(`${API}/*`, async ({ request }) => {
      calls.push({
        method: 'PATCH',
        path: decodeURIComponent(new URL(request.url).pathname).slice(ROOT.length + 1),
        body: (await request.json()) as Record<string, unknown>,
      });
      return new HttpResponse(null, { status: 204 });
    }),
    http.post(`${API}/PublishXml`, async ({ request }) => {
      calls.push({ method: 'POST', path: 'PublishXml', body: (await request.json()) as Record<string, unknown> });
      return new HttpResponse(null, { status: 204 });
    }),
    // The environment's presentation overlay (E11), the one the view modes card edits.
    http.get(`${API}/settingdefinitions`, () => HttpResponse.json({ value: [{ settingdefinitionid: DEF, uniquename: 'xrmverse_Presentation' }] })),
    http.get(`${API}/organizationsettings`, () => HttpResponse.json({ value: [{ value: 'new_look.json' }] })),
    http.get(`${API}/appsettings`, () => HttpResponse.json({ value: [] })),
    http.get(`${API}/webresourceset`, () =>
      HttpResponse.json({ value: [{ webresourceid: WR, name: 'new_look.json', content: encodeContent(JSON.stringify(OVERLAY)) }] }),
    ),
    http.get(`${API}/accounts`, ({ request }) => {
      previewFetches.push(new URL(request.url).searchParams.get('fetchXml') ?? '');
      return previewAnswer();
    }),
  );
}

beforeEach(() => {
  signInAsAdministrator();
  calls = [];
  previewFetches = [];
  current = record();
  previewAnswer = () =>
    HttpResponse.json({
      value: [
        { accountid: 'a1', name: 'Contoso', telephone1: '555-0100' },
        { accountid: 'a2', name: 'Fabrikam', telephone1: '555-0200' },
      ],
    });
  installMetadata([account, contact], [rawOneToMany('account_primary_contact', 'account', 'primarycontactid', 'contact')]);
  install();
});
afterEach(() => {
  vi.useRealTimers();
  sessionStorage.clear();
});

let router: { state: { location: { pathname: string } }; navigate(to: string | number): Promise<void> };

function open(id = ID) {
  render(
    <App
      initialEntries={[`/manage/tables/account/views/${id}`]}
      onRouter={(r) => {
        router = r as unknown as typeof router;
      }}
    />,
  );
}

const nameInput = () => screen.findByRole('textbox', { name: en['views.designer.name'] });
const columnsList = () => screen.getByRole('list', { name: en['views.columns.title'] });
const chipNames = () => within(columnsList()).getAllByRole('listitem').map((li) => li.getAttribute('aria-label'));
const saveButton = () => screen.getByRole('button', { name: en['views.designer.save'] });
const saved = (method: string) => calls.filter((c) => c.method === method && c.path.startsWith('savedqueries'));
const cellsOf = (layoutxml: unknown) =>
  Array.from(parseXml(String(layoutxml)).getElementsByTagName('cell')).map((c) => `${c.getAttribute('name')}:${c.getAttribute('width')}`);

// The whole App renders for every test and the column tests click a lot: under a full run they need more than 5 s.
describe('ViewDesigner', { timeout: 15_000 }, () => {
  it('loads a view: properties, columns in order with widths, sort and preview rows', async () => {
    open();
    expect(await nameInput()).toHaveValue('Active Accounts');
    expect(screen.getByRole('textbox', { name: en['views.designer.description'] })).toHaveValue('All the active ones');
    expect(chipNames()).toEqual(['Account Name', 'Main Phone']);
    expect(screen.getByRole('spinbutton', { name: 'Width of Account Name' })).toHaveValue(300);
    expect(screen.getByRole('spinbutton', { name: 'Width of Main Phone' })).toHaveValue(125);
    expect(screen.getByRole('combobox', { name: 'Sort column 1' })).toHaveValue('name');
    expect(screen.getByRole('combobox', { name: 'Sort direction 1' })).toHaveValue('asc');
    const preview = await screen.findByRole('table', { name: en['views.preview.label'] });
    expect(await within(preview).findByText('Contoso')).toBeInTheDocument();
    expect(within(preview).getByText('555-0200')).toBeInTheDocument();
    expect(within(preview).getByRole('columnheader', { name: 'Main Phone' })).toBeInTheDocument();
    expect(screen.queryByText(en['views.designer.managed'])).not.toBeInTheDocument();
  });

  it('heads a loaded related column "{column} ({lookup})"', async () => {
    current = record({
      fetchxml: FETCH.replace('<attribute name="accountid"/>',
        '<attribute name="accountid"/><link-entity name="contact" from="contactid" to="primarycontactid" link-type="outer" alias="pc"><attribute name="emailaddress1"/></link-entity>'),
      layoutxml: LAYOUT.replace('</row>', '<cell name="pc.emailaddress1" width="200"/></row>'),
    });
    open();
    await nameInput();
    await waitFor(() => expect(chipNames()).toEqual(['Account Name', 'Main Phone', 'Email (Primary Contact)']));
    expect(screen.getByRole('spinbutton', { name: 'Width of Email (Primary Contact)' })).toHaveValue(200);
  });

  it('new: a Public view with the primary name; Save creates, publishes, moves to the new id, then a second Save updates', async () => {
    const user = userEvent.setup();
    open('new');
    const name = await nameInput();
    expect(name).toHaveValue('');
    expect(chipNames()).toEqual(['Account Name']);
    expect(screen.getByRole('spinbutton', { name: 'Width of Account Name' })).toHaveValue(150);
    expect(saveButton()).toBeDisabled();
    await user.type(name, 'My view');
    expect(saveButton()).toBeEnabled();
    await user.click(saveButton());
    await waitFor(() => expect(router.state.location.pathname).toBe(`/manage/tables/account/views/${NEW_ID}`));
    expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual(['POST savedqueries', 'POST PublishXml']);
    expect(calls[0].body).toMatchObject({ name: 'My view', querytype: 0, returnedtypecode: 'account' });
    expect(calls[1].body?.ParameterXml).toBe('<importexportxml><entities><entity>account</entity></entities></importexportxml>');

    await user.type(name, '!');
    await user.click(saveButton());
    await waitFor(() => expect(saved('PATCH')).toHaveLength(1));
    expect(saved('PATCH')[0]).toMatchObject({ path: `savedqueries(${NEW_ID})`, body: { name: 'My view!' } });
    expect(saved('POST')).toHaveLength(1);
  });

  it('disables Save while saving, so two clicks create one view', async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    server.use(
      http.post(`${API}/savedqueries`, async () => {
        calls.push({ method: 'POST', path: 'savedqueries' });
        await gate;
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x${ROOT}/savedqueries(${NEW_ID})` } });
      }),
    );
    const user = userEvent.setup();
    open('new');
    await user.type(await nameInput(), 'Once');
    await user.click(saveButton());
    await waitFor(() => expect(saveButton()).toBeDisabled());
    await user.click(saveButton());
    release();
    await waitFor(() => expect(router.state.location.pathname).toBe(`/manage/tables/account/views/${NEW_ID}`));
    expect(saved('POST')).toHaveLength(1);
    await waitFor(() => expect(saveButton()).toBeEnabled());
  });

  it('a failed publish after a create keeps the new id: the error shows and the next Save updates it', async () => {
    let publishes = 0;
    server.use(
      http.post(`${API}/PublishXml`, () => {
        publishes += 1;
        calls.push({ method: 'POST', path: 'PublishXml' });
        return publishes === 1
          ? HttpResponse.json({ error: { code: '0x80040216', message: 'Publish went wrong' } }, { status: 500 })
          : new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    open('new');
    await user.type(await nameInput(), 'Draft');
    await user.click(saveButton());
    expect(await screen.findByText(/Publish went wrong/)).toBeInTheDocument();
    await waitFor(() => expect(router.state.location.pathname).toBe(`/manage/tables/account/views/${NEW_ID}`));
    await waitFor(() => expect(saveButton()).toBeEnabled());
    await user.click(saveButton());
    await waitFor(() => expect(saved('PATCH')).toHaveLength(1));
    expect(saved('PATCH')[0].path).toBe(`savedqueries(${NEW_ID})`);
    expect(saved('POST')).toHaveLength(1);
    await waitFor(() => expect(publishes).toBe(2));
    await waitFor(() => expect(screen.queryByText(/Publish went wrong/)).not.toBeInTheDocument());
  });

  it('a change made while saving keeps the page changed', async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    server.use(
      http.patch(`${API}/*`, async () => {
        calls.push({ method: 'PATCH', path: 'savedqueries' });
        await gate;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    open();
    const name = await nameInput();
    await user.type(name, ' A');
    await user.click(saveButton());
    await waitFor(() => expect(saveButton()).toBeDisabled());
    await user.type(name, 'B');
    release();
    await waitFor(() => expect(saveButton()).toBeEnabled());
    await user.click(screen.getByRole('button', { name: en['views.designer.back'] }));
    expect(await screen.findByRole('dialog')).toHaveTextContent(en['views.designer.leave.title']);
  });

  it('a width that clamps to the same value resyncs the text and leaves the page unchanged', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    const width = screen.getByRole('spinbutton', { name: 'Width of Main Phone' });
    await user.clear(width);
    await user.type(width, '125.2');
    await user.tab();
    expect(width).toHaveValue(125);
    await user.click(screen.getByRole('button', { name: en['views.designer.back'] }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/manage/tables/account'));
  });

  it('a width typed above the maximum twice still shows the maximum', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    const width = screen.getByRole('spinbutton', { name: 'Width of Main Phone' });
    for (let i = 0; i < 2; i += 1) {
      await user.clear(width);
      await user.type(width, '5000');
      await user.tab();
      expect(width).toHaveValue(1000);
    }
  });

  it('Add column lists the table columns and, by lookup, the related table columns; a related one is headed "{column} ({lookup})"', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    await user.click(screen.getByRole('button', { name: en['views.columns.add'] }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByRole('button', { name: 'Annual Revenue' })).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Main Phone' })).not.toBeInTheDocument();
    const source = within(dialog).getByRole('combobox', { name: en['views.addColumn.source'] });
    await user.selectOptions(source, within(dialog).getByRole('option', { name: 'Primary Contact' }));
    await user.click(await within(dialog).findByRole('button', { name: 'Full Name' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    // The closing dialog gives the page back to assistive technology asynchronously: wait for the list.
    await waitFor(() => expect(chipNames()).toEqual(['Account Name', 'Main Phone', 'Full Name (Primary Contact)']));

    await user.click(saveButton());
    await waitFor(() => expect(saved('PATCH')).toHaveLength(1));
    const fetch = parseXml(String(saved('PATCH')[0].body?.fetchxml));
    const link = fetch.getElementsByTagName('link-entity')[0];
    expect(link.getAttribute('name')).toBe('contact');
    expect(link.getAttribute('alias')).toBe('a_primarycontactid');
    expect(cellsOf(saved('PATCH')[0].body?.layoutxml)).toEqual(['name:300', 'telephone1:125', 'a_primarycontactid.fullname:100']);
  });

  it('moves, removes and resizes columns; the width clamps; the saved LayoutXML follows', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    await user.click(screen.getByRole('button', { name: en['views.columns.add'] }));
    await user.click(await within(await screen.findByRole('dialog')).findByRole('button', { name: 'Annual Revenue' }));
    await waitFor(() => expect(chipNames()).toEqual(['Account Name', 'Main Phone', 'Annual Revenue']));
    await user.click(screen.getByRole('button', { name: 'Move Annual Revenue left' }));
    expect(chipNames()).toEqual(['Account Name', 'Annual Revenue', 'Main Phone']);
    expect(screen.getByRole('button', { name: 'Move Account Name left' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Move Main Phone right' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Move Account Name right' }));
    expect(chipNames()).toEqual(['Annual Revenue', 'Account Name', 'Main Phone']);
    await user.click(screen.getByRole('button', { name: 'Remove Main Phone' }));
    expect(chipNames()).toEqual(['Annual Revenue', 'Account Name']);
    const width = screen.getByRole('spinbutton', { name: 'Width of Annual Revenue' });
    await user.clear(width);
    await user.type(width, '5000');
    await user.tab();
    expect(width).toHaveValue(1000);
    await user.clear(width);
    await user.type(width, '3{Enter}');
    expect(width).toHaveValue(25);

    await user.click(saveButton());
    await waitFor(() => expect(saved('PATCH')).toHaveLength(1));
    expect(cellsOf(saved('PATCH')[0].body?.layoutxml)).toEqual(['revenue:25', 'name:300']);
  });

  it('sort: up to two rows; a third is disabled; the orders are saved in that order', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    const add = screen.getByRole('button', { name: en['views.sort.add'] });
    expect(add).toBeEnabled();
    await user.click(add);
    expect(add).toBeDisabled();
    await user.selectOptions(screen.getByRole('combobox', { name: 'Sort column 2' }), 'revenue');
    await user.selectOptions(screen.getByRole('combobox', { name: 'Sort direction 2' }), 'desc');
    await user.click(saveButton());
    await waitFor(() => expect(saved('PATCH')).toHaveLength(1));
    const orders = Array.from(parseXml(String(saved('PATCH')[0].body?.fetchxml)).getElementsByTagName('order'));
    expect(orders.map((o) => `${o.getAttribute('attribute')}:${o.getAttribute('descending')}`)).toEqual(['name:false', 'revenue:true']);
    await user.click(screen.getByRole('button', { name: 'Remove sort 1' }));
    expect(screen.getByRole('combobox', { name: 'Sort column 1' })).toHaveValue('revenue');
    expect(add).toBeEnabled();
  });

  it('a sort the designer cannot edit shows the notice and no controls', async () => {
    current = record({
      fetchxml: FETCH.replace('<order attribute="name" descending="false"/>',
        '<order attribute="name" descending="false"/><order attribute="telephone1"/><order attribute="revenue"/>'),
    });
    open();
    await nameInput();
    expect(screen.getByText(en['views.sort.readOnly'])).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: en['views.sort.add'] })).not.toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: 'Sort column 1' })).not.toBeInTheDocument();
  });

  it('a managed view shows the info bar; a refused save shows the server message verbatim', async () => {
    current = record({ ismanaged: true });
    server.use(
      http.patch(`${API}/*`, () =>
        HttpResponse.json({ error: { code: '0x80048405', message: 'The component cannot be customized & stays as it is.' } }, { status: 400 }),
      ),
    );
    const user = userEvent.setup();
    open();
    await nameInput();
    expect(screen.getByText(en['views.designer.managed'])).toBeInTheDocument();
    await user.click(saveButton());
    expect(await screen.findByText(/The component cannot be customized & stays as it is\./)).toBeInTheDocument();
    expect(saveButton()).toBeEnabled();
  });

  it('Save as posts a copy named "{name} (copy)" and opens it', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    await user.click(screen.getByRole('button', { name: en['views.designer.saveAs'] }));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/manage/tables/account/views/${NEW_ID}`));
    expect(saved('POST')[0].body).toMatchObject({ name: 'Active Accounts (copy)', querytype: 0 });
    expect(await nameInput()).toHaveValue('Active Accounts (copy)');
    expect(saved('PATCH')).toHaveLength(0);
  });

  it('Save as of a name near the limit truncates the base so the copy name fits 200 characters', async () => {
    current = record({ name: 'N'.repeat(200) });
    const user = userEvent.setup();
    open();
    await nameInput();
    await user.click(screen.getByRole('button', { name: en['views.designer.saveAs'] }));
    await waitFor(() => expect(saved('POST')).toHaveLength(1));
    const name = String(saved('POST')[0].body?.name);
    expect(name).toHaveLength(200);
    expect(name).toBe(`${'N'.repeat(193)} (copy)`);
  });

  it('Back after Save and Save as reads the view again: it shows the saved edits and a Save keeps them', async () => {
    let reads = 0;
    server.use(
      onPath('get', `savedqueries(${ID})`, () => {
        reads += 1;
        return HttpResponse.json(current);
      }),
      http.patch(`${API}/*`, async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        const path = decodeURIComponent(new URL(request.url).pathname).slice(ROOT.length + 1);
        calls.push({ method: 'PATCH', path, body });
        if (path === `savedqueries(${ID})`) current = { ...current, ...body } as typeof current;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    open();
    await user.type(await nameInput(), ' X');
    await user.click(screen.getByRole('button', { name: 'Remove Main Phone' }));
    await user.click(saveButton());
    await waitFor(() => expect(saved('PATCH')).toHaveLength(1));
    await waitFor(() => expect(saveButton()).toBeEnabled());
    await user.click(screen.getByRole('button', { name: en['views.designer.saveAs'] }));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/manage/tables/account/views/${NEW_ID}`));
    await waitFor(() => expect(saveButton()).toBeEnabled());
    await act(() => router.navigate(-1));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/manage/tables/account/views/${ID}`));
    await waitFor(() => expect(reads).toBe(2));
    await waitFor(() => expect(screen.getByRole('textbox', { name: en['views.designer.name'] })).toHaveValue('Active Accounts X'));
    expect(chipNames()).toEqual(['Account Name']);
    await user.click(saveButton());
    await waitFor(() => expect(saved('PATCH')).toHaveLength(2));
    const second = saved('PATCH')[1];
    expect(second.path).toBe(`savedqueries(${ID})`);
    expect(second.body?.name).toBe('Active Accounts X');
    expect(cellsOf(second.body?.layoutxml)).toEqual(['name:300']);
  });

  it.each([
    ['an empty LayoutXML', { layoutxml: '' }],
    ['a null LayoutXML', { layoutxml: null }],
    ['a malformed FetchXML', { fetchxml: '<fetch><entity name="account">' }],
    ['a FetchXML without an entity', { fetchxml: '<fetch version="1.0"/>' }],
  ])('a row with %s shows the load error instead of crashing', async (_, over) => {
    current = record(over as Record<string, unknown>);
    open();
    const prefix = en['views.designer.loadFailed'].replace('{detail}', '');
    expect(await screen.findByText((text) => text.startsWith(prefix) && text.length > prefix.length)).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: en['views.designer.name'] })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: en['views.designer.back'] })).toBeInTheDocument();
  });

  it('the load error of an unreadable row goes as soon as another view opens', async () => {
    const OTHER = '22222222-2222-2222-2222-222222222222';
    current = record({ layoutxml: '' });
    let answer: (() => void) | undefined;
    server.use(
      onPath('get', `savedqueries(${OTHER})`, async () => {
        await new Promise<void>((resolve) => (answer = resolve));
        return HttpResponse.json(record({ savedqueryid: OTHER, name: 'Other Accounts' }));
      }),
    );
    open();
    const prefix = en['views.designer.loadFailed'].replace('{detail}', '');
    const failed = (text: string) => text.startsWith(prefix) && text.length > prefix.length;
    expect(await screen.findByText(failed)).toBeInTheDocument();
    await act(() => router.navigate(`/manage/tables/account/views/${OTHER}`));
    await waitFor(() => expect(answer).toBeDefined());
    // While the other view loads, the first one's error is not shown as if it were the other's.
    expect(screen.queryByText(failed)).not.toBeInTheDocument();
    answer!();
    expect(await screen.findByDisplayValue('Other Accounts')).toBeInTheDocument();
    expect(screen.queryByText(failed)).not.toBeInTheDocument();
  });

  it('Save as is offered for Public views only', async () => {
    current = record({ querytype: 4 });
    open();
    await nameInput();
    expect(screen.queryByRole('button', { name: en['views.designer.saveAs'] })).not.toBeInTheDocument();
  });

  it('Back with unsaved changes asks first; Stay keeps the page, Leave goes back to the table', async () => {
    const user = userEvent.setup();
    open();
    const name = await nameInput();
    await user.type(name, ' 2');
    await user.click(screen.getByRole('button', { name: en['views.designer.back'] }));
    const dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveTextContent(en['views.designer.leave.title']);
    await user.click(within(dialog).getByRole('button', { name: en['common.cancel'] }));
    expect(router.state.location.pathname).toBe(`/manage/tables/account/views/${ID}`);
    await user.click(screen.getByRole('button', { name: en['views.designer.back'] }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: en['views.designer.leave.confirm'] }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/manage/tables/account'));
  });

  it('Back without changes leaves at once', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    await user.click(screen.getByRole('button', { name: en['views.designer.back'] }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/manage/tables/account'));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('the preview refreshes 500 ms after the last change', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    open();
    await nameInput();
    await waitFor(() => expect(previewFetches).toHaveLength(1));
    expect(previewFetches[0]).toContain('count="10"');
    await user.click(screen.getByRole('button', { name: 'Remove Main Phone' }));
    act(() => {
      vi.advanceTimersByTime(300);
    });
    expect(previewFetches).toHaveLength(1);
    act(() => {
      vi.advanceTimersByTime(250);
    });
    await waitFor(() => expect(previewFetches).toHaveLength(2));
    expect(previewFetches[1]).not.toContain('telephone1');
  });

  it('the preview is not read again when only the name or description changes', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    open();
    const name = await nameInput();
    await waitFor(() => expect(previewFetches).toHaveLength(1));
    await user.type(name, 'x');
    await user.type(screen.getByRole('textbox', { name: en['views.designer.description'] }), 'y');
    act(() => {
      vi.advanceTimersByTime(1000);
    });
    await act(async () => {
      await Promise.resolve();
    });
    expect(previewFetches).toHaveLength(1);
  });

  it('an older preview answer that arrives after a newer one is dropped', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let releaseFirst!: () => void;
    const firstGate = new Promise<void>((r) => (releaseFirst = r));
    let n = 0;
    server.use(
      http.get(`${API}/accounts`, async ({ request }) => {
        previewFetches.push(new URL(request.url).searchParams.get('fetchXml') ?? '');
        n += 1;
        if (n === 1) {
          await firstGate;
          return HttpResponse.json({ value: [{ accountid: 'a1', name: 'Stale Co', telephone1: '1' }] });
        }
        return HttpResponse.json({ value: [{ accountid: 'a2', name: 'Fresh Co' }] });
      }),
    );
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    open();
    await nameInput();
    await waitFor(() => expect(previewFetches).toHaveLength(1));
    await user.click(screen.getByRole('button', { name: 'Remove Main Phone' }));
    act(() => {
      vi.advanceTimersByTime(600);
    });
    const area = screen.getByRole('region', { name: en['views.preview.title'] });
    expect(await within(area).findByText('Fresh Co')).toBeInTheDocument();
    releaseFirst();
    await act(async () => {
      await new Promise((r) => setTimeout(r, 50));
    });
    expect(within(area).queryByText('Stale Co')).not.toBeInTheDocument();
    expect(within(area).getByText('Fresh Co')).toBeInTheDocument();
  });

  it('a preview error shows in the preview area and does not disable Save', async () => {
    previewAnswer = () => HttpResponse.json({ error: { code: '0x80041103', message: 'Preview broke' } }, { status: 400 });
    open();
    await nameInput();
    const area = screen.getByRole('region', { name: en['views.preview.title'] });
    expect(await within(area).findByText(/Preview broke/)).toBeInTheDocument();
    expect(saveButton()).toBeEnabled();
  });
  it('mounts the filter card; a condition added there is saved in the FetchXML', async () => {
    const user = userEvent.setup();
    open();
    await nameInput();
    const filter = screen.getByRole('region', { name: en['views.filter.title'] });
    expect(within(filter).getByRole('combobox', { name: 'Column of condition 1.1' })).toHaveValue('statecode');
    expect(screen.queryByRole('region', { name: en['views.find.title'] })).not.toBeInTheDocument();
    await user.click(within(filter).getByRole('button', { name: 'Add condition to group 1' }));
    await user.selectOptions(within(filter).getByRole('combobox', { name: 'Column of condition 1.2' }), 'telephone1');
    await user.type(within(filter).getByRole('textbox', { name: 'Value of condition 1.2' }), '555');
    await user.click(saveButton());
    await waitFor(() => expect(saved('PATCH')).toHaveLength(1));
    expect(String(saved('PATCH')[0].body?.fetchxml)).toContain(
      '<filter type="and"><condition attribute="statecode" operator="eq" value="0"/><condition attribute="telephone1" operator="eq" value="555"/></filter>',
    );
  });

  it('a quick find view shows the find columns card; its columns are not in the filter card', async () => {
    current = record({
      querytype: 4,
      fetchxml: FETCH.replace('</entity>', '<filter type="or" isquickfindfields="1"><condition attribute="name" operator="like" value="{0}"/></filter></entity>'),
    });
    open();
    await nameInput();
    const find = screen.getByRole('region', { name: en['views.find.title'] });
    expect(within(find).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['Account Name']);
    const filter = screen.getByRole('region', { name: en['views.filter.title'] });
    expect(within(filter).getAllByRole('combobox', { name: /^Column of/ })).toHaveLength(1);
  });
  describe('view modes', () => {
    const modesCard = () => screen.getByRole('region', { name: en['views.modes.title'] });
    // The modes card loads the record, the table metadata and the overlay one after the other; on a busy CI runner that
    // takes longer than the 1 s default of findBy (seen on PR #260), so wait up to 5 s.
    const boardSelect = () => screen.findByRole('combobox', { name: en['views.modes.board'] }, { timeout: 5000 });
    const cardsSwitch = () => within(modesCard()).getByRole('switch', { name: en['views.modes.cards'] });
    const overlayWrites = () => calls.filter((c) => c.method === 'PATCH' && c.path.startsWith('webresourceset'));
    const writtenViews = (call: Call) => (JSON.parse(decodeContent(String(call.body?.content))) as { views: Record<string, unknown> }).views;

    it('Save with only the view changed does not touch the overlay', async () => {
      const user = userEvent.setup();
      open();
      await user.type(await nameInput(), ' X');
      expect(await boardSelect()).toHaveValue('statuscode');
      await user.click(saveButton());
      await waitFor(() => expect(calls.filter((c) => c.path === 'PublishXml')).toHaveLength(1));
      await waitFor(() => expect(saveButton()).toBeEnabled());
      expect(overlayWrites()).toEqual([]);
    });

    it('with the modes changed writes the view, then the overlay', async () => {
      const user = userEvent.setup();
      open();
      await boardSelect();
      await user.click(cardsSwitch());
      await user.click(saveButton());
      await waitFor(() => expect(calls.filter((c) => c.path === 'PublishXml')).toHaveLength(2));
      expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual([`PATCH savedqueries(${ID})`, 'POST PublishXml', `PATCH webresourceset(${WR})`, 'POST PublishXml']);
      expect(writtenViews(overlayWrites()[0])).toEqual({ [`{${ID}}`]: { kanban: { groupBy: 'statuscode' }, cards: {} } });
      expect(calls[3].body).toEqual({ ParameterXml: `<importexportxml><webresources><webresource>${WR}</webresource></webresources></importexportxml>` });
    });

    it('a new view writes its overlay entry under the created id', async () => {
      const user = userEvent.setup();
      open('new');
      await user.type(await nameInput(), 'Board view');
      expect(await boardSelect()).toHaveValue('');
      await user.click(cardsSwitch());
      await user.click(saveButton());
      await waitFor(() => expect(overlayWrites()).toHaveLength(1));
      expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual(['POST savedqueries', 'POST PublishXml', `PATCH webresourceset(${WR})`, 'POST PublishXml']);
      expect(writtenViews(overlayWrites()[0])).toEqual({ [`{${ID}}`]: { kanban: { groupBy: 'statuscode' } }, [NEW_ID]: { cards: {} } });
    });

    it('an overlay failure leaves the view saved and shows the error in the card', async () => {
      server.use(
        onPath('patch', `webresourceset(${WR})`, () => HttpResponse.json({ error: { code: '0x80040265', message: 'Overlay is locked' } }, { status: 403 })),
      );
      const user = userEvent.setup();
      open();
      await boardSelect();
      await user.click(cardsSwitch());
      await user.click(saveButton());
      expect(await within(modesCard()).findByText(/Overlay is locked/)).toBeInTheDocument();
      expect(saved('PATCH')).toHaveLength(1);
      expect(calls.filter((c) => c.path === 'PublishXml')).toHaveLength(1);
      // The modes are not written yet: leaving still asks.
      await user.click(screen.getByRole('button', { name: en['views.designer.back'] }));
      expect(await screen.findByRole('dialog')).toHaveTextContent(en['views.designer.leave.title']);
    });

    it('Save as copies the view modes to the new view and leaves the source entry as it is', async () => {
      const user = userEvent.setup();
      open();
      await boardSelect();
      await user.click(screen.getByRole('button', { name: en['views.designer.saveAs'] }));
      await waitFor(() => expect(overlayWrites()).toHaveLength(1));
      expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual(['POST savedqueries', 'POST PublishXml', `PATCH webresourceset(${WR})`, 'POST PublishXml']);
      expect(writtenViews(overlayWrites()[0])).toEqual({ [`{${ID}}`]: { kanban: { groupBy: 'statuscode' } }, [NEW_ID]: { kanban: { groupBy: 'statuscode' } } });
      expect(router.state.location.pathname).toBe(`/manage/tables/account/views/${NEW_ID}`);
    });

    it('Save as of a view without modes writes nothing to the overlay', async () => {
      server.use(
        http.get(`${API}/webresourceset`, () =>
          HttpResponse.json({ value: [{ webresourceid: WR, name: 'new_look.json', content: encodeContent(JSON.stringify({ version: 1, views: { other: { cards: {} } } })) }] }),
        ),
      );
      const user = userEvent.setup();
      open();
      expect(await boardSelect()).toHaveValue('');
      await user.click(screen.getByRole('button', { name: en['views.designer.saveAs'] }));
      await waitFor(() => expect(router.state.location.pathname).toBe(`/manage/tables/account/views/${NEW_ID}`));
      await waitFor(() => expect(saveButton()).toBeEnabled());
      expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual(['POST savedqueries', 'POST PublishXml']);
    });

    it('opening another view in the same page shows its own modes, and its Save does not write the first view\'s modes', async () => {
      const OTHER = '22222222-2222-2222-2222-222222222222';
      server.use(
        onPath('get', `savedqueries(${OTHER})`, () => HttpResponse.json(record({ savedqueryid: OTHER, name: 'Other Accounts', isdefault: false }))),
        http.get(`${API}/webresourceset`, () =>
          HttpResponse.json({
            value: [{ webresourceid: WR, name: 'new_look.json', content: encodeContent(JSON.stringify({ ...OVERLAY, views: { ...OVERLAY.views, [OTHER]: { cards: {} } } })) }],
          }),
        ),
      );
      const user = userEvent.setup();
      open();
      expect(await boardSelect()).toHaveValue('statuscode');
      await act(() => router.navigate(`/manage/tables/account/views/${OTHER}`));
      await waitFor(() => expect(screen.getByRole('textbox', { name: en['views.designer.name'] })).toHaveValue('Other Accounts'));
      expect(await boardSelect()).toHaveValue('');
      await waitFor(() => expect(cardsSwitch()).toBeChecked());
      await user.type(await nameInput(), ' 2');
      await user.click(saveButton());
      await waitFor(() => expect(calls.filter((c) => c.path === 'PublishXml')).toHaveLength(1));
      await waitFor(() => expect(saveButton()).toBeEnabled());
      expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual([`PATCH savedqueries(${OTHER})`, 'POST PublishXml']);
      expect(saved('PATCH')[0].body?.name).toBe('Other Accounts 2');
    });

    it('Save as while the overlay could not be read says the view modes were not copied', async () => {
      server.use(http.get(`${API}/webresourceset`, () => HttpResponse.json({ error: { code: '0x1', message: 'Overlay read failed' } }, { status: 500 })));
      const user = userEvent.setup();
      open();
      await nameInput();
      expect(await screen.findByText(/Overlay read failed/)).toBeInTheDocument();
      await user.click(screen.getByRole('button', { name: en['views.designer.saveAs'] }));
      expect(await within(modesCard()).findByText(en['views.modes.notCopied'])).toBeInTheDocument();
      expect(overlayWrites()).toEqual([]);
    });

    it('an after-save step is skipped when PublishXml fails and runs on the retry Save', async () => {
      let publishes = 0;
      server.use(
        http.post(`${API}/PublishXml`, async ({ request }) => {
          publishes += 1;
          calls.push({ method: 'POST', path: 'PublishXml', body: (await request.json()) as Record<string, unknown> });
          return publishes === 1
            ? HttpResponse.json({ error: { code: '0x80040216', message: 'Publish went wrong' } }, { status: 500 })
            : new HttpResponse(null, { status: 204 });
        }),
      );
      const user = userEvent.setup();
      open();
      await boardSelect();
      await user.click(cardsSwitch());
      await user.click(saveButton());
      expect(await screen.findByText(/Publish went wrong/)).toBeInTheDocument();
      await waitFor(() => expect(saveButton()).toBeEnabled());
      expect(overlayWrites()).toEqual([]);
      await user.click(saveButton());
      await waitFor(() => expect(overlayWrites()).toHaveLength(1));
      expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual([
        `PATCH savedqueries(${ID})`, 'POST PublishXml', `PATCH savedqueries(${ID})`, 'POST PublishXml', `PATCH webresourceset(${WR})`, 'POST PublishXml',
      ]);
    });
  });
});
