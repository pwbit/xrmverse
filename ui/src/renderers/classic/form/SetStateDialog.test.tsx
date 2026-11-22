import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { RecordStateMeta } from '../../../apps/record/recordStates';
import { SetStateDialog } from './SetStateDialog';

const STATES: RecordStateMeta[] = [
  { value: 0, label: 'Active', defaultStatus: 1, statuses: [{ value: 1, label: 'Active', state: 0 }] },
  {
    value: 1,
    label: 'Inactive',
    defaultStatus: 3,
    statuses: [
      { value: 2, label: 'Inactive', state: 1 },
      { value: 3, label: 'On hold', state: 1 },
    ],
  },
];

const optionsOf = (select: HTMLElement) => [...select.querySelectorAll('option')].map((o) => o.textContent);

describe('SetStateDialog', () => {
  it('deactivate_lists_the_reasons_of_the_inactive_state_with_the_default_selected', async () => {
    const onSetState = vi.fn().mockResolvedValue(true);
    const onClose = vi.fn();
    render(<SetStateDialog mode="deactivate" states={STATES} onSetState={onSetState} onClose={onClose} />);
    expect(screen.getByText('Deactivate this record?')).toBeInTheDocument();
    // A single inactive state: no state choice.
    expect(screen.queryByRole('combobox', { name: 'Status' })).toBeNull();
    const reason = screen.getByRole('combobox', { name: 'Status Reason' });
    expect(optionsOf(reason)).toEqual(['Inactive', 'On hold']);
    expect(reason).toHaveValue('3');
    await userEvent.selectOptions(reason, '2');
    await userEvent.click(screen.getByRole('button', { name: 'Deactivate' }));
    expect(onSetState).toHaveBeenCalledWith(1, 2);
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('activate_targets_state_zero', async () => {
    const onSetState = vi.fn().mockResolvedValue(true);
    render(<SetStateDialog mode="activate" states={STATES} onSetState={onSetState} onClose={vi.fn()} />);
    expect(screen.getByRole('combobox', { name: 'Status Reason' })).toHaveValue('1');
    await userEvent.click(screen.getByRole('button', { name: 'Activate' }));
    expect(onSetState).toHaveBeenCalledWith(0, 1);
  });

  it('several_inactive_states_ask_which_one', async () => {
    const onSetState = vi.fn().mockResolvedValue(true);
    const states: RecordStateMeta[] = [
      { value: 0, label: 'Open', statuses: [{ value: 1, label: 'Open', state: 0 }] },
      { value: 1, label: 'Completed', defaultStatus: 2, statuses: [{ value: 2, label: 'Completed', state: 1 }] },
      { value: 2, label: 'Canceled', defaultStatus: 3, statuses: [{ value: 3, label: 'Canceled', state: 2 }] },
    ];
    render(<SetStateDialog mode="deactivate" states={states} onSetState={onSetState} onClose={vi.fn()} />);
    const state = screen.getByRole('combobox', { name: 'Status' });
    expect(optionsOf(state)).toEqual(['Completed', 'Canceled']);
    await userEvent.selectOptions(state, '2');
    expect(screen.getByRole('combobox', { name: 'Status Reason' })).toHaveValue('3');
    await userEvent.click(screen.getByRole('button', { name: 'Deactivate' }));
    expect(onSetState).toHaveBeenCalledWith(2, 3);
  });

  it('a_failure_keeps_the_dialog_open_with_the_error', async () => {
    const onSetState = vi.fn().mockResolvedValue(false);
    const onClose = vi.fn();
    const { rerender } = render(<SetStateDialog mode="deactivate" states={STATES} onSetState={onSetState} onClose={onClose} />);
    await userEvent.click(screen.getByRole('button', { name: 'Deactivate' }));
    rerender(<SetStateDialog mode="deactivate" states={STATES} onSetState={onSetState} onClose={onClose} error="Denied" />);
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByText('Denied')).toBeInTheDocument();
  });
});
