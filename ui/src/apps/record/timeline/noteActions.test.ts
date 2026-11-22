import { describe, expect, it, vi } from 'vitest';
import type { XrmApi } from '../../../shell/api/client';
import { createNote, deleteNote, downloadNote, maxUploadBytes, megabytesText, tooLargeMessage, updateNote } from './noteActions';

const parent = { entitySetName: 'xv_projects', id: 'p1', table: 'xv_project' };

function fakeApi(over: Record<string, unknown> = {}) {
  const api = { post: vi.fn().mockResolvedValue({ id: 'n1' }), patch: vi.fn().mockResolvedValue(undefined), delete: vi.fn().mockResolvedValue(undefined), get: vi.fn(), ...over };
  return { api: api as unknown as XrmApi, mock: api };
}

describe('noteActions', () => {
  it('create_binds_the_record_and_returns_the_id', async () => {
    const { api, mock } = fakeApi();
    expect(await createNote(api, parent, { subject: 'Kickoff', text: 'Agenda' })).toBe('n1');
    expect(mock.post).toHaveBeenCalledWith('annotations', { subject: 'Kickoff', notetext: 'Agenda', 'objectid_xv_project@odata.bind': '/xv_projects(p1)' });
  });

  it('create_with_file_sends_name_type_and_base64_body', async () => {
    const { api, mock } = fakeApi();
    await createNote(api, parent, { subject: 'S', text: '', file: new File(['hi'], 'a.txt', { type: 'text/plain' }) });
    expect(mock.post.mock.calls[0]![1]).toMatchObject({ filename: 'a.txt', mimetype: 'text/plain', documentbody: 'aGk=' });
  });

  it('create_leaves_the_mimetype_to_the_server_when_the_browser_has_none', async () => {
    const { api, mock } = fakeApi();
    await createNote(api, parent, { subject: 'S', text: '', file: new File(['hi'], 'a.xyz') });
    expect(mock.post.mock.calls[0]![1]).not.toHaveProperty('mimetype');
  });

  it('update_and_delete_use_the_note_row', async () => {
    const { api, mock } = fakeApi();
    await updateNote(api, 'n1', { subject: 'New', text: 'Body' });
    await deleteNote(api, 'n1');
    expect(mock.patch).toHaveBeenCalledWith('annotations(n1)', { subject: 'New', notetext: 'Body' });
    expect(mock.delete).toHaveBeenCalledWith('annotations(n1)');
  });

  it('max_upload_reads_the_organization_with_a_default', async () => {
    expect(await maxUploadBytes(fakeApi({ get: vi.fn().mockResolvedValue({ value: [{ maxuploadfilesize: 1048576 }] }) }).api)).toBe(1048576);
    expect(await maxUploadBytes(fakeApi({ get: vi.fn().mockResolvedValue({ value: [{}] }) }).api)).toBe(5242880);
  });

  it('too_large_names_the_limit_in_mb', () => {
    const file = new File([new Uint8Array(1536)], 'big.bin');
    expect(tooLargeMessage(file, 1024)).toBe('big.bin is larger than the 0 MB limit for attachments.');
    expect(tooLargeMessage(file, 2048)).toBeUndefined();
    expect(megabytesText(5 * 1024 * 1024)).toBe('5');
    expect(megabytesText(1.5 * 1024 * 1024)).toBe('1.5');
  });

  it('download_decodes_the_body_with_its_name', async () => {
    const { api } = fakeApi({ get: vi.fn().mockResolvedValue({ documentbody: 'aGk=', filename: 'a.txt', mimetype: 'text/plain' }) });
    const file = await downloadNote(api, 'n1');
    expect(file.name).toBe('a.txt');
    expect(file.blob.type).toBe('text/plain');
    expect(file.blob.size).toBe(2);
  });

  it('download_of_a_note_without_body_is_refused', async () => {
    await expect(downloadNote(fakeApi({ get: vi.fn().mockResolvedValue({ documentbody: null }) }).api, 'n1')).rejects.toThrow('no attachment');
  });
});
