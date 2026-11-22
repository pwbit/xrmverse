import type { XrmApi } from '../../shell/api/client';
import { entityPath } from '../tables/tableApi';

/** The access rights a template can grant: `Microsoft.Crm.Sdk.Messages.AccessRights` values, as the server accepts them. */
export const ACCESS_RIGHTS = [
  { key: 'read', bit: 1 },
  { key: 'write', bit: 2 },
  { key: 'append', bit: 4 },
  { key: 'appendto', bit: 16 },
  { key: 'delete', bit: 65536 },
  { key: 'share', bit: 262144 },
  { key: 'assign', bit: 524288 },
] as const;

export type RightKey = (typeof ACCESS_RIGHTS)[number]['key'];

export const maskOf = (rights: Iterable<RightKey>): number => {
  const set = new Set(rights);
  return ACCESS_RIGHTS.filter((r) => set.has(r.key)).reduce((sum, r) => sum + r.bit, 0);
};

export interface TemplateTable {
  logicalName: string;
  objectTypeCode: number;
  enabled: boolean;
}

export interface AccessTeamTemplate {
  id: string;
  name: string;
  objectTypeCode: number;
  mask: number;
}

interface RawTable {
  LogicalName: string;
  ObjectTypeCode?: number | null;
  AutoCreateAccessTeams?: boolean | null;
}

interface RawTemplate {
  teamtemplateid: string;
  teamtemplatename: string;
  objecttypecode: number;
  defaultaccessrightsmask: number;
}

async function all<T>(api: XrmApi, path: string): Promise<T[]> {
  const items: T[] = [];
  for await (const page of api.list<T>(path)) items.push(...page);
  return items;
}

/** Every table that has an object type code, with its `AutoCreateAccessTeams` flag. */
export async function loadTemplateTables(api: XrmApi): Promise<TemplateTable[]> {
  const rows = await all<RawTable>(api, 'EntityDefinitions?$select=LogicalName,ObjectTypeCode,AutoCreateAccessTeams');
  return rows
    .filter((r) => typeof r.ObjectTypeCode === 'number')
    .map((r) => ({ logicalName: r.LogicalName, objectTypeCode: r.ObjectTypeCode as number, enabled: r.AutoCreateAccessTeams === true }))
    .sort((a, b) => a.logicalName.localeCompare(b.logicalName));
}

export async function loadTemplates(api: XrmApi): Promise<AccessTeamTemplate[]> {
  const rows = await all<RawTemplate>(api, 'teamtemplates?$select=teamtemplateid,teamtemplatename,objecttypecode,defaultaccessrightsmask&$orderby=teamtemplatename');
  return rows.map((r) => ({ id: r.teamtemplateid, name: r.teamtemplatename, objectTypeCode: r.objecttypecode, mask: r.defaultaccessrightsmask }));
}

/** The server judges the table, the mask and the limit of 4 templates per table; its 400 is an XrmError. */
export async function createTemplate(api: XrmApi, name: string, objectTypeCode: number, mask: number): Promise<void> {
  await api.post('teamtemplates', { teamtemplatename: name, objecttypecode: objectTypeCode, defaultaccessrightsmask: mask });
}

export const setTemplateMask = (api: XrmApi, id: string, mask: number): Promise<void> =>
  api.patch(`teamtemplates(${id})`, { defaultaccessrightsmask: mask });

export const deleteTemplate = (api: XrmApi, id: string): Promise<void> => api.delete(`teamtemplates(${id})`);

/** Reads the whole definition and writes it back with `AutoCreateAccessTeams` changed (the metadata API replaces), as auditing does. */
export async function setAutoCreateAccessTeams(api: XrmApi, table: string, enabled: boolean): Promise<void> {
  const path = entityPath(table);
  const body = await api.get<Record<string, unknown>>(path);
  await api.put(path, { ...body, AutoCreateAccessTeams: enabled });
}
