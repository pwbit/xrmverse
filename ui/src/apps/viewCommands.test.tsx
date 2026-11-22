import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it } from 'vitest';
import { ACTIVE, MINE, openAccounts } from '../renderers/classic/viewTestSupport';
import { server } from '../test/server';
import { setUiLanguage } from '../shell/i18n/t';
import type { MockView } from './testSupport';

afterEach(() => {
  sessionStorage.clear();
  setUiLanguage(undefined);
});

const NEW_ID = 'e0000000-0000-0000-0000-0000000000aa';
const OTHER_USER = 'aaaaaaaa-0000-0000-0000-000000000099';

interface Sent {
  method: string;
  path: string;
  body?: Record<string, unknown>;
}

/** Answers the userquery writes like the server, against the `views` the mock lists; returns the requests sent. */
function writes(views: MockView[]): Sent[] {
  const sent: Sent[] = [];
  const pathOf = (url: string) => decodeURIComponent(new URL(url).pathname.replace(/^.*\/api\/data\/v9\.2\//, ''));
  const idOf = (path: string) => /\(([^)]*)\)/.exec(path)![1];
  server.use(
    http.post('*/api/data/v9.2/userqueries', async ({ request }) => {
      const body = (await request.json()) as Record<string, string>;
      sent.push({ method: 'POST', path: pathOf(request.url), body });
      views.push({ id: NEW_ID, name: body.name, entity: body.returnedtypecode, fetchxml: body.fetchxml, layoutxml: body.layoutxml });
      return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://localhost/api/data/v9.2/userqueries(${NEW_ID.toUpperCase()})` } });
    }),
    http.patch(/\/api\/data\/v9\.2\/userqueries\(/, async ({ request }) => {
      const body = (await request.json()) as Record<string, string>;
      const path = pathOf(request.url);
      sent.push({ method: 'PATCH', path, body });
      const view = views.find((v) => v.id === idOf(path))!;
      Object.assign(view, body.name ? { name: body.name } : {}, body.fetchxml ? { fetchxml: body.fetchxml } : {});
      return new HttpResponse(null, { status: 204 });
    }),
    http.delete(/\/api\/data\/v9\.2\/userqueries\(/, ({ request }) => {
      const path = pathOf(request.url);
      sent.push({ method: 'DELETE', path });
      views.splice(views.findIndex((v) => v.id === idOf(path)), 1);
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return sent;
}

const selector = (name: string) => screen.findByRole('button', { name: `Select a view: ${name}` });
const commands = async () => {
  await userEvent.click(await screen.findByRole('button', { name: 'View actions' }));
  return (await screen.findByRole('menu')).querySelectorAll('[role="menuitem"]');
};
const commandNames = async () => [...(await commands())].map((i) => i.textContent);
const command = async (name: string) => {
  await commands();
  await userEvent.click(screen.getByRole('menuitem', { name }));
};
const parse = (xml: unknown) => new DOMParser().parseFromString(String(xml), 'application/xml').documentElement;

describe('ViewCommands', () => {
  it('save_as_creates_a_userquery_with_the_current_sort_and_columns_and_selects_it', async () => {
    const views = [MINE];
    const { fetches } = openAccounts({ userQueries: views });
    const sent = writes(views);
    const grid = await screen.findByRole('grid', { name: 'Active Accounts' });
    await userEvent.click(within(within(grid).getAllByRole('columnheader')[1]).getByRole('button'));
    await waitFor(() => expect(fetches).toHaveLength(2));

    await command('Save as new view');
    const dialog = await screen.findByRole('dialog');
    await userEvent.type(within(dialog).getByRole('textbox', { name: /Name/ }), 'By VAT');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await selector('By VAT')).toBeInTheDocument();
    expect(sent).toHaveLength(1);
    const { body } = sent[0];
    expect(body).toMatchObject({ name: 'By VAT', returnedtypecode: 'account', querytype: 0, layoutxml: ACTIVE.layoutxml });
    const fetch = parse(body!.fetchxml);
    expect([...fetch.getElementsByTagName('order')].map((o) => o.getAttribute('attribute'))).toEqual(['op365_vatnumber']);
    for (const paging of ['page', 'count', 'paging-cookie', 'returntotalrecordcount']) expect(fetch.hasAttribute(paging)).toBe(false);
    expect(fetch.querySelector('condition[attribute="statecode"]')).not.toBeNull();
    expect(fetch.querySelector('link-entity[alias="pc"]')).not.toBeNull();
    await screen.findByRole('grid', { name: 'By VAT' });
  });

  it('save_view_patches_only_the_users_own_personal_view', async () => {
    const views = [{ ...MINE }];
    openAccounts({ userQueries: views, query: `&viewid=${MINE.id}&viewType=4230` });
    const sent = writes(views);
    await screen.findByRole('grid', { name: 'My big accounts' });
    await command('Save view');
    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toMatchObject({ method: 'PATCH', path: `userqueries(${MINE.id})`, body: { layoutxml: MINE.layoutxml } });
    expect(Object.keys(sent[0].body!).sort()).toEqual(['fetchxml', 'layoutxml']);
  });

  it('save_view_is_hidden_on_system_and_shared_views', async () => {
    const shared = { ...MINE, id: 'e0000000-0000-0000-0000-000000000002', name: 'Shared with me', ownerid: OTHER_USER };
    openAccounts({ userQueries: [MINE, shared] });
    await screen.findByRole('grid', { name: 'Active Accounts' });
    expect(await commandNames()).toEqual(['Save as new view']);
    await userEvent.keyboard('{Escape}');

    await userEvent.click(await selector('Active Accounts'));
    await userEvent.click(await screen.findByRole('menuitemradio', { name: 'Shared with me' }));
    await screen.findByRole('grid', { name: 'Shared with me' });
    expect(await commandNames()).toEqual(['Save as new view']);
    await userEvent.keyboard('{Escape}');

    await userEvent.click(await selector('Shared with me'));
    await userEvent.click(await screen.findByRole('menuitemradio', { name: 'My big accounts' }));
    await screen.findByRole('grid', { name: 'My big accounts' });
    expect(await commandNames()).toEqual(['Save as new view', 'Save view', 'Rename view', 'Delete view']);
  });

  it('rename_patches_name', async () => {
    const views = [{ ...MINE }];
    openAccounts({ userQueries: views, query: `&viewid=${MINE.id}&viewType=4230` });
    const sent = writes(views);
    await screen.findByRole('grid', { name: 'My big accounts' });
    await command('Rename view');
    const dialog = await screen.findByRole('dialog');
    const input = within(dialog).getByRole('textbox', { name: /Name/ });
    expect(input).toHaveValue('My big accounts');
    await userEvent.clear(input);
    await userEvent.type(input, 'Huge accounts');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await selector('Huge accounts')).toBeInTheDocument();
    expect(sent).toEqual([{ method: 'PATCH', path: `userqueries(${MINE.id})`, body: { name: 'Huge accounts' } }]);
  });

  it('delete_falls_back_to_the_default_view', async () => {
    const views = [{ ...MINE }];
    openAccounts({ userQueries: views, query: `&viewid=${MINE.id}&viewType=4230` });
    const sent = writes(views);
    await screen.findByRole('grid', { name: 'My big accounts' });
    await command('Delete view');
    const dialog = await screen.findByRole('alertdialog');
    expect(dialog).toHaveTextContent('Delete the view "My big accounts"?');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));
    expect(await selector('Active Accounts')).toBeInTheDocument();
    await screen.findByRole('grid', { name: 'Active Accounts' });
    expect(sent).toEqual([{ method: 'DELETE', path: `userqueries(${MINE.id})` }]);
    await userEvent.click(await selector('Active Accounts'));
    expect(screen.queryByRole('menuitemradio', { name: 'My big accounts' })).toBeNull();
  });

  it('a failed save shows the error in the dialog', async () => {
    openAccounts();
    server.use(http.post('*/api/data/v9.2/userqueries', () => HttpResponse.json({ error: { code: '0x80040220', message: 'No create privilege' } }, { status: 403 })));
    await screen.findByRole('grid', { name: 'Active Accounts' });
    await command('Save as new view');
    const dialog = await screen.findByRole('dialog');
    await userEvent.type(within(dialog).getByRole('textbox', { name: /Name/ }), 'X');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await within(dialog).findByText(/The view could not be saved: .*No create privilege/)).toBeInTheDocument();
  });
});
