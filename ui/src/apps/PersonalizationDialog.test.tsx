import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { onPath } from '../admin/security/testSupport';
import { installMetadata, rawAttr, rawEntity } from '../admin/tables/tableTestSupport';
import { App } from '../shell/App';
import { en } from '../shell/i18n/en';
import { server } from '../test/server';
import { mockApps, ROLE_SALES, SITEMAP_ID, titled, USER_ID } from './testSupport';

const APP_ID = '11111111-0000-0000-0000-000000000001';
const SALES = { appmoduleid: APP_ID, name: 'Sales Hub', roles: [ROLE_SALES], siteMapId: SITEMAP_ID };
const SITEMAP = `<SiteMap><Area Id="sales">${titled('Sales')}<Group Id="c">${titled('Customers')}<SubArea Id="accounts" Entity="account">${titled('Accounts')}</SubArea></Group></Area></SiteMap>`;
const ACCOUNT = 'bbbbbbbb-0000-0000-0000-000000000002';
const F1 = '10000000-0000-0000-0000-000000000001';
const FORM_XML = `<form><tabs><tab name="general"><labels><label description="General" languagecode="1033"/></labels><columns><column width="100%"><sections><section name="s" showlabel="false" columns="1"><rows><row><cell id="{n}"><labels><label description="Name" languagecode="1033"/></labels><control id="name" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="name"/></cell></row></rows></section></sections></column></columns></tab></tabs></form>`;

const account = rawEntity(
  'account',
  'accounts',
  [rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }), rawAttr('name', 'String', { IsPrimaryName: true, display: 'Name' })],
  { SchemaName: 'Account', PrimaryNameAttribute: 'name', display: 'Account' },
);

/** The app's record form; `patches` collects the bodies of PATCH `usersettingscollection`, `settingReads` counts the reads of it. */
function setup(over: { patchFails?: boolean } = {}) {
  mockApps({ apps: [SALES], userRoles: [ROLE_SALES], privileges: ['prvReadAccount', 'prvWriteAccount'], siteMapXml: SITEMAP });
  installMetadata([account]);
  const patches: unknown[] = [];
  const reads = { settings: 0 };
  server.use(
    http.get('*/api/data/v9.2/systemforms', () =>
      HttpResponse.json({ value: [{ systemformid: F1, name: 'Main', objecttypecode: 'account', type: 2, isdefault: true, formactivationstate: 1, formxml: FORM_XML }] }),
    ),
    onPath('get', `systemusers(${USER_ID})/Microsoft.Dynamics.CRM.RetrievePrincipalAccess(Target=@t)`, () => HttpResponse.json({ AccessRights: 'ReadAccess, WriteAccess' })),
    onPath('get', `accounts(${ACCOUNT})`, () => HttpResponse.json({ accountid: ACCOUNT, name: 'Contoso' })),
    onPath('patch', `usersettingscollection(${USER_ID})`, async ({ request }) => {
      if (over.patchFails) return HttpResponse.json({ error: { code: '0x80040220', message: 'no write for you' } }, { status: 403 });
      patches.push(await request.json());
      return new HttpResponse(null, { status: 204 });
    }),
    http.get('*/api/data/v9.2/usersettingscollection*', () => {
      reads.settings++;
      return HttpResponse.json({ systemuserid: USER_ID, uilanguageid: 1033 });
    }),
  );
  return { patches, reads };
}

const open = () => render(<App initialEntries={[`/main.aspx?appid=${APP_ID}&pagetype=entityrecord&etn=account&id=${ACCOUNT}`]} />);

async function openDialog() {
  // The app's own frame replaces the loading header: open the menu only once the form is there.
  await screen.findByLabelText('Name');
  await userEvent.click(await screen.findByRole('button', { name: 'Ursula User' }));
  await userEvent.click(await screen.findByRole('menuitem', { name: en['personalization.menu'] }));
  return screen.findByRole('dialog', { name: en['personalization.title'] });
}

describe('PersonalizationDialog', () => {
  it('saves_the_look_and_scheme_on_the_users_own_row_then_reloads_the_settings', async () => {
    const { patches, reads } = setup();
    open();
    const dialog = await openDialog();
    const before = reads.settings;
    await userEvent.selectOptions(within(dialog).getByLabelText(en['personalization.look']), en['personalization.look.modern']);
    await userEvent.selectOptions(within(dialog).getByLabelText(en['personalization.scheme']), en['personalization.scheme.dark']);
    await userEvent.click(within(dialog).getByRole('button', { name: en['personalization.save'] }));
    await waitFor(() => expect(patches).toEqual([{ xrmverse_renderer: 'modern', xrmverse_colorscheme: 2, dateformatstring: 'M/d/yyyy', dateseparator: '/', timeformatstring: 'h:mm tt' }]));
    await waitFor(() => expect(reads.settings).toBeGreaterThan(before));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: en['personalization.title'] })).toBeNull());
  });

  it('app_default_sends_null', async () => {
    const { patches } = setup();
    open();
    const dialog = await openDialog();
    expect(within(dialog).getByLabelText(en['personalization.look'])).toHaveValue('');
    await userEvent.click(within(dialog).getByRole('button', { name: en['personalization.save'] }));
    await waitFor(() => expect(patches).toEqual([{ xrmverse_renderer: null, xrmverse_colorscheme: 0, dateformatstring: 'M/d/yyyy', dateseparator: '/', timeformatstring: 'h:mm tt' }]));
  });

  it('a_failed_save_shows_the_error_and_keeps_the_dialog', async () => {
    setup({ patchFails: true });
    open();
    const dialog = await openDialog();
    await userEvent.click(within(dialog).getByRole('button', { name: en['personalization.save'] }));
    expect(await screen.findByText(/no write for you/)).toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: en['personalization.title'] })).toBeInTheDocument();
  });

  it('with_a_dirty_form_the_unsaved_changes_prompt_comes_before_anything_is_saved_or_reloaded', async () => {
    const { patches, reads } = setup();
    open();
    await userEvent.type(await screen.findByLabelText('Name'), ' Ltd');
    const dialog = await openDialog();
    const before = reads.settings;
    await userEvent.selectOptions(within(dialog).getByLabelText(en['personalization.look']), en['personalization.look.modern']);
    await userEvent.click(within(dialog).getByRole('button', { name: en['personalization.save'] }));
    const prompt = await screen.findByRole('dialog', { name: en['record.discard.title'] });
    expect(patches).toEqual([]);
    expect(reads.settings).toBe(before);
    // Keeping the edits sends nothing and the form keeps them.
    await userEvent.click(within(prompt).getByRole('button', { name: en['common.cancel'] }));
    expect(patches).toEqual([]);
    expect(screen.getByLabelText('Name')).toHaveValue('Contoso Ltd');
    // Discarding goes on with the save and the reload.
    await userEvent.click(within(await screen.findByRole('dialog', { name: en['personalization.title'] })).getByRole('button', { name: en['personalization.save'] }));
    await userEvent.click(await screen.findByRole('button', { name: en['record.discard.confirm'] }));
    await waitFor(() => expect(patches).toEqual([{ xrmverse_renderer: 'modern', xrmverse_colorscheme: 0, dateformatstring: 'M/d/yyyy', dateseparator: '/', timeformatstring: 'h:mm tt' }]));
    await waitFor(() => expect(reads.settings).toBeGreaterThan(before));
  });

  it('saves_the_date_and_time_formats_with_a_sample_of_each', async () => {
    const { patches } = setup();
    open();
    const dialog = await openDialog();
    const date = within(dialog).getByLabelText(en['personalization.dateFormat']);
    expect(date).toHaveValue('M/d/yyyy');
    await userEvent.selectOptions(date, 'dd/MM/yyyy');
    await userEvent.selectOptions(within(dialog).getByLabelText(en['personalization.dateSeparator']), '.');
    await userEvent.selectOptions(within(dialog).getByLabelText(en['personalization.timeFormat']), 'HH:mm');
    // The options show today in the pattern, with the chosen separator.
    expect(within(date).getByRole('option', { selected: true }).textContent).toMatch(/^\d{2}\.\d{2}\.\d{4} \(dd\.MM\.yyyy\)$/);
    expect(within(within(dialog).getByLabelText(en['personalization.timeFormat'])).getByRole('option', { selected: true })).toHaveTextContent('14:30 (HH:mm)');
    await userEvent.click(within(dialog).getByRole('button', { name: en['personalization.save'] }));
    await waitFor(() =>
      expect(patches).toEqual([
        { xrmverse_renderer: null, xrmverse_colorscheme: 0, dateformatstring: 'dd/MM/yyyy', dateseparator: '.', timeformatstring: 'HH:mm' },
      ]),
    );
  });
});
