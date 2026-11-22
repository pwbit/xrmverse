import { t } from '../i18n/t';
import { XrmError } from './errors';

const DATA_ROOT = '/api/data/v9.2/';
const ADMIN_ROOT = '/admin/';
const AUTHORING_ROOT = '/authoring/';
const SESSION = '/api/session';
const DEFAULT_PAGE_SIZE = 50;

export interface XrmApiOptions {
  getToken: () => Promise<string>;
  onUnauthorized: () => void;
  fetch?: typeof fetch;
}

export interface Page<T> {
  value: T[];
  nextLink?: string;
}

export interface RequestExtras {
  headers?: Record<string, string>;
  signal?: AbortSignal;
}

/** Web API (`/api/data/v9.2/`), Admin API (`/admin/`) and authoring API (`/authoring/`) client; every failure is an {@link XrmError}. */
export class XrmApi {
  private readonly getToken: () => Promise<string>;
  private readonly onUnauthorized: () => void;
  private readonly fetchImpl: typeof fetch;

  /**
   * `POST /api/session` with `token` as the bearer header: the server sets the `xrmd.session` cookie (HttpOnly, SameSite=Strict,
   * expiring with the token), so that web resources and form scripts reach the API with the same token.
   */
  static async session(token: string, fetchImpl: typeof fetch = (input, init) => globalThis.fetch(input, init)): Promise<void> {
    await XrmApi.sessionRequest(fetchImpl, 'POST', { Authorization: `Bearer ${token}` });
  }

  /** `DELETE /api/session` with `token` as the bearer header: clears the cookie. */
  static async endSession(token: string, fetchImpl: typeof fetch = (input, init) => globalThis.fetch(input, init)): Promise<void> {
    await XrmApi.sessionRequest(fetchImpl, 'DELETE', { Authorization: `Bearer ${token}` });
  }

  private static async sessionRequest(fetchImpl: typeof fetch, method: string, headers: Record<string, string>): Promise<void> {
    let response: Response;
    try {
      response = await fetchImpl(SESSION, { method, headers, credentials: 'same-origin' });
    } catch (e) {
      throw new XrmError(0, e instanceof Error && e.message ? e.message : t('api.error.network'));
    }
    if (!response.ok) throw await toError(response);
  }

  constructor(opts: XrmApiOptions) {
    this.getToken = opts.getToken;
    this.onUnauthorized = opts.onUnauthorized;
    this.fetchImpl = opts.fetch ?? ((input, init) => globalThis.fetch(input, init));
  }

  get<T>(path: string, init?: RequestExtras): Promise<T> {
    return this.json<T>('GET', DATA_ROOT + path, undefined, init?.headers, init?.signal);
  }

  async *list<T>(path: string, pageSize = DEFAULT_PAGE_SIZE): AsyncIterable<T[]> {
    let next: string | undefined = path;
    while (next) {
      const page: Page<T> = await this.page<T>(next, pageSize);
      yield page.value;
      next = page.nextLink;
    }
  }

  /** `pathOrNextLink` is relative to the Web API root or an absolute `@odata.nextLink` URL. */
  async page<T>(pathOrNextLink: string, pageSize = DEFAULT_PAGE_SIZE, init?: RequestExtras): Promise<Page<T>> {
    const absolute = /^https?:\/\//i.test(pathOrNextLink);
    if (absolute && new URL(pathOrNextLink).origin !== globalThis.location?.origin) {
      throw new XrmError(0, t('api.error.foreignNextLink'));
    }
    const url = absolute ? pathOrNextLink : DATA_ROOT + pathOrNextLink;
    const callerPrefer = Object.entries(init?.headers ?? {}).find(([k]) => k.toLowerCase() === 'prefer')?.[1];
    const prefer = [`odata.maxpagesize=${pageSize}`, callerPrefer].filter(Boolean).join(', ');
    const body = await this.json<{ value?: T[]; '@odata.nextLink'?: string }>(
      'GET',
      url,
      undefined,
      { ...init?.headers, Prefer: prefer },
      init?.signal,
    );
    return { value: body.value ?? [], nextLink: body['@odata.nextLink'] };
  }

  async post<T>(path: string, body: unknown, headers?: Record<string, string>): Promise<{ id?: string; body?: T }> {
    const response = await this.send('POST', DATA_ROOT + path, body, headers);
    const entityId = response.headers.get('OData-EntityId');
    const id = entityId ? /\(([^()]*)\)\s*$/.exec(entityId)?.[1] : undefined;
    return { id, body: parseBody<T>(response, await response.text()) };
  }

  async patch(path: string, body: unknown, headers?: Record<string, string>): Promise<void> {
    await this.send('PATCH', DATA_ROOT + path, body, headers);
  }

  /** `PUT` of a whole definition (the metadata API replaces the definition it is given). */
  async put(path: string, body: unknown, headers?: Record<string, string>): Promise<void> {
    await this.send('PUT', DATA_ROOT + path, body, headers);
  }

  async delete(path: string): Promise<void> {
    await this.send('DELETE', DATA_ROOT + path);
  }

  /** `POST {path}/{nav}/$ref` with `{"@odata.id": <absolute record url>}`; `targetPath` is e.g. `roles(<id>)`. */
  async associate(path: string, nav: string, targetPath: string): Promise<void> {
    const origin = globalThis.location?.origin ?? '';
    await this.send('POST', `${DATA_ROOT}${path}/${nav}/$ref`, { '@odata.id': `${origin}${DATA_ROOT}${targetPath}` });
  }

  /** `DELETE {path}/{nav}({targetId})/$ref`. */
  async disassociate(path: string, nav: string, targetId: string): Promise<void> {
    await this.send('DELETE', `${DATA_ROOT}${path}/${nav}(${targetId})/$ref`);
  }

  action<T>(name: string, body: unknown): Promise<T> {
    return this.json<T>('POST', DATA_ROOT + name, body);
  }

  /**
   * `PATCH` with a raw body (`application/octet-stream` unless `headers` names another type; no body and no Content-Type
   * when `body` is null), as the file and image upload requests send it. Returns the status and headers the protocol reads
   * (`Location`, `x-ms-chunk-size`).
   */
  async patchRaw(
    path: string,
    body: ArrayBuffer | null,
    headers?: Record<string, string>,
    signal?: AbortSignal,
  ): Promise<{ status: number; headers: Headers }> {
    const response = await this.send('PATCH', DATA_ROOT + path, body === null ? undefined : new RawBody(body), headers, signal);
    await response.arrayBuffer();
    return { status: response.status, headers: response.headers };
  }

  /** `GET` of raw content (a file's `$value`): its status, its headers (`x-ms-file-name`, ...) and its bytes. */
  async getRaw(path: string, headers?: Record<string, string>): Promise<{ status: number; headers: Headers; blob: Blob }> {
    const response = await this.send('GET', DATA_ROOT + path, undefined, headers);
    return { status: response.status, headers: response.headers, blob: await response.blob() };
  }

  async download(path: string): Promise<Blob> {
    const response = await this.send('GET', DATA_ROOT + path);
    return response.blob();
  }

  /**
   * Any Web API request with the bearer header, returning the response itself (the Client API's `execute` hands its status,
   * headers and body to the script). A failure status still throws the {@link XrmError}.
   */
  request(method: string, path: string, body?: unknown, headers?: Record<string, string>): Promise<Response> {
    return this.send(method, DATA_ROOT + path, body, headers);
  }

  /**
   * A `$batch` request (#130): `body` is the multipart/mixed text with CRLF line breaks, `boundary` its boundary. The response is
   * returned whatever its status: a failed batch still carries the failed request's response in its body.
   */
  batch(body: string, boundary: string): Promise<Response> {
    const bytes = new TextEncoder().encode(body);
    const any = Array.from({ length: 200 }, (_, i) => 400 + i);
    return this.send('POST', `${DATA_ROOT}$batch`, new RawBody(bytes.buffer as ArrayBuffer, `multipart/mixed; boundary=${boundary}`), undefined, undefined, any);
  }

  /** `acceptStatus` lists non-2xx statuses whose body is a normal result (e.g. 503 on `health`), returned instead of thrown. */
  admin<T>(method: string, path: string, body?: unknown, opts?: { acceptStatus?: number[] }): Promise<T> {
    return this.json<T>(method, ADMIN_ROOT + path, body, undefined, undefined, opts?.acceptStatus);
  }

  /**
   * A request to the authoring API (`/authoring/`, e.g. `workflows/apply?dryRun=true`), returning the response itself. `body` is
   * sent as text with `contentType` (`text/yaml` by default); `accept` replaces the JSON `Accept` header (an export's text).
   * `acceptStatus` lists non-2xx statuses returned instead of thrown (a refused apply's 400 carries its result).
   */
  authoring(method: string, path: string, opts?: { body?: string; contentType?: string; accept?: string; acceptStatus?: number[] }): Promise<Response> {
    const body =
      opts?.body === undefined
        ? undefined
        : new RawBody(new TextEncoder().encode(opts.body).buffer as ArrayBuffer, opts.contentType ?? 'text/yaml; charset=utf-8');
    const headers = opts?.accept ? { Accept: opts.accept } : undefined;
    return this.send(method, AUTHORING_ROOT + path, body, headers, undefined, opts?.acceptStatus);
  }

  private async json<T>(
    method: string,
    url: string,
    body?: unknown,
    headers?: Record<string, string>,
    signal?: AbortSignal,
    acceptStatus?: number[],
  ): Promise<T> {
    const response = await this.send(method, url, body, headers, signal, acceptStatus);
    return parseBody<T>(response, await response.text()) as T;
  }

  /** `body` is sent as JSON, except a {@link RawBody}, which is sent as it is (see {@link patchRaw}). */
  private async send(
    method: string,
    url: string,
    body?: unknown | RawBody,
    headers?: Record<string, string>,
    signal?: AbortSignal,
    acceptStatus?: number[],
  ): Promise<Response> {
    let token: string;
    try {
      token = await this.getToken();
    } catch (e) {
      this.onUnauthorized();
      throw new XrmError(401, e instanceof Error && e.message ? e.message : t('api.error.noToken'));
    }
    const callerHeaders = Object.fromEntries(
      Object.entries(headers ?? {}).filter(([k]) => !['authorization', 'odata-version'].includes(k.toLowerCase())),
    );
    const raw = body instanceof RawBody ? body.raw : undefined;
    const init: RequestInit = {
      method,
      signal,
      headers: {
        Authorization: `Bearer ${token}`,
        'OData-Version': '4.0',
        'OData-MaxVersion': '4.0',
        Accept: 'application/json',
        ...(body !== undefined ? { 'Content-Type': raw ? ((body as RawBody).contentType ?? 'application/octet-stream') : 'application/json' } : {}),
        ...callerHeaders,
      },
      body: raw ?? (body !== undefined ? JSON.stringify(body) : undefined),
    };
    let response: Response;
    try {
      response = await this.fetchImpl(url, init);
    } catch (e) {
      if ((e as Error)?.name === 'AbortError') throw e;
      throw new XrmError(0, e instanceof Error && e.message ? e.message : t('api.error.network'));
    }
    if (response.ok || acceptStatus?.includes(response.status)) return response;
    if (response.status === 401) this.onUnauthorized();
    throw await toError(response);
  }
}

/** Bytes sent as they are, never as JSON. */
class RawBody {
  constructor(
    readonly raw: ArrayBuffer,
    readonly contentType?: string,
  ) {}
}

async function toError(response: Response): Promise<XrmError> {
  const fallback = response.statusText || t('api.error.empty', { status: response.status });
  try {
    const parsed = (await response.json()) as { error?: { code?: unknown; message?: unknown } };
    const err = parsed?.error;
    if (err && typeof err === 'object') {
      return new XrmError(
        response.status,
        typeof err.message === 'string' && err.message ? err.message : fallback,
        typeof err.code === 'string' ? err.code : undefined,
      );
    }
  } catch {
    // Non-JSON or empty body: fall through to the status text.
  }
  return new XrmError(response.status, fallback);
}

function parseBody<T>(response: Response, text: string): T | undefined {
  if (!text) return undefined;
  try {
    return JSON.parse(text) as T;
  } catch {
    throw new XrmError(response.status, t('api.error.invalidBody'));
  }
}
