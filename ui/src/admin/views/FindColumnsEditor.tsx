import { Button, makeStyles, Select, tokens } from '@fluentui/react-components';
import { t } from '../../shell/i18n/t';
import { DismissIcon } from '../../shell/icons';
import type { DesignCardProps } from './designerSlots';
import { columnKind, filterColumns } from './filterColumns';

const FIND_KINDS = new Set(['text', 'number', 'lookup']);

const useStyles = makeStyles({
  list: { listStyleType: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS },
  item: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
});

/**
 * The find columns of a quick find view (E6): the `like {0}` conditions of its `isquickfindfields` filter. Adds and removes
 * text, number and lookup columns of the table; the model writes the conditions.
 */
export function FindColumnsEditor({ design, attributes, onChange }: DesignCardProps) {
  const styles = useStyles();
  const label = (name: string) => attributes.find((a) => a.logicalName === name)?.displayName ?? name;
  const used = new Set(design.findColumns);
  const candidates = filterColumns(attributes).filter((a) => FIND_KINDS.has(columnKind(a)) && !used.has(a.logicalName));
  const set = (findColumns: string[]) => onChange({ ...design, findColumns });

  return (
    <>
      <p>{t('views.find.hint')}</p>
      {design.findColumns.length === 0 && <p>{t('views.find.empty')}</p>}
      <ul aria-label={t('views.find.title')} className={styles.list}>
        {design.findColumns.map((name) => (
          <li key={name} className={styles.item}>
            {label(name)}
            <Button
              appearance="subtle"
              size="small"
              icon={<DismissIcon />}
              aria-label={t('views.find.remove', { column: label(name) })}
              onClick={() => set(design.findColumns.filter((c) => c !== name))}
            />
          </li>
        ))}
      </ul>
      <Select
        aria-label={t('views.find.add')}
        value=""
        disabled={candidates.length === 0}
        onChange={(_, d) => d.value && set([...design.findColumns, d.value])}
      >
        <option value="">{t('views.find.choose')}</option>
        {candidates.map((a) => (
          <option key={a.logicalName} value={a.logicalName}>
            {a.displayName}
          </option>
        ))}
      </Select>
    </>
  );
}
