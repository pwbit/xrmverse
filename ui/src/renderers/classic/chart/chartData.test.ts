import { afterEach, describe, expect, it } from 'vitest';
import type { ChartModel } from '../../../model/chart';
import { setUiLanguage } from '../../../shell/i18n/t';
import { chartData } from './chartData';

const FV = '@OData.Community.Display.V1.FormattedValue';

const model: ChartModel = {
  name: 'Amount by Vendor',
  table: 'xv_order',
  fetchXml: '<fetch aggregate="true" />',
  categoryAlias: 'groupby_column',
  measures: [{ alias: 'aggregate_column', series: 0 }],
  series: [{ type: 'Column', showValues: true }],
  palette: null,
};

describe('chartData', () => {
  afterEach(() => setUiLanguage(undefined));

  it('chartData_reads_aliases_and_formatted_values', () => {
    const data = chartData(model, [
      { groupby_column: 'f0000000-0000-0000-0000-000000000001', [`groupby_column${FV}`]: 'Contoso', aggregate_column: 1200.5, [`aggregate_column${FV}`]: '1,200.50' },
      { groupby_column: 'Fabrikam', aggregate_column: 3 },
    ]);
    expect(data).toEqual({
      categories: ['Contoso', 'Fabrikam'],
      series: [{ name: 'aggregate_column', values: [1200.5, 3], labels: ['1,200.50', '3'] }],
    });
  });

  it('null_group_value_is_blank_category', () => {
    const data = chartData(model, [{ groupby_column: null, aggregate_column: 7 }, { aggregate_column: null }]);
    expect(data.categories).toEqual(['(blank)', '(blank)']);
    expect(data.series[0]!.values).toEqual([7, 0]);
    setUiLanguage(1040);
    expect(chartData(model, [{ groupby_column: null, aggregate_column: 1 }]).categories).toEqual(['(vuoto)']);
  });

  it('reads_one_series_per_measure', () => {
    const two: ChartModel = { ...model, measures: [{ alias: 'a', series: 0 }, { alias: 'b', series: 1 }] };
    const data = chartData(two, [{ groupby_column: 'X', a: 1, b: '2.5' }]);
    expect(data.series.map((s) => [s.name, s.values])).toEqual([
      ['a', [1]],
      ['b', [2.5]],
    ]);
  });
});
