import { Button, Link, makeStyles, Spinner, Text, tokens, Tooltip } from '@fluentui/react-components';
import { Fragment, useEffect, useRef, useState, type ReactNode } from 'react';
import { ConfirmDialog } from '../../../../admin/common/ConfirmDialog';
import { isUserMention, parseMentions } from '../../../../apps/record/timeline/mentions';
import { COMMENT_MAX_LENGTH, createComment, deleteComment, POST_MAX_LENGTH } from '../../../../apps/record/timeline/postActions';
import { loadComments, type PostComment, type TimelineItem } from '../../../../apps/record/timeline/timelineQuery';
import type { XrmApi } from '../../../../shell/api/client';
import { t } from '../../../../shell/i18n/t';
import { DeleteIcon } from '../../../../shell/icons';
import { MentionInput } from './MentionInput';

const useStyles = makeStyles({
  entry: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS },
  buttons: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  thread: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS, marginBlockStart: tokens.spacingVerticalXS },
  count: { color: tokens.colorNeutralForeground3 },
  replies: { margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS },
  reply: {
    listStyle: 'none',
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    columnGap: tokens.spacingHorizontalS,
    paddingInlineStart: tokens.spacingHorizontalM,
    borderLeft: `${tokens.strokeWidthThick} solid ${tokens.colorNeutralStroke2}`,
  },
  replyMain: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  meta: { color: tokens.colorNeutralForeground3 },
  text: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' },
  replyButton: { alignSelf: 'flex-start' },
});

/** A guid as a URL takes it: no braces, lower case. */
const bare = (id: string) => id.trim().replace(/^\{|\}$/g, '').toLowerCase();

export interface MentionTextProps {
  text: string;
  /** The URL of a user's record (the link's href). */
  userUrl: (userId: string) => string;
  /** Opens the user's record in the app. */
  onOpenUser: (userId: string) => void;
}

/** A post's or a comment's text: a user mention is a link to the user, another table's mention its display string, anything else as typed (spec §5). */
export function MentionText({ text, userUrl, onOpenUser }: MentionTextProps) {
  return (
    <>
      {parseMentions(text).map((p, i) =>
        p.kind === 'text' ? (
          <Fragment key={i}>{p.text}</Fragment>
        ) : isUserMention(p) ? (
          <Link
            key={i}
            href={userUrl(bare(p.id))}
            onClick={(e) => {
              // Ctrl, Cmd, Shift or a middle click keep the browser's own behaviour (a new tab or window).
              if (e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
              e.preventDefault();
              onOpenUser(bare(p.id));
            }}
          >
            {p.display}
          </Link>
        ) : (
          <Fragment key={i}>{p.display}</Fragment>
        ),
      )}
    </>
  );
}

/** The text of a post or comment as read aloud or searched: mentions by their display string. */
export const mentionPlainText = (text: string): string => parseMentions(text).map((p) => (p.kind === 'text' ? p.text : p.display)).join('');

interface MentionEntryProps {
  /** The group's name. */
  label: string;
  /** The text box's name. */
  fieldLabel: string;
  maxLength: number;
  submitLabel: string;
  api: XrmApi;
  userId: string;
  /** Bumped to move the focus to the text box again (it takes it when shown). */
  focusRequest?: number;
  onCancel: () => void;
  /** Sends the text; resolves true when it was created (the entry then clears and closes). A refusal keeps the text. */
  onSave: (text: string) => Promise<boolean>;
}

/** A text box with @mentions, its counter, and a send and a Cancel button; Ctrl+Enter sends. */
function MentionEntry({ label, fieldLabel, maxLength, submitLabel, api, userId, focusRequest = 0, onCancel, onSave }: MentionEntryProps) {
  const styles = useStyles();
  const [text, setText] = useState('');
  const [saving, setSaving] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  useEffect(() => {
    root.current?.querySelector('textarea')?.focus();
  }, [focusRequest]);
  const ready = !!text.trim() && text.length <= maxLength && !saving;
  const save = async () => {
    if (!ready) return;
    setSaving(true);
    try {
      if (await onSave(text.trim())) {
        setText('');
        onCancel();
      }
    } finally {
      setSaving(false);
    }
  };
  return (
    <div ref={root} className={styles.entry} role="group" aria-label={label}>
      <MentionInput value={text} onChange={setText} maxLength={maxLength} label={fieldLabel} api={api} userId={userId} onSubmit={() => void save()} />
      <div className={styles.buttons}>
        <Button size="small" appearance="primary" disabled={!ready} onClick={() => void save()}>
          {submitLabel}
        </Button>
        <Button size="small" disabled={saving} onClick={onCancel}>
          {t('common.cancel')}
        </Button>
      </div>
    </div>
  );
}

export interface PostEntryProps {
  api: XrmApi;
  userId: string;
  /** Bumped by the + menu's Post: the text box takes the focus. */
  focusRequest: number;
  onClose: () => void;
  /** Creates the post; resolves true when it was created. */
  onSave: (text: string) => Promise<boolean>;
}

/** The post entry (spec §4.3), opened by + > Post: one text box of 2000 characters with a counter, Post and Cancel. */
export function PostEntry({ api, userId, focusRequest, onClose, onSave }: PostEntryProps) {
  return (
    <MentionEntry
      label={t('record.timeline.post.entry')}
      fieldLabel={t('record.timeline.post.text')}
      maxLength={POST_MAX_LENGTH}
      submitLabel={t('record.timeline.post')}
      api={api}
      userId={userId}
      focusRequest={focusRequest}
      onCancel={onClose}
      onSave={onSave}
    />
  );
}

/** "1 reply" or "N replies". */
export const repliesText = (n: number): string => (n === 1 ? t('record.timeline.post.oneReply') : t('record.timeline.post.replies', { count: n }));

export interface PostThreadProps {
  api: XrmApi;
  post: TimelineItem;
  /** The post is expanded: its comments are read and shown, with Reply. */
  expanded: boolean;
  /** The signed-in user: Delete is offered on their own comments only. */
  userId: string;
  /** Reply is offered (posts on, `prvCreatePost`, not disabled). */
  canReply: boolean;
  /** Delete is offered on the user's own comments (`prvDeletePost`, not disabled). */
  canDelete: boolean;
  renderText: (text: string) => ReactNode;
  /** A comment's date as the row shows its own. */
  formatDate: (iso: string) => string;
  /** A failure to say above the list (undefined clears it). */
  onError: (reason: string | undefined) => void;
}

const errorText = (e: unknown) => (e instanceof Error ? e.message : String(e));

/** Under a post: "N replies"; expanded, its comments oldest first, Reply, and Delete on the user's own comments. */
export function PostThread({ api, post, expanded, userId, canReply, canDelete, renderText, formatDate, onError }: PostThreadProps) {
  const styles = useStyles();
  // The comments read for this post object: the timeline reading the post again (a new object) brings a fresh count.
  const [read, setRead] = useState<{ post: TimelineItem; rows: PostComment[] }>();
  const comments = read?.post === post ? read.rows : undefined;
  const setComments = (update: (rows: PostComment[] | undefined) => PostComment[] | undefined) =>
    setRead((r) => {
      const rows = update(r?.post === post ? r.rows : undefined);
      return rows ? { post, rows } : r;
    });
  const [replying, setReplying] = useState(false);
  const [pendingDelete, setPendingDelete] = useState<string>();

  // Read when the post is expanded, and again when the timeline read the post again.
  useEffect(() => {
    if (!expanded) return;
    let cancelled = false;
    loadComments(api, post.id).then(
      (rows) => !cancelled && setRead({ post, rows }),
      (e: unknown) => !cancelled && onError(errorText(e)),
    );
    return () => {
      cancelled = true;
    };
    // onError is the control's setter; the post object changes when the timeline is read again.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, post, expanded]);

  const count = comments?.length ?? post.replyCount ?? 0;
  const mine = (c: PostComment) => !!userId && c.authorId.toLowerCase() === userId.toLowerCase();

  const reply = async (text: string): Promise<boolean> => {
    onError(undefined);
    let id: string;
    try {
      id = await createComment(api, post.id, text);
    } catch (e) {
      onError(errorText(e));
      return false;
    }
    // Read again for the server's author name and date; if that fails, the comment is added as sent.
    try {
      const rows = await loadComments(api, post.id);
      setRead({ post, rows });
    } catch {
      setComments((c) => [...(c ?? []), { id, text, createdOn: new Date().toISOString(), authorId: userId, authorName: '' }]);
    }
    return true;
  };

  const remove = async (id: string) => {
    onError(undefined);
    try {
      await deleteComment(api, id);
      setComments((c) => c?.filter((x) => x.id !== id));
    } catch (e) {
      onError(errorText(e));
    }
  };

  if (!expanded) {
    return count > 0 ? (
      <Text size={200} className={styles.count}>
        {repliesText(count)}
      </Text>
    ) : null;
  }
  return (
    <div className={styles.thread}>
      {count > 0 && (
        <Text size={200} className={styles.count}>
          {repliesText(count)}
        </Text>
      )}
      {comments === undefined ? (
        <Spinner size="extra-tiny" label={t('record.timeline.loading')} />
      ) : (
        comments.length > 0 && (
          <ul className={styles.replies} aria-label={t('record.timeline.post.repliesLabel')}>
            {comments.map((c) => (
              <li key={c.id} className={styles.reply}>
                <div className={styles.replyMain}>
                  <Text size={200} className={styles.meta}>
                    {[c.authorName, formatDate(c.createdOn)].filter(Boolean).join(' · ')}
                  </Text>
                  <Text size={300} className={styles.text}>
                    {renderText(c.text)}
                  </Text>
                </div>
                {canDelete && mine(c) && (
                  <Tooltip content={t('record.cmd.delete')} relationship="label">
                    <Button appearance="subtle" size="small" icon={<DeleteIcon />} onClick={() => setPendingDelete(c.id)} />
                  </Tooltip>
                )}
              </li>
            ))}
          </ul>
        )
      )}
      {canReply &&
        (replying ? (
          <MentionEntry
            label={t('record.timeline.post.replyEntry')}
            fieldLabel={t('record.timeline.post.replyText')}
            maxLength={COMMENT_MAX_LENGTH}
            submitLabel={t('record.timeline.post.reply')}
            api={api}
            userId={userId}
            onCancel={() => setReplying(false)}
            onSave={reply}
          />
        ) : (
          <Button size="small" appearance="subtle" className={styles.replyButton} onClick={() => setReplying(true)}>
            {t('record.timeline.post.reply')}
          </Button>
        ))}
      {canDelete && (
        <ConfirmDialog
          open={pendingDelete !== undefined}
          title={t('record.timeline.comment.deleteTitle')}
          body={t('record.timeline.comment.deleteBody')}
          confirmLabel={t('record.cmd.delete')}
          cancelLabel={t('common.cancel')}
          onCancel={() => setPendingDelete(undefined)}
          onConfirm={() => {
            const id = pendingDelete;
            setPendingDelete(undefined);
            if (id) void remove(id);
          }}
        />
      )}
    </div>
  );
}
