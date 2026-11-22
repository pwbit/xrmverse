import { Field, Select, Switch } from '@fluentui/react-components';
import type { CalendarLayout, ViewMode, ViewPresentation } from '../../parsers/presentationJson';
import { t } from '../../shell/i18n/t';
import type { AttributeMeta } from '../tables/metadata';

const shown = (a: AttributeMeta) => a.validForRead && !a.hidden && !a.attributeOf;
const byName = (a: AttributeMeta, b: AttributeMeta) => a.displayName.localeCompare(b.displayName);

/** The columns a board groups by (UI-2b B1): choice, status, state and two-option columns. */
export const boardColumns = (attributes: AttributeMeta[]) => attributes.filter((a) => shown(a) && (a.kind === 'choice' || a.kind === 'bool')).sort(byName);
/** The date columns a calendar uses. */
export const dateColumns = (attributes: AttributeMeta[]) => attributes.filter((a) => shown(a) && a.kind === 'datetime').sort(byName);
/** The date behaviour a calendar's start and end must share. */
const behaviourOf = (a: AttributeMeta | undefined) => a?.behavior ?? 'UserLocal';

export interface ModeFieldsProps {
  attributes: AttributeMeta[];
  value: ViewPresentation;
  onChange(next: ViewPresentation): void;
  /** Prefix for the controls' ids, to keep them unique when the fields appear more than once on a page. */
  idPrefix?: string;
}

/**
 * The board, calendar, cards and default-mode fields of a presentation entry (used by the view designer and, for
 * subgrids, the form designer). A default on a mode that is now off falls back to the grid.
 */
export function ModeFields({ attributes, value: modes, onChange, idPrefix }: ModeFieldsProps) {
  // Only set when asked for: an explicit undefined id would override the one the Field gives its control.
  const id = (name: string) => (idPrefix ? { id: `${idPrefix}-${name}` } : {});
  const update = (next: ViewPresentation) => {
    if (next.default && next.default !== 'grid' && !next[next.default]) next = { ...next, default: undefined };
    onChange(next);
  };
  const board = boardColumns(attributes);
  const dates = dateColumns(attributes);
  const start = dates.find((a) => a.logicalName === modes.calendar?.start);
  const ends = dates.filter((a) => a.logicalName !== modes.calendar?.start && behaviourOf(a) === behaviourOf(start));
  const defaults: [ViewMode, string][] = [
    ['grid', t('views.modes.grid')],
    ...(modes.kanban ? [['kanban', t('views.modes.kanban')] as [ViewMode, string]] : []),
    ...(modes.calendar ? [['calendar', t('views.modes.calendar')] as [ViewMode, string]] : []),
    ...(modes.cards ? [['cards', t('views.modes.cards')] as [ViewMode, string]] : []),
  ];
  /** A value the overlay names that is not among the offered columns still shows, by its logical name. */
  const withCurrent = (list: AttributeMeta[], current: string | undefined) =>
    current && !list.some((a) => a.logicalName === current) ? [...list.map((a) => [a.logicalName, a.displayName]), [current, current]] : list.map((a) => [a.logicalName, a.displayName]);

  return (
    <>
      <Field label={t('views.modes.board')}>
        <Select
          {...id('board')}
          value={modes.kanban?.groupBy ?? ''}
          onChange={(_, d) => update({ ...modes, kanban: d.value ? { groupBy: d.value } : undefined })}
        >
          <option value="">{t('views.modes.off')}</option>
          {withCurrent(board, modes.kanban?.groupBy).map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </Select>
      </Field>
      <Field label={t('views.modes.calendarStart')}>
        <Select
          {...id('calendarStart')}
          value={modes.calendar?.start ?? ''}
          onChange={(_, d) => {
            if (!d.value) return update({ ...modes, calendar: undefined });
            const startAttr = dates.find((a) => a.logicalName === d.value);
            const end = dates.find((a) => a.logicalName === modes.calendar?.end);
            const keep = end && end.logicalName !== d.value && behaviourOf(end) === behaviourOf(startAttr);
            const layout = modes.calendar?.layout ? { layout: modes.calendar.layout } : {};
            update({ ...modes, calendar: keep ? { start: d.value, end: end.logicalName, ...layout } : { start: d.value, ...layout } });
          }}
        >
          <option value="">{t('views.modes.off')}</option>
          {withCurrent(dates, modes.calendar?.start).map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </Select>
      </Field>
      {modes.calendar && (
        <Field label={t('views.modes.calendarEnd')}>
          <Select
            {...id('calendarEnd')}
            value={modes.calendar.end ?? ''}
            onChange={(_, d) => {
              const { end: _end, ...rest } = modes.calendar!;
              update({ ...modes, calendar: d.value ? { ...rest, end: d.value } : rest });
            }}
          >
            <option value="">{t('views.modes.none')}</option>
            {withCurrent(ends, modes.calendar.end).map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </Select>
        </Field>
      )}
      {modes.calendar && (
        <Field label={t('views.modes.calendarLayout')}>
          <Select
            {...id('calendarLayout')}
            value={modes.calendar.layout ?? 'month'}
            onChange={(_, d) => {
              const { layout: _layout, ...rest } = modes.calendar!;
              // 'month' is the default: it is written as absent.
              update({ ...modes, calendar: d.value === 'month' ? rest : { ...rest, layout: d.value as CalendarLayout } });
            }}
          >
            <option value="month">{t('views.modes.layout.month')}</option>
            <option value="timeline">{t('views.modes.layout.timeline')}</option>
          </Select>
        </Field>
      )}
      <Switch
        {...id('cards')}
        label={t('views.modes.cards')}
        checked={!!modes.cards}
        onChange={(_, d) => update({ ...modes, cards: d.checked ? {} : undefined })}
      />
      <Field label={t('views.modes.default')}>
        <Select
          {...id('default')}
          value={modes.default && defaults.some(([m]) => m === modes.default) ? modes.default : 'grid'}
          onChange={(_, d) => update({ ...modes, default: d.value === 'grid' ? undefined : (d.value as ViewMode) })}
        >
          {defaults.map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </Select>
      </Field>
    </>
  );
}
