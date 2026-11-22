import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setLibraryLoader } from '../client-api/scriptHost';
import { sourceLoader } from '../client-api/testHost';
import { onPath } from '../admin/security/testSupport';
import { installMetadata, lbl, rawAttr, rawEntity } from '../admin/tables/tableTestSupport';
import { App } from '../shell/App';
import { server } from '../test/server';
import { mockApps, ROLE_SALES, SITEMAP_ID, titled, USER_ID } from './testSupport';

const APP_ID = '11111111-0000-0000-0000-000000000001';
const SALES = { appmoduleid: APP_ID, name: 'Sales Hub', roles: [ROLE_SALES], siteMapId: SITEMAP_ID };
const SITEMAP = `<SiteMap><Area Id="sales">${titled('Sales')}<Group Id="c">${titled('Customers')}<SubArea Id="accounts" Entity="account">${titled('Accounts')}</SubArea></Group></Area></SiteMap>`;
const ACCOUNT = 'bbbbbbbb-0000-0000-0000-000000000002';
const F1 = '10000000-0000-0000-0000-000000000001';
const F2 = '10000000-0000-0000-0000-000000000002';
const F_OTHER = '10000000-0000-0000-0000-0000000000ff';

const control = (attribute: string, label: string) =>
  `<cell id="{${attribute}-cell}"><labels><label description="${label}" languagecode="1033"/></labels><control id="${attribute}" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="${attribute}"/></cell>`;
const formXml = (...cells: string[]) =>
  `<form><tabs><tab name="general"><labels><label description="General" languagecode="1033"/></labels><columns><column width="100%"><sections><section name="s" showlabel="false" columns="1"><rows>${cells.map((c) => `<row>${c}</row>`).join('')}</rows></section></sections></column></columns></tab></tabs></form>`;

const form = (id: string, name: string, xml: string, over: { table?: string; isdefault?: boolean } = {}) => ({
  systemformid: id,
  name,
  objecttypecode: over.table ?? 'account',
  type: 2,
  isdefault: over.isdefault ?? false,
  formactivationstate: 1,
  formxml: xml,
});

const account = rawEntity(
  'account',
  'accounts',
  [
    rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }),
    rawAttr('name', 'String', { IsPrimaryName: true, display: 'Account Name' }),
    rawAttr('telephone1', 'String', { display: 'Phone' }),
    rawAttr('statecode', 'State', { OptionSet: { Options: [{ Value: 0, Label: lbl('Active') }], IsGlobal: false } }),
  ],
  { SchemaName: 'Account', PrimaryNameAttribute: 'name', display: 'Account' },
);

function setup(
  over: { forms?: ReturnType<typeof form>[]; record?: () => Response; rights?: string; autoSave?: boolean; settings?: Record<string, string> } = {},
) {
  mockApps({ apps: [{ ...SALES, settings: over.settings }], userRoles: [ROLE_SALES], privileges: ['prvReadAccount', 'prvCreateAccount'], siteMapXml: SITEMAP, autoSave: over.autoSave });
  installMetadata([account]);
  const reads: string[] = [];
  server.use(
    http.get('*/api/data/v9.2/systemforms', () =>
      HttpResponse.json({
        value: over.forms ?? [form(F1, 'Main', formXml(control('name', 'Name')), { isdefault: true }), form(F2, 'Information', formXml(control('telephone1', 'Phone')))],
      }),
    ),
    onPath('get', `systemusers(${USER_ID})/Microsoft.Dynamics.CRM.RetrievePrincipalAccess(Target=@t)`, () =>
      HttpResponse.json({ AccessRights: over.rights ?? 'ReadAccess, WriteAccess' }),
    ),
    onPath('get', `accounts(${ACCOUNT})`, ({ request }) => {
      reads.push(new URL(request.url).searchParams.get('$select') ?? '');
      return over.record ? over.record() : HttpResponse.json({ accountid: ACCOUNT, name: 'Contoso', telephone1: '555-0100' });
    }),
  );
  return reads;
}

const open = (query: string) => render(<App initialEntries={[`/main.aspx?appid=${APP_ID}&pagetype=entityrecord&etn=account${query}`]} />);


// Each scenario renders the whole record page and drives it with userEvent through several steps; on a loaded CI runner
// (the web admin job runs the suite in about 1000 s) the longest take over Vitest's 5 s default, as in ViewDesigner.test.tsx.
const RECORD_PAGE_TIMEOUT = 15_000;

describe('RecordPage in the user\'s time zone (#131)', { timeout: RECORD_PAGE_TIMEOUT }, () => {
  const dated = rawEntity(
    'account',
    'accounts',
    [
      rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }),
      rawAttr('name', 'String', { IsPrimaryName: true }),
      rawAttr('lastonholdtime', 'DateTime', { display: 'On hold' }),
      rawAttr('statecode', 'State', { OptionSet: { Options: [{ Value: 0, Label: lbl('Active') }], IsGlobal: false } }),
    ],
    { SchemaName: 'Account', PrimaryNameAttribute: 'name', display: 'Account' },
  );

  it('shows_and_saves_user_local_dates_in_the_user_time_zone', async () => {
    setup({ forms: [form(F1, 'Main', formXml(control('lastonholdtime', 'On hold')), { isdefault: true })] });
    installMetadata([dated]);
    const sent: unknown[] = [];
    server.use(
      // Code 110: W. Europe Standard Time (Berlin, Rome).
      onPath('get', `usersettingscollection(${USER_ID})`, () => HttpResponse.json({ systemuserid: USER_ID, uilanguageid: 1033, timezonecode: 110 })),
      onPath('get', `accounts(${ACCOUNT})`, () => HttpResponse.json({ accountid: ACCOUNT, name: 'Contoso', lastonholdtime: '2026-03-29T01:30:00Z' })),
      onPath('patch', `accounts(${ACCOUNT})`, async ({ request }) => {
        sent.push(await request.json());
        return new HttpResponse(null, { status: 204 });
      }),
    );
    open(`&id=${ACCOUNT}`);
    // A user without formats of their own gets the en-US ones; 24-hour text is still read.
    expect(await screen.findByLabelText('On hold')).toHaveValue('3/29/2026');
    const time = screen.getByRole('combobox', { name: 'On hold time' });
    expect(time).toHaveValue('3:30 AM');
    expect(screen.getByText('Date and time in Central European Time')).toBeInTheDocument();
    await userEvent.clear(time);
    await userEvent.type(time, '04:00');
    await userEvent.tab();
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sent).toEqual([{ lastonholdtime: '2026-03-29T02:00:00Z' }]));
  });

  it('shows_and_reads_dates_in_the_users_own_formats', async () => {
    setup({ forms: [form(F1, 'Main', formXml(control('lastonholdtime', 'On hold')), { isdefault: true })] });
    installMetadata([dated]);
    const sent: unknown[] = [];
    let stored = '2026-03-29T01:30:00Z';
    server.use(
      onPath('get', `usersettingscollection(${USER_ID})`, () =>
        HttpResponse.json({ systemuserid: USER_ID, uilanguageid: 1033, timezonecode: 110, dateformatstring: 'dd/MM/yyyy', dateseparator: '.', timeformatstring: 'HH:mm' }),
      ),
      onPath('get', `accounts(${ACCOUNT})`, () => HttpResponse.json({ accountid: ACCOUNT, name: 'Contoso', lastonholdtime: stored })),
      onPath('patch', `accounts(${ACCOUNT})`, async ({ request }) => {
        const body = (await request.json()) as { lastonholdtime: string };
        sent.push(body);
        stored = body.lastonholdtime;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    open(`&id=${ACCOUNT}`);
    const date = await screen.findByLabelText('On hold');
    expect(date).toHaveValue('29.03.2026');
    expect(date).toHaveAttribute('placeholder', 'dd.MM.yyyy');
    expect(screen.getByRole('combobox', { name: 'On hold time' })).toHaveValue('03:30');
    await userEvent.type(date, '30.03.2026', { initialSelectionStart: 0, initialSelectionEnd: 10 });
    await userEvent.tab();
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sent).toEqual([{ lastonholdtime: '2026-03-30T01:30:00Z' }]));
    // The form reads the saved record again before the test ends.
    await waitFor(() => expect(screen.getByLabelText('On hold')).toHaveValue('30.03.2026'));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled());
  });
});

describe('RecordPage', { timeout: RECORD_PAGE_TIMEOUT }, () => {
  it('opens a record on its first listed form', async () => {
    setup();
    open(`&id=${ACCOUNT}`);
    expect(await screen.findByLabelText('Name')).toHaveValue('Contoso');
    expect(screen.getByRole('heading', { level: 1, name: 'Contoso' })).toBeInTheDocument();
    expect(screen.queryByText('The requested form is not available. The default form opened instead.')).toBeNull();
  });

  it('opens the create form without an id', async () => {
    setup();
    open('');
    expect(await screen.findByRole('heading', { level: 1, name: 'New Account' })).toBeInTheDocument();
    expect(screen.getByLabelText('Name')).toHaveValue('');
  });

  it('deleted_record_message_with_list_link', async () => {
    setup({ record: () => HttpResponse.json({ error: { code: '0x80040217', message: 'Does Not Exist' } }, { status: 404 }) });
    open(`&id=${ACCOUNT}`);
    expect(await screen.findByText('This record does not exist or you do not have access to it.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to the list' })).toHaveAttribute('href', `/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=account`);
  });

  it('formid_of_other_table_falls_back_with_notice', async () => {
    setup({ forms: [form(F1, 'Main', formXml(control('name', 'Name')), { isdefault: true }), form(F_OTHER, 'Contact form', formXml(control('x', 'X')), { table: 'contact' })] });
    open(`&id=${ACCOUNT}&formid=%7B${F_OTHER}%7D`);
    expect(await screen.findByText('The requested form is not available. The default form opened instead.')).toBeInTheDocument();
    expect(screen.getByLabelText('Name')).toHaveValue('Contoso');
  });

  it('shows_a_message_when_no_form_is_listed', async () => {
    setup({ forms: [] });
    open(`&id=${ACCOUNT}`);
    expect(await screen.findByText('No form of this table is available in this app.')).toBeInTheDocument();
  });

  it('switching_form_reads_the_record_with_the_new_forms_columns', async () => {
    const reads = setup();
    open(`&id=${ACCOUNT}`);
    await screen.findByLabelText('Name');
    expect(reads[0]).toBe('accountid,name,statecode');
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Form' }), F2);
    expect(await screen.findByLabelText('Phone')).toHaveValue('555-0100');
    expect(reads[1]).toBe('accountid,name,telephone1,statecode');
  });

  it('opens_on_the_users_last_form_unless_the_url_names_one', async () => {
    setup();
    server.use(
      http.get('*/api/data/v9.2/userentityuisettingsset', () =>
        HttpResponse.json({ value: [{ userentityuisettingsid: 'u1', lastviewedformxml: `<MRUForm><Form Type="Main" Id="{${F2}}" /></MRUForm>` }] }),
      ),
    );
    const { unmount } = open(`&id=${ACCOUNT}`);
    expect(await screen.findByLabelText('Phone')).toHaveValue('555-0100');
    unmount();
    open(`&id=${ACCOUNT}&formid=${F1}`);
    expect(await screen.findByLabelText('Name')).toHaveValue('Contoso');
  });

  it('choosing_a_form_stores_it_as_the_last_form', async () => {
    setup();
    const posted: unknown[] = [];
    server.use(
      http.post('*/api/data/v9.2/userentityuisettingsset', async ({ request }) => {
        posted.push(await request.json());
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': 'http://x/api/data/v9.2/userentityuisettingsset(u1)' } });
      }),
    );
    open(`&id=${ACCOUNT}`);
    await screen.findByLabelText('Name');
    // Opening on the default form stores nothing.
    expect(posted).toEqual([]);
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Form' }), F2);
    await screen.findByLabelText('Phone');
    await waitFor(() => expect(posted).toEqual([{ objecttypecode: 'account', lastviewedformxml: `<MRUForm><Form Type="Main" Id="{${F2}}" /></MRUForm>` }]));
  });

  it('a_last_form_read_failure_opens_the_first_form', async () => {
    setup();
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    server.use(
      http.get('*/api/data/v9.2/userentityuisettingsset', () => HttpResponse.json({ error: { code: '0x80040220', message: 'denied' } }, { status: 403 })),
    );
    open(`&id=${ACCOUNT}`);
    expect(await screen.findByLabelText('Name')).toHaveValue('Contoso');
    expect(warn).toHaveBeenCalled();
    warn.mockRestore();
  });

  it('a_form_read_failure_is_shown', async () => {
    setup();
    server.use(http.get('*/api/data/v9.2/systemforms', () => HttpResponse.json({ error: { code: '0x80040216', message: 'forms read failed' } }, { status: 500 })));
    open(`&id=${ACCOUNT}`);
    expect(await screen.findByText(/forms read failed/)).toBeInTheDocument();
  });
});

describe('RecordPage currency', { timeout: RECORD_PAGE_TIMEOUT }, () => {
  const money = rawEntity(
    'account',
    'accounts',
    [
      rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }),
      rawAttr('name', 'String', { IsPrimaryName: true }),
      rawAttr('revenue', 'Money', { display: 'Revenue' }),
      rawAttr('transactioncurrencyid', 'Lookup', { Targets: ['transactioncurrency'] }),
      rawAttr('statecode', 'State', { OptionSet: { Options: [{ Value: 0, Label: lbl('Active') }], IsGlobal: false } }),
    ],
    { SchemaName: 'Account', PrimaryNameAttribute: 'name', display: 'Account' },
  );
  const CUR_USER = 'eeeeeeee-0000-0000-0000-000000000001';
  const CUR_ORG = 'eeeeeeee-0000-0000-0000-000000000002';
  const CUR_REC = 'eeeeeeee-0000-0000-0000-000000000003';
  const symbols: Record<string, string> = { [CUR_USER]: '£', [CUR_ORG]: '€', [CUR_REC]: '¥' };

  function setupMoney(over: { userCurrency?: string | null; symbolFails?: boolean; recordCurrency?: string } = {}) {
    setup({ forms: [form(F1, 'Main', formXml(control('revenue', 'Revenue')), { isdefault: true })] });
    installMetadata([money]);
    server.use(
      onPath('get', `usersettingscollection(${USER_ID})`, () =>
        HttpResponse.json({ systemuserid: USER_ID, uilanguageid: 1033, paginglimit: 50, _transactioncurrencyid_value: over.userCurrency ?? null }),
      ),
      http.get('*/api/data/v9.2/organizations', () => HttpResponse.json({ value: [{ _basecurrencyid_value: CUR_ORG }] })),
      http.get('*/api/data/v9.2/*', ({ request }) => {
        const m = /transactioncurrencies\(([^)]+)\)/.exec(decodeURIComponent(new URL(request.url).pathname));
        if (!m) return undefined;
        return over.symbolFails ? HttpResponse.json({ error: { code: '0x80040216', message: 'boom' } }, { status: 500 }) : HttpResponse.json({ currencysymbol: symbols[m[1]!] });
      }),
      onPath('get', `accounts(${ACCOUNT})`, () =>
        HttpResponse.json({
          accountid: ACCOUNT,
          name: 'Contoso',
          revenue: 5,
          ...(over.recordCurrency ? { _transactioncurrencyid_value: over.recordCurrency } : {}),
        }),
      ),
    );
  }

  it('create_form_shows_the_user_default_currency_symbol', async () => {
    setupMoney({ userCurrency: CUR_USER });
    open('');
    expect(await screen.findByLabelText('Revenue (£)')).toBeInTheDocument();
  });

  it('org_base_currency_when_the_user_has_none', async () => {
    setupMoney({ userCurrency: null });
    open('');
    expect(await screen.findByLabelText('Revenue (€)')).toBeInTheDocument();
  });

  it('a_record_without_a_currency_uses_the_org_base_currency', async () => {
    setupMoney({ userCurrency: CUR_USER });
    open(`&id=${ACCOUNT}`);
    // The record has none: the organization's base currency, as Dataverse displays it.
    expect(await screen.findByLabelText('Revenue (€)')).toHaveValue('5.00');
  });

  it('a_record_with_a_currency_uses_that_currency', async () => {
    setupMoney({ userCurrency: CUR_USER, recordCurrency: CUR_REC });
    open(`&id=${ACCOUNT}`);
    expect(await screen.findByLabelText('Revenue (¥)')).toHaveValue('5.00');
  });

  it('a_symbol_fetch_failure_shows_a_notification_and_plain_labels', async () => {
    setupMoney({ userCurrency: CUR_USER, symbolFails: true });
    open(`&id=${ACCOUNT}`);
    expect(await screen.findByText('The currency symbol could not be loaded.')).toBeInTheDocument();
    expect(screen.getByLabelText('Revenue')).toBeInTheDocument();
  });
});

describe('RecordPage commands', { timeout: RECORD_PAGE_TIMEOUT }, () => {
  const NEW = 'bbbbbbbb-0000-0000-0000-0000000000aa';
  const OWNER = 'aaaaaaaa-0000-0000-0000-0000000000a1';
  const FULL = 'ReadAccess, WriteAccess, DeleteAccess, AssignAccess, ShareAccess';

  interface Sent {
    method: string;
    path: string;
    body?: unknown;
  }

  /** Records writes; a GET of the record answers with `current.name`, which a PATCH or an Assign does not change. */
  function setupCommands(over: { rights?: string; patchError?: string; forms?: ReturnType<typeof form>[]; autoSave?: boolean } = {}) {
    const reads = setup({ rights: over.rights ?? FULL, forms: over.forms, autoSave: over.autoSave });
    const sent: Sent[] = [];
    let recordReads = 0;
    server.use(
      onPath('get', `accounts(${ACCOUNT})`, () => {
        recordReads++;
        return HttpResponse.json({ accountid: ACCOUNT, name: 'Contoso', telephone1: '555-0100' });
      }),
      onPath('get', `accounts(${NEW})`, () => HttpResponse.json({ accountid: NEW, name: 'Fabrikam', telephone1: '' })),
      onPath('post', 'accounts', async ({ request }) => {
        sent.push({ method: 'POST', path: 'accounts', body: await request.json() });
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://localhost/api/data/v9.2/accounts(${NEW})` } });
      }),
      onPath('patch', `accounts(${ACCOUNT})`, async ({ request }) => {
        sent.push({ method: 'PATCH', path: 'accounts', body: await request.json() });
        return over.patchError
          ? HttpResponse.json({ error: { code: '0x80040333', message: over.patchError } }, { status: 400 })
          : new HttpResponse(null, { status: 204 });
      }),
      onPath('delete', `accounts(${ACCOUNT})`, () => {
        sent.push({ method: 'DELETE', path: 'accounts' });
        return new HttpResponse(null, { status: 204 });
      }),
      http.get('*/api/data/v9.2/systemusers', () => HttpResponse.json({ value: [{ systemuserid: OWNER, fullname: 'Olga Owner' }] })),
      http.get('*/api/data/v9.2/teams', () => HttpResponse.json({ value: [] })),
    );
    return { reads, sent, recordReads: () => recordReads };
  }

  it('save_on_create_replaces_url_with_id', async () => {
    const { sent } = setupCommands();
    open('');
    const name = await screen.findByLabelText('Name');
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    await userEvent.type(name, 'Fabrikam');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    // The POST reaches the handler after the click resolves (fetch, then the body is read): wait for it, never let it reach the next test.
    await waitFor(() => expect(sent).toEqual([{ method: 'POST', path: 'accounts', body: { name: 'Fabrikam' } }]));
    // The record page now opens the new record: no unsaved-changes prompt, the title is the record's name.
    expect(await screen.findByRole('heading', { level: 1, name: 'Fabrikam' })).toBeInTheDocument();
    expect(screen.queryByText('Discard your changes?')).toBeNull();
    expect(screen.getByRole('button', { name: 'Delete' })).toBeInTheDocument();
  });

  it('save_and_close_returns_to_list', async () => {
    const { sent } = setupCommands();
    open(`&id=${ACCOUNT}`);
    const name = await screen.findByLabelText('Name');
    await userEvent.clear(name);
    await userEvent.type(name, 'Contoso Ltd');
    await userEvent.click(screen.getByRole('button', { name: 'Save & Close' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Accounts' })).toBeInTheDocument();
    expect(sent).toEqual([{ method: 'PATCH', path: 'accounts', body: { name: 'Contoso Ltd' } }]);
    expect(screen.queryByText('Discard your changes?')).toBeNull();
  });

  it('create_from_a_subgrid_goes_back_after_save', async () => {
    const { sent } = setupCommands();
    const back = `/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=account`;
    const refs: { path: string; body: unknown }[] = [];
    server.use(
      onPath('post', `accounts(${NEW})/xv_tags/$ref`, async ({ request }) => {
        refs.push({ path: new URL(request.url).pathname, body: await request.json() });
        return new HttpResponse(null, { status: 204 });
      }),
    );
    render(
      <App
        initialEntries={[
          {
            pathname: '/main.aspx',
            search: `?appid=${APP_ID}&pagetype=entityrecord&etn=account`,
            state: { returnTo: back, fromSubgrid: { associate: { nav: 'xv_tags', target: 'xv_projects(p1)' } } },
          },
        ]}
      />,
    );
    const name = await screen.findByLabelText('Name');
    expect(screen.queryByText(/Mapped fields/)).toBeNull();
    await userEvent.type(name, 'Fabrikam');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Accounts' })).toBeInTheDocument();
    expect(sent).toEqual([{ method: 'POST', path: 'accounts', body: { name: 'Fabrikam' } }]);
    expect(refs).toEqual([{ path: `/api/data/v9.2/accounts(${NEW})/xv_tags/$ref`, body: { '@odata.id': `${location.origin}/api/data/v9.2/xv_projects(p1)` } }]);
  });

  const PARENT = 'cccccccc-0000-0000-0000-000000000003';
  const fromParent = (initialize: () => Response) => {
    const calls: string[] = [];
    server.use(
      http.get(/\/api\/data\/v9\.2\/InitializeFrom\(/, ({ request }) => {
        calls.push(decodeURIComponent(new URL(request.url).search));
        return initialize();
      }),
    );
    render(
      <App
        initialEntries={[
          {
            pathname: '/main.aspx',
            search: `?appid=${APP_ID}&pagetype=entityrecord&etn=account`,
            state: { fromSubgrid: { createFrom: { table: 'account', set: 'accounts', id: PARENT, name: 'Parent Co' } } },
          },
        ]}
      />,
    );
    return calls;
  };

  const bothColumns = [form(F1, 'Main', formXml(control('name', 'Name'), control('telephone1', 'Phone')), { isdefault: true })];

  it('create_from_a_subgrid_fills_mapped_columns', async () => {
    const { sent } = setupCommands({ forms: bothColumns });
    const calls = fromParent(() =>
      HttpResponse.json({ '@odata.context': 'x', '@odata.type': '#Microsoft.Dynamics.CRM.account', telephone1: '555-0199', websiteurl: 'not on the form' }),
    );
    const phone = await screen.findByLabelText('Phone');
    await waitFor(() => expect(phone).toHaveValue('555-0199'));
    expect(calls).toEqual([`?@p1={'@odata.id':'accounts(${PARENT})'}&@p2='account'&@p3=Microsoft.Dynamics.CRM.TargetFieldType'ValidForCreate'`]);
    await userEvent.type(screen.getByLabelText('Name'), 'Fabrikam');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sent).toEqual([{ method: 'POST', path: 'accounts', body: { name: 'Fabrikam', telephone1: '555-0199' } }]));
  });

  it('create_from_a_subgrid_warns_when_the_maps_cannot_be_read', async () => {
    setupCommands({ forms: bothColumns });
    fromParent(() => HttpResponse.json({ error: { code: '0x80040220', message: 'No access' } }, { status: 403 }));
    expect(await screen.findByText(/^Mapped fields from the parent could not be filled: /)).toBeInTheDocument();
    expect(screen.getByLabelText('Phone')).toHaveValue('');
  });

  it('delete_confirms_then_returns_to_list', async () => {
    const { sent } = setupCommands();
    open(`&id=${ACCOUNT}`);
    await screen.findByLabelText('Name');
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
    expect(sent).toEqual([]);
    await userEvent.click((await screen.findAllByRole('button', { name: 'Delete', hidden: true })).at(-1)!);
    expect(await screen.findByRole('heading', { level: 1, name: 'Accounts' })).toBeInTheDocument();
    expect(sent).toEqual([{ method: 'DELETE', path: 'accounts' }]);
  });

  it('refresh_reloads_the_record_and_drops_changes_after_asking', async () => {
    const { recordReads } = setupCommands();
    open(`&id=${ACCOUNT}`);
    const name = await screen.findByLabelText('Name');
    await userEvent.type(name, ' Ltd');
    const before = recordReads();
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel', hidden: true }));
    expect(recordReads()).toBe(before);
    expect(screen.getByLabelText('Name')).toHaveValue('Contoso Ltd');
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Discard', hidden: true }));
    await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue('Contoso'));
    expect(recordReads()).toBe(before + 1);
  });

  it('assign_dialog_picks_user_or_team_and_patches', async () => {
    const { sent, recordReads } = setupCommands();
    open(`&id=${ACCOUNT}`);
    await screen.findByLabelText('Name');
    const before = recordReads();
    await userEvent.click(screen.getByRole('button', { name: 'Assign' }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    await userEvent.click(await within(dialog).findByRole('option', { name: 'Olga Owner', hidden: true }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Assign', hidden: true }));
    await waitFor(() => expect(screen.queryByRole('dialog', { hidden: true })).toBeNull());
    expect(sent).toEqual([{ method: 'PATCH', path: 'accounts', body: { 'ownerid@odata.bind': `/systemusers(${OWNER})` } }]);
    // The record and its rights are read again once.
    await waitFor(() => expect(recordReads()).toBe(before + 1));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(recordReads()).toBe(before + 1);
  });

  it('save_server_error_keeps_values_and_shows_message', async () => {
    const { sent } = setupCommands({ patchError: 'A record with the same name already exists.' });
    open(`&id=${ACCOUNT}`);
    const name = await screen.findByLabelText('Name');
    await userEvent.clear(name);
    await userEvent.type(name, 'Duplicate');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText(/A record with the same name already exists\./)).toBeInTheDocument();
    expect(screen.getByLabelText('Name')).toHaveValue('Duplicate');
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
    expect(sent).toHaveLength(1);
  });

  // Four guarded navigations in one test: about 5.5 s on a loaded runner, over vitest's 5 s default.
  it('leaving_changed_form_asks_cancel_keeps_edits', async () => {
    setupCommands();
    open(`&id=${ACCOUNT}`);
    const name = await screen.findByLabelText('Name');
    await userEvent.type(name, ' Ltd');

    // The sitemap.
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }));
    expect(await screen.findByText('Discard your changes?')).toBeInTheDocument();
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel', hidden: true }));
    await waitFor(() => expect(screen.queryByText('Discard your changes?')).toBeNull());
    expect(screen.getByLabelText('Name')).toHaveValue('Contoso Ltd');

    // The form selector.
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Form' }), F2);
    expect(await screen.findByText('Discard your changes?')).toBeInTheDocument();
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel', hidden: true }));
    await waitFor(() => expect(screen.queryByText('Discard your changes?')).toBeNull());
    expect(screen.getByLabelText('Name')).toHaveValue('Contoso Ltd');
    expect(screen.getByRole('combobox', { name: 'Form' })).toHaveValue(F1);

    // New.
    await userEvent.click(screen.getByRole('button', { name: 'New' }));
    expect(await screen.findByText('Discard your changes?')).toBeInTheDocument();
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel', hidden: true }));
    await waitFor(() => expect(screen.queryByText('Discard your changes?')).toBeNull());

    // Discard leaves.
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Discard', hidden: true }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Accounts' })).toBeInTheDocument();
    // Four dialog rounds (sitemap, form selector, New, Discard): over 5 s on a loaded runner.
  });

  it('auto_save_on_leaving_saves_and_goes_without_asking', async () => {
    const { sent } = setupCommands({ autoSave: true });
    open(`&id=${ACCOUNT}`);
    const name = await screen.findByLabelText('Name');
    await userEvent.type(name, ' Ltd');
    expect(screen.getByRole('status')).toHaveTextContent('Unsaved changes');
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Accounts' })).toBeInTheDocument();
    expect(sent).toEqual([{ method: 'PATCH', path: 'accounts', body: { name: 'Contoso Ltd' } }]);
    expect(screen.queryByText('Discard your changes?')).toBeNull();
  });

  it('auto_save_failure_on_leaving_asks_and_keeps_edits', async () => {
    const { sent } = setupCommands({ autoSave: true, patchError: 'Duplicate' });
    open(`&id=${ACCOUNT}`);
    const name = await screen.findByLabelText('Name');
    await userEvent.type(name, ' Ltd');
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }));
    expect(await screen.findByText('Discard your changes?')).toBeInTheDocument();
    expect(sent).toHaveLength(1);
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel', hidden: true }));
    expect(screen.getByLabelText('Name')).toHaveValue('Contoso Ltd');
    expect(screen.getByText(/Duplicate/)).toBeInTheDocument();
  });

  it('auto_save_footer_says_saved_after_a_save', async () => {
    setupCommands({ autoSave: true });
    open(`&id=${ACCOUNT}`);
    const name = await screen.findByLabelText('Name');
    expect(screen.queryByRole('status')).toBeNull();
    await userEvent.type(name, ' Ltd');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Saved'));
  });

  it('create_form_never_auto_saves_and_asks_on_leaving', async () => {
    const { sent } = setupCommands({ autoSave: true });
    open('');
    await userEvent.type(await screen.findByLabelText('Name'), 'Fabrikam');
    expect(screen.queryByRole('status')).toBeNull();
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }));
    expect(await screen.findByText('Discard your changes?')).toBeInTheDocument();
    expect(sent).toEqual([]);
  });

  it('assign_on_changed_form_asks_first_cancel_keeps_edits', async () => {
    const { sent } = setupCommands();
    open(`&id=${ACCOUNT}`);
    await userEvent.type(await screen.findByLabelText('Name'), ' Ltd');
    await userEvent.click(screen.getByRole('button', { name: 'Assign' }));
    expect(await screen.findByText('Discard your changes?')).toBeInTheDocument();
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel', hidden: true }));
    await waitFor(() => expect(screen.queryByText('Discard your changes?')).toBeNull());
    expect(screen.queryByRole('dialog', { hidden: true })).toBeNull();
    expect(screen.getByLabelText('Name')).toHaveValue('Contoso Ltd');
    expect(sent).toEqual([]);
  });

  it('assign_away_refreshes_rights', async () => {
    setupCommands();
    let reads = 0;
    server.use(
      onPath('get', `systemusers(${USER_ID})/Microsoft.Dynamics.CRM.RetrievePrincipalAccess(Target=@t)`, () =>
        HttpResponse.json({ AccessRights: ++reads === 1 ? FULL : 'ReadAccess' }),
      ),
    );
    open(`&id=${ACCOUNT}`);
    await screen.findByLabelText('Name');
    expect(screen.getByLabelText('Name')).toBeEnabled();
    await userEvent.click(screen.getByRole('button', { name: 'Assign' }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    await userEvent.click(await within(dialog).findByRole('option', { name: 'Olga Owner', hidden: true }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Assign', hidden: true }));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Delete' })).toBeNull());
    expect(screen.queryByRole('button', { name: 'Assign' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Share' })).toBeNull();
    expect(await screen.findByText('You do not have permission to edit this record.')).toBeInTheDocument();
    expect(screen.getByLabelText('Name')).toBeDisabled();
  });

  it('new_from_a_changed_create_form_gives_a_clean_form_after_discard', async () => {
    setupCommands();
    open('');
    await userEvent.type(await screen.findByLabelText('Name'), 'Fabrikam');
    await userEvent.click(screen.getByRole('button', { name: 'New' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Discard', hidden: true }));
    await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue(''));
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  describe('in a dialog (navigateTo target 2)', () => {
    // The page's form opens the Information form (F2, no scripts) in a dialog and logs what the promise resolves with.
    const opener = () =>
      form(F1, 'Main', formXml(control('name', 'Name')).replace(
        '</form>',
        `<formLibraries><Library name="d.js"/></formLibraries><events><event name="onload"><Handlers><Handler functionName="openDialog" libraryName="d.js" enabled="true" parameters="" passExecutionContext="true"/></Handlers></event></events></form>`,
      ), { isdefault: true });
    const host = () => document as unknown as { __log?: string[] };
    const loader = (input: string) =>
      sourceLoader({
        'd.js': `function openDialog() { Xrm.Navigation.navigateTo(${input}, { target: 2, title: 'Quick' }).then(function (r) { parent.document.__log.push(JSON.stringify(r === undefined ? null : r) + '|' + Xrm.Page.data.entity.getId()); }); }`,
      });
    afterEach(() => {
      setLibraryLoader(undefined);
      delete host().__log;
    });

    it('a_create_form_in_a_dialog_resolves_with_the_saved_record', async () => {
      host().__log = [];
      const input = `{ pageType: 'entityrecord', entityName: 'account', formId: '${F2}' }`;
      setLibraryLoader(loader(input));
      const { sent } = setupCommands({ forms: [opener(), form(F2, 'Information', formXml(control('telephone1', 'Phone')))] });
      open(`&id=${ACCOUNT}`);
      const dialog = await screen.findByRole('dialog', { name: 'Quick' });
      await userEvent.type(await within(dialog).findByLabelText('Phone'), '555-0199');
      await userEvent.click(within(dialog).getByRole('button', { name: 'Save & Close' }));
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Quick' })).toBeNull());
      expect(sent).toEqual([{ method: 'POST', path: 'accounts', body: { telephone1: '555-0199' } }]);
      // Xrm.Page is the page's form again.
      await waitFor(() =>
        expect(host().__log).toEqual([`{"savedEntityReference":[{"entityType":"account","id":"${NEW}","name":"Fabrikam"}]}|{${ACCOUNT.toUpperCase()}}`]),
      );
      expect(screen.getByLabelText('Name')).toHaveValue('Contoso');
    });

    it('closing_a_changed_dialog_form_asks_first_and_resolves_with_nothing_saved', async () => {
      host().__log = [];
      const input = `{ pageType: 'entityrecord', entityName: 'account', formId: '${F2}' }`;
      setLibraryLoader(loader(input));
      const { sent } = setupCommands({ forms: [opener(), form(F2, 'Information', formXml(control('telephone1', 'Phone')))] });
      open(`&id=${ACCOUNT}`);
      const dialog = await screen.findByRole('dialog', { name: 'Quick' });
      await userEvent.type(await within(dialog).findByLabelText('Phone'), '1');
      await userEvent.click(within(dialog).getByRole('button', { name: 'Close' }));
      expect(await screen.findByText('Discard your changes?')).toBeInTheDocument();
      await userEvent.click(await screen.findByRole('button', { name: 'Discard', hidden: true }));
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Quick' })).toBeNull());
      await waitFor(() => expect(host().__log).toEqual([`{"savedEntityReference":[]}|{${ACCOUNT.toUpperCase()}}`]));
      expect(sent).toEqual([]);
    });
  });

  it('an unchanged form leaves without asking', async () => {
    setupCommands();
    open(`&id=${ACCOUNT}`);
    await screen.findByLabelText('Name');
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Accounts' })).toBeInTheDocument();
  });
});

describe('RecordPage create defaults', { timeout: RECORD_PAGE_TIMEOUT }, () => {
  const NEW = 'bbbbbbbb-0000-0000-0000-0000000000bb';
  const CUR = 'eeeeeeee-0000-0000-0000-0000000000c1';
  const owned = rawEntity(
    'account',
    'accounts',
    [
      rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }),
      rawAttr('name', 'String', { IsPrimaryName: true, display: 'Name' }),
      rawAttr('revenue', 'Money', { display: 'Revenue' }),
      rawAttr('transactioncurrencyid', 'Lookup', { Targets: ['transactioncurrency'], display: 'Currency' }),
      rawAttr('ownerid', 'Lookup', { Targets: ['systemuser', 'team'], display: 'Owner', RequiredLevel: { Value: 'SystemRequired' } }),
      rawAttr('statecode', 'State', { OptionSet: { Options: [{ Value: 0, Label: lbl('Active') }], IsGlobal: false } }),
    ],
    { SchemaName: 'Account', PrimaryNameAttribute: 'name', display: 'Account', OwnershipType: 'UserOwned' },
  );
  const rel = (attribute: string, target: string, nav: string) => ({
    SchemaName: `${target}_${attribute}`,
    ReferencingEntity: 'account',
    ReferencingAttribute: attribute,
    ReferencedEntity: target,
    ReferencingEntityNavigationPropertyName: nav,
  });

  it('create_posts_the_current_user_as_owner_and_the_default_currency', async () => {
    setup({
      forms: [
        form(F1, 'Main', formXml(control('name', 'Name'), control('revenue', 'Revenue'), control('transactioncurrencyid', 'Currency'), control('ownerid', 'Owner')), {
          isdefault: true,
        }),
      ],
    });
    installMetadata([owned], [rel('ownerid', 'systemuser', 'ownerid'), rel('ownerid', 'team', 'ownerid'), rel('transactioncurrencyid', 'transactioncurrency', 'transactioncurrencyid')]);
    const posted: unknown[] = [];
    server.use(
      onPath('get', `usersettingscollection(${USER_ID})`, () =>
        HttpResponse.json({ systemuserid: USER_ID, uilanguageid: 1033, paginglimit: 50, _transactioncurrencyid_value: CUR }),
      ),
      onPath('get', `transactioncurrencies(${CUR})`, () => HttpResponse.json({ currencysymbol: '€' })),
      onPath('get', `systemusers(${USER_ID})`, () => HttpResponse.json({ systemuserid: USER_ID, fullname: 'Ursula User' })),
      onPath('post', 'accounts', async ({ request }) => {
        posted.push(await request.json());
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://localhost/api/data/v9.2/accounts(${NEW})` } });
      }),
      onPath('get', `accounts(${NEW})`, () => HttpResponse.json({ accountid: NEW, name: 'Fabrikam' })),
    );
    open('');
    await userEvent.type(await screen.findByLabelText('Name'), 'Fabrikam');
    // The defaults arrive after the form opens: the owner's name and the currency symbol show them.
    await screen.findByRole('button', { name: 'Clear Owner' });
    expect(screen.getByRole('textbox', { name: /^Owner/ })).toHaveValue('Ursula User');
    expect(await screen.findByLabelText('Revenue (€)')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]).toEqual({
      name: 'Fabrikam',
      'ownerid@odata.bind': `/systemusers(${USER_ID})`,
      'transactioncurrencyid@odata.bind': `/transactioncurrencies(${CUR})`,
    });
  });
});

describe('RecordPage scripts', { timeout: RECORD_PAGE_TIMEOUT }, () => {
  const scriptForm = (id: string, name: string, library: string, fn: string, over: { isdefault?: boolean } = {}) =>
    form(
      id,
      name,
      formXml(control('name', 'Name')).replace(
        '</form>',
        `<formLibraries><Library name="${library}"/></formLibraries><events><event name="onload"><Handlers><Handler functionName="${fn}" libraryName="${library}" enabled="true" parameters="" passExecutionContext="true"/></Handlers></event></events></form>`,
      ),
      over,
    );
  // In jsdom the IFrame's `parent` is not vitest's `window` object, but `parent.document` is the shared document, so the log lives there.
  const host = () => document as unknown as { __log?: string[] };
  const log = () => host().__log ?? [];
  afterEach(() => {
    setLibraryLoader(undefined);
    delete host().__log;
  });

  it('onload_runs_after_record_loaded_and_after_refresh', async () => {
    host().__log = [];
    setLibraryLoader(
      sourceLoader({
        'a.js': "function onLoad(ctx) { parent.document.__log.push('load:' + parent.document.querySelector('input').value); }",
      }),
    );
    setup({ forms: [scriptForm(F1, 'Main', 'a.js', 'onLoad', { isdefault: true })] });
    open(`&id=${ACCOUNT}`);
    expect(await screen.findByLabelText('Name')).toHaveValue('Contoso');
    await waitFor(() => expect(log()).toEqual(['load:Contoso']));
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(log()).toEqual(['load:Contoso', 'load:Contoso']));
  });

  it('switching_form_mid_load_old_second_library_never_runs', async () => {
    host().__log = [];
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const inner = sourceLoader({
      'a1.js': 'function noop() {}',
      'a2.js': "parent.document.__log.push('a2-ran');",
      'b.js': "function onLoad() { parent.document.__log.push('b-load'); }",
    });
    setLibraryLoader(async (win, url, name) => {
      if (name === 'a1.js') await gate;
      return inner(win, url, name);
    });
    const twoLibs = scriptForm(F1, 'Main', 'a1.js', 'noop', { isdefault: true });
    twoLibs.formxml = twoLibs.formxml.replace('<Library name="a1.js"/>', '<Library name="a1.js"/><Library name="a2.js"/>');
    setup({ forms: [twoLibs, scriptForm(F2, 'Other', 'b.js', 'onLoad')] });
    open(`&id=${ACCOUNT}`);
    await screen.findByLabelText('Name');
    await waitFor(() => expect(document.querySelectorAll('iframe')).toHaveLength(1));
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Form' }), F2);
    await waitFor(() => expect(log()).toContain('b-load'));
    release();
    await new Promise((r) => setTimeout(r, 30));
    expect(log()).toEqual(['b-load']);
    expect(document.querySelectorAll('iframe')).toHaveLength(1);
  });

  it('onload_after_assign_sees_the_reloaded_record', async () => {
    host().__log = [];
    setLibraryLoader(
      sourceLoader({ 'a.js': "function onLoad() { parent.document.__log.push('disabled:' + parent.document.querySelector('input').disabled); }" }),
    );
    setup({ forms: [scriptForm(F1, 'Main', 'a.js', 'onLoad', { isdefault: true })] });
    let reads = 0;
    server.use(
      onPath('get', `systemusers(${USER_ID})/Microsoft.Dynamics.CRM.RetrievePrincipalAccess(Target=@t)`, () =>
        HttpResponse.json({ AccessRights: ++reads === 1 ? 'ReadAccess, WriteAccess, DeleteAccess, AssignAccess, ShareAccess' : 'ReadAccess' }),
      ),
      onPath('patch', `accounts(${ACCOUNT})`, () => new HttpResponse(null, { status: 204 })),
      http.get('*/api/data/v9.2/systemusers', () => HttpResponse.json({ value: [{ systemuserid: 'aaaaaaaa-0000-0000-0000-0000000000a1', fullname: 'Olga Owner' }] })),
      http.get('*/api/data/v9.2/teams', () => HttpResponse.json({ value: [] })),
    );
    open(`&id=${ACCOUNT}`);
    await screen.findByLabelText('Name');
    await waitFor(() => expect(log()).toEqual(['disabled:false']));
    await userEvent.click(screen.getByRole('button', { name: 'Assign' }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    await userEvent.click(await within(dialog).findByRole('option', { name: 'Olga Owner', hidden: true }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Assign', hidden: true }));
    await waitFor(() => expect(log()).toHaveLength(2));
    await new Promise((r) => setTimeout(r, 50));
    expect(log()).toEqual(['disabled:false', 'disabled:true']);
  });

  it('broken_scripts_notify_and_the_form_stays_usable', async () => {
    setLibraryLoader(sourceLoader({ 'bad.js': 'function (' }));
    setup({ forms: [scriptForm(F1, 'Main', 'bad.js', 'nothing', { isdefault: true })] });
    const sent: unknown[] = [];
    server.use(
      onPath('patch', `accounts(${ACCOUNT})`, async ({ request }) => {
        sent.push(await request.json());
        return new HttpResponse(null, { status: 204 });
      }),
    );
    open(`&id=${ACCOUNT}`);
    expect(await screen.findByText('Script error: the library bad.js could not be loaded.')).toBeInTheDocument();
    const name = screen.getByLabelText('Name');
    await userEvent.type(name, 'X');
    expect(name).toHaveValue('ContosoX');
    // Save still works.
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sent).toEqual([{ name: 'ContosoX' }]));
  });

  it('dispose_on_form_switch_old_handlers_detached', async () => {
    host().__log = [];
    setLibraryLoader(
      sourceLoader({
        'a.js': "var onlyInA = 1; function onLoad() { setInterval(function () { parent.document.__log.push('tick-a'); }, 5); }",
        'b.js': "function onLoad() { parent.document.__log.push('b:' + typeof onlyInA); }",
      }),
    );
    setup({ forms: [scriptForm(F1, 'Main', 'a.js', 'onLoad', { isdefault: true }), scriptForm(F2, 'Other', 'b.js', 'onLoad')] });
    open(`&id=${ACCOUNT}`);
    await screen.findByLabelText('Name');
    await waitFor(() => expect(log()).toContain('tick-a'));
    expect(document.querySelectorAll('iframe')).toHaveLength(1);
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Form' }), F2);
    await waitFor(() => expect(log()).toContain('b:undefined'));
    expect(document.querySelectorAll('iframe')).toHaveLength(1);
    const ticks = log().filter((x) => x === 'tick-a').length;
    await new Promise((r) => setTimeout(r, 40));
    expect(log().filter((x) => x === 'tick-a').length).toBe(ticks);
  });
});

describe('RecordPage form events', { timeout: RECORD_PAGE_TIMEOUT }, () => {
  /** A form with `library` and the given `<event>` elements (handlers get the execution context). */
  const eventForm = (id: string, name: string, library: string, events: string, cells = [control('name', 'Name')], over: { isdefault?: boolean } = {}) =>
    form(id, name, formXml(...cells).replace('</form>', `<formLibraries><Library name="${library}"/></formLibraries><events>${events}</events></form>`), over);
  const handler = (fn: string, library: string) =>
    `<Handlers><Handler functionName="${fn}" libraryName="${library}" enabled="true" parameters="" passExecutionContext="true"/></Handlers>`;
  const host = () => document as unknown as { __log?: string[] };
  const log = () => host().__log ?? [];

  function setupWrites() {
    const sent: unknown[] = [];
    server.use(
      onPath('patch', `accounts(${ACCOUNT})`, async ({ request }) => {
        sent.push(await request.json());
        return new HttpResponse(null, { status: 204 });
      }),
    );
    return sent;
  }

  afterEach(() => {
    setLibraryLoader(undefined);
    delete host().__log;
  });

  it('onsave_prevent_default_stops_save', async () => {
    host().__log = [];
    setLibraryLoader(
      sourceLoader({
        'a.js': "function onSave(ctx) { var a = ctx.getEventArgs(); a.preventDefault(); parent.document.__log.push('save:' + a.getSaveMode() + ':' + a.isDefaultPrevented()); }",
      }),
    );
    setup({ forms: [eventForm(F1, 'Main', 'a.js', `<event name="onsave">${handler('onSave', 'a.js')}</event>`, undefined, { isdefault: true })] });
    const sent = setupWrites();
    open(`&id=${ACCOUNT}`);
    const name = await screen.findByLabelText('Name');
    await waitFor(() => expect(document.querySelectorAll('iframe')).toHaveLength(1));
    await userEvent.type(name, 'X');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(log()).toEqual(['save:1:true']));
    await new Promise((r) => setTimeout(r, 30));
    expect(sent).toEqual([]);
    expect(name).toHaveValue('ContosoX');
  });

  it('onsave_save_mode_1_and_2', async () => {
    host().__log = [];
    setLibraryLoader(
      sourceLoader({
        // An OnSave handler's change is part of the save.
        'a.js':
          "function onSave(ctx) { parent.document.__log.push('save:' + ctx.getEventArgs().getSaveMode()); var n = ctx.getFormContext().getAttribute('name'); n.setValue(n.getValue() + '!'); }",
      }),
    );
    setup({ forms: [eventForm(F1, 'Main', 'a.js', `<event name="onsave">${handler('onSave', 'a.js')}</event>`, undefined, { isdefault: true })] });
    const sent = setupWrites();
    open(`&id=${ACCOUNT}`);
    const name = await screen.findByLabelText('Name');
    await waitFor(() => expect(document.querySelectorAll('iframe')).toHaveLength(1));
    await userEvent.type(name, 'X');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sent).toEqual([{ name: 'ContosoX!' }]));
    await userEvent.type(screen.getByLabelText('Name'), 'Y');
    await userEvent.click(screen.getByRole('button', { name: 'Save & Close' }));
    await waitFor(() => expect(sent).toHaveLength(2));
    expect(log()).toEqual(['save:1', 'save:2']);
  });

  it('async_onsave_waits_for_the_handler_promise', async () => {
    host().__log = [];
    setLibraryLoader(
      sourceLoader({
        // The handler's promise settles after the click's own work: without AsyncOnSave the PATCH would not wait for it.
        'a.js':
          "function onSave(ctx) { var log = parent.document.__log; log.push('save:start'); return new Promise(function (resolve) { setTimeout(function () { log.push('save:done'); resolve(); }, 50); }); }",
      }),
    );
    setup({
      forms: [eventForm(F1, 'Main', 'a.js', `<event name="onsave">${handler('onSave', 'a.js')}</event>`, undefined, { isdefault: true })],
      settings: { AsyncOnSave: 'true' },
    });
    server.use(
      onPath('patch', `accounts(${ACCOUNT})`, () => {
        log().push('patch');
        return new HttpResponse(null, { status: 204 });
      }),
    );
    open(`&id=${ACCOUNT}`);
    const name = await screen.findByLabelText('Name');
    await waitFor(() => expect(document.querySelectorAll('iframe')).toHaveLength(1));
    await userEvent.type(name, 'X');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(log()).toEqual(['save:start', 'save:done', 'patch']));
  });

  it('auto_save_runs_onsave_with_mode_70_and_prevent_default_stops_it', async () => {
    host().__log = [];
    setLibraryLoader(
      sourceLoader({
        'a.js': "function onSave(ctx) { var a = ctx.getEventArgs(); parent.document.__log.push('save:' + a.getSaveMode()); if (a.getSaveMode() === 70) a.preventDefault(); }",
      }),
    );
    setup({ forms: [eventForm(F1, 'Main', 'a.js', `<event name="onsave">${handler('onSave', 'a.js')}</event>`, undefined, { isdefault: true })], autoSave: true });
    const sent = setupWrites();
    open(`&id=${ACCOUNT}`);
    const name = await screen.findByLabelText('Name');
    await waitFor(() => expect(document.querySelectorAll('iframe')).toHaveLength(1));
    await userEvent.type(name, 'X');
    await userEvent.click(screen.getByRole('link', { name: 'Accounts' }));
    // The prevented auto-save falls back to the unsaved-changes prompt.
    expect(await screen.findByText('Discard your changes?')).toBeInTheDocument();
    expect(log()).toEqual(['save:70']);
    expect(sent).toEqual([]);
  });

  it('user_change_runs_formxml_onchange_handlers_in_the_host', async () => {
    host().__log = [];
    setLibraryLoader(
      sourceLoader({
        'a.js':
          "function nameChanged(ctx) { var a = ctx.getEventSource(); parent.document.__log.push(a.getName() + '=' + a.getValue()); a.setValue(a.getValue().toUpperCase()); }",
      }),
    );
    setup({ forms: [eventForm(F1, 'Main', 'a.js', `<event name="onchange" attribute="name">${handler('nameChanged', 'a.js')}</event>`, undefined, { isdefault: true })] });
    open(`&id=${ACCOUNT}`);
    const name = await screen.findByLabelText('Name');
    await waitFor(() => expect(document.querySelectorAll('iframe')).toHaveLength(1));
    await userEvent.type(name, 'x');
    expect(log()).toEqual([]);
    await userEvent.tab();
    // Once, and the handler's own setValue does not raise it again.
    await waitFor(() => expect(log()).toEqual(['name=Contosox']));
    expect(name).toHaveValue('CONTOSOX');
  });

  it('data_save_on_create_resolves_with_the_new_id', async () => {
    const NEW = 'bbbbbbbb-0000-0000-0000-0000000000aa';
    host().__log = [];
    setLibraryLoader(
      sourceLoader({
        'a.js': "function onLoad(ctx) { var fc = ctx.getFormContext(); parent.document.__fc = fc; parent.document.__log.push('load:' + fc.ui.getFormType() + ':' + fc.data.entity.getId()); }",
      }),
    );
    setup({ forms: [eventForm(F1, 'Main', 'a.js', `<event name="onload">${handler('onLoad', 'a.js')}</event>`, undefined, { isdefault: true })] });
    const posted: unknown[] = [];
    server.use(
      onPath('post', 'accounts', async ({ request }) => {
        posted.push(await request.json());
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://localhost/api/data/v9.2/accounts(${NEW})` } });
      }),
      onPath('get', `accounts(${NEW})`, () => HttpResponse.json({ accountid: NEW, name: 'Fabrikam', telephone1: '' })),
    );
    open('');
    const name = await screen.findByLabelText('Name');
    await waitFor(() => expect(log()).toEqual(['load:1:']));
    await userEvent.type(name, 'Fabrikam');
    const fc = (document as unknown as { __fc: Record<string, Record<string, (...a: unknown[]) => unknown>> }).__fc;
    const ids: unknown[] = [];
    (fc.data!.entity as unknown as Record<string, (f: unknown) => void>).addOnPostSave((ctx: { getEventArgs(): { getEntityReference(): { id: string } } }) =>
      ids.push(ctx.getEventArgs().getEntityReference().id),
    );
    await act(async () => {
      await fc.data!.save();
    });
    expect(posted).toEqual([{ name: 'Fabrikam' }]);
    const braced = `{${NEW.toUpperCase()}}`;
    // The same formContext: the new id before save() resolved and in PostSave, and it stays usable.
    expect(ids).toEqual([braced]);
    expect((fc.data!.entity as unknown as Record<string, () => unknown>).getId()).toBe(braced);
    expect((fc.ui as unknown as Record<string, () => unknown>).getFormType()).toBe(2);
    expect((fc.getAttribute as unknown as (n: string) => { getValue(): unknown })('name').getValue()).toBe('Fabrikam');
    act(() => void (fc.ui!.setFormNotification as (...a: unknown[]) => boolean)('Created', 'INFO', 'created'));
    expect(await screen.findByText('Created')).toBeInTheDocument();
    // The form is now the new record's, in the same script host; OnLoad runs again after the first save (Learn).
    expect(await screen.findByRole('heading', { level: 1, name: 'Fabrikam' })).toBeInTheDocument();
    await waitFor(() => expect(log()).toEqual(['load:1:', `load:2:${braced}`]));
    expect(document.querySelectorAll('iframe')).toHaveLength(1);
    delete (document as unknown as { __fc?: unknown }).__fc;
  });

  it('create_new_back_opens_the_fresh_record_and_runs_onload_once', async () => {
    const NEW = 'bbbbbbbb-0000-0000-0000-0000000000aa';
    host().__log = [];
    setLibraryLoader(
      sourceLoader({
        'a.js':
          "function onLoad(ctx) { var fc = ctx.getFormContext(); parent.document.__log.push('load:' + fc.ui.getFormType() + ':' + fc.getAttribute('name').getValue()); }",
      }),
    );
    setup({ forms: [eventForm(F1, 'Main', 'a.js', `<event name="onload">${handler('onLoad', 'a.js')}</event>`, undefined, { isdefault: true })] });
    let newReads = 0;
    server.use(
      onPath('post', 'accounts', () => new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://localhost/api/data/v9.2/accounts(${NEW})` } })),
      // The record changed on the server after the first read; the later read answers late.
      onPath('get', `accounts(${NEW})`, async () => {
        newReads++;
        if (newReads === 1) return HttpResponse.json({ accountid: NEW, name: 'Fabrikam', telephone1: '' });
        await new Promise((r) => setTimeout(r, 50));
        return HttpResponse.json({ accountid: NEW, name: 'Fabrikam 2', telephone1: '' });
      }),
    );
    let router: { navigate(delta: number): unknown } | undefined;
    render(<App initialEntries={[`/main.aspx?appid=${APP_ID}&pagetype=entityrecord&etn=account`]} onRouter={(r) => (router = r)} />);
    await userEvent.type(await screen.findByLabelText('Name'), 'Fabrikam');
    await waitFor(() => expect(log()).toHaveLength(1));
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Fabrikam' })).toBeInTheDocument();
    await waitFor(() => expect(log()).toContain('load:2:Fabrikam'));
    await userEvent.click(screen.getByRole('button', { name: 'New' }));
    await waitFor(() => expect(screen.getByLabelText('Name')).toHaveValue(''));
    await waitFor(() => expect(log().at(-1)).toBe('load:1:null'));
    host().__log = [];
    await act(async () => {
      await router!.navigate(-1);
    });
    expect(await screen.findByRole('heading', { level: 1, name: 'Fabrikam 2' })).toBeInTheDocument();
    expect(screen.getByLabelText('Name')).toHaveValue('Fabrikam 2');
    await waitFor(() => expect(log()).toEqual(['load:2:Fabrikam 2']));
    await new Promise((r) => setTimeout(r, 50));
    // One OnLoad, on the fresh record: the create form's first read is not shown again.
    expect(log()).toEqual(['load:2:Fabrikam 2']);
  });

  it('between_create_and_read_the_form_is_busy_and_a_later_value_is_not_lost_silently', async () => {
    const NEW = 'bbbbbbbb-0000-0000-0000-0000000000aa';
    host().__log = [];
    setLibraryLoader(
      sourceLoader({ 'a.js': "function onLoad(ctx) { parent.document.__fc = ctx.getFormContext(); parent.document.__log.push('load'); }" }),
    );
    setup({ forms: [eventForm(F1, 'Main', 'a.js', `<event name="onload">${handler('onLoad', 'a.js')}</event>`, undefined, { isdefault: true })] });
    const sent: string[] = [];
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    server.use(
      onPath('post', 'accounts', () => {
        sent.push('POST');
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://localhost/api/data/v9.2/accounts(${NEW})` } });
      }),
      onPath('patch', `accounts(${NEW})`, () => {
        sent.push('PATCH');
        return new HttpResponse(null, { status: 204 });
      }),
      onPath('get', `accounts(${NEW})`, async () => {
        await gate;
        return HttpResponse.json({ accountid: NEW, name: 'Fabrikam', telephone1: '' });
      }),
    );
    open('');
    const name = await screen.findByLabelText('Name');
    await waitFor(() => expect(log()).toEqual(['load']));
    await userEvent.type(name, 'Fabrikam');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sent).toEqual(['POST']));
    // The new record is being read: nothing can be typed or saved, and nothing is left unsaved.
    await waitFor(() => expect(screen.getByLabelText('Name')).toBeDisabled());
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    const fc = (document as unknown as { __fc: Record<string, Record<string, (...a: unknown[]) => unknown>> }).__fc;
    // A script's value set in that window is refused by a save with a message, not dropped silently.
    act(() => void (fc.getAttribute as unknown as (n: string) => { setValue(v: unknown): void })('name').setValue('Later'));
    let outcome = 'pending';
    await act(async () => {
      await Promise.resolve(fc.data!.save()).then(
        () => (outcome = 'resolved'),
        () => (outcome = 'rejected'),
      );
    });
    expect(outcome).toBe('rejected');
    expect(await screen.findByText('The record was created and is still loading; save again once it has loaded.')).toBeInTheDocument();
    expect(sent).toEqual(['POST']);
    release();
    expect(await screen.findByRole('heading', { level: 1, name: 'Fabrikam' })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText('Name')).toBeEnabled());
    delete (document as unknown as { __fc?: unknown }).__fc;
  });

  it('form_selector_navigate_switches_form_from_onload', async () => {
    // The OpenProject365 account script: OnLoad opens the form the record belongs on.
    host().__log = [];
    setLibraryLoader(
      sourceLoader({
        'a.js': `function onLoad(ctx) {
          var ui = ctx.getFormContext().ui;
          parent.document.__log.push('form:' + ui.formSelector.getCurrentItem().getLabel() + ':' + ui.getFormType());
          ui.formSelector.items.forEach(function (item) { if (item.getId() === '${F2}') { item.navigate(); } });
        }`,
        'b.js': "function onLoad(ctx) { parent.document.__log.push('form:' + ctx.getFormContext().ui.formSelector.getCurrentItem().getLabel()); }",
      }),
    );
    setup({
      forms: [
        eventForm(F1, 'Main', 'a.js', `<event name="onload">${handler('onLoad', 'a.js')}</event>`, undefined, { isdefault: true }),
        eventForm(F2, 'Information', 'b.js', `<event name="onload">${handler('onLoad', 'b.js')}</event>`, [control('telephone1', 'Phone')]),
      ],
    });
    open(`&id=${ACCOUNT}`);
    expect(await screen.findByLabelText('Phone')).toHaveValue('555-0100');
    await waitFor(() => expect(log()).toEqual(['form:Main:2', 'form:Information']));
    expect(screen.getByRole('combobox', { name: 'Form' })).toHaveValue(F2);
  });

  it('xrm_installed_in_the_script_host_and_on_the_shell_while_the_app_is_open', async () => {
    // The OpenProject365 pattern: OnLoad reads the user's language through Xrm, and Xrm.Page is the form being loaded.
    host().__log = [];
    setLibraryLoader(
      sourceLoader({
        'a.js': `function onLoad(ctx) {
          var gc = Xrm.Utility.getGlobalContext();
          parent.document.__log.push('lang:' + gc.userSettings.languageId + ':' + gc.userSettings.userName + ':' + gc.organizationSettings.uniqueName);
          parent.document.__log.push('page:' + (Xrm.Page.data.entity.getId() === ctx.getFormContext().data.entity.getId()) + ':' + (Xrm.Page.context === gc));
        }`,
      }),
    );
    setup({ forms: [eventForm(F1, 'Main', 'a.js', `<event name="onload">${handler('onLoad', 'a.js')}</event>`, undefined, { isdefault: true })] });
    const { unmount } = open(`&id=${ACCOUNT}`);
    expect(await screen.findByLabelText('Name')).toHaveValue('Contoso');
    await waitFor(() => expect(log()).toEqual(['lang:1033:Ursula User:contoso', 'page:true:true']));
    const shell = window as unknown as { Xrm?: { Utility: { getGlobalContext(): { getClientUrl(): string } } } };
    expect(shell.Xrm?.Utility.getGlobalContext().getClientUrl()).toBe(location.origin);
    unmount();
    // Leaving the app removes window.Xrm.
    expect(shell.Xrm).toBeUndefined();
  });

  it('xrm_page_on_a_form_without_scripts', async () => {
    // A form with no libraries still registers its formContext, so a script outside the form (the shell) reads Xrm.Page.
    setup();
    const { unmount } = open(`&id=${ACCOUNT}`);
    expect(await screen.findByLabelText('Name')).toHaveValue('Contoso');
    const shell = window as unknown as { Xrm?: { Page: { data: { entity: { getId(): string; getEntityName(): string } } } } };
    await waitFor(() => expect(shell.Xrm?.Page.data.entity.getId()).toBe(`{${ACCOUNT.toUpperCase()}}`));
    expect(shell.Xrm?.Page.data.entity.getEntityName()).toBe('account');
    unmount();
  });

  it('xrm_page_is_the_form_while_its_libraries_are_still_loading', async () => {
    // A web resource on the form reads parent.Xrm.Page in its own onload, which can run before the form's libraries are in.
    host().__log = [];
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const inner = sourceLoader({ 'a.js': "function onLoad(ctx) { parent.document.__log.push('same:' + (Xrm.Page.data === ctx.getFormContext().data)); }" });
    setLibraryLoader(async (win, url, name) => {
      await gate;
      return inner(win, url, name);
    });
    setup({ forms: [eventForm(F1, 'Main', 'a.js', `<event name="onload">${handler('onLoad', 'a.js')}</event>`, undefined, { isdefault: true })] });
    const { unmount } = open(`&id=${ACCOUNT}`);
    expect(await screen.findByLabelText('Name')).toHaveValue('Contoso');
    const shell = window as unknown as { Xrm?: { Page: { data: { entity: { getId(): string } } } } };
    await waitFor(() => expect(shell.Xrm?.Page.data.entity.getId()).toBe(`{${ACCOUNT.toUpperCase()}}`));
    expect(log()).toEqual([]);
    release();
    // OnLoad then runs against the same formContext that Xrm.Page already was.
    await waitFor(() => expect(log()).toEqual(['same:true']));
    unmount();
  });

  it('a_progress_indicator_left_open_by_a_form_script_goes_away_with_the_form', async () => {
    setLibraryLoader(
      sourceLoader({ 'a.js': "function onLoad() { Xrm.Utility.showProgressIndicator('Working on it'); throw new Error('boom'); }" }),
    );
    setup({ forms: [eventForm(F1, 'Main', 'a.js', `<event name="onload">${handler('onLoad', 'a.js')}</event>`, undefined, { isdefault: true }), form(F2, 'Information', formXml(control('telephone1', 'Phone')))] });
    open(`&id=${ACCOUNT}`);
    expect(await screen.findByRole('alertdialog', { name: 'Working on it' })).toBeInTheDocument();
    // Another form is opened (as a script's formSelector navigate would; the overlay covers the selector for a user).
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Form', hidden: true }), F2);
    await waitFor(() => expect(screen.queryByRole('alertdialog', { name: 'Working on it' })).not.toBeInTheDocument());
    expect(await screen.findByLabelText('Phone')).toBeInTheDocument();
  });

  it('execution_context_get_context_is_the_global_context', async () => {
    host().__log = [];
    setLibraryLoader(
      sourceLoader({
        'a.js': `function onLoad(ctx) {
          var c = ctx.getContext();
          parent.document.__log.push('ctx:' + (c === Xrm.Utility.getGlobalContext()) + ':' + c.userSettings.userName);
        }`,
      }),
    );
    setup({ forms: [eventForm(F1, 'Main', 'a.js', `<event name="onload">${handler('onLoad', 'a.js')}</event>`, undefined, { isdefault: true })] });
    open(`&id=${ACCOUNT}`);
    expect(await screen.findByLabelText('Name')).toHaveValue('Contoso');
    await waitFor(() => expect(log()).toEqual(['ctx:true:Ursula User']));
  });

  it('open_form_parameters_fill_the_create_form', async () => {
    setup();
    open('&extraqs=' + encodeURIComponent('name=From script&nope=1'));
    expect(await screen.findByLabelText('Name')).toHaveValue('From script');
    expect(await screen.findByText(/These form parameters were ignored.*nope/)).toBeInTheDocument();
  });
});

describe('RecordPage custom commands (UI-1e)', { timeout: RECORD_PAGE_TIMEOUT }, () => {
  afterEach(() => setLibraryLoader(undefined));

  const LOC = 'Mscrm.Form.account.MainTab.Actions.Controls._children';
  /** One button whose display rule is Existing and whose enable rule is a CustomRule (written by us). */
  const RIBBON = `<RibbonDiffXml><CustomActions>
    <CustomAction Id="xv.call.CA" Location="${LOC}" Sequence="10"><CommandUIDefinition><Button Id="xv.call.B" Command="xv.call.C" LabelText="Call back" ModernImage="Send" /></CommandUIDefinition></CustomAction>
  </CustomActions><CommandDefinitions>
    <CommandDefinition Id="xv.call.C"><EnableRules><EnableRule Id="xv.call.E" /></EnableRules><DisplayRules><DisplayRule Id="xv.call.D" /></DisplayRules>
      <Actions><JavaScriptFunction Library="$webresource:xv_/account.js" FunctionName="XV.call"><CrmParameter Value="PrimaryControl" /></JavaScriptFunction></Actions></CommandDefinition>
  </CommandDefinitions><RuleDefinitions>
    <DisplayRules><DisplayRule Id="xv.call.D"><FormStateRule State="Existing" /></DisplayRule></DisplayRules>
    <EnableRules><EnableRule Id="xv.call.E"><CustomRule Library="$webresource:xv_/account.js" FunctionName="XV.enabled" Default="false" /></EnableRule></EnableRules>
  </RuleDefinitions></RibbonDiffXml>`;

  it('shows_a_custom_command_runs_it_with_the_form_context_and_refreshRibbon_evaluates_again', async () => {
    setup();
    server.use(http.get('*/Ribbon/account', () => HttpResponse.json({ entity: 'account', diffs: [{ solution: 'Xv', xml: RIBBON }] })));
    const probe = { enabled: true, seen: [] as unknown[], fc: undefined as unknown };
    (document as unknown as { __probe: typeof probe }).__probe = probe;
    setLibraryLoader(
      sourceLoader({
        'xv_/account.js': `var P = window.parent.document.__probe; var XV = {
          enabled: function () { return P.enabled; },
          call: function (fc) { P.fc = fc; P.seen.push(fc.getAttribute('name').getValue()); },
        };`,
      }),
    );
    open(`&id=${ACCOUNT}`);
    const call = await screen.findByRole('button', { name: 'Call back' });
    await userEvent.click(call);
    await waitFor(() => expect(probe.seen).toEqual(['Contoso']));
    probe.enabled = false;
    act(() => (probe.fc as { ui: { refreshRibbon(): void } }).ui.refreshRibbon());
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Call back' })).toBeNull());
  });

  it('a_ribbon_that_cannot_be_read_warns_on_the_form', async () => {
    setup();
    server.use(http.get('*/Ribbon/account', () => HttpResponse.json({ title: 'Forbidden', status: 403, detail: 'No read access to solution.' }, { status: 403 })));
    open(`&id=${ACCOUNT}`);
    expect(await screen.findByText('Custom commands could not be read: No read access to solution.')).toBeInTheDocument();
    expect(screen.getByLabelText('Name')).toHaveValue('Contoso');
  });
});
