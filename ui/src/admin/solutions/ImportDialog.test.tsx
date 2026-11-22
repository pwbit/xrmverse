import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/server';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';
import { API, onPath, signInAsAdministrator } from '../security/testSupport';
import { summarizeImportLog } from './importLog';
import { serveSolutions } from './solutionTestSupport';

const ASYNC_ID = 'cccccccc-2222-0000-0000-000000000001';
const FV = 'OData.Community.Display.V1.FormattedValue';

beforeEach(() => signInAsAdministrator());
afterEach(() => {
  sessionStorage.clear();
  vi.useRealTimers();
});

const jobBody = (statecode: number, statuscode: number, extra: object = {}) => ({
  statecode,
  statuscode,
  [`statuscode@${FV}`]: statuscode === 20 ? 'In Progress' : `Status ${statuscode}`,
  ...extra,
});

/** Serves the list, ImportSolutionAsync (body captured) and the system job (one response per poll, the last repeats). */
function serveImport(polls: object[]) {
  const counts = serveSolutions();
  const state = { body: undefined as Record<string, unknown> | undefined, polls: 0, listAfterImport: () => counts.list };
  server.use(
    http.post(`${API}/ImportSolutionAsync`, async ({ request }) => {
      state.body = (await request.json()) as Record<string, unknown>;
      return HttpResponse.json({ AsyncOperationId: ASYNC_ID, ImportJobKey: 'key' });
    }),
    onPath('get', `asyncoperations(${ASYNC_ID})`, () => HttpResponse.json(polls[Math.min(state.polls++, polls.length - 1)])),
  );
  return state;
}

async function openAndUpload(user: ReturnType<typeof userEvent.setup>) {
  render(<App initialEntries={['/manage/solutions']} />);
  await screen.findByText('Acme Publisher');
  await user.click(screen.getByRole('button', { name: en['solutions.import.open'] }));
  const file = new File(['PK-zip'], 'sales_kit.zip', { type: 'application/zip' });
  await user.upload(await screen.findByLabelText(en['solutions.import.file']), file);
}

describe('ImportDialog', () => {
  it('Import_polls_until_completed_and_reports_success', async () => {
    const state = serveImport([jobBody(1, 20), jobBody(3, 30)]);
    let jobReads = 0;
    server.use(
      http.get(`${API}/importjobs*`, () => {
        jobReads++;
        return HttpResponse.json({ data: '<importexportxml><result result="success" errorcode="0" errortext=""/></importexportxml>' });
      }),
    );
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await openAndUpload(user);
    await user.click(screen.getByRole('checkbox', { hidden: true, name: en['solutions.import.overwrite'] }));
    await user.click(screen.getByRole('button', { hidden: true, name: en['solutions.import.submit'] }));

    expect(await screen.findByText(en['solutions.import.running'].replace('{status}', 'In Progress'))).toBeInTheDocument();
    expect(state.body).toMatchObject({
      OverwriteUnmanagedCustomizations: true,
      PublishWorkflows: false,
      CustomizationFile: btoa('PK-zip'),
    });
    expect(String(state.body!.ImportJobId)).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
    // Still running before the 2 s poll interval has passed: no second poll.
    await act(async () => {
      vi.advanceTimersByTime(1500);
    });
    expect(state.polls).toBe(1);
    await act(async () => {
      vi.advanceTimersByTime(600);
    });
    expect(await screen.findByText(en['solutions.import.succeeded'], { selector: '[role="group"] *, [role="group"]' })).toBeInTheDocument();
    expect(state.polls).toBe(2);
    expect(jobReads).toBe(1);
    // The list is read again after a successful import.
    await waitFor(() => expect(state.listAfterImport()).toBe(2));
    // Polling has stopped.
    await act(async () => {
      vi.advanceTimersByTime(6000);
    });
    expect(state.polls).toBe(2);
  });

  it('Import_failure_shows_importjob_log', async () => {
    const state = serveImport([jobBody(3, 31, { message: 'Import failed: the system job message', friendlymessage: null })]);
    const log =
      '<importexportxml><solutionManifests><solutionManifest>' +
      '<result result="failure" errorcode="0x80048033" errortext="Component new_widget is missing its table."/>' +
      '</solutionManifest></solutionManifests></importexportxml>';
    const jobPaths: string[] = [];
    server.use(
      http.get(`${API}/importjobs*`, ({ request }) => {
        jobPaths.push(decodeURIComponent(new URL(request.url).pathname + new URL(request.url).search));
        return HttpResponse.json({ data: log });
      }),
    );
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await openAndUpload(user);
    await user.click(screen.getByRole('button', { hidden: true, name: en['solutions.import.submit'] }));

    expect(await screen.findByText(en['solutions.import.failed'])).toBeInTheDocument();
    expect(await screen.findByText('0x80048033 Component new_widget is missing its table.')).toBeInTheDocument();
    expect(screen.getByText(en['solutions.import.jobMessage'].replace('{message}', 'Import failed: the system job message'))).toBeInTheDocument();
    // The log was read from the import job named by the client-generated ImportJobId.
    expect(jobPaths).toHaveLength(1);
    expect(jobPaths[0]).toContain(`/api/data/v9.2/importjobs(${String(state.body!.ImportJobId)})`);
    // A failed import does not read the list again.
    expect(state.listAfterImport()).toBe(1);
  });

  it('Import_failure_without_a_readable_log_still_shows_the_job_message', async () => {
    serveImport([jobBody(3, 31, { message: 'Boom' })]);
    server.use(
      http.get(`${API}/importjobs*`, () =>
        HttpResponse.json({ error: { code: '0x80040217', message: 'importjob With Id = x Does Not Exist' } }, { status: 404 }),
      ),
    );
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await openAndUpload(user);
    await user.click(screen.getByRole('button', { hidden: true, name: en['solutions.import.submit'] }));
    expect(await screen.findByText(en['solutions.import.failed'])).toBeInTheDocument();
    expect(screen.getByText(en['solutions.import.jobMessage'].replace('{message}', 'Boom'))).toBeInTheDocument();
    expect(await screen.findByText(/The import log could not be read/)).toBeInTheDocument();
  });

  it.each([
    ['update', 'ImportSolutionAsync', {}],
    ['upgrade', 'StageAndUpgradeAsync', {}],
    ['stage', 'ImportSolutionAsync', { HoldingSolution: true }],
  ])('Import_posts_the_%s_mode', async (mode, message, extra) => {
    serveSolutions();
    const seen: { message: string; body: Record<string, unknown> }[] = [];
    server.use(
      http.post(`${API}/:message`, async ({ request, params }) => {
        seen.push({ message: String(params.message), body: (await request.json()) as Record<string, unknown> });
        return HttpResponse.json({ AsyncOperationId: ASYNC_ID });
      }),
      onPath('get', `asyncoperations(${ASYNC_ID})`, () => HttpResponse.json(jobBody(1, 20))),
    );
    const user = userEvent.setup();
    await openAndUpload(user);
    await user.selectOptions(screen.getByLabelText(en['solutions.import.mode']), mode);
    await user.click(screen.getByRole('button', { hidden: true, name: en['solutions.import.submit'] }));
    await waitFor(() => expect(seen).toHaveLength(1));
    expect(seen[0].message).toBe(message);
    expect(seen[0].body).toMatchObject(extra);
    if (mode !== 'stage') expect(seen[0].body).not.toHaveProperty('HoldingSolution');
    await screen.findByText(en['solutions.import.running'].replace('{status}', 'In Progress'));
  });
});

describe('ImportDialog resilience', () => {
  it('Poll_error_keeps_the_job_and_keep_checking_resumes', async () => {
    serveSolutions();
    let reads = 0;
    let imports = 0;
    server.use(
      http.post(`${API}/ImportSolutionAsync`, () => {
        imports++;
        return HttpResponse.json({ AsyncOperationId: ASYNC_ID, ImportJobKey: 'key' });
      }),
      onPath('get', `asyncoperations(${ASYNC_ID})`, () =>
        reads++ === 0
          ? HttpResponse.json({ error: { code: '0x80040E14', message: 'temporary failure' } }, { status: 500 })
          : HttpResponse.json(jobBody(3, 30)),
      ),
      http.get(`${API}/importjobs*`, () => HttpResponse.json({ data: '<importexportxml/>' })),
    );
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await openAndUpload(user);
    await user.click(screen.getByRole('button', { hidden: true, name: en['solutions.import.submit'] }));
    expect(await screen.findByText(new RegExp(ASYNC_ID))).toBeInTheDocument();
    // No second import can be started while the job is unresolved.
    expect(screen.getByRole('button', { hidden: true, name: en['solutions.import.submit'] })).toBeDisabled();
    await user.click(screen.getByRole('button', { hidden: true, name: en['solutions.import.keepChecking'] }));
    expect(await screen.findByText(en['solutions.import.succeeded'], { selector: '[role="group"] *, [role="group"]' })).toBeInTheDocument();
    expect(imports).toBe(1);
    expect(reads).toBe(2);
  });

  it('Unreadable_log_after_success_shows_a_warning', async () => {
    serveImport([jobBody(3, 30)]);
    server.use(
      http.get(`${API}/importjobs*`, () => HttpResponse.json({ error: { code: '0x80040217', message: 'gone' } }, { status: 404 })),
    );
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await openAndUpload(user);
    await user.click(screen.getByRole('button', { hidden: true, name: en['solutions.import.submit'] }));
    expect(await screen.findByText(/The import succeeded, but its log could not be read/)).toBeInTheDocument();
    expect(screen.getAllByText(en['solutions.import.succeeded'], { selector: '[role="group"] *, [role="group"]' }).length).toBeGreaterThan(0);
  });
});

describe('summarizeImportLog', () => {
  it('counts warnings and lists failures with code and text', () => {
    const xml =
      '<importexportxml><result result="warning" errorcode="0" errortext="w1"/>' +
      '<result result="success" errorcode="0" errortext=""/>' +
      '<result result="failure" errorcode="0x1" errortext="bad"/></importexportxml>';
    expect(summarizeImportLog(xml)).toEqual({ warnings: 1, failures: [{ code: '0x1', text: 'bad' }] });
    expect(() => summarizeImportLog('not xml <')).toThrow();
  });
});
