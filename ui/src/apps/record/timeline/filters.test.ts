import { describe, expect, it } from 'vitest';
import {
  EMPTY_FILTERS,
  activityFilter,
  activityOnlyFilterSet,
  chipSelected,
  defaultShownTypes,
  noteFilter,
  postFilter,
  queriesActivities,
  toggleChip,
  type TimelineFilters,
  type TimelineScope,
} from './filters';

const RECORD = 'cccccccc-0000-0000-0000-000000000003';
const ME = 'aaaaaaaa-0000-0000-0000-000000000001';
const NOW = new Date('2026-11-16T10:00:00.000Z');
const scope: TimelineScope = { recordId: RECORD, userId: ME, shownTypes: ['task', 'email'], hasActivities: true, hasNotes: true, hasPosts: true };
const f = (patch: Partial<TimelineFilters>): TimelineFilters => ({ ...EMPTY_FILTERS, ...patch });

describe('timeline filters', () => {
  it('chip_and_pane_are_the_same_state', () => {
    expect(chipSelected(EMPTY_FILTERS, 'all')).toBe(true);
    // Selecting a chip sets the pane's filter...
    const notes = toggleChip(EMPTY_FILTERS, 'notes');
    expect(notes.recordTypes).toEqual(['note']);
    expect(chipSelected(notes, 'all')).toBe(false);
    // ...and setting the pane's filter selects the chip.
    expect(chipSelected(f({ recordTypes: ['note'] }), 'notes')).toBe(true);
    expect(chipSelected(f({ statuses: ['active', 'overdue'] }), 'overdue')).toBe(true);
    expect(toggleChip(f({ statuses: ['active', 'overdue'] }), 'overdue').statuses).toEqual(['active']);
    expect(chipSelected(toggleChip(EMPTY_FILTERS, 'mine'), 'mine')).toBe(true);
    expect(toggleChip(f({ mine: true }), 'mine').mine).toBe(false);
    // All clears the record type.
    expect(toggleChip(f({ recordTypes: ['activity'] }), 'all').recordTypes).toEqual([]);
    expect(toggleChip(notes, 'notes')).toEqual(EMPTY_FILTERS);
  });

  it('activity_only_filters_hide_notes', () => {
    expect(activityOnlyFilterSet(EMPTY_FILTERS)).toBe(false);
    expect(noteFilter(EMPTY_FILTERS, scope, NOW)).toBe(`_objectid_value eq ${RECORD}`);
    for (const patch of [
      { activityTypes: ['task'] },
      { statuses: ['closed'] },
      { statusReasons: [{ type: 'task', value: 5 }] },
      { due: ['next7d'] },
    ] satisfies Partial<TimelineFilters>[]) {
      expect(activityOnlyFilterSet(f(patch))).toBe(true);
      expect(noteFilter(f(patch), scope, NOW)).toBeUndefined();
    }
    // Modified date, owner and search apply to notes too.
    expect(noteFilter(f({ modified: ['last7d'], mine: true, search: 'x' }), scope, NOW)).toBe(
      `_objectid_value eq ${RECORD} and modifiedon ge 2026-11-09T10:00:00.000Z and _ownerid_value eq ${ME}` +
        ` and (contains(subject,'x') or contains(notetext,'x'))`,
    );
    expect(noteFilter(f({ recordTypes: ['activity'] }), scope, NOW)).toBeUndefined();
    expect(noteFilter(EMPTY_FILTERS, { ...scope, hasNotes: false }, NOW)).toBeUndefined();
  });

  it('mine_filters_owner', () => {
    expect(activityFilter(f({ mine: true }), scope, NOW)).toContain(` and _ownerid_value eq ${ME}`);
    expect(activityFilter(EMPTY_FILTERS, scope, NOW)).not.toContain('_ownerid_value');
  });

  it('search_escapes_quotes', () => {
    expect(activityFilter(f({ search: "O'Brien" }), scope, NOW)).toContain(
      `(contains(subject,'O''Brien') or contains(description,'O''Brien'))`,
    );
    expect(activityFilter(f({ search: '   ' }), scope, NOW)).not.toContain('contains');
  });

  it('overdue_means_active_and_past_scheduledend', () => {
    expect(activityFilter(f({ statuses: ['overdue'] }), scope, NOW)).toBe(
      `_regardingobjectid_value eq ${RECORD} and (activitytypecode eq 'task' or activitytypecode eq 'email')` +
        ` and (statecode eq 0 or statecode eq 3) and scheduledend lt 2026-11-16T10:00:00.000Z`,
    );
    // Within a category OR, across categories AND.
    expect(activityFilter(f({ statuses: ['overdue', 'closed'], activityTypes: ['task'] }), scope, NOW)).toBe(
      `_regardingobjectid_value eq ${RECORD} and activitytypecode eq 'task'` +
        ` and (((statecode eq 0 or statecode eq 3) and scheduledend lt 2026-11-16T10:00:00.000Z) or (statecode eq 1 or statecode eq 2))`,
    );
  });

  it('notes_only_table_sends_no_activity_filter', () => {
    const notesOnly = { ...scope, hasActivities: false };
    expect(queriesActivities(EMPTY_FILTERS, notesOnly)).toBe(false);
    expect(queriesActivities(f({ recordTypes: ['note'] }), scope)).toBe(false);
    expect(queriesActivities(EMPTY_FILTERS, scope)).toBe(true);
    expect(noteFilter(EMPTY_FILTERS, notesOnly, NOW)).toBe(`_objectid_value eq ${RECORD}`);
  });

  it('fax_and_letter_not_in_shown_types_by_default', () => {
    expect(defaultShownTypes(['task', 'fax', 'email', 'letter', 'op365_call'])).toEqual(['task', 'email', 'op365_call']);
  });

  it('posts_filter_and_posts_by', () => {
    expect(postFilter(EMPTY_FILTERS, scope, NOW)).toBe(`_regardingobjectid_value eq ${RECORD}`);
    // The Posts chip and the pane option are one state.
    const posts = toggleChip(EMPTY_FILTERS, 'posts');
    expect(posts.recordTypes).toEqual(['post']);
    expect(chipSelected(posts, 'posts')).toBe(true);
    expect(chipSelected(posts, 'all')).toBe(false);
    // Posts alone: no activity and no note query.
    expect(queriesActivities(posts, scope)).toBe(false);
    expect(noteFilter(posts, scope, NOW)).toBeUndefined();
    expect(postFilter(posts, scope, NOW)).toBe(`_regardingobjectid_value eq ${RECORD}`);
    // Posts by: auto is source 1, users source 2; an option of either hides activities and notes (a post-only filter).
    expect(postFilter(f({ postsBy: ['users'] }), scope, NOW)).toBe(`_regardingobjectid_value eq ${RECORD} and source eq 2`);
    expect(postFilter(f({ postsBy: ['auto', 'users'] }), scope, NOW)).toBe(`_regardingobjectid_value eq ${RECORD} and (source eq 1 or source eq 2)`);
    expect(queriesActivities(f({ postsBy: ['users'] }), scope)).toBe(false);
    expect(noteFilter(f({ postsBy: ['auto'] }), scope, NOW)).toBeUndefined();
    // Mine is the creator, search the text, the modified date the creation date (posts cannot be updated).
    expect(postFilter(f({ mine: true, search: "O'Brien", modified: ['last24h'] }), scope, NOW)).toBe(
      `_regardingobjectid_value eq ${RECORD} and createdon ge 2026-11-15T10:00:00.000Z and _createdby_value eq ${ME}` +
        ` and contains(text,'O''Brien')`,
    );
    // Mine also matches the posts of the record the user commented on (read beforehand).
    expect(postFilter(f({ mine: true }), scope, NOW, ['p1', 'p2'])).toBe(
      `_regardingobjectid_value eq ${RECORD} and (_createdby_value eq ${ME} or postid eq p1 or postid eq p2)`,
    );
    // Other record types, or posts off: no post query.
    expect(postFilter(f({ recordTypes: ['note'] }), scope, NOW)).toBeUndefined();
    expect(postFilter(EMPTY_FILTERS, { ...scope, hasPosts: false }, NOW)).toBeUndefined();
  });

  it('activity_only_filter_hides_posts', () => {
    for (const patch of [
      { activityTypes: ['task'] },
      { statuses: ['overdue'] },
      { statusReasons: [{ type: 'task', value: 5 }] },
      { due: ['last7d'] },
    ] satisfies Partial<TimelineFilters>[]) {
      expect(postFilter(f(patch), scope, NOW)).toBeUndefined();
    }
    expect(postFilter(f({ recordTypes: ['post', 'note'] }), scope, NOW)).toBeDefined();
  });
});
