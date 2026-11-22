import type { LookupValue } from '../../admin/tables/formValues';
import type { XrmApi } from '../../shell/api/client';
import { idChunks, orFilter } from '../../shell/auth/roles';
import { t } from '../../shell/i18n/t';

/** Whether a command of the view's bar may run; when not, why (shown as its tooltip). An empty reason hides it. */
export type Enabled = { enabled: true } | { enabled: false; reason: string };

/** A selected record of the view: its id and its `statecode` once read. */
export interface SelectedRecord {
  id: string;
  statecode?: number;
}

export interface ViewCommandState {
  activate: Enabled;
  deactivate: Enabled;
  assign: Enabled;
  share: Enabled;
  delete: Enabled;
}

const ENABLED: Enabled = { enabled: true };
const disabled = (reason: string): Enabled => ({ enabled: false, reason });

/**
 * Which of the view's record commands apply to the selection: Assign, Share and Delete to one record or more; Activate when every
 * selected record is inactive, Deactivate when every one is active (a mixed selection allows neither). A table without
 * states has no Activate or Deactivate (empty reason: the bar leaves them out).
 */
export function commandState(sel: SelectedRecord[], table: { hasState: boolean }): ViewCommandState {
  if (sel.length === 0) {
    const none = disabled(t('apps.views.cmd.noSelection'));
    return { activate: table.hasState ? none : disabled(''), deactivate: table.hasState ? none : disabled(''), assign: none, share: none, delete: none };
  }
  let activate: Enabled;
  let deactivate: Enabled;
  if (!table.hasState) {
    activate = deactivate = disabled('');
  } else if (sel.some((r) => r.statecode === undefined)) {
    activate = deactivate = disabled(t('apps.views.cmd.statesUnknown'));
  } else if (sel.every((r) => r.statecode === 0)) {
    activate = disabled(t('apps.views.cmd.allActive'));
    deactivate = ENABLED;
  } else if (sel.every((r) => r.statecode !== 0)) {
    activate = ENABLED;
    deactivate = disabled(t('apps.views.cmd.allInactive'));
  } else {
    activate = deactivate = disabled(t('apps.views.cmd.mixedStates'));
  }
  return { activate, deactivate, assign: ENABLED, share: ENABLED, delete: ENABLED };
}

/** Activate or Deactivate one record, as the form does: one PATCH of `statecode` and `statuscode` (only `statecode` without a status). */
export const setStateOp =
  (api: XrmApi, set: string, state: number, status?: number) =>
  async (id: string): Promise<void> => {
    await api.patch(`${set}(${id})`, status === undefined ? { statecode: state } : { statecode: state, statuscode: status });
  };

/** Delete one record, as the form does: one DELETE. */
export const deleteOp =
  (api: XrmApi, set: string) =>
  async (id: string): Promise<void> => {
    await api.delete(`${set}(${id})`);
  };

/** Assign one record, as the form does: a PATCH that binds `ownerid` to the user or the team. */
export const assignOp =
  (api: XrmApi, set: string, owner: LookupValue) =>
  async (id: string): Promise<void> => {
    const ownerSet = owner.set ?? (owner.target === 'team' ? 'teams' : 'systemusers');
    await api.patch(`${set}(${id})`, { 'ownerid@odata.bind': `/${ownerSet}(${owner.id})` });
  };

/**
 * The `statecode` of each record, by id (lower case), read with one request (the server's `$filter` has no `in`: an `or` of
 * `eq`); a selection too long for one URL is split over a few.
 */
export async function readStates(api: XrmApi, set: string, primaryId: string, ids: string[]): Promise<Record<string, number>> {
  const pages = await Promise.all(
    idChunks(primaryId, ids).map((chunk) =>
      api.get<{ value: Record<string, unknown>[] }>(`${set}?$select=${primaryId},statecode&$filter=${orFilter(primaryId, chunk)}`),
    ),
  );
  const states: Record<string, number> = {};
  for (const row of pages.flatMap((p) => p.value)) {
    const id = row[primaryId];
    if (typeof id === 'string' && typeof row.statecode === 'number') states[id.toLowerCase()] = row.statecode;
  }
  return states;
}
