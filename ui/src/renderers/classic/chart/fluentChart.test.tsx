import { render, screen } from '@testing-library/react';
import { FluentProvider } from '@fluentui/react-components';
import { describe, expect, it } from 'vitest';
import { xrmverseTheme } from '../../../shell/theme';
import { chartKind, colorAt, donutData, FluentChart, groupedData, horizontalBarData, lineData, verticalBarData, type ChartData } from './fluentChart';

const ONE: ChartData = { categories: ['Contoso', '(blank)'], series: [{ name: 'sum', values: [120, 30], labels: ['€ 120.00', '€ 30.00'] }] };
const TWO: ChartData = {
  categories: ['A', 'B'],
  series: [
    { name: 's1', values: [1, 2], labels: ['1', '2'] },
    { name: 's2', values: [3, 4], labels: ['3', '4'] },
  ],
};
const PALETTE = ['red', 'blue'];

describe('fluentChart', () => {
  it.each([
    ['Column', 1, 'verticalBar'],
    ['Column', 2, 'groupedVerticalBar'],
    ['Bar', 1, 'horizontalBar'],
    ['Bar', 2, 'groupedVerticalBar'],
    ['Line', 1, 'line'],
    ['Area', 2, 'area'],
    ['Pie', 1, 'pie'],
    ['Doughnut', 1, 'donut'],
    ['StackedColumn', 1, 'verticalBar'],
  ])('chart_kind_%s_with_%i_series_is_%s', (type, n, kind) => {
    expect(chartKind(type, n)).toBe(kind);
  });

  it('palette_cycles_and_null_lets_fluent_pick', () => {
    expect([0, 1, 2, 3].map((i) => colorAt(PALETTE, i))).toEqual(['red', 'blue', 'red', 'blue']);
    expect(colorAt(null, 0)).toBeUndefined();
    expect(colorAt([], 0)).toBeUndefined();
  });

  it('column_points_carry_value_and_formatted_label', () => {
    expect(verticalBarData(ONE, PALETTE)).toEqual([
      { x: 'Contoso', y: 120, legend: 'sum', color: 'red', yAxisCalloutData: '€ 120.00', barLabel: '€ 120.00' },
      { x: '(blank)', y: 30, legend: 'sum', color: 'red', yAxisCalloutData: '€ 30.00', barLabel: '€ 30.00' },
    ]);
    expect(verticalBarData({ categories: [], series: [] }, null)).toEqual([]);
  });

  it('grouped_has_one_group_per_category_and_one_bar_per_series', () => {
    const groups = groupedData(TWO, PALETTE);
    expect(groups.map((g) => g.name)).toEqual(['A', 'B']);
    expect(groups[1]!.series.map((b) => [b.legend, b.data, b.color, b.barLabel])).toEqual([
      ['s1', 2, 'red', '2'],
      ['s2', 4, 'blue', '4'],
    ]);
  });

  it('horizontal_bar_puts_category_on_y', () => {
    expect(horizontalBarData(ONE, null).map((p) => [p.y, p.x, p.barLabel])).toEqual([
      ['Contoso', 120, '€ 120.00'],
      ['(blank)', 30, '€ 30.00'],
    ]);
  });

  it('line_uses_category_index_and_callouts', () => {
    const d = lineData(TWO, PALETTE, 'T');
    expect(d.chartTitle).toBe('T');
    expect(d.lineChartData!.map((l) => [l.legend, l.color])).toEqual([
      ['s1', 'red'],
      ['s2', 'blue'],
    ]);
    expect(d.lineChartData![1]!.data.map((p) => [p.x, p.y, p.xAxisCalloutData])).toEqual([
      [0, 3, 'A'],
      [1, 4, 'B'],
    ]);
  });

  it('donut_has_one_slice_per_category_coloured_by_category', () => {
    const d = donutData(ONE, PALETTE, 'T');
    expect(d.chartData!.map((p) => [p.legend, p.data, p.color])).toEqual([
      ['Contoso', 120, 'red'],
      ['(blank)', 30, 'blue'],
    ]);
    expect(donutData({ categories: [], series: [] }, null, 'T').chartData).toEqual([]);
  });

  it('wrapper_is_a_named_figure_with_its_kind', () => {
    render(
      <FluentProvider theme={xrmverseTheme}>
        <FluentChart type="Pie" data={ONE} palette={null} showValues height={300} label="By status" />
      </FluentProvider>,
    );
    expect(screen.getByRole('figure', { name: 'By status' })).toHaveAttribute('data-chart-kind', 'pie');
  });
});
