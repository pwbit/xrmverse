import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { setUiLanguage } from '../i18n/t';
import { server } from '../../test/server';
import { AuthProvider, SESSION_CHANNEL, tokenExpiry, useAuth } from './AuthProvider';
import { SessionCookieWarning } from './SessionCookieWarning';

/** An unsigned JWT-shaped token: the shell only reads its `exp`. */
function jwt(label: string, expSeconds: number): string {
  const part = (o: object) => btoa(JSON.stringify(o)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${part({ alg: 'none' })}.${part({ exp: expSeconds, label })}.sig`;
}

const tokens = vi.hoisted(() => ({ next: [] as string[] }));
const session = vi.hoisted(() => ({
  account: { localAccountId: 'u1', username: 'u@x', name: 'U' },
  returnPath: null as string | null,
  /** The API's getter: in MSAL it falls back to a sign-in redirect when interaction is required. */
  getToken: vi.fn(async () => tokens.next.shift() ?? 'no-more-tokens'),
  /** The keeper's getter: never redirects. */
  getTokenSilently: vi.fn(async (): Promise<string> => tokens.next.shift() ?? 'no-more-tokens'),
  signIn: vi.fn(async () => {}),
  signOut: vi.fn(async () => {}),
  forget: vi.fn(async () => {}),
}));
vi.mock('./entra', () => ({ startEntra: vi.fn(async () => session) }));

const config = {
  authMode: 'Entra',
  audience: 'api://xrmverse',
  authority: 'https://login.microsoftonline.com/t/v2.0',
  clientId: 'spa-client',
  scope: 'api://xrmverse/access',
  apiVersion: 'v9.2',
};

interface Call {
  method: string;
  authorization: string | null;
}

let calls: Call[];

function Probe() {
  const { status, api, signOut } = useAuth();
  return (
    <>
      <output aria-label="status">{status}</output>
      <button onClick={() => void api?.get('WhoAmI').catch(() => {})}>probe-call</button>
      <button onClick={signOut}>probe-sign-out</button>
      <SessionCookieWarning />
    </>
  );
}

function renderShell() {
  return render(
    <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <AuthProvider>
        <Probe />
      </AuthProvider>
    </MemoryRouter>,
  );
}

const posts = () => calls.filter((c) => c.method === 'POST').map((c) => c.authorization);
const nowSeconds = () => Math.floor(Date.now() / 1000);

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  calls = [];
  tokens.next = [];
  for (const fn of [session.getToken, session.signIn, session.signOut, session.forget]) fn.mockClear();
  session.getTokenSilently.mockReset();
  session.getTokenSilently.mockImplementation(async () => tokens.next.shift() ?? 'no-more-tokens');
  const record = ({ request }: { request: Request }) => {
    calls.push({ method: request.method, authorization: request.headers.get('Authorization') });
    return new HttpResponse(null, { status: 204 });
  };
  server.use(
    http.get('*/ui/config.json', () => HttpResponse.json(config)),
    http.post('*/api/session', record),
    http.delete('*/api/session', record),
    http.get('*/api/data/v9.2/WhoAmI', () => HttpResponse.json({ UserId: 'aaaaaaaa-0000-0000-0000-000000000001' })),
  );
});

afterEach(() => {
  vi.useRealTimers();
  setUiLanguage(undefined);
});

/** A Dataverse error envelope, as the server answers a refused `POST /api/session`. */
const fault = (status: number, message: string) =>
  HttpResponse.json({ error: { code: '0x80040203', message } }, { status });

/** Posts answered in turn by `answers` (a 204 once they run out), recorded like the default handler. */
function answerPosts(...answers: (() => Response)[]) {
  server.use(
    http.post('*/api/session', ({ request }) => {
      calls.push({ method: 'POST', authorization: request.headers.get('Authorization') });
      return (answers.shift() ?? (() => new HttpResponse(null, { status: 204 })))();
    }),
  );
}

/** Another tab of the same browser tells this one that it set (or cleared) the cookie. */
async function otherTab(message: 'set' | 'cleared') {
  const channel = new BroadcastChannel(SESSION_CHANNEL);
  channel.postMessage(message);
  channel.close();
  await act(() => vi.advanceTimersByTimeAsync(20));
}

const warningBar = () => screen.queryByText('Form scripts and web resources are unavailable');

describe('web session cookie', () => {
  it('reads_exp_from_a_jwt', () => {
    expect(tokenExpiry(jwt('a', 1_900_000_000))).toBe(1_900_000_000_000);
    expect(tokenExpiry('opaque')).toBeNull();
  });

  it('posts_session_after_sign_in_and_renewal', async () => {
    const first = jwt('first', nowSeconds() + 600);
    const renewedOnApiCall = jwt('renewed-on-call', nowSeconds() + 900);
    const renewedOnTimer = jwt('renewed-on-timer', nowSeconds() + 1200);
    tokens.next = [first, renewedOnApiCall, renewedOnTimer];
    renderShell();

    // Sign-in: the token is posted as the bearer header of POST /api/session.
    await waitFor(() => expect(posts()).toEqual([`Bearer ${first}`]));
    expect(screen.getByRole('status', { name: 'status' })).toHaveTextContent('signedIn');

    // A silent renewal seen on an API call is posted too.
    await userEvent.click(screen.getByRole('button', { name: 'probe-call' }));
    await waitFor(() => expect(posts()).toEqual([`Bearer ${first}`, `Bearer ${renewedOnApiCall}`]));

    // Before the posted token expires (two minutes ahead of exp), a fresh token is asked for and posted.
    await act(() => vi.advanceTimersByTimeAsync(900_000 - 120_000 - 5_000));
    expect(posts()).toHaveLength(2);
    await act(() => vi.advanceTimersByTimeAsync(10_000));
    await waitFor(() => expect(posts()).toEqual([`Bearer ${first}`, `Bearer ${renewedOnApiCall}`, `Bearer ${renewedOnTimer}`]));
  });

  it('failed_session_post_does_not_break_sign_in', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    tokens.next = [jwt('first', nowSeconds() + 600)];
    server.use(http.post('*/api/session', () => new HttpResponse(null, { status: 500 })));
    renderShell();

    await waitFor(() => expect(warn).toHaveBeenCalled());
    expect(screen.getByRole('status', { name: 'status' })).toHaveTextContent('signedIn');
    warn.mockRestore();
  });

  it('renewal_needing_interaction_stops_without_redirect', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const first = jwt('first', nowSeconds() + 600);
    session.getTokenSilently.mockResolvedValueOnce(first).mockRejectedValue(new Error('interaction_required'));
    renderShell();
    await waitFor(() => expect(posts()).toEqual([`Bearer ${first}`]));

    // The renewal two minutes before exp needs interaction: no redirecting getter, no post, no further attempt.
    await act(() => vi.advanceTimersByTimeAsync(600_000));
    await waitFor(() => expect(warn).toHaveBeenCalled());
    expect(session.getTokenSilently).toHaveBeenCalledTimes(2);
    expect(session.getToken).not.toHaveBeenCalled();
    expect(posts()).toHaveLength(1);
    await act(() => vi.advanceTimersByTimeAsync(3_600_000));
    expect(session.getTokenSilently).toHaveBeenCalledTimes(2);
    expect(screen.getByRole('status', { name: 'status' })).toHaveTextContent('signedIn');
    warn.mockRestore();
  });

  it('does_not_post_the_same_token_twice', async () => {
    const same = jwt('same', nowSeconds() + 600);
    tokens.next = [same, same];
    renderShell();
    await waitFor(() => expect(posts()).toHaveLength(1));

    await userEvent.click(screen.getByRole('button', { name: 'probe-call' }));
    await waitFor(() => expect(session.getToken).toHaveBeenCalledTimes(1));
    expect(posts()).toHaveLength(1);
  });

  it('deletes_session_on_sign_out', async () => {
    const first = jwt('first', nowSeconds() + 600);
    const current = jwt('current', nowSeconds() + 600);
    tokens.next = [first, current];
    renderShell();
    await waitFor(() => expect(posts()).toHaveLength(1));

    await userEvent.click(screen.getByRole('button', { name: 'probe-sign-out' }));

    await waitFor(() => expect(session.signOut).toHaveBeenCalledTimes(1));
    expect(calls.map((c) => c.method)).toEqual(['POST', 'DELETE']);
    // The DELETE carries the current token, obtained silently.
    expect(calls[1].authorization).toBe(`Bearer ${current}`);
    expect(session.getToken).not.toHaveBeenCalled();
    // No renewal after sign-out.
    await act(() => vi.advanceTimersByTimeAsync(3_600_000));
    expect(calls.map((c) => c.method)).toEqual(['POST', 'DELETE']);
  });

  it('skips_delete_on_sign_out_without_a_silent_token', async () => {
    tokens.next = [jwt('first', nowSeconds() + 600)];
    renderShell();
    await waitFor(() => expect(posts()).toHaveLength(1));
    session.getTokenSilently.mockRejectedValue(new Error('interaction_required'));

    await userEvent.click(screen.getByRole('button', { name: 'probe-sign-out' }));

    await waitFor(() => expect(session.signOut).toHaveBeenCalledTimes(1));
    expect(calls.map((c) => c.method)).toEqual(['POST']);
    expect(session.getToken).not.toHaveBeenCalled();
  });

  it('failed_post_is_retried_with_backoff_and_the_warning_shows_then_clears', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const first = jwt('first', nowSeconds() + 600);
    tokens.next = [first, first];
    answerPosts(() => fault(503, 'The server is starting.'), () => fault(503, 'The server is starting.'));
    renderShell();

    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(await screen.findByText(/could not be set up: The server is starting\./)).toBeInTheDocument();
    expect(warningBar()).toBeInTheDocument();
    // Not counted as posted: the API handing out the same token does not post it again while a retry is due.
    await userEvent.click(screen.getByRole('button', { name: 'probe-call' }));
    expect(posts()).toHaveLength(1);

    // First retry after 2 s (fails again), second after 5 s more (succeeds): the warning goes away.
    await act(() => vi.advanceTimersByTimeAsync(2_000));
    await waitFor(() => expect(posts()).toHaveLength(2));
    expect(warningBar()).toBeInTheDocument();
    await act(() => vi.advanceTimersByTimeAsync(5_000));
    await waitFor(() => expect(posts()).toEqual([`Bearer ${first}`, `Bearer ${first}`, `Bearer ${first}`]));
    await waitFor(() => expect(warningBar()).not.toBeInTheDocument());
    // Accepted: no more attempts.
    await act(() => vi.advanceTimersByTimeAsync(60_000));
    expect(posts()).toHaveLength(3);
    warn.mockRestore();
  });

  it('retries_are_bounded_and_focus_tries_again_since_the_token_was_never_posted', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const first = jwt('first', nowSeconds() + 3600);
    tokens.next = [first, first];
    answerPosts(...Array.from({ length: 6 }, () => () => HttpResponse.error()));
    renderShell();

    await waitFor(() => expect(posts()).toHaveLength(1));
    await act(() => vi.advanceTimersByTimeAsync(2_000 + 5_000 + 15_000 + 30_000 + 60_000));
    await waitFor(() => expect(posts()).toHaveLength(6));
    await act(() => vi.advanceTimersByTimeAsync(600_000));
    expect(posts()).toHaveLength(6);
    expect(warningBar()).toBeInTheDocument();

    // The tab gains focus: its token was never accepted, so it is posted again (and this time accepted).
    answerPosts();
    act(() => void window.dispatchEvent(new Event('focus')));
    await waitFor(() => expect(posts()).toHaveLength(7));
    await waitFor(() => expect(warningBar()).not.toBeInTheDocument());
    warn.mockRestore();
  });

  it('token_too_long_for_the_cookie_shows_a_clear_message_and_is_not_retried', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const long = jwt('x'.repeat(4000), nowSeconds() + 600);
    tokens.next = [long, long, long];
    answerPosts(() => fault(400, `The access token is ${long.length} characters long; a browser cannot keep more than 3900 in a cookie.`));
    renderShell();

    expect(await screen.findByText(/Your access token is too long to be kept in the web session cookie/)).toBeInTheDocument();
    expect(screen.getByText(new RegExp(`Server message: The access token is ${long.length} characters long`))).toBeInTheDocument();
    await act(() => vi.advanceTimersByTimeAsync(300_000));
    act(() => void window.dispatchEvent(new Event('focus')));
    await userEvent.click(screen.getByRole('button', { name: 'probe-call' }));
    await act(() => vi.advanceTimersByTimeAsync(1_000));
    expect(posts()).toHaveLength(1);
    expect(warningBar()).toBeInTheDocument();
    warn.mockRestore();
  });

  it('the_warning_is_in_italian_for_an_italian_user', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    setUiLanguage(1040);
    const long = jwt('x'.repeat(4000), nowSeconds() + 600);
    tokens.next = [long];
    answerPosts(() => fault(400, 'too long'));
    renderShell();
    expect(await screen.findByText('Script dei moduli e risorse Web non disponibili')).toBeInTheDocument();
    expect(screen.getByText(/Il token di accesso è troppo lungo.*Messaggio del server: too long/)).toBeInTheDocument();
    warn.mockRestore();
  });

  it('focus_reposts_the_tab_token_only_when_another_tab_set_the_cookie', async () => {
    const mine = jwt('mine', nowSeconds() + 600);
    session.getTokenSilently.mockImplementation(async () => mine);
    renderShell();
    await waitFor(() => expect(posts()).toEqual([`Bearer ${mine}`]));

    // Same token, still this tab's cookie: nothing to do.
    act(() => void window.dispatchEvent(new Event('focus')));
    await act(() => vi.advanceTimersByTimeAsync(100));
    expect(posts()).toHaveLength(1);

    // Another tab (another user) posted its token: coming back to this tab makes the cookie this tab's again.
    await otherTab('set');
    act(() => void window.dispatchEvent(new Event('focus')));
    await waitFor(() => expect(posts()).toEqual([`Bearer ${mine}`, `Bearer ${mine}`]));

    // The same through visibilitychange (the tab becomes visible again).
    await otherTab('set');
    act(() => void document.dispatchEvent(new Event('visibilitychange')));
    await waitFor(() => expect(posts()).toHaveLength(3));

    // A renewed token is posted on focus even while this tab still owns the cookie.
    const renewed = jwt('renewed', nowSeconds() + 900);
    session.getTokenSilently.mockImplementation(async () => renewed);
    act(() => void window.dispatchEvent(new Event('focus')));
    await waitFor(() => expect(posts()).toEqual([`Bearer ${mine}`, `Bearer ${mine}`, `Bearer ${mine}`, `Bearer ${renewed}`]));
  });

  it('sign_out_in_another_tab_is_repaired_when_this_tab_gains_focus', async () => {
    const mine = jwt('mine', nowSeconds() + 600);
    session.getTokenSilently.mockImplementation(async () => mine);
    renderShell();
    await waitFor(() => expect(posts()).toHaveLength(1));
    // Another tab signed out: DELETE /api/session cleared the cookie, and that tab told the others.
    await otherTab('cleared');
    act(() => void window.dispatchEvent(new Event('focus')));
    await waitFor(() => expect(posts()).toHaveLength(2));
  });
});
