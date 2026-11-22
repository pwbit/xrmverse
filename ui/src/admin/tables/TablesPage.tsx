import { Button, Checkbox, Field, Input, MessageBar, MessageBarBody, Spinner, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { t } from '../../shell/i18n/t';
import { CreateTableDialog } from './CreateTableDialog';
import { loadEntities } from './tableApi';
import { useLoad } from './useLoad';
import { PageHeader } from '../../shell/layout/PageHeader';

/** Every table of the environment, filterable by name, with "Create table". */
export function TablesPage() {
  const tables = useLoad('tables', loadEntities);
  const navigate = useNavigate();
  const [term, setTerm] = useState('');
  const [customOnly, setCustomOnly] = useState(false);
  const [creating, setCreating] = useState(false);

  const needle = term.trim().toLowerCase();
  const shown = (tables.data ?? [])
    .filter((e) => (!customOnly || e.isCustom) && (!needle || e.logicalName.toLowerCase().includes(needle) || e.displayName.toLowerCase().includes(needle)))
    .sort((a, b) => a.logicalName.localeCompare(b.logicalName, 'en'));

  return (
    <section>
      <PageHeader
        title={t('nav.tables')}
        actions={
          <Button appearance="primary" onClick={() => setCreating(true)}>
            {t('table.create.open')}
          </Button>
        }
      />
      <Field label={t('tables.filter')}>
        <Input value={term} onChange={(_, d) => setTerm(d.value)} />
      </Field>
      <Checkbox label={t('tables.customOnly')} checked={customOnly} onChange={(_, d) => setCustomOnly(!!d.checked)} />
      {tables.error && (
        <MessageBar intent="error">
          <MessageBarBody>{t('tables.loadFailed', { detail: tables.error })}</MessageBarBody>
        </MessageBar>
      )}
      {tables.loading && <Spinner label={t('common.loading')} />}
      {tables.data && shown.length === 0 && <p>{t('tables.empty')}</p>}
      {shown.length > 0 && (
        <Table aria-label={t('nav.tables')}>
          <TableHeader>
            <TableRow>
              <TableHeaderCell>{t('tables.col.name')}</TableHeaderCell>
              <TableHeaderCell>{t('tables.col.label')}</TableHeaderCell>
              <TableHeaderCell>{t('tables.col.ownership')}</TableHeaderCell>
              <TableHeaderCell>{t('tables.col.custom')}</TableHeaderCell>
            </TableRow>
          </TableHeader>
          <TableBody>
            {shown.map((e) => (
              <TableRow key={e.logicalName}>
                <TableCell>
                  <Link to={`/manage/tables/${encodeURIComponent(e.logicalName)}`}>{e.logicalName}</Link>
                </TableCell>
                <TableCell>{e.displayName}</TableCell>
                <TableCell>{e.ownership ?? ''}</TableCell>
                <TableCell>{e.isCustom ? t('common.yes') : t('common.no')}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {creating && (
        <CreateTableDialog
          onClose={() => setCreating(false)}
          onCreated={(name) => {
            setCreating(false);
            navigate(`/manage/tables/${encodeURIComponent(name)}`);
          }}
        />
      )}
    </section>
  );
}
