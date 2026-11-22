import { Link, makeStyles, mergeClasses, Popover, PopoverSurface, PopoverTrigger, tokens } from '@fluentui/react-components';
import { useMemo, type CSSProperties, type ReactNode } from 'react';
import type { ViewColumn } from '../../model/view';
import { t, uiLocale } from '../../shell/i18n/t';
import { cellText } from '../runView';
import { wallClockOf } from '../timeZones';
import { calendarBars, monthSegments, type Bar, type Segment } from './calendarBars';
import { BAR_INSET, barLabel as labelOf, barSurface, CalendarToolbar, formatDay, shortDay as shortDayOf, useBarStyles } from './calendarShared';
import { keyOf, monthGrid } from './calendarGrid';

export interface CalendarMonthProps {
  label: string;
  /** The shown month (`month` 1 to 12). */
  year: number;
  month: number;
  /** 0 Sunday, 1 Monday. */
  weekStart: 0 | 1;
  /** The start and end columns (raw values: ISO UTC for a date and time, `yyyy-mm-dd` for a date only). */
  start: string;
  end?: string;
  dateOnly: boolean;
  /** The column stores a wall clock (TimeZoneIndependent): its text is read as UTC whatever the user's zone. */
  zoneIndependent?: boolean;
  /** The user's IANA time zone. */
  zone: string;
  rows: Record<string, unknown>[];
  primaryId: string;
  /** The view's first visible column: the title of an event. */
  titleColumn: ViewColumn;
  /** The records without a start, counted by a request of their own (the range request cannot return them); else counted in `rows`. */
  undated?: number;
  /** Shown in the toolbar after the title (the layout toggle). */
  toolbarExtra?: ReactNode;
  onNavigate(year: number, month: number): void;
  onOpen(id: string): void;
}

/** The height of the day number row, of a bar lane, of a bar and of the `+N more` line, in pixels. */
const NUMBER_ROW = 28;
const LANE = 24;
const BAR = 20;
const MORE_ROW = 20;
const INSET = BAR_INSET;

const useStyles = makeStyles({
  scroll: { overflowX: 'auto' },
  grid: { minWidth: '672px' },
  row: { display: 'grid', gridTemplateColumns: 'repeat(7, minmax(96px, 1fr))' },
  week: { position: 'relative', minHeight: '88px' },
  head: { padding: tokens.spacingHorizontalXS, textAlign: 'center', color: tokens.colorNeutralForeground3 },
  cell: {
    minWidth: 0,
    padding: tokens.spacingHorizontalXS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  outside: { backgroundColor: tokens.colorNeutralBackground2, color: tokens.colorNeutralForeground3 },
  today: { backgroundColor: tokens.colorBrandBackground2 },
  number: { fontWeight: tokens.fontWeightSemibold },
  more: { marginTop: 'auto', alignSelf: 'flex-start', fontSize: tokens.fontSizeBase200, position: 'relative', zIndex: 1 },
  segment: { position: 'absolute', zIndex: 1, height: `${BAR}px`, lineHeight: `${BAR}px` },
  label: { fontWeight: tokens.fontWeightSemibold, overflow: 'hidden', textOverflow: 'ellipsis', flexShrink: 0, maxWidth: '100%' },
  dates: { opacity: 0.8, overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0 },
  hiddenList: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: tokens.spacingVerticalXS },
});

const text = (v: unknown): string | undefined => (typeof v === 'string' && v !== '' ? v : undefined);

/** A month grid of 6 weeks: each record is one continuous bar from its start to its end (in the user's zone), cut at the week rows. */
export function CalendarMonth({ label, year, month, weekStart, start, end, dateOnly, zoneIndependent, zone, rows, primaryId, titleColumn, undated: undatedCount, toolbarExtra, onNavigate, onOpen }: CalendarMonthProps) {
  const styles = useStyles();
  const barStyles = useBarStyles();
  const locale = uiLocale();
  const days = useMemo(() => monthGrid(year, month, weekStart), [year, month, weekStart]);
  const keys = useMemo(() => days.map(keyOf), [days]);

  const eventZone = zoneIndependent ? 'UTC' : zone;
  const { segments, hidden, undated: undatedRows } = useMemo(() => {
    const undated = rows.filter((row) => !text(row[start])).length;
    const bars = calendarBars(rows, {
      start,
      end,
      dateOnly,
      zone: eventZone,
      primaryId,
      title: (row) => cellText(row, titleColumn),
      first: keys[0]!,
      last: keys[keys.length - 1]!,
    });
    return { ...monthSegments(bars, keys), undated };
  }, [rows, keys, start, end, dateOnly, eventZone, primaryId, titleColumn]);

  const undated = undatedCount ?? undatedRows;
  const todayKey = keyOf(wallClockOf(new Date(), zone));
  const format = (d: { year: number; month: number; day: number }, options: Intl.DateTimeFormatOptions) => formatDay(locale, d, options);
  const weekdays = days.slice(0, 7).map((d) => format(d, { weekday: 'short' }));
  const shortDay = (key: string) => shortDayOf(locale, key);
  const barLabel = (bar: Bar) => labelOf(locale, bar);
  const open = (bar: Bar) => bar.id && onOpen(bar.id);

  const segment = (s: Segment) => {
    const left = s.roundStart ? INSET : 0;
    const right = s.roundEnd ? INSET : 0;
    const style: CSSProperties = {
      left: `calc(${(s.col * 100) / 7}% + ${left}px)`,
      width: `calc(${(s.span * 100) / 7}% - ${left + right}px)`,
      top: `${NUMBER_ROW + s.lane * LANE}px`,
      ...barSurface(s.bar.index, s.roundStart, s.roundEnd),
    };
    const lastShown = s.week * 7 + s.col + s.span - 1 === keys.indexOf(s.bar.to);
    return (
      <button key={`${s.bar.key}-${s.week}`} type="button" className={mergeClasses(barStyles.bar, styles.segment)} style={style} aria-label={barLabel(s.bar)} onClick={() => open(s.bar)}>
        {s.labelled && (
          <>
            {s.bar.startsBefore && <span className={barStyles.arrow} aria-hidden>◀</span>}
            <span className={styles.label}>{s.bar.title}</span>
            <span className={styles.dates}>
              {`${shortDay(s.bar.start)} – ${shortDay(s.bar.end)} · ${t('apps.views.calendar.days', { days: s.bar.days })}`}
            </span>
          </>
        )}
        {lastShown && s.bar.endsAfter && <span className={barStyles.end} aria-hidden>▶</span>}
      </button>
    );
  };

  return (
    <div>
      <CalendarToolbar locale={locale} year={year} month={month} zone={zone} undated={undated} extra={toolbarExtra} onNavigate={onNavigate} />
      <div className={styles.scroll}>
        <div className={styles.grid} role="grid" aria-label={label}>
          <div role="row" className={styles.row}>
            {weekdays.map((name, i) => (
              <div key={i} role="columnheader" className={styles.head}>
                {name}
              </div>
            ))}
          </div>
          {Array.from({ length: 6 }, (_, week) => {
            const inWeek = segments.filter((s) => s.week === week);
            const weekKeys = keys.slice(week * 7, week * 7 + 7);
            const lanes = inWeek.reduce((n, s) => Math.max(n, s.lane + 1), 0);
            const anyHidden = weekKeys.some((key) => hidden.has(key));
            const height = NUMBER_ROW + lanes * LANE + (anyHidden ? MORE_ROW : 0) + 8;
            return (
              <div key={week} role="row" className={mergeClasses(styles.row, styles.week)} style={{ height: `${Math.max(88, height)}px` }}>
                {days.slice(week * 7, week * 7 + 7).map((d, col) => {
                  const key = keyOf(d);
                  const more = hidden.get(key) ?? [];
                  return (
                    <div
                      key={key}
                      role="gridcell"
                      aria-label={format(d, { dateStyle: 'long' })}
                      className={mergeClasses(styles.cell, !d.inMonth && styles.outside, key === todayKey && styles.today)}
                    >
                      <span className={styles.number}>{d.day}</span>
                      {inWeek.filter((s) => s.col === col).map(segment)}
                      {more.length > 0 && (
                        <Popover withArrow>
                          <PopoverTrigger disableButtonEnhancement>
                            <Link as="button" className={styles.more}>
                              {t('apps.views.calendar.more', { count: more.length })}
                            </Link>
                          </PopoverTrigger>
                          <PopoverSurface>
                            <div className={styles.hiddenList}>
                              {more.map((bar) => (
                                <Link key={bar.key} as="button" aria-label={barLabel(bar)} onClick={() => open(bar)}>
                                  {bar.title}
                                </Link>
                              ))}
                            </div>
                          </PopoverSurface>
                        </Popover>
                      )}
                    </div>
                  );
                })}
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
