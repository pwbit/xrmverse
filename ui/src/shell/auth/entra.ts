import {
  InteractionRequiredAuthError,
  ProtocolMode,
  PublicClientApplication,
  type AccountInfo,
} from '@azure/msal-browser';
import { t } from '../i18n/t';

export interface EntraConfig {
  clientId: string;
  authority: string;
  scope: string;
  /**
   * The local identity provider (Local mode): plain OIDC against its own discovery document. MSAL then trusts the
   * authority's host (`knownAuthorities`) and never asks a Microsoft endpoint about it.
   */
  protocol?: 'OIDC';
  /** The redirect URI registered for the client; the origin when omitted. */
  redirectUri?: string;
}

export interface EntraSession {
  account: AccountInfo | null;
  /** The shell path the sign-in redirect started from (MSAL `state`), to return to after it; null when none. */
  returnPath: string | null;
  getToken(): Promise<string>;
  /** A token without any user interaction: never redirects; rejects when the token cannot be renewed silently. */
  getTokenSilently(): Promise<string>;
  signIn(): Promise<void>;
  signOut(): Promise<void>;
  /** Forgets the cached account without a round trip to Entra (after a 401). */
  forget(): Promise<void>;
}

/** The current shell path, carried through the Entra redirect as MSAL `state` (the redirect URI is the origin). */
const currentPath = () => window.location.pathname + window.location.search + window.location.hash;

/** `state` back from a redirect, if it is a path of this origin (never `//host` or a URL elsewhere). */
export function safeReturnPath(state: string | undefined | null): string | null {
  if (!state || !state.startsWith('/') || state.startsWith('//') || state.includes('\\')) return null;
  return state;
}

/** MSAL.js redirect flow (Entra, and the local identity provider in OIDC mode); the token cache lives in `sessionStorage`. */
export async function startEntra(config: EntraConfig): Promise<EntraSession> {
  const redirectUri = config.redirectUri ?? window.location.origin + '/';
  const msal = new PublicClientApplication({
    auth: {
      clientId: config.clientId,
      authority: config.authority,
      redirectUri,
      postLogoutRedirectUri: redirectUri,
      ...(config.protocol === 'OIDC'
        ? { protocolMode: ProtocolMode.OIDC, knownAuthorities: [new URL(config.authority).host] }
        : {}),
    },
    cache: { cacheLocation: 'sessionStorage' },
  });
  await msal.initialize();
  const redirected = await msal.handleRedirectPromise();
  const account = redirected?.account ?? msal.getActiveAccount() ?? msal.getAllAccounts()[0] ?? null;
  if (account) msal.setActiveAccount(account);
  const scopes = [config.scope];

  const session: EntraSession = {
    account,
    returnPath: safeReturnPath(redirected?.state),
    async getToken() {
      const active = msal.getActiveAccount();
      if (!active) throw new Error(t('api.error.noToken'));
      try {
        return (await msal.acquireTokenSilent({ scopes, account: active })).accessToken;
      } catch (e) {
        if (e instanceof InteractionRequiredAuthError) {
          await msal.acquireTokenRedirect({ scopes, account: active, state: currentPath() });
          // The page navigates away; this never resolves into a token.
          throw new Error(t('auth.signIn.working'));
        }
        throw e;
      }
    },
    async getTokenSilently() {
      const active = msal.getActiveAccount();
      if (!active) throw new Error(t('api.error.noToken'));
      return (await msal.acquireTokenSilent({ scopes, account: active })).accessToken;
    },
    signIn: () => msal.loginRedirect({ scopes, state: currentPath() }),
    signOut: () => msal.logoutRedirect({ account: msal.getActiveAccount() ?? undefined }),
    async forget() {
      msal.setActiveAccount(null);
      await msal.clearCache();
    },
  };
  return session;
}
