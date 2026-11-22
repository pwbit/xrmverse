import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';
import { forgetMostUsed } from './mostUsed';
import { installMetadata, rawAttr, rawEntity } from './tableTestSupport';

const entity = rawEntity('xv_project', 'xv_projects', [
  rawAttr('xv_projectid', 'Uniqueidentifier', { IsPrimaryId: true, IsValidForUpdate: false }),
  rawAttr('xv_name', 'String', { IsPrimaryName: true, display: 'Name' }),
  ...['a', 'b', 'c', 'd', 'e', 'f'].map((x) => rawAttr(`xv_${x}`, 'String', { display: `Col ${x.toUpperCase()}` })),
  rawAttr('createdon', 'DateTime', { IsValidForCreate: false, IsValidForUpdate: false }),
], { PrimaryIdAttribute: 'xv_projectid', PrimaryNameAttribute: 'xv_name', display: 'Project' });

const row = (id: string, name: string, cols: Record<string, string | null>) => ({ xv_projectid: id, xv_name: name, createdon: 'x', ...cols });
// Counts over the first page: a 3, b 3, c 2, d 2, e 1, f 0 -> the list shows a, b, c, d, e.
const firstPage = [
  row('p1', 'Alpha', { xv_a: '1', xv_b: '1', xv_c: '1', xv_d: '1', xv_e: '1', xv_f: null }),
  row('p2', 'Beta', { xv_a: '1', xv_b: '1', xv_c: '1', xv_d: '1', xv_e: null, xv_f: null }),
  row('p3', 'Gamma', { xv_a: '1', xv_b: '1', xv_c: null, xv_d: null, xv_e: null, xv_f: null }),
];

beforeEach(() => {
  signInAsAdministrator();
  forgetMostUsed('xv_project');
  installMetadata([entity]);
});
afterEach(() => {
  vi.useRealTimers();
  sessionStorage.clear();
});

describe('RecordList', () => {
  it('RecordList_pages_with_nextLink_and_debounces_filter', async () => {
    const requests: { select: string | null; filter: string | null; prefer: string | null; page: string | null }[] = [];
    server.use(
      http.get(`${API}/xv_projects`, ({ request }) => {
        const url = new URL(request.url);
        requests.push({ select: url.searchParams.get('$select'), filter: url.searchParams.get('$filter'), prefer: request.headers.get('Prefer'), page: url.searchParams.get('page') });
        if (url.searchParams.get('$filter')) return HttpResponse.json({ value: [row('p9', 'Abacus', { xv_a: '9' })] });
        if (url.searchParams.get('page') === '2') return HttpResponse.json({ value: [row('p4', 'Delta', { xv_a: '4' })] });
        return HttpResponse.json({ value: firstPage, '@odata.nextLink': `${url.origin}/api/data/v9.2/xv_projects?$select=${url.searchParams.get('$select')}&page=2` });
      }),
    );
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('tab', { name: en['table.tab.records'] }));

    // The first page (all candidate columns) decides the columns; the list then asks only for the name and those 5.
    await screen.findByText('Alpha');
    const [forColumns, list] = requests;
    expect(forColumns.select).toBe('xv_projectid,xv_a,xv_b,xv_c,xv_d,xv_e,xv_f');
    expect(forColumns.filter).toBeNull();
    expect(list.select).toBe('xv_projectid,xv_name,xv_a,xv_b,xv_c,xv_d,xv_e');
    expect(list.prefer).toContain('odata.maxpagesize=50');
    expect(list.prefer).toContain('OData.Community.Display.V1.FormattedValue');
    for (const header of ['Col A', 'Col B', 'Col C', 'Col D', 'Col E']) expect(screen.getByRole('columnheader', { name: header })).toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: 'Col F' })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: 'Created On' })).toBeNull();

    // Next page by the server's nextLink.
    await user.click(screen.getByRole('button', { name: en['common.loadMore'] }));
    await screen.findByText('Delta');
    expect(requests.at(-1)?.page).toBe('2');
    expect(screen.getByText('Alpha')).toBeInTheDocument();

    // Filter: three keystrokes send nothing until 300 ms have passed, then exactly one request.
    const before = requests.length;
    await user.type(screen.getByLabelText(en['records.filter']), "A'b");
    expect(requests).toHaveLength(before);
    await act(async () => {
      vi.advanceTimersByTime(250);
    });
    expect(requests).toHaveLength(before);
    await act(async () => {
      vi.advanceTimersByTime(100);
    });
    await screen.findByText('Abacus');
    expect(requests).toHaveLength(before + 1);
    expect(requests.at(-1)?.filter).toBe("contains(xv_name,'A''b')");
    expect(screen.queryByText('Alpha')).toBeNull();
  });

  it('computes the columns once per table per sign-in', async () => {
    let calls = 0;
    server.use(
      http.get(`${API}/xv_projects`, () => {
        calls++;
        return HttpResponse.json({ value: firstPage });
      }),
    );
    const user = userEvent.setup();
    const first = render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('tab', { name: en['table.tab.records'] }));
    await screen.findByText('Alpha');
    expect(calls).toBe(2); // the columns' first page, then the list
    // The list mounts again in the same sign-in: only the list is read again.
    await user.click(screen.getByRole('tab', { name: en['table.tab.columns'] }));
    await user.click(screen.getByRole('tab', { name: en['table.tab.records'] }));
    await waitFor(() => expect(calls).toBe(3));
    await screen.findByText('Alpha');
    first.unmount();
    // A new sign-in (a new shell) may be another user: the columns are computed again.
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('tab', { name: en['table.tab.records'] }));
    await waitFor(() => expect(calls).toBe(5));
    await screen.findByText('Alpha');
  });

  it('deletes only after confirmation', async () => {
    let deleted: string | undefined;
    server.use(
      http.get(`${API}/xv_projects`, () => HttpResponse.json({ value: deleted ? [] : firstPage })),
      onPath('delete', 'xv_projects(p1)', ({ request }) => {
        deleted = new URL(request.url).pathname;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('tab', { name: en['table.tab.records'] }));
    // No delete before a record is selected: the command acts on the selected row.
    await screen.findByRole('radio', { hidden: true, name: 'Select Alpha' });
    expect(screen.queryByRole('button', { name: 'Delete Alpha' })).toBeNull();
    await user.click(screen.getByRole('radio', { hidden: true, name: 'Select Alpha' }));
    await user.click(await screen.findByRole('button', { name: 'Delete Alpha' }));
    expect(deleted).toBeUndefined();
    const dialog = await screen.findByRole('dialog', { hidden: true });
    expect(dialog).toHaveTextContent('Delete the record "Alpha"');
    await user.click(screen.getByRole('button', { hidden: true, name: en['records.delete.confirm'] }));
    expect(await screen.findByText(en['records.deleted'])).toBeInTheDocument();
    expect(deleted).toBe('/api/data/v9.2/xv_projects(p1)');
    await screen.findByText(en['records.empty']);
  });

  it('offers Retry after the column query fails and recovers', async () => {
    let calls = 0;
    server.use(
      http.get(`${API}/xv_projects`, () => {
        calls++;
        return calls === 1 ? HttpResponse.json({ error: { code: '0x80040217', message: 'Boom' } }, { status: 500 }) : HttpResponse.json({ value: firstPage });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_project']} />);
    await user.click(await screen.findByRole('tab', { name: en['table.tab.records'] }));
    await screen.findByText(/0x80040217/);
    await user.click(screen.getByRole('button', { name: en['common.retry'] }));
    await screen.findByText('Alpha');
    expect(screen.queryByText(/0x80040217/)).toBeNull();
  });
});
