import type { LookupRelationship, ManyToManyRelationship, Relationships } from '../../admin/tables/tableApi';
import { childElements, parseXml } from '../../parsers/xml';

/** A relationship a subgrid names: one-to-many (the grid lists the referencing rows) or many-to-many. */
export type RelationshipMeta = LookupRelationship | ManyToManyRelationship;

const isOneToMany = (r: RelationshipMeta): r is LookupRelationship => 'referencingAttribute' in r;

/** The relationship named `schemaName` (case-insensitive, as Dataverse compares schema names), among all of the organization's. */
export function findRelationship(rels: Relationships, schemaName: string): RelationshipMeta | undefined {
  const name = schemaName.toLowerCase();
  return rels.oneToMany.find((r) => r.schemaName.toLowerCase() === name) ?? rels.manyToMany.find((r) => r.schemaName.toLowerCase() === name);
}

const element = (doc: Document, name: string, attributes: Record<string, string>): Element => {
  const el = doc.createElement(name);
  for (const [key, value] of Object.entries(attributes)) el.setAttribute(key, value);
  return el;
};

function parseView(viewFetchXml: string): { doc: Document; entity: Element } {
  const doc = parseXml(viewFetchXml);
  const entity = childElements(doc.documentElement, 'entity')[0];
  if (doc.documentElement.tagName !== 'fetch' || !entity) throw new Error('Not a FetchXML document.');
  return { doc, entity };
}

/** ANDs `condition` with the entity's own filters, which stay whole inside a new `and` filter (so an `or` view filter stays correct). */
function andCondition(doc: Document, entity: Element, condition: Element): void {
  const filter = element(doc, 'filter', { type: 'and' });
  for (const existing of childElements(entity, 'filter')) filter.appendChild(existing);
  filter.appendChild(condition);
  entity.insertBefore(filter, childElements(entity, 'link-entity')[0] ?? null);
}

/**
 * The view's FetchXML limited to the rows whose lookup column `targetColumn` equals `id` (the view's own filter is kept whole
 * inside a new `and` filter). The id goes in as an attribute value, so the serializer escapes it.
 */
export function subgridLookupFetchXml(viewFetchXml: string, targetColumn: string, id: string): string {
  const { doc, entity } = parseView(viewFetchXml);
  andCondition(doc, entity, element(doc, 'condition', { attribute: targetColumn, operator: 'eq', value: id }));
  return new XMLSerializer().serializeToString(doc);
}

/**
 * The view's FetchXML limited to the rows related to the record `recordId` of the other side of `relationship`.
 * - One-to-many: the view lists the referencing table; its filter is ANDed with `{ReferencingAttribute} eq {recordId}`
 *   (the view's own filter is kept whole inside a new `and` filter, so an `or` view filter stays correct).
 * - Many-to-many: an inner `link-entity` through the intersect table the metadata names: `from` is the intersect column of the
 *   view table's side, `to` the view table's primary id (`viewPrimaryId`, else `{table}id`), and the filter is on the intersect
 *   column of the record's side. A self-referential relationship takes entity 1 as the record's side and entity 2 as the view's.
 *   Metadata without the intersect names fails explicitly.
 * The record id goes in as an attribute value, so the serializer escapes it. A view of the wrong table throws.
 */
export function subgridFetchXml(viewFetchXml: string, relationship: RelationshipMeta, recordId: string, viewPrimaryId?: string): string {
  const { doc, entity } = parseView(viewFetchXml);
  const viewTable = entity.getAttribute('name') ?? '';
  const condition = (attribute: string) => element(doc, 'condition', { attribute, operator: 'eq', value: recordId });

  if (isOneToMany(relationship)) {
    if (viewTable !== relationship.referencingEntity) {
      throw new Error(`The view lists ${viewTable}, but the relationship ${relationship.schemaName} relates ${relationship.referencingEntity}.`);
    }
    andCondition(doc, entity, condition(relationship.referencingAttribute));
  } else {
    const { entity1, entity2, intersectEntity, entity1IntersectAttribute: first, entity2IntersectAttribute: second } = relationship;
    if (!intersectEntity || !first || !second) {
      throw new Error(`The metadata of the many-to-many relationship ${relationship.schemaName} does not name its intersect table and columns.`);
    }
    if (viewTable !== entity1 && viewTable !== entity2) {
      throw new Error(`The view lists ${viewTable}, but the relationship ${relationship.schemaName} relates ${entity1} and ${entity2}.`);
    }
    // Self-referential: entity 1 is the side the record is on and entity 2 the side the view lists.
    const viewIsSecond = entity1 === entity2 || viewTable === entity2;
    const [recordColumn, viewColumn] = viewIsSecond ? [first, second] : [second, first];
    const link = element(doc, 'link-entity', {
      name: intersectEntity,
      from: viewColumn,
      to: viewPrimaryId ?? `${viewTable}id`,
      'link-type': 'inner',
      intersect: 'true',
      visible: 'false',
    });
    const filter = element(doc, 'filter', { type: 'and' });
    filter.appendChild(condition(recordColumn));
    link.appendChild(filter);
    entity.appendChild(link);
  }
  return new XMLSerializer().serializeToString(doc);
}
