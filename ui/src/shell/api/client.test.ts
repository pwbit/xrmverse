import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../test/server';
import { XrmApi } from './client';
import { XrmError } from './errors';
import { literal, key } from './odata';

function makeApi() {
  const onUnauthorized = vi.fn();
  const api = new XrmApi({ getToken: async () => 'tok', onUnauthorized });
  return { api, onUnauthorized };
}

describe('XrmApi', () => {
  it('Sends_bearer_and_odata_headers', async () => {
    let headers: Headers | undefined;
    server.use(
      http.get('*/api/data/v9.2/accounts', ({ request }) => {
        headers = request.headers;
        return HttpResponse.json({ value: [] });
      }),
    );
    await makeApi().api.get('accounts');
    expect(headers?.get('Authorization')).toBe('Bearer tok');
    expect(headers?.get('OData-Version')).toBe('4.0');
    expect(headers?.get('Accept')).toBe('application/json');
  });

  it('Parses_dataverse_error_into_XrmError', async () => {
    server.use(
      http.get('*/api/data/v9.2/accounts', () =>
        HttpResponse.json({ error: { code: '0x80040217', message: 'Does Not Exist' } }, { status: 400 }),
      ),
    );
    const error = await makeApi().api.get('accounts').catch((e: unknown) => e);
    expect(error).toBeInstanceOf(XrmError);
    expect(error).toMatchObject({ status: 400, code: '0x80040217', serverMessage: 'Does Not Exist' });
  });

  it('Admin_403_keeps_0x80040220', async () => {
    server.use(
      http.get('*/admin/health', () =>
        HttpResponse.json({ error: { code: '0x80040220', message: 'Access denied' } }, { status: 403 }),
      ),
    );
    const { api, onUnauthorized } = makeApi();
    await expect(api.admin('GET', 'health')).rejects.toMatchObject({ status: 403, code: '0x80040220' });
    expect(onUnauthorized).not.toHaveBeenCalled();
  });

  it('401_calls_onUnauthorized_once_and_rejects', async () => {
    server.use(http.get('*/api/data/v9.2/accounts', () => new HttpResponse(null, { status: 401 })));
    const { api, onUnauthorized } = makeApi();
    await expect(api.get('accounts')).rejects.toMatchObject({ status: 401, code: undefined });
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
  });

  it('Network_failure_is_XrmError_status_0_with_message', async () => {
    const api = new XrmApi({
      getToken: async () => 'tok',
      onUnauthorized: vi.fn(),
      fetch: () => Promise.reject(new TypeError('boom')),
    });
    const error = await api.get('accounts').catch((e: unknown) => e);
    expect(error).toBeInstanceOf(XrmError);
    expect(error).toMatchObject({ status: 0, serverMessage: 'boom' });
  });

  it('non_json_error_body_uses_status_text', async () => {
    server.use(
      http.get('*/api/data/v9.2/accounts', () => new HttpResponse('<html>', { status: 502, statusText: 'Bad Gateway' })),
    );
    await expect(makeApi().api.get('accounts')).rejects.toMatchObject({
      status: 502,
      code: undefined,
      serverMessage: 'Bad Gateway',
    });
  });

  it('page_sends_Prefer_maxpagesize', async () => {
    const prefer: (string | null)[] = [];
    server.use(
      http.get('*/api/data/v9.2/accounts', ({ request }) => {
        prefer.push(request.headers.get('Prefer'));
        return HttpResponse.json({ value: [{ n: 1 }], '@odata.nextLink': 'http://localhost:3000/api/data/v9.2/accounts?$skiptoken=x' });
      }),
    );
    const { api } = makeApi();
    const page = await api.page('accounts', 25);
    expect(prefer).toEqual(['odata.maxpagesize=25']);
    expect(page.nextLink).toContain('$skiptoken=x');
    await api.page('accounts');
    expect(prefer[1]).toBe('odata.maxpagesize=50');
  });

  it('list_follows_nextLink_until_absent', async () => {
    const seen: string[] = [];
    server.use(
      http.get('*/api/data/v9.2/accounts', ({ request }) => {
        const url = new URL(request.url);
        seen.push(url.search);
        return url.searchParams.get('$skiptoken')
          ? HttpResponse.json({ value: [{ n: 3 }] })
          : HttpResponse.json({
              value: [{ n: 1 }, { n: 2 }],
              '@odata.nextLink': `${url.origin}/api/data/v9.2/accounts?$skiptoken=abc`,
            });
      }),
    );
    const pages: { n: number }[][] = [];
    for await (const p of makeApi().api.list<{ n: number }>('accounts')) pages.push(p);
    expect(pages).toEqual([[{ n: 1 }, { n: 2 }], [{ n: 3 }]]);
    expect(seen).toEqual(['', '?$skiptoken=abc']);
  });

  it('post_returns_id_from_OData_EntityId', async () => {
    let body: unknown;
    server.use(
      http.post('*/api/data/v9.2/accounts', async ({ request }) => {
        body = await request.json();
        return new HttpResponse(null, {
          status: 204,
          headers: { 'OData-EntityId': 'http://localhost/api/data/v9.2/accounts(11111111-2222-3333-4444-555555555555)' },
        });
      }),
    );
    const result = await makeApi().api.post('accounts', { name: 'x' });
    expect(result).toEqual({ id: '11111111-2222-3333-4444-555555555555', body: undefined });
    expect(body).toEqual({ name: 'x' });
  });

  it('patch_delete_and_action_use_the_data_root', async () => {
    const calls: string[] = [];
    server.use(
      http.patch(/\/api\/data\/v9\.2\/accounts\(1\)$/, () => (calls.push('patch'), new HttpResponse(null, { status: 204 }))),
      http.delete(/\/api\/data\/v9\.2\/accounts\(1\)$/, () => (calls.push('delete'), new HttpResponse(null, { status: 204 }))),
      http.post('*/api/data/v9.2/WhoAmI', () => HttpResponse.json({ UserId: 'u' })),
    );
    const { api } = makeApi();
    await api.patch('accounts(1)', { name: 'y' });
    await api.delete('accounts(1)');
    expect(await api.action('WhoAmI', {})).toEqual({ UserId: 'u' });
    expect(calls).toEqual(['patch', 'delete']);
  });

  it('associate_and_disassociate_use_ref_urls', async () => {
    let posted: unknown;
    let deleted = '';
    server.use(
      http.post(/\/api\/data\/v9\.2\/teams\(t1\)\/teammembership_association\/\$ref$/, async ({ request }) => {
        posted = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
      http.delete(/\/api\/data\/v9\.2\/teams\(t1\)\/teammembership_association\(u1\)\/\$ref$/, ({ request }) => {
        deleted = new URL(request.url).pathname;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const { api } = makeApi();
    await api.associate('teams(t1)', 'teammembership_association', 'systemusers(u1)');
    await api.disassociate('teams(t1)', 'teammembership_association', 'u1');
    expect((posted as Record<string, string>)['@odata.id']).toMatch(/\/api\/data\/v9\.2\/systemusers\(u1\)$/);
    expect(deleted).toBe('/api/data/v9.2/teams(t1)/teammembership_association(u1)/$ref');
  });

  it('download_returns_a_Blob', async () => {
    server.use(http.get('*/api/data/v9.2/files', () => new HttpResponse('abc', { headers: { 'Content-Type': 'text/plain' } })));
    const blob = await makeApi().api.download('files');
    expect(await blob.text()).toBe('abc');
  });

  it('patchRaw_sends_the_bytes_and_returns_status_and_headers', async () => {
    let seen: { type: string | null; name: string | null; body: string } | undefined;
    server.use(
      http.patch('*/api/data/v9.2/files', async ({ request }) => {
        seen = { type: request.headers.get('Content-Type'), name: request.headers.get('x-ms-file-name'), body: await request.text() };
        return new HttpResponse(null, { status: 206, headers: { 'x-ms-chunk-size': '4194304' } });
      }),
    );
    const result = await makeApi().api.patchRaw('files', new TextEncoder().encode('abc').buffer as ArrayBuffer, { 'x-ms-file-name': 'a.txt' });
    expect(seen).toEqual({ type: 'application/octet-stream', name: 'a.txt', body: 'abc' });
    expect(result.status).toBe(206);
    expect(result.headers.get('x-ms-chunk-size')).toBe('4194304');
  });

  it('patchRaw_without_a_body_sends_no_content_type_and_throws_the_server_error', async () => {
    let type: string | null = 'unset';
    server.use(
      http.patch('*/api/data/v9.2/files', ({ request }) => {
        type = request.headers.get('Content-Type');
        return HttpResponse.json({ error: { code: '0x80044a02', message: 'The file is larger than the maximum size of the column.' } }, { status: 400 });
      }),
    );
    await expect(makeApi().api.patchRaw('files', null, { 'x-ms-transfer-mode': 'chunked' })).rejects.toMatchObject({ code: '0x80044a02' });
    expect(type).toBeNull();
  });

  it('getRaw_returns_status_headers_and_a_Blob', async () => {
    server.use(http.get('*/api/data/v9.2/files', () => new HttpResponse('abc', { headers: { 'x-ms-file-name': 'a.txt' } })));
    const result = await makeApi().api.getRaw('files');
    expect(result.status).toBe(200);
    expect(result.headers.get('x-ms-file-name')).toBe('a.txt');
    expect(await result.blob.text()).toBe('abc');
  });

  it('admin_targets_admin_root_with_body', async () => {
    let body: unknown;
    server.use(
      http.post('*/admin/things', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({ ok: true });
      }),
    );
    expect(await makeApi().api.admin('POST', 'things', { a: 1 })).toEqual({ ok: true });
    expect(body).toEqual({ a: 1 });
  });

  it('admin_acceptStatus_returns_the_body_of_a_listed_non_2xx_status_only', async () => {
    server.use(
      http.get('*/admin/health', () => HttpResponse.json({ status: 'Unhealthy' }, { status: 503 })),
      http.get('*/admin/other', () => HttpResponse.json({ error: { code: '0x1', message: 'down' } }, { status: 502 })),
    );
    const { api } = makeApi();
    expect(await api.admin('GET', 'health', undefined, { acceptStatus: [503] })).toEqual({ status: 'Unhealthy' });
    await expect(api.admin('GET', 'health')).rejects.toMatchObject({ status: 503 });
    await expect(api.admin('GET', 'other', undefined, { acceptStatus: [503] })).rejects.toMatchObject({ status: 502, code: '0x1' });
  });
});

describe('XrmApi hardening', () => {
  it('invalid_2xx_json_body_is_XrmError', async () => {
    server.use(
      http.get('*/api/data/v9.2/a', () => new HttpResponse('<html>', { status: 200 })),
      http.post('*/api/data/v9.2/b', () => new HttpResponse('nope', { status: 200 })),
    );
    const { api } = makeApi();
    await expect(api.get('a')).rejects.toMatchObject({ status: 200, serverMessage: expect.stringContaining('not valid JSON') });
    await expect(api.post('b', {})).rejects.toBeInstanceOf(XrmError);
  });

  it('getToken_failure_is_401_and_calls_onUnauthorized_once', async () => {
    const onUnauthorized = vi.fn();
    const api = new XrmApi({ getToken: () => Promise.reject(new Error('expired')), onUnauthorized });
    await expect(api.get('a')).rejects.toMatchObject({ status: 401, serverMessage: 'expired' });
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
  });

  it('abort_rethrows_AbortError', async () => {
    server.use(http.get('*/api/data/v9.2/a', () => HttpResponse.json({})));
    const ctrl = new AbortController();
    ctrl.abort();
    const error = await makeApi().api.get('a', { signal: ctrl.signal }).catch((e: unknown) => e);
    expect(error).not.toBeInstanceOf(XrmError);
    expect((error as Error).name).toBe('AbortError');
  });

  it('page_refuses_foreign_origin_nextLink_without_sending_token', async () => {
    const fetchSpy = vi.fn();
    const getToken = vi.fn(async () => 'tok');
    const api = new XrmApi({ getToken, onUnauthorized: vi.fn(), fetch: fetchSpy });
    await expect(api.page('https://evil.example/api/data/v9.2/a?$skiptoken=1')).rejects.toMatchObject({ status: 0 });
    expect(fetchSpy).not.toHaveBeenCalled();
    expect(getToken).not.toHaveBeenCalled();
  });

  it('caller_cannot_override_Authorization_or_OData_Version', async () => {
    let headers: Headers | undefined;
    server.use(
      http.post('*/api/data/v9.2/a', ({ request }) => {
        headers = request.headers;
        return new HttpResponse(null, { status: 204 });
      }),
    );
    await makeApi().api.post('a', {}, { authorization: 'Bearer evil', 'OData-Version': '9.9', 'X-Custom': 'ok' });
    expect(headers?.get('Authorization')).toBe('Bearer tok');
    expect(headers?.get('OData-Version')).toBe('4.0');
    expect(headers?.get('X-Custom')).toBe('ok');
  });

  it('caller_Prefer_is_merged_with_maxpagesize', async () => {
    let prefer: string | null = null;
    server.use(
      http.get('*/api/data/v9.2/a', ({ request }) => {
        prefer = request.headers.get('Prefer');
        return HttpResponse.json({ value: [] });
      }),
    );
    await makeApi().api.page('a', 10, { headers: { Prefer: 'odata.include-annotations="*"' } });
    expect(prefer).toBe('odata.maxpagesize=10, odata.include-annotations="*"');
  });

  it('empty_2xx_body_returns_undefined_from_get_and_admin', async () => {
    server.use(
      http.get('*/api/data/v9.2/a', () => new HttpResponse(null, { status: 204 })),
      http.get('*/admin/x', () => new HttpResponse(null, { status: 204 })),
    );
    const { api } = makeApi();
    expect(await api.get('a')).toBeUndefined();
    expect(await api.admin('GET', 'x')).toBeUndefined();
  });
});

describe('odata', () => {
  it('key_wraps_id_in_parentheses', () => {
    expect(key('abc')).toBe('(abc)');
  });

  it('literal_doubles_quotes', () => {
    expect(literal("O'Brien")).toBe("'O''Brien'");
    expect(key('abc')).toBe('(abc)');
  });
});
