import type { XrmApi } from '../../shell/api/client';

/** `roleprivileges.privilegedepthmask` bits, widest last. The names are the Web API's `PrivilegeDepth` members. */
export const DEPTHS = [
  { name: 'Basic', mask: 1, canBe: 'canbebasic' },
  { name: 'Local', mask: 2, canBe: 'canbelocal' },
  { name: 'Deep', mask: 4, canBe: 'canbedeep' },
  { name: 'Global', mask: 8, canBe: 'canbeglobal' },
] as const;

export type DepthName = (typeof DEPTHS)[number]['name'];

export interface Privilege {
  privilegeid: string;
  name: string;
  canbebasic?: boolean | null;
  canbelocal?: boolean | null;
  canbedeep?: boolean | null;
  canbeglobal?: boolean | null;
}

/** Longest first, so `AppendTo` is not read as `Append` + `To...`. */
const PARSE_ACTIONS = ['AppendTo', 'Append', 'Create', 'Read', 'Write', 'Delete', 'Assign', 'Share'] as const;

/** Splits `prv{Action}{Entity}`; a name of another shape has no table (`table` is empty). */
export function parsePrivilegeName(name: string): { action: string; table: string } {
  if (name.startsWith('prv')) {
    const rest = name.slice(3);
    const action = PARSE_ACTIONS.find((a) => rest.startsWith(a) && rest.length > a.length);
    if (action) return { action, table: rest.slice(action.length) };
  }
  return { action: name, table: '' };
}

/** The widest depth set in the mask; the matrix shows one choice per privilege. */
export function highestDepth(mask: number): DepthName | undefined {
  return [...DEPTHS].reverse().find((d) => (mask & d.mask) !== 0)?.name;
}

/** The eight privilege actions of a table, in display order (`prv{Action}{SchemaName}`). */
export const ACTIONS = ['Create', 'Read', 'Write', 'Delete', 'Append', 'AppendTo', 'Assign', 'Share'] as const;

/** The names of a table's privileges, e.g. `prvCreateAccount`, `prvReadAccount`, ... */
export const privilegeNamesOf = (schemaName: string) => ACTIONS.map((a) => `prv${a}${schemaName}`);

/** `POST roles({id})/Microsoft.Dynamics.CRM.AddPrivilegesRole`; the role's business unit goes with the privilege as `BusinessUnitId`. */
export function addPrivilege(api: XrmApi, roleId: string, privilegeId: string, depth: DepthName, businessUnitId: string | null | undefined) {
  return api.action(`roles(${roleId})/Microsoft.Dynamics.CRM.AddPrivilegesRole`, {
    Privileges: [{ Depth: depth, PrivilegeId: privilegeId, ...(businessUnitId ? { BusinessUnitId: businessUnitId } : {}) }],
  });
}

/** `POST roles({id})/Microsoft.Dynamics.CRM.RemovePrivilegeRole`. */
export function removePrivilege(api: XrmApi, roleId: string, privilegeId: string) {
  return api.action(`roles(${roleId})/Microsoft.Dynamics.CRM.RemovePrivilegeRole`, {
    Privilege: { '@odata.type': 'Microsoft.Dynamics.CRM.privilege', privilegeid: privilegeId },
  });
}
