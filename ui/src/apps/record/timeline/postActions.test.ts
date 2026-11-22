import { describe, expect, it, vi } from 'vitest';
import type { XrmApi } from '../../../shell/api/client';
import { createComment, createPost, deleteComment, deletePost } from './postActions';

function fakeApi(id = 'new1') {
  const api = { post: vi.fn().mockResolvedValue({ id }), delete: vi.fn().mockResolvedValue(undefined) };
  return { api: api as unknown as XrmApi, mock: api };
}

describe('postActions', () => {
  it('create_post_binds_the_regarding_record_and_returns_the_id', async () => {
    const { api, mock } = fakeApi('p9');
    expect(await createPost(api, { table: 'xv_project', id: 'r1', entitySetName: 'xv_projects' }, 'Hello @[8,{u1},"Bea"]')).toBe('p9');
    expect(mock.post).toHaveBeenCalledWith('posts', { text: 'Hello @[8,{u1},"Bea"]', 'regardingobjectid_xv_project@odata.bind': '/xv_projects(r1)' });
  });

  it('create_comment_binds_the_post', async () => {
    const { api, mock } = fakeApi('c9');
    expect(await createComment(api, 'p1', 'Agreed')).toBe('c9');
    expect(mock.post).toHaveBeenCalledWith('postcomments', { text: 'Agreed', 'postid@odata.bind': '/posts(p1)' });
  });

  it('delete_post_and_comment_use_their_rows', async () => {
    const { api, mock } = fakeApi();
    await deletePost(api, 'p1');
    await deleteComment(api, 'c1');
    expect(mock.delete.mock.calls).toEqual([['posts(p1)'], ['postcomments(c1)']]);
  });
});
