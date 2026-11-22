import type { EntityMeta } from '../../../admin/tables/metadata';
import type { LookupValue } from '../../../admin/tables/formValues';
import type { XrmApi } from '../../../shell/api/client';
import { mainAspxUrl } from '../../mainAspx';
import { holds } from '../privileges';
import { defaultStatusOf, statesOf } from '../recordStates';
import { defaultShownTypes } from './filters';

/** The record a new activity is regarding. */
export interface RegardingRecord {
  table: string;
  id: string;
  /** Its primary name: the Regarding lookup's label on the new activity. */
  name: string;
}

/**
 * The activity tables the + menu lists (spec §6.4): those the user may create (`prvCreate`), without fax and letter, by
 * display name.
 */
export function creatableActivityTypes(activityTables: EntityMeta[], held: ReadonlySet<string>): EntityMeta[] {
  const shown = new Set(defaultShownTypes(activityTables.map((e) => e.logicalName)));
  return activityTables
    .filter((e) => shown.has(e.logicalName) && holds(held, 'Create', e))
    .sort((a, b) => a.displayName.localeCompare(b.displayName));
}

/** The main form of a new `table` activity, its Regarding set to `parent` (`extraqs`, as a subgrid's New sets its lookup). */
export function newActivityUrl(appId: string | undefined, table: string, parent: RegardingRecord): string {
  return mainAspxUrl({
    appId,
    pageType: 'entityrecord',
    etn: table,
    extraqs: { regardingobjectid: parent.id, regardingobjectidname: parent.name, regardingobjectidtype: parent.table },
  });
}

/**
 * What Complete sets: state 1 (Completed) with its default status reason. Undefined for an email (it is sent, not completed)
 * and for a table without state 1.
 */
export function completeState(entity: EntityMeta): { state: number; status?: number } | undefined {
  if (entity.logicalName === 'email') return undefined;
  const completed = statesOf(entity).find((s) => s.value === 1);
  if (!completed) return undefined;
  const status = defaultStatusOf(completed);
  return status === undefined ? { state: completed.value } : { state: completed.value, status };
}

/** The quick actions an activity table allows the user, besides Open (spec §5.4): each needs its privilege. */
export function activityActionsAllowed(held: ReadonlySet<string>, table: EntityMeta) {
  return { complete: holds(held, 'Write', table), assign: holds(held, 'Assign', table), delete: holds(held, 'Delete', table) };
}

/** Complete: one PATCH of `statecode` and `statuscode`. */
export async function setActivityState(api: XrmApi, set: string, id: string, target: { state: number; status?: number }): Promise<void> {
  await api.patch(`${set}(${id})`, target.status === undefined ? { statecode: target.state } : { statecode: target.state, statuscode: target.status });
}

/** Assign: the owner set with a bind, as the form's Assign does. */
export async function assignActivity(api: XrmApi, set: string, id: string, owner: LookupValue): Promise<void> {
  const ownerSet = owner.set ?? (owner.target === 'team' ? 'teams' : 'systemusers');
  await api.patch(`${set}(${id})`, { 'ownerid@odata.bind': `/${ownerSet}(${owner.id})` });
}
