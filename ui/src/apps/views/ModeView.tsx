import { makeStyles, MessageBar, MessageBarBody, Spinner, ToggleButton } from '@fluentui/react-components';
import { useEffect, useMemo, useState } from 'react';
import { withNullCondition, withPaging, withTotalCount } from '../../parsers/fetchXml';
import { runView } from '../runView';
import type { ViewColumn } from '../../model/view';
import type { CalendarLayout } from '../../parsers/presentationJson';
import type { XrmApi } from '../../shell/api/client';
import { t } from '../../shell/i18n/t';
import { CalendarMonth } from './CalendarMonth';
import { CalendarTimeline } from './CalendarTimeline';
import { gridRange, monthGrid } from './calendarGrid';
import { KanbanBoard } from './KanbanBoard';
import { useModeRecords } from './useModeRecords';
import type { ResolvedModes } from './viewModes';

export interface ModeViewProps {
  mode: 'kanban' | 'calendar';
  resolved: ResolvedModes;
  api?: XrmApi;
  /** The logical name of the view's table (the board's choice colours). */
  table?: string;
  entitySet?: string;
  /** The view's query with quick find and sort applied, without paging. */
  fetchXml?: string;
  /** The board's view: its name, visible columns and their headers, and the table's primary id attribute. */
  label: string;
  columns: ViewColumn[];
  headers: Record<string, string>;
  primaryId?: string;
  /** Saves a card's move; rejects with the server's error. */
  onMove(id: string, body: Record<string, unknown>): Promise<void>;
  onOpen(id: string): void;
  /** The calendar: the shown month, the user's time zone and language, and the navigation. */
  calendar?: { year: number; month: number; zone: string; lcid: number; onNavigate(year: number, month: number): void };
  /** The notice shown when more records match than are read; absent, the list's (which points to quick find). */
  limitNotice?: string;
}

const useStyles = makeStyles({ toggle: { display: 'flex' } });

/** The board or the calendar of a view: one request of up to BOARD_LIMIT records (no paging). */
export function ModeView({ mode, resolved, api, table, entitySet, fetchXml, label, columns, headers, primaryId, onMove, onOpen, calendar, limitNotice }: ModeViewProps) {
  const attributes =
    mode === 'kanban'
      ? resolved.kanban
        ? // A status moves with its state: the board needs the record's statecode too.
          [resolved.kanban.groupBy, ...(resolved.kanban.groupBy === 'statuscode' ? ['statecode'] : [])]
        : []
      : resolved.calendar ? [resolved.calendar.start, ...(resolved.calendar.end ? [resolved.calendar.end] : [])] : [];
  // The calendar asks only for the records of its 6-week grid (B4); Monday starts the week unless the language is en-US.
  const calYear = calendar?.year;
  const calMonth = calendar?.month;
  // A TimeZoneIndependent column stores a wall clock: its day is the UTC text's day, not the user's.
  const calZone = resolved.calendar?.zoneIndependent ? 'UTC' : calendar?.zone;
  const weekStart: 0 | 1 = (calendar?.lcid ?? 0) === 1033 ? 0 : 1;
  const calStart = resolved.calendar?.start;
  const calEnd = resolved.calendar?.end;
  const calDateOnly = resolved.calendar?.dateOnly;
  const grid = useMemo(
    () => (mode === 'calendar' && calYear !== undefined && calMonth !== undefined ? monthGrid(calYear, calMonth, weekStart) : undefined),
    [mode, calYear, calMonth, weekStart],
  );
  const range = useMemo(() => {
    if (!grid || calZone === undefined || calStart === undefined || calDateOnly === undefined) return undefined;
    return { attribute: calStart, end: calEnd, dateOnly: calDateOnly, ...gridRange(grid, calDateOnly, calZone) };
  }, [grid, calZone, calStart, calEnd, calDateOnly]);
  const records = useModeRecords({ api, entitySet, fetchXml, attributes, range });
  const undated = useUndatedCount(api, entitySet, mode === 'calendar' ? fetchXml : undefined, calStart);
  // The user's layout lasts while the view's calendar stays the same; another view starts on its configured layout.
  const [picked, setPicked] = useState<{ for: ResolvedModes['calendar']; layout: CalendarLayout }>();
  const layout: CalendarLayout = picked && picked.for === resolved.calendar ? picked.layout : (resolved.calendar?.layout ?? 'month');
  const styles = useStyles();
  const Calendar = layout === 'timeline' ? CalendarTimeline : CalendarMonth;
  const toggle = (
    <div role="group" aria-label={t('apps.views.calendar.layoutLabel')} className={styles.toggle}>
      <ToggleButton checked={layout === 'month'} onClick={() => setPicked({ for: resolved.calendar, layout: 'month' })}>
        {t('apps.views.calendar.layout.month')}
      </ToggleButton>
      <ToggleButton checked={layout === 'timeline'} onClick={() => setPicked({ for: resolved.calendar, layout: 'timeline' })}>
        {t('apps.views.calendar.layout.timeline')}
      </ToggleButton>
    </div>
  );

  if (records.status === 'loading') return <Spinner label={t('apps.views.loading')} />;
  if (records.status === 'error') {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{t('apps.views.error', { detail: records.error })}</MessageBarBody>
      </MessageBar>
    );
  }
  return (
    <>
      {records.more && (
        <MessageBar intent="info">
          <MessageBarBody>{limitNotice ?? t('apps.views.modes.limit')}</MessageBarBody>
        </MessageBar>
      )}
      {mode === 'kanban' && resolved.kanban ? (
        <KanbanBoard
          label={label}
          table={table}
          columns={columns}
          headers={headers}
          groupBy={resolved.kanban.groupBy}
          column={resolved.kanban.column}
          rows={records.rows}
          primaryId={primaryId ?? ''}
          onMove={async (id, body) => {
            await onMove(id, body);
            records.reload();
          }}
          onOpen={onOpen}
        />
      ) : mode === 'calendar' && resolved.calendar && calendar && columns[0] ? (
        <Calendar
          toolbarExtra={toggle}
          label={label}
          year={calendar.year}
          month={calendar.month}
          weekStart={weekStart}
          start={resolved.calendar.start}
          end={resolved.calendar.end}
          dateOnly={resolved.calendar.dateOnly}
          zoneIndependent={resolved.calendar.zoneIndependent}
          zone={calendar.zone}
          rows={records.rows}
          primaryId={primaryId ?? ''}
          titleColumn={columns[0]}
          undated={undated}
          onNavigate={calendar.onNavigate}
          onOpen={onOpen}
        />
      ) : null}
    </>
  );
}

/**
 * The number of the view's records (quick find applied) without a start date, from a request of their own: the range request
 * leaves them out. Undefined while unknown or when the request fails (the calendar then shows no note).
 */
function useUndatedCount(api: XrmApi | undefined, entitySet: string | undefined, fetchXml: string | undefined, start: string | undefined): number | undefined {
  const [counted, setCounted] = useState<{ key: string; total?: number }>({ key: '' });
  const request = useMemo(() => {
    if (!fetchXml || !start) return undefined;
    try {
      return withTotalCount(withPaging(withNullCondition(fetchXml, start), 1, 1));
    } catch {
      return undefined;
    }
  }, [fetchXml, start]);
  const key = request && entitySet ? `${entitySet}\n${request}` : '';
  useEffect(() => {
    if (!api || !entitySet || !request) return;
    let cancelled = false;
    const abort = new AbortController();
    runView(api, entitySet, request, 1, undefined, abort.signal).then(
      (page) => !cancelled && setCounted({ key, total: page.total }),
      () => !cancelled && setCounted({ key }),
    );
    return () => {
      cancelled = true;
      abort.abort();
    };
  }, [api, entitySet, request, key]);
  return counted.key === key && key ? counted.total : undefined;
}
