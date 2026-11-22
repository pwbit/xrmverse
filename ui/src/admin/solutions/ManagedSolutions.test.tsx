import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { signInAsAdministrator } from '../security/testSupport';
import { SOL1, SOL2, serveSolutions, solution } from './solutionTestSupport';

const HOLD = 'aaaaaaaa-1111-0000-0000-000000000009';
const NAME = (key: keyof typeof en, name: string) => en[key].replace('{name}', name);

beforeEach(() => signInAsAdministrator());
afterEach(() => sessionStorage.clear());

const rows = () => [
  solution({ solutionid: SOL1, uniquename: 'pack', friendlyname: 'Pack', ismanaged: true }),
  solution({ solutionid: HOLD, uniquename: 'pack_Upgrade', friendlyname: 'Pack Upgrade', ismanaged: true }),
  solution({ solutionid: SOL2, uniquename: 'solo', friendlyname: 'Solo', ismanaged: true }),
];

const selectRow = (user: ReturnType<typeof userEvent.setup>, name: string) =>
  user.click(screen.getByRole('radio', { hidden: true, name: `Select ${name}` }));

async function openPage() {
  render(<App initialEntries={['/manage/solutions']} />);
  await screen.findAllByText('Acme Publisher');
}

const plan = (dataLoss: object[]) => ({
  deleted: [{ type: 1, name: 'acme_widget' }],
  restored: ['acme_gadget'],
  kept: [{ type: 2, name: 'acme_size', reason: 'held by another solution' }],
  dataLoss,
});

/** Serves a POST action, recording each body. */
function servePlan(action: 'uninstall' | 'promote', name: string, dataLoss: object[]) {
  const bodies: Record<string, unknown>[] = [];
  server.use(
    http.post(`*/admin/solutions/${name}/${action}`, async ({ request }) => {
      bodies.push((await request.json()) as Record<string, unknown>);
      return HttpResponse.json(plan(dataLoss));
    }),
  );
  return bodies;
}

describe('Managed solutions on the Solutions page', () => {
  it('shows managed and holding badges, and Apply upgrade only on a base with a holding', async () => {
    serveSolutions(rows());
    const user = userEvent.setup();
    await openPage();
    const base = (await screen.findByText('Pack')).closest('tr') as HTMLElement;
    const holding = screen.getByText('Pack Upgrade').closest('tr') as HTMLElement;
    expect(within(holding).getByText(en['solutions.badge.holding'])).toBeInTheDocument();
    expect(within(base).queryByText(en['solutions.badge.holding'])).toBeNull();
    expect(within(base).getByText(en['solutions.type.managed'])).toBeInTheDocument();
    await selectRow(user, 'Pack');
    expect(screen.getByRole('button', { name: NAME('solutions.promote.open', 'Pack') })).toBeInTheDocument();
    await selectRow(user, 'Solo');
    expect(screen.queryByRole('button', { name: /Apply the staged upgrade/ })).toBeNull();
    expect(screen.getByRole('button', { name: NAME('solutions.uninstall.open', 'Solo') })).toBeInTheDocument();
    // A holding solution has no commands.
    await selectRow(user, 'Pack Upgrade');
    expect(screen.queryByRole('button', { name: /Pack Upgrade/ })).toBeNull();
  });

  it('Uninstall_dialog_requires_data_loss_box_then_posts_dropData_true', async () => {
    serveSolutions(rows());
    const bodies = servePlan('uninstall', 'solo', [{ table: 'acme_widget', column: null, rows: 7 }]);
    const user = userEvent.setup();
    await openPage();
    await selectRow(user, 'Solo');
    await user.click(await screen.findByRole('button', { name: NAME('solutions.uninstall.open', 'Solo') }));
    expect(await screen.findByText('acme_widget (Table)')).toBeInTheDocument();
    await waitFor(() => expect(bodies).toEqual([{ dropData: false, dryRun: true }]));
    expect(screen.getByText('acme_gadget')).toBeInTheDocument();
    expect(screen.getByText(/acme_size \(Column\): held by another solution/)).toBeInTheDocument();
    expect(screen.getByText('Table acme_widget: 7 row(s)')).toBeInTheDocument();
    const confirm = screen.getByRole('button', { name: en['solutions.uninstall.confirm'] });
    expect(confirm).toBeDisabled();
    await user.click(screen.getByRole('checkbox', { name: en['solutions.plan.lossAck'] }));
    expect(confirm).toBeEnabled();
    await user.click(confirm);
    await waitFor(() => expect(bodies).toHaveLength(2));
    expect(bodies[1]).toEqual({
      dropData: true,
      dryRun: false,
      expectedDataLoss: [{ table: 'acme_widget', column: null }],
    });
  });

  it('Apply_upgrade_without_data_loss_posts_dropData_false', async () => {
    serveSolutions(rows());
    const bodies = servePlan('promote', 'pack', []);
    const user = userEvent.setup();
    await openPage();
    await selectRow(user, 'Pack');
    await user.click(await screen.findByRole('button', { name: NAME('solutions.promote.open', 'Pack') }));
    await screen.findByText('acme_widget (Table)');
    const confirm = await screen.findByRole('button', { name: en['solutions.promote.confirm'] });
    await waitFor(() => expect(confirm).toBeEnabled());
    await user.click(confirm);
    await waitFor(() => expect(bodies).toHaveLength(2));
    expect(bodies[1]).toEqual({ dropData: false, dryRun: false, expectedDataLoss: [] });
  });

  it('Confirm_answered_409_reloads_the_plan_and_needs_a_new_acknowledgement', async () => {
    serveSolutions(rows());
    const bodies: Record<string, unknown>[] = [];
    let dataLoss: object[] = [];
    server.use(
      http.post('*/admin/solutions/solo/uninstall', async ({ request }) => {
        const body = (await request.json()) as Record<string, unknown>;
        bodies.push(body);
        if (body.dryRun) return HttpResponse.json(plan(dataLoss));
        // A row was written after the dry run: the plan under the lock drops a table the user did not see.
        dataLoss = [{ table: 'acme_widget', column: null, rows: 1 }];
        return HttpResponse.json(
          { error: { code: '0x80040203', message: 'The uninstall would now drop data its plan did not show.' } },
          { status: 409 },
        );
      }),
    );
    const user = userEvent.setup();
    await openPage();
    await selectRow(user, 'Solo');
    await user.click(await screen.findByRole('button', { name: NAME('solutions.uninstall.open', 'Solo') }));
    await screen.findByText('acme_widget (Table)');
    const confirm = () => screen.findByRole('button', { hidden: true, name: en['solutions.uninstall.confirm'] });
    await waitFor(async () => expect(await confirm()).toBeEnabled());
    await user.click(await confirm());
    await waitFor(() => expect(bodies).toHaveLength(3));
    expect(bodies[1]).toEqual({ dropData: false, dryRun: false, expectedDataLoss: [] });
    expect(bodies[2]).toEqual({ dropData: false, dryRun: true });
    expect(await screen.findByText('Table acme_widget: 1 row(s)')).toBeInTheDocument();
    expect(await confirm()).toBeDisabled();
    expect(await screen.findByRole('checkbox', { hidden: true, name: en['solutions.plan.lossAck'] })).not.toBeChecked();
  });

  it('Layers_panel_lists_layers_top_first', async () => {
    serveSolutions(rows());
    let asked = 0;
    server.use(
      http.get('*/admin/solutions/solo/layers', () => {
        asked++;
        return HttpResponse.json([
          { componentType: 1, objectId: 'x', solution: 'System', kind: 'system', order: 0, version: null },
          { componentType: 1, objectId: 'x', solution: 'solo', kind: 'managed', order: 5, version: '1.0.0.0' },
          { componentType: 1, objectId: 'x', solution: 'Active', kind: 'active', order: 9, version: null },
        ]);
      }),
    );
    const user = userEvent.setup();
    await openPage();
    await selectRow(user, 'Solo');
    await user.click(await screen.findByRole('button', { name: NAME('solutions.layers.open', 'Solo') }));
    const table = await screen.findByRole('table', { name: en['solutions.layers.title'] });
    await waitFor(() => expect(asked).toBe(1));
    const body = within(table).getAllByRole('row').slice(1);
    expect(body.map((r) => within(r).getAllByRole('cell')[1].textContent)).toEqual(['Active', 'solo', 'System']);
  });
});
