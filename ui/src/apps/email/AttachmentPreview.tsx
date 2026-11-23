import { Button, Dialog, DialogSurface, DialogTitle, makeStyles, mergeClasses, MessageBar, MessageBarBody, Spinner, Text, tokens } from '@fluentui/react-components';
import { useEffect, useState } from 'react';
import type { DownloadedFile } from '../../admin/tables/fileApi';
import { dialogSurface } from '../../client-api/DialogHost';
import { t } from '../../shell/i18n/t';
import { DismissIcon, DownloadIcon, FileIcon, ImageFileIcon, NextPageIcon, PdfFileIcon, PreviousPageIcon } from '../../shell/icons';
import { extensionOf, sizeText, type Attachment } from './attachments';

export type PreviewKind = 'pdf' | 'image' | 'text' | 'none';

/** Markup that can run scripts: never rendered, whatever its type says. */
const NEVER = new Set(['html', 'htm', 'xhtml', 'xht', 'svg', 'svgz', 'xml', 'mht', 'mhtml']);
const IMAGES: Record<string, string> = { png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', gif: 'image/gif', bmp: 'image/bmp', webp: 'image/webp' };
const TEXT = new Set(['txt', 'log', 'csv']);

/**
 * How an attachment is previewed: a PDF in the browser's viewer, an image, plain text; anything else, and always HTML, SVG and
 * other markup (by extension or by type), is not rendered ("No preview").
 */
export function previewKind(a: Attachment): PreviewKind {
  const extension = extensionOf(a.fileName);
  const type = (a.mimeType ?? '').toLowerCase();
  if (NEVER.has(extension) || /html|svg|xml/.test(type)) return 'none';
  if (extension === 'pdf' || type === 'application/pdf') return 'pdf';
  if (IMAGES[extension] || Object.values(IMAGES).includes(type)) return 'image';
  if (TEXT.has(extension) || type === 'text/plain') return 'text';
  return 'none';
}

/** The type the blob is given for the viewer, so the browser never sniffs it as something else. */
const blobType = (a: Attachment, kind: PreviewKind): string =>
  kind === 'pdf' ? 'application/pdf' : kind === 'image' ? (IMAGES[extensionOf(a.fileName)] ?? a.mimeType ?? 'application/octet-stream') : 'text/plain';

/** The text of a blob, read with a FileReader (every browser and the tests' DOM have it). */
function textOf(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error ?? new Error('The file could not be read.'));
    reader.readAsText(blob);
  });
}

/** True when the blob starts with the PDF signature `%PDF-` (read with a FileReader, as `textOf`). */
function isPdf(blob: Blob): Promise<boolean> {
  return new Promise((resolve) => {
    const reader = new FileReader();
    reader.onload = () => {
      const head = new Uint8Array(reader.result as ArrayBuffer);
      resolve(head.length === 5 && String.fromCharCode(...head) === '%PDF-');
    };
    reader.onerror = () => resolve(false);
    reader.readAsArrayBuffer(blob.slice(0, 5));
  });
}

export function FileKindIcon({ attachment }: { attachment: Attachment }) {
  const kind = previewKind(attachment);
  const Icon = kind === 'pdf' ? PdfFileIcon : kind === 'image' ? ImageFileIcon : FileIcon;
  return <Icon aria-hidden />;
}

const useStyles = makeStyles({
  // The Share pane's surface: on the far side, the window's height.
  surface: { display: 'flex', flexDirection: 'column', maxWidth: '100vw', maxHeight: '100vh', padding: 0, overflow: 'hidden' },
  side: { marginRight: 0, marginLeft: 'auto', height: '100vh', borderRadius: 0 },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    paddingInline: tokens.spacingHorizontalXL,
    paddingBlock: tokens.spacingVerticalM,
    borderBottom: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  title: { flexGrow: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  meta: { color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' },
  body: { flexGrow: 1, minHeight: 0, display: 'flex', alignItems: 'stretch', justifyContent: 'center', overflow: 'auto', backgroundColor: tokens.colorNeutralBackground3 },
  frame: { flexGrow: 1, border: 'none', backgroundColor: tokens.colorNeutralBackground1 },
  image: { maxWidth: '100%', maxHeight: '100%', objectFit: 'contain', alignSelf: 'center' },
  text: {
    flexGrow: 1,
    margin: 0,
    padding: tokens.spacingHorizontalXL,
    whiteSpace: 'pre-wrap',
    overflowWrap: 'anywhere',
    fontFamily: tokens.fontFamilyMonospace,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  none: { alignSelf: 'center', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: tokens.spacingVerticalM },
  footer: { paddingInline: tokens.spacingHorizontalXL, paddingBlock: tokens.spacingVerticalS, color: tokens.colorNeutralForeground3, borderTop: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}` },
});

type Shown = { status: 'loading' } | { status: 'url'; url: string } | { status: 'text'; text: string } | { status: 'error'; message: string } | { status: 'none' };

export interface AttachmentPreviewProps {
  items: Attachment[];
  /** The attachment the pane opens on. */
  index: number;
  /** Reads the attachment's file (kept in memory only: nothing is written to disk). */
  load: (a: Attachment) => Promise<DownloadedFile>;
  onDownload: (a: Attachment) => void;
  onClose: () => void;
}

/**
 * The preview of an email's attachments (plan 4.1b, task 9): a side pane 66% of the window wide, the surface of the Share pane,
 * with the file's name, size, "n of m", previous and next, Download and Close. A PDF opens in the browser's viewer in a frame on
 * an application/pdf blob URL, only when its bytes start with %PDF- (any other "PDF" shows "No preview"), an image as an image, plain text as text; any other type, and HTML or SVG (never rendered,
 * to keep scripts out), shows "No preview" with Download. A blob URL is revoked when the pane moves on or closes.
 */
export function AttachmentPreview({ items, index: start, load, onDownload, onClose }: AttachmentPreviewProps) {
  const styles = useStyles();
  const [index, setIndex] = useState(start);
  const current = items[index];
  const kind = current ? previewKind(current) : 'none';
  const [shown, setShown] = useState<Shown>({ status: 'loading' });

  useEffect(() => {
    if (!current || kind === 'none') {
      setShown({ status: 'none' });
      return;
    }
    let live = true;
    let url: string | undefined;
    setShown({ status: 'loading' });
    load(current).then(
      async (file) => {
        if (!live) return;
        if (kind === 'text') {
          const text = await textOf(file.blob);
          if (live) setShown({ status: 'text', text });
          return;
        }
        // A PDF is framed without a sandbox (Chromium blocks its viewer in any sandboxed frame), so only one that is a PDF: its
        // bytes start with %PDF- and the blob is typed application/pdf. Anything else that claims to be one is not shown.
        if (kind === 'pdf' && !(await isPdf(file.blob))) {
          if (live) setShown({ status: 'none' });
          return;
        }
        if (!live) return;
        url = URL.createObjectURL(new Blob([file.blob], { type: blobType(current, kind) }));
        setShown({ status: 'url', url });
      },
      (e: unknown) => live && setShown({ status: 'error', message: e instanceof Error ? e.message : String(e) }),
    );
    return () => {
      live = false;
      if (url) URL.revokeObjectURL(url);
    };
    // One read per attachment shown; `load` is the section's.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [current?.id, kind]);

  if (!current) return null;
  const { style } = dialogSurface({ position: 2, width: { value: 66, unit: '%' } });
  return (
    <Dialog open modalType="modal" onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={mergeClasses(styles.surface, styles.side)} style={{ ...style, minWidth: 'min(100vw, 640px)' }} aria-label={current.fileName}>
        <div className={styles.header}>
          <FileKindIcon attachment={current} />
          <DialogTitle className={styles.title}>{current.fileName}</DialogTitle>
          <Text className={styles.meta}>{t('email.preview.position', { size: sizeText(current.size), n: index + 1, m: items.length })}</Text>
          <Button appearance="subtle" icon={<PreviousPageIcon />} aria-label={t('email.preview.previous')} title={t('email.preview.previous')} disabled={index === 0} onClick={() => setIndex(index - 1)} />
          <Button
            appearance="subtle"
            icon={<NextPageIcon />}
            aria-label={t('email.preview.next')}
            title={t('email.preview.next')}
            disabled={index >= items.length - 1}
            onClick={() => setIndex(index + 1)}
          />
          <Button appearance="subtle" icon={<DownloadIcon />} aria-label={t('email.attachments.download', { name: current.fileName })} title={t('email.attachments.downloadTitle')} onClick={() => onDownload(current)} />
          <Button appearance="subtle" icon={<DismissIcon />} aria-label={t('common.close')} title={t('common.close')} onClick={onClose} />
        </div>
        <div className={styles.body}>
          {shown.status === 'loading' && <Spinner label={t('common.loading')} />}
          {shown.status === 'error' && (
            <MessageBar intent="error">
              <MessageBarBody>{shown.message}</MessageBarBody>
            </MessageBar>
          )}
          {shown.status === 'url' && kind === 'pdf' && (
            // A verified PDF on an application/pdf blob: the browser's PDF viewer draws it (it runs no script of the document).
            <iframe className={styles.frame} src={shown.url} title={current.fileName} />
          )}
          {shown.status === 'url' && kind === 'image' && <img className={styles.image} src={shown.url} alt={current.fileName} />}
          {shown.status === 'text' && <pre className={styles.text}>{shown.text}</pre>}
          {shown.status === 'none' && (
            <div className={styles.none}>
              <FileKindIcon attachment={current} />
              <Text weight="semibold">{t('email.preview.none')}</Text>
              <Button icon={<DownloadIcon />} onClick={() => onDownload(current)}>
                {t('email.attachments.downloadTitle')}
              </Button>
            </div>
          )}
        </div>
        <Text className={styles.footer} size={200}>
          {t('email.preview.footer')}
        </Text>
      </DialogSurface>
    </Dialog>
  );
}
