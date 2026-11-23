import { http, HttpResponse } from 'msw';
import { server } from '../test/server';
import { saveDevCredentials } from '../shell/auth/devToken';
import { SYSTEM_ADMINISTRATOR_TEMPLATE } from '../shell/auth/roles';

export const USER_ID = 'aaaaaaaa-0000-0000-0000-000000000001';
export const TEAM_ID = 'bbbbbbbb-0000-0000-0000-000000000001';
export const ROLE_SALES = 'cccccccc-0000-0000-0000-000000000001';
export const ROLE_TEAM = 'cccccccc-0000-0000-0000-000000000002';
export const ROLE_OTHER = 'cccccccc-0000-0000-0000-000000000003';
export const ROLE_ADMIN = 'cccccccc-0000-0000-0000-0000000000ad';
export const SITEMAP_ID = 'dddddddd-0000-0000-0000-000000000001';
export const ORG_ID = 'eeeeeeee-0000-0000-0000-000000000001';

export interface MockApp {
  appmoduleid: string;
  name: string;
  uniquename?: string;
  description?: string;
  clienttype?: number;
  /** 1: inactive (the picker reads active apps only). */
  statecode?: number;
  roles: string[];
  siteMapId?: string;
  /** The app's `appsetting` rows (#129): value by setting definition unique name (`AsyncOnLoad`, `AsyncOnSave`, others). */
  settings?: Record<string, string>;
  /** The app's form components (`componenttype` 60): system form ids, dashboards included. */
  forms?: string[];
}

export interface AppsMock {
  apps?: MockApp[];
  userRoles?: string[];
  teamRoles?: string[];
  privileges?: string[];
  siteMapXml?: string;
  lcid?: number | null;
  schemaNames?: Record<string, string>;
  /** A path (e.g. `appmodules`, `sitemaps`, `accounts`) answered with a 500. */
  fail?: string;
  /** `usersettings.paginglimit`. */
  pagingLimit?: number | null;
  /** `savedquery` rows (any table; filtered by `returnedtypecode`). */
  savedQueries?: MockView[];
  /** `userquery` rows (any table; filtered by `returnedtypecode`). */
  userQueries?: MockView[];
  /** Table metadata by logical name; a table not listed gets `{name}s`, `{name}id` and no labels. */
  tables?: Record<string, MockTable>;
  /** `organization.isautosaveenabled` (#117); off by default here, so a form test is not saved by a timer. */
  autoSave?: boolean;
  /** `settingdefinition.defaultvalue` of AsyncOnLoad and AsyncOnSave (#129); `false` by default, as seeded. */
  settingDefaults?: Record<string, string>;
  /** The `organizationsetting` rows (environment values), by setting definition unique name. */
  environmentSettings?: Record<string, string>;
  /** Setting definitions the organization lacks (not yet seeded), by unique name. */
  missingDefinitions?: string[];
  /** Answers `GET {entityset}?fetchXml=…`; no rows by default. */
  records?: (entitySet: string, fetch: Element) => Record<string, unknown>;
  /** `systemform` rows: when given, `systemforms` (filtered by `type eq N`) and `systemforms({id})` are answered. */
  systemForms?: MockSystemForm[];
  /** `usersettings.xrmverse_renderer` (null: follow the app). */
  userRenderer?: string | null;
  /** `usersettings.xrmverse_colorscheme`. */
  colorScheme?: number | null;
}

export interface MockSystemForm {
  systemformid: string;
  name: string;
  type: number;
  objecttypecode: string;
  formxml?: string;
}

export interface MockView {
  id: string;
  name: string;
  entity: string;
  querytype?: number;
  isdefault?: boolean;
  fetchxml: string;
  layoutxml: string;
  /** The owner of a `userquery`; the signed-in user by default. */
  ownerid?: string;
}

export interface MockTable {
  entitySetName: string;
  primaryId: string;
  /** Display labels by attribute, then by LCID. */
  labels: Record<string, Record<number, string>>;
  primaryName?: string;
  /** The table's display name (1033). */
  displayName?: string;
  /** `IsActivity`. */
  isActivity?: boolean;
}

/** The ids of the seeded setting definitions (#129, the observed well-known ids). */
export const SETTING_DEFINITIONS: Record<string, string> = {
  AsyncOnLoad: 'fe39737f-4953-4a92-9943-56a697b82c7b',
  AsyncOnSave: 'aaf2a566-153a-4384-9acb-7d762a411c6d',
  xrmverse_TimelineGroupByDate: '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e01',
  xrmverse_TimelineRememberFilters: '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e02',
  xrmverse_TimelinePosts: '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e03',
  xrmverse_Renderer: '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e10',
  xrmverse_Presentation: '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e11',
  xrmverse_AppOnLoad: '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e20',
  xrmverse_EmailSendFrom: '6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e21',
};

const DEFAULT_SCHEMA_NAMES: Record<string, string> = {
  account: 'Account',
  contact: 'Contact',
  role: 'Role',
  op365_project: 'op365_Project',
};

/** Signs in a user (dev token) and answers the requests of the app picker and the app shell. Returns the paths read. */
export function mockApps({
  apps = [],
  userRoles = [],
  teamRoles = [],
  privileges = [],
  siteMapXml = '<SiteMap/>',
  lcid = 1033,
  schemaNames = DEFAULT_SCHEMA_NAMES,
  fail,
  pagingLimit = 50,
  savedQueries = [],
  userQueries = [],
  tables = {},
  records = () => ({ value: [] }),
  autoSave = false,
  settingDefaults = {},
  missingDefinitions = [],
  environmentSettings = {},
  systemForms,
  userRenderer,
  colorScheme,
}: AppsMock = {}): string[] {
  sessionStorage.clear();
  saveDevCredentials({ signingKey: 'k'.repeat(40), objectId: USER_ID, name: 'Ursula User' });
  const requests: string[] = [];
  const has = (filter: string, id: string) => filter.toLowerCase().includes(id.toLowerCase());
  server.use(
    http.get('*/ui/config.json', () =>
      HttpResponse.json({ authMode: 'Development', audience: 'http://localhost/', apiVersion: 'v9.2' }),
    ),
    http.get('*/api/data/v9.2/*', ({ request }) => {
      const url = new URL(request.url);
      const path = decodeURIComponent(url.pathname.replace(/^.*\/api\/data\/v9\.2\//, ''));
      const filter = url.searchParams.get('$filter') ?? '';
      requests.push(path + (url.search ? `?${decodeURIComponent(url.search.slice(1))}` : ''));
      const list = (value: unknown[]) => HttpResponse.json({ value });
      if (fail && (path === fail || path.startsWith(`${fail}(`))) {
        return HttpResponse.json({ error: { code: '0x80040216', message: `${fail} read failed` } }, { status: 500 });
      }
      if (path === 'WhoAmI') return HttpResponse.json({ UserId: USER_ID });
      // No last form stored (#119); a test that needs one overrides this.
      if (path === 'userentityuisettingsset') return list([]);
      if (path.startsWith('usersettingscollection(')) return HttpResponse.json({
          systemuserid: USER_ID,
          uilanguageid: lcid,
          paginglimit: pagingLimit,
          ...(userRenderer !== undefined ? { xrmverse_renderer: userRenderer } : {}),
          ...(colorScheme !== undefined ? { xrmverse_colorscheme: colorScheme } : {}),
        });
      if (path === 'appmodules') {
        return list(
          apps.filter((a) => !/statecode eq 0/.test(filter) || (a.statecode ?? 0) === 0).map((a) => ({
            appmoduleid: a.appmoduleid,
            name: a.name,
            uniquename: a.uniquename ?? a.name.replace(/\W/g, ''),
            description: a.description ?? null,
            clienttype: a.clienttype ?? 4,
          })),
        );
      }
      if (path === 'appmodulerolescollection') {
        return list(
          apps
            .flatMap((a) => a.roles.map((roleid) => ({ appmoduleid: a.appmoduleid, roleid })))
            .filter((r) => has(filter, r.roleid)),
        );
      }
      if (path === 'appmodulecomponents') {
        return list(
          apps
            .filter((a) => has(filter, a.appmoduleid))
            .flatMap((a) => [
              ...(a.siteMapId ? [{ _appmoduleidunique_value: a.appmoduleid, componenttype: 62, objectid: a.siteMapId }] : []),
              ...(a.forms ?? []).map((objectid) => ({ _appmoduleidunique_value: a.appmoduleid, componenttype: 60, objectid })),
            ]),
        );
      }
      if (systemForms && path === 'systemforms') {
        const type = /\btype eq (\d+)/.exec(filter)?.[1];
        return list(systemForms.filter((f) => type === undefined || f.type === Number(type)));
      }
      const form = /^systemforms\(([^)]+)\)$/.exec(path);
      if (systemForms && form) {
        const row = systemForms.find((f) => f.systemformid.toLowerCase() === form[1]!.toLowerCase());
        if (row) return HttpResponse.json(row);
        return HttpResponse.json({ error: { code: '0x80040217', message: `systemform With Id = ${form[1]} Does Not Exist` } }, { status: 404 });
      }
      // The app settings (#129): the two seeded definitions, and the rows of the apps the filter names.
      if (path === 'settingdefinitions') {
        return list(
          Object.entries(SETTING_DEFINITIONS)
            .filter(([name]) => filter.includes(`'${name}'`) && !missingDefinitions.includes(name))
            .map(([uniquename, settingdefinitionid]) => ({
              settingdefinitionid,
              uniquename,
              defaultvalue: settingDefaults[uniquename] ?? (uniquename === 'xrmverse_Presentation' || uniquename === 'xrmverse_AppOnLoad' ? '' : uniquename === 'xrmverse_Renderer' ? 'classic' : uniquename === 'xrmverse_EmailSendFrom' ? 'userOrQueue' : 'false'),
            })),
        );
      }
      if (path === 'organizationsettings') {
        return list(
          Object.entries(environmentSettings)
            .filter(([name]) => has(filter, SETTING_DEFINITIONS[name]!))
            .map(([name, value]) => ({ _settingdefinitionid_value: SETTING_DEFINITIONS[name], value })),
        );
      }
      if (path === 'appsettings') {
        return list(
          apps
            .filter((a) => has(filter, a.appmoduleid))
            .flatMap((a) =>
              Object.entries(a.settings ?? {}).map(([name, value]) => ({
                _parentappmoduleid_value: a.appmoduleid,
                _settingdefinitionid_value: SETTING_DEFINITIONS[name] ?? `99999999-0000-0000-0000-${name.length.toString().padStart(12, '0')}`,
                value,
              })),
            ),
        );
      }
      if (path === 'systemuserrolescollection') return list(has(filter, USER_ID) ? userRoles.map((roleid) => ({ roleid })) : []);
      if (path === 'teammemberships') return list(has(filter, USER_ID) && teamRoles.length ? [{ teamid: TEAM_ID }] : []);
      if (path === 'teams') return list(has(filter, TEAM_ID) ? [{ teamid: TEAM_ID, teamtype: 0 }] : []);
      if (path === 'teamrolescollection') return list(has(filter, TEAM_ID) ? teamRoles.map((roleid) => ({ roleid })) : []);
      if (path === 'roles') {
        return list(
          [...userRoles, ...teamRoles]
            .filter((id) => has(filter, id))
            .map((roleid) => ({ roleid, name: `Role ${roleid.slice(-2)}`, roletemplateid: roleid === ROLE_ADMIN ? SYSTEM_ADMINISTRATOR_TEMPLATE : null })),
        );
      }
      // The Client API's global context (Task 5): the user's name and the organization.
      if (path === `systemusers(${USER_ID})`) return HttpResponse.json({ systemuserid: USER_ID, fullname: 'Ursula User' });
      if (path === 'organizations') return list([{ organizationid: ORG_ID, name: 'contoso', _basecurrencyid_value: null, isautosaveenabled: autoSave }]);
      if (path.startsWith('sitemaps(')) return HttpResponse.json({ sitemapid: SITEMAP_ID, sitemapxml: siteMapXml });
      if (path === `systemusers(${USER_ID})/Microsoft.Dynamics.CRM.RetrieveUserPrivileges()`) {
        return HttpResponse.json({
          RolePrivileges: privileges.map((p, i) => ({ Depth: 'Global', PrivilegeId: `0000000${i}`, BusinessUnitId: TEAM_ID, PrivilegeName: p })),
        });
      }
      const table = /^EntityDefinitions\(LogicalName='(\w+)'\)(\/Attributes)?$/.exec(path);
      if (table) {
        const [, name, attributes] = table;
        const meta = tables[name] ?? { entitySetName: `${name}s`, primaryId: `${name}id`, labels: {} };
        if (!attributes)
          return HttpResponse.json({
            LogicalName: name,
            SchemaName: schemaNames[name],
            EntitySetName: meta.entitySetName,
            PrimaryIdAttribute: meta.primaryId,
            PrimaryNameAttribute: meta.primaryName ?? null,
            IsActivity: meta.isActivity ?? false,
            DisplayName: meta.displayName ? { LocalizedLabels: [{ Label: meta.displayName, LanguageCode: 1033 }] } : null,
          });
        return list(
          Object.entries(meta.labels).map(([LogicalName, labels]) => ({
            LogicalName,
            DisplayName: { LocalizedLabels: Object.entries(labels).map(([lcid, Label]) => ({ Label, LanguageCode: Number(lcid) })) },
          })),
        );
      }
      const etn = /returnedtypecode eq '(\w+)'/.exec(filter)?.[1];
      if (path === 'savedqueries') {
        return list(
          savedQueries
            .filter((v) => v.entity === etn)
            .map((v) => ({ savedqueryid: v.id, name: v.name, querytype: v.querytype ?? 0, isdefault: v.isdefault ?? false, fetchxml: v.fetchxml, layoutxml: v.layoutxml })),
        );
      }
      if (path === 'userqueries') {
        return list(
          userQueries
            .filter((v) => v.entity === etn)
            .map((v) => ({ userqueryid: v.id, name: v.name, querytype: v.querytype ?? 0, fetchxml: v.fetchxml, layoutxml: v.layoutxml, _ownerid_value: v.ownerid ?? USER_ID })),
        );
      }
      const fetchXml = url.searchParams.get('fetchXml');
      if (fetchXml) return HttpResponse.json(records(path, new DOMParser().parseFromString(fetchXml, 'application/xml').documentElement));
      if (path === 'EntityDefinitions') {
        return list(Object.entries(schemaNames).map(([LogicalName, SchemaName]) => ({ LogicalName, SchemaName })));
      }
      return HttpResponse.json({ error: { code: '0x80040217', message: `unexpected ${path}` } }, { status: 404 });
    }),
  );
  return requests;
}

/** A sitemap with an English and an Italian title per node, unless `titles` is given as raw XML. */
export const titled = (en: string, it?: string) =>
  `<Titles><Title LCID="1033" Title="${en}"/>${it ? `<Title LCID="1040" Title="${it}"/>` : ''}</Titles>`;
