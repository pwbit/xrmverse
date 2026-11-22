import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { lbl } from '../tables/tableTestSupport';
import { API, filterOf, onPath, signInAsAdministrator } from './testSupport';

const ADMIN_PROFILE = '572329c1-a042-4e22-be47-367c6374ea45';
const FINANCE = 'ffffffff-0000-0000-0000-000000000001';
const PERMISSION = 'ffffffff-0000-0000-0000-0000000000a1';
const USER = 'cccccccc-0000-0000-0000-000000000009';

/** The requests the page and its panels made, as "METHOD path?query" (decoded). */
let requests: string[] = [];

beforeEach(() => {
  requests = [];
  signInAsAdministrator();
  server.events.on('request:start', ({ request }) => {
    const url = new URL(request.url);
    if (url.pathname.startsWith('/api/data/v9.2/')) requests.push(`${request.method} ${decodeURIComponent(url.pathname.slice(15) + url.search)}`);
  });
  server.use(
    http.get(`${API}/fieldsecurityprofiles`, () =>
      HttpResponse.json({
        value: [
          { fieldsecurityprofileid: FINANCE, name: 'Finance', description: 'Budget readers' },
          { fieldsecurityprofileid: ADMIN_PROFILE, name: 'System Administrator', description: 'Full access' },
        ],
      }),
    ),
    http.get(`${API}/EntityDefinitions`, () =>
      HttpResponse.json({
        value: [
          {
            LogicalName: 'account',
            DisplayName: lbl('Account'),
            Attributes: [
              { LogicalName: 'creditlimit', DisplayName: lbl('Credit limit'), IsSecured: true },
              { LogicalName: 'revenue', DisplayName: lbl('Revenue'), IsSecured: true },
              { LogicalName: 'name', DisplayName: lbl('Name'), IsSecured: false },
            ],
          },
        ],
      }),
    ),
    http.get(`${API}/fieldpermissions`, () =>
      HttpResponse.json({
        value: [{ fieldpermissionid: PERMISSION, entityname: 'account', attributelogicalname: 'creditlimit', canread: 4, cancreate: 0, canupdate: 0 }],
      }),
    ),
  );
});
afterEach(() => {
  server.events.removeAllListeners();
  sessionStorage.clear();
});

/** Opens the permissions of a profile and waits for both of its reads (secured columns, permissions) to be shown. */
async function openPermissions(user: ReturnType<typeof userEvent.setup>, name: string) {
  await user.click(await screen.findByRole('radio', { hidden: true, name: `Select ${name}` }));
  await user.click(await screen.findByRole('button', { hidden: true, name: `Permissions of ${name}` }));
  return screen.findByRole('table', { hidden: true, name: `Permissions of ${name}` });
}

describe('FieldSecurityPage', () => {
  it('is in the navigation as "Field security"', async () => {
    render(<App initialEntries={['/manage/security/fieldprofiles']} />);
    expect(await screen.findByText('Finance')).toBeInTheDocument();
    expect(screen.getAllByText(en['nav.fieldSecurity']).length).toBeGreaterThan(0);
  });

  it('the System Administrator profile has no edit controls', async () => {
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/fieldprofiles']} />);
    expect(await screen.findByText('System Administrator')).toBeInTheDocument();
    expect(screen.getByText(en['fieldSecurity.readOnly'])).toBeInTheDocument();
    await user.click(screen.getByRole('radio', { hidden: true, name: 'Select System Administrator' }));
    expect(screen.getByRole('button', { hidden: true, name: 'Permissions of System Administrator' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { hidden: true, name: 'Delete System Administrator' })).toBeNull();
    expect(screen.queryByRole('button', { hidden: true, name: 'Users of System Administrator' })).toBeNull();
    expect(screen.queryByRole('button', { hidden: true, name: 'Teams of System Administrator' })).toBeNull();
    await user.click(screen.getByRole('radio', { hidden: true, name: 'Select Finance' }));
    expect(screen.getByRole('button', { hidden: true, name: 'Delete Finance' })).toBeInTheDocument();

    const matrix = await openPermissions(user, 'System Administrator');
    expect(await within(matrix).findByText('creditlimit')).toBeInTheDocument();
    // Display labels first, the logical name as the secondary line.
    expect(within(matrix).getByText('Credit limit')).toBeInTheDocument();
    expect(within(matrix).getAllByText('Account').length).toBeGreaterThan(0);
    expect(within(matrix).getAllByText('account').length).toBeGreaterThan(0);
    expect(within(matrix).queryAllByRole('checkbox', { hidden: true })).toHaveLength(0);
    expect(within(matrix).getAllByText(en['fieldSecurity.matrix.allowed'])).toHaveLength(6);
    expect(screen.getByText(en['fieldSecurity.permissions.adminNote'])).toBeInTheDocument();
    // Its permissions are never read: it has none and allows everything.
    expect(requests.some((r) => r.startsWith('GET fieldpermissions'))).toBe(false);
  });

  it('the matrix toggles call PATCH fieldpermissions(...) and create a missing permission', async () => {
    const patched: unknown[] = [];
    let posted: unknown;
    const reads = () => requests.filter((r) => r.startsWith('GET fieldpermissions')).length;
    server.use(
      onPath('patch', `fieldpermissions(${PERMISSION})`, async ({ request }) => {
        patched.push(await request.json());
        return new HttpResponse(null, { status: 204 });
      }),
      http.post(`${API}/fieldpermissions`, async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `${API}/fieldpermissions(ffffffff-0000-0000-0000-0000000000a2)` } });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/fieldprofiles']} />);
    const matrix = await openPermissions(user, 'Finance');
    const box = (name: string) => within(matrix).getByRole('checkbox', { hidden: true, name });
    await waitFor(() => expect(box('Read account.creditlimit')).toBeChecked());
    expect(box('Update account.creditlimit')).not.toBeChecked();
    expect(requests).toContain(
      `GET fieldpermissions?$select=fieldpermissionid,entityname,attributelogicalname,canread,cancreate,canupdate&$filter=_fieldsecurityprofileid_value eq ${FINANCE}`,
    );
    expect(within(matrix).queryByText('name')).toBeNull();

    /** Clicks a box, then waits for the write's reload of the permissions to end (the boxes are enabled again). */
    async function click(name: string, readsAfter: number) {
      await user.click(box(name));
      await waitFor(() => expect(reads()).toBe(readsAfter));
      await waitFor(() => expect(box(name)).toBeEnabled());
    }
    await click('Update account.creditlimit', 2);
    expect(patched).toEqual([{ canupdate: 4 }]);
    await click('Read account.creditlimit', 3);
    expect(patched).toEqual([{ canupdate: 4 }, { canread: 0 }]);
    await click('Create account.revenue', 4);
    expect(posted).toEqual({
      'fieldsecurityprofileid@odata.bind': `/fieldsecurityprofiles(${FINANCE})`,
      entityname: 'account',
      attributelogicalname: 'revenue',
      canread: 0,
      cancreate: 4,
      canupdate: 0,
    });
  });

  it('creates a profile', async () => {
    let posted: unknown;
    server.use(
      http.post(`${API}/fieldsecurityprofiles`, async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/fieldprofiles']} />);
    await user.click(await screen.findByRole('button', { hidden: true, name: en['fieldSecurity.create.open'] }));
    await user.type(await screen.findByLabelText(new RegExp(`^${en['fieldSecurity.field.name']}`)), 'Auditors');
    await user.type(screen.getByLabelText(en['fieldSecurity.field.description']), 'Read only');
    await user.click(screen.getByRole('button', { hidden: true, name: en['fieldSecurity.create.submit'] }));
    expect(await screen.findByText('Profile "Auditors" created.')).toBeInTheDocument();
    expect(posted).toEqual({ name: 'Auditors', description: 'Read only' });
    await waitFor(() => expect(requests.filter((r) => r.startsWith('GET fieldsecurityprofiles'))).toHaveLength(2));
  });

  it('deletes a profile after a confirmation', async () => {
    let deleted = false;
    server.use(
      onPath('delete', `fieldsecurityprofiles(${FINANCE})`, () => {
        deleted = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/fieldprofiles']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Finance' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Delete Finance' }));
    expect(deleted).toBe(false);
    const dialog = await screen.findByRole('dialog', { hidden: true });
    await user.click(within(dialog).getByRole('button', { hidden: true, name: en['fieldSecurity.delete.button'] }));
    expect(await screen.findByText('Profile "Finance" deleted.')).toBeInTheDocument();
    expect(deleted).toBe(true);
    await waitFor(() => expect(requests.filter((r) => r.startsWith('GET fieldsecurityprofiles'))).toHaveLength(2));
  });

  it('gives the profile to a user with LinkPanel', async () => {
    let associated: { path: string; id: string } | undefined;
    const pickerFilters: (string | null)[] = [];
    server.use(
      http.get(`${API}/systemuserprofilescollection`, () => HttpResponse.json({ value: [] })),
      http.get(`${API}/systemusers`, ({ request }) => {
        pickerFilters.push(filterOf(request));
        return HttpResponse.json({ value: [{ systemuserid: USER, fullname: 'Mia Member' }] });
      }),
      onPath('post', `fieldsecurityprofiles(${FINANCE})/systemuserprofiles_association/$ref`, async ({ request }) => {
        associated = { path: new URL(request.url).pathname, id: ((await request.json()) as Record<string, string>)['@odata.id'] };
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/fieldprofiles']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Finance' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Users of Finance' }));
    expect(await screen.findByText(en['link.profileUsers.empty'])).toBeInTheDocument();
    expect(requests).toContain(`GET systemuserprofilescollection?$select=systemuserid&$filter=fieldsecurityprofileid eq ${FINANCE}`);
    await user.click(await screen.findByRole('option', { hidden: true, name: 'Mia Member' }));
    await user.click(screen.getByRole('button', { hidden: true, name: en['link.profileUsers.add'] }));
    expect(await screen.findByText('Profile given to "Mia Member".')).toBeInTheDocument();
    expect(associated?.path).toBe(`/api/data/v9.2/fieldsecurityprofiles(${FINANCE})/systemuserprofiles_association/$ref`);
    expect(associated?.id).toMatch(new RegExp(`/api/data/v9.2/systemusers\\(${USER}\\)$`));
    await waitFor(() => expect(requests.filter((r) => r.startsWith('GET systemuserprofilescollection'))).toHaveLength(2));
    expect(pickerFilters[0]).toBeNull();
  });
});
