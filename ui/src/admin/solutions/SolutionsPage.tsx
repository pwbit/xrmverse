import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
} from '@fluentui/react-components';
import { useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { useAccess } from '../../shell/auth/roles';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../common/rowSelection';
import { useNames } from '../common/useNames';
import { usePagedList } from '../common/usePagedList';
import { CascadeDeleteDialog } from './CascadeDeleteDialog';
import { ImportDialog } from './ImportDialog';
import { LayersPanel } from './LayersPanel';
import { PromoteDialog } from './PromoteDialog';
import { UninstallDialog } from './UninstallDialog';
import { PageHeader } from '../../shell/layout/PageHeader';
import { DeleteIcon, ExportIcon, ImportIcon } from '../../shell/icons';

const SELECT = 'solutionid,uniquename,friendlyname,version,ismanaged,_publisherid_value';
/** The system solutions: they cannot be exported by hand or deleted, so the page offers no action on them. */
const SYSTEM_SOLUTIONS = new Set(['default', 'active', 'basic']);

type Solution = {
  solutionid: string;
  uniquename: string;
  friendlyname?: string | null;
  version?: string | null;
  ismanaged?: boolean | null;
  _publisherid_value?: string | null;
};

const HOLDING_SUFFIX = '_Upgrade';
const displayName = (s: Solution) => s.friendlyname || s.uniquename;

function base64ToBlob(base64: string): Blob {
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return new Blob([bytes], { type: 'application/zip' });
}

/** Saves `blob` as `fileName` through a temporary link. */
function saveBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

export function SolutionsPage() {
  const { api } = useAuth();
  const notify = useNotify();
  const { level } = useAccess();
  const list = usePagedList<Solution>(`solutions?$select=${SELECT}&$orderby=friendlyname`);
  const publishers = useNames('publishers', 'publisherid', 'friendlyname', list.items.map((s) => s._publisherid_value));
  const [importing, setImporting] = useState(false);
  const [deleting, setDeleting] = useState<Solution>();
  const [cascading, setCascading] = useState<Solution>();
  const [uninstalling, setUninstalling] = useState<Solution>();
  const [promoting, setPromoting] = useState<Solution>();
  const [layersOf, setLayersOf] = useState<Solution>();

  // A holding solution is `{other}_Upgrade` where `{other}` exists (the rule of the MCP list_solutions tool).
  const names = new Set(list.items.map((s) => s.uniquename.toLowerCase()));
  const isHolding = (s: Solution) =>
    s.uniquename.toLowerCase().endsWith(HOLDING_SUFFIX.toLowerCase()) &&
    names.has(s.uniquename.slice(0, -HOLDING_SUFFIX.length).toLowerCase());
  const hasHolding = (s: Solution) => names.has(`${s.uniquename}${HOLDING_SUFFIX}`.toLowerCase());
  const isSystem = (s: Solution) => SYSTEM_SOLUTIONS.has(s.uniquename.toLowerCase());

  const selection = useRowSelection(list.items, (s) => s.solutionid);
  const sel = selection.selected;
  const selName = sel ? displayName(sel) : '';
  /** Unmanaged, non-system solutions can be exported and deleted. */
  const actionable = !!sel && !sel.ismanaged && !isSystem(sel);
  /** Managed, non-system solutions that are not a holding solution show their layers and can be uninstalled. */
  const managedActionable = !!sel && !!sel.ismanaged && !isSystem(sel) && !isHolding(sel);

  async function exportSolution(solution: Solution) {
    if (!api) return;
    try {
      const response = await api.action<{ ExportSolutionFile: string }>('ExportSolution', {
        SolutionName: solution.uniquename,
        Managed: false,
      });
      const fileName = `${solution.uniquename}_${(solution.version ?? '').replace(/\./g, '_')}.zip`;
      saveBlob(base64ToBlob(response.ExportSolutionFile), fileName);
      notify.success(t('solutions.exported', { file: fileName }));
    } catch (e) {
      notify.error(e);
    }
  }

  async function deleteContainer(solution: Solution) {
    if (!api) return;
    try {
      await api.delete(`solutions(${solution.solutionid})`);
      setDeleting(undefined);
      notify.success(t('solutions.deleted', { name: displayName(solution) }));
      list.reload();
    } catch (e) {
      setDeleting(undefined);
      notify.error(e);
    }
  }

  return (
    <section>
      <PageHeader
        title={t('nav.solutions')}
        actions={
          <>
            <Button appearance="primary" icon={<ImportIcon />} onClick={() => setImporting(true)}>
              {t('solutions.import.open')}
            </Button>
            {sel && managedActionable && (
              <>
                <RowCommand aria-label={t('solutions.layers.open', { name: selName })} onClick={() => setLayersOf(sel)}>
                  {t('solutions.layers.button')}
                </RowCommand>
                {hasHolding(sel) && (
                  <RowCommand aria-label={t('solutions.promote.open', { name: selName })} onClick={() => setPromoting(sel)}>
                    {t('solutions.promote.button')}
                  </RowCommand>
                )}
                <RowCommand aria-label={t('solutions.uninstall.open', { name: selName })} onClick={() => setUninstalling(sel)}>
                  {t('solutions.uninstall.button')}
                </RowCommand>
              </>
            )}
            {sel && actionable && (
              <>
                <RowCommand icon={<ExportIcon />} aria-label={t('solutions.export.open', { name: selName })} onClick={() => void exportSolution(sel)}>
                  {t('solutions.export.button')}
                </RowCommand>
                <RowCommand icon={<DeleteIcon />} aria-label={t('solutions.delete.open', { name: selName })} onClick={() => setDeleting(sel)}>
                  {t('solutions.delete.button')}
                </RowCommand>
                {level === 'admin' && (
                  <RowCommand aria-label={t('solutions.cascade.open', { name: selName })} onClick={() => setCascading(sel)}>
                    {t('solutions.cascade.button')}
                  </RowCommand>
                )}
              </>
            )}
          </>
        }
      />
      {list.error && (
        <MessageBar intent="error">
          <MessageBarBody>{t('solutions.loadFailed', { detail: list.error })}</MessageBarBody>
        </MessageBar>
      )}
      {!list.loading && !list.error && list.items.length === 0 && <p>{t('solutions.empty')}</p>}
      {list.items.length > 0 && (
        <Table aria-label={t('nav.solutions')}>
          <TableHeader>
            <TableRow>
              <SelectHeaderCell />
              <TableHeaderCell>{t('solutions.col.name')}</TableHeaderCell>
              <TableHeaderCell>{t('solutions.col.uniqueName')}</TableHeaderCell>
              <TableHeaderCell>{t('solutions.col.publisher')}</TableHeaderCell>
              <TableHeaderCell>{t('solutions.col.version')}</TableHeaderCell>
              <TableHeaderCell>{t('solutions.col.managed')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {list.items.map((s) => {
              const name = displayName(s);
              return (
                <TableRow key={s.solutionid} {...selectableRow(selection, s)}>
                  <SelectCell selection={selection} item={s} name={name} />
                  <TableCell>{name}</TableCell>
                  <TableCell>{s.uniquename}</TableCell>
                  <TableCell>{s._publisherid_value ? publishers[s._publisherid_value] ?? '' : ''}</TableCell>
                  <TableCell>{s.version ?? ''}</TableCell>
                  <TableCell>
                    {s.ismanaged ? <Badge appearance="filled">{t('solutions.type.managed')}</Badge> : t('solutions.type.unmanaged')}
                    {isHolding(s) && <Badge appearance="outline">{t('solutions.badge.holding')}</Badge>}
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      )}
      {list.loading && <Spinner label={t('common.loading')} />}
      {list.nextLink && !list.loading && <Button onClick={list.loadMore}>{t('common.loadMore')}</Button>}

      {importing && <ImportDialog onClose={() => setImporting(false)} onImported={list.reload} />}
      <ConfirmDialog
        open={!!deleting}
        title={t('solutions.delete.title')}
        body={deleting ? t('solutions.delete.body', { name: displayName(deleting) }) : ''}
        confirmLabel={t('solutions.delete.confirm')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => deleting && void deleteContainer(deleting)}
        onCancel={() => setDeleting(undefined)}
      />
      {uninstalling && (
        <UninstallDialog
          uniqueName={uninstalling.uniquename}
          onClose={(changed) => {
            setUninstalling(undefined);
            if (changed) list.reload();
          }}
        />
      )}
      {promoting && (
        <PromoteDialog
          uniqueName={promoting.uniquename}
          onClose={(changed) => {
            setPromoting(undefined);
            if (changed) list.reload();
          }}
        />
      )}
      {layersOf && <LayersPanel target={{ solution: layersOf.uniquename }} onClose={() => setLayersOf(undefined)} />}
      {cascading && (
        <CascadeDeleteDialog
          solution={cascading}
          onClose={(deleted) => {
            setCascading(undefined);
            if (deleted) list.reload();
          }}
        />
      )}
    </section>
  );
}
