import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/server';
import { en } from '../i18n/en';
import { AccessGate } from './AccessGate';
import { AuthProvider } from './AuthProvider';

const msal = vi.hoisted(() => ({
  options: [] as { auth: Record<string, unknown> }[],
  loginRedirect: vi.fn(async (_request: unknown) => {}),
}));
vi.mock('@azure/msal-browser', () => ({
  InteractionRequiredAuthError: class extends Error {},
  ProtocolMode: { AAD: 'AAD', OIDC: 'OIDC' },
  PublicClientApplication: class {
    constructor(options: { auth: Record<string, unknown> }) {
      msal.options.push(options);
    }
    initialize = async () => {};
    handleRedirectPromise = async () => null;
    getActiveAccount = () => null;
    getAllAccounts = () => [];
    setActiveAccount = () => {};
    loginRedirect = msal.loginRedirect;
  },
}));

const localConfig = {
  authMode: 'Local',
  audience: 'https://xrm.example.test/',
  authority: 'https://xrm.example.test/adfs',
  clientId: 'xrmd-web',
  scope: 'https://xrm.example.test/.default',
  apiVersion: 'v9.2',
};

beforeEach(() => {
  msal.options.length = 0;
  msal.loginRedirect.mockClear();
  server.use(http.get('*/ui/config.json', () => HttpResponse.json(localConfig)));
});

function renderApp() {
  return render(
    <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <AuthProvider>
        <AccessGate>
          <p>admin</p>
        </AccessGate>
      </AuthProvider>
    </MemoryRouter>,
  );
}

describe('AuthProvider in Local mode', () => {
  it('Builds_MSAL_for_the_local_idp_in_OIDC_mode_without_a_Microsoft_authority', async () => {
    renderApp();
    await userEvent.click(await screen.findByRole('button', { name: en['auth.signIn.local'] }));

    expect(msal.options).toHaveLength(1);
    const auth = msal.options[0].auth;
    expect(auth).toMatchObject({
      clientId: 'xrmd-web',
      authority: 'https://xrm.example.test/adfs',
      knownAuthorities: ['xrm.example.test'],
      protocolMode: 'OIDC',
      redirectUri: 'https://xrm.example.test/',
    });
    expect(JSON.stringify(msal.options)).not.toContain('login.microsoftonline.com');
    expect(msal.loginRedirect).toHaveBeenCalledWith(expect.objectContaining({ scopes: ['https://xrm.example.test/.default'] }));
  });
});
