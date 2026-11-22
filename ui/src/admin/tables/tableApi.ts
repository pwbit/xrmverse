import { t } from '../../shell/i18n/t';
import type { XrmApi } from '../../shell/api/client';
import { literal } from '../../shell/api/odata';
import { parseEntity, parseOptions, type EntityMeta, type OptionMeta, type RawEntity, type RawOptionSet } from './metadata';

/** `EntityDefinitions(LogicalName='x')`, quoted for a URL. */
export const entityPath = (table: string): string => `EntityDefinitions(LogicalName=${encodeURIComponent(literal(table))})`;

const ENTITY_SELECT = 'LogicalName,SchemaName,EntitySetName,DisplayName,PrimaryIdAttribute,PrimaryNameAttribute,OwnershipType,IsCustomEntity,IsAuditEnabled,AutoCreateAccessTeams,ChangeTrackingEnabled,CanChangeTrackingBeEnabled,IsActivity,HasNotes,HasActivities,IsDuplicateDetectionEnabled';

async function all<T>(api: XrmApi, path: string): Promise<T[]> {
  const items: T[] = [];
  for await (const page of api.list<T>(path)) items.push(...page);
  return items;
}

/** A table with all its attributes. */
export async function loadEntity(api: XrmApi, table: string): Promise<EntityMeta> {
  return parseEntity(await api.get<RawEntity>(`${entityPath(table)}?$select=${ENTITY_SELECT}&$expand=Attributes`));
}

/** Every table, without attributes. */
export async function loadEntities(api: XrmApi): Promise<EntityMeta[]> {
  return (await all<RawEntity>(api, `EntityDefinitions?$select=${ENTITY_SELECT}`)).map(parseEntity);
}

/** A one-to-many relationship: `referencingAttribute` of `referencingEntity` is the lookup to `referencedEntity`. */
export interface LookupRelationship {
  schemaName: string;
  referencingEntity: string;
  referencingAttribute: string;
  referencedEntity: string;
  /** `ReferencingEntityNavigationPropertyName`: the only name a lookup is bound with. */
  nav?: string;
  /** `ReferencedEntityNavigationPropertyName`: the collection-valued navigation property on the referenced table. */
  referencedNav?: string;
}

export interface ManyToManyRelationship {
  schemaName: string;
  entity1: string;
  entity2: string;
  /** The intersect table, and its columns holding the ids of entity 1 and of entity 2 (from the metadata). */
  intersectEntity?: string;
  entity1IntersectAttribute?: string;
  entity2IntersectAttribute?: string;
  /** `Entity1NavigationPropertyName` / `Entity2NavigationPropertyName`: the collection of the other side, on entity 1 / 2. */
  entity1Nav?: string;
  entity2Nav?: string;
}

export interface Relationships {
  oneToMany: LookupRelationship[];
  manyToMany: ManyToManyRelationship[];
}

interface RawRelationship {
  SchemaName: string;
  ReferencingEntity?: string;
  ReferencingAttribute?: string;
  ReferencedEntity?: string;
  ReferencingEntityNavigationPropertyName?: string | null;
  ReferencedEntityNavigationPropertyName?: string | null;
  Entity1LogicalName?: string;
  Entity2LogicalName?: string;
  IntersectEntityName?: string | null;
  Entity1IntersectAttribute?: string | null;
  Entity2IntersectAttribute?: string | null;
  Entity1NavigationPropertyName?: string | null;
  Entity2NavigationPropertyName?: string | null;
}

/** All relationships of the organization (the server filters nothing on this endpoint; callers pick their table's). */
export async function loadRelationships(api: XrmApi): Promise<Relationships> {
  const select =
    'SchemaName,ReferencingEntity,ReferencingAttribute,ReferencedEntity,ReferencingEntityNavigationPropertyName,ReferencedEntityNavigationPropertyName,Entity1LogicalName,Entity2LogicalName,IntersectEntityName,Entity1IntersectAttribute,Entity2IntersectAttribute,Entity1NavigationPropertyName,Entity2NavigationPropertyName';
  const result: Relationships = { oneToMany: [], manyToMany: [] };
  for (const r of await all<RawRelationship>(api, `RelationshipDefinitions?$select=${select}`)) {
    if (r.ReferencingEntity && r.ReferencingAttribute && r.ReferencedEntity) {
      result.oneToMany.push({
        schemaName: r.SchemaName,
        referencingEntity: r.ReferencingEntity,
        referencingAttribute: r.ReferencingAttribute,
        referencedEntity: r.ReferencedEntity,
        nav: r.ReferencingEntityNavigationPropertyName || undefined,
        referencedNav: r.ReferencedEntityNavigationPropertyName || undefined,
      });
    } else if (r.Entity1LogicalName && r.Entity2LogicalName) {
      result.manyToMany.push({
        schemaName: r.SchemaName,
        entity1: r.Entity1LogicalName,
        entity2: r.Entity2LogicalName,
        intersectEntity: r.IntersectEntityName || undefined,
        entity1IntersectAttribute: r.Entity1IntersectAttribute || undefined,
        entity2IntersectAttribute: r.Entity2IntersectAttribute || undefined,
        entity1Nav: r.Entity1NavigationPropertyName || undefined,
        entity2Nav: r.Entity2NavigationPropertyName || undefined,
      });
    }
  }
  return result;
}

/** The lookups of `table`: its many-to-one relationships. */
export const lookupsOf = (rels: Relationships, table: string): LookupRelationship[] =>
  rels.oneToMany.filter((r) => r.referencingEntity === table);

/** The navigation property that binds the lookup `attribute` of `table` to a record of `target`; undefined when metadata has none. */
export const navigationFor = (rels: Relationships, table: string, attribute: string, target: string): string | undefined =>
  rels.oneToMany.find((r) => r.referencingEntity === table && r.referencingAttribute === attribute && r.referencedEntity === target)?.nav;

/**
 * The collection-valued navigation property of the party rows of the activity `table` (plan 2.11): the referenced side of
 * the relationship of `activityparty.activityid` to it, as the server names it (`{activity}_activity_parties`). Undefined
 * when the metadata has none.
 */
export const partyNavigationOf = (rels: Relationships, table: string): string | undefined =>
  rels.oneToMany.find((r) => r.referencingEntity === 'activityparty' && r.referencingAttribute === 'activityid' && r.referencedEntity === table)
    ?.referencedNav;

/** Global choices by name, with their options. */
export async function loadGlobalChoices(api: XrmApi): Promise<Record<string, OptionMeta[]>> {
  const raw = await all<NonNullable<RawOptionSet> & { Name: string }>(api, 'GlobalOptionSetDefinitions?$select=Name,Options');
  return Object.fromEntries(raw.map((o) => [o.Name, parseOptions(o)]));
}

const escapeXml = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/** Where an option lives: a global choice by name, or the local option set of a column. */
export type OptionTarget = { optionSetName: string } | { table: string; column: string };

/**
 * Sets (`#RRGGBB`) or clears (null) the colour of the option `value` with `UpdateOptionValue`. Not published: call
 * {@link publishOptions} once after the last change.
 */
export async function updateOptionColor(api: XrmApi, target: OptionTarget, value: number, color: string | null): Promise<void> {
  const where = 'optionSetName' in target ? { OptionSetName: target.optionSetName } : { EntityLogicalName: target.table, AttributeLogicalName: target.column };
  await api.action('UpdateOptionValue', { ...where, Value: value, Color: color ?? '', MergeLabels: true });
}

/** Publishes the options of `target`: the option set (a global choice) or the table (a local one). */
export async function publishOptions(api: XrmApi, target: OptionTarget): Promise<void> {
  const xml =
    'optionSetName' in target
      ? `<optionsets><optionset>${escapeXml(target.optionSetName)}</optionset></optionsets>`
      : `<entities><entity>${escapeXml(target.table)}</entity></entities>`;
  await api.action('PublishXml', { ParameterXml: `<importexportxml>${xml}</importexportxml>` });
}

/**
 * The customization prefix of the Default solution's publisher: the prefix Dataverse gives new tables and columns made
 * outside a solution (the seed's default publisher has `new`).
 */
export async function loadDefaultPrefix(api: XrmApi): Promise<string> {
  const solutions = await api.get<{ value: { _publisherid_value?: string | null }[] }>(
    `solutions?$select=_publisherid_value&$filter=${encodeURIComponent(`uniquename eq ${literal('Default')}`)}`,
  );
  const publisherId = solutions.value[0]?._publisherid_value;
  if (!publisherId) throw new Error(t('schema.noDefaultPublisher'));
  const publisher = await api.get<{ customizationprefix?: string | null }>(`publishers(${publisherId})?$select=customizationprefix`);
  return publisher.customizationprefix ?? '';
}

/**
 * Switches a one-way table setting on (`ChangeTrackingEnabled`, `HasNotes`, `HasActivities`): the whole definition is read
 * and written back (the metadata API replaces); the computed `CanChangeTrackingBeEnabled` is not sent. The server refuses
 * to switch any of them off.
 */
export async function enableTableSetting(api: XrmApi, table: string, setting: 'ChangeTrackingEnabled' | 'HasNotes' | 'HasActivities'): Promise<void> {
  const path = entityPath(table);
  const { CanChangeTrackingBeEnabled: _computed, '@odata.context': _context, ...body } = await api.get<Record<string, unknown>>(path);
  await api.put(path, { ...body, [setting]: true });
}

/** Switches change tracking on (see {@link enableTableSetting}). */
export const enableChangeTracking = (api: XrmApi, table: string): Promise<void> => enableTableSetting(api, table, 'ChangeTrackingEnabled');

/** Switches duplicate detection on or off for a table: the whole definition is read and written back (the metadata API replaces), with the managed flag's `Value` changed. */
export async function setDuplicateDetection(api: XrmApi, table: string, enabled: boolean): Promise<void> {
  const path = entityPath(table);
  const { CanChangeTrackingBeEnabled: _computed, ...body } = await api.get<Record<string, unknown>>(path);
  const flag = (body.IsDuplicateDetectionEnabled ?? {}) as Record<string, unknown>;
  await api.put(path, { ...body, IsDuplicateDetectionEnabled: { ...flag, Value: enabled } });
}
