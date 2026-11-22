import { Input, Select } from '@fluentui/react-components';
import { t } from '../../shell/i18n/t';
import { ChoicesField } from '../tables/fields/ChoicesField';
import { optionsOf, type FieldContext } from '../tables/fields/fieldProps';
import { LookupField } from '../tables/fields/LookupField';
import type { AttributeMeta, OptionMeta } from '../tables/metadata';
import { columnKind } from './filterColumns';
import { noValueOperator, type FilterCondition } from './filterModel';

/** How many values an operator takes: none, a count of days, a list (`in`), or one. */
export type ValueShape = 'none' | 'count' | 'multi' | 'single';

export function valueShape(operator: string): ValueShape {
  if (noValueOperator(operator)) return 'none';
  if (operator === 'last-x-days' || operator === 'next-x-days') return 'count';
  if (operator === 'in' || operator === 'not-in') return 'multi';
  return 'single';
}

const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;

/** A lookup value picked in this session: its name and table, shown instead of the bare id. */
export interface PickedRecord {
  name?: string;
  target: string;
}

export interface ConditionValueProps {
  condition: FilterCondition;
  attr: AttributeMeta | undefined;
  /** The accessible name of the value editor ("Value of condition 1.2"). */
  label: string;
  table: string;
  globalChoices: Record<string, OptionMeta[]>;
  picked?: PickedRecord;
  onValues: (values: string[]) => void;
  onPicked: (record: PickedRecord | undefined) => void;
}

/**
 * The value editor of a condition (E5): a text, number or date input, a choice list (a checkbox group for `in`/`not-in`), the
 * admin's lookup field, or nothing for an operator without a value.
 */
export function ConditionValue({ condition, attr, label, table, globalChoices, picked, onValues, onPicked }: ConditionValueProps) {
  const shape = valueShape(condition.operator);
  if (shape === 'none') return null;
  const value = condition.values[0] ?? '';
  const kind = columnKind(attr);
  const input = (type: 'text' | 'number' | 'date') => (
    <Input aria-label={label} type={type} value={value} onChange={(_, d) => onValues(d.value === '' ? [] : [d.value])} />
  );
  if (shape === 'count') return input('number');
  if (!attr) return input('text');

  const context: FieldContext = {
    table,
    lookups: attr.targets.map((target) => ({
      schemaName: `${attr.logicalName}_${target}`,
      referencingEntity: table,
      referencingAttribute: attr.logicalName,
      referencedEntity: target,
    })),
    globalChoices,
  };
  // The admin's field editors take their label from the column: here it names the value.
  const named: AttributeMeta = { ...attr, displayName: label, required: false };

  if (kind === 'choice') {
    if (shape === 'multi') {
      return (
        <ChoicesField
          attr={named}
          value={condition.values.map(Number)}
          onChange={(v) => onValues(Array.isArray(v) ? v.map(String) : [])}
          disabled={false}
          context={context}
        />
      );
    }
    return (
      <Select aria-label={label} value={value} onChange={(_, d) => onValues(d.value === '' ? [] : [d.value])}>
        <option value="">{t('views.filter.choose')}</option>
        {optionsOf(attr, context).map((o) => (
          <option key={o.value} value={String(o.value)}>
            {o.label}
          </option>
        ))}
        {value !== '' && !optionsOf(attr, context).some((o) => String(o.value) === value) && <option value={value}>{value}</option>}
      </Select>
    );
  }
  if (kind === 'lookup') {
    const target = picked?.target ?? (attr.targets.length === 1 ? attr.targets[0] : '');
    return (
      <LookupField
        attr={named}
        value={value ? { id: value, target, name: picked?.name } : null}
        onChange={(v) => {
          if (v && typeof v === 'object' && !Array.isArray(v)) {
            onPicked({ name: v.name, target: v.target });
            onValues([v.id]);
          } else {
            onPicked(undefined);
            onValues([]);
          }
        }}
        disabled={false}
        context={context}
      />
    );
  }
  if (kind === 'number') return input('number');
  if (kind === 'date') return input(value === '' || ISO_DATE.test(value) ? 'date' : 'text');
  return input('text');
}
