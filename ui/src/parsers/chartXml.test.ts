import { describe, expect, it } from 'vitest';
import type { SubgridControl } from '../model/form';
import { ChartTableMismatchError, mergeChartFetch, parseChart, parseDashboard, type ChartRow } from './chartXml';

const fetchXml = `<fetch mapping="logical" aggregate="true"><entity name="xv_order">
  <attribute name="xv_amount" aggregate="sum" alias="aggregate_column" />
  <attribute name="xv_vendorid" groupby="true" alias="groupby_column" />
</entity></fetch>`;

const data = `<datadescription><datadefinition><fetchcollection>${fetchXml}</fetchcollection>
<categorycollection><category><measurecollection><measure alias="aggregate_column" /></measurecollection></category></categorycollection>
</datadefinition></datadescription>`;

const presentation = (type: string, extra = ''): string =>
  `<presentationdescription><Chart Palette="None" PaletteCustomColors="15,108,189; 0,120,212; 1,2,3; 4,5,6; 7,8,9; 10,11,12"${extra}>
<Series><Series ChartType="${type}" IsValueShownAsLabel="True" /></Series>
<ChartAreas><ChartArea><AxisY /><AxisX /></ChartArea></ChartAreas></Chart></presentationdescription>`;

const row = (type: string, over: Partial<ChartRow> = {}): ChartRow => ({
  name: 'Amount by Vendor',
  primaryentitytypecode: 'xv_order',
  datadescription: data,
  presentationdescription: presentation(type),
  charttype: 0,
  _webresourceid_value: null,
  ...over,
});

describe('parseChart', () => {
  it('parses_bar_chart_with_group_and_sum', () => {
    const chart = parseChart(row('Bar'));
    expect(chart).toMatchObject({
      name: 'Amount by Vendor',
      table: 'xv_order',
      categoryAlias: 'groupby_column',
      measures: [{ alias: 'aggregate_column', series: 0 }],
      series: [{ type: 'Bar', showValues: true }],
    });
    expect(chart.unsupported).toBeUndefined();
    expect(chart.palette).toHaveLength(6);
    expect(chart.palette![0]).toBe('rgb(15, 108, 189)');
    expect(chart.fetchXml).toContain('aggregate="true"');
  });

  it('doughnut_and_pie_are_supported', () => {
    for (const type of ['Doughnut', 'Pie', 'Column', 'Line', 'Area']) expect(parseChart(row(type)).unsupported).toBeUndefined();
  });

  it('stacked_column_is_unsupported_with_message', () => {
    expect(parseChart(row('StackedColumn')).unsupported).toEqual({
      kind: 'chartType',
      type: 'StackedColumn',
      message: 'Chart type StackedColumn is not supported in this version',
    });
  });

  it('two_group_by_attributes_are_unsupported_with_message', () => {
    const two = data.replace(
      '<attribute name="xv_vendorid" groupby="true" alias="groupby_column" />',
      '<attribute name="xv_vendorid" groupby="true" alias="groupby_column" /><attribute name="statecode" groupby="true" alias="state" />',
    );
    expect(parseChart(row('Column', { datadescription: two })).unsupported).toEqual({
      kind: 'multipleGroupBy',
      message: 'Charts with more than one group-by are not supported in this version.',
    });
  });

  it('two_categories_are_unsupported_with_message', () => {
    const two = data.replace('</category></categorycollection>', '</category><category><measurecollection><measure alias="aggregate_column" /></measurecollection></category></categorycollection>');
    expect(parseChart(row('Column', { datadescription: two })).unsupported?.kind).toBe('multipleGroupBy');
  });

  it('power_bi_and_web_resource_charts_are_unsupported', () => {
    const kind = { kind: 'chartKind', message: 'This chart kind is not supported in this version' };
    expect(parseChart(row('Bar', { charttype: 1, datadescription: '' })).unsupported).toEqual(kind);
    expect(parseChart(row('Bar', { _webresourceid_value: '{22222222-0000-0000-0000-000000000001}' })).unsupported).toEqual(kind);
  });

  it('empty_web_resource_lookup_is_not_a_web_resource_chart', () => {
    expect(parseChart(row('Bar', { _webresourceid_value: '' })).unsupported).toBeUndefined();
  });

  it('unreadable_datadescription_gives_unsupported_instead_of_throwing', () => {
    for (const datadescription of ['', '<a><b></a>']) {
      const chart = parseChart(row('Bar', { datadescription }));
      expect(chart.unsupported?.kind).toBe('unreadable');
      expect(chart.unsupported?.message).toMatch(/^Chart 'Amount by Vendor' could not be read: .+/);
    }
  });

  it('missing_lookup_key_and_palette_absent', () => {
    const r = row('Bar', { presentationdescription: '<presentationdescription><Chart><Series><Series ChartType="Bar"/></Series></Chart></presentationdescription>' });
    delete r._webresourceid_value;
    const chart = parseChart(r);
    expect(chart.unsupported).toBeUndefined();
    expect(chart.palette).toBeNull();
    expect(chart.series).toEqual([{ type: 'Bar', showValues: false }]);
  });
});

describe('mergeChartFetch', () => {
  const view = `<fetch version="1.0"><entity name="xv_order">
    <attribute name="xv_name" /><attribute name="xv_amount" />
    <order attribute="xv_name" descending="false" />
    <filter type="and"><condition attribute="statuscode" operator="ne" value="365000003" /></filter>
    <link-entity name="xv_vendor" from="xv_vendorid" to="xv_vendorid" alias="v" link-type="inner">
      <filter><condition attribute="xv_active" operator="eq" value="1" /></filter>
    </link-entity>
    <link-entity name="xv_project" from="xv_projectid" to="xv_projectid" alias="p">
      <attribute name="xv_name" /><order attribute="xv_name" />
    </link-entity>
  </entity></fetch>`;

  it('merges_view_filter_and_links_into_chart_fetch', () => {
    const merged = mergeChartFetch(fetchXml, view, 'Amount by Vendor');
    const doc = new DOMParser().parseFromString(merged, 'application/xml');
    expect(doc.documentElement.getAttribute('aggregate')).toBe('true');
    expect(doc.getElementsByTagName('filter')).toHaveLength(2);
    expect(doc.querySelector('condition[attribute="statuscode"]')).not.toBeNull();
    expect(doc.querySelector('link-entity[alias="v"]')).not.toBeNull();
    expect(doc.querySelector('link-entity[alias="p"]')).not.toBeNull();
    expect(doc.getElementsByTagName('order')).toHaveLength(0);
    expect(doc.querySelector('attribute[name="xv_name"]')).toBeNull();
    expect(doc.querySelectorAll('entity > attribute')).toHaveLength(2);
  });

  it('keeps_every_view_link_with_nested_filters_and_no_attributes', () => {
    const v = `<fetch><entity name="xv_order">
      <link-entity name="xv_vendor" from="xv_vendorid" to="xv_vendorid" alias="outer">
        <filter><condition attribute="xv_active" operator="eq" value="1" /></filter>
        <link-entity name="xv_region" from="xv_regionid" to="xv_regionid" alias="inner">
          <attribute name="xv_name" /><all-attributes /><order attribute="xv_name" />
        </link-entity>
      </link-entity></entity></fetch>`;
    const doc = new DOMParser().parseFromString(mergeChartFetch(fetchXml, v, 'c'), 'application/xml');
    expect(doc.querySelector('link-entity[alias="outer"] > filter')).not.toBeNull();
    expect(doc.querySelector('link-entity[alias="outer"] > link-entity[alias="inner"]')).not.toBeNull();
    expect(doc.querySelectorAll('link-entity attribute, link-entity all-attributes, link-entity order')).toHaveLength(0);
  });

  it('renames_view_link_alias_that_collides_and_rewrites_entityname', () => {
    const chart = fetchXml.replace('</entity>', '<link-entity name="xv_vendor" from="a" to="b" alias="v" /></entity>');
    const v = `<fetch><entity name="xv_order">
      <filter><condition entityname="v" attribute="xv_active" operator="eq" value="1" /></filter>
      <link-entity name="xv_vendor" from="xv_vendorid" to="xv_vendorid" alias="v"><filter><condition attribute="x" operator="eq" value="1" /></filter></link-entity>
      <link-entity name="xv_other" from="a" to="b" alias="groupby_column" /></entity></fetch>`;
    const doc = new DOMParser().parseFromString(mergeChartFetch(chart, v, 'c'), 'application/xml');
    const aliases = Array.from(doc.getElementsByTagName('link-entity')).map((l) => l.getAttribute('alias'));
    expect(aliases).toEqual(['v', 'v_v', 'groupby_column_v']);
    expect(doc.querySelector('entity > filter > condition')!.getAttribute('entityname')).toBe('v_v');
  });

  it('chart_and_view_on_different_tables_throws', () => {
    expect(() => mergeChartFetch(fetchXml, '<fetch><entity name="xv_vendor"/></fetch>', 'Amount by Vendor')).toThrow(
      "Chart 'Amount by Vendor' is for table xv_order, the view is for xv_vendor",
    );
    expect(() => mergeChartFetch(fetchXml, '<fetch><entity name="xv_vendor"/></fetch>', 'Amount by Vendor')).toThrow(ChartTableMismatchError);
  });
});

describe('parseDashboard', () => {
  const sub = (params: string): string =>
    `<cell id="c" rowspan="10" colspan="1"><labels/><control id="s1" classid="{E7A81278-8635-4d9e-8D4D-59480B391C5B}"><parameters>${params}</parameters></control></cell>`;
  const dash = (cells: string, attrs = ''): string =>
    `<form ${attrs}><tabs><tab name="t"><labels/><columns><column width="100%"><sections><section name="s" columns="1"><labels/><rows><row>${cells}</row></rows></section></sections></column></columns></tab></tabs></form>`;

  it('parses_dashboard_subgrid_chart_parameters', () => {
    const d = parseDashboard(
      dash(
        sub(
          '<ViewId>{33333333-0000-0000-0000-000000000001}</ViewId><TargetEntityType>xv_order</TargetEntityType><ChartGridMode>Chart</ChartGridMode><VisualizationId>{11111111-0000-0000-0000-000000000001}</VisualizationId><EnableChartPicker>true</EnableChartPicker><EnableViewPicker>false</EnableViewPicker>',
        ) + sub('<ChartGridMode>Grid</ChartGridMode><RecordsPerPage>10</RecordsPerPage><TargetEntityType>xv_order</TargetEntityType>'),
      ),
      1033,
    );
    const [chart, grid] = d.tabs[0]!.columns[0]!.sections[0]!.rows[0]!.map((c) => c.control as SubgridControl);
    expect(chart).toMatchObject({
      chartGridMode: 'Chart',
      visualizationId: '{11111111-0000-0000-0000-000000000001}',
      targetEntity: 'xv_order',
      pickers: true,
    });
    expect(grid).toMatchObject({ chartGridMode: 'Grid', recordsPerPage: 10 });
    expect(grid!.pickers).toBeUndefined();
    expect(d.entity).toBe('none');
    expect(d.interactive).toBe(false);
  });

  it('flags_interactive_dashboard', () => {
    expect(parseDashboard(dash('', 'FormPresentation="1"'), 1033).interactive).toBe(true);
    expect(parseDashboard(dash('<cell id="x"><labels/><stream/></cell>'), 1033).interactive).toBe(true);
    expect(parseDashboard(dash('', 'FormPresentation="0"'), 1033).interactive).toBe(false);
  });
});
