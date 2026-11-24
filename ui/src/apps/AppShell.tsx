import { Body1, makeStyles, MessageBar, MessageBarBody, Spinner, tokens } from '@fluentui/react-components';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, Navigate, useLocation, useNavigate } from 'react-router-dom';
import { createDialogHost, DialogHostView } from '../client-api/DialogHost';
import type { FormContext } from '../client-api/formContext';
import { loadXrmContext, type CurrentUser, type OrgSettings } from '../client-api/globalContext';
import { runAppOnLoad } from '../client-api/xrmverse/appOnLoad';
import { createAppNotifications } from '../client-api/xrmverse/appNotifications';
import { createSidePanes } from '../client-api/xrmverse/sidePanes';
import { createXrmVerse, type AppRuntime } from '../client-api/xrmverse/xrmVerse';
import { createXrm, XrmHostContext, type XrmHost } from '../client-api/xrm';
import type { AppModel } from '../model/app';
import { localized, type SiteMapModel } from '../model/sitemap';
import { parseSiteMap } from '../parsers/siteMapXml';
import { Placeholder } from '../renderers/classic/Placeholder';
import { isCurrent, subAreaTarget } from '../renderers/classic/SiteMapNav';
import { modernRenderer } from '../renderers/modern';
import { useFilledIconStyles } from '../renderers/modern/filledIcons';
import { buildTheme } from '../renderers/modern/theme';
import { PresentationProvider } from '../renderers/modern/PresentationContext';
import { usePresentation } from '../renderers/modern/usePresentation';
import { RendererProvider, useRenderer } from '../renderers/RendererContext';
import { resolveColorScheme, resolveRenderer } from '../renderers/resolve';
import { ThemeScope } from '../renderers/ThemeScope';
import { useMediaQuery } from '../shell/layout/useMediaQuery';
import { xrmverseBrand } from '../shell/theme';
import type { XrmApi } from '../shell/api/client';
import { useAuth } from '../shell/auth/AuthProvider';
import { SessionCookieWarning } from '../shell/auth/SessionCookieWarning';
import { t } from '../shell/i18n/t';
import { AppHeader } from './AppHeader';
import { AppNotificationBar } from './AppNotificationBar';
import { DashboardPage } from './DashboardPage';
import { EntityListPage } from './EntityListPage';
import { knownApps } from './loadApps';
import { RecordDialog } from './RecordDialog';
import { SubgridFiltersProvider } from './record/SubgridFiltersContext';
import { RecordOpenerContext, type RecordOpenerEnv } from './record/recordOpener';
import { RecordPage } from './RecordPage';
import { SidePaneArea } from './SidePaneArea';
import { WebResourcePage } from './WebResourcePage';
import { parseMainAspx, type MainAspxState } from './mainAspx';
import { trimSiteMap } from './trimSiteMap';
import { useUserSettings } from './userSettings';

interface Loaded {
  app: AppModel;
  /** The app's sitemap, trimmed to what the user may see. */
  siteMap: SiteMapModel;
}

/** What the Client API's global context shows of the user and the organization. */
interface XrmContextData {
  user: CurrentUser;
  org: OrgSettings;
}

type LoadState = { status: 'loading' } | { status: 'noAccess' } | { status: 'error'; error: string } | ({ status: 'ready' } & Loaded & XrmContextData);

/** The caller's privilege names (`RetrieveUserPrivileges`; a System Administrator gets every privilege). */
async function userPrivileges(api: XrmApi, userId: string): Promise<Set<string>> {
  const result = await api.get<{ RolePrivileges: { PrivilegeName: string }[] }>(
    `systemusers(${userId})/Microsoft.Dynamics.CRM.RetrieveUserPrivileges()`,
  );
  return new Set(result.RolePrivileges.map((p) => p.PrivilegeName));
}

/** Logical name (lower case) to SchemaName and IsActivity, for `prv{Verb}{SchemaName}` (`prv{Verb}Activity` for an activity). */
async function entityTables(api: XrmApi): Promise<Record<string, { schemaName: string; isActivity: boolean }>> {
  const result = await api.get<{ value: { LogicalName: string; SchemaName: string; IsActivity?: boolean | null }[] }>(
    'EntityDefinitions?$select=LogicalName,SchemaName,IsActivity',
  );
  return Object.fromEntries(result.value.map((e) => [e.LogicalName.toLowerCase(), { schemaName: e.SchemaName, isActivity: e.IsActivity === true }]));
}

/** The app (when the user may open it) and its trimmed sitemap; `undefined` for a missing or forbidden app. */
async function loadApp(api: XrmApi, appId: string, userId: string): Promise<Loaded | undefined> {
  const app = (await knownApps(api)).find((a) => a.id.toLowerCase() === appId.toLowerCase());
  if (!app) return undefined;
  if (!app.siteMapId) return { app, siteMap: { areas: [] } };
  const [row, privileges, tables] = await Promise.all([
    api.get<{ sitemapxml?: string | null }>(`sitemaps(${app.siteMapId})?$select=sitemapxml`),
    userPrivileges(api, userId),
    entityTables(api),
  ]);
  const siteMap = row.sitemapxml ? parseSiteMap(row.sitemapxml) : { areas: [] };
  return { app, siteMap: trimSiteMap(siteMap, privileges, tables) };
}

const NO_NAMES: string[] = [];

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', minHeight: '100vh' },
  body: { display: 'flex', flexGrow: 1 },
  main: { flexGrow: 1, padding: '16px 24px', minWidth: 0 },
  link: { color: tokens.colorBrandForegroundLink },
  notice: { marginBottom: tokens.spacingVerticalM },
});

/** `/main.aspx`: the model-driven app shell (header, sitemap navigation and the page of the URL). */
export function AppShell() {
  const { search } = useLocation();
  const state = useMemo(() => parseMainAspx(search), [search]);
  if (!state.appId) return <Navigate to="/apps" replace />;
  return <AppShellLoader appId={state.appId} state={state} />;
}

function AppShellLoader({ appId, state }: { appId: string; state: MainAspxState }) {
  const styles = useStyles();
  // The renderer of an enclosing provider (classic by default) draws the classic look; tests inject theirs through it.
  const classic = useRenderer();
  const { api } = useAuth();
  const settings = useUserSettings();
  const { userId, lcid } = settings;
  const [load, setLoad] = useState<LoadState>({ status: 'loading' });
  const navigate = useNavigate();
  const navigateRef = useRef(navigate);
  navigateRef.current = navigate;
  // One dialog host per open app: the Client API's dialogs and progress indicator.
  const dialogs = useMemo(createDialogHost, []);

  // One runtime (side panes, notifications, `XrmVerse`) per open app. It is created and disposed inside one effect and held in
  // state, so React 18 StrictMode's mount/unmount/mount gives a fresh runtime and never reuses a disposed one. It is tagged
  // with its app id: during the render in which `appId` changes, the previous app's runtime is not used for the new app.
  const [runtimeOf, setRuntimeOf] = useState<{ appId: string; runtime: AppRuntime; closed: AbortSignal } | null>(null);
  useEffect(() => {
    const panes = createSidePanes();
    const notifications = createAppNotifications();
    const runtime: AppRuntime = {
      panes,
      notifications,
      xrmVerse: createXrmVerse(panes, notifications),
      dispose: () => {
        panes.dispose();
        notifications.dispose();
      },
    };
    // Aborted with the runtime: ends the app's OnLoad run and its script host.
    const closed = new AbortController();
    setRuntimeOf({ appId, runtime, closed: closed.signal });
    return () => {
      closed.abort();
      runtime.dispose();
    };
  }, [appId]);
  const runtime = runtimeOf?.appId === appId ? runtimeOf.runtime : undefined;

  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    // A reload of the user's settings (the Personalization dialog) reads the app again but keeps the shown app until the new
    // read is in: swapping in the loading screen would unmount the frame, the main area and every pane IFrame. Only another
    // app shows the loading screen.
    setLoad((previous) => (previous.status === 'ready' && previous.app.id.toLowerCase() === appId.toLowerCase() ? previous : { status: 'loading' }));
    // Scripts read the global context synchronously: it is loaded with the app, before any form opens.
    Promise.all([loadApp(api, appId, userId), loadXrmContext(api, settings)]).then(
      ([loaded, context]) => !cancelled && setLoad(loaded ? { status: 'ready', ...loaded, ...context } : { status: 'noAccess' }),
      (e: unknown) => !cancelled && setLoad({ status: 'error', error: e instanceof Error ? e.message : String(e) }),
    );
    return () => {
      cancelled = true;
    };
  }, [api, appId, userId, settings]);

  const ready = load.status === 'ready' ? load : undefined;

  // The look (UI-2a): the user's choice, else the app's, environment's or default; an unknown id gives classic and a notice.
  // Only the modern look reads the presentation overlay (classic ignores it).
  const readyApp = ready?.app;
  const resolved = useMemo(() => (readyApp ? resolveRenderer({ renderer: settings.renderer }, readyApp) : undefined), [readyApp, settings.renderer]);
  const modern = resolved?.renderer === 'modern';
  const presentation = usePresentation(modern ? resolved?.overlayName : undefined);
  const osDark = useMediaQuery('(prefers-color-scheme: dark)');
  const overlayScheme = modern ? presentation.model?.theme.colorScheme : undefined;
  const dark = resolveColorScheme(settings.colorScheme, overlayScheme, osDark) === 'dark';
  const modernTheme = useMemo(() => (modern ? buildTheme(presentation.model, dark) : undefined), [modern, presentation.model, dark]);
  const filledIcons = useFilledIconStyles();
  const xrmHost = useMemo((): XrmHost | null => {
    if (!api || !ready || !runtime) return null;
    const xrmVerse = runtime.xrmVerse;
    let current: FormContext | null = null;
    const xrm = createXrm({
      api,
      app: ready.app,
      user: ready.user,
      org: ready.org,
      navigate: (url) => navigateRef.current(url),
      dialogs,
      currentForm: () => current,
    });
    return {
      xrm,
      globalContext: (xrm.Utility.getGlobalContext as () => unknown)(),
      org: ready.org,
      install: (win) => {
        const w = win as unknown as { Xrm?: unknown; XrmVerse?: unknown };
        w.Xrm = xrm;
        w.XrmVerse = xrmVerse;
      },
      setCurrentForm: (fc) => {
        // A form that is left takes its progress indicator with it: a handler that threw before closing it must not leave a
        // full-screen overlay over the next page.
        if (!fc) dialogs.clearProgress();
        current = fc;
      },
      currentForm: () => current,
    };
    // A new load of the app gets a new Xrm; nothing else in `load` changes it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, ready?.app, ready?.user, ready?.org, dialogs, runtime]);

  // The app's OnLoad handlers: one run for each runtime (each time the app is opened), started after the app is ready. The page
  // never waits for them; navigation inside the app keeps the runtime, so it does not start another run.
  const onLoadStarted = useRef(new WeakSet<AppRuntime>());
  const closed = runtimeOf?.appId === appId ? runtimeOf.closed : undefined;
  useEffect(() => {
    if (!xrmHost || !runtime || !closed || !readyApp || onLoadStarted.current.has(runtime)) return;
    onLoadStarted.current.add(runtime);
    void runAppOnLoad(readyApp.appOnLoad, {
      install: xrmHost.install,
      globalContext: xrmHost.globalContext,
      notify: (n) => void runtime.notifications.add(n).catch(() => undefined),
      signal: closed,
    });
  }, [xrmHost, runtime, closed, readyApp]);

  // How the app's forms open related records (lookups, subgrids): a side pane unless the app says page.
  const openMode = ready?.app.recordOpenMode ?? 'sidepane';
  const readyAppId = ready?.app.id;
  const opener = useMemo((): RecordOpenerEnv | null => (readyAppId ? { appId: readyAppId, mode: openMode, dialogs } : null), [readyAppId, openMode, dialogs]);

  // `window.Xrm` while the app is open (web resources reach it as `parent.Xrm`); removed when the app closes.
  useEffect(() => {
    if (!xrmHost) return;
    const w = window as unknown as { Xrm?: unknown; XrmVerse?: unknown };
    w.Xrm = xrmHost.xrm;
    w.XrmVerse = runtime?.xrmVerse;
    return () => {
      if (w.Xrm === xrmHost.xrm) delete w.Xrm;
      if (w.XrmVerse === runtime?.xrmVerse) delete w.XrmVerse;
    };
  }, [xrmHost, runtime]);

  // Ruling R3: the plain header until the app and its overlay are resolved.
  if (load.status !== 'ready' || presentation.loading || !runtime) {
    return (
      <div className={styles.root}>
        <AppHeader />
        <SessionCookieWarning />
        <main className={styles.main}>
          {(load.status === 'loading' || load.status === 'ready') && <Spinner label={t('apps.opening')} />}
          {load.status === 'error' && (
            <MessageBar intent="error">
              <MessageBarBody>{t('apps.error', { detail: load.error })}</MessageBarBody>
            </MessageBar>
          )}
          {load.status === 'noAccess' && (
            <>
              <MessageBar intent="warning">
                <MessageBarBody>{t('apps.noAccess')}</MessageBarBody>
              </MessageBar>
              <Body1 block>
                <Link className={styles.link} to="/apps">
                  {t('apps.backToApps')}
                </Link>
              </Body1>
            </>
          )}
        </main>
      </div>
    );
  }

  const subAreas = load.siteMap.areas.flatMap((a) => a.groups.flatMap((g) => g.subAreas));
  if (state.pageType === '') {
    // The app home is the first subarea the user may see that opens inside the app.
    const home = subAreas.map((s) => subAreaTarget(s, load.app.id)).find((target) => 'state' in target);
    if (home && 'state' in home) return <Navigate to={home.href} replace />;
  }

  const renderer = modern ? modernRenderer : classic;
  const { AppFrame } = renderer;
  const notices = [resolved?.unknownRenderer ? t('apps.look.unknown', { id: resolved.unknownRenderer }) : undefined, modern ? presentation.notice : undefined].filter(
    (n): n is string => !!n,
  );
  const frame = (
    <SubgridFiltersProvider names={load.app.subgridFilters ?? NO_NAMES}>
    <XrmHostContext.Provider value={xrmHost}>
     <RecordOpenerContext.Provider value={opener}>
      <AppFrame
        appName={load.app.name}
        siteMap={load.siteMap}
        lcid={lcid}
        appId={load.app.id}
        current={state}
        overlays={<DialogHostView host={dialogs} renderRecord={(options, done) => <RecordDialog app={load.app} options={options} done={done} />} />}
        whiteLabel={modern ? presentation.model?.whiteLabel : undefined}
        appBar={<AppNotificationBar store={runtime.notifications} />}
        sidePanes={<SidePaneArea store={runtime.panes} />}
      >
        {notices.map((notice) => (
          <MessageBar key={notice} intent="warning" className={styles.notice}>
            <MessageBarBody>{notice}</MessageBarBody>
          </MessageBar>
        ))}
        {state.pageType === '' ? (
          <Body1 block>{t('apps.empty')}</Body1>
        ) : state.pageType === 'entitylist' ? (
          <EntityListPage
            key={state.etn}
            appId={load.app.id}
            etn={state.etn ?? ''}
            viewId={state.viewId}
            title={(() => {
              const sub = subAreas.find((s) => isCurrent(subAreaTarget(s, load.app.id), state));
              return sub ? localized(sub.titles, lcid, sub.id, sub.firstLcid) : (state.etn ?? '');
            })()}
          />
        ) : state.pageType === 'entityrecord' && state.etn ? (
          // Keyed by table only: the page keys each record's form itself, and keeps a create form mounted for the record it created.
          <RecordPage key={state.etn} app={load.app} etn={state.etn} id={state.id} formId={state.formId} parameters={state.extraqs} />
        ) : state.pageType === 'entityrecord' ? (
          <MessageBar intent="warning">
            <MessageBarBody>{t('record.noTable')}</MessageBarBody>
          </MessageBar>
        ) : state.pageType === 'dashboard' ? (
          <DashboardPage appId={load.app.id} id={state.id} lcid={lcid} />
        ) : state.pageType === 'webresource' ? (
          state.name ? (
            <WebResourcePage key={`${state.name}|${state.data ?? ''}`} name={state.name} data={state.data} />
          ) : (
            <MessageBar intent="warning">
              <MessageBarBody>{t('webresource.pageMissingName')}</MessageBarBody>
            </MessageBar>
          )
        ) : (
          <Placeholder pageType={state.pageType} />
        )}
      </AppFrame>
     </RecordOpenerContext.Provider>
    </XrmHostContext.Provider>
    </SubgridFiltersProvider>
  );
  const page = (
    <PresentationProvider model={modern ? presentation.model : undefined} modern={modern}>
      <RendererProvider renderer={renderer}>{frame}</RendererProvider>
    </PresentationProvider>
  );
  // Modern draws inside its own theme scope (overlay ramp, font, density, scheme); classic keeps the root theme.
  if (!modern || !modernTheme) return page;
  return (
    <ThemeScope theme={modernTheme} dark={dark} brand={presentation.model?.theme.brand ?? xrmverseBrand} className={filledIcons.root}>
      {page}
    </ThemeScope>
  );
}
