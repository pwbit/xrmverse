import type { XrmApi } from '../../../shell/api/client';
import {
  activityConditions,
  and,
  noteConditions,
  notOverdue,
  overdue,
  postConditions,
  queriesActivities,
  toFetchFilter,
  toOData,
  type Cond,
  type RecordKind,
  type TimelineFilters,
  type TimelineScope,
} from './filters';

/**
 * One row of the timeline: an activity (`table` is its activity table), a note (`table` is `annotation`) or a post (`table`
 * is `post`, `title` its text). `modifiedOn` is the date the timeline sorts and shows: a post's `createdon`.
 */
export interface TimelineItem {
  kind: RecordKind;
  id: string;
  table: string;
  title: string;
  body?: string;
  modifiedOn: string;
  /** A post's `createdon` (the same as its `modifiedOn`). */
  createdOn?: string;
  /** A post's `source`: 1 Auto Post, 2 Manual Post, 3 ActionHub Post. */
  source?: number;
  /** How many comments a post has; absent when they could not be counted. */
  replyCount?: number;
  /** A post's creator. */
  authorId?: string;
  authorName?: string;
  scheduledEnd?: string;
  stateCode?: number;
  statusCode?: number;
  statusLabel?: string;
  ownerName?: string;
  ownerId?: string;
  fileName?: string;
  fileSize?: number;
}

/** The chip and filter pane counts, faceted: each option counted with every other category's selection, not its own. */
export interface TimelineCounts {
  all: number;
  activities: number;
  notes: number;
  posts: number;
  /** Posts by: Auto post (`source` 1) and Users (`source` 2). */
  postsByAuto: number;
  postsByUsers: number;
  overdue: number;
  mine: number;
  byType: Record<string, number>;
  byReason: { type: string; value: number; label: string; count: number }[];
}

const FV = '@OData.Community.Display.V1.FormattedValue';
const ANNOTATIONS = { headers: { Prefer: 'odata.include-annotations="*"' } };
const ACTIVITY_SELECT = 'activityid,activitytypecode,subject,description,modifiedon,scheduledend,statecode,statuscode,_ownerid_value';
const NOTE_SELECT = 'annotationid,subject,notetext,modifiedon,filename,filesize,_ownerid_value';
const POST_SELECT = 'postid,text,source,type,createdon,_createdby_value';
const COMMENT_SELECT = 'postcommentid,text,createdon,_createdby_value';
const MAX_TOP = 5000;
const OVERDUE_LIMIT = 50;

type Row = Record<string, unknown>;

const str = (v: unknown) => (typeof v === 'string' ? v : undefined);
const num = (v: unknown) => (typeof v === 'number' ? v : undefined);
/** The item without its undefined fields. */
const compact = (item: TimelineItem): TimelineItem =>
  Object.fromEntries(Object.entries(item).filter(([, v]) => v !== undefined)) as unknown as TimelineItem;

function activityItem(r: Row): TimelineItem {
  return compact({
    kind: 'activity',
    id: String(r.activityid),
    table: String(r.activitytypecode ?? ''),
    title: str(r.subject) ?? '',
    body: str(r.description),
    modifiedOn: String(r.modifiedon),
    scheduledEnd: str(r.scheduledend),
    stateCode: num(r.statecode),
    statusCode: num(r.statuscode),
    statusLabel: str(r[`statuscode${FV}`]),
    ownerId: str(r._ownerid_value),
    ownerName: str(r[`_ownerid_value${FV}`]),
  });
}

function noteItem(r: Row): TimelineItem {
  return compact({
    kind: 'note',
    id: String(r.annotationid),
    table: 'annotation',
    title: str(r.subject) ?? '',
    body: str(r.notetext),
    modifiedOn: String(r.modifiedon),
    ownerId: str(r._ownerid_value),
    ownerName: str(r[`_ownerid_value${FV}`]),
    fileName: str(r.filename),
    fileSize: num(r.filesize),
  });
}

function postItem(r: Row): TimelineItem {
  const createdOn = String(r.createdon);
  return compact({
    kind: 'post',
    id: String(r.postid),
    table: 'post',
    title: str(r.text) ?? '',
    modifiedOn: createdOn,
    createdOn,
    source: num(r.source),
    authorId: str(r._createdby_value),
    authorName: str(r[`_createdby_value${FV}`]),
  });
}

/** One comment of a post. */
export interface PostComment {
  id: string;
  text: string;
  createdOn: string;
  authorId: string;
  /** The creator's name; empty when the server sent none. */
  authorName: string;
}

/** The comments of post `postId`, oldest first. */
export async function loadComments(api: XrmApi, postId: string): Promise<PostComment[]> {
  const body = await api.get<{ value?: Row[] }>(
    `postcomments?$select=${COMMENT_SELECT}&$filter=${encodeURIComponent(`_postid_value eq ${postId}`)}&$orderby=${encodeURIComponent('createdon asc')}`,
    ANNOTATIONS,
  );
  return (body.value ?? []).map((r) => ({
    id: String(r.postcommentid),
    text: str(r.text) ?? '',
    createdOn: String(r.createdon),
    authorId: str(r._createdby_value) ?? '',
    authorName: str(r[`_createdby_value${FV}`]) ?? '',
  }));
}

const errorText = (e: unknown) => (e instanceof Error ? e.message : String(e));

/** Post ids per reply count request, so that the GET URL stays bounded. */
const REPLY_BATCH = 100;

/**
 * Sets `replyCount` on `posts` from aggregate FetchXML on `postcomment` grouped by `postid`, one request per 100 posts (the
 * server has no `$expand` of a collection-valued navigation property yet). A batch that cannot be counted leaves its posts
 * without `replyCount`: they are still shown.
 */
async function countReplies(api: XrmApi, posts: TimelineItem[]): Promise<void> {
  const batches: TimelineItem[][] = [];
  for (let i = 0; i < posts.length; i += REPLY_BATCH) batches.push(posts.slice(i, i + REPLY_BATCH));
  await Promise.all(
    batches.map(async (batch) => {
      const fetchXml =
        `<fetch aggregate="true"><entity name="postcomment">` +
        `<attribute name="postid" groupby="true" alias="post"/>` +
        `<attribute name="postcommentid" aggregate="count" alias="n"/>` +
        `<filter type="or">${batch.map((p) => toFetchFilter({ kind: 'cmp', attribute: 'postid', op: 'eq', value: p.id })).join('')}</filter>` +
        `</entity></fetch>`;
      let rows: Row[];
      try {
        rows = (await api.get<{ value?: Row[] }>(`postcomments?fetchXml=${encodeURIComponent(fetchXml)}`)).value ?? [];
      } catch (e) {
        console.warn(`Timeline: the post replies cannot be counted: ${errorText(e)}`);
        return;
      }
      const counts = new Map(rows.map((r) => [String(r.post).toLowerCase(), Number(r.n)]));
      for (const p of batch) p.replyCount = counts.get(p.id.toLowerCase()) ?? 0;
    }),
  );
}

/**
 * The posts of the record the user commented on (for Mine, spec §4.2): one aggregate FetchXML on the user's comments, joined
 * to their post regarding the record and grouped by `postid` (the server has no `any` lambda in `$filter`). A read that fails
 * leaves Mine to the posts the user created.
 */
async function repliedPosts(api: XrmApi, scope: TimelineScope): Promise<string[]> {
  const fetchXml =
    `<fetch aggregate="true"><entity name="postcomment">` +
    `<attribute name="postid" groupby="true" alias="post"/>` +
    `<attribute name="postcommentid" aggregate="count" alias="n"/>` +
    `<filter type="and">${toFetchFilter({ kind: 'cmp', attribute: 'createdby', op: 'eq', value: scope.userId })}</filter>` +
    `<link-entity name="post" from="postid" to="postid" link-type="inner">` +
    `<filter type="and">${toFetchFilter({ kind: 'cmp', attribute: 'regardingobjectid', op: 'eq', value: scope.recordId })}</filter>` +
    `</link-entity></entity></fetch>`;
  try {
    const rows = (await api.get<{ value?: Row[] }>(`postcomments?fetchXml=${encodeURIComponent(fetchXml)}`)).value ?? [];
    return rows.map((r) => str(r.post)).filter((id): id is string => !!id);
  } catch (e) {
    console.warn(`Timeline: the posts the user replied to cannot be read: ${errorText(e)}`);
    return [];
  }
}

/** Whether Mine on posts needs {@link repliedPosts}: a post query with Mine. */
const needsReplied = (f: TimelineFilters, scope: TimelineScope, now: Date) => f.mine && !!postConditions(f, scope, now);

interface StreamDef {
  set: string;
  idAttribute: string;
  /** The date the stream is ordered and continued by: `modifiedon`, a post's `createdon`. */
  dateAttribute: 'modifiedon' | 'createdon';
  select: string;
  conditions: Cond;
  toItem: (r: Row) => TimelineItem;
  /** Completes the items of one read (a post's reply count). */
  complete?: (api: XrmApi, items: TimelineItem[]) => Promise<void>;
}

/**
 * Where a stream stands: the rows read and not yet shown, whether the server has more, and the continuation. The server
 * answers `modifiedon` (and `createdon`) in whole seconds while it stores more precision, so the stream continues from the second of the
 * last row read (`lt` its end when newest first, `ge` its start when oldest first) and drops the rows of that second it
 * has already read (`seen`).
 */
interface StreamState {
  def: StreamDef;
  buffer: TimelineItem[];
  done: boolean;
  second?: number;
  seen: string[];
}

/** Where "Load more" continues: opaque to the caller, valid for the filters it was read with. */
export class TimelineCursor {
  /** @internal */
  constructor(readonly streams: readonly StreamState[]) {}
}

const secondOf = (iso: string) => Math.floor(Date.parse(iso) / 1000) * 1000;

async function fill(api: XrmApi, s: StreamState, want: number, sort: 'asc' | 'desc'): Promise<void> {
  while (!s.done && s.buffer.length < want) {
    const need = want - s.buffer.length;
    const top = Math.min(MAX_TOP, need + s.seen.length);
    const continuation =
      s.second === undefined
        ? []
        : [
            sort === 'desc'
              ? ({ kind: 'cmp', attribute: s.def.dateAttribute, op: 'lt', value: new Date(s.second + 1000).toISOString() } as const)
              : ({ kind: 'cmp', attribute: s.def.dateAttribute, op: 'ge', value: new Date(s.second).toISOString() } as const),
          ];
    const { def } = s;
    const body = await api.get<{ value?: Row[] }>(
      `${def.set}?$select=${def.select}&$filter=${encodeURIComponent(toOData(and(def.conditions, ...continuation)))}` +
        `&$orderby=${encodeURIComponent(`${def.dateAttribute} ${sort},${def.idAttribute} ${sort}`)}&$top=${top}`,
      ANNOTATIONS,
    );
    const rows = body.value ?? [];
    const before = s.buffer.length;
    s.done = rows.length < top;
    // A row read before comes back only from the boundary second; while the server has more, at least one row is new
    // (top exceeds the rows already read), so the loop moves on.
    const read = new Set(s.seen);
    const fresh = rows.map(def.toItem).filter((item) => !read.has(item.id));
    await def.complete?.(api, fresh);
    for (const item of fresh) {
      s.buffer.push(item);
      const second = secondOf(item.modifiedOn);
      if (second !== s.second) {
        s.second = second;
        s.seen = [];
      }
      s.seen.push(item.id);
    }
    // Only past the 5000-row cap (more than 5000 rows in one second) can a read bring nothing new: stop rather than loop.
    if (s.buffer.length === before) s.done = true;
  }
}

function streams(scope: TimelineScope, f: TimelineFilters, now: Date, excludeOverdue: boolean, replied: string[]): StreamState[] {
  const result: StreamState[] = [];
  if (queriesActivities(f, scope)) {
    const base = activityConditions(f, scope, now);
    result.push({
      def: {
        set: 'activitypointers',
        idAttribute: 'activityid',
        dateAttribute: 'modifiedon',
        select: ACTIVITY_SELECT,
        conditions: excludeOverdue ? and(base, notOverdue(now)) : base,
        toItem: activityItem,
      },
      buffer: [],
      done: false,
      seen: [],
    });
  }
  const notes = noteConditions(f, scope, now);
  if (notes) {
    result.push({
      def: { set: 'annotations', idAttribute: 'annotationid', dateAttribute: 'modifiedon', select: NOTE_SELECT, conditions: notes, toItem: noteItem },
      buffer: [],
      done: false,
      seen: [],
    });
  }
  const posts = postConditions(f, scope, now, replied);
  if (posts) {
    result.push({
      def: { set: 'posts', idAttribute: 'postid', dateAttribute: 'createdon', select: POST_SELECT, conditions: posts, toItem: postItem, complete: countReplies },
      buffer: [],
      done: false,
      seen: [],
    });
  }
  return result;
}

/** Negative when `a` comes before `b` in the sort order: by `modifiedon` (a post's `createdon`), then by id. */
function compare(a: TimelineItem, b: TimelineItem, sort: 'asc' | 'desc'): number {
  const byDate = Date.parse(a.modifiedOn) - Date.parse(b.modifiedOn);
  const order = byDate !== 0 ? byDate : a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
  return sort === 'asc' ? order : -order;
}

/**
 * One page of the timeline: up to `pageSize + 1` activities, notes and posts read per stream, ordered by `modifiedon` (posts
 * by `createdon`) then id,
 * merged and cut to `pageSize`. `cursor` (from the previous page) continues each stream where it stopped; no cursor in
 * the result: nothing more. `excludeOverdue` leaves out the overdue activities (with groups on, {@link loadOverdue} reads them).
 */
export async function loadPage(
  api: XrmApi,
  scope: TimelineScope,
  f: TimelineFilters,
  cursor: TimelineCursor | undefined,
  now: Date,
  pageSize = 10,
  excludeOverdue = false,
): Promise<{ items: TimelineItem[]; cursor?: TimelineCursor }> {
  const state: StreamState[] = cursor
    ? cursor.streams.map((s) => ({ ...s, buffer: [...s.buffer], seen: [...s.seen] }))
    : streams(scope, f, now, excludeOverdue, needsReplied(f, scope, now) ? await repliedPosts(api, scope) : []);
  await Promise.all(state.map((s) => fill(api, s, pageSize + 1, f.sort)));

  const items: TimelineItem[] = [];
  while (items.length < pageSize) {
    let next: StreamState | undefined;
    for (const s of state) {
      if (s.buffer.length && (!next || compare(s.buffer[0]!, next.buffer[0]!, f.sort) < 0)) next = s;
    }
    if (!next) break;
    items.push(next.buffer.shift()!);
  }
  const more = state.some((s) => s.buffer.length > 0 || !s.done);
  return more ? { items, cursor: new TimelineCursor(state) } : { items };
}

/** The overdue activities (active, `scheduledend` passed) matching `f`, by `scheduledend` ascending: up to 50, and whether more exist. */
export async function loadOverdue(
  api: XrmApi,
  scope: TimelineScope,
  f: TimelineFilters,
  now: Date,
): Promise<{ items: TimelineItem[]; more: boolean }> {
  if (!queriesActivities(f, scope)) return { items: [], more: false };
  const filter = toOData(and(activityConditions(f, scope, now), overdue(now)));
  const body = await api.get<{ value?: Row[] }>(
    `activitypointers?$select=${ACTIVITY_SELECT}&$filter=${encodeURIComponent(filter)}` +
      `&$orderby=${encodeURIComponent('scheduledend asc,activityid asc')}&$top=${OVERDUE_LIMIT + 1}`,
    ANNOTATIONS,
  );
  const rows = body.value ?? [];
  return { items: rows.slice(0, OVERDUE_LIMIT).map(activityItem), more: rows.length > OVERDUE_LIMIT };
}

/** A FetchXML `<filter>` for `c` (a single condition wrapped in an AND filter). */
const fetchFilter = (c: Cond) => (c.kind === 'and' || c.kind === 'or' ? toFetchFilter(c) : `<filter type="and">${toFetchFilter(c)}</filter>`);

/**
 * The faceted counts: Activities, Notes, Posts and All ignore the record type; Overdue replaces the status; Mine sets the
 * owner (a post's creator); each Posts by option replaces the Posts by selection;
 * the activity types ignore the type and the status reasons ignore the reason. The numbers come from `$count=true`
 * (with `$top=1`: the server refuses `$top=0`) on the same `$filter` as the pages, the per-type and per-reason ones from
 * aggregate FetchXML grouped by `activitytypecode` (and `statuscode`) on the same conditions. All run as the user.
 */
export async function loadCounts(api: XrmApi, scope: TimelineScope, f: TimelineFilters, now: Date): Promise<TimelineCounts> {
  const requests = new Map<string, Promise<unknown>>();
  const get = <T>(path: string): Promise<T> => {
    if (!requests.has(path)) requests.set(path, api.get<T>(path, ANNOTATIONS));
    return requests.get(path) as Promise<T>;
  };
  const count = async (set: string, id: string, c: Cond | undefined): Promise<number> => {
    if (!c) return 0;
    const body = await get<{ '@odata.count'?: number }>(`${set}?$select=${id}&$filter=${encodeURIComponent(toOData(c))}&$count=true&$top=1`);
    return body['@odata.count'] ?? 0;
  };
  const activities = (g: TimelineFilters) => count('activitypointers', 'activityid', queriesActivities(g, scope) ? activityConditions(g, scope, now) : undefined);
  const notes = (g: TimelineFilters) => count('annotations', 'annotationid', noteConditions(g, scope, now));
  // Read once, and only when a posts count has Mine (the Mine count always has).
  let replied: Promise<string[]> | undefined;
  const posts = async (g: TimelineFilters) => {
    if (!needsReplied(g, scope, now)) return count('posts', 'postid', postConditions(g, scope, now));
    replied ??= repliedPosts(api, scope);
    return count('posts', 'postid', postConditions(g, scope, now, await replied));
  };
  const grouped = async (g: TimelineFilters, byReason: boolean): Promise<Row[]> => {
    if (!queriesActivities(g, scope)) return [];
    const fetchXml =
      `<fetch aggregate="true"><entity name="activitypointer">` +
      `<attribute name="activitytypecode" groupby="true" alias="type"/>` +
      (byReason ? `<attribute name="statuscode" groupby="true" alias="reason"/>` : '') +
      `<attribute name="activityid" aggregate="count" alias="n"/>` +
      fetchFilter(activityConditions(g, scope, now)) +
      `</entity></fetch>`;
    return (await get<{ value?: Row[] }>(`activitypointers?fetchXml=${encodeURIComponent(fetchXml)}`)).value ?? [];
  };

  const anyType = { ...f, recordTypes: [] };
  const mine = { ...f, mine: true };
  const [activityCount, noteCount, postCount, overdueCount, mineActivities, mineNotes, minePosts, postsByAuto, postsByUsers, types, reasons] = await Promise.all([
    activities(anyType),
    notes(anyType),
    posts(anyType),
    activities({ ...f, statuses: ['overdue'] }),
    activities(mine),
    notes(mine),
    posts(mine),
    posts({ ...f, postsBy: ['auto'] }),
    posts({ ...f, postsBy: ['users'] }),
    grouped({ ...f, activityTypes: [] }, false),
    grouped({ ...f, statusReasons: [] }, true),
  ]);
  return {
    all: activityCount + noteCount + postCount,
    activities: activityCount,
    notes: noteCount,
    posts: postCount,
    postsByAuto,
    postsByUsers,
    overdue: overdueCount,
    mine: mineActivities + mineNotes + minePosts,
    byType: Object.fromEntries(types.map((r) => [String(r.type), Number(r.n)])),
    byReason: reasons.map((r) => ({
      type: String(r.type),
      value: Number(r.reason),
      label: str(r[`reason${FV}`]) ?? String(r.reason),
      count: Number(r.n),
    })),
  };
}
