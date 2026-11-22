import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { createMemoryRouter, RouterProvider, useLocation, useNavigate } from 'react-router-dom';
import { useUnsavedGuard } from './useUnsavedGuard';

const future = {
  v7_relativeSplatPath: true,
  v7_fetcherPersist: true,
  v7_normalizeFormMethod: true,
  v7_partialHydration: true,
  v7_skipActionErrorRevalidation: true,
} as const;

function Probe({ dirty }: { dirty: boolean }) {
  const { blocker, navigateAnyway } = useUnsavedGuard(dirty);
  const navigate = useNavigate();
  const { pathname } = useLocation();
  return (
    <>
      <output data-testid="path">{pathname}</output>
      <output data-testid="blocker">{blocker.state}</output>
      <button onClick={() => navigate('/other')}>go</button>
      <button onClick={() => navigateAnyway(() => navigate('/forced'))}>force</button>
      <button onClick={() => blocker.state === 'blocked' && blocker.reset()}>stay</button>
      <button onClick={() => blocker.state === 'blocked' && blocker.proceed()}>leave</button>
    </>
  );
}

function renderGuard(dirty: boolean) {
  const router = createMemoryRouter([{ path: '*', element: <Probe dirty={dirty} /> }], { initialEntries: ['/form'], future });
  return render(<RouterProvider router={router} future={{ v7_startTransition: true }} />);
}

const beforeUnload = () => {
  const event = new Event('beforeunload', { cancelable: true });
  act(() => {
    window.dispatchEvent(event);
  });
  return event.defaultPrevented;
};

describe('useUnsavedGuard', () => {
  it('beforeunload_set_only_when_dirty', () => {
    const clean = renderGuard(false);
    expect(beforeUnload()).toBe(false);
    clean.unmount();
    const dirty = renderGuard(true);
    expect(beforeUnload()).toBe(true);
    dirty.unmount();
    // The listener is removed with the form.
    expect(beforeUnload()).toBe(false);
  });

  it('a clean form navigates freely', async () => {
    renderGuard(false);
    await userEvent.click(screen.getByRole('button', { name: 'go' }));
    expect(screen.getByTestId('path')).toHaveTextContent('/other');
  });

  it('a dirty form blocks, stay keeps the location and leave proceeds', async () => {
    renderGuard(true);
    await userEvent.click(screen.getByRole('button', { name: 'go' }));
    expect(screen.getByTestId('blocker')).toHaveTextContent('blocked');
    expect(screen.getByTestId('path')).toHaveTextContent('/form');
    await userEvent.click(screen.getByRole('button', { name: 'stay' }));
    expect(screen.getByTestId('blocker')).toHaveTextContent('unblocked');
    expect(screen.getByTestId('path')).toHaveTextContent('/form');
    await userEvent.click(screen.getByRole('button', { name: 'go' }));
    await userEvent.click(screen.getByRole('button', { name: 'leave' }));
    expect(screen.getByTestId('path')).toHaveTextContent('/other');
  });

  it('navigateAnyway lets the form leave itself after a save', async () => {
    renderGuard(true);
    await userEvent.click(screen.getByRole('button', { name: 'force' }));
    expect(screen.getByTestId('path')).toHaveTextContent('/forced');
    expect(screen.getByTestId('blocker')).toHaveTextContent('unblocked');
  });
});
