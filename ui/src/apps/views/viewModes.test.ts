import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ColumnKind } from './columnKinds';
import { BOARD_LIMIT, resolveModes } from './viewModes';

const PICKLIST: ColumnKind = { kind: 'choice', type: 'Picklist', options: [{ value: 1, label: 'Hot' }] };
const KINDS: Record<string, ColumnKind> = {
  industrycode: PICKLIST,
  description: { kind: 'other' },
  scheduledstart: { kind: 'date', dateOnly: false, zoneIndependent: false },
  endtime: { kind: 'date', dateOnly: false, zoneIndependent: false },
  birthday: { kind: 'date', dateOnly: true, zoneIndependent: false },
  wallclock: { kind: 'date', dateOnly: false, zoneIndependent: true },
};

describe('resolveModes', () => {
  afterEach(() => vi.restoreAllMocks());

  it('board_limit_is_250', () => {
    expect(BOARD_LIMIT).toBe(250);
  });

  it('kanban_on_a_multi_select_is_dropped', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const multi: ColumnKind = { kind: 'choice', type: 'MultiSelectPicklist', options: [{ value: 1, label: 'A' }] };
    const resolved = resolveModes({ kanban: { groupBy: 'tags' } }, { tags: multi });
    expect(resolved.modes).toEqual(['grid']);
    expect(resolved.kanban).toBeUndefined();
    expect(warn).toHaveBeenCalledTimes(1);
  });

  it('no_overlay_gives_the_grid_only', () => {
    expect(resolveModes(undefined, KINDS)).toEqual({ modes: ['grid'], initial: 'grid' });
  });

  it('kanban_on_a_picklist_is_kept', () => {
    const resolved = resolveModes({ default: 'kanban', kanban: { groupBy: 'industrycode' } }, KINDS);
    expect(resolved).toEqual({ modes: ['grid', 'kanban'], initial: 'kanban', kanban: { groupBy: 'industrycode', column: PICKLIST } });
  });

  it.each(['description', 'missing'])('kanban_on_%s_is_dropped_with_one_warning', (groupBy) => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const resolved = resolveModes({ default: 'kanban', kanban: { groupBy } }, KINDS);
    expect(resolved).toEqual({ modes: ['grid'], initial: 'grid' });
    expect(warn).toHaveBeenCalledTimes(1);
    expect(warn.mock.calls[0][0]).toBe('[xrmverse presentation]');
  });

  it('calendar_with_a_date_start_is_kept', () => {
    const resolved = resolveModes({ calendar: { start: 'birthday' } }, KINDS);
    expect(resolved).toEqual({ modes: ['grid', 'calendar'], initial: 'grid', calendar: { start: 'birthday', dateOnly: true, zoneIndependent: false, layout: 'month' } });
  });

  it('calendar_end_not_a_date_keeps_the_calendar_without_end_and_warns', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const resolved = resolveModes({ default: 'calendar', calendar: { start: 'scheduledstart', end: 'description' } }, KINDS);
    expect(resolved).toEqual({ modes: ['grid', 'calendar'], initial: 'calendar', calendar: { start: 'scheduledstart', dateOnly: false, zoneIndependent: false, layout: 'month' } });
    expect(warn).toHaveBeenCalledTimes(1);
    expect(warn.mock.calls[0][0]).toBe('[xrmverse presentation]');
  });

  it('calendar_with_a_date_end_of_the_same_kind_keeps_it', () => {
    const resolved = resolveModes({ calendar: { start: 'scheduledstart', end: 'endtime' } }, KINDS);
    expect(resolved.calendar).toEqual({ start: 'scheduledstart', end: 'endtime', dateOnly: false, zoneIndependent: false, layout: 'month' });
  });

  it.each(['birthday', 'wallclock'])('calendar_end_%s_of_another_kind_than_the_start_is_dropped_with_a_warning', (end) => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const resolved = resolveModes({ calendar: { start: 'scheduledstart', end } }, KINDS);
    expect(resolved.modes).toEqual(['grid', 'calendar']);
    expect(resolved.calendar).toEqual({ start: 'scheduledstart', dateOnly: false, zoneIndependent: false, layout: 'month' });
    expect(warn).toHaveBeenCalledTimes(1);
    expect(warn.mock.calls[0][0]).toBe('[xrmverse presentation]');
  });

  it('calendar_layout_defaults_to_month_and_carries_timeline', () => {
    expect(resolveModes({ calendar: { start: 'birthday' } }, KINDS).calendar?.layout).toBe('month');
    expect(resolveModes({ calendar: { start: 'birthday', layout: 'timeline' } }, KINDS).calendar?.layout).toBe('timeline');
    expect(resolveModes({ calendar: { start: 'scheduledstart', end: 'endtime', layout: 'timeline' } }, KINDS).calendar).toEqual({
      start: 'scheduledstart',
      end: 'endtime',
      dateOnly: false,
      zoneIndependent: false,
      layout: 'timeline',
    });
  });

  it('calendar_start_time_zone_independent_is_carried', () => {
    expect(resolveModes({ calendar: { start: 'wallclock' } }, KINDS).calendar).toEqual({ start: 'wallclock', dateOnly: false, zoneIndependent: true, layout: 'month' });
  });

  it('calendar_start_not_a_date_is_dropped_with_a_warning', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    expect(resolveModes({ default: 'calendar', calendar: { start: 'description' } }, KINDS)).toEqual({ modes: ['grid'], initial: 'grid' });
    expect(warn).toHaveBeenCalledTimes(1);
  });

  it('failed_kinds_drop_the_modes_without_a_warning_per_mode', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const resolved = resolveModes({ default: 'kanban', kanban: { groupBy: 'industrycode' }, calendar: { start: 'birthday' }, cards: {} }, {}, true);
    expect(resolved.modes).toEqual(['grid', 'cards']);
    expect(warn).not.toHaveBeenCalled();
  });

  it('modes_follow_grid_kanban_calendar_cards_and_the_default_falls_back_when_dropped', () => {
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    const resolved = resolveModes(
      { default: 'kanban', cards: {}, calendar: { start: 'birthday' }, kanban: { groupBy: 'description' } },
      KINDS,
    );
    expect(resolved.modes).toEqual(['grid', 'calendar', 'cards']);
    expect(resolved.initial).toBe('grid');
    const all = resolveModes({ default: 'cards', cards: {}, calendar: { start: 'birthday' }, kanban: { groupBy: 'industrycode' } }, KINDS);
    expect(all.modes).toEqual(['grid', 'kanban', 'calendar', 'cards']);
    expect(all.initial).toBe('cards');
  });
});
