import type { FieldContext } from '../../admin/tables/fields/fieldProps';
import { recordKey, type AttributeMeta, type EntityMeta } from '../../admin/tables/metadata';
import { PARTY_SELECT, PARTY_TYPE_OF } from '../../admin/tables/partyLists';
import { loadEntity, loadGlobalChoices, loadRelationships, lookupsOf, partyNavigationOf } from '../../admin/tables/tableApi';
import type { XrmApi } from '../../shell/api/client';
import { XrmError } from '../../shell/api/errors';
import { t } from '../../shell/i18n/t';
import type { FormModel } from '../../model/form';
import { canCreate as holdsCreate } from './canCreate';
import { retrievePrincipalAccess, type AccessRight } from './access';

/** Everything a record form needs besides its FormXml. */
export interface RecordContext {
  entity: EntityMeta;
  fieldContext: FieldContext;
  /** The record as the Web API returns it (with annotations); undefined on the create form. */
  record?: Record<string, unknown>;
  /** The signed-in user's rights on the record; empty on the create form. */
  rights: AccessRight[];
  /** The user holds `prvCreate{SchemaName}` on the table (`prvCreateActivity` for an activity table). */
  canCreate: boolean;
}

const PREFER_ANNOTATIONS = { Prefer: 'odata.include-annotations="*"' };

/** The attributes a record read selects: every readable one except the companions of other attributes and party lists. */
const readable = (a: AttributeMeta) => a.validForRead && a.kind !== 'unsupported' && a.kind !== 'partylist' && !a.hidden && !a.attributeOf;

/** The properties of a record that hold the attribute (a file also has its `_name`). */
const properties = (a: AttributeMeta): string[] => (a.kind === 'file' ? [a.logicalName, `${a.logicalName}_name`] : [recordKey(a)]);

/**
 * The attributes a form shows: the field cells of its tabs, header and footer, and the lookups of its quick views. Pass them
 * to {@link loadRecord} so that only these columns are read.
 */
export function formColumns(form: FormModel): string[] {
  const cells = [...form.tabs.flatMap((tab) => tab.columns.flatMap((c) => c.sections.flatMap((s) => s.rows.flat()))), ...form.header, ...form.footer];
  const names = cells.flatMap((c) => (c.control?.kind === 'field' ? [c.control.attribute] : c.control?.kind === 'quickview' ? [c.control.lookupAttribute] : []));
  return [...new Set(names.filter(Boolean))];
}

/** The party list columns among `columns` that the form can show (a participation type is known for them). */
const partyColumns = (entity: EntityMeta, columns?: string[]): AttributeMeta[] =>
  entity.attributes.filter((a) => a.kind === 'partylist' && PARTY_TYPE_OF[a.logicalName] !== undefined && (!columns || columns.includes(a.logicalName)));

/**
 * The record `id` of `entity` with annotations; with `columns`, only those (plus the key, the primary name for the form title, `statecode`, `statuscode`, `ownerid` and `transactioncurrencyid` for the currency symbol). A 404 or 403 (the record does not exist, or the user may not read it) throws an
 * {@link XrmError} with the message "This record does not exist or you do not have access to it."; any other failure is rethrown.
 * With `partyNavigation` (plan 4.1b, task 9) the party rows are expanded when a party list is among the columns.
 */
export async function fetchRecord(api: XrmApi, entity: EntityMeta, id: string, columns?: string[], partyNavigation?: string): Promise<Record<string, unknown>> {
  const wanted = columns && new Set([entity.primaryIdAttribute, entity.primaryNameAttribute ?? '', ...columns, 'statecode', 'statuscode', 'ownerid', 'transactioncurrencyid']);
  const attributes = entity.attributes.filter((a) => readable(a) && (!wanted || wanted.has(a.logicalName)));
  const select = [...new Set([entity.primaryIdAttribute, ...attributes.flatMap(properties)])].join(',');
  const expand = partyNavigation && partyColumns(entity, columns).length > 0 ? `&$expand=${partyNavigation}($select=${PARTY_SELECT})` : '';
  try {
    return await api.get<Record<string, unknown>>(`${entity.entitySetName}(${id})?$select=${select}${expand}`, { headers: PREFER_ANNOTATIONS });
  } catch (e) {
    if (e instanceof XrmError && (e.status === 404 || e.status === 403)) throw new XrmError(e.status, t('record.notFound'));
    throw e;
  }
}

/**
 * The metadata, relationships and global choices of `table`, the record `id` (when given) and what the user may do with it.
 * The rights of an existing record come from `RetrievePrincipalAccess`; `canCreate` from `RetrieveUserPrivileges`
 * (`prvCreate{SchemaName}`, or `prvCreateActivity` for an activity table; false when the server returned no SchemaName). With `options.readOnly` (a read-only card such as a quick
 * view) the rights are not read: `rights` is `['Read']` and `canCreate` false. Throws XrmError.
 */
export async function loadRecord(api: XrmApi, table: string, id?: string, columns?: string[], options: { readOnly?: boolean } = {}): Promise<RecordContext> {
  const entity = await loadEntity(api, table);
  const set = entity.entitySetName;
  if (!set) throw new XrmError(0, t('records.noSet', { table }));
  const needsGlobalChoices = entity.attributes.some((a) => a.globalOptionSet && a.options.length === 0);
  const relationships = loadRelationships(api);
  // A party list is read through the party navigation, whose name the relationships give (never guessed).
  const withParties = partyColumns(entity, columns).length > 0;
  const [[rights, canCreate], rels, globalChoices, record] = await Promise.all([
    options.readOnly
      ? Promise.resolve([['Read'] as AccessRight[], false] as const)
      : api.get<{ UserId: string }>('WhoAmI').then((me) =>
          Promise.all([id ? retrievePrincipalAccess(api, me.UserId, set, id) : Promise.resolve([] as AccessRight[]), holdsCreate(api, me.UserId, entity)]),
        ),
    relationships,
    needsGlobalChoices ? loadGlobalChoices(api) : Promise.resolve({}),
    id
      ? withParties
        ? relationships.then((r) => fetchRecord(api, entity, id, columns, partyNavigationOf(r, table)))
        : fetchRecord(api, entity, id, columns)
      : Promise.resolve(undefined),
  ]);
  const partyNavigation = entity.attributes.some((a) => a.kind === 'partylist') ? partyNavigationOf(rels, table) : undefined;
  return {
    entity,
    fieldContext: {
      table,
      lookups: lookupsOf(rels, table),
      globalChoices,
      record: id && record ? { set, id, data: record } : undefined,
      ...(partyNavigation ? { partyNavigation } : {}),
    },
    record,
    rights,
    canCreate,
  };
}
