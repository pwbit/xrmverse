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
  Select,
  Spinner,
} from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import {
  createRule,
  loadColumns,
  loadConditions,
  OPERATORS,
  takesCount,
  updateRule,
  type DuplicateRule,
  type RuleCondition,
  type TableChoice,
} from './duplicateRulesApi';

const messageOf = (e: unknown) => (e instanceof Error ? e.message : String(e));
const blankCondition = (): RuleCondition => ({ baseAttribute: '', matchingAttribute: '', operator: 0, ignoreBlank: false });

/** The columns of a table, read when the table is chosen. */
function useColumns(table: string): string[] {
  const { api } = useAuth();
  const [columns, setColumns] = useState<string[]>([]);
  useEffect(() => {
    if (!api || !table) {
      setColumns([]);
      return;
    }
    let live = true;
    loadColumns(api, table)
      .then((c) => live && setColumns(c))
      .catch(() => live && setColumns([]));
    return () => {
      live = false;
    };
  }, [api, table]);
  return columns;
}

/**
 * Creates or edits a rule with its conditions. The tables of an existing rule cannot change. The server judges everything else
 * (a table without duplicate detection, a column that does not exist, a rule that is published); its 400 is shown as it is.
 */
export function RuleDialog({ rule, tables, onClose, onSaved, onPartial }: { rule?: DuplicateRule; tables: TableChoice[]; onClose: () => void; onSaved: (name: string) => void; onPartial: () => void }) {
  const { api } = useAuth();
  const [name, setName] = useState(rule?.name ?? '');
  const [description, setDescription] = useState(rule?.description ?? '');
  const [baseTable, setBaseTable] = useState(rule?.baseTable ?? '');
  const [matchingTable, setMatchingTable] = useState(rule?.matchingTable ?? '');
  const [caseSensitive, setCaseSensitive] = useState(rule?.caseSensitive ?? false);
  const [excludeInactive, setExcludeInactive] = useState(rule?.excludeInactive ?? false);
  const [conditions, setConditions] = useState<RuleCondition[]>(rule ? [] : [blankCondition()]);
  const [before, setBefore] = useState<RuleCondition[]>([]);
  const [loading, setLoading] = useState(!!rule);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const baseColumns = useColumns(baseTable);
  const matchingColumns = useColumns(matchingTable);
  const usable = tables.filter((x) => x.enabled || x.logicalName === rule?.baseTable || x.logicalName === rule?.matchingTable);

  useEffect(() => {
    if (!rule || !api) return;
    let live = true;
    loadConditions(api, rule.id)
      .then((rows) => {
        if (!live) return;
        setBefore(rows);
        setConditions(rows);
      })
      .catch((e) => live && setError(t('duplicates.dialog.loadFailed', { detail: messageOf(e) })))
      .finally(() => live && setLoading(false));
    return () => {
      live = false;
    };
  }, [api, rule]);

  const patch = (i: number, change: Partial<RuleCondition>) => setConditions((all) => all.map((c, at) => (at === i ? { ...c, ...change } : c)));
  const complete =
    name.trim() !== '' &&
    baseTable !== '' &&
    matchingTable !== '' &&
    conditions.length > 0 &&
    conditions.every((c) => c.baseAttribute && c.matchingAttribute && (!takesCount(c.operator) || (c.param ?? 0) >= 1));

  async function save() {
    if (!api) return;
    setBusy(true);
    setError(undefined);
    const input = { name: name.trim(), description: description.trim(), baseTable, matchingTable, caseSensitive, excludeInactive };
    try {
      if (rule) await updateRule(api, rule.id, input, conditions, before);
      else await createRule(api, input, conditions);
      onSaved(input.name);
    } catch (e) {
      if (rule) {
        // The edit is several calls: some may have been saved. Show what the server holds now, so Save can be tried again.
        setError(`${messageOf(e)} ${t('duplicates.dialog.partial')}`);
        try {
          const fresh = await loadConditions(api, rule.id);
          setBefore(fresh);
          setConditions(fresh);
        } catch {
          /* the first error stays on screen */
        }
        onPartial();
      } else setError(messageOf(e));
    } finally {
      setBusy(false);
    }
  }

  const columnSelect = (label: string, value: string, columns: string[], onChange: (v: string) => void) => (
    <Select aria-label={label} value={value} onChange={(_, d) => onChange(d.value)}>
      <option value="">{t('duplicates.dialog.columnPlaceholder')}</option>
      {value && !columns.includes(value) && <option value={value}>{value}</option>}
      {columns.map((c) => (
        <option key={c} value={c}>
          {c}
        </option>
      ))}
    </Select>
  );

  const tableSelect = (label: string, value: string, onChange: (v: string) => void, disabled: boolean) => (
    <Field label={label}>
      <Select value={value} disabled={disabled} onChange={(_, d) => onChange(d.value)}>
        <option value="">{t('duplicates.dialog.tablePlaceholder')}</option>
        {usable.map((x) => (
          <option key={x.logicalName} value={x.logicalName}>
            {x.logicalName}
          </option>
        ))}
      </Select>
    </Field>
  );

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{rule ? t('duplicates.dialog.editTitle', { name: rule.name }) : t('duplicates.dialog.createTitle')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {loading && <Spinner label={t('duplicates.loading')} />}
            {usable.length === 0 && (
              <MessageBar intent="warning">
                <MessageBarBody>{t('duplicates.dialog.noTables')}</MessageBarBody>
              </MessageBar>
            )}
            <Field label={t('duplicates.dialog.name')}>
              <Input value={name} onChange={(_, d) => setName(d.value)} />
            </Field>
            <Field label={t('duplicates.dialog.description')}>
              <Input value={description} onChange={(_, d) => setDescription(d.value)} />
            </Field>
            {tableSelect(t('duplicates.dialog.base'), baseTable, setBaseTable, !!rule)}
            {tableSelect(t('duplicates.dialog.matching'), matchingTable, setMatchingTable, !!rule)}
            <Checkbox label={t('duplicates.dialog.caseSensitive')} checked={caseSensitive} onChange={(_, d) => setCaseSensitive(d.checked === true)} />
            <Checkbox label={t('duplicates.dialog.excludeInactive')} checked={excludeInactive} onChange={(_, d) => setExcludeInactive(d.checked === true)} />
            <div role="group" aria-label={t('duplicates.dialog.conditions')} style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {conditions.map((c, i) => {
                const n = i + 1;
                return (
                  <div key={c.id ?? `new-${i}`} style={{ display: 'flex', flexWrap: 'wrap', gap: 8, alignItems: 'center' }}>
                    {columnSelect(t('duplicates.dialog.baseColumn', { n }), c.baseAttribute, baseColumns, (v) =>
                      patch(i, { baseAttribute: v, matchingAttribute: c.matchingAttribute || (baseTable === matchingTable ? v : '') }),
                    )}
                    {columnSelect(t('duplicates.dialog.matchingColumn', { n }), c.matchingAttribute, matchingColumns, (v) => patch(i, { matchingAttribute: v }))}
                    <Select aria-label={t('duplicates.dialog.operator', { n })} value={String(c.operator)} onChange={(_, d) => patch(i, { operator: Number(d.value) })}>
                      {OPERATORS.map((op) => (
                        <option key={op} value={op}>
                          {t(`duplicates.op.${op}` as 'duplicates.op.0')}
                        </option>
                      ))}
                    </Select>
                    {takesCount(c.operator) && (
                      <Input
                        type="number"
                        min={1}
                        aria-label={t('duplicates.dialog.count', { n })}
                        value={c.param === undefined ? '' : String(c.param)}
                        onChange={(_, d) => patch(i, { param: d.value === '' ? undefined : Number(d.value) })}
                      />
                    )}
                    <Checkbox
                      label={t('duplicates.dialog.ignoreBlank', { n })}
                      checked={c.ignoreBlank}
                      onChange={(_, d) => patch(i, { ignoreBlank: d.checked === true })}
                    />
                    <Button appearance="subtle" onClick={() => setConditions((all) => all.filter((_, at) => at !== i))}>
                      {t('duplicates.dialog.removeCondition', { n })}
                    </Button>
                  </div>
                );
              })}
              <div>
                <Button onClick={() => setConditions((all) => [...all, blankCondition()])}>{t('duplicates.dialog.addCondition')}</Button>
              </div>
            </div>
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" disabled={busy} onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button appearance="primary" disabled={busy || loading || !complete} onClick={() => void save()}>
              {t('duplicates.dialog.save')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
