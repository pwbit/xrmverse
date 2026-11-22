import { describe, expect, it } from 'vitest';
import { CLASS_ID } from '../../parsers/controlClass';
import { addSection, setTab } from './formDesign';
import { parseFormXml } from '../../parsers/formXml';
import { childElements, parseXml } from '../../parsers/xml';
import type { Cell, Control } from '../../model/form';
import {
  addQuickView, addSubgrid, addWebResource, readControl, setControl, uniqueControlId, validPerPage, webResourceControlId, writeControl,
  type QuickViewSpec, type SubgridSpec, type WebResourceSpec,
} from './formControls';
import { readForm, writeForm, type DesignCell, type FormDesign } from './formDesign';
import { formRow } from './formDesignTestSupport';

const GUID = /^\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}$/;
const serialize = (n: Node) => new XMLSerializer().serializeToString(n);

const FORM = `<form>
  <tabs>
    <tab name="general" id="{a0000000-0000-0000-0000-000000000001}">
      <labels>
        <label description="General" languagecode="1033" />
      </labels>
      <columns>
        <column width="100%">
          <sections>
            <section name="main" columns="2" id="{a0000000-0000-0000-0000-000000000002}">
              <labels>
                <label description="Main" languagecode="1033" />
              </labels>
              <rows>
                <row>
                  <cell id="{a0000000-0000-0000-0000-000000000003}">
                    <labels>
                      <label description="Name" languagecode="1033" />
                    </labels>
                    <control id="name" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="name" />
                  </cell>
                  <cell id="{a0000000-0000-0000-0000-000000000004}" rowspan="3" colspan="1" auto="false">
                    <labels>
                      <label description="Contacts" languagecode="1033" />
                    </labels>
                    <control id="Contact" classid="{E7A81278-8635-4d9e-8D4D-59480B391C5B}" indicationOfSubgrid="true">
                      <parameters>
                        <ViewId>{00000000-0000-0000-00aa-000010001003}</ViewId>
                        <RelationshipName>contact_customer_accounts</RelationshipName>
                        <TargetEntityType>contact</TargetEntityType>
                        <EnableViewPicker>false</EnableViewPicker>
                        <RecordsPerPage>8</RecordsPerPage>
                        <MakerExtra>keep me</MakerExtra>
                      </parameters>
                    </control>
                  </cell>
                </row>
                <row>
                  <cell id="{a0000000-0000-0000-0000-000000000005}">
                    <labels>
                      <label description="Primary contact" languagecode="1033" />
                    </labels>
                    <control id="contactcard" classid="{5C5600E0-1D6E-4205-A272-BE80DA87FD42}" datafieldname="primarycontactid">
                      <parameters>
                        <QuickForms>&lt;QuickForms&gt;&lt;QuickFormIds&gt;&lt;QuickFormId entityname="contact"&gt;{33333333-0000-0000-0000-000000000001}&lt;/QuickFormId&gt;&lt;/QuickFormIds&gt;&lt;/QuickForms&gt;</QuickForms>
                      </parameters>
                    </control>
                  </cell>
                </row>
                <row>
                  <cell id="{a0000000-0000-0000-0000-000000000006}">
                    <labels>
                      <label description="Map" languagecode="1033" />
                    </labels>
                    <control id="WebResource_map" classid="{9FDF5F91-88B1-47F4-AD53-C11EFC01A01D}">
                      <parameters>
                        <Url>new_map.html</Url>
                        <PassParameters>true</PassParameters>
                        <Security>false</Security>
                        <Scrolling>never</Scrolling>
                        <Border>false</Border>
                        <WebResourceId>{44444444-0000-0000-0000-000000000001}</WebResourceId>
                      </parameters>
                    </control>
                  </cell>
                </row>
                <row>
                  <cell id="{a0000000-0000-0000-0000-000000000007}">
                    <labels>
                      <label description="Site" languagecode="1033" />
                    </labels>
                    <control id="IFRAME_site" classid="{FD2A7985-3187-444E-908D-6624B21F69C0}">
                      <parameters>
                        <Url>https://example.com</Url>
                      </parameters>
                    </control>
                  </cell>
                  <cell id="{a0000000-0000-0000-0000-000000000008}">
                    <labels>
                      <label description="Timeline" languagecode="1033" />
                    </labels>
                    <control id="notescontrol" classid="{06375649-C143-495E-A496-C962E5B4488E}" />
                  </cell>
                </row>
              </rows>
            </section>
          </sections>
        </column>
      </columns>
    </tab>
  </tabs>
  <footer>
    <rows>
      <row>
        <cell id="{a0000000-0000-0000-0000-000000000009}">
          <control id="salesorder" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="salesorder" />
        </cell>
      </row>
    </rows>
  </footer>
</form>`;

const read = (xml = FORM) => readForm(formRow(xml), 1033);
const section = (d: FormDesign) => d.tabs[0].columns[0].sections[0];
const sectionsOf = (d: FormDesign) => d.tabs[0].columns[0].sections;
const cellById = (d: FormDesign, id: string) => section(d).cells.find((c) => c.id === id)!;
const lastCell = (d: FormDesign): DesignCell => section(d).cells[section(d).cells.length - 1];
/** The `<cell>` element `id` of the written FormXML, serialized with its id replaced by `{ID}`. */
const writtenCell = (xml: string, id: string) => {
  const el = Array.from(parseXml(xml).getElementsByTagName('cell')).find((c) => c.getAttribute('id') === id)!;
  return serialize(el).replace(id, '{ID}');
};
/** The runtime control `id` that `parseFormXml` reads from `xml`. */
const runtimeControl = (xml: string, id: string): Control | undefined => {
  const model = parseFormXml({ systemformid: 'f1', name: 'F', objecttypecode: 'account', type: 2, formxml: xml });
  const cells: Cell[] = model.tabs.flatMap((t) => t.columns.flatMap((c) => c.sections.flatMap((s) => s.rows.flat())));
  return cells.map((c) => c.control).find((c) => c?.id === id);
};

const SUBGRID: SubgridSpec = {
  kind: 'subgrid', controlId: '', relationship: 'order_customer_accounts', targetEntity: 'salesorder',
  viewId: '{00000000-0000-0000-00aa-000010001001}', recordsPerPage: 8, viewPicker: false,
};
const QUICK_VIEW: QuickViewSpec = {
  kind: 'quickview', controlId: '', lookup: 'parentaccountid', forms: { account: '{55555555-0000-0000-0000-000000000001}' },
};
const WEB_RESOURCE: WebResourceSpec = {
  kind: 'webresource', controlId: '', url: 'new_/maps/chart.svg', webResourceId: '66666666-0000-0000-0000-000000000001',
  data: '', passParameters: false, restrictScripting: false, scrolling: 'auto', border: true,
};

describe('readControl', () => {
  it('reads subgrid, quick view and web resource cells into specs; IFRAME and timeline cells keep none', () => {
    const d = read();
    expect(cellById(d, '{a0000000-0000-0000-0000-000000000004}').control).toEqual({
      kind: 'subgrid', controlId: 'Contact', relationship: 'contact_customer_accounts', targetEntity: 'contact',
      viewId: '{00000000-0000-0000-00aa-000010001003}', recordsPerPage: 8, viewPicker: false,
    });
    expect(cellById(d, '{a0000000-0000-0000-0000-000000000005}').control).toEqual({
      kind: 'quickview', controlId: 'contactcard', lookup: 'primarycontactid',
      forms: { contact: '{33333333-0000-0000-0000-000000000001}' },
    });
    expect(cellById(d, '{a0000000-0000-0000-0000-000000000006}').control).toEqual({
      kind: 'webresource', controlId: 'WebResource_map', url: 'new_map.html', webResourceId: '{44444444-0000-0000-0000-000000000001}',
      data: '', passParameters: true, restrictScripting: false, scrolling: 'no', border: false,
    });
    expect(cellById(d, '{a0000000-0000-0000-0000-000000000007}')).toMatchObject({ kind: 'webresource' });
    expect(cellById(d, '{a0000000-0000-0000-0000-000000000007}').control).toBeUndefined();
    expect(cellById(d, '{a0000000-0000-0000-0000-000000000008}').control).toBeUndefined();
    expect(cellById(d, '{a0000000-0000-0000-0000-000000000003}').control).toBeUndefined();
  });

  it('returns undefined for an IFRAME, the timeline and an unknown control', () => {
    const el = (xml: string) => parseXml(xml).documentElement;
    expect(readControl(el(`<control id="i" classid="${CLASS_ID.iframe}"/>`))).toBeUndefined();
    expect(readControl(el(`<control id="t" classid="${CLASS_ID.timeline}"/>`))).toBeUndefined();
    expect(readControl(el('<control id="u" classid="{00000000-0000-0000-0000-000000000000}"/>'))).toBeUndefined();
    expect(readControl(el(`<control id="f" classid="${CLASS_ID.text}" datafieldname="name"/>`))).toBeUndefined();
  });
});

describe('writeForm with controls', () => {
  it('gives back the same string for an unedited form with all three kinds', () => {
    expect(writeForm(read())).toBe(FORM);
  });
});

describe('uniqueControlId', () => {
  it('adds _2, _3 when the name is taken anywhere on the form, ignoring case', () => {
    const d = read();
    expect(uniqueControlId(d, 'account')).toBe('account');
    expect(uniqueControlId(d, 'contact')).toBe('contact_2');
    expect(uniqueControlId(d, 'NAME')).toBe('NAME_2');
    expect(uniqueControlId(d, 'salesorder')).toBe('salesorder_2');
  });

  it('counts the controls the design added but has not written yet', () => {
    const d = addSubgrid(read(), section(read()).key, { ...SUBGRID, targetEntity: 'contact' }, 'C');
    expect(lastCell(d).control).toMatchObject({ controlId: 'contact_2' });
    const d2 = addSubgrid(d, section(d).key, { ...SUBGRID, targetEntity: 'contact' }, 'C');
    expect(lastCell(d2).control).toMatchObject({ controlId: 'contact_3' });
  });
});

describe('addSubgrid', () => {
  it('writes the cell and control of G2, in that parameter order', () => {
    const d = addSubgrid(read(), section(read()).key, SUBGRID, 'Orders');
    const cell = lastCell(d);
    expect(cell).toMatchObject({ kind: 'subgrid', rowSpan: 8, colSpan: 2, labels: { 1033: 'Orders' }, showLabel: true, visible: true });
    expect(cell.id).toMatch(GUID);
    expect(writtenCell(writeForm(d), cell.id)).toBe([
      '<cell id="{ID}" showlabel="true" rowspan="8" colspan="2" auto="false">',
      '                    <labels>',
      '                      <label description="Orders" languagecode="1033"/>',
      '                    </labels>',
      `                    <control id="salesorder_2" classid="${CLASS_ID.subgrid}" indicationOfSubgrid="true">`,
      '                      <parameters>',
      '                        <ViewId>{00000000-0000-0000-00aa-000010001001}</ViewId>',
      '                        <IsUserView>false</IsUserView>',
      '                        <RelationshipName>order_customer_accounts</RelationshipName>',
      '                        <TargetEntityType>salesorder</TargetEntityType>',
      '                        <AutoExpand>Fixed</AutoExpand>',
      '                        <EnableQuickFind>false</EnableQuickFind>',
      '                        <EnableViewPicker>false</EnableViewPicker>',
      '                        <EnableJumpBar>false</EnableJumpBar>',
      '                        <ChartGridMode>Grid</ChartGridMode>',
      '                        <VisualizationId/>',
      '                        <IsUserChart>false</IsUserChart>',
      '                        <EnableChartPicker>false</EnableChartPicker>',
      '                        <RecordsPerPage>8</RecordsPerPage>',
      '                      </parameters>',
      '                    </control>',
      '                  </cell>',
    ].join('\n'));
  });

  it('parses back into the runtime SubgridControl, with an empty relationship for All records', () => {
    const d = addSubgrid(read(), section(read()).key, { ...SUBGRID, relationship: '', recordsPerPage: 4, viewPicker: true }, 'All');
    const xml = writeForm(d);
    expect(xml).toContain('<RelationshipName/>');
    expect(runtimeControl(xml, 'salesorder_2')).toEqual({
      kind: 'subgrid', id: 'salesorder_2', relationship: '', targetEntity: 'salesorder',
      viewId: '{00000000-0000-0000-00aa-000010001001}', recordsPerPage: 4, enableViewPicker: true, chartGridMode: 'Grid', pickers: true,
    });
    expect(lastCell(d).rowSpan).toBe(4);
    expect(lastCell(readForm(formRow(xml), 1033)).control).toEqual({ ...SUBGRID, controlId: 'salesorder_2', relationship: '', recordsPerPage: 4, viewPicker: true });
  });

  it('puts the label in the UI language, and is a no-op for an unknown section', () => {
    const it = readForm(formRow(FORM), 1040);
    expect(lastCell(addSubgrid(it, section(it).key, SUBGRID, 'Ordini')).labels).toEqual({ 1040: 'Ordini' });
    const d = read();
    expect(addSubgrid(d, 'nope', SUBGRID, 'X')).toBe(d);
  });
});

describe('a new subgrid is written as Dataverse writes it', () => {
  const rowIds = (xml: string) => {
    const main = Array.from(parseXml(xml).getElementsByTagName('section')).find((s) => s.getAttribute('name') === 'main')!;
    const rows = childElements(main, 'rows')[0];
    return { rows, ids: childElements(rows, 'row').map((r) => childElements(r, 'cell').map((c) => c.getAttribute('id'))) };
  };

  it('goes in a new row at the end of the section, with no padding rows, and rowspan at most 100', () => {
    const d = addSubgrid(read(), section(read()).key, { ...SUBGRID, recordsPerPage: 250 }, 'Orders');
    const cell = lastCell(d);
    expect(cell.rowSpan).toBe(100);
    expect(cell.control).toMatchObject({ recordsPerPage: 250 });
    const xml = writeForm(d);
    const { rows, ids } = rowIds(xml);
    const source = rowIds(FORM);
    expect(ids).toEqual([...source.ids, [cell.id]]);
    const rowEls = childElements(rows, 'row');
    expect(rowEls[rowEls.length - 1].nextElementSibling).toBeNull();
    const cellsOf = (rs: Element) => childElements(rs, 'row').map((r) => childElements(r, 'cell').map(serialize));
    expect(cellsOf(rows).slice(0, -1)).toEqual(cellsOf(source.rows));
    const cellEl = childElements(rowEls[rowEls.length - 1], 'cell')[0];
    expect([cellEl.getAttribute('rowspan'), cellEl.getAttribute('colspan'), cellEl.getAttribute('auto')]).toEqual(['100', '2', 'false']);
    expect(xml).toContain('<RecordsPerPage>250</RecordsPerPage>');
    expect(writeForm(readForm(formRow(xml), 1033))).toBe(xml);
  });

  it('goes after the source padding rows of a tall cell, and keeps them', () => {
    const d = addSubgrid(read(), section(read()).key, { ...SUBGRID, recordsPerPage: 8 }, 'Orders');
    const { ids } = rowIds(writeForm(d));
    expect(ids[ids.length - 1]).toEqual([lastCell(d).id]);
    const tall = `<form><tabs><tab name="t"><columns><column width="100%"><sections><section name="main" columns="11"><rows>`
      + `<row><cell id="a" rowspan="2"><control id="a" classid="${CLASS_ID.text}" datafieldname="a"/></cell></row>`
      + '<row/></rows></section></sections></column></columns></tab></tabs></form>';
    const t = read(tall);
    const out = rowIds(writeForm(addSubgrid(t, section(t).key, SUBGRID, 'O'))).ids;
    expect(out.slice(0, 2)).toEqual([['a'], []]);
    expect(out).toHaveLength(3);
    expect(out[2]).toHaveLength(1);
  });

  it('pads nothing after a new subgrid in a new section', () => {
    const d = addSection(read(), read().tabs[0].key, 0);
    const added = sectionsOf(d)[1];
    const withGrid = addSubgrid(d, added.key, SUBGRID, 'O');
    const el = Array.from(parseXml(writeForm(withGrid)).getElementsByTagName('section')).find((s) => s.getAttribute('name') === added.name)!;
    const rows = childElements(childElements(el, 'rows')[0], 'row');
    expect(rows.map((r) => childElements(r, 'cell').length)).toEqual([1]);
  });
});

describe('the model clamps its input', () => {
  it('clamps records per page to 1–250, NaN to 4, in addSubgrid and setControl', () => {
    const d = read();
    const per = (n: number) => (lastCell(addSubgrid(d, section(d).key, { ...SUBGRID, recordsPerPage: n }, 'O')).control as SubgridSpec).recordsPerPage;
    expect([per(0), per(-3), per(999), per(Number.NaN), per(7.6)]).toEqual([1, 1, 250, 4, 8]);
    const id = '{a0000000-0000-0000-0000-000000000004}';
    const sub = cellById(d, id).control as SubgridSpec;
    const set = (n: number) => (cellById(setControl(d, cellById(d, id).key, { ...sub, recordsPerPage: n }), id).control as SubgridSpec).recordsPerPage;
    expect([set(300), set(Number.NaN), set(0)]).toEqual([250, 4, 1]);
  });

  it('returns d for an empty target table or lookup', () => {
    const d = read();
    expect(addSubgrid(d, section(d).key, { ...SUBGRID, targetEntity: ' ' }, 'O')).toBe(d);
    expect(addQuickView(d, section(d).key, { ...QUICK_VIEW, lookup: '' }, 'Q')).toBe(d);
  });
});

describe('a malformed QuickForms text', () => {
  const broken = FORM.replace(
    /<QuickForms>&lt;QuickForms&gt;.*<\/QuickForms>/,
    '<QuickForms>&lt;QuickForms&gt;&lt;QuickFormIds&gt;&lt;broken</QuickForms>',
  );

  it('leaves the quick view an untouched control instead of making the form unreadable', () => {
    expect(broken).not.toBe(FORM);
    const el = parseXml(broken).getElementsByTagName('control')[2];
    expect(readControl(el)).toBeUndefined();
    const d = read(broken);
    const cell = cellById(d, '{a0000000-0000-0000-0000-000000000005}');
    expect(cell.kind).toBe('quickview');
    expect(cell.control).toBeUndefined();
    expect(writeForm(d)).toBe(broken);
    const edited = writeForm(setTab(d, d.tabs[0].key, { label: 'X' }));
    expect(edited).toContain('<QuickForms>&lt;QuickForms&gt;&lt;QuickFormIds&gt;&lt;broken</QuickForms>');
  });
});

describe('addQuickView', () => {
  it('writes the control of G3 with the escaped QuickForms text', () => {
    const d = addQuickView(read(), section(read()).key, QUICK_VIEW, 'Parent');
    const cell = lastCell(d);
    expect(cell).toMatchObject({ kind: 'quickview', colSpan: 1, rowSpan: 1, labels: { 1033: 'Parent' } });
    const xml = writeForm(d);
    const out = writtenCell(xml, cell.id);
    const uniqueId = /uniqueid="([^"]+)"/.exec(out)![1];
    expect(uniqueId).toMatch(GUID);
    expect(out).toBe([
      '<cell id="{ID}" showlabel="true">',
      '                    <labels>',
      '                      <label description="Parent" languagecode="1033"/>',
      '                    </labels>',
      `                    <control id="parentaccountid_quickview" classid="${CLASS_ID.quickView}" datafieldname="parentaccountid" uniqueid="${uniqueId}">`,
      '                      <parameters>',
      '                        <QuickForms>&lt;QuickForms&gt;&lt;QuickFormIds&gt;&lt;QuickFormId entityname="account"&gt;'
        + '{55555555-0000-0000-0000-000000000001}&lt;/QuickFormId&gt;&lt;/QuickFormIds&gt;&lt;/QuickForms&gt;</QuickForms>',
      '                      </parameters>',
      '                    </control>',
      '                  </cell>',
    ].join('\n'));
    expect(runtimeControl(xml, 'parentaccountid_quickview')).toEqual({
      kind: 'quickview', id: 'parentaccountid_quickview', lookupAttribute: 'parentaccountid',
      forms: { account: '{55555555-0000-0000-0000-000000000001}' },
    });
  });

  it('a new quick view keeps its uniqueid across saves and Save as (H1)', () => {
    const d = addQuickView(read(), section(read()).key, QUICK_VIEW, 'Parent');
    const cell = lastCell(d);
    const xml = writeForm(d);
    expect(writeForm(d)).toBe(xml);
    const uniqueOf = (x: string) => /uniqueid="([^"]+)"/.exec(writtenCell(x, cell.id))![1];
    expect(uniqueOf(xml)).toMatch(GUID);
    expect(uniqueOf(xml)).toBe(cell.uniqueId);
    expect(uniqueOf(writeForm({ ...d, name: 'copy' }))).toBe(cell.uniqueId);
  });

  it('escapes the form ids and table names it puts in the QuickForms text', () => {
    const d = addQuickView(read(), section(read()).key, { ...QUICK_VIEW, forms: { 'a"b': 'x<y', contact: '{1}' } }, 'P');
    expect(runtimeControl(writeForm(d), 'parentaccountid_quickview')).toMatchObject({ forms: { 'a"b': 'x<y', contact: '{1}' } });
  });
});

describe('addWebResource', () => {
  it('derives the control id from the name without prefix and path', () => {
    expect(webResourceControlId('new_/maps/chart.svg')).toBe('WebResource_chartsvg');
    expect(webResourceControlId('new_map.html')).toBe('WebResource_maphtml');
    expect(webResourceControlId('logo')).toBe('WebResource_logo');
  });

  it('writes the control of G4', () => {
    const d = addWebResource(read(), section(read()).key, WEB_RESOURCE, 'Chart');
    const cell = lastCell(d);
    expect(cell).toMatchObject({ kind: 'webresource', colSpan: 1, rowSpan: 1 });
    const xml = writeForm(d);
    expect(writtenCell(xml, cell.id)).toBe([
      '<cell id="{ID}" showlabel="true">',
      '                    <labels>',
      '                      <label description="Chart" languagecode="1033"/>',
      '                    </labels>',
      `                    <control id="WebResource_chartsvg" classid="${CLASS_ID.webResource}">`,
      '                      <parameters>',
      '                        <Url>new_/maps/chart.svg</Url>',
      '                        <PassParameters>false</PassParameters>',
      '                        <Security>false</Security>',
      '                        <Scrolling>auto</Scrolling>',
      '                        <Border>true</Border>',
      '                        <WebResourceId>{66666666-0000-0000-0000-000000000001}</WebResourceId>',
      '                        <Data/>',
      '                      </parameters>',
      '                    </control>',
      '                  </cell>',
    ].join('\n'));
    expect(runtimeControl(xml, 'WebResource_chartsvg')).toEqual({
      kind: 'webresource', id: 'WebResource_chartsvg', classId: CLASS_ID.webResource, iframe: false, url: 'new_/maps/chart.svg',
      data: '', passParameters: false, restrictScripting: false, scrolling: 'auto', border: true,
    });
  });

  it('makes the derived id unique', () => {
    const d = addWebResource(read(), section(read()).key, { ...WEB_RESOURCE, url: 'new_map' }, 'Map');
    expect(lastCell(d).control).toMatchObject({ controlId: 'WebResource_map_2' });
  });
});

describe('setControl', () => {
  const subgridKey = (d: FormDesign) => cellById(d, '{a0000000-0000-0000-0000-000000000004}').key;
  const spec = <T,>(d: FormDesign, id: string) => cellById(d, id).control as T;
  const normal = serialize(parseXml(FORM));

  it('changing RecordsPerPage edits only <RecordsPerPage>, and an unknown parameter survives', () => {
    const d = read();
    const edited = setControl(d, subgridKey(d), { ...spec<SubgridSpec>(d, '{a0000000-0000-0000-0000-000000000004}'), recordsPerPage: 12 });
    expect(edited).not.toBe(d);
    const xml = writeForm(edited);
    expect(xml).not.toBe(FORM);
    expect(xml).toBe(normal.replace('<RecordsPerPage>8</RecordsPerPage>', '<RecordsPerPage>12</RecordsPerPage>'));
    expect(xml).toContain('<MakerExtra>keep me</MakerExtra>');
  });

  it('creates a missing parameter at the end of <parameters>', () => {
    const d = read();
    const id = '{a0000000-0000-0000-0000-000000000004}';
    const xml = writeForm(setControl(d, subgridKey(d), { ...spec<SubgridSpec>(d, id), viewPicker: true, viewId: '{v2}' }));
    expect(xml).toBe(normal
      .replace('<ViewId>{00000000-0000-0000-00aa-000010001003}</ViewId>', '<ViewId>{v2}</ViewId>')
      .replace('<EnableViewPicker>false</EnableViewPicker>', '<EnableViewPicker>true</EnableViewPicker>'));
    const web = '{a0000000-0000-0000-0000-000000000006}';
    const out = writeForm(setControl(d, cellById(d, web).key, { ...spec<WebResourceSpec>(d, web), data: 'a=1', scrolling: 'yes' }));
    expect(out).toBe(normal
      .replace('<Scrolling>never</Scrolling>', '<Scrolling>yes</Scrolling>')
      .replace('<WebResourceId>{44444444-0000-0000-0000-000000000001}</WebResourceId>',
        '<WebResourceId>{44444444-0000-0000-0000-000000000001}</WebResourceId>\n                        <Data>a=1</Data>'));
  });

  it('rewrites the QuickForms text only when a form changes', () => {
    const d = read();
    const id = '{a0000000-0000-0000-0000-000000000005}';
    const same = spec<QuickViewSpec>(d, id);
    expect(setControl(d, cellById(d, id).key, { ...same, forms: { ...same.forms } })).toBe(d);
    const xml = writeForm(setControl(d, cellById(d, id).key, { ...same, forms: { ...same.forms, account: '{f2}' } }));
    expect(runtimeControl(xml, 'contactcard')).toMatchObject({
      forms: { contact: '{33333333-0000-0000-0000-000000000001}', account: '{f2}' },
    });
    const control = parseXml(xml).getElementsByTagName('QuickForms')[0];
    expect(childElements(control.parentElement!, 'QuickForms')).toHaveLength(1);
  });

  it('ignores a change of relationship, target, lookup or control id (G2: delete and add instead)', () => {
    const d = read();
    const sub = spec<SubgridSpec>(d, '{a0000000-0000-0000-0000-000000000004}');
    expect(setControl(d, subgridKey(d), { ...sub, relationship: 'other', targetEntity: 'lead', controlId: 'x' })).toBe(d);
    const changedPage = setControl(d, subgridKey(d), { ...sub, relationship: 'other', recordsPerPage: 9 });
    expect(cellById(changedPage, '{a0000000-0000-0000-0000-000000000004}').control).toEqual({ ...sub, recordsPerPage: 9 });
    const qv = '{a0000000-0000-0000-0000-000000000005}';
    expect(setControl(d, cellById(d, qv).key, { ...spec<QuickViewSpec>(d, qv), lookup: 'ownerid' })).toBe(d);
  });

  it('keeps the cell layout, and is a no-op for another kind, an IFRAME or an unknown cell', () => {
    const d = read();
    const edited = setControl(d, subgridKey(d), { ...spec<SubgridSpec>(d, '{a0000000-0000-0000-0000-000000000004}'), recordsPerPage: 20 });
    expect(cellById(edited, '{a0000000-0000-0000-0000-000000000004}').rowSpan).toBe(3);
    expect(setControl(d, subgridKey(d), WEB_RESOURCE)).toBe(d);
    expect(setControl(d, cellById(d, '{a0000000-0000-0000-0000-000000000007}').key, WEB_RESOURCE)).toBe(d);
    expect(setControl(d, 'nope', SUBGRID)).toBe(d);
  });
});

describe('setControl in the header', () => {
  const HEADER_FORM = `<form>
  <tabs>
    <tab name="general" id="{a0000000-0000-0000-0000-000000000001}">
      <columns><column width="100%"><sections>
        <section name="main" columns="1" id="{a0000000-0000-0000-0000-000000000002}"><rows><row>
          <cell id="{a0000000-0000-0000-0000-000000000003}"><control id="name" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="name" /></cell>
        </row></rows></section>
      </sections></column></columns>
    </tab>
  </tabs>
  <header id="{a0000000-0000-0000-0000-000000000004}" columns="1111">
    <rows>
      <row>
        <cell id="{a0000000-0000-0000-0000-000000000005}">
          <control id="WebResource_head" classid="{9FDF5F91-88B1-47F4-AD53-C11EFC01A01D}">
            <parameters>
              <Url>new_head.html</Url>
              <PassParameters>false</PassParameters>
              <Security>false</Security>
              <Scrolling>auto</Scrolling>
              <Border>false</Border>
              <WebResourceId>{44444444-0000-0000-0000-000000000002}</WebResourceId>
            </parameters>
          </control>
        </cell>
      </row>
    </rows>
  </header>
</form>`;

  it("setControl edits a header cell's control and writes it in <header>", () => {
    const d = read(HEADER_FORM);
    const headCell = d.header[0];
    const spec = headCell.control as WebResourceSpec;
    expect(spec.scrolling).toBe('auto');
    const edited = setControl(d, headCell.key, { ...spec, scrolling: 'no' });
    expect(edited).not.toBe(d);
    expect(edited.header[0].control).toMatchObject({ scrolling: 'no' });
    const xml = writeForm(edited);
    expect(xml).toBe(serialize(parseXml(HEADER_FORM)).replace('<Scrolling>auto</Scrolling>', '<Scrolling>no</Scrolling>'));
    const header = parseXml(xml).getElementsByTagName('header')[0];
    expect(header.getElementsByTagName('Scrolling')[0].textContent).toBe('no');
    expect(parseXml(xml).getElementsByTagName('Scrolling')).toHaveLength(1);
  });
});

describe('writeControl', () => {
  it('creates a control element, or updates only the parameters that differ from before', () => {
    const doc = parseXml('<form/>');
    const el = writeControl(doc, undefined, { ...SUBGRID, controlId: 'orders' });
    expect(el.getAttribute('id')).toBe('orders');
    expect(childElements(childElements(el, 'parameters')[0], 'RecordsPerPage')[0].textContent).toBe('8');
    const before = readControl(el)!;
    const params = childElements(el, 'parameters')[0];
    params.appendChild(doc.createElement('Unknown'));
    const same = writeControl(doc, el, { ...SUBGRID, controlId: 'orders', recordsPerPage: 5 }, before);
    expect(same).toBe(el);
    expect(childElements(params, 'RecordsPerPage')[0].textContent).toBe('5');
    expect(Array.from(params.children).map((c) => c.tagName).slice(-2)).toEqual(['RecordsPerPage', 'Unknown']);
  });

  it('a new subgrid in an indented form writes <parameters> one level under <control>', () => {
    const d = addSubgrid(read(), section(read()).key, SUBGRID, 'Orders');
    const cell = Array.from(parseXml(writeForm(d)).getElementsByTagName('cell')).find((c) => c.getAttribute('id') === lastCell(d).id)!;
    const indent = (el: Element) => /[^\n]*$/.exec(el.previousSibling?.nodeValue ?? '')![0].length;
    const control = childElements(cell, 'control')[0];
    const params = childElements(control, 'parameters')[0];
    expect([indent(cell), indent(control), indent(params), indent(params.firstElementChild!)]).toEqual([18, 20, 22, 24]);
    expect(params.lastChild?.nodeValue).toBe('\n                      ');
    expect(control.lastChild?.nodeValue).toBe('\n                    ');
    // The same edit in a compact copy of the form adds no line.
    const compact = FORM.replace(/>\s+</g, '><');
    const c = read(compact);
    expect(writeForm(addSubgrid(c, section(c).key, SUBGRID, 'Orders'))).not.toContain('\n');
  });

  it('validPerPage_takes_whole_numbers_from_1_to_250', () => {
    expect(['1', '4', '250', ' 25 '].map(validPerPage)).toEqual([true, true, true, true]);
    expect(['', '0', '251', '2.5', '-1', 'x'].map(validPerPage)).toEqual([false, false, false, false, false, false]);
  });
});
