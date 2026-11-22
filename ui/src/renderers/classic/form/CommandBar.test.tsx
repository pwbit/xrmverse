import { render, screen, waitFor } from '@testing-library/react';
import { en } from '../../../shell/i18n/en';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { AccessRight } from '../../../apps/record/access';
import type { RecordForm } from '../../../apps/record/useRecordForm';
import { attr, entity } from '../../../admin/tables/testAttrs';
import { CommandBar, type CommandBarProps } from './CommandBar';
import { contextOf } from './formTestSupport';

const ALL: AccessRight[] = ['Read', 'Write', 'Delete', 'Assign', 'Share'];
const STATE_ATTRS = [
  attr('statecode', {
    kind: 'choice',
    options: [
      { value: 0, label: 'Active', defaultStatus: 1 },
      { value: 1, label: 'Inactive', defaultStatus: 2 },
    ],
  }),
  attr('statuscode', {
    kind: 'choice',
    options: [
      { value: 1, label: 'Active', state: 0 },
      { value: 2, label: 'Inactive', state: 1 },
    ],
  }),
];

function props(
  over: {
    rights?: AccessRight[];
    canCreate?: boolean;
    creating?: boolean;
    ownership?: string;
    queue?: boolean;
    dirty?: boolean;
    busy?: boolean;
    readOnly?: boolean;
    /** The record's statecode; the table then has statecode and statuscode. */
    statecode?: number;
  } = {},
): CommandBarProps {
  const withState = over.statecode !== undefined;
  const ctx = contextOf([], over.creating ? undefined : withState ? { statecode: over.statecode } : {}, { rights: over.rights ?? ALL });
  ctx.entity = entity(withState ? STATE_ATTRS : [], { ownership: over.ownership ?? 'UserOwned', isValidForQueue: over.queue });
  ctx.canCreate = over.canCreate ?? true;
  const state = { dirty: over.dirty ?? false, busy: over.busy ?? false, readOnly: over.readOnly ?? false } as RecordForm;
  return {
    ctx,
    state,
    onNew: vi.fn(),
    onSave: vi.fn(),
    onSaveAndClose: vi.fn(),
    onDelete: vi.fn(),
    onRefresh: vi.fn(),
    onAssign: vi.fn(),
    onSetState: vi.fn().mockResolvedValue(true),
  };
}

// jsdom measures every width as 0, so the overflow menu's button may show up: it is not a command of the bar.
const names = () =>
  screen
    .getAllByRole('button')
    .filter((b) => b.getAttribute('aria-label') !== en['record.cmd.more'])
    .map((b) => b.textContent);

describe('CommandBar', () => {
  it('Add to queue shows on an existing record of a queue-enabled table only', () => {
    const { rerender } = render(<CommandBar {...props({ queue: true })} />);
    expect(names()).toEqual(['New', 'Save', 'Save & Close', 'Delete', 'Refresh', 'Assign', 'Add to queue', 'Share']);
    rerender(<CommandBar {...props({ queue: false })} />);
    expect(names()).not.toContain('Add to queue');
    rerender(<CommandBar {...props({ queue: true, creating: true })} />);
    expect(names()).not.toContain('Add to queue');
  });

  it('commands_in_order_and_hidden_without_rights', () => {
    const { rerender } = render(<CommandBar {...props()} />);
    expect(names()).toEqual(['New', 'Save', 'Save & Close', 'Delete', 'Refresh', 'Assign', 'Share']);
    expect(screen.getByRole('toolbar', { name: 'Commands' })).toBeInTheDocument();

    rerender(<CommandBar {...props({ canCreate: false })} />);
    expect(names()).not.toContain('New');
    rerender(<CommandBar {...props({ rights: ['Read', 'Write', 'Assign', 'Share'] })} />);
    expect(names()).not.toContain('Delete');
    rerender(<CommandBar {...props({ rights: ['Read', 'Write', 'Delete', 'Share'] })} />);
    expect(names()).not.toContain('Assign');
    rerender(<CommandBar {...props({ rights: ['Read', 'Write', 'Delete', 'Assign'] })} />);
    expect(names()).not.toContain('Share');
    // An organization-owned table has no Assign and no Share, whatever the rights say.
    rerender(<CommandBar {...props({ ownership: 'OrganizationOwned' })} />);
    expect(names()).toEqual(['New', 'Save', 'Save & Close', 'Delete', 'Refresh']);
  });

  it('close_is_the_last_command', async () => {
    const onClose = vi.fn();
    render(<CommandBar {...props()} onClose={onClose} />);
    const buttons = screen.getAllByRole('button');
    expect(buttons.at(-1)).toHaveAccessibleName('Close');
    await userEvent.click(buttons.at(-1)!);
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('the create form has only New, Save and Save & Close', () => {
    render(<CommandBar {...props({ creating: true })} />);
    expect(names()).toEqual(['New', 'Save', 'Save & Close']);
  });

  it('save_hidden_on_a_read_only_record_and_on_create_without_create_privilege', () => {
    const { rerender } = render(<CommandBar {...props({ readOnly: true, rights: ['Read', 'Delete', 'Assign', 'Share'] })} />);
    expect(names()).toEqual(['New', 'Delete', 'Refresh', 'Assign', 'Share']);
    rerender(<CommandBar {...props({ creating: true, canCreate: false })} />);
    expect(screen.queryAllByRole('button')).toEqual([]);
  });

  it('save_disabled_until_change', async () => {
    const clean = props();
    const { rerender } = render(<CommandBar {...clean} />);
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save & Close' })).toBeDisabled();
    const changed = props({ dirty: true });
    rerender(<CommandBar {...changed} />);
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(changed.onSave).toHaveBeenCalledOnce();
    await userEvent.click(screen.getByRole('button', { name: 'Save & Close' }));
    expect(changed.onSaveAndClose).toHaveBeenCalledOnce();
    rerender(<CommandBar {...props({ dirty: true, busy: true })} />);
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('delete asks before calling back', async () => {
    const p = props();
    render(<CommandBar {...p} />);
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
    expect(await screen.findByText('Delete this record?')).toBeInTheDocument();
    expect(p.onDelete).not.toHaveBeenCalled();
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel', hidden: true }));
    expect(p.onDelete).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
    await userEvent.click((await screen.findAllByRole('button', { name: 'Delete', hidden: true })).at(-1)!);
    expect(p.onDelete).toHaveBeenCalledOnce();
  });

  it('assign_on_changed_form_asks_first_and_cancel_keeps_edits', async () => {
    const changed = props({ dirty: true });
    render(<CommandBar {...changed} />);
    await userEvent.click(screen.getByRole('button', { name: 'Assign' }));
    expect(await screen.findByText('Discard your changes?')).toBeInTheDocument();
    expect(screen.queryByText('Assign record')).toBeNull();
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel', hidden: true }));
    await waitFor(() => expect(screen.queryByText('Discard your changes?')).toBeNull());
    expect(screen.queryByText('Assign record')).toBeNull();
    expect(changed.onAssign).not.toHaveBeenCalled();
  });

  it('refresh_with_changes_asks_first', async () => {
    const clean = props();
    const { rerender } = render(<CommandBar {...clean} />);
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(clean.onRefresh).toHaveBeenCalledOnce();

    const changed = props({ dirty: true });
    rerender(<CommandBar {...changed} />);
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByText('Discard your changes?')).toBeInTheDocument();
    expect(changed.onRefresh).not.toHaveBeenCalled();
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel', hidden: true }));
    expect(changed.onRefresh).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Discard', hidden: true }));
    expect(changed.onRefresh).toHaveBeenCalledOnce();
  });

  it('Deactivate_shown_on_active_record_with_write', async () => {
    const p = props({ statecode: 0 });
    render(<CommandBar {...p} />);
    expect(names()).toEqual(['New', 'Save', 'Save & Close', 'Delete', 'Deactivate', 'Refresh', 'Assign', 'Share']);
    await userEvent.click(screen.getByRole('button', { name: 'Deactivate' }));
    expect(await screen.findByText('Deactivate this record?')).toBeInTheDocument();
    await userEvent.click((await screen.findAllByRole('button', { name: 'Deactivate', hidden: true })).at(-1)!);
    expect(p.onSetState).toHaveBeenCalledWith(1, 2);
  });

  it('Activate_shown_on_inactive_record', () => {
    render(<CommandBar {...props({ statecode: 1, readOnly: true })} />);
    expect(names()).toEqual(['New', 'Delete', 'Activate', 'Refresh', 'Assign', 'Share']);
  });

  it('State_commands_hidden_without_statecode_without_write_and_on_create', () => {
    const { rerender } = render(<CommandBar {...props()} />);
    expect(names()).not.toContain('Deactivate');
    rerender(<CommandBar {...props({ statecode: 0, rights: ['Read', 'Delete'] })} />);
    expect(names()).not.toContain('Deactivate');
    rerender(<CommandBar {...props({ statecode: 0, creating: true })} />);
    expect(names()).toEqual(['New', 'Save', 'Save & Close']);
  });

  it('deactivate_on_changed_form_asks_to_discard_first', async () => {
    const p = props({ statecode: 0, dirty: true });
    render(<CommandBar {...p} />);
    await userEvent.click(screen.getByRole('button', { name: 'Deactivate' }));
    expect(await screen.findByText('Discard your changes?')).toBeInTheDocument();
    expect(screen.queryByText('Deactivate this record?')).toBeNull();
    await userEvent.click(await screen.findByRole('button', { name: 'Discard', hidden: true }));
    expect(await screen.findByText('Deactivate this record?')).toBeInTheDocument();
    expect(p.onSetState).not.toHaveBeenCalled();
  });

  it('command names are unchanged', () => {
    render(<CommandBar {...props({ statecode: 0 })} />);
    for (const name of ['New', 'Save', 'Save & Close', 'Delete', 'Deactivate', 'Refresh', 'Assign', 'Share']) {
      expect(screen.getByRole('button', { name }).querySelector('svg[aria-hidden="true"]')).not.toBeNull();
    }
  });

  it('Activate shows an icon too', () => {
    render(<CommandBar {...props({ statecode: 1 })} />);
    expect(screen.getByRole('button', { name: 'Activate' }).querySelector('svg[aria-hidden="true"]')).not.toBeNull();
  });

  it('custom_commands_follow_the_standard_ones_with_E6_icons', async () => {
    const run = vi.fn();
    const cmd = (id: string, label: string, icon?: string) => ({ id, buttonId: `${id}.B`, sequence: 1, label, icon, tooltip: `${label} tip`, displayRules: [], enableRules: [], actions: [] });
    const custom = [
      { command: cmd('a', 'Approve', 'Accept'), busy: false, run },
      { command: cmd('b', 'Plain', 'Unknown'), busy: false, run: vi.fn() },
      { command: cmd('c', 'Running', 'Refresh'), busy: true, run: vi.fn() },
    ];
    render(<CommandBar {...props({ ownership: 'OrganizationOwned' })} custom={custom} />);
    expect(names()).toEqual(['New', 'Save', 'Save & Close', 'Delete', 'Refresh', 'Approve', 'Plain', 'Running']);
    const approve = screen.getByRole('button', { name: 'Approve' });
    expect(approve.querySelector('svg')).not.toBeNull();
    expect(approve).toHaveAttribute('title', 'Approve tip');
    // Only the E6 names have an icon.
    expect(screen.getByRole('button', { name: 'Plain' }).querySelector('svg')).toBeNull();
    expect(screen.getByRole('button', { name: 'Running' })).toBeDisabled();
    await userEvent.click(approve);
    expect(run).toHaveBeenCalledTimes(1);
  });
});
