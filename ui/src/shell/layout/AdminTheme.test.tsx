import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PAGE_TEST_TIMEOUT } from '../../admin/forms/controlsTestSupport';
import { saveDevCredentials } from '../auth/devToken';
import { RootTheme, useActiveTheme } from '../../renderers/ThemeScope';
import { server } from '../../test/server';
import { App } from '../App';
import { en } from '../i18n/en';
import { xrmverseDarkTheme, xrmverseTheme } from '../theme';
import { AdminTheme, AdminThemeNotice } from './AdminTheme';
import { mockAdmin, PRESENTATION_DEF, providerOf, RAMP, SYSTEM_CUSTOMIZER, tokenOf, USER_ID } from './themeTestSupport';

/** When set, the environment overlay the admin reads (no settings request); otherwise the real hook runs. */
const overlay = vi.hoisted(() => ({ name: undefined as string | undefined, error: undefined as string | undefined, active: false }));
vi.mock('./useEnvironmentOverlay', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./useEnvironmentOverlay')>();
  return {
    ...actual,
    useEnvironmentOverlay: () => (overlay.active ? { name: overlay.name, error: overlay.error, reload: () => {} } : actual.useEnvironmentOverlay()),
  };
});

const LOOK = 'x_/look.json';
/** The environment's presentation is {@link LOOK}, which answers `overlay` (JSON), or `respond()` when given. */
const withOverlay = (overlay: object, respond?: () => Response) =>
  mockAdmin({
    rows: [{ organizationsettingid: 'cccccccc-0000-0000-0000-000000000001', _settingdefinitionid_value: PRESENTATION_DEF, value: LOOK }],
    overlays: { [LOOK]: respond ?? (() => HttpResponse.json(overlay) as unknown as Response) },
  });

beforeEach(() => {
  sessionStorage.clear();
  saveDevCredentials({ signingKey: 'k'.repeat(40), objectId: USER_ID, name: 'Ada Admin' });
});
afterEach(() => {
  sessionStorage.clear();
  overlay.active = false;
  overlay.error = undefined;
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

const mainProvider = async () => providerOf(await screen.findByRole('main'));

describe('AdminTheme', { timeout: PAGE_TEST_TIMEOUT }, () => {
  it('the overlay ramp and font apply to the web admin', async () => {
    withOverlay({ version: 1, theme: { brandRamp: RAMP, fontFamily: 'Contoso Sans' } });
    render(<App initialEntries={['/manage/tables']} />);
    await screen.findByText(en['tables.empty']);
    await waitFor(async () => expect(tokenOf(await mainProvider(), 'colorBrandBackground')?.toLowerCase()).toBe(RAMP[80].toLowerCase()));
    expect(tokenOf(await mainProvider(), 'fontFamilyBase')).toContain('Contoso Sans');
  });

  it('a compact overlay shrinks the spacing tokens', async () => {
    withOverlay({ version: 1, theme: { density: 'compact' } });
    render(<App initialEntries={['/manage/tables']} />);
    await screen.findByText(en['tables.empty']);
    const comfortable = parseInt(xrmverseTheme.spacingHorizontalL, 10);
    await waitFor(async () => expect(parseInt(tokenOf(await mainProvider(), 'spacingHorizontalL') ?? '', 10)).toBeLessThan(comfortable));
  });

  it('whiteLabel does not apply: the header keeps the xrmverse name and logo', async () => {
    withOverlay({ version: 1, theme: { brandRamp: RAMP }, whiteLabel: { name: 'Contoso Portal', logo: 'x_/logo.svg' } });
    render(<App initialEntries={['/manage/tables']} />);
    await screen.findByText(en['tables.empty']);
    await waitFor(async () => expect(tokenOf(await mainProvider(), 'colorBrandBackground')?.toLowerCase()).toBe(RAMP[80].toLowerCase()));
    const banner = screen.getByRole('banner');
    expect(within(banner).getByText(en['app.name'])).toBeInTheDocument();
    expect(within(banner).getByRole('img', { name: en['apps.header.logo'] })).not.toHaveAttribute('src', expect.stringContaining('x_/logo.svg'));
    expect(screen.queryByText('Contoso Portal')).not.toBeInTheDocument();
  });

  it('the overlay dark scheme gives the dark theme inside /manage', async () => {
    withOverlay({ version: 1, theme: { colorScheme: 'dark' } });
    render(<App initialEntries={['/manage/tables']} />);
    await screen.findByText(en['tables.empty']);
    await waitFor(async () => expect(tokenOf(await mainProvider(), 'colorNeutralBackground1')).toBe(xrmverseDarkTheme.colorNeutralBackground1));
  });

  it('an overlay that cannot be read keeps the xrmverse theme and shows a dismissible warning', async () => {
    withOverlay({}, () => new HttpResponse(null, { status: 404 }) as unknown as Response);
    render(<App initialEntries={['/manage/tables']} />);
    await screen.findByText(en['tables.empty']);
    const notice = await screen.findByText(/x_\/look\.json could not be read: the server answered 404/);
    // Under the header, not inside the page.
    expect(within(screen.getByRole('main')).queryByText(/could not be read/)).not.toBeInTheDocument();
    expect(screen.getByRole('banner').compareDocumentPosition(notice) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(tokenOf(await mainProvider(), 'colorBrandBackground')).toBe(xrmverseTheme.colorBrandBackground);
    await userEvent.click(screen.getByRole('button', { name: en['notify.dismiss'] }));
    expect(screen.queryByText(/could not be read/)).not.toBeInTheDocument();
  });

  it('no setting: the xrmverse theme and no notice, and no overlay is requested', async () => {
    // Restored by vi.restoreAllMocks() after each test.
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    mockAdmin({ definitions: false });
    render(<App initialEntries={['/manage/tables']} />);
    await screen.findByText(en['tables.empty']);
    expect(tokenOf(await mainProvider(), 'colorBrandBackground')).toBe(xrmverseTheme.colorBrandBackground);
    expect(screen.queryByRole('button', { name: en['notify.dismiss'] })).not.toBeInTheDocument();
    const urls = fetchSpy.mock.calls.map((c) => String(c[0] instanceof Request ? c[0].url : c[0]));
    expect(urls.some((u) => u.includes('organizationsettings'))).toBe(false);
    expect(urls.some((u) => u.includes('/WebResources/'))).toBe(false);
  });

  it('a System Customizer reads no look setting and keeps the xrmverse theme, with no notice', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    mockAdmin({
      role: SYSTEM_CUSTOMIZER,
      rows: [{ organizationsettingid: 'cccccccc-0000-0000-0000-000000000001', _settingdefinitionid_value: PRESENTATION_DEF, value: LOOK }],
      overlays: { [LOOK]: () => HttpResponse.json({ version: 1, theme: { brandRamp: RAMP } }) as unknown as Response },
    });
    render(<App initialEntries={['/manage/tables']} />);
    await screen.findByText(en['tables.empty']);
    await new Promise((r) => setTimeout(r, 50));
    expect(tokenOf(await mainProvider(), 'colorBrandBackground')).toBe(xrmverseTheme.colorBrandBackground);
    expect(screen.queryByRole('button', { name: en['notify.dismiss'] })).not.toBeInTheDocument();
    const urls = fetchSpy.mock.calls.map((c) => String(c[0] instanceof Request ? c[0].url : c[0]));
    expect(urls.some((u) => u.includes('settingdefinitions') || u.includes('organizationsettings') || u.includes('/WebResources/'))).toBe(false);
  });

  it('a 500 reading the setting keeps the xrmverse theme and shows the error as a dismissible notice', async () => {
    mockAdmin();
    server.use(http.get('*/api/data/v9.2/organizationsettings', () => HttpResponse.json({ error: { code: '0x80040216', message: 'Database unavailable' } }, { status: 500 })));
    render(<App initialEntries={['/manage/tables']} />);
    await screen.findByText(en['tables.empty']);
    const notice = await screen.findByText(/could not be read; the web admin uses the xrmverse theme\. Database unavailable/);
    expect(within(screen.getByRole('main')).queryByText(/Database unavailable/)).not.toBeInTheDocument();
    expect(screen.getByRole('banner').compareDocumentPosition(notice) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(tokenOf(await mainProvider(), 'colorBrandBackground')).toBe(xrmverseTheme.colorBrandBackground);
    await userEvent.click(screen.getByRole('button', { name: en['notify.dismiss'] }));
    expect(screen.queryByText(/Database unavailable/)).not.toBeInTheDocument();
  });

  it('an error with an empty detail shows the notice without a trailing space', () => {
    overlay.active = true;
    overlay.name = undefined;
    overlay.error = '';
    render(
      <RootTheme>
        <AdminTheme>
          <AdminThemeNotice />
        </AdminTheme>
      </RootTheme>,
    );
    const notice = screen.getByText(/could not be read; the web admin uses the xrmverse theme\./);
    expect(notice.textContent).toBe(notice.textContent?.trim());
  });

  it('a 403 reading the setting (no read access) keeps the xrmverse theme with no notice', async () => {
    let denied = false;
    mockAdmin();
    server.use(
      http.get('*/api/data/v9.2/organizationsettings', () => {
        denied = true;
        return HttpResponse.json({ error: { code: '0x80040220', message: 'Principal user is missing prvReadorganizationsetting' } }, { status: 403 });
      }),
    );
    render(<App initialEntries={['/manage/tables']} />);
    await screen.findByText(en['tables.empty']);
    await waitFor(() => expect(denied).toBe(true));
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByText(/prvReadorganizationsetting/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: en['notify.dismiss'] })).not.toBeInTheDocument();
    expect(tokenOf(await mainProvider(), 'colorBrandBackground')).toBe(xrmverseTheme.colorBrandBackground);
  });
});

/** What `useActiveTheme` gives inside the admin theme (what PCF controls receive, R7). */
function Probe() {
  const { dark, brand } = useActiveTheme();
  return <output data-testid="probe" data-dark={String(dark)} data-brand80={brand?.[80] ?? ''} />;
}

/** The admin theme alone, with the environment overlay `name` answering `json`, and the operating system's scheme. */
function renderAdminTheme(json: object | undefined, osDark: boolean) {
  vi.stubGlobal('matchMedia', (query: string) => ({ matches: osDark && query === '(prefers-color-scheme: dark)', media: query, addEventListener: () => {}, removeEventListener: () => {} }));
  overlay.active = true;
  overlay.name = json ? LOOK : undefined;
  if (json) server.use(http.get(`*/WebResources/${LOOK}`, () => HttpResponse.json(json)));
  render(
    <RootTheme>
      <AdminTheme>
        <Probe />
      </AdminTheme>
    </RootTheme>,
  );
  return () => screen.getByTestId('probe');
}

describe('AdminTheme: switching overlays', () => {
  const OTHER = 'x_/other.json';
  const RAMP_B = { ...RAMP, 80: RAMP[80] === '#123456' ? '#654321' : '#123456' } as typeof RAMP;
  const brandOf = () => screen.getByTestId('probe').getAttribute('data-brand80');

  it('keeps the previous theme while the next overlay loads, then applies it; a failure falls back with the notice', async () => {
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: false, media: query, addEventListener: () => {}, removeEventListener: () => {} }));
    overlay.active = true;
    overlay.name = LOOK;
    let release!: () => void;
    const held = new Promise<void>((resolve) => (release = resolve));
    server.use(
      http.get(`*/WebResources/${LOOK}`, () => HttpResponse.json({ version: 1, theme: { brandRamp: RAMP } })),
      http.get(`*/WebResources/${OTHER}`, async () => {
        await held;
        return HttpResponse.json({ version: 1, theme: { brandRamp: RAMP_B } });
      }),
    );
    const tree = () => (
      <RootTheme>
        <AdminTheme>
          <Probe />
        </AdminTheme>
      </RootTheme>
    );
    const { rerender } = render(tree());
    await waitFor(() => expect(brandOf()).toBe(RAMP[80]));
    overlay.name = OTHER;
    rerender(tree());
    await new Promise((r) => setTimeout(r, 50));
    expect(brandOf()).toBe(RAMP[80]);
    release();
    await waitFor(() => expect(brandOf()).toBe(RAMP_B[80]));
  });

  it('when the next overlay cannot be read, the xrmverse theme applies with the notice', async () => {
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: false, media: query, addEventListener: () => {}, removeEventListener: () => {} }));
    overlay.active = true;
    overlay.name = LOOK;
    server.use(
      http.get(`*/WebResources/${LOOK}`, () => HttpResponse.json({ version: 1, theme: { brandRamp: RAMP } })),
      http.get(`*/WebResources/${OTHER}`, () => new HttpResponse(null, { status: 404 })),
    );
    const tree = () => (
      <RootTheme>
        <AdminTheme>
          <Probe />
          <AdminThemeNotice />
        </AdminTheme>
      </RootTheme>
    );
    const { rerender } = render(tree());
    await waitFor(() => expect(brandOf()).toBe(RAMP[80]));
    overlay.name = OTHER;
    rerender(tree());
    await waitFor(() => expect(brandOf()).not.toBe(RAMP[80]));
    expect(await screen.findByText(/other\.json could not be read: the server answered 404/)).toBeInTheDocument();
  });
});

describe('AdminTheme: useActiveTheme inside the admin (R3, R7)', () => {
  it('the overlay dark scheme beats a light operating system', async () => {
    const probe = renderAdminTheme({ version: 1, theme: { colorScheme: 'dark', brandRamp: RAMP } }, false);
    await waitFor(() => expect(probe()).toHaveAttribute('data-dark', 'true'));
    expect(probe()).toHaveAttribute('data-brand80', RAMP[80]);
  });

  it('the overlay light scheme beats a dark operating system', async () => {
    const probe = renderAdminTheme({ version: 1, theme: { colorScheme: 'light', brandRamp: RAMP } }, true);
    await waitFor(() => expect(probe()).toHaveAttribute('data-brand80', RAMP[80]));
    expect(probe()).toHaveAttribute('data-dark', 'false');
  });

  it('an overlay with no scheme follows the operating system', async () => {
    const probe = renderAdminTheme({ version: 1, theme: { brandRamp: RAMP } }, true);
    await waitFor(() => expect(probe()).toHaveAttribute('data-brand80', RAMP[80]));
    expect(probe()).toHaveAttribute('data-dark', 'true');
  });

  it('with no overlay the root theme applies: the operating system scheme and the xrmverse brand', () => {
    const probe = renderAdminTheme(undefined, true);
    expect(probe()).toHaveAttribute('data-dark', 'true');
    expect(probe()).not.toHaveAttribute('data-brand80', RAMP[80]);
  });
});
