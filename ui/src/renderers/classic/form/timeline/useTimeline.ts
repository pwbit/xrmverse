import { useCallback, useEffect, useRef, useState } from 'react';
import type { EntityMeta } from '../../../../admin/tables/metadata';
import { loadEntities, loadEntity } from '../../../../admin/tables/tableApi';
import { defaultShownTypes, EMPTY_FILTERS, type TimelineFilters, type TimelineScope } from '../../../../apps/record/timeline/filters';
import { completeState } from '../../../../apps/record/timeline/activityActions';
import { loadCounts, loadOverdue, loadPage, type TimelineCounts, type TimelineCursor, type TimelineItem } from '../../../../apps/record/timeline/timelineQuery';
import { heldPrivileges, holds } from '../../../../apps/record/privileges';
import { useLoadedUserSettings } from '../../../../apps/userSettings';
import { wallClockOf } from '../../../../apps/timeZones';
import type { TimelineControl } from '../../../../model/form';
import type { XrmApi } from '../../../../shell/api/client';
import { useAuth } from '../../../../shell/auth/AuthProvider';
import { cachedRead, type ControlEnv } from '../controlEnv';
import { readRemembered, writeRemembered } from './rememberedFilters';

const PAGE_SIZE = 10;
const DAY = 86_400_000;

const NO_TABLES: EntityMeta[] = [];

const errorText = (e: unknown) => (e instanceof Error ? e.message : String(e));

/** Every table without attributes, shared by the controls of a form. */
const tablesOf = (env: ControlEnv, api: XrmApi) => cachedRead(env, 'entities', () => loadEntities(api));

/** The activity tables of the organization (`IsActivity`), without `activitypointer` itself. */
export const activityTablesOf = (tables: EntityMeta[]): EntityMeta[] => tables.filter((e) => e.isActivity === true && e.logicalName !== 'activitypointer');

/** Which items show their body: all but the toggled ones when `all`, else only the toggled ones. */
export interface TimelineExpansion {
  all: boolean;
  toggled: ReadonlySet<string>;
}

export const isExpanded = (e: TimelineExpansion, id: string): boolean => e.all !== e.toggled.has(id);

/** What the timeline can show before it reads anything: a new record or a table without activities or notes cannot have one. */
export type TimelineAvailability = 'ready' | 'newRecord' | 'noActivitiesOrNotes';

interface Setup {
  key: string;
  scope?: TimelineScope;
  /** Activity table display names by logical name. */
  typeNames?: Record<string, string>;
  /** Every activity table of the organization (the + menu offers those the user may create). */
  activityTables?: EntityMeta[];
  /** The user's privilege names; absent when they could not be read (no create menu and no quick actions then). */
  held?: ReadonlySet<string>;
  /** Where the filters are remembered (user, form, control), when the app turns the option on. */
  remember?: [string, string, string];
  error?: string;
}

interface Data {
  key: string;
  items: TimelineItem[];
  cursor?: TimelineCursor;
  overdue: TimelineItem[];
  overdueMore: boolean;
  counts?: TimelineCounts;
  /** The instant the first page was read at: Overdue, groups and the continuation are relative to it. */
  now: Date;
  error?: string;
}

/**
 * The timeline's state: the scope (record, user, shown activity types), the filters, the pages read so far, the overdue
 * items (with groups on) and the counts. Changing the filters, `reload`, the form's refresh or `refresh()` reads page 1 again.
 * With the app's remembered filters on (spec §5.8), the filters start as the user last left them on this form.
 */
export function useTimeline(env: ControlEnv, control: TimelineControl, reload = 0) {
  const { api } = useAuth();
  const settings = useLoadedUserSettings();
  const recordId = env.ctx.fieldContext.record?.id;
  const table = env.ctx.entity;
  const hasActivities = table.hasActivities === true;
  const hasNotes = table.hasNotes === true;
  const groupByDate = env.timeline?.groupByDate === true;
  const postsOn = env.timeline?.posts === true;
  const formId = env.timeline?.rememberFilters === true ? env.formId : undefined;
  const availability: TimelineAvailability = !recordId ? 'newRecord' : !hasActivities && !hasNotes ? 'noActivitiesOrNotes' : 'ready';

  const [filters, setFilters] = useState<TimelineFilters>(EMPTY_FILTERS);
  const [expanded, setExpanded] = useState<TimelineExpansion>({ all: false, toggled: new Set() });
  const [tick, setTick] = useState(0);
  const [moreLoading, setMoreLoading] = useState(false);

  // The scope: the user and the activity tables that exist (the server refuses an unknown activitytypecode).
  const setupKey = [availability, control.id, recordId, table.logicalName, hasActivities, hasNotes, postsOn, settings?.userId, formId, env.refreshKey ?? 0].join('|');
  const [setup, setSetup] = useState<Setup>({ key: '' });
  // The remembered filters are read once per user, form and control, not again on a refresh.
  const restoredFor = useRef('');
  useEffect(() => {
    if (!api || availability !== 'ready' || !recordId) return;
    let cancelled = false;
    (async () => {
      const userId = settings?.userId ?? (await cachedRead(env, 'whoami', () => api.get<{ UserId: string }>('WhoAmI'))).UserId;
      const [activityTables, held] = await Promise.all([
        hasActivities ? tablesOf(env, api).then(activityTablesOf) : Promise.resolve([]),
        cachedRead(env, 'privileges', () => heldPrivileges(api)).catch((e: unknown) => {
          console.warn(`Timeline '${control.id}': the user's privileges cannot be read, so it offers no create menu or actions: ${errorText(e)}`);
          return undefined;
        }),
      ]);
      // Notes are left out without prvReadNote (the server answers 403 on annotations). A type the user cannot read is left out (spec §6.1); with no type left, the activities are not queried at all.
      const readable = held ? activityTables.filter((e) => holds(held, 'Read', e)) : activityTables;
      const shownTypes = defaultShownTypes(readable.map((e) => e.logicalName));
      // Posts need the app's option and prvReadPost; unlike notes, privileges that cannot be read leave them out (spec §4.1).
      const hasPosts = postsOn && !!held && holds(held, 'Read', { schemaName: 'Post' });
      const scope: TimelineScope = {
        recordId,
        userId,
        shownTypes,
        hasActivities: hasActivities && shownTypes.length > 0,
        hasNotes: hasNotes && (!held || held.has('prvReadNote')),
        hasPosts,
      };
      const remember: [string, string, string] | undefined = formId ? [userId, formId, control.id] : undefined;
      return { scope, activityTables, held, remember, typeNames: Object.fromEntries(activityTables.map((e) => [e.logicalName, e.displayName])) };
    })().then(
      (s) => {
        if (cancelled) return;
        // Set with the scope, so that the first page is already read with them.
        const where = s.remember?.join('|');
        if (s.remember && where !== restoredFor.current) {
          restoredFor.current = where!;
          const remembered = readRemembered(...s.remember);
          // An activity table that is no longer shown (deleted, or no longer readable) is dropped: the server refuses an unknown type.
          const shown = (type: string) => s.scope.shownTypes.includes(type);
          // Without posts, a remembered Posts record type or Posts by is dropped too: no chip or option could clear it.
          if (remembered)
            setFilters({
              ...remembered,
              activityTypes: remembered.activityTypes.filter(shown),
              statusReasons: remembered.statusReasons.filter((r) => shown(r.type)),
              ...(s.scope.hasPosts ? {} : { recordTypes: remembered.recordTypes.filter((k) => k !== 'post'), postsBy: [] }),
            });
        }
        setSetup({ key: setupKey, ...s });
      },
      (e: unknown) => !cancelled && setSetup({ key: setupKey, error: errorText(e) }),
    );
    return () => {
      cancelled = true;
    };
    // setupKey stands for the record, the table, the user and the form's refresh.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, setupKey]);
  const ready = setup.key === setupKey ? setup : undefined;
  const scope = ready?.scope;

  // Every change of the filters is remembered, once the remembered ones were read (with the option off, nothing is).
  const remember = ready?.remember;
  useEffect(() => {
    if (remember && remember.join('|') === restoredFor.current) writeRemembered(...remember, filters);
  }, [remember, filters]);

  // With groups on, the overdue items come from their own query and stay out of the pages (unless the user filters on Overdue).
  const excludeOverdue = groupByDate && !filters.statuses.includes('overdue');
  const dataKey = JSON.stringify([setupKey, filters, excludeOverdue, reload, tick]);
  const [data, setData] = useState<Data>({ key: '', items: [], overdue: [], overdueMore: false, now: new Date() });
  const latest = useRef(dataKey);
  latest.current = dataKey;
  useEffect(() => {
    if (!api || !scope) return;
    let cancelled = false;
    const now = new Date();
    Promise.all([
      loadPage(api, scope, filters, undefined, now, PAGE_SIZE, excludeOverdue),
      excludeOverdue ? loadOverdue(api, scope, filters, now) : Promise.resolve({ items: [], more: false }),
      // Counts that cannot be read leave the chips without numbers; the list is still shown.
      loadCounts(api, scope, filters, now).catch((e: unknown) => {
        if (!cancelled) console.warn(`Timeline '${control.id}': the counts cannot be read: ${errorText(e)}`);
        return undefined;
      }),
    ]).then(
      ([page, overdue, counts]) =>
        !cancelled && setData({ key: dataKey, items: page.items, cursor: page.cursor, overdue: overdue.items, overdueMore: overdue.more, counts, now }),
      (e: unknown) => !cancelled && setData({ key: dataKey, items: [], overdue: [], overdueMore: false, now, error: errorText(e) }),
    );
    return () => {
      cancelled = true;
    };
    // dataKey stands for the scope, the filters and the reload counters.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, scope, dataKey]);
  const current = data.key === dataKey ? data : undefined;

  /** Reads the next page; resolves with how many items it added (0 when none were read). */
  const loadMore = useCallback(async (): Promise<number> => {
    if (!api || !scope || !current?.cursor) return 0;
    const key = dataKey;
    setMoreLoading(true);
    try {
      const page = await loadPage(api, scope, filters, current.cursor, current.now, PAGE_SIZE, excludeOverdue);
      if (latest.current !== key) return 0;
      setData((d) => (d.key === key ? { ...d, items: [...d.items, ...page.items], cursor: page.cursor } : d));
      return page.items.length;
    } catch (e) {
      if (latest.current === key) setData((d) => (d.key === key ? { ...d, error: errorText(e) } : d));
      return 0;
    } finally {
      setMoreLoading(false);
    }
  }, [api, scope, current, filters, excludeOverdue, dataKey]);

  const refresh = useCallback(() => setTick((n) => n + 1), []);

  return {
    availability,
    scope,
    typeNames: ready?.typeNames ?? {},
    activityTables: ready?.activityTables ?? NO_TABLES,
    held: ready?.held,
    items: current?.items ?? [],
    overdue: current?.overdue ?? [],
    overdueMore: current?.overdueMore ?? false,
    counts: current?.counts,
    now: current?.now ?? data.now,
    filters,
    setFilters,
    loadMore,
    hasMore: !!current?.cursor,
    refresh,
    loading: availability === 'ready' && !(ready?.error || current),
    moreLoading,
    error: ready?.error ?? current?.error,
    expanded,
    setExpanded,
    groupByDate,
    showDue: excludeOverdue,
  };
}

/**
 * The status reason labels of the activity tables `types`, read from each table's own `statuscode` options (the same value
 * means different things in different tables), keyed `type|value`. The filter pane calls it, so the tables are read only
 * once the pane is opened, and shared with the form's other controls.
 */
export function useStatusReasonLabels(env: ControlEnv, types: string[]): Record<string, string> {
  const { api } = useAuth();
  const [labels, setLabels] = useState<Record<string, string>>({});
  const key = [...new Set(types)].sort().join(',');
  useEffect(() => {
    if (!api || !key) return;
    let cancelled = false;
    Promise.all(
      key.split(',').map((type) =>
        cachedRead(env, `entity|${type}`, () => loadEntity(api, type)).then(
          (e) => (e.attributes.find((a) => a.logicalName === 'statuscode')?.options ?? []).map((o) => [`${type}|${o.value}`, o.label] as const),
          () => [],
        ),
      ),
    ).then((pairs) => !cancelled && setLabels(Object.fromEntries(pairs.flat())));
    return () => {
      cancelled = true;
    };
    // key stands for the types.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, key]);
  return labels;
}

/**
 * What Complete sets on each activity table of `types` ({@link completeState}), read from the table's own states and shared
 * with the form's other controls like {@link useStatusReasonLabels}. A table whose metadata cannot be read has no Complete.
 */
export function useCompleteStates(env: ControlEnv, types: string[]): Record<string, { state: number; status?: number } | undefined> {
  const { api } = useAuth();
  const [states, setStates] = useState<Record<string, { state: number; status?: number } | undefined>>({});
  const key = [...new Set(types)].sort().join(',');
  useEffect(() => {
    if (!api || !key) return;
    let cancelled = false;
    Promise.all(
      key.split(',').map((type) =>
        cachedRead(env, `entity|${type}`, () => loadEntity(api, type)).then(
          (e) => [type, completeState(e)] as const,
          () => [type, undefined] as const,
        ),
      ),
    ).then((pairs) => !cancelled && setStates(Object.fromEntries(pairs)));
    return () => {
      cancelled = true;
    };
    // key stands for the types.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, key]);
  return states;
}

/** The date groups (spec §5.5), Due first; the others by the item's date in the user's time zone. */
export type DateGroup = 'due' | 'today' | 'yesterday' | 'thisWeek' | 'thisMonth' | 'older';

/** The calendar day of `instant` in `zone`, as a day number, and its year and month. */
function dayIn(instant: Date, zone: string) {
  const w = wallClockOf(instant, zone);
  const day = Math.floor(Date.UTC(w.year, w.month - 1, w.day) / DAY);
  return { day, year: w.year, month: w.month };
}

/** The group of a date `iso` seen at `now` in `zone`: weeks start on Monday; Yesterday wins over the week. */
export function dateGroupOf(iso: string, now: Date, zone: string): Exclude<DateGroup, 'due'> {
  const d = dayIn(new Date(iso), zone);
  const today = dayIn(now, zone);
  if (d.day >= today.day) return 'today';
  if (d.day === today.day - 1) return 'yesterday';
  // Day 0 (1970-01-01) was a Thursday: Monday is 0.
  const weekday = (today.day + 3) % 7;
  if (d.day >= today.day - weekday) return 'thisWeek';
  if (d.year === today.year && d.month === today.month) return 'thisMonth';
  return 'older';
}

/** Whether `iso` falls on the same calendar day as `now` in `zone`. */
export const sameDay = (iso: string, now: Date, zone: string): boolean => dayIn(new Date(iso), zone).day === dayIn(now, zone).day;

/** The status tag of an activity: Closed (state 1 or 2), Overdue (active and `scheduledend` passed) or Active. */
export function activityStatusOf(item: TimelineItem, now: Date): 'active' | 'overdue' | 'closed' {
  if (item.stateCode === 1 || item.stateCode === 2) return 'closed';
  return item.scheduledEnd && Date.parse(item.scheduledEnd) < now.getTime() ? 'overdue' : 'active';
}

/** Whole days an overdue activity is past its `scheduledend`. */
export const overdueDays = (item: TimelineItem, now: Date): number => (item.scheduledEnd ? Math.floor((now.getTime() - Date.parse(item.scheduledEnd)) / DAY) : 0);
