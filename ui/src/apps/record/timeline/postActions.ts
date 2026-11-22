import type { XrmApi } from '../../../shell/api/client';

/** The record a new post is about; its entity set names the record in `regardingobjectid_{table}@odata.bind`. */
export interface PostParent {
  table: string;
  id: string;
  entitySetName: string;
}

/** A post's limit (`post.text`, String 2000); a mention counts towards it. */
export const POST_MAX_LENGTH = 2000;
/** A comment's limit (`postcomment.text`, String 1000). */
export const COMMENT_MAX_LENGTH = 1000;

/** `POST posts` with the text and the record bound as `regardingobjectid_{table}`; `source` is left to the server (Manual Post). Returns the new post's id. */
export async function createPost(api: XrmApi, parent: PostParent, text: string): Promise<string> {
  const created = await api.post('posts', { text, [`regardingobjectid_${parent.table}@odata.bind`]: `/${parent.entitySetName}(${parent.id})` });
  return created.id ?? '';
}

/** `POST postcomments` with the text and the post bound as `postid`. Returns the new comment's id. */
export async function createComment(api: XrmApi, postId: string, text: string): Promise<string> {
  const created = await api.post('postcomments', { text, 'postid@odata.bind': `/posts(${postId})` });
  return created.id ?? '';
}

/** Deletes the post; the server deletes its comments with it (Post_Comments cascades). */
export async function deletePost(api: XrmApi, id: string): Promise<void> {
  await api.delete(`posts(${id})`);
}

export async function deleteComment(api: XrmApi, id: string): Promise<void> {
  await api.delete(`postcomments(${id})`);
}
