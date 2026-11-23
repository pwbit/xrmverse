import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { onPath } from '../../admin/security/testSupport';
import { rawOneToMany } from '../../admin/tables/tableTestSupport';
import { Providers } from '../../renderers/classic/form/formTestSupport';
import { server } from '../../test/server';
import { mockApps } from '../testSupport';
import { AttachmentsSection } from './AttachmentsSection';

const EMAIL = 'eeeeeeee-0000-0000-0000-000000000001';
const A1 = 'f0000000-0000-0000-0000-000000000001';
const A2 = 'f0000000-0000-0000-0000-000000000002';

interface Row {
  activitymimeattachmentid: string;
  filename: string;
  filesize: number;
  mimetype: string;
}

function mockAttachments(initial: Row[], org: { maxuploadfilesize?: number; blockedattachments?: string } = {}) {
  mockApps();
  let rows = [...initial];
  const posts: Record<string, unknown>[] = [];
  const deletes: string[] = [];
  const filters: string[] = [];
  server.use(
    http.get('*/api/data/v9.2/organizations', () => HttpResponse.json({ value: [{ maxuploadfilesize: 1024 * 1024, blockedattachments: 'exe;js;bat', ...org }] })),
    http.get('*/api/data/v9.2/RelationshipDefinitions', () =>
      HttpResponse.json({ value: [rawOneToMany('email_activity_mime_attachment', 'activitymimeattachment', 'objectid', 'email', 'objectid_email')] }),
    ),
    http.get('*/api/data/v9.2/activitymimeattachments', ({ request }) => {
      filters.push(new URL(request.url).searchParams.get('$filter') ?? '');
      return HttpResponse.json({ value: rows });
    }),
    http.post('*/api/data/v9.2/activitymimeattachments', async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      posts.push(body);
      rows = [...rows, { activitymimeattachmentid: A2, filename: String(body.filename), filesize: 5, mimetype: 'text/plain' }];
      return new HttpResponse(null, { status: 204, headers: { 'OData-EntityId': `http://x/api/data/v9.2/activitymimeattachments(${A2})` } });
    }),
    http.delete('*/api/data/v9.2/:path', ({ params }) => {
      deletes.push(decodeURIComponent(String(params.path)));
      rows = rows.filter((r) => !String(params.path).includes(r.activitymimeattachmentid));
      return new HttpResponse(null, { status: 204 });
    }),
    onPath('get', `activitymimeattachments(${A1})`, () => HttpResponse.json({ body: btoa('%PDF-1.4'), filename: 'invoice-1042.pdf', mimetype: 'application/pdf' })),
  );
  return { posts, deletes, filters };
}

const PDF: Row = { activitymimeattachmentid: A1, filename: 'invoice-1042.pdf', filesize: 188416, mimetype: 'application/pdf' };

function show(props: Partial<Parameters<typeof AttachmentsSection>[0]> = {}) {
  return render(
    <Providers>
      <AttachmentsSection emailId={EMAIL} readOnly={false} {...props} />
    </Providers>,
  );
}

beforeEach(() => {
  Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:fake'), revokeObjectURL: vi.fn() });
});

describe('AttachmentsSection', () => {
  it("lists the email's attachments with their size", async () => {
    const { filters } = mockAttachments([PDF]);
    show();
    const list = await screen.findByRole('list', { name: 'Attachments (1)' });
    expect(within(list).getByText('invoice-1042.pdf')).toBeInTheDocument();
    expect(within(list).getByText('184 KB')).toBeInTheDocument();
    expect(filters[0]).toBe(`_objectid_value eq ${EMAIL}`);
  });

  it('is hidden on a new email', () => {
    mockAttachments([]);
    show({ emailId: undefined });
    expect(screen.queryByRole('region')).toBeNull();
    expect(screen.queryByText(/Attachments/)).toBeNull();
  });

  it('uploads a file as base64 bound to the email', async () => {
    const { posts } = mockAttachments([]);
    show();
    await screen.findByRole('list', { name: 'Attachments (0)' });
    await userEvent.upload(screen.getByLabelText('Attach files'), new File(['hello'], 'notes.txt', { type: 'text/plain' }));
    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0]).toEqual({ filename: 'notes.txt', mimetype: 'text/plain', body: btoa('hello'), 'objectid_email@odata.bind': `/emails(${EMAIL})`, objecttypecode: 'email' });
    expect(await screen.findByRole('list', { name: 'Attachments (1)' })).toBeInTheDocument();
  });

  it('refuses a file over the upload limit or with a blocked extension before sending it', async () => {
    const { posts } = mockAttachments([], { maxuploadfilesize: 4 });
    show();
    await screen.findByRole('list', { name: 'Attachments (0)' });
    await userEvent.upload(screen.getByLabelText('Attach files'), new File(['hello'], 'big.txt'));
    expect(await screen.findByText(/big\.txt is larger than the upload limit/)).toBeInTheDocument();
    await userEvent.upload(screen.getByLabelText('Attach files'), new File(['x'], 'run.exe. '));
    expect(await screen.findByText(/Files of the type \.exe are blocked/)).toBeInTheDocument();
    expect(posts).toEqual([]);
  });

  it('downloads through the file download path and deletes', async () => {
    const { deletes } = mockAttachments([PDF]);
    show();
    await userEvent.click(await screen.findByRole('button', { name: 'Download invoice-1042.pdf' }));
    await waitFor(() => expect(URL.createObjectURL).toHaveBeenCalled());
    await userEvent.click(screen.getByRole('button', { name: 'Delete invoice-1042.pdf' }));
    await waitFor(() => expect(deletes).toEqual([`activitymimeattachments(${A1})`]));
    expect(await screen.findByRole('list', { name: 'Attachments (0)' })).toBeInTheDocument();
  });

  it('is read-only once sent: preview and download only', async () => {
    mockAttachments([PDF]);
    show({ readOnly: true });
    await screen.findByRole('list', { name: 'Attachments (1)' });
    expect(screen.getByRole('button', { name: 'Preview invoice-1042.pdf' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Download invoice-1042.pdf' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete invoice-1042.pdf' })).toBeNull();
    expect(screen.queryByLabelText('Attach files')).toBeNull();
  });

  it('Preview opens the side pane on the attachment', async () => {
    mockAttachments([PDF]);
    show();
    await userEvent.click(await screen.findByRole('button', { name: 'Preview invoice-1042.pdf' }));
    const pane = await screen.findByRole('dialog', { name: 'invoice-1042.pdf' });
    expect(within(pane).getByText(/1 of 1/)).toBeInTheDocument();
  });
});
