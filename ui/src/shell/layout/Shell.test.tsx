import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/server';
import { XrmError } from '../api/errors';
import { saveDevCredentials } from '../auth/devToken';
import { App } from '../App';
import { en } from '../i18n/en';
import { useNotify } from '../notify/Notifications';

const SYSTEM_ADMIN = '627090ff-40a3-4053-8790-584edc5be201';
const SYSTEM_CUSTOMIZER = '119f245c-3cc8-4b62-b31c-d1a046ced15d';
const USER_ID = 'aaaaaaaa-0000-0000-0000-000000000001';

function mock(roleTemplate: string, orgs: () => Response = () => HttpResponse.json({ value: [{ name: 'Acme Dev' }] }) as unknown as Response) {
  server.use(
    http.get('*/ui/config.json', () =>
      HttpResponse.json({ authMode: 'Development', audience: 'http://localhost/', apiVersion: 'v9.2' }),
    ),
    http.get('*/api/data/v9.2/*', ({ request }) => {
      const entity = new URL(request.url).pathname.split('/').pop()!;
      if (entity === 'WhoAmI') return HttpResponse.json({ UserId: USER_ID });
      if (entity === 'systemuserrolescollection') return HttpResponse.json({ value: [{ roleid: roleTemplate }] });
      if (entity === 'roles') return HttpResponse.json({ value: [{ roleid: roleTemplate, name: 'Role', roletemplateid: roleTemplate }] });
      if (entity === 'organizations') return orgs();
      return HttpResponse.json({ value: [] });
    }),
  );
}

beforeEach(() => {
  sessionStorage.clear();
  saveDevCredentials({ signingKey: 'k'.repeat(40), objectId: USER_ID, name: 'Ada Admin' });
});
afterEach(() => sessionStorage.clear());

describe('Shell', () => {
  it('session_cookie_failure_shows_a_warning_bar_under_the_header', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    mock(SYSTEM_ADMIN);
    server.use(http.post('*/api/session', () => HttpResponse.json({ error: { code: '0x80040203', message: 'Cookie refused.' } }, { status: 400 })));
    render(<App initialEntries={['/manage']} />);
    const title = await screen.findByText(en['auth.sessionCookie.title']);
    expect(screen.getByText(/could not be set up: Cookie refused\./)).toBeInTheDocument();
    // In the shell, between the brand bar and the page, not inside the page.
    expect(within(screen.getByRole('main')).queryByText(en['auth.sessionCookie.title'])).not.toBeInTheDocument();
    expect(screen.getByRole('banner').compareDocumentPosition(title) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(await screen.findByText(en['tables.empty'])).toBeInTheDocument();
    warn.mockRestore();
  });

  it('the header links to the workflow studio when the workflow list is readable', async () => {
    mock(SYSTEM_ADMIN);
    server.use(http.get('*/authoring/workflows', () => HttpResponse.json([])));
    render(<App initialEntries={['/manage']} />);
    const link = await within(await screen.findByRole('banner')).findByRole('link', { name: en['studio.title'] });
    expect(link.getAttribute('href')).toBe('/studio/workflows');
  });

  it('the header has no studio link when the workflow list is refused', async () => {
    mock(SYSTEM_ADMIN);
    let asked = 0;
    server.use(
      http.get('*/authoring/workflows', () => {
        asked++;
        return HttpResponse.json({ error: { code: '0x80040220', message: 'no' } }, { status: 403 });
      }),
    );
    render(<App initialEntries={['/manage']} />);
    await screen.findByText('Acme Dev');
    await vi.waitFor(() => expect(asked).toBe(1));
    expect(within(screen.getByRole('banner')).queryByRole('link', { name: en['studio.title'] })).toBeNull();
  });

  it('Admin_nav_has_environment_entry_and_header_shows_environment_and_user', async () => {
    mock(SYSTEM_ADMIN);
    render(<App initialEntries={['/manage']} />);
    const nav = await screen.findByRole('navigation');
    expect(within(nav).getByText(en['nav.environment'])).toBeInTheDocument();
    expect(within(nav).getByText(en['nav.fieldSecurity'])).toBeInTheDocument();
    expect(within(nav).getByText(en['nav.hierarchySecurity'])).toBeInTheDocument();
    expect(within(nav).getByText(en['nav.accessTeamTemplates'])).toBeInTheDocument();
    expect(await screen.findByText('Acme Dev')).toBeInTheDocument();
    expect(screen.getByText('Ada Admin')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: en['auth.signOut'] })).toBeInTheDocument();
    // /manage redirects to the tables section; the Audit section (plan 13) is in the navigation.
    expect(await screen.findByRole('heading', { name: en['nav.tables'] })).toBeInTheDocument();
    // The tables page reads the table list on mount: wait for it so no request is in flight when the test ends.
    expect(await screen.findByText(en['tables.empty'])).toBeInTheDocument();
    expect(within(nav).getByText(en['nav.audit'])).toBeInTheDocument();
  });

  it('Header_shows_logo_product_and_environment_as_separate_elements', async () => {
    mock(SYSTEM_ADMIN);
    render(<App initialEntries={['/manage']} />);
    const banner = await screen.findByRole('banner');
    expect(within(banner).getByRole('img', { name: en['apps.header.logo'] })).toBeInTheDocument();
    expect(within(banner).getByText(en['app.name']).textContent).toBe(en['app.name']);
    expect((await within(banner).findByText('Acme Dev')).textContent).toBe('Acme Dev');
    expect(await screen.findByText(en['tables.empty'])).toBeInTheDocument();
  });

  it('Nav_marks_the_current_section', async () => {
    mock(SYSTEM_ADMIN);
    render(<App initialEntries={['/manage/tables']} />);
    const nav = await screen.findByRole('navigation', { name: en['nav.label'] });
    expect(within(nav).getByRole('link', { name: en['nav.tables'] })).toHaveAttribute('aria-current', 'page');
    expect(within(nav).getByRole('link', { name: en['nav.audit'] })).not.toHaveAttribute('aria-current', 'page');
    expect(await screen.findByText('Acme Dev')).toBeInTheDocument();
    expect(await screen.findByText(en['tables.empty'])).toBeInTheDocument();
  });

  it('Menu_button_toggles_the_navigation_and_a_choice_closes_it', async () => {
    mock(SYSTEM_ADMIN);
    render(<App initialEntries={['/manage/tables']} />);
    const menu = await screen.findByRole('button', { name: en['nav.menu'] });
    const nav = screen.getByRole('navigation', { name: en['nav.label'] });
    expect(menu).toHaveAttribute('aria-expanded', 'false');
    expect(menu).toHaveAttribute('aria-controls', nav.id);
    await userEvent.click(menu);
    expect(menu).toHaveAttribute('aria-expanded', 'true');
    await userEvent.click(within(nav).getByRole('link', { name: en['nav.audit'] }));
    expect(menu).toHaveAttribute('aria-expanded', 'false');
    expect(await screen.findByText('Acme Dev')).toBeInTheDocument();
  });

  it('Customizer_nav_has_no_environment_entry_and_url_shows_admin_only_page', async () => {
    mock(SYSTEM_CUSTOMIZER);
    render(<App initialEntries={['/manage/environment']} />);
    // The Header's organizations request must finish before the test ends: a request still in flight when
    // the handlers are reset is reported by MSW as unhandled.
    expect(await screen.findByText('Acme Dev')).toBeInTheDocument();
    expect(await screen.findByText(en['route.adminOnly.title'])).toBeInTheDocument();
    expect(screen.getByText(en['route.adminOnly.body'])).toBeInTheDocument();
    const nav = screen.getByRole('navigation');
    expect(within(nav).queryByText(en['nav.environment'])).toBeNull();
    // Field security is administered by System Administrators only (ruling F9).
    expect(within(nav).queryByText(en['nav.fieldSecurity'])).toBeNull();
    expect(within(nav).queryByText(en['nav.hierarchySecurity'])).toBeNull();
    expect(within(nav).getByText(en['nav.accessTeamTemplates'])).toBeInTheDocument();
    expect(within(nav).getByText(en['nav.tables'])).toBeInTheDocument();
  });

  it('Unknown_manage_route_shows_not_found_inside_shell', async () => {
    mock(SYSTEM_ADMIN);
    render(<App initialEntries={['/manage/nope']} />);
    // Wait for the Header's organizations request so none is in flight when the test ends.
    expect(await screen.findByText('Acme Dev')).toBeInTheDocument();
    expect(await screen.findByText(en['route.notFound.title'])).toBeInTheDocument();
    expect(screen.getByRole('navigation')).toBeInTheDocument();
  });

  it.each(['/main.aspx', '/apps'])('Main_aspx_and_apps_open_the_app_picker_outside_the_admin_shell %s', async (path) => {
    mock(SYSTEM_ADMIN);
    render(<App initialEntries={[path]} />);
    expect(await screen.findByText(en['apps.none'])).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: en['apps.title'] })).toBeInTheDocument();
    expect(screen.queryByRole('navigation')).toBeNull();
  });

  it('Header_environment_failure_is_notified_not_blank', async () => {
    mock(SYSTEM_ADMIN, () =>
      HttpResponse.json({ error: { code: '0x80040265', message: 'org read failed' } }, { status: 500 }) as unknown as Response,
    );
    render(<App initialEntries={['/manage']} />);
    expect(await screen.findByText('0x80040265')).toBeInTheDocument();
    expect(screen.getByText('org read failed')).toBeInTheDocument();
    // The tables page reads the table list on mount: wait for it so no request is in flight when the test ends.
    expect(await screen.findByText(en['tables.empty'])).toBeInTheDocument();
  });
});

function Probe({ e }: { e: unknown }) {
  const notify = useNotify();
  return (
    <>
      <button onClick={() => notify.error(e)}>fail</button>
      <button onClick={() => notify.success('Saved it')}>ok</button>
    </>
  );
}

describe('Notifications', () => {
  async function renderProbe(e: unknown) {
    const { NotificationsProvider } = await import('../notify/Notifications');
    const { FluentProvider, webLightTheme } = await import('@fluentui/react-components');
    render(
      <FluentProvider theme={webLightTheme}>
        <NotificationsProvider>
          <Probe e={e} />
        </NotificationsProvider>
      </FluentProvider>,
    );
  }

  it('Error_notification_shows_code_and_message', async () => {
    await renderProbe(new XrmError(403, 'Not allowed here', '0x80040220'));
    await userEvent.click(screen.getByRole('button', { name: 'fail' }));
    expect(await screen.findByText('0x80040220')).toBeInTheDocument();
    expect(screen.getByText('Not allowed here')).toBeInTheDocument();
  });

  it('Error_notification_for_XrmError_without_code_shows_server_message', async () => {
    await renderProbe(new XrmError(500, 'No code here'));
    await userEvent.click(screen.getByRole('button', { name: 'fail' }));
    expect(await screen.findByText('No code here')).toBeInTheDocument();
  });

  it('Error_notification_for_plain_error_shows_its_message', async () => {
    await renderProbe(new Error('boom'));
    await userEvent.click(screen.getByRole('button', { name: 'fail' }));
    expect(await screen.findByText('boom')).toBeInTheDocument();
  });

  it('Success_notification_shows_message', async () => {
    await renderProbe(null);
    await userEvent.click(screen.getByRole('button', { name: 'ok' }));
    expect(await screen.findByText('Saved it')).toBeInTheDocument();
  });
});
