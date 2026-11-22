import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/server';
import { XrmApi } from '../../shell/api/client';
import { CHUNK_SIZE, download, remove, saveFile, upload } from './fileApi';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });
const RECORD_PATH = '/api/data/v9.2/xv_projects(R1)/xv_doc';

interface Seen {
  method: string;
  path: string;
  search: string;
  headers: Headers;
  body: Uint8Array;
}

/** Records every request on the record's file column (and its upload session), answering with `answer`. */
function capture(answer: (seen: Seen, index: number) => Response): Seen[] {
  const seen: Seen[] = [];
  server.use(
    http.all('*/api/data/v9.2/*', async ({ request }) => {
      const url = new URL(request.url);
      const s = { method: request.method, path: decodeURIComponent(url.pathname), search: url.search, headers: request.headers, body: new Uint8Array(await request.arrayBuffer()) };
      seen.push(s);
      return answer(s, seen.length - 1);
    }),
  );
  return seen;
}

const bytes = (size: number) => {
  const b = new Uint8Array(size);
  for (let i = 0; i < size; i++) b[i] = i % 251;
  return b;
};

/** Byte equality without a deep diff of megabytes on failure. */
const same = (a: Uint8Array, b: Uint8Array) => Buffer.from(a).equals(Buffer.from(b));

afterEach(() => vi.restoreAllMocks());

describe('fileApi', () => {
  it('Large_file_uses_chunked_protocol_with_content_range', async () => {
    const total = CHUNK_SIZE * 2 + 10;
    const content = bytes(total);
    const seen = capture((s, i) => {
      if (i === 0)
        return new HttpResponse(null, {
          status: 200,
          headers: { Location: `http://elsewhere.example${RECORD_PATH}?sessiontoken=tok%2Bx`, 'x-ms-chunk-size': String(CHUNK_SIZE) },
        });
      const last = s.headers.get('Content-Range')!.endsWith(`${total - 1}/${total}`);
      return new HttpResponse(null, { status: last ? 204 : 206 });
    });
    const progress: number[] = [];
    await upload(api(), 'xv_projects', 'R1', 'xv_doc', new File([content], 'big.bin'), (sent) => progress.push(sent));

    // The first request opens the session: no body, the name in x-ms-file-name.
    expect(seen[0]).toMatchObject({ method: 'PATCH', path: RECORD_PATH, search: '' });
    expect(seen[0].headers.get('x-ms-transfer-mode')).toBe('chunked');
    expect(seen[0].headers.get('x-ms-file-name')).toBe('big.bin');
    expect(seen[0].body.length).toBe(0);
    // Each chunk goes to the Location (same origin, its path and query), at most x-ms-chunk-size bytes, in order.
    const chunks = seen.slice(1);
    expect(chunks.map((c) => c.headers.get('Content-Range'))).toEqual([
      `bytes 0-${CHUNK_SIZE - 1}/${total}`,
      `bytes ${CHUNK_SIZE}-${CHUNK_SIZE * 2 - 1}/${total}`,
      `bytes ${CHUNK_SIZE * 2}-${total - 1}/${total}`,
    ]);
    for (const c of chunks) {
      expect(c).toMatchObject({ method: 'PATCH', path: RECORD_PATH, search: '?sessiontoken=tok%2Bx' });
      expect(c.headers.get('Content-Type')).toBe('application/octet-stream');
    }
    const joined = new Uint8Array(chunks.reduce((n, c) => n + c.body.length, 0));
    let offset = 0;
    for (const c of chunks) {
      joined.set(c.body, offset);
      offset += c.body.length;
    }
    expect(same(joined, content)).toBe(true);
    expect(progress).toEqual([CHUNK_SIZE, CHUNK_SIZE * 2, total]);
  });

  it('Small_file_uses_single_patch_with_x_ms_file_name', async () => {
    const seen = capture(() => new HttpResponse(null, { status: 204 }));
    const progress: number[] = [];
    await upload(api(), 'xv_projects', 'R1', 'xv_doc', new File([bytes(CHUNK_SIZE)], 'report.pdf'), (sent) => progress.push(sent));
    expect(seen).toHaveLength(1);
    expect(seen[0]).toMatchObject({ method: 'PATCH', path: RECORD_PATH, search: '' });
    expect(seen[0].headers.get('x-ms-file-name')).toBe('report.pdf');
    expect(seen[0].headers.get('x-ms-transfer-mode')).toBeNull();
    expect(seen[0].headers.get('Content-Type')).toBe('application/octet-stream');
    expect(same(seen[0].body, bytes(CHUNK_SIZE))).toBe(true);
    expect(progress).toEqual([CHUNK_SIZE]);
  });

  it('sends a name that is not ASCII in the x-ms-file-name query parameter (the header takes ASCII only)', async () => {
    const seen = capture(() => new HttpResponse(null, { status: 204 }));
    await upload(api(), 'xv_projects', 'R1', 'xv_doc', new File(['x'], 'Größe.txt'), () => {});
    expect(seen[0].headers.get('x-ms-file-name')).toBeNull();
    expect(new URLSearchParams(seen[0].search).get('x-ms-file-name')).toBe('Größe.txt');
  });

  it('refuses a session Location outside the Web API', async () => {
    capture(() => new HttpResponse(null, { status: 200, headers: { Location: 'http://x/other?sessiontoken=t', 'x-ms-chunk-size': String(CHUNK_SIZE) } }));
    await expect(upload(api(), 'xv_projects', 'R1', 'xv_doc', new File([bytes(CHUNK_SIZE + 1)], 'a.bin'), () => {})).rejects.toThrow(/Location/);
  });

  it('Download_uses_server_file_name', async () => {
    const seen = capture(() => new HttpResponse('hello', { status: 200, headers: { 'x-ms-file-name': 'Gr%C3%B6%C3%9Fe.txt', 'Content-Type': 'application/octet-stream' } }));
    const file = await download(api(), 'xv_projects', 'R1', 'xv_doc');
    expect(seen[0]).toMatchObject({ method: 'GET', path: `${RECORD_PATH}/$value`, search: '' });
    expect(file.name).toBe('Größe.txt');
    expect(await file.blob.text()).toBe('hello');

    // Saved under that name.
    const created = vi.fn(() => 'blob:fake');
    const revoked = vi.fn();
    Object.assign(URL, { createObjectURL: created, revokeObjectURL: revoked });
    let savedAs: string | undefined;
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      savedAs = this.download;
    });
    saveFile(file);
    expect(savedAs).toBe('Größe.txt');
    expect(created).toHaveBeenCalledWith(file.blob);
    await vi.waitFor(() => expect(revoked).toHaveBeenCalledWith('blob:fake'));
  });

  it.each(['100%25.txt', 'a%20b.txt', 'Größe 100%.txt', 'plain.txt'])('round-trips the name %s verbatim through upload and download', async (name) => {
    // A fake of the server: it keeps the name as sent, and percent-encodes it in the response header when it is not ASCII
    // or holds a '%' (FileEndpoints.HeaderValue).
    let stored = '';
    capture((s) => {
      if (s.method === 'PATCH') {
        stored = s.headers.get('x-ms-file-name') ?? new URLSearchParams(s.search).get('x-ms-file-name') ?? '';
        return new HttpResponse(null, { status: 204 });
      }
      const header = /^[\x20-\x7e]*$/.test(stored) && !stored.includes('%') ? stored : encodeURIComponent(stored);
      return new HttpResponse('x', { status: 200, headers: { 'x-ms-file-name': header } });
    });
    await upload(api(), 'xv_projects', 'R1', 'xv_doc', new File(['x'], name), () => {});
    expect(stored).toBe(name);
    expect((await download(api(), 'xv_projects', 'R1', 'xv_doc')).name).toBe(name);
  });

  it('stops a chunked upload when its signal is aborted', async () => {
    const controller = new AbortController();
    const seen = capture((_, i) => {
      if (i === 0)
        return new HttpResponse(null, { status: 200, headers: { Location: `http://x${RECORD_PATH}?sessiontoken=t`, 'x-ms-chunk-size': String(CHUNK_SIZE) } });
      controller.abort();
      return new HttpResponse(null, { status: 206 });
    });
    const sent = upload(api(), 'xv_projects', 'R1', 'xv_doc', new File([bytes(CHUNK_SIZE * 2 + 1)], 'big.bin'), () => {}, controller.signal);
    await expect(sent).rejects.toThrow();
    expect(seen).toHaveLength(2);
  });

  it('downloads the full image with size=full, and names a missing one', async () => {
    const seen = capture(() => new HttpResponse(null, { status: 204 }));
    await expect(download(api(), 'xv_projects', 'R1', 'xv_doc', { full: true })).rejects.toThrow(/no full-size image/i);
    expect(seen[0].search).toBe('?size=full');
  });

  it('remove sends DELETE on the column', async () => {
    const seen = capture(() => new HttpResponse(null, { status: 204 }));
    await remove(api(), 'xv_projects', 'R1', 'xv_doc');
    expect(seen[0]).toMatchObject({ method: 'DELETE', path: RECORD_PATH });
  });
});
