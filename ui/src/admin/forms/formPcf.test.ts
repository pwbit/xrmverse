import { describe, expect, it } from 'vitest';
import invented from '../../parsers/__fixtures__/forms/invented-all-features.xml?raw';
import { CLASS_ID } from '../../parsers/controlClass';
import { parseFormXml } from '../../parsers/formXml';
import { childElements, parseXml } from '../../parsers/xml';
import { parseManifest } from '../../pcf/manifest';
import type { AttributeMeta } from '../tables/metadata';
import type { Cell, DataSetControlRef, Tab } from '../../model/form';
import { setControl, type SubgridSpec } from './formControls';
import { addHeaderField, addSection, readForm, removeField, removeHeaderField, removeSection, removeTab, writeForm, type DesignCell, type FormDesign } from './formDesign';
import { formRow } from './formDesignTestSupport';
import {
  installedName, missingRequired, newPcfBinding, pcfChoices, pcfFits, pcfMissing, readPcf, setPcf, type PcfBinding,
} from './formPcf';

const TEXT = CLASS_ID.text;
const SUBGRID = CLASS_ID.subgrid;
const PROGRESS = '{aaaaaaaa-0000-0000-0000-000000000001}';
const RATING = '{aaaaaaaa-0000-0000-0000-000000000002}';

const entry = (attrs: string, value: string) =>
  `<customControl ${attrs}><parameters><value>${value}</value></parameters></customControl>`;

/** A form with a field PCF whose phone and tablet entries follow the web one, one whose phone entry has its own settings, and a subgrid. */
const FORM = `<form>
  <tabs>
    <tab name="general" id="{10000000-0000-0000-0000-000000000001}">
      <labels />
      <columns>
        <column width="100%">
          <sections>
            <section name="main" id="{10000000-0000-0000-0000-000000000002}" columns="1">
              <labels />
              <rows>
                <row>
                  <cell id="{10000000-0000-0000-0000-000000000003}">
                    <labels><label description="Name" languagecode="1033" /></labels>
                    <control id="name" classid="${TEXT}" datafieldname="name" />
                  </cell>
                </row>
                <row>
                  <cell id="{10000000-0000-0000-0000-000000000004}">
                    <labels><label description="Progress" languagecode="1033" /></labels>
                    <control id="progress" classid="${CLASS_ID.integer}" datafieldname="progress" uniqueid="${PROGRESS}" />
                  </cell>
                </row>
                <row>
                  <cell id="{10000000-0000-0000-0000-000000000005}">
                    <labels><label description="Rating" languagecode="1033" /></labels>
                    <control id="rating" classid="${CLASS_ID.integer}" datafieldname="rating" uniqueid="${RATING}" />
                  </cell>
                </row>
                <row>
                  <cell id="{10000000-0000-0000-0000-000000000006}" colspan="1" rowspan="1">
                    <labels><label description="Contacts" languagecode="1033" /></labels>
                    <control id="contacts" classid="${SUBGRID}" indicationOfSubgrid="true">
                      <parameters>
                        <ViewId>{20000000-0000-0000-0000-000000000001}</ViewId>
                        <IsUserView>false</IsUserView>
                        <RelationshipName>contact_customer_accounts</RelationshipName>
                        <TargetEntityType>contact</TargetEntityType>
                        <EnableViewPicker>false</EnableViewPicker>
                        <RecordsPerPage>5</RecordsPerPage>
                      </parameters>
                    </control>
                  </cell>
                </row>
              </rows>
            </section>
          </sections>
        </column>
      </columns>
    </tab>
    <tab name="other" id="{10000000-0000-0000-0000-000000000007}">
      <labels />
      <columns><column width="100%"><sections><section name="o" id="{10000000-0000-0000-0000-000000000008}" columns="1"><labels /><rows /></section></sections></column></columns>
    </tab>
  </tabs>
  <header id="{10000000-0000-0000-0000-000000000009}" columns="1111">
    <rows>
      <row>
        <cell id="{10000000-0000-0000-0000-00000000000a}">
          <labels><label description="Owner" languagecode="1033" /></labels>
          <control id="header_ownerid" classid="${CLASS_ID.lookup}" datafieldname="ownerid" />
        </cell>
      </row>
    </rows>
  </header>
  <controlDescriptions>
    <controlDescription forControl="${PROGRESS}">
      <customControl id="${CLASS_ID.integer}"><parameters><datafieldname>progress</datafieldname></parameters></customControl>
      ${entry('formFactor="0" name="new_NS.Slider"', 'progress')}
      ${entry('formFactor="1" name="new_NS.Slider"', 'progress')}
      ${entry('formFactor="2" name="new_NS.Slider"', ' progress ')}
    </controlDescription>
    <controlDescription forControl="${RATING}">
      <customControl id="${CLASS_ID.integer}"><parameters><datafieldname>rating</datafieldname></parameters></customControl>
      <customControl formFactor="0" name="new_NS.Stars"><parameters><value>rating</value><max static="true" type="Whole.None">5</max></parameters></customControl>
      <customControl formFactor="1" name="new_NS.Stars"><parameters><value>rating</value><max static="true" type="Whole.None">3</max></parameters></customControl>
      <customControl formFactor="2" name="new_NS.Stars"><parameters><value>rating</value><max static="true" type="Whole.None">5</max></parameters></customControl>
    </controlDescription>
    <controlDescription forControl="{aaaaaaaa-0000-0000-0000-0000000000ff}">
      <customControl id="${TEXT}"><parameters><datafieldname>gone</datafieldname></parameters></customControl>
    </controlDescription>
  </controlDescriptions>
</form>`;

const read = (xml = FORM): FormDesign => readForm(formRow(xml), 1033);
const allCells = (d: FormDesign): DesignCell[] =>
  [...d.tabs.flatMap((t) => t.columns.flatMap((c) => c.sections.flatMap((s) => s.cells))), ...d.header];
const cellOf = (d: FormDesign, id: string) =>
  allCells(d).find((c) => c.attribute === id || c.control?.controlId === id)!;
const formOf = (xml: string) => parseXml(xml).documentElement;
const descriptions = (xml: string) =>
  childElements(formOf(xml), 'controlDescriptions').flatMap((c) => childElements(c, 'controlDescription'));
const descriptionFor = (xml: string, uniqueId: string) =>
  descriptions(xml).find((el) => el.getAttribute('forControl')?.toLowerCase() === uniqueId.toLowerCase());
const serialize = (n: Node) => new XMLSerializer().serializeToString(n);

const attr = (logicalName: string, kind: AttributeMeta['kind'] = 'string', extra: Partial<AttributeMeta> = {}): AttributeMeta => ({
  logicalName, displayName: logicalName, kind, typeName: 'String', requiredLevel: 'None', required: false, validForCreate: true,
  validForUpdate: true, validForRead: true, isPrimaryId: false, isPrimaryName: false, isCustom: false, targets: [], options: [],
  ...extra,
} as AttributeMeta);

const manifest = (body: string) =>
  parseManifest(`<manifest><control namespace="NS" constructor="C" version="1.0.0">${body}</control></manifest>`);

const TEXT_CONTROL = manifest(`
  <property name="value" of-type="SingleLine.Text" usage="bound" required="true" />
  <property name="placeholder" of-type="SingleLine.Text" usage="input" required="true" />
  <property name="limit" of-type="Whole.None" usage="input" default-value="10" />`);
const NUMBER_CONTROL = manifest(`
  <type-group name="numbers"><type>Whole.None</type><type>Decimal</type></type-group>
  <property name="value" of-type-group="numbers" usage="bound" required="true" />`);
const GRID_CONTROL = manifest('<data-set name="records" />');
const SET_GRID = manifest('<data-set name="records"><property-set name="title" of-type="SingleLine.Text" usage="bound" /></data-set>');
const MIXED_GRID = manifest('<data-set name="withSet"><property-set name="t" of-type="SingleLine.Text" usage="bound" /></data-set><data-set name="plain" />');
const TEXT_WITH_SET = manifest(`
  <property name="value" of-type="SingleLine.Text" usage="bound" />
  <data-set name="items" />`);

/** The `customControl` the runtime parser reads for the control `id` of the written form. */
function runtimeCustom(xml: string, id: string): DataSetControlRef | undefined {
  const model = parseFormXml({ systemformid: 'f', name: 'n', objecttypecode: 'account', type: 2, formxml: xml });
  const cells: Cell[] = [
    ...model.tabs.flatMap((t: Tab) => t.columns.flatMap((c) => c.sections.flatMap((s) => s.rows.flat()))),
    ...model.header,
  ];
  const control = cells.map((c) => c.control).find((c) => c && 'id' in c && c.id === id);
  return control && 'customControl' in control ? control.customControl : undefined;
}

describe('readPcf', () => {
  it('reads the web entry of a field binding, and whether phone and tablet follow it', () => {
    const form = formOf(FORM);
    expect(readPcf(form, PROGRESS)).toEqual({
      name: 'new_NS.Slider', parameters: { value: { value: 'progress', static: false } }, ownFactors: false,
    });
    expect(readPcf(form, RATING.toUpperCase())).toMatchObject({
      name: 'new_NS.Stars', parameters: { max: { value: '5', static: true, type: 'Whole.None' } }, ownFactors: true,
    });
    // No named entry: the designer cannot model it.
    expect(readPcf(form, '{aaaaaaaa-0000-0000-0000-0000000000ff}')).toBeUndefined();
    expect(readPcf(form, '{nope}')).toBeUndefined();
  });

  it('counts a missing phone or tablet entry as different, and fills DesignCell.pcf for field cells', () => {
    const d = read(invented);
    const progress = allCells(d).find((c) => c.attribute === 'progress')!;
    expect(progress.pcf).toEqual({ name: 'inv_Invented.ProgressBar', parameters: { value: { value: 'progress', static: false } }, ownFactors: true });
    expect(cellOf(read(), 'name').pcf).toBeUndefined();
    expect(cellOf(read(), 'progress').pcf?.name).toBe('new_NS.Slider');
  });
});

describe('setPcf and the write', () => {
  it('binding a field writes the classic entry plus three named entries and adds a uniqueid', () => {
    const d = read();
    const name = cellOf(d, 'name');
    const binding = newPcfBinding('new_NS.TextBox', TEXT_CONTROL, name);
    expect(binding).toEqual({
      name: 'new_NS.TextBox',
      parameters: { value: { value: 'name', static: false }, limit: { value: '10', static: true, type: 'Whole.None' } },
      ownFactors: false,
    });
    const next = setPcf(d, name.key, binding);
    expect(cellOf(next, 'name').pcf).toEqual(binding);
    const out = writeForm(next);
    const control = Array.from(formOf(out).getElementsByTagName('control')).find((c) => c.getAttribute('id') === 'name')!;
    const uniqueId = control.getAttribute('uniqueid')!;
    expect(uniqueId).toMatch(/^\{[0-9a-f-]{36}\}$/);
    const desc = descriptionFor(out, uniqueId)!;
    expect(serialize(desc)).toBe([
      `<controlDescription forControl="${uniqueId}">`,
      `      <customControl id="${TEXT}">`,
      '        <parameters>',
      '          <datafieldname>name</datafieldname>',
      '        </parameters>',
      '      </customControl>',
      ...[0, 1, 2].flatMap((f) => [
        `      <customControl formFactor="${f}" name="new_NS.TextBox">`,
        '        <parameters>',
        '          <value>name</value>',
        '          <limit static="true" type="Whole.None">10</limit>',
        '        </parameters>',
        '      </customControl>',
      ]),
      '    </controlDescription>',
    ].join('\n'));
    expect(runtimeCustom(out, 'name')).toEqual({
      name: 'new_NS.TextBox',
      parameters: { value: { value: 'name', static: false }, limit: { value: '10', static: true, type: 'Whole.None' } },
    });
    // The other descriptions keep their text.
    for (const id of [PROGRESS, RATING]) expect(serialize(descriptionFor(out, id)!)).toBe(serialize(descriptionFor(FORM, id)!));
    // Read back: the same binding, and no further change.
    const again = read(out);
    expect(cellOf(again, 'name').pcf).toEqual(binding);
    expect(writeForm(again)).toBe(out);
  });

  it('binding a new header field writes its description, and creates <controlDescriptions> when there is none', () => {
    const plain = FORM.replace(/<controlDescriptions>[\s\S]*<\/controlDescriptions>/, '');
    const d = addHeaderField(read(plain), attr('telephone1'));
    const cell = d.header[1];
    const out = writeForm(setPcf(d, cell.key, newPcfBinding('new_NS.TextBox', TEXT_CONTROL, cell)));
    const control = Array.from(formOf(out).getElementsByTagName('control')).find((c) => c.getAttribute('id') === 'header_telephone1')!;
    expect(descriptionFor(out, control.getAttribute('uniqueid')!)).toBeDefined();
    expect(runtimeCustom(out, 'header_telephone1')?.name).toBe('new_NS.TextBox');
  });

  it('binding a subgrid writes <data-set> with the subgrid parameters', () => {
    const d = read();
    const grid = cellOf(d, 'contacts');
    const binding = newPcfBinding('new_NS.Grid', GRID_CONTROL, grid);
    expect(binding.dataSet).toEqual({
      name: 'records',
      parameters: {
        ViewId: '{20000000-0000-0000-0000-000000000001}', IsUserView: 'false', RelationshipName: 'contact_customer_accounts',
        TargetEntityType: 'contact', EnableViewPicker: 'false', RecordsPerPage: '5',
      },
    });
    const out = writeForm(setPcf(d, grid.key, binding));
    const control = Array.from(formOf(out).getElementsByTagName('control')).find((c) => c.getAttribute('id') === 'contacts')!;
    const desc = descriptionFor(out, control.getAttribute('uniqueid')!)!;
    const [classic, ...named] = childElements(desc, 'customControl');
    expect(classic.getAttribute('id')).toBe(SUBGRID);
    // The classic entry holds a copy of the control's parameters, indented at its own depth (C3).
    expect(serialize(childElements(classic, 'parameters')[0])).toBe([
      '<parameters>',
      '          <ViewId>{20000000-0000-0000-0000-000000000001}</ViewId>',
      '          <IsUserView>false</IsUserView>',
      '          <RelationshipName>contact_customer_accounts</RelationshipName>',
      '          <TargetEntityType>contact</TargetEntityType>',
      '          <EnableViewPicker>false</EnableViewPicker>',
      '          <RecordsPerPage>5</RecordsPerPage>',
      '        </parameters>',
    ].join('\n'));
    expect(named.map((n) => n.getAttribute('formFactor'))).toEqual(['0', '1', '2']);
    expect(serialize(childElements(named[0], 'parameters')[0])).toBe([
      '<parameters>',
      '          <data-set name="records">',
      '            <ViewId>{20000000-0000-0000-0000-000000000001}</ViewId>',
      '            <IsUserView>false</IsUserView>',
      '            <RelationshipName>contact_customer_accounts</RelationshipName>',
      '            <TargetEntityType>contact</TargetEntityType>',
      '            <EnableViewPicker>false</EnableViewPicker>',
      '            <RecordsPerPage>5</RecordsPerPage>',
      '          </data-set>',
      '        </parameters>',
    ].join('\n'));
    expect(runtimeCustom(out, 'contacts')).toEqual({ name: 'new_NS.Grid', parameters: {}, dataSet: binding.dataSet });
  });

  it('a subgrid edit follows into its description: the classic entry and the data-set', () => {
    const d = read();
    const grid = cellOf(d, 'contacts');
    const bound = read(writeForm(setPcf(d, grid.key, newPcfBinding('new_NS.Grid', GRID_CONTROL, grid))));
    const cell = cellOf(bound, 'contacts');
    const next = setControl(bound, cell.key, { ...(cell.control as SubgridSpec), recordsPerPage: 9 });
    expect(cellOf(next, 'contacts').pcf?.dataSet?.parameters.RecordsPerPage).toBe('9');
    const out = writeForm(next);
    const desc = descriptions(out).find((el) => el.getElementsByTagName('data-set').length > 0)!;
    expect(Array.from(desc.getElementsByTagName('RecordsPerPage')).map((e) => e.textContent)).toEqual(['9', '9', '9', '9']);
    expect(runtimeCustom(out, 'contacts')?.dataSet?.parameters.RecordsPerPage).toBe('9');
  });

  it('an edit with ownFactors false changes all three entries', () => {
    const d = read();
    const cell = cellOf(d, 'progress');
    expect(cell.pcf!.ownFactors).toBe(false);
    const binding: PcfBinding = {
      ...cell.pcf!, parameters: { ...cell.pcf!.parameters, step: { value: '2', static: true, type: 'Whole.None' } },
    };
    const out = writeForm(setPcf(d, cell.key, binding));
    const desc = descriptionFor(out, PROGRESS)!;
    const named = childElements(desc, 'customControl').filter((c) => c.hasAttribute('name'));
    expect(named.map((n) => serialize(n.getElementsByTagName('step')[0]))).toEqual(
      Array(3).fill('<step static="true" type="Whole.None">2</step>'));
    // The classic entry is unchanged; and the source's whitespace is kept around the parameters not edited.
    expect(serialize(childElements(desc, 'customControl')[0])).toBe(serialize(childElements(descriptionFor(FORM, PROGRESS)!, 'customControl')[0]));
    expect(named[2].getElementsByTagName('value')[0].textContent).toBe(' progress ');
    expect(runtimeCustom(out, 'progress')?.parameters.step).toEqual({ value: '2', static: true, type: 'Whole.None' });
  });

  it('an edit with ownFactors true changes only the formFactor="0" entry', () => {
    const d = read();
    const cell = cellOf(d, 'rating');
    expect(cell.pcf!.ownFactors).toBe(true);
    // setPcf keeps ownFactors as read, whatever the binding says.
    const next = setPcf(d, cell.key, { ...cell.pcf!, ownFactors: false, parameters: { ...cell.pcf!.parameters, max: { value: '10', static: true, type: 'Whole.None' } } });
    expect(cellOf(next, 'rating').pcf!.ownFactors).toBe(true);
    const out = writeForm(next);
    const maxes = Array.from(descriptionFor(out, RATING)!.getElementsByTagName('max')).map((m) => m.textContent);
    expect(maxes).toEqual(['10', '3', '5']);
    expect(runtimeCustom(out, 'rating')?.parameters.max.value).toBe('10');
  });

  it('edits the entry without formFactor when there is no formFactor="0" one', () => {
    const xml = FORM.replace('formFactor="0" name="new_NS.Stars"', 'name="new_NS.Stars"');
    const d = read(xml);
    const cell = cellOf(d, 'rating');
    expect(cell.pcf).toMatchObject({ name: 'new_NS.Stars', ownFactors: true });
    const out = writeForm(setPcf(d, cell.key, { ...cell.pcf!, name: 'new_NS.Other' }));
    expect(childElements(descriptionFor(out, RATING)!, 'customControl').map((c) => c.getAttribute('name')))
      .toEqual([null, 'new_NS.Other', 'new_NS.Stars', 'new_NS.Stars']);
  });

  it('removing the binding removes the controlDescription', () => {
    const d = read();
    const out = writeForm(setPcf(d, cellOf(d, 'progress').key, undefined));
    expect(descriptionFor(out, PROGRESS)).toBeUndefined();
    expect(serialize(descriptionFor(out, RATING)!)).toBe(serialize(descriptionFor(FORM, RATING)!));
    expect(runtimeCustom(out, 'progress')).toBeUndefined();
    expect(read(out).tabs[0].columns[0].sections[0].cells.map((c) => c.pcf?.name)).toEqual([undefined, undefined, 'new_NS.Stars', undefined]);
  });

  it('removing the cell removes its controlDescription, but not an orphan one', () => {
    const d = read();
    const ff = '{aaaaaaaa-0000-0000-0000-0000000000ff}';
    const out = writeForm(removeField(d, cellOf(d, 'progress').key));
    expect(descriptionFor(out, PROGRESS)).toBeUndefined();
    expect(descriptionFor(out, RATING)).toBeDefined();
    expect(descriptionFor(out, ff)).toBeDefined();
    // A section and a tab take their cells' descriptions with them; the emptied <controlDescriptions> stays only with the orphan.
    const withSection = addSection(d, d.tabs[0].key, 0);
    const noSection = writeForm(removeSection(withSection, d.tabs[0].columns[0].sections[0].key));
    expect(descriptions(noSection).map((el) => el.getAttribute('forControl'))).toEqual([ff]);
    const noTab = writeForm(removeTab(d, d.tabs[0].key));
    expect(descriptions(noTab).map((el) => el.getAttribute('forControl'))).toEqual([ff]);
  });

  it('removing a header field removes its controlDescription, and an emptied <controlDescriptions> goes', () => {
    const plain = FORM.replace(/<controlDescriptions>[\s\S]*<\/controlDescriptions>/, '');
    const d = addHeaderField(read(plain), attr('telephone1'));
    const bound = read(writeForm(setPcf(d, d.header[1].key, newPcfBinding('new_NS.TextBox', TEXT_CONTROL, d.header[1]))));
    expect(bound.header[1].pcf?.name).toBe('new_NS.TextBox');
    const out = writeForm(removeHeaderField(bound, bound.header[1].key));
    expect(childElements(formOf(out), 'controlDescriptions')).toHaveLength(0);
  });

  it('an untouched description keeps its text, and setPcf with the same binding is a no-op', () => {
    const d = read();
    const cell = cellOf(d, 'progress');
    expect(setPcf(d, cell.key, { ...cell.pcf! })).toBe(d);
    expect(writeForm(d)).toBe(FORM);
    expect(writeForm(read(invented))).toBe(invented);
    // An edit elsewhere keeps every description.
    const out = writeForm(setPcf(d, cellOf(d, 'rating').key, undefined));
    expect(serialize(descriptionFor(out, PROGRESS)!)).toBe(serialize(descriptionFor(FORM, PROGRESS)!));
    // An unknown cell, and a cell that is neither a field nor a subgrid, take no binding.
    expect(setPcf(d, 'nope', cell.pcf)).toBe(d);
    const inv = read(invented);
    const quickView = allCells(inv).find((c) => c.kind === 'quickview')!;
    expect(setPcf(inv, quickView.key, cell.pcf)).toBe(inv);
  });

  it('writeForm notices a binding-only edit (the no-change check covers bindings)', () => {
    const d = read();
    const cell = cellOf(d, 'progress');
    const out = writeForm(setPcf(d, cell.key, { ...cell.pcf!, name: 'new_NS.Other' }));
    expect(out).not.toBe(FORM);
    expect(runtimeCustom(out, 'progress')?.name).toBe('new_NS.Other');
  });
});

describe('fix round 1', () => {
  it('removing a binding removes every description of the control, duplicates included', () => {
    const dup = `<controlDescription forControl="${PROGRESS}">${entry('formFactor="0" name="new_NS.Old"', 'progress')}</controlDescription>`;
    const xml = FORM.replace('<controlDescriptions>', `<controlDescriptions>${dup}`);
    const d = read(xml);
    expect(cellOf(d, 'progress').pcf?.name).toBe('new_NS.Slider');
    const out = writeForm(setPcf(d, cellOf(d, 'progress').key, undefined));
    expect(descriptionFor(out, PROGRESS)).toBeUndefined();
    expect(runtimeCustom(out, 'progress')).toBeUndefined();
    expect(cellOf(read(out), 'progress').pcf).toBeUndefined();
  });

  it('handles a parameter named like an Object.prototype key', () => {
    const xml = FORM.replace('<max static="true" type="Whole.None">5</max></parameters></customControl>\n      <customControl formFactor="1"',
      '<max static="true" type="Whole.None">5</max><constructor>x</constructor></parameters></customControl>\n      <customControl formFactor="1"');
    expect(xml).not.toBe(FORM);
    const d = read(xml);
    const cell = cellOf(d, 'rating');
    expect(cell.pcf!.parameters.constructor).toEqual({ value: 'x', static: false });
    // Dropping it removes the element; a binding without it must not keep it through the prototype.
    const { constructor: _, ...rest } = cell.pcf!.parameters;
    const out = writeForm(setPcf(d, cell.key, { ...cell.pcf!, parameters: rest }));
    expect(descriptionFor(out, RATING)!.getElementsByTagName('constructor')).toHaveLength(0);
    expect(runtimeCustom(out, 'rating')?.parameters).toEqual({ value: { value: 'rating', static: false }, max: { value: '5', static: true, type: 'Whole.None' } });
    const m = manifest('<property name="constructor" of-type="SingleLine.Text" usage="input" required="true" />');
    expect(missingRequired({ ...cell.pcf!, parameters: rest }, m)).toEqual(['constructor']);
  });
});

describe('pcfChoices and missingRequired', () => {
  const rows = [
    { name: 'new_NS.TextBox', manifest: TEXT_CONTROL },
    { name: 'new_NS.Number', manifest: NUMBER_CONTROL },
    { name: 'new_NS.Grid', manifest: GRID_CONTROL },
    { name: 'new_NS.Broken', error: 'the manifest is not valid XML' },
    { name: 'new_NS.SetGrid', manifest: SET_GRID },
    { name: 'new_NS.MixedGrid', manifest: MIXED_GRID },
    { name: 'new_NS.TextWithSet', manifest: TEXT_WITH_SET },
  ];

  it('filters by of-type on a field cell and by data-set on a subgrid cell', () => {
    const d = read();
    const names = (cell: DesignCell, a?: AttributeMeta) => pcfChoices(rows, cell, a).map((c) => c.name);
    expect(names(cellOf(d, 'name'), attr('name'))).toEqual(['new_NS.TextBox']);
    expect(names(cellOf(d, 'progress'), attr('progress', 'int'))).toEqual(['new_NS.Number']);
    expect(names(cellOf(d, 'progress'), attr('progress', 'decimal'))).toEqual(['new_NS.Number']);
    expect(names(cellOf(d, 'name'), attr('email', 'string', { format: 'Email' }))).toEqual([]);
    expect(names(cellOf(d, 'name'))).toEqual([]);
    // Not what the runtime cannot render: a data-set with a property-set only, or a field control with a data-set.
    expect(names(cellOf(d, 'contacts'))).toEqual(['new_NS.Grid', 'new_NS.MixedGrid', 'new_NS.TextWithSet']);
    expect(newPcfBinding('new_NS.MixedGrid', MIXED_GRID, cellOf(d, 'contacts')).dataSet?.name).toBe('plain');
    expect(pcfChoices(rows, cellOf(d, 'contacts'), undefined)[0].manifest).toBe(GRID_CONTROL);
  });

  it('lists the required properties that are not set', () => {
    const d = read();
    const binding = newPcfBinding('new_NS.TextBox', TEXT_CONTROL, cellOf(d, 'name'));
    expect(missingRequired(binding, TEXT_CONTROL)).toEqual(['placeholder']);
    const set = { ...binding, parameters: { ...binding.parameters, placeholder: { value: ' Hi ', static: true, type: 'SingleLine.Text' } } };
    expect(missingRequired(set, TEXT_CONTROL)).toEqual([]);
    const blank = { ...binding, parameters: { ...binding.parameters, value: { value: ' ', static: false } } };
    expect(missingRequired(blank, TEXT_CONTROL)).toEqual(['value', 'placeholder']);
  });

  it('a required property the binding leaves out counts as set when the manifest gives it a default value', () => {
    const withDefault = manifest(`
  <property name="value" of-type="SingleLine.Text" usage="bound" required="true" />
  <property name="mode" of-type="SingleLine.Text" usage="input" required="true" default-value="compact" />
  <property name="blankDefault" of-type="SingleLine.Text" usage="input" required="true" default-value=" " />`);
    // As imported from a solution: only the bound column is written.
    const imported: PcfBinding = { name: 'new_NS.Modes', parameters: { value: { value: 'name', static: false } }, ownFactors: false };
    expect(missingRequired(imported, withDefault)).toEqual(['blankDefault']);
    // A value the binding does give is what the runtime uses, so a blank one is still missing.
    const blank = { ...imported, parameters: { ...imported.parameters, mode: { value: '', static: true } } };
    expect(missingRequired(blank, withDefault)).toEqual(['mode', 'blankDefault']);
  });

  it('pcfMissing lists the cells whose known control lacks required properties, and skips unknown or broken controls', () => {
    const d = read();
    const name = cellOf(d, 'name');
    const withBox = setPcf(d, name.key, newPcfBinding('new_NS.TextBox', TEXT_CONTROL, name));
    expect(pcfMissing(withBox, rows, [attr('name')]).map((m) => [m.cell.key, m.properties])).toEqual([[name.key, ['placeholder']]]);
    // The name is matched without case; a control that is not installed or does not parse is not checked.
    expect(pcfMissing(setPcf(d, name.key, { ...newPcfBinding('new_NS.TextBox', TEXT_CONTROL, name), name: 'NEW_ns.textbox' }), rows, [attr('name')])).toHaveLength(1);
    expect(pcfMissing(withBox, rows.filter((r) => r.name !== 'new_NS.TextBox'), [attr('name')])).toEqual([]);
    expect(pcfMissing(setPcf(d, name.key, { ...newPcfBinding('new_NS.TextBox', TEXT_CONTROL, name), name: 'new_NS.Broken' }), rows, [attr('name')])).toEqual([]);
  });
});

describe('UI-2g follow-ups (#274)', () => {
  const controlOf = (xml: string, id: string) =>
    Array.from(formOf(xml).getElementsByTagName('control')).find((c) => c.getAttribute('id') === id)!;

  it('a new binding on a control without uniqueid keeps one id across saves (H1)', () => {
    const d = read();
    const name = cellOf(d, 'name');
    expect(controlOf(FORM, 'name').hasAttribute('uniqueid')).toBe(false);
    const next = setPcf(d, name.key, newPcfBinding('new_NS.TextBox', TEXT_CONTROL, name));
    const out = writeForm(next);
    expect(writeForm(next)).toBe(out);
    const uniqueId = controlOf(out, 'name').getAttribute('uniqueid')!;
    expect(uniqueId).toBe(cellOf(next, 'name').uniqueId);
    // The binding's description points at the control by exactly that uniqueid (not just a case-insensitive match).
    expect(descriptionFor(out, uniqueId)!.getAttribute('forControl')).toBe(uniqueId);
    expect(descriptions(out).filter((el) => el.getAttribute('forControl') === uniqueId)).toHaveLength(1);
    expect(writeForm({ ...next, name: 'copy' })).toBe(out);
  });

  it('removing a binding given in the session also removes its uniqueid', () => {
    const d = read();
    const k = cellOf(d, 'name').key;
    const bound = setPcf(d, k, newPcfBinding('new_NS.TextBox', TEXT_CONTROL, cellOf(d, 'name')));
    const unbound = setPcf(bound, k, undefined);
    expect(cellOf(unbound, 'name').uniqueId).toBeUndefined();
    expect(writeForm(unbound)).toBe(FORM);
    // A uniqueid of the source stays.
    const progress = cellOf(d, 'progress');
    expect(cellOf(setPcf(d, progress.key, undefined), 'progress').uniqueId).toBe(PROGRESS);
  });

  it('a new controlDescription is indented', () => {
    const d = read();
    const name = cellOf(d, 'name');
    const out = writeForm(setPcf(d, name.key, newPcfBinding('new_NS.TextBox', TEXT_CONTROL, name)));
    const desc = descriptionFor(out, controlOf(out, 'name').getAttribute('uniqueid')!)!;
    expect(desc.previousSibling?.nodeValue).toBe('\n    ');
    expect(desc.nextSibling?.nodeValue).toBe('\n  ');
    expect(serialize(desc)).toContain('>\n      <customControl formFactor="0" name="new_NS.TextBox">\n        <parameters>\n          <value>');
    // A new <controlDescriptions> sits on its own line too.
    const plain = FORM.replace(/\n  <controlDescriptions>[\s\S]*<\/controlDescriptions>/, '');
    const p = read(plain);
    const fresh = writeForm(setPcf(p, cellOf(p, 'name').key, newPcfBinding('new_NS.TextBox', TEXT_CONTROL, cellOf(p, 'name'))));
    expect(fresh).toContain('\n  </header>\n  <controlDescriptions>\n    <controlDescription forControl=');
    expect(fresh.endsWith('    </controlDescription>\n  </controlDescriptions>\n</form>')).toBe(true);
    // The same edit in a compact copy of the form adds no line.
    const c = read(FORM.replace(/>\s+</g, '><'));
    expect(writeForm(setPcf(c, cellOf(c, 'name').key, newPcfBinding('new_NS.TextBox', TEXT_CONTROL, cellOf(c, 'name'))))).not.toContain('\n');
  });

  it('pcfFits and installedName ignore case; pcfMissing skips an unfit binding', () => {
    const d = read();
    const rows = [{ name: 'new_Ctl.Box', manifest: TEXT_CONTROL }];
    const attributes = [attr('name'), attr('progress', 'int')];
    const name = cellOf(d, 'name');
    const binding = { ...newPcfBinding('new_Ctl.Box', TEXT_CONTROL, name), name: 'NEW_ctl.box' };
    const onText = setPcf(d, name.key, binding);
    expect(pcfFits(rows, cellOf(onText, 'name'), attributes[0])).toBe(true);
    expect(installedName(rows, 'NEW_ctl.box')).toBe('new_Ctl.Box');
    expect(installedName(rows, 'new_Other.Box')).toBeUndefined();
    expect(pcfMissing(onText, rows, attributes).map((m) => m.properties)).toEqual([['placeholder']]);
    // The same binding on a whole number field does not fit: it is left out, though `placeholder` is unset.
    const progress = cellOf(d, 'progress');
    const onNumber = setPcf(d, progress.key, binding);
    expect(pcfFits(rows, cellOf(onNumber, 'progress'), attributes[1])).toBe(false);
    expect(missingRequired(cellOf(onNumber, 'progress').pcf!, TEXT_CONTROL)).toEqual(['placeholder']);
    expect(pcfMissing(onNumber, rows, attributes)).toEqual([]);
  });
});
