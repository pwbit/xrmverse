import type { DownloadedFile } from '../../../admin/tables/fileApi';
import type { XrmApi } from '../../../shell/api/client';
import { XrmError } from '../../../shell/api/errors';
import { t } from '../../../shell/i18n/t';

/** The record a new note is attached to. */
export interface NoteParent {
  entitySetName: string;
  id: string;
  /** Its logical table name: the lookup `objectid_{table}` is bound on the Web API (docs/activities.md, "Notes and attachments"). */
  table: string;
}

export interface NewNote {
  subject: string;
  text: string;
  file?: File;
}

/** `organization.maxuploadfilesize` when the organization row does not carry it: the server default, 5 MB. */
export const DEFAULT_MAX_UPLOAD_BYTES = 5 * 1024 * 1024;

const MB = 1024 * 1024;

/** The limit as people read it, in MB (one decimal when it is not whole). */
export const megabytesText = (bytes: number): string => String(Math.round((bytes / MB) * 10) / 10);

/** `organizations?$select=maxuploadfilesize`: the largest note attachment in bytes. A form reads it once (through its cache). */
export async function maxUploadBytes(api: XrmApi): Promise<number> {
  const result = await api.get<{ value: { maxuploadfilesize?: number | null }[] }>('organizations?$select=maxuploadfilesize');
  return result.value[0]?.maxuploadfilesize ?? DEFAULT_MAX_UPLOAD_BYTES;
}

/** The refusal text of a file larger than `limit` bytes, naming the limit in MB; undefined when the file fits. */
export function tooLargeMessage(file: File, limit: number): string | undefined {
  return file.size > limit ? t('record.timeline.note.tooLarge', { name: file.name, limit: megabytesText(limit) }) : undefined;
}

/** The base64 of `blob`, read with a FileReader (every browser and the tests' DOM have it). */
export function base64Of(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ''));
    reader.onerror = () => reject(reader.error ?? new Error('The file could not be read.'));
    reader.readAsDataURL(blob);
  });
}

/**
 * Creates the note: `POST annotations` with the subject, the text, the record bound with `objectid_{table}@odata.bind` and,
 * for a file, `filename`, `mimetype` (only when the browser knows it: the server derives it from the extension otherwise)
 * and `documentbody` as base64. Returns the new note's id.
 */
export async function createNote(api: XrmApi, parent: NoteParent, note: NewNote): Promise<string> {
  const body: Record<string, unknown> = {
    subject: note.subject,
    notetext: note.text,
    [`objectid_${parent.table}@odata.bind`]: `/${parent.entitySetName}(${parent.id})`,
  };
  if (note.file) {
    body.filename = note.file.name;
    if (note.file.type) body.mimetype = note.file.type;
    body.documentbody = await base64Of(note.file);
  }
  const created = await api.post('annotations', body);
  return created.id ?? '';
}

/** Edit: one PATCH of the subject and the text (the attachment stays). */
export async function updateNote(api: XrmApi, id: string, note: { subject: string; text: string }): Promise<void> {
  await api.patch(`annotations(${id})`, { subject: note.subject, notetext: note.text });
}

export async function deleteNote(api: XrmApi, id: string): Promise<void> {
  await api.delete(`annotations(${id})`);
}

/**
 * The note's attachment. A note's `documentbody` is no file column (its property route is refused, docs/activities.md), so
 * it is read as the base64 column with the name and type, and handed to the same `saveFile` the file columns use.
 */
export async function downloadNote(api: XrmApi, id: string): Promise<DownloadedFile> {
  const row = await api.get<{ documentbody?: string | null; filename?: string | null; mimetype?: string | null }>(
    `annotations(${id})?$select=documentbody,filename,mimetype`,
  );
  if (!row.documentbody) throw new XrmError(404, t('record.timeline.note.noFile'));
  const binary = atob(row.documentbody);
  const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0));
  return { blob: new Blob([bytes], { type: row.mimetype || 'application/octet-stream' }), name: row.filename || 'attachment' };
}
