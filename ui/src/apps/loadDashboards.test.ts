import { describe, expect, it } from 'vitest';
import { XrmApi } from '../shell/api/client';
import { loadDashboards } from './loadDashboards';
import { mockApps, ROLE_SALES, SITEMAP_ID, titled, type MockSystemForm } from './testSupport';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const APP_ID = '11111111-0000-0000-0000-000000000001';

const D1 = 'd0000000-0000-0000-0000-000000000001';
const D2 = 'd0000000-0000-0000-0000-000000000002';
const D3 = 'd0000000-0000-0000-0000-000000000003';
const OTHER = 'd0000000-0000-0000-0000-000000000009';
const MAIN = 'f0000000-0000-0000-0000-000000000001';

const dashboard = (systemformid: string, name: string): MockSystemForm => ({ systemformid, name, type: 0, objecttypecode: 'none' });
const FORMS: MockSystemForm[] = [
  dashboard(D1, 'Charlie'),
  dashboard(D2, 'Alpha'),
  dashboard(D3, 'Bravo'),
  dashboard(OTHER, 'Other app'),
  { systemformid: MAIN, name: 'Account', type: 2, objecttypecode: 'account' },
];

const sitemapWith = (...ids: string[]) =>
  `<SiteMap><Area Id="a">${titled('A')}<Group Id="g">${titled('G')}${ids
    .map((id, i) => `<SubArea Id="s${i}" Url="/main.aspx?pagetype=dashboard&amp;id=%7b${id.toUpperCase()}%7d&amp;type=system">${titled(`S${i}`)}</SubArea>`)
    .join('')}</Group></Area></SiteMap>`;

function mock(forms: string[] | undefined, siteMapXml: string) {
  return mockApps({
    apps: [{ appmoduleid: APP_ID, name: 'Sales', roles: [ROLE_SALES], siteMapId: SITEMAP_ID, forms }],
    userRoles: [ROLE_SALES],
    siteMapXml,
    systemForms: FORMS,
  });
}

describe('loadDashboards', () => {
  it('lists_the_app_dashboards_in_sitemap_order_then_by_name', async () => {
    // D1 is in the sitemap; D2 and D3 only in the app's components; the main form and the other app's dashboard are left out.
    mock([`{${D3.toUpperCase()}}`, D2, D1, MAIN], sitemapWith(D1));
    expect(await loadDashboards(api(), APP_ID)).toEqual([
      { id: D1, name: 'Charlie' },
      { id: D2, name: 'Alpha' },
      { id: D3, name: 'Bravo' },
    ]);
  });

  it('sitemap_order_wins_over_names', async () => {
    mock([D1, D2, D3], sitemapWith(D3, D1));
    expect((await loadDashboards(api(), APP_ID)).map((d) => d.id)).toEqual([D3, D1, D2]);
  });

  it('an_app_without_dashboard_components_lists_the_sitemap_dashboards', async () => {
    mock([MAIN], sitemapWith(D2, D1, 'd0000000-0000-0000-0000-0000000000ff'));
    expect(await loadDashboards(api(), APP_ID)).toEqual([
      { id: D2, name: 'Alpha' },
      { id: D1, name: 'Charlie' },
    ]);
  });

  it('an_app_with_no_dashboards_lists_none', async () => {
    mock(undefined, '<SiteMap/>');
    expect(await loadDashboards(api(), APP_ID)).toEqual([]);
  });

  it('an_unknown_app_lists_none', async () => {
    mock([D1], sitemapWith(D1));
    expect(await loadDashboards(api(), '99999999-0000-0000-0000-000000000009')).toEqual([]);
  });
});
