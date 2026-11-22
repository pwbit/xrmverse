import { afterEach, describe, expect, it, vi } from 'vitest';
import { EMPTY_FILTERS, type TimelineFilters } from '../../../../apps/record/timeline/filters';
import { readRemembered, writeRemembered } from './rememberedFilters';

const USER = 'aaaaaaaa-0000-0000-0000-000000000001';
const FORM = 'ffffffff-0000-0000-0000-000000000002';

afterEach(() => {
  vi.restoreAllMocks();
  localStorage.clear();
});

describe('rememberedFilters', () => {
  it('round_trip', () => {
    const filters: TimelineFilters = {
      ...EMPTY_FILTERS,
      recordTypes: ['activity', 'post'],
      activityTypes: ['task'],
      statuses: ['overdue'],
      statusReasons: [{ type: 'task', value: 5 }],
      due: ['next7d'],
      modified: ['last30d'],
      mine: true,
      postsBy: ['users'],
      search: 'kickoff',
      sort: 'asc',
    };
    expect(readRemembered(USER, FORM, 'notescontrol')).toBeUndefined();
    writeRemembered(USER, FORM, 'notescontrol', filters);
    expect(localStorage.getItem(`xrmverse.timeline.${USER}.${FORM}.notescontrol`)).not.toBeNull();
    // The search is not remembered (spec §5.8: chips, pane filters and sort order).
    expect(readRemembered(USER, FORM, 'notescontrol')).toEqual({ ...filters, search: '' });
    // Per user, form and control.
    expect(readRemembered(USER, FORM, 'other')).toBeUndefined();
    expect(readRemembered('someone-else', FORM, 'notescontrol')).toBeUndefined();
  });

  it('storage_throwing_is_not_remembered', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('quota');
    });
    expect(() => writeRemembered(USER, FORM, 'notescontrol', EMPTY_FILTERS)).not.toThrow();
    expect(readRemembered(USER, FORM, 'notescontrol')).toBeUndefined();
  });

  it('unreadable_value_is_not_remembered', () => {
    localStorage.setItem(`xrmverse.timeline.${USER}.${FORM}.notescontrol`, '{not json');
    expect(readRemembered(USER, FORM, 'notescontrol')).toBeUndefined();
    localStorage.setItem(`xrmverse.timeline.${USER}.${FORM}.notescontrol`, JSON.stringify({ recordTypes: 'note', mine: 'yes', sort: 'up' }));
    expect(readRemembered(USER, FORM, 'notescontrol')).toEqual(EMPTY_FILTERS);
    // Values a filter does not know are left out.
    localStorage.setItem(
      `xrmverse.timeline.${USER}.${FORM}.notescontrol`,
      JSON.stringify({ recordTypes: ['note', 'email'], postsBy: ['bots', 'auto'], statuses: ['late', 'closed'], due: ['tomorrow'], modified: ['last7d', 'ever'] }),
    );
    expect(readRemembered(USER, FORM, 'notescontrol')).toEqual({ ...EMPTY_FILTERS, recordTypes: ['note'], postsBy: ['auto'], statuses: ['closed'], modified: ['last7d'] });
  });
});
