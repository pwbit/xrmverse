import { FORMATTED_VALUE } from '../../../apps/runView';
import type { ChartModel } from '../../../model/chart';
import { t, uiLocale } from '../../../shell/i18n/t';

/** What a chart draws: one category per result row and one series per measure, with a value and a label per category. */
export interface ChartData {
  categories: string[];
  series: { name: string; values: number[]; labels: string[] }[];
}

const scalar = (value: unknown): string | undefined =>
  typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean' ? String(value) : undefined;

/**
 * The aggregate result rows (keyed by alias, ruling F5) as chart data. A category is the group-by alias's formatted value,
 * else its raw value, else "(blank)" (Review Focus 1). A value that is null or not a number counts as 0; its label is the
 * formatted value, else the number in the UI locale.
 */
export function chartData(model: ChartModel, rows: Record<string, unknown>[]): ChartData {
  const alias = model.categoryAlias;
  const categories = rows.map((row) => (alias ? (scalar(row[alias + FORMATTED_VALUE]) ?? scalar(row[alias])) : undefined) || t('chart.blank'));
  const format = new Intl.NumberFormat(uiLocale());
  const series = model.measures.map((measure) => {
    const values = rows.map((row) => {
      const n = Number(row[measure.alias] ?? 0);
      return Number.isFinite(n) ? n : 0;
    });
    const labels = rows.map((row, i) => scalar(row[measure.alias + FORMATTED_VALUE]) ?? format.format(values[i]!));
    return { name: measure.alias, values, labels };
  });
  return { categories, series };
}
