import { describe, expect, it } from 'vitest';
import type { LookupRelationship, ManyToManyRelationship } from '../../admin/tables/tableApi';
import { parseXml } from '../../parsers/xml';
import { subgridFetchXml } from './subgridQuery';

const PROJECT = 'cccccccc-0000-0000-0000-000000000003';

// OpenProject365's op365_project_timeentry_projectid: op365_TimeEntry.op365_ProjectId references op365_Project.
const projectTimeEntries: LookupRelationship = {
  schemaName: 'op365_project_timeentry_projectid',
  referencingEntity: 'op365_timeentry',
  referencingAttribute: 'op365_projectid',
  referencedEntity: 'op365_project',
};
const VIEW = `<fetch version="1.0" mapping="logical"><entity name="op365_timeentry"><attribute name="op365_hours" /><attribute name="op365_timeentryid" /><order attribute="op365_hours" descending="false" /></entity></fetch>`;

const root = (xml: string) => parseXml(xml).documentElement;
const entityOf = (xml: string) => root(xml).getElementsByTagName('entity')[0]!;
const kids = (el: Element, name: string) => Array.from(el.children).filter((c) => c.tagName === name);

describe('subgridFetchXml', () => {
  it('one_to_many_adds_referencing_condition', () => {
    const entity = entityOf(subgridFetchXml(VIEW, projectTimeEntries, PROJECT));
    const filters = kids(entity, 'filter');
    expect(filters).toHaveLength(1);
    const conditions = kids(filters[0]!, 'condition');
    expect(conditions).toHaveLength(1);
    expect(conditions[0]!.getAttribute('attribute')).toBe('op365_projectid');
    expect(conditions[0]!.getAttribute('operator')).toBe('eq');
    expect(conditions[0]!.getAttribute('value')).toBe(PROJECT);
    // The view's attributes and order stay.
    expect(kids(entity, 'attribute')).toHaveLength(2);
    expect(kids(entity, 'order')).toHaveLength(1);
  });

  const m2m: ManyToManyRelationship = {
    schemaName: 'xv_Project_Contact',
    entity1: 'xv_project',
    entity2: 'contact',
    intersectEntity: 'xv_project_contact',
    entity1IntersectAttribute: 'xv_projectid',
    entity2IntersectAttribute: 'contactid',
  };

  it('many_to_many_adds_intersect_link', () => {
    const view = `<fetch><entity name="contact"><attribute name="fullname" /></entity></fetch>`;
    const entity = entityOf(subgridFetchXml(view, m2m, PROJECT));
    const link = kids(entity, 'link-entity')[0]!;
    expect(link.getAttribute('name')).toBe('xv_project_contact');
    expect(link.getAttribute('from')).toBe('contactid');
    expect(link.getAttribute('to')).toBe('contactid');
    expect(link.getAttribute('intersect')).toBe('true');
    const condition = kids(kids(link, 'filter')[0]!, 'condition')[0]!;
    expect(condition.getAttribute('attribute')).toBe('xv_projectid');
    expect(condition.getAttribute('value')).toBe(PROJECT);
    // The other side of the relationship works the same way.
    const other = entityOf(subgridFetchXml(`<fetch><entity name="xv_project"/></fetch>`, m2m, 'dddddddd-0000-0000-0000-000000000004'));
    expect(other.getElementsByTagName('link-entity')[0]!.getAttribute('from')).toBe('xv_projectid');
    expect(other.getElementsByTagName('condition')[0]!.getAttribute('attribute')).toBe('contactid');
  });

  it('many_to_many_uses_the_custom_intersect_name_and_the_primary_id', () => {
    const rel: ManyToManyRelationship = {
      ...m2m,
      intersectEntity: 'xv_custom_link',
      entity1IntersectAttribute: 'xv_left',
      entity2IntersectAttribute: 'xv_right',
    };
    const entity = entityOf(subgridFetchXml(`<fetch><entity name="contact"/></fetch>`, rel, PROJECT, 'contactkey'));
    const link = kids(entity, 'link-entity')[0]!;
    expect(link.getAttribute('name')).toBe('xv_custom_link');
    expect(link.getAttribute('from')).toBe('xv_right');
    expect(link.getAttribute('to')).toBe('contactkey');
    expect(link.getElementsByTagName('condition')[0]!.getAttribute('attribute')).toBe('xv_left');
  });

  it('many_to_many_self_referential_takes_entity_1_as_the_record_side', () => {
    const rel: ManyToManyRelationship = {
      schemaName: 'xv_self',
      entity1: 'xv_project',
      entity2: 'xv_project',
      intersectEntity: 'xv_self',
      entity1IntersectAttribute: 'xv_projectidone',
      entity2IntersectAttribute: 'xv_projectidtwo',
    };
    const link = entityOf(subgridFetchXml(`<fetch><entity name="xv_project"/></fetch>`, rel, PROJECT)).getElementsByTagName('link-entity')[0]!;
    expect(link.getAttribute('from')).toBe('xv_projectidtwo');
    expect(link.getElementsByTagName('condition')[0]!.getAttribute('attribute')).toBe('xv_projectidone');
  });

  it('many_to_many_without_intersect_metadata_fails_explicitly', () => {
    const { intersectEntity: _gone, ...bare } = m2m;
    expect(() => subgridFetchXml(`<fetch><entity name="contact"/></fetch>`, bare, PROJECT)).toThrow(/intersect/);
  });

  it('view_of_another_table_fails_explicitly', () => {
    expect(() => subgridFetchXml(`<fetch><entity name="account"/></fetch>`, projectTimeEntries, PROJECT)).toThrow(/op365_timeentry/);
  });

  it('existing_filter_is_kept_and_anded', () => {
    const view = `<fetch><entity name="op365_timeentry"><attribute name="op365_hours"/><filter type="or"><condition attribute="statecode" operator="eq" value="0"/><condition attribute="op365_hours" operator="gt" value="1"/></filter></entity></fetch>`;
    const entity = entityOf(subgridFetchXml(view, projectTimeEntries, PROJECT));
    const filters = kids(entity, 'filter');
    expect(filters).toHaveLength(1);
    expect(filters[0]!.getAttribute('type')).toBe('and');
    // The "or" filter survives intact inside the new "and"; the referencing condition is a sibling of it.
    const inner = kids(filters[0]!, 'filter')[0]!;
    expect(inner.getAttribute('type')).toBe('or');
    expect(kids(inner, 'condition')).toHaveLength(2);
    expect(kids(filters[0]!, 'condition')[0]!.getAttribute('attribute')).toBe('op365_projectid');
  });

  it('record_id_is_escaped', () => {
    const evil = `x" /><condition attribute="a" operator="eq" value="<b>&`;
    const xml = subgridFetchXml(VIEW, projectTimeEntries, evil);
    // Still one well-formed condition whose value is the text itself.
    const conditions = entityOf(xml).getElementsByTagName('condition');
    expect(conditions).toHaveLength(1);
    expect(conditions[0]!.getAttribute('value')).toBe(evil);
    expect(xml).not.toContain('<b>');
  });
});
