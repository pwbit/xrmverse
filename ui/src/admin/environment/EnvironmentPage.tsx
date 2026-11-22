import {
  Badge,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Title3,
  tokens,
} from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { PageHeader } from '../../shell/layout/PageHeader';
import { LookSettings } from './LookSettings';

/** `GET /admin/health` (`HealthStatus`): 200 when Healthy, 503 otherwise; both carry this body. */
interface Health {
  status: string;
  version: string;
  organization: string;
  database: string;
  schemaVersion: number;
  pendingMigrations: number;
  asyncQueue: { waiting: number; inProgress: number };
  authMode: string;
}

/** `GET /admin/doctor`: `status` is `ok`, `warning` or `error`. */
interface DoctorCheck {
  name: string;
  status: string;
  detail: string;
}

const STATUS_COLOR = { Healthy: 'success', Degraded: 'warning', Unhealthy: 'danger' } as const;
const CHECK_COLOR = { ok: 'success', warning: 'warning', error: 'danger' } as const;
const CHECK_LABEL = { ok: 'environment.check.ok', warning: 'environment.check.warning', error: 'environment.check.error' } as const;

/** The organization's auto-save switch (#117): `organization.isautosaveenabled`. */
interface AutoSave {
  id: string;
  on: boolean;
}

export function EnvironmentPage() {
  const { api } = useAuth();
  const notify = useNotify();
  const [health, setHealth] = useState<Health>();
  const [checks, setChecks] = useState<DoctorCheck[]>();
  const [healthError, setHealthError] = useState<string>();
  const [doctorError, setDoctorError] = useState<string>();
  const [autoSave, setAutoSave] = useState<AutoSave>();
  const [autoSaveBusy, setAutoSaveBusy] = useState(false);

  useEffect(() => {
    if (!api) return;
    let live = true;
    api
      .admin<Health>('GET', 'health', undefined, { acceptStatus: [503] })
      .then((h) => live && setHealth(h))
      .catch((e: unknown) => {
        if (!live) return;
        setHealthError(e instanceof Error ? e.message : String(e));
        notify.error(e);
      });
    api
      .admin<{ checks: DoctorCheck[] }>('GET', 'doctor')
      .then((d) => live && setChecks(d.checks))
      .catch((e: unknown) => {
        if (!live) return;
        setDoctorError(e instanceof Error ? e.message : String(e));
        notify.error(e);
      });
    api
      .get<{ value: { organizationid: string; isautosaveenabled?: boolean | null }[] }>('organizations?$select=organizationid,isautosaveenabled&$top=1')
      .then((r) => {
        const row = r.value[0];
        if (live && row) setAutoSave({ id: row.organizationid, on: row.isautosaveenabled === true });
      })
      .catch((e: unknown) => live && notify.error(e));
    return () => {
      live = false;
    };
  }, [api, notify]);

  const writeAutoSave = async (on: boolean) => {
    if (!api || !autoSave) return;
    setAutoSaveBusy(true);
    try {
      await api.patch(`organizations(${autoSave.id})`, { isautosaveenabled: on });
      setAutoSave({ ...autoSave, on });
    } catch (e: unknown) {
      notify.error(e);
    } finally {
      setAutoSaveBusy(false);
    }
  };

  const statusLabel = health
    ? health.status in STATUS_COLOR
      ? t(`environment.status.${health.status as keyof typeof STATUS_COLOR}` as const)
      : health.status
    : '';

  return (
    <section>
      <PageHeader title={t('environment.title')} />
      {healthError && (
        <MessageBar intent="error">
          <MessageBarBody>{t('environment.healthFailed', { detail: healthError })}</MessageBarBody>
        </MessageBar>
      )}
      {!health && !healthError && <Spinner label={t('environment.loading')} />}
      {health && (
        <dl>
          <dt>{t('environment.status')}</dt>
          <dd>
            <Badge appearance="filled" color={STATUS_COLOR[health.status as keyof typeof STATUS_COLOR] ?? 'informative'}>
              {statusLabel}
            </Badge>
          </dd>
          <dt>{t('environment.version')}</dt>
          <dd>{health.version}</dd>
          <dt>{t('environment.organization')}</dt>
          <dd>{health.organization}</dd>
          <dt>{t('environment.database')}</dt>
          <dd>{health.database}</dd>
          <dt>{t('environment.schema')}</dt>
          <dd>{health.schemaVersion}</dd>
          <dt>{t('environment.pendingMigrations')}</dt>
          <dd>{health.pendingMigrations}</dd>
          <dt>{t('environment.authMode')}</dt>
          <dd>{health.authMode}</dd>
          <dt>{t('environment.asyncWaiting')}</dt>
          <dd data-testid="async-waiting">{health.asyncQueue.waiting}</dd>
          <dt>{t('environment.asyncInProgress')}</dt>
          <dd data-testid="async-inprogress">{health.asyncQueue.inProgress}</dd>
        </dl>
      )}
      {autoSave && (
        <>
          <Title3 as="h3" block>{t('environment.forms.title')}</Title3>
          <Switch
            label={t('environment.forms.autoSave')}
            checked={autoSave.on}
            disabled={autoSaveBusy}
            onChange={(_, d) => void writeAutoSave(d.checked)}
          />
        </>
      )}
      <LookSettings />
      <Title3 as="h3" block>{t('environment.doctor.title')}</Title3>
      {doctorError && (
        <MessageBar intent="error">
          <MessageBarBody>{t('environment.doctorFailed', { detail: doctorError })}</MessageBarBody>
        </MessageBar>
      )}
      {!checks && !doctorError && <Spinner label={t('environment.loading')} />}
      {checks && checks.length === 0 && <p>{t('environment.doctor.empty')}</p>}
      {checks && checks.length > 0 && (
        <Table aria-label={t('environment.doctor.title')}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{t('environment.check.name')}</TableHeaderCell>
              <TableHeaderCell>{t('environment.check.status')}</TableHeaderCell>
              <TableHeaderCell>{t('environment.check.detail')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {checks.map((c) => (
              <TableRow key={c.name} data-status={c.status} style={c.status === 'error' ? { background: tokens.colorStatusDangerBackground1 } : undefined}>
                <TableCell>{c.name}</TableCell>
                <TableCell>
                  <Badge appearance="filled" color={CHECK_COLOR[c.status as keyof typeof CHECK_COLOR] ?? 'informative'}>
                    {c.status in CHECK_LABEL ? t(CHECK_LABEL[c.status as keyof typeof CHECK_LABEL]) : c.status}
                  </Badge>
                </TableCell>
                <TableCell>{c.detail}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </section>
  );
}
