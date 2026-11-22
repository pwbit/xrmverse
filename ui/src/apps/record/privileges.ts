import type { XrmApi } from '../../shell/api/client';

/** The signed-in user's privilege names (`RetrieveUserPrivileges`); a form reads them once through its cache. */
export async function heldPrivileges(api: XrmApi): Promise<Set<string>> {
  const who = await api.get<{ UserId: string }>('WhoAmI');
  const result = await api.get<{ RolePrivileges: { PrivilegeName: string }[] }>(`systemusers(${who.UserId})/Microsoft.Dynamics.CRM.RetrieveUserPrivileges()`);
  return new Set(result.RolePrivileges.map((p) => p.PrivilegeName));
}

export type PrivilegeVerb = 'Create' | 'Read' | 'Write' | 'Delete' | 'Append' | 'AppendTo' | 'Assign' | 'Share';

/**
 * The user holds `prv{verb}` on `table`: `prv{verb}{SchemaName}`, or for an activity table the shared `prv{verb}Activity`
 * (the server names the privileges of the pointer and of every activity table with the stem `Activity`, as Dataverse does
 * for its activities). False when the SchemaName is unknown and the table is not an activity.
 */
export function holds(held: ReadonlySet<string>, verb: PrivilegeVerb, table: { schemaName?: string; isActivity?: boolean }): boolean {
  if (table.isActivity === true && held.has(`prv${verb}Activity`)) return true;
  return !!table.schemaName && held.has(`prv${verb}${table.schemaName}`);
}
