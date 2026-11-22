import { useEffect, useState } from 'react';
import { createBrowserRouter, createMemoryRouter, RouterProvider } from 'react-router-dom';
import { AppRoutes } from './AppRoutes';
import { AuthProvider } from './auth/AuthProvider';
import { GlobalStyles } from './GlobalStyles';
import { NotificationsProvider } from './notify/Notifications';
import { RouteError } from './RouteError';
import { RootTheme } from '../renderers/ThemeScope';

const future = {
  v7_relativeSplatPath: true,
  v7_fetcherPersist: true,
  v7_normalizeFormMethod: true,
  v7_partialHydration: true,
  v7_skipActionErrorRevalidation: true,
} as const;

const routes = [
  {
    path: '*',
    element: (
      <AuthProvider>
        <AppRoutes />
      </AuthProvider>
    ),
    errorElement: <RouteError />,
  },
];

// One browser router for the page's life (created on first use, so importing this module touches no `window`).
let browserRouter: ReturnType<typeof createBrowserRouter> | undefined;
const browser = () => (browserRouter ??= createBrowserRouter(routes, { future }));

/**
 * `initialEntries` selects an in-memory router (tests); the browser router is used otherwise. A data router, so that a form with
 * unsaved changes can block every navigation (links, `navigate`, the back button) through `useBlocker`.
 */
export function App({ initialEntries, onRouter }: { initialEntries?: (string | { pathname: string; search?: string; state?: unknown })[]; /** Tests: the in-memory router, to go Back. */ onRouter?: (router: { navigate(delta: number): unknown }) => void } = {}) {
  const [router] = useState(() => {
    const made = initialEntries ? createMemoryRouter(routes, { initialEntries, future }) : browser();
    onRouter?.(made);
    return made;
  });
  // An in-memory router belongs to this component; the browser's one outlives it.
  useEffect(() => (initialEntries ? () => router.dispose() : undefined), [router, initialEntries]);
  // The theme follows the signed-in user's colour scheme inside the apps, else the operating system's light or dark mode.
  return (
    <RootTheme>
      <GlobalStyles />
      <NotificationsProvider>
        <RouterProvider router={router} future={{ v7_startTransition: true }} />
      </NotificationsProvider>
    </RootTheme>
  );
}
