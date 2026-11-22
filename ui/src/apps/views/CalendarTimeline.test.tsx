import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ViewColumn } from '../../model/view';
import { setUiLanguage } from '../../shell/i18n/t';
import { wallClockOf } from '../timeZones';
import { CalendarTimeline, TIMELINE_STICKY_CLASS } from './CalendarTimeline';
import type { CalendarMonthProps } from './CalendarMonth';

const TITLE: ViewColumn = { name: 'name', width: 100, attribute: 'name', isHidden: false };

function timeline(props: Partial<Omit<CalendarMonthProps, 'weekStart'>> = {}) {
  const onNavigate = vi.fn();
  const onOpen = vi.fn();
  const utils = render(
    <FluentProvider theme={webLightTheme}>
      <CalendarTimeline
        label="Projects"
        year={2026}
        month={11}
        start="starts"
        end="ends"
        dateOnly
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

const ROWS = [
  { id: 'r2', name: 'CRM migration', starts: '2026-11-03', ends: '2026-11-26' },
  { id: 'r1', name: 'Website redesign', starts: '2026-10-12', ends: '2026-11-18' },
  { id: 'r3', name: 'Go-live support', starts: '2026-11-23', ends: '2026-12-20' },
  { id: 'r4', name: 'Last year', starts: '2026-10-01', ends: '2026-10-20' },
];

const recordRows = () => screen.getAllByRole('row').slice(1);
const bar = (title: string) => screen.getByRole('button', { name: new RegExp(`^${title},`) });

describe('CalendarTimeline', () => {
  afterEach(() => setUiLanguage(1033));

  it('lists one row per record meeting the month, sorted by start', () => {
    timeline({ rows: ROWS });
    const titles = recordRows().map((row) => within(row).getByRole('rowheader').textContent);
    expect(titles).toHaveLength(3);
    expect(titles[0]).toContain('Website redesign');
    expect(titles[1]).toContain('CRM migration');
    expect(titles[2]).toContain('Go-live support');
    expect(screen.queryByText('Last year')).toBeNull();
  });

  it('shows the title and the dates with the duration in the title cell', () => {
    timeline({ rows: ROWS });
    const header = within(recordRows()[1]!).getByRole('rowheader');
    expect(within(header).getByText('CRM migration')).toBeInTheDocument();
    expect(within(header).getByText('Nov 3 – Nov 26 · 24 d')).toBeInTheDocument();
  });

  it('has a column per day of the month after the title column', () => {
    timeline();
    const heads = screen.getAllByRole('columnheader');
    expect(heads).toHaveLength(31);
    expect(heads[0]).toHaveTextContent('Record');
    expect(heads[30]).toHaveAccessibleName('November 30, 2026');
  });

  it('places the bar on the columns of its days and opens the record', async () => {
    const { onOpen } = timeline({ rows: ROWS });
    const crm = bar('CRM migration');
    expect(crm.parentElement!.style.gridColumn).toBe('4 / span 24');
    expect(crm).toHaveAccessibleName('CRM migration, Nov 3 – Nov 26, 24 days');
    expect(crm).toHaveTextContent('24 d');
    await userEvent.click(crm);
    expect(onOpen).toHaveBeenCalledWith('r2');
  });

  it('starts a bar from October at the first day with an arrow', () => {
    timeline({ rows: ROWS });
    const site = bar('Website redesign');
    expect(site.parentElement!.style.gridColumn).toBe('2 / span 18');
    expect(site).toHaveTextContent('◀');
    expect(site).toHaveTextContent('38 d');
  });

  it('ends a bar going into December at the last day with an arrow', () => {
    timeline({ rows: ROWS });
    const live = bar('Go-live support');
    expect(live.parentElement!.style.gridColumn).toBe('24 / span 8');
    expect(live).toHaveTextContent('▶');
    expect(live).not.toHaveTextContent('◀');
  });

  it('keeps the title column sticky', () => {
    timeline({ rows: ROWS });
    expect(screen.getAllByRole('columnheader')[0]).toHaveClass(TIMELINE_STICKY_CLASS);
    for (const row of recordRows()) expect(within(row).getByRole('rowheader')).toHaveClass(TIMELINE_STICKY_CLASS);
  });

  it('navigates by month and shows the toolbar extra and the undated note', async () => {
    const { onNavigate } = timeline({ undated: 2, toolbarExtra: <span>extra</span> });
    expect(screen.getByText('November 2026')).toBeInTheDocument();
    expect(screen.getByText('extra')).toBeInTheDocument();
    expect(screen.getByText('Without a date, not shown: 2')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Previous month' }));
    expect(onNavigate).toHaveBeenLastCalledWith(2026, 10);
    await userEvent.click(screen.getByRole('button', { name: 'Next month' }));
    expect(onNavigate).toHaveBeenLastCalledWith(2026, 12);
    await userEvent.click(screen.getByRole('button', { name: 'Today' }));
    const today = wallClockOf(new Date(), 'Europe/Rome');
    expect(onNavigate).toHaveBeenLastCalledWith(today.year, today.month);
  });

  it('shows the days of February only', () => {
    timeline({ year: 2027, month: 2 });
    expect(screen.getAllByRole('columnheader')).toHaveLength(29);
  });
});
