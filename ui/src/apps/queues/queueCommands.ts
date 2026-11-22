import type { XrmApi } from '../../shell/api/client';
import { t } from '../../shell/i18n/t';
import type { Enabled } from '../views/viewCommands';

const NS = 'Microsoft.Dynamics.CRM';

/** The table a view or a form lists, as far as the queue commands need it. */
export interface QueueTable {
  logicalName: string;
  /** The primary key attribute (`activityid` for an email). */
  primaryId: string;
  /** `IsValidForQueue` of the table's metadata: its records may be put in a queue. */
  isValidForQueue?: boolean;
}

/** Where RouteTo sends a queue item: a queue, a user or a team. */
export interface RouteTarget {
  kind: 'queue' | 'systemuser' | 'team';
  id: string;
  name: string;
}

export interface QueueCommandState {
  add: Enabled;
  pick: Enabled;
  release: Enabled;
  route: Enabled;
  remove: Enabled;
}

const HIDDEN: Enabled = { enabled: false, reason: '' };
const ENABLED: Enabled = { enabled: true };

/**
 * Which queue commands apply: Add to queue on a table that is valid for queues, Pick, Release, Route and Remove from queue on
 * the queue item grid only. An empty reason hides the command; with nothing selected the ones that apply are disabled.
 */
export function queueCommandState(selectedCount: number, table: QueueTable | { logicalName: string; isValidForQueue?: boolean }): QueueCommandState {
  const isItem = table.logicalName === 'queueitem';
  const some = selectedCount > 0 ? ENABLED : ({ enabled: false, reason: t('apps.views.cmd.noSelection') } as Enabled);
  const add = !isItem && table.isValidForQueue === true ? some : HIDDEN;
  const onItem = isItem ? some : HIDDEN;
  return { add, pick: onItem, release: onItem, route: onItem, remove: onItem };
}

/** Add one record to `queueId`: AddToQueue bound to the queue, the record as `Target`. */
export const addToQueueOp =
  (api: XrmApi, table: QueueTable, queueId: string) =>
  async (id: string): Promise<void> => {
    await api.post(`queues(${queueId})/${NS}.AddToQueue`, { Target: { '@odata.type': `${NS}.${table.logicalName}`, [table.primaryId]: id } });
  };

/** Remove one queue item from its queue (the record stays). */
export const removeOp =
  (api: XrmApi) =>
  async (id: string): Promise<void> => {
    await api.post(`queueitems(${id})/${NS}.RemoveFromQueue`, {});
  };

/** Give one queue item back to its queue (clears the worker). */
export const releaseOp =
  (api: XrmApi) =>
  async (id: string): Promise<void> => {
    await api.post(`queueitems(${id})/${NS}.ReleaseToQueue`, {});
  };

/** Pick one queue item for the user `workerId`; the item stays in the queue. */
export const pickOp =
  (api: XrmApi, workerId: string) =>
  async (id: string): Promise<void> => {
    await api.post(`queueitems(${id})/${NS}.PickFromQueue`, {
      SystemUser: { '@odata.type': `${NS}.systemuser`, systemuserid: workerId },
      RemoveQueueItem: false,
    });
  };

/** Route one queue item to a queue, a user or a team (the unbound RouteTo). */
export const routeOp =
  (api: XrmApi, target: RouteTarget) =>
  async (id: string): Promise<void> => {
    await api.post('RouteTo', {
      Target: { '@odata.type': `${NS}.${target.kind}`, [`${target.kind === 'systemuser' ? 'systemuser' : target.kind}id`]: target.id },
      QueueItem: { queueitemid: id },
    });
  };

/** The signed-in user's id, for Pick. */
export async function currentUserId(api: XrmApi): Promise<string> {
  return (await api.get<{ UserId: string }>('WhoAmI')).UserId;
}
