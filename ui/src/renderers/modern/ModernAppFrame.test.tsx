import { act, render, renderHook, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mockApps, ROLE_SALES, SITEMAP_ID, titled, type AppsMock } from '../../apps/testSupport';
import type { PresentationModel } from '../../parsers/presentationJson';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { setUiLanguage } from '../../shell/i18n/t';
import { xrmverseDarkTheme, xrmverseTheme } from '../../shell/theme';
import { server } from '../../test/server';
import { ThemeScope, useActiveTheme } from '../ThemeScope';
import { modernRenderer } from '.';
import { classicRenderer } from '../classic';
import { ModernAppFrame } from './ModernAppFrame';
import { ModernViewGrid } from './ModernViewGrid';
import { ModernFormView } from './ModernFormView';
import { buildTheme } from './theme';

const APP_ID = '11111111-0000-0000-0000-000000000001';
const LOOK = 'x_/look.json';
const SITEMAP = `<SiteMap>
  <Area Id="sales">${titled('Sales')}
    <Group Id="customers">${titled('Customers')}
      <SubArea Id="accounts" Entity="account">${titled('Accounts')}</SubArea>
    </Group>
  </Area>
</SiteMap>`;

const RAMP = Object.fromEntries(
  [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160].map((k, i) => [k, `#10${(i * 16).toString(16).padStart(2, '0')}20`]),
) as unknown as NonNullable<PresentationModel['theme']['brand']>;

afterEach(() => {
  sessionStorage.clear();
  setUiLanguage(undefined);
});

function renderApp(renderer: string, over: Partial<AppsMock> = {}, overlay?: unknown) {
  mockApps({
    apps: [{ appmoduleid: APP_ID, name: 'Sales Hub', roles: [ROLE_SALES], siteMapId: SITEMAP_ID, settings: { xrmverse_Renderer: renderer, ...(overlay ? { xrmverse_Presentation: LOOK } : {}) } }],
    userRoles: [ROLE_SALES],
    privileges: ['prvReadAccount'],
    siteMapXml: SITEMAP,
    ...over,
  });
  if (overlay) server.use(http.get(`*/WebResources/${LOOK}`, () => HttpResponse.json(overlay)));
  return render(<App initialEntries={[`/main.aspx?appid=${APP_ID}&pagetype=entityrecord`]} />);
}

/** The value of the CSS variable `--{token}` that a FluentProvider element defines (Fluent writes one style rule per provider). */
function tokenOf(provider: Element, token: string): string | undefined {
  const classes = [...provider.classList];
  for (const style of document.querySelectorAll('style')) {
    for (const rule of (style.sheet?.cssRules ?? []) as Iterable<CSSRule>) {
      const r = rule as CSSStyleRule;
      const selectors = r.selectorText?.split(',').map((x) => x.trim()) ?? [];
      if (!classes.some((c) => selectors.includes(`.${c}`))) continue;
      const value = r.style?.getPropertyValue(`--${token}`);
      if (value) return value.trim();
    }
  }
  return undefined;
}

const providers = () => [...document.querySelectorAll('.fui-FluentProvider')];

describe('modernRenderer', () => {
  it('draws its own frame, view grid and form slot and keeps the classic command bar and dashboards', () => {
    expect(modernRenderer.id).toBe('modern');
    expect(modernRenderer.AppFrame).toBe(ModernAppFrame);
    expect(modernRenderer.ViewGrid).toBe(ModernViewGrid);
    expect(modernRenderer.FormView).toBe(ModernFormView);
    expect(modernRenderer.CommandBar).toBe(classicRenderer.CommandBar);
    expect(modernRenderer.DashboardLayout).toBe(classicRenderer.DashboardLayout);
  });
});

describe('ModernAppFrame', () => {
  it('the navigation rail collapses and expands with a keyboard-reachable button', async () => {
    renderApp('modern');
    const toggle = await screen.findByRole('button', { name: en['apps.nav.toggle'] });
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const nav = screen.getByRole('navigation', { name: 'Sales Hub' });
    expect(document.getElementById(toggle.getAttribute('aria-controls') ?? '')).toContainElement(nav);
    expect(within(nav).getByRole('link', { name: 'Accounts' })).toBeInTheDocument();

    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('navigation', { name: 'Sales Hub' })).toBeNull();

    // Keyboard: the toggle is reached with Tab and works with Enter and Space.
    act(() => (document.activeElement as HTMLElement | null)?.blur());
    let tabs = 0;
    while (document.activeElement !== toggle && tabs++ < 20) await userEvent.tab();
    expect(toggle).toHaveFocus();
    await userEvent.keyboard('{Enter}');
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('navigation', { name: 'Sales Hub' })).toBeInTheDocument();
    await userEvent.keyboard(' ');
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('the collapsed rail stays collapsed after a remount', async () => {
    const first = renderApp('modern');
    await userEvent.click(await screen.findByRole('button', { name: en['apps.nav.toggle'] }));
    first.unmount();
    renderApp('modern');
    const toggle = await screen.findByRole('button', { name: en['apps.nav.toggle'] });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('navigation', { name: 'Sales Hub' })).toBeNull();
  });

  it.each([
    [true, 'false', false],
    [false, 'true', true],
  ])('on a narrow screen (%s) the navigation rail starts with aria-expanded %s', async (narrow, expanded, visible) => {
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: narrow && query === '(max-width: 640px)', media: query, addEventListener: () => {}, removeEventListener: () => {} }));
    try {
      renderApp('modern');
      const toggle = await screen.findByRole('button', { name: en['apps.nav.toggle'] });
      expect(toggle).toHaveAttribute('aria-expanded', expanded);
      expect(screen.queryByRole('navigation', { name: 'Sales Hub' }) !== null).toBe(visible);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('without an overlay shows the app name and the xrmverse logo', async () => {
    renderApp('modern');
    await screen.findByRole('button', { name: en['apps.nav.toggle'] });
    const banner = screen.getByRole('banner');
    expect(within(banner).getByText('Sales Hub')).toBeInTheDocument();
    expect(within(banner).getByRole('img', { name: 'xrmverse' })).toBeInTheDocument();
    expect(within(banner).getByText('Ursula User')).toBeInTheDocument();
    expect(within(banner).getByRole('button', { name: en['auth.signOut'] })).toBeInTheDocument();
  });

  it('shows the filled variant of every bundled icon', async () => {
    renderApp('modern');
    await screen.findByRole('button', { name: en['apps.nav.toggle'] });
    const rules = [...document.querySelectorAll('style')].flatMap((style) => [...((style.sheet?.cssRules ?? []) as Iterable<CSSRule>)]) as CSSStyleRule[];
    const display = (icon: string) =>
      rules.find((r) => r.selectorText?.includes(icon) && r.style?.getPropertyValue('display') && r.style.getPropertyPriority('display') === 'important')?.style.getPropertyValue('display');
    expect(display('.fui-Icon-filled')).toBe('inline');
    expect(display('.fui-Icon-regular')).toBe('none');
  });

  describe('filled icons in popups (portals)', () => {
    /** The classes that scope a rule showing the filled icon variant (`.x .fui-Icon-filled { display: inline !important }`). */
    function filledScopes(): Set<string> {
      const rules = [...document.querySelectorAll('style')].flatMap((style) => [...((style.sheet?.cssRules ?? []) as Iterable<CSSRule>)]) as CSSStyleRule[];
      const scopes = new Set<string>();
      for (const r of rules) {
        if (r.style?.getPropertyValue('display') !== 'inline' || r.style.getPropertyPriority('display') !== 'important') continue;
        for (const selector of r.selectorText?.split(',') ?? []) {
          const m = /^\s*\.([\w-]+)\s+\.fui-Icon-filled\s*$/.exec(selector);
          if (m) scopes.add(m[1]!);
        }
      }
      return scopes;
    }

    /** Whether an ancestor of `el` carries a class that shows filled icons. */
    function showsFilled(el: Element): boolean {
      const scopes = filledScopes();
      for (let node: Element | null = el; node; node = node.parentElement) if ([...node.classList].some((c) => scopes.has(c))) return true;
      return false;
    }

    async function openUserMenu() {
      // The app's own frame, not the plain header drawn while the app loads.
      await screen.findByRole('navigation', { name: 'Sales Hub' });
      await userEvent.click(within(await screen.findByRole('banner')).getByRole('button', { name: 'Ursula User' }));
      return screen.findByRole('menuitem', { name: en['personalization.menu'] });
    }

    it('a menu opened in the modern look shows filled icons, outside the frame too', async () => {
      renderApp('modern');
      const item = await openUserMenu();
      // The menu is portaled out of the frame's root, into the modern provider's mount node.
      expect(item.closest('header')).toBeNull();
      expect(showsFilled(item)).toBe(true);
    });

    it('a menu opened in the classic look keeps the regular icons', async () => {
      renderApp('classic');
      const item = await openUserMenu();
      expect(showsFilled(item)).toBe(false);
    });
  });

  it('the overlay ramp themes the modern frame', async () => {
    renderApp('modern', {}, { version: 1, theme: { brandRamp: RAMP } });
    await screen.findByRole('button', { name: en['apps.nav.toggle'] });
    const inner = providers().at(-1)!;
    expect(providers().length).toBeGreaterThan(1);
    expect(tokenOf(inner, 'colorBrandBackground')?.toLowerCase()).toBe(RAMP[80].toLowerCase());
  });

  it('the overlay dark scheme applies unless the user chose light', async () => {
    const { unmount } = renderApp('modern', {}, { version: 1, theme: { colorScheme: 'dark' } });
    await screen.findByRole('button', { name: en['apps.nav.toggle'] });
    expect(tokenOf(providers().at(-1)!, 'colorNeutralBackground1')).toBe(xrmverseDarkTheme.colorNeutralBackground1);
    unmount();
    renderApp('modern', { colorScheme: 1 }, { version: 1, theme: { colorScheme: 'dark' } });
    await screen.findByRole('button', { name: en['apps.nav.toggle'] });
    expect(tokenOf(providers().at(-1)!, 'colorNeutralBackground1')).toBe(xrmverseTheme.colorNeutralBackground1);
  });
});

describe('colour scheme at the root', () => {
  it('follows the user colour scheme under classic too', async () => {
    renderApp('classic', { colorScheme: 2 });
    await screen.findByRole('navigation', { name: 'Sales Hub' });
    expect(tokenOf(providers()[0]!, 'colorNeutralBackground1')).toBe(xrmverseDarkTheme.colorNeutralBackground1);
  });

  it('classic ignores the overlay colour scheme', async () => {
    renderApp('classic', {}, { version: 1, theme: { colorScheme: 'dark' } });
    await screen.findByRole('navigation', { name: 'Sales Hub' });
    for (const p of providers()) expect(tokenOf(p, 'colorNeutralBackground1')).toBe(xrmverseTheme.colorNeutralBackground1);
  });
});

describe('ThemeScope', () => {
  it('useActiveTheme gives the scoped theme', () => {
    const theme = buildTheme({ theme: { brand: RAMP, density: 'comfortable' }, whiteLabel: {}, views: {}, forms: {}, choices: {} }, true);
    const { result } = renderHook(() => useActiveTheme(), { wrapper: ({ children }) => <ThemeScope theme={theme} dark brand={RAMP}>{children}</ThemeScope> });
    expect(result.current.theme).toBe(theme);
    expect(result.current.dark).toBe(true);
    expect(result.current.brand).toBe(RAMP);
  });

  it('outside a scope useActiveTheme is the xrmverse light theme (no matchMedia in jsdom)', () => {
    const { result } = renderHook(() => useActiveTheme());
    expect(result.current.theme).toBe(xrmverseTheme);
    expect(result.current.dark).toBe(false);
  });
});
