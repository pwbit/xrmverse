import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, filterOf, isGateRequest, onPath, signInAsAdministrator } from './testSupport';

const BU1 = 'bbbbbbbb-0000-0000-0000-000000000001';
const BU2 = 'bbbbbbbb-0000-0000-0000-000000000002';
const USER = 'cccccccc-0000-0000-0000-000000000001';
const ROLE1 = 'dddddddd-0000-0000-0000-000000000001';
const ROLE2 = 'dddddddd-0000-0000-0000-000000000002';

const alice = (extra: object = {}) => ({
  systemuserid: USER,
  fullname: 'Alice Example',
  domainname: 'alice@example.com',
  azureactivedirectoryobjectid: '11111111-2222-3333-4444-555555555555',
  isdisabled: false,
  _businessunitid_value: BU1,
  ...extra,
});

beforeEach(() => {
  signInAsAdministrator();
  server.use(
    http.get(`${API}/businessunits`, ({ request }) => {
      const filter = filterOf(request) ?? '';
      const rows = [
        { businessunitid: BU1, name: 'Acme' },
        { businessunitid: BU2, name: 'Sales' },
      ];
      return HttpResponse.json({ value: filter.includes('businessunitid eq') ? rows.filter((r) => filter.includes(r.businessunitid)) : rows });
    }),
  );
});
afterEach(() => sessionStorage.clear());

describe('UsersPage', () => {
  it('lists users with their business unit name', async () => {
    server.use(http.get(`${API}/systemusers`, () => HttpResponse.json({ value: [alice()] })));
    render(<App initialEntries={['/manage/security/users']} />);
    const row = (await screen.findByText('Alice Example')).closest('tr') as HTMLElement;
    expect(await within(row).findByText('Acme')).toBeInTheDocument();
    // The name opens the user's record form, where its columns are edited.
    expect(within(row).getByRole('link', { name: 'Alice Example' }).getAttribute('href')).toMatch(/^\/manage\/tables\/systemuser\/records\/[0-9a-f-]+$/);
    expect(within(row).getByText(en['users.status.enabled'])).toBeInTheDocument();
  });

  it('Create_user_posts_bind_to_business_unit', async () => {
    let posted: unknown;
    server.use(
      http.get(`${API}/systemusers`, () => HttpResponse.json({ value: [] })),
      http.post(`${API}/systemusers`, async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `${new URL(request.url).origin}/api/data/v9.2/systemusers(${USER})` } });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/users']} />);
    await user.click(await screen.findByRole('button', { hidden: true, name: en['users.create.open'] }));
    await user.type(await screen.findByLabelText(new RegExp(`^${en['users.field.fullname']}`)), 'Bob Builder');
    await user.type(screen.getByLabelText(en['users.field.domainname']), 'bob@example.com');
    await user.type(screen.getByLabelText(new RegExp('azureactivedirectoryobjectid')), '99999999-2222-3333-4444-555555555555');
    await user.click(await screen.findByRole('option', { hidden: true, name: 'Sales' }));
    await user.click(screen.getByRole('button', { hidden: true, name: en['users.create.submit'] }));
    expect(await screen.findByText(en['users.created'].replace('{name}', 'Bob Builder'))).toBeInTheDocument();
    expect(posted).toEqual({
      fullname: 'Bob Builder',
      domainname: 'bob@example.com',
      azureactivedirectoryobjectid: '99999999-2222-3333-4444-555555555555',
      'businessunitid@odata.bind': `/businessunits(${BU2})`,
    });
  });

  it('Role_picker_lists_only_roles_of_users_bu', async () => {
    const pickerFilters: (string | null)[] = [];
    server.use(
      http.get(`${API}/systemusers`, () => HttpResponse.json({ value: [alice()] })),
      http.get(`${API}/systemuserrolescollection`, ({ request }) => (isGateRequest(request) ? undefined : HttpResponse.json({ value: [] }))),
      http.get(`${API}/roles`, ({ request }) => {
        if (isGateRequest(request)) return undefined;
        pickerFilters.push(filterOf(request));
        return HttpResponse.json({ value: [{ roleid: ROLE1, name: 'Acme Reader' }] });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/users']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Alice Example' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Roles of Alice Example' }));
    expect(await screen.findByRole('option', { hidden: true, name: 'Acme Reader' })).toBeInTheDocument();
    expect(pickerFilters).toEqual([`(_businessunitid_value eq ${BU1})`]);
  });

  it('Role_picker_does_not_offer_roles_already_assigned', async () => {
    const pickerFilters: (string | null)[] = [];
    server.use(
      http.get(`${API}/systemusers`, () => HttpResponse.json({ value: [alice()] })),
      http.get(`${API}/systemuserrolescollection`, ({ request }) =>
        isGateRequest(request) ? undefined : HttpResponse.json({ value: [{ roleid: ROLE2 }] }),
      ),
      http.get(`${API}/roles`, ({ request }) => {
        if (isGateRequest(request)) return undefined;
        const filter = filterOf(request);
        if (filter?.startsWith('roleid eq')) return HttpResponse.json({ value: [{ roleid: ROLE2, name: 'Acme Writer' }] });
        pickerFilters.push(filter);
        return HttpResponse.json({ value: [{ roleid: ROLE1, name: 'Acme Reader' }] });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/users']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Alice Example' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Roles of Alice Example' }));
    await screen.findByRole('option', { hidden: true, name: 'Acme Reader' });
    // The picker may query once before the assigned roles arrive; wait for the query that excludes them.
    await waitFor(() => expect(pickerFilters.at(-1)).toBe(`(_businessunitid_value eq ${BU1} and roleid ne ${ROLE2})`));
  });

  it('Assign_role_associates_and_refreshes', async () => {
    let associated: { path: string; body: { '@odata.id': string } } | undefined;
    let assigned: { roleid: string }[] = [];
    server.use(
      http.get(`${API}/systemusers`, () => HttpResponse.json({ value: [alice()] })),
      http.get(`${API}/systemuserrolescollection`, ({ request }) => (isGateRequest(request) ? undefined : HttpResponse.json({ value: assigned }))),
      http.get(`${API}/roles`, ({ request }) => {
        if (isGateRequest(request)) return undefined;
        const filter = filterOf(request) ?? '';
        if (filter.includes('roleid eq')) return HttpResponse.json({ value: [{ roleid: ROLE1, name: 'Acme Reader' }] });
        return HttpResponse.json({ value: [{ roleid: ROLE1, name: 'Acme Reader' }, { roleid: ROLE2, name: 'Acme Writer' }] });
      }),
      onPath('post', `systemusers(${USER})/systemuserroles_association/$ref`, async ({ request }) => {
        associated = { path: new URL(request.url).pathname, body: (await request.json()) as { '@odata.id': string } };
        assigned = [{ roleid: ROLE1 }];
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/users']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Alice Example' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Roles of Alice Example' }));
    expect(await screen.findByText(en['link.userRoles.empty'])).toBeInTheDocument();
    await user.click(await screen.findByRole('option', { hidden: true, name: 'Acme Reader' }));
    await user.click(screen.getByRole('button', { hidden: true, name: en['link.userRoles.add'] }));
    expect(await screen.findByText('Role "Acme Reader" assigned.')).toBeInTheDocument();
    expect(associated?.path).toBe(`/api/data/v9.2/systemusers(${USER})/systemuserroles_association/$ref`);
    expect(associated?.body['@odata.id']).toMatch(new RegExp(`/api/data/v9.2/roles\\(${ROLE1}\\)$`));
    // The list was re-read from the server and now shows the role in the assigned table.
    const table = await screen.findByRole('table', { hidden: true, name: en['link.userRoles.title'] });
    expect(await within(table).findByText('Acme Reader')).toBeInTheDocument();
  });

  it('Server_error_on_assign_is_shown_with_code', async () => {
    server.use(
      http.get(`${API}/systemusers`, () => HttpResponse.json({ value: [alice()] })),
      http.get(`${API}/systemuserrolescollection`, ({ request }) => (isGateRequest(request) ? undefined : HttpResponse.json({ value: [] }))),
      http.get(`${API}/roles`, ({ request }) =>
        isGateRequest(request) ? undefined : HttpResponse.json({ value: [{ roleid: ROLE1, name: 'Acme Reader' }] }),
      ),
      onPath('post', `systemusers(${USER})/systemuserroles_association/$ref`, () =>
        HttpResponse.json({ error: { code: '0x80040237', message: 'Duplicate record' } }, { status: 412 }),
      ),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/users']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Alice Example' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Roles of Alice Example' }));
    await user.click(await screen.findByRole('option', { hidden: true, name: 'Acme Reader' }));
    await user.click(screen.getByRole('button', { hidden: true, name: en['link.userRoles.add'] }));
    expect(await screen.findByText('0x80040237')).toBeInTheDocument();
    expect(screen.getByText('Duplicate record')).toBeInTheDocument();
  });

  it('Remove_role_confirms_then_disassociates', async () => {
    let deleted: string | undefined;
    server.use(
      http.get(`${API}/systemusers`, () => HttpResponse.json({ value: [alice()] })),
      http.get(`${API}/systemuserrolescollection`, ({ request }) =>
        isGateRequest(request) ? undefined : HttpResponse.json({ value: deleted ? [] : [{ roleid: ROLE1 }] }),
      ),
      http.get(`${API}/roles`, ({ request }) =>
        isGateRequest(request) ? undefined : HttpResponse.json({ value: [{ roleid: ROLE1, name: 'Acme Reader' }] }),
      ),
      onPath('delete', `systemusers(${USER})/systemuserroles_association(${ROLE1})/$ref`, ({ request }) => {
        deleted = new URL(request.url).pathname;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/users']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Alice Example' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Roles of Alice Example' }));
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Acme Reader' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: en['link.userRoles.remove'] }));
    expect(deleted).toBeUndefined();
    const dialogs = await screen.findAllByRole('dialog', { hidden: true });
    await user.click(await within(dialogs.at(-1)!).findByRole('button', { hidden: true, name: en['link.userRoles.remove'] }));
    expect(await screen.findByText(en['link.userRoles.empty'])).toBeInTheDocument();
    expect(deleted).toBe(`/api/data/v9.2/systemusers(${USER})/systemuserroles_association(${ROLE1})/$ref`);
  });

  it('Disable_user_confirms_then_patches_isdisabled', async () => {
    let patched: unknown;
    let disabled = false;
    server.use(
      http.get(`${API}/systemusers`, () => HttpResponse.json({ value: [alice()] })),
      onPath('get', `systemusers(${USER})`, () => HttpResponse.json(alice({ isdisabled: disabled }))),
      onPath('patch', `systemusers(${USER})`, async ({ request }) => {
        patched = await request.json();
        disabled = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/users']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Alice Example' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Disable Alice Example' }));
    expect(patched).toBeUndefined();
    await user.click(await screen.findByRole('button', { hidden: true, name: en['users.disable.confirm'] }));
    expect(await screen.findByText(en['users.status.disabled'])).toBeInTheDocument();
    expect(patched).toEqual({ isdisabled: true });
    expect(screen.getByRole('button', { hidden: true, name: 'Enable Alice Example' })).toBeInTheDocument();
  });

  it('Change_business_unit_patches_the_bind', async () => {
    let patched: unknown;
    server.use(
      http.get(`${API}/systemusers`, () => HttpResponse.json({ value: [alice()] })),
      onPath('get', `systemusers(${USER})`, () => HttpResponse.json(alice({ _businessunitid_value: BU2 }))),
      onPath('patch', `systemusers(${USER})`, async ({ request }) => {
        patched = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/users']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Alice Example' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Change business unit of Alice Example' }));
    await user.click(await screen.findByRole('option', { hidden: true, name: 'Sales' }));
    await user.click(screen.getByRole('button', { hidden: true, name: en['users.changeBu.submit'] }));
    expect(await screen.findByText('Alice Example now belongs to Sales.')).toBeInTheDocument();
    expect(patched).toEqual({ 'businessunitid@odata.bind': `/businessunits(${BU2})` });
  });
});
