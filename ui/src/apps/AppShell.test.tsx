import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setLibraryLoader } from '../client-api/scriptHost';
import { sourceLoader } from '../client-api/testHost';
import { onPath } from '../admin/security/testSupport';
import { installMetadata, rawAttr, rawEntity } from '../admin/tables/tableTestSupport';
import { App } from '../shell/App';
import { en } from '../shell/i18n/en';
import { it as itStrings } from '../shell/i18n/it';
import { setUiLanguage, t } from '../shell/i18n/t';
import { server } from '../test/server';
import { mockApps, ROLE_SALES, SITEMAP_ID, titled, USER_ID, type AppsMock } from './testSupport';

const APP_ID = '11111111-0000-0000-0000-000000000001';
const SALES = { appmoduleid: APP_ID, name: 'Sales Hub', roles: [ROLE_SALES], siteMapId: SITEMAP_ID };

const SITEMAP = `<SiteMap>
  <Area Id="admin">${titled('Admin')}
    <Group Id="sec">${titled('Security')}<SubArea Id="roles" Entity="role">${titled('Roles')}</SubArea></Group>
  </Area>
  <Area Id="sales">${titled('Sales', 'Vendite')}
    <Group Id="customers">${titled('Customers', 'Clienti')}
      <SubArea Id="accounts" Entity="account">${titled('Accounts', 'Aziende')}</SubArea>
      <SubArea Id="contacts" Entity="contact">${titled('Contacts')}</SubArea>
      <SubArea Id="dash" Url="/main.aspx?pagetype=dashboard&amp;id=%7b37705131-eccf-f111-a05b-7ced8d2d53e4%7d&amp;type=system">${titled('Overview', 'Panoramica')}</SubArea>
    </Group>
  </Area>
</SiteMap>`;

const PRIVILEGES = ['prvReadAccount', 'prvReadContact'];

afterEach(() => {
  sessionStorage.clear();
  setUiLanguage(undefined);
});

const OVERVIEW = {
  systemformid: '37705131-eccf-f111-a05b-7ced8d2d53e4',
  name: 'Overview Dashboard',
  type: 0,
  objecttypecode: 'none',
  formxml:
    '<form><tabs><tab name="t"><labels/><columns><column width="100%"><sections><section name="s" showlabel="true" columns="1"><labels><label description="Summary" languagecode="1033"/></labels><rows/></section></sections></column></columns></tab></tabs></form>',
};

function renderAt(url: string, lcid = 1033, fail?: string) {
  mockApps({ apps: [SALES], userRoles: [ROLE_SALES], privileges: PRIVILEGES, siteMapXml: SITEMAP, lcid, fail, systemForms: [OVERVIEW] });
  return render(<App initialEntries={[url]} />);
}

describe('AppShell', () => {
  it('opening /main.aspx?appid=X lands on the first visible subarea', async () => {
    renderAt(`/main.aspx?appid=${APP_ID}`);
    const nav = await screen.findByRole('navigation', { name: 'Sales Hub' });
    // The Admin area (Roles needs prvReadRole) is trimmed away: Accounts is the first visible subarea.
    const current = await within(nav).findByRole('link', { current: 'page' });
    expect(current).toHaveTextContent('Accounts');
    expect(current).toHaveAttribute('href', `/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=account`);
    expect(within(nav).queryByText('Roles')).toBeNull();
    expect(within(nav).getByText('Customers')).toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'Accounts' })).toBeInTheDocument();
  });

  it('a dashboard subarea opens the dashboard page', async () => {
    renderAt(`/main.aspx?appid=${APP_ID}`);
    const nav = await screen.findByRole('navigation', { name: 'Sales Hub' });
    await userEvent.click(within(nav).getByRole('link', { name: 'Overview' }));
    expect(await screen.findByRole('heading', { name: 'Overview Dashboard' })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Summary' })).toBeInTheDocument();
    expect(screen.queryByText(/open in stage/)).toBeNull();
    expect(within(nav).getByRole('link', { current: 'page' })).toHaveTextContent('Overview');
  });

  it('pagetype=entityrecord without a table is not a record page', async () => {
    renderAt(`/main.aspx?appid=${APP_ID}&pagetype=entityrecord&id=1`);
    expect(await screen.findByText('The address does not name a table.')).toBeInTheDocument();
  });

  it.each([
    ['custom&name=x_page', 'Custom pages are not planned.'],
    ['search', 'Pages of type "search" are not available in this version.'],
  ])('pagetype=%s shows its placeholder', async (pageType, text) => {
    renderAt(`/main.aspx?appid=${APP_ID}&pagetype=${pageType}`);
    expect(await screen.findByText(text)).toBeInTheDocument();
  });

  it('pagetype=webresource opens the web resource full size inside the shell', async () => {
    server.use(http.get('*/WebResources/x_page.html', () => new HttpResponse('<html></html>', { headers: { 'Content-Type': 'text/html' } })));
    renderAt(`/main.aspx?appid=${APP_ID}&pagetype=webresource&webresourceName=x_page.html&data=a%3D1`);
    const frame = await screen.findByTitle('Web resource x_page.html');
    expect(frame).toHaveAttribute('src', '/WebResources/x_page.html?data=a%3D1');
    expect(await screen.findByRole('navigation', { name: 'Sales Hub' })).toBeInTheDocument();
  });

  it('pagetype=webresource with a name that leaves WebResources is refused', async () => {
    renderAt(`/main.aspx?appid=${APP_ID}&pagetype=webresource&webresourceName=..%2Fadmin`);
    expect(await screen.findByText('../admin is not a web resource name.')).toBeInTheDocument();
  });

  it('pagetype=webresource without a name says so', async () => {
    renderAt(`/main.aspx?appid=${APP_ID}&pagetype=webresource`);
    expect(await screen.findByText('The address does not name a web resource.')).toBeInTheDocument();
  });

  it('an unknown appid shows the no-access message with a link to /apps', async () => {
    renderAt('/main.aspx?appid=99999999-0000-0000-0000-000000000009');
    expect(await screen.findByText('This app does not exist or you do not have access to it.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: en['apps.backToApps'] })).toHaveAttribute('href', '/apps');
  });

  it('titles follow uilanguageid 1040, falling back to 1033', async () => {
    renderAt(`/main.aspx?appid=${APP_ID}&pagetype=entityrecord`, 1040);
    const nav = await screen.findByRole('navigation', { name: 'Sales Hub' });
    expect(await within(nav).findByText('Clienti')).toBeInTheDocument();
    expect(within(nav).getByRole('link', { name: 'Aziende' })).toBeInTheDocument();
    expect(within(nav).getByRole('link', { name: 'Contacts' })).toBeInTheDocument();
    // Shell strings follow the language too.
    expect(screen.getByText(itStrings['record.noTable']!)).toBeInTheDocument();
  });

  it('the header shows the xrmverse name and the app name', async () => {
    renderAt(`/main.aspx?appid=${APP_ID}`);
    // The header gets the app's name once the app is loaded (the navigation is shown at the same time).
    await screen.findByRole('navigation', { name: 'Sales Hub' });
    const banner = screen.getByRole('banner');
    expect(within(banner).getByText(en['app.name'])).toBeInTheDocument();
    expect(within(banner).getByText('Sales Hub')).toBeInTheDocument();
    expect(within(banner).getByRole('img', { name: 'xrmverse' })).toBeInTheDocument();
  });

  it('/main.aspx without an appid opens the app picker', async () => {
    renderAt('/main.aspx');
    expect(await screen.findByRole('list', { name: en['apps.title'] })).toBeInTheDocument();
  });

  it('leaving the app shell puts the shell strings back in English', async () => {
    const { unmount } = renderAt(`/main.aspx?appid=${APP_ID}&pagetype=entityrecord`, 1040);
    expect(await screen.findByText(itStrings['record.noTable']!)).toBeInTheDocument();
    expect(t('record.noTable')).toBe(itStrings['record.noTable']);
    unmount();
    expect(t('record.noTable')).toBe(en['record.noTable']);
  });

  it('a sitemap read failure shows the error', async () => {
    renderAt(`/main.aspx?appid=${APP_ID}`, 1033, 'sitemaps');
    const text = await screen.findByText(/sitemaps read failed/);
    expect(text.closest('.fui-MessageBar')).not.toBeNull();
    expect(screen.queryByRole('navigation', { name: 'Sales Hub' })).toBeNull();
  });

  describe('look and presentation (UI-2a)', () => {
    const LOOK = 'x_/look.json';
    const RAMP = Object.fromEntries([10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160].map((k, i) => [String(k), `#00${(i * 16).toString(16).padStart(2, '0')}40`]));
    const OVERLAY = { version: 1, theme: { brandRamp: RAMP }, whiteLabel: { name: 'Contoso Desk', logo: 'x_/logo.svg', favicon: 'x_/favicon.ico' } };

    /** The app with renderer `renderer` and the overlay `LOOK`; counts the overlay requests. */
    function renderLook(renderer: string, overlay: (() => Response) | undefined, over: Partial<AppsMock> = {}) {
      const reads = { overlay: 0 };
      mockApps({
        apps: [{ ...SALES, settings: { xrmverse_Renderer: renderer, xrmverse_Presentation: LOOK } }],
        userRoles: [ROLE_SALES],
        privileges: PRIVILEGES,
        siteMapXml: SITEMAP,
        ...over,
      });
      server.use(
        http.get(`*/WebResources/${LOOK}`, () => {
          reads.overlay++;
          return overlay ? overlay() : HttpResponse.json(OVERLAY);
        }),
      );
      const view = render(<App initialEntries={[`/main.aspx?appid=${APP_ID}&pagetype=entityrecord`]} />);
      return { ...view, reads };
    }

    afterEach(() => {
      document.head.querySelectorAll('link[rel="icon"]').forEach((l) => l.remove());
      document.title = '';
    });

    it('modern with an overlay shows the white-label name, logo, title and favicon, restored on leaving', async () => {
      const original = document.createElement('link');
      original.rel = 'icon';
      original.href = '/favicon.svg';
      document.head.appendChild(original);
      document.title = 'xrmverse';
      const { unmount } = renderLook('modern', undefined);
      await screen.findByRole('button', { name: en['apps.nav.toggle'] });
      const banner = screen.getByRole('banner');
      expect(within(banner).getByText('Contoso Desk')).toBeInTheDocument();
      const logo = within(banner).getByRole('img', { name: 'Contoso Desk' });
      expect(logo).toHaveAttribute('src', '/WebResources/x_/logo.svg');
      expect(within(banner).queryByRole('img', { name: 'xrmverse' })).toBeNull();
      expect(document.title).toContain('Contoso Desk');
      const icon = document.head.querySelector<HTMLLinkElement>('link[rel="icon"]');
      expect(icon?.getAttribute('href')).toBe('/WebResources/x_/favicon.ico');
      unmount();
      expect(document.head.querySelector<HTMLLinkElement>('link[rel="icon"]')?.getAttribute('href')).toBe('/favicon.svg');
      expect(document.title).toBe('xrmverse');
    });

    it('the user choosing classic keeps the classic header and never reads the overlay', async () => {
      const { reads } = renderLook('modern', undefined, { userRenderer: 'classic' });
      await screen.findByRole('navigation', { name: 'Sales Hub' });
      const banner = screen.getByRole('banner');
      expect(within(banner).getByText('Sales Hub')).toBeInTheDocument();
      expect(within(banner).getByRole('img', { name: 'xrmverse' })).toBeInTheDocument();
      expect(screen.queryByText('Contoso Desk')).toBeNull();
      expect(reads.overlay).toBe(0);
    });

    it('an overlay that cannot be read gives the notice and the modern frame with the default look', async () => {
      renderLook('modern', () => new HttpResponse(null, { status: 404 }) as unknown as Response);
      const reason = t('presentation.reason.status', { status: 404 });
      expect(await screen.findByText(t('presentation.unreadable', { name: LOOK, reason }))).toBeInTheDocument();
      expect(screen.getByText(t('presentation.unreadable', { name: LOOK, reason })).closest('.fui-MessageBar')).not.toBeNull();
      expect(screen.getByRole('button', { name: en['apps.nav.toggle'] })).toBeInTheDocument();
      const banner = screen.getByRole('banner');
      expect(within(banner).getByText('Sales Hub')).toBeInTheDocument();
      expect(within(banner).getByRole('img', { name: 'xrmverse' })).toBeInTheDocument();
    });

    it('an overlay of another version gives the version notice', async () => {
      renderLook('modern', () => HttpResponse.json({ version: 2 }) as unknown as Response);
      expect(await screen.findByText(t('presentation.version', { v: 2 }))).toBeInTheDocument();
      expect(screen.getByRole('button', { name: en['apps.nav.toggle'] })).toBeInTheDocument();
    });

    it('an overlay without a version gives its own notice', async () => {
      renderLook('modern', () => HttpResponse.json({}) as unknown as Response);
      expect(await screen.findByText(t('presentation.noVersion'))).toBeInTheDocument();
      expect(screen.queryByText(/version undefined/)).toBeNull();
    });

    it('overlay warnings go to the console', async () => {
      const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
      try {
        renderLook('modern', () => HttpResponse.json({ ...OVERLAY, extra: true }) as unknown as Response);
        await screen.findByText('Contoso Desk');
        expect(warn).toHaveBeenCalledWith('[xrmverse presentation]', 'Unknown key extra');
      } finally {
        warn.mockRestore();
      }
    });

    it('an unknown look gives classic and the notice', async () => {
      const { reads } = renderLook('fancy', undefined);
      expect(await screen.findByText("The look 'fancy' is not available in this version; the classic look is used.")).toBeInTheDocument();
      expect(screen.getByRole('navigation', { name: 'Sales Hub' })).toBeInTheDocument();
      expect(reads.overlay).toBe(0);
    });

    it('the unknown-look notice is translated', async () => {
      renderLook('fancy', undefined, { lcid: 1040 });
      expect(await screen.findByText("Il look 'fancy' non è disponibile in questa versione; viene usato il look classico.")).toBeInTheDocument();
    });
  });
});

type XrmVerseLike = { App: { sidePanes: { createPane(o?: unknown): Promise<{ navigate(p: unknown): Promise<void> }> } } };
const xrmVerse = () => (window as unknown as { XrmVerse?: XrmVerseLike }).XrmVerse;
const xrm = () => (window as unknown as { Xrm: { Navigation: { openForm(o: unknown): Promise<unknown> } } }).Xrm;

describe('XrmVerse on the app shell (X.10)', () => {
  const APP2 = '11111111-0000-0000-0000-000000000002';
  const ACCOUNT = 'bbbbbbbb-0000-0000-0000-000000000002';
  const accountTable = rawEntity(
    'account',
    'accounts',
    [rawAttr('accountid', 'Uniqueidentifier', { IsPrimaryId: true }), rawAttr('name', 'String', { IsPrimaryName: true, display: 'Account Name' })],
    { SchemaName: 'Account', PrimaryNameAttribute: 'name', display: 'Account' },
  );
  const FORM = {
    systemformid: '10000000-0000-0000-0000-000000000001',
    name: 'Main',
    objecttypecode: 'account',
    type: 2,
    isdefault: true,
    formactivationstate: 1,
    formxml:
      '<form><tabs><tab name="general"><labels><label description="General" languagecode="1033"/></labels><columns><column width="100%"><sections><section name="s" showlabel="false" columns="1"><rows><row><cell id="{n}"><labels><label description="Name" languagecode="1033"/></labels><control id="name" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="name"/></cell></row></rows></section></sections></column></columns></tab></tabs><formLibraries><Library name="probe.js"/></formLibraries></form>',
  };
  const setup = (entry: string, onRouter?: (r: { navigate(to: string): unknown }) => void) => {
    mockApps({
      apps: [SALES, { ...SALES, appmoduleid: APP2, name: 'Service Hub' }],
      userRoles: [ROLE_SALES],
      privileges: PRIVILEGES,
      siteMapXml: SITEMAP,
      systemForms: [OVERVIEW],
    });
    installMetadata([accountTable]);
    server.use(
      http.get('*/api/data/v9.2/systemforms', () => HttpResponse.json({ value: [FORM] })),
      onPath('get', `systemusers(${USER_ID})/Microsoft.Dynamics.CRM.RetrievePrincipalAccess(Target=@t)`, () => HttpResponse.json({ AccessRights: 'ReadAccess' })),
      onPath('get', `accounts(${ACCOUNT})`, () => HttpResponse.json({ accountid: ACCOUNT, name: 'Contoso' })),
      http.get('*/WebResources/op_/pane.html', () => new HttpResponse('<html></html>', { headers: { 'Content-Type': 'text/html' } })),
    );
    return render(<App initialEntries={[entry]} onRouter={onRouter as never} />);
  };
  afterEach(() => setLibraryLoader(undefined));

  it('window.XrmVerse is set in an app and deleted after unmount', async () => {
    const { unmount } = setup(`/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=account`);
    await screen.findByRole('navigation', { name: 'Sales Hub' });
    expect(xrmVerse()).toBeDefined();
    expect(xrmVerse()?.App.sidePanes).toBeDefined();
    // Xrm.App and Xrm.Panel stay not available.
    expect(() => (xrm() as unknown as { App: unknown }).App).toThrow('Xrm.App is not available in this version');
    expect(() => (xrm() as unknown as { Panel: unknown }).Panel).toThrow('Xrm.Panel is not available in this version');
    unmount();
    expect(xrmVerse()).toBeUndefined();
    expect((window as unknown as { Xrm?: unknown }).Xrm).toBeUndefined();
  });

  it('is not defined outside an app', async () => {
    mockApps({ apps: [SALES], userRoles: [ROLE_SALES], privileges: PRIVILEGES, siteMapXml: SITEMAP });
    render(<App initialEntries={['/main.aspx']} />);
    await screen.findByRole('list', { name: en['apps.title'] });
    expect(xrmVerse()).toBeUndefined();
  });

  it('closing the shell disposes the stores', async () => {
    const { unmount } = setup(`/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=account`);
    await screen.findByRole('navigation', { name: 'Sales Hub' });
    const old = xrmVerse()!;
    unmount();
    await expect(old.App.sidePanes.createPane()).rejects.toThrow('The app was closed');
  });

  it('switching to another app disposes the panes of the old one and gives the new one its own', async () => {
    let router!: { navigate(to: string): unknown };
    setup(`/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=account`, (r) => (router = r));
    await screen.findByRole('navigation', { name: 'Sales Hub' });
    const old = xrmVerse()!;
    await act(async () => void router.navigate(`/main.aspx?appid=${APP2}&pagetype=entitylist&etn=account`));
    await screen.findByRole('navigation', { name: 'Service Hub' });
    expect(xrmVerse()).toBeDefined();
    expect(xrmVerse()).not.toBe(old);
    await expect(old.App.sidePanes.createPane()).rejects.toThrow('The app was closed');
    await act(async () => void (await expect(xrmVerse()!.App.sidePanes.createPane()).resolves.toBeDefined()));
  });

  it('a script host window gets XrmVerse', async () => {
    const seen: unknown[] = [];
    const inner = sourceLoader({ 'probe.js': 'function noop() {}' });
    setLibraryLoader(async (win, url, name) => {
      seen.push((win as unknown as { XrmVerse?: unknown }).XrmVerse);
      return inner(win, url, name);
    });
    setup(`/main.aspx?appid=${APP_ID}&pagetype=entityrecord&etn=account&id=${ACCOUNT}`);
    await screen.findByLabelText('Name');
    await waitFor(() => expect(seen).toHaveLength(1));
    expect(seen[0]).toBe(xrmVerse());
  });

  it('pane navigation keeps pane mounted', async () => {
    setup(`/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=account`);
    await screen.findByRole('navigation', { name: 'Sales Hub' });
    await act(async () => {
      const pane = await xrmVerse()!.App.sidePanes.createPane({ title: 'Chat' });
      void pane.navigate({ pageType: 'webresource', webresourceName: 'op_/pane.html' });
    });
    const frame = await screen.findByTitle('Web resource op_/pane.html');
    await act(async () => void (await xrm().Navigation.openForm({ entityName: 'account', entityId: ACCOUNT })));
    expect(await screen.findByLabelText('Name')).toHaveValue('Contoso');
    expect(screen.getByTitle('Web resource op_/pane.html')).toBe(frame);
    expect(frame.isConnected).toBe(true);
  });

  it('a personalization save that keeps the look keeps the frame and the pane IFrames mounted', async () => {
    const reads = { settings: 0 };
    setup(`/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=account`);
    server.use(
      onPath('patch', `usersettingscollection(${USER_ID})`, () => new HttpResponse(null, { status: 204 })),
      http.get('*/api/data/v9.2/usersettingscollection*', () => {
        reads.settings++;
        return HttpResponse.json({ systemuserid: USER_ID, uilanguageid: 1033, xrmverse_colorscheme: 2 });
      }),
    );
    await screen.findByRole('navigation', { name: 'Sales Hub' });
    await act(async () => {
      const pane = await xrmVerse()!.App.sidePanes.createPane({ title: 'Console', alwaysRender: true });
      void pane.navigate({ pageType: 'webresource', webresourceName: 'op_/pane.html' });
    });
    const frame = await screen.findByTitle('Web resource op_/pane.html');
    const main = document.querySelector('main')!;
    await userEvent.click(screen.getByRole('button', { name: 'Ursula User' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: en['personalization.menu'] }));
    const dialog = await screen.findByRole('dialog', { name: en['personalization.title'] });
    await userEvent.selectOptions(within(dialog).getByLabelText(en['personalization.scheme']), en['personalization.scheme.dark']);
    await userEvent.click(within(dialog).getByRole('button', { name: en['personalization.save'] }));
    await waitFor(() => expect(reads.settings).toBeGreaterThan(0));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: en['personalization.title'] })).toBeNull());
    // The app is read again with the new settings; the frame stays, never swapped for the loading screen.
    await act(() => new Promise((r) => setTimeout(r, 100)));
    expect(screen.queryByText(en['apps.opening'])).toBeNull();
    expect(document.querySelector('main')).toBe(main);
    expect(screen.getByTitle('Web resource op_/pane.html')).toBe(frame);
    expect(frame.isConnected).toBe(true);
  });

  describe('app OnLoad', () => {
    const ONLOAD = JSON.stringify([{ library: 'onload.js', function: 'App.onLoad' }]);
    const open = (sources: Record<string, string>, entry = `/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=account`) => {
      const calls: string[] = [];
      setLibraryLoader(sourceLoader(sources));
      mockApps({
        apps: [{ ...SALES, settings: { xrmverse_AppOnLoad: ONLOAD } }],
        userRoles: [ROLE_SALES],
        privileges: PRIVILEGES,
        siteMapXml: SITEMAP,
        systemForms: [OVERVIEW],
      });
      let router!: { navigate(to: string): unknown };
      const view = render(<App initialEntries={[entry]} onRouter={((r: typeof router) => (router = r)) as never} />);
      return { calls, view, navigate: (to: string) => router.navigate(to) };
    };

    it('shows the first page before a pending handler settles', async () => {
      open({ 'onload.js': 'var App = { onLoad: function () { return new Promise(function () {}); } };' });
      expect(await screen.findByRole('heading', { name: 'Accounts' })).toBeInTheDocument();
    });

    it('reports a failing handler as an app notification', async () => {
      open({ 'onload.js': "var App = { onLoad: function () { throw new Error('bad'); } };" });
      const region = await screen.findByRole('region', { name: en['apps.notifications.label'] });
      expect(await within(region).findByText('App OnLoad: onload.js/App.onLoad: bad')).toBeInTheDocument();
    });

    it('runs once for the app, not on navigation inside it', async () => {
      const loads: string[] = [];
      const inner = sourceLoader({ 'onload.js': 'var App = { onLoad: function () {} };' });
      const { navigate } = open({});
      setLibraryLoader(async (win, url, name) => {
        loads.push(name);
        return inner(win, url, name);
      });
      await screen.findByRole('heading', { name: 'Accounts' });
      await waitFor(() => expect(loads).toEqual(['onload.js']));
      await act(async () => void navigate(`/main.aspx?appid=${APP_ID}&pagetype=entitylist&etn=contact`));
      await screen.findByRole('heading', { name: 'Contacts' });
      await new Promise((r) => setTimeout(r, 50));
      expect(loads).toEqual(['onload.js']);
    });
  });
});
