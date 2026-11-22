import {
  Button,
  Checkbox,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Select,
  Spinner,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Title3,
} from '@fluentui/react-components';
import { useCallback, useEffect, useRef, useState } from 'react';
import { XrmError } from '../../shell/api/errors';
import { useAuth } from '../../shell/auth/AuthProvider';
import { useAccess } from '../../shell/auth/roles';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { useDebounced } from '../common/useDebounced';
import { useLoad } from '../tables/useLoad';
import {
  auditDetails,
  changesOf,
  listAudits,
  loadOrganization,
  setOrgAuditing,
  setOrgRetention,
  type AuditDetail,
  type AuditRow,
} from './auditApi';
import { PageHeader } from '../../shell/layout/PageHeader';

const OPERATIONS = [1, 2, 3] as const;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const when = (iso: string) => (iso ? new Date(iso).toLocaleString() : '');
const message = (e: unknown) => (e instanceof XrmError ? e.serverMessage : e instanceof Error ? e.message : String(e));

/** The organization's audit settings and the audit log (newest first, filtered by table, operation and user) with a detail drawer. */
export function AuditPage() {
  const { api } = useAuth();
  const notify = useNotify();
  const { level } = useAccess();
  const isAdmin = level === 'admin';

  // ---- organization settings ----
  const org = useLoad('org', (a) => loadOrganization(a));
  const [retention, setRetention] = useState('');
  const [forever, setForever] = useState(false);
  const [retentionError, setRetentionError] = useState(false);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    if (!org.data) return;
    setForever(org.data.retention === -1);
    setRetention(org.data.retention === -1 ? '' : String(org.data.retention));
  }, [org.data]);

  async function toggle(enabled: boolean) {
    if (!api) return;
    setBusy(true);
    try {
      await setOrgAuditing(api, enabled);
      notify.success(t('audit.org.saved'));
      org.reload();
    } catch (e) {
      notify.error(e);
    } finally {
      setBusy(false);
    }
  }

  async function saveRetention() {
    if (!api) return;
    const days = forever ? -1 : /^\d+$/.test(retention.trim()) ? Number(retention.trim()) : NaN;
    if (!forever && !(days >= 1 && days <= 365000)) {
      setRetentionError(true);
      return;
    }
    setRetentionError(false);
    setBusy(true);
    try {
      await setOrgRetention(api, days);
      notify.success(t('audit.org.saved'));
      org.reload();
    } catch (e) {
      notify.error(e);
    } finally {
      setBusy(false);
    }
  }

  // ---- the log ----
  const [table, setTable] = useState('');
  const [operation, setOperation] = useState('');
  const [user, setUser] = useState('');
  const debouncedTable = useDebounced(table.trim(), 300);
  const debouncedUser = useDebounced(user.trim(), 300);
  const userInvalid = debouncedUser !== '' && !GUID.test(debouncedUser);
  const userId = debouncedUser !== '' && !userInvalid ? debouncedUser : undefined;
  const [rows, setRows] = useState<AuditRow[]>([]);
  const [nextLink, setNextLink] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<{ status: number; detail: string }>();
  const generation = useRef(0);

  const fetchRows = useCallback(
    async (page: string | undefined) => {
      if (!api) return;
      const mine = ++generation.current;
      setLoading(true);
      setError(undefined);
      try {
        const result = await listAudits(api, {
          table: debouncedTable || undefined,
          operation: operation ? Number(operation) : undefined,
          userId,
          page,
        });
        if (mine !== generation.current) return;
        setRows((prev) => (page ? [...prev, ...result.rows] : result.rows));
        setNextLink(result.nextLink);
      } catch (e) {
        if (mine !== generation.current) return;
        setRows([]);
        setNextLink(undefined);
        setError({ status: e instanceof XrmError ? e.status : 0, detail: message(e) });
      } finally {
        if (mine === generation.current) setLoading(false);
      }
    },
    [api, debouncedTable, operation, userId],
  );
  useEffect(() => {
    void fetchRows(undefined);
  }, [fetchRows]);

  // ---- the detail drawer ----
  const [selected, setSelected] = useState<AuditRow>();
  const [detail, setDetail] = useState<AuditDetail>();
  const [detailError, setDetailError] = useState<string>();
  useEffect(() => {
    setDetail(undefined);
    setDetailError(undefined);
    if (!selected || !api) return;
    let live = true;
    auditDetails(api, selected.auditid)
      .then((d) => live && setDetail(d))
      .catch((e: unknown) => live && setDetailError(message(e)));
    return () => {
      live = false;
    };
  }, [api, selected]);
  const changes = detail ? changesOf(detail) : [];

  return (
    <section>
      <PageHeader title={t('audit.title')} />
      <Title3 as="h3" block>{t('audit.org.title')}</Title3>
      {org.error && (
        <MessageBar intent="error">
          <MessageBarBody>{t('audit.org.loadFailed', { detail: org.error })}</MessageBarBody>
        </MessageBar>
      )}
      {!isAdmin && <p>{t('audit.org.readOnly')}</p>}
      {org.loading && !org.data && <Spinner size="tiny" label={t('audit.loading')} />}
      {org.data && (
        <div>
          <Switch
            label={t('audit.org.enabled')}
            checked={org.data.enabled}
            disabled={!isAdmin || busy}
            onChange={(_, d) => void toggle(d.checked)}
          />
          <Field
            label={t('audit.org.retention')}
            validationState={retentionError ? 'error' : 'none'}
            validationMessage={retentionError ? t('audit.org.retentionInvalid') : undefined}
            hint={t('audit.org.retentionNote')}
          >
            <Input
              type="number"
              value={retention}
              disabled={!isAdmin || busy || forever}
              onChange={(_, d) => setRetention(d.value)}
            />
          </Field>
          <Checkbox
            label={t('audit.org.forever')}
            checked={forever}
            disabled={!isAdmin || busy}
            onChange={(_, d) => setForever(d.checked === true)}
          />
          <Button appearance="primary" disabled={!isAdmin || busy} onClick={() => void saveRetention()}>
            {t('audit.org.saveRetention')}
          </Button>
        </div>
      )}

      <Title3 as="h3" block>{t('audit.list.title')}</Title3>
      <Field label={t('audit.filter.table')}>
        <Input value={table} onChange={(_, d) => setTable(d.value)} />
      </Field>
      <Field label={t('audit.filter.operation')}>
        <Select value={operation} onChange={(_, d) => setOperation(d.value)}>
          <option value="">{t('audit.filter.operation.all')}</option>
          {OPERATIONS.map((o) => (
            <option key={o} value={String(o)}>
              {t(`audit.operation.${o}` as const)}
            </option>
          ))}
        </Select>
      </Field>
      <Field
        label={t('audit.filter.user')}
        validationState={userInvalid ? 'error' : 'none'}
        validationMessage={userInvalid ? t('audit.filter.userInvalid') : undefined}
      >
        <Input value={user} onChange={(_, d) => setUser(d.value)} />
      </Field>
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error.status === 403 ? t('audit.forbidden', { detail: error.detail }) : t('audit.loadFailed', { detail: error.detail })}</MessageBarBody>
        </MessageBar>
      )}
      {!loading && !error && rows.length === 0 && <p>{t('audit.empty')}</p>}
      {rows.length > 0 && (
        <Table aria-label={t('audit.list.title')}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{t('audit.col.date')}</TableHeaderCell>
              <TableHeaderCell>{t('audit.col.table')}</TableHeaderCell>
              <TableHeaderCell>{t('audit.col.record')}</TableHeaderCell>
              <TableHeaderCell>{t('audit.col.operation')}</TableHeaderCell>
              <TableHeaderCell>{t('audit.col.action')}</TableHeaderCell>
              <TableHeaderCell>{t('audit.col.user')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((r) => (
              <TableRow key={r.auditid}>
                <TableCell>
                  <Button appearance="transparent" aria-label={t('audit.row.open')} onClick={() => setSelected(r)}>
                    {when(r.createdon)}
                  </Button>
                </TableCell>
                <TableCell>{r.table}</TableCell>
                <TableCell>{r.objectId ?? ''}</TableCell>
                <TableCell>{r.operation}</TableCell>
                <TableCell>{r.action}</TableCell>
                <TableCell>{r.user ?? ''}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {loading && <Spinner label={t('audit.loading')} />}
      {nextLink && !loading && <Button onClick={() => void fetchRows(nextLink)}>{t('audit.loadMore')}</Button>}

      <OverlayDrawer position="end" size="large" open={!!selected} onOpenChange={(_, d) => !d.open && setSelected(undefined)}>
        {selected && (
          <>
            <DrawerHeader>
              <DrawerHeaderTitle
                action={
                  <Button appearance="subtle" onClick={() => setSelected(undefined)}>
                    {t('audit.detail.close')}
                  </Button>
                }
              >
                {t('audit.detail.title')}: {selected.action} {selected.table}
              </DrawerHeaderTitle>
            </DrawerHeader>
            <DrawerBody>
              <p>
                {when(selected.createdon)} {selected.user ?? ''}
              </p>
              {detailError && (
                <MessageBar intent="error">
                  <MessageBarBody>{t('audit.detail.loadFailed', { detail: detailError })}</MessageBarBody>
                </MessageBar>
              )}
              {!detail && !detailError && <Spinner size="tiny" label={t('audit.loading')} />}
              {detail && changes.length === 0 && <p>{t('audit.detail.empty')}</p>}
              {changes.length > 0 && (
                <Table aria-label={t('audit.detail.title')}>
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell>{t('audit.detail.col.column')}</TableHeaderCell>
                      <TableHeaderCell>{t('audit.detail.col.old')}</TableHeaderCell>
                      <TableHeaderCell>{t('audit.detail.col.new')}</TableHeaderCell>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {changes.map((c, i) => (
                      <TableRow key={`${c.column}-${i}`}>
                        <TableCell>{c.column}</TableCell>
                        <TableCell>{c.oldValue || t('audit.history.empty.value')}</TableCell>
                        <TableCell>{c.newValue || t('audit.history.empty.value')}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </DrawerBody>
          </>
        )}
      </OverlayDrawer>
    </section>
  );
}
