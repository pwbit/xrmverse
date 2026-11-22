import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
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
import { Link } from 'react-router-dom';
import { useState } from 'react';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../common/rowSelection';
import { EntityPicker, type PickedEntity } from '../common/EntityPicker';
import { useNames } from '../common/useNames';
import { usePagedList } from '../common/usePagedList';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { CreateUserDialog } from './CreateUserDialog';
import { LinkPanel } from './LinkPanel';
import { PageHeader } from '../../shell/layout/PageHeader';
import { ActivateIcon, DeactivateIcon, NewIcon } from '../../shell/icons';

const SELECT = 'systemuserid,fullname,domainname,azureactivedirectoryobjectid,isdisabled,_businessunitid_value';

type User = {
  systemuserid: string;
  fullname?: string | null;
  domainname?: string | null;
  azureactivedirectoryobjectid?: string | null;
  isdisabled?: boolean | null;
  _businessunitid_value?: string | null;
};

export function UsersPage() {
  const { api } = useAuth();
  const notify = useNotify();
  const list = usePagedList<User>(`systemusers?$select=${SELECT}&$orderby=fullname`);
  const names = useNames('businessunits', 'businessunitid', 'name', list.items.map((u) => u._businessunitid_value));
  const [creating, setCreating] = useState(false);
  const [disabling, setDisabling] = useState<User>();
  const [moving, setMoving] = useState<User>();
  const [newBu, setNewBu] = useState<PickedEntity>();
  const [roleUser, setRoleUser] = useState<User>();
  const selection = useRowSelection(list.items, (u) => u.systemuserid);
  const sel = selection.selected;
  const selName = sel?.fullname ?? '';

  /** Re-reads one row after a write so the table shows what the server stored. */
  async function refreshRow(id: string) {
    if (!api) return;
    const fresh = await api.get<User>(`systemusers(${id})?$select=${SELECT}`);
    list.setItems((prev) => prev.map((u) => (u.systemuserid === id ? fresh : u)));
  }

  async function setDisabled(user: User, isdisabled: boolean) {
    if (!api) return;
    try {
      await api.patch(`systemusers(${user.systemuserid})`, { isdisabled });
      setDisabling(undefined);
      notify.success(t(isdisabled ? 'users.disabled' : 'users.enabled', { name: user.fullname ?? '' }));
      await refreshRow(user.systemuserid).catch(() => list.reload());
    } catch (e) {
      setDisabling(undefined);
      notify.error(e);
    }
  }

  async function changeBusinessUnit(user: User, bu: PickedEntity) {
    if (!api) return;
    try {
      await api.patch(`systemusers(${user.systemuserid})`, { 'businessunitid@odata.bind': `/businessunits(${bu.id})` });
      setMoving(undefined);
      setNewBu(undefined);
      notify.success(t('users.buChanged', { name: user.fullname ?? '', bu: bu.name }));
      await refreshRow(user.systemuserid).catch(() => list.reload());
    } catch (e) {
      notify.error(e);
    }
  }

  return (
    <section>
      <PageHeader
        title={t('nav.users')}
        actions={
          <>
            <Button appearance="primary" icon={<NewIcon />} onClick={() => setCreating(true)}>
              {t('users.create.open')}
            </Button>
            {sel && (
              <>
                <RowCommand aria-label={t('users.roles.open', { name: selName })} onClick={() => setRoleUser(sel)}>
                  {t('users.roles.button')}
                </RowCommand>
                <RowCommand aria-label={t('users.changeBu.open', { name: selName })} onClick={() => setMoving(sel)}>
                  {t('users.changeBu.button')}
                </RowCommand>
                {sel.isdisabled ? (
                  <RowCommand icon={<ActivateIcon />} aria-label={t('users.enable.open', { name: selName })} onClick={() => void setDisabled(sel, false)}>
                    {t('users.enable.button')}
                  </RowCommand>
                ) : (
                  <RowCommand icon={<DeactivateIcon />} aria-label={t('users.disable.open', { name: selName })} onClick={() => setDisabling(sel)}>
                    {t('users.disable.button')}
                  </RowCommand>
                )}
              </>
            )}
          </>
        }
      />
      {list.error && (
        <MessageBar intent="error">
          <MessageBarBody>{t('users.loadFailed', { detail: list.error })}</MessageBarBody>
        </MessageBar>
      )}
      {!list.loading && !list.error && list.items.length === 0 && <p>{t('users.empty')}</p>}
      {list.items.length > 0 && (
        <Table aria-label={t('nav.users')}>
          <TableHeader>
            <TableRow>
              <SelectHeaderCell />
              <TableHeaderCell>{t('users.col.fullname')}</TableHeaderCell>
              <TableHeaderCell>{t('users.col.domainname')}</TableHeaderCell>
              <TableHeaderCell>{t('users.col.objectId')}</TableHeaderCell>
              <TableHeaderCell>{t('users.col.businessUnit')}</TableHeaderCell>
              <TableHeaderCell>{t('users.col.status')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {list.items.map((u) => (
              <TableRow key={u.systemuserid} {...selectableRow(selection, u)}>
                <SelectCell selection={selection} item={u} name={u.fullname ?? ''} />
                <TableCell>
                  {/* The user's own record opens in the record form, where its columns can be edited. */}
                  <Link to={`/manage/tables/systemuser/records/${u.systemuserid}`}>{u.fullname || u.systemuserid}</Link>
                </TableCell>
                <TableCell>{u.domainname}</TableCell>
                <TableCell>{u.azureactivedirectoryobjectid}</TableCell>
                <TableCell>{u._businessunitid_value ? (names[u._businessunitid_value] ?? u._businessunitid_value) : ''}</TableCell>
                <TableCell>{u.isdisabled ? t('users.status.disabled') : t('users.status.enabled')}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {list.loading && <Spinner label={t('common.loading')} />}
      {list.nextLink && !list.loading && <Button onClick={list.loadMore}>{t('common.loadMore')}</Button>}

      {creating && (
        <CreateUserDialog
          onClose={() => setCreating(false)}
          onCreated={() => {
            setCreating(false);
            list.reload();
          }}
        />
      )}
      <ConfirmDialog
        open={!!disabling}
        title={t('users.disable.title')}
        body={disabling ? t('users.disable.body', { name: disabling.fullname ?? '' }) : ''}
        confirmLabel={t('users.disable.confirm')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => disabling && void setDisabled(disabling, true)}
        onCancel={() => setDisabling(undefined)}
      />
      <Dialog open={!!moving} onOpenChange={(_, d) => !d.open && (setMoving(undefined), setNewBu(undefined))}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>{t('users.changeBu.title', { name: moving?.fullname ?? '' })}</DialogTitle>
            <DialogContent>
              {moving && (
                <EntityPicker
                  entitySet="businessunits"
                  idAttribute="businessunitid"
                  nameAttribute="name"
                  label={t('users.field.businessUnit')}
                  selected={newBu}
                  onSelect={setNewBu}
                />
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => (setMoving(undefined), setNewBu(undefined))}>
                {t('common.cancel')}
              </Button>
              <Button appearance="primary" disabled={!newBu} onClick={() => moving && newBu && void changeBusinessUnit(moving, newBu)}>
                {t('users.changeBu.submit')}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      <Dialog open={!!roleUser} onOpenChange={(_, d) => !d.open && setRoleUser(undefined)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>{t('users.roles.title', { name: roleUser?.fullname ?? '' })}</DialogTitle>
            <DialogContent>
              {roleUser && (
                <LinkPanel
                  key={roleUser.systemuserid}
                  scope="userRoles"
                  ownerPath={`systemusers(${roleUser.systemuserid})`}
                  ownerId={roleUser.systemuserid}
                  nav="systemuserroles_association"
                  linkSet="systemuserrolescollection"
                  ownerAttribute="systemuserid"
                  targetSet="roles"
                  targetIdAttribute="roleid"
                  targetNameAttribute="name"
                  pickerFilter={roleUser._businessunitid_value ? `_businessunitid_value eq ${roleUser._businessunitid_value}` : undefined}
                />
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setRoleUser(undefined)}>
                {t('common.close')}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </section>
  );
}
