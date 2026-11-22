import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { StrictMode, useEffect } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/server';
import { en } from '../i18n/en';
import { AccessGate } from './AccessGate';
import { AuthProvider, useAuth } from './AuthProvider';

const session = vi.hoisted(() => ({
  account: null as null | { localAccountId: string; username: string; name?: string },
  returnPath: null as string | null,
  getToken: vi.fn(async () => 'entra-token'),
  getTokenSilently: vi.fn(async () => 'entra-token'),
  signIn: vi.fn(async () => {}),
  signOut: vi.fn(async () => {}),
  forget: vi.fn(async () => {}),
}));
const startEntra = vi.hoisted(() => vi.fn());
vi.mock('./entra', () => ({ startEntra }));

const entraConfig = {
  authMode: 'Entra',
  audience: 'api://xrmverse',
  authority: 'https://login.microsoftonline.com/t/v2.0',
  clientId: 'spa-client',
  scope: 'api://xrmverse/access',
  apiVersion: 'v9.2',
};

function mockConfig(config: Record<string, unknown> = entraConfig) {
  server.use(http.get('*/ui/config.json', () => HttpResponse.json(config)));
}

function Probe() {
  const { api, signOut } = useAuth();
  useEffect(() => {
    void api?.get('accounts').catch(() => {});
  }, [api]);
  return <button onClick={signOut}>probe-sign-out</button>;
}

function Where() {
  return <output aria-label="location">{useLocation().pathname}</output>;
}

function renderApp(strict = false) {
  const tree = (
    <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <AuthProvider>
        <AccessGate>
          <Probe />
        </AccessGate>
      </AuthProvider>
      <Where />
    </MemoryRouter>
  );
  return render(strict ? <StrictMode>{tree}</StrictMode> : tree);
}

beforeEach(() => {
  session.account = null;
  session.returnPath = null;
  startEntra.mockReset();
  startEntra.mockImplementation(async () => session);
  for (const fn of [session.getToken, session.getTokenSilently, session.signIn, session.signOut, session.forget]) fn.mockClear();
});

describe('AuthProvider in Entra mode', () => {
  it('Strict_mode_starts_MSAL_once', async () => {
    mockConfig();
    renderApp(true);
    expect(await screen.findByRole('button', { name: en['auth.signIn.entra'] })).toBeInTheDocument();
    expect(startEntra).toHaveBeenCalledTimes(1);
    expect(startEntra).toHaveBeenCalledWith({ clientId: 'spa-client', authority: entraConfig.authority, scope: entraConfig.scope });
  });

  it('Sign_in_button_calls_entra_signIn', async () => {
    mockConfig();
    renderApp();
    await userEvent.click(await screen.findByRole('button', { name: en['auth.signIn.entra'] }));
    expect(session.signIn).toHaveBeenCalledTimes(1);
  });

  it('Sign_out_calls_entra_signOut', async () => {
    mockConfig();
    session.account = { localAccountId: 'u1', username: 'u@x', name: 'U' };
    server.use(
      http.get('*/api/data/v9.2/*', ({ request }) => {
        const entity = new URL(request.url).pathname.split('/').pop();
        if (entity === 'WhoAmI') return HttpResponse.json({ UserId: 'aaaaaaaa-0000-0000-0000-000000000001' });
        if (entity === 'systemuserrolescollection') return HttpResponse.json({ value: [{ roleid: '627090ff-40a3-4053-8790-584edc5be201' }] });
        if (entity === 'roles')
          return HttpResponse.json({
            value: [{ roleid: '627090ff-40a3-4053-8790-584edc5be201', name: 'System Administrator', roletemplateid: '627090ff-40a3-4053-8790-584edc5be201' }],
          });
        return HttpResponse.json({ value: [] });
      }),
    );
    renderApp();
    await userEvent.click(await screen.findByRole('button', { name: 'probe-sign-out' }));
    // After the session cookie is cleared (DELETE /api/session).
    await waitFor(() => expect(session.signOut).toHaveBeenCalledTimes(1));
  });

  it('Back_from_the_redirect_returns_to_the_deep_link', async () => {
    mockConfig();
    session.account = { localAccountId: 'u1', username: 'u@x' };
    session.returnPath = '/manage/tables/account';
    const seen: string[] = [];
    server.use(
      http.get('*/api/data/v9.2/*', ({ request }) => {
        seen.push(new URL(request.url).pathname.split('/').pop()!);
        return new HttpResponse(null, { status: 401 });
      }),
    );
    renderApp();
    await waitFor(() => expect(screen.getByRole('status', { name: 'location' })).toHaveTextContent('/manage/tables/account'));
    // The access check (a 401 here) has been answered before the test ends.
    expect(await screen.findByRole('button', { name: en['auth.signIn.entra'] })).toBeInTheDocument();
    await waitFor(() => expect(seen.length).toBeGreaterThan(0));
  });

  it('Api_401_forgets_the_account_and_shows_sign_in_with_expiry_notice', async () => {
    mockConfig();
    session.account = { localAccountId: 'u1', username: 'u@x' };
    server.use(http.get('*/api/data/v9.2/*', () => new HttpResponse(null, { status: 401 })));
    renderApp();
    expect(await screen.findByRole('button', { name: en['auth.signIn.entra'] })).toBeInTheDocument();
    expect(screen.getByText(en['auth.sessionExpired'])).toBeInTheDocument();
    await waitFor(() => expect(session.forget).toHaveBeenCalledTimes(1));
  });

  it('Missing_authority_names_every_required_key', async () => {
    mockConfig({ ...entraConfig, authority: null });
    renderApp();
    const message = await screen.findByText(/Ui:Entra:ClientId/);
    expect(message).toHaveTextContent('Ui:Entra:Scope');
    expect(message).toHaveTextContent('Auth:TenantId');
    expect(startEntra).not.toHaveBeenCalled();
  });
});
