import type { CalendarLayout, ViewMode, ViewPresentation } from '../../parsers/presentationJson';
import type { ColumnKind } from './columnKinds';

/** The most records the board and the calendar read (one request). */
export const BOARD_LIMIT = 250;

export interface ResolvedModes {
  /** Always starts with 'grid'; then kanban, calendar, cards in this order when usable. */
  modes: ViewMode[];
  /** The overlay default when usable, else 'grid'. */
  initial: ViewMode;
  kanban?: { groupBy: string; column: Extract<ColumnKind, { kind: 'choice' }> };
  calendar?: {
    start: string;
    end?: string;
    dateOnly: boolean;
    /** Stored wall clock (TimeZoneIndependent): the day does not depend on the user's zone. */
    zoneIndependent: boolean;
    /** The overlay layout, 'month' when absent. */
    layout: CalendarLayout;
  };
}

/** The choice types a board can group by; a multi-select value belongs to several lanes, so it is refused. */
const KANBAN_TYPES: readonly string[] = ['Picklist', 'Status', 'State', 'Boolean'];

const warnPresentation = (message: string) => console.warn('[xrmverse presentation]', message);

/** The column names the overlay of a view refers to (kanban groupBy, calendar start and end). */
export function overlayColumns(view: ViewPresentation | undefined): string[] {
  if (!view) return [];
  const names = [view.kanban?.groupBy, view.calendar?.start, view.calendar?.end].filter((n): n is string => !!n);
  return [...new Set(names)];
}

/** Drops (with a console warning, unless `kindsFailed`) a kanban whose groupBy is not a choice kind (B1) and a calendar whose start/end is not a date. */
export function resolveModes(view: ViewPresentation | undefined, kinds: Record<string, ColumnKind>, kindsFailed = false): ResolvedModes {
  // When the kinds could not be read at all, every column looks missing: the modes are dropped without a warning per mode.
  const warn = kindsFailed ? () => {} : warnPresentation;
  const resolved: ResolvedModes = { modes: ['grid'], initial: 'grid' };
  if (!view) return resolved;

  if (view.kanban) {
    const { groupBy } = view.kanban;
    const column = kinds[groupBy];
    if (column?.kind === 'choice' && KANBAN_TYPES.includes(column.type)) {
      resolved.kanban = { groupBy, column };
      resolved.modes.push('kanban');
    } else {
      warn(`kanban.groupBy '${groupBy}' is not a choice column of the view's table; the board is not offered.`);
    }
  }

  if (view.calendar) {
    const { start, end } = view.calendar;
    const layout = view.calendar.layout ?? 'month';
    const startKind = kinds[start];
    if (startKind?.kind === 'date') {
      const { dateOnly, zoneIndependent } = startKind;
      resolved.calendar = { start, dateOnly, zoneIndependent, layout };
      if (end !== undefined) {
        const endKind = kinds[end];
        if (endKind?.kind !== 'date') warn(`calendar.end '${end}' is not a date column of the view's table; the calendar uses the start only.`);
        else if (endKind.dateOnly !== dateOnly || endKind.zoneIndependent !== zoneIndependent)
          warn(`calendar.end '${end}' has another date behaviour than calendar.start '${start}'; the calendar uses the start only.`);
        else resolved.calendar = { start, end, dateOnly, zoneIndependent, layout };
      }
      resolved.modes.push('calendar');
    } else {
      warn(`calendar.start '${start}' is not a date column of the view's table; the calendar is not offered.`);
    }
  }

  if (view.cards) resolved.modes.push('cards');

  if (view.default && resolved.modes.includes(view.default)) resolved.initial = view.default;
  return resolved;
}
