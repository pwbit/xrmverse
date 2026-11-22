import {
  Badge, Button, MessageBar, MessageBarBody, Spinner, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Toolbar,
} from '@fluentui/react-components';
import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useSurfaceStyles } from '../../shell/layout/surface';
import { DeleteIcon, NewIcon } from '../../shell/icons';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../common/rowSelection';
import { useLoad } from '../tables/useLoad';
import { deleteView, listViews, setDefaultView, type ViewRecord } from './viewApi';

const TYPE_LABELS: Record<number, Parameters<typeof t>[0]> = {
  0: 'views.type.public',
  1: 'views.type.advancedFind',
  2: 'views.type.associated',
  4: 'views.type.quickFind',
  64: 'views.type.lookup',
};

const typeLabel = (n: number) => (TYPE_LABELS[n] ? t(TYPE_LABELS[n]) : t('views.type.other', { n: String(n) }));

/**
 * The table's saved views: each opens in the designer. The tab's command bar acts on the selected view: a non-default
 * Public view can be made the default, and a non-default view can be deleted.
 */
export function ViewsTab({ table }: { table: string }) {
  const { api } = useAuth();
  const surface = useSurfaceStyles();
  const navigate = useNavigate();
  const views = useLoad(table, (a) => listViews(a, table));
  const [confirming, setConfirming] = useState<{ view: ViewRecord; open: boolean }>();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string>();
  const base = `/manage/tables/${encodeURIComponent(table)}/views`;
  const selection = useRowSelection(views.data ?? [], (v) => v.savedqueryid);
  const sel = selection.selected;

  async function run(action: () => Promise<void>) {
    setBusy(true);
    setFailure(undefined);
    try {
      await action();
    } catch (e) {
      setFailure(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
      views.reload(true);
    }
  }

  return (
    <div className={surface.section}>
      <Toolbar aria-label={t('page.commands', { title: t('table.tab.views') })} className={surface.commandBar}>
        <Button appearance="primary" icon={<NewIcon />} as="a"
          href={`${base}/new`}
          onClick={(e: React.MouseEvent) => {
            e.preventDefault();
            navigate(`${base}/new`);
          }}
        >
          {t('views.new')}
        </Button>
        {/* The default view offers no command; only a Public view can become the default. */}
        {sel && !sel.isdefault && (
          <>
            {sel.querytype === 0 && (
              <RowCommand disabled={busy || !api} onClick={() => void run(() => setDefaultView(api!, sel.savedqueryid, table))}>
                {t('views.setDefault')}
              </RowCommand>
            )}
            <RowCommand icon={<DeleteIcon />} disabled={busy || !api} onClick={() => setConfirming({ view: sel, open: true })}>
              {t('views.delete')}
            </RowCommand>
          </>
        )}
      </Toolbar>
      {(views.error || failure) && (
        <MessageBar intent="error">
          <MessageBarBody>{failure ?? t('views.loadFailed', { detail: views.error ?? '' })}</MessageBarBody>
        </MessageBar>
      )}
      {views.loading && <Spinner label={t('common.loading')} />}
      {views.data && views.data.length === 0 && <p>{t('views.empty')}</p>}
      {views.data && views.data.length > 0 && (
        <Table aria-label={t('table.tab.views')}>
          <TableHeader>
            <TableRow>
              <SelectHeaderCell />
              <TableHeaderCell>{t('views.col.name')}</TableHeaderCell>
              <TableHeaderCell>{t('views.col.type')}</TableHeaderCell>
              <TableHeaderCell>{t('views.col.default')}</TableHeaderCell>
              <TableHeaderCell>{t('views.col.managed')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {views.data.map((v) => (
              <TableRow key={v.savedqueryid} {...selectableRow(selection, v)}>
                <SelectCell selection={selection} item={v} name={v.name} />
                <TableCell>
                  <Link to={`${base}/${v.savedqueryid}`}>{v.name}</Link>
                </TableCell>
                <TableCell>{typeLabel(v.querytype)}</TableCell>
                <TableCell>{v.isdefault ? '✓' : ''}</TableCell>
                <TableCell>{v.ismanaged && <Badge appearance="outline">{t('views.managed')}</Badge>}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      <ConfirmDialog
        open={confirming?.open ?? false}
        title={t('views.delete.title')}
        body={confirming ? t('views.delete.body', { name: confirming.view.name }) : ''}
        confirmLabel={t('views.delete')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => {
          const v = confirming!.view;
          setConfirming({ view: v, open: false });
          void run(() => deleteView(api!, v.savedqueryid, table));
        }}
        onCancel={() => setConfirming((c) => c && { ...c, open: false })}
      />
    </div>
  );
}
