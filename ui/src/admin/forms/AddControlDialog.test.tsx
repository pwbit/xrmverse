import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/server';
import { en } from '../../shell/i18n/en';
import { CLASS_ID } from '../../parsers/controlClass';
import { API, onPath } from '../security/testSupport';
import { rawOneToMany } from '../tables/tableTestSupport';
import { addSubgrid } from './formControls';
import {
  ACCOUNT_VIEW, canvasReady, CONTACT_CARD, CONTACT_DEFAULT_VIEW, CONTACT_VIEW, controlById, installControls, LOGO_WR, openForm,
  PAGE_TEST_TIMEOUT, paramsOf, propsCard, saveButton, savedForm, section, structure, type Call,
} from './controlsTestSupport';

// A spy on what the subgrid dialog would add: it sees every render's choice, also one the DOM never shows.
vi.mock('./formControls', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./formControls')>();
  return { ...actual, addSubgrid: vi.fn(actual.addSubgrid) };
});

type User = ReturnType<typeof userEvent.setup>;

afterEach(() => sessionStorage.clear());

const addButton = (key: 'forms.controls.addSubgrid' | 'forms.controls.addQuickView' | 'forms.controls.addWebResource') =>
  within(structure()).getByRole('button', { name: en[key] });

async function openDialog(user: User, key: Parameters<typeof addButton>[0]) {
  await canvasReady();
  await user.click(within(section('Main')).getByRole('button', { name: 'Main' }));
  await user.click(addButton(key));
  return screen.findByRole('dialog');
}

const confirm = (dialog: HTMLElement) => within(dialog).getByRole('button', { name: en['forms.controls.add'] });

async function saved(user: User, calls: Call[]) {
  await user.click(saveButton());
  await waitFor(() => expect(calls.some((c) => c.path.startsWith('systemforms('))).toBe(true));
  return savedForm(calls);
}

/** The `<cell>` holding the control. */
const cellOf = (control: Element | undefined) => control?.parentElement;

describe('AddControlDialog', { timeout: PAGE_TEST_TIMEOUT }, () => {
  it.each([
    [2, ['forms.controls.addSubgrid', 'forms.controls.addQuickView', 'forms.controls.addWebResource']],
    [6, ['forms.controls.addSubgrid', 'forms.controls.addWebResource']],
    [7, []],
  ] as const)('a type %i form offers only its own Add buttons', async (type, offered) => {
    installControls({}, type);
    openForm();
    await canvasReady();
    for (const key of ['forms.controls.addSubgrid', 'forms.controls.addQuickView', 'forms.controls.addWebResource'] as const) {
      const button = within(structure()).queryByRole('button', { name: en[key] });
      if ((offered as readonly string[]).includes(key)) expect(button).toBeInTheDocument();
      else expect(button).not.toBeInTheDocument();
    }
  });

  it('the Add buttons wait for a section to be selected', async () => {
    installControls();
    openForm();
    await canvasReady();
    expect(addButton('forms.controls.addSubgrid')).toHaveAttribute('aria-disabled', 'true');
    await userEvent.setup().click(within(section('Controls')).getByRole('button', { name: 'Controls' }));
    expect(addButton('forms.controls.addSubgrid')).not.toHaveAttribute('aria-disabled', 'true');
  });

  it('the disabled Add buttons are described by a visible hint (A2)', async () => {
    installControls();
    openForm();
    await canvasReady();
    for (const key of ['forms.controls.addSubgrid', 'forms.controls.addQuickView', 'forms.controls.addWebResource'] as const) {
      const button = addButton(key);
      expect(button).toHaveAttribute('aria-disabled', 'true');
      expect(button).not.toHaveAttribute('title');
      const ids = (button.getAttribute('aria-describedby') ?? '').split(' ').filter(Boolean);
      expect(ids.length).toBeGreaterThan(0);
      expect(ids.map((id) => document.getElementById(id)?.textContent)).toContain(en['forms.controls.selectSection.hint']);
    }
    expect(within(structure()).getByText(en['forms.controls.selectSection.hint'])).toBeVisible();
    // A selected section: the hint goes, and the buttons point to nothing.
    await userEvent.setup().click(within(section('Controls')).getByRole('button', { name: 'Controls' }));
    expect(within(structure()).queryByText(en['forms.controls.selectSection.hint'])).not.toBeInTheDocument();
    expect(addButton('forms.controls.addSubgrid')).not.toHaveAttribute('aria-describedby');
  });

  it('adds a subgrid of related records with the default view preselected, and Save writes G2', async () => {
    const user = userEvent.setup();
    const calls = installControls();
    openForm();
    const dialog = await openDialog(user, 'forms.controls.addSubgrid');
    expect(confirm(dialog)).toBeDisabled();
    const relationship = within(dialog).getByRole('combobox', { name: en['forms.controls.relationship'] });
    await waitFor(() => expect(within(relationship).getAllByRole('option')).toHaveLength(2));
    // account_primary_contact is a lookup of account itself: not offered.
    expect(within(relationship).getAllByRole('option').map((o) => o.textContent)).toEqual([en['forms.controls.choose'], 'Contact (contact_customer_accounts)']);
    await user.selectOptions(relationship, 'contact_customer_accounts');
    const view = within(dialog).getByRole('combobox', { name: en['forms.controls.view'] });
    await waitFor(() => expect(view).toHaveValue(CONTACT_DEFAULT_VIEW));
    await user.selectOptions(view, CONTACT_VIEW);
    const perPage = within(dialog).getByRole('spinbutton', { name: en['forms.controls.recordsPerPage'] });
    expect(perPage).toHaveValue(4);
    await user.clear(perPage);
    await user.type(perPage, '300');
    expect(confirm(dialog)).toBeDisabled();
    await user.clear(perPage);
    await user.type(perPage, '10');
    const label = within(dialog).getByRole('textbox', { name: en['forms.controls.label'] });
    expect(label).toHaveValue('Contact');
    await user.clear(label);
    await user.type(label, 'Customers');
    await user.click(confirm(dialog));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    const added = within(section('Main')).getByRole('button', { name: 'Customers (Subgrid)' });
    expect(added).toHaveAttribute('aria-pressed', 'true');
    expect(within(propsCard()).getByText('Subgrid contact')).toBeInTheDocument();

    const form = await saved(user, calls);
    const control = controlById(form, 'contact');
    expect(control?.getAttribute('classid')).toBe(CLASS_ID.subgrid);
    expect(control?.getAttribute('indicationOfSubgrid')).toBe('true');
    expect(paramsOf(control)).toEqual([
      ['ViewId', `{${CONTACT_VIEW}}`],
      ['IsUserView', 'false'],
      ['RelationshipName', 'contact_customer_accounts'],
      ['TargetEntityType', 'contact'],
      ['AutoExpand', 'Fixed'],
      ['EnableQuickFind', 'false'],
      ['EnableViewPicker', 'false'],
      ['EnableJumpBar', 'false'],
      ['ChartGridMode', 'Grid'],
      ['VisualizationId', ''],
      ['IsUserChart', 'false'],
      ['EnableChartPicker', 'false'],
      ['RecordsPerPage', '10'],
    ]);
    const cell = cellOf(control);
    expect(cell?.getAttribute('colspan')).toBe('2');
    expect(cell?.getAttribute('rowspan')).toBe('10');
    expect(cell?.getAttribute('auto')).toBe('false');
    expect(cell?.getElementsByTagName('label')[0]?.getAttribute('description')).toBe('Customers');
  });

  it('a subgrid of all records lists any table and writes an empty RelationshipName', async () => {
    const user = userEvent.setup();
    const calls = installControls();
    openForm();
    const dialog = await openDialog(user, 'forms.controls.addSubgrid');
    await user.click(within(dialog).getByRole('radio', { name: en['forms.controls.records.all'] }));
    const table = within(dialog).getByRole('combobox', { name: en['forms.controls.table'] });
    await waitFor(() => expect(table).toBeEnabled());
    await user.selectOptions(table, 'account');
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: en['forms.controls.view'] })).toHaveValue(ACCOUNT_VIEW));
    await waitFor(() => expect(confirm(dialog)).toBeEnabled());
    await user.click(confirm(dialog));
    const form = await saved(user, calls);
    const params = Object.fromEntries(paramsOf(controlById(form, 'account')));
    expect(params).toMatchObject({ RelationshipName: '', TargetEntityType: 'account', ViewId: `{${ACCOUNT_VIEW}}`, RecordsPerPage: '4' });
  });

  it('adds a quick view: lookup, then target, then a quick view form (G3)', async () => {
    const user = userEvent.setup();
    const calls = installControls();
    openForm();
    const dialog = await openDialog(user, 'forms.controls.addQuickView');
    await user.selectOptions(within(dialog).getByRole('combobox', { name: en['forms.controls.lookup'] }), 'primarycontactid');
    expect(within(dialog).getByRole('combobox', { name: en['forms.controls.target'] })).toHaveValue('contact');
    const form = within(dialog).getByRole('combobox', { name: en['forms.controls.form'] });
    // Only the active type 6 forms of contact.
    await waitFor(() => expect(within(form).getAllByRole('option').map((o) => o.textContent)).toEqual(['Contact card']));
    expect(within(dialog).getByRole('textbox', { name: en['forms.controls.label'] })).toHaveValue('Primary Contact');
    await waitFor(() => expect(confirm(dialog)).toBeEnabled());
    await user.click(confirm(dialog));
    expect(within(section('Main')).getByRole('button', { name: 'Primary Contact (Quick view)' })).toBeInTheDocument();

    const xml = await saved(user, calls);
    const control = controlById(xml, 'primarycontactid_quickview');
    expect(control?.getAttribute('classid')).toBe(CLASS_ID.quickView);
    expect(control?.getAttribute('datafieldname')).toBe('primarycontactid');
    expect(control?.getAttribute('uniqueid')).toMatch(/^\{[0-9a-f-]{36}\}$/);
    expect(paramsOf(control)).toEqual([
      ['QuickForms', `<QuickForms><QuickFormIds><QuickFormId entityname="contact">{${CONTACT_CARD}}</QuickFormId></QuickFormIds></QuickForms>`],
    ]);
  });

  it('adds a web resource of the listed types (G4)', async () => {
    const user = userEvent.setup();
    const calls = installControls();
    openForm();
    const dialog = await openDialog(user, 'forms.controls.addWebResource');
    const resource = within(dialog).getByRole('combobox', { name: en['forms.controls.webResource'] });
    await waitFor(() => expect(resource).toBeEnabled());
    await user.selectOptions(resource, LOGO_WR);
    expect(within(dialog).getByRole('textbox', { name: en['forms.controls.label'] })).toHaveValue('Logo');
    await user.click(confirm(dialog));
    expect(within(section('Main')).getByRole('button', { name: 'Logo (Web resource)' })).toBeInTheDocument();

    const xml = await saved(user, calls);
    const control = controlById(xml, 'WebResource_logopng');
    expect(control?.getAttribute('classid')).toBe(CLASS_ID.webResource);
    expect(paramsOf(control)).toEqual([
      ['Url', 'new_logo.png'],
      ['PassParameters', 'false'],
      ['Security', 'true'],
      ['Scrolling', 'auto'],
      ['Border', 'true'],
      ['WebResourceId', `{${LOGO_WR}}`],
      ['Data', ''],
    ]);
  });

  it.each([
    ['views', 'forms.controls.addSubgrid', 'Cannot read savedquery'],
    ['relationships', 'forms.controls.addSubgrid', 'Cannot read relationships'],
    ['entities', 'forms.controls.addSubgrid', 'Cannot read tables'],
    ['forms', 'forms.controls.addQuickView', 'Cannot read systemform'],
    ['webResources', 'forms.controls.addWebResource', 'Cannot read webresource'],
  ] as const)('a %s list that fails shows the server text and Add stays disabled', async (list, key, message) => {
    const user = userEvent.setup();
    installControls({ [list]: message });
    openForm();
    const dialog = await openDialog(user, key);
    if (list === 'entities') await user.click(within(dialog).getByRole('radio', { name: en['forms.controls.records.all'] }));
    if (list === 'views') await user.selectOptions(await within(dialog).findByRole('combobox', { name: en['forms.controls.relationship'] }), 'contact_customer_accounts');
    if (list === 'forms') await user.selectOptions(within(dialog).getByRole('combobox', { name: en['forms.controls.lookup'] }), 'primarycontactid');
    // The server's text, verbatim (with its Dataverse code), in the error bar.
    const bar = await within(dialog).findByText((text) => text.startsWith(en['forms.controls.loadFailed'].replace('{detail}', '')) && text.includes(message));
    expect(bar).toBeInTheDocument();
    expect(confirm(dialog)).toBeDisabled();
  });

  it('after a relationship change the views of the previous target are never shown (H9)', async () => {
    const user = userEvent.setup();
    installControls();
    let release!: () => void;
    const gate = new Promise<void>((r) => {
      release = r;
    });
    server.use(
      http.get(`${API}/RelationshipDefinitions`, () => HttpResponse.json({ value: [
        rawOneToMany('contact_customer_accounts', 'contact', 'parentcustomerid', 'account'),
        rawOneToMany('account_parent_account', 'account', 'parentaccountid', 'account'),
      ] })),
      onPath('get', 'savedqueries', async ({ request }) => {
        if ((new URL(request.url).searchParams.get('$filter') ?? '').includes("'contact'")) {
          return HttpResponse.json({ value: [
            { savedqueryid: CONTACT_VIEW, name: 'Active Contacts', isdefault: false },
            { savedqueryid: CONTACT_DEFAULT_VIEW, name: 'My Contacts', isdefault: true },
          ] });
        }
        await gate;
        return HttpResponse.json({ value: [{ savedqueryid: ACCOUNT_VIEW, name: 'Active Accounts', isdefault: true }] });
      }),
    );
    openForm();
    const dialog = await openDialog(user, 'forms.controls.addSubgrid');
    const relationship = within(dialog).getByRole('combobox', { name: en['forms.controls.relationship'] });
    await waitFor(() => expect(within(relationship).getAllByRole('option')).toHaveLength(3));
    await user.selectOptions(relationship, 'contact_customer_accounts');
    const view = () => within(dialog).getByRole('combobox', { name: en['forms.controls.view'] });
    await waitFor(() => expect(view()).toHaveValue(CONTACT_DEFAULT_VIEW));
    await waitFor(() => expect(confirm(dialog)).toBeEnabled());

    vi.mocked(addSubgrid).mockClear();
    await user.selectOptions(relationship, 'account_parent_account');
    expect(within(view()).queryByRole('option', { name: 'My Contacts' })).not.toBeInTheDocument();
    expect(within(view()).queryByRole('option', { name: 'Active Contacts' })).not.toBeInTheDocument();
    expect(confirm(dialog)).toBeDisabled();
    release();
    await waitFor(() => expect(view()).toHaveValue(ACCOUNT_VIEW));
    expect(confirm(dialog)).toBeEnabled();
    // No render offered the account subgrid with a view of contact.
    const offered = vi.mocked(addSubgrid).mock.calls.map(([, , spec]) => [spec.targetEntity, spec.viewId.toLowerCase()]);
    expect(offered.filter(([table, id]) => table === 'account' && !id.includes(ACCOUNT_VIEW))).toEqual([]);
  });
});
