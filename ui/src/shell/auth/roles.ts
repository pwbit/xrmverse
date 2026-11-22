import { createContext, useContext, useEffect, useState } from 'react';
import { XrmError } from '../api/errors';
import type { XrmApi } from '../api/client';
import { useAuth } from './AuthProvider';

/**
 * The standard roles are recognised by `role.roletemplateid` (the server sets it on the root business unit's copies,
 * see `Xrmd.Security.StandardRoles`), never by name: names can be edited or duplicated in custom roles.
 */
export const SYSTEM_ADMINISTRATOR_TEMPLATE = '627090ff-40a3-4053-8790-584edc5be201';
export const SYSTEM_CUSTOMIZER_TEMPLATE = '119f245c-3cc8-4b62-b31c-d1a046ced15d';

export type AccessLevel = 'admin' | 'customizer' | 'denied' | 'loading' | 'error';
export interface Access {
  level: AccessLevel;
  /** Names of the roles the user holds directly or through owner teams. */
  roles: string[];
  error?: XrmError;
}

const LOADING: Access = { level: 'loading', roles: [] };

export const AccessContext = createContext<Access>(LOADING);

/** Access of the signed-in user; only meaningful below `<AccessGate>`. */
export function useAccess(): Access {
  return useContext(AccessContext);
}

interface RoleRow {
  roleid: string;
  name: string;
  roletemplateid?: string | null;
}

export async function collect<T>(api: XrmApi, path: string): Promise<T[]> {
  const rows: T[] = [];
  for await (const page of api.list<T>(path)) rows.push(...page);
  return rows;
}

export const orFilter = (field: string, ids: string[]) => ids.map((id) => `${field} eq ${id}`).join(' or ');

/** The longest `$filter` one request carries (#110): a longer id list is split over several requests. */
export const MAX_FILTER_LENGTH = 2000;

/** The id lists of `ids` whose `orFilter(field, …)` stays within {@link MAX_FILTER_LENGTH} (one id per list at least). */
export function idChunks(field: string, ids: string[]): string[][] {
  const chunks: string[][] = [];
  let current: string[] = [];
  for (const id of ids) {
    if (current.length && orFilter(field, [...current, id]).length > MAX_FILTER_LENGTH) {
      chunks.push(current);
      current = [];
    }
    current.push(id);
  }
  if (current.length) chunks.push(current);
  return chunks;
}

/** The rows of `path` (with its `$select`) whose `field` is one of `ids`, the id list split so no URL grows unbounded. */
export async function collectByIds<T>(api: XrmApi, path: string, field: string, ids: string[]): Promise<T[]> {
  const pages = await Promise.all(idChunks(field, ids).map((chunk) => collect<T>(api, `${path}&$filter=${orFilter(field, chunk)}`)));
  return pages.flat();
}

/**
 * The ids (lower case) of the roles `userId` holds directly or through owner teams. Only owner teams (teamtype 0, or
 * unset) pass their roles to members, as on the server (SecurityCache). The intersects are readable by every user.
 */
export async function userRoleIds(api: XrmApi, userId: string): Promise<string[]> {
  const userRoles = await collect<{ roleid: string }>(
    api,
    `systemuserrolescollection?$select=roleid&$filter=systemuserid eq ${userId}`,
  );
  const memberships = await collect<{ teamid: string }>(
    api,
    `teammemberships?$select=teamid&$filter=systemuserid eq ${userId}`,
  );
  const teams = memberships.length
    ? (
        await collectByIds<{ teamid: string; teamtype?: number | null }>(
          api,
          'teams?$select=teamid,teamtype',
          'teamid',
          memberships.map((x) => x.teamid),
        )
      ).filter((t) => (t.teamtype ?? 0) === 0)
    : [];
  const teamRoles = teams.length
    ? await collectByIds<{ roleid: string }>(api, 'teamrolescollection?$select=roleid', 'teamid', teams.map((x) => x.teamid))
    : [];
  return [...new Set([...userRoles, ...teamRoles].map((r) => r.roleid.toLowerCase()))];
}

/** WhoAmI, then the user's roles and the roles of the user's owner teams. Throws XrmError. */
export async function resolveAccess(api: XrmApi): Promise<Access> {
  const me = await api.get<{ UserId: string }>('WhoAmI');
  const ids = await userRoleIds(api, me.UserId);
  const roles = ids.length
    ? await collectByIds<RoleRow>(api, `roles?$select=roleid,name,roletemplateid`, 'roleid', ids)
    : [];
  const templates = new Set(roles.map((r) => r.roletemplateid?.toLowerCase()).filter(Boolean));
  const level: AccessLevel = templates.has(SYSTEM_ADMINISTRATOR_TEMPLATE)
    ? 'admin'
    : templates.has(SYSTEM_CUSTOMIZER_TEMPLATE)
      ? 'customizer'
      : 'denied';
  return { level, roles: roles.map((r) => r.name).sort() };
}

/** Loads the access once per sign-in. A 403 (no system user, or no privilege to read these tables) is `denied`. */
export function useLoadAccess(): Access {
  const { api, status } = useAuth();
  const [access, setAccess] = useState<Access>(LOADING);
  useEffect(() => {
    if (status !== 'signedIn' || !api) return;
    let cancelled = false;
    setAccess(LOADING);
    resolveAccess(api).then(
      (result) => !cancelled && setAccess(result),
      (e: unknown) => {
        if (cancelled) return;
        if (e instanceof XrmError && e.status === 401) return; // signing out; the gate shows the sign-in page
        if (e instanceof XrmError && e.status === 403) setAccess({ level: 'denied', roles: [], error: e });
        else setAccess({ level: 'error', roles: [], error: e instanceof XrmError ? e : new XrmError(0, String(e)) });
      },
    );
    return () => {
      cancelled = true;
    };
  }, [api, status]);
  return access;
}
