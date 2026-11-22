import { describe, expect, it } from 'vitest';
import { childElements, parseXml } from '../../parsers/xml';
import invented from '../../parsers/__fixtures__/forms/invented-all-features.xml?raw';
import type { AttributeMeta } from '../tables/metadata';
import { CLASS_ID } from '../../parsers/controlClass';
import {
  addField, addHeaderField, addSection, addTab, bodyAttributes, FORM_TYPE, classIdFor, containerImpact, designable, fieldImpact, flowCells, HEADER_LIMIT, moveCell,
  moveHeaderField, moveSection, moveTab, placeCell, placeHeaderField, placeSection, placeTab, newForm, newGuid, readForm, removeField, removeHeaderField, removeSection, removeTab,
  setCell, setSection, setTab, setTabLayout, uiLcid, writeForm, type FormDesign,
} from './formDesign';
import { blocksOf, formRow, KEPT_BLOCKS, RICH_FORM } from './formDesignTestSupport';

const GUID = /^\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}$/;
const read = (xml = RICH_FORM, lcid = 1033) => readForm(formRow(xml), lcid);
const written = (d: FormDesign) => parseXml(writeForm(d)).documentElement;
const serialize = (n: Node) => new XMLSerializer().serializeToString(n);
const sections = (d: FormDesign) => d.tabs.flatMap((t) => t.columns.flatMap((c) => c.sections));
const sectionEl = (form: Element, name: string) =>
  Array.from(form.getElementsByTagName('section')).find((s) => s.getAttribute('name') === name)!;
const rowsOf = (section: Element) =>
  Array.from(section.getElementsByTagName('row')).map((r) => childElements(r, 'cell').map((c) => c.getAttribute('id')));

describe('readForm', () => {
  it('reads properties, tabs, sections, cells and header', () => {
    const d = readForm(formRow(RICH_FORM, { description: 'D', isdefault: true, formactivationstate: null }), 1033);
    expect(d).toMatchObject({
      table: 'account', name: 'Information', description: 'D', type: 2, isDefault: true, active: true, lcid: 1033,
      text: RICH_FORM,
    });
    expect(d.tabs.map((t) => [t.name, t.id, t.showLabel, t.visible, t.columns.map((c) => c.width)])).toEqual([
      ['general', '{a0000000-0000-0000-0000-000000000001}', true, true, ['50%', '50%']],
      ['details', '{a0000000-0000-0000-0000-00000000000a}', true, true, ['100%']],
    ]);
    expect(d.tabs[0].labels).toEqual({ 1031: 'Allgemein', 1033: 'General' });
    const main = d.tabs[0].columns[0].sections[0];
    expect(main).toMatchObject({ name: 'main', columns: 2, showLabel: true, visible: true });
    expect(main.cells.map((c) => [c.kind, c.attribute, c.colSpan])).toEqual([
      ['field', 'name', 1], ['field', 'telephone1', 1], ['subgrid', undefined, 2], ['field', 'fax', 1], ['empty', undefined, 1],
    ]);
    expect(d.tabs[0].columns[1].sections[0].cells[0].kind).toBe('custom');
    expect(d.header.map((c) => [c.kind, c.attribute])).toEqual([['field', 'ownerid']]);
    const keys = [...d.tabs.map((t) => t.key), ...sections(d).map((s) => s.key), ...sections(d).flatMap((s) => s.cells.map((c) => c.key)), ...d.header.map((c) => c.key)];
    expect(new Set(keys).size).toBe(keys.length);
  });

  it('reads a deactivated form, and the kinds of the invented fixture', () => {
    const d = readForm(formRow(invented, { formactivationstate: 0 }), 1033);
    expect(d.active).toBe(false);
    expect(sections(d)[0].cells.map((c) => c.kind)).toEqual(['field', 'empty', 'field', 'quickview', 'field', 'unknown']);
    expect(sections(d)[0].cells[2]).toMatchObject({ visible: false, attribute: 'hiddenfield' });
    expect(sections(d)[0].cells[3]).toMatchObject({ colSpan: 2, rowSpan: 4 });
  });

  it.each([
    ['empty', ''],
    ['null', null],
    ['malformed', '<form><tabs>'],
    ['without tabs', '<form><header /></form>'],
    ['not a form', '<grid><tabs /></grid>'],
  ])('throws a readable error on a FormXML that is %s', (_, xml) => {
    expect(() => readForm(formRow(xml), 1033)).toThrow(/FormXML/);
  });
});

describe('writeForm', () => {
  it('gives back the same string with no edit', () => {
    expect(writeForm(read(invented))).toBe(invented);
    expect(writeForm(read(RICH_FORM))).toBe(RICH_FORM);
  });

  it('gives back the same string after an edit that is undone', () => {
    const added = addTab(read());
    expect(writeForm(removeTab(added, added.tabs[2].key))).toBe(RICH_FORM);
  });

  it('keeps unknown attributes, unknown blocks and other-language labels after an edit', () => {
    const d = setTab(read(), read().tabs[0].key, { label: 'Generale' });
    const form = written(d);
    expect(form.getAttribute('maxWidth')).toBe('1920');
    const tab = form.getElementsByTagName('tab')[0];
    expect(tab.getAttribute('locklevel')).toBe('0');
    expect(tab.getAttributeNS('urn:x', 'extra')).toBe('1');
    expect(serialize(form.getElementsByTagName('unknownBlock')[0])).toBe('<unknownBlock a="b"><inner/></unknownBlock>');
    expect(form.getElementsByTagName('row')[0].getAttribute('height')).toBe('2');
    expect(form.getElementsByTagName('control')[2].getAttribute('indicationOfSubgrid')).toBe('true');
  });

  it('keeps events, libraries, navigation, display conditions and footer byte-equal after tab and section edits', () => {
    const d = read();
    const tab = d.tabs[0].key;
    const main = d.tabs[0].columns[0].sections[0].key;
    const edits: FormDesign[] = [
      addTab(d), removeTab(d, d.tabs[1].key), moveTab(d, tab, 1), setTab(d, tab, { label: 'X', visible: false }),
      setTabLayout(d, tab, '1'), setTabLayout(d, tab, '3'), addSection(d, tab, 0), removeSection(d, main),
      moveSection(d, main, 'next-column'), setSection(d, main, { columns: 1, label: 'Y' }),
    ];
    for (const e of edits) {
      expect(e).not.toBe(d);
      const out = writeForm(e);
      expect(out).not.toBe(RICH_FORM);
      // Removing section main removes the last control of `name`, so its onchange event goes with it (ruling F7).
      const kept = e === edits[7] ? KEPT_BLOCKS.filter((t) => t !== 'events') : KEPT_BLOCKS;
      for (const tag of [...kept, 'header', 'unknownBlock']) expect(blocksOf(out, tag)).toEqual(blocksOf(RICH_FORM, tag));
    }
    const events = (xml: string) => Array.from(parseXml(xml).getElementsByTagName('event')).map(serialize);
    expect(events(writeForm(edits[7]))).toEqual(events(RICH_FORM).filter((ev) => !ev.includes('attribute="name"')));
  });

  it('keeps control parameters byte-equal after edits that move their cells', () => {
    const d = read();
    const main = d.tabs[0].columns[0].sections[0].key;
    const params = (xml: string) => Array.from(parseXml(xml).getElementsByTagName('parameters')).map(serialize);
    for (const e of [setSection(d, main, { columns: 1 }), moveSection(d, main, 'next-column'), setTabLayout(d, d.tabs[0].key, '1')]) {
      expect(params(writeForm(e))).toEqual(params(RICH_FORM));
    }
  });

  it('updates every label of the language when a label is duplicated', () => {
    const d = read('<form><tabs><tab name="t"><labels><label description="A" languagecode="1033"/>'
      + '<label description="B" languagecode="1033"/></labels></tab></tabs></form>');
    expect(d.tabs[0].labels).toEqual({ 1033: 'B' });
    const next = setTab(d, d.tabs[0].key, { label: 'C' });
    expect(read(writeForm(next)).tabs[0].labels).toEqual({ 1033: 'C' });
    expect(Array.from(written(next).getElementsByTagName('label')).map((l) => l.getAttribute('description'))).toEqual(['C', 'C']);
  });

  it('round trips labels with markup characters, accents and emoji', () => {
    const label = '<&"è😀\'>';
    const d = setSection(setTab(read(), read().tabs[0].key, { label }), read().tabs[1].columns[0].sections[0].key, { label });
    const again = read(writeForm(d));
    expect(again.tabs[0].labels[1033]).toBe(label);
    expect(again.tabs[1].columns[0].sections[0].labels[1033]).toBe(label);
    expect(writeForm(again)).toBe(writeForm(d));
  });
});

describe('tabs', () => {
  it('addTab gives tab_{n} with a unique name, a {guid} id and one section', () => {
    const d = read('<form><tabs><tab name="tab_1"><columns><column width="100%"><sections /></column></columns></tab>'
      + '<tab name="tab_3" /></tabs></form>');
    const next = addTab(d);
    const tab = next.tabs[2];
    expect(tab).toMatchObject({ name: 'tab_2', showLabel: true, visible: true });
    expect(tab.id).toMatch(GUID);
    expect(tab.columns.map((c) => c.width)).toEqual(['100%']);
    expect(tab.columns[0].sections).toHaveLength(1);
    expect(addTab(next).tabs[3].name).toBe('tab_4');

    const el = written(next).getElementsByTagName('tab')[2];
    expect(el.getAttribute('name')).toBe('tab_2');
    expect(el.getAttribute('id')).toBe(tab.id);
    expect(['showlabel', 'expanded', 'verticallayout'].map((a) => el.getAttribute(a))).toEqual(['true', 'true', 'true']);
    const section = el.getElementsByTagName('section')[0];
    expect(section.getAttribute('id')).toMatch(GUID);
    expect(['showlabel', 'showbar', 'columns'].map((a) => section.getAttribute(a))).toEqual(['true', 'false', '1']);
    expect(childElements(section, 'rows')).toHaveLength(1);
    expect(el.getElementsByTagName('column')[0].getAttribute('width')).toBe('100%');
  });

  it('removeTab of the only tab is a no-op, of another removes it', () => {
    const one = read('<form><tabs><tab name="a" /></tabs></form>');
    expect(removeTab(one, one.tabs[0].key)).toBe(one);
    const d = read();
    const next = removeTab(d, d.tabs[0].key);
    expect(next.tabs.map((t) => t.name)).toEqual(['details']);
    expect(Array.from(written(next).getElementsByTagName('tab')).map((t) => t.getAttribute('name'))).toEqual(['details']);
    expect(removeTab(d, 'nope')).toBe(d);
  });

  it('moveTab reorders the tabs', () => {
    const d = read();
    const next = moveTab(d, d.tabs[1].key, -1);
    expect(next.tabs.map((t) => t.name)).toEqual(['details', 'general']);
    expect(Array.from(written(next).getElementsByTagName('tab')).map((t) => t.getAttribute('name'))).toEqual(['details', 'general']);
    expect(moveTab(d, d.tabs[0].key, -1)).toBe(d);
    expect(moveTab(d, d.tabs[1].key, 1)).toBe(d);
  });

  it('setTab edits only the UI-LCID label', () => {
    const d = read(RICH_FORM, 1040);
    const next = setTab(d, d.tabs[0].key, { label: 'Generale', showLabel: false, visible: false });
    expect(next.tabs[0].labels).toEqual({ 1031: 'Allgemein', 1033: 'General', 1040: 'Generale' });
    const tab = written(next).getElementsByTagName('tab')[0];
    expect(Array.from(tab.getElementsByTagName('label')).filter((l) => l.parentElement?.parentElement === tab)
      .map((l) => [l.getAttribute('languagecode'), l.getAttribute('description')]))
      .toEqual([['1031', 'Allgemein'], ['1033', 'General'], ['1040', 'Generale']]);
    expect(tab.getAttribute('showlabel')).toBe('false');
    expect(tab.getAttribute('visible')).toBe('false');
    expect(setTab(d, d.tabs[0].key, { showLabel: true, visible: true })).toBe(d);

    const en = setTab(read(), d.tabs[0].key, { label: 'Overview' });
    expect(en.tabs[0].labels).toEqual({ 1031: 'Allgemein', 1033: 'Overview' });
  });

  it("setTabLayout('1') on a 3-column tab keeps all sections in order", () => {
    const d = read();
    const three = addSection(setTabLayout(d, d.tabs[0].key, '3'), d.tabs[0].key, 2);
    expect(three.tabs[0].columns.map((c) => c.width)).toEqual(['33%', '33%', '34%']);
    const order = three.tabs[0].columns.flatMap((c) => c.sections.map((s) => s.name));
    expect(order).toEqual(['main', 'side', 'section_1']);
    const one = setTabLayout(three, d.tabs[0].key, '1');
    expect(one.tabs[0].columns).toHaveLength(1);
    expect(one.tabs[0].columns[0].width).toBe('100%');
    expect(one.tabs[0].columns[0].sections.map((s) => s.name)).toEqual(order);
    const tab = written(one).getElementsByTagName('tab')[0];
    expect(Array.from(tab.getElementsByTagName('column')).map((c) => c.getAttribute('width'))).toEqual(['100%']);
    expect(Array.from(tab.getElementsByTagName('section')).map((s) => s.getAttribute('name'))).toEqual(order);
    expect(setTabLayout(d, d.tabs[0].key, '2-50')).toBe(d);
    expect(setTabLayout(d, d.tabs[0].key, '2-67').tabs[0].columns.map((c) => c.width)).toEqual(['67%', '33%']);
  });
});

describe('sections', () => {
  it('addSection gives a unique section_{n} at the end of the column', () => {
    const d = read('<form><tabs><tab name="a"><columns><column width="100%"><sections>'
      + '<section name="section_1" /></sections></column></columns></tab>'
      + '<tab name="b"><columns><column width="100%"><sections><section name="section_2" /></sections></column></columns></tab>'
      + '</tabs></form>');
    const next = addSection(d, d.tabs[0].key, 0);
    expect(next.tabs[0].columns[0].sections.map((s) => s.name)).toEqual(['section_1', 'section_3']);
    expect(next.tabs[0].columns[0].sections[1].id).toMatch(GUID);
    expect(addSection(next, d.tabs[1].key, 0).tabs[1].columns[0].sections[1].name).toBe('section_4');
    expect(addSection(d, d.tabs[0].key, 5)).toBe(d);
  });

  it("moveSection('next-column') moves the section to the next column", () => {
    const d = read();
    const main = d.tabs[0].columns[0].sections[0].key;
    const next = moveSection(d, main, 'next-column');
    expect(next.tabs[0].columns.map((c) => c.sections.map((s) => s.name))).toEqual([[], ['side', 'main']]);
    const cols = written(next).getElementsByTagName('tab')[0].getElementsByTagName('column');
    expect(Array.from(cols[1].getElementsByTagName('section')).map((s) => s.getAttribute('name'))).toEqual(['side', 'main']);
    expect(cols[0].getElementsByTagName('section')).toHaveLength(0);
    expect(moveSection(next, main, 'next-column')).toBe(next);
    expect(moveSection(next, main, 'prev-column').tabs[0].columns[0].sections.map((s) => s.name)).toEqual(['main']);
    expect(moveSection(next, main, 'up').tabs[0].columns[1].sections.map((s) => s.name)).toEqual(['main', 'side']);
    expect(moveSection(next, main, 'down')).toBe(next);
  });

  it('removeSection removes it, but not the only section of a tab', () => {
    const d = read();
    expect(removeSection(d, d.tabs[1].columns[0].sections[0].key)).toBe(d);
    const next = removeSection(d, d.tabs[0].columns[1].sections[0].key);
    expect(sections(next).map((s) => s.name)).toEqual(['main', 'more']);
    expect(sectionEl(written(next), 'side')).toBeUndefined();
  });

  it('setSection({columns: 2}) re-flows the cells, clamping colspan', () => {
    const d = read();
    const main = d.tabs[0].columns[0].sections[0];
    const ids = main.cells.map((c) => c.id);
    const one = setSection(d, main.key, { columns: 1 });
    expect(one.tabs[0].columns[0].sections[0].cells.map((c) => c.colSpan)).toEqual([1, 1, 1, 1, 1]);
    const el1 = sectionEl(written(one), 'main');
    expect(el1.getAttribute('columns')).toBe('1');
    expect(rowsOf(el1)).toEqual(ids.map((id) => [id]));
    expect(el1.getElementsByTagName('cell')[2].hasAttribute('colspan')).toBe(true);
    expect(el1.getElementsByTagName('cell')[2].getAttribute('colspan')).toBe('1');

    const two = setSection(one, main.key, { columns: 2 });
    const el2 = sectionEl(written(two), 'main');
    expect(el2.getAttribute('columns')).toBe('11');
    // Back to the source's column count: the source's row breaks are kept (ruling I1), only the colspan stays clamped.
    expect(rowsOf(el2)).toEqual([[ids[0], ids[1]], [ids[2]], [ids[3], ids[4]]]);

    const four = setSection(d, main.key, { columns: 4 });
    const el4 = sectionEl(written(four), 'main');
    expect(el4.getAttribute('columns')).toBe('1111');
    expect(rowsOf(el4)).toEqual([[ids[0], ids[1], ids[2]], [ids[3], ids[4]]]);
    expect(setSection(d, main.key, { columns: 2 })).toBe(d);
  });

  it('re-flows around a tall cell: beside it, then padding rows', () => {
    const d = read(invented);
    const main = d.tabs[0].columns[0].sections[0];
    const ids = main.cells.map((c) => c.id);
    const four = sectionEl(written(setSection(d, main.key, { columns: 4 })), 'main');
    expect(four.getAttribute('columns')).toBe('1111');
    // name (2) spacer hidden | quick view (2, 4 rows) progress mystery | 3 rows of padding under the quick view.
    expect(rowsOf(four)).toEqual([[ids[0], ids[1], ids[2]], [ids[3], ids[4], ids[5]], [], [], []]);

    const one = sectionEl(written(setSection(d, main.key, { columns: 1 })), 'main');
    expect(rowsOf(one)).toEqual([[ids[0]], [ids[1]], [ids[2]], [ids[3]], [], [], [], [ids[4]], [ids[5]]]);
  });

  it('keeps the padding of a tall cell after a re-flow', () => {
    const cell = (id: string, extra = '') => `<cell id="${id}"${extra}><control id="${id}" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="${id}"/></cell>`;
    const d = read('<form><tabs><tab name="t"><columns><column width="100%"><sections><section name="s" columns="1"><rows>'
      + `<row>${cell('g', ' rowspan="3"')}</row><row/><row/><row>${cell('a')}</row><row>${cell('b')}</row>`
      + '</rows></section></sections></column></columns></tab></tabs></form>');
    const s = d.tabs[0].columns[0].sections[0];
    expect(rowsOf(sectionEl(written(setSection(d, s.key, { columns: 2 })), 's'))).toEqual([['g', 'a'], ['b'], []]);
    const back = setSection(setSection(d, s.key, { columns: 2 }), s.key, { columns: 1 });
    expect(rowsOf(sectionEl(written(back), 's'))).toEqual([['g'], [], [], ['a'], ['b']]);
  });

  it('flowCells places cells in the first free slots and pads after tall cells', () => {
    const c = (colSpan: number, rowSpan = 1, n = '') => ({ colSpan, rowSpan, n });
    const names = (rows: { n: string }[][]) => rows.map((r) => r.map((x) => x.n));
    expect(names(flowCells([c(1, 1, 'a'), c(2, 1, 'b'), c(1, 1, 'c')], 2))).toEqual([['a'], ['b'], ['c']]);
    expect(names(flowCells([c(1, 2, 'a'), c(1, 1, 'b'), c(1, 1, 'c'), c(2, 1, 'd')], 2))).toEqual([['a', 'b'], ['c'], ['d']]);
    expect(names(flowCells([c(2, 2, 'a'), c(1, 1, 'b')], 2))).toEqual([['a'], [], ['b']]);
    expect(names(flowCells([c(5, 1, 'a')], 3))).toEqual([['a']]);
    expect(flowCells([], 2)).toEqual([]);
  });

  it('setSection edits label, show label and visible', () => {
    const d = read();
    const main = d.tabs[0].columns[0].sections[0];
    const next = setSection(d, main.key, { label: 'Principal', showLabel: false, visible: false });
    expect(next.tabs[0].columns[0].sections[0].labels).toEqual({ 1031: 'Haupt', 1033: 'Principal' });
    const el = sectionEl(written(next), 'main');
    expect([el.getAttribute('showlabel'), el.getAttribute('visible'), el.getAttribute('labelwidth')]).toEqual(['false', 'false', '115']);
    expect(el.getElementsByTagName('row')[0].getAttribute('height')).toBe('2');
  });
});

describe('newForm and helpers', () => {
  const primary = { logicalName: 'name', displayName: 'Account Name', kind: 'string' } as AttributeMeta;

  it('newForm makes a Main form with tab and section General and the primary name', () => {
    const d = newForm('account', primary, 1040, 'Mine');
    expect(d).toMatchObject({ table: 'account', name: 'Mine', description: '', type: 2, isDefault: false, active: true, lcid: 1040 });
    expect(d.tabs).toHaveLength(1);
    expect(d.tabs[0].labels).toEqual({ 1040: 'General' });
    expect(d.tabs[0].columns[0].sections[0]).toMatchObject({ labels: { 1040: 'General' }, columns: 1 });
    expect(d.tabs[0].columns[0].sections[0].cells).toMatchObject([{ kind: 'field', attribute: 'name', labels: { 1040: 'Account Name' } }]);
    expect(writeForm(d)).toBe(d.text);
    const control = parseXml(d.text).getElementsByTagName('control')[0];
    expect(control.getAttribute('classid')).toBe('{4273EDBD-AC1D-40d3-9FB2-095C621B552D}');
    expect(control.getAttribute('datafieldname')).toBe('name');
  });

  it('newForm builds a quick view and a quick create form with the same skeleton', () => {
    const ids = (text: string) => text.replace(/\{[0-9a-f-]{36}\}/gi, '{id}');
    const main = newForm('account', primary, 1040, 'Mine');
    expect(main.type).toBe(2);
    for (const type of [FORM_TYPE.quickView, FORM_TYPE.quickCreate]) {
      const d = newForm('account', primary, 1040, 'Mine', type);
      expect(d.type).toBe(type);
      expect(ids(writeForm(d))).toBe(ids(writeForm(main)));
    }
  });

  it('newGuid is a lower case guid in braces, uiLcid a known LCID', () => {
    expect(newGuid()).toMatch(GUID);
    expect(newGuid()).not.toBe(newGuid());
    expect([1033, 1040]).toContain(uiLcid());
  });
});

const attr = (logicalName: string, kind: AttributeMeta['kind'] = 'string', extra: Partial<AttributeMeta> = {}) =>
  ({ logicalName, displayName: `${logicalName} label`, kind, requiredLevel: 'None', ...extra }) as AttributeMeta;
const controlOf = (form: Element, id: string) =>
  Array.from(form.getElementsByTagName('control')).find((c) => c.getAttribute('id') === id);
const cellOf = (d: FormDesign, attribute: string) =>
  sections(d).flatMap((s) => s.cells).find((c) => c.attribute === attribute)!;

/** A 2-column section `two` holding one field, a 1-column section `one`, and a second tab with section `far`. */
const SMALL = `<form><tabs>
  <tab name="a"><columns><column width="100%"><sections>
    <section name="two" columns="11"><rows><row><cell id="{c1}"><control id="name" classid="${CLASS_ID.text}" datafieldname="name" /></cell></row></rows></section>
    <section name="one" columns="1"><rows /></section>
  </sections></column></columns></tab>
  <tab name="b"><columns><column width="100%"><sections>
    <section name="far" columns="1"><rows><row><cell id="{c2}"><control id="fax" classid="${CLASS_ID.text}" datafieldname="fax" /></cell></row></rows></section>
  </sections></column></columns></tab>
</tabs></form>`;

describe('fields', () => {
  it('addField writes the F6 cell with the class id of the column kind', () => {
    const d = read(SMALL);
    const one = sections(d)[1].key;
    const cases: [AttributeMeta, string][] = [
      [attr('t1', 'string'), CLASS_ID.text], [attr('c1', 'choice'), CLASS_ID.choice], [attr('l1', 'lookup'), CLASS_ID.lookup],
      [attr('d1', 'datetime'), CLASS_ID.dateTime], [attr('m1', 'money'), CLASS_ID.money],
    ];
    const next = cases.reduce((acc, [a]) => addField(acc, one, a), d);
    const form = written(next);
    const section = sectionEl(form, 'one');
    expect(rowsOf(section)).toHaveLength(5);
    for (const [a, classId] of cases) {
      const control = controlOf(form, a.logicalName)!;
      expect(serialize(control)).toBe(
        `<control id="${a.logicalName}" classid="${classId}" datafieldname="${a.logicalName}" disabled="false"/>`,
      );
      const cell = control.parentNode as Element;
      expect(cell.getAttribute('id')).toMatch(GUID);
      expect(cell.getAttribute('showlabel')).toBe('true');
      expect(serialize(childElements(cell, 'labels')[0]))
        .toBe(`<labels><label description="${a.logicalName} label" languagecode="1033"/></labels>`);
    }
    expect(read(writeForm(next)).tabs[0].columns[0].sections[1].cells.map((c) => c.attribute)).toEqual(['t1', 'c1', 'l1', 'd1', 'm1']);
  });

  it('classIdFor falls back to the text class id for kinds without one', () => {
    expect(classIdFor(attr('x', 'memo'))).toBe(CLASS_ID.memo);
    expect(classIdFor(attr('x', 'int'))).toBe(CLASS_ID.integer);
    expect(classIdFor(attr('x', 'file'))).toBe(CLASS_ID.text);
    expect(classIdFor(attr('x', 'unsupported'))).toBe(CLASS_ID.text);
    expect(['string', 'choice', 'lookup', 'datetime', 'money', 'bool', 'partylist'].every((k) => designable(attr('x', k as AttributeMeta['kind'])))).toBe(true);
    expect(['bigint', 'file', 'image', 'unsupported'].some((k) => designable(attr('x', k as AttributeMeta['kind'])))).toBe(false);
  });

  it('addField fills the free slot of the last row of a 2-column section, then opens a new row', () => {
    const d = read(SMALL);
    const two = sections(d)[0].key;
    const once = addField(d, two, attr('t1'));
    const ids = (x: FormDesign) => rowsOf(sectionEl(written(x), 'two')).map((r) => r.length);
    expect(ids(once)).toEqual([2]);
    expect(ids(addField(once, two, attr('t2')))).toEqual([2, 1]);
  });

  it('addField of a column already in the body is a no-op, also with another case', () => {
    const d = read(SMALL);
    expect(addField(d, sections(d)[1].key, attr('name'))).toBe(d);
    expect(addField(d, sections(d)[1].key, attr('FAX'))).toBe(d);
    expect(addField(d, 'nope', attr('t1'))).toBe(d);
    expect(bodyAttributes(d)).toEqual(new Set(['name', 'fax']));
  });

  it('removeField removes the cell and only that attribute\'s onchange event', () => {
    const xml = RICH_FORM.replace(
      '<Handler functionName="App.onName" libraryName="new_app.js" handlerUniqueId="{c0000000-0000-0000-0000-000000000002}" enabled="true" />',
      '<Handler functionName="App.onName" libraryName="new_app.js" handlerUniqueId="{c0000000-0000-0000-0000-000000000002}" enabled="true" />\n'
      + '        <Handler functionName="App.onName2" libraryName="new_app.js" handlerUniqueId="{c0000000-0000-0000-0000-000000000004}" enabled="true" />',
    ).replace('  </events>', `    <event name="onchange" application="false" active="false" attribute="fax">
      <Handlers>
        <Handler functionName="App.onFax" libraryName="new_app.js" handlerUniqueId="{c0000000-0000-0000-0000-000000000005}" enabled="true" />
      </Handlers>
    </event>
  </events>`);
    const d = read(xml);
    const name = cellOf(d, 'name');
    const attrs = [attr('name', 'string', { requiredLevel: 'ApplicationRequired' }), attr('fax'), attr('telephone1', 'string', { requiredLevel: 'SystemRequired' })];
    expect(fieldImpact(d, name.key, attrs)).toEqual({ handlers: 2, required: true });
    expect(fieldImpact(d, cellOf(d, 'fax').key, attrs)).toEqual({ handlers: 1, required: false });
    expect(fieldImpact(d, cellOf(d, 'telephone1').key, attrs)).toEqual({ handlers: 0, required: true });
    expect(fieldImpact(d, 'nope', attrs)).toEqual({ handlers: 0, required: false });

    const next = removeField(d, name.key);
    expect(bodyAttributes(next).has('name')).toBe(false);
    const out = writeForm(next);
    const events = (text: string) => Array.from(parseXml(text).getElementsByTagName('event')).map(serialize);
    expect(events(out)).toEqual(events(xml).filter((e) => !e.includes('attribute="name"')));
    expect(events(out)).toHaveLength(2);
    expect(controlOf(parseXml(out).documentElement, 'name')).toBeUndefined();
    for (const tag of KEPT_BLOCKS.filter((t) => t !== 'events')) expect(blocksOf(out, tag)).toEqual(blocksOf(xml, tag));
    expect(fieldImpact(next, cellOf(next, 'fax').key, attrs).handlers).toBe(1);
  });

  it('removeField drops the column\'s handlers from the model: adding the column back does not restore them (F7)', () => {
    const d = read();
    const main = sections(d)[0].key;
    const removed = removeField(d, cellOf(d, 'name').key);
    expect(removed.events.filter((e) => e.name === 'onchange')).toEqual([]);
    const back = addField(removed, main, attr('name'));
    expect(parseXml(writeForm(back)).getElementsByTagName('event')).toHaveLength(1);
  });

  it("moveCell moves within the section and crosses sections and tabs in reading order", () => {
    const d = read(SMALL);
    const name = cellOf(d, 'name').key;
    const fax = cellOf(d, 'fax').key;
    const toOne = moveCell(d, name, 'next-section');
    expect(sections(toOne).map((s) => s.cells.map((c) => c.attribute))).toEqual([[], ['name'], ['fax']]);
    const toFar = moveCell(toOne, name, 'next-section');
    expect(sections(toFar).map((s) => s.cells.map((c) => c.attribute))).toEqual([[], [], ['name', 'fax']]);
    expect(moveCell(toFar, name, 'prev-section')).toEqual(toOne);
    expect(moveCell(d, name, 'prev-section')).toBe(d);
    expect(moveCell(d, fax, 'next-section')).toBe(d);
    const down = moveCell(toFar, name, 'down');
    expect(sections(down)[2].cells.map((c) => c.attribute)).toEqual(['fax', 'name']);
    expect(moveCell(down, name, 'down')).toBe(down);
    expect(moveCell(down, name, 'up')).toEqual(toFar);
    expect(rowsOf(sectionEl(written(toFar), 'far'))).toHaveLength(2);
    expect(read(writeForm(toFar)).tabs[1].columns[0].sections[0].cells.map((c) => c.attribute)).toEqual(['name', 'fax']);
  });

  it('moveCell clamps the colspan to the target section', () => {
    const d = read();
    const subgrid = sections(d)[0].cells[2];
    const moved = moveCell(d, subgrid.key, 'next-section');
    expect(sections(moved)[1].cells[0]).toMatchObject({ key: subgrid.key, colSpan: 1 });
  });

  it('setCell edits label, show label, visible and disabled', () => {
    const d = read();
    const name = cellOf(d, 'name').key;
    const next = setCell(d, name, { disabled: true, label: 'N', showLabel: false, visible: false });
    const control = controlOf(written(next), 'name')!;
    expect(control.getAttribute('disabled')).toBe('true');
    const cell = control.parentNode as Element;
    expect(cell.getAttribute('showlabel')).toBe('false');
    expect(cell.getAttribute('visible')).toBe('false');
    expect(read(writeForm(next)).tabs[0].columns[0].sections[0].cells[0]).toMatchObject({ labels: { 1033: 'N' }, disabled: true });
    expect(setCell(d, name, { disabled: false })).toBe(d);
    expect(setCell(d, 'nope', { disabled: true })).toBe(d);
  });

  it('moves and removes non-field cells, keeping their XML byte-equal', () => {
    const d = read();
    const subgrid = sections(d)[0].cells[2];
    const original = serialize(Array.from(parseXml(RICH_FORM).getElementsByTagName('cell')).find((c) => c.getAttribute('id') === subgrid.id)!);
    const cellXml = (x: FormDesign) =>
      Array.from(written(x).getElementsByTagName('cell')).find((c) => c.getAttribute('id') === subgrid.id);
    for (const moved of [moveCell(d, subgrid.key, 'up'), moveCell(d, subgrid.key, 'down')]) {
      expect(moved).not.toBe(d);
      expect(serialize(cellXml(moved)!)).toBe(original);
    }
    const custom = sections(d)[1].cells[0];
    expect(custom.kind).toBe('custom');
    const customXml = (x: FormDesign) =>
      Array.from(written(x).getElementsByTagName('cell')).find((c) => c.getAttribute('id') === custom.id);
    const customSource = Array.from(parseXml(RICH_FORM).getElementsByTagName('cell')).find((c) => c.getAttribute('id') === custom.id)!;
    const crossed = moveCell(d, custom.key, 'prev-section');
    expect(serialize(customXml(crossed)!)).toBe(serialize(customSource));
    const removed = removeField(d, subgrid.key);
    expect(cellXml(removed)).toBeUndefined();
    expect(parseXml(writeForm(removed)).getElementsByTagName('event')).toHaveLength(2);
    expect(fieldImpact(d, subgrid.key, [])).toEqual({ handlers: 0, required: false });
  });
});

describe('onchange events follow the last control of the column', () => {
  const events = (text: string) => Array.from(parseXml(text).getElementsByTagName('event'))
    .map((e) => e.getAttribute('attribute') ?? e.getAttribute('name'));
  const OWNER_EVENT = RICH_FORM.replace('  </events>', `    <event name="onchange" application="false" active="false" attribute="ownerid">
      <Handlers>
        <Handler functionName="App.onOwner" libraryName="new_app.js" handlerUniqueId="{c0000000-0000-0000-0000-000000000006}" enabled="true" />
      </Handlers>
    </event>
  </events>`);

  it('keeps the events when the body field is removed while the column is in the header', () => {
    const d = addHeaderField(read(), attr('name'));
    const name = cellOf(d, 'name').key;
    expect(fieldImpact(d, name, [])).toEqual({ handlers: 0, required: false });
    expect(fieldImpact(d, d.header[1].key, [])).toEqual({ handlers: 0, required: false });
    expect(events(writeForm(removeField(d, name)))).toEqual(['onload', 'name']);
  });

  it('drops the events when the last control, in the header, is removed', () => {
    const d = read(OWNER_EVENT);
    expect(fieldImpact(d, d.header[0].key, [])).toEqual({ handlers: 1, required: false });
    const out = writeForm(removeHeaderField(d, d.header[0].key));
    expect(events(out)).toEqual(['onload', 'name']);
    expect(blocksOf(out, 'events')[0]).toBe(blocksOf(RICH_FORM, 'events')[0]);
  });

  it('does not restore the events when the column comes back in the header after a body removal (F7 in the model)', () => {
    const d = read();
    const removed = removeField(d, cellOf(d, 'name').key);
    expect(events(writeForm(removed))).toEqual(['onload']);
    expect(events(writeForm(addHeaderField(removed, attr('name'))))).toEqual(['onload']);
    // Added to the header first, the column never leaves the form: the events stay.
    const both = removeField(addHeaderField(d, attr('name')), cellOf(d, 'name').key);
    expect(events(writeForm(both))).toEqual(['onload', 'name']);
  });

  it('keeps the events when another body control of the column remains', () => {
    const xml = RICH_FORM.replace(
      '<control id="fax" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="fax" />',
      '<control id="name1" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="name" />',
    );
    const d = read(xml);
    const names = sections(d).flatMap((s) => s.cells).filter((c) => c.attribute === 'name');
    expect(names).toHaveLength(2);
    expect(fieldImpact(d, names[0].key, [])).toEqual({ handlers: 0, required: false });
    const once = removeField(d, names[0].key);
    expect(events(writeForm(once))).toEqual(['onload', 'name']);
    expect(fieldImpact(once, names[1].key, [])).toEqual({ handlers: 1, required: false });
    expect(events(writeForm(removeField(once, names[1].key)))).toEqual(['onload']);
  });

  it('counts a footer control as still on the form', () => {
    const xml = OWNER_EVENT.replace('attribute="ownerid"', 'attribute="modifiedon"');
    const d = read(xml);
    const added = addField(d, sections(d)[2].key, attr('modifiedon', 'datetime'));
    const cell = cellOf(added, 'modifiedon').key;
    expect(fieldImpact(added, cell, [])).toEqual({ handlers: 0, required: false });
    expect(events(writeForm(removeField(added, cell)))).toEqual(['onload', 'name', 'modifiedon']);
  });
});

describe('containerImpact', () => {
  it('counts the cells of a tab or section and the handlers of columns placed only there', () => {
    const d = read();
    const [main, side, more] = sections(d);
    expect(containerImpact(d, d.tabs[0].key)).toEqual({ cells: 5, handlers: 1 });
    expect(containerImpact(d, main.key)).toEqual({ cells: 4, handlers: 1 });
    expect(containerImpact(d, side.key)).toEqual({ cells: 1, handlers: 0 });
    expect(containerImpact(d, more.key)).toEqual({ cells: 0, handlers: 0 });
    expect(containerImpact(d, d.tabs[1].key)).toEqual({ cells: 0, handlers: 0 });
    expect(containerImpact(d, 'nope')).toEqual({ cells: 0, handlers: 0 });
  });

  it('counts no handlers for a column that stays elsewhere on the form', () => {
    const inHeader = addHeaderField(read(), attr('name'));
    expect(containerImpact(inHeader, sections(inHeader)[0].key)).toEqual({ cells: 4, handlers: 0 });
    const d = read();
    const moved = placeCell(d, cellOf(d, 'name').key, sections(d)[2].key, 0);
    expect(containerImpact(moved, moved.tabs[0].key)).toEqual({ cells: 4, handlers: 0 });
    expect(containerImpact(moved, moved.tabs[1].key)).toEqual({ cells: 1, handlers: 1 });
  });
});

describe('header', () => {
  it('adds header fields with the header_ control id, up to HEADER_LIMIT', () => {
    expect(HEADER_LIMIT).toBe(4);
    let d = read();
    expect(addHeaderField(d, attr('ownerid', 'lookup'))).toBe(d);
    for (const name of ['statuscode', 'revenue', 'createdon']) {
      d = addHeaderField(d, attr(name, name === 'revenue' ? 'money' : 'choice'));
    }
    expect(d.header.map((c) => c.attribute)).toEqual(['ownerid', 'statuscode', 'revenue', 'createdon']);
    expect(addHeaderField(d, attr('fifth'))).toBe(d);
    const form = written(d);
    const header = form.getElementsByTagName('header')[0];
    expect(serialize(controlOf(header, 'header_revenue')!)).toBe(
      `<control id="header_revenue" classid="${CLASS_ID.money}" datafieldname="revenue" disabled="false"/>`,
    );
    expect(rowsOf(header)).toEqual([[expect.any(String), expect.any(String), expect.any(String), expect.any(String)]]);
    // A column in the header may also be in the body.
    expect(addHeaderField(read(), attr('name')).header).toHaveLength(2);
  });

  it('creates the header when the form has none', () => {
    const d = addHeaderField(read(SMALL), attr('ownerid', 'lookup'));
    const header = written(d).getElementsByTagName('header')[0];
    expect(controlOf(header, 'header_ownerid')!.getAttribute('classid')).toBe(CLASS_ID.lookup);
  });

  it('counts only field cells against the limit; moves and removes header fields', () => {
    const xml = RICH_FORM.replace('<cell id="{a0000000-0000-0000-0000-00000000000d}">',
      '<cell id="{a0000000-0000-0000-0000-0000000000aa}"><control id="wr" classid="{9FDF5F91-88B1-47F4-AD53-C11EFC01A01D}" /></cell>\n'
      + '        <cell id="{a0000000-0000-0000-0000-00000000000d}">');
    let d = read(xml);
    expect(d.header.map((c) => c.kind)).toEqual(['webresource', 'field']);
    for (const name of ['a', 'b', 'c']) d = addHeaderField(d, attr(name));
    expect(d.header).toHaveLength(5);
    expect(addHeaderField(d, attr('e'))).toBe(d);
    const owner = d.header[1].key;
    expect(moveHeaderField(d, owner, -1)).toBe(d);
    const right = moveHeaderField(d, owner, 1);
    expect(right.header.map((c) => c.attribute)).toEqual([undefined, 'a', 'ownerid', 'b', 'c']);
    expect(moveHeaderField(moveHeaderField(right, owner, 1), owner, -1).header).toEqual(right.header);
    expect(removeHeaderField(d, d.header[0].key)).toBe(d);
    const removed = removeHeaderField(d, owner);
    expect(removed.header.map((c) => c.attribute)).toEqual([undefined, 'a', 'b', 'c']);
    expect(controlOf(written(removed), 'header_ownerid')).toBeUndefined();
    expect(controlOf(written(removed), 'wr')).toBeDefined();
    const added = addHeaderField(read(xml), attr('z'));
    expect(writeForm(removeHeaderField(added, added.header[2].key))).toBe(xml);
  });

  it('setCell edits a header cell', () => {
    const d = read();
    const next = setCell(d, d.header[0].key, { label: 'Owner!' });
    expect(read(writeForm(next)).header[0].labels).toEqual({ 1033: 'Owner!' });
  });
});

describe('row breaks (ruling I1: an edit re-flows only the rows it touches)', () => {
  const cell = (id: string, extra = '') =>
    `<cell id="${id}"${extra}><control id="${id}" classid="${CLASS_ID.text}" datafieldname="${id}"/></cell>`;
  const row = (...cells: string[]) => (cells.length === 0 ? '<row/>' : `<row>${cells.join('')}</row>`);
  const form = (...sectionXml: string[]) =>
    `<form><tabs><tab name="t"><columns><column width="100%"><sections>${sectionXml.join('')}</sections></column></columns></tab></tabs></form>`;
  const section = (name: string, columns: string, ...rows: string[]) =>
    `<section name="${name}" columns="${columns}"><rows>${rows.join('')}</rows></section>`;
  const rowsIn = (d: FormDesign, name = 's') => rowsOf(sectionEl(written(d), name));
  const keyOf = (d: FormDesign, id: string) => sections(d).flatMap((s) => s.cells).find((c) => c.id === id)!.key;

  it('remove: the later cells of that row shift left, other rows stay', () => {
    const d = read(form(section('s', '11', row(cell('a'), cell('b')), row(cell('c'), cell('d')))));
    const next = removeField(d, keyOf(d, 'a'));
    expect(rowsIn(next)).toEqual([['b'], ['c', 'd']]);
    expect(read(writeForm(next)).tabs[0].columns[0].sections[0].cells.map((c) => c.id)).toEqual(['b', 'c', 'd']);
    expect(rowsIn(removeField(d, keyOf(d, 'c')))).toEqual([['a', 'b'], ['d']]);
  });

  it('remove: a row left empty goes, but an empty padding row of the source stays', () => {
    const d = read(form(section('s', '11', row(cell('a')), row(cell('b'), cell('c')))));
    expect(rowsIn(removeField(d, keyOf(d, 'a')))).toEqual([['b', 'c']]);
    const padded = read(form(section('s', '11', row(cell('g', ' rowspan="2"'), cell('a')), row(), row(cell('b')))));
    expect(rowsIn(removeField(padded, keyOf(padded, 'b')))).toEqual([['g', 'a'], []]);
  });

  it('add: the first free slot of the last row, else a new row; the short rows before stay short', () => {
    const short = read(form(section('s', '11', row(cell('a')), row(cell('b'), cell('c')))));
    const s1 = sections(short)[0].key;
    const once = addField(short, s1, attr('z'));
    expect(rowsIn(once).map((r) => r.length)).toEqual([1, 2, 1]);
    expect(rowsIn(once).slice(0, 2)).toEqual([['a'], ['b', 'c']]);
    const open = read(form(section('s', '11', row(cell('a')), row(cell('b')))));
    const twice = addField(open, sections(open)[0].key, attr('z'));
    expect(rowsIn(twice).map((r) => r.length)).toEqual([1, 2]);
    expect(rowsIn(twice)[0]).toEqual(['a']);
    // A removal and an add in one save: still no re-pack.
    const full = read(form(section('s', '11', row(cell('a'), cell('b')), row(cell('c'), cell('d')))));
    const both = addField(removeField(full, keyOf(full, 'a')), sections(full)[0].key, attr('z'));
    expect(rowsIn(both).map((r) => r.length)).toEqual([1, 2, 1]);
    expect(rowsIn(both).slice(0, 2)).toEqual([['b'], ['c', 'd']]);
  });

  it('move: a swap inside the section keeps the row breaks', () => {
    const d = read(form(section('s', '11', row(cell('a'), cell('b')), row(cell('c'), cell('d')), row(cell('e')))));
    expect(rowsIn(moveCell(d, keyOf(d, 'b'), 'down'))).toEqual([['a', 'c'], ['b', 'd'], ['e']]);
    expect(rowsIn(moveCell(d, keyOf(d, 'd'), 'down'))).toEqual([['a', 'b'], ['c', 'e'], ['d']]);
  });

  it('move: across sections only the rows the cell leaves and enters change', () => {
    const d = read(form(
      section('s', '11', row(cell('a'), cell('b')), row(cell('c'), cell('d'))),
      section('u', '11', row(cell('x')), row(cell('y'), cell('w'))),
    ));
    const down = moveCell(d, keyOf(d, 'd'), 'next-section');
    expect(rowsIn(down)).toEqual([['a', 'b'], ['c']]);
    expect(rowsIn(down, 'u')).toEqual([['d', 'x'], ['y', 'w']]);
    const up = moveCell(d, keyOf(d, 'x'), 'prev-section');
    expect(rowsIn(up)).toEqual([['a', 'b'], ['c', 'd'], ['x']]);
    expect(rowsIn(up, 'u')).toEqual([['y', 'w']]);
  });

  it('respects the slots a tall cell keeps busy', () => {
    const d = read(form(section('s', '11', row(cell('g', ' rowspan="2"'), cell('a')), row(cell('b')), row(cell('c'), cell('e')))));
    const s1 = sections(d)[0].key;
    expect(rowsIn(removeField(d, keyOf(d, 'a')))).toEqual([['g'], ['b'], ['c', 'e']]);
    const added = addField(removeField(d, keyOf(d, 'e')), s1, attr('z'));
    expect(rowsIn(added).map((r) => r.length)).toEqual([2, 1, 2]);
    // b sits beside the tall cell; a new cell after b does not fit in that row.
    const grow = read(form(section('s', '11', row(cell('g', ' rowspan="2"'), cell('a')), row(cell('b')))));
    expect(rowsIn(addField(grow, sections(grow)[0].key, attr('z'))).map((r) => r.length)).toEqual([2, 1, 1]);
  });

  it('clamps an absurd rowspan to 100', () => {
    const d = read(form(section('s', '1', row(cell('g', ' rowspan="1000000"')), row(cell('a')))));
    expect(sections(d)[0].cells[0].rowSpan).toBe(100);
    const rows = rowsIn(removeField(d, keyOf(d, 'a')));
    expect(rows.length).toBeLessThanOrEqual(100);
    expect(written(removeField(d, keyOf(d, 'a'))).getElementsByTagName('cell')[0].getAttribute('rowspan')).toBe('1000000');
  });

  it('keeps 4 header fields in one row, widening a 3-column header', () => {
    let d = read(RICH_FORM.replace('columns="1111" celllabelposition', 'columns="111" celllabelposition'));
    for (const name of ['a', 'b', 'c']) d = addHeaderField(d, attr(name));
    const header = written(d).getElementsByTagName('header')[0];
    expect(rowsOf(header)).toHaveLength(1);
    expect(rowsOf(header)[0]).toHaveLength(4);
    expect(header.getAttribute('columns')).toBe('1111');
    const three = addHeaderField(read(RICH_FORM.replace('columns="1111" celllabelposition', 'columns="111" celllabelposition')), attr('a'));
    expect(written(three).getElementsByTagName('header')[0].getAttribute('columns')).toBe('111');
  });

  it('keeps a label of another language that has no description when a label is edited (F9)', () => {
    const d = read('<form><tabs><tab name="t"><labels><label description="A" languagecode="1033"/>'
      + '<label languagecode="1040"/></labels></tab></tabs></form>');
    const next = setTab(d, d.tabs[0].key, { label: 'C' });
    const labels = Array.from(written(next).getElementsByTagName('label'));
    expect(labels.map((l) => l.getAttribute('languagecode'))).toEqual(['1033', '1040']);
  });
});

describe('positional moves (UI-2g)', () => {
  const cell = (id: string) => `<cell id="${id}"><control id="${id}" classid="${CLASS_ID.text}" datafieldname="${id}"/></cell>`;
  const row = (...cells: string[]) => `<row>${cells.join('')}</row>`;
  const section = (name: string, ...rows: string[]) => `<section name="${name}" columns="11"><rows>${rows.join('')}</rows></section>`;
  const column = (...sectionXml: string[]) => `<column width="50%"><sections>${sectionXml.join('')}</sections></column>`;
  const tab = (name: string, ...columnXml: string[]) => `<tab name="${name}"><columns>${columnXml.join('')}</columns></tab>`;
  const FORM = `<form><tabs>${tab('t1',
    column(section('s', row(cell('a'), cell('b')), row(cell('c'), cell('d'))), section('u', row(cell('x')), row(cell('y'), cell('w')))),
    column(section('v', row(cell('p')))))}${tab('t2', column(section('z', row(cell('q')))))}${tab('t3', column(section('k', row(cell('r')))))}</tabs>`
    + `<header columns="1111"><rows><row>${['h1', 'h2', 'h3'].map(cell).join('')}</row></rows></header></form>`;
  const d0 = read(FORM);
  const cells = (d: FormDesign, name: string) => sections(d).find((s) => s.name === name)!.cells.map((c) => c.id);
  const key = (d: FormDesign, id: string) => sections(d).flatMap((s) => s.cells).find((c) => c.id === id)!.key;
  const sectionKey = (d: FormDesign, name: string) => sections(d).find((s) => s.name === name)!.key;
  const rowsIn = (d: FormDesign, name: string) => rowsOf(sectionEl(written(d), name));

  it('placeCell goes to the first, middle and last position, counted after removal', () => {
    const s = sectionKey(d0, 's');
    expect(cells(placeCell(d0, key(d0, 'c'), s, 0), 's')).toEqual(['c', 'a', 'b', 'd']);
    expect(cells(placeCell(d0, key(d0, 'a'), s, 2), 's')).toEqual(['b', 'c', 'a', 'd']);
    expect(cells(placeCell(d0, key(d0, 'a'), s, 3), 's')).toEqual(['b', 'c', 'd', 'a']);
    expect(cells(placeCell(d0, key(d0, 'a'), s, 99), 's')).toEqual(['b', 'c', 'd', 'a']);
    expect(cells(placeCell(d0, key(d0, 'd'), s, -5), 's')).toEqual(['d', 'a', 'b', 'c']);
    const u = sectionKey(d0, 'u');
    expect(cells(placeCell(d0, key(d0, 'a'), u, 0), 'u')).toEqual(['a', 'x', 'y', 'w']);
    expect(cells(placeCell(d0, key(d0, 'a'), u, 1), 'u')).toEqual(['x', 'a', 'y', 'w']);
    expect(cells(placeCell(d0, key(d0, 'a'), u, 3), 'u')).toEqual(['x', 'y', 'w', 'a']);
    expect(cells(placeCell(d0, key(d0, 'a'), u, 3), 's')).toEqual(['b', 'c', 'd']);
    expect(cells(placeCell(d0, key(d0, 'q'), s, 1), 's')).toEqual(['a', 'q', 'b', 'c', 'd']);
  });

  it('placeCell returns d for a drop at its own position or an unknown key', () => {
    const s = sectionKey(d0, 's');
    expect(placeCell(d0, key(d0, 'a'), s, 0)).toBe(d0);
    expect(placeCell(d0, key(d0, 'c'), s, 2)).toBe(d0);
    expect(placeCell(d0, key(d0, 'd'), s, 3)).toBe(d0);
    expect(placeCell(d0, key(d0, 'd'), s, 99)).toBe(d0);
    expect(placeCell(d0, 'nope', s, 0)).toBe(d0);
    expect(placeCell(d0, key(d0, 'a'), 'nope', 0)).toBe(d0);
  });

  it('placeCell across sections keeps the source rows, like moveCell', () => {
    const viaMove = moveCell(d0, key(d0, 'd'), 'next-section');
    const viaPlace = placeCell(d0, key(d0, 'd'), sectionKey(d0, 'u'), 0);
    expect(writeForm(viaPlace)).toBe(writeForm(viaMove));
    expect(rowsIn(viaPlace, 's')).toEqual([['a', 'b'], ['c']]);
    const upMove = moveCell(d0, key(d0, 'x'), 'prev-section');
    const upPlace = placeCell(d0, key(d0, 'x'), sectionKey(d0, 's'), 4);
    expect(writeForm(upPlace)).toBe(writeForm(upMove));
    expect(rowsIn(upPlace, 'u')).toEqual([['y', 'w']]);
  });

  it('placeCell there and back gives the original string', () => {
    const original = writeForm(d0);
    const there = placeCell(d0, key(d0, 'b'), sectionKey(d0, 'u'), 1);
    expect(writeForm(there)).not.toBe(original);
    const back = placeCell(there, key(there, 'b'), sectionKey(there, 's'), 1);
    expect(writeForm(back)).toBe(original);
  });

  it('placeSection goes to the first, middle and last position of a column and of another column of its tab', () => {
    const names = (d: FormDesign, tabIndex = 0) => d.tabs[tabIndex].columns.map((c) => c.sections.map((s) => s.name));
    const c1 = d0.tabs[0].columns[0].key;
    const c2 = d0.tabs[0].columns[1].key;
    expect(names(placeSection(d0, sectionKey(d0, 'u'), c1, 0))).toEqual([['u', 's'], ['v']]);
    expect(names(placeSection(d0, sectionKey(d0, 's'), c1, 1))).toEqual([['u', 's'], ['v']]);
    expect(names(placeSection(d0, sectionKey(d0, 's'), c2, 0))).toEqual([['u'], ['s', 'v']]);
    expect(names(placeSection(d0, sectionKey(d0, 's'), c2, 1))).toEqual([['u'], ['v', 's']]);
    expect(names(placeSection(d0, sectionKey(d0, 's'), c2, 99))).toEqual([['u'], ['v', 's']]);
    expect(names(placeSection(d0, sectionKey(d0, 'v'), c1, 1))).toEqual([['s', 'v', 'u'], []]);
  });

  it('placeSection returns d at its own position, for an unknown key and for another tab (G1)', () => {
    const c1 = d0.tabs[0].columns[0].key;
    expect(placeSection(d0, sectionKey(d0, 's'), c1, 0)).toBe(d0);
    expect(placeSection(d0, sectionKey(d0, 'u'), c1, 1)).toBe(d0);
    expect(placeSection(d0, sectionKey(d0, 'u'), c1, 99)).toBe(d0);
    expect(placeSection(d0, 'nope', c1, 0)).toBe(d0);
    expect(placeSection(d0, sectionKey(d0, 's'), 'nope', 0)).toBe(d0);
    expect(placeSection(d0, sectionKey(d0, 's'), d0.tabs[1].columns[0].key, 0)).toBe(d0);
    expect(placeSection(d0, sectionKey(d0, 'z'), c1, 0)).toBe(d0);
  });

  it('placeTab goes to the first, middle and last position and returns d at its own', () => {
    const order = (d: FormDesign) => d.tabs.map((t) => t.name);
    const [t1, t2, t3] = d0.tabs.map((t) => t.key);
    expect(order(placeTab(d0, t3, 0))).toEqual(['t3', 't1', 't2']);
    expect(order(placeTab(d0, t1, 1))).toEqual(['t2', 't1', 't3']);
    expect(order(placeTab(d0, t1, 2))).toEqual(['t2', 't3', 't1']);
    expect(order(placeTab(d0, t1, 99))).toEqual(['t2', 't3', 't1']);
    expect(placeTab(d0, t1, 0)).toBe(d0);
    expect(placeTab(d0, t2, 1)).toBe(d0);
    expect(placeTab(d0, t3, 2)).toBe(d0);
    expect(placeTab(d0, 'nope', 0)).toBe(d0);
  });

  it('placeHeaderField goes to the first, middle and last position and returns d at its own', () => {
    const order = (d: FormDesign) => d.header.map((c) => c.id);
    const [h1, h2, h3] = ['h1', 'h2', 'h3'].map((id) => d0.header.find((c) => c.id === id)!.key);
    expect(order(placeHeaderField(d0, h3, 0))).toEqual(['h3', 'h1', 'h2']);
    expect(order(placeHeaderField(d0, h1, 1))).toEqual(['h2', 'h1', 'h3']);
    expect(order(placeHeaderField(d0, h1, 2))).toEqual(['h2', 'h3', 'h1']);
    expect(order(placeHeaderField(d0, h1, 99))).toEqual(['h2', 'h3', 'h1']);
    expect(placeHeaderField(d0, h1, 0)).toBe(d0);
    expect(placeHeaderField(d0, h2, 1)).toBe(d0);
    expect(placeHeaderField(d0, h3, 2)).toBe(d0);
    expect(placeHeaderField(d0, 'nope', 0)).toBe(d0);
  });
});

describe('UI-2g follow-ups (#274)', () => {
  it('a tall cell moved into another section gets no padding rows (H8, as Dataverse)', () => {
    const cell = (id: string, extra = '') =>
      `<cell id="${id}"${extra}><control id="${id}" classid="${CLASS_ID.text}" datafieldname="${id}"/></cell>`;
    const d = read('<form><tabs><tab name="t"><columns><column width="100%"><sections>'
      + `<section name="s" columns="1"><rows><row>${cell('g', ' rowspan="3"')}</row><row/><row/><row>${cell('a')}</row></rows></section>`
      + `<section name="u" columns="1"><rows><row>${cell('x')}</row></rows></section>`
      + '</sections></column></columns></tab></tabs></form>');
    const g = sections(d)[0].cells.find((c) => c.id === 'g')!;
    const moved = placeCell(d, g.key, sections(d)[1].key, 99);
    const rows = childElements(childElements(sectionEl(written(moved), 'u'), 'rows')[0], 'row');
    expect(rows.map((r) => childElements(r, 'cell').map((c) => c.getAttribute('id')))).toEqual([['x'], ['g']]);
  });

  it('a 2-column-wide cell moved into a 1-column section is written with colspan 1', () => {
    const wide = `<cell id="w" colspan="2"><control id="w" classid="${CLASS_ID.text}" datafieldname="w"/></cell>`;
    const x = `<cell id="x"><control id="x" classid="${CLASS_ID.text}" datafieldname="x"/></cell>`;
    const d = read('<form><tabs><tab name="t"><columns><column width="100%"><sections>'
      + `<section name="two" columns="2"><rows><row>${wide}</row></rows></section>`
      + `<section name="one" columns="1"><rows><row>${x}</row></rows></section>`
      + '</sections></column></columns></tab></tabs></form>');
    const key = sections(d)[0].cells[0].key;
    const colspanOf = (e: FormDesign) =>
      Array.from(written(e).getElementsByTagName('cell')).find((c) => c.getAttribute('id') === 'w')!.getAttribute('colspan');
    expect(colspanOf(d)).toBe('2');
    const placed = placeCell(d, key, sections(d)[1].key, 0);
    const moved = moveCell(d, key, 'next-section');
    for (const e of [placed, moved]) {
      expect(sections(e)[1].cells.map((c) => c.id)).toContain('w');
      expect(colspanOf(e)).toBe('1');
    }
    // A clamp is a design edit: moving the cell back does not restore the 2.
    expect(colspanOf(placeCell(placed, key, sections(placed)[0].key, 0))).toBe('1');
    expect(colspanOf(moveCell(moved, key, 'prev-section'))).toBe('1');
  });

  it('a re-flowed section of an indented form is re-indented; untouched sections keep their text', () => {
    const d = read();
    const out = writeForm(placeCell(d, cellOf(d, 'name').key, sections(d)[2].key, 0));
    const form = parseXml(out).documentElement;
    const indentOf = (el: Element) => /[^\n]*$/.exec(el.previousSibling!.nodeValue!)![0];
    for (const name of ['main', 'more']) {
      const rows = childElements(sectionEl(form, name), 'rows')[0];
      const own = indentOf(rows);
      expect(own).toBe('              ');
      for (const row of childElements(rows, 'row')) {
        expect(row.previousSibling?.nodeValue).toBe(`\n${own}  `);
        for (const c of childElements(row, 'cell')) expect(c.previousSibling?.nodeValue).toBe(`\n${own}    `);
        expect(row.lastChild?.nodeValue).toBe(`\n${own}  `);
      }
      expect(rows.lastChild?.nodeValue).toBe(`\n${own}`);
    }
    const source = parseXml(RICH_FORM).documentElement;
    expect(serialize(sectionEl(form, 'side'))).toBe(serialize(sectionEl(source, 'side')));
    for (const tag of ['header', 'footer', 'events']) expect(blocksOf(out, tag)).toEqual(blocksOf(RICH_FORM, tag));
    // The moved cell keeps its own text.
    const moved = (f: Element) => Array.from(f.getElementsByTagName('cell')).find((c) => c.getAttribute('id')?.endsWith('03}'))!;
    expect(serialize(moved(form))).toBe(serialize(moved(source)));
  });
});
