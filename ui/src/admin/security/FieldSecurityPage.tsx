import {
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Subtitle1,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
} from '@fluentui/react-components';
import { useCallback, useEffect, useState } from 'react';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../common/rowSelection';
import { usePagedList } from '../common/usePagedList';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import {
  ALLOWED,
  isAdministratorProfile,
  loadPermissions,
  loadSecuredColumns,
  PROFILES_PATH,
  RIGHTS,
  setRight,
  type Permission,
  type Profile,
  type Right,
  type SecuredColumn,
} from './fieldSecurityApi';
import { LinkPanel } from './LinkPanel';
import { PageHeader } from '../../shell/layout/PageHeader';

const RIGHT_LABEL = {
  canread: 'fieldSecurity.matrix.read',
  cancreate: 'fieldSecurity.matrix.create',
  canupdate: 'fieldSecurity.matrix.update',
} as const;

type Panel = { profile: Profile; kind: 'users' | 'teams' };

const nameOf = (p: Profile) => p.name ?? p.fieldsecurityprofileid;

/**
 * Field security profiles: lists, creates and deletes them, edits their permissions (secured columns against read, create
 * and update) and gives them to users and teams. The System Administrator profile is shown read-only.
 */
export function FieldSecurityPage() {
  const { api } = useAuth();
  const notify = useNotify();
  const list = usePagedList<Profile>(PROFILES_PATH);
  const [creating, setCreating] = useState(false);
  const [deleting, setDeleting] = useState<Profile>();
  const [selected, setSelected] = useState<Profile>();
  const [panel, setPanel] = useState<Panel>();
  const selection = useRowSelection(list.items, (x) => x.fieldsecurityprofileid);
  const sel = selection.selected;
  const selName = sel ? nameOf(sel) : '';
  const selReadOnly = sel ? isAdministratorProfile(sel.fieldsecurityprofileid) : false;

  async function remove(profile: Profile) {
    setDeleting(undefined);
    if (!api) return;
    try {
      await api.delete(`fieldsecurityprofiles(${profile.fieldsecurityprofileid})`);
      notify.success(t('fieldSecurity.deleted', { name: nameOf(profile) }));
      if (selected?.fieldsecurityprofileid === profile.fieldsecurityprofileid) setSelected(undefined);
      list.reload();
    } catch (e) {
      notify.error(e);
    }
  }

  return (
    <section>
      <PageHeader
        title={t('nav.fieldSecurity')}
        actions={
          <>
            <Button appearance="primary" onClick={() => setCreating(true)}>
              {t('fieldSecurity.create.open')}
            </Button>
            {sel && (
              <>
                <RowCommand aria-label={t('fieldSecurity.permissions.open', { name: selName })} onClick={() => setSelected(sel)}>
                  {t('fieldSecurity.permissions.button')}
                </RowCommand>
                {!selReadOnly && (
                  <>
                    <RowCommand aria-label={t('fieldSecurity.users.open', { name: selName })} onClick={() => setPanel({ profile: sel, kind: 'users' })}>
                      {t('fieldSecurity.users.button')}
                    </RowCommand>
                    <RowCommand aria-label={t('fieldSecurity.teams.open', { name: selName })} onClick={() => setPanel({ profile: sel, kind: 'teams' })}>
                      {t('fieldSecurity.teams.button')}
                    </RowCommand>
                    <RowCommand aria-label={t('fieldSecurity.delete.open', { name: selName })} onClick={() => setDeleting(sel)}>
                      {t('fieldSecurity.delete.button')}
                    </RowCommand>
                  </>
                )}
              </>
            )}
          </>
        }
      />
      <p>{t('fieldSecurity.intro')}</p>
      {list.error && (
        <MessageBar intent="error">
          <MessageBarBody>{t('fieldSecurity.loadFailed', { detail: list.error })}</MessageBarBody>
        </MessageBar>
      )}
      {!list.loading && !list.error && list.items.length === 0 && <p>{t('fieldSecurity.empty')}</p>}
      {list.items.length > 0 && (
        <Table aria-label={t('nav.fieldSecurity')}>
          <TableHeader>
            <TableRow>
              <SelectHeaderCell />
              <TableHeaderCell>{t('fieldSecurity.col.name')}</TableHeaderCell>
              <TableHeaderCell>{t('fieldSecurity.col.description')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {list.items.map((profile) => {
              const name = nameOf(profile);
              const readOnly = isAdministratorProfile(profile.fieldsecurityprofileid);
              return (
                <TableRow key={profile.fieldsecurityprofileid} {...selectableRow(selection, profile)}>
                  <SelectCell selection={selection} item={profile} name={name} />
                  <TableCell>
                    <Text block>{name}</Text>
                    {readOnly && (
                      <Text block size={200}>
                        {t('fieldSecurity.readOnly')}
                      </Text>
                    )}
                  </TableCell>
                  <TableCell>{profile.description ?? ''}</TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      )}
      {list.loading && <Spinner label={t('common.loading')} />}
      {list.nextLink && !list.loading && <Button onClick={list.loadMore}>{t('common.loadMore')}</Button>}

      {selected && <PermissionMatrix key={selected.fieldsecurityprofileid} profile={selected} />}

      {creating && (
        <CreateProfileDialog
          onClose={() => setCreating(false)}
          onCreated={() => {
            setCreating(false);
            list.reload();
          }}
        />
      )}
      <ConfirmDialog
        open={!!deleting}
        title={t('fieldSecurity.delete.title')}
        body={t('fieldSecurity.delete.body', { name: deleting ? nameOf(deleting) : '' })}
        confirmLabel={t('fieldSecurity.delete.button')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => deleting && void remove(deleting)}
        onCancel={() => setDeleting(undefined)}
      />
      <Dialog open={!!panel} onOpenChange={(_, d) => !d.open && setPanel(undefined)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>
              {panel && t(panel.kind === 'users' ? 'fieldSecurity.users.title' : 'fieldSecurity.teams.title', { name: nameOf(panel.profile) })}
            </DialogTitle>
            <DialogContent>
              {panel?.kind === 'users' && (
                <LinkPanel
                  key={`u-${panel.profile.fieldsecurityprofileid}`}
                  scope="profileUsers"
                  ownerPath={`fieldsecurityprofiles(${panel.profile.fieldsecurityprofileid})`}
                  ownerId={panel.profile.fieldsecurityprofileid}
                  nav="systemuserprofiles_association"
                  linkSet="systemuserprofilescollection"
                  ownerAttribute="fieldsecurityprofileid"
                  targetSet="systemusers"
                  targetIdAttribute="systemuserid"
                  targetNameAttribute="fullname"
                />
              )}
              {panel?.kind === 'teams' && (
                <LinkPanel
                  key={`t-${panel.profile.fieldsecurityprofileid}`}
                  scope="profileTeams"
                  ownerPath={`fieldsecurityprofiles(${panel.profile.fieldsecurityprofileid})`}
                  ownerId={panel.profile.fieldsecurityprofileid}
                  nav="teamprofiles_association"
                  linkSet="teamprofilescollection"
                  ownerAttribute="fieldsecurityprofileid"
                  targetSet="teams"
                  targetIdAttribute="teamid"
                  targetNameAttribute="name"
                />
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setPanel(undefined)}>
                {t('common.close')}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </section>
  );
}

/** The secured columns of the organization against read, create and update for one profile; read-only for System Administrator. */
function PermissionMatrix({ profile }: { profile: Profile }) {
  const { api } = useAuth();
  const notify = useNotify();
  const readOnly = isAdministratorProfile(profile.fieldsecurityprofileid);
  const [columns, setColumns] = useState<SecuredColumn[]>();
  const [permissions, setPermissions] = useState<Permission[]>([]);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const name = nameOf(profile);

  const reloadPermissions = useCallback(async () => {
    if (!api || readOnly) return;
    setPermissions(await loadPermissions(api, profile.fieldsecurityprofileid));
  }, [api, readOnly, profile.fieldsecurityprofileid]);

  useEffect(() => {
    if (!api) return;
    let live = true;
    (async () => {
      try {
        const [secured, held] = await Promise.all([
          loadSecuredColumns(api),
          readOnly ? Promise.resolve([]) : loadPermissions(api, profile.fieldsecurityprofileid),
        ]);
        if (!live) return;
        setColumns(secured);
        setPermissions(held);
      } catch (e) {
        if (live) setError(e instanceof Error ? e.message : String(e));
      }
    })();
    return () => {
      live = false;
    };
  }, [api, readOnly, profile.fieldsecurityprofileid]);

  const permissionOf = (c: SecuredColumn) =>
    permissions.find((p) => p.entityname === c.table && p.attributelogicalname === c.column);

  async function toggle(column: SecuredColumn, right: Right, allowed: boolean) {
    if (!api) return;
    setBusy(true);
    try {
      await setRight(api, profile.fieldsecurityprofileid, column, permissionOf(column), right, allowed);
      await reloadPermissions();
    } catch (e) {
      notify.error(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <Subtitle1 as="h3">{t('fieldSecurity.permissions.title', { name })}</Subtitle1>
      {readOnly && <p>{t('fieldSecurity.permissions.adminNote')}</p>}
      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{t('fieldSecurity.permissions.loadFailed', { detail: error })}</MessageBarBody>
        </MessageBar>
      )}
      {!columns && !error && <Spinner size="tiny" label={t('common.loading')} />}
      {columns && columns.length === 0 && <p>{t('fieldSecurity.permissions.empty')}</p>}
      {columns && columns.length > 0 && (
        <Table aria-label={t('fieldSecurity.permissions.title', { name })}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{t('fieldSecurity.matrix.table')}</TableHeaderCell>
              <TableHeaderCell>{t('fieldSecurity.matrix.column')}</TableHeaderCell>
              {RIGHTS.map((r) => (
                <TableHeaderCell key={r}>{t(RIGHT_LABEL[r])}</TableHeaderCell>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {columns.map((c) => {
              const permission = permissionOf(c);
              return (
                <TableRow key={`${c.table}.${c.column}`}>
                  <NameCell label={c.tableLabel} name={c.table} />
                  <NameCell label={c.columnLabel} name={c.column} />
                  {RIGHTS.map((r) => (
                    <TableCell key={r}>
                      {readOnly ? (
                        <Text>{t('fieldSecurity.matrix.allowed')}</Text>
                      ) : (
                        <Checkbox
                          aria-label={t('fieldSecurity.matrix.toggle', { right: t(RIGHT_LABEL[r]), table: c.table, column: c.column })}
                          checked={permission?.[r] === ALLOWED}
                          disabled={busy || !api}
                          onChange={(_, d) => void toggle(c, r, d.checked === true)}
                        />
                      )}
                    </TableCell>
                  ))}
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      )}
    </div>
  );
}

function CreateProfileDialog({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const { api } = useAuth();
  const notify = useNotify();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [busy, setBusy] = useState(false);

  async function create() {
    if (!api) return;
    setBusy(true);
    try {
      await api.post('fieldsecurityprofiles', { name: name.trim(), description: description.trim() || null });
      notify.success(t('fieldSecurity.created', { name: name.trim() }));
      onCreated();
    } catch (e) {
      notify.error(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('fieldSecurity.create.title')}</DialogTitle>
          <DialogContent>
            <Field label={t('fieldSecurity.field.name')} required>
              <Input value={name} onChange={(_, d) => setName(d.value)} />
            </Field>
            <Field label={t('fieldSecurity.field.description')}>
              <Input value={description} onChange={(_, d) => setDescription(d.value)} />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={!name.trim() || busy} onClick={() => void create()}>
              {t('fieldSecurity.create.submit')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** A table or column: its display label, with the logical name under it when the two differ. */
function NameCell({ label, name }: { label: string; name: string }) {
  return (
    <TableCell>
      {label && label !== name ? (
        <>
          <Text block>{label}</Text>
          <Text block size={200}>
            {name}
          </Text>
        </>
      ) : (
        <Text>{name}</Text>
      )}
    </TableCell>
  );
}
