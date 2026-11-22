import {
  AreaChart,
  DonutChart,
  GroupedVerticalBarChart,
  HorizontalBarChartWithAxis,
  LineChart,
  VerticalBarChart,
  type ChartProps,
  type GroupedVerticalBarChartData,
  type HorizontalBarChartWithAxisDataPoint,
  type VerticalBarChartDataPoint,
} from '@fluentui/react-charts';
import type { ChartData } from './chartData';

export type { ChartData } from './chartData';

export interface FluentChartProps {
  /** A `ChartType`: Column, Bar, Line, Area, Pie or Doughnut (anything else is drawn as Column). */
  type: string;
  data: ChartData;
  /** `PaletteCustomColors` as CSS colours, used in order; null for Fluent's data visualization palette. */
  palette: string[] | null;
  /** `IsValueShownAsLabel`: each point shows its formatted value. */
  showValues: boolean;
  /** The chart's height in pixels; it is as wide as its cell. */
  height: number;
  /** The accessible name (the chart's name). */
  label: string;
}

/** Which Fluent chart draws a `ChartType` with this many series. */
export type FluentChartKind = 'verticalBar' | 'groupedVerticalBar' | 'horizontalBar' | 'line' | 'area' | 'pie' | 'donut';

export function chartKind(type: string, seriesCount: number): FluentChartKind {
  switch (type) {
    case 'Pie':
      return 'pie';
    case 'Doughnut':
      return 'donut';
    case 'Line':
      return 'line';
    case 'Area':
      return 'area';
    // Fluent's horizontal bar chart draws one series; several are drawn as grouped columns.
    case 'Bar':
      return seriesCount > 1 ? 'groupedVerticalBar' : 'horizontalBar';
    default:
      return seriesCount > 1 ? 'groupedVerticalBar' : 'verticalBar';
  }
}

/** The custom colour of item `i`, cycling through the palette; undefined lets Fluent pick from its own palette. */
export const colorAt = (palette: string[] | null, i: number): string | undefined =>
  palette && palette.length > 0 ? palette[i % palette.length] : undefined;

/** Column (one series): one bar per category, with the formatted value as its callout and label. */
export function verticalBarData(data: ChartData, palette: string[] | null): VerticalBarChartDataPoint[] {
  const s = data.series[0];
  if (!s) return [];
  return data.categories.map((category, i) => ({
    x: category,
    y: s.values[i] ?? 0,
    legend: s.name,
    color: colorAt(palette, 0),
    yAxisCalloutData: s.labels[i],
    barLabel: s.labels[i],
  }));
}

/** Column or Bar with several series: one group per category, one bar per series. */
export function groupedData(data: ChartData, palette: string[] | null): GroupedVerticalBarChartData[] {
  return data.categories.map((category, i) => ({
    name: category,
    series: data.series.map((s, j) => ({
      key: `${category}-${s.name}`,
      data: s.values[i] ?? 0,
      legend: s.name,
      color: colorAt(palette, j),
      yAxisCalloutData: s.labels[i],
      barLabel: s.labels[i],
    })),
  }));
}

/** Bar (one series): one horizontal bar per category. */
export function horizontalBarData(data: ChartData, palette: string[] | null): HorizontalBarChartWithAxisDataPoint[] {
  const s = data.series[0];
  if (!s) return [];
  return data.categories.map((category, i) => ({
    x: s.values[i] ?? 0,
    y: category,
    legend: s.name,
    color: colorAt(palette, 0),
    xAxisCalloutData: s.labels[i],
    barLabel: s.labels[i],
  }));
}

/** Line or Area: one line per series; the x axis is the category index, labelled with the category. */
export function lineData(data: ChartData, palette: string[] | null, title: string): ChartProps {
  return {
    chartTitle: title,
    lineChartData: data.series.map((s, j) => ({
      legend: s.name,
      color: colorAt(palette, j),
      data: data.categories.map((category, i) => ({ x: i, y: s.values[i] ?? 0, xAxisCalloutData: category, yAxisCalloutData: s.labels[i] })),
    })),
  };
}

/** Pie or Doughnut: one slice per category of the first series, coloured by category. */
export function donutData(data: ChartData, palette: string[] | null, title: string): ChartProps {
  const s = data.series[0];
  return {
    chartTitle: title,
    chartData: s ? data.categories.map((category, i) => ({ legend: category, data: s.values[i] ?? 0, color: colorAt(palette, i), yAxisCalloutData: s.labels[i] })) : [],
  };
}

/** A chart drawn by Fluent UI React Charts (`@fluentui/react-charts`), as wide as its cell. A named figure, not an image: its bars, arcs and legend stay reachable by keyboard and screen reader. */
export function FluentChart({ type, data, palette, showValues, height, label }: FluentChartProps) {
  const kind = chartKind(type, data.series.length);
  const hideLabels = !showValues;
  const ticks = data.categories.map((_, i) => i);
  const tick = (v: number | Date) => (typeof v === 'number' ? (data.categories[v] ?? '') : String(v));
  let chart;
  switch (kind) {
    case 'pie':
    case 'donut':
      chart = <DonutChart data={donutData(data, palette, label)} innerRadius={kind === 'donut' ? height * 0.22 : 0} height={height} hideLabels={hideLabels} />;
      break;
    case 'line':
      chart = <LineChart data={lineData(data, palette, label)} height={height} tickValues={ticks} tickFormat={tick} />;
      break;
    case 'area':
      chart = <AreaChart data={lineData(data, palette, label)} height={height} tickValues={ticks} tickFormat={tick} />;
      break;
    case 'horizontalBar':
      chart = <HorizontalBarChartWithAxis data={horizontalBarData(data, palette)} height={height} hideLabels={hideLabels} />;
      break;
    case 'groupedVerticalBar':
      chart = <GroupedVerticalBarChart data={groupedData(data, palette)} height={height} hideLabels={hideLabels} />;
      break;
    default:
      chart = <VerticalBarChart data={verticalBarData(data, palette)} height={height} hideLabels={hideLabels} />;
  }
  return (
    <div role="figure" aria-label={label} data-chart-kind={kind} style={{ width: '100%' }}>
      {chart}
    </div>
  );
}
