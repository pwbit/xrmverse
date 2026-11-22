import { Field, FieldContextProvider, makeStyles, tokens, type FieldControlProps } from '@fluentui/react-components';
import { DatePicker, defaultDatePickerStrings, type CalendarStrings, type DatePickerProps } from '@fluentui/react-datepicker-compat';
import { TimePicker, type TimePickerProps } from '@fluentui/react-timepicker-compat';
import { useContext, useEffect, useMemo, useState } from 'react';
import { DisplayZoneContext } from '../../../apps/displayZone';
import { formatUserDate, formatUserTime, parseUserDate, parseUserTime, patternText, usesTwelveHours } from '../../../apps/dateFormat';
import { zoneLabel } from '../../../apps/timeZones';
import { useLoadedUserSettings } from '../../../apps/userSettings';
import { t, uiLocale } from '../../../shell/i18n/t';
import { isUserLocal } from '../metadata';
import { useFieldAppearance } from './fieldAppearance';
import type { FieldProps } from './fieldProps';

type DayOfWeek = NonNullable<DatePickerProps['firstDayOfWeek']>;
type TimeChange = NonNullable<TimePickerProps['onTimeChange']>;
type TimeParse = NonNullable<TimePickerProps['parseTimeStringToDate']>;

const useStyles = makeStyles({
  // In a narrow form column the time box wraps under the date instead of running past the section's edge.
  row: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS, alignItems: 'start' },
  date: { flexGrow: 1, flexBasis: '160px', minWidth: 0 },
  // The combobox's input keeps its intrinsic width (about 20 characters) unless told otherwise, which pushes the
  // dropdown button out of a 120px box: its grid column may shrink and the input fills it.
  time: { width: '120px', minWidth: '120px', gridTemplateColumns: 'minmax(0, 1fr) auto' },
  // Same for the date's input: without it a narrow column cannot shrink the picker below about 250px.
  input: { minWidth: 0, width: '100%' },
});

const pad = (n: number): string => String(n).padStart(2, '0');

/**
 * `YYYY-MM-DD` from the date's local parts. The picker's dates are local midnights built from the same parts
 * (`parseIsoDate`), so a date goes in and out unchanged whatever the browser's time zone.
 */
export function formatIsoDate(date?: Date): string {
  return date ? `${String(date.getFullYear()).padStart(4, '0')}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}` : '';
}

/** Parses `YYYY-MM-DD` into a local date; any other text, or a day that does not exist (`2026-02-30`), is null. */
export function parseIsoDate(text: string): Date | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text.trim());
  if (!m) return null;
  const [year, month, day] = [Number(m[1]), Number(m[2]) - 1, Number(m[3])];
  const date = new Date(year, month, day);
  date.setFullYear(year); // the constructor maps years 0-99 to 1900-1999
  return date.getFullYear() === year && date.getMonth() === month && date.getDate() === day ? date : null;
}

/** `HH:mm` (24 hours) of a time's local parts. */
const formatTime = (date: Date): string => `${pad(date.getHours())}:${pad(date.getMinutes())}`;

/** Parses `H:mm` or `HH:mm` (24 hours); null when it is not a valid time. */
function parseTime(text: string): { hours: number; minutes: number } | null {
  const m = /^(\d{1,2}):(\d{2})$/.exec(text.trim());
  if (!m) return null;
  const [hours, minutes] = [Number(m[1]), Number(m[2])];
  return hours < 24 && minutes < 60 ? { hours, minutes } : null;
}

/**
 * The day the time box's times live on. The value is a clock in the user's zone (or UTC, or a time zone independent value),
 * never in the browser's, so it must not depend on the browser's clock of the chosen day: on a DST change a local day lacks (or repeats) an hour, and `setHours(2, 30)` on 2026-03-29 in Rome gives 03:30.
 * A fixed day with no DST change keeps every HH:mm; only HH:mm is ever read back.
 */
const TIME_ANCHOR = new Date(2000, 0, 1);

const atTime = (time: { hours: number; minutes: number }): Date =>
  new Date(TIME_ANCHOR.getFullYear(), TIME_ANCHOR.getMonth(), TIME_ANCHOR.getDate(), time.hours, time.minutes);

/** Calendar month and day names of the shell language (`Intl`), with the picker's other strings from the catalogue. */
function calendarStrings(locale: string): CalendarStrings {
  const months = (month: 'long' | 'short') =>
    Array.from({ length: 12 }, (_, i) => new Intl.DateTimeFormat(locale, { month }).format(new Date(2026, i, 1)));
  // 2026-01-04 is a Sunday: the calendar's day arrays start on Sunday.
  const days = (weekday: 'long' | 'narrow') =>
    Array.from({ length: 7 }, (_, i) => new Intl.DateTimeFormat(locale, { weekday }).format(new Date(2026, 0, 4 + i)));
  return {
    ...defaultDatePickerStrings,
    months: months('long'),
    shortMonths: months('short'),
    days: days('long'),
    shortDays: days('narrow'),
    goToToday: t('field.date.goToToday'),
    prevMonthAriaLabel: t('field.date.prevMonth'),
    nextMonthAriaLabel: t('field.date.nextMonth'),
    prevYearAriaLabel: t('field.date.prevYear'),
    nextYearAriaLabel: t('field.date.nextYear'),
  };
}

/**
 * `datetime` columns: a date (`YYYY-MM-DD`), or a date and time (`YYYY-MM-DDTHH:mm`): a User Local one in the display zone
 * (the user's in the apps, UTC in the web admin; the form converts it, #131), any other as stored. The date is typed as `YYYY-MM-DD` or picked in the calendar; the time is typed as `HH:mm` or picked in
 * 30-minute steps, and is `00:00` until set. Clearing the date clears the value. In the apps the date and the time are shown
 * and typed in the user's formats (`usersettings`, plan "personal date format" F5), `YYYY-MM-DD` and `HH:mm` being read too.
 */
export function DateField({ attr, value, onChange, disabled, error }: FieldProps) {
  const styles = useStyles();
  const appearance = useFieldAppearance(disabled);
  const text = typeof value === 'string' ? value : '';
  const datePart = text.slice(0, 10);
  const time = attr.dateOnly ? '' : text.slice(11, 16);
  const date = useMemo(() => parseIsoDate(datePart), [datePart]);
  const locale = uiLocale();
  const strings = useMemo(() => calendarStrings(locale), [locale]);
  const zone = useContext(DisplayZoneContext);
  const formats = useLoadedUserSettings()?.formats;
  const showDate = useMemo(() => (formats ? (d?: Date) => (d ? formatUserDate(d, formats, locale) : '') : formatIsoDate), [formats, locale]);
  const readDate = useMemo(() => (formats ? (s: string) => parseUserDate(s, formats) ?? parseIsoDate(s) : parseIsoDate), [formats]);
  const showTime = useMemo(
    () => (formats ? (t: { hours: number; minutes: number }) => formatUserTime(t.hours, t.minutes, formats) : (t: { hours: number; minutes: number }) => `${pad(t.hours)}:${pad(t.minutes)}`),
    [formats],
  );
  const readTime = useMemo(() => (formats ? (s: string) => parseUserTime(s, formats) : parseTime), [formats]);
  const shownTime = useMemo(() => {
    const stored = parseTime(time);
    return stored ? showTime(stored) : '';
  }, [time, showTime]);
  // A User Local date and time names its zone; a date or a time zone independent value has none (#131).
  const hint = attr.dateOnly || !isUserLocal(attr) ? undefined : zone && zone !== 'Etc/UTC' ? t('field.date.zone', { zone: zoneLabel(zone, locale) }) : t('field.date.utc');

  // The time box's text: the stored time, or what the user typed or picked before there is a date. A new value from
  // outside (a script) brings the box back to the stored time; a Refresh mounts the field again.
  const [timeText, setTimeText] = useState(shownTime);
  useEffect(() => setTimeText(shownTime), [shownTime]);
  // What the user typed and could not be read: shown on the field until the next valid input (the value is unchanged).
  const [dateError, setDateError] = useState<string>();
  const [timeError, setTimeError] = useState<string>();
  const parsedTime = readTime(timeText);
  const selectedTime = useMemo(() => {
    const parsed = readTime(timeText);
    return parsed ? atTime(parsed) : null;
  }, [timeText, readTime]);

  const emit = (next: string) => {
    if (next !== text) onChange(next);
  };

  const onSelectDate = (picked: Date | null | undefined) => {
    if (!picked) return emit('');
    const iso = formatIsoDate(picked);
    emit(attr.dateOnly ? iso : `${iso}T${parsedTime ? `${pad(parsedTime.hours)}:${pad(parsedTime.minutes)}` : '00:00'}`);
  };

  const onTimeChange: TimeChange = (_, data) => {
    if (data.errorType) {
      setTimeText(shownTime); // not a time: back to the stored one
      setTimeError(formats ? t('field.date.invalidTimeAs', { pattern: patternText(formats.timeFormat, formats) }) : t('field.date.invalidTime'));
      return;
    }
    setTimeError(undefined);
    // An empty time box with a date is midnight, which is what is stored.
    const next = data.selectedTime ? formatTime(data.selectedTime) : datePart ? '00:00' : '';
    const nextParts = parseTime(next);
    setTimeText(nextParts ? showTime(nextParts) : '');
    if (datePart) emit(`${datePart}T${next || '00:00'}`);
  };

  const parseTimeText: TimeParse = (input) => {
    if (!input?.trim()) return { date: null };
    const parsed = readTime(input);
    return parsed ? { date: atTime(parsed) } : { date: null, errorType: 'invalid-input' };
  };

  // `control` carries the field's control id (so the label's `for` finds the input), label, hint and required mark.
  const picker = (control: FieldControlProps) => (
    <DatePicker
      {...control}
      appearance={appearance}
      className={attr.dateOnly ? undefined : styles.date}
      input={{ className: styles.input }}
      value={date}
      allowTextInput
      formatDate={showDate}
      parseDateFromString={readDate}
      placeholder={formats ? patternText(formats.dateFormat, formats) : 'YYYY-MM-DD'}
      strings={strings}
      firstDayOfWeek={(locale === 'it-IT' ? 1 : 0) as DayOfWeek}
      disabled={disabled}
      // The calendar icon gets its own name (Fluent labels it with the input, so two controls would be named after the
      // column); a disabled field's icon does nothing (the picker checks `disabled`), which assistive technology is told.
      contentAfter={{ 'aria-labelledby': undefined, 'aria-label': t('field.date.openCalendar'), 'aria-disabled': disabled || undefined }}
      onSelectDate={onSelectDate}
      // A required empty date is the form's to report; only text that is not a date is reported here.
      onValidationResult={(data) =>
        setDateError(
          data.error !== 'invalid-input' ? undefined : formats ? t('field.date.invalidDateAs', { pattern: patternText(formats.dateFormat, formats) }) : t('field.date.invalidDate'),
        )
      }
    />
  );

  return (
    <Field label={attr.displayName} required={attr.required} validationMessage={error ?? dateError ?? timeError} hint={hint}>
      {(control: FieldControlProps) =>
        attr.dateOnly ? (
          picker(control)
        ) : (
          <div className={styles.row}>
            {picker(control)}
            {/* The time box has its own name; the field's label, hint and required mark belong to the date. */}
            <FieldContextProvider value={undefined}>
              <TimePicker
                appearance={appearance}
                className={styles.time}
                input={{ className: styles.input }}
                aria-label={t('field.date.time', { name: attr.displayName })}
                aria-invalid={error || timeError ? true : undefined}
                freeform
                increment={30}
                hourCycle={formats && usesTwelveHours(formats) ? 'h12' : 'h23'}
                placeholder={formats ? patternText(formats.timeFormat, formats) : 'HH:mm'}
                dateAnchor={TIME_ANCHOR}
                selectedTime={selectedTime}
                value={timeText}
                onInput={(e) => setTimeText((e.target as HTMLInputElement).value)}
                onTimeChange={onTimeChange}
                formatDateToTimeString={(d) => showTime({ hours: d.getHours(), minutes: d.getMinutes() })}
                parseTimeStringToDate={parseTimeText}
                disabled={disabled}
              />
            </FieldContextProvider>
          </div>
        )
      }
    </Field>
  );
}
