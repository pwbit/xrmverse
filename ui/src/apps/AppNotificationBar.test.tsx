import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createAppNotifications } from '../client-api/xrmverse/appNotifications';
import { en } from '../shell/i18n/en';
import { AppNotificationBar } from './AppNotificationBar';

describe('AppNotificationBar', () => {
  it('shows three and +N more', async () => {
    const store = createAppNotifications();
    render(<AppNotificationBar store={store} />);
    await act(async () => {
      for (let i = 1; i <= 5; i++) await store.add({ level: 4, message: `n${i}` });
    });
    expect(screen.getAllByRole('group')).toHaveLength(3);
    expect(screen.getByText('n5')).toBeInTheDocument();
    expect(screen.queryByText('n2')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: '+2 more' }));
    expect(screen.getAllByRole('group')).toHaveLength(5);
    expect(screen.queryByRole('button', { name: /more/ })).toBeNull();
  });

  it('folds again once the expanded list is back to three or fewer', async () => {
    const store = createAppNotifications();
    render(<AppNotificationBar store={store} />);
    const ids: string[] = [];
    await act(async () => {
      for (let i = 1; i <= 4; i++) ids.push(await store.add({ level: 4, message: `n${i}` }));
    });
    await userEvent.click(screen.getByRole('button', { name: '+1 more' }));
    expect(screen.getAllByRole('group')).toHaveLength(4);
    await act(async () => {
      await store.clear(ids[0]);
      await store.clear(ids[1]);
    });
    await act(async () => {
      for (let i = 5; i <= 7; i++) await store.add({ level: 4, message: `n${i}` });
    });
    expect(screen.getAllByRole('group')).toHaveLength(3);
    expect(screen.getByRole('button', { name: '+2 more' })).toBeInTheDocument();
  });

  it('close button only when showCloseButton', async () => {
    const store = createAppNotifications();
    render(<AppNotificationBar store={store} />);
    await act(async () => {
      await store.add({ level: 1, message: 'plain' });
      await store.add({ level: 3, message: 'closable', showCloseButton: true });
    });
    const buttons = screen.getAllByRole('button', { name: en['apps.notifications.close'] });
    expect(buttons).toHaveLength(1);
    await userEvent.click(buttons[0]);
    expect(screen.queryByText('closable')).toBeNull();
    expect(screen.getByText('plain')).toBeInTheDocument();
  });

  it('action handler is called and a throwing handler is logged, bar stays', async () => {
    const store = createAppNotifications();
    const err = vi.spyOn(console, 'error').mockImplementation(() => {});
    const ok = vi.fn();
    render(<AppNotificationBar store={store} />);
    await act(async () => {
      await store.add({ level: 2, message: 'boom', action: { actionLabel: 'Throw', eventHandler: () => { throw new Error('bad'); } } });
      await store.add({ level: 1, message: 'fine', action: { actionLabel: 'Go', eventHandler: ok } });
    });
    await userEvent.click(screen.getByRole('button', { name: 'Go' }));
    expect(ok).toHaveBeenCalledTimes(1);
    await userEvent.click(screen.getByRole('button', { name: 'Throw' }));
    expect(err).toHaveBeenCalled();
    expect(screen.getByText('boom')).toBeInTheDocument();
    err.mockRestore();
  });

  it('renders nothing when empty and clears on dispose', async () => {
    const store = createAppNotifications();
    const { container } = render(<AppNotificationBar store={store} />);
    expect(container).toBeEmptyDOMElement();
    await act(async () => { await store.add({ level: 1, message: 'x' }); });
    expect(screen.getByText('x')).toBeInTheDocument();
    act(() => store.dispose());
    expect(container).toBeEmptyDOMElement();
  });
});
