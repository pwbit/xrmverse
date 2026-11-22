import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, signInAsAdministrator } from '../security/testSupport';
import { installMetadata, rawAttr, rawEntity } from '../tables/tableTestSupport';

const account = rawEntity(
  'account',
  'accounts',
  [rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }), rawAttr('name', 'String', { IsPrimaryName: true })],
  { PrimaryIdAttribute: 'accountid', PrimaryNameAttribute: 'name', display: 'Account' },
);

const view = (id: string, name: string, over: Record<string, unknown> = {}) => ({
  savedqueryid: id,
  name,
  querytype: 0,
  isdefault: false,
  ismanaged: false,
  fetchxml: '<fetch/>',
  layoutxml: '<grid/>',
  returnedtypecode: 'account',
  ...over,
});

let rows: ReturnType<typeof view>[];
let calls: string[];

beforeEach(() => {
  signInAsAdministrator();
  calls = [];
  rows = [
    view('11111111-1111-1111-1111-111111111111', 'Active Accounts', { isdefault: true }),
    view('22222222-2222-2222-2222-222222222222', 'All Accounts'),
    view('33333333-3333-3333-3333-333333333333', 'Quick Find', { querytype: 4 }),
    view('44444444-4444-4444-4444-444444444444', 'Odd', { querytype: 16, ismanaged: true }),
  ];
  installMetadata([account], []);
  server.use(
    http.get(`${API}/savedqueries`, () => HttpResponse.json({ value: rows })),
    http.patch(`${API}/savedqueries*`, ({ request }) => {
      calls.push(`PATCH ${decodeURIComponent(new URL(request.url).pathname)}`);
      return new HttpResponse(null, { status: 204 });
    }),
    http.post(`${API}/PublishXml`, () => {
      calls.push('PublishXml');
      return new HttpResponse(null, { status: 204 });
    }),
  );
});
afterEach(() => sessionStorage.clear());

async function openViews(user: ReturnType<typeof userEvent.setup>) {
  render(<App initialEntries={['/manage/tables/account']} />);
  await user.click(await screen.findByRole('tab', { name: en['table.tab.views'] }));
  return screen.findByRole('table', { name: en['table.tab.views'] });
}

const commands = () => screen.getByRole('toolbar', { name: `${en['table.tab.views']} commands` });
async function select(user: ReturnType<typeof userEvent.setup>, name: string) {
  await user.click(screen.getByRole('radio', { name: `Select ${name}` }));
}

describe('ViewsTab', () => {
  it('lists the views with link, type label, default tick and managed badge', async () => {
    const user = userEvent.setup();
    const table = await openViews(user);
    const link = within(table).getByRole('link', { name: 'All Accounts' });
    expect(link).toHaveAttribute('href', '/manage/tables/account/views/22222222-2222-2222-2222-222222222222');
    const active = within(table).getByRole('row', { name: /Active Accounts/ });
    expect(within(active).getByText(en['views.type.public'])).toBeInTheDocument();
    expect(within(active).getByText('✓')).toBeInTheDocument();
    expect(within(table).getByRole('row', { name: /Quick Find/ })).toHaveTextContent(en['views.type.quickFind']);
    const odd = within(table).getByRole('row', { name: /Odd/ });
    expect(odd).toHaveTextContent('Other (16)');
    expect(within(odd).getByText(en['views.managed'])).toBeInTheDocument();
    expect(within(table).getByRole('row', { name: /All Accounts/ })).not.toHaveTextContent(en['views.managed']);
  });

  it('links New view to views/new', async () => {
    const user = userEvent.setup();
    await openViews(user);
    expect(screen.getByRole('link', { name: en['views.new'] })).toHaveAttribute('href', '/manage/tables/account/views/new');
  });

  it('offers Set as default only on a selected non-default Public view', async () => {
    const user = userEvent.setup();
    await openViews(user);
    // Nothing selected: only New.
    expect(within(commands()).queryByRole('button', { name: en['views.delete'] })).not.toBeInTheDocument();
    // The default view offers neither command.
    await select(user, 'Active Accounts');
    expect(within(commands()).queryByRole('button', { name: en['views.setDefault'] })).not.toBeInTheDocument();
    expect(within(commands()).queryByRole('button', { name: en['views.delete'] })).not.toBeInTheDocument();
    await select(user, 'Quick Find');
    expect(within(commands()).queryByRole('button', { name: en['views.setDefault'] })).not.toBeInTheDocument();
    expect(within(commands()).getByRole('button', { name: en['views.delete'] })).toBeInTheDocument();
    await select(user, 'All Accounts');
    expect(within(commands()).getByRole('button', { name: en['views.setDefault'] })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Select All Accounts' })).toBeChecked();
  });

  it('sets a view as default and reloads', async () => {
    const user = userEvent.setup();
    await openViews(user);
    server.use(http.get(`${API}/savedqueries`, () => HttpResponse.json({ value: rows.map((r) => ({ ...r, isdefault: r.name === 'All Accounts' })) })));
    await select(user, 'All Accounts');
    await user.click(within(commands()).getByRole('button', { name: en['views.setDefault'] }));
    await waitFor(() => expect(calls).toContain('PublishXml'));
    expect(calls[0]).toBe('PATCH /api/data/v9.2/savedqueries(22222222-2222-2222-2222-222222222222)');
    await waitFor(() => expect(within(within(screen.getByRole('table', { name: en['table.tab.views'] })).getByRole('row', { name: /All Accounts/ })).getByText('✓')).toBeInTheDocument());
  });

  it('deletes after confirmation', async () => {
    const user = userEvent.setup();
    let deleted = false;
    server.use(
      http.delete(`${API}/savedqueries*`, ({ request }) => {
        deleted = decodeURIComponent(request.url).includes('savedqueries(22222222-2222-2222-2222-222222222222)');
        return new HttpResponse(null, { status: 204 });
      }),
    );
    await openViews(user);
    await select(user, 'All Accounts');
    await user.click(within(commands()).getByRole('button', { name: en['views.delete'] }));
    expect(deleted).toBe(false);
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: en['views.delete'] }));
    await waitFor(() => expect(deleted).toBe(true));
  });

  it('shows the server error when a delete is refused', async () => {
    const user = userEvent.setup();
    server.use(
      http.delete(`${API}/savedqueries*`, () =>
        HttpResponse.json({ error: { code: '0x80040203', message: 'Cannot delete a managed view' } }, { status: 400 }),
      ),
    );
    await openViews(user);
    await select(user, 'Odd');
    await user.click(within(commands()).getByRole('button', { name: en['views.delete'] }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: en['views.delete'] }));
    expect(await screen.findByText(/Cannot delete a managed view/)).toBeInTheDocument();
  });

  it('says there are no views', async () => {
    rows = [];
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/account']} />);
    await user.click(await screen.findByRole('tab', { name: en['table.tab.views'] }));
    expect(await screen.findByText(en['views.empty'])).toBeInTheDocument();
  });
});
