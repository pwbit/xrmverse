import type { EntityMeta, OptionMeta } from '../../admin/tables/metadata';

/** A state of a table (a `statecode` option) with the status reasons (`statuscode` options) that belong to it. */
export interface RecordStateMeta {
  value: number;
  label: string;
  /** The status reason the state starts with (`DefaultStatus`), when the server returned it. */
  defaultStatus?: number;
  statuses: OptionMeta[];
}

/**
 * The states of `entity`, in metadata order, each with its status reasons. Empty when the table has no `statecode` or fewer
 * than two states: such a table has no Activate or Deactivate.
 */
export function statesOf(entity: EntityMeta): RecordStateMeta[] {
  const state = entity.attributes.find((a) => a.logicalName === 'statecode');
  if (!state || state.options.length < 2) return [];
  const status = entity.attributes.find((a) => a.logicalName === 'statuscode');
  return state.options.map((o) => ({
    value: o.value,
    label: o.label,
    ...(o.defaultStatus !== undefined ? { defaultStatus: o.defaultStatus } : {}),
    statuses: (status?.options ?? []).filter((s) => s.state === o.value),
  }));
}

/** The record's `statecode`, when it was read. */
export function stateOfRecord(record: Record<string, unknown> | undefined): number | undefined {
  const v = record?.statecode;
  return typeof v === 'number' ? v : undefined;
}

/** The status reason a change to `state` selects first: its `DefaultStatus` when it is one of its reasons, else the first one. */
export function defaultStatusOf(state: RecordStateMeta): number | undefined {
  if (state.defaultStatus !== undefined && state.statuses.some((s) => s.value === state.defaultStatus)) return state.defaultStatus;
  return state.statuses[0]?.value;
}
