import { describe, expect, it } from 'vitest';
import type { FieldControl, QuickViewControl, SubgridControl } from '../model/form';
import { controlKind } from './controlClass';
import { labelFor } from './formXml';
import { allCells, parseForm, rowOf, wrap } from './formXml.testSupport';
import invented from './__fixtures__/forms/invented-all-features.xml?raw';

/** The invented form with every feature. The tests on the OpenProject365 forms are in formXml.op365.test.ts. */
const parse = (over: Record<string, unknown> = {}) => parseForm('invented-all-features.xml', invented, { objecttypecode: 'account', ...over });

describe('formXml snapshots', () => {
  it('invented form with every feature', () => {
    expect(parse()).toMatchSnapshot();
  });
});

describe('project form', () => {
  it('timeline_control_lists_its_parameter_elements', () => {
    const f = rowOf(
      wrap(
        '<cell id="c"><labels/><control id="tl" classid="{06375649-c143-495e-a496-c962e5b4488e}"><parameters><RecordsPerPage>5</RecordsPerPage><X/></parameters></control></cell>',
      ),
    );
    expect(allCells(f)[0]!.control).toEqual({ kind: 'timeline', id: 'tl', classId: '{06375649-c143-495e-a496-c962e5b4488e}', parameterNames: ['RecordsPerPage', 'X'] });
  });

  it('subgrid_defaults_records_per_page_to_four', () => {
    const f = rowOf(
      wrap(
        '<cell id="c"><labels/><control id="g" classid="{e7a81278-8635-4d9e-8d4d-59480b391c5b}"><parameters><ViewId>{v}</ViewId><RelationshipName>r</RelationshipName><TargetEntityType>contact</TargetEntityType></parameters></control></cell>',
      ),
    );
    const sub = allCells(f)[0]!.control as SubgridControl;
    expect(sub.recordsPerPage).toBe(4);
    expect(sub.viewId).toBe('{v}');
  });
});

describe('dataset subgrid', () => {
  it('formXml_subgrid_with_dataset_customControl', () => {
    const f = rowOf(
      wrap(
        '<cell id="c"><labels/><control id="nodes" classid="{E7A81278-8635-4d9e-8D4D-59480B391C5B}" uniqueid="{D2842545-0000-0000-0000-000000000001}"><parameters><ViewId>{5a26f37e-0000-0000-0000-000000000002}</ViewId><IsUserView>false</IsUserView><RelationshipName>xv_project_node_projectid</RelationshipName><TargetEntityType>xv_node</TargetEntityType><EnableViewPicker>false</EnableViewPicker><EnableQuickFind>false</EnableQuickFind><RecordsPerPage>20</RecordsPerPage></parameters></control></cell>',
        '<controlDescriptions><controlDescription forControl="{d2842545-0000-0000-0000-000000000001}">' +
          '<customControl id="{E7A81278-8635-4d9e-8D4D-59480B391C5B}"><parameters><ViewId>{5a26f37e-0000-0000-0000-000000000002}</ViewId><RelationshipName>xv_project_node_projectid</RelationshipName><TargetEntityType>xv_node</TargetEntityType></parameters></customControl>' +
          '<customControl formFactor="0" name="xv_XV.TreeGrid"><parameters><data-set name="nodes">' +
          '<ViewId>{5a26f37e-0000-0000-0000-000000000002}</ViewId><IsUserView>false</IsUserView>' +
          '<RelationshipName>xv_project_node_projectid</RelationshipName><TargetEntityType>xv_node</TargetEntityType>' +
          '<EnableViewPicker>false</EnableViewPicker><EnableQuickFind>false</EnableQuickFind><RecordsPerPage>20</RecordsPerPage>' +
          '</data-set><title static="true" type="SingleLine.Text">Nodes</title></parameters></customControl>' +
          '</controlDescription></controlDescriptions>',
      ),
    );
    const sub = allCells(f)[0]!.control as SubgridControl;
    expect(sub).toMatchObject({ kind: 'subgrid', targetEntity: 'xv_node', recordsPerPage: 20 });
    expect(sub.customControl).toEqual({
      name: 'xv_XV.TreeGrid',
      parameters: { title: { value: 'Nodes', static: true, type: 'SingleLine.Text' } },
      dataSet: {
        name: 'nodes',
        parameters: {
          ViewId: '{5a26f37e-0000-0000-0000-000000000002}',
          IsUserView: 'false',
          RelationshipName: 'xv_project_node_projectid',
          TargetEntityType: 'xv_node',
          EnableViewPicker: 'false',
          EnableQuickFind: 'false',
          RecordsPerPage: '20',
        },
      },
    });
  });

  it('subgrid_without_control_description_has_no_custom_control', () => {
    const f = rowOf(
      wrap(
        '<cell id="c"><labels/><control id="g" classid="{e7a81278-8635-4d9e-8d4d-59480b391c5b}" uniqueid="{D2842545-0000-0000-0000-000000000009}"><parameters><TargetEntityType>contact</TargetEntityType></parameters></control></cell>',
      ),
    );
    expect((allCells(f)[0]!.control as SubgridControl).customControl).toBeUndefined();
  });
});

describe('labels', () => {
  it('cell_label_prefers_user_language', () => {
    expect(labelFor({ 1033: 'English', 1031: 'Deutsch' }, 1040)).toBe('English');
    expect(labelFor({ 1031: 'Deutsch' }, 1040)).toBeUndefined();
    expect(labelFor({}, 1033)).toBeUndefined();
  });
});

describe('visibility and spacers', () => {
  const f = parse();

  it('hidden_tab_section_cell_kept_with_visible_false', () => {
    expect(f.tabs.map((t) => [t.name, t.visible])).toEqual([
      ['general', true],
      ['hiddentab', false],
    ]);
    const sections = f.tabs[0]!.columns[0]!.sections;
    expect(sections.map((s) => [s.name, s.visible])).toEqual([
      ['main', true],
      ['secret', false],
    ]);
    const hidden = allCells(f).find((c) => c.id === '{11111111-0000-0000-0000-000000000005}')!;
    expect(hidden.visible).toBe(false);
    expect(sections[0]!.labelPosition).toBe('top');
    expect(sections[0]!.columns).toBe(2);
  });

  it('spacer_cell_has_no_control', () => {
    const spacer = allCells(f).find((c) => c.id === '{11111111-0000-0000-0000-000000000004}')!;
    expect(spacer.control).toBeUndefined();
    expect(spacer.showLabel).toBe(false);
  });

  it('header_footer_colspan_rowspan_and_defaults', () => {
    expect(f.header).toHaveLength(1);
    expect((f.header[0]!.control as FieldControl).attribute).toBe('ownerid');
    expect(f.footer).toHaveLength(1);
    expect((f.footer[0]!.control as FieldControl).disabled).toBe(true);
    const card = allCells(f).find((c) => c.id === '{11111111-0000-0000-0000-000000000006}')!;
    expect([card.colSpan, card.rowSpan]).toEqual([2, 4]);
    const name = allCells(f)[0]!;
    expect([name.showLabel, name.visible, name.rowSpan]).toEqual([true, true, 1]);
  });

  it('quick_view_cell_reads_forms_and_lookup', () => {
    const q = allCells(f).find((c) => c.control?.kind === 'quickview')!.control as QuickViewControl;
    expect(q.lookupAttribute).toBe('primarycontactid');
    expect(q.forms).toEqual({ contact: '{33333333-0000-0000-0000-000000000001}' });
  });
});

describe('display conditions', () => {
  it('display_conditions_give_order_and_roles', () => {
    const f = parse();
    expect(f.order).toBe(3);
    expect(f.roles).toEqual([
      '66666666-0000-0000-0000-000000000001',
      '66666666-0000-0000-0000-000000000002',
    ]);
  });

  it('role_ids_are_normalised_without_braces_in_lower_case', () => {
    const f = rowOf(wrap('', '<DisplayConditions Order="1"><Role Id="{ABCDEF00-0000-0000-0000-00000000000A}" /><Role Id="ABCDEF00-0000-0000-0000-00000000000B" /></DisplayConditions>'));
    expect(f.roles).toEqual(['abcdef00-0000-0000-0000-00000000000a', 'abcdef00-0000-0000-0000-00000000000b']);
  });

  it('everyone_means_no_roles', () => {
    const f = rowOf(wrap('', '<DisplayConditions Order="1"><Everyone /></DisplayConditions>'));
    expect(f.roles).toEqual([]);
    expect(f.order).toBe(1);
    const none = rowOf(wrap(''));
    expect(none.roles).toEqual([]);
    expect(none.order).toBe(Number.MAX_SAFE_INTEGER);
  });

  it('fallback_form_flag', () => {
    expect(rowOf(wrap('', '<DisplayConditions Order="1" FallbackForm="true"><Everyone /></DisplayConditions>')).fallback).toBe(true);
    expect(rowOf(wrap('', '<DisplayConditions Order="1" FallbackForm="false"><Everyone /></DisplayConditions>')).fallback).toBe(false);
    expect(rowOf(wrap('')).fallback).toBe(false);
  });
});

describe('controls', () => {
  it('custom_control_set_from_control_descriptions', () => {
    const inv = parse();
    const byAttr = (a: string) =>
      allCells(inv).find((c) => c.control?.kind === 'field' && c.control.attribute === a)!
        .control as FieldControl;
    expect(byAttr('progress').customControl).toEqual({
      name: 'inv_Invented.ProgressBar',
      parameters: { value: { value: 'progress', static: false } },
    });
    expect(byAttr('name').customControl).toBeUndefined();
  });

  const pcfCell = (descriptions: string) =>
    allCells(
      rowOf(
        wrap(
          '<cell id="c"><control id="f" classid="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}" datafieldname="xv_amount" uniqueid="{AAAA0000-0000-0000-0000-000000000001}" /></cell>',
          `<controlDescriptions><controlDescription forControl="{aaaa0000-0000-0000-0000-000000000001}"><customControl id="{4273EDBD-AC1D-40d3-9FB2-095C621B552D}"><parameters><datafieldname>xv_amount</datafieldname></parameters></customControl>${descriptions}</controlDescription></controlDescriptions>`,
        ),
      ),
    )[0]!.control as FieldControl;

  it('custom_control_prefers_the_web_form_factor_and_reads_static_parameters', () => {
    const control = pcfCell(
      '<customControl formFactor="2" name="xv_Phone.Ctl"><parameters><value>xv_amount</value></parameters></customControl>' +
        '<customControl name="xv_Any.Ctl"><parameters><value>xv_amount</value></parameters></customControl>' +
        '<customControl formFactor="0" name="xv_Web.Ctl"><parameters><value>xv_amount</value><maxValue type="Whole.None" static="true"> 100 </maxValue><caption static="false">xv_name</caption></parameters></customControl>',
    );
    expect(control.customControl).toEqual({
      name: 'xv_Web.Ctl',
      parameters: {
        value: { value: 'xv_amount', static: false },
        maxValue: { value: '100', static: true, type: 'Whole.None' },
        caption: { value: 'xv_name', static: false },
      },
    });
  });

  it('custom_control_without_form_factor_is_used_when_no_web_one_exists', () => {
    const control = pcfCell(
      '<customControl formFactor="1" name="xv_Tablet.Ctl"><parameters /></customControl><customControl name="xv_Any.Ctl"><parameters><value>xv_amount</value></parameters></customControl>',
    );
    expect(control.customControl?.name).toBe('xv_Any.Ctl');
  });

  it('custom_control_only_for_other_form_factors_keeps_the_standard_control', () => {
    const control = pcfCell(
      '<customControl formFactor="1" name="xv_Tablet.Ctl"><parameters /></customControl><customControl formFactor="2" name="xv_Phone.Ctl"><parameters /></customControl>',
    );
    expect(control.customControl).toBeUndefined();
    expect(pcfCell('').customControl).toBeUndefined();
  });

  it('unknown_classid_on_bound_cell_is_field', () => {
    const f = rowOf(
      wrap('<cell id="c"><control id="a" classid="{AAAAAAAA-0000-0000-0000-000000000000}" datafieldname="a" /></cell>'),
    );
    expect(allCells(f)[0]!.control).toMatchObject({ kind: 'field', attribute: 'a' });
  });

  it('unknown_classid_without_field_is_unknown', () => {
    const f = rowOf(
      wrap('<cell id="c"><control id="a" classid="{AAAAAAAA-0000-0000-0000-000000000000}" /></cell>'),
    );
    expect(allCells(f)[0]!.control).toEqual({
      kind: 'unknown',
      id: 'a',
      classId: '{AAAAAAAA-0000-0000-0000-000000000000}',
    });
  });

  it('classid_match_is_case_insensitive', () => {
    expect(controlKind('{06375649-c143-495e-a496-c962e5b4488e}')).toBe('timeline');
    expect(controlKind('{06375649-C143-495E-A496-C962E5B4488E}')).toBe('timeline');
    expect(controlKind('{e7a81278-8635-4d9e-8d4d-59480b391c5b}')).toBe('subgrid');
    expect(controlKind('{5c5600e0-1d6e-4205-a272-be80da87fd42}')).toBe('quickview');
    expect(controlKind('{FD2A7985-3187-444E-908D-6624B21F69C0}')).toBe('webresource');
    expect(controlKind('{9fdf5f91-88b1-47f4-ad53-c11efc01a01d}')).toBe('webresource');
    expect(controlKind('{4273EDBD-AC1D-40d3-9FB2-095C621B552D}')).toBe('field');
  });
});

describe('web resource and IFRAME controls', () => {
  const control = (classId: string, params: string) =>
    rowOf(wrap(`<cell id="c"><control id="w" classid="${classId}"><parameters>${params}</parameters></control></cell>`));

  it('web_resource_parameters_are_read', () => {
    const f = control(
      '{9FDF5F91-88B1-47F4-AD53-C11EFC01A01D}',
      '<Url>op365_/pages/x.html</Url><Data>a=1</Data><PassParameters>true</PassParameters><Security>false</Security><Scrolling>no</Scrolling><Border>true</Border>',
    );
    expect(allCells(f)[0]!.control).toEqual({
      kind: 'webresource',
      id: 'w',
      classId: '{9FDF5F91-88B1-47F4-AD53-C11EFC01A01D}',
      iframe: false,
      url: 'op365_/pages/x.html',
      data: 'a=1',
      passParameters: true,
      restrictScripting: false,
      scrolling: 'no',
      border: true,
    });
  });

  it('iframe_parameters_and_defaults', () => {
    const f = control('{fd2a7985-3187-444e-908d-6624b21f69c0}', '<Url>https://example.org/</Url><Security>true</Security><Scrolling>yes</Scrolling>');
    expect(allCells(f)[0]!.control).toMatchObject({ kind: 'webresource', iframe: true, url: 'https://example.org/', restrictScripting: true, scrolling: 'yes', passParameters: false, border: false, data: '' });
    expect(allCells(control('{FD2A7985-3187-444E-908D-6624B21F69C0}', ''))[0]!.control).toMatchObject({ url: '', scrolling: 'auto' });
  });
});

describe('scripts', () => {
  it('scripts_detected_from_form_libraries_or_events', () => {
    expect(
      rowOf(wrap('', '<events><event name="onload"><Handlers /></event></events>')).hasScripts,
    ).toBe(true);
    expect(rowOf(wrap('', '<formLibraries><Library name="a.js" /></formLibraries>')).hasScripts).toBe(true);
    expect(rowOf(wrap('', '<formLibraries /><events />')).hasScripts).toBe(false);
  });
});

describe('row mapping', () => {
  it('maps_row_columns_and_boolean_isdefault', () => {
    const f = parse({ isdefault: true, type: 2 });
    expect(f).toMatchObject({ id: '{00000000-0000-0000-0000-000000000001}', entity: 'account', type: 2, isDefault: true });
    expect(rowOf(wrap('')).isDefault).toBe(false);
  });

  it('malformed_xml_throws', () => {
    expect(() => rowOf('<form><tabs>')).toThrow(/Malformed XML/);
  });
});

describe('libraries and events', () => {
  const inventedXml = wrap(
    '',
    `<formLibraries><Library name="b_/one.js"/><Library name="b_/two.js"/></formLibraries>
     <events>
       <event name="onload"><Handlers><Handler functionName="A.load" libraryName="b_/one.js" enabled="true" parameters="" passExecutionContext="true"/></Handlers></event>
       <event name="onsave"><Handlers><Handler functionName="A.save" libraryName="b_/two.js" enabled="false" parameters="1,&quot;x&quot;" passExecutionContext="false"/></Handlers></event>
       <event name="onchange" attribute="name"><Handlers><Handler functionName="A.chg" libraryName="b_/two.js" enabled="true" parameters="" passExecutionContext="true"/></Handlers></event>
       <event name="onrecordselect" attribute="x"><Handlers><Handler functionName="A.no" libraryName="b_/one.js" enabled="true" parameters="" passExecutionContext="true"/></Handlers></event>
     </events>`,
  );

  it('libraries_in_order', () => {
    expect(rowOf(inventedXml).libraries).toEqual(['b_/one.js', 'b_/two.js']);
  });

  it('onload_onsave_handlers', () => {
    const invented = rowOf(inventedXml);
    expect(invented.events.map((e) => e.name)).toEqual(['onload', 'onsave', 'onchange']);
    expect(invented.events[1]!.handlers[0]).toMatchObject({ functionName: 'A.save', parameters: '1,"x"', passExecutionContext: false });
  });

  it('onchange_event_has_attribute', () => {
    const change = rowOf(inventedXml).events.find((e) => e.name === 'onchange');
    expect(change?.attribute).toBe('name');
    expect(rowOf(inventedXml).events.find((e) => e.name === 'onload')?.attribute).toBeUndefined();
  });

  it('disabled_handler_kept_with_enabled_false', () => {
    const save = rowOf(inventedXml).events.find((e) => e.name === 'onsave');
    expect(save?.handlers).toHaveLength(1);
    expect(save?.handlers[0]!.enabled).toBe(false);
  });
});

describe('navigation', () => {
  const title = (text: string) => `<Titles><Title LCID="1033" Text="${text}" /><Title LCID="1040" Text="${text} IT" /></Titles>`;
  it('related_items_in_sequence_order_without_hidden_ones', () => {
    const f = rowOf(
      wrap(
        '',
        `<Navigation><NavBar>
          <NavBarByRelationshipItem RelationshipName="xv_project_tasks" Id="nav_tasks" Sequence="20">${title('Tasks')}</NavBarByRelationshipItem>
          <NavBarByRelationshipItem RelationshipName="xv_project_notes" Id="nav_notes" Sequence="10">${title('Notes')}</NavBarByRelationshipItem>
          <NavBarByRelationshipItem RelationshipName="xv_hidden" Id="nav_hidden" Sequence="5" Show="false">${title('Hidden')}</NavBarByRelationshipItem>
          <NavBarItem Id="navLink" Url="https://example.org" Sequence="30">${title('Site')}</NavBarItem>
        </NavBar></Navigation>`,
      ),
    );
    expect(f.navigation).toEqual([
      { kind: 'relationship', id: 'nav_notes', relationship: 'xv_project_notes', labels: { 1033: 'Notes', 1040: 'Notes IT' } },
      { kind: 'relationship', id: 'nav_tasks', relationship: 'xv_project_tasks', labels: { 1033: 'Tasks', 1040: 'Tasks IT' } },
      { kind: 'unsupported', id: 'navLink', labels: { 1033: 'Site', 1040: 'Site IT' } },
    ]);
  });

  it('no_navigation_no_items', () => {
    expect(rowOf(wrap('')).navigation).toEqual([]);
  });
});

describe('section columns', () => {
  const sectionWith = (columns: string) =>
    rowOf(
      `<form><tabs><tab name="t"><labels/><columns><column width="100%"><sections><section name="s" columns="${columns}"><labels/><rows><row/></rows></section></sections></column></columns></tab></tabs></form>`,
    ).tabs[0]!.columns[0]!.sections[0]!;

  it('a_digit_per_column_gives_the_count_and_the_relative_widths', () => {
    // "111" is three equal columns (a dashboard's charts), "12" two columns of one third and two thirds.
    expect([sectionWith('111').columns, sectionWith('111').widths]).toEqual([3, [1, 1, 1]]);
    expect([sectionWith('1111').columns, sectionWith('1111').widths]).toEqual([4, [1, 1, 1, 1]]);
    expect([sectionWith('12').columns, sectionWith('12').widths]).toEqual([2, [1, 2]]);
  });

  it('a_single_digit_is_the_count_of_equal_columns', () => {
    expect([sectionWith('1').columns, sectionWith('1').widths]).toEqual([1, undefined]);
    expect([sectionWith('3').columns, sectionWith('3').widths]).toEqual([3, undefined]);
    expect([sectionWith('').columns, sectionWith('x').columns]).toEqual([1, 1]);
  });

  it('several_digits_with_a_zero_are_invalid_and_give_one_column', () => {
    expect([sectionWith('10').columns, sectionWith('10').widths]).toEqual([1, undefined]);
    expect([sectionWith('101').columns, sectionWith('101').widths]).toEqual([1, undefined]);
  });
});
