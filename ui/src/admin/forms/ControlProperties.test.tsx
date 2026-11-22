import { waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse } from 'msw';
import { afterEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { en } from '../../shell/i18n/en';
import { onPath } from '../security/testSupport';
import {
  ACCOUNT_CARD, canvasReady, CONTACT_CARD, CONTACT_DEFAULT_VIEW, CONTACT_VIEW, controlById, installControls, LOGO_WR, MAP_WR,
  openForm, PAGE_TEST_TIMEOUT, paramsOf, propsCard, saveButton, savedForm, section, type Call,
} from './controlsTestSupport';

type User = ReturnType<typeof userEvent.setup>;

afterEach(() => sessionStorage.clear());

async function select(user: User, name: string) {
  await canvasReady();
  await user.click(within(section('Controls')).getByRole('button', { name }));
  return propsCard();
}

async function saved(user: User, calls: Call[]) {
  await user.click(saveButton());
  await waitFor(() => expect(calls.some((c) => c.path.startsWith('systemforms('))).toBe(true));
  return savedForm(calls);
}

const combo = (panel: HTMLElement, name: string) => within(panel).getByRole('combobox', { name });
const toggle = (panel: HTMLElement, name: string) => within(panel).getByRole('switch', { name });

describe('ControlProperties', { timeout: PAGE_TEST_TIMEOUT }, () => {
  it('edits a subgrid: label, show label, visible, view, records per page and view selector (G2)', async () => {
    const user = userEvent.setup();
    const calls = installControls();
    openForm();
    const panel = await select(user, 'Contacts (Subgrid)');
    expect(within(panel).getByText('Subgrid Contacts')).toBeInTheDocument();
    expect(within(panel).getByText(en['forms.props.subgridRelated'].replace('{table}', 'contact').replace('{relationship}', 'contact_customer_accounts'))).toBeInTheDocument();
    const view = combo(panel, en['forms.controls.view']);
    // The source's id is upper case in braces: the view is still recognised.
    await waitFor(() => expect(view).toHaveValue(`{${CONTACT_VIEW}}`));
    await user.selectOptions(view, `{${CONTACT_DEFAULT_VIEW}}`);
    const perPage = within(panel).getByRole('spinbutton', { name: en['forms.controls.recordsPerPage'] });
    await user.clear(perPage);
    expect(within(panel).getByText(en['forms.controls.recordsPerPage.invalid'].replace('{max}', '250'))).toBeInTheDocument();
    await user.type(perPage, '25');
    await user.click(toggle(panel, en['forms.props.viewPicker']));
    const label = within(panel).getByRole('textbox', { name: en['forms.props.label'] });
    await user.clear(label);
    await user.type(label, 'Kids');
    await user.click(toggle(panel, en['forms.props.showLabel']));
    await user.click(toggle(panel, en['forms.props.visible']));

    const form = await saved(user, calls);
    const control = controlById(form, 'Contacts');
    // Only the edited parameters change; the others keep their element, order and text.
    expect(paramsOf(control)).toEqual([
      ['ViewId', `{${CONTACT_DEFAULT_VIEW}}`],
      ['IsUserView', 'false'],
      ['RelationshipName', 'contact_customer_accounts'],
      ['TargetEntityType', 'contact'],
      ['AutoExpand', 'Fixed'],
      ['EnableViewPicker', 'true'],
      ['RecordsPerPage', '25'],
    ]);
    const cell = control?.parentElement;
    expect(cell?.getElementsByTagName('label')[0]?.getAttribute('description')).toBe('Kids');
    expect(cell?.getAttribute('showlabel')).toBe('false');
    expect(cell?.getAttribute('visible')).toBe('false');
    // A records-per-page edit leaves the cell's rows as they are.
    expect(cell?.getAttribute('rowspan')).toBeNull();
  });

  it('a lookup with two targets offers a form per target (G3)', async () => {
    const user = userEvent.setup();
    const calls = installControls();
    openForm();
    const panel = await select(user, 'Parent card (Quick view)');
    expect(within(panel).getByText(en['forms.props.quickViewLookup'].replace('{lookup}', 'Parent'))).toBeInTheDocument();
    const forAccount = combo(panel, en['forms.props.formFor'].replace('{table}', 'account'));
    const forContact = combo(panel, en['forms.props.formFor'].replace('{table}', 'contact'));
    await waitFor(() => expect(forAccount).toHaveValue(`{${ACCOUNT_CARD}}`));
    expect(forContact).toHaveValue('');
    // The only form of the control cannot be cleared.
    expect(within(forAccount).getByRole('option', { name: en['forms.props.noForm'] })).toBeDisabled();
    await waitFor(() => expect(forContact).toBeEnabled());
    await user.selectOptions(forContact, `{${CONTACT_CARD}}`);
    expect(within(forAccount).getByRole('option', { name: en['forms.props.noForm'] })).toBeEnabled();

    const form = await saved(user, calls);
    expect(paramsOf(controlById(form, 'parent_qv'))).toEqual([
      ['QuickForms', `<QuickForms><QuickFormIds><QuickFormId entityname="account">{${ACCOUNT_CARD}}</QuickFormId><QuickFormId entityname="contact">{${CONTACT_CARD}}</QuickFormId></QuickFormIds></QuickForms>`],
    ]);
  });

  it('edits a web resource: resource, data, pass parameters, cross-frame scripting, scrolling and border (G4)', async () => {
    const user = userEvent.setup();
    const calls = installControls();
    openForm();
    const panel = await select(user, 'Map (Web resource)');
    const resource = combo(panel, en['forms.controls.webResource']);
    await waitFor(() => expect(resource).toHaveValue(MAP_WR));
    await user.selectOptions(resource, LOGO_WR);
    await user.type(within(panel).getByRole('textbox', { name: en['forms.props.data'] }), 'a=1');
    await user.click(toggle(panel, en['forms.props.passParameters']));
    await user.click(toggle(panel, en['forms.props.restrictScripting']));
    await user.selectOptions(combo(panel, en['forms.props.scrolling']), 'no');
    await user.click(toggle(panel, en['forms.props.border']));

    const form = await saved(user, calls);
    expect(paramsOf(controlById(form, 'WebResource_map'))).toEqual([
      ['Url', 'new_logo.png'],
      ['PassParameters', 'true'],
      ['Security', 'false'],
      ['Scrolling', 'no'],
      ['Border', 'false'],
      ['WebResourceId', `{${LOGO_WR}}`],
      ['Data', 'a=1'],
    ]);
  });

  it.each([['Site (Web resource)', 'Web resource'], ['Timeline (Timeline)', 'Timeline']])('%s stays a read-only chip', async (name, kind) => {
    const user = userEvent.setup();
    installControls();
    openForm();
    const panel = await select(user, name);
    expect(within(panel).getByText(en['forms.props.control'].replace('{kind}', kind))).toBeInTheDocument();
    expect(within(panel).queryByRole('textbox')).not.toBeInTheDocument();
    expect(within(panel).queryByRole('combobox')).not.toBeInTheDocument();
  });

  it.each([
    ['views', 'Contacts (Subgrid)', 'Cannot read savedquery', 'forms.controls.view'],
    ['forms', 'Parent card (Quick view)', 'Cannot read systemform', undefined],
    ['webResources', 'Map (Web resource)', 'Cannot read webresource', 'forms.controls.webResource'],
  ] as const)('a %s list that fails shows the server text in the panel', async (list, name, message, field) => {
    const user = userEvent.setup();
    installControls({ [list]: message });
    openForm();
    const panel = await select(user, name);
    const prefix = en['forms.controls.loadFailed'].replace('{detail}', '');
    // One bar per list read: the quick view reads one per target.
    const bars = await within(panel).findAllByText((text) => text.startsWith(prefix) && text.includes(message));
    expect(bars.length).toBeGreaterThan(0);
    if (field) expect(combo(panel, en[field])).toBeDisabled();
    else for (const c of within(panel).getAllByRole('combobox')) expect(c).toBeDisabled();
  });

  it('a quick view target change never shows the forms of the previous target (H9)', async () => {
    const user = userEvent.setup();
    installControls();
    let release!: () => void;
    const gate = new Promise<void>((r) => {
      release = r;
    });
    server.use(onPath('get', 'systemforms', async ({ request }) => {
      if ((new URL(request.url).searchParams.get('$filter') ?? '').includes("'contact'")) {
        await gate;
        return HttpResponse.json({ value: [{ systemformid: CONTACT_CARD, name: 'Contact card', type: 6, formactivationstate: 1 }] });
      }
      return HttpResponse.json({ value: [{ systemformid: ACCOUNT_CARD, name: 'Account card', type: 6, formactivationstate: 1 }] });
    }));
    openForm();
    const panel = await select(user, 'Parent card (Quick view)');
    const forAccount = combo(panel, en['forms.props.formFor'].replace('{table}', 'account'));
    const forContact = combo(panel, en['forms.props.formFor'].replace('{table}', 'contact'));
    await waitFor(() => expect(forAccount).toHaveValue(`{${ACCOUNT_CARD}}`));
    // The contact list is still on its way: the contact picker offers nothing of account.
    expect(within(forContact).queryByRole('option', { name: 'Account card' })).not.toBeInTheDocument();
    expect(forContact).toBeDisabled();
    release();
    await waitFor(() => expect(forContact).toBeEnabled());
    expect(within(forContact).getAllByRole('option').map((o) => o.textContent)).toEqual([en['forms.props.noForm'], 'Contact card']);
    await user.selectOptions(forContact, `{${CONTACT_CARD}}`);
    expect(forContact).toHaveValue(`{${CONTACT_CARD}}`);
    expect(within(forAccount).queryByRole('option', { name: 'Contact card' })).not.toBeInTheDocument();
  });
});
