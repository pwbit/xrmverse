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

const form = (id: string, name: string, over: Record<string, unknown> = {}) => ({
  systemformid: id,
  name,
  type: 2,
  isdefault: false,
  formactivationstate: 1,
  ismanaged: false,
  objecttypecode: 'account',
  ...over,
});

const D1 = '11111111-1111-1111-1111-111111111111';
const D2 = '22222222-2222-2222-2222-222222222222';

let rows: ReturnType<typeof form>[];
let calls: string[];

beforeEach(() => {
  signInAsAdministrator();
  calls = [];
  rows = [
    form(D1, 'Account', { isdefault: true }),
    form(D2, 'Information'),
    form('33333333-3333-3333-3333-333333333333', 'Quick View', { type: 6, formactivationstate: null }),
    form('44444444-4444-4444-4444-444444444444', 'Quick Create', { type: 7, formactivationstate: 0 }),
    form('55555555-5555-5555-5555-555555555555', 'Card', { type: 11 }),
    form('66666666-6666-6666-6666-666666666666', 'Dash', { type: 0 }),
    form('77777777-7777-7777-7777-777777777777', 'Odd', { type: 10, ismanaged: true }),
    form('88888888-8888-8888-8888-888888888888', 'Retired', { formactivationstate: 0 }),
  ];
  installMetadata([account], []);
  server.use(
    http.get(`${API}/systemforms`, () => HttpResponse.json({ value: rows })),
    http.patch(`${API}/systemforms*`, ({ request }) => {
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

async function openForms(user: ReturnType<typeof userEvent.setup>) {
  render(<App initialEntries={['/manage/tables/account']} />);
  await user.click(await screen.findByRole('tab', { name: en['table.tab.forms'] }));
  return screen.findByRole('table', { name: en['table.tab.forms'] });
}

const rowOf = (table: HTMLElement, name: RegExp) => within(table).getByRole('row', { name });
const commands = () => screen.getByRole('toolbar', { name: `${en['table.tab.forms']} commands` });
const command = (key: keyof typeof en) => within(commands()).queryByRole('button', { name: en[key] });
async function select(user: ReturnType<typeof userEvent.setup>, name: string) {
  await user.click(screen.getByRole('radio', { name: `Select ${name}` }));
}

describe('FormsTab', () => {
  it('lists the forms with the type labels, default, active and managed columns', async () => {
    const user = userEvent.setup();
    const table = await openForms(user);
    expect(within(table).getByRole('link', { name: 'Information' })).toHaveAttribute('href', `/manage/tables/account/forms/${D2}`);
    const main = rowOf(table, /^Select Account /);
    expect(main).toHaveTextContent(en['forms.type.main']);
    expect(within(main).getAllByText('✓')).toHaveLength(2); // default and active
    expect(rowOf(table, /Information/).textContent).toContain('✓'); // active, not default
    expect(within(rowOf(table, /Information/)).getAllByText('✓')).toHaveLength(1);
    const quickView = rowOf(table, /Quick View/);
    expect(quickView).toHaveTextContent(en['forms.type.quickView']);
    expect(within(quickView).getAllByText('✓')).toHaveLength(1); // null counts as active
    const quickCreate = rowOf(table, /Quick Create/);
    expect(quickCreate).toHaveTextContent(en['forms.type.quickCreate']);
    expect(within(quickCreate).queryByText('✓')).not.toBeInTheDocument();
    expect(rowOf(table, /Card/)).toHaveTextContent(en['forms.type.card']);
    expect(rowOf(table, /Dash/)).toHaveTextContent(en['forms.type.dashboard']);
    const odd = rowOf(table, /Odd/);
    expect(odd).toHaveTextContent('Other (10)');
    expect(within(odd).getByText(en['forms.managed'])).toBeInTheDocument();
    expect(rowOf(table, /Information/)).not.toHaveTextContent(en['forms.managed']);
  });

  it('New form offers Main, Quick view and Quick create', async () => {
    let router: { state: { location: { pathname: string; search: string } } };
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/account']} onRouter={(r) => (router = r as unknown as typeof router)} />);
    await user.click(await screen.findByRole('tab', { name: en['table.tab.forms'] }));
    await screen.findByRole('table', { name: en['table.tab.forms'] });
    const at = () => router.state.location.pathname + router.state.location.search;
    await user.click(screen.getByRole('button', { name: en['forms.new'] }));
    const items = await screen.findAllByRole('menuitem');
    expect(items.map((i) => i.textContent)).toEqual([en['forms.new.main'], en['forms.new.quickView'], en['forms.new.quickCreate']]);
    await user.click(screen.getByRole('menuitem', { name: en['forms.new.quickView'] }));
    await waitFor(() => expect(at()).toBe('/manage/tables/account/forms/new?type=6'));
  });

  it('New form > Main form navigates to forms/new', async () => {
    let router: { state: { location: { pathname: string; search: string } } };
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/account']} onRouter={(r) => (router = r as unknown as typeof router)} />);
    await user.click(await screen.findByRole('tab', { name: en['table.tab.forms'] }));
    await screen.findByRole('table', { name: en['table.tab.forms'] });
    await user.click(screen.getByRole('button', { name: en['forms.new'] }));
    await user.click(await screen.findByRole('menuitem', { name: en['forms.new.main'] }));
    await waitFor(() => expect(router.state.location.pathname + router.state.location.search).toBe('/manage/tables/account/forms/new'));
  });

  it('New form > Quick create form navigates to forms/new?type=7', async () => {
    let router: { state: { location: { pathname: string; search: string } } };
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/account']} onRouter={(r) => (router = r as unknown as typeof router)} />);
    await user.click(await screen.findByRole('tab', { name: en['table.tab.forms'] }));
    await screen.findByRole('table', { name: en['table.tab.forms'] });
    await user.click(screen.getByRole('button', { name: en['forms.new'] }));
    await user.click(await screen.findByRole('menuitem', { name: en['forms.new.quickCreate'] }));
    await waitFor(() => expect(router.state.location.pathname + router.state.location.search).toBe('/manage/tables/account/forms/new?type=7'));
  });

  it('opens a form from its row link', async () => {
    const user = userEvent.setup();
    const table = await openForms(user);
    await user.click(within(table).getByRole('link', { name: 'Information' }));
    expect(await screen.findByRole('button', { name: en['forms.designer.back'] })).toBeInTheDocument();
  });

  it('offers Set as default only on a selected non-default Main form', async () => {
    const user = userEvent.setup();
    await openForms(user);
    expect(command('forms.setDefault')).not.toBeInTheDocument();
    await select(user, 'Information');
    expect(command('forms.setDefault')).toBeInTheDocument();
    await select(user, 'Quick View');
    expect(command('forms.setDefault')).not.toBeInTheDocument();
  });

  it('does not offer Set as default on a deactivated Main form, only Activate', async () => {
    const user = userEvent.setup();
    await openForms(user);
    await select(user, 'Retired');
    expect(command('forms.activate')).toBeInTheDocument();
    expect(command('forms.setDefault')).not.toBeInTheDocument();
  });

  it('offers no command on the default form', async () => {
    const user = userEvent.setup();
    await openForms(user);
    await select(user, 'Account');
    expect(command('forms.setDefault')).not.toBeInTheDocument();
    expect(command('forms.deactivate')).not.toBeInTheDocument();
    expect(command('forms.delete')).not.toBeInTheDocument();
  });

  it('offers Activate on an inactive form and Deactivate on an active one', async () => {
    const user = userEvent.setup();
    await openForms(user);
    await select(user, 'Quick Create');
    expect(command('forms.activate')).toBeInTheDocument();
    expect(command('forms.deactivate')).not.toBeInTheDocument();
    await select(user, 'Information');
    expect(command('forms.deactivate')).toBeInTheDocument();
    expect(command('forms.activate')).not.toBeInTheDocument();
  });

  it('deactivates a form and publishes', async () => {
    const user = userEvent.setup();
    await openForms(user);
    await select(user, 'Information');
    await user.click(command('forms.deactivate')!);
    await waitFor(() => expect(calls).toContain('PublishXml'));
    expect(calls[0]).toBe(`PATCH /api/data/v9.2/systemforms(${D2})`);
  });

  it('sets a form as default and reloads', async () => {
    const user = userEvent.setup();
    await openForms(user);
    server.use(http.get(`${API}/systemforms`, () => HttpResponse.json({ value: rows.map((r) => ({ ...r, isdefault: r.name === 'Information' })) })));
    await select(user, 'Information');
    await user.click(command('forms.setDefault')!);
    await waitFor(() => expect(calls).toContain('PublishXml'));
    expect(calls[0]).toBe(`PATCH /api/data/v9.2/systemforms(${D2})`);
    await waitFor(() => expect(within(within(screen.getByRole('table', { name: en['table.tab.forms'] })).getByRole('row', { name: /Information/ })).getAllByText('✓')).toHaveLength(2));
  }, 15000);

  it('deletes after confirmation', async () => {
    const user = userEvent.setup();
    let deleted = false;
    server.use(
      http.delete(`${API}/systemforms*`, ({ request }) => {
        deleted = decodeURIComponent(request.url).includes(`systemforms(${D2})`);
        return new HttpResponse(null, { status: 204 });
      }),
    );
    await openForms(user);
    await select(user, 'Information');
    await user.click(command('forms.delete')!);
    expect(deleted).toBe(false);
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: en['forms.delete'] }));
    await waitFor(() => expect(deleted).toBe(true));
  });

  it('shows the server error verbatim when a delete is refused', async () => {
    const user = userEvent.setup();
    server.use(
      http.delete(`${API}/systemforms*`, () =>
        HttpResponse.json({ error: { code: '0x80040203', message: 'Cannot delete a managed form' } }, { status: 400 }),
      ),
    );
    await openForms(user);
    await select(user, 'Odd');
    await user.click(command('forms.delete')!);
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: en['forms.delete'] }));
    expect(await screen.findByText(/Cannot delete a managed form/)).toBeInTheDocument();
  });

  it('says there are no forms', async () => {
    rows = [];
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/account']} />);
    await user.click(await screen.findByRole('tab', { name: en['table.tab.forms'] }));
    expect(await screen.findByText(en['forms.empty'])).toBeInTheDocument();
  });
});
