import {
  Badge,
  Button,
  Dropdown,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Option,
  SearchBox,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  tokens,
} from '@fluentui/react-components';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { ConfirmDialog } from '../../admin/common/ConfirmDialog';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../../admin/common/rowSelection';
import { errorText } from '../../shell/api/errors';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { ActivateIcon, DeactivateIcon, DeleteIcon, NewIcon, OpenIcon, RefreshIcon } from '../../shell/icons';
import { PageHeader } from '../../shell/layout/PageHeader';
import { useNotify } from '../../shell/notify/Notifications';
import { deleteWorkflow, listWorkflows, managedIds, setWorkflowState, type ProcessKind, type WorkflowSummary } from '../api/studioApi';
import type { NewProcessState } from '../editor/EditorPage';
import { NewProcessDialog, newProcessYaml, type NewProcess } from './NewProcessDialog';

const KINDS: ProcessKind[] = ['workflow', 'businessRule', 'action'];
const ALL = 'all';

const useStyles = makeStyles({
  filters: { display: 'flex', flexWrap: 'wrap', alignItems: 'end', gap: tokens.spacingHorizontalM, marginBottom: tokens.spacingVerticalM },
  name: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: tokens.spacingHorizontalS },
});

/**
 * `/studio/workflows`: the processes the user may read (`GET /authoring/workflows`), filtered by kind and by table and searched by name. The
 * command bar acts on the selected row, as in the web admin; the server decides every command (a refusal is shown as it is).
 */
export function WorkflowList() {
  const styles = useStyles();
  const { api } = useAuth();
  const notify = useNotify();
  const navigate = useNavigate();
  const [rows, setRows] = useState<WorkflowSummary[]>();
  const [managed, setManaged] = useState<Set<string>>(new Set());
  const [loadError, setLoadError] = useState<string>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [kind, setKind] = useState<ProcessKind | typeof ALL>(ALL);
  const [table, setTable] = useState<string>(ALL);
  const [search, setSearch] = useState('');
  const [deleting, setDeleting] = useState<WorkflowSummary>();
  const [creating, setCreating] = useState(false);

  const shown = useMemo(() => {
    const term = search.trim().toLowerCase();
    return (rows ?? []).filter(
      (r) => (kind === ALL || r.kind === kind) && (table === ALL || r.entity === table) && (!term || r.name.toLowerCase().includes(term)),
    );
  }, [rows, kind, table, search]);
  // The tables of the rows, the global actions' `none` first.
  const tables = useMemo(() => [...new Set((rows ?? []).map((r) => r.entity))].sort((a, b) => (a === 'none' ? -1 : b === 'none' ? 1 : a.localeCompare(b))), [rows]);
  const tableLabel = (entity: string) => (entity === 'none' ? t('studio.list.global') : entity);
  const selection = useRowSelection(shown, (r) => r.id);
  const sel = selection.selected;

  const reload = useCallback(
    async (live: () => boolean = () => true) => {
      if (!api) return;
      try {
        const list = await listWorkflows(api);
        if (!live()) return;
        setRows(list);
        setLoadError(undefined);
        // The Managed badge only: a failure leaves the rows without it.
        managedIds(
          api,
          list.map((r) => r.id),
        ).then(
          (ids) => live() && setManaged(ids),
          () => undefined,
        );
      } catch (e) {
        if (live()) setLoadError(errorText(e));
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

  async function write(action: () => Promise<unknown>, done: string) {
    setBusy(true);
    setError(undefined);
    try {
      await action();
      notify.success(done);
      await reload();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }

  function create(p: NewProcess) {
    setCreating(false);
    const state: NewProcessState = { yaml: newProcessYaml(p), name: p.name };
    navigate('/studio/workflows/new', { state });
  }

  return (
    <section>
      <PageHeader
        title={t('studio.list.title')}
        actions={
          <>
            <Button appearance="primary" icon={<NewIcon />} disabled={busy} onClick={() => setCreating(true)}>
              {t('studio.cmd.new')}
            </Button>
            <RowCommand icon={<RefreshIcon />} disabled={busy} onClick={() => void reload()}>
              {t('studio.cmd.refresh')}
            </RowCommand>
            {sel && (
              <>
                <RowCommand icon={<OpenIcon />} disabled={busy} onClick={() => navigate(`/studio/workflows/${sel.id}`)}>
                  {t('studio.cmd.open')}
                </RowCommand>
                <RowCommand
                  icon={<ActivateIcon />}
                  disabled={busy || sel.state === 'activated'}
                  onClick={() => void write(() => setWorkflowState(api!, sel.id, true), t('studio.done.activated', { name: sel.name }))}
                >
                  {t('studio.cmd.activate')}
                </RowCommand>
                <RowCommand
                  icon={<DeactivateIcon />}
                  disabled={busy || sel.state === 'draft'}
                  onClick={() => void write(() => setWorkflowState(api!, sel.id, false), t('studio.done.deactivated', { name: sel.name }))}
                >
                  {t('studio.cmd.deactivate')}
                </RowCommand>
                <RowCommand icon={<DeleteIcon />} disabled={busy} onClick={() => setDeleting(sel)}>
                  {t('studio.cmd.delete')}
                </RowCommand>
              </>
            )}
          </>
        }
      />
      <div className={styles.filters}>
        <Field label={t('studio.list.kindFilter')}>
          <Dropdown
            value={kind === ALL ? t('studio.list.allKinds') : t(`studio.kind.${kind}`)}
            selectedOptions={[kind]}
            onOptionSelect={(_, d) => d.optionValue && setKind(d.optionValue as ProcessKind | typeof ALL)}
          >
            <Option value={ALL}>{t('studio.list.allKinds')}</Option>
            {KINDS.map((k) => (
              <Option key={k} value={k}>
                {t(`studio.kind.${k}`)}
              </Option>
            ))}
          </Dropdown>
        </Field>
        <Field label={t('studio.list.tableFilter')}>
          <Dropdown
            value={table === ALL ? t('studio.list.allTables') : tableLabel(table)}
            selectedOptions={[table]}
            onOptionSelect={(_, d) => d.optionValue && setTable(d.optionValue)}
          >
            <Option value={ALL}>{t('studio.list.allTables')}</Option>
            {tables.map((e) => (
              <Option key={e} value={e}>
                {tableLabel(e)}
              </Option>
            ))}
          </Dropdown>
        </Field>
        <Field label={t('studio.list.search')}>
          <SearchBox value={search} onChange={(_, d) => setSearch(d.value)} />
        </Field>
      </div>
      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{t('studio.list.loadFailed', { detail: loadError })}</MessageBarBody>
        </MessageBar>
      )}
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {!rows && !loadError && <Spinner label={t('studio.list.loading')} />}
      {rows && rows.length === 0 && <p>{t('studio.list.none')}</p>}
      {rows && rows.length > 0 && (
        <Table aria-label={t('studio.list.title')}>
          <TableHeader>
            <TableRow>
              <SelectHeaderCell />
              <TableHeaderCell>{t('studio.col.name')}</TableHeaderCell>
              <TableHeaderCell>{t('studio.col.kind')}</TableHeaderCell>
              <TableHeaderCell>{t('studio.col.table')}</TableHeaderCell>
              <TableHeaderCell>{t('studio.col.mode')}</TableHeaderCell>
              <TableHeaderCell>{t('studio.col.state')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {shown.map((r) => (
              <TableRow key={r.id} {...selectableRow(selection, r)}>
                <SelectCell selection={selection} item={r} name={r.name} />
                <TableCell>
                  <span className={styles.name}>
                    <Link to={`/studio/workflows/${r.id}`}>{r.name}</Link>
                    {managed.has(r.id) && (
                      <Badge appearance="outline" color="informative">
                        {t('studio.list.managed')}
                      </Badge>
                    )}
                    {r.unsupportedSteps > 0 && (
                      <Badge appearance="tint" color="warning">
                        {r.unsupportedSteps === 1 ? t('studio.list.unsupportedOne') : t('studio.list.unsupported', { count: r.unsupportedSteps })}
                      </Badge>
                    )}
                  </span>
                </TableCell>
                <TableCell>{t(`studio.kind.${r.kind}`)}</TableCell>
                <TableCell>{tableLabel(r.entity)}</TableCell>
                <TableCell>{r.mode ? t(`studio.mode.${r.mode}`) : ''}</TableCell>
                <TableCell>{t(`studio.state.${r.state}`)}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {deleting && (
        <ConfirmDialog
          open
          title={t('studio.delete.title')}
          body={t('studio.delete.body', { name: deleting.name })}
          confirmLabel={t('studio.delete.confirm')}
          cancelLabel={t('studio.new.cancel')}
          onCancel={() => setDeleting(undefined)}
          onConfirm={() => {
            const target = deleting;
            setDeleting(undefined);
            void write(() => deleteWorkflow(api!, target.id), t('studio.done.deleted', { name: target.name }));
          }}
        />
      )}
      {creating && <NewProcessDialog onCreate={create} onCancel={() => setCreating(false)} />}
    </section>
  );
}
