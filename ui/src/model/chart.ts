/**
 * Why a chart cannot be drawn, as a code the UI translates; `message` is the English text (Global Constraints) for logs and
 * tests. `chartType` and `multipleGroupBy` show the view's grid in the chart's place.
 */
export type ChartUnsupported = (
  | { kind: 'chartKind' }
  | { kind: 'chartType'; type: string }
  | { kind: 'multipleGroupBy' }
  | { kind: 'unreadable'; name: string; reason: string }
) & { message: string };

/** A system chart (`savedqueryvisualization` row) as the UI draws it. */
export interface ChartModel {
  name: string;
  /** The table's logical name (`primaryentitytypecode`). */
  table: string;
  /** The aggregate FetchXML of `datadescription`; empty when the chart kind cannot be drawn. */
  fetchXml: string;
  /** Alias of the group-by column; empty when the fetch has none. */
  categoryAlias: string;
  /** The measures in declaration order; `series` is the index of the series (`<Series><Series>`) that draws it. */
  measures: { alias: string; series: number }[];
  /** `ChartType` (default `Column`) and `IsValueShownAsLabel` of each series. */
  series: { type: string; showValues: boolean }[];
  /** CSS colours from `PaletteCustomColors`; null when absent. */
  palette: string[] | null;
  /** Set when the chart cannot be drawn. */
  unsupported?: ChartUnsupported;
}
