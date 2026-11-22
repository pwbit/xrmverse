import type { XrmApi } from '../../shell/api/client';
import { XrmError } from '../../shell/api/errors';
import { t } from '../../shell/i18n/t';

/**
 * File and image columns through the Web API (src/Xrmd.WebApi/FileEndpoints.cs, Learn "Use file column data"):
 * `PATCH {set}({id})/{column}` uploads, `GET .../$value` downloads, `DELETE {set}({id})/{column}` clears.
 */

/** The largest block Dataverse takes (4 MB): a larger file is uploaded in chunks, a smaller one in a single request. */
export const CHUNK_SIZE = 4194304;

const DATA_ROOT = '/api/data/v9.2/';
const FILE_NAME = 'x-ms-file-name';

export type UploadProgress = (sent: number, total: number) => void;

export interface DownloadedFile {
  blob: Blob;
  /** The name the server sends in `x-ms-file-name`. */
  name: string;
}

const columnPath = (set: string, id: string, column: string) => `${set}(${id})/${column}`;

/**
 * Where the file name goes: the `x-ms-file-name` header, which takes printable ASCII only, or else the query parameter of the
 * same name (FileEndpoints.FileName).
 */
function nameOf(fileName: string): { query: string; headers: Record<string, string> } {
  return /^[\x20-\x7e]+$/.test(fileName)
    ? { query: '', headers: { [FILE_NAME]: fileName } }
    : { query: `?${FILE_NAME}=${encodeURIComponent(fileName)}`, headers: {} };
}

/**
 * Uploads `file` to the column: a single `PATCH` with the bytes when it is at most {@link CHUNK_SIZE}; otherwise the
 * chunked protocol: a `PATCH` with `x-ms-transfer-mode: chunked` opens a session and answers with its `Location`
 * (`...?sessiontoken=...`) and `x-ms-chunk-size`, then each chunk is a `PATCH` on that Location with
 * `Content-Range: bytes from-to/total`, in order. `onProgress` gets the bytes sent so far after each request. Aborting
 * `signal` cancels the request in flight and sends no further chunk (the server's session then expires unused).
 */
export async function upload(
  api: XrmApi,
  set: string,
  id: string,
  column: string,
  file: File,
  onProgress: UploadProgress,
  signal?: AbortSignal,
): Promise<void> {
  const path = columnPath(set, id, column);
  const name = nameOf(file.name);
  const total = file.size;
  if (total <= CHUNK_SIZE) {
    await api.patchRaw(path + name.query, await bytesOf(file), name.headers, signal);
    onProgress(total, total);
    return;
  }
  const start = await api.patchRaw(path + name.query, null, { ...name.headers, 'x-ms-transfer-mode': 'chunked' }, signal);
  const session = sessionPath(start.headers.get('Location'));
  const sizeHeader = start.headers.get('x-ms-chunk-size') ?? '';
  const chunkSize = /^\d+$/.test(sizeHeader) ? Number(sizeHeader) : 0;
  if (chunkSize <= 0) throw new XrmError(start.status, t('file.err.noChunkSize', { value: sizeHeader }));
  for (let from = 0; from < total; from += chunkSize) {
    const to = Math.min(from + chunkSize, total) - 1;
    const chunk = await bytesOf(file.slice(from, to + 1));
    signal?.throwIfAborted();
    await api.patchRaw(session, chunk, { 'Content-Range': `bytes ${from}-${to}/${total}` }, signal);
    onProgress(to + 1, total);
  }
}

/** The bytes of `blob`, read with a FileReader (which every browser and the tests' DOM have). */
function bytesOf(blob: Blob): Promise<ArrayBuffer> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as ArrayBuffer);
    reader.onerror = () => reject(reader.error ?? new Error(`The file could not be read.`));
    reader.readAsArrayBuffer(blob);
  });
}

/**
 * The upload session's `Location`, relative to the Web API root. The server writes it absolute with the host it was reached
 * at (which a proxy may have changed): its path and query are sent to this origin, and only under the Web API.
 */
function sessionPath(location: string | null): string {
  if (!location) throw new XrmError(0, t('file.err.noLocation'));
  const url = new URL(location, globalThis.location?.origin);
  if (!url.pathname.startsWith(DATA_ROOT)) throw new XrmError(0, t('file.err.badLocation', { location }));
  return url.pathname.slice(DATA_ROOT.length) + url.search;
}

/**
 * Downloads the column's file (an image column's thumbnail, or its full image with `full`) with the name the server sends
 * in `x-ms-file-name` (percent-encoded by the server when it is not ASCII or holds a `%`, so decoding it gives the name back). A full image the record does not have is
 * answered 204, and refused here by name.
 */
export async function download(api: XrmApi, set: string, id: string, column: string, opts?: { full?: boolean }): Promise<DownloadedFile> {
  const response = await api.getRaw(`${columnPath(set, id, column)}/$value${opts?.full ? '?size=full' : ''}`);
  if (response.status === 204) throw new XrmError(204, t('file.err.noFullImage'));
  const header = response.headers.get(FILE_NAME);
  if (!header) throw new XrmError(response.status, t('file.err.noFileName'));
  return { blob: response.blob, name: decodeName(header) };
}

function decodeName(header: string): string {
  try {
    return decodeURIComponent(header);
  } catch {
    return header;
  }
}

/** `DELETE {set}({id})/{column}`: the file, or the image with its full-size image, is deleted. */
export async function remove(api: XrmApi, set: string, id: string, column: string): Promise<void> {
  await api.delete(columnPath(set, id, column));
}

/** Hands the downloaded file to the browser, saved under its name. */
export function saveFile(file: DownloadedFile): void {
  const url = URL.createObjectURL(file.blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = file.name;
  anchor.click();
  // Released once the click's download has taken the blob.
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
