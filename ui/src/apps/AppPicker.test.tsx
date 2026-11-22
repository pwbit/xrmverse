import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { server } from '../test/server';
import { AppTile } from './AppTile';
import { afterEach, describe, expect, it } from 'vitest';
import { App } from '../shell/App';
import { en } from '../shell/i18n/en';
import { setUiLanguage } from '../shell/i18n/t';
import { idChunks, MAX_FILTER_LENGTH } from '../shell/auth/roles';
import { mockApps, ROLE_ADMIN, ROLE_OTHER, ROLE_SALES, ROLE_TEAM, SITEMAP_ID, titled } from './testSupport';

const SALES = { appmoduleid: '11111111-0000-0000-0000-000000000001', name: 'Sales Hub', description: 'Sell things', roles: [ROLE_SALES] };
const FIELD = { appmoduleid: '11111111-0000-0000-0000-000000000002', name: 'Field Service', roles: [ROLE_TEAM, ROLE_OTHER] };
const HR = { appmoduleid: '11111111-0000-0000-0000-000000000003', name: 'People', roles: [ROLE_OTHER] };
const NO_ROLES = { appmoduleid: '11111111-0000-0000-0000-000000000004', name: 'Unassigned', roles: [] };
const ALL = [SALES, FIELD, HR, NO_ROLES];

afterEach(() => {
  sessionStorage.clear();
  setUiLanguage(undefined);
});

async function appLinks() {
  const list = await screen.findByRole('list', { name: en['apps.title'] });
  return within(list)
    .getAllByRole('link')
    .map((a) => [a.textContent, a.getAttribute('href')]);
}

describe('AppPicker', () => {
  it('app picker lists only apps sharing a role with the user', async () => {
    const requests = mockApps({ apps: ALL, userRoles: [ROLE_SALES], teamRoles: [ROLE_TEAM] });
    render(<App initialEntries={['/apps']} />);
    const links = await appLinks();
    // The app roles are read for the caller's roles only, not for every app.
    expect(requests.filter((r) => r.startsWith('appmodulerolescollection'))).toEqual([
      `appmodulerolescollection?$select=appmoduleid,roleid&$filter=roleid eq ${ROLE_SALES} or roleid eq ${ROLE_TEAM}`,
    ]);
    expect(links.map(([text]) => text)).toEqual([
      expect.stringContaining('Field Service'),
      expect.stringContaining('Sales Hub'),
    ]);
    expect(links[1]![1]).toBe(`/main.aspx?appid=${SALES.appmoduleid}`);
    expect(screen.getByText('Sell things')).toBeInTheDocument();
    expect(screen.queryByText('People')).toBeNull();
    // The picker is outside the web admin: no admin navigation.
    expect(screen.queryByRole('navigation', { name: en['nav.label'] })).toBeNull();
  });

  it('renders each app as a centred banner tile', async () => {
    mockApps({ apps: [SALES], userRoles: [ROLE_SALES] });
    const { container } = render(<App initialEntries={['/apps']} />);
    const list = await screen.findByRole('list', { name: en['apps.title'] });
    const item = within(list).getByRole('listitem');
    expect(item.querySelector('.fui-Card')).not.toBeNull();
    expect(within(item).getByRole('link', { name: /Sales Hub/ })).toBeInTheDocument();
    const banner = within(item).getByTestId('app-tile-banner');
    // The default image is the decorative xrmverse mark.
    expect(banner.querySelector('svg')!.getAttribute('aria-hidden')).toBe('true');
    expect(item.querySelector('.fui-Avatar')).toBeNull();
    expect(getComputedStyle(within(item).getByTestId('app-tile-text')).textAlign).toBe('center');
    expect(container.querySelectorAll('[role="img"][aria-label="xrmverse"]').length).toBe(1); // the header's logo only
  });

  it('the banner uses the brand tokens', async () => {
    mockApps({ apps: [SALES], userRoles: [ROLE_SALES] });
    render(<App initialEntries={['/apps']} />);
    const banner = await screen.findByTestId('app-tile-banner');
    const style = banner.getAttribute('style') ?? '';
    expect(style).toContain('linear-gradient(135deg');
    expect(style).toContain('var(--colorBrandBackgroundPressed)');
    expect(style).toContain('var(--colorBrandBackground)');
    expect(style).not.toMatch(/#|rgb/);
  });

  it('long names keep the full name as tooltip', async () => {
    const long = 'A very long application name that cannot possibly fit on a single line of a tile';
    mockApps({ apps: [{ ...SALES, name: long }], userRoles: [ROLE_SALES] });
    render(<App initialEntries={['/apps']} />);
    const link = await screen.findByRole('link', { name: long });
    expect(link.getAttribute('title')).toBe(long);
  });

  it('AppTile takes its own icon in place of the mark', () => {
    render(
      <MemoryRouter>
        <AppTile name="Workflow studio" to="/studio" icon={<svg data-testid="own-icon" />} />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('own-icon')).toBeInTheDocument();
    expect(screen.getByTestId('app-tile-banner').querySelector('[data-testid="app-tile-mark"]')).toBeNull();
    expect(screen.getByRole('link', { name: 'Workflow studio' }).getAttribute('href')).toBe('/studio');
  });

  it('the Workflow studio tile shows when the workflow list is readable', async () => {
    mockApps({ apps: [SALES], userRoles: [ROLE_SALES] });
    server.use(http.get('*/authoring/workflows', () => HttpResponse.json([])));
    render(<App initialEntries={['/apps']} />);
    const tile = await screen.findByRole('link', { name: en['studio.title'] });
    expect(tile.getAttribute('href')).toBe('/studio/workflows');
    const banner = within(tile.closest('.fui-Card') as HTMLElement).getByTestId('app-tile-banner');
    expect(banner.querySelector('[data-testid="app-tile-mark"]')).toBeNull();
    // Not one of the apps.
    expect((await appLinks()).map(([text]) => text)).toEqual([expect.stringContaining('Sales Hub')]);
  });

  it('the Workflow studio tile is hidden when the workflow list is refused', async () => {
    mockApps({ apps: [SALES], userRoles: [ROLE_SALES] });
    let asked = 0;
    server.use(
      http.get('*/authoring/workflows', () => {
        asked++;
        return HttpResponse.json({ error: { code: '0x80040220', message: 'missing prvReadWorkflow' } }, { status: 403 });
      }),
    );
    render(<App initialEntries={['/apps']} />);
    await appLinks();
    await waitFor(() => expect(asked).toBe(1));
    expect(screen.queryByRole('link', { name: en['studio.title'] })).toBeNull();
  });

  it('a system administrator sees every app', async () => {
    mockApps({ apps: ALL, userRoles: [ROLE_ADMIN] });
    render(<App initialEntries={['/apps']} />);
    expect((await appLinks()).map(([text]) => text)).toEqual([
      expect.stringContaining('Field Service'),
      expect.stringContaining('People'),
      expect.stringContaining('Sales Hub'),
      expect.stringContaining('Unassigned'),
    ]);
  });

  it('an inactive app is not listed, not even to a system administrator', async () => {
    const requests = mockApps({ apps: [SALES, { ...HR, statecode: 1 }], userRoles: [ROLE_ADMIN] });
    render(<App initialEntries={['/apps']} />);
    expect((await appLinks()).map(([text]) => text)).toEqual([expect.stringContaining('Sales Hub')]);
    expect(requests.find((r) => r.startsWith('appmodules?'))).toContain('$filter=statecode eq 0');
  });

  it('an app without roles is hidden from non-admins', async () => {
    mockApps({ apps: [NO_ROLES, SALES], userRoles: [ROLE_SALES, ROLE_OTHER] });
    render(<App initialEntries={['/apps']} />);
    expect((await appLinks()).map(([text]) => text)).toEqual([expect.stringContaining('Sales Hub')]);
    expect(screen.queryByText('Unassigned')).toBeNull();
  });

  it('a user without apps is told so', async () => {
    mockApps({ apps: [HR], userRoles: [ROLE_SALES] });
    render(<App initialEntries={['/apps']} />);
    expect(await screen.findByText(en['apps.none'])).toBeInTheDocument();
  });

  it('only tool tiles: the Workflow studio tile shows without the no-apps sentence', async () => {
    mockApps({ apps: [HR], userRoles: [ROLE_SALES] });
    server.use(http.get('*/authoring/workflows', () => HttpResponse.json([])));
    render(<App initialEntries={['/apps']} />);
    expect(await screen.findByRole('link', { name: en['studio.title'] })).toBeInTheDocument();
    expect(screen.queryByText(en['apps.none'])).toBeNull();
  });

  it('the page fills the viewport with the page background token', async () => {
    mockApps({ apps: [SALES], userRoles: [ROLE_SALES] });
    render(<App initialEntries={['/apps']} />);
    await appLinks();
    const root = screen.getByTestId('app-picker');
    const classes = root.className.split(' ');
    const rule = Array.from(document.styleSheets)
      .flatMap((sheet) => Array.from(sheet.cssRules))
      .filter((r): r is CSSStyleRule => r instanceof CSSStyleRule && classes.some((c) => r.selectorText === `.${c}`))
      .map((r) => r.cssText.replace(/\s+/g, ''))
      .join('');
    expect(rule).toContain('min-height:100vh');
    expect(rule).toContain('background-color:var(--colorNeutralBackground1)');
  });

  it('the header shows the xrmverse name on the picker', async () => {
    mockApps({ apps: [SALES], userRoles: [ROLE_SALES] });
    render(<App initialEntries={['/apps']} />);
    const banner = await screen.findByRole('banner');
    expect(within(banner).getByText(en['app.name'])).toBeInTheDocument();
    await appLinks();
  });

  it('an apps read failure shows the error', async () => {
    mockApps({ apps: ALL, userRoles: [ROLE_SALES], fail: 'appmodules' });
    render(<App initialEntries={['/apps']} />);
    expect(await screen.findByText(/appmodules read failed/)).toBeInTheDocument();
    expect(screen.getByText(/appmodules read failed/).closest('.fui-MessageBar')).not.toBeNull();
    expect(screen.queryByRole('list', { name: en['apps.title'] })).toBeNull();
  });

  it('opening_an_app_does_not_reload_the_app_list', async () => {
    const siteMapXml = `<SiteMap><Area Id="a">${titled('Sales')}<Group Id="g">${titled('Customers')}<SubArea Id="s" Url="/x">${titled('X')}</SubArea></Group></Area></SiteMap>`;
    const requests = mockApps({ apps: [{ ...SALES, siteMapId: SITEMAP_ID }], userRoles: [ROLE_SALES], siteMapXml });
    render(<App initialEntries={['/apps']} />);
    await userEvent.click((await screen.findAllByRole('link', { name: /Sales Hub/ }))[0]);
    await screen.findByRole('navigation', { name: /Sales/ });
    expect(requests.filter((r) => r.startsWith('appmodules?'))).toHaveLength(1);
    expect(requests.filter((r) => r.startsWith('appmodulecomponents?'))).toHaveLength(1);
  });

  it('long_or_filter_is_split_over_several_requests', () => {
    const ids = Array.from({ length: 120 }, (_, i) => `cccccccc-0000-0000-0000-${String(i).padStart(12, '0')}`);
    const chunks = idChunks('roleid', ids);
    expect(chunks.length).toBeGreaterThan(1);
    expect(chunks.flat()).toEqual(ids);
    for (const chunk of chunks) expect(chunk.map((id) => `roleid eq ${id}`).join(' or ').length).toBeLessThanOrEqual(MAX_FILTER_LENGTH);
    expect(idChunks('roleid', ids.slice(0, 3))).toEqual([ids.slice(0, 3)]);
  });

  it('a user with many roles still sees the apps', async () => {
    const extra = Array.from({ length: 80 }, (_, i) => `cccccccc-0000-0000-0001-${String(i).padStart(12, '0')}`);
    const requests = mockApps({ apps: ALL, userRoles: [...extra, ROLE_SALES] });
    render(<App initialEntries={['/apps']} />);
    expect((await appLinks()).map(([text]) => text)).toEqual([expect.stringContaining('Sales Hub')]);
    const appRoleRequests = requests.filter((r) => r.startsWith('appmodulerolescollection'));
    expect(appRoleRequests.length).toBeGreaterThan(1);
    for (const r of appRoleRequests) expect(r.slice(r.indexOf('$filter=') + 8).length).toBeLessThanOrEqual(MAX_FILTER_LENGTH);
  });
});
