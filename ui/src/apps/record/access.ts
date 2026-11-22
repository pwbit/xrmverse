import type { XrmApi } from '../../shell/api/client';

/** The rights of a record that Dataverse's `AccessRights` flags name (without the `Access` suffix). */
export type AccessRight = 'Read' | 'Write' | 'Append' | 'AppendTo' | 'Create' | 'Delete' | 'Share' | 'Assign';

/** The `AccessRights` member names, in the order the share dialog lists them. */
export const ACCESS_RIGHTS: readonly AccessRight[] = ['Read', 'Write', 'Delete', 'Append', 'AppendTo', 'Assign', 'Share', 'Create'];

export type PrincipalType = 'systemuser' | 'team';
export interface Principal {
  type: PrincipalType;
  id: string;
}
export interface Share {
  principal: Principal;
  rights: AccessRight[];
}

const NS = 'Microsoft.Dynamics.CRM';

/** `"ReadAccess, WriteAccess"` (the enum string form of the Web API) to rights; `None` gives no right. An unknown name throws. */
export function parseAccessRights(text: string | null | undefined): AccessRight[] {
  const rights: AccessRight[] = [];
  for (const part of (text ?? '').split(',').map((p) => p.trim()).filter(Boolean)) {
    if (part === 'None') continue;
    const name = part.replace(/Access$/, '') as AccessRight;
    if (!part.endsWith('Access') || !ACCESS_RIGHTS.includes(name)) throw new Error(`Unknown access right "${part}".`);
    rights.push(name);
  }
  return rights;
}

/** Rights to the enum string form (`ReadAccess, WriteAccess`). */
export const formatAccessRights = (rights: AccessRight[]): string => (rights.length ? rights.map((r) => `${r}Access`).join(', ') : 'None');

const target = (set: string, id: string) => ({ '@odata.id': `${set}(${id})` });
const aliasOf = (set: string, id: string) => encodeURIComponent(JSON.stringify(target(set, id)));
const entityOf = (p: Principal) => ({ '@odata.type': `${NS}.${p.type}`, [`${p.type}id`]: p.id });

/** The rights the user has on the record `set(id)` (`RetrievePrincipalAccess`, bound to the user). */
export async function retrievePrincipalAccess(api: XrmApi, userId: string, set: string, id: string): Promise<AccessRight[]> {
  const result = await api.get<{ AccessRights: string }>(
    `systemusers(${userId})/${NS}.RetrievePrincipalAccess(Target=@t)?@t=${aliasOf(set, id)}`,
  );
  return parseAccessRights(result.AccessRights);
}

/** The principals the record is shared with, and their rights (`RetrieveSharedPrincipalsAndAccess`). */
export async function retrieveShares(api: XrmApi, set: string, id: string): Promise<Share[]> {
  const result = await api.get<{ PrincipalAccesses: { AccessMask: string; Principal: Record<string, unknown> }[] }>(
    `RetrieveSharedPrincipalsAndAccess(Target=@t)?@t=${aliasOf(set, id)}`,
  );
  return result.PrincipalAccesses.map(({ AccessMask, Principal }) => {
    const type: PrincipalType = String(Principal['@odata.type']).toLowerCase().endsWith('team') ? 'team' : 'systemuser';
    return { principal: { type, id: String(Principal[`${type}id`]) }, rights: parseAccessRights(AccessMask) };
  });
}

const principalAccess = (principal: Principal, rights: AccessRight[]) => ({
  AccessMask: formatAccessRights(rights),
  Principal: entityOf(principal),
});

export async function grantAccess(api: XrmApi, set: string, id: string, principal: Principal, rights: AccessRight[]): Promise<void> {
  await api.post('GrantAccess', { Target: target(set, id), PrincipalAccess: principalAccess(principal, rights) });
}

/** Replaces the principal's rights on the record with `rights`. */
export async function modifyAccess(api: XrmApi, set: string, id: string, principal: Principal, rights: AccessRight[]): Promise<void> {
  await api.post('ModifyAccess', { Target: target(set, id), PrincipalAccess: principalAccess(principal, rights) });
}

export async function revokeAccess(api: XrmApi, set: string, id: string, principal: Principal): Promise<void> {
  await api.post('RevokeAccess', { Target: target(set, id), Revokee: entityOf(principal) });
}
