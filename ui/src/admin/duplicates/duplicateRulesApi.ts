import type { XrmApi } from '../../shell/api/client';
import { loadEntity } from '../tables/tableApi';

/** The organization's four duplicate detection switches (`organizations` columns). */
export type OrgSwitch =
  | 'isduplicatedetectionenabled'
  | 'isduplicatedetectionenabledforonlinecreateupdate'
  | 'isduplicatedetectionenabledforimport'
  | 'isduplicatedetectionenabledforofflinesync';

export interface OrgSwitches {
  id: string;
  values: Record<OrgSwitch, boolean>;
}

export interface DuplicateRule {
  id: string;
  name: string;
  description: string;
  baseTable: string;
  matchingTable: string;
  /** 0 Unpublished, 1 Publishing, 2 Published. */
  status: number;
  caseSensitive: boolean;
  excludeInactive: boolean;
}

export interface RuleCondition {
  /** Set on a condition the server already has. */
  id?: string;
  baseAttribute: string;
  matchingAttribute: string;
  operator: number;
  /** The N of "same first/last N characters". */
  param?: number;
  ignoreBlank: boolean;
}

/** The operators a rule can use: `operatorcode` values. 5 (pick list label) is refused by the server and not offered. */
export const OPERATORS = [0, 1, 2, 3, 4, 6] as const;
export const takesCount = (operator: number): boolean => operator === 1 || operator === 2;

export interface TableChoice {
  logicalName: string;
  enabled: boolean;
}

export interface JobState {
  state: number;
  status: number;
  message?: string | null;
}

const ORG_SWITCHES: OrgSwitch[] = [
  'isduplicatedetectionenabled',
  'isduplicatedetectionenabledforonlinecreateupdate',
  'isduplicatedetectionenabledforimport',
  'isduplicatedetectionenabledforofflinesync',
];

async function all<T>(api: XrmApi, path: string): Promise<T[]> {
  const items: T[] = [];
  for await (const page of api.list<T>(path)) items.push(...page);
  return items;
}

export async function loadSwitches(api: XrmApi): Promise<OrgSwitches> {
  const rows = await all<Record<string, unknown>>(api, `organizations?$select=organizationid,${ORG_SWITCHES.join(',')}`);
  const row = rows[0];
  if (!row) throw new Error('The organization was not found.');
  const values = Object.fromEntries(ORG_SWITCHES.map((k) => [k, row[k] === true])) as Record<OrgSwitch, boolean>;
  return { id: String(row.organizationid), values };
}

export const setSwitch = (api: XrmApi, organizationId: string, column: OrgSwitch, value: boolean): Promise<void> =>
  api.patch(`organizations(${organizationId})`, { [column]: value });

interface RawRule {
  duplicateruleid: string;
  name: string;
  description?: string | null;
  baseentityname: string;
  matchingentityname: string;
  statuscode?: number | null;
  iscasesensitive?: boolean | null;
  excludeinactiverecords?: boolean | null;
}

export async function loadRules(api: XrmApi): Promise<DuplicateRule[]> {
  const rows = await all<RawRule>(
    api,
    'duplicaterules?$select=duplicateruleid,name,description,baseentityname,matchingentityname,statuscode,iscasesensitive,excludeinactiverecords&$orderby=name',
  );
  return rows.map((r) => ({
    id: r.duplicateruleid,
    name: r.name,
    description: r.description ?? '',
    baseTable: r.baseentityname,
    matchingTable: r.matchingentityname,
    status: r.statuscode ?? 0,
    caseSensitive: r.iscasesensitive === true,
    excludeInactive: r.excludeinactiverecords === true,
  }));
}

/** Every table with its duplicate detection flag. */
export async function loadTables(api: XrmApi): Promise<TableChoice[]> {
  const rows = await all<{ LogicalName: string; IsDuplicateDetectionEnabled?: { Value?: boolean } | null }>(
    api,
    'EntityDefinitions?$select=LogicalName,IsDuplicateDetectionEnabled',
  );
  return rows
    .map((r) => ({ logicalName: r.LogicalName, enabled: r.IsDuplicateDetectionEnabled?.Value === true }))
    .sort((a, b) => a.logicalName.localeCompare(b.logicalName));
}

/** The logical names of a table's columns. */
export async function loadColumns(api: XrmApi, table: string): Promise<string[]> {
  const entity = await loadEntity(api, table);
  return entity.attributes.filter((a) => a.validForRead && !a.attributeOf).map((a) => a.logicalName).sort((a, b) => a.localeCompare(b));
}

interface RawCondition {
  duplicateruleconditionid: string;
  baseattributename: string;
  matchingattributename: string;
  operatorcode: number;
  operatorparam?: number | null;
  ignoreblankvalues?: boolean | null;
}

export async function loadConditions(api: XrmApi, ruleId: string): Promise<RuleCondition[]> {
  const rows = await all<RawCondition>(
    api,
    `duplicateruleconditions?$select=duplicateruleconditionid,baseattributename,matchingattributename,operatorcode,operatorparam,ignoreblankvalues&$filter=_regardingobjectid_value eq ${ruleId}`,
  );
  return rows.map((r) => ({
    id: r.duplicateruleconditionid,
    baseAttribute: r.baseattributename,
    matchingAttribute: r.matchingattributename,
    operator: r.operatorcode,
    param: r.operatorparam ?? undefined,
    ignoreBlank: r.ignoreblankvalues === true,
  }));
}

/** `forUpdate`: a PATCH also clears `operatorparam` when the operator takes no N, so an old N does not linger. */
const conditionBody = (c: RuleCondition, forUpdate = false) => ({
  baseattributename: c.baseAttribute,
  matchingattributename: c.matchingAttribute,
  operatorcode: c.operator,
  ignoreblankvalues: c.ignoreBlank,
  ...(takesCount(c.operator) ? { operatorparam: c.param } : forUpdate ? { operatorparam: null } : {}),
});

export interface RuleInput {
  name: string;
  description: string;
  baseTable: string;
  matchingTable: string;
  caseSensitive: boolean;
  excludeInactive: boolean;
}

/**
 * Creates a rule and its conditions. A condition the server refuses takes the new rule back (a rule without the conditions that
 * were asked for is worse than none); the server's 400 is then thrown as it is.
 */
export async function createRule(api: XrmApi, rule: RuleInput, conditions: RuleCondition[]): Promise<string> {
  const { id } = await api.post('duplicaterules', {
    name: rule.name,
    description: rule.description || null,
    baseentityname: rule.baseTable,
    matchingentityname: rule.matchingTable,
    iscasesensitive: rule.caseSensitive,
    excludeinactiverecords: rule.excludeInactive,
  });
  if (!id) throw new Error('The server did not return the id of the new rule.');
  try {
    for (const c of conditions) await api.post('duplicateruleconditions', { ...conditionBody(c), 'regardingobjectid@odata.bind': `duplicaterules(${id})` });
  } catch (e) {
    await api.delete(`duplicaterules(${id})`).catch(() => undefined);
    throw e;
  }
  return id;
}

/** Updates the rule's own columns, then its conditions: those the server has are patched or deleted in order, new ones are created. */
export async function updateRule(api: XrmApi, id: string, rule: RuleInput, conditions: RuleCondition[], before: RuleCondition[]): Promise<void> {
  await api.patch(`duplicaterules(${id})`, {
    name: rule.name,
    description: rule.description || null,
    iscasesensitive: rule.caseSensitive,
    excludeinactiverecords: rule.excludeInactive,
  });
  const kept = new Set(conditions.map((c) => c.id).filter(Boolean));
  for (const old of before) if (old.id && !kept.has(old.id)) await api.delete(`duplicateruleconditions(${old.id})`);
  for (const c of conditions) {
    const original = before.find((o) => o.id === c.id);
    if (!c.id) await api.post('duplicateruleconditions', { ...conditionBody(c), 'regardingobjectid@odata.bind': `duplicaterules(${id})` });
    else if (JSON.stringify(original) !== JSON.stringify(c)) await api.patch(`duplicateruleconditions(${c.id})`, conditionBody(c, true));
  }
}

export const deleteRule = (api: XrmApi, id: string): Promise<void> => api.delete(`duplicaterules(${id})`);

/** Starts the publish job: the rule turns Publishing and the server returns the system job. */
export async function publishRule(api: XrmApi, id: string): Promise<string> {
  const { body } = await api.post<{ asyncoperationid?: string }>(`duplicaterules(${id})/Microsoft.Dynamics.CRM.PublishDuplicateRule`, undefined);
  if (!body?.asyncoperationid) throw new Error('The server did not return the publish job.');
  return body.asyncoperationid;
}

export const unpublishRule = async (api: XrmApi, id: string): Promise<void> => {
  await api.post('UnpublishDuplicateRule', { DuplicateRuleId: id });
};

export async function loadJob(api: XrmApi, jobId: string): Promise<JobState> {
  const row = await api.get<{ statecode: number; statuscode: number; message?: string | null }>(
    `asyncoperations(${jobId})?$select=statecode,statuscode,message`,
  );
  return { state: row.statecode, status: row.statuscode, message: row.message };
}

/** A job that ended (state 3) with status 30 succeeded. */
export const jobEnded = (job: JobState): boolean => job.state === 3;
export const jobSucceeded = (job: JobState): boolean => job.state === 3 && job.status === 30;
