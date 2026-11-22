import {
  Button,
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
  Title2,
} from '@fluentui/react-components';
import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../common/rowSelection';
import {
  deleteRule,
  jobEnded,
  jobSucceeded,
  loadJob,
  loadRules,
  loadSwitches,
  loadTables,
  publishRule,
  setSwitch,
  unpublishRule,
  type DuplicateRule,
  type OrgSwitch,
  type OrgSwitches,
  type TableChoice,
} from './duplicateRulesApi';
import { RuleDialog } from './RuleDialog';
import { PageHeader } from '../../shell/layout/PageHeader';
import { DeleteIcon, NewIcon } from '../../shell/icons';

const messageOf = (e: unknown) => (e instanceof Error ? e.message : String(e));
const POLL_MS = 1000;

const SWITCHES: { column: OrgSwitch; label: 'duplicates.switch.enabled' | 'duplicates.switch.createUpdate' | 'duplicates.switch.import' | 'duplicates.switch.offline' }[] = [
  { column: 'isduplicatedetectionenabled', label: 'duplicates.switch.enabled' },
  { column: 'isduplicatedetectionenabledforonlinecreateupdate', label: 'duplicates.switch.createUpdate' },
  { column: 'isduplicatedetectionenabledforimport', label: 'duplicates.switch.import' },
  { column: 'isduplicatedetectionenabledforofflinesync', label: 'duplicates.switch.offline' },
];

interface Job {
  ruleId: string;
  name: string;
  jobId: string;
  state: number;
}

/**
 * Duplicate detection: the organization's switches, the rules with their status, and publishing. The server judges every value;
 * its 400 text is shown as it is.
 */
export function DuplicateRulesPage() {
  const { api } = useAuth();
  const notify = useNotify();
  const [switches, setSwitches] = useState<OrgSwitches>();
  const [rules, setRules] = useState<DuplicateRule[]>();
  const [tables, setTables] = useState<TableChoice[]>();
  const [loadError, setLoadError] = useState<string>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState<DuplicateRule | 'new'>();
  const [unpublishing, setUnpublishing] = useState<DuplicateRule>();
  const [deleting, setDeleting] = useState<DuplicateRule>();
  const [job, setJob] = useState<Job>();
  const selection = useRowSelection(rules ?? [], (r) => r.id);
  const sel = selection.selected;

  const reload = useCallback(
    async (live: () => boolean = () => true) => {
      if (!api) return;
      try {
        const [s, r, x] = await Promise.all([loadSwitches(api), loadRules(api), loadTables(api)]);
        if (!live()) return;
        setSwitches(s);
        setRules(r);
        setTables(x);
      } catch (e) {
        if (live()) setLoadError(messageOf(e));
      }
    },
    [api],
  );

  useEffect(() => {
    let live = true;
    void reload(() => live);
    return () => {
      live = false;
    };
  }, [reload]);

  /** Runs one write, then reads everything again, so the page shows what the server stored. */
  async function write(action: () => Promise<void>, done?: string) {
    setBusy(true);
    setError(undefined);
    try {
      await action();
      if (done) notify.success(done);
      await reload();
    } catch (e) {
      setError(messageOf(e));
    } finally {
      setBusy(false);
    }
  }

  // Follows the publish job until it ends, then reads the rules again.
  const jobId = job?.jobId;
  useEffect(() => {
    if (!api || !jobId) return;
    let live = true;
    let timer: ReturnType<typeof setTimeout>;
    const poll = async () => {
      try {
        const state = await loadJob(api, jobId);
        if (!live) return;
        setJob((j) => (j && j.jobId === jobId ? { ...j, state: state.state } : j));
        if (jobEnded(state)) {
          const name = job?.name ?? '';
          if (jobSucceeded(state)) notify.success(t('duplicates.job.done', { name }));
          else setError(t('duplicates.job.failed', { name, detail: state.message ?? String(state.status) }));
          setJob(undefined);
          await reload();
          return;
        }
      } catch (e) {
        if (!live) return;
        setError(messageOf(e));
        setJob(undefined);
        return;
      }
      timer = setTimeout(() => void poll(), POLL_MS);
    };
    void poll();
    return () => {
      live = false;
      clearTimeout(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, jobId]);

  async function publish(rule: DuplicateRule) {
    if (!api) return;
    setBusy(true);
    setError(undefined);
    try {
      const id = await publishRule(api, rule.id);
      setJob({ ruleId: rule.id, name: rule.name, jobId: id, state: 0 });
      await reload();
    } catch (e) {
      setError(messageOf(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section>
      <PageHeader
        title={t('nav.duplicateRules')}
        actions={
          <>
            <Button appearance="primary" icon={<NewIcon />} disabled={busy || !(switches && rules && tables)} onClick={() => setEditing('new')}>
              {t('duplicates.new')}
            </Button>
            {sel && (
              <>
                <RowCommand disabled={busy} aria-label={t('duplicates.edit', { name: sel.name })} onClick={() => setEditing(sel)}>
                  {t('duplicates.editButton')}
                </RowCommand>
                <RowCommand disabled={busy || sel.status === 1} aria-label={t('duplicates.publish', { name: sel.name })} onClick={() => void publish(sel)}>
                  {t('duplicates.publishButton')}
                </RowCommand>
                <RowCommand disabled={busy || sel.status === 0} aria-label={t('duplicates.unpublish', { name: sel.name })} onClick={() => setUnpublishing(sel)}>
                  {t('duplicates.unpublishButton')}
                </RowCommand>
                <RowCommand icon={<DeleteIcon />} disabled={busy} aria-label={t('duplicates.delete', { name: sel.name })} onClick={() => setDeleting(sel)}>
                  {t('duplicates.deleteButton')}
                </RowCommand>
              </>
            )}
          </>
        }
      />
      <p>{t('duplicates.intro')}</p>
      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{t('duplicates.loadFailed', { detail: loadError })}</MessageBarBody>
        </MessageBar>
      )}
      {!(switches && rules && tables) && !loadError && <Spinner label={t('duplicates.loading')} />}
      {switches && rules && tables && (
        <>
          <div>
            {SWITCHES.map((s) => (
              <Switch
                key={s.column}
                label={t(s.label)}
                checked={switches.values[s.column]}
                disabled={busy}
                onChange={(_, d) => void write(() => setSwitch(api!, switches.id, s.column, d.checked))}
              />
            ))}
            <p>{t('duplicates.switch.stored')}</p>
          </div>
          {error && (
            <MessageBar intent="error">
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          )}
          {job && (
            <MessageBar intent="info">
              <MessageBarBody>{t('duplicates.job', { name: job.name, state: t(`duplicates.job.${job.state}` as 'duplicates.job.0') })}</MessageBarBody>
            </MessageBar>
          )}
          <Title2 as="h3" block>{t('duplicates.rules')}</Title2>
          {rules.length === 0 ? (
            <p>{t('duplicates.none')}</p>
          ) : (
            <Table aria-label={t('duplicates.rules')}>
              <TableHeader>
                <TableRow>
                  <SelectHeaderCell />
                  <TableHeaderCell>{t('duplicates.col.name')}</TableHeaderCell>
                  <TableHeaderCell>{t('duplicates.col.base')}</TableHeaderCell>
                  <TableHeaderCell>{t('duplicates.col.matching')}</TableHeaderCell>
                  <TableHeaderCell>{t('duplicates.col.status')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {rules.map((r) => (
                  <TableRow key={r.id} {...selectableRow(selection, r)}>
                    <SelectCell selection={selection} item={r} name={r.name} />
                    <TableCell>{r.name}</TableCell>
                    <TableCell>{r.baseTable}</TableCell>
                    <TableCell>{r.matchingTable}</TableCell>
                    <TableCell>{t(`duplicates.status.${r.status}` as 'duplicates.status.0')}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
          {editing && (
            <RuleDialog
              rule={editing === 'new' ? undefined : editing}
              tables={tables}
              onClose={() => setEditing(undefined)}
              onPartial={() => void reload()}
              onSaved={(name) => {
                setEditing(undefined);
                notify.success(t('duplicates.dialog.saved', { name }));
                void reload();
              }}
            />
          )}
        </>
      )}
      <ConfirmDialog
        open={!!unpublishing}
        title={t('duplicates.unpublishTitle')}
        body={unpublishing ? t('duplicates.unpublishBody', { name: unpublishing.name }) : ''}
        confirmLabel={t('duplicates.unpublishButton')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => {
          const r = unpublishing;
          setUnpublishing(undefined);
          if (r) void write(() => unpublishRule(api!, r.id), t('duplicates.unpublished', { name: r.name }));
        }}
        onCancel={() => setUnpublishing(undefined)}
      />
      <ConfirmDialog
        open={!!deleting}
        title={t('duplicates.deleteTitle')}
        body={deleting ? t('duplicates.deleteBody', { name: deleting.name }) : ''}
        confirmLabel={t('duplicates.deleteButton')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => {
          const r = deleting;
          setDeleting(undefined);
          if (r) void write(() => deleteRule(api!, r.id), t('duplicates.deleted', { name: r.name }));
        }}
        onCancel={() => setDeleting(undefined)}
      />
    </section>
  );
}
