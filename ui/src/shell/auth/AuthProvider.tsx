import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import { XrmApi } from '../api/client';
import { XrmError } from '../api/errors';
import { t } from '../i18n/t';
import { entraConfigError, loadUiConfig, type UiConfig } from './config';
import { clearDevCredentials, createDevToken, loadDevCredentials, saveDevCredentials, type DevCredentials } from './devToken';
import type { EntraSession } from './entra';

export type AuthStatus = 'signedOut' | 'signingIn' | 'signedIn' | 'error';

export interface Auth {
  status: AuthStatus;
  user?: { id: string; name: string };
  error?: string;
  config?: UiConfig;
  /** True after a 401 signed the user out. */
  expired?: boolean;
  /** Present once signed in: every request carries the current token and a 401 signs the user out. */
  api?: XrmApi;
  /** Set while the web session cookie (form scripts and web resources) could not be set; cleared once it is. */
  sessionCookieProblem?: SessionCookieProblem;
  getToken(): Promise<string>;
  /** Entra and Local: starts the redirect flow. Development: use {@link Auth.signInDev}. */
  signIn(): void;
  signInDev(credentials: DevCredentials): void;
  signOut(): void;
}

const AuthContext = createContext<Auth | null>(null);

export function useAuth(): Auth {
  const auth = useContext(AuthContext);
  if (!auth) throw new Error('useAuth must be used inside <AuthProvider>');
  return auth;
}

/** The auth state, or null outside an `<AuthProvider>` (a component rendered on its own, as in a unit test). */
export function useOptionalAuth(): Auth | null {
  return useContext(AuthContext);
}

interface State {
  status: AuthStatus;
  user?: { id: string; name: string };
  error?: string;
  config?: UiConfig;
  expired?: boolean;
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<State>({ status: 'signingIn' });
  const dev = useRef<DevCredentials | null>(null);
  const entra = useRef<EntraSession | null>(null);
  // StrictMode runs the init effect twice: MSAL must be created and its redirect handled once.
  const entraStart = useRef<Promise<EntraSession> | null>(null);
  const navigate = useNavigate();
  const navigateRef = useRef(navigate);
  navigateRef.current = navigate;
  const sessionCookie = useRef<SessionCookieKeeper | null>(null);
  const [sessionCookieProblem, setSessionCookieProblem] = useState<SessionCookieProblem>();

  useEffect(() => {
    let cancelled = false;
    const set = (next: State) => !cancelled && setState(next);
    (async () => {
      let config: UiConfig;
      try {
        config = await loadUiConfig();
      } catch (e) {
        return set({ status: 'error', error: (e as Error).message });
      }
      if (config.authMode === 'Development') {
        const stored = loadDevCredentials();
        dev.current = stored;
        return set(
          stored
            ? { status: 'signedIn', config, user: { id: stored.objectId, name: stored.name } }
            : { status: 'signedOut', config },
        );
      }
      if (config.authMode === 'Entra' || config.authMode === 'Local') {
        const local = config.authMode === 'Local';
        const problem = entraConfigError(config);
        if (problem) return set({ status: 'error', config, error: problem });
        try {
          // MSAL is loaded only in Entra mode.
          entraStart.current ??= import('./entra').then(({ startEntra }) =>
            startEntra({
              clientId: config.clientId!,
              authority: config.authority!,
              scope: config.scope!,
              // The local identity provider: plain OIDC, and the redirect URI it registered for xrmd-web (the audience).
              ...(local ? { protocol: 'OIDC' as const, redirectUri: config.audience } : {}),
            }),
          );
          const session = await entraStart.current;
          entra.current = session;
          const account = session.account;
          // Back from the Entra redirect (whose URI is the origin): return to the deep link the sign-in started from.
          if (!cancelled && account && session.returnPath) navigateRef.current(session.returnPath, { replace: true });
          return set(
            account
              ? { status: 'signedIn', config, user: { id: account.localAccountId, name: account.name ?? account.username } }
              : { status: 'signedOut', config },
          );
        } catch (e) {
          return set({ status: 'error', config, error: t('auth.entra.failed', { detail: e instanceof Error ? e.message : String(e) }) });
        }
      }
      set({ status: 'error', config, error: t('auth.config.unknownMode', { mode: config.authMode }) });
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const audience = state.config?.audience ?? '';
  const mode = state.config?.authMode;

  const getToken = useCallback(async () => {
    if (mode === 'Development') {
      const c = dev.current;
      if (!c) throw new Error(t('api.error.noToken'));
      return createDevToken({ signingKey: c.signingKey, objectId: c.objectId, name: c.name, audience });
    }
    if ((mode === 'Entra' || mode === 'Local') && entra.current) return entra.current.getToken();
    throw new Error(t('api.error.noToken'));
  }, [mode, audience]);

  /**
   * A token without user interaction (never a sign-in redirect), for the session cookie: a renewal in the background must not
   * leave the page while the user is editing. Development tokens are minted locally, so they are always silent.
   */
  const silentToken = useCallback(async () => {
    if ((mode === 'Entra' || mode === 'Local') && entra.current) return entra.current.getTokenSilently();
    return getToken();
  }, [mode, getToken]);

  // Signed in: the web session cookie carries the current token, renewed before it expires (see SessionCookieKeeper).
  useEffect(() => {
    if (state.status !== 'signedIn') return;
    const keeper = new SessionCookieKeeper(silentToken, mode !== 'Development', (problem) => setSessionCookieProblem(problem ?? undefined));
    sessionCookie.current = keeper;
    keeper.start();
    // The cookie is shared by every tab; sign-in is per tab: the tab the user comes back to sets it to its own token again.
    const resync = () => {
      if (document.visibilityState !== 'hidden') void keeper.resync();
    };
    window.addEventListener('focus', resync);
    document.addEventListener('visibilitychange', resync);
    return () => {
      window.removeEventListener('focus', resync);
      document.removeEventListener('visibilitychange', resync);
      void keeper.stop();
      setSessionCookieProblem(undefined);
      if (sessionCookie.current === keeper) sessionCookie.current = null;
    };
  }, [state.status, silentToken, mode]);

  /**
   * Stops renewing the cookie and waits for a post in flight. With `clear`, then sends `DELETE /api/session` with a token obtained
   * without interaction; when there is none (e.g. it expired), the DELETE is skipped and the cookie lapses at the token's `exp`.
   * Never fails.
   */
  const endSession = useCallback(
    async (clear: boolean) => {
      // Asked for before anything else: sign-out forgets the development credentials right after this call.
      const token = clear ? silentToken().catch(() => null) : Promise.resolve(null);
      const keeper = sessionCookie.current;
      sessionCookie.current = null;
      await keeper?.stop();
      const bearer = await token;
      if (!bearer) return;
      await XrmApi.endSession(bearer).then(
        () => SessionCookieKeeper.cleared(),
        (e: unknown) => console.warn('xrmverse: the web session cookie could not be cleared.', e),
      );
    },
    [silentToken],
  );

  /** The API's token: a token renewed since the last post is posted to the session cookie too. */
  const apiToken = useCallback(async () => {
    const token = await getToken();
    sessionCookie.current?.observe(token);
    return token;
  }, [getToken]);

  /** A 401: the token is unusable, so forget it locally and return to the sign-in page (no Entra round trip). */
  const expire = useCallback(() => {
    // The token was refused, so a DELETE with it would be too: the cookie (the same token) is no longer accepted either.
    void endSession(false);
    clearDevCredentials();
    dev.current = null;
    void entra.current?.forget();
    setState((s) => (s.status === 'signedIn' ? { status: 'signedOut', config: s.config, expired: true } : s));
  }, [endSession]);

  const api = useMemo(
    () => (state.status === 'signedIn' ? new XrmApi({ getToken: apiToken, onUnauthorized: expire }) : undefined),
    [state.status, apiToken, expire],
  );

  const signIn = useCallback(() => {
    if ((mode !== 'Entra' && mode !== 'Local') || !entra.current) return;
    setState((s) => ({ ...s, status: 'signingIn', expired: false }));
    entra.current.signIn().catch((e: unknown) =>
      setState((s) => ({ ...s, status: 'error', error: t('auth.entra.failed', { detail: e instanceof Error ? e.message : String(e) }) })),
    );
  }, [mode]);

  const signInDev = useCallback((credentials: DevCredentials) => {
    saveDevCredentials(credentials);
    dev.current = credentials;
    setState((s) => ({ status: 'signedIn', config: s.config, user: { id: credentials.objectId, name: credentials.name } }));
  }, []);

  const signOut = useCallback(() => {
    if ((mode === 'Entra' || mode === 'Local') && entra.current) {
      const session = entra.current;
      // The cookie is cleared before the sign-out redirect leaves the page.
      void endSession(true).then(() => session.signOut());
      return;
    }
    void endSession(true);
    clearDevCredentials();
    dev.current = null;
    setState((s) => ({ status: 'signedOut', config: s.config }));
  }, [mode, endSession]);

  const auth = useMemo<Auth>(
    () => ({
      status: state.status,
      user: state.user,
      error: state.error,
      config: state.config,
      expired: state.expired,
      api,
      sessionCookieProblem,
      getToken,
      signIn,
      signInDev,
      signOut,
    }),
    [state, api, sessionCookieProblem, getToken, signIn, signInDev, signOut],
  );
  return <AuthContext.Provider value={auth}>{children}</AuthContext.Provider>;
}

/** How long before the token's `exp` the cookie gets a fresh token (MSAL renews silently within five minutes of `exp`). */
const RENEW_BEFORE_EXP_MS = 2 * 60_000;
/** The shortest wait between two renewals, whatever the token's lifetime. */
const MIN_RENEW_DELAY_MS = 30_000;
/** The waits before each new attempt after a failed post (a network error or a server error); then the keeper gives up. */
const SESSION_RETRY_DELAYS_MS = [2_000, 5_000, 15_000, 30_000, 60_000];
/** The longest token the server keeps in the cookie (`SessionCookie.MaxTokenLength`); a longer one is refused with 400. */
const MAX_COOKIE_TOKEN_LENGTH = 3900;
/**
 * The tabs of this origin tell each other when they set or clear the cookie, so that a tab knows whether the cookie is still
 * its own. Nothing is stored: only tabs that are open at the time need to know.
 */
export const SESSION_CHANNEL = 'xrmverse.sessionCookie';

/** Why the web session cookie is not set: shown in the shell, since every form script and web resource depends on it. */
export interface SessionCookieProblem {
  /** The server's message (or the network error). */
  message: string;
  /** The server refused the token because it is longer than a cookie can hold: not retried. */
  tooLong: boolean;
}

/** The `exp` of a JWT, in milliseconds; null when the token is not a JWT with a numeric `exp`. */
export function tokenExpiry(token: string): number | null {
  try {
    const payload = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    const exp = (JSON.parse(atob(payload)) as { exp?: unknown }).exp;
    return typeof exp === 'number' ? exp * 1000 : null;
  } catch {
    return null;
  }
}

/** The channel to the other tabs; null where the browser has none (a tab then posts again on every focus). */
function openChannel(): BroadcastChannel | null {
  try {
    return typeof BroadcastChannel === 'function' ? new BroadcastChannel(SESSION_CHANNEL) : null;
  } catch {
    return null;
  }
}

/** A refusal that a new attempt with the same token cannot change (4xx other than timeout and throttling). */
const isPermanent = (status: number) => status >= 400 && status < 500 && status !== 408 && status !== 429;

/**
 * Keeps the `xrmd.session` cookie (plan 3.5 UI-1c, ruling C1) on the current token: posts it at sign-in, again before it expires
 * (a fresh token from the silent getter, which never redirects; when it fails, renewal stops), and, when `postRenewals`, whenever the API is handed a token other than the posted one (a silent renewal).
 * Development tokens are minted per request, so they are posted on the timer only.
 *
 * The cookie is one per browser, but sign-in is per tab: when the tab gains focus or becomes visible, it posts its own token
 * again if that token differs from the one it last posted or another tab has set the cookie since (the active tab wins).
 *
 * A token counts as posted only once the server accepted it. A failed post is retried after {@link SESSION_RETRY_DELAYS_MS}
 * (a 4xx refusal, such as the 400 for a token too long for a cookie, is not) and reported through `onProblem` until a post
 * succeeds; it never breaks the sign-in: the shell's own requests carry the bearer header.
 */
class SessionCookieKeeper {
  private active = false;
  /** The token the server last accepted from this tab. */
  private posted: string | null = null;
  /** The token last sent (accepted, in flight, waiting for a retry, or refused): not sent again by a renewal or an API call. */
  private latest: string | null = null;
  /** A token the server refused for good: not sent again, not even on focus. */
  private refused: string | null = null;
  private inFlight: string | null = null;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  private pending: Promise<void> = Promise.resolve();
  /** True while the cookie holds this tab's token: set by an accepted post, dropped when another tab sets or clears it. */
  private owns = false;
  private channel: BroadcastChannel | null = null;

  constructor(
    private readonly getToken: () => Promise<string>,
    private readonly postRenewals: boolean,
    private readonly onProblem: (problem: SessionCookieProblem | null) => void,
  ) {}

  start(): void {
    this.active = true;
    this.channel = openChannel();
    if (this.channel) this.channel.onmessage = () => void (this.owns = false);
    void this.renew();
  }

  observe(token: string): void {
    if (this.active && this.postRenewals && token !== this.latest) this.post(token);
  }

  /** The tab gained focus or became visible: make the cookie this tab's again if another tab (or a failure) changed it. */
  async resync(): Promise<void> {
    if (!this.active) return;
    let token: string;
    try {
      token = await this.getToken();
    } catch {
      return;
    }
    if (!this.active || token === this.refused || token === this.inFlight) return;
    if (token === this.posted && this.owns) return;
    this.send(token, 0);
    this.schedule(token);
  }

  /** Stops renewing; resolves once a post in flight has finished, so that a later clear is not overtaken by it. */
  stop(): Promise<void> {
    this.active = false;
    clearTimeout(this.timer);
    clearTimeout(this.retryTimer);
    this.channel?.close();
    this.channel = null;
    return this.pending;
  }

  /** After `DELETE /api/session`: the other tabs no longer own the cookie, so the next one to gain focus sets it again. */
  static cleared(): void {
    const channel = openChannel();
    channel?.postMessage('cleared');
    channel?.close();
  }

  private async renew(): Promise<void> {
    let token: string;
    try {
      token = await this.getToken();
    } catch (e) {
      // Interaction needed (or no token): stop renewing and let the cookie lapse; the next API call the user makes signs in again.
      console.warn('xrmverse: the web session cookie is no longer renewed.', e);
      return;
    }
    if (this.active) this.post(token);
  }

  private post(token: string): void {
    if (token !== this.latest) this.send(token, 0);
    this.schedule(token);
  }

  private schedule(token: string): void {
    clearTimeout(this.timer);
    const exp = tokenExpiry(token);
    if (exp === null) return;
    const delay = Math.max(exp - RENEW_BEFORE_EXP_MS - Date.now(), MIN_RENEW_DELAY_MS);
    this.timer = setTimeout(() => void this.renew(), delay);
  }

  private send(token: string, attempt: number): void {
    clearTimeout(this.retryTimer);
    this.latest = token;
    this.inFlight = token;
    this.pending = this.pending
      .then(() => XrmApi.session(token))
      .then(
        () => {
          if (this.inFlight === token) this.inFlight = null;
          this.posted = token;
          this.refused = null;
          this.owns = true;
          this.channel?.postMessage('set');
          if (this.active && this.latest === token) this.onProblem(null);
        },
        (e: unknown) => {
          if (this.inFlight === token) this.inFlight = null;
          // A newer token was sent meanwhile, or the keeper stopped: this failure no longer says anything about the cookie.
          if (!this.active || this.latest !== token) return;
          console.warn('xrmverse: the web session cookie could not be set.', e);
          const status = e instanceof XrmError ? e.status : 0;
          const message = e instanceof XrmError ? e.serverMessage : e instanceof Error ? e.message : String(e);
          const permanent = isPermanent(status);
          this.onProblem({ message, tooLong: permanent && token.length > MAX_COOKIE_TOKEN_LENGTH });
          if (permanent) {
            this.refused = token;
            return;
          }
          const delay = SESSION_RETRY_DELAYS_MS[attempt];
          if (delay === undefined) return;
          this.retryTimer = setTimeout(() => {
            if (this.active && this.latest === token && this.posted !== token) this.send(token, attempt + 1);
          }, delay);
        },
      );
  }
}
