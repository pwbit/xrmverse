import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { onPath } from '../admin/security/testSupport';
import { rawAttr, rawEntity } from '../admin/tables/tableTestSupport';
import { resetPcfLoader, setScriptLoader } from '../pcf/loader';
import { App } from '../shell/App';
import { setUiLanguage } from '../shell/i18n/t';
import { probeDatasetControl, serveControls } from '../test/pcf/serve';
import { server } from '../test/server';
import { mockApps, ROLE_SALES, SITEMAP_ID, titled, type MockSystemForm } from './testSupport';

const FV = '@OData.Community.Display.V1.FormattedValue';
const APP_ID = '11111111-0000-0000-0000-000000000001';
const SPEND = 'd0000000-0000-0000-0000-000000000001';
const OTHER = 'd0000000-0000-0000-0000-000000000002';
const CHART_ID = '11111111-0000-0000-0000-0000000000c1';
const VIEW_ID = '22222222-0000-0000-0000-000000000001';
const SUBGRID = '{E7A81278-8635-4d9e-8D4D-59480B391C5B}';

const globals = window as unknown as Record<string, unknown>;

beforeEach(() => resetPcfLoader());
afterEach(() => {
  sessionStorage.clear();
  setUiLanguage(undefined);
  setScriptLoader(undefined);
  resetPcfLoader();
  for (const k of ['ComponentFramework', '__xvProbe']) delete globals[k];
});

const labels = (text: string) => `<labels><label description="${text}" languagecode="1033" /></labels>`;

interface CellSpec {
  id: string;
  label: string;
  /** The `<control>` element. */
  control: string;
}

const subgrid = (id: string, params: string) => `<control id="${id}" classid="${SUBGRID}"><parameters>${params}</parameters></control>`;
const chartCell = (id = 'chart1', viewId: string | undefined = VIEW_ID, extra = ''): CellSpec => ({
  id: `cell-${id}`,
  label: 'Spend by Vendor',
  control: subgrid(
    id,
    `${viewId ? `<ViewId>{${viewId}}</ViewId>` : ''}<TargetEntityType>xv_order</TargetEntityType><ChartGridMode>Chart</ChartGridMode><VisualizationId>{${CHART_ID.toUpperCase()}}</VisualizationId>${extra}`,
  ),
});
const gridCell = (id = 'grid1', viewId = VIEW_ID, extra = ''): CellSpec => ({
  id: `cell-${id}`,
  label: 'Open Orders',
  control: subgrid(id, `<ViewId>{${viewId}}</ViewId><TargetEntityType>xv_order</TargetEntityType><ChartGridMode>Grid</ChartGridMode><RecordsPerPage>7</RecordsPerPage>${extra}`),
});
const timelineCell = (): CellSpec => ({
  id: 'cell-timeline',
  label: 'Activity',
  control: '<control id="tl" classid="{06375649-c143-495e-a496-c962e5b4488e}" />',
});

/** A dashboard of one tab: one section per entry of `sections`, each cell on its own row. */
function dashboardXml(sections: { label: string; cells: CellSpec[] }[], attrs = '', descriptions = ''): string {
  const body = sections
    .map(
      (s, i) =>
        `<section name="s${i}" showlabel="true" columns="1">${labels(s.label)}<rows>${s.cells
          .map((c) => `<row><cell id="${c.id}" showlabel="true" colspan="1" rowspan="8">${labels(c.label)}${c.control}</cell></row>`)
          .join('')}</rows></section>`,
    )
    .join('');
  return `<form${attrs}><tabs><tab name="main" showlabel="false">${labels('Main')}<columns><column width="100%"><sections>${body}</sections></column></columns></tab></tabs>${descriptions}</form>`;
}

const SPEND_XML = dashboardXml([
  { label: 'Charts', cells: [chartCell()] },
  { label: 'Lists', cells: [gridCell()] },
]);

const dashboard = (systemformid: string, name: string, formxml: string): MockSystemForm => ({ systemformid, name, type: 0, objecttypecode: 'none', formxml });

const VIEW_ROW = {
  savedqueryid: VIEW_ID,
  name: 'Open Orders',
  returnedtypecode: 'xv_order',
  fetchxml: `<fetch version="1.0" mapping="logical"><entity name="xv_order"><attribute name="xv_name" /><attribute name="xv_orderid" /><order attribute="xv_name" descending="false" /><filter type="and"><condition attribute="statecode" operator="eq" value="0" /></filter></entity></fetch>`,
  layoutxml:
    '<grid name="resultset" object="10001" jump="xv_name" select="1" icon="1" preview="1"><row name="result" id="xv_orderid"><cell name="xv_name" width="200" /></row></grid>',
};

const CHART_ROW = {
  savedqueryvisualizationid: CHART_ID,
  name: 'Spend by Vendor',
  primaryentitytypecode: 'xv_order',
  charttype: 0,
  _webresourceid_value: null,
  datadescription: `<datadescription><datadefinition><fetchcollection><fetch mapping="logical" aggregate="true"><entity name="xv_order">
    <attribute name="xv_amount" aggregate="sum" alias="aggregate_column" /><attribute name="xv_vendorid" groupby="true" alias="groupby_column" />
    </entity></fetch></fetchcollection><categorycollection><category><measurecollection><measure alias="aggregate_column" /></measurecollection></category></categorycollection></datadefinition></datadescription>`,
  presentationdescription: `<Chart Palette="None"><Series><Series ChartType="Bar" IsValueShownAsLabel="True" /></Series><ChartAreas><ChartArea /></ChartAreas></Chart>`,
};

const AGGREGATE_ROWS = [
  { groupby_column: 'v1', [`groupby_column${FV}`]: 'Contoso', aggregate_column: 120, [`aggregate_column${FV}`]: '120' },
  { groupby_column: 'v2', [`groupby_column${FV}`]: 'Fabrikam', aggregate_column: 30, [`aggregate_column${FV}`]: '30' },
];

const SITEMAP = `<SiteMap><Area Id="a">${titled('Work')}<Group Id="g">${titled('Dashboards')}
  <SubArea Id="spend" Url="/main.aspx?pagetype=dashboard&amp;id=%7b${SPEND.toUpperCase()}%7d&amp;type=system">${titled('Spend')}</SubArea>
  <SubArea Id="orders" Entity="xv_order">${titled('Orders')}</SubArea>
</Group></Area></SiteMap>`;

interface Options {
  forms?: MockSystemForm[];
  appForms?: string[];
  siteMapXml?: string;
}

/** Signs in, serves the app, its dashboards, the view and the chart; returns the FetchXML of every query. */
function serve({ forms = [dashboard(SPEND, 'Spend', SPEND_XML), dashboard(OTHER, 'Another', dashboardXml([{ label: 'Other lists', cells: [gridCell('g2')] }]))], appForms = [SPEND, OTHER], siteMapXml = SITEMAP }: Options = {}) {
  const fetches: Element[] = [];
  const requests = mockApps({
    apps: [{ appmoduleid: APP_ID, name: 'Sales Hub', roles: [ROLE_SALES], siteMapId: SITEMAP_ID, forms: appForms }],
    userRoles: [ROLE_SALES],
    privileges: ['prvReadxv_Order', 'prvCreatexv_Order', 'prvAppendToxv_Order'],
    schemaNames: { xv_order: 'xv_Order' },
    siteMapXml,
    systemForms: forms,
    savedQueries: [{ id: VIEW_ID, name: VIEW_ROW.name, entity: 'xv_order', isdefault: true, fetchxml: VIEW_ROW.fetchxml, layoutxml: VIEW_ROW.layoutxml }],
    tables: { xv_order: { entitySetName: 'xv_orders', primaryId: 'xv_orderid', primaryName: 'xv_name', labels: { xv_name: { 1033: 'Order Name' } } } },
    records: (_set, fetch) => {
      fetches.push(fetch);
      if (fetch.getAttribute('aggregate') === 'true') return { value: AGGREGATE_ROWS };
      return { value: [{ xv_orderid: 'f0000000-0000-0000-0000-000000000009', xv_name: 'Order 9' }] };
    },
  });
  server.use(
    http.get('*/api/data/v9.2/*', ({ request }) => {
      const path = decodeURIComponent(new URL(request.url).pathname.replace(/^.*\/api\/data\/v9\.2\//, ''));
      if (path === `savedqueryvisualizations(${CHART_ID})`) return HttpResponse.json(CHART_ROW);
      if (path === `savedqueries(${VIEW_ID})`) return HttpResponse.json(VIEW_ROW);
      if (/^savedqueries\(/.test(path) || /^savedqueryvisualizations\(/.test(path))
        return HttpResponse.json({ error: { code: '0x80040217', message: 'Does Not Exist' } }, { status: 404 });
      return undefined;
    }),
  );
  return { fetches, requests };
}

const renderAt = (query: string) => render(<App initialEntries={[`/main.aspx?appid=${APP_ID}&pagetype=dashboard${query}`]} />);

/** The fetches that list rows (not the chart's aggregate). */
const gridFetches = (fetches: Element[]) => fetches.filter((f) => f.getAttribute('aggregate') !== 'true');

describe('DashboardPage', () => {
  it('opens_dashboard_by_id_and_lays_out_sections', async () => {
    serve();
    renderAt(`&id=%7B${SPEND.toUpperCase()}%7D&type=system`);
    expect(await screen.findByRole('heading', { name: 'Spend' })).toBeInTheDocument();
    const charts = await screen.findByRole('group', { name: 'Charts' });
    const lists = screen.getByRole('group', { name: 'Lists' });
    expect(charts.compareDocumentPosition(lists) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(await within(charts).findByRole('figure', { name: 'Spend by Vendor' })).toBeInTheDocument();
    expect(within(lists).getByText('Open Orders')).toBeInTheDocument();
    expect(await within(lists).findByText('Order 9')).toBeInTheDocument();
    // The sitemap marks the dashboard as the current page.
    const nav = screen.getByRole('navigation', { name: 'Sales Hub' });
    expect(within(nav).getByRole('link', { current: 'page' })).toHaveTextContent('Spend');
  });

  it('no_id_opens_first_dashboard_of_app', async () => {
    serve();
    renderAt('');
    expect(await screen.findByRole('heading', { name: 'Spend' })).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Dashboard' })).toHaveValue(SPEND);
    // The address now names the dashboard: the sitemap marks it as the current page.
    const nav = screen.getByRole('navigation', { name: 'Sales Hub' });
    await waitFor(() => expect(within(nav).getByRole('link', { current: 'page' })).toHaveTextContent('Spend'));
  });

  it('app_without_dashboards_shows_message', async () => {
    serve({ forms: [], appForms: [], siteMapXml: `<SiteMap><Area Id="a">${titled('Work')}<Group Id="g">${titled('G')}<SubArea Id="orders" Entity="xv_order">${titled('Orders')}</SubArea></Group></Area></SiteMap>` });
    renderAt('');
    expect(await screen.findByText('This app has no dashboards.')).toBeInTheDocument();
  });

  it('selector_lists_app_dashboards_and_changes_url', async () => {
    serve();
    renderAt(`&id=${SPEND}`);
    const select = await screen.findByRole('combobox', { name: 'Dashboard' });
    await waitFor(() => expect(within(select).getAllByRole('option').map((o) => o.textContent)).toEqual(['Spend', 'Another']));
    await userEvent.selectOptions(select, OTHER);
    expect(await screen.findByRole('heading', { name: 'Another' })).toBeInTheDocument();
    expect(await screen.findByRole('group', { name: 'Other lists' })).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Dashboard' })).toHaveValue(OTHER);
  });

  it('grid_cell_runs_view_without_parent_filter', async () => {
    const { fetches } = serve();
    renderAt(`&id=${SPEND}`);
    expect(await screen.findByText('Order 9')).toBeInTheDocument();
    const [fetch] = gridFetches(fetches);
    expect(fetch).toBeDefined();
    expect(fetch!.getAttribute('count')).toBe('7');
    expect(fetch!.getAttribute('page')).toBe('1');
    // Only the view's own condition: no relationship condition and no link to a parent record.
    expect(Array.from(fetch!.getElementsByTagName('condition')).map((c) => c.getAttribute('attribute'))).toEqual(['statecode']);
    expect(fetch!.getElementsByTagName('link-entity')).toHaveLength(0);
    // No New or Add existing without a parent record.
    expect(screen.queryByRole('toolbar', { name: /commands/ })).toBeNull();
    // A row opens the record.
    await userEvent.click(screen.getByText('Order 9'));
    const nav = screen.getByRole('navigation', { name: 'Sales Hub' });
    await waitFor(() => expect(within(nav).getByRole('link', { current: 'page' })).toHaveTextContent('Orders'));
    expect(screen.queryByRole('heading', { name: 'Spend' })).toBeNull();
  });

  it('chart_cell_renders_chart', async () => {
    const { fetches } = serve();
    renderAt(`&id=${SPEND}`);
    const svg = await screen.findByRole('figure', { name: 'Spend by Vendor' });
    expect(svg).toHaveAttribute('data-chart-kind', 'horizontalBar');
    const aggregate = fetches.find((f) => f.getAttribute('aggregate') === 'true')!;
    // The view's filter limits the chart (ruling F5).
    expect(aggregate.querySelector('entity > filter > condition')?.getAttribute('attribute')).toBe('statecode');
  });

  it('dataset_pcf_cell_loads_without_parent', async () => {
    const seen: { ids: string[]; loading: boolean }[] = [];
    const modes: { disabled: boolean; pageReadOnly: boolean }[] = [];
    globals.__xvProbe = class {
      init() {}
      updateView(context: {
        parameters: { records: { loading: boolean; sortedRecordIds: string[] } };
        mode: { isControlDisabled: boolean };
        page: { isPageReadOnly: boolean };
      }) {
        modes.push({ disabled: context.mode.isControlDisabled, pageReadOnly: context.page.isPageReadOnly });
        seen.push({ loading: context.parameters.records.loading, ids: [...context.parameters.records.sortedRecordIds] });
      }
      getOutputs() {
        return {};
      }
      destroy() {}
    };
    const control = probeDatasetControl('DashProbe');
    const pcf = `<cell id="cell-pcf" showlabel="true" colspan="1" rowspan="8">${labels('Load')}${subgrid('pcf1', `<ViewId>{${VIEW_ID}}</ViewId><TargetEntityType>xv_order</TargetEntityType><RecordsPerPage>5</RecordsPerPage>`).replace('<control ', `<control uniqueid="{aa000000-0000-0000-0000-000000000001}" `)}</cell>`;
    const xml = `<form><tabs><tab name="main">${labels('Main')}<columns><column width="100%"><sections><section name="s" showlabel="true" columns="1">${labels('Resources')}<rows><row>${pcf}</row></rows></section></sections></column></columns></tab></tabs>
      <controlDescriptions><controlDescription forControl="{aa000000-0000-0000-0000-000000000001}">
      <customControl id="${SUBGRID}"><parameters><ViewId>{${VIEW_ID}}</ViewId><TargetEntityType>xv_order</TargetEntityType></parameters></customControl>
      <customControl formFactor="0" name="${control.name}"><parameters><data-set name="records"><ViewId>{${VIEW_ID}}</ViewId><TargetEntityType>xv_order</TargetEntityType><RecordsPerPage>5</RecordsPerPage></data-set></parameters></customControl>
      </controlDescription></controlDescriptions></form>`;
    const { fetches } = serve({ forms: [dashboard(SPEND, 'Spend', xml)] });
    serveControls([control]);
    server.use(
      onPath('get', "EntityDefinitions(LogicalName='xv_order')", () =>
        HttpResponse.json(
          rawEntity(
            'xv_order',
            'xv_orders',
            [rawAttr('xv_orderid', 'Uniqueidentifier', { IsPrimaryId: true }), rawAttr('xv_name', 'String', { IsPrimaryName: true, display: 'Order Name' })],
            { PrimaryNameAttribute: 'xv_name' },
          ),
        ),
      ),
    );
    renderAt(`&id=${SPEND}`);
    await waitFor(() => expect(seen.at(-1)).toEqual({ loading: false, ids: ['f0000000-0000-0000-0000-000000000009'] }));
    // The control's page (RecordsPerPage 5); a late request of the previous test's grid can reach this test's handler.
    const ofControl = gridFetches(fetches).filter((f) => f.getAttribute('count') === '5');
    expect(ofControl).toHaveLength(1);
    const fetch = ofControl[0];
    expect(Array.from(fetch!.getElementsByTagName('condition')).map((c) => c.getAttribute('attribute'))).toEqual(['statecode']);
    // A dashboard is not a read-only page: the control is enabled.
    expect(modes.at(-1)).toEqual({ disabled: false, pageReadOnly: false });
    // The control runs: no classic grid in its place.
    expect(screen.queryByRole('grid')).toBeNull();
  });

  it('unsupported_cell_shows_notice_and_others_render', async () => {
    serve({ forms: [dashboard(SPEND, 'Spend', dashboardXml([{ label: 'Mixed', cells: [timelineCell(), gridCell()] }]))] });
    renderAt(`&id=${SPEND}`);
    expect(await screen.findByText('This dashboard component is not supported in this version')).toBeInTheDocument();
    expect(await screen.findByText('Order 9')).toBeInTheDocument();
  });

  it('missing_view_cell_shows_not_found', async () => {
    const MISSING = '99999999-0000-0000-0000-000000000099';
    serve({ forms: [dashboard(SPEND, 'Spend', dashboardXml([{ label: 'Mixed', cells: [gridCell('g1', MISSING), chartCell('c1', MISSING), gridCell('g2')] }]))] });
    renderAt(`&id=${SPEND}`);
    // The grid and the chart over the missing view each say so.
    await waitFor(() => expect(screen.getAllByText(`The view ${MISSING} was not found`)).toHaveLength(2));
    expect(await screen.findByText('Order 9')).toBeInTheDocument();
  });

  it('interactive_dashboard_shows_notice', async () => {
    serve({ forms: [dashboard(SPEND, 'Spend', dashboardXml([{ label: 'Lists', cells: [gridCell()] }], ' FormPresentation="1"'))] });
    renderAt(`&id=${SPEND}`);
    expect(await screen.findByText('Interactive dashboards are not supported in this version')).toBeInTheDocument();
    // Still laid out with the classic rules.
    expect(await screen.findByText('Order 9')).toBeInTheDocument();
  });

  it('pickers_true_shows_one_notice', async () => {
    const xml = dashboardXml([
      {
        label: 'Charts',
        cells: [chartCell('c1', VIEW_ID, '<EnableChartPicker>true</EnableChartPicker>'), gridCell('g1', VIEW_ID, '<EnableViewPicker>true</EnableViewPicker>')],
      },
    ]);
    serve({ forms: [dashboard(SPEND, 'Spend', xml)] });
    renderAt(`&id=${SPEND}`);
    expect(await screen.findAllByText('Chart and view pickers are not available in this version')).toHaveLength(1);
    await screen.findByRole('figure', { name: 'Spend by Vendor' });
    expect(screen.getAllByText('Chart and view pickers are not available in this version')).toHaveLength(1);
  });

  it('a_dashboard_that_does_not_exist_says_so', async () => {
    serve();
    renderAt('&id=99999999-0000-0000-0000-000000000001');
    expect(await screen.findByText('The dashboard 99999999-0000-0000-0000-000000000001 was not found.')).toBeInTheDocument();
    // No empty page heading without a dashboard name.
    expect(screen.queryByRole('heading', { level: 1 })).toBeNull();
  });
});
