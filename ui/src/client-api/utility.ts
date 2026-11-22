import { entityPath } from '../admin/tables/tableApi';
import type { XrmApi } from '../shell/api/client';
import type { DialogHost, LookupOptions, LookupView } from './DialogHost';
import { entityMetadataOf } from './entityMetadata';
import { notAvailable, Stage, stubMembers } from './notImplemented';
import { plainId } from './values';
import { toWebApiError } from './webApi';

export interface UtilityEnv {
  api: XrmApi;
  dialogs: DialogHost;
  globalContext: Record<string, unknown>;
  /** The user's language, for the labels of `getEntityMetadata`. */
  lcid: number;
}

const LOGICAL_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/**
 * A system view of `lookupObjects` (`viewIds`, `defaultViewId`; Learn: only system views): its FetchXML's own `<filter>`
 * elements narrow the search. A view that is not a system view of one of the tables, or that filters through related tables
 * (which the lookup search cannot apply), is refused: the user is never offered more than the view allows.
 */
async function lookupView(api: XrmApi, id: string, entityTypes: string[]): Promise<LookupView> {
  if (!GUID.test(id)) throw new Error(`Xrm.Utility.lookupObjects: ${id} is not a view id.`);
  let row: { savedqueryid: string; name?: string | null; returnedtypecode?: string | null; fetchxml?: string | null };
  try {
    row = await api.get(`savedqueries(${id})?$select=savedqueryid,name,returnedtypecode,fetchxml`);
  } catch (e) {
    throw toWebApiError(e);
  }
  const entity = String(row.returnedtypecode ?? '').toLowerCase();
  if (!entityTypes.includes(entity)) throw new Error(`Xrm.Utility.lookupObjects: the view ${id} is a view of ${entity}, which is not in entityTypes.`);
  const doc = new DOMParser().parseFromString(row.fetchxml ?? '', 'application/xml');
  const root = doc.querySelector('fetch > entity');
  if (!root || doc.querySelector('parsererror')) throw new Error(`Xrm.Utility.lookupObjects: the FetchXML of the view ${id} could not be read.`);
  const children = Array.from(root.children);
  if (children.some((c) => c.localName === 'link-entity' && c.querySelector('filter, condition'))) {
    throw notAvailable('Xrm.Utility.lookupObjects with a view that filters on related tables', Stage.later);
  }
  const serializer = new XMLSerializer();
  const filters = children.filter((c) => c.localName === 'filter').map((f) => serializer.serializeToString(f));
  return { id, name: row.name ?? id, entity, filters };
}

/** `Xrm.Utility` (Learn). */
export function createUtility(env: UtilityEnv): Record<string, unknown> {
  const utility = {
    getGlobalContext: () => env.globalContext,
    showProgressIndicator: (message: unknown) => env.dialogs.showProgress(typeof message === 'string' ? message : String(message ?? '')),
    closeProgressIndicator: () => env.dialogs.closeProgress(),
    lookupObjects: async (lookupOptions: unknown) => {
      const o = (lookupOptions ?? {}) as Partial<LookupOptions>;
      const types = Array.isArray(o.entityTypes) ? o.entityTypes.filter((x): x is string => typeof x === 'string' && LOGICAL_NAME.test(x)) : [];
      if (types.length === 0) throw new Error('Xrm.Utility.lookupObjects: entityTypes must name at least one table.');
      const filters = Array.isArray(o.filters)
        ? o.filters.filter((f) => f && typeof f.filterXml === 'string' && typeof f.entityLogicalName === 'string')
        : undefined;
      const entityTypes = types.map((x) => x.toLowerCase());
      const raw = lookupOptions as { viewIds?: unknown; defaultViewId?: unknown; searchText?: unknown };
      const viewIds = [raw.defaultViewId, ...(Array.isArray(raw.viewIds) ? raw.viewIds : [])].filter((v) => v !== undefined && v !== null && v !== '');
      const views = await Promise.all([...new Set(viewIds.map((v) => plainId(String(v))))].map((id) => lookupView(env.api, id, entityTypes)));
      const defaultViewId = raw.defaultViewId ? plainId(String(raw.defaultViewId)) : undefined;
      const defaultView = views.find((v) => v.id === defaultViewId);
      const defaultEntityType = typeof o.defaultEntityType === 'string' ? o.defaultEntityType.toLowerCase() : defaultView?.entity;
      return env.dialogs.lookup({
        entityTypes,
        defaultEntityType,
        allowMultiSelect: o.allowMultiSelect === true,
        filters,
        views,
        defaultViewId,
        searchText: typeof raw.searchText === 'string' ? raw.searchText : undefined,
      });
    },
    /** The table's definitions in Learn's shape, with the definitions of `attributes` only (none when none are asked for). */
    getEntityMetadata: async (entityName: unknown, attributes?: unknown) => {
      if (typeof entityName !== 'string' || !LOGICAL_NAME.test(entityName)) throw new Error(`Xrm.Utility.getEntityMetadata: ${String(entityName)} is not a table name.`);
      const wanted = new Set(Array.isArray(attributes) ? attributes.map((a) => String(a).toLowerCase()) : []);
      let meta: Record<string, unknown>;
      try {
        meta = await env.api.get<Record<string, unknown>>(`${entityPath(entityName.toLowerCase())}?$expand=Attributes`);
      } catch (e) {
        throw toWebApiError(e);
      }
      return entityMetadataOf(meta, wanted, env.lcid);
    },
  };
  stubMembers(utility, 'Xrm.Utility');
  return utility;
}
