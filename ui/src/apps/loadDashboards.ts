import { parseSiteMap } from '../parsers/siteMapXml';
import { normaliseId } from '../parsers/formXml';
import type { XrmApi } from '../shell/api/client';
import { collect } from '../shell/auth/roles';
import { knownApps } from './loadApps';
import { parseMainAspx } from './mainAspx';

/** A dashboard as the selector lists it. */
export interface DashboardEntry {
  id: string;
  name: string;
}

interface DashboardRow {
  systemformid: string;
  name: string;
  type: number;
  objecttypecode: string;
}

const DASHBOARD_FORM = 0;

/** The dashboard ids the sitemap's subareas open (`pagetype=dashboard&id=…`), in sitemap order. */
async function siteMapDashboards(api: XrmApi, siteMapId: string | undefined): Promise<string[]> {
  if (!siteMapId) return [];
  const row = await api.get<{ sitemapxml?: string | null }>(`sitemaps(${siteMapId})?$select=sitemapxml`);
  if (!row.sitemapxml) return [];
  const ids: string[] = [];
  for (const area of parseSiteMap(row.sitemapxml).areas)
    for (const group of area.groups)
      for (const sub of group.subAreas) {
        if (sub.kind !== 'dashboard' || !sub.url) continue;
        const query = sub.url.includes('?') ? sub.url.slice(sub.url.indexOf('?') + 1) : sub.url;
        const id = parseMainAspx(query).id;
        if (id && !ids.includes(id)) ids.push(id);
      }
  return ids;
}

/**
 * The system dashboards of an app (`systemform` rows of type 0 and table `none`; personal dashboards are out of scope, ruling
 * F6): the app's form components (component type 60) that are dashboards; when the app names none, the dashboards its
 * sitemap opens. Those in the sitemap come first, in sitemap order, then the others by name. An unknown app lists none.
 */
export async function loadDashboards(api: XrmApi, appId: string): Promise<DashboardEntry[]> {
  const app = (await knownApps(api)).find((a) => a.id.toLowerCase() === appId.toLowerCase());
  if (!app) return [];
  const [inSiteMap, rows] = await Promise.all([
    siteMapDashboards(api, app.siteMapId),
    collect<DashboardRow>(api, `systemforms?$select=systemformid,name,type,objecttypecode&$filter=type eq ${DASHBOARD_FORM} and objecttypecode eq 'none'`),
  ]);
  const dashboards = rows.filter((r) => r.type === DASHBOARD_FORM && r.objecttypecode === 'none');
  const components = new Set(app.components.forms.map(normaliseId));
  const ofApp = dashboards.filter((r) => components.has(normaliseId(r.systemformid)));
  const listed = ofApp.length > 0 ? ofApp : dashboards.filter((r) => inSiteMap.includes(normaliseId(r.systemformid)));
  const position = (r: DashboardRow) => {
    const i = inSiteMap.indexOf(normaliseId(r.systemformid));
    return i < 0 ? Number.MAX_SAFE_INTEGER : i;
  };
  return listed
    .sort((a, b) => position(a) - position(b) || a.name.localeCompare(b.name))
    .map((r) => ({ id: normaliseId(r.systemformid), name: r.name }));
}
