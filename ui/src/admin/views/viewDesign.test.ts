import { describe, expect, it } from 'vitest';
import { parseXml } from '../../parsers/xml';
import type { FilterCondition, FilterGroup } from './filterModel';
import { addColumn, moveColumn, newView, readView, removeColumn, setWidth, writeView, type ViewDesign } from './viewDesign';
import { ACCOUNT, normalizeXml } from './viewDesignTestSupport';

const LAYOUT = '<grid name="resultset" jump="name" select="1" icon="1" preview="1"><row name="result" id="accountid">'
  + '<cell name="name" width="250"/><cell name="telephone1" width="120"/></row></grid>';
const FETCH = '<fetch version="1.0" mapping="logical"><entity name="account"><attribute name="name"/>'
  + '<attribute name="telephone1"/><attribute name="accountid"/><order attribute="name" descending="false"/>'
  + '<filter type="and"><condition attribute="statecode" operator="eq" value="0"/></filter></entity></fetch>';

function row(fetchxml: string, layoutxml: string, querytype = 0) {
  return { name: 'V', description: null, querytype, isdefault: false, fetchxml, layoutxml, returnedtypecode: 'account' };
}
const view = (fetchxml = FETCH, layoutxml = LAYOUT, querytype = 0) => readView(row(fetchxml, layoutxml, querytype), ACCOUNT);
const fetchDoc = (d: ViewDesign) => parseXml(writeView(d).fetchxml);
const layoutDoc = (d: ViewDesign) => parseXml(writeView(d).layoutxml);
const serialize = (n: Node) => new XMLSerializer().serializeToString(n);

describe('readView', () => {
  it('reads properties, columns, orders and filter', () => {
    const d = readView({ ...row(FETCH, LAYOUT), description: 'd', ismanaged: true, isdefault: true }, ACCOUNT);
    expect(d).toMatchObject({
      table: 'account', primaryId: 'accountid', primaryName: 'name', name: 'V', description: 'd', queryType: 0,
      isDefault: true, isManaged: true, links: [], ordersEditable: true, findColumns: [],
      columns: [{ name: 'name', attribute: 'name', width: 250 }, { name: 'telephone1', attribute: 'telephone1', width: 120 }],
      orders: [{ attribute: 'name', descending: false }],
      source: { fetchXml: FETCH, layoutXml: LAYOUT },
    });
    expect(d.filter.type).toBe('and');
    expect(d.filter.children).toHaveLength(1);
    expect((d.filter.children[0] as FilterGroup).children[0]).toMatchObject({ attribute: 'statecode', operator: 'eq', values: ['0'] });
  });
});

describe('round trip', () => {
  it('reads a missing or null isdefault as not default', () => {
    expect(readView({ ...row(FETCH, LAYOUT), isdefault: null as unknown as boolean }, ACCOUNT).isDefault).toBe(false);
    const { isdefault: _, ...without } = row(FETCH, LAYOUT);
    expect(readView(without as ReturnType<typeof row>, ACCOUNT).isDefault).toBe(false);
  });

  it('round_trips_unknown_parts', () => {
    const fetch = '<fetch version="1.0" mapping="logical" distinct="true" no-lock="true"><entity name="account">'
      + '<attribute name="name"/><attribute name="x" alias="y"/><attribute name="accountid"/>'
      + '<link-entity name="contact" from="contactid" to="primarycontactid" link-type="inner" alias="pc">'
      + '<attribute name="fullname"/><filter type="and"><condition attribute="statecode" operator="eq" value="0"/></filter>'
      + '</link-entity>'
      + '<filter type="and" hint="h"><condition attribute="ownerid" operator="eq" value="{00000000-0000-0000-0000-000000000001}"'
      + ' uitype="systemuser" uiname="A &amp; B"/></filter></entity></fetch>';
    const layout = '<grid name="resultset" jump="name" select="1" icon="1" preview="1" extra="e"><row name="result" id="accountid">'
      + '<cell name="name" width="250"/><cell name="y" width="80"/>'
      + '<cell name="pc.fullname" width="120" imageproviderfunctionname="f" imageproviderwebresource="$webresource:a.js"/>'
      + '</row></grid>';
    const d = view(fetch, layout);
    expect(d.columns.map((c) => [c.name, c.attribute, c.alias])).toEqual([
      ['name', 'name', undefined], ['y', 'x', undefined], ['pc.fullname', 'fullname', 'pc'],
    ]);
    expect(d.links).toEqual([{ alias: 'pc', entity: 'contact', from: 'contactid', to: 'primarycontactid' }]);
    const linkFilter = d.filter.children.find((c) => c.kind === 'opaque');
    expect(linkFilter).toEqual({
      kind: 'opaque', key: expect.any(String),
      xml: '<filter type="and"><condition attribute="statecode" operator="eq" value="0"/></filter>',
    });
    const out = writeView(d);
    expect(normalizeXml(out.fetchxml)).toBe(normalizeXml(fetch));
    expect(normalizeXml(out.layoutxml)).toBe(normalizeXml(layout));
  });

  it('keeps unknown attributes of a changed condition and its filter, and the rest unchanged', () => {
    const fetch = '<fetch><entity name="account"><attribute name="name"/>'
      + '<filter type="and" hint="h"><condition attribute="name" operator="eq" value="A" foo="bar"/>'
      + '<condition attribute="telephone1" operator="not-null"/></filter></entity></fetch>';
    const d = view(fetch, '<grid><row><cell name="name" width="100"/></row></grid>');
    const group = d.filter.children[0] as FilterGroup;
    const changed: FilterGroup = {
      ...group, type: 'or',
      children: [{ ...(group.children[0] as FilterCondition), values: ['B'] }, group.children[1]],
    };
    const out = writeView({ ...d, filter: { ...d.filter, children: [changed] } });
    expect(normalizeXml(out.fetchxml)).toBe(normalizeXml(fetch.replace('type="and" hint', 'type="or" hint').replace('value="A"', 'value="B"')));
  });

  it('a link-entity filter deleted from the root is removed from its link-entity', () => {
    const fetch = '<fetch><entity name="account"><attribute name="name"/>'
      + '<link-entity name="contact" from="contactid" to="primarycontactid" alias="pc"><attribute name="fullname"/>'
      + '<filter><condition attribute="statecode" operator="eq" value="0"/></filter></link-entity></entity></fetch>';
    const d = view(fetch, '<grid><row><cell name="name" width="100"/><cell name="pc.fullname" width="100"/></row></grid>');
    const out = fetchDoc({ ...d, filter: { ...d.filter, children: [] } });
    expect(out.getElementsByTagName('link-entity')[0].getElementsByTagName('filter')).toHaveLength(0);
  });
});

describe('columns', () => {
  it('add_related_column_creates_or_reuses_link', () => {
    let d = addColumn(view(), 'emailaddress1', { lookup: 'primarycontactid', entity: 'contact', from: 'contactid' });
    let doc = fetchDoc(d);
    const links = doc.getElementsByTagName('link-entity');
    expect(links).toHaveLength(1);
    expect(serialize(links[0])).toBe('<link-entity name="contact" from="contactid" to="primarycontactid" link-type="outer"'
      + ' alias="a_primarycontactid"><attribute name="emailaddress1"/></link-entity>');
    expect(d.columns.at(-1)).toEqual({ name: 'a_primarycontactid.emailaddress1', attribute: 'emailaddress1', alias: 'a_primarycontactid', width: 100 });
    expect(serialize(layoutDoc(d).getElementsByTagName('cell')[2])).toBe('<cell name="a_primarycontactid.emailaddress1" width="100"/>');

    d = addColumn(d, 'telephone1', { lookup: 'primarycontactid', entity: 'contact', from: 'contactid' });
    doc = fetchDoc(d);
    expect(doc.getElementsByTagName('link-entity')).toHaveLength(1);
    expect(Array.from(doc.getElementsByTagName('link-entity')[0].children).map((a) => a.getAttribute('name')))
      .toEqual(['emailaddress1', 'telephone1']);
    expect(d.links).toHaveLength(1);

    const existing = FETCH.replace('<order', '<link-entity name="contact" from="contactid" to="primarycontactid" alias="ac">'
      + '<attribute name="fullname"/></link-entity><order');
    d = addColumn(view(existing), 'emailaddress1', { lookup: 'primarycontactid', entity: 'contact', from: 'contactid' });
    expect(d.columns.at(-1)?.name).toBe('ac.emailaddress1');
    doc = fetchDoc(d);
    expect(doc.getElementsByTagName('link-entity')).toHaveLength(1);
    expect(serialize(doc.getElementsByTagName('link-entity')[0])).toContain('<attribute name="fullname"/><attribute name="emailaddress1"/>');
  });

  it('adds a column of the table to the fetch and the layout; the primary name gets 150, the primary id never', () => {
    let d = addColumn(view(), 'revenue');
    expect(d.columns.at(-1)).toEqual({ name: 'revenue', attribute: 'revenue', width: 100 });
    const attrs = Array.from(fetchDoc(d).getElementsByTagName('attribute')).map((a) => a.getAttribute('name'));
    expect(attrs).toEqual(['name', 'telephone1', 'accountid', 'revenue']);
    expect(addColumn(d, 'revenue')).toBe(d);
    expect(addColumn(d, 'accountid')).toBe(d);
    d = addColumn(removeColumn(d, 'name'), 'name');
    expect(d.columns.at(-1)).toEqual({ name: 'name', attribute: 'name', width: 150 });
  });

  it('remove_column_keeps_attribute_used_by_filter_or_order', () => {
    const fetch = FETCH.replace('</filter>', '<condition attribute="telephone1" operator="not-null"/></filter>');
    let d = removeColumn(view(fetch), 'telephone1');
    expect(d.columns.map((c) => c.name)).toEqual(['name']);
    let attrs = Array.from(fetchDoc(d).getElementsByTagName('attribute')).map((a) => a.getAttribute('name'));
    expect(attrs).toContain('telephone1');
    expect(Array.from(layoutDoc(d).getElementsByTagName('cell')).map((c) => c.getAttribute('name'))).toEqual(['name']);

    d = removeColumn(view(), 'name');
    attrs = Array.from(fetchDoc(d).getElementsByTagName('attribute')).map((a) => a.getAttribute('name'));
    expect(attrs).toEqual(['name', 'telephone1', 'accountid']);

    d = removeColumn(view(), 'telephone1');
    attrs = Array.from(fetchDoc(d).getElementsByTagName('attribute')).map((a) => a.getAttribute('name'));
    expect(attrs).toEqual(['name', 'accountid']);
  });

  it('remove_last_related_column_drops_empty_link', () => {
    const via = { lookup: 'primarycontactid', entity: 'contact', from: 'contactid' };
    let d = addColumn(addColumn(view(), 'emailaddress1', via), 'telephone1', via);
    d = removeColumn(d, 'a_primarycontactid.emailaddress1');
    expect(fetchDoc(d).getElementsByTagName('link-entity')).toHaveLength(1);
    d = removeColumn(d, 'a_primarycontactid.telephone1');
    expect(fetchDoc(d).getElementsByTagName('link-entity')).toHaveLength(0);

    // A saved view: the link-entity is in the source and its last column goes.
    const saved = readView(row(writeView(addColumn(view(), 'emailaddress1', via)).fetchxml,
      writeView(addColumn(view(), 'emailaddress1', via)).layoutxml), ACCOUNT);
    expect(fetchDoc(removeColumn(saved, 'a_primarycontactid.emailaddress1')).getElementsByTagName('link-entity')).toHaveLength(0);
  });

  it('move_and_width', () => {
    const d = view();
    expect(moveColumn(d, 'name', -1).columns).toEqual(d.columns);
    expect(moveColumn(d, 'telephone1', 1).columns).toEqual(d.columns);
    const moved = moveColumn(d, 'telephone1', -1);
    expect(moved.columns.map((c) => c.name)).toEqual(['telephone1', 'name']);
    expect(Array.from(layoutDoc(moved).getElementsByTagName('cell')).map((c) => c.getAttribute('name'))).toEqual(['telephone1', 'name']);
    expect(d.columns.map((c) => c.name)).toEqual(['name', 'telephone1']);
    expect(setWidth(d, 'name', 5).columns[0].width).toBe(25);
    expect(setWidth(d, 'name', 5000).columns[0].width).toBe(1000);
    expect(layoutDoc(setWidth(d, 'name', 300)).getElementsByTagName('cell')[0].getAttribute('width')).toBe('300');
  });
});

describe('escaping', () => {
  it('escapes_special_characters', () => {
    const d = view();
    const value = 'A & <B> "c"';
    const condition: FilterCondition = { kind: 'condition', attribute: 'name', operator: 'eq', values: [value], key: 'n-x' };
    const named = { ...d, name: 'Mine & yours', description: '<b>"x"</b>', filter: { ...d.filter, children: [...d.filter.children, condition] } };
    const out = writeView(named);
    const doc = parseXml(out.fetchxml);
    const read = Array.from(doc.getElementsByTagName('condition')).map((c) => c.getAttribute('value'));
    expect(read).toContain(value);
    expect(out.fetchxml).toContain('A &amp; &lt;B&gt; &quot;c&quot;');
    const again = readView({ ...row(out.fetchxml, out.layoutxml), name: named.name, description: named.description }, ACCOUNT);
    expect(again.name).toBe('Mine & yours');
    expect(again.description).toBe('<b>"x"</b>');
    expect(JSON.stringify(again.filter)).toContain(JSON.stringify(value));
  });
});

describe('orders', () => {
  it('reads two orders as editable and writes the changed ones', () => {
    const fetch = FETCH.replace('<order attribute="name" descending="false"/>',
      '<order attribute="name" descending="false"/><order attribute="telephone1" descending="true"/>');
    const d = view(fetch);
    expect(d.ordersEditable).toBe(true);
    expect(d.orders).toEqual([{ attribute: 'name', descending: false }, { attribute: 'telephone1', descending: true }]);
    const out = fetchDoc({ ...d, orders: [{ attribute: 'telephone1', descending: false }] });
    expect(Array.from(out.getElementsByTagName('order')).map(serialize)).toEqual(['<order attribute="telephone1" descending="false"/>']);
    expect(out.getElementsByTagName('entity')[0].children[3].tagName).toBe('order');
  });

  it.each([
    ['three orders', '<order attribute="name" descending="false"/><order attribute="a" descending="false"/><order attribute="b" descending="true"/>'],
    ['an order inside a link-entity', '<order attribute="name" descending="false"/><link-entity name="contact" from="contactid" to="primarycontactid" alias="c"><order attribute="fullname" descending="false"/></link-entity>'],
    ['an aggregate order', '<order alias="cnt" descending="true"/>'],
  ])('%s is not editable and is written back unchanged', (_, orders) => {
    const fetch = FETCH.replace('<order attribute="name" descending="false"/>', orders);
    const d = view(fetch);
    expect(d.ordersEditable).toBe(false);
    const out = writeView({ ...d, orders: [] });
    expect(normalizeXml(out.fetchxml)).toBe(normalizeXml(fetch));
  });
});

describe('quick find', () => {
  const QF = FETCH.replace('</entity>', '<filter type="or" isquickfindfields="1"><condition attribute="name" operator="like" value="{0}"/>'
    + '<condition attribute="accountnumber" operator="like" value="{0}"/></filter></entity>');

  it('quick_find_find_columns', () => {
    const d = view(QF, LAYOUT, 4);
    expect(d.findColumns).toEqual(['name', 'accountnumber']);
    expect(JSON.stringify(d.filter)).not.toContain('accountnumber');
    expect(normalizeXml(writeView(d).fetchxml)).toBe(normalizeXml(QF));

    const added = writeView({ ...d, findColumns: [...d.findColumns, 'telephone1'] }).fetchxml;
    const qf = Array.from(parseXml(added).getElementsByTagName('filter')).find((f) => f.getAttribute('isquickfindfields') === '1')!;
    expect(serialize(qf.children[2])).toBe('<condition attribute="telephone1" operator="like" value="{0}"/>');

    const removed = writeView({ ...d, findColumns: ['accountnumber'] }).fetchxml;
    expect(removed).not.toContain('attribute="name" operator="like"');
    expect(removed).toContain('attribute="accountnumber" operator="like"');
  });

  it('creates the quick find filter when a quick find view has none', () => {
    const d = view(FETCH, LAYOUT, 4);
    expect(d.findColumns).toEqual([]);
    expect(writeView({ ...d, findColumns: ['name'] }).fetchxml)
      .toContain('<filter type="or" isquickfindfields="1"><condition attribute="name" operator="like" value="{0}"/></filter>');
  });
});

describe('newView', () => {
  it('new_view', () => {
    const d = newView('account', ACCOUNT, 'My view');
    expect(d).toMatchObject({ table: 'account', name: 'My view', queryType: 0, isDefault: false, isManaged: false, ordersEditable: true });
    const { fetchxml, layoutxml } = writeView(d);
    const entity = serialize(parseXml(fetchxml).getElementsByTagName('entity')[0]);
    expect(entity).toBe('<entity name="account"><attribute name="name"/><attribute name="accountid"/>'
      + '<order attribute="name" descending="false"/>'
      + '<filter type="and"><condition attribute="statecode" operator="eq" value="0"/></filter></entity>');
    expect(normalizeXml(layoutxml)).toBe('<grid name="resultset" jump="name" select="1" icon="1" preview="1">'
      + '<row name="result" id="accountid"><cell name="name" width="150"/></row></grid>');
  });
});
