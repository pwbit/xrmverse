import { isNumeric, type AttributeMeta } from '../tables/metadata';
import type { ColumnKind } from './filterModel';

/** The E5 kind of a column (status, state and two-option columns are choices); undefined columns are `other`. */
export function columnKind(a: AttributeMeta | undefined): ColumnKind {
  if (!a) return 'other';
  if (a.kind === 'string' || a.kind === 'memo') return 'text';
  if (isNumeric(a.kind)) return 'number';
  if (a.kind === 'datetime') return 'date';
  if (a.kind === 'choice' || a.kind === 'bool') return 'choice';
  if (a.kind === 'lookup') return 'lookup';
  return 'other';
}

/** A column of the table a condition can use: readable, shown, not a companion, of an E5 kind. */
export const filterable = (a: AttributeMeta): boolean =>
  a.validForRead && !a.hidden && !a.attributeOf && columnKind(a) !== 'other';

/** The filterable columns by display name. */
export const filterColumns = (attributes: AttributeMeta[]): AttributeMeta[] =>
  attributes.filter(filterable).sort((a, b) => a.displayName.localeCompare(b.displayName));
