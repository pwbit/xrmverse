import { columnKinds } from '../../../apps/columnHeaders';
import { makeStyles, MessageBar, MessageBarBody, Spinner, Text, tokens } from '@fluentui/react-components';
import { useEffect, useMemo, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { loadTable, type TableInfo } from '../../../apps/loadViews';
import { mainAspxUrl } from '../../../apps/mainAspx';
import { cellText, runView, type ViewPage } from '../../../apps/runView';
import type { ChartModel, ChartUnsupported } from '../../../model/chart';
import { ChartTableMismatchError, mergeChartFetch, parseChart, type ChartRow } from '../../../parsers/chartXml';
import { linkEntities, parseFetch, withOrder, withPaging } from '../../../parsers/fetchXml';
import { normaliseId } from '../../../parsers/formXml';
import { parseLayout } from '../../../parsers/layoutXml';
import type { XrmApi } from '../../../shell/api/client';
import { XrmError } from '../../../shell/api/errors';
import { useAuth } from '../../../shell/auth/AuthProvider';
import { t } from '../../../shell/i18n/t';
import { ViewGrid, type GridRow, type GridSort } from '../ViewGrid';
import { useViewPresentation } from '../../modern/PresentationContext';
import { chartData, type ChartData } from './chartData';
import { FluentChart } from './fluentChart';

export interface ChartControlProps {
  /** The `savedqueryvisualization` id (braces and case as the FormXml writes them). */
  visualizationId: string;
  /** The `savedquery` whose filter limits the chart (ruling F5); without one the chart's own fetch runs. */
  viewId?: string;
  /** The cell's label, shown above the chart. */
  title?: string;
  /** The app a record opened from the fallback grid opens in. */
  appId?: string;
  /** The user's language, for the fallback grid's column labels. */
  lcid: number;
}

/** The `savedquery` columns the chart reads: the filter for the chart, the rest for the grid shown in its place. */
interface ViewRow {
  name: string;
  fetchxml: string;
  layoutxml?: string | null;
  returnedtypecode: string;
}

/** The view's grid in the place of a chart type that is not drawn. */
interface FallbackView {
  id: string;
  row: ViewRow;
}

type Loaded =
  | { kind: 'chart'; model: ChartModel; data: ChartData; truncated: boolean }
  | { kind: 'noData' }
  | { kind: 'notice'; intent: 'info' | 'warning' | 'error'; text: string; grid?: FallbackView };

/** A failure whose text is already the cell's notice (a chart or view that is not imported, Review Focus 3). */
class CellNotice extends Error {}

const CHART_SELECT = '$select=name,primaryentitytypecode,datadescription,presentationdescription,charttype,_webresourceid_value';
const VIEW_SELECT = '$select=name,fetchxml,layoutxml,returnedtypecode';
/** The fallback grid's page size: a dashboard subgrid's default `RecordsPerPage`. */
const GRID_PAGE_SIZE = 10;
/** The categories a chart asks for when its fetch names no `count` (the largest FetchXML page). */
const CHART_CATEGORY_LIMIT = 5000;

/** Why a chart is not drawn, in the UI language. */
export function localizeUnsupported(u: ChartUnsupported): string {
  switch (u.kind) {
    case 'chartKind':
      return t('chart.kindUnsupported');
    case 'chartType':
      return t('chart.typeUnsupported', { type: u.type });
    case 'multipleGroupBy':
      return t('chart.multipleGroupBy');
    case 'unreadable':
      return t('chart.unreadable', { name: u.name, reason: u.reason });
  }
}

/** The chart kinds whose view is listed in the chart's place. */
const showsGrid = (u: ChartUnsupported) => u.kind === 'chartType' || u.kind === 'multipleGroupBy';

/** One row by key; a 404 is the cell notice `notFound`. */
async function readRow<T>(api: XrmApi, path: string, notFound: string): Promise<T> {
  try {
    return await api.get<T>(path);
  } catch (e) {
    if (e instanceof XrmError && e.status === 404) throw new CellNotice(notFound);
    throw e;
  }
}

/** The notice of a failed cell: not found, no access (Review Focus 4), a chart message, else the reason. */
function noticeOf(e: unknown): Loaded {
  if (e instanceof CellNotice) return { kind: 'notice', intent: 'warning', text: e.message };
  if (e instanceof XrmError && e.status === 403) return { kind: 'notice', intent: 'error', text: t('chart.noAccess', { detail: e.serverMessage }) };
  if (e instanceof ChartTableMismatchError)
    return { kind: 'notice', intent: 'warning', text: t('chart.tableMismatch', { name: e.chartName, chartTable: e.chartTable, viewTable: e.viewTable }) };
  const message = e instanceof Error ? e.message : String(e);
  return { kind: 'notice', intent: 'warning', text: t('chart.cannotShow', { reason: message }) };
}

async function loadChart(api: XrmApi, visualizationId: string, viewId: string | undefined, lcid: number, signal: AbortSignal): Promise<Loaded> {
  const chartId = normaliseId(visualizationId);
  const view = viewId ? normaliseId(viewId) : undefined;
  const [row, viewRow] = await Promise.all([
    readRow<ChartRow>(api, `savedqueryvisualizations(${chartId})?${CHART_SELECT}`, t('chart.notFound', { id: chartId })),
    view ? readRow<ViewRow>(api, `savedqueries(${view})?${VIEW_SELECT}`, t('chart.viewNotFound', { id: view })) : undefined,
  ]);
  const model = parseChart({ ...row, _webresourceid_value: row._webresourceid_value ?? null });
  if (model.unsupported) {
    const grid = showsGrid(model.unsupported) && view && viewRow ? { id: view, row: viewRow } : undefined;
    return { kind: 'notice', intent: 'info', text: localizeUnsupported(model.unsupported), ...(grid ? { grid } : {}) };
  }
  const merged = viewRow ? mergeChartFetch(model.fetchXml, viewRow.fetchxml, model.name) : model.fetchXml;
  // runView would page a fetch without `count` by 50, cutting the chart's categories; an explicit count is kept.
  const fetchXml = parseFetch(merged).count === undefined ? withPaging(merged, 1, CHART_CATEGORY_LIMIT) : merged;
  const table = await loadTable(api, model.table, lcid);
  const page = await runView(api, table.entitySetName, fetchXml, 1, undefined, signal);
  if (page.rows.length === 0) return { kind: 'noData' };
  return { kind: 'chart', model, data: chartData(model, page.rows), truncated: page.more };
}

const useStyles = makeStyles({
  cell: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, minWidth: 0, maxWidth: '100%' },
});

function Notice({ intent, text }: { intent: 'info' | 'warning' | 'error'; text: string }) {
  return (
    <MessageBar intent={intent}>
      <MessageBarBody>{text}</MessageBarBody>
    </MessageBar>
  );
}

/**
 * A system chart (`savedqueryvisualization`) on a dashboard: its aggregate FetchXML with the view's filter (ruling F5),
 * drawn as SVG. It needs no record, so it takes the API from the signed-in session. A chart that cannot be drawn shows
 * its notice, with the view's grid in its place for a chart type that is not drawn or a second group-by; a failure stays in this cell.
 */
export function ChartControl({ visualizationId, viewId, title, appId, lcid }: ChartControlProps) {
  const styles = useStyles();
  const { api } = useAuth();
  const key = [visualizationId, viewId ?? '', lcid].join('|');
  const [state, setState] = useState<{ key: string; loaded?: Loaded }>({ key: '' });

  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    const abort = new AbortController();
    loadChart(api, visualizationId, viewId, lcid, abort.signal).then(
      (loaded) => !cancelled && setState({ key, loaded }),
      (e: unknown) => !cancelled && setState({ key, loaded: noticeOf(e) }),
    );
    return () => {
      cancelled = true;
      abort.abort();
    };
    // `key` stands for the chart, the view and the language.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, key]);

  const loaded = state.key === key ? state.loaded : undefined;
  let content;
  if (!loaded) content = <Spinner size="tiny" label={t('chart.loading')} />;
  else if (loaded.kind === 'noData') content = <Text>{t('chart.noData')}</Text>;
  else if (loaded.kind === 'notice')
    content = (
      <>
        <Notice intent={loaded.intent} text={loaded.text} />
        {loaded.grid && <FallbackGrid view={loaded.grid} appId={appId} lcid={lcid} />}
      </>
    );
  else {
    const { model, data, truncated } = loaded;
    const type = model.series[0]?.type ?? 'Column';
    const mixed = model.series.some((s) => s.type !== type);
    content = (
      <>
        {truncated && <Notice intent="info" text={t('chart.truncated', { n: data.categories.length })} />}
        {mixed && <Notice intent="info" text={t('chart.mixedTypes', { type })} />}
        <FluentChart
          type={type}
          data={data}
          palette={model.palette}
          showValues={model.series.some((s) => s.showValues)}
          height={300}
          label={model.name}
        />
      </>
    );
  }
  return (
    <div className={styles.cell} data-testid="chart-cell">
      {title && <Text weight="semibold">{title}</Text>}
      {content}
    </div>
  );
}

/** The view's first page (10 rows) in the UI-1a grid, with no parent filter; a row opens its record. */
function FallbackGrid({ view, appId, lcid }: { view: FallbackView; appId?: string; lcid: number }) {
  const { api } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const { row } = view;
  const wrap = useViewPresentation(view.id)?.wrap;
  const target = parseFetch(row.fetchxml).entity;
  const columns = useMemo(() => parseLayout(row.layoutxml ?? '').filter((c) => !c.isHidden), [row]);
  const [sort, setSort] = useState<GridSort | undefined>(() => parseFetch(row.fetchxml).order.find((o) => !o.alias));
  const fetchXml = useMemo(() => {
    const paged = withPaging(row.fetchxml, 1, GRID_PAGE_SIZE);
    return sort ? withOrder(paged, sort.attribute, sort.descending, sort.linkAlias) : paged;
  }, [row, sort]);
  const [result, setResult] = useState<{ key: string; tables?: Record<string, TableInfo>; page?: ViewPage; error?: string }>({ key: '' });

  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    const abort = new AbortController();
    const names = [...new Set([target, ...Object.values(linkEntities(row.fetchxml))])];
    (async () => {
      const infos = await Promise.all(names.map((name) => loadTable(api, name, lcid)));
      const tables = Object.fromEntries(infos.map((info) => [info.logicalName, info]));
      const page = await runView(api, tables[target]!.entitySetName, fetchXml, 1, undefined, abort.signal);
      return { tables, page };
    })().then(
      ({ tables, page }) => !cancelled && setResult({ key: fetchXml, tables, page }),
      (e: unknown) => !cancelled && setResult({ key: fetchXml, error: e instanceof Error ? e.message : String(e) }),
    );
    return () => {
      cancelled = true;
      abort.abort();
    };
  }, [api, row, target, fetchXml, lcid]);

  const current = result.key === fetchXml ? result : undefined;
  if (current?.error !== undefined) return <Notice intent="warning" text={t('chart.cannotShow', { reason: current.error })} />;
  if (!current?.page || !current.tables) return <Spinner size="tiny" label={t('chart.loading')} />;
  const { tables, page } = current;
  const links = linkEntities(row.fetchxml);
  const headers = Object.fromEntries(columns.map((c) => [c.name, tables[c.alias ? (links[c.alias] ?? '') : target]?.labels[c.attribute] ?? c.name]));
  const kinds = columnKinds(columns, row.fetchxml, tables);
  const primaryId = tables[target]?.primaryId ?? '';
  const rows: GridRow[] = page.rows.map((r, index) => {
    const value = r[primaryId];
    const id = typeof value === 'string' && value ? value : undefined;
    return { key: id ?? `row-${index}`, id, cells: Object.fromEntries(columns.map((c) => [c.name, cellText(r, c)])) };
  });
  return (
    <>
      <ViewGrid
        label={row.name}
        columns={columns}
        headers={headers}
        kinds={kinds}
        rows={rows}
        wrap={wrap}
        sort={sort}
        onSort={setSort}
        onOpen={(id) =>
          appId && navigate(mainAspxUrl({ appId, pageType: 'entityrecord', etn: target, id }), { state: { returnTo: location.pathname + location.search } })
        }
      />
      {rows.length === 0 && <Text block>{t('apps.views.empty')}</Text>}
    </>
  );
}
