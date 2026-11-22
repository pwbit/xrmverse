import type { XrmApi } from '../../shell/api/client';
import { literal } from '../../shell/api/odata';
import { entityPath } from '../tables/tableApi';
import { labelText, type RawEntity } from '../tables/metadata';

/** The seeded System Administrator profile: it allows everything and cannot be changed. */
export const SYSTEM_ADMINISTRATOR_PROFILE = '572329c1-a042-4e22-be47-367c6374ea45';

/** The values of `canread`, `cancreate` and `canupdate`. */
export const ALLOWED = 4;
export const NOT_ALLOWED = 0;

export type Right = 'canread' | 'cancreate' | 'canupdate';
export const RIGHTS: Right[] = ['canread', 'cancreate', 'canupdate'];

export interface Profile {
  fieldsecurityprofileid: string;
  name?: string | null;
  description?: string | null;
}

export interface Permission {
  fieldpermissionid: string;
  entityname: string;
  attributelogicalname: string;
  canread?: number | null;
  cancreate?: number | null;
  canupdate?: number | null;
}

export interface SecuredColumn {
  table: string;
  tableLabel: string;
  column: string;
  columnLabel: string;
}

export const isAdministratorProfile = (id: string): boolean => id.toLowerCase() === SYSTEM_ADMINISTRATOR_PROFILE;

export const PROFILES_PATH = 'fieldsecurityprofiles?$select=fieldsecurityprofileid,name,description&$orderby=name';

/** Every secured column of the organization, by table then column. */
export async function loadSecuredColumns(api: XrmApi): Promise<SecuredColumn[]> {
  const columns: SecuredColumn[] = [];
  const path = 'EntityDefinitions?$select=LogicalName,DisplayName&$expand=Attributes($select=LogicalName,DisplayName,IsSecured)';
  for await (const page of api.list<RawEntity & { Attributes?: { LogicalName: string; IsSecured?: boolean; DisplayName?: RawEntity['DisplayName'] }[] }>(path))
    for (const entity of page)
      for (const a of entity.Attributes ?? [])
        if (a.IsSecured)
          columns.push({
            table: entity.LogicalName,
            tableLabel: labelText(entity.DisplayName, entity.LogicalName),
            column: a.LogicalName,
            columnLabel: labelText(a.DisplayName, a.LogicalName),
          });
  return columns.sort((x, y) => x.table.localeCompare(y.table) || x.column.localeCompare(y.column));
}

/** The permissions of one profile. */
export async function loadPermissions(api: XrmApi, profileId: string): Promise<Permission[]> {
  const items: Permission[] = [];
  const filter = encodeURIComponent(`_fieldsecurityprofileid_value eq ${profileId}`);
  const path = `fieldpermissions?$select=fieldpermissionid,entityname,attributelogicalname,canread,cancreate,canupdate&$filter=${filter}`;
  for await (const page of api.list<Permission>(path)) items.push(...page);
  return items;
}

/**
 * Sets one right of the profile on a column: `PATCH fieldpermissions(<id>)` when the profile has a permission on it, else a
 * new permission with only that right allowed.
 */
export async function setRight(
  api: XrmApi,
  profileId: string,
  column: Pick<SecuredColumn, 'table' | 'column'>,
  existing: Permission | undefined,
  right: Right,
  allowed: boolean,
): Promise<void> {
  const value = allowed ? ALLOWED : NOT_ALLOWED;
  if (existing) {
    await api.patch(`fieldpermissions(${existing.fieldpermissionid})`, { [right]: value });
    return;
  }
  await api.post('fieldpermissions', {
    'fieldsecurityprofileid@odata.bind': `/fieldsecurityprofiles(${profileId})`,
    entityname: column.table,
    attributelogicalname: column.column,
    canread: NOT_ALLOWED,
    cancreate: NOT_ALLOWED,
    canupdate: NOT_ALLOWED,
    [right]: value,
  });
}

/** Reads the column's definition and writes it back with `IsSecured` changed (the metadata API replaces what it is given). */
export async function setColumnSecured(api: XrmApi, table: string, column: string, secured: boolean): Promise<void> {
  const path = `${entityPath(table)}/Attributes(LogicalName=${encodeURIComponent(literal(column))})`;
  const body = await api.get<Record<string, unknown>>(path);
  const { CanBeSecuredForCreate: _c, CanBeSecuredForRead: _r, CanBeSecuredForUpdate: _u, '@odata.context': _ctx, ...rest } = body;
  await api.put(path, { ...rest, IsSecured: secured });
}
