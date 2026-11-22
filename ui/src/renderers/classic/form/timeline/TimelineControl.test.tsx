import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { onPath } from '../../../../admin/security/testSupport';
import { attr, entity } from '../../../../admin/tables/testAttrs';
import type { RecordContext } from '../../../../apps/record/loadRecord';
import type { RecordForm } from '../../../../apps/record/useRecordForm';
import { mockApps } from '../../../../apps/testSupport';
import type { TimelineControl as TimelineModel } from '../../../../model/form';
import { server } from '../../../../test/server';
import type { ControlEnv } from '../controlEnv';
import { Providers, RECORD_ID } from '../formTestSupport';
import { TimelineControl } from './TimelineControl';

const FV = '@OData.Community.Display.V1.FormattedValue';
const NOW = new Date('2026-11-16T10:00:00.000Z');
const ME = 'aaaaaaaa-0000-0000-0000-000000000001';
const APP_ID = 'dddddddd-0000-0000-0000-000000000004';
const FORM_ID = 'ffffffff-0000-0000-0000-000000000005';

interface Act {
  id: string;
  subject: string;
  at: string;
  type?: string;
  description?: string;
  scheduledend?: string;
  statecode?: number;
  statuscode?: number;
}
interface NoteRow {
  id: string;
  subject: string;
  at: string;
  notetext?: string;
  filename?: string;
  filesize?: number;
}
interface PostRow {
  id: string;
  text: string;
  at: string;
  source?: number;
  /** The creator; the signed-in user when absent. */
  by?: string;
}
interface CommentRow {
  id: string;
  postId: string;
  text: string;
  at: string;
  by?: string;
}

const TABLES = [
  { LogicalName: 'task', SchemaName: 'Task', EntitySetName: 'tasks', IsActivity: true, DisplayName: { LocalizedLabels: [{ Label: 'Task', LanguageCode: 1033 }] } },
  { LogicalName: 'email', SchemaName: 'Email', EntitySetName: 'emails', IsActivity: true, DisplayName: { LocalizedLabels: [{ Label: 'Email', LanguageCode: 1033 }] } },
  { LogicalName: 'fax', SchemaName: 'Fax', EntitySetName: 'faxes', IsActivity: true, DisplayName: { LocalizedLabels: [{ Label: 'Fax', LanguageCode: 1033 }] } },
  { LogicalName: 'activitypointer', SchemaName: 'ActivityPointer', IsActivity: true },
  { LogicalName: 'xv_project', SchemaName: 'xv_Project', IsActivity: false },
];

const isOverdue = (a: Act) => (a.statecode ?? 0) !== 1 && (a.statecode ?? 0) !== 2 && !!a.scheduledend && a.scheduledend < NOW.toISOString();

const label = (text: string) => ({ LocalizedLabels: [{ Label: text, LanguageCode: 1033 }] });

/** A Web API over two lists, enough for the control: search, overdue, continuation, order, top, counts and aggregates. */
function fake(data: { activities?: Act[]; notes?: NoteRow[]; posts?: PostRow[]; comments?: CommentRow[]; failCounts?: boolean }): URL[] {
  const seen: URL[] = [];
  const activities = data.activities ?? [];
  const notes = data.notes ?? [];
  const posts = data.posts ?? [];
  const comments = data.comments ?? [];
  const nameOf = (by?: string) => (!by || by === ME ? 'Ada Author' : 'Otto Other');
  server.use(
    http.get('*/api/data/v9.2/*', ({ request }) => {
      const url = new URL(request.url);
      const path = decodeURIComponent(url.pathname.replace(/^.*\/api\/data\/v9\.2\//, ''));
      if (path === 'EntityDefinitions') return HttpResponse.json({ value: TABLES });
      const one = /^EntityDefinitions\(LogicalName='(\w+)'\)$/.exec(path);
      if (one && one[1] !== 'xv_project') {
        return HttpResponse.json({
          LogicalName: one[1],
          EntitySetName: `${one[1]}s`,
          PrimaryIdAttribute: 'activityid',
          Attributes: [
            {
              LogicalName: 'statecode',
              AttributeType: 'State',
              OptionSet: {
                Options: [
                  { Value: 0, Label: label('Open'), DefaultStatus: 2 },
                  { Value: 1, Label: label('Completed'), DefaultStatus: 5 },
                ],
              },
            },
            {
              LogicalName: 'statuscode',
              AttributeType: 'Status',
              OptionSet: {
                Options: [
                  { Value: 2, State: 0, Label: label(`${one[1]} open`) },
                  { Value: 4, State: 1, Label: label(`${one[1]} waiting`) },
                  { Value: 5, State: 1, Label: label(`${one[1]} done`) },
                ],
              },
            },
          ],
        });
      }
      if (path !== 'activitypointers' && path !== 'annotations' && path !== 'posts' && path !== 'postcomments') return undefined;
      seen.push(url);
      if (path === 'postcomments') {
        if (url.searchParams.get('fetchXml')) {
          const n = new Map<string, number>();
          for (const c of comments) n.set(c.postId, (n.get(c.postId) ?? 0) + 1);
          return HttpResponse.json({ value: [...n].map(([post, count]) => ({ post, n: count })) });
        }
        const postId = /_postid_value eq (\S+)/.exec(url.searchParams.get('$filter') ?? '')?.[1];
        return HttpResponse.json({
          value: comments
            .filter((c) => c.postId === postId)
            .sort((a, b) => a.at.localeCompare(b.at))
            .map((c) => ({ postcommentid: c.id, text: c.text, createdon: c.at, _createdby_value: c.by ?? ME, [`_createdby_value${FV}`]: nameOf(c.by) })),
        });
      }
      if (path === 'posts') {
        const postFilter = url.searchParams.get('$filter') ?? '';
        const source = /source eq (\d)/.exec(postFilter)?.[1];
        const shown = posts.filter((p) => !source || (p.source ?? 2) === Number(source));
        if (url.searchParams.get('$count') === 'true') return HttpResponse.json({ '@odata.count': shown.length, value: [] });
        return HttpResponse.json({
          value: shown
            .sort((a, b) => b.at.localeCompare(a.at))
            .map((p) => ({ postid: p.id, text: p.text, source: p.source ?? 2, type: 7, createdon: p.at, _createdby_value: p.by ?? ME, [`_createdby_value${FV}`]: nameOf(p.by) })),
        });
      }
      const filter = url.searchParams.get('$filter') ?? '';
      const fetchXml = url.searchParams.get('fetchXml');
      if (data.failCounts && (fetchXml || url.searchParams.get('$count') === 'true')) {
        return HttpResponse.json({ error: { code: '0x80040216', message: 'Counts refused' } }, { status: 500 });
      }
      if (fetchXml) {
        const byReason = fetchXml.includes('alias="reason"');
        const groups = new Map<string, Record<string, unknown>>();
        for (const a of activities) {
          const type = a.type ?? 'task';
          const key = byReason ? `${type}|${a.statuscode ?? 2}` : type;
          const g = groups.get(key) ?? { type, n: 0, ...(byReason ? { reason: a.statuscode ?? 2, [`reason${FV}`]: 'Pointer label' } : {}) };
          g.n = Number(g.n) + 1;
          groups.set(key, g);
        }
        return HttpResponse.json({ value: [...groups.values()] });
      }
      const search = /contains\(subject,'([^']*)'\)/.exec(filter)?.[1];
      const matches = (subject: string, body?: string) => !search || subject.includes(search) || !!body?.includes(search);
      if (url.searchParams.get('$count') === 'true') {
        const n =
          path === 'annotations'
            ? notes.filter((n) => matches(n.subject, n.notetext)).length
            : activities.filter((a) => matches(a.subject, a.description) && (!filter.includes('scheduledend lt') || isOverdue(a))).length;
        return HttpResponse.json({ '@odata.count': n, value: [] });
      }
      const orderby = url.searchParams.get('$orderby') ?? '';
      const top = Number(url.searchParams.get('$top') ?? 5000);
      if (orderby.startsWith('scheduledend')) {
        const due = activities.filter(isOverdue).sort((a, b) => a.scheduledend!.localeCompare(b.scheduledend!));
        return HttpResponse.json({ value: due.slice(0, top).map(activityJson) });
      }
      const asc = /modifiedon asc/.test(orderby);
      const lt = /modifiedon lt (\S+?)\)?(?: |$)/.exec(filter)?.[1];
      const ge = /\(?modifiedon ge (\S+?)\)?(?: |$)/.exec(filter)?.[1];
      const within = (at: string) => (lt ? at < new Date(lt).toISOString() : true) && (ge ? at >= new Date(ge).toISOString() : true);
      const order = <T extends { id: string; at: string }>(rows: T[]) =>
        rows.sort((a, b) => (a.at === b.at ? a.id.localeCompare(b.id) : a.at.localeCompare(b.at)) * (asc ? 1 : -1)).slice(0, top);
      if (path === 'annotations') return HttpResponse.json({ value: order(notes.filter((n) => within(n.at) && matches(n.subject, n.notetext))).map(noteJson) });
      const excludeOverdue = filter.includes('scheduledend eq null');
      return HttpResponse.json({
        value: order(activities.filter((a) => within(a.at) && matches(a.subject, a.description) && !(excludeOverdue && isOverdue(a)))).map(activityJson),
      });
    }),
  );
  return seen;
}

const activityJson = (a: Act) => ({
  activityid: a.id,
  activitytypecode: a.type ?? 'task',
  subject: a.subject,
  description: a.description,
  modifiedon: a.at,
  scheduledend: a.scheduledend,
  statecode: a.statecode ?? 0,
  statuscode: a.statuscode ?? 2,
  _ownerid_value: ME,
  [`_ownerid_value${FV}`]: 'Ada Owner',
});
const noteJson = (n: NoteRow) => ({
  annotationid: n.id,
  subject: n.subject,
  notetext: n.notetext,
  modifiedon: n.at,
  filename: n.filename,
  filesize: n.filesize,
  _ownerid_value: ME,
});

const control = (parameterNames: string[] = []): TimelineModel => ({ kind: 'timeline', id: 'notescontrol', classId: '{06375649-C143-495E-A496-C962E5B4488E}', parameterNames });

function ctxOf(over: { hasActivities?: boolean; hasNotes?: boolean; saved?: boolean } = {}): RecordContext {
  const saved = over.saved ?? true;
  const data = { xv_projectid: RECORD_ID, xv_name: 'Alpha' };
  return {
    entity: entity([attr('xv_name', { isPrimaryName: true })], { hasActivities: over.hasActivities ?? true, hasNotes: over.hasNotes ?? true }),
    fieldContext: { table: 'xv_project', lookups: [], globalChoices: {}, record: saved ? { set: 'xv_projects', id: RECORD_ID, data } : undefined },
    record: saved ? data : undefined,
    rights: saved ? ['Read', 'Write'] : [],
    canCreate: true,
  };
}

/** Reading every activity table (the shared activity privilege) and notes: no create, write, assign or delete. */
const READ_ONLY = ['prvReadActivity', 'prvReadNote'];
const ALL_TASK = ['prvReadActivity', 'prvCreateActivity', 'prvWriteActivity', 'prvAssignActivity', 'prvDeleteActivity'];

function renderTimeline(
  data: { activities?: Act[]; notes?: NoteRow[]; posts?: PostRow[]; comments?: CommentRow[]; failCounts?: boolean },
  options: { ctx?: RecordContext; groupByDate?: boolean; rememberFilters?: boolean; posts?: boolean; parameterNames?: string[]; privileges?: string[]; disabled?: boolean; timelineAppearance?: ControlEnv['timelineAppearance'] } = {},
) {
  mockApps({ privileges: options.privileges ?? READ_ONLY });
  const seen = fake(data);
  const env: ControlEnv = {
    ctx: options.ctx ?? ctxOf(),
    state: {} as RecordForm,
    lcid: 1033,
    appId: APP_ID,
    cache: new Map(),
    timeline: { groupByDate: options.groupByDate ?? false, rememberFilters: options.rememberFilters ?? false, posts: options.posts ?? false },
    formId: FORM_ID,
    ...(options.timelineAppearance ? { timelineAppearance: options.timelineAppearance } : {}),
  };
  render(
    <Providers>
      <TimelineControl control={control(options.parameterNames)} env={env} label="Timeline" disabled={options.disabled} />
    </Providers>,
  );
  return seen;
}

const titles = () => screen.queryAllByTestId('timeline-item-title').map((e) => e.textContent);
/** The page reads (not the counts or the overdue list) made so far, by table. */
const pageReads = (seen: URL[], set: string) =>
  seen.filter((u) => u.pathname.endsWith(`/${set}`) && u.searchParams.get('$orderby')?.startsWith('modifiedon') && !u.searchParams.has('$count'));

const today = (hhmm: string) => `2026-11-16T${hhmm}:00Z`;
const notesN = (n: number): NoteRow[] =>
  Array.from({ length: n }, (_, i) => ({ id: `n${String(i).padStart(2, '0')}`, subject: `Note ${i + 1}`, at: new Date(NOW.getTime() - (i + 1) * 60_000).toISOString().replace('.000', '') }));

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(NOW);
});
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('TimelineControl', () => {
  it('shows_activities_and_notes_newest_first', async () => {
    renderTimeline({
      activities: [
        { id: 'a1', subject: 'Review WBS', at: today('09:00') },
        { id: 'a2', subject: 'Call supplier', type: 'email', at: '2026-11-15T08:00:00Z' },
      ],
      notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }],
    });
    await waitFor(() => expect(titles()).toEqual(['Review WBS', 'Kickoff minutes', 'Call supplier']));
    const items = screen.getAllByRole('listitem');
    // Today: the time only, in the user's zone (UTC here); another day: the date.
    expect(within(items[0]!).getByText('9:00 AM')).toBeInTheDocument();
    expect(within(items[0]!).getByText('Active')).toBeInTheDocument();
    expect(within(items[0]!).getByText(/Task · Ada Owner/)).toBeInTheDocument();
    expect(within(items[1]!).getByText(/Note/)).toBeInTheDocument();
    expect(within(items[2]!).getByText('11/15/26')).toBeInTheDocument();
    expect(within(items[2]!).getByText(/Email/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'All 3' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Activities 2' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Notes 1' })).toBeInTheDocument();
  });

  it('only_existing_activity_types_without_fax_are_queried', async () => {
    const seen = renderTimeline({ activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }] });
    await waitFor(() => expect(titles()).toEqual(['Review WBS']));
    const filter = pageReads(seen, 'activitypointers')[0]!.searchParams.get('$filter')!;
    expect(filter).toContain("activitytypecode eq 'task'");
    expect(filter).toContain("activitytypecode eq 'email'");
    expect(filter).not.toContain('fax');
    expect(filter).not.toContain("'activitypointer'");
  });

  it('chip_click_filters_and_marks_pane_option', async () => {
    const seen = renderTimeline({
      activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }],
      notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }],
    });
    await waitFor(() => expect(titles()).toHaveLength(2));
    expect(screen.getByRole('button', { name: 'Filter' })).toBeInTheDocument();
    const before = pageReads(seen, 'activitypointers').length;
    await userEvent.click(screen.getByRole('button', { name: /^Notes/ }));
    await waitFor(() => expect(titles()).toEqual(['Kickoff minutes']));
    expect(pageReads(seen, 'activitypointers')).toHaveLength(before);
    expect(screen.getByRole('button', { name: /^Notes/ })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: /^All/ })).toHaveAttribute('aria-pressed', 'false');
    await userEvent.click(screen.getByRole('button', { name: 'Filter (filters applied)' }));
    const pane = screen.getByRole('group', { name: 'Timeline filters' });
    expect(within(pane).getByRole('checkbox', { name: /^Notes/ })).toBeChecked();
    expect(within(pane).getByRole('checkbox', { name: /^Activities/ })).not.toBeChecked();
    // The pane option is the same state: unchecking it turns the chip off.
    await userEvent.click(within(pane).getByRole('checkbox', { name: /^Notes/ }));
    await waitFor(() => expect(titles()).toHaveLength(2));
    expect(screen.getByRole('button', { name: /^Notes/ })).toHaveAttribute('aria-pressed', 'false');
  });

  it('status_reasons_are_labelled_from_each_activity_table', async () => {
    renderTimeline({
      activities: [
        { id: 'a1', subject: 'Review WBS', at: today('09:00') },
        { id: 'a2', subject: 'Offer', type: 'email', at: today('08:00') },
      ],
    });
    await waitFor(() => expect(titles()).toHaveLength(2));
    await userEvent.click(screen.getByRole('button', { name: 'Filter' }));
    const pane = screen.getByRole('group', { name: 'Timeline filters' });
    expect(await within(pane).findByRole('checkbox', { name: /^Task · task open/ })).toBeInTheDocument();
    expect(within(pane).getByRole('checkbox', { name: /^Email · email open/ })).toBeInTheDocument();
    expect(within(pane).queryByText(/Pointer label/)).toBeNull();
  });

  it('clear_all_filters', async () => {
    renderTimeline({
      activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }],
      notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }],
    });
    await waitFor(() => expect(titles()).toHaveLength(2));
    await userEvent.click(screen.getByRole('button', { name: /^Notes/ }));
    await userEvent.click(screen.getByRole('button', { name: /^Mine/ }));
    await waitFor(() => expect(titles()).toEqual(['Kickoff minutes']));
    await userEvent.click(screen.getByRole('button', { name: 'Filter (filters applied)' }));
    const pane = screen.getByRole('group', { name: 'Timeline filters' });
    expect(within(pane).getByRole('checkbox', { name: 'Me' })).toBeChecked();
    await userEvent.click(within(pane).getByRole('button', { name: 'Clear all filters' }));
    await waitFor(() => expect(titles()).toHaveLength(2));
    expect(screen.getByRole('button', { name: /^All/ })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: /^Mine/ })).toHaveAttribute('aria-pressed', 'false');
    expect(within(pane).getByRole('checkbox', { name: 'Me' })).not.toBeChecked();
    expect(screen.getByRole('button', { name: 'Filter' })).toBeInTheDocument();
  });

  it('search_sends_contains', async () => {
    const seen = renderTimeline({
      activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }],
      notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }],
    });
    await waitFor(() => expect(titles()).toHaveLength(2));
    await userEvent.type(screen.getByRole('searchbox', { name: 'Search timeline' }), "Kick'");
    await waitFor(() => expect(pageReads(seen, 'annotations').at(-1)!.searchParams.get('$filter')).toContain("contains(subject,'Kick''')"));
    expect(pageReads(seen, 'annotations').at(-1)!.searchParams.get('$filter')).toContain("contains(notetext,'Kick''')");
    expect(pageReads(seen, 'activitypointers').at(-1)!.searchParams.get('$filter')).toContain("contains(description,'Kick''')");
    // Debounced: one search request per stream, not one per key.
    expect(pageReads(seen, 'annotations').filter((u) => u.searchParams.get('$filter')!.includes('contains'))).toHaveLength(1);
  });

  it('load_more_appends_ten', async () => {
    renderTimeline({ notes: notesN(25) }, { ctx: ctxOf({ hasActivities: false }) });
    await waitFor(() => expect(titles()).toHaveLength(10));
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(titles()).toHaveLength(20));
    expect(titles().slice(9, 11)).toEqual(['Note 10', 'Note 11']);
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(titles()).toHaveLength(25));
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
  });

  it('notes_only_table_hides_activity_ui', async () => {
    const seen = renderTimeline({ notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }] }, { ctx: ctxOf({ hasActivities: false }) });
    await waitFor(() => expect(titles()).toEqual(['Kickoff minutes']));
    expect(screen.queryByRole('button', { name: /^Activities/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /^Overdue/ })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Filter' }));
    const pane = screen.getByRole('group', { name: 'Timeline filters' });
    for (const category of ['Record type', 'Activity type', 'Activity status', 'Activity status reason', 'Activity due date (active)']) {
      expect(within(pane).queryByRole('group', { name: category })).toBeNull();
    }
    expect(within(pane).getByRole('group', { name: 'Modified date' })).toBeInTheDocument();
    expect(within(pane).getByRole('group', { name: 'Owner' })).toBeInTheDocument();
    expect(seen.filter((u) => u.pathname.endsWith('/activitypointers'))).toHaveLength(0);
  });

  it('new_record_shows_save_first', async () => {
    const seen = renderTimeline({}, { ctx: ctxOf({ saved: false }) });
    expect(await screen.findByText('Save the record to see its timeline.')).toBeInTheDocument();
    expect(seen).toHaveLength(0);
  });

  it('table_without_activities_or_notes_says_so', async () => {
    const seen = renderTimeline({}, { ctx: ctxOf({ hasActivities: false, hasNotes: false }) });
    expect(await screen.findByText('This table has no activities or notes.')).toBeInTheDocument();
    expect(seen).toHaveLength(0);
  });

  it('groups_only_when_option_on', async () => {
    const data = {
      activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }],
      notes: [
        { id: 'n1', subject: 'Yesterday note', at: '2026-11-15T12:00:00Z' },
        { id: 'n2', subject: 'Tuesday note', at: '2026-11-10T12:00:00Z' },
        { id: 'n3', subject: 'Early month note', at: '2026-11-02T12:00:00Z' },
        { id: 'n4', subject: 'Old note', at: '2026-10-01T12:00:00Z' },
      ],
    };
    renderTimeline(data);
    await waitFor(() => expect(titles()).toHaveLength(5));
    expect(screen.queryAllByTestId('timeline-group')).toHaveLength(0);
    cleanup();

    renderTimeline(data, { groupByDate: true });
    await waitFor(() => expect(titles()).toHaveLength(5));
    // 2026-11-16 is a Monday: yesterday (Sunday) is last week, but it is Yesterday.
    const groups = screen.getAllByTestId('timeline-group');
    expect(groups.map((g) => g.getAttribute('data-group'))).toEqual(['today', 'yesterday', 'thisMonth', 'older']);
    expect(within(groups[0]!).getByText('Today')).toBeInTheDocument();
    expect(within(groups[2]!).getAllByTestId('timeline-item-title').map((e) => e.textContent)).toEqual(['Tuesday note', 'Early month note']);
  });

  it('this_week_group_from_monday', async () => {
    vi.setSystemTime(new Date('2026-11-19T10:00:00.000Z'));
    renderTimeline({ notes: [{ id: 'n1', subject: 'Monday note', at: '2026-11-16T12:00:00Z' }, { id: 'n2', subject: 'Sunday note', at: '2026-11-15T12:00:00Z' }] }, { groupByDate: true });
    await waitFor(() => expect(titles()).toHaveLength(2));
    expect(screen.getAllByTestId('timeline-group').map((g) => g.getAttribute('data-group'))).toEqual(['thisWeek', 'thisMonth']);
  });

  it('due_group_first_even_when_sorted_ascending', async () => {
    renderTimeline(
      {
        activities: [
          { id: 'a1', subject: 'Late task', at: '2026-11-01T09:00:00Z', scheduledend: '2026-11-14T09:00:00Z' },
          { id: 'a2', subject: 'Fresh task', at: today('09:00') },
        ],
        notes: [{ id: 'n1', subject: 'Old note', at: '2026-10-01T12:00:00Z' }],
      },
      { groupByDate: true },
    );
    await waitFor(() => expect(titles()).toEqual(['Late task', 'Fresh task', 'Old note']));
    await userEvent.click(screen.getByRole('button', { name: 'More options' }));
    await userEvent.click(await screen.findByRole('menuitemradio', { name: 'Oldest first' }));
    await waitFor(() => expect(titles()).toEqual(['Late task', 'Old note', 'Fresh task']));
    const groups = screen.getAllByTestId('timeline-group');
    expect(groups.map((g) => g.getAttribute('data-group'))).toEqual(['due', 'older', 'today']);
    expect(within(groups[0]!).getByText('Overdue 2 days')).toBeInTheDocument();
    expect(within(groups[0]!).getByText('Overdue')).toBeInTheDocument();
  });

  it('overdue_item_in_flat_list', async () => {
    renderTimeline({ activities: [{ id: 'a1', subject: 'Late task', at: today('09:00'), scheduledend: '2026-11-15T09:00:00Z' }] });
    await waitFor(() => expect(titles()).toEqual(['Late task']));
    expect(screen.getByText('Overdue 1 day')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Overdue 1' })).toBeInTheDocument();
  });

  it('parameters_notice_and_console_warning', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    renderTimeline({ notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }] }, { parameterNames: ['RecordsPerPage', 'X'] });
    const notice = 'This timeline has settings xrmverse does not read yet. It uses the default settings.';
    expect(await screen.findByText(notice)).toBeInTheDocument();
    await waitFor(() => expect(titles()).toHaveLength(1));
    const timelineWarnings = warn.mock.calls.filter((c) => String(c[0]).includes('RecordsPerPage'));
    expect(timelineWarnings).toHaveLength(1);
    expect(String(timelineWarnings[0]![0])).toContain('X');
    await userEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(screen.queryByText(notice)).toBeNull();
  });

  it('no_notice_without_parameters', async () => {
    renderTimeline({ notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }] });
    await waitFor(() => expect(titles()).toHaveLength(1));
    expect(screen.queryByText(/does not read yet/)).toBeNull();
  });

  it('expand_all_shows_bodies', async () => {
    renderTimeline({
      activities: [{ id: 'a1', subject: 'Review WBS', description: 'Check phase 2', at: today('09:00') }],
      notes: [{ id: 'n1', subject: 'Kickoff minutes', notetext: 'Agreed scope', filename: 'minutes.pdf', filesize: 245_760, at: today('08:30') }],
    });
    await waitFor(() => expect(titles()).toHaveLength(2));
    expect(screen.queryByText('Check phase 2')).toBeNull();
    expect(screen.queryByText('Agreed scope')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Expand all' }));
    expect(screen.getByText('Check phase 2')).toBeInTheDocument();
    expect(screen.getByText('Agreed scope')).toBeInTheDocument();
    expect(screen.getByText('minutes.pdf (240 KB)')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Collapse all' }));
    expect(screen.queryByText('Check phase 2')).toBeNull();
    // One item on its own.
    await userEvent.click(screen.getAllByRole('button', { name: 'Show details' })[1]!);
    expect(screen.getByText('Agreed scope')).toBeInTheDocument();
    expect(screen.queryByText('Check phase 2')).toBeNull();
  });

  it('failed_counts_keep_the_list', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    renderTimeline({
      activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }],
      notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }],
      failCounts: true,
    });
    await waitFor(() => expect(titles()).toEqual(['Review WBS', 'Kickoff minutes']));
    // The chips are there, without numbers.
    expect(screen.getByRole('button', { name: 'All' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Activities' })).toBeInTheDocument();
    expect(screen.queryByText(/cannot be shown/)).toBeNull();
    await waitFor(() => expect(warn.mock.calls.filter((c) => String(c[0]).includes('Counts refused'))).toHaveLength(1));
  });

  it('unreadable_activity_types_are_left_out', async () => {
    const seen = renderTimeline({ activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }] }, { privileges: ['prvReadTask', 'prvReadNote'] });
    await waitFor(() => expect(titles()).toEqual(['Review WBS']));
    const filter = pageReads(seen, 'activitypointers')[0]!.searchParams.get('$filter')!;
    expect(filter).toContain("activitytypecode eq 'task'");
    expect(filter).not.toContain("'email'");
    await userEvent.click(screen.getByRole('button', { name: 'Filter' }));
    const types = within(screen.getByRole('group', { name: 'Timeline filters' })).getByRole('group', { name: 'Activity type' });
    expect(within(types).queryByRole('checkbox', { name: /^Email/ })).toBeNull();
    cleanup();

    // No activity type readable: notes only, the activities are not queried.
    const none = renderTimeline({ notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }] }, { privileges: ['prvReadNote'] });
    await waitFor(() => expect(titles()).toEqual(['Kickoff minutes']));
    expect(screen.queryByRole('button', { name: /^Activities/ })).toBeNull();
    expect(none.filter((u) => u.pathname.endsWith('/activitypointers'))).toHaveLength(0);
  });

  it('notes_hidden_without_prvReadNote_keeps_activities', async () => {
    const seen = renderTimeline(
      { activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }], notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }] },
      { privileges: ['prvReadActivity'] },
    );
    await waitFor(() => expect(titles()).toEqual(['Review WBS']));
    expect(seen.filter((u) => u.pathname.endsWith('/annotations'))).toHaveLength(0);
    expect(screen.queryByRole('button', { name: /^Notes/ })).toBeNull();
  });

  it('create_menu_opens_a_new_activity_regarding_the_record', async () => {
    renderTimeline({ notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }] }, { privileges: [...READ_ONLY, 'prvCreateActivity'] });
    await waitFor(() => expect(titles()).toHaveLength(1));
    await userEvent.click(screen.getByRole('button', { name: 'Create a timeline record' }));
    const items = (await screen.findAllByRole('menuitem')).map((e) => e.textContent);
    expect(items).toEqual(['Email', 'Task']);
    await userEvent.click(screen.getByRole('menuitem', { name: 'Task' }));
    const location = screen.getByTestId('location').textContent!;
    expect(location).toContain('etn=task');
    expect(decodeURIComponent(location)).toContain(`regardingobjectid=${RECORD_ID}&regardingobjectidname=Alpha&regardingobjectidtype=xv_project`);
    // After its first save the new activity's form comes back to this record (RecordPage, as from a subgrid's New).
    expect(screen.getByTestId('location-return-to').textContent).toBe('/');
    expect(screen.getByTestId('location-from-subgrid').textContent).toBe('{}');
  });

  it('no_create_menu_without_create_privilege', async () => {
    renderTimeline({ notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }] });
    await waitFor(() => expect(titles()).toHaveLength(1));
    expect(screen.queryByRole('button', { name: 'Create a timeline record' })).toBeNull();
  });

  it('notes_only_table_has_no_activity_items_in_create_menu', async () => {
    const seen = renderTimeline(
      { notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }] },
      { ctx: ctxOf({ hasActivities: false }), privileges: [...READ_ONLY, 'prvCreateActivity'] },
    );
    await waitFor(() => expect(titles()).toEqual(['Kickoff minutes']));
    expect(screen.queryByRole('button', { name: 'Create a timeline record' })).toBeNull();
    expect(seen.filter((u) => u.pathname.endsWith('/activitypointers'))).toHaveLength(0);
  });

  it('actions_hidden_without_privilege', async () => {
    const data = {
      activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }],
      notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }],
    };
    renderTimeline(data);
    await waitFor(() => expect(titles()).toHaveLength(2));
    const [task, note] = screen.getAllByRole('listitem');
    expect(within(task!).getByRole('button', { name: 'Open' })).toBeInTheDocument();
    for (const name of ['Complete', 'Assign', 'Delete']) expect(within(task!).queryByRole('button', { name })).toBeNull();
    // Note actions are not activity actions.
    expect(within(note!).queryByRole('button', { name: 'Open' })).toBeNull();
    cleanup();

    // By SchemaName: Assign without Delete.
    renderTimeline(data, { privileges: ['prvReadActivity', 'prvReadNote', 'prvAssignTask'] });
    await waitFor(() => expect(titles()).toHaveLength(2));
    const row = screen.getAllByRole('listitem')[0]!;
    expect(within(row).getByRole('button', { name: 'Assign' })).toBeInTheDocument();
    expect(within(row).queryByRole('button', { name: 'Delete' })).toBeNull();
    expect(within(row).queryByRole('button', { name: 'Complete' })).toBeNull();
    cleanup();

    // Every privilege: an open task has Complete; an email and a closed task do not.
    renderTimeline(
      {
        activities: [
          { id: 'a1', subject: 'Review WBS', at: today('09:00') },
          { id: 'a2', subject: 'Offer', type: 'email', at: today('08:00') },
          { id: 'a3', subject: 'Old task', at: today('07:00'), statecode: 1, statuscode: 5 },
        ],
      },
      { privileges: ALL_TASK },
    );
    await waitFor(() => expect(titles()).toHaveLength(3));
    const [open, email, closed] = screen.getAllByRole('listitem');
    expect(await within(open!).findByRole('button', { name: 'Complete' })).toBeInTheDocument();
    for (const name of ['Open', 'Assign', 'Delete']) expect(within(open!).getByRole('button', { name })).toBeInTheDocument();
    expect(within(email!).queryByRole('button', { name: 'Complete' })).toBeNull();
    expect(within(email!).getByRole('button', { name: 'Delete' })).toBeInTheDocument();
    expect(within(closed!).queryByRole('button', { name: 'Complete' })).toBeNull();
  });

  it('complete_then_refreshes', async () => {
    const sent: unknown[] = [];
    const seen = renderTimeline({ activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }] }, { privileges: ALL_TASK });
    server.use(
      onPath('patch', 'tasks(a1)', async ({ request }) => {
        sent.push(await request.json());
        return new HttpResponse(null, { status: 204 });
      }),
    );
    await waitFor(() => expect(titles()).toHaveLength(1));
    const before = pageReads(seen, 'activitypointers').length;
    await userEvent.click(await screen.findByRole('button', { name: 'Complete' }));
    await waitFor(() => expect(sent).toEqual([{ statecode: 1, statuscode: 5 }]));
    await waitFor(() => expect(pageReads(seen, 'activitypointers').length).toBeGreaterThan(before));
  });

  it('delete_then_refreshes', async () => {
    const deleted: string[] = [];
    const seen = renderTimeline({ activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }] }, { privileges: ALL_TASK });
    server.use(
      onPath('delete', 'tasks(a1)', ({ request }) => {
        deleted.push(new URL(request.url).pathname);
        return new HttpResponse(null, { status: 204 });
      }),
    );
    await waitFor(() => expect(titles()).toHaveLength(1));
    const before = pageReads(seen, 'activitypointers').length;
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
    // Nothing is sent before the confirmation.
    const dialog = await screen.findByRole('dialog', { name: 'Delete this record?' });
    expect(deleted).toHaveLength(0);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(deleted).toHaveLength(1));
    await waitFor(() => expect(pageReads(seen, 'activitypointers').length).toBeGreaterThan(before));
  });

  it('failed_action_shows_its_reason', async () => {
    renderTimeline({ activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }] }, { privileges: ALL_TASK });
    server.use(onPath('delete', 'tasks(a1)', () => HttpResponse.json({ error: { code: '0x80040220', message: 'Delete refused' } }, { status: 403 })));
    await waitFor(() => expect(titles()).toHaveLength(1));
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete' }));
    expect(await screen.findByText(/Delete refused/)).toBeInTheDocument();
    expect(titles()).toEqual(['Review WBS']);
  });

  it('assign_then_refreshes', async () => {
    const sent: unknown[] = [];
    const seen = renderTimeline({ activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }] }, { privileges: ALL_TASK });
    server.use(
      http.get('*/api/data/v9.2/systemusers', () => HttpResponse.json({ value: [{ systemuserid: 'u1', fullname: 'Alice Adams' }] })),
      onPath('patch', 'tasks(a1)', async ({ request }) => {
        sent.push(await request.json());
        return new HttpResponse(null, { status: 204 });
      }),
    );
    await waitFor(() => expect(titles()).toHaveLength(1));
    const before = pageReads(seen, 'activitypointers').length;
    await userEvent.click(screen.getByRole('button', { name: 'Assign' }));
    const dialog = await screen.findByRole('dialog', { name: 'Assign record', hidden: true });
    await userEvent.click(await within(dialog).findByRole('option', { name: 'Alice Adams', hidden: true }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Assign', hidden: true }));
    await waitFor(() => expect(sent).toEqual([{ 'ownerid@odata.bind': '/systemusers(u1)' }]));
    await waitFor(() => expect(pageReads(seen, 'activitypointers').length).toBeGreaterThan(before));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('open_navigates_to_activity_form', async () => {
    renderTimeline({ activities: [{ id: 'a1', subject: 'Offer', type: 'email', at: today('09:00') }] });
    await waitFor(() => expect(titles()).toHaveLength(1));
    await userEvent.click(screen.getByRole('button', { name: 'Open' }));
    const location = screen.getByTestId('location').textContent!;
    expect(location).toContain(`appid=${APP_ID}`);
    expect(location).toContain('pagetype=entityrecord');
    expect(location).toContain('etn=email');
    expect(location).toContain('id=a1');
    // Save & Close or Delete on the activity come back to the record.
    expect(screen.getByTestId('location-return-to').textContent).toBe('/');
  });
});

describe('TimelineControl notes', () => {
  const NOTE_CREATE = [...READ_ONLY, 'prvCreateNote'];
  const NOTE_ALL = [...READ_ONLY, 'prvCreateNote', 'prvWriteNote', 'prvDeleteNote'];
  const KICKOFF: NoteRow = { id: 'n1', subject: 'Kickoff minutes', at: today('08:30'), notetext: 'Agenda', filename: 'plan.txt', filesize: 2 };
  const entryField = () => screen.queryByPlaceholderText('Take a note...');
  const captureCreates = () => {
    const bodies: Record<string, unknown>[] = [];
    server.use(
      http.post('*/api/data/v9.2/annotations', async ({ request }) => {
        bodies.push((await request.json()) as Record<string, unknown>);
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': 'http://x/api/data/v9.2/annotations(new1)' } });
      }),
    );
    return bodies;
  };

  it('quick_entry_creates_note_bound_to_record', async () => {
    const seen = renderTimeline({ notes: [KICKOFF] }, { privileges: NOTE_CREATE });
    const bodies = captureCreates();
    await waitFor(() => expect(titles()).toHaveLength(1));
    const before = pageReads(seen, 'annotations').length;
    await userEvent.click(await screen.findByPlaceholderText('Take a note...'));
    await userEvent.type(screen.getByRole('textbox', { name: 'Title' }), 'Call notes');
    await userEvent.type(screen.getByRole('textbox', { name: 'Note text' }), 'Agreed on scope');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(bodies).toEqual([{ subject: 'Call notes', notetext: 'Agreed on scope', 'objectid_xv_project@odata.bind': `/xv_projects(${RECORD_ID})` }]));
    await waitFor(() => expect(pageReads(seen, 'annotations').length).toBeGreaterThan(before));
    // The entry closes and clears.
    await waitFor(() => expect(entryField()).toBeInTheDocument());
  });

  it('create_menu_lists_note_first_and_opens_the_entry', async () => {
    renderTimeline({ notes: [KICKOFF] }, { privileges: [...NOTE_CREATE, 'prvCreateActivity'] });
    await waitFor(() => expect(titles()).toHaveLength(1));
    await userEvent.click(screen.getByRole('button', { name: 'Create a timeline record' }));
    expect((await screen.findAllByRole('menuitem')).map((e) => e.textContent)).toEqual(['Note', 'Email', 'Task']);
    await userEvent.click(screen.getByRole('menuitem', { name: 'Note' }));
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Title' })).toHaveFocus());
  });

  it('entry_stays_closed_after_cancel_following_the_menu', async () => {
    renderTimeline({ notes: [KICKOFF] }, { privileges: NOTE_CREATE });
    await waitFor(() => expect(titles()).toHaveLength(1));
    await userEvent.click(screen.getByRole('button', { name: 'Create a timeline record' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Note' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }));
    expect(await screen.findByPlaceholderText('Take a note...')).toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByRole('textbox', { name: 'Title' })).toBeNull();
    expect(entryField()).toBeInTheDocument();
  });

  it('entry_stays_closed_after_save_following_the_menu', async () => {
    renderTimeline({ notes: [KICKOFF] }, { privileges: NOTE_CREATE });
    const bodies = captureCreates();
    await waitFor(() => expect(titles()).toHaveLength(1));
    await userEvent.click(screen.getByRole('button', { name: 'Create a timeline record' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Note' }));
    await userEvent.type(await screen.findByRole('textbox', { name: 'Title' }), 'Quick');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(await screen.findByPlaceholderText('Take a note...')).toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByRole('textbox', { name: 'Title' })).toBeNull();
  });

  it('note_with_file_sends_documentbody', async () => {
    renderTimeline({ notes: [KICKOFF] }, { privileges: NOTE_CREATE });
    const bodies = captureCreates();
    await userEvent.click(await screen.findByPlaceholderText('Take a note...'));
    await userEvent.type(screen.getByRole('textbox', { name: 'Title' }), 'Contract');
    await userEvent.upload(screen.getByLabelText('Choose a file to attach'), new File(['hi'], 'a.txt', { type: 'text/plain' }));
    expect(await screen.findByText('a.txt (2 B)')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toMatchObject({ subject: 'Contract', filename: 'a.txt', mimetype: 'text/plain', documentbody: 'aGk=' });
  });

  it('drop_opens_entry_with_file', async () => {
    renderTimeline({ notes: [KICKOFF] }, { privileges: NOTE_CREATE });
    await waitFor(() => expect(titles()).toHaveLength(1));
    const file = new File(['hi'], 'dropped.txt', { type: 'text/plain' });
    fireEvent.drop(screen.getByTestId('timeline'), { dataTransfer: { files: [file], types: ['Files'] } });
    expect(await screen.findByRole('textbox', { name: 'Title' })).toBeInTheDocument();
    expect(screen.getByText('dropped.txt (2 B)')).toBeInTheDocument();
  });

  it('drop_of_a_too_large_file_is_refused_without_a_request', async () => {
    renderTimeline({ notes: [KICKOFF] }, { privileges: NOTE_CREATE });
    const bodies = captureCreates();
    server.use(http.get('*/api/data/v9.2/organizations', () => HttpResponse.json({ value: [{ organizationid: 'o1', maxuploadfilesize: 1048576 }] })));
    await waitFor(() => expect(titles()).toHaveLength(1));
    const big = new File([new Uint8Array(2 * 1024 * 1024)], 'big.bin');
    fireEvent.drop(screen.getByTestId('timeline'), { dataTransfer: { files: [big], types: ['Files'] } });
    expect(await screen.findByText('big.bin is larger than the 1 MB limit for attachments.')).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'Title' })).toBeNull();
    expect(bodies).toHaveLength(0);
  });

  it('edit_note_patches_subject_and_text', async () => {
    const sent: unknown[] = [];
    const seen = renderTimeline({ notes: [KICKOFF] }, { privileges: NOTE_ALL });
    server.use(
      onPath('patch', 'annotations(n1)', async ({ request }) => {
        sent.push(await request.json());
        return new HttpResponse(null, { status: 204 });
      }),
    );
    await waitFor(() => expect(titles()).toHaveLength(1));
    const before = pageReads(seen, 'annotations').length;
    await userEvent.click(screen.getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog', { name: 'Edit note' });
    const subject = within(dialog).getByRole('textbox', { name: 'Title' });
    expect(subject).toHaveValue('Kickoff minutes');
    await userEvent.clear(subject);
    await userEvent.type(subject, 'Kickoff v2');
    await userEvent.clear(within(dialog).getByRole('textbox', { name: 'Note text' }));
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Note text' }), 'New agenda');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(sent).toEqual([{ subject: 'Kickoff v2', notetext: 'New agenda' }]));
    await waitFor(() => expect(pageReads(seen, 'annotations').length).toBeGreaterThan(before));
  });

  it('delete_note_then_refreshes', async () => {
    const deleted: string[] = [];
    const seen = renderTimeline({ notes: [KICKOFF] }, { privileges: NOTE_ALL });
    server.use(
      onPath('delete', 'annotations(n1)', ({ request }) => {
        deleted.push(new URL(request.url).pathname);
        return new HttpResponse(null, { status: 204 });
      }),
    );
    await waitFor(() => expect(titles()).toHaveLength(1));
    const before = pageReads(seen, 'annotations').length;
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete this record?' });
    expect(deleted).toHaveLength(0);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(deleted).toHaveLength(1));
    await waitFor(() => expect(pageReads(seen, 'annotations').length).toBeGreaterThan(before));
  });

  it('note_actions_follow_privileges', async () => {
    renderTimeline({ notes: [KICKOFF] }, { privileges: READ_ONLY });
    await waitFor(() => expect(titles()).toHaveLength(1));
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Delete' })).toBeNull();
    // Download needs only the read the timeline already has.
    expect(screen.getByRole('button', { name: 'Download attachment' })).toBeInTheDocument();
  });

  it('download_link_for_note_with_file', async () => {
    const reads: string[] = [];
    renderTimeline({ notes: [KICKOFF, { id: 'n2', subject: 'No file', at: today('08:00') }] }, { privileges: READ_ONLY });
    server.use(
      http.get('*/api/data/v9.2/annotations\\(n1\\)', ({ request }) => {
        reads.push(decodeURIComponent(new URL(request.url).search));
        return HttpResponse.json({ documentbody: 'aGk=', filename: 'plan.txt', mimetype: 'text/plain' });
      }),
    );
    const create = vi.fn(() => 'blob:note');
    Object.assign(URL, { createObjectURL: create, revokeObjectURL: vi.fn() });
    const saved: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      saved.push(this.download);
    });
    await waitFor(() => expect(titles()).toHaveLength(2));
    // Only the note with a file offers it.
    expect(screen.getAllByRole('button', { name: 'Download attachment' })).toHaveLength(1);
    await userEvent.click(screen.getByRole('button', { name: 'Download attachment' }));
    await waitFor(() => expect(saved).toEqual(['plan.txt']));
    expect(reads).toEqual(['?$select=documentbody,filename,mimetype']);
    expect(create).toHaveBeenCalledTimes(1);
  });

  it('no_note_entry_when_table_has_no_notes', async () => {
    renderTimeline({ activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }] }, { ctx: ctxOf({ hasNotes: false }), privileges: [...NOTE_CREATE, 'prvCreateActivity'] });
    await waitFor(() => expect(titles()).toHaveLength(1));
    expect(entryField()).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Create a timeline record' }));
    expect((await screen.findAllByRole('menuitem')).map((e) => e.textContent)).toEqual(['Email', 'Task']);
    fireEvent.drop(screen.getByTestId('timeline'), { dataTransfer: { files: [new File(['x'], 'x.txt')], types: ['Files'] } });
    expect(screen.queryByRole('textbox', { name: 'Title' })).toBeNull();
  });

  it('notes_hidden_when_user_lacks_prvCreateNote', async () => {
    renderTimeline({ notes: [KICKOFF] }, { privileges: [...READ_ONLY, 'prvCreateActivity'] });
    await waitFor(() => expect(titles()).toHaveLength(1));
    expect(entryField()).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Create a timeline record' }));
    expect((await screen.findAllByRole('menuitem')).map((e) => e.textContent)).toEqual(['Email', 'Task']);
  });
});

describe('TimelineControl keyboard and options', () => {
  const rows = () => screen.getAllByRole('listitem');
  const announced = () => within(screen.getByTestId('timeline')).getByRole('status');
  const DATA = {
    activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00'), description: 'Work breakdown' }],
    notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30'), notetext: 'Agenda' }],
  };
  afterEach(() => localStorage.clear());

  it('keyboard_moves_opens_and_expands', async () => {
    renderTimeline(DATA);
    await waitFor(() => expect(titles()).toEqual(['Review WBS', 'Kickoff minutes']));
    // One tab stop for the list: the first item; the others are reached with the arrows.
    expect(rows()[0]).toHaveAttribute('tabindex', '0');
    expect(rows()[1]).toHaveAttribute('tabindex', '-1');
    rows()[0]!.focus();
    await userEvent.keyboard('{ArrowDown}');
    expect(rows()[1]).toHaveFocus();
    expect(rows()[1]).toHaveAttribute('tabindex', '0');
    await userEvent.keyboard('{ArrowDown}');
    expect(rows()[1]).toHaveFocus();
    await userEvent.keyboard(' ');
    expect(await screen.findByText('Agenda')).toBeInTheDocument();
    await userEvent.keyboard(' ');
    await waitFor(() => expect(screen.queryByText('Agenda')).toBeNull());
    await userEvent.keyboard('{ArrowUp}');
    expect(rows()[0]).toHaveFocus();
    await userEvent.keyboard('{Enter}');
    const location = screen.getByTestId('location').textContent!;
    expect(location).toContain('etn=task');
    expect(location).toContain('id=a1');
  });

  it('n_opens_note_entry', async () => {
    renderTimeline(DATA, { privileges: [...READ_ONLY, 'prvCreateNote'] });
    await waitFor(() => expect(titles()).toHaveLength(2));
    rows()[0]!.focus();
    await userEvent.keyboard('n');
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Title' })).toHaveFocus());
    // Typed into the entry, N is a letter like any other.
    await userEvent.keyboard('Next');
    expect(screen.getByRole('textbox', { name: 'Title' })).toHaveValue('Next');
  });

  it('disabled_keeps_open_and_hides_other_actions', async () => {
    renderTimeline({ activities: [{ id: 'a1', subject: 'Offer', type: 'email', at: today('09:00') }] }, { privileges: ALL_TASK, disabled: true });
    await waitFor(() => expect(titles()).toHaveLength(1));
    expect(screen.queryByRole('button', { name: 'Delete' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Assign' })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Open' }));
    expect(screen.getByTestId('location').textContent).toContain('id=a1');
  });

  it('n_does_nothing_when_disabled', async () => {
    renderTimeline(DATA, { privileges: [...READ_ONLY, 'prvCreateNote'], disabled: true });
    await waitFor(() => expect(titles()).toHaveLength(2));
    rows()[0]!.focus();
    await userEvent.keyboard('n');
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByRole('textbox', { name: 'Title' })).toBeNull();
  });

  it('load_more_is_announced', async () => {
    renderTimeline({ notes: notesN(15) }, { ctx: ctxOf({ hasActivities: false }) });
    await waitFor(() => expect(titles()).toHaveLength(10));
    expect(announced()).toHaveAttribute('aria-live', 'polite');
    expect(announced()).toHaveTextContent('');
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(titles()).toHaveLength(15));
    await waitFor(() => expect(announced()).toHaveTextContent('5 more items loaded'));
    await userEvent.click(screen.getByRole('button', { name: /^Mine/ }));
    await waitFor(() => expect(announced()).toHaveTextContent('Timeline filters changed'));
  });

  it('repeated_announcements_each_reach_the_region', async () => {
    renderTimeline({ notes: notesN(30) }, { ctx: ctxOf({ hasActivities: false }) });
    await waitFor(() => expect(titles()).toHaveLength(10));
    const said: string[] = [];
    const region = announced();
    const observer = new MutationObserver(() => said.push(region.textContent ?? ''));
    observer.observe(region, { childList: true, characterData: true, subtree: true });
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(titles()).toHaveLength(20));
    await waitFor(() => expect(region).toHaveTextContent('10 more items loaded'));
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(titles()).toHaveLength(30));
    // The same words a second time: the region is emptied first, so assistive technology hears them again.
    await waitFor(() => expect(said.filter(Boolean)).toEqual(['10 more items loaded', '10 more items loaded']));
    observer.disconnect();
  });

  it('remembered_filters_restore_on_next_open', async () => {
    renderTimeline(DATA, { rememberFilters: true });
    await waitFor(() => expect(titles()).toHaveLength(2));
    await userEvent.click(screen.getByRole('button', { name: /^Notes/ }));
    await waitFor(() => expect(titles()).toEqual(['Kickoff minutes']));
    cleanup();
    const seen = renderTimeline(DATA, { rememberFilters: true });
    await waitFor(() => expect(titles()).toEqual(['Kickoff minutes']));
    expect(screen.getByRole('button', { name: /^Notes/ })).toHaveAttribute('aria-pressed', 'true');
    // The first page read already has the remembered filter: no activity page is read.
    expect(pageReads(seen, 'activitypointers')).toHaveLength(0);
  });

  it('remembered_type_no_longer_shown_is_dropped', async () => {
    localStorage.setItem(`xrmverse.timeline.${ME}.${FORM_ID}.notescontrol`, JSON.stringify({ activityTypes: ['xv_gone', 'task'], sort: 'desc' }));
    const seen = renderTimeline(DATA, { rememberFilters: true });
    await waitFor(() => expect(titles()).toEqual(['Review WBS']));
    const filter = pageReads(seen, 'activitypointers')[0]!.searchParams.get('$filter')!;
    expect(filter).toContain("activitytypecode eq 'task'");
    expect(filter).not.toContain('xv_gone');
  });

  it('option_off_reads_and_writes_nothing', async () => {
    const getItem = vi.spyOn(Storage.prototype, 'getItem');
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    renderTimeline(DATA);
    await waitFor(() => expect(titles()).toHaveLength(2));
    await userEvent.click(screen.getByRole('button', { name: /^Notes/ }));
    await waitFor(() => expect(titles()).toEqual(['Kickoff minutes']));
    const timelineKeys = (calls: unknown[][]) => calls.filter(([key]) => String(key).startsWith('xrmverse.timeline.'));
    expect(timelineKeys(getItem.mock.calls)).toEqual([]);
    expect(timelineKeys(setItem.mock.calls)).toEqual([]);
  });

  const WITH_POSTS = {
    activities: [{ id: 'a1', subject: 'Review WBS', at: today('09:00') }],
    notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }],
    posts: [
      { id: 'p1', text: 'Hello team', at: today('09:30') },
      { id: 'p2', text: '<post><secret>xml</secret></post>', at: today('08:00'), source: 1 },
    ],
  };
  const READ_POSTS = [...READ_ONLY, 'prvReadPost'];
  const postReads = (seen: URL[]) => seen.filter((u) => u.pathname.endsWith('/posts') || u.pathname.endsWith('/postcomments'));

  it('posts_off_runs_no_post_query', async () => {
    const seen = renderTimeline(WITH_POSTS, { privileges: READ_POSTS });
    await waitFor(() => expect(titles()).toEqual(['Review WBS', 'Kickoff minutes']));
    expect(screen.getByRole('button', { name: 'All 2' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Posts/ })).toBeNull();
    expect(postReads(seen)).toHaveLength(0);
  });

  it('posts_hidden_without_read_privilege', async () => {
    const seen = renderTimeline(WITH_POSTS, { posts: true, privileges: READ_ONLY });
    await waitFor(() => expect(titles()).toEqual(['Review WBS', 'Kickoff minutes']));
    expect(screen.queryByRole('button', { name: /^Posts/ })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Filter' }));
    const pane = screen.getByRole('group', { name: 'Timeline filters' });
    expect(within(pane).queryByRole('checkbox', { name: /^Posts/ })).toBeNull();
    expect(within(pane).queryByRole('group', { name: 'Posts by' })).toBeNull();
    expect(postReads(seen)).toHaveLength(0);
  });

  it('auto_post_row_shows_notice', async () => {
    const seen = renderTimeline(WITH_POSTS, { posts: true, privileges: READ_POSTS });
    await waitFor(() => expect(titles()).toEqual(['Hello team', 'Review WBS', 'Kickoff minutes', 'Auto post']));
    const items = screen.getAllByRole('listitem');
    expect(within(items[0]!).getByText(/Post · Ada Author/)).toBeInTheDocument();
    expect(within(items[3]!).getByText('The content of auto posts is not shown in this version')).toBeInTheDocument();
    expect(screen.queryByText(/secret/)).toBeNull();
    // The Posts chip and the Posts by category, with their counts.
    expect(screen.getByRole('button', { name: 'All 4' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Posts 2' }));
    await waitFor(() => expect(titles()).toEqual(['Hello team', 'Auto post']));
    await userEvent.click(screen.getByRole('button', { name: 'Filter (filters applied)' }));
    const pane = screen.getByRole('group', { name: 'Timeline filters' });
    expect(within(pane).getByRole('checkbox', { name: /^Posts/ })).toBeChecked();
    const by = within(pane).getByRole('group', { name: 'Posts by' });
    await userEvent.click(within(by).getByRole('checkbox', { name: 'Users 1' }));
    await waitFor(() => expect(titles()).toEqual(['Hello team']));
    expect(within(by).getByRole('checkbox', { name: 'Auto post 1' })).not.toBeChecked();
    expect(postReads(seen).filter((u) => !u.searchParams.has('$count') && !u.searchParams.has('fetchXml')).at(-1)!.searchParams.get('$filter')).toContain('source eq 2');
  });
});

describe('TimelineControl ActionHub posts', () => {
  it('actionhub_post_row_shows_auto_post_notice_not_text', async () => {
    renderTimeline(
      { posts: [{ id: 'p3', text: 'secret hub text', at: today('08:00'), source: 3 }] },
      { posts: true, privileges: [...READ_ONLY, 'prvReadPost'] },
    );
    await waitFor(() => expect(titles()).toEqual(['Auto post']));
    expect(screen.getByText('The content of auto posts is not shown in this version')).toBeInTheDocument();
    expect(screen.queryByText(/secret hub text/)).toBeNull();
  });
});

describe('TimelineControl writing posts', () => {
  const OTHER = 'bbbbbbbb-0000-0000-0000-000000000002';
  const POST_ALL = [...READ_ONLY, 'prvReadPost', 'prvCreatePost', 'prvDeletePost'];
  const postRows = () => screen.getAllByRole('listitem').filter((li) => li.hasAttribute('data-timeline-item'));
  const rowOf = (title: string) => postRows().find((li) => within(li).queryByTestId('timeline-item-title')?.textContent === title)!;
  const created = (set: string, onCreate: (body: Record<string, unknown>) => void, id = 'new1') =>
    server.use(
      http.post(`*/api/data/v9.2/${set}`, async ({ request }) => {
        onCreate((await request.json()) as Record<string, unknown>);
        return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x/api/data/v9.2/${set}(${id})` } });
      }),
    );

  it('post_entry_creates_post_and_refreshes', async () => {
    const posts: PostRow[] = [{ id: 'p1', text: 'Hello team', at: today('09:30') }];
    const bodies: Record<string, unknown>[] = [];
    renderTimeline({ posts }, { posts: true, privileges: POST_ALL });
    created('posts', (b) => {
      bodies.push(b);
      posts.push({ id: 'p9', text: String(b.text), at: NOW.toISOString() });
    }, 'p9');
    await waitFor(() => expect(titles()).toEqual(['Hello team']));
    await userEvent.click(screen.getByRole('button', { name: 'Create a timeline record' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Post' }));
    const field = await screen.findByRole('textbox', { name: 'Post text' });
    await waitFor(() => expect(field).toHaveFocus());
    expect(screen.getByText('0 / 2000')).toBeInTheDocument();
    await userEvent.type(field, 'Release is out');
    await userEvent.keyboard('{Control>}{Enter}{/Control}');
    await waitFor(() => expect(bodies).toEqual([{ text: 'Release is out', 'regardingobjectid_xv_project@odata.bind': `/xv_projects(${RECORD_ID})` }]));
    await waitFor(() => expect(titles()).toEqual(['Release is out', 'Hello team']));
    // The entry closes.
    await waitFor(() => expect(screen.queryByRole('textbox', { name: 'Post text' })).toBeNull());
  });

  it('post_entry_shows_server_error_and_keeps_text', async () => {
    renderTimeline({ posts: [{ id: 'p1', text: 'Hello team', at: today('09:30') }] }, { posts: true, privileges: POST_ALL });
    server.use(http.post('*/api/data/v9.2/posts', () => HttpResponse.json({ error: { code: '0x80040220', message: 'Post refused' } }, { status: 403 })));
    await waitFor(() => expect(titles()).toHaveLength(1));
    await userEvent.click(screen.getByRole('button', { name: 'Create a timeline record' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Post' }));
    await userEvent.type(await screen.findByRole('textbox', { name: 'Post text' }), 'Nope');
    await userEvent.click(screen.getByRole('button', { name: 'Post' }));
    expect(await screen.findByText(/Post refused/)).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Post text' })).toHaveValue('Nope');
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('textbox', { name: 'Post text' })).toBeNull();
  });

  it('reply_creates_comment', async () => {
    const comments: CommentRow[] = [{ id: 'c1', postId: 'p1', text: 'First reply', at: today('09:40'), by: OTHER }];
    const bodies: Record<string, unknown>[] = [];
    renderTimeline({ posts: [{ id: 'p1', text: 'Hello team', at: today('09:30') }], comments }, { posts: true, privileges: POST_ALL });
    created('postcomments', (b) => {
      bodies.push(b);
      comments.push({ id: 'c9', postId: 'p1', text: String(b.text), at: NOW.toISOString() });
    }, 'c9');
    await waitFor(() => expect(titles()).toEqual(['Hello team']));
    const row = rowOf('Hello team');
    expect(within(row).getByText('1 reply')).toBeInTheDocument();
    await userEvent.click(within(row).getByRole('button', { name: 'Show details' }));
    const replies = await within(row).findByRole('list', { name: 'Replies' });
    expect(within(replies).getByText('First reply')).toBeInTheDocument();
    await userEvent.click(within(row).getByRole('button', { name: 'Reply' }));
    const field = within(row).getByRole('textbox', { name: 'Reply text' });
    expect(within(row).getByText('0 / 1000')).toBeInTheDocument();
    await userEvent.type(field, 'Second');
    await userEvent.click(within(row).getByRole('button', { name: 'Reply' }));
    await waitFor(() => expect(bodies).toEqual([{ text: 'Second', 'postid@odata.bind': '/posts(p1)' }]));
    await waitFor(() => expect(within(row).getByText('2 replies')).toBeInTheDocument());
    expect(within(within(row).getByRole('list', { name: 'Replies' })).getAllByRole('listitem').map((li) => li.textContent)).toEqual([
      expect.stringContaining('First reply'),
      expect.stringContaining('Second'),
    ]);
    expect(within(row).queryByRole('textbox', { name: 'Reply text' })).toBeNull();
  });

  it('delete_only_on_own_items', async () => {
    const deleted: string[] = [];
    const comments: CommentRow[] = [
      { id: 'c1', postId: 'p1', text: 'My reply', at: today('09:40') },
      { id: 'c2', postId: 'p1', text: 'Their reply', at: today('09:45'), by: OTHER },
    ];
    const seen = renderTimeline(
      {
        posts: [
          { id: 'p1', text: 'Mine post', at: today('09:30') },
          { id: 'p2', text: 'Their post', at: today('09:00'), by: OTHER },
        ],
        comments,
      },
      { posts: true, privileges: POST_ALL },
    );
    server.use(
      http.delete('*/api/data/v9.2/:target', ({ params }) => {
        const target = String(params.target);
        deleted.push(target);
        const c = /^postcomments\((\w+)\)$/.exec(target)?.[1];
        if (c) comments.splice(comments.findIndex((x) => x.id === c), 1);
        return new HttpResponse(null, { status: 204 });
      }),
    );
    await waitFor(() => expect(titles()).toEqual(['Mine post', 'Their post']));
    expect(within(rowOf('Their post')).queryByRole('button', { name: 'Delete' })).toBeNull();
    const mine = rowOf('Mine post');
    // The reply that is not the user's has no Delete.
    await userEvent.click(within(mine).getByRole('button', { name: 'Show details' }));
    const replies = await within(mine).findByRole('list', { name: 'Replies' });
    const [myReply, theirReply] = within(replies).getAllByRole('listitem');
    expect(within(theirReply!).queryByRole('button', { name: 'Delete' })).toBeNull();
    await userEvent.click(within(myReply!).getByRole('button', { name: 'Delete' }));
    const replyDialog = await screen.findByRole('dialog', { name: 'Delete this reply?' });
    expect(deleted).toHaveLength(0);
    await userEvent.click(within(replyDialog).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(deleted).toEqual(['postcomments(c1)']));
    await waitFor(() => expect(within(mine).queryByText('My reply')).toBeNull());
    expect(within(mine).getByText('1 reply')).toBeInTheDocument();
    // The post itself.
    const before = postReadsOf(seen).length;
    await userEvent.click(within(within(mine).getByRole('toolbar')).getByRole('button', { name: 'Delete' }));
    const postDialog = await screen.findByRole('dialog', { name: 'Delete this post?' });
    await userEvent.click(within(postDialog).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(deleted).toEqual(['postcomments(c1)', 'posts(p1)']));
    await waitFor(() => expect(postReadsOf(seen).length).toBeGreaterThan(before));
  });

  it('delete_hidden_without_delete_privilege', async () => {
    renderTimeline(
      { posts: [{ id: 'p1', text: 'Mine post', at: today('09:30') }], comments: [{ id: 'c1', postId: 'p1', text: 'My reply', at: today('09:40') }] },
      { posts: true, privileges: POST_ALL.filter((p) => p !== 'prvDeletePost') },
    );
    await waitFor(() => expect(titles()).toEqual(['Mine post']));
    const row = rowOf('Mine post');
    expect(within(row).queryByRole('button', { name: 'Delete' })).toBeNull();
    await userEvent.click(within(row).getByRole('button', { name: 'Show details' }));
    expect(await within(row).findByText('My reply')).toBeInTheDocument();
    // Reply needs only prvCreatePost.
    expect(within(row).getByRole('button', { name: 'Reply' })).toBeInTheDocument();
    expect(within(row).queryByRole('button', { name: 'Delete' })).toBeNull();
  });

  it('disabled_hides_post_actions', async () => {
    renderTimeline(
      { posts: [{ id: 'p1', text: 'Mine post', at: today('09:30') }], comments: [{ id: 'c1', postId: 'p1', text: 'My reply', at: today('09:40') }] },
      { posts: true, privileges: POST_ALL, disabled: true },
    );
    await waitFor(() => expect(titles()).toEqual(['Mine post']));
    expect(screen.queryByRole('button', { name: 'Create a timeline record' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Delete' })).toBeNull();
    const row = rowOf('Mine post');
    await userEvent.click(within(row).getByRole('button', { name: 'Show details' }));
    // The replies can still be read.
    expect(await within(row).findByText('My reply')).toBeInTheDocument();
    expect(within(row).queryByRole('button', { name: 'Reply' })).toBeNull();
    expect(within(row).queryByRole('button', { name: 'Delete' })).toBeNull();
  });

  it('mention_renders_as_user_link', async () => {
    const BEA = 'cccccccc-0000-0000-0000-000000000003';
    renderTimeline({ posts: [{ id: 'p1', text: `Thanks @[8,{${BEA}},"Bea Boss"] and @[account,{a1},"Contoso"] for @[broken`, at: today('09:30') }] }, { posts: true, privileges: POST_ALL });
    await waitFor(() => expect(titles()).toEqual(['Thanks Bea Boss and Contoso for @[broken']));
    expect(screen.queryByRole('link', { name: 'Contoso' })).toBeNull();
    await userEvent.click(screen.getByRole('link', { name: 'Bea Boss' }));
    const location = screen.getByTestId('location').textContent!;
    expect(location).toContain('pagetype=entityrecord');
    expect(location).toContain('etn=systemuser');
    expect(location).toContain(`id=${BEA}`);
  });

  it('plus_menu_has_post_only_when_posts_on', async () => {
    const menu = async () => {
      await waitFor(() => expect(titles().length).toBeGreaterThan(0));
      await userEvent.click(screen.getByRole('button', { name: 'Create a timeline record' }));
      const items = (await screen.findAllByRole('menuitem')).map((e) => e.textContent);
      cleanup();
      return items;
    };
    const DATA = { notes: [{ id: 'n1', subject: 'Kickoff minutes', at: today('08:30') }], posts: [{ id: 'p1', text: 'Hello team', at: today('09:30') }] };
    const WITH_NOTE = [...POST_ALL, 'prvCreateNote'];
    renderTimeline(DATA, { posts: false, privileges: WITH_NOTE });
    expect(await menu()).toEqual(['Note']);
    renderTimeline(DATA, { posts: true, privileges: WITH_NOTE });
    expect(await menu()).toEqual(['Note', 'Post']);
    renderTimeline(DATA, { posts: true, privileges: WITH_NOTE.filter((p) => p !== 'prvCreatePost') });
    expect(await menu()).toEqual(['Note']);
  });
});

/** The post page reads (not the counts). */
const postReadsOf = (seen: URL[]) => seen.filter((u) => u.pathname.endsWith('/posts') && !u.searchParams.has('$count'));

describe('TimelineControl appearance (the form overlay)', () => {
  const data = { notes: [{ id: 'n1', subject: 'Kickoff', at: today('08:30') }, { id: 'n2', subject: 'Plan', at: today('08:00') }] };

  it('cards_keep_keyboard_navigation_and_expanding', async () => {
    const withText = { notes: [{ id: 'n1', subject: 'Kickoff', at: today('08:30'), notetext: 'Agenda' }, { id: 'n2', subject: 'Plan', at: today('08:00') }] };
    renderTimeline(withText, { timelineAppearance: { appearance: 'cards', density: 'comfortable' } });
    await waitFor(() => expect(titles()).toEqual(['Kickoff', 'Plan']));
    const rows = () => screen.getAllByRole('listitem');
    expect(rows()[0]).toHaveAttribute('tabindex', '0');
    rows()[0]!.focus();
    await userEvent.keyboard(' ');
    expect(await screen.findByText('Agenda')).toBeInTheDocument();
    await userEvent.keyboard('{ArrowDown}');
    expect(rows()[1]).toHaveFocus();
  });

  it('cards_draw_each_item_in_a_card', async () => {
    renderTimeline(data, { timelineAppearance: { appearance: 'cards', density: 'comfortable' } });
    await waitFor(() => expect(titles()).toEqual(['Kickoff', 'Plan']));
    const items = screen.getAllByRole('listitem');
    for (const item of items) expect(item.querySelector('[data-appearance="cards"]')).not.toBeNull();
    expect(document.querySelectorAll('li [data-appearance="cards"]')).toHaveLength(2);
    expect(screen.getByRole('list', { name: 'Timeline items' })).toHaveAttribute('data-appearance', 'cards');
  });

  it('compact_marks_the_list_and_keeps_items_collapsed', async () => {
    renderTimeline(data, { timelineAppearance: { appearance: 'list', density: 'compact' } });
    await waitFor(() => expect(titles()).toHaveLength(2));
    expect(screen.getByRole('list', { name: 'Timeline items' })).toHaveAttribute('data-density', 'compact');
    for (const b of screen.getAllByRole('button', { name: 'Show details' })) expect(b).toHaveAttribute('aria-expanded', 'false');
    expect(document.querySelector('[data-appearance="cards"]')).toBeNull();
  });

  it('without_an_appearance_the_rendering_is_unchanged', async () => {
    renderTimeline(data);
    await waitFor(() => expect(titles()).toHaveLength(2));
    const list = screen.getByRole('list', { name: 'Timeline items' });
    expect(list).not.toHaveAttribute('data-appearance');
    expect(list).not.toHaveAttribute('data-density');
    expect(document.querySelector('[data-appearance="cards"]')).toBeNull();
  });

  it('list_comfortable_is_the_classic_look', async () => {
    renderTimeline(data, { timelineAppearance: { appearance: 'list', density: 'comfortable' } });
    await waitFor(() => expect(titles()).toHaveLength(2));
    expect(document.querySelector('[data-appearance="cards"]')).toBeNull();
  });
});
