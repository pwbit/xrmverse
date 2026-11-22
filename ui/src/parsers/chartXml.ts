import type { ChartModel } from '../model/chart';
import type { DashboardModel } from '../model/dashboard';
import { parseFormXml } from './formXml';
import { parseFetch } from './fetchXml';
import { childElements, parseXml } from './xml';

/** The columns of a `savedqueryvisualization` row that the parser reads. A missing lookup key is null. */
export interface ChartRow {
  name: string;
  primaryentitytypecode: string;
  datadescription: string;
  presentationdescription: string;
  charttype: number;
  _webresourceid_value?: string | null;
}

/** The `ChartType` values the chart control draws. */
export const DRAWN_CHART_TYPES = ['Column', 'Bar', 'Line', 'Area', 'Pie', 'Doughnut'] as const;

const serialize = (el: Element): string => new XMLSerializer().serializeToString(el);

const descendants = (el: Element, name: string): Element[] => Array.from(el.getElementsByTagName(name));

/** "r,g,b; r,g,b" to CSS colours; an entry that is not three numbers is skipped. */
function paletteOf(text: string | null): string[] | null {
  if (!text) return null;
  const colors = text
    .split(';')
    .map((c) => c.trim().split(',').map((n) => n.trim()))
    .filter((p) => p.length === 3 && p.every((n) => /^\d{1,3}$/.test(n)))
    .map((p) => `rgb(${p.join(', ')})`);
  return colors.length > 0 ? colors : null;
}

export function parseChart(row: ChartRow): ChartModel {
  const model: ChartModel = {
    name: row.name,
    table: row.primaryentitytypecode,
    fetchXml: '',
    categoryAlias: '',
    measures: [],
    series: [],
    palette: null,
  };
  if (row.charttype === 1 || !!row._webresourceid_value) {
    model.unsupported = { kind: 'chartKind', message: 'This chart kind is not supported in this version' };
    return model;
  }
  let groupBys = 0;
  let categories = 0;
  try {
    const data = parseXml(row.datadescription).documentElement;
    const fetch = descendants(data, 'fetch')[0];
    if (fetch) {
      model.fetchXml = serialize(fetch);
      const groups = descendants(fetch, 'attribute').filter((a) => a.getAttribute('groupby')?.toLowerCase() === 'true');
      model.categoryAlias = groups[0]?.getAttribute('alias') ?? '';
      groupBys = groups.length;
    }
    categories = descendants(data, 'category').length;
    model.measures = descendants(data, 'measure').flatMap((m, index) => {
      const alias = m.getAttribute('alias');
      return alias ? [{ alias, series: index }] : [];
    });

    const root = parseXml(row.presentationdescription).documentElement;
    const chart = root.tagName === 'Chart' ? root : (descendants(root, 'Chart')[0] ?? root);
    model.palette = paletteOf(chart.getAttribute('PaletteCustomColors'));
    model.series = childElements(chart, 'Series').flatMap((holder) =>
      childElements(holder, 'Series').map((s) => ({
        type: s.getAttribute('ChartType')?.trim() || 'Column',
        showValues: s.getAttribute('IsValueShownAsLabel')?.trim().toLowerCase() === 'true',
      })),
    );
  } catch (e) {
    const reason = e instanceof Error ? e.message : String(e);
    model.unsupported = { kind: 'unreadable', name: row.name, reason, message: `Chart '${row.name}' could not be read: ${reason}` };
    return model;
  }
  const bad = model.series.find((s) => !(DRAWN_CHART_TYPES as readonly string[]).includes(s.type));
  if (bad) model.unsupported = { kind: 'chartType', type: bad.type, message: `Chart type ${bad.type} is not supported in this version` };
  // A second group-by (or `<category>`) makes series per value of it; the chart control draws one category axis only.
  else if (groupBys > 1 || categories > 1)
    model.unsupported = { kind: 'multipleGroupBy', message: 'Charts with more than one group-by are not supported in this version.' };
  return model;
}

/** `mergeChartFetch`'s error for a chart and a view on different tables; the UI translates it from its fields. */
export class ChartTableMismatchError extends Error {
  readonly chartName: string;
  readonly chartTable: string;
  readonly viewTable: string;
  constructor(chartName: string, chartTable: string, viewTable: string) {
    super(`Chart '${chartName}' is for table ${chartTable}, the view is for ${viewTable}`);
    this.name = 'ChartTableMismatchError';
    this.chartName = chartName;
    this.chartTable = chartTable;
    this.viewTable = viewTable;
  }
}

/**
 * Ruling F5 (chart query): the chart's fetch plus, from the view's root `<entity>`, its `<filter>` elements and its
 * every `<link-entity>` (with its `<attribute>`, `<all-attributes>` and `<order>` removed, nested links and filters kept); the view's columns and orders are not used. A colliding link alias is renamed. `chartName` only
 * names the chart in the error for a table mismatch.
 */
export function mergeChartFetch(chartFetch: string, viewFetch: string, chartName: string): string {
  const chartTable = parseFetch(chartFetch).entity;
  const viewTable = parseFetch(viewFetch).entity;
  if (chartTable !== viewTable) throw new ChartTableMismatchError(chartName, chartTable, viewTable);
  const doc = parseXml(chartFetch);
  const target = childElements(doc.documentElement, 'entity')[0]!;
  const source = childElements(parseXml(viewFetch).documentElement, 'entity')[0]!;
  const used = new Set(descendants(doc.documentElement, '*').flatMap((e) => e.getAttribute('alias') ?? []));
  const copies = Array.from(source.children)
    .filter((el) => el.tagName === 'filter' || el.tagName === 'link-entity')
    .map((el) => doc.importNode(el, true) as Element);
  for (const copy of copies) {
    for (const name of ['attribute', 'all-attributes', 'order']) for (const e of descendants(copy, name)) e.parentNode?.removeChild(e);
  }
  // A view link whose alias is taken gets `_v`, `_v2`, ... appended; its `entityname` references follow.
  const renamed = new Map<string, string>();
  for (const link of copies.flatMap((c) => [...(c.tagName === 'link-entity' ? [c] : []), ...descendants(c, 'link-entity')])) {
    const alias = link.getAttribute('alias');
    if (!alias) continue;
    if (!used.has(alias)) {
      used.add(alias);
      continue;
    }
    let candidate = `${alias}_v`;
    for (let n = 2; used.has(candidate); n++) candidate = `${alias}_v${n}`;
    used.add(candidate);
    renamed.set(alias, candidate);
    link.setAttribute('alias', candidate);
  }
  for (const copy of copies) {
    for (const cond of descendants(copy, 'condition')) {
      const to = renamed.get(cond.getAttribute('entityname') ?? '');
      if (to) cond.setAttribute('entityname', to);
    }
    target.appendChild(copy);
  }
  return serialize(doc.documentElement);
}

/**
 * A dashboard's `<form>` (the root, as for entity forms) through the form parser. `lcid` is accepted for the caller's
 * signature; labels stay keyed by LCID in the model, so nothing here depends on it. The id, name and table are filled
 * in by the caller from the `systemform` row.
 */
export function parseDashboard(formXml: string, _lcid: number): DashboardModel {
  const form = parseFormXml({ systemformid: '', name: '', objecttypecode: 'none', type: 0, formxml: formXml });
  const root = parseXml(formXml).documentElement;
  const presentation = root.getAttribute('FormPresentation') ?? childElements(root, 'FormPresentation')[0]?.textContent;
  const interactive = presentation?.trim() === '1' || descendants(root, 'stream').length > 0;
  return { ...form, interactive };
}
