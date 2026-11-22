import { http, HttpResponse } from 'msw';
import type { PresentationModel } from '../../parsers/presentationJson';
import { server } from '../../test/server';

export const SYSTEM_ADMIN = '627090ff-40a3-4053-8790-584edc5be201';
export const SYSTEM_CUSTOMIZER = '119f245c-3cc8-4b62-b31c-d1a046ced15d';
export const USER_ID = 'aaaaaaaa-0000-0000-0000-000000000001';
export const PRESENTATION_DEF = '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e11';
export const RENDERER_DEF = '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e10';

/** A brand ramp whose shades are easy to tell from the xrmverse ramp. */
export const RAMP = Object.fromEntries(
  [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160].map((k, i) => [k, `#10${(i * 16).toString(16).padStart(2, '0')}20`]),
) as unknown as NonNullable<PresentationModel['theme']['brand']>;

export type SettingRow = { organizationsettingid: string; _settingdefinitionid_value: string; value: string };

/**
 * A signed-in System Administrator (or the role template `role`) in the web admin: the look definitions (none with `definitions: false`), the environment
 * rows `rows` (live: changed rows show on the next read) and the overlay web resources `overlays` (name → response).
 */
export function mockAdmin(opts: { role?: string; definitions?: boolean; rows?: SettingRow[]; overlays?: Record<string, () => Response> } = {}) {
  const rows = opts.rows ?? [];
  const role = opts.role ?? SYSTEM_ADMIN;
  server.use(
    http.get('*/ui/config.json', () => HttpResponse.json({ authMode: 'Development', audience: 'http://localhost/', apiVersion: 'v9.2' })),
    http.get('*/api/data/v9.2/*', ({ request }) => {
      const entity = new URL(request.url).pathname.split('/').pop()!;
      if (entity === 'WhoAmI') return HttpResponse.json({ UserId: USER_ID });
      if (entity === 'systemuserrolescollection') return HttpResponse.json({ value: [{ roleid: role }] });
      if (entity === 'roles') return HttpResponse.json({ value: [{ roleid: role, name: 'Role', roletemplateid: role }] });
      if (entity === 'organizations') return HttpResponse.json({ value: [{ name: 'Acme Dev' }] });
      if (entity === 'settingdefinitions')
        return HttpResponse.json({
          value:
            opts.definitions === false
              ? []
              : [
                  { settingdefinitionid: RENDERER_DEF, uniquename: 'xrmverse_Renderer', defaultvalue: 'classic' },
                  { settingdefinitionid: PRESENTATION_DEF, uniquename: 'xrmverse_Presentation', defaultvalue: '' },
                ],
        });
      if (entity === 'organizationsettings') return HttpResponse.json({ value: rows });
      return HttpResponse.json({ value: [] });
    }),
    http.get('*/WebResources/*', ({ request }) => {
      const name = decodeURIComponent(new URL(request.url).pathname.slice('/WebResources/'.length));
      return opts.overlays?.[name]?.() ?? new HttpResponse(null, { status: 404 });
    }),
  );
  return rows;
}

/** The value of the CSS variable `--{token}` that a FluentProvider element defines (Fluent writes one style rule per provider). */
export function tokenOf(provider: Element, token: string): string | undefined {
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

/** The nearest FluentProvider around `el`: the theme the element renders with. */
export const providerOf = (el: Element) => el.closest('.fui-FluentProvider')!;
