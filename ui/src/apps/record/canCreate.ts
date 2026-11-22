import type { XrmApi } from '../../shell/api/client';
import { holds } from './privileges';

/**
 * The user holds `prvCreate{SchemaName}` (`RetrieveUserPrivileges`), or for an activity table the shared `prvCreateActivity`
 * (see {@link holds}); false when the table's SchemaName is unknown and it is not an activity.
 */
export async function canCreate(api: XrmApi, userId: string, table: { schemaName?: string; isActivity?: boolean }): Promise<boolean> {
  return (await tableRights(api, userId, table)).create;
}

/** Whether the user holds the table's Create and Delete privileges, with one `RetrieveUserPrivileges` (as {@link canCreate}). */
export async function tableRights(
  api: XrmApi,
  userId: string,
  table: { schemaName?: string; isActivity?: boolean },
): Promise<{ create: boolean; delete: boolean }> {
  if (!table.schemaName && table.isActivity !== true) return { create: false, delete: false };
  const result = await api.get<{ RolePrivileges: { PrivilegeName: string }[] }>(
    `systemusers(${userId})/Microsoft.Dynamics.CRM.RetrieveUserPrivileges()`,
  );
  const held = new Set(result.RolePrivileges.map((p) => p.PrivilegeName));
  return { create: holds(held, 'Create', table), delete: holds(held, 'Delete', table) };
}
