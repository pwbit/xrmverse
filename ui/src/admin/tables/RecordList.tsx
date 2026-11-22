import { Button, Field, Input, MessageBar, MessageBarBody, Spinner, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Toolbar } from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { literal } from '../../shell/api/odata';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useSurfaceStyles } from '../../shell/layout/surface';
import { useNotify } from '../../shell/notify/Notifications';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../common/rowSelection';
import { useDebounced } from '../common/useDebounced';
import { usePagedList } from '../common/usePagedList';
import { isListCandidate, cachedMostUsed, mostUsed } from './mostUsed';
import { recordKey, type AttributeMeta, type EntityMeta } from './metadata';
import { DeleteIcon, NewIcon } from '../../shell/icons';

const FILTER_DELAY_MS = 300;
const FORMATTED = '@OData.Community.Display.V1.FormattedValue';
const PREFER_FORMATTED = { Prefer: 'odata.include-annotations="OData.Community.Display.V1.FormattedValue"' };

type Row = Record<string, unknown>;

const cell = (row: Row, a: AttributeMeta): string => {
  const key = recordKey(a);
  const value = row[key + FORMATTED] ?? row[key];
  return value === null || value === undefined ? '' : String(value);
};

/**
 * The records of a table: the primary name plus the 5 most-used columns (ruling R3), a debounced `contains` filter on the
 * primary name, server paging, open / new / delete with confirmation. New and the delete of the selected record are on the tab's
 * command bar.
 */
export function RecordList({ entity }: { entity: EntityMeta }) {
  const { api } = useAuth();
  const notify = useNotify();
  const navigate = useNavigate();
  const set = entity.entitySetName;
  const nameAttribute = entity.attributes.find((a) => a.logicalName === entity.primaryNameAttribute);
  const [columns, setColumns] = useState<AttributeMeta[]>();
  const [columnsError, setColumnsError] = useState<string>();
  const [attempt, setAttempt] = useState(0);
  const [term, setTerm] = useState('');
  const debounced = useDebounced(term.trim(), FILTER_DELAY_MS);
  const [deleting, setDeleting] = useState<Row>();

  // Ruling R3: the columns come from the first, unfiltered page; the choice is made once per table per session.
  useEffect(() => {
    if (!api || !set) return;
    let current = true;
    setColumnsError(undefined);
    const candidates = entity.attributes.filter(isListCandidate);
    cachedMostUsed(api, entity.logicalName, async () => {
      if (candidates.length === 0) return [];
      const select = [entity.primaryIdAttribute, ...candidates.map(recordKey)].join(',');
      const order = entity.primaryNameAttribute ? `&$orderby=${entity.primaryNameAttribute}` : '';
      const page = await api.page<Row>(`${set}?$select=${select}${order}`);
      return mostUsed(page.value, candidates);
    })
      .then((names) => current && setColumns(names.map((n) => entity.attributes.find((a) => a.logicalName === n)!)))
      .catch((e) => current && setColumnsError(e instanceof Error ? e.message : String(e)));
    return () => {
      current = false;
    };
  }, [api, set, entity, attempt]);

  const select = columns && [entity.primaryIdAttribute, ...(entity.primaryNameAttribute ? [entity.primaryNameAttribute] : []), ...columns.map(recordKey)];
  const filter =
    debounced && entity.primaryNameAttribute
      ? `&$filter=${encodeURIComponent(`contains(${entity.primaryNameAttribute},${literal(debounced)})`)}`
      : '';
  const order = entity.primaryNameAttribute ? `&$orderby=${entity.primaryNameAttribute}` : '';
  const list = usePagedList<Row>(set && select ? `${set}?$select=${select.join(',')}${order}${filter}` : undefined, 50, { headers: PREFER_FORMATTED });
  const selection = useRowSelection(list.items, (row) => String(row[entity.primaryIdAttribute]));
  const sel = selection.selected;
  const surface = useSurfaceStyles();

  if (!set) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>{t('records.noSet', { table: entity.logicalName })}</MessageBarBody>
      </MessageBar>
    );
  }

  async function remove(row: Row) {
    setDeleting(undefined);
    try {
      await api!.delete(`${set}(${String(row[entity.primaryIdAttribute])})`);
      notify.success(t('records.deleted'));
      list.reload();
    } catch (e) {
      notify.error(e);
    }
  }

  const recordLink = (row: Row) => `/manage/tables/${encodeURIComponent(entity.logicalName)}/records/${String(row[entity.primaryIdAttribute])}`;
  const label = (row: Row) => (nameAttribute ? cell(row, nameAttribute) : '') || String(row[entity.primaryIdAttribute]);

  return (
    <div>
      <Toolbar aria-label={t('page.commands', { title: t('table.tab.records') })} className={surface.commandBar}>
        <Button appearance="primary" icon={<NewIcon />} onClick={() => navigate(`/manage/tables/${encodeURIComponent(entity.logicalName)}/records/new`)}>
          {t('records.new')}
        </Button>
        {sel && (
          <RowCommand icon={<DeleteIcon />} aria-label={t('records.delete.open', { name: label(sel) })} onClick={() => setDeleting(sel)}>
            {t('records.delete.button')}
          </RowCommand>
        )}
      </Toolbar>
      {entity.primaryNameAttribute && (
        <Field label={t('records.filter')}>
          <Input value={term} onChange={(_, d) => setTerm(d.value)} placeholder={t('records.filter.placeholder')} />
        </Field>
      )}
      {(columnsError || list.error) && (
        <MessageBar intent="error">
          <MessageBarBody>{t('records.loadFailed', { detail: columnsError ?? list.error ?? '' })}</MessageBarBody>
          <Button size="small" onClick={() => (columnsError ? setAttempt((n) => n + 1) : list.reload())}>
            {t('common.retry')}
          </Button>
        </MessageBar>
      )}
      {!list.loading && columns && !list.error && list.items.length === 0 && <p>{t('records.empty')}</p>}
      {list.items.length > 0 && columns && (
        <Table aria-label={t('records.table', { table: entity.displayName })}>
          <TableHeader>
            <TableRow>
              <SelectHeaderCell />
              <TableHeaderCell>{nameAttribute?.displayName ?? entity.primaryIdAttribute}</TableHeaderCell>
              {columns.map((c) => (
                <TableHeaderCell key={c.logicalName}>{c.displayName}</TableHeaderCell>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {list.items.map((row) => (
              <TableRow key={String(row[entity.primaryIdAttribute])} {...selectableRow(selection, row)}>
                <SelectCell selection={selection} item={row} name={label(row)} />
                <TableCell>
                  <Link to={recordLink(row)}>{label(row)}</Link>
                </TableCell>
                {columns.map((c) => (
                  <TableCell key={c.logicalName}>{cell(row, c)}</TableCell>
                ))}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {(list.loading || (!columns && !columnsError)) && <Spinner label={t('common.loading')} />}
      {list.nextLink && !list.loading && <Button onClick={list.loadMore}>{t('common.loadMore')}</Button>}
      <ConfirmDialog
        open={!!deleting}
        title={t('records.delete.title')}
        body={t('records.delete.body', { name: deleting ? label(deleting) : '' })}
        confirmLabel={t('records.delete.confirm')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => deleting && void remove(deleting)}
        onCancel={() => setDeleting(undefined)}
      />
    </div>
  );
}
