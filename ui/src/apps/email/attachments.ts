import type { DownloadedFile } from '../../admin/tables/fileApi';
import { loadRelationships } from '../../admin/tables/tableApi';
import type { XrmApi } from '../../shell/api/client';
import { XrmError } from '../../shell/api/errors';
import { t } from '../../shell/i18n/t';
import { base64Of, DEFAULT_MAX_UPLOAD_BYTES, megabytesText } from '../record/timeline/noteActions';

/** An email attachment (`activitymimeattachment`, plan 4.1b task 4) as the form lists it. */
export interface Attachment {
  id: string;
  fileName: string;
  /** Bytes (`filesize`). */
  size: number;
  mimeType?: string;
}

/** The organization's limits on attachments: `maxuploadfilesize` and the `blockedattachments` extensions. */
export interface AttachmentLimits {
  maxBytes: number;
  blocked: string[];
}

interface Row {
  activitymimeattachmentid: string;
  filename?: string | null;
  filesize?: number | null;
  mimetype?: string | null;
}

/** The attachments of the email, in their order (`attachmentnumber`). */
export async function listAttachments(api: XrmApi, emailId: string): Promise<Attachment[]> {
  const filter = `_objectid_value eq ${emailId}`;
  const result = await api.get<{ value: Row[] }>(
    `activitymimeattachments?$select=activitymimeattachmentid,filename,filesize,mimetype&$filter=${encodeURIComponent(filter)}&$orderby=attachmentnumber`,
  );
  return result.value.map((r) => ({ id: r.activitymimeattachmentid, fileName: r.filename || 'attachment', size: r.filesize ?? 0, ...(r.mimetype ? { mimeType: r.mimetype } : {}) }));
}

/** `organization.maxuploadfilesize` (5 MB when not set) and `blockedattachments` (`;`-separated extensions). */
export async function attachmentLimits(api: XrmApi): Promise<AttachmentLimits> {
  const result = await api.get<{ value: { maxuploadfilesize?: number | null; blockedattachments?: string | null }[] }>(
    'organizations?$select=maxuploadfilesize,blockedattachments',
  );
  const org = result.value[0];
  return {
    maxBytes: org?.maxuploadfilesize ?? DEFAULT_MAX_UPLOAD_BYTES,
    blocked: (org?.blockedattachments ?? '')
      .split(';')
      .map((e) => e.trim().replace(/^\*/, '').replace(/^\./, '').toLowerCase())
      .filter(Boolean),
  };
}

/** The extension the server checks: trailing dots and spaces dropped, as Windows saves the file. */
export const extensionOf = (fileName: string): string => {
  const trimmed = fileName.replace(/[.\s]+$/, '');
  const dot = trimmed.lastIndexOf('.');
  return dot < 0 ? '' : trimmed.slice(dot + 1).trim().toLowerCase();
};

/** Why the file cannot be attached (larger than the limit, a blocked extension); undefined when it can. The server stays the authority. */
export function refusal(file: File, limits: AttachmentLimits): string | undefined {
  if (file.size > limits.maxBytes) return t('email.attachments.tooLarge', { name: file.name, limit: megabytesText(limits.maxBytes) });
  const extension = extensionOf(file.name);
  if (extension && limits.blocked.includes(extension)) return t('email.attachments.blocked', { name: file.name, extension: `.${extension}` });
  return undefined;
}

/**
 * The navigation property that binds an attachment to its email (`activitymimeattachment.objectid` to `email`), read from the
 * relationships, never guessed.
 */
async function emailBinding(api: XrmApi): Promise<string> {
  const rels = await loadRelationships(api);
  const nav = rels.oneToMany.find((r) => r.referencingEntity === 'activitymimeattachment' && r.referencingAttribute === 'objectid' && r.referencedEntity === 'email')?.nav;
  if (!nav) throw new Error(t('email.attachments.noRelationship'));
  return nav;
}

/** `POST activitymimeattachments`: the file name, its type when the browser knows it, the body as base64, bound to the email (`objecttypecode` email). */
export async function uploadAttachment(api: XrmApi, emailId: string, file: File): Promise<void> {
  const nav = await emailBinding(api);
  await api.post('activitymimeattachments', {
    filename: file.name,
    ...(file.type ? { mimetype: file.type } : {}),
    body: await base64Of(file),
    [`${nav}@odata.bind`]: `/emails(${emailId})`,
    // As Dataverse's samples send it; the server checks it names the bound record's table.
    objecttypecode: 'email',
  });
}

export const deleteAttachment = (api: XrmApi, id: string): Promise<void> => api.delete(`activitymimeattachments(${id})`);

/**
 * The attachment's file, read as the note body is (`body` is base64, no file column), for the same `saveFile` the file columns
 * use, or for the preview. Its type is the one the server stored.
 */
export async function downloadAttachment(api: XrmApi, id: string): Promise<DownloadedFile> {
  const row = await api.get<{ body?: string | null; filename?: string | null; mimetype?: string | null }>(`activitymimeattachments(${id})?$select=body,filename,mimetype`);
  if (row.body === null || row.body === undefined) throw new XrmError(404, t('email.attachments.noBody'));
  const binary = atob(row.body);
  const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0));
  return { blob: new Blob([bytes], { type: row.mimetype || 'application/octet-stream' }), name: row.filename || 'attachment' };
}

/** A size as people read it: bytes, KB or MB. */
export function sizeText(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${megabytesText(bytes)} MB`;
}
