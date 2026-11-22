import { Link, makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { useMemo, type CSSProperties } from 'react';
import { t, uiLocale } from '../../shell/i18n/t';
import { cellText } from '../runView';
import { wallClockOf } from '../timeZones';
import { calendarBars, type Bar } from './calendarBars';
import { keyOf } from './calendarGrid';
import type { CalendarMonthProps } from './CalendarMonth';
import { BAR_INSET, barLabel, barSurface, CalendarToolbar, formatDay, shortDay, useBarStyles } from './calendarShared';

/** The plain class of the title column (header and cells), which stays in view when the days scroll sideways. */
export const TIMELINE_STICKY_CLASS = 'xrmv-timeline-sticky';

const BAR = 20;

const useStyles = makeStyles({
  scroll: { overflowX: 'auto', border: `1px solid ${tokens.colorNeutralStroke2}` },
  row: { display: 'grid', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, ':last-child': { borderBottom: 'none' } },
  sticky: {
    position: 'sticky',
    left: 0,
    zIndex: 2,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    gridRow: 1,
    gridColumn: 1,
    minWidth: 0,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    display: 'flex',
    flexDirection: 'column',
    justifyContent: 'center',
  },
  corner: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, justifyContent: 'flex-start' },
  head: {
    gridRow: 1,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    padding: `${tokens.spacingVerticalXS} 0`,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
    color: tokens.colorNeutralForeground3,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  headToday: { backgroundColor: tokens.colorBrandBackground, color: tokens.colorNeutralForegroundOnBrand, fontWeight: tokens.fontWeightSemibold },
  day: { gridRow: 1, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  weekend: { backgroundColor: tokens.colorNeutralBackground2 },
  today: { backgroundColor: tokens.colorBrandBackground2 },
  title: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', textAlign: 'start', fontWeight: tokens.fontWeightSemibold, maxWidth: '100%' },
  dates: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' },
  cell: { gridRow: 1, alignSelf: 'center', zIndex: 1, display: 'flex', minWidth: 0 },
  bar: { width: '100%', height: `${BAR}px`, lineHeight: `${BAR}px`, fontWeight: tokens.fontWeightSemibold },
});

/** The days `yyyy-mm-01` to the last of the month. */
function monthDays(year: number, month: number): { year: number; month: number; day: number; weekday: number }[] {
  const last = new Date(0);
  last.setUTCFullYear(year, month, 0);
  const count = last.getUTCDate();
  return Array.from({ length: count }, (_, i) => {
    const date = new Date(Date.UTC(2000, month - 1, i + 1));
    date.setUTCFullYear(year);
    return { year, month, day: i + 1, weekday: date.getUTCDay() };
  });
}

/** The month as a timeline: one row per record meeting the month, the days of the month as columns, the record's bar across its days. */
export function CalendarTimeline({ label, year, month, start, end, dateOnly, zoneIndependent, zone, rows, primaryId, titleColumn, undated: undatedCount, toolbarExtra, onNavigate, onOpen }: Omit<CalendarMonthProps, 'weekStart'>) {
  const styles = useStyles();
  const barStyles = useBarStyles();
  const locale = uiLocale();
  const days = useMemo(() => monthDays(year, month), [year, month]);
  const keys = useMemo(() => days.map(keyOf), [days]);

  const eventZone = zoneIndependent ? 'UTC' : zone;
  const { bars, undated: undatedRows } = useMemo(() => {
    const undated = rows.filter((row) => !(typeof row[start] === 'string' && row[start] !== '')).length;
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
    return { bars, undated };
  }, [rows, keys, start, end, dateOnly, eventZone, primaryId, titleColumn]);

  const undated = undatedCount ?? undatedRows;
  const todayKey = keyOf(wallClockOf(new Date(), zone));
  const template: CSSProperties = { gridTemplateColumns: `minmax(200px, 260px) repeat(${days.length}, minmax(28px, 1fr))` };
  const minWidth = 200 + days.length * 28;
  const open = (bar: Bar) => bar.id && onOpen(bar.id);
  const dayClass = (i: number) => {
    const d = days[i]!;
    return mergeClasses(styles.day, (d.weekday === 0 || d.weekday === 6) && styles.weekend, keys[i] === todayKey && styles.today);
  };

  const barButton = (bar: Bar) => {
    const from = keys.indexOf(bar.from);
    const span = keys.indexOf(bar.to) - from + 1;
    const roundStart = !bar.startsBefore;
    const roundEnd = !bar.endsAfter;
    // The bar sits in a gridcell spanning its days, so the row reads as rowheader + one cell per bar.
    const cellStyle: CSSProperties = {
      gridColumn: `${from + 2} / span ${span}`,
      marginLeft: roundStart ? `${BAR_INSET}px` : 0,
      marginRight: roundEnd ? `${BAR_INSET}px` : 0,
    };
    return (
      <div role="gridcell" className={styles.cell} style={cellStyle}>
        <button type="button" className={mergeClasses(barStyles.bar, styles.bar)} style={barSurface(bar.index, roundStart, roundEnd)} aria-label={barLabel(locale, bar)} onClick={() => open(bar)}>
          {bar.startsBefore && <span className={barStyles.arrow} aria-hidden>◀</span>}
          <span>{t('apps.views.calendar.days', { days: bar.days })}</span>
          {bar.endsAfter && <span className={barStyles.end} aria-hidden>▶</span>}
        </button>
      </div>
    );
  };

  return (
    <div>
      <CalendarToolbar locale={locale} year={year} month={month} zone={zone} undated={undated} extra={toolbarExtra} onNavigate={onNavigate} />
      <div className={styles.scroll}>
        <div role="grid" aria-label={label} style={{ minWidth: `${minWidth}px` }}>
          <div role="row" className={styles.row} style={template}>
            <div role="columnheader" className={mergeClasses(TIMELINE_STICKY_CLASS, styles.sticky, styles.corner)}>
              {t('apps.views.calendar.record')}
            </div>
            {days.map((d, i) => (
              <div
                key={keys[i]}
                role="columnheader"
                aria-label={formatDay(locale, d, { dateStyle: 'long' })}
                className={mergeClasses(styles.head, d.weekday === 0 || d.weekday === 6 ? styles.weekend : undefined, keys[i] === todayKey && styles.headToday)}
                style={{ gridColumn: i + 2 }}
              >
                <span aria-hidden>{formatDay(locale, d, { weekday: 'narrow' })}</span>
                <span aria-hidden>{d.day}</span>
              </div>
            ))}
          </div>
          {bars.map((bar) => (
            <div key={bar.key} role="row" className={styles.row} style={template}>
              <div role="rowheader" className={mergeClasses(TIMELINE_STICKY_CLASS, styles.sticky)}>
                {bar.id ? (
                  <Link as="button" className={styles.title} onClick={() => open(bar)}>
                    {bar.title}
                  </Link>
                ) : (
                  <span className={styles.title}>{bar.title}</span>
                )}
                <span className={styles.dates}>{`${shortDay(locale, bar.start)} – ${shortDay(locale, bar.end)} · ${t('apps.views.calendar.days', { days: bar.days })}`}</span>
              </div>
              {days.map((_, i) => (
                <div key={keys[i]} aria-hidden className={dayClass(i)} style={{ gridColumn: i + 2 }} />
              ))}
              {barButton(bar)}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
