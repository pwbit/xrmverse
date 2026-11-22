import { useCallback, useEffect, useMemo, useState } from 'react';
import { useLoadedUserSettings } from '../../apps/userSettings';
import { loadColumnKinds, type ColumnKind } from '../../apps/views/columnKinds';
import type { ViewColumn } from '../../model/view';
import { useOptionalAuth } from '../../shell/auth/AuthProvider';
import { badgeColors, choiceKey } from './choiceColors';
import { usePresentationModel } from './PresentationContext';

/** One choice value shown as a badge: its label and its colours. */
export interface ChoiceBadgeValue {
  label: string;
  background: string;
  text: string;
}

/** The badges of a cell's raw value, or undefined when the value is shown as plain text. */
export type ChoicePalette = (column: ViewColumn, raw: unknown) => ChoiceBadgeValue[] | undefined;

const NO_CHOICES = {};

/**
 * The badges of the choice columns of `table`: reads the kinds of the root columns once (labels in the user's language) and
 * gives each coloured value its badge. A related-table column, a non-choice column, an unknown value, or a value with an
 * option without colour (a multi-select one included) gives undefined: the cell keeps its text. No table, no reading.
 */
export function useChoicePalette(table: string | undefined, columns: ViewColumn[]): ChoicePalette {
  const api = useOptionalAuth()?.api;
  const lcid = useLoadedUserSettings()?.lcid ?? 1033;
  const choices = usePresentationModel()?.choices ?? NO_CHOICES;
  const names = useMemo(() => [...new Set(columns.filter((c) => !c.alias).map((c) => c.attribute))], [columns]);
  const key = table && names.length > 0 ? [table, lcid, ...names].join('\n') : '';
  const [loaded, setLoaded] = useState<{ key: string; kinds: Record<string, ColumnKind> }>();

  useEffect(() => {
    if (!api || !table || !key) return;
    let cancelled = false;
    loadColumnKinds(api, table, names, lcid).then(
      (kinds) => !cancelled && setLoaded({ key, kinds }),
      (e: unknown) => {
        if (cancelled) return;
        console.warn('[xrmverse choices]', `The choice colours of '${table}' could not be read: ${e instanceof Error ? e.message : String(e)}`);
        setLoaded({ key, kinds: {} });
      },
    );
    return () => {
      cancelled = true;
    };
    // key stands for the table, the language and the names.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, key]);

  const kinds = loaded && loaded.key === key ? loaded.kinds : undefined;
  return useCallback<ChoicePalette>(
    (column, raw) => {
      if (!table || !kinds || column.alias) return undefined;
      const kind = kinds[column.attribute];
      if (kind?.kind !== 'choice') return undefined;
      const values: unknown[] =
        typeof raw === 'number' || typeof raw === 'boolean'
          ? [raw]
          : typeof raw === 'string' && kind.type === 'MultiSelectPicklist' && raw.trim() !== ''
            ? raw.split(',').map((v) => Number(v.trim()))
            : [];
      if (values.length === 0) return undefined;
      const overlayKey = choiceKey(table, column.attribute, kind.globalName);
      const badges: ChoiceBadgeValue[] = [];
      for (const value of values) {
        const option = kind.options.find((o) => o.value === value);
        if (!option) return undefined;
        const colors = badgeColors({ value: Number(option.value), color: option.color }, overlayKey, choices);
        if (!colors) return undefined;
        badges.push({ label: option.label, ...colors });
      }
      return badges;
    },
    [table, kinds, choices],
  );
}
