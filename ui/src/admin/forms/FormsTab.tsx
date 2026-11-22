import {
  Badge, Menu, MenuButton, MenuItem, MenuList, MenuPopover, MenuTrigger, MessageBar, MessageBarBody, Spinner,
  Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Toolbar,
} from '@fluentui/react-components';
import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useSurfaceStyles } from '../../shell/layout/surface';
import { ActivateIcon, DeactivateIcon, DeleteIcon, NewIcon } from '../../shell/icons';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../common/rowSelection';
import { useLoad } from '../tables/useLoad';
import { deleteForm, listForms, setDefaultForm, setFormActive, type FormRecord } from './formApi';
import { FORM_TYPE } from './formDesign';

const TYPE_LABELS: Record<number, Parameters<typeof t>[0]> = {
  [FORM_TYPE.main]: 'forms.type.main',
  [FORM_TYPE.quickView]: 'forms.type.quickView',
  [FORM_TYPE.quickCreate]: 'forms.type.quickCreate',
  11: 'forms.type.card',
  0: 'forms.type.dashboard',
};

const isActive = (f: FormRecord) => f.formactivationstate !== 0;
const canSetDefault = (f: FormRecord) => f.type === FORM_TYPE.main && !f.isdefault && isActive(f);
const canToggle = (f: FormRecord) => !isActive(f) || !f.isdefault;
const canDelete = (f: FormRecord) => !f.isdefault;

const typeLabel = (n: number) => (TYPE_LABELS[n] ? t(TYPE_LABELS[n]) : t('forms.type.other', { n: String(n) }));

/**
 * The table's forms: each opens in the designer. The tab's command bar acts on the selected form: an active non-default Main
 * form can be made the default; the default form can be neither deactivated nor deleted.
 */
export function FormsTab({ table }: { table: string }) {
  const { api } = useAuth();
  const surface = useSurfaceStyles();
  const navigate = useNavigate();
  const forms = useLoad(table, (a) => listForms(a, table));
  const [confirming, setConfirming] = useState<{ form: FormRecord; open: boolean }>();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string>();
  const base = `/manage/tables/${encodeURIComponent(table)}/forms`;
  const selection = useRowSelection(forms.data ?? [], (f) => f.systemformid);
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
      forms.reload(true);
    }
  }

  return (
    <div className={surface.section}>
      <Toolbar aria-label={t('page.commands', { title: t('table.tab.forms') })} className={surface.commandBar}>
        <Menu>
          <MenuTrigger disableButtonEnhancement>
            <MenuButton appearance="primary" icon={<NewIcon />}>{t('forms.new')}</MenuButton>
          </MenuTrigger>
          <MenuPopover>
            <MenuList>
              <MenuItem onClick={() => navigate(`${base}/new`)}>{t('forms.new.main')}</MenuItem>
              <MenuItem onClick={() => navigate(`${base}/new?type=${FORM_TYPE.quickView}`)}>{t('forms.new.quickView')}</MenuItem>
              <MenuItem onClick={() => navigate(`${base}/new?type=${FORM_TYPE.quickCreate}`)}>{t('forms.new.quickCreate')}</MenuItem>
            </MenuList>
          </MenuPopover>
        </Menu>
        {sel && (
          <>
            {canSetDefault(sel) && (
              <RowCommand disabled={busy || !api} onClick={() => void run(() => setDefaultForm(api!, sel.systemformid, table))}>
                {t('forms.setDefault')}
              </RowCommand>
            )}
            {canToggle(sel) && (
              <RowCommand
                icon={isActive(sel) ? <DeactivateIcon /> : <ActivateIcon />}
                disabled={busy || !api}
                onClick={() => void run(() => setFormActive(api!, sel.systemformid, table, !isActive(sel)))}
              >
                {t(isActive(sel) ? 'forms.deactivate' : 'forms.activate')}
              </RowCommand>
            )}
            {canDelete(sel) && (
              <RowCommand icon={<DeleteIcon />} disabled={busy || !api} onClick={() => setConfirming({ form: sel, open: true })}>
                {t('forms.delete')}
              </RowCommand>
            )}
          </>
        )}
      </Toolbar>
      {(forms.error || failure) && (
        <MessageBar intent="error">
          <MessageBarBody>{failure ?? t('forms.loadFailed', { detail: forms.error ?? '' })}</MessageBarBody>
        </MessageBar>
      )}
      {forms.loading && <Spinner label={t('common.loading')} />}
      {forms.data && forms.data.length === 0 && <p>{t('forms.empty')}</p>}
      {forms.data && forms.data.length > 0 && (
        <Table aria-label={t('table.tab.forms')}>
          <TableHeader>
            <TableRow>
              <SelectHeaderCell />
              <TableHeaderCell>{t('forms.col.name')}</TableHeaderCell>
              <TableHeaderCell>{t('forms.col.type')}</TableHeaderCell>
              <TableHeaderCell>{t('forms.col.default')}</TableHeaderCell>
              <TableHeaderCell>{t('forms.col.active')}</TableHeaderCell>
              <TableHeaderCell>{t('forms.col.managed')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {forms.data.map((f) => (
              <TableRow key={f.systemformid} {...selectableRow(selection, f)}>
                <SelectCell selection={selection} item={f} name={f.name} />
                <TableCell>
                  <Link to={`${base}/${f.systemformid}`}>{f.name}</Link>
                </TableCell>
                <TableCell>{typeLabel(f.type)}</TableCell>
                <TableCell>{f.isdefault ? '✓' : ''}</TableCell>
                <TableCell>{f.formactivationstate !== 0 ? '✓' : ''}</TableCell>
                <TableCell>{f.ismanaged && <Badge appearance="outline">{t('forms.managed')}</Badge>}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      <ConfirmDialog
        open={confirming?.open ?? false}
        title={t('forms.delete.title')}
        body={confirming ? t('forms.delete.body', { name: confirming.form.name }) : ''}
        confirmLabel={t('forms.delete')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => {
          const f = confirming!.form;
          setConfirming({ form: f, open: false });
          void run(() => deleteForm(api!, f.systemformid, table));
        }}
        onCancel={() => setConfirming((c) => c && { ...c, open: false })}
      />
    </div>
  );
}
