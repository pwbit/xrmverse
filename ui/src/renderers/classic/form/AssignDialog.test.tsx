import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { mockApps } from '../../../apps/testSupport';
import { AuthProvider, useAuth } from '../../../shell/auth/AuthProvider';
import { NotificationsProvider } from '../../../shell/notify/Notifications';
import { server } from '../../../test/server';
import { AssignDialog, type AssignDialogProps } from './AssignDialog';

const Gate = ({ children }: { children: React.ReactNode }) => (useAuth().api ? <>{children}</> : null);

function renderDialog(props: Partial<AssignDialogProps> = {}) {
  mockApps();
  server.use(
    http.get('*/api/data/v9.2/systemusers', () => HttpResponse.json({ value: [{ systemuserid: 'u1', fullname: 'Alice Adams' }] })),
    http.get('*/api/data/v9.2/teams', () => HttpResponse.json({ value: [{ teamid: 't1', name: 'Sales team' }] })),
  );
  const onAssign = vi.fn().mockResolvedValue(true);
  const onClose = vi.fn();
  render(
    <NotificationsProvider>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <AuthProvider>
          <Gate>
            <AssignDialog onAssign={onAssign} onClose={onClose} {...props} />
          </Gate>
        </AuthProvider>
      </MemoryRouter>
    </NotificationsProvider>,
  );
  return { onAssign, onClose };
}

describe('AssignDialog', () => {
  it('picks a user and reports it', async () => {
    const { onAssign, onClose } = renderDialog();
    const assign = await screen.findByRole('button', { name: 'Assign', hidden: true });
    expect(assign).toBeDisabled();
    await userEvent.click(await screen.findByRole('option', { name: 'Alice Adams', hidden: true }));
    await userEvent.click(assign);
    expect(onAssign).toHaveBeenCalledWith({ id: 'u1', name: 'Alice Adams', target: 'systemuser', set: 'systemusers' });
    expect(onClose).toHaveBeenCalled();
  });

  it('picks a team', async () => {
    const { onAssign } = renderDialog();
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Assign to', hidden: true }), 'team');
    await userEvent.click(await screen.findByRole('option', { name: 'Sales team', hidden: true }));
    await userEvent.click(screen.getByRole('button', { name: 'Assign', hidden: true }));
    expect(onAssign).toHaveBeenCalledWith({ id: 't1', name: 'Sales team', target: 'team', set: 'teams' });
  });

  it('stays_open_and_shows_the_error_when_assign_fails', async () => {
    const { onClose } = renderDialog({ onAssign: vi.fn().mockRejectedValue(new Error('Assign refused.')) });
    await userEvent.click(await screen.findByRole('option', { name: 'Alice Adams', hidden: true }));
    await userEvent.click(screen.getByRole('button', { name: 'Assign', hidden: true }));
    expect(await screen.findByText('Assign refused.')).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Assign', hidden: true })).toBeEnabled();
  });

  it('stays_open_and_shows_the_form_error_when_assign_returns_false', async () => {
    const { onClose } = renderDialog({ onAssign: vi.fn().mockResolvedValue(false), error: 'The owner cannot be changed.' });
    // The form's message is shown only once an assign has been tried.
    await userEvent.click(await screen.findByRole('option', { name: 'Alice Adams', hidden: true }));
    expect(screen.queryByText('The owner cannot be changed.')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Assign', hidden: true }));
    expect(await screen.findByText('The owner cannot be changed.')).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
  });
});
