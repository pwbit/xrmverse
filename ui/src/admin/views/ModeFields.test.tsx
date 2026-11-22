import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { en } from '../../shell/i18n/en';
import { attr } from '../tables/testAttrs';
import { ModeFields } from './ModeFields';

const ATTRIBUTES = [
  attr('statuscode', { kind: 'choice', typeName: 'Status', displayName: 'Status Reason', options: [{ value: 1, label: 'Active' }] }),
  attr('createdon', { kind: 'datetime', typeName: 'DateTime', displayName: 'Created On', behavior: 'UserLocal' }),
  attr('xv_start', { kind: 'datetime', typeName: 'DateTime', displayName: 'Start Day', behavior: 'DateOnly', dateOnly: true }),
  attr('xv_end', { kind: 'datetime', typeName: 'DateTime', displayName: 'End Day', behavior: 'DateOnly', dateOnly: true }),
];

describe('ModeFields', () => {
  it('reports a chosen board column as kanban.groupBy', async () => {
    const onChange = vi.fn();
    render(<ModeFields attributes={ATTRIBUTES} value={{}} onChange={onChange} />);
    await userEvent.selectOptions(screen.getByLabelText(en['views.modes.board']), 'statuscode');
    expect(onChange).toHaveBeenCalledWith({ kanban: { groupBy: 'statuscode' } });
  });

  it('drops the default when its mode goes off', async () => {
    const onChange = vi.fn();
    render(<ModeFields attributes={ATTRIBUTES} value={{ kanban: { groupBy: 'statuscode' }, default: 'kanban' }} onChange={onChange} />);
    await userEvent.selectOptions(screen.getByLabelText(en['views.modes.board']), '');
    expect(onChange).toHaveBeenCalledTimes(1);
    const next = onChange.mock.calls[0][0];
    expect(next.kanban).toBeUndefined();
    expect(next.default).toBeUndefined();
  });

  it('does not offer a calendar end with another date behaviour', () => {
    render(<ModeFields attributes={ATTRIBUTES} value={{ calendar: { start: 'xv_start' } }} onChange={() => {}} />);
    const end = screen.getByLabelText(en['views.modes.calendarEnd']);
    const options = Array.from(end.querySelectorAll('option')).map((o) => o.value);
    expect(options).toContain('xv_end');
    expect(options).not.toContain('createdon');
    expect(options).not.toContain('xv_start');
  });

  it('offers the calendar layout only with a calendar, writes month as absent, and keeps it through start and end changes', async () => {
    const { rerender } = render(<ModeFields attributes={ATTRIBUTES} value={{}} onChange={() => {}} />);
    expect(screen.queryByLabelText(en['views.modes.calendarLayout'])).not.toBeInTheDocument();
    const onChange = vi.fn();
    rerender(<ModeFields attributes={ATTRIBUTES} value={{ calendar: { start: 'xv_start' } }} onChange={onChange} />);
    expect(screen.getByLabelText(en['views.modes.calendarLayout'])).toHaveValue('month');
    await userEvent.selectOptions(screen.getByLabelText(en['views.modes.calendarLayout']), 'timeline');
    expect(onChange).toHaveBeenLastCalledWith({ calendar: { start: 'xv_start', layout: 'timeline' } });
    rerender(<ModeFields attributes={ATTRIBUTES} value={{ calendar: { start: 'xv_start', layout: 'timeline' } }} onChange={onChange} />);
    await userEvent.selectOptions(screen.getByLabelText(en['views.modes.calendarEnd']), 'xv_end');
    expect(onChange).toHaveBeenLastCalledWith({ calendar: { start: 'xv_start', end: 'xv_end', layout: 'timeline' } });
    await userEvent.selectOptions(screen.getByLabelText(en['views.modes.calendarStart']), 'createdon');
    expect(onChange).toHaveBeenLastCalledWith({ calendar: { start: 'createdon', layout: 'timeline' } });
    await userEvent.selectOptions(screen.getByLabelText(en['views.modes.calendarLayout']), 'month');
    expect(onChange).toHaveBeenLastCalledWith({ calendar: { start: 'xv_start' } });
    await userEvent.selectOptions(screen.getByLabelText(en['views.modes.calendarStart']), '');
    expect(onChange).toHaveBeenLastCalledWith({ calendar: undefined });
  });

  it('prefixes the ids of its fields with idPrefix, so two sets on one page stay apart', () => {
    const value = { kanban: { groupBy: 'statuscode' }, calendar: { start: 'xv_start' }, cards: {} };
    const { container } = render(
      <>
        <ModeFields attributes={ATTRIBUTES} value={value} onChange={() => {}} idPrefix="subgrid-a" />
        <ModeFields attributes={ATTRIBUTES} value={value} onChange={() => {}} idPrefix="subgrid-b" />
      </>,
    );
    const ids = Array.from(container.querySelectorAll('[id^="subgrid-"]')).map((e) => e.id);
    for (const p of ['subgrid-a', 'subgrid-b']) expect(ids).toEqual(expect.arrayContaining([`${p}-board`, `${p}-calendarStart`, `${p}-calendarEnd`, `${p}-calendarLayout`, `${p}-cards`]));
    expect(new Set(ids).size).toBe(ids.length);
  });
});
