import { describe, expect, it } from 'vitest';
import { applyQuickFind, linkEntities, parseFetch, withAttributes, withDateRange, withNullCondition, withOrder, withPaging, withTotalCount } from './fetchXml';

const fetchOf = (savedquery: string) => /<fetch[ >][\s\S]*?<\/fetch>/.exec(savedquery)![0];

// Invented views in the shape of a quick find view and of a public view of accounts (the tests on the views of the
// OpenProject365 solution are in fetchXml.op365.test.ts).
const quickFind = `<savedquery><fetchxml><fetch version="1.0" mapping="logical">
  <entity name="account">
    <attribute name="name" /><attribute name="accountnumber" /><attribute name="primarycontactid" /><attribute name="accountid" />
    <order attribute="name" descending="false" />
    <filter type="and"><condition attribute="statecode" operator="eq" value="0" /></filter>
    <filter type="or" isquickfindfields="1">
      <condition attribute="name" operator="like" value="{0}" />
      <condition attribute="accountnumber" operator="like" value="{0}" />
      <condition attribute="primarycontactid" operator="like" value="{0}" />
    </filter>
  </entity>
</fetch></fetchxml></savedquery>`;
const customers = `<savedquery><fetchxml><fetch version="1.0" mapping="logical">
  <entity name="account">
    <attribute name="name" /><attribute name="xv_customertype" /><attribute name="accountid" />
    <order attribute="name" descending="false" />
    <filter type="and"><condition attribute="statecode" operator="eq" value="0" /></filter>
    <filter type="and"><condition attribute="xv_iscustomer" operator="eq" value="1" /></filter>
  </entity>
</fetch></fetchxml></savedquery>`;
const QF = fetchOf(quickFind);
const parse = (xml: string) => new DOMParser().parseFromString(xml, 'application/xml');
const quickValues = (xml: string) =>
  [...parse(xml).querySelectorAll('filter[isquickfindfields="1"] > condition')].map((c) => c.getAttribute('value'));

describe('parseFetch', () => {
  it('reads entity, order and count', () => {
    expect(parseFetch(fetchOf(customers))).toEqual({
      entity: 'account',
      order: [{ attribute: 'name', descending: false }],
      count: undefined,
      linkAliases: [],
    });
  });

  it('reads link aliases, alias orders and count', () => {
    const r = parseFetch(
      '<fetch count="25"><entity name="a"><order attribute="n" descending="true"/><order alias="agg"/>' +
        '<link-entity name="b" from="x" to="y" alias="bb"><link-entity name="c" from="x" to="y" alias="cc"/></link-entity></entity></fetch>',
    );
    expect(r.count).toBe(25);
    expect(r.linkAliases).toEqual(['bb', 'cc']);
    expect(r.order).toEqual([
      { attribute: 'n', descending: true },
      { attribute: '', descending: false, alias: 'agg' },
    ]);
  });
});

describe('parseFetch link orders (#100)', () => {
  it('parseFetch_reads_link_entity_orders after the root orders, in declaration order', () => {
    const r = parseFetch(
      '<fetch><entity name="a"><link-entity name="b" from="x" to="y" alias="bb"><order attribute="z" descending="true"/></link-entity>' +
        '<order attribute="n"/></entity></fetch>',
    );
    expect(r.order).toEqual([
      { attribute: 'n', descending: false },
      { attribute: 'z', descending: true, linkAlias: 'bb' },
    ]);
  });

  it('an order in a link-entity without alias is not read', () => {
    const r = parseFetch('<fetch><entity name="a"><link-entity name="b" from="x" to="y"><order attribute="z"/></link-entity></entity></fetch>');
    expect(r.order).toEqual([]);
  });
});

describe('applyQuickFind', () => {
  it('fills only isquickfindfields conditions', () => {
    const out = applyQuickFind(QF, 'acme');
    expect(quickValues(out)).toEqual(['acme%', 'acme%', 'acme%']);
    expect(out).toContain('attribute="statecode"');
    expect(out).toContain('value="0"');
    expect(out).not.toContain('{0}');
  });

  it('appends % unless the text ends with *', () => {
    expect(quickValues(applyQuickFind(QF, 'ab'))[0]).toBe('ab%');
    expect(quickValues(applyQuickFind(QF, 'ab*'))[0]).toBe('ab%');
  });

  it('turns a leading * into %', () => {
    expect(quickValues(applyQuickFind(QF, '*ab'))[0]).toBe('%ab%');
    expect(quickValues(applyQuickFind(QF, '*ab*'))[0]).toBe('%ab%');
  });

  it("escapes ' < & in the value", () => {
    const out = applyQuickFind(QF, `O'Neil <&> "Co"`);
    expect(parse(out).querySelector('parsererror')).toBeNull();
    expect(quickValues(out)[0]).toBe(`O'Neil <&> "Co"%`);
    expect(out).not.toContain('<&>');
  });

  it('leaves a fetch without quick find fields unchanged in meaning', () => {
    const out = applyQuickFind(fetchOf(customers), 'x');
    expect(parse(out).querySelectorAll('condition').length).toBe(2);
  });

  it('drops the quick find filter for blank text', () => {
    const out = applyQuickFind(QF, '   ');
    expect(out).not.toContain('{0}');
    expect(parse(out).querySelector('filter[isquickfindfields]')).toBeNull();
  });
});

describe('withPaging', () => {
  it('sets page, count and the cookie attribute, XML-escaped', () => {
    const cookie = `<cookie page="1"><name last="A&B" first="'x'"/></cookie>`;
    const out = withPaging(fetchOf(customers), 2, 50, cookie);
    const f = parse(out).documentElement;
    expect(parse(out).querySelector('parsererror')).toBeNull();
    expect(f.getAttribute('page')).toBe('2');
    expect(f.getAttribute('count')).toBe('50');
    expect(f.getAttribute('paging-cookie')).toBe(cookie);
    expect(out).toContain('paging-cookie="&lt;cookie');
  });

  it('omits paging-cookie when none is given and drops a stale one', () => {
    const out = withPaging('<fetch paging-cookie="old"><entity name="a"/></fetch>', 1, 25);
    const f = parse(out).documentElement;
    expect(f.hasAttribute('paging-cookie')).toBe(false);
    expect(f.getAttribute('page')).toBe('1');
  });
});

describe('applyQuickFind placeholders', () => {
  const qf = (conds: string) =>
    `<fetch><entity name="a"><filter type="or" isquickfindfields="1">${conds}</filter></entity></fetch>`;
  const NUM = qf(
    '<condition attribute="n" operator="like" value="{0}"/><condition attribute="m" operator="eq" value="{1}"/>' +
      '<condition attribute="b" operator="begins-with" value="{2}"/>',
  );

  it('passes % and _ through as typed', () => {
    expect(quickValues(applyQuickFind(QF, '50%_x'))[0]).toBe('50%_x%');
  });

  it('fills eq placeholders with the number (no %) when the text is numeric', () => {
    expect(quickValues(applyQuickFind(NUM, ' 12.5 '))).toEqual(['12.5%', '12.5', '12.5']);
  });

  it('removes numeric conditions when the text is not a number, keeping the text ones', () => {
    expect(quickValues(applyQuickFind(NUM, 'abc'))).toEqual(['abc%', 'abc']);
    expect(quickValues(applyQuickFind(NUM, '1,5'))).toEqual(['1,5%', '1,5']);
  });

  it('removes an emptied filter', () => {
    const only = qf('<condition attribute="m" operator="eq" value="{1}"/>');
    expect(parse(applyQuickFind(only, 'abc')).querySelector('filter')).toBeNull();
  });

  it('throws naming a placeholder it cannot fill', () => {
    const bad = qf('<condition attribute="m" operator="in" value="{3}"/>');
    expect(() => applyQuickFind(bad, 'abc')).toThrow('{3}');
  });
});

describe('withOrder', () => {
  it('replaces every order (root and link-entity) with the one column', () => {
    const xml = withOrder(
      '<fetch><entity name="a"><order attribute="n"/><order attribute="m" descending="true"/>' +
        '<link-entity name="b" from="x" to="y" alias="bb"><order attribute="z"/></link-entity></entity></fetch>',
      'm',
      true,
    );
    const orders = [...parse(xml).getElementsByTagName('order')];
    expect(orders.map((o) => [o.parentElement!.tagName, o.getAttribute('attribute'), o.getAttribute('descending')])).toEqual([
      ['entity', 'm', 'true'],
    ]);
  });

  it('withOrder_on_a_link_column_orders_inside_the_link_entity (#100)', () => {
    const xml = withOrder(
      '<fetch><entity name="a"><order attribute="n"/><link-entity name="b" from="x" to="y" alias="bb">' +
        '<link-entity name="c" from="x" to="y" alias="cc"/></link-entity></entity></fetch>',
      'emailaddress1',
      false,
      'cc',
    );
    const orders = [...parse(xml).getElementsByTagName('order')];
    expect(orders.map((o) => [o.parentElement!.getAttribute('alias'), o.getAttribute('attribute'), o.getAttribute('descending')])).toEqual([
      ['cc', 'emailaddress1', 'false'],
    ]);
  });

  it('an unknown link alias is an explicit error', () => {
    expect(() => withOrder('<fetch><entity name="a"/></fetch>', 'n', false, 'zz')).toThrow('zz');
  });

  it('writes descending="false" for ascending', () => {
    const xml = withOrder('<fetch><entity name="a"/></fetch>', 'n', false);
    expect(parse(xml).getElementsByTagName('order')[0].getAttribute('descending')).toBe('false');
  });
});

describe('withTotalCount', () => {
  it('sets returntotalrecordcount="true"', () => {
    expect(parse(withTotalCount('<fetch><entity name="a"/></fetch>')).documentElement.getAttribute('returntotalrecordcount')).toBe('true');
  });
});

describe('linkEntities', () => {
  it('maps each link alias to its table, nested ones included', () => {
    expect(
      linkEntities(
        '<fetch><entity name="a"><link-entity name="contact" from="x" to="y" alias="pc">' +
          '<link-entity name="account" from="x" to="y" alias="pa"/></link-entity><link-entity name="team" from="x" to="y"/></entity></fetch>',
      ),
    ).toEqual({ pc: 'contact', pa: 'account' });
  });
});

describe('withAttributes', () => {
  const base = '<fetch><entity name="account"><attribute name="name"/><link-entity name="contact" from="a" to="b"><attribute name="fullname"/></link-entity></entity></fetch>';
  const rootAttrs = (xml: string) =>
    [...parse(xml).documentElement.querySelectorAll('entity > attribute')].map((a) => a.getAttribute('name'));

  it('adds_the_missing_root_attributes', () => {
    expect(rootAttrs(withAttributes(base, ['statuscode', 'createdon']))).toEqual(['name', 'statuscode', 'createdon']);
  });

  it('keeps_existing_and_adds_no_duplicate', () => {
    expect(rootAttrs(withAttributes(base, ['name', 'x', 'x']))).toEqual(['name', 'x']);
  });

  it('a_link_entity_attribute_does_not_count_as_a_root_one', () => {
    expect(rootAttrs(withAttributes(base, ['fullname']))).toEqual(['name', 'fullname']);
  });

  it('leaves_an_all_attributes_query_untouched', () => {
    const all = '<fetch><entity name="account"><all-attributes/></entity></fetch>';
    expect(withAttributes(all, ['x'])).toBe(all);
  });
});

describe('withDateRange', () => {
  const rootFilters = (xml: string) => [...parse(xml).documentElement.querySelectorAll('entity > filter')];
  const conds = (f: Element) => [...f.querySelectorAll(':scope > condition')].map((c) => [c.getAttribute('attribute'), c.getAttribute('operator'), c.getAttribute('value')]);
  const withFilter = '<fetch><entity name="t"><attribute name="a"/><filter type="or"><condition attribute="x" operator="eq" value="1"/></filter></entity></fetch>';

  it('appends_a_root_and_filter_and_keeps_the_existing_one', () => {
    const filters = rootFilters(withDateRange(withFilter, 'start', '2026-01-01T00:00:00.000Z', '2026-02-01T00:00:00.000Z'));
    expect(filters).toHaveLength(2);
    expect(filters[0].getAttribute('type')).toBe('or');
    expect(filters[1].getAttribute('type')).toBe('and');
  });

  it('a_date_time_column_uses_ge_and_le_with_the_iso_values', () => {
    const [, f] = rootFilters(withDateRange(withFilter, 'start', '2026-01-01T00:00:00.000Z', '2026-02-01T00:00:00.000Z'));
    expect(conds(f)).toEqual([
      ['start', 'ge', '2026-01-01T00:00:00.000Z'],
      ['start', 'le', '2026-02-01T00:00:00.000Z'],
    ]);
  });

  it('a_date_only_column_uses_on_or_after_and_on_or_before', () => {
    const [f] = rootFilters(withDateRange('<fetch><entity name="t"/></fetch>', 'due', '2026-01-01', '2026-01-31', { dateOnly: true }));
    expect(conds(f)).toEqual([
      ['due', 'on-or-after', '2026-01-01'],
      ['due', 'on-or-before', '2026-01-31'],
    ]);
  });

  it('an_end_attribute_also_matches_records_that_start_before_the_range', () => {
    const [f] = rootFilters(withDateRange('<fetch><entity name="t"/></fetch>', 'start', 'F', 'T', { end: 'end' }));
    expect(conds(f)).toEqual([['start', 'le', 'T']]);
    const or = f.querySelector(':scope > filter[type="or"]')!;
    expect([...or.querySelectorAll(':scope > condition')].map((c) => [c.getAttribute('attribute'), c.getAttribute('operator'), c.getAttribute('value')])).toEqual([['end', 'ge', 'F']]);
    const inner = or.querySelector(':scope > filter[type="and"]')!;
    expect(conds(inner)).toEqual([
      ['end', 'null', null],
      ['start', 'ge', 'F'],
    ]);
  });

  it('an_end_attribute_on_a_date_only_range_uses_the_date_operators', () => {
    const [f] = rootFilters(withDateRange('<fetch><entity name="t"/></fetch>', 'start', '2026-01-01', '2026-01-31', { dateOnly: true, end: 'end' }));
    expect(conds(f)).toEqual([['start', 'on-or-before', '2026-01-31']]);
    const or = f.querySelector(':scope > filter[type="or"]')!;
    expect(or.querySelector(':scope > condition')!.getAttribute('operator')).toBe('on-or-after');
    expect(or.querySelector('filter')!.querySelector('condition[attribute="start"]')!.getAttribute('operator')).toBe('on-or-after');
  });
});

describe('withNullCondition', () => {
  it('appends_a_root_and_filter_with_one_null_condition_and_keeps_the_existing_filter', () => {
    const xml = withNullCondition('<fetch><entity name="t"><filter type="or"><condition attribute="x" operator="eq" value="1"/></filter></entity></fetch>', 'start');
    const filters = [...parse(xml).documentElement.querySelectorAll('entity > filter')];
    expect(filters).toHaveLength(2);
    expect(filters[0].getAttribute('type')).toBe('or');
    expect(filters[1].getAttribute('type')).toBe('and');
    const conditions = [...filters[1].querySelectorAll('condition')];
    expect(conditions).toHaveLength(1);
    expect(conditions[0].getAttribute('attribute')).toBe('start');
    expect(conditions[0].getAttribute('operator')).toBe('null');
    expect(conditions[0].hasAttribute('value')).toBe(false);
  });
  it('rejects_a_document_without_an_entity', () => {
    expect(() => withNullCondition('<fetch/>', 'start')).toThrow();
  });
});
