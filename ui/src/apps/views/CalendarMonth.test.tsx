import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { setUiLanguage } from '../../shell/i18n/t';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ViewColumn } from '../../model/view';
import { wallClockOf } from '../timeZones';
import { monthGrid } from './calendarGrid';
import { CalendarMonth, type CalendarMonthProps } from './CalendarMonth';

const TITLE: ViewColumn = { name: 'name', width: 100, attribute: 'name', isHidden: false };

function calendar(props: Partial<CalendarMonthProps> = {}) {
  const onNavigate = vi.fn();
  const onOpen = vi.fn();
  const utils = render(
    <FluentProvider theme={webLightTheme}>
      <CalendarMonth
        label="Meetings"
        year={2026}
        month={11}
        weekStart={1}
        start="starts"
        dateOnly={false}
        zone="Europe/Rome"
        rows={[]}
        primaryId="id"
        titleColumn={TITLE}
        onNavigate={onNavigate}
        onOpen={onOpen}
        {...props}
      />
    </FluentProvider>,
  );
  return { onNavigate, onOpen, ...utils };
}

const cell = (name: RegExp | string) => screen.getAllByRole('gridcell').find((c) => (c.getAttribute('aria-label') ?? '').match(name))!;

describe('CalendarMonth', () => {
  afterEach(() => setUiLanguage(1033));

  it('shows the month in the locale of the user and has 42 day cells', () => {
    calendar();
    expect(screen.getByText('November 2026')).toBeInTheDocument();
    expect(screen.getAllByRole('gridcell')).toHaveLength(42);
    expect(cell(/November 12, 2026/)).toBeDefined();
  });

  it('shows the month in Italian for lcid 1040', () => {
    setUiLanguage(1040);
    calendar();
    expect(screen.getByText('novembre 2026')).toBeInTheDocument();
  });

  it('navigates to the previous, next and today months', async () => {
    const { onNavigate } = calendar();
    await userEvent.click(screen.getByRole('button', { name: 'Previous month' }));
    expect(onNavigate).toHaveBeenLastCalledWith(2026, 10);
    await userEvent.click(screen.getByRole('button', { name: 'Next month' }));
    expect(onNavigate).toHaveBeenLastCalledWith(2026, 12);
    await userEvent.click(screen.getByRole('button', { name: 'Today' }));
    const today = wallClockOf(new Date(), 'Europe/Rome');
    expect(onNavigate).toHaveBeenLastCalledWith(today.year, today.month);
  });

  it('wraps from January to December of the previous year', async () => {
    const { onNavigate } = calendar({ month: 1 });
    await userEvent.click(screen.getByRole('button', { name: 'Previous month' }));
    expect(onNavigate).toHaveBeenLastCalledWith(2025, 12);
  });

  it('shows a record on the day of its start in the zone and opens it', async () => {
    const { onOpen } = calendar({ rows: [{ id: 'r1', name: 'Kick-off', starts: '2026-11-30T23:30:00Z' }] });
    // 23:30 UTC is already December 1st in Rome.
    const day = cell(/December 1, 2026/);
    await userEvent.click(within(day).getByRole('button', { name: 'Kick-off, Dec 1 – Dec 1, 1 day' }));
    expect(onOpen).toHaveBeenCalledWith('r1');
    expect(within(cell(/November 30, 2026/)).queryByRole('button')).toBeNull();
  });

  it('places a date only record by its date text', () => {
    calendar({ dateOnly: true, zone: 'America/Los_Angeles', rows: [{ id: 'r1', name: 'Due', starts: '2026-12-01T00:00:00Z' }] });
    expect(within(cell(/December 1, 2026/)).getByRole('button', { name: /^Due,/ })).toBeInTheDocument();
  });

  it('draws one continuous bar per record, cut by week, labelled once', async () => {
    const { onOpen } = calendar({
      end: 'ends',
      rows: [{ id: 'r1', name: 'CRM migration', starts: '2026-11-03T09:00:00Z', ends: '2026-11-26T09:00:00Z' }],
    });
    expect(screen.getAllByText('CRM migration')).toHaveLength(1);
    const segments = screen.getAllByRole('button', { name: 'CRM migration, Nov 3 – Nov 26, 24 days' });
    expect(segments).toHaveLength(4);
    expect(screen.getByText('Nov 3 – Nov 26 · 24 d')).toBeInTheDocument();
    expect(within(cell(/November 3, 2026/)).getByRole('button')).toBe(segments[0]);
    await userEvent.click(segments[2]!);
    expect(onOpen).toHaveBeenCalledWith('r1');
  });

  it('marks a record that starts before the grid and one that ends after it', () => {
    calendar({
      end: 'ends',
      rows: [
        { id: 'r1', name: 'Redesign', starts: '2026-10-12T09:00:00Z', ends: '2026-11-18T09:00:00Z' },
        { id: 'r2', name: 'Go-live', starts: '2026-11-23T09:00:00Z', ends: '2026-12-20T09:00:00Z' },
      ],
    });
    const before = screen.getAllByRole('button', { name: /^Redesign,/ });
    expect(before[0]).toHaveTextContent('◀');
    expect(before.slice(1).some((b) => b.textContent?.includes('◀'))).toBe(false);
    expect(before.some((b) => b.textContent?.includes('▶'))).toBe(false);
    // The grid starts on Monday Oct 26: the label sits on the first shown day.
    expect(within(cell(/October 26, 2026/)).getByText('Redesign')).toBeInTheDocument();
    const after = screen.getAllByRole('button', { name: /^Go-live,/ });
    expect(after[after.length - 1]).toHaveTextContent('▶');
    expect(after[0]).not.toHaveTextContent('▶');
  });

  it('spans the whole grid with arrows on both sides', () => {
    calendar({ end: 'ends', rows: [{ id: 'r1', name: 'Year', starts: '2026-01-01T09:00:00Z', ends: '2026-12-31T09:00:00Z' }] });
    const segments = screen.getAllByRole('button', { name: /^Year,/ });
    expect(segments).toHaveLength(6);
    expect(screen.getAllByText('Year')).toHaveLength(1);
    expect(segments[0]).toHaveTextContent('◀');
    expect(segments[5]).toHaveTextContent('▶');
  });

  it('counts the bars beyond four lanes and opens them from the popover', async () => {
    const rows = ['A', 'B', 'C', 'D', 'E'].map((name, i) => ({ id: `r${i}`, name, starts: '2026-11-10T09:00:00Z', ends: '2026-11-11T09:00:00Z' }));
    const { onOpen } = calendar({ end: 'ends', rows });
    expect(screen.queryAllByRole('button', { name: /^E,/ })).toHaveLength(0);
    expect(within(cell(/November 10, 2026/)).getByText('+1 more')).toBeInTheDocument();
    expect(within(cell(/November 11, 2026/)).getByText('+1 more')).toBeInTheDocument();
    expect(within(cell(/November 12, 2026/)).queryByText(/more/)).toBeNull();
    await userEvent.click(within(cell(/November 10, 2026/)).getByText('+1 more'));
    await userEvent.click(await screen.findByRole('button', { name: /^E,/ }));
    expect(onOpen).toHaveBeenCalledWith('r4');
  });

  it('keeps the today style on the cell of today', () => {
    const today = wallClockOf(new Date(), 'Europe/Rome');
    calendar({ year: today.year, month: today.month });
    const name = new Intl.DateTimeFormat('en-US', { dateStyle: 'long', timeZone: 'UTC' }).format(new Date(Date.UTC(today.year, today.month - 1, today.day)));
    const todayCell = cell(name);
    const other = screen.getAllByRole('gridcell').find((c) => c !== todayCell && c.className.split(' ').length === todayCell.className.split(' ').length - 1);
    expect(other).toBeDefined();
  });

  it('renders the toolbar extra after the title', () => {
    calendar({ toolbarExtra: <button type="button">Layout</button> });
    expect(screen.getByRole('button', { name: 'Layout' })).toBeInTheDocument();
  });

  it('counts the records without a start', () => {
    calendar({ rows: [{ id: 'r1', name: 'A', starts: null }, { id: 'r2', name: 'B' }, { id: 'r3', name: 'C', starts: '2026-11-03T10:00:00Z' }] });
    expect(screen.getByText('Without a date, not shown: 2')).toBeInTheDocument();
  });

  it('shows the counted number of undated records when given', () => {
    calendar({ undated: 7, rows: [{ id: 'r1', name: 'C', starts: '2026-11-03T10:00:00Z' }] });
    expect(screen.getByText('Without a date, not shown: 7')).toBeInTheDocument();
  });

  it('shows no undated note when every record is dated', () => {
    calendar({ rows: [{ id: 'r1', name: 'C', starts: '2026-11-03T10:00:00Z' }] });
    expect(screen.queryByText(/without a date/)).toBeNull();
  });

  it('uses the grid of monthGrid', () => {
    calendar({ weekStart: 0, month: 6 });
    expect(screen.getAllByRole('gridcell')[0]).toHaveAttribute('aria-label', expect.stringContaining('May 31, 2026'));
    expect(monthGrid(2026, 6, 0)[0]!.day).toBe(31);
  });
});
