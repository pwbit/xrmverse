import type { XrmApi } from '../shell/api/client';
import { XrmError } from '../shell/api/errors';
import { batchBody, parseBatch, parseJson, type Call } from './batch';
import { literal } from '../shell/api/odata';
import { entityPath } from '../admin/tables/tableApi';
import { stubMembers } from './notImplemented';
import { plainId } from './values';

/**
 * `Xrm.WebApi` (Learn, Xrm.WebApi and Xrm.WebApi.online). Every request goes through the shell's {@link XrmApi}, so it carries
 * the user's bearer header and the server applies the user's security. Failures reject with Learn's error object.
 */

/** Learn: the error a failed call rejects with. `errorCode` is the number of the hex `code`. */
export interface WebApiError {
  errorCode: number;
  message: string;
  code: string;
}

/** The `getMetadata()` of a request passed to `execute` (Learn, request.getMetadata method). */
interface ParameterType {
  typeName?: string;
  structuralProperty?: number;
  enumProperties?: { name: string; value: number }[];
}

interface OperationMetadata {
  boundParameter?: string | null;
  parameterTypes?: Record<string, ParameterType | undefined>;
  operationType?: number;
  operationName?: string;
}

const OperationType = { Action: 0, Function: 1, CRUD: 2 } as const;
const StructuralProperty = { EnumerationType: 3 } as const;

/** Learn's default page size of `retrieveMultipleRecords` for a standard table. */
const DEFAULT_MAX_PAGE_SIZE = 5000;
/** Xrm.WebApi returns the formatted values and lookup annotations with the record (ruling R-T5-3). */
const ANNOTATIONS = 'odata.include-annotations="*"';

export type WebApi = Record<string, unknown> & { online: Record<string, unknown> };

/** Learn's error object from any failure: a Web API error keeps its code; anything else has code 0. */
export function toWebApiError(e: unknown): WebApiError {
  if (e instanceof XrmError) {
    const code = e.code ?? '';
    const n = /^0x[0-9a-f]+$/i.test(code) ? Number.parseInt(code.slice(2), 16) : 0;
    return { errorCode: n, message: e.serverMessage, code };
  }
  if (e && typeof e === 'object' && 'errorCode' in e && 'message' in e) return e as WebApiError;
  return { errorCode: 0, message: e instanceof Error ? e.message : String(e), code: '' };
}

/** Runs `run`, rejecting with Learn's error object. A "not available" error stays an Error: it names the member. */
async function guarded<T>(run: () => Promise<T>): Promise<T> {
  try {
    return await run();
  } catch (e) {
    if (e instanceof Error && !(e instanceof XrmError) && / is not available in this version /.test(e.message)) throw e;
    throw toWebApiError(e);
  }
}

const NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;

function checkName(what: string, value: unknown): string {
  if (typeof value !== 'string' || !NAME.test(value)) throw new Error(`Xrm.WebApi: ${String(value)} is not a valid ${what}.`);
  return value;
}

function checkId(value: unknown): string {
  const id = typeof value === 'string' ? plainId(value) : '';
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(id)) throw new Error(`Xrm.WebApi: ${String(value)} is not a record id.`);
  return id;
}

/** `options` as a query string: it starts with `?`; a FetchXML query may come encoded or not (Learn: not encoded). */
function queryOf(options: unknown): string {
  if (options === undefined || options === null || options === '') return '';
  if (typeof options !== 'string') throw new Error('Xrm.WebApi: options must be a query string starting with "?".');
  const query = options.startsWith('?') ? options.slice(1) : options;
  const fetch = /^fetchXml=([\s\S]*)$/i.exec(query);
  if (fetch) {
    // Unencoded FetchXML may hold "&" and "%" of its own: everything after the name is the query.
    const raw = fetch[1]!;
    const xml = raw.includes('<') ? raw : decodeURIComponent(raw);
    return `?fetchXml=${encodeURIComponent(xml)}`;
  }
  return `?${query}`;
}

interface TableInfo {
  set: string;
  primaryId: string;
}

/** The entity set name and primary id of a table from its metadata, read once per shell. */
function tableCache(api: XrmApi) {
  const cache = new Map<string, Promise<TableInfo>>();
  return (logicalName: string): Promise<TableInfo> => {
    const key = logicalName.toLowerCase();
    let found = cache.get(key);
    if (!found) {
      found = api
        .get<{ EntitySetName?: string; PrimaryIdAttribute?: string }>(`${entityPath(key)}?$select=EntitySetName,PrimaryIdAttribute`)
        .then((m) => {
          if (!m?.EntitySetName) throw new Error(`Xrm.WebApi: the table ${logicalName} has no entity set.`);
          return { set: m.EntitySetName, primaryId: m.PrimaryIdAttribute ?? `${key}id` };
        });
      // A failed read is not kept: the next call reads it again.
      found.catch(() => cache.delete(key));
      cache.set(key, found);
    }
    return found;
  };
}

/** The `Response`-like object `execute` resolves with (Learn, execute return value). */
async function responseOf(response: Response) {
  const text = await response.text();
  const headers: Record<string, string> = {};
  response.headers.forEach((value, name) => (headers[name] = value));
  return {
    ok: response.ok,
    status: response.status,
    statusText: response.statusText,
    headers,
    url: response.url,
    type: '',
    body: text,
    json: async (): Promise<unknown> => JSON.parse(text),
    text: async (): Promise<string> => text,
  };
}

/**
 * A function parameter value as an OData literal of the URL's parameter alias. An enumeration is written by its member name
 * (Learn's `enumProperties` give the names of the values); an `Edm.Guid` is written bare, as OData writes it.
 */
function functionLiteral(value: unknown, type: ParameterType | undefined): string {
  if (value === null || value === undefined) return 'null';
  if (type?.structuralProperty === StructuralProperty.EnumerationType && type.typeName) {
    const typeName = type.typeName.replace(/^mscrm\./, 'Microsoft.Dynamics.CRM.');
    const members = Array.isArray(type.enumProperties) ? type.enumProperties : [];
    const member = members.find((m) => m?.value === value || m?.name === value);
    if (members.length && !member) throw new Error(`Xrm.WebApi.online.execute: ${String(value)} is not a member of ${typeName}.`);
    return `${typeName}'${member ? member.name : String(value)}'`;
  }
  if (type?.typeName === 'Edm.Guid' && typeof value === 'string') return plainId(value);
  if (typeof value === 'string') return literal(value);
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  if (value instanceof Date) return value.toISOString();
  return JSON.stringify(value);
}

export function createWebApi(api: XrmApi): WebApi {
  const table = tableCache(api);
  const read = { Prefer: ANNOTATIONS };

  const createRecord = (entityLogicalName: unknown, data: unknown) =>
    guarded(async () => {
      const etn = checkName('table name', entityLogicalName);
      const { set } = await table(etn);
      const { id } = await api.post(set, data ?? {});
      // The new id comes from OData-EntityId; a response without it is a failure, not a record without an id.
      if (!id) throw new Error(`Xrm.WebApi.createRecord: the server did not return the id of the new ${etn} record.`);
      return { entityType: etn, id };
    });

  const retrieveRecord = (entityLogicalName: unknown, id: unknown, options?: unknown) =>
    guarded(async () => {
      const etn = checkName('table name', entityLogicalName);
      const key = checkId(id);
      const { set } = await table(etn);
      return api.get<Record<string, unknown>>(`${set}(${key})${queryOf(options)}`, { headers: read });
    });

  const retrieveMultipleRecords = (entityLogicalName: unknown, options?: unknown, maxPageSize?: unknown) =>
    guarded(async () => {
      const etn = checkName('table name', entityLogicalName);
      if (maxPageSize !== undefined && maxPageSize !== null && !(typeof maxPageSize === 'number' && Number.isInteger(maxPageSize) && maxPageSize > 0)) {
        throw new Error('Xrm.WebApi.retrieveMultipleRecords: maxPageSize must be a positive whole number.');
      }
      const { set } = await table(etn);
      const size = (maxPageSize as number | undefined) ?? DEFAULT_MAX_PAGE_SIZE;
      const body = await api.get<Record<string, unknown>>(`${set}${queryOf(options)}`, { headers: { Prefer: `odata.maxpagesize=${size}, ${ANNOTATIONS}` } });
      const result: Record<string, unknown> = { entities: (body?.value as unknown[]) ?? [] };
      if (typeof body?.['@odata.nextLink'] === 'string') result.nextLink = body['@odata.nextLink'];
      const cookie = body?.['@Microsoft.Dynamics.CRM.fetchxmlpagingcookie'];
      if (typeof cookie === 'string') result.fetchXmlPagingCookie = cookie;
      return result;
    });

  const updateRecord = (entityLogicalName: unknown, id: unknown, data: unknown) =>
    guarded(async () => {
      const etn = checkName('table name', entityLogicalName);
      const key = checkId(id);
      const { set } = await table(etn);
      await api.patch(`${set}(${key})`, data ?? {});
      return { entityType: etn, id: key };
    });

  const deleteRecord = (entityLogicalName: unknown, id: unknown) =>
    guarded(async () => {
      const etn = checkName('table name', entityLogicalName);
      const key = checkId(id);
      const { set } = await table(etn);
      await api.delete(`${set}(${key})`);
      return { entityType: etn, id: key };
    });

  /** An entity reference (`{ entityType, id }`) as the address of its record. */
  const recordPath = async (ref: unknown): Promise<string> => {
    const r = ref as { entityType?: unknown; id?: unknown } | null;
    const { set } = await table(checkName('table name', r?.entityType));
    return `${set}(${checkId(r?.id)})`;
  };

  /** Learn's CRUD requests of `execute` (operationType 2), by the property names its samples give them. */
  const crud = async (request: Record<string, unknown>, name: string): Promise<Call[]> => {
    switch (name) {
      case 'Create': {
        const { set } = await table(checkName('table name', request.etn));
        return [{ method: 'POST', path: set, body: request.payload ?? {} }];
      }
      case 'Retrieve': {
        const columns = Array.isArray(request.columns) ? request.columns.map((c) => checkName('column name', c)) : [];
        const select = columns.length ? `?$select=${columns.join(',')}` : '';
        return [{ method: 'GET', path: `${await recordPath(request.entityReference)}${select}`, headers: read }];
      }
      case 'Update':
        return [{ method: 'PATCH', path: await recordPath({ entityType: request.etn, id: request.id }), body: request.payload ?? {} }];
      case 'Delete':
        return [{ method: 'DELETE', path: await recordPath(request.entityReference) }];
      case 'Associate': {
        const target = await recordPath(request.target);
        const nav = checkName('relationship', request.relationship);
        const related = Array.isArray(request.relatedEntities) ? request.relatedEntities : [];
        if (!related.length) throw new Error('Xrm.WebApi.online.execute: Associate needs relatedEntities.');
        const calls: Call[] = [];
        for (const r of related) {
          calls.push({ method: 'POST', path: `${target}/${nav}/$ref`, body: { '@odata.id': `${location.origin}/api/data/v9.2/${await recordPath(r)}` } });
        }
        return calls;
      }
      case 'Disassociate': {
        const target = await recordPath(request.target);
        const nav = checkName('relationship', request.relationship);
        return [{ method: 'DELETE', path: `${target}/${nav}(${checkId(request.relatedEntityId)})/$ref` }];
      }
      default:
        throw new Error(`Xrm.WebApi.online.execute: ${name} is not a CRUD operation (Create, Retrieve, Update, Delete, Associate, Disassociate).`);
    }
  };

  /** The Web API calls of one request of `execute`: one, or one per related record of an Associate. */
  const callsOf = async (request: unknown): Promise<Call[]> => {
    const r = request as Record<string, unknown> | null;
    if (!r || typeof r.getMetadata !== 'function') throw new Error('Xrm.WebApi.online.execute: the request has no getMetadata method.');
    const meta = (r.getMetadata as () => OperationMetadata)() ?? {};
    const name = checkName('operation name', meta.operationName);
    if (meta.operationType === OperationType.CRUD) return crud(r, name);
    if (meta.operationType !== OperationType.Action && meta.operationType !== OperationType.Function) {
      throw new Error(`Xrm.WebApi.online.execute: ${String(meta.operationType)} is not an operation type (0 action, 1 function, 2 CRUD).`);
    }
    const bound = typeof meta.boundParameter === 'string' && meta.boundParameter ? meta.boundParameter : undefined;
    const types = meta.parameterTypes ?? {};
    const names = Object.keys(types).filter((p) => p !== bound && r[p] !== undefined);
    let prefix = '';
    if (bound) {
      const target = r[bound] as { entityType?: unknown; id?: unknown; entitySetName?: unknown } | undefined;
      // Bound to a record, or to the table's collection when the reference carries no id.
      prefix = target?.id !== undefined ? `${await recordPath(target)}/` : `${(await table(checkName('table name', target?.entityType))).set}/`;
      prefix += 'Microsoft.Dynamics.CRM.';
    }
    if (meta.operationType === OperationType.Action) {
      const body = Object.fromEntries(names.map((p) => [p, r[p]]));
      return [{ method: 'POST', path: `${prefix}${name}`, body }];
    }
    const aliases = names.map((p, i) => `${p}=@p${i + 1}`).join(',');
    const query = names.map((p, i) => `@p${i + 1}=${encodeURIComponent(functionLiteral(r[p], types[p]))}`).join('&');
    return [{ method: 'GET', path: `${prefix}${name}(${aliases})${query ? `?${query}` : ''}`, headers: read }];
  };

  /** Runs the calls of one request one after the other; the last response is the request's. */
  const executeOne = async (request: unknown): Promise<Response> => {
    let last: Response | undefined;
    for (const c of await callsOf(request)) last = await api.request(c.method, c.path, c.body, c.headers);
    return last!;
  };

  const execute = (request: unknown) => guarded(async () => responseOf(await executeOne(request)));

  // Ruling C5: one request after the other; the first failure stops the rest and rejects. A change set (an array in the list,
  // #130) needs a transaction: then the whole list goes as one $batch, each change set atomic, and the responses come back in order.
  const executeMultiple = (requests: unknown) =>
    guarded(async () => {
      if (!Array.isArray(requests)) throw new Error('Xrm.WebApi.online.executeMultiple: requests must be an array.');
      if (!requests.some(Array.isArray)) {
        const responses = [];
        for (const request of requests) responses.push(await responseOf(await executeOne(request)));
        return responses;
      }
      const items: (Call | Call[])[] = [];
      for (const request of requests) {
        if (Array.isArray(request)) items.push((await Promise.all(request.map(callsOf))).flat());
        else items.push(...(await callsOf(request)));
      }
      const { body, boundary } = batchBody(items);
      const response = await api.batch(body, boundary);
      const parts = parseBatch(await response.text(), response.headers.get('Content-Type') ?? '');
      const failed = parts.find((p) => p.status >= 400);
      if (!response.ok || failed) {
        const error = failed ? parseJson(failed.body)?.error : undefined;
        throw new XrmError(failed?.status ?? response.status, typeof error?.message === 'string' ? error.message : response.statusText, typeof error?.code === 'string' ? error.code : undefined);
      }
      return Promise.all(parts.map((p) => responseOf(new Response(p.status === 204 || p.body === '' ? null : p.body, { status: p.status, statusText: p.statusText, headers: p.headers }))));
    });

  const crudMembers = { createRecord, retrieveRecord, retrieveMultipleRecords, updateRecord, deleteRecord };
  const online = { ...crudMembers, execute, executeMultiple };
  const webApi: WebApi = { ...crudMembers, isAvailableOffline: () => false, execute, executeMultiple, online };
  stubMembers(webApi, 'Xrm.WebApi');
  return webApi;
}
