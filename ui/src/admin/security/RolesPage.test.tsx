import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, filterOf, isGateRequest, onPath, signInAsAdministrator } from './testSupport';

const BU1 = 'bbbbbbbb-0000-0000-0000-000000000001';
const BU2 = 'bbbbbbbb-0000-0000-0000-000000000002';
const ROLE = 'dddddddd-0000-0000-0000-000000000001';
const ROLE2 = 'dddddddd-0000-0000-0000-000000000002';
const P_READ = 'pppppppp-0000-0000-0000-000000000001';
const P_WRITE = 'pppppppp-0000-0000-0000-000000000002';
const P_ACT = 'pppppppp-0000-0000-0000-000000000003';

const privilege = (privilegeid: string, name: string, extra: object = {}) => ({
  privilegeid,
  name,
  canbebasic: true,
  canbelocal: true,
  canbedeep: true,
  canbeglobal: true,
  ...extra,
});

let roleMasks: Record<string, number>;

beforeEach(() => {
  signInAsAdministrator();
  roleMasks = { [P_READ]: 4 };
  server.use(
    http.get(`${API}/businessunits`, () =>
      HttpResponse.json({
        value: [
          { businessunitid: BU1, name: 'Acme' },
          { businessunitid: BU2, name: 'Sales' },
        ],
      }),
    ),
    http.get(`${API}/roles`, ({ request }) => {
      if (isGateRequest(request)) return undefined;
      return HttpResponse.json({
        value: [
          { roleid: ROLE, name: 'Project Manager', _businessunitid_value: BU1 },
          { roleid: ROLE2, name: 'Sales Rep', _businessunitid_value: BU2 },
        ],
      });
    }),
    http.get(`${API}/EntityDefinitions`, () =>
      HttpResponse.json({
        value: [
          { LogicalName: 'pb_project', SchemaName: 'pb_Project' },
          { LogicalName: 'team', SchemaName: 'Team' },
        ],
      }),
    ),
    http.get(`${API}/privileges`, ({ request }) => {
      const filter = filterOf(request) ?? '';
      const all = [
        privilege(P_READ, 'prvReadpb_Project'),
        privilege(P_WRITE, 'prvWritepb_Project', { canbeglobal: false }),
        privilege(P_ACT, 'prvActOnBehalfOfAnotherUser'),
      ];
      if (!filter.startsWith('name eq')) return HttpResponse.json({ value: all });
      return HttpResponse.json({ value: all.filter((p) => filter.includes(`'${p.name}'`)) });
    }),
    http.get(`${API}/roleprivilegescollection`, ({ request }) => {
      const filter = filterOf(request) ?? '';
      return HttpResponse.json({
        value: Object.entries(roleMasks)
          .filter(([id]) => filter.includes(id))
          .map(([privilegeid, privilegedepthmask]) => ({ privilegeid, privilegedepthmask })),
      });
    }),
  );
});
afterEach(() => sessionStorage.clear());

async function openPrivileges(user: ReturnType<typeof userEvent.setup>) {
  render(<App initialEntries={['/manage/security/roles']} />);
  await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Project Manager' }));
  await user.click(await screen.findByRole('button', { hidden: true, name: 'Privileges of Project Manager' }));
  await user.click(await screen.findByRole('option', { hidden: true, name: 'pb_project' }));
  // Resolves once the role's held depths were read (the radios are inert until then).
  return screen.findByRole('radio', { hidden: true, name: 'prvReadpb_Project: Deep', checked: true });
}

describe('RolesPage', () => {
  it('lists roles with their business unit and filters by business unit', async () => {
    const filters: (string | null)[] = [];
    server.use(
      http.get(`${API}/roles`, ({ request }) => {
        if (isGateRequest(request)) return undefined;
        filters.push(filterOf(request));
        return HttpResponse.json({ value: [{ roleid: ROLE2, name: 'Sales Rep', _businessunitid_value: BU2 }] });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/roles']} />);
    expect(await screen.findByText('Sales Rep')).toBeInTheDocument();
    await user.click(await screen.findByRole('option', { hidden: true, name: 'Sales' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: en['roles.filter.clear'] }));
    await screen.findByText('Sales Rep');
    expect(filters).toContain(`_businessunitid_value eq ${BU2}`);
    expect(filters.at(-1)).toBeNull();
  });

  it('Create_role_posts_business_unit_bind', async () => {
    let posted: unknown;
    server.use(
      http.post(`${API}/roles`, async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/roles']} />);
    await user.click(await screen.findByRole('button', { hidden: true, name: en['roles.create.open'] }));
    await user.type(await screen.findByLabelText(new RegExp(`^${en['roles.field.name']}`)), 'Auditor');
    const options = await screen.findAllByRole('option', { hidden: true, name: 'Sales' });
    await user.click(options.at(-1)!);
    await user.click(screen.getByRole('button', { hidden: true, name: en['roles.create.submit'] }));
    expect(await screen.findByText('Role "Auditor" created.')).toBeInTheDocument();
    expect(posted).toEqual({ name: 'Auditor', 'businessunitid@odata.bind': `/businessunits(${BU2})` });
  });

  it('Delete_role_confirms_then_deletes', async () => {
    let deleted: string | undefined;
    server.use(
      onPath('delete', `roles(${ROLE})`, ({ request }) => {
        deleted = new URL(request.url).pathname;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/roles']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Project Manager' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Delete Project Manager' }));
    expect(deleted).toBeUndefined();
    await user.click(await screen.findByRole('button', { hidden: true, name: en['roles.delete.confirm'] }));
    expect(await screen.findByText('Role "Project Manager" deleted.')).toBeInTheDocument();
    expect(deleted).toBe(`/api/data/v9.2/roles(${ROLE})`);
    expect(screen.queryByText('Project Manager')).toBeNull();
  });

  it('Delete_refused_by_server_shows_its_code_and_message', async () => {
    server.use(
      onPath('delete', `roles(${ROLE})`, () =>
        HttpResponse.json({ error: { code: '0x80040220', message: 'Standard roles cannot be deleted.' } }, { status: 403 }),
      ),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/roles']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Project Manager' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Delete Project Manager' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: en['roles.delete.confirm'] }));
    expect(await screen.findByText('0x80040220')).toBeInTheDocument();
    expect(screen.getByText('Standard roles cannot be deleted.')).toBeInTheDocument();
    expect(screen.getByText('Project Manager')).toBeInTheDocument();
  });

  it('Matrix_is_table_first_and_loads_exactly_that_tables_privileges', async () => {
    const filters: (string | null)[] = [];
    server.use(
      http.get(`${API}/privileges`, ({ request }) => {
        filters.push(filterOf(request));
        return HttpResponse.json({ value: [privilege(P_WRITE, 'prvWritepb_Project', { canbeglobal: false }), privilege(P_READ, 'prvReadpb_Project')] });
      }),
    );
    const user = userEvent.setup();
    await openPrivileges(user);
    expect(filters).toEqual([
      ['Create', 'Read', 'Write', 'Delete', 'Append', 'AppendTo', 'Assign', 'Share'].map((a) => `name eq 'prv${a}pb_Project'`).join(' or '),
    ]);
    expect(await screen.findByText('pb_Project')).toBeInTheDocument();
    // Rows follow the action order (Read before Write) whatever order the server returned.
    const names = (await screen.findAllByRole('radio', { hidden: true, name: /: Basic$/ })).map((r) => r.getAttribute('aria-label'));
    expect(names).toEqual(['prvReadpb_Project: Basic', 'prvWritepb_Project: Basic']);
    expect(await screen.findByRole('radio', { hidden: true, name: 'prvReadpb_Project: Deep', checked: true })).toBeInTheDocument();
    expect(await screen.findByRole('radio', { hidden: true, name: 'prvReadpb_Project: Basic', checked: false })).toBeInTheDocument();
    // A depth the privilege cannot take is not offered.
    expect(await screen.findByRole('radio', { hidden: true, name: 'prvWritepb_Project: Global' })).toBeDisabled();
  });

  it('Other_privileges_are_found_by_name', async () => {
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/roles']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Project Manager' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Privileges of Project Manager' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: en['privileges.mode.other'] }));
    expect(await screen.findByRole('radio', { hidden: true, name: 'prvActOnBehalfOfAnotherUser: Basic' })).toBeInTheDocument();
    expect(screen.getByText(en['privileges.group.other'])).toBeInTheDocument();
  });

  it('Failed_mask_read_offers_retry', async () => {
    let calls = 0;
    server.use(
      http.get(`${API}/roleprivilegescollection`, () => {
        calls++;
        return calls === 1
          ? HttpResponse.json({ error: { code: '0x80040216', message: 'boom' } }, { status: 500 })
          : HttpResponse.json({ value: [{ privilegeid: P_READ, privilegedepthmask: 4 }] });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/security/roles']} />);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'Select Project Manager' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: 'Privileges of Project Manager' }));
    await user.click(await screen.findByRole('option', { hidden: true, name: 'pb_project' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: en['common.retry'] }));
    expect(await screen.findByRole('radio', { hidden: true, name: 'prvReadpb_Project: Deep', checked: true })).toBeInTheDocument();
  });

  it('Privilege_change_calls_AddPrivilegesRole_with_depth', async () => {
    let call: { path: string; body: unknown } | undefined;
    server.use(
      onPath('post', `roles(${ROLE})/Microsoft.Dynamics.CRM.AddPrivilegesRole`, async ({ request }) => {
        call = { path: new URL(request.url).pathname, body: await request.json() };
        roleMasks[P_WRITE] = 2;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    await openPrivileges(user);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'prvWritepb_Project: Local' }));
    expect(await screen.findByRole('radio', { hidden: true, name: 'prvWritepb_Project: Local', checked: true })).toBeInTheDocument();
    expect(call).toEqual({
      path: `/api/data/v9.2/roles(${ROLE})/Microsoft.Dynamics.CRM.AddPrivilegesRole`,
      body: {
        Privileges: [
          { Depth: 'Local', PrivilegeId: P_WRITE, BusinessUnitId: BU1 },
        ],
      },
    });
  });

  it('Remove_privilege_calls_RemovePrivilegeRole', async () => {
    let call: unknown;
    server.use(
      onPath('post', `roles(${ROLE})/Microsoft.Dynamics.CRM.RemovePrivilegeRole`, async ({ request }) => {
        call = await request.json();
        delete roleMasks[P_READ];
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    await openPrivileges(user);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'prvReadpb_Project: None' }));
    await screen.findByText('prvReadpb_Project removed from the role.');
    expect(call).toEqual({ Privilege: { '@odata.type': 'Microsoft.Dynamics.CRM.privilege', privilegeid: P_READ } });
    expect(await screen.findByRole('radio', { hidden: true, name: 'prvReadpb_Project: Deep', checked: false })).toBeInTheDocument();
    expect(screen.getByRole('radio', { hidden: true, name: 'prvReadpb_Project: None', checked: true })).toBeInTheDocument();
  });

  it('Unsupported_privilege_call_shows_the_server_error_and_leaves_the_matrix_unchanged', async () => {
    server.use(
      onPath('post', `roles(${ROLE})/Microsoft.Dynamics.CRM.AddPrivilegesRole`, () =>
        HttpResponse.json({ error: { code: '0x80060888', message: 'Resource not found for the segment AddPrivilegesRole' } }, { status: 404 }),
      ),
    );
    const user = userEvent.setup();
    await openPrivileges(user);
    await user.click(await screen.findByRole('radio', { hidden: true, name: 'prvWritepb_Project: Local' }));
    expect(await screen.findByText('0x80060888')).toBeInTheDocument();
    expect(await screen.findByRole('radio', { hidden: true, name: 'prvWritepb_Project: Local', checked: false })).toBeInTheDocument();
  });
});
