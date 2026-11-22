import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { saveDevCredentials } from '../../shell/auth/devToken';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';

const SYSTEM_ADMIN = '627090ff-40a3-4053-8790-584edc5be201';
const USER_ID = 'aaaaaaaa-0000-0000-0000-000000000001';
const FV = 'OData.Community.Display.V1.FormattedValue';

function job(n: number, statecode: number, statuscode: number, extra: object = {}) {
  const labels: Record<number, string> = { 0: 'Ready', 1: 'Suspended', 2: 'Locked', 3: 'Completed' };
  return {
    asyncoperationid: `00000000-0000-0000-0000-00000000000${n}`,
    name: `Job ${n}`,
    operationtype: 1,
    [`operationtype@${FV}`]: 'System Event',
    statecode,
    [`statecode@${FV}`]: labels[statecode],
    statuscode,
    [`statuscode@${FV}`]: `Status ${statuscode}`,
    createdon: '2026-11-08T10:00:00Z',
    completedon: null,
    message: null,
    friendlymessage: null,
    ...extra,
  };
}

beforeEach(() => {
  sessionStorage.clear();
  saveDevCredentials({ signingKey: 'k'.repeat(40), objectId: USER_ID, name: 'Ada Admin' });
  server.use(
    http.get('*/ui/config.json', () =>
      HttpResponse.json({ authMode: 'Development', audience: 'http://localhost/', apiVersion: 'v9.2' }),
    ),
    http.get('*/api/data/v9.2/WhoAmI', () => HttpResponse.json({ UserId: USER_ID })),
    http.get('*/api/data/v9.2/systemuserrolescollection', () => HttpResponse.json({ value: [{ roleid: SYSTEM_ADMIN }] })),
    http.get('*/api/data/v9.2/roles', () => HttpResponse.json({ value: [{ roleid: SYSTEM_ADMIN, name: 'Role', roletemplateid: SYSTEM_ADMIN }] })),
    http.get('*/api/data/v9.2/teammemberships', () => HttpResponse.json({ value: [] })),
    http.get('*/api/data/v9.2/organizations', () => HttpResponse.json({ value: [{ name: 'Acme Dev' }] })),
  );
});
afterEach(() => sessionStorage.clear());

// Fluent's modal dialog and drawer are marked aria-hidden by the focus manager (tabster) until it has processed the
// focus move into them; under load that lags the DOM. The tests assert behaviour, not that transient state, so
// they query the dialog parts with `hidden: true` (as EntityPicker.test does).
const selectRow = (user: ReturnType<typeof userEvent.setup>, name: string) =>
  user.click(screen.getByRole('radio', { hidden: true, name: `Select ${name}` }));

describe('AsyncPage', () => {
  it('Async_list_pages_and_filters_by_state', async () => {
    const requests: { filter: string | null; prefer: string | null; url: string }[] = [];
    server.use(
      http.get('*/api/data/v9.2/asyncoperations', ({ request }) => {
        const url = new URL(request.url);
        requests.push({ filter: url.searchParams.get('$filter'), prefer: request.headers.get('Prefer'), url: request.url });
        if (url.searchParams.get('$filter') === 'statecode eq 3') return HttpResponse.json({ value: [job(9, 3, 30)] });
        if (url.searchParams.get('page') === '2') return HttpResponse.json({ value: [job(2, 2, 20)] });
        return HttpResponse.json({
          value: [job(1, 0, 0)],
          '@odata.nextLink': `${url.origin}/api/data/v9.2/asyncoperations?$select=name&page=2`,
        });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/async']} />);

    expect(await screen.findByText('Job 1')).toBeInTheDocument();
    expect(requests[0].prefer).toContain('odata.maxpagesize=50');
    expect(requests[0].prefer).toContain(`odata.include-annotations="${FV}"`);
    expect(requests[0].filter).toBeNull();
    expect(within(screen.getByRole('table')).getByText('Ready')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: en['async.loadMore'] }));
    expect(await screen.findByText('Job 2')).toBeInTheDocument();
    expect(screen.getByText('Job 1')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: en['async.loadMore'] })).toBeNull();

    await user.selectOptions(screen.getByLabelText(en['async.filter.state']), '3');
    expect(await screen.findByText('Job 9')).toBeInTheDocument();
    expect(screen.queryByText('Job 1')).toBeNull();
    expect(requests.at(-1)!.filter).toBe('statecode eq 3');
  });

  it('Cancel_sends_statecode_3_statuscode_32_after_confirmation_and_updates_row_in_place', async () => {
    let patched: { url: string; body: unknown } | undefined;
    let listCalls = 0;
    server.use(
      http.get('*/api/data/v9.2/asyncoperations', ({ request }) => {
        listCalls++;
        const url = new URL(request.url);
        if (url.searchParams.get('page') === '2') return HttpResponse.json({ value: [job(3, 2, 20)] });
        return HttpResponse.json({
          value: [job(1, 1, 10), job(2, 3, 30)],
          '@odata.nextLink': `${url.origin}/api/data/v9.2/asyncoperations?$select=name&page=2`,
        });
      }),
      http.get('*/api/data/v9.2/:target', ({ params }) =>
        String(params.target).startsWith('asyncoperations(')
          ? HttpResponse.json(job(1, 3, 32, { 'statuscode@OData.Community.Display.V1.FormattedValue': 'Canceled' }))
          : undefined,
      ),
      http.patch('*/api/data/v9.2/:target', async ({ request }) => {
        patched = { url: new URL(request.url).pathname, body: await request.json() };
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/async']} />);
    await screen.findByText('Job 1');
    await user.click(screen.getByRole('button', { name: en['async.loadMore'] }));
    await screen.findByText('Job 3');
    // Only the waiting/suspended job offers Cancel, on the command bar once it is selected.
    expect(screen.queryByRole('button', { name: /^Cancel/ })).toBeNull();
    await selectRow(user, 'Job 2');
    expect(screen.queryByRole('button', { name: /^Cancel/ })).toBeNull();
    await selectRow(user, 'Job 3');
    expect(screen.queryByRole('button', { name: /^Cancel/ })).toBeNull();
    await selectRow(user, 'Job 1');
    await user.click(screen.getByRole('button', { name: 'Cancel Job 1' }));
    // Nothing is sent until the dialog is confirmed.
    expect(patched).toBeUndefined();
    await user.click(await screen.findByRole('button', { hidden: true, name: en['async.confirm.yes'] }));
    expect(await screen.findByText(en['async.cancelled'])).toBeInTheDocument();
    expect(patched).toEqual({
      url: '/api/data/v9.2/asyncoperations(00000000-0000-0000-0000-000000000001)',
      body: { statecode: 3, statuscode: 32 },
    });
    // Row updated in place; the loaded second page stays; no list reload.
    expect(await screen.findByText('Canceled')).toBeInTheDocument();
    expect(screen.getByText('Job 3')).toBeInTheDocument();
    expect(screen.queryByRole('button', { hidden: true, name: 'Cancel Job 1' })).toBeNull();
    expect(listCalls).toBe(2);
  });

  it('Cancel_declined_sends_nothing', async () => {
    let patched = false;
    server.use(
      http.get('*/api/data/v9.2/asyncoperations', () => HttpResponse.json({ value: [job(1, 0, 0)] })),
      http.patch('*/api/data/v9.2/:target', () => {
        patched = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/async']} />);
    await screen.findByText('Job 1');
    await selectRow(user, 'Job 1');
    await user.click(screen.getByRole('button', { name: 'Cancel Job 1' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: en['async.confirm.no'] }));
    expect(patched).toBe(false);
  });

  it('Cancel_refused_by_server_shows_its_code_and_message', async () => {
    server.use(
      http.get('*/api/data/v9.2/asyncoperations', () => HttpResponse.json({ value: [job(1, 0, 0)] })),
      http.patch('*/api/data/v9.2/:target', () =>
        HttpResponse.json({ error: { code: '0x80040203', message: 'Only cancellation allowed' } }, { status: 400 }),
      ),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/async']} />);
    await screen.findByText('Job 1');
    await selectRow(user, 'Job 1');
    await user.click(screen.getByRole('button', { name: 'Cancel Job 1' }));
    await user.click(await screen.findByRole('button', { hidden: true, name: en['async.confirm.yes'] }));
    expect(await screen.findByText('0x80040203')).toBeInTheDocument();
    expect(screen.getByText('Only cancellation allowed')).toBeInTheDocument();
  });

  it('Detail_drawer_shows_message_and_friendly_message', async () => {
    server.use(
      http.get('*/api/data/v9.2/asyncoperations', () =>
        HttpResponse.json({ value: [job(1, 3, 31, { message: 'Stack: boom', friendlymessage: 'Something broke' })] }),
      ),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/async']} />);
    await user.click(await screen.findByRole('button', { name: 'Job 1' }));
    const drawer = await screen.findByRole('dialog', { hidden: true });
    expect(within(drawer).getByText('Stack: boom')).toBeInTheDocument();
    expect(within(drawer).getByText('Something broke')).toBeInTheDocument();
  });

  it('List_failure_shows_code_and_message', async () => {
    server.use(
      http.get('*/api/data/v9.2/asyncoperations', () =>
        HttpResponse.json({ error: { code: '0x80040220', message: 'no privilege' } }, { status: 403 }),
      ),
    );
    render(<App initialEntries={['/manage/async']} />);
    expect(await screen.findAllByText('0x80040220')).not.toHaveLength(0);
  });
});
