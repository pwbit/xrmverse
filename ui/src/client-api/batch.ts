/** One Web API call of the Client API: a path under `/api/data/v9.2/`, with its JSON body and headers. */
export interface Call {
  method: string;
  path: string;
  body?: unknown;
  headers?: Record<string, string>;
}

/** One response of a `$batch` response. */
export interface BatchPart {
  status: number;
  statusText: string;
  headers: Record<string, string>;
  body: string;
}

const CRLF = '\r\n';

function part(call: Call, contentId?: number): string {
  const head = [`Content-Type: application/http`, `Content-Transfer-Encoding: binary`, ...(contentId ? [`Content-ID: ${contentId}`] : [])];
  const headers = { ...(call.body !== undefined ? { 'Content-Type': 'application/json; type=entry' } : {}), ...call.headers };
  const lines = [...head, '', `${call.method} ${call.path} HTTP/1.1`, ...Object.entries(headers).map(([k, v]) => `${k}: ${v}`), ''];
  return lines.join(CRLF) + CRLF + (call.body !== undefined ? JSON.stringify(call.body) : '') + CRLF;
}

/**
 * The multipart/mixed body of a `$batch` (Learn, "Execute batch operations using the Web API"): a call alone is a request, an
 * array of calls a change set, whose requests get Content-IDs 1, 2, …
 */
export function batchBody(items: (Call | Call[])[]): { body: string; boundary: string } {
  const id = crypto.randomUUID();
  const boundary = `batch_${id}`;
  let body = '';
  items.forEach((item, i) => {
    body += `--${boundary}${CRLF}`;
    if (!Array.isArray(item)) {
      body += part(item);
      return;
    }
    const changeSet = `changeset_${id}_${i}`;
    body += `Content-Type: multipart/mixed; boundary=${changeSet}${CRLF}${CRLF}`;
    item.forEach((call, n) => (body += `--${changeSet}${CRLF}${part(call, n + 1)}`));
    body += `--${changeSet}--${CRLF}`;
  });
  return { body: body + `--${boundary}--${CRLF}`, boundary };
}

const boundaryOf = (contentType: string) => /boundary="?([^";]+)"?/i.exec(contentType)?.[1];

/** The responses of a `$batch` response body in order, those of a change set in place of it. */
export function parseBatch(text: string, contentType: string): BatchPart[] {
  const boundary = boundaryOf(contentType);
  if (!boundary) return [];
  const parts: BatchPart[] = [];
  for (const chunk of text.split(`--${boundary}`).slice(1)) {
    if (chunk.startsWith('--')) break;
    const nested = /Content-Type:\s*multipart\/mixed;\s*boundary=([^\r\n]+)/i.exec(chunk);
    if (nested) {
      parts.push(...parseBatch(chunk, `multipart/mixed; boundary=${nested[1].trim()}`));
      continue;
    }
    const start = chunk.indexOf('HTTP/1.1 ');
    if (start < 0) continue;
    const message = chunk.slice(start).replace(/\r\n/g, '\n');
    const split = message.indexOf('\n\n');
    const [statusLine, ...headerLines] = (split < 0 ? message : message.slice(0, split)).split('\n');
    const [, status, ...reason] = statusLine.split(' ');
    const headers: Record<string, string> = {};
    for (const line of headerLines) {
      const colon = line.indexOf(':');
      if (colon > 0) headers[line.slice(0, colon).trim()] = line.slice(colon + 1).trim();
    }
    parts.push({ status: Number(status), statusText: reason.join(' '), headers, body: split < 0 ? '' : message.slice(split + 2).trim() });
  }
  return parts;
}

export function parseJson(text: string): { error?: { code?: unknown; message?: unknown } } | undefined {
  try {
    return JSON.parse(text) as { error?: { code?: unknown; message?: unknown } };
  } catch {
    return undefined;
  }
}
