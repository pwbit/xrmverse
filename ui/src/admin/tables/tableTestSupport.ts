import { http, HttpResponse } from 'msw';
import { server } from '../../test/server';
import { API, onPath } from '../security/testSupport';

export const lbl = (text: string) => ({
  LocalizedLabels: [{ Label: text, LanguageCode: 1033 }],
  UserLocalizedLabel: { Label: text, LanguageCode: 1033 },
});

/** A raw Web API attribute (`odata` is the metadata class without `AttributeMetadata`, e.g. `Picklist`). */
export function rawAttr(logicalName: string, odata: string, over: Record<string, unknown> = {}) {
  return {
    '@odata.type': `#Microsoft.Dynamics.CRM.${odata}AttributeMetadata`,
    LogicalName: logicalName,
    DisplayName: lbl(over.display ? String(over.display) : logicalName),
    RequiredLevel: { Value: 'None' },
    IsValidForCreate: true,
    IsValidForUpdate: true,
    IsValidForRead: true,
    IsPrimaryId: false,
    IsPrimaryName: false,
    ...over,
  };
}

export function rawEntity(logicalName: string, entitySetName: string | null, attributes: unknown[], over: Record<string, unknown> = {}) {
  return {
    '@odata.type': '#Microsoft.Dynamics.CRM.EntityMetadata',
    LogicalName: logicalName,
    EntitySetName: entitySetName,
    DisplayName: lbl(over.display ? String(over.display) : logicalName),
    PrimaryIdAttribute: `${logicalName}id`,
    PrimaryNameAttribute: `${logicalName.split('_')[1] ?? 'name'}name`,
    OwnershipType: 'UserOwned',
    IsCustomEntity: true,
    Attributes: attributes,
    ...over,
  };
}

export const rawOneToMany = (schemaName: string, referencing: string, attribute: string, referenced: string, nav?: string) => ({
  '@odata.type': '#Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata',
  SchemaName: schemaName,
  ReferencingEntity: referencing,
  ReferencingAttribute: attribute,
  ReferencedEntity: referenced,
  ReferencingEntityNavigationPropertyName: nav,
});

export const DEFAULT_PUBLISHER_ID = '00000000-0000-0000-0000-00000000d001';

/** Answers the read of the Default solution's publisher prefix (the default SchemaName of a new table or column). */
export function installDefaultPrefix(prefix = 'xv') {
  server.use(
    http.get(`${API}/solutions`, () => HttpResponse.json({ value: [{ _publisherid_value: DEFAULT_PUBLISHER_ID }] })),
    onPath('get', `publishers(${DEFAULT_PUBLISHER_ID})`, () => HttpResponse.json({ customizationprefix: prefix })),
  );
}

/** Answers the metadata reads of the generic UI: one `EntityDefinitions(LogicalName='x')` per entity, the list of them, and all relationships. */
export function installMetadata(entities: ReturnType<typeof rawEntity>[], relationships: unknown[] = []) {
  server.use(
    ...entities.map((e) => onPath('get', `EntityDefinitions(LogicalName='${e.LogicalName}')`, () => HttpResponse.json(e))),
    http.get(`${API}/EntityDefinitions`, () => HttpResponse.json({ value: entities.map(({ Attributes: _attributes, ...rest }) => rest) })),
    http.get(`${API}/RelationshipDefinitions`, () => HttpResponse.json({ value: relationships })),
  );
  installDefaultPrefix();
}
