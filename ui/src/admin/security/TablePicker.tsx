import { Button, Field, Input, Spinner } from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';

export interface TableRef {
  LogicalName: string;
  SchemaName: string;
}

const SHOWN = 20;

/**
 * Picks a table. The table list is the schema (`EntityDefinitions`, names only), which the server returns whole and cannot
 * search or page, so it is read once and filtered here; at most 20 matches are listed.
 */
export function TablePicker({ selected, onSelect }: { selected?: TableRef; onSelect: (table: TableRef) => void }) {
  const { api } = useAuth();
  const notify = useNotify();
  const [tables, setTables] = useState<TableRef[]>();
  const [term, setTerm] = useState('');

  useEffect(() => {
    if (!api) return;
    let current = true;
    api
      .get<{ value: TableRef[] }>('EntityDefinitions?$select=LogicalName,SchemaName')
      .then((r) => current && setTables([...r.value].sort((a, b) => a.LogicalName.localeCompare(b.LogicalName))))
      .catch((e) => current && (setTables([]), notify.error(e)));
    return () => {
      current = false;
    };
  }, [api, notify]);

  const needle = term.trim().toLowerCase();
  const matches = (tables ?? []).filter((x) => x.LogicalName.toLowerCase().includes(needle) || x.SchemaName.toLowerCase().includes(needle));
  return (
    <div>
      <Field label={t('privileges.table.search')}>
        <Input value={term} onChange={(_, d) => setTerm(d.value)} placeholder={t('picker.search')} />
      </Field>
      {!tables && <Spinner size="tiny" label={t('common.loading')} />}
      {tables && matches.length === 0 && <p>{t('picker.empty')}</p>}
      <div role="listbox" aria-label={t('privileges.table.results')}>
        {matches.slice(0, SHOWN).map((x) => (
          <Button
            key={x.LogicalName}
            role="option"
            aria-selected={selected?.LogicalName === x.LogicalName}
            appearance={selected?.LogicalName === x.LogicalName ? 'primary' : 'subtle'}
            onClick={() => onSelect(x)}
          >
            {x.LogicalName}
          </Button>
        ))}
      </div>
      {matches.length > SHOWN && <p>{t('privileges.table.more', { count: matches.length - SHOWN })}</p>}
    </div>
  );
}
