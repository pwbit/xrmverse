import { beforeEach, describe, expect, it, vi } from 'vitest';

const msal = vi.hoisted(() => ({
  redirected: null as null | { account: { localAccountId: string; username: string }; state?: string },
  loginRedirect: vi.fn(async (_request: unknown) => {}),
  acquireTokenRedirect: vi.fn(async (_request: unknown) => {}),
  active: null as null | { localAccountId: string; username: string },
  options: [] as unknown[],
}));
const InteractionRequired = vi.hoisted(() => class InteractionRequiredAuthError extends Error {});
vi.mock('@azure/msal-browser', () => ({
  InteractionRequiredAuthError: InteractionRequired,
  ProtocolMode: { AAD: 'AAD', OIDC: 'OIDC' },
  PublicClientApplication: class {
    constructor(options: unknown) {
      msal.options.push(options);
    }
    initialize = async () => {};
    handleRedirectPromise = async () => msal.redirected;
    getActiveAccount = () => msal.active;
    acquireTokenSilent = async () => {
      throw new InteractionRequired('interaction_required');
    };
    acquireTokenRedirect = msal.acquireTokenRedirect;
    getAllAccounts = () => [];
    setActiveAccount = () => {};
    loginRedirect = msal.loginRedirect;
  },
}));

import { safeReturnPath, startEntra } from './entra';

const config = { clientId: 'c', authority: 'https://login.microsoftonline.com/t/v2.0', scope: 'api://x/access' };

beforeEach(() => {
  msal.redirected = null;
  msal.options.length = 0;
  msal.loginRedirect.mockClear();
  msal.acquireTokenRedirect.mockClear();
  msal.active = null;
  window.history.replaceState(null, '', '/');
});

describe('startEntra', () => {
  it('uses the Microsoft authority protocol and the origin redirect by default', async () => {
    await startEntra(config);
    const auth = (msal.options[0] as { auth: Record<string, unknown> }).auth;
    expect(auth).toMatchObject({ authority: config.authority, redirectUri: window.location.origin + '/' });
    expect(auth).not.toHaveProperty('protocolMode');
    expect(auth).not.toHaveProperty('knownAuthorities');
  });

  it('carries the current path through the sign-in redirect as state', async () => {
    window.history.replaceState(null, '', '/manage/tables/account?tab=records');
    const session = await startEntra(config);
    await session.signIn();
    expect(msal.loginRedirect).toHaveBeenCalledWith({ scopes: ['api://x/access'], state: '/manage/tables/account?tab=records' });
  });

  it('returns the path the redirect started from', async () => {
    msal.redirected = { account: { localAccountId: 'u1', username: 'u@x' }, state: '/manage/plugins' };
    expect((await startEntra(config)).returnPath).toBe('/manage/plugins');
  });
});

describe('safeReturnPath', () => {
  it.each([undefined, null, '', 'manage', '//evil.example/x', 'https://evil.example/', '/\\evil.example'])('refuses %s', (state) => {
    expect(safeReturnPath(state)).toBeNull();
  });

  it('keeps a path of this origin', () => {
    expect(safeReturnPath('/manage/tables/account?tab=records#x')).toBe('/manage/tables/account?tab=records#x');
  });
});

describe('getTokenSilently', () => {
  it('rejects without a redirect when interaction is required, where getToken redirects', async () => {
    msal.active = { localAccountId: 'u1', username: 'u@x' };
    const session = await startEntra(config);

    await expect(session.getTokenSilently()).rejects.toBeInstanceOf(InteractionRequired);
    expect(msal.acquireTokenRedirect).not.toHaveBeenCalled();

    await expect(session.getToken()).rejects.toThrow();
    expect(msal.acquireTokenRedirect).toHaveBeenCalledTimes(1);
  });
});
