import { Button, Field, Input, Spinner } from '@fluentui/react-components';
import { useState } from 'react';
import { useDebounced } from '../common/useDebounced';
import { usePagedList } from '../common/usePagedList';
import { literal } from '../../shell/api/odata';
import { t } from '../../shell/i18n/t';
import { PrivilegeMatrix, type RoleRef } from './PrivilegeMatrix';
import { privilegeNamesOf, type Privilege } from './privilegeModel';
import { TablePicker, type TableRef } from './TablePicker';

export type { RoleRef };

const SELECT = 'privilegeid,name,canbebasic,canbelocal,canbedeep,canbeglobal';

/** Table-first privilege editing: pick a table to see exactly its eight privileges; "other privileges" is a name search. */
export function RolePrivileges({ role }: { role: RoleRef }) {
  const [mode, setMode] = useState<'table' | 'other'>('table');
  return (
    <div>
      <Button appearance={mode === 'table' ? 'primary' : 'secondary'} onClick={() => setMode('table')}>
        {t('privileges.mode.table')}
      </Button>
      <Button appearance={mode === 'other' ? 'primary' : 'secondary'} onClick={() => setMode('other')}>
        {t('privileges.mode.other')}
      </Button>
      {mode === 'table' ? <TableMode role={role} /> : <OtherMode role={role} />}
    </div>
  );
}

function TableMode({ role }: { role: RoleRef }) {
  const [table, setTable] = useState<TableRef>();
  // Exactly the table's privileges: prvCreate<Schema>, prvRead<Schema>, ... (8 names).
  const filter = table ? privilegeNamesOf(table.SchemaName).map((n) => `name eq ${literal(n)}`).join(' or ') : undefined;
  const list = usePagedList<Privilege>(
    filter ? `privileges?$select=${SELECT}&$filter=${encodeURIComponent(filter)}` : undefined,
  );
  const order = table ? privilegeNamesOf(table.SchemaName) : [];
  const rows = [...list.items].sort((a, b) => order.indexOf(a.name) - order.indexOf(b.name));
  return (
    <div>
      <TablePicker selected={table} onSelect={setTable} />
      {table && list.loading && <Spinner size="tiny" label={t('common.loading')} />}
      {table && !list.loading && !list.error && rows.length === 0 && <p>{t('privileges.table.none', { table: table.LogicalName })}</p>}
      {rows.length > 0 && <PrivilegeMatrix key={table?.LogicalName} role={role} privileges={rows} />}
    </div>
  );
}

function OtherMode({ role }: { role: RoleRef }) {
  const [search, setSearch] = useState('');
  const term = useDebounced(search.trim(), 300);
  const filter = term ? `&$filter=${encodeURIComponent(`contains(name,${literal(term)})`)}` : '';
  const list = usePagedList<Privilege>(`privileges?$select=${SELECT}&$orderby=name${filter}`);
  return (
    <div>
      <Field label={t('privileges.search')}>
        <Input value={search} onChange={(_, d) => setSearch(d.value)} />
      </Field>
      {!list.loading && !list.error && list.items.length === 0 && <p>{t('privileges.empty')}</p>}
      <PrivilegeMatrix role={role} privileges={list.items} />
      {list.loading && <Spinner label={t('common.loading')} />}
      {list.nextLink && !list.loading && <Button onClick={list.loadMore}>{t('common.loadMore')}</Button>}
    </div>
  );
}
