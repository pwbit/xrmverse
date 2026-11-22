import { render, screen } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { RouteError } from './RouteError';

function Boom(): never {
  throw new Error('Render exploded');
}

describe('RouteError', () => {
  it('a render error shows the app message, not the router default', () => {
    // React logs the caught error.
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const router = createMemoryRouter([{ path: '*', element: <Boom />, errorElement: <RouteError /> }], {
      future: { v7_relativeSplatPath: true, v7_fetcherPersist: true, v7_normalizeFormMethod: true, v7_partialHydration: true, v7_skipActionErrorRevalidation: true },
    });
    render(<RouterProvider router={router} future={{ v7_startTransition: true }} />);
    expect(screen.getByRole('heading', { name: 'Something went wrong' })).toBeInTheDocument();
    expect(screen.getByText('This page could not be shown: Render exploded')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reload' })).toBeInTheDocument();
    vi.restoreAllMocks();
  });
});
