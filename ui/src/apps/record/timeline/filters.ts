/**
 * The timeline's filter state (search, chips and filter pane are one state) and the query conditions it stands for, written
 * as an OData `$filter` for the pages and counts and as FetchXML for the aggregate counts, from one condition tree so that
 * both say the same thing. Within a category the options are OR, across categories AND; a filter that only an activity can
 * match hides the notes and the posts, and one that only a post can match (Posts by) hides the activities and the notes.
 */

export type RecordKind = 'activity' | 'note' | 'post';
/** Posts by: `auto` is `source` 1 (Auto Post), `users` is `source` 2 (Manual Post); an ActionHub post (3) is neither. */
export type PostsBy = 'auto' | 'users';
export type ActivityStatus = 'active' | 'overdue' | 'closed';
export type DueRange = 'next30d' | 'next7d' | 'next24h' | 'last24h' | 'last7d' | 'last30d';
export type ModifiedRange = 'last24h' | 'last7d' | 'last30d';

export interface TimelineFilters {
  /** Empty: all. */
  recordTypes: RecordKind[];
  /** `activitytypecode` logical names; empty: every shown type. */
  activityTypes: string[];
  statuses: ActivityStatus[];
  statusReasons: { type: string; value: number }[];
  due: DueRange[];
  modified: ModifiedRange[];
  mine: boolean;
  /** Empty: every source. */
  postsBy: PostsBy[];
  search: string;
  sort: 'desc' | 'asc';
}

export const EMPTY_FILTERS: TimelineFilters = {
  recordTypes: [],
  activityTypes: [],
  statuses: [],
  statusReasons: [],
  due: [],
  modified: [],
  mine: false,
  postsBy: [],
  search: '',
  sort: 'desc',
};

/** The record the timeline belongs to, the signed-in user and what its table can have. */
export interface TimelineScope {
  recordId: string;
  userId: string;
  /** The activity types the timeline shows (fax and letter left out by default, see {@link defaultShownTypes}). */
  shownTypes: string[];
  hasActivities: boolean;
  hasNotes: boolean;
  /** The app turns posts on (`xrmverse_TimelinePosts`) and the user holds `prvReadPost`. */
  hasPosts: boolean;
}

export type ChipId = 'all' | 'activities' | 'notes' | 'posts' | 'overdue' | 'mine';

const HIDDEN_BY_DEFAULT = new Set(['fax', 'letter']);

/** The activity tables the timeline shows by default: all of them but fax and letter. */
export function defaultShownTypes(activityTables: string[]): string[] {
  return activityTables.filter((t) => !HIDDEN_BY_DEFAULT.has(t));
}

/** Whether a chip is on: each chip reads the pane filter it is a shortcut for (All: no record type chosen). */
export function chipSelected(f: TimelineFilters, chip: ChipId): boolean {
  switch (chip) {
    case 'all':
      return f.recordTypes.length === 0;
    case 'activities':
      return f.recordTypes.includes('activity');
    case 'notes':
      return f.recordTypes.includes('note');
    case 'posts':
      return f.recordTypes.includes('post');
    case 'overdue':
      return f.statuses.includes('overdue');
    case 'mine':
      return f.mine;
  }
}

const toggle = <T>(list: T[], value: T): T[] => (list.includes(value) ? list.filter((v) => v !== value) : [...list, value]);

/** Turns a chip on or off by changing the pane filter it stands for; All clears the record type. */
export function toggleChip(f: TimelineFilters, chip: ChipId): TimelineFilters {
  switch (chip) {
    case 'all':
      return { ...f, recordTypes: [] };
    case 'activities':
      return { ...f, recordTypes: toggle(f.recordTypes, 'activity') };
    case 'notes':
      return { ...f, recordTypes: toggle(f.recordTypes, 'note') };
    case 'posts':
      return { ...f, recordTypes: toggle(f.recordTypes, 'post') };
    case 'overdue':
      return { ...f, statuses: toggle(f.statuses, 'overdue') };
    case 'mine':
      return { ...f, mine: !f.mine };
  }
}

/** A filter only an activity can match (type, status, status reason, due date) is set: notes and posts are hidden. */
export function activityOnlyFilterSet(f: TimelineFilters): boolean {
  return f.activityTypes.length > 0 || f.statuses.length > 0 || f.statusReasons.length > 0 || f.due.length > 0;
}

/** A filter only a post can match (Posts by) is set: activities and notes are hidden. */
export function postOnlyFilterSet(f: TimelineFilters): boolean {
  return f.postsBy.length > 0;
}

const includes = (f: TimelineFilters, kind: RecordKind) => f.recordTypes.length === 0 || f.recordTypes.includes(kind);

/** Whether the activities are queried at all: the table has activities, the record type does not leave them out and no post-only filter is set. */
export function queriesActivities(f: TimelineFilters, scope: TimelineScope): boolean {
  return scope.hasActivities && includes(f, 'activity') && !postOnlyFilterSet(f);
}

// ---- condition tree ---------------------------------------------------------------------------------------------

type Value = string | number | null;

/** A condition: `attribute` is the logical name; `lookup` writes it as `_x_value` in OData. */
export type Cond =
  | { kind: 'cmp'; attribute: string; op: 'eq' | 'lt' | 'le' | 'ge'; value: Value; lookup?: boolean; quoted?: boolean }
  | { kind: 'contains'; attribute: string; text: string }
  | { kind: 'and' | 'or'; items: Cond[] };

const cmp = (attribute: string, op: 'eq' | 'lt' | 'le' | 'ge', value: Value, extra?: { lookup?: boolean; quoted?: boolean }): Cond => ({
  kind: 'cmp',
  attribute,
  op,
  value,
  ...extra,
});
const text = (attribute: string, value: string) => cmp(attribute, 'eq', value, { quoted: true });

/** AND of the items, nested ANDs flattened; one item is itself. */
export function and(...items: Cond[]): Cond {
  const flat = items.flatMap((c) => (c.kind === 'and' ? c.items : [c]));
  return flat.length === 1 ? flat[0]! : { kind: 'and', items: flat };
}

/** OR of the items; one item is itself. */
export function or(...items: Cond[]): Cond {
  return items.length === 1 ? items[0]! : { kind: 'or', items };
}

const iso = (d: Date) => d.toISOString();
const HOUR = 3_600_000;
const DAY = 24 * HOUR;

export const ACTIVE: Cond = or(cmp('statecode', 'eq', 0), cmp('statecode', 'eq', 3));
const CLOSED: Cond = or(cmp('statecode', 'eq', 1), cmp('statecode', 'eq', 2));

/** Active and `scheduledend` before `now`. */
export function overdue(now: Date): Cond {
  return and(ACTIVE, cmp('scheduledend', 'lt', iso(now)));
}

/** Not overdue: closed, or without a due date, or due from `now` on. */
export function notOverdue(now: Date): Cond {
  return or(cmp('statecode', 'eq', 1), cmp('statecode', 'eq', 2), cmp('scheduledend', 'eq', null), cmp('scheduledend', 'ge', iso(now)));
}

function status(s: ActivityStatus, now: Date): Cond {
  return s === 'active' ? ACTIVE : s === 'closed' ? CLOSED : overdue(now);
}

const DUE: Record<DueRange, { from: number; to: number }> = {
  next30d: { from: 0, to: 30 * DAY },
  next7d: { from: 0, to: 7 * DAY },
  next24h: { from: 0, to: 24 * HOUR },
  last24h: { from: -24 * HOUR, to: 0 },
  last7d: { from: -7 * DAY, to: 0 },
  last30d: { from: -30 * DAY, to: 0 },
};

function due(range: DueRange, now: Date): Cond {
  const { from, to } = DUE[range];
  const start = cmp('scheduledend', 'ge', iso(new Date(now.getTime() + from)));
  return to === 0
    ? and(start, cmp('scheduledend', 'lt', iso(now)))
    : and(start, cmp('scheduledend', 'le', iso(new Date(now.getTime() + to))));
}

const MODIFIED: Record<ModifiedRange, number> = { last24h: 24 * HOUR, last7d: 7 * DAY, last30d: 30 * DAY };

/** The search on `attributes` (OR), none when the search is blank. */
function search(f: TimelineFilters, ...attributes: string[]): Cond[] {
  const term = f.search.trim();
  return term ? [or(...attributes.map((attribute): Cond => ({ kind: 'contains', attribute, text: term })))] : [];
}

/** Modified date, owner and search: the categories that apply to every record type (a post: created on, created by, its text). */
function shared(f: TimelineFilters, scope: TimelineScope, now: Date, columns: { date: string; owner: string; search: string[] }, alsoMine: Cond[] = []): Cond[] {
  return [
    ...(f.modified.length ? [or(...f.modified.map((m) => cmp(columns.date, 'ge', iso(new Date(now.getTime() - MODIFIED[m])))))] : []),
    ...(f.mine ? [or(cmp(columns.owner, 'eq', scope.userId, { lookup: true }), ...alsoMine)] : []),
    ...search(f, ...columns.search),
  ];
}

const ACTIVITY_COLUMNS = { date: 'modifiedon', owner: 'ownerid', search: ['subject', 'description'] };
const NOTE_COLUMNS = { date: 'modifiedon', owner: 'ownerid', search: ['subject', 'notetext'] };
// A post cannot be updated: its creation is its last change. Mine is its creator (posts are organization-owned).
const POST_COLUMNS = { date: 'createdon', owner: 'createdby', search: ['text'] };
const POST_SOURCE: Record<PostsBy, number> = { auto: 1, users: 2 };

/** The activity conditions of `f`: regarding the record, of the chosen (else shown) types, and every other category. */
export function activityConditions(f: TimelineFilters, scope: TimelineScope, now: Date): Cond {
  const types = f.activityTypes.length ? f.activityTypes : scope.shownTypes;
  return and(
    cmp('regardingobjectid', 'eq', scope.recordId, { lookup: true }),
    ...(types.length ? [or(...types.map((t) => text('activitytypecode', t)))] : []),
    ...(f.statuses.length ? [or(...f.statuses.map((s) => status(s, now)))] : []),
    ...(f.statusReasons.length
      ? [or(...f.statusReasons.map((r) => and(text('activitytypecode', r.type), cmp('statuscode', 'eq', r.value))))]
      : []),
    ...(f.due.length ? [and(ACTIVE, or(...f.due.map((d) => due(d, now))))] : []),
    ...shared(f, scope, now, ACTIVITY_COLUMNS),
  );
}

/** The note conditions of `f`, or undefined when no note can match (see {@link noteFilter}). */
export function noteConditions(f: TimelineFilters, scope: TimelineScope, now: Date): Cond | undefined {
  if (!scope.hasNotes || !includes(f, 'note') || activityOnlyFilterSet(f) || postOnlyFilterSet(f)) return undefined;
  return and(cmp('objectid', 'eq', scope.recordId, { lookup: true }), ...shared(f, scope, now, NOTE_COLUMNS));
}

/**
 * The post conditions of `f`, or undefined when no post can match: posts off, another record type, or an activity-only filter.
 * Mine matches the posts the user created and `replied`, the posts of the record the user commented on (spec §4.2).
 */
export function postConditions(f: TimelineFilters, scope: TimelineScope, now: Date, replied: readonly string[] = []): Cond | undefined {
  if (!scope.hasPosts || !includes(f, 'post') || activityOnlyFilterSet(f)) return undefined;
  return and(
    cmp('regardingobjectid', 'eq', scope.recordId, { lookup: true }),
    ...(f.postsBy.length ? [or(...f.postsBy.map((p) => cmp('source', 'eq', POST_SOURCE[p])))] : []),
    ...shared(f, scope, now, POST_COLUMNS, replied.map((id) => cmp('postid', 'eq', id))),
  );
}

/** The OData `$filter` of the activities (`activitypointers`); not sent when {@link queriesActivities} is false. */
export function activityFilter(f: TimelineFilters, scope: TimelineScope, now: Date): string {
  return toOData(activityConditions(f, scope, now));
}

/** The OData `$filter` of the notes (`annotations`); undefined: do not query notes. */
export function noteFilter(f: TimelineFilters, scope: TimelineScope, now: Date): string | undefined {
  const c = noteConditions(f, scope, now);
  return c && toOData(c);
}

/** The OData `$filter` of the posts (`posts`); undefined: do not query posts. */
export function postFilter(f: TimelineFilters, scope: TimelineScope, now: Date, replied: readonly string[] = []): string | undefined {
  const c = postConditions(f, scope, now, replied);
  return c && toOData(c);
}

// ---- writers ----------------------------------------------------------------------------------------------------

const odataValue = (c: Extract<Cond, { kind: 'cmp' }>) =>
  c.value === null ? 'null' : c.quoted ? `'${String(c.value).replace(/'/g, "''")}'` : String(c.value);

/** The condition as an OData `$filter`: the top level unparenthesized, each nested AND or OR in parentheses. */
export function toOData(c: Cond, nested = false): string {
  switch (c.kind) {
    case 'cmp':
      return `${c.lookup ? `_${c.attribute}_value` : c.attribute} ${c.op} ${odataValue(c)}`;
    case 'contains':
      return `contains(${c.attribute},'${c.text.replace(/'/g, "''")}')`;
    default: {
      const body = c.items.map((i) => toOData(i, true)).join(` ${c.kind} `);
      return nested ? `(${body})` : body;
    }
  }
}

const xmlAttr = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
/** LIKE wildcards matched literally, as the server's `contains` does. */
const likeEscape = (s: string) => s.replace(/[[%_]/g, (ch) => `[${ch}]`);

/** The condition as a FetchXML `<filter>` (or a single `<condition>` inside an AND filter). */
export function toFetchFilter(c: Cond): string {
  switch (c.kind) {
    case 'cmp':
      return c.value === null
        ? `<condition attribute="${c.attribute}" operator="null"/>`
        : `<condition attribute="${c.attribute}" operator="${c.op}" value="${xmlAttr(String(c.value))}"/>`;
    case 'contains':
      return `<condition attribute="${c.attribute}" operator="like" value="${xmlAttr(`%${likeEscape(c.text)}%`)}"/>`;
    default:
      return `<filter type="${c.kind}">${c.items.map(toFetchFilter).join('')}</filter>`;
  }
}
