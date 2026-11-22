import { useEffect } from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { en } from '../i18n/en';
import { AccessGate } from './AccessGate';
import { AuthProvider, useAuth } from './AuthProvider';
import { saveDevCredentials } from './devToken';
import { useAccess } from './roles';

const SYSTEM_ADMIN = '627090FF-40A3-4053-8790-584EDC5BE201';
const SYSTEM_CUSTOMIZER = '119F245C-3CC8-4B62-B31C-D1A046CED15D';
const BASIC_USER = '236750CD-45AE-4939-AB12-B24B920CED93';
const USER_ID = 'aaaaaaaa-0000-0000-0000-000000000001';
const TEAM_ID = 'bbbbbbbb-0000-0000-0000-000000000001';

const roleRows = [
  { roleid: SYSTEM_ADMIN.toLowerCase(), name: 'System Administrator', roletemplateid: SYSTEM_ADMIN.toLowerCase() },
  { roleid: SYSTEM_CUSTOMIZER.toLowerCase(), name: 'System Customizer', roletemplateid: SYSTEM_CUSTOMIZER.toLowerCase() },
  { roleid: BASIC_USER.toLowerCase(), name: 'Basic User', roletemplateid: BASIC_USER.toLowerCase() },
];

let requests: string[] = [];

interface Setup {
  config?: Record<string, unknown>;
  userRoles?: string[];
  teamRoles?: string[];
  /** The team's `teamtype`: 0 owner (default), 1 access; undefined leaves it unset. */
  teamType?: number | null;
  whoAmI?: () => Response;
}

function mock({ config, userRoles = [], teamRoles, teamType = 0, whoAmI }: Setup = {}) {
  server.use(
    http.get('*/ui/config.json', () =>
      HttpResponse.json({ authMode: 'Development', audience: 'http://localhost/', apiVersion: 'v9.2', ...config }),
    ),
    http.get('*/api/data/v9.2/*', ({ request }) => {
      const url = new URL(request.url);
      requests.push(url.pathname.split('/').pop()! + (url.search ? '?' : '') + decodeURIComponent(url.search.slice(1)));
      const entity = url.pathname.split('/').pop()!;
      if (entity === 'WhoAmI') return whoAmI?.() ?? HttpResponse.json({ UserId: USER_ID });
      if (entity === 'systemuserrolescollection') return HttpResponse.json({ value: userRoles.map((roleid) => ({ roleid })) });
      if (entity === 'teammemberships') return HttpResponse.json({ value: teamRoles ? [{ teamid: TEAM_ID }] : [] });
      if (entity === 'teams') return HttpResponse.json({ value: [{ teamid: TEAM_ID, teamtype: teamType }] });
      if (entity === 'teamrolescollection') return HttpResponse.json({ value: (teamRoles ?? []).map((roleid) => ({ roleid })) });
      if (entity === 'roles') {
        const filter = url.searchParams.get('$filter') ?? '';
        return HttpResponse.json({ value: roleRows.filter((r) => filter.includes(r.roleid)) });
      }
      return HttpResponse.json({ value: [] });
    }),
  );
}

function Child() {
  const { level, roles } = useAccess();
  const { api } = useAuth();
  // A data request issued by a child must never precede the access decision.
  useEffect(() => {
    const controller = new AbortController();
    api?.get('accounts', { signal: controller.signal }).catch(() => undefined);
    return () => controller.abort();
  }, [api]);
  return (
    <div>
      children level={level} roles={roles.join('|')}
    </div>
  );
}

/**
 * The child's `accounts` request is issued after the children mount. Wait until the server has seen it: a request
 * still in flight when the test ends would arrive during the next test and land in its `requests`.
 */
const childRequestSeen = () => waitFor(() => expect(requests).toContain('accounts'));

function renderGate() {
  return render(
    <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <AuthProvider>
        <AccessGate>
          <Child />
        </AccessGate>
      </AuthProvider>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  requests = [];
  sessionStorage.clear();
  saveDevCredentials({ signingKey: 'k'.repeat(40), objectId: USER_ID, name: 'Administrator' });
});
afterEach(() => sessionStorage.clear());

describe('AccessGate', () => {
  it('AccessGate_admin_sees_children', async () => {
    mock({ userRoles: [SYSTEM_ADMIN.toLowerCase()] });
    renderGate();
    expect(await screen.findByText(/children level=admin/)).toBeInTheDocument();
    expect(screen.getByText(/roles=System Administrator/)).toBeInTheDocument();
    await childRequestSeen();
  });

  it('AccessGate_customizer_sees_children_with_level_customizer', async () => {
    mock({ userRoles: [SYSTEM_CUSTOMIZER.toLowerCase()] });
    renderGate();
    expect(await screen.findByText(/children level=customizer/)).toBeInTheDocument();
    await childRequestSeen();
  });

  it('AccessGate_team_role_grants_access', async () => {
    mock({ userRoles: [BASIC_USER.toLowerCase()], teamRoles: [SYSTEM_CUSTOMIZER.toLowerCase()] });
    renderGate();
    expect(await screen.findByText(/children level=customizer/)).toBeInTheDocument();
    await childRequestSeen();
    expect(requests.some((r) => r.startsWith('teamrolescollection') && r.includes(TEAM_ID))).toBe(true);
  });

  it('AccessGate_team_role_with_unset_team_type_counts_as_owner_team', async () => {
    mock({ userRoles: [BASIC_USER.toLowerCase()], teamRoles: [SYSTEM_CUSTOMIZER.toLowerCase()], teamType: null });
    renderGate();
    expect(await screen.findByText(/children level=customizer/)).toBeInTheDocument();
    await childRequestSeen();
  });

  it('AccessGate_access_team_roles_are_not_counted', async () => {
    mock({ userRoles: [BASIC_USER.toLowerCase()], teamRoles: [SYSTEM_ADMIN.toLowerCase()], teamType: 1 });
    renderGate();
    expect(await screen.findByText(en['access.denied.title'])).toBeInTheDocument();
    expect(requests.some((r) => r.startsWith('teams?') && r.includes(TEAM_ID))).toBe(true);
    // As on the server, an access team's roles are never read.
    expect(requests.some((r) => r.startsWith('teamrolescollection'))).toBe(false);
    expect(requests).not.toContain('accounts');
  });

  it('AccessGate_admin_wins_over_customizer', async () => {
    mock({ userRoles: [SYSTEM_CUSTOMIZER.toLowerCase(), SYSTEM_ADMIN.toLowerCase()] });
    renderGate();
    expect(await screen.findByText(/children level=admin/)).toBeInTheDocument();
    await childRequestSeen();
  });

  it('AccessGate_other_role_sees_denied_page', async () => {
    mock({ userRoles: [BASIC_USER.toLowerCase()] });
    renderGate();
    expect(await screen.findByText(en['access.denied.title'])).toBeInTheDocument();
    expect(screen.getByText(/System Administrator/)).toBeInTheDocument();
    expect(screen.getByText(/System Customizer/)).toBeInTheDocument();
    expect(screen.queryByText(/children level/)).toBeNull();
    expect(requests).not.toContain('accounts');
  });

  it('AccessGate_no_systemuser_403_sees_denied_page', async () => {
    mock({
      whoAmI: () =>
        HttpResponse.json({ error: { code: '0x80072560', message: 'no user' } }, { status: 403 }) as unknown as Response,
    });
    renderGate();
    expect(await screen.findByText(en['access.denied.title'])).toBeInTheDocument();
  });

  it('AccessGate_server_failure_shows_code_and_message_not_denied', async () => {
    mock({
      whoAmI: () => HttpResponse.json({ error: { code: '0x80040265', message: 'db down' } }, { status: 500 }) as unknown as Response,
    });
    renderGate();
    expect(await screen.findByText(/0x80040265: db down/)).toBeInTheDocument();
    expect(screen.queryByText(en['access.denied.title'])).toBeNull();
  });

  it('AccessGate_issues_no_data_request_before_level_known', async () => {
    mock({ userRoles: [SYSTEM_ADMIN.toLowerCase()] });
    sessionStorage.clear();
    renderGate();
    // Signed out: the sign-in form is shown and nothing was requested.
    expect(await screen.findByRole('button', { name: en['auth.dev.submit'] })).toBeInTheDocument();
    expect(requests).toEqual([]);

    await userEvent.type(screen.getByLabelText(en['auth.dev.signingKey']), 'k'.repeat(40));
    await userEvent.clear(screen.getByLabelText(en['auth.dev.objectId']));
    await userEvent.type(screen.getByLabelText(en['auth.dev.objectId']), USER_ID);
    await userEvent.click(screen.getByRole('button', { name: en['auth.dev.submit'] }));
    expect(await screen.findByText(/children level=admin/)).toBeInTheDocument();

    expect(requests[0]).toBe('WhoAmI');
    await waitFor(() => expect(requests).toContain('accounts'));
    expect(requests.indexOf('accounts')).toBeGreaterThan(requests.findIndex((r) => r.startsWith('roles')));
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.getItem('xrmverse.devSignIn')).toContain(USER_ID);
  });

  it('Entra_mode_without_clientId_shows_config_error_naming_Ui_Entra_ClientId_and_Scope', async () => {
    mock({ config: { authMode: 'Entra', authority: 'https://login.microsoftonline.com/t/v2.0', clientId: null, scope: null } });
    renderGate();
    const message = await screen.findByText(/Ui:Entra:ClientId/);
    expect(message).toHaveTextContent('Ui:Entra:Scope');
    expect(requests).toEqual([]);
  });

  it('401_signs_out_and_returns_to_sign_in', async () => {
    mock({ whoAmI: () => new HttpResponse(null, { status: 401 }) as unknown as Response });
    renderGate();
    expect(await screen.findByRole('button', { name: en['auth.dev.submit'] })).toBeInTheDocument();
    await waitFor(() => expect(sessionStorage.getItem('xrmverse.devSignIn')).toBeNull());
    expect(screen.getByText(en['auth.sessionExpired'])).toBeInTheDocument();
    expect(screen.queryByText(/children level/)).toBeNull();
  });
  it('SignedOut_page_shows_product_and_title_as_separate_block_headings', async () => {
    sessionStorage.clear();
    mock();
    renderGate();
    const h1 = await screen.findByRole('heading', { level: 1 });
    const h2 = screen.getByRole('heading', { level: 2 });
    expect(h1.textContent).toBe(en['app.name']);
    expect(h2.textContent).toBe(en['auth.signIn.title']);
    expect(h1.contains(h2)).toBe(false);
    expect(screen.getByRole('banner')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: en['auth.dev.submit'] })).toBeInTheDocument();
  });
});
