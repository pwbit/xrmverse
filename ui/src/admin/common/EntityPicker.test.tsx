import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, signInAsAdministrator } from '../security/testSupport';

beforeEach(signInAsAdministrator);
afterEach(() => {
  vi.useRealTimers();
  sessionStorage.clear();
});

describe('EntityPicker', () => {
  it('searches the primary name with contains, debounced by 300 ms, 20 per page, and pages with nextLink', async () => {
    const requests: { filter: string | null; prefer: string | null; at: number }[] = [];
    server.use(
      http.get(`${API}/systemusers`, () => HttpResponse.json({ value: [] })),
      http.get(`${API}/businessunits`, ({ request }) => {
        const url = new URL(request.url);
        requests.push({ filter: url.searchParams.get('$filter'), prefer: request.headers.get('Prefer'), at: Date.now() });
        if (url.searchParams.get('page') === '2') return HttpResponse.json({ value: [{ businessunitid: 'b2', name: 'Second page' }] });
        return HttpResponse.json({
          value: [{ businessunitid: 'b1', name: "O'Brien Unit" }],
          '@odata.nextLink': `${url.origin}/api/data/v9.2/businessunits?$select=name&page=2`,
        });
      }),
    );
    // Fake clock: the debounce is proved by advancing time, not by waiting for it. `shouldAdvanceTime` keeps
    // Fluent's and Testing Library's own timers moving (about 20 fake ms per 20 real ms tick).
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    render(<App initialEntries={['/manage/security/users']} />);
    await user.click(await screen.findByRole('button', { hidden: true, name: en['users.create.open'] }));
    await screen.findByRole('option', { hidden: true, name: "O'Brien Unit" });
    expect(requests).toHaveLength(1);
    expect(requests[0].filter).toBeNull();
    expect(requests[0].prefer).toContain('odata.maxpagesize=20');

    const search = screen.getAllByPlaceholderText(en['picker.search']).at(-1)!;
    await user.type(search, "O'B");
    const typedAt = Date.now();
    // Three keystrokes in a row: nothing is sent while the 300 ms debounce is still running...
    expect(requests).toHaveLength(1);
    await act(async () => {
      vi.advanceTimersByTime(250);
    });
    expect(requests).toHaveLength(1);
    // ...and exactly one search goes out once it has elapsed, with the typed text (quote doubled).
    await act(async () => {
      vi.advanceTimersByTime(100);
    });
    await waitFor(() => expect(requests).toHaveLength(2));
    await screen.findByRole('option', { hidden: true, name: "O'Brien Unit" });
    await act(async () => {
      vi.advanceTimersByTime(1000);
    });
    expect(requests).toHaveLength(2);
    expect(requests[1].filter).toBe("contains(name,'O''B')");
    // Sent 300 ms (fake clock) after the last keystroke, not earlier and not much later.
    expect(requests[1].at - typedAt).toBeGreaterThanOrEqual(300);
    expect(requests[1].at - typedAt).toBeLessThan(450);

    await user.click(await screen.findByRole('button', { hidden: true, name: en['picker.loadMore'] }));
    expect(await screen.findByRole('option', { hidden: true, name: 'Second page' })).toBeInTheDocument();
    expect(screen.getByRole('option', { hidden: true, name: "O'Brien Unit" })).toBeInTheDocument();
  });
});
