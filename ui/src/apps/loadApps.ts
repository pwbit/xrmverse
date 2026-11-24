import type { AppModel, TimelineOptions } from '../model/app';
import { parseAppModule, type AppModuleComponentRow, type AppModuleRow } from '../parsers/appModule';
import { parseAppOnLoad } from '../client-api/xrmverse/appOnLoadSetting';
import { PRESENTATION, RENDERER } from '../renderers/lookSettings';
import { mergeSubgridFilterNames, parseSubgridFilterNames } from './record/subgridFilters';
import { parseRecordOpenMode, type RecordOpenMode } from './record/recordOpener';
import type { XrmApi } from '../shell/api/client';
import { collect, collectByIds, SYSTEM_ADMINISTRATOR_TEMPLATE, userRoleIds } from '../shell/auth/roles';

interface AppModuleRoleRow {
  appmoduleid: string;
  roleid: string;
}

interface ComponentRow extends AppModuleComponentRow {
  _appmoduleidunique_value: string;
}

interface SettingDefinitionRow {
  settingdefinitionid: string;
  uniquename: string;
  defaultvalue?: string | null;
}

interface AppSettingRow {
  _parentappmoduleid_value: string;
  _settingdefinitionid_value: string;
  value?: string | null;
}

interface EnvironmentSettingRow {
  _settingdefinitionid_value: string;
  value?: string | null;
}

const lower = (id: string | null | undefined) => (id ?? '').toLowerCase();

/** A setting is on when its value is `true`, in any case (#129); `1` and any other value leave it off. */
const isOn = (value: string | null | undefined) => (value ?? '').trim().toLowerCase() === 'true';

/**
 * The apps the signed-in user may open, sorted by name (Global Constraints, "Who sees which app"):
 * only active apps (`statecode` 0) are listed, as Dataverse offers no inactive app; a System Administrator sees every
 * active app; any other user sees an app when one of its `appmoduleroles` is among the user's roles, held directly or
 * through an owner team; an app with no role is shown to System Administrators only.
 *
 * Ruling R1': the Web API has no collection-valued `$expand` and no N:N metadata for these tables, so `appmodules`,
 * `appmoduleroles`, `systemuserroles`, `teammembership` and `teamroles` are read as plain entity sets filtered by id
 * and joined here. Throws XrmError.
 */
export function loadApps(client: XrmApi): Promise<AppModel[]> {
  const load = readApps(client);
  lastLoad.set(client, load);
  load.catch(() => lastLoad.get(client) === load && lastLoad.delete(client));
  return load;
}

/** The last {@link loadApps} of each connection: opening an app from the picker reuses it (#110). */
const lastLoad = new WeakMap<XrmApi, Promise<AppModel[]>>();

/** The apps the last {@link loadApps} on `client` read (the picker's), else a fresh load. */
export function knownApps(client: XrmApi): Promise<AppModel[]> {
  return lastLoad.get(client) ?? loadApps(client);
}

async function readApps(client: XrmApi): Promise<AppModel[]> {
  const me = await client.get<{ UserId: string }>('WhoAmI');
  const rows = await collect<AppModuleRow>(client, 'appmodules?$select=appmoduleid,uniquename,name,description,clienttype&$filter=statecode eq 0');
  if (rows.length === 0) return [];
  const roleIds = await userRoleIds(client, me.UserId);
  const roles = roleIds.length
    ? await collectByIds<{ roleid: string; roletemplateid?: string | null }>(client, `roles?$select=roleid,roletemplateid`, 'roleid', roleIds)
    : [];
  const isAdmin = roles.some((r) => lower(r.roletemplateid) === SYSTEM_ADMINISTRATOR_TEMPLATE);

  let visible = rows;
  if (!isAdmin) {
    const held = new Set(roleIds);
    // Only the caller's roles can open an app: read the app roles of those roles.
    const appRoles = roleIds.length
      ? await collectByIds<AppModuleRoleRow>(client, `appmodulerolescollection?$select=appmoduleid,roleid`, 'roleid', roleIds)
      : [];
    const open = new Set(appRoles.filter((r) => held.has(lower(r.roleid))).map((r) => lower(r.appmoduleid)));
    visible = rows.filter((r) => open.has(lower(r.appmoduleid)));
  }
  if (visible.length === 0) return [];

  const components = await collectByIds<ComponentRow>(
    client,
    `appmodulecomponents?$select=_appmoduleidunique_value,componenttype,objectid`,
    '_appmoduleidunique_value',
    visible.map((r) => r.appmoduleid),
  );
  const settings = await readAppSettings(client, visible.map((r) => r.appmoduleid));
  return visible
    .map((row) => ({
      ...parseAppModule(
        row,
        components.filter((c) => lower(c._appmoduleidunique_value) === lower(row.appmoduleid)),
      ),
      asyncHandlers: settings.asyncHandlers(row.appmoduleid),
      timeline: settings.timeline(row.appmoduleid),
      presentation: settings.presentation(row.appmoduleid),
      recordOpenMode: settings.recordOpenMode(row.appmoduleid),
      appOnLoad: settings.appOnLoad(row.appmoduleid),
      emailSendFrom: settings.emailSendFrom(row.appmoduleid),
      subgridFilters: settings.subgridFilters(row.appmoduleid),
    }))
    .sort((a, b) => a.name.localeCompare(b.name));
}

const ASYNC_ON_LOAD = 'AsyncOnLoad';
const ASYNC_ON_SAVE = 'AsyncOnSave';
const TIMELINE_GROUP_BY_DATE = 'xrmverse_TimelineGroupByDate';
const TIMELINE_REMEMBER_FILTERS = 'xrmverse_TimelineRememberFilters';
const TIMELINE_POSTS = 'xrmverse_TimelinePosts';
const LOOKUP_OPEN_MODE = 'xrmverse_LookupOpenMode';
const APP_ON_LOAD = 'xrmverse_AppOnLoad';
/** The sender policy of email (plan 4.1b, an xrmverse extension), set at environment level. */
const EMAIL_SEND_FROM = 'xrmverse_EmailSendFrom';
/** The web resources that configure subgrids filtered by a lookup of the form (X.11): a JSON array of names, at app and environment level. */
const SUBGRID_FILTERS = 'xrmverse_SubgridFilters';
const SETTING_NAMES = [ASYNC_ON_LOAD, ASYNC_ON_SAVE, TIMELINE_GROUP_BY_DATE, TIMELINE_REMEMBER_FILTERS, TIMELINE_POSTS, RENDERER, PRESENTATION, APP_ON_LOAD, LOOKUP_OPEN_MODE, EMAIL_SEND_FROM, SUBGRID_FILTERS];

/**
 * The app settings of `appIds` the forms read: AsyncOnLoad and AsyncOnSave (#129), and the timeline options (UI-1g). A setting is
 * the app's `appsetting` row of the definition when it has one, else the definition's `defaultvalue`; a definition the
 * organization lacks is off. Every user reads both tables (Basic User has Global Read on them), so a failed read is an error of
 * the shell like the other app reads.
 */
async function readAppSettings(
  client: XrmApi,
  appIds: string[],
): Promise<{ asyncHandlers: (appId: string) => { onLoad: boolean; onSave: boolean }; timeline: (appId: string) => TimelineOptions; presentation: (appId: string) => NonNullable<AppModel['presentation']>; appOnLoad: (appId: string) => AppModel['appOnLoad']; recordOpenMode: (appId: string) => RecordOpenMode; emailSendFrom: (appId: string) => NonNullable<AppModel['emailSendFrom']>; subgridFilters: (appId: string) => string[] }> {
  const filter = SETTING_NAMES.map((n) => `uniquename eq '${n}'`).join(' or ');
  const definitions = await collect<SettingDefinitionRow>(
    client,
    `settingdefinitions?$select=settingdefinitionid,uniquename,defaultvalue&$filter=${filter}`,
  );
  const rows = await collectByIds<AppSettingRow>(
    client,
    'appsettings?$select=_parentappmoduleid_value,_settingdefinitionid_value,value',
    '_parentappmoduleid_value',
    appIds,
  );
  // The environment values (`organizationsetting`, at most one per definition), read for the look definitions, the record open
  // mode and the email sender policy only.
  const lookIds = [RENDERER, PRESENTATION, LOOKUP_OPEN_MODE, EMAIL_SEND_FROM, SUBGRID_FILTERS].map((n) => definitions.find((d) => d.uniquename.toLowerCase() === n.toLowerCase())?.settingdefinitionid).filter((id): id is string => !!id);
  const environment = lookIds.length
    ? await collectByIds<EnvironmentSettingRow>(client, 'organizationsettings?$select=_settingdefinitionid_value,value', '_settingdefinitionid_value', lookIds)
    : [];
  const definition = (name: string) => definitions.find((d) => d.uniquename.toLowerCase() === name.toLowerCase());
  const setting = (appId: string, name: string) => {
    const d = definition(name);
    if (!d) return false;
    const row = rows.find(
      (r) => lower(r._parentappmoduleid_value) === lower(appId) && lower(r._settingdefinitionid_value) === lower(d.settingdefinitionid),
    );
    return isOn(row ? row.value : d.defaultvalue);
  };
  /** The text of a look setting: the app's value, else the environment's, else the default; empty means not set. */
  const text = (appId: string, name: string): string | undefined => {
    const d = definition(name);
    if (!d) return undefined;
    const id = lower(d.settingdefinitionid);
    const app = rows.find((r) => lower(r._parentappmoduleid_value) === lower(appId) && lower(r._settingdefinitionid_value) === id)?.value;
    const env = environment.find((r) => lower(r._settingdefinitionid_value) === id)?.value;
    return [app, env, d.defaultvalue].find((v) => !!v?.trim())?.trim() || undefined;
  };
  /** The text of an environment-only setting: the environment's value, else the default (an app row is ignored). */
  const environmentText = (name: string): string | undefined => {
    const d = definition(name);
    if (!d) return undefined;
    const env = environment.find((r) => lower(r._settingdefinitionid_value) === lower(d.settingdefinitionid))?.value;
    return [env, d.defaultvalue].find((v) => !!v?.trim())?.trim() || undefined;
  };
  /** The subgrid filter resource names: unlike {@link text}, the app's list and the environment's both apply (app first). */
  const subgridFilters = (appId: string): string[] => {
    const d = definition(SUBGRID_FILTERS);
    if (!d) return [];
    const id = lower(d.settingdefinitionid);
    const app = rows.find((r) => lower(r._parentappmoduleid_value) === lower(appId) && lower(r._settingdefinitionid_value) === id)?.value;
    const env = environment.find((r) => lower(r._settingdefinitionid_value) === id)?.value;
    return mergeSubgridFilterNames(parseSubgridFilterNames(app ?? undefined), parseSubgridFilterNames(env ?? undefined));
  };
  return {
    subgridFilters,
    presentation: (appId) => ({ renderer: text(appId, RENDERER), overlay: text(appId, PRESENTATION) }),
    recordOpenMode: (appId) => parseRecordOpenMode(text(appId, LOOKUP_OPEN_MODE)),
    // Environment-only, as the server applies it (an app value is ignored); any value but queueOnly (the definition missing too) is the default.
    emailSendFrom: () => (environmentText(EMAIL_SEND_FROM)?.toLowerCase() === 'queueonly' ? 'queueOnly' : 'userOrQueue'),
    appOnLoad: (appId) => {
      const value = text(appId, APP_ON_LOAD);
      return value === undefined ? undefined : parseAppOnLoad(value);
    },
    asyncHandlers: (appId) => ({ onLoad: setting(appId, ASYNC_ON_LOAD), onSave: setting(appId, ASYNC_ON_SAVE) }),
    timeline: (appId) => ({
      groupByDate: setting(appId, TIMELINE_GROUP_BY_DATE),
      rememberFilters: setting(appId, TIMELINE_REMEMBER_FILTERS),
      posts: setting(appId, TIMELINE_POSTS),
    }),
  };
}
