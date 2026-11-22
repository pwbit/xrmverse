import { MessageBar, MessageBarBody, Spinner } from '@fluentui/react-components';
import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import type { XrmApi } from '../shell/api/client';
import { XrmError } from '../shell/api/errors';
import { useUserColorScheme } from '../renderers/ThemeScope';
import { useAuth } from '../shell/auth/AuthProvider';
import { setUiLanguage, t } from '../shell/i18n/t';
import { DisplayZoneContext } from './displayZone';
import { ianaOf } from './timeZones';

/** The user's date and time formats (`usersettings`, #131): what `dateFormattingInfo` gives scripts. */
export interface DateFormats {
  /** `dateformatstring`. */
  dateFormat: string;
  /** `timeformatstring`. */
  timeFormat: string;
  dateSeparator: string;
  timeSeparator: string;
  /** `amdesignator`. */
  am: string;
  /** `pmdesignator`. */
  pm: string;
}

/** The formats of a new user (ruling Z2, observed on a fresh trial): en-US. */
export const DEFAULT_DATE_FORMATS: DateFormats = { dateFormat: 'M/d/yyyy', timeFormat: 'h:mm tt', dateSeparator: '/', timeSeparator: ':', am: 'AM', pm: 'PM' };
/** UTC, the time zone of a new user and of a settings row without one. */
const DEFAULT_TIME_ZONE_CODE = 92;

/** The signed-in user's id and the `usersettings` the model-driven shell reads. */
export interface UserSettings {
  userId: string;
  /** `uilanguageid`; 1033 when unset. */
  lcid: number;
  /** `paginglimit`; 50 when unset. */
  pagingLimit: number;
  /** `timezonecode`; 92 (UTC) when unset. */
  timeZoneCode: number;
  /** The IANA id of {@link timeZoneCode}, which the browser converts dates with. */
  timeZone: string;
  /** The date and time formats; each unset one is {@link DEFAULT_DATE_FORMATS}'s. */
  formats: DateFormats;
  /** `xrmverse_renderer`: the look the user chose; undefined (null or absent) follows the app. */
  renderer?: string;
  /** `xrmverse_colorscheme`: 0 System, 1 Light, 2 Dark; 0 when unset. */
  colorScheme: 0 | 1 | 2;
}

const DEFAULT_LCID = 1033;
const DEFAULT_PAGING_LIMIT = 50;

const UserSettingsContext = createContext<UserSettings | undefined>(undefined);

/** The settings loaded by {@link UserSettingsGate}; only meaningful below it. */
export function useUserSettings(): UserSettings {
  const settings = useContext(UserSettingsContext);
  if (!settings) throw new Error('useUserSettings is used outside UserSettingsGate.');
  return settings;
}

const ReloadUserSettingsContext = createContext<() => void>(() => {});

/** Re-reads the user's settings row and re-renders the consumers (after the Personalization dialog saved); a no-op outside the gate. */
export function useReloadUserSettings(): () => void {
  return useContext(ReloadUserSettingsContext);
}

/** The settings when rendered below {@link UserSettingsGate}, else undefined (a form control rendered on its own). */
export function useLoadedUserSettings(): UserSettings | undefined {
  return useContext(UserSettingsContext);
}

interface SettingsRow {
  uilanguageid?: number | null;
  paginglimit?: number | null;
  timezonecode?: number | null;
  dateformatstring?: string | null;
  timeformatstring?: string | null;
  dateseparator?: string | null;
  timeseparator?: string | null;
  amdesignator?: string | null;
  pmdesignator?: string | null;
  xrmverse_renderer?: string | null;
  xrmverse_colorscheme?: number | null;
}

const COLUMNS = 'uilanguageid,paginglimit,timezonecode,dateformatstring,timeformatstring,dateseparator,timeseparator,amdesignator,pmdesignator,xrmverse_renderer,xrmverse_colorscheme';

/**
 * WhoAmI, then the caller's `usersettings` row (a user without one gets the defaults). Throws XrmError, or an Error for a
 * `timezonecode` that is not a time zone code (the app says so rather than showing dates in another zone).
 */
export async function loadUserSettings(api: XrmApi): Promise<UserSettings> {
  const me = await api.get<{ UserId: string }>('WhoAmI');
  let row: SettingsRow = {};
  try {
    row = await api.get(`usersettingscollection(${me.UserId})?$select=${COLUMNS}`);
  } catch (e) {
    if (!(e instanceof XrmError && e.status === 404)) throw e;
  }
  const timeZoneCode = row.timezonecode ?? DEFAULT_TIME_ZONE_CODE;
  const d = DEFAULT_DATE_FORMATS;
  return {
    userId: me.UserId,
    lcid: row.uilanguageid ?? DEFAULT_LCID,
    pagingLimit: row.paginglimit ?? DEFAULT_PAGING_LIMIT,
    timeZoneCode,
    timeZone: ianaOf(timeZoneCode),
    formats: {
      dateFormat: row.dateformatstring || d.dateFormat,
      timeFormat: row.timeformatstring || d.timeFormat,
      dateSeparator: row.dateseparator || d.dateSeparator,
      timeSeparator: row.timeseparator || d.timeSeparator,
      am: row.amdesignator || d.am,
      pm: row.pmdesignator || d.pm,
    },
    renderer: row.xrmverse_renderer || undefined,
    colorScheme: row.xrmverse_colorscheme === 1 || row.xrmverse_colorscheme === 2 ? row.xrmverse_colorscheme : 0,
  };
}

/**
 * Loads the user's settings once per sign-in, selects the shell language from `uilanguageid` and renders its
 * children with the settings in context, and the user's time zone as the forms' display zone (#131).
 */
export function UserSettingsGate({ children }: { children: ReactNode }) {
  const { api } = useAuth();
  const [state, setState] = useState<{ settings?: UserSettings; error?: string }>({});
  const [reloads, setReloads] = useState(0);
  const reload = useCallback(() => setReloads((n) => n + 1), []);
  // The page's root theme honours the user's colour scheme while the apps are open (classic and modern alike).
  useUserColorScheme(state.settings?.colorScheme ?? 0);
  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    loadUserSettings(api).then(
      (settings) => {
        if (cancelled) return;
        setUiLanguage(settings.lcid);
        setState({ settings });
      },
      (e: unknown) => !cancelled && setState({ error: e instanceof Error ? e.message : String(e) }),
    );
    return () => {
      cancelled = true;
      // Leaving the apps: the rest of the UI (the web admin) stays in English for now.
      setUiLanguage(undefined);
    };
  }, [api, reloads]);
  if (state.error) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{t('apps.error', { detail: state.error })}</MessageBarBody>
      </MessageBar>
    );
  }
  if (!state.settings) return <Spinner label={t('apps.loading')} />;
  return (
    <UserSettingsContext.Provider value={state.settings}>
      <ReloadUserSettingsContext.Provider value={reload}>
        <DisplayZoneContext.Provider value={state.settings.timeZone}>{children}</DisplayZoneContext.Provider>
      </ReloadUserSettingsContext.Provider>
    </UserSettingsContext.Provider>
  );
}
