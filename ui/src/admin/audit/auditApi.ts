import type { XrmApi } from '../../shell/api/client';
import { literal } from '../../shell/api/odata';
import { entityPath } from '../tables/tableApi';

const FV = 'OData.Community.Display.V1.FormattedValue';
const FORMATTED = { Prefer: `odata.include-annotations="${FV}"` };
const SELECT = 'auditid,createdon,action,operation,objecttypecode,_objectid_value,_userid_value';
export const HISTORY_PAGE_SIZE = 20;

export interface AuditFilter {
  table?: string;
  operation?: number;
  userId?: string;
  /** The `nextLink` of the previous page; absent for the first page. */
  page?: string;
}

export interface AuditRow {
  auditid: string;
  createdon: string;
  table: string;
  objectId: string | null;
  action: string;
  operation: string;
  user: string | null;
}

type Raw = Record<string, unknown>;
const text = (row: Raw, key: string): string | null => {
  const v = row[key];
  return typeof v === 'string' ? v : v == null ? null : String(v);
};
const formatted = (row: Raw, key: string): string | null => text(row, `${key}@${FV}`) ?? text(row, key);

function toRow(row: Raw): AuditRow {
  return {
    auditid: String(row.auditid),
    createdon: text(row, 'createdon') ?? '',
    table: text(row, 'objecttypecode') ?? '',
    objectId: text(row, '_objectid_value'),
    action: formatted(row, 'action') ?? '',
    operation: formatted(row, 'operation') ?? '',
    user: formatted(row, '_userid_value'),
  };
}

/** One page of `GET audits`, newest first, with formatted values. `changedata` is never asked for. */
export async function listAudits(api: XrmApi, f: AuditFilter): Promise<{ rows: AuditRow[]; nextLink?: string }> {
  const clauses = [
    f.table ? `objecttypecode eq ${literal(f.table)}` : '',
    f.operation !== undefined ? `operation eq ${f.operation}` : '',
    f.userId ? `_userid_value eq ${f.userId}` : '',
  ].filter(Boolean);
  const filter = clauses.length ? `&$filter=${encodeURIComponent(clauses.join(' and '))}` : '';
  const path = f.page ?? `audits?$select=${SELECT}&$orderby=${encodeURIComponent('createdon desc,auditid asc')}${filter}`;
  const page = await api.page<Raw>(path, 50, { headers: FORMATTED });
  return { rows: page.value.map(toRow), nextLink: page.nextLink };
}

/** An `AuditDetail` as the Web API writes it (`@odata.type` and the properties of its kind). */
export type AuditDetail = Record<string, unknown> & { '@odata.type'?: string };

/** `RetrieveAuditDetails` of one audit row. */
export async function auditDetails(api: XrmApi, auditId: string): Promise<AuditDetail> {
  const response = await api.get<{ AuditDetail?: AuditDetail }>(`audits(${auditId})/Microsoft.Dynamics.CRM.RetrieveAuditDetails`, {
    headers: FORMATTED,
  });
  return response.AuditDetail ?? {};
}

export interface HistoryEntry {
  auditid: string;
  detail: AuditDetail;
  date: string;
  user?: string;
  action?: string;
}

/**
 * One page (20 entries, newest first) of the change history of a record, as the CLI assembles it: the `audits` rows of the
 * record, then `RetrieveAuditDetails` of each row, so every detail is tied to its own `auditid`. `page` is the `nextLink` of the
 * previous page; absent for the first. Errors are the caller's (a 403 means the audit privileges are missing).
 */
export async function recordHistory(api: XrmApi, id: string, page?: string): Promise<{ entries: HistoryEntry[]; nextLink?: string }> {
  const path =
    page ??
    `audits?$select=${SELECT}&$orderby=${encodeURIComponent('createdon desc,auditid desc')}&$filter=${encodeURIComponent(`_objectid_value eq ${id}`)}`;
  const result = await api.page<Raw>(path, HISTORY_PAGE_SIZE, { headers: FORMATTED });
  const entries = await Promise.all(
    result.value.map(async (raw): Promise<HistoryEntry> => {
      const row = toRow(raw);
      return { auditid: row.auditid, detail: await auditDetails(api, row.auditid), date: row.createdon, user: row.user ?? undefined, action: row.action };
    }),
  );
  return { entries, nextLink: result.nextLink };
}

export interface Change {
  column: string;
  oldValue: string;
  newValue: string;
}

const recordRef = (ref: unknown): string => {
  if (!ref || typeof ref !== 'object') return '';
  const r = ref as Raw;
  const type = String(r['@odata.type'] ?? '').split('.').pop() ?? '';
  const key = Object.entries(r).find(([k]) => !k.startsWith('@'))?.[1];
  return `${type}:${String(key ?? '')}`;
};

const valueText = (record: Raw | undefined, key: string): string => {
  if (!record) return '';
  const f = record[`${key}@${FV}`];
  if (typeof f === 'string') return f;
  const v = record[key];
  return v == null ? '' : typeof v === 'string' ? v : JSON.stringify(v);
};

/** The changes one audit detail holds: a row per changed column of an attribute detail, one row for the other kinds. */
export function changesOf(detail: AuditDetail): Change[] {
  const type = detail['@odata.type'] ?? '';
  const count = (key: string) => (Array.isArray(detail[key]) ? (detail[key] as unknown[]).length : 0);
  if (type.endsWith('RelationshipAuditDetail')) {
    const targets = Array.isArray(detail.TargetRecords) ? detail.TargetRecords.map(recordRef).join(', ') : '';
    return [{ column: String(detail.RelationshipName ?? ''), oldValue: '', newValue: targets }];
  }
  if (type.endsWith('ShareAuditDetail')) {
    return [{ column: `share ${recordRef(detail.Principal)}`, oldValue: String(detail.OldPrivileges ?? ''), newValue: String(detail.NewPrivileges ?? '') }];
  }
  if (type.endsWith('RolePrivilegeAuditDetail')) {
    return [{ column: 'privileges', oldValue: `${count('OldRolePrivileges')} privileges`, newValue: `${count('NewRolePrivileges')} privileges` }];
  }
  const old = detail.OldValue as Raw | undefined;
  const now = detail.NewValue as Raw | undefined;
  const keys: string[] = [];
  for (const record of [old, now])
    for (const key of Object.keys(record ?? {})) if (!key.includes('@') && !keys.includes(key)) keys.push(key);
  return keys.map((key) => ({
    column: key.startsWith('_') && key.endsWith('_value') ? key.slice(1, -6) : key,
    oldValue: valueText(old, key),
    newValue: valueText(now, key),
  }));
}

// ---- switches ---------------------------------------------------------------------------------------------------

export interface Organization {
  id: string;
  enabled: boolean;
  /** Days, or -1 for forever. */
  retention: number;
}

export async function loadOrganization(api: XrmApi): Promise<Organization> {
  const page = await api.page<Raw>('organizations?$select=organizationid,isauditenabled,auditretentionperiodv2', 1);
  const row = page.value[0];
  if (!row) throw new Error('The organization was not found.');
  return { id: String(row.organizationid), enabled: row.isauditenabled === true, retention: Number(row.auditretentionperiodv2 ?? 30) };
}

async function patchOrganization(api: XrmApi, body: Record<string, unknown>): Promise<void> {
  const org = await loadOrganization(api);
  await api.patch(`organizations(${org.id})`, body);
}

export const setOrgAuditing = (api: XrmApi, enabled: boolean) => patchOrganization(api, { isauditenabled: enabled });

/** `days` 1..365000, or -1 for forever; applies to rows written from now on. */
export const setOrgRetention = (api: XrmApi, days: number) => patchOrganization(api, { auditretentionperiodv2: days });

/** Reads the whole definition and writes it back with `IsAuditEnabled` changed, as the CLI does (the metadata API replaces). */
async function putAuditing(api: XrmApi, path: string, enabled: boolean): Promise<void> {
  const body = await api.get<Record<string, unknown> & { IsAuditEnabled?: { Value?: boolean } }>(path);
  const setting = body.IsAuditEnabled ?? {};
  await api.put(path, { ...body, IsAuditEnabled: { ...setting, Value: enabled } });
}

export const setTableAuditing = (api: XrmApi, table: string, enabled: boolean) => putAuditing(api, entityPath(table), enabled);

export const setColumnAuditing = (api: XrmApi, table: string, column: string, enabled: boolean) =>
  putAuditing(api, `${entityPath(table)}/Attributes(LogicalName=${encodeURIComponent(literal(column))})`, enabled);
