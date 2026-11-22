import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { delay, http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';
import { installMetadata, lbl, rawAttr, rawEntity, rawOneToMany } from './tableTestSupport';

const project = rawEntity(
  'xv_project',
  'xv_projects',
  [
    rawAttr('xv_projectid', 'Uniqueidentifier', { IsPrimaryId: true, IsValidForUpdate: false }),
    rawAttr('xv_name', 'String', { IsPrimaryName: true, display: 'Name', RequiredLevel: { Value: 'ApplicationRequired' } }),
    rawAttr('xv_count', 'Integer', { display: 'Count' }),
    rawAttr('xv_prio', 'Picklist', { display: 'Priority', OptionSet: { Options: [{ Value: 1, Label: lbl('High') }, { Value: 2, Label: lbl('Low') }], IsGlobal: false } }),
    rawAttr('xv_tags', 'MultiSelectPicklist', { display: 'Tags', OptionSet: { Options: [], IsGlobal: true, Name: 'xv_tagset' } }),
    rawAttr('xv_parent', 'Lookup', { display: 'Parent', Targets: ['contact'] }),
    rawAttr('xv_regarding', 'Lookup', { display: 'Regarding', Targets: ['account', 'contact'] }),
    rawAttr('xv_scan', 'Image', { display: 'Scan', CanStoreFullImage: false }),
    rawAttr('xv_scanid', 'Uniqueidentifier', { AttributeOf: 'xv_scan', IsValidForCreate: false, IsValidForUpdate: false }),
    rawAttr('xv_doc', 'File', { display: 'Document', IsValidForCreate: false, IsValidForUpdate: false }),
    rawAttr('xv_doc_name', 'String', { AttributeOf: 'xv_doc', IsValidForCreate: false, IsValidForUpdate: false }),
    rawAttr('xv_ref', 'Uniqueidentifier', { display: 'Reference' }),
    rawAttr('createdon', 'DateTime', { IsValidForCreate: false, IsValidForUpdate: false }),
  ],
  { PrimaryIdAttribute: 'xv_projectid', PrimaryNameAttribute: 'xv_name', display: 'Project' },
);
const contact = rawEntity('contact', 'contacts', [], { PrimaryIdAttribute: 'contactid', PrimaryNameAttribute: 'fullname' });
const account = rawEntity('account', 'accounts', [], { PrimaryIdAttribute: 'accountid', PrimaryNameAttribute: 'name' });
// Navigation names that differ from the logical names: the form must use these, never a guess.
const relationships = [
  rawOneToMany('xv_project_xv_parent_contact', 'xv_project', 'xv_parent', 'contact', 'xv_ParentId'),
  rawOneToMany('xv_project_xv_regarding_account', 'xv_project', 'xv_regarding', 'account', 'xv_Regarding'),
  rawOneToMany('xv_project_xv_regarding_contact', 'xv_project', 'xv_regarding', 'contact', 'xv_Regarding'),
];

const RECORD = {
  xv_projectid: 'R1',
  xv_name: 'Alpha',
  xv_count: 3,
  xv_prio: 1,
  xv_tags: '1',
  _xv_parent_value: 'c1',
  '_xv_parent_value@Microsoft.Dynamics.CRM.lookuplogicalname': 'contact',
  '_xv_parent_value@OData.Community.Display.V1.FormattedValue': 'Ada Lovelace',
  _xv_regarding_value: null,
};

beforeEach(() => {
  signInAsAdministrator();
  installMetadata([project, contact, account], relationships);
  server.use(
    http.get(`${API}/GlobalOptionSetDefinitions`, () =>
      HttpResponse.json({ value: [{ Name: 'xv_tagset', Options: [{ Value: 1, Label: lbl('Red') }, { Value: 2, Label: lbl('Blue') }] }] }),
    ),
    http.get(`${API}/contacts`, () => HttpResponse.json({ value: [{ contactid: 'c2', fullname: 'Grace Hopper' }] })),
    http.get(`${API}/accounts`, () => HttpResponse.json({ value: [{ accountid: 'a1', name: 'Acme' }] })),
  );
});
afterEach(() => sessionStorage.clear());

describe('RecordForm', () => {
  it('RecordForm_patch_sends_only_changed_fields', async () => {
    let patched: unknown;
    let reads = 0;
    server.use(
      onPath('get', 'xv_projects(R1)', async () => {
        reads++;
        // The record arrives after the metadata and relationships: the form must not show before its values are in.
        if (reads === 1) await delay(100);
        return HttpResponse.json(reads === 1 ? RECORD : { ...RECORD, xv_name: 'Beta (saved)' });
      }),
      onPath('patch', 'xv_projects(R1)', async ({ request }) => {
        patched = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project/records/R1']} />);
    const name = await screen.findByLabelText(/^Name/);
    expect(name).toHaveValue('Alpha');
    expect(screen.getByLabelText('Count')).toHaveValue('3');
    expect(screen.getByDisplayValue('Ada Lovelace')).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Red' })).toBeChecked();

    // Nothing changed: nothing is sent.
    await user.click(screen.getByRole('button', { name: en['form.save'] }));
    expect(await screen.findByText(en['form.noChanges'])).toBeInTheDocument();
    expect(patched).toBeUndefined();

    await user.clear(name);
    await user.type(name, 'Beta');
    await user.selectOptions(screen.getByLabelText('Priority'), 'Low');
    await user.click(screen.getByRole('checkbox', { name: 'Blue' }));
    await user.click(screen.getByRole('button', { name: en['form.save'] }));
    expect(await screen.findByText(en['form.saved'])).toBeInTheDocument();
    expect(patched).toEqual({ xv_name: 'Beta', xv_prio: 2, xv_tags: '1,2' });
    // The record is read again after a save.
    expect(await screen.findByDisplayValue('Beta (saved)')).toBeInTheDocument();
    expect(reads).toBe(2);
  });

  it('shows the server error with its Dataverse code and keeps the form', async () => {
    server.use(
      http.post(`${API}/xv_projects`, () =>
        HttpResponse.json({ error: { code: '0x80040203', message: 'xv_name is required.' } }, { status: 400 }),
      ),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project/records/new']} />);
    // The required marker is only a marker: the server decides.
    expect(await screen.findByText('*')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: en['form.create'] }));
    expect(await screen.findByText('0x80040203')).toBeInTheDocument();
    expect(screen.getByText('xv_name is required.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: en['form.create'] })).toBeEnabled();
  });

  it('Lookup_uses_relationship_navigation_name', async () => {
    let posted: unknown;
    server.use(
      http.post(`${API}/xv_projects`, async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `${new URL(request.url).origin}/api/data/v9.2/xv_projects(R9)` } });
      }),
      onPath('get', 'xv_projects(R9)', () => HttpResponse.json({ ...RECORD, xv_projectid: 'R9', xv_name: 'Created', _xv_parent_value: 'c2', '_xv_parent_value@Microsoft.Dynamics.CRM.lookuplogicalname': 'contact' })),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project/records/new']} />);
    await user.type(await screen.findByLabelText(/^Name/), 'Gamma');
    await user.click(screen.getByRole('button', { name: 'Choose Parent' }));
    await user.click(await screen.findByRole('option', { name: 'Grace Hopper' }));
    await user.click(screen.getByRole('button', { name: en['form.create'] }));
    expect(await screen.findByText(en['form.created'])).toBeInTheDocument();
    // `xv_ParentId` comes from ReferencingEntityNavigationPropertyName; the logical name is `xv_parent`.
    expect(posted).toEqual({ xv_name: 'Gamma', 'xv_ParentId@odata.bind': '/contacts(c2)' });
    // The created record opens: wait for its form so no request outlives the test.
    expect(await screen.findByText('Edit Project')).toBeInTheDocument();
    expect(await screen.findByDisplayValue('Created')).toBeInTheDocument();
  });

  it('Polymorphic_lookup_asks_the_target_table_first', async () => {
    let patched: unknown;
    server.use(
      onPath('get', 'xv_projects(R1)', () => HttpResponse.json(RECORD)),
      onPath('patch', 'xv_projects(R1)', async ({ request }) => {
        patched = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project/records/R1']} />);
    await screen.findByLabelText(/^Name/);
    await user.click(screen.getByRole('button', { name: 'Choose Regarding' }));
    // No search is offered until the table is chosen.
    const table = await screen.findByLabelText('Table of Regarding');
    expect(screen.queryByPlaceholderText(en['picker.search'])).toBeNull();
    await user.selectOptions(table, 'account');
    await user.click(await screen.findByRole('option', { name: 'Acme' }));
    // The side panel is modal: the form is reachable again once it has closed.
    await user.click(await screen.findByRole('button', { name: en['form.save'] }));
    expect(await screen.findByText(en['form.saved'])).toBeInTheDocument();
    expect(patched).toEqual({ 'xv_Regarding@odata.bind': '/accounts(a1)' });
    await waitFor(() => expect(screen.getByRole('button', { name: en['form.save'] })).toBeEnabled());
    await screen.findByDisplayValue('Alpha');
  });

  it('clears a lookup with a null bind on its navigation property', async () => {
    let patched: unknown;
    let reads = 0;
    server.use(
      onPath('get', 'xv_projects(R1)', () => {
        reads++;
        return HttpResponse.json(RECORD);
      }),
      onPath('patch', 'xv_projects(R1)', async ({ request }) => {
        patched = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project/records/R1']} />);
    await screen.findByLabelText(/^Name/);
    await user.click(screen.getByRole('button', { name: 'Clear Parent' }));
    await user.click(screen.getByRole('button', { name: en['form.save'] }));
    expect(await screen.findByText(en['form.saved'])).toBeInTheDocument();
    expect(patched).toEqual({ 'xv_ParentId@odata.bind': null });
    // The record is read again after the save: wait for it so no request outlives the test.
    await waitFor(() => expect(reads).toBe(2));
    await waitFor(() => expect(screen.getByRole('button', { name: en['form.save'] })).toBeEnabled());
  });

  it('Create_form_shows_file_fields_disabled_with_reason', async () => {
    let posted: unknown;
    server.use(
      http.post(`${API}/xv_projects`, async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `${new URL(request.url).origin}/api/data/v9.2/xv_projects(R1)` } });
      }),
      onPath('get', 'xv_projects(R1)', () => HttpResponse.json(RECORD)),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project/records/new']} />);
    await screen.findByLabelText(/^Name/);
    // Files and images are set on an existing record only: shown, disabled, with the reason.
    for (const label of ['Scan', 'Document']) {
      const field = screen.getByRole('group', { name: label });
      expect(within(field).getByText(en['field.file.afterCreate'])).toBeInTheDocument();
      expect(within(field).getByRole('button', { name: `Upload ${label}` })).toBeDisabled();
      expect(within(field).getByLabelText(`File for ${label}`)).toBeDisabled();
    }
    await user.type(screen.getByLabelText(/^Name/), 'X');
    await user.click(screen.getByRole('button', { name: en['form.create'] }));
    expect(await screen.findByText(en['form.created'])).toBeInTheDocument();
    // Never sent in the record body: an image or a file goes through its own upload.
    expect(posted).toEqual({ xv_name: 'X' });
    // The created record opens with the editors enabled: wait for it so no request outlives the test.
    expect(await screen.findByText('Edit Project')).toBeInTheDocument();
    await waitFor(() => expect(within(screen.getByRole('group', { name: 'Scan' })).getByRole('button', { name: 'Upload Scan' })).toBeEnabled());
  });

  it('Unsupported_type_is_visible_and_read_only', async () => {
    let posted: unknown;
    server.use(
      http.post(`${API}/xv_projects`, async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `${new URL(request.url).origin}/api/data/v9.2/xv_projects(R1)` } });
      }),
      onPath('get', 'xv_projects(R1)', () => HttpResponse.json(RECORD)),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project/records/new']} />);
    await screen.findByLabelText(/^Name/);
    const message = screen.getByText('Not editable in the generic UI (type Uniqueidentifier)');
    expect(message).toBeInTheDocument();
    // Read-only: no input is offered for the column, and it is never sent.
    expect(screen.queryByLabelText('Reference')).toBeNull();
    expect(within(message.closest('div')!.parentElement!).queryByRole('textbox')).toBeNull();
    await user.type(screen.getByLabelText(/^Name/), 'X');
    await user.click(screen.getByRole('button', { name: en['form.create'] }));
    expect(await screen.findByText(en['form.created'])).toBeInTheDocument();
    expect(posted).toEqual({ xv_name: 'X' });
    // The created record opens: wait for its form so no request outlives the test.
    expect(await screen.findByText('Edit Project')).toBeInTheDocument();
  });

  it('names a number field that does not parse instead of sending it', async () => {
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project/records/new']} />);
    await user.type(await screen.findByLabelText('Count'), '12x');
    await user.click(screen.getByRole('button', { name: en['form.create'] }));
    expect(await screen.findByText(en['form.err.wholeNumber'].replace('{value}', '12x'))).toBeInTheDocument();
  });

  it('deletes after confirmation and returns to the table', async () => {
    let deleted: string | undefined;
    server.use(
      onPath('get', 'xv_projects(R1)', () => HttpResponse.json(RECORD)),
      onPath('delete', 'xv_projects(R1)', ({ request }) => {
        deleted = new URL(request.url).pathname;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project/records/R1']} />);
    await screen.findByLabelText(/^Name/);
    await user.click(screen.getByRole('button', { name: en['records.delete.button'] }));
    expect(deleted).toBeUndefined();
    await user.click(await screen.findByRole('button', { hidden: true, name: en['records.delete.confirm'] }));
    expect(await screen.findByText(en['records.deleted'])).toBeInTheDocument();
    expect(deleted).toBe('/api/data/v9.2/xv_projects(R1)');
    // Back on the table page: its metadata loads.
    expect(await screen.findByRole('tab', { name: en['table.tab.columns'] })).toBeInTheDocument();
  });
  it('RecordForm shows the audit history panel only for audited tables', async () => {
    let historyCalls = 0;
    server.use(
      onPath('get', 'xv_projects(R1)', () => HttpResponse.json(RECORD)),
      onPath('get', 'audits(a1)/Microsoft.Dynamics.CRM.RetrieveAuditDetails', () =>
        HttpResponse.json({ AuditDetail: { '@odata.type': '#Microsoft.Dynamics.CRM.AttributeAuditDetail', OldValue: { xv_name: 'Alpha' }, NewValue: { xv_name: 'Alpha 2' } } }),
      ),
      http.get(`${API}/audits`, () => {
        historyCalls++;
        return HttpResponse.json({ value: [{ auditid: 'a1', createdon: '2026-11-09T10:00:00Z', 'action@OData.Community.Display.V1.FormattedValue': 'Update', '_userid_value@OData.Community.Display.V1.FormattedValue': 'Grace Hopper' }] });
      }),
    );
    // Not audited: no panel, no request.
    const first = render(<App initialEntries={['/manage/tables/xv_project/records/R1']} />);
    await screen.findByDisplayValue('Alpha');
    expect(screen.queryByRole('heading', { name: en['audit.history.title'] })).toBeNull();
    expect(historyCalls).toBe(0);
    first.unmount();

    const audited = { ...project, IsAuditEnabled: { Value: true, CanBeChanged: true } };
    installMetadata([audited, contact, account], relationships);
    render(<App initialEntries={['/manage/tables/xv_project/records/R1']} />);
    await screen.findByDisplayValue('Alpha');
    expect(await screen.findByRole('heading', { name: en['audit.history.title'] })).toBeInTheDocument();
    expect(await screen.findByText('Alpha 2')).toBeInTheDocument();
    expect(screen.getByText('Grace Hopper')).toBeInTheDocument();
    expect(historyCalls).toBe(1);
  });
  it('RecordForm audit history panel names the missing privilege on 403', async () => {
    const audited = { ...project, IsAuditEnabled: { Value: true, CanBeChanged: true } };
    installMetadata([audited, contact, account], relationships);
    server.use(
      onPath('get', 'xv_projects(R1)', () => HttpResponse.json(RECORD)),
      http.get(`${API}/audits`, () => HttpResponse.json({ error: { code: '0x80040220', message: 'Access is denied.' } }, { status: 403 })),
    );
    render(<App initialEntries={['/manage/tables/xv_project/records/R1']} />);
    await screen.findByDisplayValue('Alpha');
    expect(await screen.findByText(/prvReadAuditSummary/)).toBeInTheDocument();
    expect(screen.queryByText(en['audit.history.empty'])).toBeNull();
  });

  it('RecordForm audit history panel shows other errors as they are', async () => {
    const audited = { ...project, IsAuditEnabled: { Value: true, CanBeChanged: true } };
    installMetadata([audited, contact, account], relationships);
    server.use(
      onPath('get', 'xv_projects(R1)', () => HttpResponse.json(RECORD)),
      http.get(`${API}/audits`, () => HttpResponse.json({ error: { code: '0x1', message: 'Database is down.' } }, { status: 500 })),
    );
    render(<App initialEntries={['/manage/tables/xv_project/records/R1']} />);
    await screen.findByDisplayValue('Alpha');
    expect(await screen.findByText(/Database is down\./)).toBeInTheDocument();
    expect(screen.queryByText(/prvReadAuditSummary/)).toBeNull();
  });
  it('shows "No access" only for secured columns the form requested', async () => {
    const secured = rawEntity(
      'xv_deal',
      'xv_deals',
      [
        rawAttr('xv_dealid', 'Uniqueidentifier', { IsPrimaryId: true, IsValidForUpdate: false }),
        rawAttr('xv_name', 'String', { IsPrimaryName: true, display: 'Name' }),
        // Secured but not valid for read: never requested, so its absence from the record says nothing about access.
        rawAttr('xv_secret', 'String', { display: 'Secret', IsSecured: true, IsValidForRead: false }),
      ],
      { PrimaryIdAttribute: 'xv_dealid', PrimaryNameAttribute: 'xv_name', display: 'Deal' },
    );
    installMetadata([secured], []);
    let select: string | null = null;
    server.use(
      onPath('get', 'xv_deals(D1)', ({ request }) => {
        select = new URL(request.url).searchParams.get('$select');
        return HttpResponse.json({ xv_dealid: 'D1', xv_name: 'Big deal' });
      }),
    );
    render(<App initialEntries={['/manage/tables/xv_deal/records/D1']} />);
    await screen.findByDisplayValue('Big deal');
    expect(select).not.toContain('xv_secret');
    expect(screen.queryByText(en['fieldSecurity.noAccess'])).toBeNull();
  });
  it('shows a lock on secured columns and "No access" for a secured column the record omits', async () => {
    const secured = rawEntity(
      'xv_deal',
      'xv_deals',
      [
        rawAttr('xv_dealid', 'Uniqueidentifier', { IsPrimaryId: true, IsValidForUpdate: false }),
        rawAttr('xv_name', 'String', { IsPrimaryName: true, display: 'Name' }),
        rawAttr('xv_margin', 'Integer', { display: 'Margin', IsSecured: true }),
        rawAttr('xv_notes', 'String', { display: 'Notes', IsSecured: true }),
      ],
      { PrimaryIdAttribute: 'xv_dealid', PrimaryNameAttribute: 'xv_name', display: 'Deal' },
    );
    installMetadata([secured], []);
    let select: string | null = null;
    let patched: unknown;
    server.use(
      // xv_margin is secured and unreadable: the server leaves it out; xv_notes is secured and readable.
      onPath('get', 'xv_deals(D1)', ({ request }) => {
        select = new URL(request.url).searchParams.get('$select');
        return HttpResponse.json({ xv_dealid: 'D1', xv_name: 'Big deal', xv_notes: 'Visible' });
      }),
      onPath('patch', 'xv_deals(D1)', async ({ request }) => {
        patched = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_deal/records/D1']} />);
    const notes = await screen.findByRole('textbox', { name: /Notes/ });
    expect(notes).toHaveValue('Visible');
    expect(select).toContain('xv_margin');
    expect(screen.getByText(en['fieldSecurity.noAccess'])).toBeInTheDocument();
    expect(screen.queryByRole('spinbutton', { name: /Margin/ })).toBeNull();
    expect(screen.queryByRole('textbox', { name: /Margin/ })).toBeNull();
    expect(screen.getAllByRole('img', { name: en['fieldSecurity.lock'] })).toHaveLength(2);

    // Saving never writes the column that could not be read.
    await user.clear(screen.getByRole('textbox', { name: /Name/ }));
    await user.type(screen.getByRole('textbox', { name: /Name/ }), 'Bigger deal');
    await user.click(screen.getByRole('button', { name: en['form.save'] }));
    await waitFor(() => expect(patched).toEqual({ xv_name: 'Bigger deal' }));
    expect(await screen.findByText(en['form.saved'])).toBeInTheDocument();
  });
});

describe('RecordForm money columns', () => {
  it('money base and exchange rate are read-only, currency lookup is editable', async () => {
    const budget = rawEntity(
      'xv_budget',
      'xv_budgets',
      [
        rawAttr('xv_budgetid', 'Uniqueidentifier', { IsPrimaryId: true, IsValidForUpdate: false }),
        rawAttr('xv_name', 'String', { IsPrimaryName: true, display: 'Name' }),
        rawAttr('xv_amount', 'Money', { display: 'Amount' }),
        rawAttr('xv_amount_base', 'Money', { display: 'Amount (Base)', IsValidForCreate: false, IsValidForUpdate: false }),
        rawAttr('exchangerate', 'Decimal', { display: 'Exchange Rate', IsValidForCreate: false, IsValidForUpdate: false }),
        rawAttr('transactioncurrencyid', 'Lookup', { display: 'Currency', Targets: ['transactioncurrency'] }),
      ],
      { PrimaryIdAttribute: 'xv_budgetid', PrimaryNameAttribute: 'xv_name', display: 'Budget' },
    );
    const currency = rawEntity('transactioncurrency', 'transactioncurrencies', [], { PrimaryIdAttribute: 'transactioncurrencyid', PrimaryNameAttribute: 'currencyname' });
    installMetadata([budget, currency], [rawOneToMany('TransactionCurrency_xv_budget', 'xv_budget', 'transactioncurrencyid', 'transactioncurrency', 'transactioncurrencyid')]);
    render(<App initialEntries={['/manage/tables/xv_budget/records/new']} />);
    const amount = await screen.findByLabelText(/^Amount$/);
    expect(amount).toBeEnabled();
    // Computed by the server (valid for neither create nor update): the form does not offer them at all.
    expect(screen.queryByLabelText(/^Amount \(Base\)/)).toBeNull();
    expect(screen.queryByLabelText(/^Exchange Rate/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Choose Currency' })).toBeEnabled();
  });
});

describe('RecordForm activities and notes', () => {
  const FORMATTED = '@OData.Community.Display.V1.FormattedValue';
  const LOGICAL = '@Microsoft.Dynamics.CRM.lookuplogicalname';
  const party = (logical: string) => ({ Targets: ['account', 'contact', 'systemuser'], AttributeType: 'PartyList', display: logical });
  const email = rawEntity(
    'email',
    'emails',
    [
      rawAttr('activityid', 'Uniqueidentifier', { IsPrimaryId: true, IsValidForUpdate: false }),
      rawAttr('subject', 'String', { IsPrimaryName: true, display: 'Subject' }),
      rawAttr('from', 'Lookup', party('From')),
      rawAttr('to', 'Lookup', party('To')),
      rawAttr('cc', 'Lookup', party('Cc')),
      rawAttr('activitytypecode', 'EntityName', { display: 'Activity Type', IsValidForCreate: false, IsValidForUpdate: false }),
    ],
    { PrimaryIdAttribute: 'activityid', PrimaryNameAttribute: 'subject', display: 'Email', IsActivity: true, HasNotes: true },
  );
  // The party rows of an email are the collection-valued navigation property the server names after the relationship.
  const partiesRelationship = {
    ...rawOneToMany('email_activity_parties', 'activityparty', 'activityid', 'email', 'activityid_email'),
    ReferencedEntityNavigationPropertyName: 'email_activity_parties',
  };
  const EMAIL = {
    activityid: 'E1',
    subject: 'Hello',
    activitytypecode: 'email',
    email_activity_parties: [
      { activitypartyid: 'p1', participationtypemask: 1, _partyid_value: 'u1', [`_partyid_value${LOGICAL}`]: 'systemuser', [`_partyid_value${FORMATTED}`]: 'Admin User', addressused: 'admin@example.com' },
      { activitypartyid: 'p2', participationtypemask: 2, _partyid_value: 'c1', [`_partyid_value${LOGICAL}`]: 'contact', [`_partyid_value${FORMATTED}`]: 'Ada Lovelace', addressused: 'ada@example.com' },
      { activitypartyid: 'p3', participationtypemask: 2, _partyid_value: null, addressused: 'bob@example.com' },
      { activitypartyid: 'p4', participationtypemask: 9, _partyid_value: 'u1', [`_partyid_value${LOGICAL}`]: 'systemuser', [`_partyid_value${FORMATTED}`]: 'Admin User' },
    ],
  };

  it('record form shows party lists read-only', async () => {
    let query: URLSearchParams | undefined;
    let patched: unknown;
    installMetadata([email, contact], [partiesRelationship]);
    server.use(
      onPath('get', 'emails(E1)', ({ request }) => {
        query = new URL(request.url).searchParams;
        return HttpResponse.json(EMAIL);
      }),
      onPath('patch', 'emails(E1)', async ({ request }) => {
        patched = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/email/records/E1']} />);
    const subject = await screen.findByLabelText(/^Subject/);
    expect(subject).toHaveValue('Hello');

    // Party lists are not properties of the record: they are read through the party navigation, never selected.
    const select = query!.get('$select')!.split(',');
    expect(select).not.toContain('to');
    expect(select).not.toContain('_to_value');
    expect(select).not.toContain('_from_value');
    expect(query!.get('$expand')).toBe('email_activity_parties($select=participationtypemask,_partyid_value,addressused)');

    const to = screen.getByRole('group', { name: 'To' });
    // A party shows its name and table; an unresolved address shows the address.
    expect(within(to).getByText('Ada Lovelace (contact)')).toBeInTheDocument();
    expect(within(to).getByText('bob@example.com')).toBeInTheDocument();
    expect(within(screen.getByRole('group', { name: 'From' })).getByText('Admin User (systemuser)')).toBeInTheDocument();
    expect(within(screen.getByRole('group', { name: 'Cc' })).getByText(en['form.partyList.none'])).toBeInTheDocument();
    // Read-only: no picker, no input; the owner row (type 9) belongs to no party list column.
    expect(screen.queryByRole('button', { name: 'Choose To' })).toBeNull();
    expect(within(to).queryByRole('textbox')).toBeNull();
    expect(screen.getAllByText(en['form.partyList.readOnly']).length).toBeGreaterThan(0);

    await user.clear(subject);
    await user.type(subject, 'Hi');
    await user.click(screen.getByRole('button', { name: en['form.save'] }));
    expect(await screen.findByText(en['form.saved'])).toBeInTheDocument();
    expect(patched).toEqual({ subject: 'Hi' });
  });

  it('record form shows party lists read-only on create and never sends them', async () => {
    let posted: unknown;
    installMetadata([email, contact], [partiesRelationship]);
    server.use(
      http.post(`${API}/emails`, async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/email/records/new']} />);
    await user.type(await screen.findByLabelText(/^Subject/), 'New');
    expect(within(screen.getByRole('group', { name: 'To' })).getByText(en['form.partyList.none'])).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: en['form.create'] }));
    expect(await screen.findByText(en['form.created'])).toBeInTheDocument();
    expect(posted).toEqual({ subject: 'New' });
  });

  it('record form hides documentbody', async () => {
    let select: string[] | undefined;
    let patched: unknown;
    const annotation = rawEntity(
      'annotation',
      'annotations',
      [
        rawAttr('annotationid', 'Uniqueidentifier', { IsPrimaryId: true, IsValidForUpdate: false }),
        rawAttr('subject', 'String', { IsPrimaryName: true, display: 'Title' }),
        rawAttr('notetext', 'Memo', { display: 'Description' }),
        rawAttr('filename', 'String', { display: 'File Name' }),
        rawAttr('documentbody', 'String', { display: 'Document', MaxLength: 1073741823 }),
      ],
      { PrimaryIdAttribute: 'annotationid', PrimaryNameAttribute: 'subject', display: 'Note' },
    );
    installMetadata([annotation]);
    server.use(
      onPath('get', 'annotations(N1)', ({ request }) => {
        select = new URL(request.url).searchParams.get('$select')!.split(',');
        return HttpResponse.json({ annotationid: 'N1', subject: 'Scan', notetext: 'See file', filename: 'scan.pdf' });
      }),
      onPath('patch', 'annotations(N1)', async ({ request }) => {
        patched = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/annotation/records/N1']} />);
    const title = await screen.findByLabelText(/^Title/);
    expect(screen.getByLabelText(/^File Name/)).toHaveValue('scan.pdf');
    // The body (base64, up to the upload limit) is neither read nor offered.
    expect(screen.queryByLabelText(/^Document/)).toBeNull();
    expect(select).not.toContain('documentbody');
    await user.clear(title);
    await user.type(title, 'Scan 2');
    await user.click(screen.getByRole('button', { name: en['form.save'] }));
    expect(await screen.findByText(en['form.saved'])).toBeInTheDocument();
    expect(patched).toEqual({ subject: 'Scan 2' });
  });
});
