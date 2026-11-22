import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { onPath } from '../../../admin/security/testSupport';
import { mockApps } from '../../../apps/testSupport';
import { AuthProvider, useAuth } from '../../../shell/auth/AuthProvider';
import { NotificationsProvider } from '../../../shell/notify/Notifications';
import { server } from '../../../test/server';
import { ShareDialog } from './ShareDialog';

const Gate = ({ children }: { children: React.ReactNode }) => (useAuth().api ? <>{children}</> : null);
const RECORD = 'cccccccc-0000-0000-0000-000000000003';
const ALICE = 'aaaaaaaa-0000-0000-0000-0000000000a1';
const BOB = 'aaaaaaaa-0000-0000-0000-0000000000b2';
const TEAM = 'bbbbbbbb-0000-0000-0000-0000000000c3';

interface Posted {
  action: string;
  body: Record<string, unknown>;
}

function setup({ unreadable = false, ids, failOn }: { unreadable?: boolean; ids?: string[]; failOn?: string } = {}) {
  // The view's bulk runner (W9): each record on its own, each failure reported, the others still run.
  const outcomes: [string, string][] = [];
  const onBulk = async (op: (id: string) => Promise<void>) => {
    for (const record of ids ?? []) {
      try {
        await op(record);
        outcomes.push([record, 'done']);
      } catch {
        outcomes.push([record, 'failed']);
      }
    }
    return true;
  };
  mockApps();
  const posted: Posted[] = [];
  let reads = 0;
  const shares = [
    { AccessMask: 'ReadAccess, WriteAccess', Principal: { '@odata.type': '#Microsoft.Dynamics.CRM.systemuser', systemuserid: ALICE } },
    { AccessMask: 'ReadAccess', Principal: { '@odata.type': '#Microsoft.Dynamics.CRM.team', teamid: TEAM } },
  ];
  server.use(
    onPath('get', 'RetrieveSharedPrincipalsAndAccess(Target=@t)', () => {
      reads++;
      return HttpResponse.json({ PrincipalAccesses: shares });
    }),
    onPath('get', `systemusers(${ALICE})`, () => HttpResponse.json({ fullname: 'Alice Adams' })),
    onPath('get', `teams(${TEAM})`, () => (unreadable ? HttpResponse.json({ error: { message: 'denied' } }, { status: 403 }) : HttpResponse.json({ name: 'Sales team' }))),
    http.get('*/api/data/v9.2/systemusers', () => HttpResponse.json({ value: [{ systemuserid: BOB, fullname: 'Bob Brown' }] })),
    http.get('*/api/data/v9.2/teams', () => HttpResponse.json({ value: [] })),
    ...['GrantAccess', 'ModifyAccess', 'RevokeAccess'].map((action) =>
      onPath('post', action, async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        if (failOn && JSON.stringify(body).includes(failOn)) return HttpResponse.json({ error: { code: '0x80040220', message: 'denied' } }, { status: 403 });
        posted.push({ action, body });
        return new HttpResponse(null, { status: 204 });
      }),
    ),
  );
  render(
    <NotificationsProvider>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <AuthProvider>
          <Gate>
            {ids ? (
              <ShareDialog set="accounts" id={ids[0]} ids={ids} onBulk={onBulk} onClose={() => {}} />
            ) : (
              <ShareDialog set="accounts" id={RECORD} onClose={() => {}} />
            )}
          </Gate>
        </AuthProvider>
      </MemoryRouter>
    </NotificationsProvider>,
  );
  return { posted, reads: () => reads, outcomes };
}

describe('ShareDialog', () => {
  it('share_pane_lists_principals_by_name_with_one_column_per_right', async () => {
    setup();
    const alice = await screen.findByRole('row', { name: /Alice Adams/, hidden: true });
    expect(within(alice).getByRole('checkbox', { name: 'Read: Alice Adams', hidden: true })).toBeChecked();
    expect(within(alice).getByRole('checkbox', { name: 'Write: Alice Adams', hidden: true })).toBeChecked();
    expect(within(alice).getByRole('checkbox', { name: 'Delete: Alice Adams', hidden: true })).not.toBeChecked();
    expect(within(alice).getAllByRole('checkbox', { hidden: true })).toHaveLength(7);
    const team = screen.getByRole('row', { name: /Sales team/, hidden: true });
    expect(within(team).getByRole('checkbox', { name: 'Read: Sales team', hidden: true })).toBeChecked();
    for (const header of ['Read', 'Write', 'Delete', 'Append', 'Append To', 'Assign', 'Share'])
      expect(screen.getByRole('columnheader', { name: header, hidden: true })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Current shares (2)', hidden: true })).toHaveAttribute('aria-selected', 'true');
  });

  it('share_pane_shows_unknown_principal_not_its_id', async () => {
    setup({ unreadable: true });
    expect(await screen.findByRole('row', { name: /Unknown team/, hidden: true })).toBeInTheDocument();
    const row = screen.getByRole('row', { name: /Unknown team/, hidden: true });
    expect(row.textContent).not.toContain(TEAM);
  });

  it('share_pane_saves_edits_with_modify_and_removes_with_revoke', async () => {
    const { posted, reads } = setup();
    const alice = await screen.findByRole('row', { name: /Alice Adams/, hidden: true });
    const save = screen.getByRole('button', { name: /Save changes/, hidden: true });
    expect(save).toBeDisabled();
    await userEvent.click(within(alice).getByRole('checkbox', { name: 'Delete: Alice Adams', hidden: true }));
    // Unchecking every right of the team removes its share on save.
    await userEvent.click(within(screen.getByRole('row', { name: /Sales team/, hidden: true })).getByRole('checkbox', { name: 'Read: Sales team', hidden: true }));
    expect(screen.getByText('A row with no rights left is removed when you save.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Save changes (2)', hidden: true }));
    await waitFor(() => expect(posted).toHaveLength(2));
    expect(posted[0]).toEqual({
      action: 'ModifyAccess',
      body: {
        Target: { '@odata.id': `accounts(${RECORD})` },
        PrincipalAccess: { AccessMask: 'ReadAccess, WriteAccess, DeleteAccess', Principal: { '@odata.type': 'Microsoft.Dynamics.CRM.systemuser', systemuserid: ALICE } },
      },
    });
    expect(posted[1]).toEqual({
      action: 'RevokeAccess',
      body: { Target: { '@odata.id': `accounts(${RECORD})` }, Revokee: { '@odata.type': 'Microsoft.Dynamics.CRM.team', teamid: TEAM } },
    });
    await waitFor(() => expect(reads()).toBe(2));

    // Remove acts at once.
    await userEvent.click(within(screen.getByRole('row', { name: /Alice Adams/, hidden: true })).getByRole('button', { name: 'Remove the share with Alice Adams', hidden: true }));
    await waitFor(() => expect(posted.at(-1)).toEqual({
      action: 'RevokeAccess',
      body: { Target: { '@odata.id': `accounts(${RECORD})` }, Revokee: { '@odata.type': 'Microsoft.Dynamics.CRM.systemuser', systemuserid: ALICE } },
    }));
  });

  it('share_pane_add_tab_grants_the_picked_principals', async () => {
    const { posted } = setup();
    await screen.findByRole('row', { name: /Alice Adams/, hidden: true });
    await userEvent.click(screen.getByRole('tab', { name: 'Add users or teams', hidden: true }));
    const share = screen.getByRole('button', { name: 'Share', hidden: true });
    expect(share).toBeDisabled();
    await userEvent.click(await screen.findByRole('option', { name: 'Bob Brown', hidden: true }));
    expect(screen.getByText('Selected (1)')).toBeInTheDocument();
    // Read is ticked by default; the mask lists the rights in the pane's order, whatever order they were ticked in.
    for (const right of ['Append To', 'Assign']) await userEvent.click(screen.getByRole('checkbox', { name: right, hidden: true }));
    await userEvent.click(share);
    await waitFor(() => expect(posted).toHaveLength(1));
    expect(posted[0]).toEqual({
      action: 'GrantAccess',
      body: {
        Target: { '@odata.id': `accounts(${RECORD})` },
        PrincipalAccess: { AccessMask: 'ReadAccess, AppendToAccess, AssignAccess', Principal: { '@odata.type': 'Microsoft.Dynamics.CRM.systemuser', systemuserid: BOB } },
      },
    });
    // Back on the current shares.
    await waitFor(() => expect(screen.getByRole('tab', { name: /Current shares/, hidden: true })).toHaveAttribute('aria-selected', 'true'));
  });

  it('share_pane_with_several_records_shows_only_the_add_tab_and_grants_on_each', async () => {
    const OTHER = 'cccccccc-0000-0000-0000-000000000004';
    const { posted, reads } = setup({ ids: [RECORD, OTHER] });
    const share = await screen.findByRole('button', { name: 'Share', hidden: true });
    expect(screen.queryByRole('tab', { name: /Current shares/, hidden: true })).toBeNull();
    expect(reads()).toBe(0);
    await userEvent.click(await screen.findByRole('option', { name: 'Bob Brown', hidden: true }));
    await userEvent.click(share);
    await waitFor(() => expect(posted).toHaveLength(2));
    const bob = { AccessMask: 'ReadAccess', Principal: { '@odata.type': 'Microsoft.Dynamics.CRM.systemuser', systemuserid: BOB } };
    expect(posted).toEqual([
      { action: 'GrantAccess', body: { Target: { '@odata.id': `accounts(${RECORD})` }, PrincipalAccess: bob } },
      { action: 'GrantAccess', body: { Target: { '@odata.id': `accounts(${OTHER})` }, PrincipalAccess: bob } },
    ]);
  });

  it('share_with_several_records_reports_each_record_and_a_failure_does_not_stop_the_others', async () => {
    const OTHER = 'cccccccc-0000-0000-0000-000000000004';
    const { posted, outcomes } = setup({ ids: [RECORD, OTHER], failOn: RECORD });
    const share = await screen.findByRole('button', { name: 'Share', hidden: true });
    await userEvent.click(await screen.findByRole('option', { name: 'Bob Brown', hidden: true }));
    await userEvent.click(share);
    await waitFor(() => expect(outcomes).toHaveLength(2));
    expect(outcomes).toEqual([
      [RECORD, 'failed'],
      [OTHER, 'done'],
    ]);
    expect(posted.map((p) => (p.body.Target as Record<string, string>)['@odata.id'])).toEqual([`accounts(${OTHER})`]);
  });
});
