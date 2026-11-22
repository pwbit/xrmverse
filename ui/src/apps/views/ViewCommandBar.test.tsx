import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { RecordStateMeta } from '../record/recordStates';
import type { XrmApi } from '../../shell/api/client';
import { t } from '../../shell/i18n/t';
import { ViewCommandBar, type ViewCommandBarProps } from './ViewCommandBar';

const STATES: RecordStateMeta[] = [
  { value: 0, label: 'Active', defaultStatus: 1, statuses: [{ value: 1, label: 'Active', state: 0 }] },
  { value: 1, label: 'Inactive', defaultStatus: 2, statuses: [{ value: 2, label: 'Inactive', state: 1 }] },
];

function setup(over: Partial<ViewCommandBarProps> = {}) {
  const api = { patch: vi.fn().mockResolvedValue(undefined), delete: vi.fn().mockResolvedValue(undefined) };
  const onRun = vi.fn(async (op: (id: string) => Promise<void>) => {
    for (const id of ['a', 'b']) await op(id);
  });
  const onNew = vi.fn();
  const props: ViewCommandBarProps = {
    api: api as unknown as XrmApi,
    set: 'accounts',
    onNew,
    selected: [
      { id: 'a', statecode: 0 },
      { id: 'b', statecode: 0 },
    ],
    states: STATES,
    owned: true,
    onRun,
    ...over,
  };
  render(<ViewCommandBar {...props} />);
  return { api, onRun, onNew };
}

const bar = () => screen.getByRole('toolbar', { name: 'View commands' });
// jsdom measures every width as 0: the overflow menu's button may show up, it is not a command.
const labels = () =>
  within(bar())
    .getAllByRole('button')
    .map((b) => b.textContent)
    .filter((l) => l);
const button = (name: string) => within(bar()).getByRole('button', { name });

describe('ViewCommandBar', () => {
  it('shows New, Activate, Deactivate and Assign in a toolbar, Share as an icon at its end', () => {
    const { onNew } = setup();
    expect(labels()).toEqual(['New', 'Activate', 'Deactivate', 'Assign']);
    expect(button('Share')).toBeInTheDocument();
    button('New').click();
    expect(onNew).toHaveBeenCalledOnce();
  });

  it('a disabled command carries its reason as tooltip', () => {
    setup({ selected: [{ id: 'a', statecode: 0 }, { id: 'b', statecode: 1 }] });
    for (const name of ['Activate', 'Deactivate']) {
      expect(button(name)).toHaveAttribute('aria-disabled', 'true');
      expect(button(name)).toHaveAttribute('title', t('apps.views.cmd.mixedStates'));
    }
    expect(button('Assign')).not.toHaveAttribute('aria-disabled', 'true');
  });

  it('nothing selected: the record commands are disabled', () => {
    setup({ selected: [] });
    for (const name of ['Activate', 'Deactivate', 'Assign', 'Share']) expect(button(name)).toHaveAttribute('aria-disabled', 'true');
  });

  it('a table without states has no Activate or Deactivate; one not user-owned no Assign or Share', () => {
    setup({ states: [], owned: false });
    expect(labels()).toEqual(['New']);
  });

  it('board and calendar: New only', () => {
    setup({ selected: undefined });
    expect(labels()).toEqual(['New']);
  });

  it('without the create privilege there is no New', () => {
    setup({ onNew: undefined });
    expect(labels()).not.toContain('New');
  });

  it('Deactivate patches each selected record through the runner and closes', async () => {
    const { api, onRun } = setup();
    await userEvent.click(button('Deactivate'));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('Deactivate 2 records?')).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Deactivate' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(onRun).toHaveBeenCalledOnce();
    expect(api.patch.mock.calls).toEqual([
      ['accounts(a)', { statecode: 1, statuscode: 2 }],
      ['accounts(b)', { statecode: 1, statuscode: 2 }],
    ]);
  });

  it('with the Delete privilege, Delete asks first and then deletes each selected record', async () => {
    const { api, onRun } = setup({ canDelete: true });
    expect(labels()).toEqual(['New', 'Delete', 'Activate', 'Deactivate', 'Assign']);
    await userEvent.click(button('Delete'));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(t('apps.views.records.deleteBody', { count: 2 }))).toBeInTheDocument();
    expect(api.delete).not.toHaveBeenCalled();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(onRun).toHaveBeenCalledOnce());
    expect(api.delete.mock.calls).toEqual([['accounts(a)'], ['accounts(b)']]);
  });

  it('without the Delete privilege there is no Delete; nothing selected disables it', () => {
    setup({ canDelete: true, selected: [] });
    expect(button('Delete')).toHaveAttribute('aria-disabled', 'true');
  });

  it('Refresh reads the records again', async () => {
    const onRefresh = vi.fn();
    setup({ onRefresh });
    expect(labels()).toContain('Refresh');
    await userEvent.click(button('Refresh'));
    expect(onRefresh).toHaveBeenCalledOnce();
  });

  it('Add to queue shows on a queue-enabled table, not on another', () => {
    setup({ queue: { logicalName: 'email', primaryId: 'activityid', isValidForQueue: true } });
    expect(labels()).toContain('Add to queue');
  });

  it('a table that is not queue-enabled has no queue command', () => {
    setup({ queue: { logicalName: 'account', primaryId: 'accountid', isValidForQueue: false } });
    expect(labels()).toEqual(['New', 'Activate', 'Deactivate', 'Assign']);
  });

  it('the queue item grid has Pick, Release, Route and Remove, and no Add to queue', () => {
    setup({ queue: { logicalName: 'queueitem', primaryId: 'queueitemid' }, owned: false });
    expect(labels()).toEqual(['New', 'Activate', 'Deactivate', 'Pick', 'Release', 'Route', 'Remove from queue']);
  });

  it('Release posts ReleaseToQueue for each selected row', async () => {
    const post = vi.fn().mockResolvedValue(undefined);
    const onRun = vi.fn(async (op: (id: string) => Promise<void>) => {
      for (const id of ['a', 'b']) await op(id);
    });
    render(
      <ViewCommandBar
        api={{ post } as unknown as XrmApi}
        set="queueitems"
        selected={[{ id: 'a', statecode: 0 }, { id: 'b', statecode: 0 }]}
        states={[]}
        owned={false}
        queue={{ logicalName: 'queueitem', primaryId: 'queueitemid' }}
        onRun={onRun}
      />,
    );
    await userEvent.click(button('Release'));
    await waitFor(() => expect(post).toHaveBeenCalledTimes(2));
    expect(post.mock.calls.map((c) => c[0])).toEqual(['queueitems(a)/Microsoft.Dynamics.CRM.ReleaseToQueue', 'queueitems(b)/Microsoft.Dynamics.CRM.ReleaseToQueue']);
  });

  it('Pick reads the signed-in user and posts PickFromQueue; a failed WhoAmI reaches the run as a failure', async () => {
    const post = vi.fn().mockResolvedValue(undefined);
    const get = vi.fn().mockResolvedValueOnce({ UserId: 'U1' }).mockRejectedValueOnce(new Error('WhoAmI failed'));
    const failures: string[] = [];
    const onRun = vi.fn(async (op: (id: string) => Promise<void>) => {
      try {
        await op('a');
      } catch (e) {
        failures.push((e as Error).message);
      }
    });
    const mount = () =>
      render(
        <ViewCommandBar
          api={{ post, get } as unknown as XrmApi}
          set="queueitems"
          selected={[{ id: 'a', statecode: 0 }]}
          states={[]}
          owned={false}
          queue={{ logicalName: 'queueitem', primaryId: 'queueitemid' }}
          onRun={onRun}
        />,
      );
    const first = mount();
    await userEvent.click(button('Pick'));
    await waitFor(() => expect(post).toHaveBeenCalledOnce());
    expect(post.mock.calls[0][1]).toMatchObject({ SystemUser: { systemuserid: 'U1' }, RemoveQueueItem: false });
    first.unmount();
    mount();
    await userEvent.click(button('Pick'));
    await waitFor(() => expect(failures).toEqual(['WhoAmI failed']));
    expect(post).toHaveBeenCalledOnce();
  });
});
