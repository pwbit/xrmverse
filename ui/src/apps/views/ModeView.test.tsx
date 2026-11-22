import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { ViewColumn } from '../../model/view';
import type { XrmApi } from '../../shell/api/client';
import { ModeView } from './ModeView';
import type { ResolvedModes } from './viewModes';

const FETCH = '<fetch><entity name="appointment"><attribute name="subject"/></entity></fetch>';
const TITLE: ViewColumn = { name: 'subject', width: 100, attribute: 'subject', isHidden: false };

function show(zoneIndependent: boolean) {
  const paths: string[] = [];
  const api = {
    get: async (path: string) => {
      paths.push(decodeURIComponent(path));
      return { value: [{ activityid: 'r1', subject: 'Wall clock', starts: '2026-11-30T23:30:00Z' }] };
    },
  } as unknown as XrmApi;
  const resolved: ResolvedModes = { modes: ['grid', 'calendar'], initial: 'calendar', calendar: { start: 'starts', dateOnly: false, zoneIndependent, layout: 'month' } };
  render(
    <FluentProvider theme={webLightTheme}>
      <ModeView
        mode="calendar"
        resolved={resolved}
        api={api}
        entitySet="appointments"
        fetchXml={FETCH}
        label="Meetings"
        columns={[TITLE]}
        headers={{}}
        primaryId="activityid"
        onMove={vi.fn()}
        onOpen={vi.fn()}
        calendar={{ year: 2026, month: 11, zone: 'Europe/Rome', lcid: 1033, onNavigate: vi.fn() }}
      />
    </FluentProvider>,
  );
  return paths;
}

describe('ModeView calendar time zone', () => {
  it('reads_a_time_zone_independent_column_as_utc', async () => {
    const paths = show(true);
    // 23:30 stored is still November 30th: the stored wall clock is not shifted into Rome.
    const event = await screen.findByRole('button', { name: /^Wall clock,/ });
    expect(event.closest('[role="gridcell"]')?.getAttribute('aria-label')).toContain('November 30, 2026');
    // The range is the UTC days of the grid (Sunday 1 November to Saturday 12 December, lcid 1033), not Rome's local midnights.
    await waitFor(() => expect(paths.length).toBeGreaterThan(0));
    expect(paths.join('\n')).toContain('2026-11-01T00:00:00Z');
  });

  it('shifts_a_user_local_column_into_the_users_zone', async () => {
    show(false);
    const event = await screen.findByRole('button', { name: /^Wall clock,/ });
    expect(event.closest('[role="gridcell"]')?.getAttribute('aria-label')).toContain('December 1, 2026');
  });
});

describe('ModeView calendar layout', () => {
  const api = {
    get: async () => ({ value: [{ activityid: 'r1', subject: 'Workshop', starts: '2026-11-10T09:00:00Z' }] }),
  } as unknown as XrmApi;
  const resolvedWith = (layout: 'month' | 'timeline'): ResolvedModes => ({
    modes: ['grid', 'calendar'],
    initial: 'calendar',
    calendar: { start: 'starts', dateOnly: false, zoneIndependent: false, layout },
  });
  const view = (resolved: ResolvedModes) => (
    <FluentProvider theme={webLightTheme}>
      <ModeView
        mode="calendar"
        resolved={resolved}
        api={api}
        entitySet="appointments"
        fetchXml={FETCH}
        label="Meetings"
        columns={[TITLE]}
        headers={{}}
        primaryId="activityid"
        onMove={vi.fn()}
        onOpen={vi.fn()}
        calendar={{ year: 2026, month: 11, zone: 'Europe/Rome', lcid: 1033, onNavigate: vi.fn() }}
      />
    </FluentProvider>
  );
  const isTimeline = () => screen.getAllByRole('columnheader')[0]!.textContent === 'Record';

  it('starts_on_the_configured_layout_and_switches', async () => {
    const { rerender } = render(view(resolvedWith('timeline')));
    await screen.findByRole('button', { name: /^Workshop,/ });
    const group = screen.getByRole('group', { name: 'Calendar layout' });
    expect(within(group).getByRole('button', { name: 'Timeline' })).toHaveAttribute('aria-pressed', 'true');
    expect(within(group).getByRole('button', { name: 'Month' })).toHaveAttribute('aria-pressed', 'false');
    expect(isTimeline()).toBe(true);

    await userEvent.click(within(group).getByRole('button', { name: 'Month' }));
    expect(screen.getAllByRole('gridcell')).toHaveLength(42);
    expect(screen.getByRole('button', { name: 'Month' })).toHaveAttribute('aria-pressed', 'true');

    // Another view (a new resolved calendar) starts again on its own layout.
    rerender(view(resolvedWith('timeline')));
    await screen.findByRole('button', { name: /^Workshop,/ });
    expect(isTimeline()).toBe(true);
  });
});
