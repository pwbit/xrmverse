import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../../test/server';
import { App } from '../../../shell/App';
import { en } from '../../../shell/i18n/en';
import { onPath, signInAsAdministrator } from '../../security/testSupport';
import { installMetadata, rawAttr, rawEntity } from '../tableTestSupport';

const doc = rawEntity(
  'xv_doc',
  'xv_docs',
  [
    rawAttr('xv_docid', 'Uniqueidentifier', { IsPrimaryId: true, IsValidForUpdate: false }),
    rawAttr('xv_name', 'String', { IsPrimaryName: true, display: 'Name' }),
    // What the server reports for a file column: not valid for create or update (only the upload messages write it).
    rawAttr('xv_contract', 'File', { display: 'Contract', IsValidForCreate: false, IsValidForUpdate: false, MaxSizeInKB: 32768 }),
    rawAttr('xv_contract_name', 'String', { AttributeOf: 'xv_contract', IsValidForCreate: false, IsValidForUpdate: false }),
  ],
  { PrimaryIdAttribute: 'xv_docid', PrimaryNameAttribute: 'xv_name', display: 'Document' },
);

const WITH_FILE = { xv_docid: 'D1', xv_name: 'Lease', xv_contract: 'f1', xv_contract_name: 'contract.pdf' };
const WITHOUT_FILE = { ...WITH_FILE, xv_contract: null, xv_contract_name: null };

/** Answers the record reads in turn (the form's, then each refresh of the editor); the last answer repeats. */
function recordReads(...answers: Record<string, unknown>[]) {
  const selects: string[] = [];
  server.use(
    onPath('get', 'xv_docs(D1)', ({ request }) => {
      selects.push(new URL(request.url).searchParams.get('$select') ?? '');
      return HttpResponse.json(answers[Math.min(selects.length - 1, answers.length - 1)]);
    }),
  );
  return selects;
}

async function openRecord() {
  const rendered = render(<App initialEntries={['/manage/tables/xv_doc/records/D1']} />);
  const field = await screen.findByRole('group', { name: 'Contract' });
  return Object.assign(field, { unmount: rendered.unmount });
}

const CONFIRM_BODY = 'Delete "contract.pdf" from Contract? This cannot be undone.';

/**
 * The delete confirmation, found by its text (a role query over the whole page is slow enough to time out on a loaded
 * machine); its buttons are then looked up inside it.
 */
async function confirmation(): Promise<HTMLElement> {
  const dialog = (await screen.findByText(CONFIRM_BODY)).closest<HTMLElement>('[role="dialog"]')!;
  expect(within(dialog).getByRole('heading', { hidden: true, name: en['field.file.delete.title'] })).toBeInTheDocument();
  return dialog;
}

beforeEach(() => {
  signInAsAdministrator();
  installMetadata([doc]);
});
afterEach(() => {
  sessionStorage.clear();
  vi.restoreAllMocks();
});

describe('FileField', () => {
  it('shows the file name the record carries and reads it with the column', async () => {
    const selects = recordReads(WITH_FILE);
    const field = await openRecord();
    expect(await within(field).findByText('contract.pdf')).toBeInTheDocument();
    expect(selects[0].split(',')).toEqual(expect.arrayContaining(['xv_contract', 'xv_contract_name']));
    expect(within(field).getByText('Up to 32768 KB')).toBeInTheDocument();
  });

  it('uploads a chosen file and shows the name the server then reports', async () => {
    let uploaded: { name: string | null; body: string } | undefined;
    const selects = recordReads(WITHOUT_FILE, { xv_contract: 'f2', xv_contract_name: 'new.txt' });
    server.use(
      onPath('patch', 'xv_docs(D1)/xv_contract', async ({ request }) => {
        uploaded = { name: request.headers.get('x-ms-file-name'), body: await request.text() };
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    const field = await openRecord();
    expect(await within(field).findByText(en['field.file.none'])).toBeInTheDocument();
    await user.upload(within(field).getByLabelText('File for Contract'), new File(['hello'], 'new.txt'));
    expect(await screen.findByText('File "new.txt" uploaded.')).toBeInTheDocument();
    expect(uploaded).toEqual({ name: 'new.txt', body: 'hello' });
    expect(await within(field).findByText('new.txt')).toBeInTheDocument();
    // The editor reads its own column again; the rest of the form is left as the user has it.
    expect(selects[1]).toBe('xv_contract,xv_contract_name');
  });

  it('downloads the file under the name the server sends', async () => {
    recordReads(WITH_FILE);
    server.use(
      onPath('get', 'xv_docs(D1)/xv_contract/$value', () => new HttpResponse('pdf', { headers: { 'x-ms-file-name': 'server-name.pdf' } })),
    );
    Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:fake'), revokeObjectURL: vi.fn() });
    let savedAs: string | undefined;
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      savedAs = this.download;
    });
    const user = userEvent.setup();
    const field = await openRecord();
    await within(field).findByText('contract.pdf');
    await user.click(within(field).getByRole('button', { name: 'Download Contract' }));
    await waitFor(() => expect(savedAs).toBe('server-name.pdf'));
  });

  it('Delete_confirms_then_sends_DELETE', async () => {
    let deleted = 0;
    recordReads(WITH_FILE, WITHOUT_FILE);
    server.use(
      onPath('delete', 'xv_docs(D1)/xv_contract', () => {
        deleted++;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    const field = await openRecord();
    await within(field).findByText('contract.pdf');
    await user.click(within(field).getByRole('button', { name: 'Delete Contract' }));
    const dialog = await confirmation();
    expect(deleted).toBe(0);
    await user.click(within(dialog).getByRole('button', { hidden: true, name: en['field.file.delete.confirm'] }));
    expect(await screen.findByText('Contract: the file was deleted.')).toBeInTheDocument();
    expect(deleted).toBe(1);
    expect(await within(field).findByText(en['field.file.none'])).toBeInTheDocument();
  });

  it('cancelling the delete sends nothing', async () => {
    let deleted = 0;
    recordReads(WITH_FILE);
    server.use(
      onPath('delete', 'xv_docs(D1)/xv_contract', () => {
        deleted++;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    const field = await openRecord();
    await within(field).findByText('contract.pdf');
    await user.click(within(field).getByRole('button', { name: 'Delete Contract' }));
    const dialog = await confirmation();
    await user.click(within(dialog).getByRole('button', { hidden: true, name: en['common.cancel'] }));
    await waitFor(() => expect(screen.queryByText(CONFIRM_BODY)).toBeNull());
    expect(deleted).toBe(0);
    expect(within(field).getByText('contract.pdf')).toBeInTheDocument();
  });

  it('aborts a chunked upload still running when the editor goes away', async () => {
    recordReads(WITH_FILE);
    const chunks: { range: string | null; signal: AbortSignal }[] = [];
    let release: () => void = () => {};
    const held = new Promise<void>((resolve) => (release = resolve));
    server.use(
      onPath('patch', 'xv_docs(D1)/xv_contract', async ({ request }) => {
        if (!new URL(request.url).searchParams.has('sessiontoken'))
          return new HttpResponse(null, {
            status: 200,
            headers: { Location: 'http://localhost/api/data/v9.2/xv_docs(D1)/xv_contract?sessiontoken=t', 'x-ms-chunk-size': '4194304' },
          });
        chunks.push({ range: request.headers.get('Content-Range'), signal: request.signal });
        await held;
        return new HttpResponse(null, { status: 206 });
      }),
    );
    const user = userEvent.setup();
    const field = await openRecord();
    await within(field).findByText('contract.pdf');
    await user.upload(within(field).getByLabelText('File for Contract'), new File([new Uint8Array(4194304 + 10)], 'big.bin'));
    await waitFor(() => expect(chunks).toHaveLength(1));

    field.unmount();
    await waitFor(() => expect(chunks[0].signal.aborted).toBe(true));
    release();
    // Nothing follows the aborted chunk.
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(chunks.map((c) => c.range)).toEqual(['bytes 0-4194303/4194314']);
  });

  it('Server_error_shows_code', async () => {
    recordReads(WITH_FILE);
    server.use(
      onPath('patch', 'xv_docs(D1)/xv_contract', () =>
        HttpResponse.json({ error: { code: '0x80044a02', message: 'The file is larger than the maximum size of the column.' } }, { status: 400 }),
      ),
    );
    const user = userEvent.setup();
    const field = await openRecord();
    await within(field).findByText('contract.pdf');
    await user.upload(within(field).getByLabelText('File for Contract'), new File(['x'.repeat(10)], 'huge.bin'));
    expect(await screen.findByText('0x80044a02')).toBeInTheDocument();
    expect(screen.getByText('The file is larger than the maximum size of the column.')).toBeInTheDocument();
    // The column keeps what it had, and the editor can be used again.
    expect(within(field).getByText('contract.pdf')).toBeInTheDocument();
    await waitFor(() => expect(within(field).getByRole('button', { name: 'Upload Contract' })).toBeEnabled());
  });
});
