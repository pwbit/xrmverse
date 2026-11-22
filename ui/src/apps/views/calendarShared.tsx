import { Button, makeStyles, Text, tokens } from '@fluentui/react-components';
import type { CSSProperties, ReactNode } from 'react';
import { t } from '../../shell/i18n/t';
import { NextPageIcon, PreviousPageIcon } from '../../shell/icons';
import { wallClockOf } from '../timeZones';
import { barColour } from './barColours';
import type { Bar } from './calendarBars';

type YMD = { year: number; month: number; day: number };

const utcDate = (d: YMD) => {
  const date = new Date(Date.UTC(2000, d.month - 1, d.day));
  date.setUTCFullYear(d.year);
  return date;
};

/** Formats a calendar day (no time, no zone shift) in `locale`. */
export const formatDay = (locale: string, d: YMD, options: Intl.DateTimeFormatOptions): string =>
  new Intl.DateTimeFormat(locale, { ...options, timeZone: 'UTC' }).format(utcDate(d));

/** The day of a `yyyy-mm-dd` key. */
export const dayOfKey = (key: string): YMD => ({ year: Number(key.slice(0, 4)), month: Number(key.slice(5, 7)), day: Number(key.slice(8, 10)) });

/** The short day of a `yyyy-mm-dd` key, e.g. `Nov 3` in en-US, `3 nov` in Italian. */
export const shortDay = (locale: string, key: string): string => formatDay(locale, dayOfKey(key), { day: 'numeric', month: 'short' });

/** The accessible name of a bar: `title, start – end, N days`. */
export const barLabel = (locale: string, bar: Bar): string =>
  t(bar.days === 1 ? 'apps.views.calendar.barLabelOne' : 'apps.views.calendar.barLabel', { title: bar.title, start: shortDay(locale, bar.start), end: shortDay(locale, bar.end), days: bar.days });

/** The inset of a bar on a day where the record really starts or ends, and the width of its start marker, in pixels. */
export const BAR_INSET = 4;

/** The colours and edges of a bar: rounded with a start marker only where the record really starts, rounded where it really ends. */
export function barSurface(index: number, roundStart: boolean, roundEnd: boolean): CSSProperties {
  const { fill, ink } = barColour(index);
  const radius = tokens.borderRadiusMedium;
  return {
    backgroundColor: fill,
    color: ink,
    borderLeft: roundStart ? `${BAR_INSET}px solid ${ink}` : undefined,
    borderStartStartRadius: roundStart ? radius : 0,
    borderEndStartRadius: roundStart ? radius : 0,
    borderStartEndRadius: roundEnd ? radius : 0,
    borderEndEndRadius: roundEnd ? radius : 0,
  };
}

/** The look of a bar button shared by the month and the timeline layouts (place and size are the layout's). */
export const useBarStyles = makeStyles({
  bar: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    boxSizing: 'border-box',
    margin: 0,
    border: 'none',
    paddingBlock: 0,
    paddingInline: tokens.spacingHorizontalXS,
    overflow: 'hidden',
    whiteSpace: 'nowrap',
    fontFamily: 'inherit',
    fontSize: tokens.fontSizeBase200,
    textAlign: 'start',
    cursor: 'pointer',
    ':hover': { filter: 'brightness(0.96)' },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '1px' },
  },
  arrow: { flexShrink: 0, fontSize: '10px' },
  end: { marginLeft: 'auto', flexShrink: 0, fontSize: '10px' },
});

const useToolbarStyles = makeStyles({
  bar: { display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: tokens.spacingHorizontalS, paddingBottom: tokens.spacingVerticalS },
  title: { flexGrow: 1, textTransform: 'capitalize' },
});

export interface CalendarToolbarProps {
  locale: string;
  year: number;
  month: number;
  zone: string;
  undated: number;
  extra?: ReactNode;
  onNavigate(year: number, month: number): void;
}

/** Previous / next / today, the month title, the layout toggle and the undated note. */
export function CalendarToolbar({ locale, year, month, zone, undated, extra, onNavigate }: CalendarToolbarProps) {
  const styles = useToolbarStyles();
  const previous = month === 1 ? [year - 1, 12] : [year, month - 1];
  const next = month === 12 ? [year + 1, 1] : [year, month + 1];
  return (
    <div className={styles.bar}>
      <Button appearance="subtle" icon={<PreviousPageIcon />} aria-label={t('apps.views.calendar.previous')} onClick={() => onNavigate(previous[0]!, previous[1]!)} />
      <Button appearance="subtle" icon={<NextPageIcon />} aria-label={t('apps.views.calendar.next')} onClick={() => onNavigate(next[0]!, next[1]!)} />
      <Button
        onClick={() => {
          const now = wallClockOf(new Date(), zone);
          onNavigate(now.year, now.month);
        }}
      >
        {t('apps.views.calendar.today')}
      </Button>
      <Text as="h2" size={500} weight="semibold" className={styles.title} aria-live="polite">
        {formatDay(locale, { year, month, day: 1 }, { month: 'long', year: 'numeric' })}
      </Text>
      {extra}
      {undated > 0 && <Text>{t('apps.views.calendar.undated', { count: undated })}</Text>}
    </div>
  );
}
