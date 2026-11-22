import {
  Button,
  makeStyles,
  mergeClasses,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Toolbar,
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
import { useSurfaceStyles } from '../../shell/layout/surface';

export type LinkScope = 'userRoles' | 'teamRoles' | 'teamMembers' | 'profileUsers' | 'profileTeams';

export interface LinkPanelProps {
  /** Selects the wording: roles of a user, roles of a team or members of a team. */
  scope: LinkScope;
  /** Path of the owning record, e.g. `systemusers(<id>)`. */
  ownerPath: string;
  ownerId: string;
  /** The security many-to-many relationship, e.g. `systemuserroles_association`. */
  nav: string;
  /** Entity set of its intersect table, e.g. `systemuserrolescollection`, and the owner's column in it. */
  linkSet: string;
  ownerAttribute: string;
  /** The linked table: id and primary name attributes (the intersect uses the same id column name). */
  targetSet: string;
  targetIdAttribute: string;
  targetNameAttribute: string;
  /** Restricts the picker, e.g. the roles of one business unit. */
  pickerFilter?: string;
}

const useStyles = makeStyles({ commands: { display: 'flex', flexWrap: 'wrap', gap: '8px', marginTop: '8px' } });

/**
 * Lists what is associated with a user or team (roles, members), adds through `$ref` and removes the selected row after a
 * confirmation; Add and Remove sit in the panel's own command bar.
 */
export function LinkPanel(p: LinkPanelProps) {
  const { api } = useAuth();
  const notify = useNotify();
  const list = usePagedList<Record<string, string>>(
    `${p.linkSet}?$select=${p.targetIdAttribute}&$filter=${encodeURIComponent(`${p.ownerAttribute} eq ${p.ownerId}`)}`,
  );
  const names = useNames(p.targetSet, p.targetIdAttribute, p.targetNameAttribute, list.items.map((i) => i[p.targetIdAttribute]));
  // Already-linked records (of the loaded page) are not offered again.
  const linkedIds = list.items.map((i) => i[p.targetIdAttribute]);
  const pickerFilter =
    [p.pickerFilter, ...linkedIds.map((id) => `${p.targetIdAttribute} ne ${id}`)].filter(Boolean).join(' and ') || undefined;
  const [picked, setPicked] = useState<PickedEntity>();
  const [removing, setRemoving] = useState<PickedEntity>();
  const [busy, setBusy] = useState(false);
  const selection = useRowSelection(list.items, (i) => i[p.targetIdAttribute]);
  const selId = selection.selected?.[p.targetIdAttribute];
  const styles = useStyles();
  const surface = useSurfaceStyles();

  async function add() {
    if (!api || !picked) return;
    setBusy(true);
    try {
      await api.associate(p.ownerPath, p.nav, `${p.targetSet}(${picked.id})`);
      notify.success(t(`link.${p.scope}.added`, { name: picked.name }));
      setPicked(undefined);
      list.reload();
    } catch (e) {
      notify.error(e);
    } finally {
      setBusy(false);
    }
  }

  async function remove(target: PickedEntity) {
    if (!api) return;
    try {
      await api.disassociate(p.ownerPath, p.nav, target.id);
      notify.success(t(`link.${p.scope}.removed`, { name: target.name }));
      list.reload();
    } catch (e) {
      notify.error(e);
    } finally {
      setRemoving(undefined);
    }
  }

  return (
    <div>
      {!list.loading && !list.error && list.items.length === 0 && <p>{t(`link.${p.scope}.empty`)}</p>}
      {list.items.length > 0 && (
        <Table aria-label={t(`link.${p.scope}.title`)}>
          <TableHeader>
            <TableRow>
              <SelectHeaderCell />
              <TableHeaderCell>{t('link.col.name')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {list.items.map((item) => {
              const id = item[p.targetIdAttribute];
              const name = names[id] ?? id;
              return (
                <TableRow key={id} {...selectableRow(selection, item)}>
                  <SelectCell selection={selection} item={item} name={name} />
                  <TableCell>{name}</TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      )}
      {list.loading && <Spinner size="tiny" label={t('common.loading')} />}
      {list.nextLink && !list.loading && <Button onClick={list.loadMore}>{t('common.loadMore')}</Button>}
      <EntityPicker
        entitySet={p.targetSet}
        idAttribute={p.targetIdAttribute}
        nameAttribute={p.targetNameAttribute}
        label={t(`link.${p.scope}.pick`)}
        filter={pickerFilter}
        selected={picked}
        onSelect={setPicked}
      />
      <Toolbar aria-label={t('page.commands', { title: t(`link.${p.scope}.title`) })} className={mergeClasses(surface.commandBar, styles.commands)}>
        <Button appearance="primary" disabled={!picked || busy} onClick={() => void add()}>
          {t(`link.${p.scope}.add`)}
        </Button>
        {selId && (
          <RowCommand onClick={() => setRemoving({ id: selId, name: names[selId] ?? selId })}>{t(`link.${p.scope}.remove`)}</RowCommand>
        )}
      </Toolbar>
      <ConfirmDialog
        open={!!removing}
        title={t(`link.${p.scope}.confirmTitle`)}
        body={removing ? t(`link.${p.scope}.confirmBody`, { name: removing.name }) : ''}
        confirmLabel={t(`link.${p.scope}.remove`)}
        cancelLabel={t('common.cancel')}
        onConfirm={() => removing && void remove(removing)}
        onCancel={() => setRemoving(undefined)}
      />
    </div>
  );
}
