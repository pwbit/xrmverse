import { describe, expect, it } from 'vitest';
import { parseXml } from '../../parsers/xml';
import { noValueOperator, operatorsFor, readFilter, writeFilter, type FilterCondition, type FilterGroup } from './filterModel';

const serialize = (n: Node) => new XMLSerializer().serializeToString(n);

/** Parses `<fetch><entity name="account">{inner}</entity></fetch>` and returns its first `<filter>`. */
function filterOf(inner: string) {
  const doc = parseXml(`<fetch><entity name="account">${inner}</entity></fetch>`);
  const entity = doc.getElementsByTagName('entity')[0];
  return { doc, entity, filter: doc.getElementsByTagName('filter')[0] };
}

describe('operatorsFor', () => {
  it('lists the text operators', () => {
    expect(operatorsFor('text')).toEqual(['eq', 'ne', 'like', 'not-like', 'begins-with', 'ends-with', 'null', 'not-null']);
  });

  it('lists the number operators', () => {
    expect(operatorsFor('number')).toEqual(['eq', 'ne', 'gt', 'ge', 'lt', 'le', 'null', 'not-null']);
  });

  it('lists the date operators', () => {
    expect(operatorsFor('date')).toEqual([
      'on', 'on-or-after', 'on-or-before', 'today', 'yesterday', 'tomorrow', 'this-week', 'this-month', 'this-year',
      'last-x-days', 'next-x-days', 'null', 'not-null',
    ]);
  });

  it('lists the choice operators', () => {
    expect(operatorsFor('choice')).toEqual(['eq', 'ne', 'in', 'not-in', 'null', 'not-null']);
  });

  it('lists the lookup operators, with user and business unit ones only for their targets', () => {
    expect(operatorsFor('lookup', 'contact', 'primarycontactid')).toEqual(['eq', 'ne', 'null', 'not-null']);
    expect(operatorsFor('lookup', 'systemuser', 'ownerid')).toEqual(['eq', 'ne', 'null', 'not-null', 'eq-userid', 'ne-userid']);
    expect(operatorsFor('lookup', 'businessunit', 'x_bu')).toEqual(['eq', 'ne', 'null', 'not-null', 'eq-businessid', 'ne-businessid']);
    expect(operatorsFor('lookup', undefined, 'owningbusinessunit')).toEqual([
      'eq', 'ne', 'null', 'not-null', 'eq-businessid', 'ne-businessid',
    ]);
    expect(operatorsFor('lookup', 'contact')).not.toContain('eq-userid');
    expect(operatorsFor('lookup', 'systemuser')).not.toContain('eq-businessid');
  });

  it('lists only null checks for other columns', () => {
    expect(operatorsFor('other')).toEqual(['null', 'not-null']);
  });
});

describe('noValueOperator', () => {
  it('knows the operators that take no value', () => {
    for (const op of ['null', 'not-null', 'today', 'yesterday', 'tomorrow', 'this-week', 'this-month', 'this-year',
      'eq-userid', 'ne-userid', 'eq-businessid', 'ne-businessid']) expect(noValueOperator(op)).toBe(true);
    for (const op of ['eq', 'like', 'in', 'on', 'last-x-days', 'next-x-days']) expect(noValueOperator(op)).toBe(false);
  });
});

describe('readFilter', () => {
  it('reads editable conditions', () => {
    const { filter, entity } = filterOf('<filter type="or"><condition attribute="name" operator="eq" value="A"/>'
      + '<condition attribute="telephone1" operator="null"/></filter>');
    const node = readFilter(filter, entity) as FilterGroup;
    expect(node.kind).toBe('group');
    expect(node.type).toBe('or');
    expect(node.children.map((c) => (c.kind === 'condition' ? [c.attribute, c.operator, c.values] : c.kind))).toEqual([
      ['name', 'eq', ['A']],
      ['telephone1', 'null', []],
    ]);
  });

  it.each([
    ['a condition with entityname', '<condition entityname="c" attribute="x" operator="eq" value="1"/>'],
    ['an unknown operator', '<condition attribute="x" operator="under" value="1"/>'],
    ['valueof', '<condition attribute="x" operator="eq" valueof="y"/>'],
    ['an unknown child element', '<foo/>'],
  ])('marks opaque a filter holding %s', (_, inner) => {
    const xml = `<filter type="and"><condition attribute="name" operator="eq" value="A"/>${inner}</filter>`;
    const { filter, entity } = filterOf(xml);
    const node = readFilter(filter, entity);
    expect(node.kind).toBe('opaque');
    expect(node.kind === 'opaque' && node.xml).toBe(serialize(filter));
  });

  it('marks opaque a filter inside a link-entity', () => {
    const { doc, entity } = filterOf('<link-entity name="contact" from="contactid" to="primarycontactid" alias="c">'
      + '<filter type="and"><condition attribute="x" operator="eq" value="1"/></filter></link-entity>');
    expect(readFilter(doc.getElementsByTagName('filter')[0], entity).kind).toBe('opaque');
  });

  it('keeps editable a nested group of editable conditions, and an opaque group does not spread to its parent', () => {
    const { filter, entity } = filterOf('<filter type="and"><condition attribute="a" operator="eq" value="1"/>'
      + '<filter type="or"><condition attribute="b" operator="eq" value="2"/><condition attribute="c" operator="ne" value="3"/></filter>'
      + '<filter type="or"><condition attribute="d" operator="eq" valueof="e"/></filter></filter>');
    const node = readFilter(filter, entity) as FilterGroup;
    expect(node.kind).toBe('group');
    expect(node.children.map((c) => c.kind)).toEqual(['condition', 'group', 'opaque']);
    const inner = node.children[1] as FilterGroup;
    expect(inner.type).toBe('or');
    expect(inner.children).toHaveLength(2);
    expect(new Set([node.key, ...node.children.map((c) => c.key), ...inner.children.map((c) => c.key)]).size).toBe(6);
  });

  it('strips the % of like (contains) and keeps raw begins-with / ends-with values', () => {
    const { filter, entity } = filterOf('<filter><condition attribute="a" operator="like" value="%acme%"/>'
      + '<condition attribute="b" operator="begins-with" value="ac"/><condition attribute="c" operator="ends-with" value="me"/></filter>');
    const node = readFilter(filter, entity) as FilterGroup;
    expect(node.type).toBe('and');
    expect(node.children.map((c) => c.kind === 'condition' && c.values)).toEqual([['acme'], ['ac'], ['me']]);
  });

  it('reads in values from <value> children', () => {
    const { filter, entity } = filterOf('<filter type="and"><condition attribute="a" operator="in">'
      + '<value>1</value><value>2</value></condition></filter>');
    const node = readFilter(filter, entity) as FilterGroup;
    expect(node.children[0]).toMatchObject({ kind: 'condition', operator: 'in', values: ['1', '2'] });
  });
});

describe('writeFilter', () => {
  it('writes like with %…%, begins-with raw, in as <value> children, no-value operators without value', () => {
    const doc = parseXml('<fetch/>');
    const group: FilterGroup = {
      kind: 'group', type: 'or', key: 'g', children: [
        { kind: 'condition', attribute: 'a', operator: 'like', values: ['acme'], key: 'c1' },
        { kind: 'condition', attribute: 'b', operator: 'begins-with', values: ['ac'], key: 'c2' },
        { kind: 'condition', attribute: 'c', operator: 'in', values: ['1', '2'], key: 'c3' },
        { kind: 'condition', attribute: 'd', operator: 'null', values: [], key: 'c4' },
        { kind: 'group', type: 'and', key: 'g2', children: [
          { kind: 'condition', attribute: 'e', operator: 'eq', values: ['5'], key: 'c5' },
        ] },
      ],
    };
    expect(serialize(writeFilter(group, doc))).toBe('<filter type="or">'
      + '<condition attribute="a" operator="like" value="%acme%"/>'
      + '<condition attribute="b" operator="begins-with" value="ac"/>'
      + '<condition attribute="c" operator="in"><value>1</value><value>2</value></condition>'
      + '<condition attribute="d" operator="null"/>'
      + '<filter type="and"><condition attribute="e" operator="eq" value="5"/></filter></filter>');
  });

  it('starts from the source element: unknown attributes stay, lookup display hints go when the value changes', () => {
    const { doc, filter, entity } = filterOf('<filter type="and" hint="h"><condition attribute="ownerid" operator="eq"'
      + ' value="{1}" uiname="A" uitype="systemuser" foo="bar"/></filter>');
    const group = readFilter(filter, entity) as FilterGroup;
    const sources = new Map<string, Element>([['f', filter], ['f.0', filter.children[0]]]);
    expect(serialize(writeFilter(group, doc, sources))).toBe(serialize(filter));
    const condition = group.children[0] as FilterCondition;
    const changed = { ...group, children: [{ ...condition, values: ['{2}'] }] };
    expect(serialize(writeFilter(changed, doc, sources))).toBe('<filter type="and" hint="h">'
      + '<condition attribute="ownerid" operator="eq" value="{2}" foo="bar"/></filter>');
    const operator = { ...group, children: [{ ...condition, operator: 'ne' }] };
    expect(serialize(writeFilter(operator, doc, sources))).toContain('operator="ne" value="{1}" uiname="A" uitype="systemuser"');
  });

  it('writes an opaque node byte for byte', () => {
    const doc = parseXml('<fetch/>');
    const xml = '<filter type="and"><condition entityname="c" attribute="x" operator="eq" value="1" foo="bar"/></filter>';
    const group: FilterGroup = { kind: 'group', type: 'and', key: 'g', children: [{ kind: 'opaque', xml, key: 'o' }] };
    expect(serialize(writeFilter(group, doc))).toBe(`<filter type="and">${xml}</filter>`);
  });
});
