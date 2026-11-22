import { Field, makeStyles, MessageBar, MessageBarBody, Select, Spinner, Title2, tokens } from '@fluentui/react-components';
import { useEffect, useMemo, useState } from 'react';
import { Navigate, useNavigate } from 'react-router-dom';
import type { DashboardModel } from '../model/dashboard';
import { parseDashboard } from '../parsers/chartXml';
import { normaliseId } from '../parsers/formXml';
import { useRenderer } from '../renderers/RendererContext';
import { dashboardEnv } from '../renderers/classic/dashboard/DashboardLayout';
import { XrmError } from '../shell/api/errors';
import { useAuth } from '../shell/auth/AuthProvider';
import { t } from '../shell/i18n/t';
import { loadDashboards, type DashboardEntry } from './loadDashboards';
import { mainAspxUrl } from './mainAspx';

const useStyles = makeStyles({
  header: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalL,
    marginBlockEnd: tokens.spacingVerticalM,
  },
});

const DASHBOARD_FORM = 0;

/** The `systemform` columns a dashboard needs. */
interface DashboardRow {
  systemformid: string;
  name: string;
  type: number;
  objecttypecode: string;
  formxml?: string | null;
}

type ListState = { status: 'loading' } | { status: 'ready'; dashboards: DashboardEntry[] } | { status: 'error'; error: string };
type DashboardState =
  | { id: string; status: 'loading' }
  | { id: string; status: 'ready'; dashboard: DashboardModel }
  | { id: string; status: 'notFound' }
  | { id: string; status: 'error'; error: string };

const errorText = (e: unknown) => (e instanceof Error ? e.message : String(e));

/** The dashboard `id` (a `systemform` of type 0 and table `none`); undefined when there is none. */
async function readDashboard(api: NonNullable<ReturnType<typeof useAuth>['api']>, id: string, lcid: number): Promise<DashboardModel | undefined> {
  let row: DashboardRow;
  try {
    row = await api.get<DashboardRow>(`systemforms(${id})?$select=systemformid,name,type,objecttypecode,formxml`);
  } catch (e) {
    if (e instanceof XrmError && e.status === 404) return undefined;
    throw e;
  }
  if (row.type !== DASHBOARD_FORM || row.objecttypecode !== 'none' || !row.formxml) return undefined;
  return { ...parseDashboard(row.formxml, lcid), id: normaliseId(row.systemformid), name: row.name };
}

export interface DashboardPageProps {
  appId: string;
  /** The dashboard of the URL (`id`); without one the app's first dashboard opens. */
  id?: string;
  lcid: number;
}

/**
 * `pagetype=dashboard`: a system dashboard of the app, laid out as a form, with a selector of the app's dashboards. Without
 * an `id` the first dashboard of the app opens (the URL then names it); an app without dashboards says so.
 */
export function DashboardPage({ appId, id, lcid }: DashboardPageProps) {
  const styles = useStyles();
  const { DashboardLayout } = useRenderer();
  const { api } = useAuth();
  const navigate = useNavigate();
  const [list, setList] = useState<ListState>({ status: 'loading' });
  const [current, setCurrent] = useState<DashboardState | undefined>();

  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    setList({ status: 'loading' });
    loadDashboards(api, appId).then(
      (dashboards) => !cancelled && setList({ status: 'ready', dashboards }),
      (e: unknown) => !cancelled && setList({ status: 'error', error: errorText(e) }),
    );
    return () => {
      cancelled = true;
    };
  }, [api, appId]);

  useEffect(() => {
    if (!api || !id) return;
    let cancelled = false;
    setCurrent({ id, status: 'loading' });
    readDashboard(api, id, lcid).then(
      (dashboard) => !cancelled && setCurrent(dashboard ? { id, status: 'ready', dashboard } : { id, status: 'notFound' }),
      (e: unknown) => !cancelled && setCurrent({ id, status: 'error', error: errorText(e) }),
    );
    return () => {
      cancelled = true;
    };
  }, [api, id, lcid]);

  // One set of shared metadata reads per dashboard shown (`id` renews the cache).
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const env = useMemo(() => dashboardEnv(lcid, appId, new Map()), [lcid, appId, id]);

  if (!id) {
    if (list.status === 'loading') return <Spinner label={t('dashboard.loading')} />;
    if (list.status === 'error') return <Notice intent="error" text={t('dashboard.error', { detail: list.error })} />;
    const first = list.dashboards[0];
    if (!first) return <Notice intent="info" text={t('dashboard.noDashboards')} />;
    return <Navigate to={mainAspxUrl({ appId, pageType: 'dashboard', id: first.id })} replace />;
  }

  const shown = current?.id === id ? current : undefined;
  const listed = list.status === 'ready' ? list.dashboards : [];
  // The dashboard of the URL is offered even when the app does not list it, so that the selector shows it.
  const options = shown?.status === 'ready' && !listed.some((d) => d.id === id) ? [...listed, { id, name: shown.dashboard.name }] : listed;

  return (
    <div>
      <div className={styles.header}>
        {shown?.status === 'ready' && shown.dashboard.name && <Title2 as="h1">{shown.dashboard.name}</Title2>}
        {options.length > 0 && (
          <Field label={t('dashboard.selector')} orientation="horizontal">
            <Select value={options.some((d) => d.id === id) ? id : ''} onChange={(_, d) => d.value && navigate(mainAspxUrl({ appId, pageType: 'dashboard', id: d.value }))}>
              {!options.some((d) => d.id === id) && <option value="" disabled />}
              {options.map((d) => (
                <option key={d.id} value={d.id}>
                  {d.name}
                </option>
              ))}
            </Select>
          </Field>
        )}
      </div>
      {!shown || shown.status === 'loading' ? (
        <Spinner label={t('dashboard.loading')} />
      ) : shown.status === 'notFound' ? (
        <Notice intent="warning" text={t('dashboard.notFound', { id })} />
      ) : shown.status === 'error' ? (
        <Notice intent="error" text={t('dashboard.error', { detail: shown.error })} />
      ) : (
        <DashboardLayout key={id} dashboard={shown.dashboard} env={env} appId={appId} lcid={lcid} />
      )}
    </div>
  );
}

function Notice({ intent, text }: { intent: 'info' | 'warning' | 'error'; text: string }) {
  return (
    <MessageBar intent={intent}>
      <MessageBarBody>{text}</MessageBarBody>
    </MessageBar>
  );
}
