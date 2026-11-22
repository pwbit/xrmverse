import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { mockApps } from '../../../apps/testSupport';
import { AuthProvider, useAuth } from '../../../shell/auth/AuthProvider';
import { server } from '../../../test/server';
import { setUiLanguage } from '../../../shell/i18n/t';
import { ChartControl, localizeUnsupported } from './ChartControl';

const FV = '@OData.Community.Display.V1.FormattedValue';
const CHART_ID = '11111111-0000-0000-0000-000000000001';
const VIEW_ID = '22222222-0000-0000-0000-000000000001';

const chartFetch = `<fetch mapping="logical" aggregate="true"><entity name="xv_order">
  <attribute name="xv_amount" aggregate="sum" alias="aggregate_column" />
  <attribute name="xv_vendorid" groupby="true" alias="groupby_column" />
</entity></fetch>`;

const chartRow = (type: string) => ({
  savedqueryvisualizationid: CHART_ID,
  name: 'Amount by Vendor',
  primaryentitytypecode: 'xv_order',
  charttype: 0,
  datadescription: `<datadescription><datadefinition><fetchcollection>${chartFetch}</fetchcollection>
    <categorycollection><category><measurecollection><measure alias="aggregate_column" /></measurecollection></category></categorycollection>
    </datadefinition></datadescription>`,
  presentationdescription: `<Chart Palette="None" PaletteCustomColors="15,108,189; 0,120,212">
    <Series><Series ChartType="${type}" IsValueShownAsLabel="True" /></Series><ChartAreas><ChartArea /></ChartAreas></Chart>`,
});

const viewRow = {
  savedqueryid: VIEW_ID,
  name: 'Open Orders',
  returnedtypecode: 'xv_order',
  fetchxml: `<fetch version="1.0" mapping="logical"><entity name="xv_order">
    <attribute name="xv_name" /><attribute name="xv_amount" /><attribute name="xv_orderid" /><order attribute="xv_name" descending="false" />
    <filter type="and"><condition attribute="statecode" operator="eq" value="0" /></filter>
  </entity></fetch>`,
  layoutxml: '<grid name="resultset" object="10001" jump="xv_name" select="1" icon="1" preview="1"><row name="result" id="xv_orderid"><cell name="xv_name" width="200" /><cell name="xv_amount" width="100" /></row></grid>',
};

const Gate = ({ children }: { children: React.ReactNode }) => (useAuth().api ? <>{children}</> : null);

interface Options {
  chart?: Record<string, unknown> | number;
  view?: Record<string, unknown> | number;
  rows?: Record<string, unknown>[];
  /** The status of the query of the chart's table, when it fails. */
  queryStatus?: number;
  /** The aggregate answer says more records match (`morerecords`). */
  more?: boolean;
}

function renderChart({ chart = chartRow('Bar'), view = viewRow, rows = [], queryStatus, more }: Options = {}) {
  const fetches: string[] = [];
  mockApps({
    tables: { xv_order: { entitySetName: 'xv_orders', primaryId: 'xv_orderid', labels: { xv_name: { 1033: 'Order Name' }, xv_amount: { 1033: 'Amount' } } } },
    records: (_set, fetch) => {
      fetches.push(new XMLSerializer().serializeToString(fetch));
      if (fetch.getAttribute('aggregate') === 'true') return { value: rows, ...(more ? { '@Microsoft.Dynamics.CRM.morerecords': true } : {}) };
      return { value: [{ xv_orderid: 'f0000000-0000-0000-0000-000000000009', xv_name: 'Order 9', xv_amount: 10 }] };
    },
  });
  const answer = (row: Record<string, unknown> | number, what: string) =>
    typeof row === 'number' ? HttpResponse.json({ error: { code: '0x80040217', message: `${what} does not exist` } }, { status: row }) : HttpResponse.json(row);
  server.use(
    http.get('*/api/data/v9.2/*', ({ request }) => {
      const path = decodeURIComponent(new URL(request.url).pathname.replace(/^.*\/api\/data\/v9\.2\//, ''));
      if (path === `savedqueryvisualizations(${CHART_ID})`) return answer(chart, 'savedqueryvisualization');
      if (path === `savedqueries(${VIEW_ID})`) return answer(view, 'savedquery');
      if (queryStatus && path === 'xv_orders' && new URL(request.url).searchParams.get('fetchXml')?.includes('aggregate="true"'))
        return HttpResponse.json(
          { error: { code: '0x80040220', message: "Principal user is missing prvReadxv_Order privilege" } },
          { status: queryStatus },
        );
      return undefined;
    }),
  );
  render(
    <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <AuthProvider>
        <Gate>
          <ChartControl visualizationId={`{${CHART_ID.toUpperCase()}}`} viewId={`{${VIEW_ID}}`} title="Spend by vendor" appId="app1" lcid={1033} />
        </Gate>
      </AuthProvider>
    </MemoryRouter>,
  );
  return fetches;
}

const ROWS = [
  { groupby_column: 'v1', [`groupby_column${FV}`]: 'Contoso', aggregate_column: 120, [`aggregate_column${FV}`]: '€ 120.00' },
  { groupby_column: null, aggregate_column: 30, [`aggregate_column${FV}`]: '€ 30.00' },
];

describe('ChartControl', () => {
  it('ChartControl_runs_merged_fetch_and_draws', async () => {
    const fetches = renderChart({ rows: ROWS });
    const svg = await screen.findByRole('figure', { name: 'Amount by Vendor' });
    // A Bar chart with one series is a Fluent horizontal bar chart; its bars and labels are covered by fluentChart.test and the dashboards E2E.
    expect(svg).toHaveAttribute('data-chart-kind', 'horizontalBar');
    expect(screen.getByText('Spend by vendor')).toBeInTheDocument();
    const sent = new DOMParser().parseFromString(fetches[0]!, 'application/xml');
    expect(sent.documentElement.getAttribute('aggregate')).toBe('true');
    const entity = sent.querySelector('fetch > entity')!;
    // The chart's columns, the view's filter; not the view's columns or order.
    expect(Array.from(entity.querySelectorAll(':scope > attribute')).map((a) => a.getAttribute('alias'))).toEqual(['aggregate_column', 'groupby_column']);
    expect(entity.querySelector(':scope > filter > condition')?.getAttribute('attribute')).toBe('statecode');
    expect(entity.querySelector(':scope > order')).toBeNull();
  });

  it('chart_fetch_without_count_asks_for_5000_categories', async () => {
    const fetches = renderChart({ rows: ROWS });
    await screen.findByRole('figure', { name: 'Amount by Vendor' });
    const sent = new DOMParser().parseFromString(fetches[0]!, 'application/xml').documentElement;
    expect(sent.getAttribute('count')).toBe('5000');
    expect(sent.getAttribute('page')).toBe('1');
    expect(screen.queryByText(/categories are shown/)).toBeNull();
  });

  it('chart_fetch_with_count_keeps_it', async () => {
    const row = chartRow('Bar');
    const fetches = renderChart({ rows: ROWS, chart: { ...row, datadescription: row.datadescription.replace('aggregate="true">', 'aggregate="true" count="20">') } });
    await screen.findByRole('figure', { name: 'Amount by Vendor' });
    expect(new DOMParser().parseFromString(fetches[0]!, 'application/xml').documentElement.getAttribute('count')).toBe('20');
  });

  it('more_records_shows_truncated_notice_above_chart', async () => {
    renderChart({ rows: ROWS, more: true });
    const notice = await screen.findByText('Only the first 2 categories are shown.');
    const svg = await screen.findByRole('figure', { name: 'Amount by Vendor' });
    expect(notice.compareDocumentPosition(svg) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('mixed_series_types_show_notice_and_draw_first_type', async () => {
    const row = chartRow('Column');
    renderChart({
      rows: ROWS.map((r) => ({ ...r, second: 1 })),
      chart: {
        ...row,
        datadescription: row.datadescription.replace('<measure alias="aggregate_column" />', '<measure alias="aggregate_column" /><measure alias="second" />'),
        presentationdescription: row.presentationdescription.replace('IsValueShownAsLabel="True" /></Series>', 'IsValueShownAsLabel="True" /><Series ChartType="Line" /></Series>'),
      },
    });
    expect(await screen.findByText('This chart combines several chart types; it is drawn as Column.')).toBeInTheDocument();
    const svg = await screen.findByRole('figure', { name: 'Amount by Vendor' });
    // Two series drawn as Column: grouped columns, no line.
    expect(svg).toHaveAttribute('data-chart-kind', 'groupedVerticalBar');
  });

  it('no_rows_shows_no_data', async () => {
    renderChart({ rows: [] });
    expect(await screen.findByText('No data to show')).toBeInTheDocument();
    expect(screen.queryByRole('figure')).toBeNull();
  });

  it('missing_chart_shows_not_found_notice', async () => {
    renderChart({ chart: 404 });
    expect(await screen.findByText(`The chart ${CHART_ID} was not found`)).toBeInTheDocument();
  });

  it('missing_view_shows_not_found_notice', async () => {
    renderChart({ view: 404 });
    expect(await screen.findByText(`The view ${VIEW_ID} was not found`)).toBeInTheDocument();
  });

  it('unsupported_chart_type_shows_notice_and_grid', async () => {
    const fetches = renderChart({ chart: chartRow('Funnel') });
    expect(await screen.findByText('Chart type Funnel is not supported in this version')).toBeInTheDocument();
    const grid = await screen.findByRole('grid', { name: 'Open Orders' });
    expect(grid).toHaveTextContent('Order Name');
    expect(await screen.findByText('Order 9')).toBeInTheDocument();
    expect(screen.queryByRole('figure')).toBeNull();
    // The view's own query (no parent filter), first page of 10.
    const sent = new DOMParser().parseFromString(fetches[0]!, 'application/xml').documentElement;
    expect(sent.getAttribute('aggregate')).toBeNull();
    expect(sent.getAttribute('count')).toBe('10');
  });

  it('two_group_bys_show_notice_and_grid', async () => {
    const row = chartRow('Column');
    const fetches = renderChart({
      chart: {
        ...row,
        datadescription: row.datadescription.replace(
          '<attribute name="xv_vendorid" groupby="true" alias="groupby_column" />',
          '<attribute name="xv_vendorid" groupby="true" alias="groupby_column" /><attribute name="statecode" groupby="true" alias="state" />',
        ),
      },
    });
    expect(await screen.findByText('Charts with more than one group-by are not supported in this version.')).toBeInTheDocument();
    expect(await screen.findByRole('grid', { name: 'Open Orders' })).toBeInTheDocument();
    expect(await screen.findByText('Order 9')).toBeInTheDocument();
    expect(screen.queryByRole('figure')).toBeNull();
    // No aggregate query runs: only the view's grid.
    expect(fetches.every((f) => !f.includes('aggregate="true"'))).toBe(true);
  });

  it('power_bi_chart_shows_the_kind_notice_without_grid', async () => {
    renderChart({ chart: { ...chartRow('Bar'), charttype: 1 } });
    expect(await screen.findByText('This chart kind is not supported in this version')).toBeInTheDocument();
    expect(screen.queryByRole('grid')).toBeNull();
  });

  it('chart_and_view_of_different_tables_is_a_cell_error', async () => {
    renderChart({ view: { ...viewRow, returnedtypecode: 'account', fetchxml: '<fetch><entity name="account"><attribute name="name" /></entity></fetch>' } });
    expect(await screen.findByText("Chart 'Amount by Vendor' is for table xv_order, the view is for account")).toBeInTheDocument();
  });

  it('table_mismatch_is_translated', async () => {
    setUiLanguage(1040);
    try {
      renderChart({ view: { ...viewRow, returnedtypecode: 'account', fetchxml: '<fetch><entity name="account"><attribute name="name" /></entity></fetch>' } });
      expect(await screen.findByText("Il grafico 'Amount by Vendor' è per la tabella xv_order, la vista è per account")).toBeInTheDocument();
    } finally {
      setUiLanguage(undefined);
    }
  });

  it('query_403_shows_access_error_in_cell', async () => {
    renderChart({ rows: ROWS, queryStatus: 403 });
    expect(await screen.findByText(/You do not have access to the data of this chart/)).toHaveTextContent('prvReadxv_Order');
    expect(screen.queryByRole('figure')).toBeNull();
  });
});

describe('localizeUnsupported', () => {
  it('translates_the_parser_codes_into_the_ui_language', () => {
    setUiLanguage(1040);
    try {
      expect(localizeUnsupported({ kind: 'chartType', type: 'Funnel', message: '' })).toBe('Il tipo di grafico Funnel non è supportato in questa versione');
      expect(localizeUnsupported({ kind: 'chartKind', message: '' })).toBe('Questo tipo di grafico non è supportato in questa versione');
      expect(localizeUnsupported({ kind: 'multipleGroupBy', message: '' })).toBe('I grafici con più di un raggruppamento non sono supportati in questa versione.');
      expect(localizeUnsupported({ kind: 'unreadable', name: 'A', reason: 'bad XML', message: '' })).toBe("Impossibile leggere il grafico 'A': bad XML");
    } finally {
      setUiLanguage(undefined);
    }
  });
});
