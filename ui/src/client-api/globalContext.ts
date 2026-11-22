import { offsetMinutes } from '../apps/timeZones';
import type { DateFormats, UserSettings } from '../apps/userSettings';
import type { AppModel } from '../model/app';
import type { XrmApi } from '../shell/api/client';
import { collectByIds, userRoleIds } from '../shell/auth/roles';
import { uiLocale } from '../shell/i18n/t';
import { collection } from './collection';
import { stubMembers } from './notImplemented';
import { bracedUpper } from './values';

/** The signed-in user as the global context shows it. */
export interface CurrentUser {
  id: string;
  name: string;
  /** `usersettings.uilanguageid`. */
  lcid: number;
  /** The roles the user holds directly or through owner teams. */
  roles: { id: string; name: string }[];
  /** The IANA id of `usersettings.timezonecode` (#131). */
  timeZone: string;
  /** The date and time formats of `usersettings` (#131). */
  formats: DateFormats;
}

/** The organization as the global context shows it. */
export interface OrgSettings {
  id: string;
  /** The organization's name: xrmverse has one organization and no separate unique name (ruling R-T5-6). */
  uniqueName: string;
  /** xrmverse keeps no base language on the organization: 1033 (ruling R-T5-6). */
  languageId: number;
  baseCurrencyId: string | null;
  /** `organization.isautosaveenabled` (#117): only an explicit true turns auto-save on. */
  isAutoSaveEnabled: boolean;
}

/** The version `getVersion()` returns (ruling C9): the Web API version xrmverse speaks. */
export const CLIENT_API_VERSION = '9.2.0.0';
const ORG_LANGUAGE = 1033;
/** Right-to-left UI languages among the LCIDs Dataverse offers: Arabic, Hebrew, Urdu, Persian (ruling R-T5-7). */
const RTL_LCIDS = new Set([1025, 1037, 1056, 1065]);

/** Reads what the global context needs beyond the user's settings: the user's name and roles, and the organization. */
export async function loadXrmContext(api: XrmApi, settings: UserSettings): Promise<{ user: CurrentUser; org: OrgSettings }> {
  const { userId, lcid, timeZone, formats } = settings;
  const [me, roleIds, orgs] = await Promise.all([
    api.get<{ fullname?: string | null }>(`systemusers(${userId})?$select=fullname`),
    userRoleIds(api, userId),
    api.get<{ value: { organizationid: string; name?: string | null; _basecurrencyid_value?: string | null; isautosaveenabled?: boolean | null }[] }>(
      'organizations?$select=organizationid,name,_basecurrencyid_value,isautosaveenabled&$top=1',
    ),
  ]);
  const roles = roleIds.length ? await collectByIds<{ roleid: string; name?: string | null }>(api, `roles?$select=roleid,name`, 'roleid', roleIds) : [];
  const row = orgs.value[0];
  return {
    user: { id: userId, name: me.fullname ?? '', lcid, roles: roles.map((r) => ({ id: r.roleid.toLowerCase(), name: r.name ?? '' })), timeZone, formats },
    org: { id: row?.organizationid ?? '', uniqueName: row?.name ?? '', languageId: ORG_LANGUAGE, baseCurrencyId: row?._basecurrencyid_value ?? null, isAutoSaveEnabled: row?.isautosaveenabled === true },
  };
}

const GLOBAL = 'Xrm.Utility.getGlobalContext()';

/**
 * `userSettings.dateFormattingInfo` (#131, ruling Z7): the user's patterns, separators and designators from `usersettings`, the
 * first day of the week as the date picker uses it, and the day and month names of the shell language (`Intl`; the month lists
 * have 13 entries, the last empty, as .NET's `DateTimeFormatInfo`). The long patterns throw "not available".
 */
function dateFormattingInfo(formats: DateFormats): Record<string, unknown> {
  const locale = uiLocale();
  const names = (count: number, options: Intl.DateTimeFormatOptions, at: (i: number) => Date) =>
    Array.from({ length: count }, (_, i) => new Intl.DateTimeFormat(locale, { ...options, timeZone: 'UTC' }).format(at(i)));
  // 2026-01-04 is a Sunday: the day lists start on Sunday.
  const day = (i: number) => new Date(Date.UTC(2026, 0, 4 + i));
  const month = (i: number) => new Date(Date.UTC(2026, i, 1));
  const info = {
    AMDesignator: formats.am,
    PMDesignator: formats.pm,
    DateSeparator: formats.dateSeparator,
    TimeSeparator: formats.timeSeparator,
    ShortDatePattern: formats.dateFormat,
    ShortTimePattern: formats.timeFormat,
    FirstDayOfWeek: locale === 'it-IT' ? 1 : 0,
    DayNames: names(7, { weekday: 'long' }, day),
    AbbreviatedDayNames: names(7, { weekday: 'short' }, day),
    MonthNames: [...names(12, { month: 'long' }, month), ''],
    AbbreviatedMonthNames: [...names(12, { month: 'short' }, month), ''],
  };
  stubMembers(info, `${GLOBAL}.userSettings.dateFormattingInfo`);
  return info;
}

/** `Xrm.Utility.getGlobalContext()` (Learn): one object per app shell. */
export function createGlobalContext(app: AppModel, user: CurrentUser, org: OrgSettings): Record<string, unknown> {
  const roles = user.roles.map((r) => ({ ...r }));
  const userSettings = {
    userId: bracedUpper(user.id),
    userName: user.name,
    languageId: user.lcid,
    isRTL: RTL_LCIDS.has(user.lcid),
    // Learn: deprecated in favour of `roles`; the role ids.
    securityRoles: roles.map((r) => r.id),
    // Learn: a collection of `{ id, name }` lookups (found by index; the items have no getName).
    roles: collection<{ id: string; name: string; getName?: () => string }>(() => roles),
    // #131 (ruling Z7, lifting C8): the user's time zone offset now, east of UTC positive.
    getTimeZoneOffsetMinutes: () => offsetMinutes(user.timeZone, new Date()),
    dateFormattingInfo: dateFormattingInfo(user.formats),
  };
  stubMembers(userSettings, `${GLOBAL}.userSettings`);
  const organizationSettings = {
    organizationId: org.id,
    uniqueName: org.uniqueName,
    languageId: org.languageId,
    baseCurrencyId: org.baseCurrencyId,
    defaultCountryCode: '',
    isAutoSaveEnabled: org.isAutoSaveEnabled,
  };
  stubMembers(organizationSettings, `${GLOBAL}.organizationSettings`);
  const appUrl = () => `${location.origin}/main.aspx?appid=${app.id}`;
  const context = {
    userSettings,
    organizationSettings,
    getClientUrl: () => location.origin,
    getCurrentAppUrl: appUrl,
    getCurrentAppName: () => Promise.resolve(app.name),
    getCurrentAppProperties: () =>
      Promise.resolve({
        appId: app.id,
        displayName: app.name,
        uniqueName: app.uniqueName,
        url: appUrl(),
        webResourceId: null,
        webResourceName: null,
        welcomePageId: null,
        welcomePageName: null,
      }),
    getVersion: () => CLIENT_API_VERSION,
    // xrmverse serves one organization at the root of its address: no organization name goes in a path (ruling R-T5-6).
    prependOrgName: (path: unknown) => String(path ?? ''),
    getQueryStringParameters: () => Object.fromEntries(new URLSearchParams(location.search)),
  };
  stubMembers(context, GLOBAL);
  return context;
}
