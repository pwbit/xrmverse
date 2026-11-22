import {
  Button,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
} from '@fluentui/react-components';
import { useState } from 'react';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../common/rowSelection';
import { EntityPicker, type PickedEntity } from '../common/EntityPicker';
import { useNames } from '../common/useNames';
import { usePagedList } from '../common/usePagedList';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { CreateRoleDialog } from './CreateRoleDialog';
import { RolePrivileges, type RoleRef } from './RolePrivileges';
import { PageHeader } from '../../shell/layout/PageHeader';
import { DeleteIcon, NewIcon } from '../../shell/icons';

export function RolesPage() {
  const { api } = useAuth();
  const notify = useNotify();
  const [buFilter, setBuFilter] = useState<PickedEntity>();
  const filter = buFilter ? `&$filter=${encodeURIComponent(`_businessunitid_value eq ${buFilter.id}`)}` : '';
  const list = usePagedList<RoleRef>(`roles?$select=roleid,name,_businessunitid_value&$orderby=name${filter}`);
  const names = useNames('businessunits', 'businessunitid', 'name', list.items.map((r) => r._businessunitid_value));
  const [creating, setCreating] = useState(false);
  const [deleting, setDeleting] = useState<RoleRef>();
  const [editing, setEditing] = useState<RoleRef>();
  const selection = useRowSelection(list.items, (r) => r.roleid);
  const sel = selection.selected;
  const selName = sel?.name ?? '';

  async function remove(role: RoleRef) {
    if (!api) return;
    try {
      await api.delete(`roles(${role.roleid})`);
      notify.success(t('roles.deleted', { name: role.name ?? '' }));
      list.setItems((prev) => prev.filter((r) => r.roleid !== role.roleid));
    } catch (e) {
      notify.error(e);
    } finally {
      setDeleting(undefined);
    }
  }

  return (
    <section>
      <PageHeader
        title={t('nav.roles')}
        actions={
          <>
            <Button appearance="primary" icon={<NewIcon />} onClick={() => setCreating(true)}>
              {t('roles.create.open')}
            </Button>
            {sel && (
              <>
                <RowCommand aria-label={t('roles.privileges.open', { name: selName })} onClick={() => setEditing(sel)}>
                  {t('roles.privileges.button')}
                </RowCommand>
                <RowCommand icon={<DeleteIcon />} aria-label={t('roles.delete.open', { name: selName })} onClick={() => setDeleting(sel)}>
                  {t('roles.delete.button')}
                </RowCommand>
              </>
            )}
          </>
        }
      />
      <EntityPicker
        entitySet="businessunits"
        idAttribute="businessunitid"
        nameAttribute="name"
        label={t('roles.filter.businessUnit')}
        selected={buFilter}
        onSelect={setBuFilter}
      />
      {buFilter && <Button onClick={() => setBuFilter(undefined)}>{t('roles.filter.clear')}</Button>}
      {list.error && (
        <MessageBar intent="error">
          <MessageBarBody>{t('roles.loadFailed', { detail: list.error })}</MessageBarBody>
        </MessageBar>
      )}
      {!list.loading && !list.error && list.items.length === 0 && <p>{t('roles.empty')}</p>}
      {list.items.length > 0 && (
        <Table aria-label={t('nav.roles')}>
          <TableHeader>
            <TableRow>
              <SelectHeaderCell />
              <TableHeaderCell>{t('roles.col.name')}</TableHeaderCell>
              <TableHeaderCell>{t('roles.col.businessUnit')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {list.items.map((r) => (
              <TableRow key={r.roleid} {...selectableRow(selection, r)}>
                <SelectCell selection={selection} item={r} name={r.name ?? ''} />
                <TableCell>{r.name}</TableCell>
                <TableCell>{r._businessunitid_value ? (names[r._businessunitid_value] ?? r._businessunitid_value) : ''}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {list.loading && <Spinner label={t('common.loading')} />}
      {list.nextLink && !list.loading && <Button onClick={list.loadMore}>{t('common.loadMore')}</Button>}

      {creating && (
        <CreateRoleDialog
          onClose={() => setCreating(false)}
          onCreated={() => {
            setCreating(false);
            list.reload();
          }}
        />
      )}
      <ConfirmDialog
        open={!!deleting}
        title={t('roles.delete.title')}
        body={deleting ? t('roles.delete.body', { name: deleting.name ?? '' }) : ''}
        confirmLabel={t('roles.delete.confirm')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => deleting && void remove(deleting)}
        onCancel={() => setDeleting(undefined)}
      />
      <OverlayDrawer position="end" size="large" open={!!editing} onOpenChange={(_, d) => !d.open && setEditing(undefined)}>
        {editing && (
          <>
            <DrawerHeader>
              <DrawerHeaderTitle action={<Button appearance="subtle" onClick={() => setEditing(undefined)}>{t('common.close')}</Button>}>
                {t('privileges.title', { role: editing.name ?? '' })}
              </DrawerHeaderTitle>
            </DrawerHeader>
            <DrawerBody>
              <RolePrivileges key={editing.roleid} role={editing} />
            </DrawerBody>
          </>
        )}
      </OverlayDrawer>
    </section>
  );
}
