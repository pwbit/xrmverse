import type { XrmApi } from '../../shell/api/client';
import { XrmError } from '../../shell/api/errors';
import { collectByIds } from '../../shell/auth/roles';
import { t } from '../../shell/i18n/t';
import type { ProcessKind } from '../model/types';

/**
 * The studio's calls: the authoring API (`/authoring/workflows`, docs/workflow-authoring.md "Commands, API and tools") for the
 * list, export, apply and state, and the Web API for delete and the managed flag. Every process is addressed by its id.
 */

export type { ProcessKind };
export type ProcessState = 'draft' | 'activated';

/** A row of `GET /authoring/workflows`. A global action's `entity` is `none`. */
export interface WorkflowSummary {
  id: string;
  name: string;
  kind: ProcessKind;
  entity: string;
  mode: 'realtime' | 'background' | null;
  state: ProcessState;
  /** Steps the format cannot represent: the editor shows such a process read-only. */
  unsupportedSteps: number;
}

export interface ApplyError {
  message: string;
  at?: { line: number; column: number };
}

/** The result of an apply (docs/workflow-authoring.md "The apply result"). */
export interface ApplyResult {
  id: string;
  name: string;
  action: 'created' | 'updated' | 'unchanged';
  state: ProcessState;
  stateChanged: boolean;
  assumedShapes: string[];
  errors: ApplyError[];
}

export interface ApplyOptions {
  dryRun: boolean;
  solution?: string;
  activate?: boolean;
}

const ROOT = 'workflows';

export async function listWorkflows(api: XrmApi, filter?: { kind?: ProcessKind; entity?: string }): Promise<WorkflowSummary[]> {
  const query = new URLSearchParams();
  if (filter?.kind) query.set('kind', filter.kind);
  if (filter?.entity) query.set('entity', filter.entity);
  const search = query.toString();
  const response = await api.authoring('GET', search ? `${ROOT}?${search}` : ROOT);
  return (await jsonOf<WorkflowSummary[]>(response)) ?? [];
}

/** The process as YAML (the editor's text) or XAML; a failure is an {@link XrmError} with the server message. */
export async function exportWorkflow(api: XrmApi, id: string, format: 'yaml' | 'xaml'): Promise<string> {
  const response = await api.authoring('GET', `${ROOT}/${encodeURIComponent(id)}?format=${format}`, {
    accept: format === 'yaml' ? 'text/yaml, application/json' : 'text/xml, application/json',
  });
  return response.text();
}

/**
 * Applies `yaml` (a dry run unless `dryRun` is false). A document the server refuses is a 400 whose body is the result with
 * its errors: it resolves, it does not throw. Any other failure (the error envelope) throws the {@link XrmError}.
 */
export async function applyWorkflow(api: XrmApi, yaml: string, opts: ApplyOptions): Promise<ApplyResult> {
  const query = new URLSearchParams({ dryRun: String(opts.dryRun) });
  if (opts.solution) query.set('solution', opts.solution);
  if (opts.activate !== undefined) query.set('activate', String(opts.activate));
  const response = await api.authoring('POST', `${ROOT}/apply?${query.toString()}`, { body: yaml, acceptStatus: [400] });
  const body = await jsonOf<ApplyResult & { error?: { code?: unknown; message?: unknown } }>(response);
  if (response.ok || Array.isArray(body?.errors)) return body as ApplyResult;
  const err = body?.error;
  throw new XrmError(
    response.status,
    typeof err?.message === 'string' && err.message ? err.message : response.statusText || String(response.status),
    typeof err?.code === 'string' ? err.code : undefined,
  );
}

/** Activates or deactivates the process; the server answers with its summary after the change. */
export async function setWorkflowState(api: XrmApi, id: string, activate: boolean): Promise<WorkflowSummary> {
  const response = await api.authoring('POST', `${ROOT}/${encodeURIComponent(id)}/${activate ? 'activate' : 'deactivate'}`);
  return (await jsonOf<WorkflowSummary>(response)) as WorkflowSummary;
}

/** The Web API delete of the `workflow` row. */
export function deleteWorkflow(api: XrmApi, id: string): Promise<void> {
  return api.delete(`workflows(${id})`);
}

/** The ids among `ids` whose process is managed (`workflow.ismanaged`), in lower case as given. */
export async function managedIds(api: XrmApi, ids: string[]): Promise<Set<string>> {
  if (ids.length === 0) return new Set();
  const rows = await collectByIds<{ workflowid: string; ismanaged: boolean }>(api, 'workflows?$select=workflowid,ismanaged', 'workflowid', ids);
  const managed = new Set(rows.filter((r) => r.ismanaged).map((r) => r.workflowid.toLowerCase()));
  return new Set(ids.filter((id) => managed.has(id.toLowerCase())));
}

async function jsonOf<T>(response: Response): Promise<T | undefined> {
  const text = await response.text();
  if (!text) return undefined;
  try {
    return JSON.parse(text) as T;
  } catch {
    throw new XrmError(response.status, t('api.error.invalidBody'));
  }
}
