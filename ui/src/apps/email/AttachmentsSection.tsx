import { Button, Link, makeStyles, mergeClasses, MessageBar, MessageBarBody, Spinner, Text, tokens } from '@fluentui/react-components';
import { useCallback, useEffect, useRef, useState, type DragEvent } from 'react';
import { saveFile } from '../../admin/tables/fileApi';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { DeleteIcon, DownloadIcon, PreviewIcon } from '../../shell/icons';
import { megabytesText } from '../record/timeline/noteActions';
import { AttachmentPreview, FileKindIcon } from './AttachmentPreview';
import {
  attachmentLimits,
  deleteAttachment,
  downloadAttachment,
  listAttachments,
  refusal,
  sizeText,
  uploadAttachment,
  type Attachment,
  type AttachmentLimits,
} from './attachments';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingHorizontalL,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow4,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  title: { margin: 0, fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  list: { listStyleType: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column' },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    paddingBlock: tokens.spacingVerticalXS,
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  name: { flexGrow: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  size: { color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' },
  drop: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalM,
    border: `1px dashed ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorNeutralForeground2,
    textAlign: 'center',
  },
  dragging: {
    borderTopColor: tokens.colorBrandStroke1,
    borderRightColor: tokens.colorBrandStroke1,
    borderBottomColor: tokens.colorBrandStroke1,
    borderLeftColor: tokens.colorBrandStroke1,
    backgroundColor: tokens.colorBrandBackground2,
  },
  hint: { color: tokens.colorNeutralForeground3 },
  hidden: { display: 'none' },
});

export interface AttachmentsSectionProps {
  /** The saved email; undefined on a new one, which shows nothing (an attachment needs its email). */
  emailId: string | undefined;
  /** A sent or sending email: its attachments are previewed and downloaded, never added or deleted. */
  readOnly: boolean;
}

/**
 * The attachments of an email (plan 4.1b, task 9), beside the form: each file with its size, Preview, Download and Delete;
 * files are added by browsing or dropping them (the body as base64). A file over `maxuploadfilesize` or with a blocked
 * extension is refused here first; the server stays the authority and its refusal is shown the same way.
 */
export function AttachmentsSection({ emailId, readOnly }: AttachmentsSectionProps) {
  const styles = useStyles();
  const { api } = useAuth();
  const input = useRef<HTMLInputElement>(null);
  const [items, setItems] = useState<Attachment[]>();
  const [limits, setLimits] = useState<AttachmentLimits>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [previewing, setPreviewing] = useState<number>();

  const reload = useCallback(async () => {
    if (!api || !emailId) return;
    setItems(await listAttachments(api, emailId));
  }, [api, emailId]);

  useEffect(() => {
    if (!api || !emailId) return;
    let live = true;
    Promise.all([listAttachments(api, emailId), attachmentLimits(api)]).then(
      ([list, found]) => {
        if (!live) return;
        setItems(list);
        setLimits(found);
      },
      (e: unknown) => live && setError(e instanceof Error ? e.message : String(e)),
    );
    return () => {
      live = false;
    };
  }, [api, emailId]);

  if (!emailId) return null;

  async function run(work: () => Promise<void>) {
    setError(undefined);
    setBusy(true);
    try {
      await work();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  const add = (files: File[]) =>
    run(async () => {
      if (!api || files.length === 0) return;
      const refused = limits ? files.map((f) => refusal(f, limits)).filter((r): r is string => !!r) : [];
      if (refused.length > 0) throw new Error(refused.join(' '));
      for (const file of files) await uploadAttachment(api, emailId, file);
      await reload();
    });

  const download = (a: Attachment) =>
    void run(async () => {
      if (api) saveFile(await downloadAttachment(api, a.id));
    });

  const remove = (a: Attachment) =>
    void run(async () => {
      if (!api) return;
      await deleteAttachment(api, a.id);
      await reload();
    });

  const onDrop = (e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setDragging(false);
    void add([...e.dataTransfer.files]);
  };

  const count = items?.length ?? 0;
  const heading = t('email.attachments.title', { count });
  return (
    <section className={styles.card} aria-label={heading}>
      <h2 className={styles.title}>{heading}</h2>
      {error && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {!items ? (
        !error && <Spinner size="tiny" label={t('common.loading')} />
      ) : (
        <ul className={styles.list} aria-label={heading}>
          {items.map((a, i) => (
            <li key={a.id} className={styles.row}>
              <FileKindIcon attachment={a} />
              <Text className={styles.name} title={a.fileName}>
                {a.fileName}
              </Text>
              <Text className={styles.size}>{sizeText(a.size)}</Text>
              <Button appearance="subtle" size="small" icon={<PreviewIcon />} aria-label={t('email.attachments.preview', { name: a.fileName })} title={t('email.attachments.previewTitle')} onClick={() => setPreviewing(i)} />
              <Button appearance="subtle" size="small" icon={<DownloadIcon />} aria-label={t('email.attachments.download', { name: a.fileName })} title={t('email.attachments.downloadTitle')} disabled={busy} onClick={() => download(a)} />
              {!readOnly && (
                <Button appearance="subtle" size="small" icon={<DeleteIcon />} aria-label={t('email.attachments.delete', { name: a.fileName })} title={t('email.attachments.deleteTitle')} disabled={busy} onClick={() => remove(a)} />
              )}
            </li>
          ))}
        </ul>
      )}
      {!readOnly && (
        <div
          className={mergeClasses(styles.drop, dragging && styles.dragging)}
          onDragOver={(e) => {
            e.preventDefault();
            setDragging(true);
          }}
          onDragLeave={() => setDragging(false)}
          onDrop={onDrop}
        >
          <input
            ref={input}
            className={styles.hidden}
            type="file"
            multiple
            aria-label={t('email.attachments.attach')}
            onChange={(e) => {
              const files = [...(e.target.files ?? [])];
              e.target.value = '';
              void add(files);
            }}
          />
          <Text>
            {t('email.attachments.dropBefore')}{' '}
            <Link as="button" disabled={busy} onClick={() => input.current?.click()}>
              {t('email.attachments.browse')}
            </Link>
          </Text>
          {limits && (
            <Text size={200} className={styles.hint}>
              {t(limits.blocked.length ? 'email.attachments.limitsBlocked' : 'email.attachments.limits', {
                limit: megabytesText(limits.maxBytes),
                blocked: limits.blocked.slice(0, 4).map((e) => `.${e}`).join(' ') + (limits.blocked.length > 4 ? ' …' : ''),
              })}
            </Text>
          )}
          {busy && <Spinner size="tiny" />}
        </div>
      )}
      {previewing !== undefined && items && api && (
        <AttachmentPreview items={items} index={previewing} load={(a) => downloadAttachment(api, a.id)} onDownload={download} onClose={() => setPreviewing(undefined)} />
      )}
    </section>
  );
}
