import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { XrmApi } from '../../../shell/api/client';
import { server } from '../../../test/server';
import { EMPTY_FILTERS, activityFilter, noteFilter, type TimelineFilters, type TimelineScope } from './filters';
import { loadComments, loadCounts, loadOverdue, loadPage, type TimelineItem } from './timelineQuery';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const FV = '@OData.Community.Display.V1.FormattedValue';
const RECORD = 'cccccccc-0000-0000-0000-000000000003';
const ME = 'aaaaaaaa-0000-0000-0000-000000000001';
const NOW = new Date('2026-11-16T10:00:00.000Z');
const scope: TimelineScope = { recordId: RECORD, userId: ME, shownTypes: ['task', 'email'], hasActivities: true, hasNotes: true, hasPosts: false };
const withPosts: TimelineScope = { ...scope, hasPosts: true };
const f = (patch: Partial<TimelineFilters>): TimelineFilters => ({ ...EMPTY_FILTERS, ...patch });

/** A stored row: `at` is the full-precision modifiedon; the fake answers it truncated to seconds, as the server does. */
interface Row {
  id: string;
  at: string;
  scheduledend?: string;
  /** A post's source: 1 auto, 2 users (the default). */
  source?: number;
  /** A post's creator (default: ME). */
  createdby?: string;
}

interface Seen {
  set: string;
  params: URLSearchParams;
  prefer: string | null;
}

const seconds = (iso: string) => new Date(iso).toISOString().replace(/\.\d{3}Z$/, 'Z');

const activityJson = (r: Row) => ({
  activityid: r.id,
  activitytypecode: 'task',
  subject: `Subject ${r.id}`,
  description: `Body ${r.id}`,
  modifiedon: seconds(r.at),
  scheduledend: r.scheduledend,
  statecode: 0,
  statuscode: 2,
  [`statuscode${FV}`]: 'Not Started',
  _ownerid_value: ME,
  [`_ownerid_value${FV}`]: 'Ada',
});
const noteJson = (r: Row) => ({
  annotationid: r.id,
  subject: `Note ${r.id}`,
  notetext: `Text ${r.id}`,
  modifiedon: seconds(r.at),
  filename: 'minutes.pdf',
  filesize: 240000,
  _ownerid_value: ME,
});

const postJson = (r: Row) => ({
  postid: r.id,
  text: `Post ${r.id}`,
  source: r.source ?? 2,
  type: 7,
  createdon: seconds(r.at),
  _createdby_value: r.createdby ?? ME,
  [`_createdby_value${FV}`]: 'Ada',
});

interface Fake {
  activities?: Row[];
  notes?: Row[];
  posts?: Row[];
  /** Comments per post id, for the reply count aggregate. */
  replies?: Record<string, number>;
  comments?: Record<string, unknown>[];
  /** The posts of the record the user commented on (the Mine pre-query). */
  replied?: string[];
  /** The reply count aggregate answers 500. */
  failReplies?: boolean;
  counts?: (set: string, filter: string) => number;
  aggregate?: (fetchXml: string) => Record<string, unknown>[];
}

/** A small Web API over two lists: `$filter` continuation on modifiedon (`lt`/`ge`), `$orderby` direction, `$top`. */
function fake(data: Fake): Seen[] {
  const seen: Seen[] = [];
  server.use(
    http.get('*/api/data/v9.2/:set', ({ request, params }) => {
      const url = new URL(request.url);
      const set = String(params.set);
      seen.push({ set, params: url.searchParams, prefer: request.headers.get('Prefer') });
      const fetchXml = url.searchParams.get('fetchXml');
      if (fetchXml && set === 'postcomments' && fetchXml.includes('<link-entity')) {
        return HttpResponse.json({ value: (data.replied ?? []).map((id) => ({ post: id, n: 1 })) });
      }
      if (fetchXml && set === 'postcomments' && data.failReplies) {
        return HttpResponse.json({ error: { code: '0x80040216', message: 'Replies refused' } }, { status: 500 });
      }
      if (fetchXml && set === 'postcomments') {
        const ids = [...fetchXml.matchAll(/attribute="postid" operator="eq" value="([^"]+)"/g)].map((m) => m[1]!);
        return HttpResponse.json({ value: ids.filter((id) => data.replies?.[id]).map((id) => ({ post: id, n: data.replies![id] })) });
      }
      if (fetchXml) return HttpResponse.json({ value: data.aggregate?.(fetchXml) ?? [] });
      const filter = url.searchParams.get('$filter') ?? '';
      if (url.searchParams.get('$count') === 'true') {
        return HttpResponse.json({ '@odata.count': data.counts?.(set, filter) ?? 0, value: [] });
      }
      if (set === 'postcomments') return HttpResponse.json({ value: data.comments ?? [] });
      const rows = (set === 'activitypointers' ? data.activities : set === 'posts' ? data.posts : data.notes) ?? [];
      const asc = /(modifiedon|createdon) asc/.test(url.searchParams.get('$orderby') ?? '') || /scheduledend asc/.test(url.searchParams.get('$orderby') ?? '');
      const lt = /(?:modifiedon|createdon) lt (\S+?)\)?(?: |$)/.exec(filter)?.[1];
      const ge = /\(?(?:modifiedon|createdon) ge (\S+?)\)?(?: |$)/.exec(filter)?.[1];
      const top = Number(url.searchParams.get('$top') ?? 5000);
      const creator = /_createdby_value eq (\S+?)\)?(?: |$)/.exec(filter)?.[1];
      const page = rows
        .filter((r) => set !== 'posts' || !creator || (r.createdby ?? ME) === creator || filter.includes(`postid eq ${r.id}`))
        .filter((r) => (lt ? r.at < new Date(lt).toISOString() : true) && (ge ? r.at >= new Date(ge).toISOString() : true))
        .sort((a, b) => (a.at === b.at ? a.id.localeCompare(b.id) : a.at.localeCompare(b.at)) * (asc ? 1 : -1))
        .slice(0, top);
      return HttpResponse.json({ value: page.map((r): Record<string, unknown> => (set === 'activitypointers' ? activityJson(r) : set === 'posts' ? postJson(r) : noteJson(r))) });
    }),
  );
  return seen;
}

/** Rows `prefix1..prefixN`, one minute apart going back from `start`, with `offset` seconds added. */
function rows(prefix: string, n: number, start = '2026-11-16T09:00:00.000Z', offsetSeconds = 0): Row[] {
  return Array.from({ length: n }, (_, i) => ({
    id: `${prefix}${String(i + 1).padStart(2, '0')}`,
    at: new Date(Date.parse(start) - i * 60_000 + offsetSeconds * 1000).toISOString(),
  }));
}

const ids = (items: TimelineItem[]) => items.map((i) => i.id);
const pageRequests = (seen: Seen[], set: string) => seen.filter((s) => s.set === set && !s.params.has('$count') && !s.params.has('fetchXml'));

describe('timeline query', () => {
  it('first_page_merges_two_streams_by_modifiedon_desc', async () => {
    // Activities on the minute, notes 30 seconds later: they alternate, note first.
    const seen = fake({ activities: rows('a', 8), notes: rows('n', 8, undefined, 30) });
    const { items, cursor } = await loadPage(api(), scope, EMPTY_FILTERS, undefined, NOW);
    expect(ids(items)).toEqual(['n01', 'a01', 'n02', 'a02', 'n03', 'a03', 'n04', 'a04', 'n05', 'a05']);
    expect(cursor).toBeDefined();

    const [activities] = pageRequests(seen, 'activitypointers');
    expect(activities!.params.get('$select')).toBe(
      'activityid,activitytypecode,subject,description,modifiedon,scheduledend,statecode,statuscode,_ownerid_value',
    );
    expect(activities!.params.get('$filter')).toBe(activityFilter(EMPTY_FILTERS, scope, NOW));
    expect(activities!.params.get('$orderby')).toBe('modifiedon desc,activityid desc');
    expect(activities!.params.get('$top')).toBe('11');
    expect(activities!.prefer).toContain('odata.include-annotations="*"');
    const [notes] = pageRequests(seen, 'annotations');
    expect(notes!.params.get('$select')).toBe('annotationid,subject,notetext,modifiedon,filename,filesize,_ownerid_value');
    expect(notes!.params.get('$select')).not.toContain('documentbody');
    expect(notes!.params.get('$filter')).toBe(`_objectid_value eq ${RECORD}`);
    expect(notes!.params.get('$orderby')).toBe('modifiedon desc,annotationid desc');
    expect(notes!.params.get('$top')).toBe('11');

    expect(items[0]).toEqual({
      kind: 'note', id: 'n01', table: 'annotation', title: 'Note n01', body: 'Text n01', modifiedOn: '2026-11-16T09:00:30Z',
      ownerId: ME, fileName: 'minutes.pdf', fileSize: 240000,
    });
    expect(items[1]).toEqual({
      kind: 'activity', id: 'a01', table: 'task', title: 'Subject a01', body: 'Body a01', modifiedOn: '2026-11-16T09:00:00Z',
      stateCode: 0, statusCode: 2, statusLabel: 'Not Started', ownerId: ME, ownerName: 'Ada',
    });
  });

  it('load_more_continues_each_stream', async () => {
    const seen = fake({ activities: rows('a', 8), notes: rows('n', 8, undefined, 30) });
    const first = await loadPage(api(), scope, EMPTY_FILTERS, undefined, NOW);
    const second = await loadPage(api(), scope, EMPTY_FILTERS, first.cursor, NOW);
    expect(ids(second.items)).toEqual(['n06', 'a06', 'n07', 'a07', 'n08', 'a08']);
    expect(second.cursor).toBeUndefined();
    // Each stream had read 8 of 8 rows: nothing more to ask.
    expect(pageRequests(seen, 'activitypointers')).toHaveLength(1);
    expect(pageRequests(seen, 'annotations')).toHaveLength(1);
  });

  it('load_more_asks_each_stream_from_where_it_stopped', async () => {
    const seen = fake({ activities: rows('a', 30), notes: rows('n', 2, '2026-11-15T00:00:00.000Z') });
    const first = await loadPage(api(), scope, EMPTY_FILTERS, undefined, NOW);
    expect(ids(first.items)).toEqual(rows('a', 10).map((r) => r.id));
    const second = await loadPage(api(), scope, EMPTY_FILTERS, first.cursor, NOW);
    expect(ids(second.items)).toEqual(rows('a', 20).slice(10).map((r) => r.id));
    const [, more] = pageRequests(seen, 'activitypointers');
    // The 11th row (a11) was read and kept; the stream continues after it.
    expect(more!.params.get('$filter')).toBe(`${activityFilter(EMPTY_FILTERS, scope, NOW)} and modifiedon lt 2026-11-16T08:50:01.000Z`);
    expect(more!.params.get('$top')).toBe('11');
    // The notes were read whole on the first page.
    expect(pageRequests(seen, 'annotations')).toHaveLength(1);
    const third = await loadPage(api(), scope, EMPTY_FILTERS, second.cursor, NOW);
    expect(ids(third.items)).toEqual([...rows('a', 30).slice(20).map((r) => r.id)]);
    const fourth = await loadPage(api(), scope, EMPTY_FILTERS, third.cursor, NOW);
    expect(ids(fourth.items)).toEqual(['n01', 'n02']);
    expect(fourth.cursor).toBeUndefined();
  });

  it('load_more_with_equal_dates_neither_repeats_nor_skips', async () => {
    // 25 activities in the same second (the server answers whole seconds), stored a millisecond apart.
    const same: Row[] = Array.from({ length: 25 }, (_, i) => ({
      id: `a${String(i + 1).padStart(2, '0')}`,
      at: new Date(Date.parse('2026-11-16T09:00:00.000Z') + (i % 7)).toISOString(),
    }));
    fake({ activities: same, notes: [{ id: 'n01', at: '2026-11-16T09:00:00.003Z' }] });
    const all: string[] = [];
    let cursor;
    for (let guard = 0; guard < 10; guard++) {
      const page = await loadPage(api(), scope, EMPTY_FILTERS, cursor, NOW);
      all.push(...ids(page.items));
      cursor = page.cursor;
      if (!cursor) break;
    }
    expect(all).toHaveLength(26);
    expect(new Set(all).size).toBe(26);
  });

  it('no_cursor_when_both_streams_are_exhausted', async () => {
    fake({ activities: rows('a', 3), notes: rows('n', 2, undefined, 30) });
    const { items, cursor } = await loadPage(api(), scope, EMPTY_FILTERS, undefined, NOW);
    expect(items).toHaveLength(5);
    expect(cursor).toBeUndefined();
  });

  it('sort_asc_reverses_order', async () => {
    const seen = fake({ activities: rows('a', 3), notes: rows('n', 3, undefined, 30) });
    const { items } = await loadPage(api(), scope, f({ sort: 'asc' }), undefined, NOW);
    expect(ids(items)).toEqual(['a03', 'n03', 'a02', 'n02', 'a01', 'n01']);
    expect(pageRequests(seen, 'activitypointers')[0]!.params.get('$orderby')).toBe('modifiedon asc,activityid asc');
    expect(pageRequests(seen, 'annotations')[0]!.params.get('$orderby')).toBe('modifiedon asc,annotationid asc');
  });

  it('exclude_overdue_leaves_them_out_of_the_page', async () => {
    const seen = fake({ activities: rows('a', 2), notes: rows('n', 1) });
    await loadPage(api(), scope, EMPTY_FILTERS, undefined, NOW, 10, true);
    expect(pageRequests(seen, 'activitypointers')[0]!.params.get('$filter')).toBe(
      `${activityFilter(EMPTY_FILTERS, scope, NOW)}` +
        ` and (statecode eq 1 or statecode eq 2 or scheduledend eq null or scheduledend ge 2026-11-16T10:00:00.000Z)`,
    );
    // Notes are never overdue.
    expect(pageRequests(seen, 'annotations')[0]!.params.get('$filter')).toBe(`_objectid_value eq ${RECORD}`);
  });

  it('overdue_stream_sorted_by_scheduledend_max_50', async () => {
    const overdue = Array.from({ length: 60 }, (_, i) => ({
      id: `o${String(i + 1).padStart(2, '0')}`,
      at: '2026-11-10T00:00:00.000Z',
      scheduledend: new Date(Date.parse('2026-11-01T00:00:00.000Z') + i * 3_600_000).toISOString(),
    }));
    const seen = fake({ activities: overdue, notes: rows('n', 3) });
    const { items, more } = await loadOverdue(api(), scope, f({ mine: true }), NOW);
    expect(items).toHaveLength(50);
    expect(more).toBe(true);
    expect(seen).toHaveLength(1);
    const [request] = seen;
    expect(request!.set).toBe('activitypointers');
    expect(request!.params.get('$orderby')).toBe('scheduledend asc,activityid asc');
    expect(request!.params.get('$top')).toBe('51');
    expect(request!.params.get('$filter')).toBe(
      `${activityFilter(f({ mine: true }), scope, NOW)} and (statecode eq 0 or statecode eq 3) and scheduledend lt 2026-11-16T10:00:00.000Z`,
    );

    fake({ activities: overdue.slice(0, 4), notes: [] });
    expect(await loadOverdue(api(), scope, EMPTY_FILTERS, NOW)).toMatchObject({ more: false });
    expect(await loadOverdue(api(), scope, f({ recordTypes: ['note'] }), NOW)).toEqual({ items: [], more: false });
  });

  const aggregate = (fetchXml: string) =>
    fetchXml.includes('alias="reason"')
      ? [
          { type: 'task', reason: 5, [`reason${FV}`]: 'Completed', n: 3 },
          { type: 'email', reason: 3, n: 1 },
        ]
      : [
          { type: 'task', n: 10 },
          { type: 'email', n: 5 },
        ];

  it('counts_are_faceted', async () => {
    const seen = fake({
      counts: (set, filter) => (set === 'annotations' ? (filter.includes('_ownerid_value') ? 3 : 8) : filter.includes('scheduledend lt') ? 2 : filter.includes('_ownerid_value') ? 6 : 15),
      aggregate,
    });
    // Notes selected: the Activities count stays, as the other record type's options do.
    const counts = await loadCounts(api(), scope, f({ recordTypes: ['note'] }), NOW);
    expect(counts).toMatchObject({ all: 23, activities: 15, notes: 8 });
    // Overdue and the activity types and reasons use the record type selection (Notes): no activity can match.
    expect(counts.overdue).toBe(0);
    expect(counts.byType).toEqual({});
    expect(counts.byReason).toEqual([]);
    expect(counts.mine).toBe(3);
    for (const s of seen.filter((r) => r.params.has('$count'))) expect(s.params.get('$top')).toBe('1');

    // Nothing selected: every option counts.
    const open = await loadCounts(api(), scope, EMPTY_FILTERS, NOW);
    expect(open).toEqual({
      all: 23, activities: 15, notes: 8, posts: 0, postsByAuto: 0, postsByUsers: 0, overdue: 2, mine: 9,
      byType: { task: 10, email: 5 },
      byReason: [
        { type: 'task', value: 5, label: 'Completed', count: 3 },
        { type: 'email', value: 3, label: '3', count: 1 },
      ],
    });

    // An activity type selected: the type counts ignore it, the notes count turns 0 (an activity-only filter).
    const seenTask = fake({ counts: () => 4, aggregate });
    const task = await loadCounts(api(), scope, f({ activityTypes: ['task'] }), NOW);
    expect(task.notes).toBe(0);
    expect(task.byType).toEqual({ task: 10, email: 5 });
    const typeFetch = seenTask.map((s) => s.params.get('fetchXml')).find((x) => x && !x.includes('alias="reason"'))!;
    expect(typeFetch).toContain('<condition attribute="activitytypecode" operator="eq" value="email"/>');
    const reasonFetch = seenTask.map((s) => s.params.get('fetchXml')).find((x) => x?.includes('alias="reason"'))!;
    expect(reasonFetch).toContain('aggregate="true"');
    expect(reasonFetch).toContain('<attribute name="activitytypecode" groupby="true" alias="type"/>');
    expect(reasonFetch).toContain('<attribute name="statuscode" groupby="true" alias="reason"/>');
    expect(reasonFetch).not.toContain('value="email"');
  });

  it('counts_use_the_same_filter_as_the_page', async () => {
    const filters = f({ search: "O'Brien", modified: ['last7d'] });
    const seen = fake({ activities: rows('a', 1), notes: rows('n', 1), counts: () => 1, aggregate });
    await loadPage(api(), scope, filters, undefined, NOW);
    await loadCounts(api(), scope, filters, NOW);
    const countFilters = (set: string) => seen.filter((s) => s.set === set && s.params.has('$count')).map((s) => s.params.get('$filter'));
    expect(countFilters('activitypointers')).toContain(pageRequests(seen, 'activitypointers')[0]!.params.get('$filter'));
    expect(countFilters('annotations')).toContain(pageRequests(seen, 'annotations')[0]!.params.get('$filter'));
    expect(pageRequests(seen, 'annotations')[0]!.params.get('$filter')).toBe(noteFilter(filters, scope, NOW));
    // The aggregate carries the same conditions in FetchXML.
    const fetchXml = seen.map((s) => s.params.get('fetchXml')).find(Boolean)!;
    expect(fetchXml).toContain(`<condition attribute="regardingobjectid" operator="eq" value="${RECORD}"/>`);
    expect(fetchXml).toContain('<condition attribute="modifiedon" operator="ge" value="2026-11-09T10:00:00.000Z"/>');
    expect(fetchXml).toContain(`<condition attribute="subject" operator="like" value="%O'Brien%"/>`);
  });

  it('notes_only_table_never_queries_activitypointers', async () => {
    const notesOnly = { ...scope, hasActivities: false };
    const seen = fake({ notes: rows('n', 2), counts: () => 2, aggregate });
    const page = await loadPage(api(), notesOnly, EMPTY_FILTERS, undefined, NOW);
    expect(ids(page.items)).toEqual(['n01', 'n02']);
    const counts = await loadCounts(api(), notesOnly, EMPTY_FILTERS, NOW);
    expect(counts).toEqual({ all: 2, activities: 0, notes: 2, posts: 0, postsByAuto: 0, postsByUsers: 0, overdue: 0, mine: 2, byType: {}, byReason: [] });
    expect(await loadOverdue(api(), notesOnly, EMPTY_FILTERS, NOW)).toEqual({ items: [], more: false });
    expect(seen.some((s) => s.set === 'activitypointers')).toBe(false);
  });

  it('merges_three_streams_by_date', async () => {
    // Activities on the minute, notes 20 seconds later, posts 40 seconds later: posts by createdon, the others by modifiedon.
    const seen = fake({ activities: rows('a', 2), notes: rows('n', 2, undefined, 20), posts: rows('p', 15, undefined, 40), replies: { p01: 2 } });
    const first = await loadPage(api(), withPosts, EMPTY_FILTERS, undefined, NOW);
    expect(ids(first.items)).toEqual(['p01', 'n01', 'a01', 'p02', 'n02', 'a02', 'p03', 'p04', 'p05', 'p06']);
    const [posts] = pageRequests(seen, 'posts');
    expect(posts!.params.get('$select')).toBe('postid,text,source,type,createdon,_createdby_value');
    expect(posts!.params.get('$filter')).toBe(`_regardingobjectid_value eq ${RECORD}`);
    expect(posts!.params.get('$orderby')).toBe('createdon desc,postid desc');
    expect(posts!.params.get('$top')).toBe('11');
    expect(posts!.prefer).toContain('odata.include-annotations="*"');
    expect(first.items[0]).toEqual({
      kind: 'post', id: 'p01', table: 'post', title: 'Post p01', modifiedOn: '2026-11-16T09:00:40Z', createdOn: '2026-11-16T09:00:40Z',
      source: 2, authorId: ME, authorName: 'Ada', replyCount: 2,
    });
    expect(first.items[3]).toMatchObject({ id: 'p02', replyCount: 0 });

    // Load more continues each stream: the posts from the second of the last post read, by createdon.
    const second = await loadPage(api(), withPosts, EMPTY_FILTERS, first.cursor, NOW);
    expect(ids(second.items)).toEqual(['p07', 'p08', 'p09', 'p10', 'p11', 'p12', 'p13', 'p14', 'p15']);
    expect(second.cursor).toBeUndefined();
    const [, more] = pageRequests(seen, 'posts');
    expect(more!.params.get('$filter')).toBe(`_regardingobjectid_value eq ${RECORD} and createdon lt 2026-11-16T08:50:41.000Z`);
    expect(pageRequests(seen, 'activitypointers')).toHaveLength(1);
    expect(pageRequests(seen, 'annotations')).toHaveLength(1);
  });

  it('reply_counts_come_from_one_aggregate_per_read', async () => {
    const seen = fake({ posts: rows('p', 3), replies: { p01: 4, p03: 1 } });
    const { items } = await loadPage(api(), { ...withPosts, hasActivities: false, hasNotes: false }, EMPTY_FILTERS, undefined, NOW);
    expect(items.map((i) => i.replyCount)).toEqual([4, 0, 1]);
    const aggregates = seen.filter((s) => s.set === 'postcomments');
    expect(aggregates).toHaveLength(1);
    const fetchXml = aggregates[0]!.params.get('fetchXml')!;
    expect(fetchXml).toContain('<fetch aggregate="true"><entity name="postcomment">');
    expect(fetchXml).toContain('<attribute name="postid" groupby="true" alias="post"/>');
    expect(fetchXml).toContain('<attribute name="postcommentid" aggregate="count" alias="n"/>');
    for (const id of ['p01', 'p02', 'p03']) expect(fetchXml).toContain(`<condition attribute="postid" operator="eq" value="${id}"/>`);
  });

  it('posts_off_runs_no_post_query', async () => {
    const seen = fake({ activities: rows('a', 2), notes: rows('n', 2), posts: rows('p', 2), counts: () => 1, aggregate });
    const page = await loadPage(api(), scope, EMPTY_FILTERS, undefined, NOW);
    expect(ids(page.items)).toEqual(['n01', 'a01', 'n02', 'a02']);
    const counts = await loadCounts(api(), scope, EMPTY_FILTERS, NOW);
    expect(counts).toMatchObject({ posts: 0, postsByAuto: 0, postsByUsers: 0 });
    expect(seen.some((s) => s.set === 'posts' || s.set === 'postcomments')).toBe(false);
  });

  it('faceted_counts_include_posts', async () => {
    const counts = (set: string, filter: string) =>
      set === 'posts'
        ? filter.includes('source eq 1') ? 1 : filter.includes('source eq 2') ? 4 : filter.includes('_createdby_value') ? 2 : 5
        : set === 'annotations'
          ? filter.includes('_ownerid_value') ? 3 : 8
          : filter.includes('scheduledend lt') ? 2 : filter.includes('_ownerid_value') ? 6 : 15;
    const seen = fake({ counts, aggregate });
    expect(await loadCounts(api(), withPosts, EMPTY_FILTERS, NOW)).toMatchObject({
      all: 28, activities: 15, notes: 8, posts: 5, postsByAuto: 1, postsByUsers: 4, mine: 11,
    });
    const mine = seen.find((s) => s.set === 'posts' && s.params.get('$filter')!.includes('_createdby_value'))!;
    expect(mine.params.get('$filter')).toBe(`_regardingobjectid_value eq ${RECORD} and _createdby_value eq ${ME}`);
    // The record type is ignored by Activities, Notes and Posts, as in PR1.
    expect(await loadCounts(api(), withPosts, f({ recordTypes: ['post'] }), NOW)).toMatchObject({ activities: 15, notes: 8, posts: 5, postsByUsers: 4 });
    // Posts by Users: the activities and notes turn 0; each Posts by option ignores its own category.
    expect(await loadCounts(api(), withPosts, f({ postsBy: ['users'] }), NOW)).toMatchObject({
      all: 4, activities: 0, notes: 0, posts: 4, postsByAuto: 1, postsByUsers: 4, overdue: 0,
    });
    // An activity-only filter: no post can match.
    expect(await loadCounts(api(), withPosts, f({ statuses: ['overdue'] }), NOW)).toMatchObject({ posts: 0, postsByAuto: 0, postsByUsers: 0 });
  });

  it('load_comments_oldest_first', async () => {
    const seen = fake({
      comments: [
        { postcommentid: 'c1', text: 'First', createdon: '2026-11-16T08:00:00Z', _createdby_value: ME, [`_createdby_value${FV}`]: 'Ada' },
        { postcommentid: 'c2', text: 'Second', createdon: '2026-11-16T08:05:00Z', _createdby_value: ME },
      ],
    });
    expect(await loadComments(api(), 'p01')).toEqual([
      { id: 'c1', text: 'First', createdOn: '2026-11-16T08:00:00Z', authorId: ME, authorName: 'Ada' },
      { id: 'c2', text: 'Second', createdOn: '2026-11-16T08:05:00Z', authorId: ME, authorName: '' },
    ]);
    const [request] = seen;
    expect(request!.set).toBe('postcomments');
    expect(request!.params.get('$select')).toBe('postcommentid,text,createdon,_createdby_value');
    expect(request!.params.get('$filter')).toBe('_postid_value eq p01');
    expect(request!.params.get('$orderby')).toBe('createdon asc');
    expect(request!.prefer).toContain('odata.include-annotations="*"');
  });

  it('mine_includes_posts_the_user_replied_to', async () => {
    const OTHER = 'bbbbbbbb-0000-0000-0000-000000000002';
    const posts: Row[] = [
      { id: 'p01', at: '2026-11-16T09:00:00.000Z', createdby: ME },
      { id: 'p02', at: '2026-11-16T08:00:00.000Z', createdby: OTHER },
      { id: 'p03', at: '2026-11-16T07:00:00.000Z', createdby: OTHER },
    ];
    const postsOnly = { ...withPosts, hasActivities: false, hasNotes: false };
    const seen = fake({ posts, replied: ['p02'], counts: (set, filter) => (set === 'posts' && filter.includes('postid eq p02') ? 2 : 3) });
    const { items } = await loadPage(api(), postsOnly, f({ mine: true }), undefined, NOW);
    // p02 was created by someone else and commented on by the user; p03 neither.
    expect(ids(items)).toEqual(['p01', 'p02']);
    expect(pageRequests(seen, 'posts')[0]!.params.get('$filter')).toBe(
      `_regardingobjectid_value eq ${RECORD} and (_createdby_value eq ${ME} or postid eq p02)`,
    );
    const pre = seen.map((s) => s.params.get('fetchXml')).find((x) => x?.includes('<link-entity'))!;
    expect(pre).toContain(`<condition attribute="createdby" operator="eq" value="${ME}"/>`);
    expect(pre).toContain('<link-entity name="post" from="postid" to="postid" link-type="inner">');
    expect(pre).toContain(`<condition attribute="regardingobjectid" operator="eq" value="${RECORD}"/>`);

    // The Mine count has them too; the pre-query is read once per counts.
    const countSeen = fake({ posts, replied: ['p02'], counts: (set, filter) => (set === 'posts' && filter.includes('postid eq p02') ? 2 : 3) });
    expect((await loadCounts(api(), postsOnly, EMPTY_FILTERS, NOW)).mine).toBe(2);
    expect(countSeen.filter((s) => s.params.get('fetchXml')?.includes('<link-entity'))).toHaveLength(1);

    // Without Mine, no pre-query on the page.
    const plain = fake({ posts, replied: ['p02'] });
    await loadPage(api(), postsOnly, EMPTY_FILTERS, undefined, NOW);
    expect(plain.some((s) => s.params.get('fetchXml')?.includes('<link-entity'))).toBe(false);
  });

  it('reply_counts_are_batched_by_100', async () => {
    const many: Row[] = Array.from({ length: 250 }, (_, i) => ({ id: `p${String(i).padStart(3, '0')}`, at: new Date(Date.parse('2026-11-16T09:00:00.000Z') - i * 60_000).toISOString() }));
    const seen = fake({ posts: many, replies: { p000: 1, p249: 3 } });
    const { items } = await loadPage(api(), { ...withPosts, hasActivities: false, hasNotes: false }, EMPTY_FILTERS, undefined, NOW, 249);
    const aggregates = seen.filter((s) => s.set === 'postcomments');
    expect(aggregates).toHaveLength(3);
    for (const a of aggregates) expect([...a.params.get('fetchXml')!.matchAll(/attribute="postid" operator="eq"/g)].length).toBeLessThanOrEqual(100);
    expect(items[0]!.replyCount).toBe(1);
    expect(items[248]!.replyCount).toBe(0);
  });

  it('failed_reply_count_still_shows_posts', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    fake({ posts: rows('p', 2), failReplies: true });
    const { items } = await loadPage(api(), { ...withPosts, hasActivities: false, hasNotes: false }, EMPTY_FILTERS, undefined, NOW);
    expect(ids(items)).toEqual(['p01', 'p02']);
    expect(items.every((i) => i.replyCount === undefined)).toBe(true);
    expect(warn.mock.calls.some((c) => String(c[0]).includes('Replies refused'))).toBe(true);
    warn.mockRestore();
  });
});
